# Deadlock Handling (死鎖處理)

> 简体中文 · English version: [README.md](README.md)

*一个反应式限界上下文，从 TrafficControl 的预约争用图中检测循环等待死锁并请求解决——它从不持有预约，也不规划路径。*

---

## 1. 目的与职责

Deadlock 上下文回答唯一一个问题：**“在当前谁*拥有*、谁*等待*哪些资源的情况下，机群是否陷入了循环等待，如果是，谁应当让步？”**

它是一个**纯分析器**。具体来说它会：

- **读取**一份时间点上的 `ResourceAllocationGraphSnapshot`（即*拥有*/*等待*边），由 TrafficControl 产出——它从不修改 TrafficControl，从不持有其锁，也从不持久化状态。接缝处的注释说得很明确：*"TrafficControl produces it; Deadlock consumes it (never mutating, never holding TrafficControl locks)"*（`Shared/SwarmRoute.SpatioTemporal.Kernel/ResourceAllocationGraphSnapshot.cs:4`）。
- 通过构建资源分配图(RAG)并在其上运行环检测来**检测**循环等待（`SwarmRoute.Deadlock.Domain/Services/RagDeadlockDetector.cs:24`）。
- **决定**一个牺牲者 + 策略，并通过抛出集成事件来**请求**解决；由*机群*（Coordination / TrafficControl / PathPlanning）做出反应。绕道预约本身则通过一处接缝委派回 TrafficControl 正常的 `TryReserve` 路径，从而保证恢复动作永远不会制造新的碰撞（不变量 **I1**）。

它明确**不做**的事：

- 它**不**持有或授予预约——那是 TrafficControl 的职责（解决用的绕道会经由 `IDetourReservationService` → TrafficControl 走回去）。
- 它**不**规划路径——那归 PathPlanning 所有。
- 它**没有** EF/DbContext：死锁是瞬态的，因此案例被建模为聚合仅仅是为了使用领域事件通道和乐观并发约定；什么都不存储（`SwarmRoute.Deadlock.Domain/Aggregates/DeadlockCase.cs:14`）。

### 来源（AJR.MAPF 移植）

本上下文是对原始 `AJR.MAPF` 死锁子系统的整洁架构重新实现：

| AJR.MAPF 原始 | SwarmRoute Deadlock |
| --- | --- |
| `MapResourceAllocationGraph.GenerateGraph` | `ResourceAllocationGraph.Build()`（`.../ValueObjects/ResourceAllocationGraph.cs:107`） |
| `ConflictDetect.IndependenceDetection.DeadlockDetect` | `IDeadlockDetector` / `RagDeadlockDetector`（`.../Services/RagDeadlockDetector.cs`） |
| `CyclesDetector.CyclicVertices(graph, "agent_")` | 原样复用（`src/vendor/SwarmRoute.Algorithms/Graphs/CyclesDetector.cs:175`） |
| 桩实现的 `ISolver.Solve()` + `Recover()`、`ConflictSolveStateMachine` | `IDeadlockResolver` + `AvoidancePlan` 状态机 |
| “go to avoid point” 恢复 | `ResolutionStrategy.SendToAvoidSite` |

---

## 2. 分层与项目

六个项目，整洁架构分层（依赖指向内层）。只有 `Domain` 和 `Application.Contract` 携带对共享 Kernel 的编译期依赖——本上下文可**独立**构建（与 TrafficControl 之间不存在编译期边）。

| 项目 | 角色 | 依赖于 |
| --- | --- | --- |
| `SwarmRoute.Deadlock.Domain.Shared` | 枚举（`DeadlockKind`、`DeadlockCaseStatus`、`ResolutionStrategy`、`AvoidancePlanStep`）+ `DeadlockErrorCodes`。 | —（无） |
| `SwarmRoute.Deadlock.Domain` | RAG 值对象、环检测、聚合、领域服务 + 集成接缝、领域事件。 | Kernel、`Domain.Abstractions`、`Domain.Shared`、NetDevPack、`SwarmRoute.Algorithms`（graph + `CyclesDetector`） |
| `SwarmRoute.Deadlock.Application.Contract` | `IDeadlockAppService` + DTO（`DeadlockReportDto`、`DeadlockCycleDto`）。 | Kernel |
| `SwarmRoute.Deadlock.Application` | `DeadlockAppService` 编排器、`AllocationContendedSubscriber`、`IDeadlockSnapshotProvider` 消费者接缝。 | `Domain`、`Application.Contract` |
| `SwarmRoute.Deadlock.Infra.CrossCutting.IoC` | `DeadlockNativeInjectorBootStrapper` DI 注册。 | `Application` |
| `SwarmRoute.Deadlock.Tests` | xUnit 单元测试。 | `Domain`、`Application` |

关键外部类型：图相关机制位于 `AJR.Platform.Algorithms.*` 命名空间下，但在本地按 `src/vendor/SwarmRoute.Algorithms*` 进行了内联引入（`DirectedSparseGraph<T>`、`CyclesDetector`）。跨上下文的词汇（`ResourceRef`、`ResourceKind`、`ResourceAllocationGraphSnapshot`）来自**冻结的** `SwarmRoute.SpatioTemporal.Kernel`。

---

## 3. 领域模型

### `ResourceAllocationGraph`（值对象）— `.../ValueObjects/ResourceAllocationGraph.cs`

RAG 是一个**不可变值对象**（`ValueObject`，按排序后的 owns/waits 边集合判等——`:152`）。它将冻结的 `ResourceAllocationGraphSnapshot` 适配成一张供环检测运行的有向图。`FromSnapshot` 校验每个 agent/resource id 非空白并对其进行 trim（`:71`、`:81`）。

它携带两族边，对应 AJR 源中的三族顶点（`agent_`、`occupySite_`、`applySite_`——`:39`）：

- **拥有**（已持有）：`occupySite_<resource> → agent_<owner>`——“此资源由该 agent 持有”（`:140`）。
- **等待**（请求）：`agent_<waiter> → occupySite_<resource>`——“此 agent 被该资源阻塞”（`:144`）。

关键的建模决策（忠实于 AJR）：拥有边与等待边**都**以每个资源**单一共享**的 `occupySite_` 顶点为枢轴，从而使 `agent → resource → agent → resource → …` 这样的路径能够闭合成一个环。`applySite_` 标记顶点是为保真/可观测性而添加的，但**不携带任何边**（`:133`）。`ResourceKey` 将资源命名空间化为 `Kind:Id`，因此同名 id 的 CP 与 Lane 是两个不同的顶点（`:150`）。

`Build()` 是两阶段的（先顶点，后边），因为内联引入的 `DirectedSparseGraph<T>.AddEdge` 在某个端点顶点缺失时会静默返回 `false`（`src/vendor/SwarmRoute.Algorithms.DataStructures/Graphs/DirectedSparseGraph.cs:167`）——先添加全部顶点可保证每条边都能落地。

#### ASCII：一个 2-agent 的循环等待

`A` 拥有 `r1` 并想要 `r2`；`B` 拥有 `r2` 并想要 `r1`：

```
        owns                       wait
   ┌───────────────┐      ┌────────────────────┐
   ▼               │      ▼                     │
occupySite_CP:r1   │   occupySite_CP:r2         │
   │               │      │                     │
   │ owns          │ wait │ owns          wait  │
   ▼               │      ▼                     │
 agent_A ──────────┘    agent_B ────────────────┘
   (A waits r2)            (B waits r1)

Edges actually built:
  occupySite_CP:r1 → agent_A        (ownership)
  occupySite_CP:r2 → agent_B        (ownership)
  agent_A          → occupySite_CP:r2   (wait-for)
  agent_B          → occupySite_CP:r1   (wait-for)

Cycle through agent vertices:
  agent_A → occupySite_CP:r2 → agent_B → occupySite_CP:r1 → agent_A
```

`CyclesDetector.CyclicVertices(graph, "agent_")` 返回 `[agent_A, agent_B]`。（由 `ResourceAllocationGraphTests.Build_ProducesGraphThatCycleDetectorFlags` 验证。）

### `DeadlockCycle`（值对象）— `.../ValueObjects/DeadlockCycle.cs`

一个循环等待中的 agent id 集合，存储时**不带** `agent_` 前缀、去重，并**按 ordinal 升序排序**，因此其标识与发现顺序无关、确定可复现（`:37`）。`FromVertices` 从 RAG 顶点名中剥离 `agent_` 前缀（`:55`）。正是这种稳定的排序使得牺牲者选择可复现（见 §6）。

### 环检测算法 — `RagDeadlockDetector`（`.../Services/RagDeadlockDetector.cs`）

两个阶段：

1. **忠实的 AJR 移植。** 构建 RAG，运行 `CyclesDetector.CyclicVertices(graph, "agent_")`（`:30`）。内联引入的检测器执行一次带递归栈的有向 DFS，并标记每一个*从其可达某个环*的 `agent_` 顶点（`src/vendor/.../CyclesDetector.cs:175`）。这是一种**过近似**：一个仅仅排在死锁*后面*的饥饿等待者也会被标记，即便它并不属于任何相互等待。

2. **SwarmRoute 精化——划分为真正的环**（`PartitionIntoCycles`，`:63`）。为使解决具备可操作性，并满足*“两个独立的环必须分别报告”*，检测器构建限定在环内 agent 上的 **agent 阻塞有向图**——当 `a` 等待某个 `b` 所拥有的资源时存在边 `a → b`（`:84`）——并返回其**非平凡强连通分量**：规模 ≥ 2 的 SCC，或带有自阻塞边的单点（`:99`）。SCC 由一个迭代式（基于栈、无递归深度风险）的 **Tarjan 强连通分量**实现求得（`StronglyConnectedComponents`，`:121`），采用确定的 ordinal 迭代顺序。平凡的单点（即饥饿等待者）会被丢弃。

所报告的环按其最小成员 agent id 排序（`:46`），因此检测在**重复运行间是确定的**（由 `DeadlockDetectorTests.Detection_IsDeterministic_AcrossRepeatedRuns` 断言）。

### 聚合

**`DeadlockCase`**（`.../Aggregates/DeadlockCase.cs`）——一个被检测到的死锁所对应的聚合根。生命周期 `Detected → Resolving → Resolved | Escalated`：

- `Detect(cycle)` 以 `Detected` 状态开启一个案例并抛出 `DeadlockCaseDetectedEvent`（`:68`）。
- `RequestResolution(victim, strategy, suggestedAvoidTarget?)`：`Detected → Resolving`，抛出 `DeadlockCaseResolutionRequestedEvent`；拒绝不在环内的牺牲者（`:105`、`:115`）。
- `MarkResolved()`：`Resolving → Resolved`，抛出 `DeadlockCaseResolvedEvent`（`:130`）。
- `Escalate(reason?)`：`Detected/Resolving → Escalated`，幂等（`:146`）。
- 按惯例携带 `StateVersion`（受检自增的乐观并发）（`:160`）。

**`AvoidancePlan`**（`.../Aggregates/AvoidancePlan.cs`）——仅向前推进的恢复状态机（移植 AJR `ISolver.Solve+Recover` / `ConflictSolveStateMachine`）：

```
SelectVictim → SelectAvoidancePoint → ReserveDetour → DispatchToAvoid → ConfirmCleared → Recover → Completed
                                                                                                  ↘ Aborted (terminal failure)
```

每个 `RecordX`/`AdvanceX` 都会强制检查预期的当前步骤（`Expect`，`:152`）并递增 `StateVersion`。该聚合与传输无关：它记录的是*决定了什么*（牺牲者、绕道点）以及*恢复进展到何处*；副作用由 resolver 经由接缝执行，再反馈回来。

---

## 4. 反应式流程（事件驱动）

整个上下文由**一个**入站集成事件触发，并发出**三个**出站事件，全部经由进程内事件总线。

```
TrafficControl.ReservationTable
    │  request queued behind a held resource (or a stale request aged)
    │  AddDomainEvent(new AllocationContendedEvent(...))      [ReservationTable.cs:168 / :429]
    ▼
"TrafficControl.Allocation.Contended"  (IIntegrationEvent, v1)
    │  in-process bus → handler.CanHandle / HandleAsync
    ▼
AllocationContendedSubscriber.HandleAsync          [Application/Subscribers/AllocationContendedSubscriber.cs:42]
    │  (1) re-entrancy guard (AsyncLocal ScanDepth)  ── §see note
    │  (2) snapshot = IDeadlockSnapshotProvider.GetSnapshotAsync()
    ▼
DeadlockAppService.ScanAsync(snapshot)             [Application/Services/DeadlockAppService.cs:48]
    │  cycles = IDeadlockDetector.Detect(snapshot)
    │  for each cycle:
    │     case = DeadlockCase.Detect(cycle)         ── raises Deadlock.Case.Detected
    │     IDeadlockResolver.SolveAsync(case)        ── raises Deadlock.Case.ResolutionRequested
    │                                                   (and Resolved on recovery, or Escalate)
    │  drain case.DomainEvents → IIntegrationEventPublisher.PublishAsync(...)
    ▼
Published integration events (consumed by Coordination / TrafficControl / PathPlanning):
    • "Deadlock.Case.Detected"             (DeadlockCaseDetectedEvent,            v1)
    • "Deadlock.Case.ResolutionRequested"  (DeadlockCaseResolutionRequestedEvent, v1)  → victim + suggested avoid target
    • "Deadlock.Case.Resolved"             (DeadlockCaseResolvedEvent,            v1)
```

**实际事件类型与名称**（均为 `DomainEvent, IIntegrationEvent`，`Version = "v1"`）：

| 方向 | `EventName` | 类型 | 载荷 |
| --- | --- | --- | --- |
| **入** | `TrafficControl.Allocation.Contended` | `AllocationContendedEvent`（`TrafficControl/.../Events/AllocationContendedEvent.cs`） | reservationTableId, agentId, contendedRequestCount |
| 出 | `Deadlock.Case.Detected` | `DeadlockCaseDetectedEvent`（`.../Events/DeadlockCaseDetectedEvent.cs`） | caseId, kind, agentIds |
| 出 | `Deadlock.Case.ResolutionRequested` | `DeadlockCaseResolutionRequestedEvent` | caseId, victimAgentId, strategy, suggestedAvoidTarget |
| 出 | `Deadlock.Case.Resolved` | `DeadlockCaseResolvedEvent` | caseId, victimAgentId |

**订阅者**（`AllocationContendedSubscriber.cs:19`）实现 `IIntegrationEventHandler`。`CanHandle` 严格按 `EventName == "TrafficControl.Allocation.Contended"` 匹配（`:37`）。**v0 忽略载荷**——任何争用都会触发一次完整的重新扫描（`:57` 注释）。

**重入保护。** 一次绕道预约本身可能经由进程内总线同步发布 `Allocation.Contended`；`AsyncLocal<int> ScanDepth` 会将嵌套扫描短路为 `DeadlockReportDto.Empty`，从而保证正在进行中的一次解决不会递归地开启第二个案例（`AllocationContendedSubscriber.cs:23`、`:61`）。这是整个流程中最微妙的一处，并有一个专门的测试覆盖（见 §8）。

**“Commit” 角色。** 由于本上下文没有 DbContext，`DeadlockAppService` 扮演了别处由 `BaseDbContext.Commit()` 扮演的角色：在运行完各案例后，它从每个案例中抽干 `Entity.DomainEvents`，并自行将被标记为集成事件的子集交给 `IIntegrationEventPublisher.PublishAsync`（`DeadlockAppService.cs:79`）。

---

## 5. 快照接缝

`IDeadlockSnapshotProvider`（`.../Application/Abstractions/IDeadlockSnapshotProvider.cs:14`）是**消费者侧**接缝——Deadlock 声明它，以便在不对 TrafficControl 产生编译期依赖的情况下保持可构建：

```csharp
Task<ResourceAllocationGraphSnapshot> GetSnapshotAsync(CancellationToken ct = default);
```

- **独立默认：** `NullDeadlockSnapshotProvider` 返回一份空的（健康的）快照 `new ResourceAllocationGraphSnapshot([], [])`（`:25`）。
- **集成适配器（Host）：** `TrafficSnapshotDeadlockAdapter`（`host/SwarmRoute.Host/Adapters/TrafficSnapshotDeadlockAdapter.cs`）将异步的 Deadlock 接缝桥接到 TrafficControl **权威的、同步的** `ITrafficControlSnapshotProvider.GetSnapshot()`（`TrafficControl/.../Services/ITrafficControlSnapshotProvider.cs:11`），用 `Task.FromResult` 包裹。TrafficControl 是唯一的写者；Deadlock 只读。在那里，`Owns` = 每个活跃租约一条边，`Waits` = 每个排队/争用请求一条边。
- **测试适配器：** `AllocationContendedSubscriberTests.EmptySnapshotProvider` 是测试中的替身，返回一份空快照。

双方约定的形状是冻结的 Kernel 记录 `ResourceAllocationGraphSnapshot(Owns, Waits)`，其中每条边为一个 `(string AgentId, ResourceRef Resource)`。

---

## 6. 解决

解决由 `IDeadlockResolver` → `AvoidanceDeadlockResolver`（`.../Services/AvoidanceDeadlockResolver.cs`）编排，它驱动一个 `AvoidancePlan` 走过 AJR 的避让/恢复状态机。

**`SolveAsync(case)`**（`:40`）：

1. **SelectVictim** — `IVictimSelector` → `DeterministicVictimSelector`。启发式：在最小的环上操作；在其中选取**字典序最小（ordinal）的 agent id**，由于环已预排序，这就是简单的 `cycle.AgentIds[0]`（`DeterministicVictimSelector.cs:30`）。设计上即确定——*同一个死锁总是提名同一个牺牲者*，这正是防止**活锁**的关键（R6）。
2. **SelectAvoidancePoint** — `IAvoidancePointSelector.SelectAvoidancePoint(victim)`。
3. `case.RequestResolution(victim, SendToAvoidSite, avoidSite)` ——**无条件**抛出，以便 Coordination 始终知晓预定的牺牲者，*即便*绕道随后失败（`:59`）。
4. 若无绕道点 → `plan.Abort` + `case.Escalate`（`:61`）。
5. **ReserveDetour** — `IDetourReservationService.TryReserveDetourAsync(victim, avoidSite)`；被拒绝时 → abort + escalate（`:72`）。
6. **DispatchToAvoid** — 记录已派发；plan 现在处于 `ConfirmCleared`。

**`Recover(case, plan)`**（`:88`）：仅可从 `ConfirmCleared` 进入；检查 `IClearanceConfirmer.IsCleared(victim)`；成功时走过 `ConfirmCleared → Recover → Completed` 并 `case.MarkResolved()`（抛出 `Deadlock.Case.Resolved`）。

### 策略

`ResolutionStrategy`（`Domain.Shared/Enums/ResolutionStrategy.cs`）：**`SendToAvoidSite`（v0 基线，已实现）**；`Preempt` 与 `Requeue` 已声明但*预留供后续演进*（v0 中未使用）。

### 集成接缝——何为桩、何为已实现

有三处接缝在 Deadlock **领域**中声明，但刻意未在此处提供生产实现（真正的工作归 TrafficControl/Map 所有）。每一处都附带一个 `Null*` 默认实现：

| 接缝 | 独立默认（在本上下文中） | Host 适配器（已集成） |
| --- | --- | --- |
| `IAvoidancePointSelector` | `NullAvoidancePointSelector` → 始终为 `null` → resolver 升级处理（`NullIntegrationSeams.cs:9`） | `MapAvoidancePointSelector`——从活跃路网图中挑选一个空闲的 `AvoidSite`/`RelaySite`，遵守拓扑闭合 + 黑名单，ordinal 确定（`host/.../Adapters/MapAvoidancePointSelector.cs`） |
| `IDetourReservationService` | `NullDetourReservationService` → 始终为 `false`（`:20`） | `TrafficDetourReservationAdapter`——经由 TrafficControl 的 `ITrafficCoordinatorAppService.TryReserveAsync` 在一个有界的 60 s 窗口内预约绕道点，因此绕道遵守每一份租约且不会碰撞（`host/.../Adapters/TrafficDetourReservationAdapter.cs`） |
| `IClearanceConfirmer` | `NullClearanceConfirmer` → 乐观地 `true`（`:35`） | *（Host 中尚未覆盖——v0 中仍为乐观）* |

因此在**独立**构建中，`SolveAsync` 总是升级处理（无绕道点）——但牺牲者/策略以及 `ResolutionRequested` 事件*仍会产出*，这正是“Deadlock 分析，机群行动”这一预期契约。绕道适配器自身的注释指出，v0 只是一个有界的目的地保持，而非完整的“到绕道点的路径”预约，因为牺牲者的当前位姿属于一个尚未就位的机群状态/派发集成。

---

## 7. 组装 / 装配

`DeadlockNativeInjectorBootStrapper.RegisterServices(...)`（`.../Infra.CrossCutting.IoC/DeadlockNativeInjectorBootStrapper.cs`）遵循惯例的 `*NativeInjectorBootStrapper` 约定，同时提供 `WebApplicationBuilder` 重载（`:31`）和一个与 web 无关的 `IServiceCollection` 重载（`:42`）。`RegisterCore`（`:49`）：

- **领域（始终具体）：** `IDeadlockDetector → RagDeadlockDetector`、`IVictimSelector → DeterministicVictimSelector`、`IDeadlockResolver → AvoidanceDeadlockResolver`（`AddScoped`）。
- **集成接缝经由 `TryAdd`：** `IAvoidancePointSelector`、`IDetourReservationService`、`IClearanceConfirmer`、`IDeadlockSnapshotProvider` 获得各自的 `Null*` 默认实现——因此本上下文可完全独立解析，**并且** Host 只需注册一个真实适配器即可覆盖任意一处（由于 bootstrapper 用的是 `TryAdd`，显式注册会胜出）。
- **应用：** `IDeadlockAppService → DeadlockAppService`；`AllocationContendedSubscriber` 既被具体注册，*也*经由工厂委托注册为 `IIntegrationEventHandler`（`:65`），以便进程内总线通过 `GetServices<IIntegrationEventHandler>()` 发现它。
- **刻意不在此处注册：** `IIntegrationEventPublisher`——归 EventBus/Host 装配所有（`:27`）。

**Host 顺序很重要**（`host/SwarmRoute.Host/Program.cs`）：`AddEventBus()`（第 1 步，提供进程内的发布者 + 分发器） → `DeadlockBootStrapper.RegisterServices`（第 5 步，`TryAdd` 的 Null 接缝） → 第 6b 步在 bootstrapper *之后*显式注册 `TrafficSnapshotDeadlockAdapter`、`MapAvoidancePointSelector`、`TrafficDetourReservationAdapter`，从而覆盖那些 null（`Program.cs:69-71`）。

进程内发布者（`Shared/SwarmRoute.EventBus/InProcessIntegrationEventPublisher.cs`）过滤出 `IIntegrationEvent`，取出作用域内所有的 `IIntegrationEventHandler`，并经由 `CanHandle`/`HandleAsync` 分发——它会吞掉/记录处理器异常，从而保证某个失败的订阅者不会打断发布循环（`:53`）。日后 CAP/RabbitMQ 宿主可以将同一个处理器绑定到同一个事件名，而无需更改任何应用代码。

---

## 8. 测试（`SwarmRoute.Deadlock.Tests`，xUnit）

纯内存测试——无宿主、无 DB。一个流式的 `SnapshotBuilder` 制造 RAG 快照（`SnapshotBuilder.cs`，含为规范的 n-agent 环准备的 `SnapshotBuilder.Cycle(n)`）；`Fakes.cs` 提供一个 `CapturingIntegrationEventPublisher` 以及若干“已集成”的伪实现（`FixedAvoidancePointSelector`、`AlwaysGrantDetourReservationService`、`StubClearanceConfirmer`）。

| 文件 | 覆盖内容 |
| --- | --- |
| `ResourceAllocationGraphTests` | RAG 构建：agent/`occupySite`/`applySite` 顶点 + 拥有/等待边；resource key 包含 `Kind`；值判等基于边集合（与顺序无关）且能区分 Owns 与 Waits；所构建的图正是 `CyclesDetector` 所标记的那张图。 |
| `DeadlockDetectorTests` | 2/3/4-agent 环标记出全部成员；无环快照 → 无；人人持有、无人等待 → 无；自环（同一资源既被拥有又被等待）被确定地标记；**两个独立的环分别报告**；一个额外的非环等待者（`Z`）被排除；**重复运行间的确定性**；null 快照抛异常。 |
| `VictimSelectionTests` | 最小 ordinal id 的牺牲者，与输入顺序无关而稳定，按 ordinal（非数值/长度）比较（`"10" < "2"`），重复调用结果一致。 |
| `DeadlockCaseTests` | 生命周期：`Detect` 抛出 `Deadlock.Case.Detected`；`RequestResolution` → `Resolving` + 事件，拒绝不在环内的牺牲者；`MarkResolved` 从 `Resolving`（且从 `Detected` 抛异常）；`Escalate` 幂等；空环被拒绝。 |
| `AvoidancePlanTests` | 起始于 `SelectVictim`/版本 1；走到 `Completed` 的完整顺利路径，每步递增版本；乱序转换抛异常；空白绕道点被拒绝；`Abort` 记录原因；终态时再 abort 为无操作。 |
| `DeadlockResolverTests` | 在已集成接缝下 → 牺牲者 `A` 被派发到绕道点，案例 `Resolving`；`Recover` 完成并解决；未清除时 recover 不做任何事；**无绕道点 → 升级处理（仍选出牺牲者）**；**绕道被拒 → 升级处理**。 |
| `DeadlockAppServiceTests` | 健康快照 → 空报告，无任何发布；2-agent 环 → 开启案例、选出牺牲者、发布 `Detected` + `ResolutionRequested` 两者；两个独立的环 → 报告两个；**在没有已集成接缝时，牺牲者仍经由 `ResolutionRequested` 报告**（升级路径）。 |
| `AllocationContendedSubscriberTests` | **重入保护**：一次在扫描中途重新发布 `Allocation.Contended` 的预约*不会*触发嵌套扫描（`ScanCount == 1`）。 |

---

## 9. v0 状态与 v1 路线图

### v0 已实现

- 忠实于 `AJR.MAPF.MapResourceAllocationGraph` 的 RAG 构建，复用内联引入的 `CyclesDetector.CyclicVertices`。
- 基于 SCC 划分为独立、确定排序的环的**循环**死锁检测（过近似被正确剪除）。
- 确定的牺牲者选择（无活锁）。
- 带乐观并发版本控制的完整 `DeadlockCase` + `AvoidancePlan` 生命周期。
- 反应式触发（`AllocationContended` → 扫描）配以重入保护，以及三个出站集成事件。
- 用于快照读取、绕道点选择和绕道预约的真实 Host 适配器（经由 TrafficControl 的 `TryReserve`——不变量 I1 得到遵守）。

### 推迟 / 桩实现（v1+）

- **`DeadlockKind.Livelock`**——已声明但 v0 的 RAG 环检测*不予检测*（`DeadlockKind.cs:14`）。检测“在动但无净进展”需要时间/进展信号，而非静态 RAG。
- **`ResolutionStrategy.Preempt` / `Requeue`**——已声明、未使用；只有 `SendToAvoidSite` 接入了装配。
- **`IClearanceConfirmer`**——即便在 Host 中仍是乐观的 `NullClearanceConfirmer`；真正的确认应当重新取快照 / 重新检测该环确已清除。
- **绕道完整性**——`TrafficDetourReservationAdapter` 只预约一个有界的*目的地保持*，而非完整的“到绕道点的路径”，待机群状态/派发集成（牺牲者位姿）就位后再补。
- **持久化**——无；案例按设计为瞬态。若日后需要审计/历史，这些聚合已经对事件溯源友好。
- **传输**——仅进程内事件总线；处理器已被塑造成可让 CAP/RabbitMQ 绑定无需更改任何应用代码。

---

### 跨上下文依赖（小结）

- **消费（入）：** `TrafficControl.Allocation.Contended`（`AllocationContendedEvent`）——触发器；`ITrafficControlSnapshotProvider.GetSnapshot()`——RAG 读取，经由 `TrafficSnapshotDeadlockAdapter`；`ITrafficCoordinatorAppService.TryReserveAsync`——绕道写入，经由 `TrafficDetourReservationAdapter`；Map 的路网图（`IRoadmapRepository` + `IResourceTopology`、`MapSiteType.AvoidSite/RelaySite`）——绕道点选择，经由 `MapAvoidancePointSelector`。
- **产出（出）：** `Deadlock.Case.Detected`、`Deadlock.Case.ResolutionRequested`、`Deadlock.Case.Resolved`——由 Coordination / TrafficControl / PathPlanning 消费。
- **共享内核：** `SwarmRoute.SpatioTemporal.Kernel`（`ResourceRef`、`ResourceKind`、`ResourceAllocationGraphSnapshot`）和 `SwarmRoute.Domain.Abstractions.EventBus`（`IIntegrationEvent`、`IIntegrationEventHandler`、`IIntegrationEventPublisher`）。
