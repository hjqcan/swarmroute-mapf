# Simulation (模擬 — 閉環執行器與記憶體模擬 API)

> 简体中文 · English version: [README.md](README.md)

*抽象的规划引擎在这里成为可观测、可验证的闭环：一个无数据库、每请求的内存模拟，它驱动真实的 Map + PathPlanning + TrafficControl + Coordination 技术栈运行到完成，记录逐 tick 的时间线，并证明其无碰撞。*

---

## 1. Purpose

其他上下文从抽象层面回答*"一支车队能否在无冲突的前提下完成规划与预约？"*。而 Simulation 上下文则**具体且可观测地**回答它：它在内存路网之上搭起真实的引擎，运行一个多 AGV 场景从起点到目标，并发出一个逐 tick 一帧的回放供前端渲染。这里**没有数据库**——没有 EF，没有 Postgres——所以整套流程可以在单元测试中运行，或藏在一次 HTTP 调用之后。

有两个属性让它不只是一个演示：

- **它复用真实的服务。** 引擎是从生产环境的组合根接线而来的——`PathPlanningNativeInjectorBootStrapper.RegisterServices`、`TrafficControlNativeInjectorBootStrapper.RegisterServices`、`services.AddCoordination()`（`InMemorySimulationEngineFactory.cs:31-33`）。唯一的替换是一个内存路网数据源和一个 tick 驱动的时钟。所以一次绿色的模拟就是证据，证明*真实的*规划器/预约/协调代码是无碰撞的,而不是一个玩具式的重新实现。
- **它是验证器，而不只是运行器。** `FleetLoopDriver`(执行器)记录每一个 tick，运行一项防御性的同一 CP 安全检查，并诚实地把僵持报告为 `DidNotConverge`,而不是崩溃或悄悄截断。它所包含的闭环主体是从 `ClosedLoopIntegrationTests` 中抽取到生产代码里的，因此 API 与测试驱动的是*同一个*经过验证的闭环。

有两个程序集补全了这个故事;两者都位于 `Simulation.Application` 之外，因为它们了解具体的 Infra/Host 接线：
- `host/SwarmRoute.Host/Adapters/InMemorySimulationEngineFactory.cs` —— 构建每请求的引擎。
- `host/SwarmRoute.Host/Controllers/SimulationController.cs` —— `POST /api/simulation/run` 端点。

---

## 2. Per-request engine — isolation by construction

`InMemorySimulationEngineFactory.Create(RoadmapGraph, PlannerKind)` **每次调用都构建一个全新的 `ServiceCollection` 和 `ServiceProvider`**：

```csharp
var roadmapId = Guid.NewGuid();
var services = new ServiceCollection();
services.AddLogging();
services.AddEventBus();
services.AddSingleton<IRoadmapQueryService>(new InMemoryRoadmapQueryService(roadmapId, graph));
services.AddSingleton(new PlannerOptions { Default = planner });
PathPlanningNativeInjectorBootStrapper.RegisterServices(services);
TrafficControlNativeInjectorBootStrapper.RegisterServices(services);
services.AddCoordination();
// ... swap the clock (below) ...
var provider = services.BuildServiceProvider();
return new Engine(provider, roadmapId, provider.GetRequiredService<IFleetCoordinationCycle>(), clock);
```

为什么每请求都要一个完整的容器？因为权威的 `ReservationTable` 在生产环境中是一个**进程级单例**（`TrafficControlNativeInjectorBootStrapper.cs:74`，注释为*"the in-memory authoritative reservation state: a process-wide SINGLETON"*）。如果两个并发的模拟共享同一个 provider，它们就会共享那张表，一次运行的租约就会阻塞或破坏另一次运行的租约。每次运行一个私有的 provider，使得每个模拟都拥有它**自己的 `ReservationTable`、`IFleetCoordinationCycle`、规划器和时钟**——并发的 `POST /run` 调用彼此完全隔离。`ISimulationEngine : IAsyncDisposable`（`ISimulationEngineFactory.cs:17`）以及服务中的 `await using`（`SimulationService.cs:53`）会在运行结束时拆除 provider。

