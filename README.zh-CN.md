# SwarmRoute MAPF — 多機路徑規劃系統

> 简体中文 · English version: [README.md](README.md)

> 一套面向多机器人（AGV）的 **终身路径规划与交通管制** 引擎，以领域驱动的方式构建为一组运行在
> **.NET 10 / C# 14** 上的限界上下文，配备纯内存闭环模拟器和 Web 可视化界面。
> 机器人在共享路网上从 A 行驶到 B **且永不碰撞** —— 整个系统的存在意义就是
> 提供这一保证，并让你亲眼见证它发生。

SwarmRoute 是将一个早期 MAPF 原型（`third-party/AJR.MAPF`）按照参考代码库（`third-party/grukirbs`）的
DDD 约定进行整洁架构重构后的产物。车队控制问题被拆解为
**四个领域**（业界将其视为彼此独立的关注点），外加一个编排循环和一套可观测的
模拟：

| 領域 / Context | 各自负责 | README |
|---|---|---|
| **Map — 資源 / 地圖** | 静态路网：控制点（CP）、有向车道、互斥块；纯内存的 `RoadmapGraph` 读模型。 | [Map/README.zh-CN.md](Map/README.zh-CN.md) |
| **Path Planning — 路徑規劃** | 在图上进行单体路线搜索；将路线提升为时空路径。 | [PathPlanning/README.zh-CN.md](PathPlanning/README.zh-CN.md) |
| **Traffic Control — 交通管制** | 路权：基于区间的时空预约（谁可以在何时占用哪个资源）；授予 / 拒绝 / 释放。 | [TrafficControl/README.zh-CN.md](TrafficControl/README.zh-CN.md) |
| **Deadlock Handling — 死鎖處理** | 从预约竞争图中反应式地检测环形等待；请求解决（绕行 / 重路由）。 | [Deadlock/README.zh-CN.md](Deadlock/README.zh-CN.md) |
| **Coordination — 協調** | RHCR 滚动时域控制循环，将四个领域编排为一个车队 tick。 | [Coordination/README.zh-CN.md](Coordination/README.zh-CN.md) |
| **Simulation — 模擬** | 无数据库的闭环 **执行器 + 校验器**，以及驱动整个技术栈的 HTTP 回放 API。 | [Simulation/README.zh-CN.md](Simulation/README.zh-CN.md) |
| **SwarmRoute Web — 前端** | React/Vite 实现的「调度员控制台」，可运行场景并在画布上回放。 | [host/swarmroute-web/README.zh-CN.md](host/swarmroute-web/README.zh-CN.md) |

设计背景与团队计划见 [`docs/architecture-design.md`](docs/architecture-design.md) 和
[`docs/team-implementation-plan.md`](docs/team-implementation-plan.md)。

---

## Architecture

整洁架构，单向依赖。每个上下文都是一个垂直切片
（`Domain.Shared → Domain → Application.Contract → Application → Infra.Data → Infra.CrossCutting.IoC → Api`），
各上下文 **只** 通过 `Application.Contract` 接口和共享 **Kernel** 通信 —— 绝不跨越
彼此的领域内部实现。

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ swarmroute-web   React 19 / Vite — dispatcher's console, HTML5-canvas replay   │
└─────────────────────────────────┬──────────────────────────────────────────────┘
                                   │  POST /api/simulation/run   (Vite dev-proxy)
┌─────────────────────────────────▼──────────────────────────────────────────────┐
│ Host   ASP.NET Core — composition root, SimulationController, integration       │
│        adapters (topology / detour / deadlock-snapshot / in-memory engine)      │
└─────────────────────────────────┬──────────────────────────────────────────────┘
┌─────────────────────────────────▼──────────────────────────────────────────────┐
│ Simulation 模擬   FleetLoopDriver (tick executor + right-of-way gate),           │
│                   ManualFleetClock, per-request in-memory engine, replay DTO    │
└─────────────────────────────────┬──────────────────────────────────────────────┘
                                   │  RunCycleAsync / ReleaseAsync
┌─────────────────────────────────▼──────────────────────────────────────────────┐
│ Coordination 協調   RHCR rolling-horizon cycle — orchestrates the four domains   │
└────┬──────────────────────┬───────────────────────┬─────────────────────────────┘
     │ IRoadmapQueryService  │ IPathPlanner           │ ITrafficCoordinatorAppService
     │                       │ IReservationQuery      │           │  AllocationContended