**路网数据源。** `InMemoryRoadmapQueryService`（`InMemoryRoadmapQueryService.cs`）是一个由预先构建好的 `RoadmapGraph` 支撑的单路网 `IRoadmapQueryService`；`GetGraphAsync(id)` 会为那个唯一注册的 `roadmapId` 返回它，否则抛出 `KeyNotFoundException`。它镜像了测试用的 fake,并替代了生产环境中由仓储支撑的 `RoadmapGraphProvider`。

**时钟替换——关键所在。** TrafficControl 的 bootstrapper 把 `SystemFleetClock` 注册为默认的 `IFleetClock`。工厂将其移除，并替换为一个 `ManualFleetClock`（`InMemorySimulationEngineFactory.cs:38-40`）：

```csharp
var clock = new ManualFleetClock();
services.RemoveAll<IFleetClock>();
services.AddSingleton<IFleetClock>(clock);
```

同一个 `clock` 实例被交给 `Engine`,以便驱动器可以推进它。正是这次注册把预约时间与执行时间耦合在一起——参见 §3/§4。

---

## 3. `FleetLoopDriver` — the closed-loop executor (the core)

`FleetLoopDriver.RunToCompletionAsync(...)`（`FleetLoopDriver.cs:134-355`）是这个上下文的核心。给定引擎的 `IFleetCoordinationCycle`、`roadmapId`、`RoadmapGraph`、一支由 `FleetAgentSpec` 组成的车队、一个 `maxTicks` 预算以及一个 `advanceClock` setter,它驱动真实引擎并记录一个 `FleetLoopResult`。

它在一个私有的 `RunAgent`（`FleetLoopDriver.cs:102-123`）中保存每个 agent 的运行状态：`Start`（*当前*的规划起点——改道时会被改写）、`Goal`、`Priority`、`EnRoute`/`Done` 标志、已预约的 `CpRoute`、持有的 `AllResources`、路线索引 `Idx` 以及一个 `Replans` 计数器。车队被排序为一个**稳定顺序**——`OrderBy(Priority).ThenBy(Id, Ordinal)`（`FleetLoopDriver.cs:149-153`）——以便给定的输入总是产生相同的时间线。

### The tick loop

```
                       ┌──────────────────────────────────────────────────────────────┐
  tick-0 frame         │  record frame 0: every agent Waiting at its start CP           │
  (all Waiting)        └──────────────────────────────────────────────────────────────┘
                                              │
        ┌─────────────────────────────────────▼─────────────────────────── while any agent !Done ─┐
        │  if tick+1 > maxTicks  ──► status = DidNotConverge; break                                 │
        │  tick++                                                                                   │
        │                                                                                           │
        │  (0) advanceClock(tick)        ManualFleetClock.NowMs = tick   ← reservation axis = tick   │
        │                                                                                           │
        │  (1) PLAN + RESERVE pending (idle) agents                                                 │
        │        cycle.RunCycleAsync(roadmapId, pending, blocked: parkedCells-as-SiteRefs, ct)      │
        │        for each reserved result: EnRoute=true, Idx=0, CpRoute = path's CP cells           │
        │        (assert CpRoute runs Start→Goal, else throw FleetLoopException)                    │
        │                                                                                           │
        │  (2) ADVANCE en-route agents: greedy gate for Dijkstra, schedule-faithful for SIPP         │
        │        occupantNow  = who physically sits on each CP at tick start                        │
        │        claimedNext  = CPs held after this tick (seed: every NON-mover keeps its CP)        │
        │        for each mover (priority order):                                                   │
        │           toCp = next CP                                                                   │
        │           ├─ occupant on toCp is Done (parked)? ─► RE-ROUTE: release path, Start=fromCp,   │
        │           │                                         rejoin planning next cycle             │
        │           ├─ toCp in claimedNext OR occupied now? ─► WAIT: hold fromCp (keep all leases)   │
        │           └─ else ─► Idx++, claim toCp, ReleaseAsync(fromCp-CP, fromCp→toCp-Lane)          │
        │        on reaching last CP ─► Done, parkedCells.Add(goal), ReleaseAsync(all held)          │
        │                                                                                           │
        │  (3) SAFETY (defensive): no two right-of-way holders share a CP                            │
        │        if they do ─► Collisions++, status = CollisionDetected (record the frame, break)    │
        │                                                                                           │
        │  (4) RECORD frame: every agent's CP + AgentMotionState this tick                          │
        └───────────────────────────────────────────────────────────────────────────────────────────┘
```

**（tick 0）初始帧。** 在循环之前,会为 tick 0 记录一帧,其中每个 agent 都在其起点 CP 处 `Waiting`（`FleetLoopDriver.cs:169-174`）。这给观看者一个"车队在起点"的帧,因为循环*在同一个 tick 内进行预约和推进*——所以 tick 1 已经前进了一个 CP。

**（0）推进 tick 时钟。** 在每个 tick 的最开始,`advanceClock?.Invoke(tick)` 把车队时钟设置为整数 tick（`FleetLoopDriver.cs:192`;模拟按 `SimulationService.cs:61` 传入 `engine.Clock.SetTick`）。这发生在规划**之前**,所以本周期预约的每个 `TimeInterval` 都以 *tick 单位*表达——这正是执行器移动所在的同一轴(一个 tick = 一次 CP 跳跃)。这是该循环中最重要的单一设计决策;§4 解释了原因。

**（1）规划 + 预约待处理的 agent。** 每个 `!Done && !EnRoute` 的 agent 都变成一个 `AgentGoal(Id, Start, Goal, Priority)`（`FleetLoopDriver.cs:195-198`）,这一批被交给 `cycle.RunCycleAsync(roadmapId, pending, blocked, ct)`（`FleetLoopDriver.cs:205`）。关键在于,**`parkedCells` 被作为 `blockedResources` 集合传入**——`parkedCells.Select(RoadmapGraph.SiteRef).ToHashSet()`（`FleetLoopDriver.cs:202-204`）——这样规划器就会把车队的其余部分*绕过*那些已经完成并停在其目标 CP 上的 agent。对每个已预约的结果,驱动器把该 agent 翻转为在途状态,提取其 `CpRoute`(所返回 `SpaceTimePath` 中 `ResourceKind.CP` 的那些 cell,`FleetLoopDriver.cs:216-219`),缓存 `AllResources` 以便稍后释放,并累加 `Replans += max(0, Attempts-1)`(超出首次的尝试是该周期内部的剪枝-重规划重试)。然后它断言已预约的路径确实从 `Start → Goal`,仅当这个内部不变量被违反时才抛出 `FleetLoopException`(`FleetLoopDriver.cs:222-225`)。

**（2）执行模式——greedy 门控或忠于调度的 SIPP。** Dijkstra 走 v0 的停-等门控；SIPP 走 schedule-faithful 执行：当下一个 CP 的计划进入 tick 到达时，agent 才推进，驱动器会把候选移动解析成一个后置 CP 互不重复的集合。两个结构仍然保护执行：
- `occupantNow` —— 在 tick *开始*时哪个 agent 物理上坐在每个 CP 上(待处理的 agent 在其起点,在途的在其当前 CP,已到达的在其目标)。
- `claimedNext` —— tick *之后*将被占据的那些 CP。它**以每个非移动者的位置作为种子**,所以移动者永远不会踏上一个等待或停放中的 agent 所持有的 cell。

然后,对每个在途的 agent(按稳定的优先级顺序),查看下一个 CP `toCp`：

1. **停放的阻挡者 → 改道。** 如果 `occupant.Done`(一辆已完成的车辆永久停放在 `toCp` 上),等待将永远无法解除。所以该 agent **放弃其预约**（`ReleaseAsync(held)`）,设置 `EnRoute=false`、`Start = fromCp`,把其路线重置为 `[fromCp]`,并在下一周期重新加入规划（`FleetLoopDriver.cs:263-278`）。因为那个停放的 cell 已经在 `parkedCells` 中,下一次规划会绕过它。`Replans++`。
2. **瞬态阻挡者 → 等待。** 在 greedy 模式中，如果 `toCp` 已经在 `claimedNext` 中(一个更高优先级的移动者占用了它)**或**当前仍被占据，该 agent **原地保持**并保留其全部租约。在 schedule-faithful 模式中，未推进通常是计划中的等待，除非防御性解析器撤销了该步移动。
3. **畅通 / 已到计划时刻 → 推进一个 CP。** 否则 `Idx++`,占用 `toCp`,并 `ReleaseAsync` 刚刚腾出的 cell+lane——`fromCp` CP 和 `fromCp→toCp` `Lane`——把它们交还给预约表,以便后随/交叉的 agent 可以使用。