┌────▼─────────┐   ┌─────────▼────────┐   ┌───────────▼──────┐    │   ┌───────────────┐
│ Map 資源/地圖 │   │ PathPlanning      │   │ TrafficControl   │────┼──►│ Deadlock 死鎖  │
│ RoadmapGraph │   │ 路徑規劃 (Dijkstra)│   │ 交通管制 (預約表) │◄───┼───│ 死鎖處理 (RAG)  │
└──────────────┘   └──────────────────┘   └──────────────────┘  snapshot └───────────────┘
       └───────────────────────┴───────────────────────┴────────────────┬───────────┘
┌─────────────────────────────────────────────────────────────────────────▼──────────┐
│ Shared / Kernel   SpatioTemporal.Kernel (ResourceRef, SpaceTimePath, TimeInterval,   │
│   IFleetClock, IReservationView, SafeInterval), EventBus, Domain.Abstractions,       │
│   Infra.Data.Core, StateMachine.Core, vendored graph algorithms, NetDevPack          │
└──────────────────────────────────────────────────────────────────────────────────────┘
```

关键接缝（上下文之间耦合的唯一途径）：

- **`IRoadmapQueryService`**（Map）→ 供 PathPlanning、Coordination、Simulation 消费的 `RoadmapGraph` 读模型。
- **`IPathPlanner`**（PathPlanning）→ 单体规划，由 Coordination 调用。
- **`IReservationQuery`** —— 由 PathPlanning *声明*（默认 `NullReservationQuery`），由 TrafficControl *实现*（`ReservationService`），后者在组合根处覆盖该注册。
- **`ITrafficCoordinatorAppService`**（TrafficControl）→ `TryReserve` / `Release` / `BlockedResources`，即 Coordination 驱动的写入接缝。
- **`AllocationContended`** 集成事件（TrafficControl → Deadlock），外加一个反向的预约 **快照** 读取接缝。
- **`IFleetClock`**（Kernel）→ 每个预约区间所参照的时间轴。

---

## How the closed loop works

**一个协调周期**（`CoordinationCycleService.RunCycleAsync`，即 RHCR 滚动时域）—— 各智能体按
确定性的优先级顺序处理；每个智能体先规划，尝试获取路权，被拒时则剪枝
竞争资源并在有界预算内重规划：

```
for each agent goal (ascending Priority, then ordinal id):
   graph  = Map.GetGraph(roadmap)
   view   = TrafficControl.ReservationView
   path   = PathPlanning.Plan(graph, request, view)        # Dijkstra, honours blacklist
   outcome= TrafficControl.TryReserve(path, agent)          # whole-path interval lock
   if Queued/Blocked:  prune the blocking CP/Lane → re-plan (bounded)   # = "wait / detour"
```

**执行器**（`FleetLoopDriver`，位于 Simulation 上下文中）将周期逐 tick 地转化为运动，
也正是让运行过程 **可观测且无碰撞** 的组件：

```
tick 0 : record initial frame (every AGV waiting at its start)
each tick t:
   clock ← t                          # tick-driven ManualFleetClock (see below)
   plan + reserve idle agents          # via RunCycleAsync, parked vehicles passed as obstacles
   for each en-route agent (priority order):
       next = the agent's next CP
       if next is occupied this tick → WAIT   (right-of-way gate)
            └ if the blocker is a *parked* (arrived) vehicle → DROP reservation & re-route
       else → advance one CP, release the CP+lane behind
       on arrival → release all leases, mark the goal cell a parked obstacle
   record a frame