在到达最后一个 CP 时,该 agent 变为 `Done`,它的目标 CP 被加入 `parkedCells`,并且**所有仍持有的资源都被释放**(无租约泄漏)（`FleetLoopDriver.cs:296-305`）。

**（3）防御性安全检查。** 移动之后,驱动器断言没有两个路权持有者(在途或刚到达)占据同一个 CP（`FleetLoopDriver.cs:309-325`）。有了门控之后,这*不可能*发生;如果它真的发生了,会被记录为 `FleetLoopStatus.CollisionDetected`,附带一个 `FleetCollisionInfo(tick, siteId, [a,b])`,并且运行停止——这是一个回归信号,而非正常流程。

**（4）记录帧**,带上每个 agent 的 `(AgentId, SiteId, AgentMotionState)`（`FleetLoopDriver.cs:330-335`）,把 `MaxConcurrentEnRoute` 作为并行度信号进行跟踪,然后循环。

驱动器在给定确定性输入时是**确定性的**(tick 时钟消除了挂钟时间依赖),并且是一个**验证器**：它仅在内部的 `Start→Goal` 不变量被破坏时才抛出,从不因僵持而抛出。

---

## 4. Collision-freedom & liveness

### Two layers guarantee no collision

1. **区间预约(谁规划经过何处,以及何时)。** 在 `RunCycleAsync` 内部,`CoordinationCycleService`（`Coordination/.../CoordinationCycleService.cs:37`）针对当前的预约视图规划每个 agent,并把它的路径作为区间独占租约预约到单例 `ReservationTable` 中。这在*规划时*协调车队：两个 agent 不会在同一资源上被授予重叠的 `[start,end)` 区间。
2. **执行器路权门控(最终的停-等)。** 仅有预约,其安全性只取决于它们所处的时间轴。步骤（2）中的门控是*执行时*的硬保证：一辆车物理上进入下一个 CP **只有当它在这个 tick 是空的且未被前面的移动者占用时**——否则它等待。因此,无论预约有何微妙之处,同一 CP 碰撞都是*从构造上*不可能发生的。

一个病态的僵持(门控无法解决的相互阻塞)会退化为 **`DidNotConverge`**——绝不会是崩溃,绝不会是碰撞。

### Why the tick clock matters (the wall-clock root cause)

门控保证了安全,但模拟的全部要点是验证*预约层*。只有当被预约的区间与被执行的 tick 处于**同一轴**上时,那一层才有意义。生产环境的 `SystemFleetClock` 报告 `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`（`TrafficControl/.../SystemFleetClock.cs:12`）——挂钟时间毫秒。一次模拟在亚毫秒内运行**许多周期**,所以两个被表认为时间上分离的预约可能在同一个 tick 落在同一个 CP 上：预约轴(毫秒)与执行轴(tick)是*解耦*的。`ManualFleetClock` 修复了这一点（`ManualFleetClock.cs`）：驱动器在每个周期之前设置 `NowMs = tick`（`ManualFleetClock.cs:24`）,所以一个 tick 的执行等于一个单位的预约时间。这正是让预约表的区间无碰撞在执行时成为*真正的*保证、并让运行可复现的关键。

### Liveness & the throughput trade-off

- **诚实的非收敛。** 当网格密集时,整条路径的预约会把 agent 串行化,而保守的门控让后随车辆等待。如果车队在 `maxTicks` 内仍未全部到达,循环会设置 `DidNotConverge` 并跳出（`FleetLoopDriver.cs:180-185`）,而不是碰撞或悄悄截断。密集实例因此会如实*报告*该局限。
- **停放车辆改道**(步骤（2）.1 + `parkedCells`)是活性的逃生阀：一个停在目标 CP 上的已完成 agent 成为规划器障碍,所以车队的其余部分绕过它,而不是永远死锁在它后面。
- **执行感知 planner。** Dijkstra 使用保守门控，因此后随车辆可能等待一个 tick 让前方 cell 清空；SIPP 使用计划中的 CP 进入 tick，通过在时间中路由而非在门控处反应来收回吞吐量。

`FleetLoopStatus`（`FleetLoopDriver.cs:28-38`）：`Completed`(全部到达,无碰撞)、`CollisionDetected`(回归信号——绝不应发生)、`DidNotConverge`(tick 预算耗尽：活锁 / 死锁 / 饥饿)。

---

## 5. `SimulationService` — scenario generation & DTO mapping

`SimulationService.RunAsync`（`SimulationService.cs:30-66`）编排一次运行：

1. **校验**（`SimulationService.cs:68-84`）：`Width ≥ 1`、`Height ≥ 1`、`AgvCount ≥ 1`,以及关键不变量 **`Width*Height ≥ 2*AgvCount`**——网格必须为每个 AGV 容纳一个*独立的起点*和*一个独立的目标*。违反会抛出 `ArgumentException`(→ HTTP 400)。
2. 通过 `GridFieldFactory.BuildGrid(w, h)` **构建网格**(见下文)。
3. **播种独立的起点/目标**,使用一个被播种的 `Random(request.Seed ?? DefaultSeed)`,其中 `DefaultSeed = 1469` 让无 seed 的请求保持可复现（`SimulationService.cs:13, 41`）。它对所有 site id 进行 Fisher–Yates **洗牌**（`Shuffle`,`SimulationService.cs:152-160`）,并取两个不相交的块——`shuffled[i]` 作为起点,`shuffled[AgvCount + i]` 作为目标（`SimulationService.cs:45-50`）。在已校验的容量不变量下,两个不相交的切片保证了*每个起点 ≠ 其目标***并且**所有起点/目标两两不同。Agent `i` 得到 id `agv-{i+1}` 与 `Priority: i`。
4. 为这个请求**获取一个全新的引擎**：`await using var engine = _engineFactory.Create(field.Graph, request.Planner)`——隔离见 §2，并按请求选择 planner。
5. **运行循环**,`maxTicks = ((Width+Height) * (AgvCount+1) * 2) + 100`——传入 `advanceClock: engine.Clock.SetTick`，并为 SIPP 选择 `FleetExecutionMode.ScheduleFaithful`，为 Dijkstra 选择 `FleetExecutionMode.Greedy`。
6. 把 `FleetLoopResult` **映射**为 `SimulationResultDto`：sites → `SiteDto`,每条有向的 `RoadmapGraph` 边 → `LaneDto`,每个 agent 的已占用轨迹 → `AgentDto.PathSiteIds`，未完成前方路线 → `AgentDto.RemainingSiteIds`,每一帧 → `FrameDto`/`PositionDto`,以及 `FleetLoopStats` → `StatsDto`。

`GridFieldFactory.BuildGrid(w, h)`（`GridFieldFactory.cs:26-63`）构建一个由 `WorkSite` 控制点组成的矩形路网,id 遵循 `r{row}c{col}` 约定（`GridFieldFactory.cs:66`）,定位于 `MapPosition(X=col, Y=row)`。每个无向的 4 邻接关系都变成**一对单位距离的有向 `MapLine`**(双向,`AddBidirectional`,`GridFieldFactory.cs:68-72`),然后 `RoadmapGraph.Build(mapSites, lines)`。它返回一个 `GridField(Width, Height, Graph, Sites)`——面向引擎的图加上渲染元数据。

**注册**（`SimulationServiceCollectionExtensions.cs:14-23`）：`AddSimulation()` 把 `GridFieldFactory` 和 `FleetLoopDriver` 注册为单例(两者都是无状态的),把 `ISimulationService → SimulationService` 注册为 scoped。Host 提供 `ISimulationEngineFactory → InMemorySimulationEngineFactory`(scoped)并调用 `AddSimulation()`（`Program.cs:79-80`）。Application 刻意**不**了解引擎是如何接线的——那个组合属于 Host/Infra。

---

## 6. HTTP contract