```

### The collision-freedom guarantee

两个相互独立的层级，因此同格碰撞 *在构造上* 不可能发生：

1. **预约层** —— TrafficControl 通过区间互斥租约，协调 *谁规划经过哪个控制点、何时经过*。
2. **执行器路权门控** —— 车辆绝不会进入本 tick 中已被另一车辆占用的控制点；
   它会等待，或绕过一辆已停放车辆重路由。

因此，真正的僵持会退化为一个诚实的 **`DidNotConverge`** 状态 —— 绝不崩溃，绝不
碰撞。

> **为何 tick 时钟很重要。** 生产环境的 `SystemFleetClock` 报告挂钟时间的毫秒数。由于各周期
> 在亚毫秒级内运行完毕，预约表认为彼此时间分隔的两个预约，可能在同一个 *tick* 落到同一个控制
> 点上 —— 预约时间轴与执行轴并不相关。模拟
> 注入了一个 **`ManualFleetClock`**（一个 tick = 一次 CP 跳转），将预约与执行置于同一条轴上，
> 使区间保证名副其实，并让运行可复现。这是闭环的基石 —— 参见
> [Simulation/README.zh-CN.md](Simulation/README.zh-CN.md)。

---

## Tech stack

- **.NET 10.0 / C# 14**、EF Core 10、Npgsql/PostgreSQL（仅用于快照/审计 —— 热路径在内存中），
  CAP + Hangfire（集成事件 / 作业）、AutoMapper、**NetDevPack** 基类（`Entity`、
  `IAggregateRoot`、`ValueObject`、`DomainEvent`、`IUnitOfWork`）、内置（vendored）图算法。
- **前端：** React 19 + TypeScript（strict）+ Vite 7 + Tailwind 3 + Ant Design 6（已套用主题）+ Zustand 5 +
  react-intl（zh-CN / en）+ 原生 HTML5 Canvas。

## Repository layout

```
Map/  PathPlanning/  TrafficControl/  Deadlock/   # the four domains (each: per-context README + DDD layers)
Coordination/                                     # RHCR orchestration loop
Simulation/                                       # closed-loop executor + in-memory engine + replay DTOs
Shared/                                           # Kernel, EventBus, Domain.Abstractions, Infra.Data.Core, StateMachine.Core
host/SwarmRoute.Host/                             # ASP.NET Core composition root + Simulation HTTP API + adapters
host/swarmroute-web/                              # React/Vite simulator & visualizer
tests/                                            # SwarmRoute.Integration.Tests (closed-loop + cycle integration)
docs/                                             # architecture design + team plan
src/vendor/                                       # vendored graph data-structures/algorithms
third-party/                                      # reference material: AJR.MAPF (prototype) + grukirbs (DDD reference)
lib/                                              # NetDevPack
SwarmRoute.Mapf.sln
```

每个领域还附带单元测试（`SwarmRoute.<Context>.Tests`）；跨上下文的行为由
`tests/SwarmRoute.Integration.Tests` 覆盖。

---

## Getting started

### Prerequisites
- .NET 10 SDK
- Node.js + npm（用于前端）
- **无需数据库** —— 模拟 API 和集成测试完全在内存中运行。（仅
  EF 支持的 Map 导入/快照路径才需要 PostgreSQL，而模拟器并不触及这些路径。）

### Build & test
```bash
dotnet build                       # whole solution
dotnet test                        # full suite (Map, PathPlanning, TrafficControl, Deadlock, Integration)
```

### Run the backend (no DB)
```bash
ASPNETCORE_URLS=http://localhost:5062 dotnet run --project host/SwarmRoute.Host
```

直接驱动一次模拟：
```bash
curl -s -XPOST localhost:5062/api/simulation/run \
  -H 'content-type: application/json' \
  -d '{"width":16,"height":16,"agvCount":12}'
# → { field, agents, timeline{frames[{tick, positions[…]}]}, stats{status, collisions, arrived, replans, …} }
```
`stats.status` 取值为 `Completed` | `CollisionDetected` | `DidNotConverge`。（`CollisionDetected` 是一个回归
指示器 —— 引擎在构造上即无碰撞；密集、不可行的排布会报告 `DidNotConverge`。）

### Run the web simulator
```bash
cd host/swarmroute-web
npm install
npm run dev            # http://localhost:5173  (Vite proxies /api → http://localhost:5062, no CORS)
```
设置场地尺寸与 AGV 数量，按下 **运行 (Run)**，即可观看每辆 AGV 沿其规划路径从 A→B 行驶，同时
**时空预约带（reservation ribbon）** 会展示为何任意两辆车从不共享同一个控制点。

---

## Status & roadmap

**v0（当前）。** 端到端闭环，全部测试通过，构造上即无碰撞：
- Dijkstra 最短路径规划 + 黑名单驱动的剪枝重规划。
- 整条路径、基于区间的预约（SIPP-ready 模型）。
- 在资源分配图上进行反应式死锁检测。
- 带路权门控与已停放车辆重路由的 tick 同步执行器。
- 内存模拟 API + 画布回放前端。

在密度扫描中验证通过（10×10、16×16、20×20 网格；多达数十辆 AGV；多个随机种子）：**处处
零碰撞**；典型密度下均能完成；只有真正过度排满的场地才会报告 `DidNotConverge`。

**v1（计划中）。** 在保持 `IPathPlanner` 接缝不变的前提下，将规划器替换为 **SIPP**（安全区间路径
规划），并转向忠于排程的执行 —— 在时间维度上协调交汇以获得更高吞吐量，沿用同样的
上下文、契约和循环主体。

---

## Provenance

- `third-party/AJR.MAPF` —— 最初的 MAPF 原型（真正的逻辑位于 `AJR.Platform.Minimal`）；其
  CBS「无法锁定路径 → 等待 / 重规划」的行为在此被重新表达为 Coordination 的
  剪枝重规划循环，其 `GraphMap` 的锁定/解锁则表达为 TrafficControl 的区间租约。
- `third-party/grukirbs` —— DDD / 整洁架构参考，本代码库的分层与 NetDevPack 约定
  即以其为镜像。