**端点**（`SimulationController.cs:21-35`）：`POST /api/simulation/run`。遇到 `ArgumentException` 时它返回 `400` 并带 `ProblemDetails`;否则返回 `200` 并带结果。响应由 Host 默认的 `AddControllers()` 序列化（`Program.cs:41`）——未设置自定义 `JsonNamingPolicy`,所以应用 System.Text.Json 的默认 **camelCase**。前端回放所返回的时间线。

**请求** —— `SimulationRequest`（`SimulationRequest.cs:15`）:
```jsonc
{ "width": 8, "height": 8, "agvCount": 4, "seed": 1469, "planner": "Sipp" }
// seed optional → DefaultSeed; planner optional → raw backend contract defaults to Dijkstra
```

**响应** —— `SimulationResultDto`（`SimulationResultDto.cs`）,camelCase JSON：
```jsonc
{
  "field": {                       // FieldDto
    "width": 8, "height": 8,
    "sites": [ { "id": "r0c0", "x": 0, "y": 0, "type": "WorkSite" }, ... ],   // SiteDto[]
    "lanes": [ { "id": "r0c0-r0c1", "from": "r0c0", "to": "r0c1" }, ... ]      // LaneDto[] (directed)
  },
  "agents": [                      // AgentDto[]
    { "id": "agv-1", "startSiteId": "r0c0", "goalSiteId": "r7c7",
      "colorIndex": 0,
      "pathSiteIds": ["r0c0", "r0c1", ...],                                    // occupied trail
      "remainingSiteIds": [] }                                                  // road ahead when not arrived
  ],
  "timeline": {                    // TimelineDto — what the frontend replays
    "tickCount": 31,
    "frames": [                    // FrameDto[] — one per tick (incl. tick 0 and any colliding tick)
      { "tick": 0, "positions": [
          { "agentId": "agv-1", "siteId": "r0c0", "x": 0, "y": 0, "state": "Waiting" }, ...  // PositionDto
      ] }, ...
    ]
  },
  "stats": {                       // StatsDto
    "ticks": 30, "collisions": 0, "arrived": 4, "replans": 2,
    "status": "Completed",         // "Completed" | "CollisionDetected" | "DidNotConverge"
    "collisionTick": null,         // set only when status == CollisionDetected
    "collisionAgentIds": null,     // the agents involved, else null
    "redirects": 0,
    "recoveries": 0,
    "flowtimeTicks": 42
  }
}
```

`PositionDto.State` 是字符串化的 `AgentMotionState`：`Waiting`(尚未被授予路权,坐在起点)、`Moving`(在途)、`Arrived`（`SimulationResultDto.cs:43-47`）。前端通过步进 `timeline.frames` 并在 `(x,y)` 处绘制每个 `position`(由该 agent 的 `colorIndex` 着色)来做动画。

---

## 7. Tests

闭环集成测试位于 `tests/SwarmRoute.Integration.Tests/ClosedLoopIntegrationTests.cs`,并演练这个完全相同的驱动器——它们**直接**调用 `FleetLoopDriver.RunToCompletionAsync`(生产方法就是从这些测试中抽取出来的),在一个真实的 `RoadmapGraph` 之上,用一个 `ManualFleetClock`（`advanceClock: clock.SetTick`）接线**真实的**协调 host（`FakeRoadmapQueryService` 的链/交叉路口,以及 `GridFieldFactory` 网格）。三个场景,每个都断言 `Stats.Collisions == 0`、`Stats.Arrived == fleet.Count`,以及**无租约泄漏**（`AssertNoLeasesLeak`）:

- `ClosedLoop_IndependentAgents_AllReachGoals_InParallel_NoCollision_NoLeak` —— 不相交的走廊;还断言 `MaxConcurrentEnRoute >= 2`(真正的并行)。
- `ClosedLoop_IntersectionCrossing_SerialisedThroughCentre_BothReachGoals_NoCollision_NoLeak` —— 一个共享的 `+` 交叉路口,通过中心串行化。
- `ClosedLoop_FourAgents_PerimeterRotation_AllReachGoals_NoCollision_NoLeak` —— 四个 agent 围绕一个 4×4 周界旋转四分之一圈;断言并发。

`SippClosedLoopTests` 演练同一个内存真实引擎下的 v1 路径：SIPP 在可解密度下收敛，在密集种子上优于 Dijkstra/greedy 基线，重规划次数显著更少，并且固定 seed 下确定。

所有收敛场景都断言 `Completed`/零碰撞。`DidNotConverge` 路径和 `CollisionDetected` 回归信号是驱动器(§4)的一等结果,通过 `StatsDto` 浮现出来,而非被抛出。

---

## 8. v0/v1 状态

**v0（已闭环）。** 锁步、离散 tick 的执行：一个 tick = 一次 CP 跳跃,通过真实的 `CoordinationCycleService` 进行整条路径的区间预约,加上一个**保守的路权门控**(后随车辆等待一个 tick 让前方的 cell 清空)以及停放车辆改道。它无碰撞且可复现(tick 时钟),并且把密集实例的局限如实报告为 `DidNotConverge`。

**v1（已闭环）。** SIPP 与 schedule-faithful 执行已经实现，并可按模拟请求选择。planner 搜索预约安全区间，执行器在 `ManualFleetClock` 轴上遵循计划 CP 进入 tick，同一套 DTO/回放契约暴露结果。Web 控制台默认请求 SIPP，同时保留 Dijkstra 作为 A/B 对照。

---

## Cross-context dependencies

| Depends on | What this context uses | Key types (`path:line`) |
|---|---|---|
| **SpatioTemporal.Kernel** (Shared) | 预约时间轴 + 资源引用 | `IFleetClock`（`Shared/.../IFleetClock.cs:7`,`NowMs:10`）、`ResourceRef`/`ResourceKind`（`Shared/.../ResourceRef.cs:30, 8`;`CP`、`Lane`）、`TimeInterval` `[StartMs,EndMs)`（`Shared/.../TimeInterval.cs:11`）、`SpaceTimePath` |
| **Map.Domain** | 路网图 + 网格 sites/lines | `RoadmapGraph.Build`（`Map/.../RoadmapGraph.cs:37`）、`.SiteRef`（`:132`）、`.Vertices`（`:75`）、`.Neighbours`（`:87`）;`MapSite`/`MapLine`/`MapPosition`/`MapSiteType.WorkSite` |
| **Map.Application.Contract** | 路网数据源接缝(被换成内存实现) | `IRoadmapQueryService` —— 由 `InMemoryRoadmapQueryService` 实现 |
| **Coordination.Application** | 规划+预约+释放周期(引擎) | `IFleetCoordinationCycle.RunCycleAsync` / `.ReleaseAsync`（`Coordination/.../IFleetCoordinationCycle.cs:22, 33`）、`AgentGoal`（`:AgentGoal.cs:9`）、`CycleReport`/`AgentCycleResult`（`Results`、`Reserved`、`Path`、`Attempts`）、`CoordinationCycleService`（`:CoordinationCycleService.cs:37`）、`AddCoordination`（`:CoordinationServiceCollectionExtensions.cs:39`） |
| **PathPlanning** (via DI) | 注册进每请求容器的规划器 | `PathPlanningNativeInjectorBootStrapper.RegisterServices`(Host 工厂) |
| **TrafficControl** (via DI) | 单例 `ReservationTable` + 默认时钟(被替换) | `TrafficControlNativeInjectorBootStrapper.RegisterServices`、`ReservationTable` 单例（`TrafficControl/.../TrafficControlNativeInjectorBootStrapper.cs:74`）、`SystemFleetClock`（`TrafficControl/.../SystemFleetClock.cs:12`） |
| **EventBus** | 引擎内部的内存派发 | `AddEventBus()` |
| **Host** (downstream) | 组合引擎 + 暴露 HTTP API | `InMemorySimulationEngineFactory`（`host/.../Adapters/InMemorySimulationEngineFactory.cs`）、`SimulationController`（`host/.../Controllers/SimulationController.cs`）、注册（`host/.../Program.cs:79-80`） |

*项目引用：`SwarmRoute.Simulation.Application.csproj` 只引用 Kernel、Map.Domain、Map.Application.Contract 和 Coordination.Application。PathPlanning/TrafficControl 的 Infra 是通过 **Host** 工厂(它调用它们的 bootstrapper)传递性地拉入的,使 Application 程序集免于 Infra/EF 依赖。*
