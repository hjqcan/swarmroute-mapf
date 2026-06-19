# Liveness（活性）

> 简体中文 · English version: [README.md](README.md)

*一个保证机群持续取得进展的限界上下文。它通过两种方式实现，两者都只做决策：在预约授予时**预防**循环等待（一处 TrafficControl 会查询的 RAG 环检测），并拥有同步的、分阶段的 **`ILivenessPolicy`**，用来解决预约表看不见的**物理**僵持（迎面互换、阻塞链、被停车封死的目标）。它从不持有预约，从不规划路径，也从不修改引擎状态——它只做决策。*

---

## 1. 目的与职责

预约表授予的是区间独占的格点/车道租约，因此它能让时间上不相交的计划无碰撞。但仅靠预约表仍有两类故障无法解决：

1. **授予时的循环等待。** 智能体 A 持有 r1 并请求 r2；B 持有 r2 并请求 r1。每条租约单独看都合法，但*等待图*已经闭合成环，谁都无法前进。
2. **执行时的物理僵持。** 两个智能体持有时间上不相交的预约，却在走廊里头对头（迎面互换），或形成阻塞链/环形旋转，或一辆*已完成*的车停在另一智能体目标的唯一进近格上。预约表看不到任何冲突——智能体只是再也不动了。

Liveness 拥有这两者的策略，且**只**拥有策略：

- **预防（建设性）。** `RagCycleDetector` 实现了 TrafficControl 的 `IWouldCloseCycleDetector`：在授予一条租约之前，它询问“这条新的等待边会闭合一个等待环吗？”，若会，则拒绝授予以促使规划器改道——循环等待从一开始就不会形成。同一个类还实现了 `IDeadlockDetector.Detect` 用于事后分析（把活的 RAG 划分成真正的环）。
- **物理僵持的解决（响应式，仅决策）。** 执行器每个 tick 的每个阶段会查询一次 `ILivenessPolicy.Evaluate`。它观察机群的物理状态，并以一组 `LivenessDirective`（指令）的形式返回最廉价的安全解决方案。机制由执行器执行（释放租约、移动位姿、调用集群规划器）；策略本身从不执行。

它明确**不做**的事：

- 它**不**持有或授予预约——那是 TrafficControl 的职责。预防是对一次授予的*否决*；解决则发出由执行器经其正常的预约/规划路径执行的*指令*。
- 它**不**规划路径——那归 PathPlanning 所有。`YieldAndReplan` / `SolveClusterJointly` 指令是请求*执行器*去重新规划或调用集群规划器。
- 它**纯且同步**：`Evaluate` 是 `LivenessSnapshot` 加上策略自身每次运行的工作记忆（在途停滞计数与一个跳数缓存）的确定性函数。无 I/O，不修改引擎状态。

### 历史（本上下文过去是什么）

本上下文最初是 `AJR.MAPF` *响应式*死锁子系统的移植——一个事件驱动的流程（`AllocationContended` → 扫描 → `DeadlockCase`/`AvoidancePlan` 聚合 → 在集成总线上 resolve/recover/escalate，配合 `IAvoidancePointSelector` / `IDetourReservationService` / `IClearanceConfirmer` 接缝）。**整个响应式流程已被移除。** 预防抢在它之前（一个从不形成的环无需恢复），而物理僵持现在通过 `ILivenessPolicy` 在执行器循环内被同步解决。RAG + `CyclesDetector` 机制之所以保留，是因为两个存活下来的角色（预防与检测）本质上正是环检测。如果你在找 `DeadlockAppService`、`AvoidanceDeadlockResolver`、avoid-point/detour/clearance 接缝，或 `Deadlock.Case.*` 集成事件——它们都没有了。

---

## 2. 分层与项目

整洁架构分层（依赖指向内层）。本上下文可**独立**构建——与 TrafficControl 之间没有编译期边（它实现的预防接口经由 TrafficControl 的 *Domain* 声明，唯一共享的边是冻结的 Kernel）。

| 项目 | 角色 | 关键类型 |
| --- | --- | --- |
| `SwarmRoute.Liveness.Domain.Shared` | 错误码。 | `DeadlockErrorCodes` |
| `SwarmRoute.Liveness.Domain` | 环检测原语 + 纯粹的解决算法。 | `Detection/RagCycleDetector`、`Detection/StuckClusterDetector`、`Resolution/PibtZoneResolver`、`Resolution/ParkedRelocationSelector`、`Resolution/HopDistances`、`Resolution/PibtAgentView`、`ValueObjects/ResourceAllocationGraph`、`ValueObjects/DeadlockCycle`、`Services/IDeadlockDetector` |
| `SwarmRoute.Liveness.Application.Contract` | 策略**接缝**：接口、快照、指令、选项。 | `Policy/ILivenessPolicy`、`Policy/LivenessSnapshot`（+ `LivenessPhase`、`AgentLivenessView`）、`Policy/LivenessDirective`（9 种变体）、`Policy/LivenessOptions`（+ `JointResolverKind`）、`Policy/NoOpLivenessPolicy` |
| `SwarmRoute.Liveness.Application` | 具体的分阶段策略。 | `Policy/LivenessPolicy` |
| `SwarmRoute.Liveness.Infra.CrossCutting.IoC` | DI 注册。 | `DeadlockNativeInjectorBootStrapper` |
| `SwarmRoute.Liveness.Tests` | xUnit 单元测试。 | — |

关键外部类型：图相关机制（`DirectedSparseGraph<T>`、`CyclesDetector`）在 `src/vendor/SwarmRoute.Algorithms*` 处内联引入。跨上下文的词汇（`ResourceRef`、`ResourceKind`、`ResourceAllocationGraphSnapshot`）来自**冻结的** `SwarmRoute.SpatioTemporal.Kernel`。解决算法所遍历的路网图是 PathPlanning 的 `RoadmapGraph`。

---

## 3. 授予时的环预防 —— `RagCycleDetector`

`RagCycleDetector`（`SwarmRoute.Liveness.Domain/Detection/RagCycleDetector.cs:23`）以字节级一致的环语义同时实现了**两个**存活下来的活性角色：

```csharp
public sealed class RagCycleDetector : IDeadlockDetector, IWouldCloseCycleDetector
```

### 预防 —— `IWouldCloseCycleDetector.WouldCloseCycle`

`IWouldCloseCycleDetector` 由 TrafficControl 声明（`TrafficControl/SwarmRoute.TrafficControl.Domain/Services/IWouldCloseCycleDetector.cs`）；`ReservationTable` 在构造时被注入一个，并在 `TryGrant` 中、记录一条争用等待边**之前**调用它：

```csharp
bool WouldCloseCycle(
    ResourceAllocationGraphSnapshot currentEdges,
    string candidateAgentId,
    IReadOnlyCollection<(string OwnerAgentId, ResourceRef Resource)> candidateWaitEdges);
```

检测器构建*假设的* RAG = 当前的 owns/waits 边**加上**候选者将要新增的等待边，运行 `CyclesDetector.CyclicVertices(graph, "agent_")`，返回候选者此刻是否落在某个环上。为真 ⇒ 拒绝授予、规划器改道，循环等待便永不形成。这是按运行选择启用的（`SimulationRequest.PreventDeadlockCycles`）；关闭 = Null 检测器 = 与基线字节级一致。

### RAG（`ResourceAllocationGraph` 值对象）

`ResourceAllocationGraph`（`.../ValueObjects/ResourceAllocationGraph.cs`）将冻结的 `ResourceAllocationGraphSnapshot` 适配成一张供环检测运行的有向图。三个顶点族（`agent_`、`occupySite_`、`applySite_`），两个边族：

- **拥有**（持有中）：`occupySite_<resource> → agent_<owner>`。
- **等待**（请求中）：`agent_<waiter> → occupySite_<resource>`。

两者都围绕每个资源*单一共享*的 `occupySite_` 顶点旋转，因此一条 `agent → resource → agent → resource → …` 的路径能闭合成环。`ResourceKey` 将资源命名空间化为 `Kind:Id`，因此同 id 的 CP 与 Lane 是不同顶点。

#### ASCII：一个 2-智能体循环等待

`A` 拥有 `r1` 且想要 `r2`；`B` 拥有 `r2` 且想要 `r1`：

```
构建的边：
  occupySite_CP:r1 → agent_A        (ownership)
  occupySite_CP:r2 → agent_B        (ownership)
  agent_A          → occupySite_CP:r2   (wait-for)
  agent_B          → occupySite_CP:r1   (wait-for)

经由 agent 顶点的环：
  agent_A → occupySite_CP:r2 → agent_B → occupySite_CP:r1 → agent_A
```

`CyclesDetector.CyclicVertices(graph, "agent_")` 标记出 `[agent_A, agent_B]`。

### 检测 —— `IDeadlockDetector.Detect`

对于事后分析，`Detect(snapshot)` 运行相同的环检测，然后对结果做精炼：内联的检测器会*过近似*（仅排在死锁后面的等待者也会被标记），所以检测器构建限定在环上智能体的**智能体阻塞有向图**，并通过一个迭代式（基于栈）的 **Tarjan** 实现返回其**非平凡强连通分量**（规模 ≥ 2 的 SCC，或带自阻塞边的单点）。环按最小成员 id 排序返回，因此检测是确定性的。`DeadlockCycle`（`.../ValueObjects/`）是结果值对象——一个环中的智能体 id，去重并按序数升序排序。

---

## 4. 同步活性策略

### 接缝 —— `ILivenessPolicy`

`ILivenessPolicy`（`.../Application.Contract/Policy/ILivenessPolicy.cs`）是物理僵持策略的唯一拥有者。一个方法：

```csharp
IReadOnlyList<LivenessDirective> Evaluate(LivenessSnapshot snapshot);
```

纯且同步（见 §1）。路网图与 `LivenessOptions` 在构造时为本次运行绑定，故不是快照字段。`NoOpLivenessPolicy.Instance` 是恒返回空的默认实现（在执行器未被赋予策略时使用）。

### 策略看到什么 —— `LivenessSnapshot`

```csharp
public sealed record LivenessSnapshot(
    long Tick,                                 // 仅用于诊断
    LivenessPhase Phase,                       // 本次查询处于哪个机制点
    bool ScheduleFaithful,                     // 在忠实调度（SIPP）执行器下为 true
    IReadOnlyList<AgentLivenessView> Agents,   // 本 tick 每个智能体的物理视图
    IReadOnlySet<string> ParkedCells);         // 已完成车辆停驻的格点
```

`AgentLivenessView` 是只读的每智能体视图（`Position`、`Goal`、`EffectiveGoal`、`Priority`、`EnRouteNextCell`，停滞计数 `BlockedTicks` / `StuckTicks` / `PibtHeldTicks`，联合求解器标志，以及仅 `Advance` 阶段有效的 `AtRouteEnd` / `NextCellIsParked` / `ScheduledToAdvance` / `ScheduledToMoveThisTick`）。它由执行器从其可变的 `RunAgent` 构建；策略从不直接看到或修改引擎状态。

### 策略返回什么 —— `LivenessDirective`

每条指令都与现有的某个执行器变更 1:1 对应（`.../Policy/LivenessDirective.cs`）：

| 指令 | 执行器做的事 |
| --- | --- |
| `YieldAndReplan(AgentId, Reason)` | 释放该智能体的租约，从其当前位姿重新规划。`Reason` 为 `head-on-yield` 或 `stall-reroute`。 |
| `EnterJointResolver(AgentIds)` | 释放这些智能体停滞的租约，开始一段 PIBT 片段。 |
| `MoveTo(AgentId, Cell)` | 本 tick 把一个 PIBT 智能体移动一跳到 `Cell`（当 `Cell` == 当前格时保持不动）。 |
| `ExitJointResolver(AgentId, Reason)` | 结束该智能体的 PIBT 片段（到达目标 / 被持过久 / 预算耗尽）→ 之后正常重新规划。 |
| `SolveClusterJointly(AgentIds)` | 对集群调用集群（CBS）规划器，并原子地预约无冲突结果。 |
| `RelocateParked(BlockerId, Dest, YieldWindow, WalledAgentId)` | 把停车的阻塞者让位到 `Dest`，持续 `YieldWindow` 个 tick，以打开被封死智能体的目标进近。 |
| `RestoreGoal(AgentId)` | 一个被让位的守门者其让位窗口已过 → 让它重新规划回自己的目标。 |
| `EscalateLivelock(AgentId, Reason)` | 防活锁终止：停止尝试让位/解决该智能体。 |
| `Diagnostic(Message)` | 把一条面向人的僵持诊断转发给日志接收器。 |

### 为何分阶段 —— `LivenessPhase`

物理僵持的决策本质上是*分阶段*的：停车的阻塞者必须在规划器为被封死智能体改道**之前**被让位；拥堵集群在 plan+reserve **之后**形成（这样它能看到刚规划好的位姿），但在调度决定谁前进**之前**；联合求解器的驱动以及每智能体的让步则在调度被解析**之后**决定。因此执行器**每个 tick 的每个阶段**查询一次策略，每次都恰好在其输入可用的机制点——这使得从执行器旧的内联逻辑中的抽取在构造上即保行为不变。

| 阶段 | 何时 | 策略决定 |
| --- | --- | --- |
| `BeforePlanning` | plan+reserve 之前 | 恢复让位窗口已过的守门者（`RestoreGoal`），然后把停车的阻塞者从被封死的进近上让开（`RelocateParked`，当 `StepAside` 开启时）。 |
| `ClusterFormation` | plan+reserve 之后、advances 解析之前 | 形成物理僵持集群并交给联合求解器——`EnterJointResolver`（PIBT）或 `SolveClusterJointly`（CBS）。当 `JointResolver == None` 时无操作。 |
| `JointDrive` | advances 解析之后、闸门之前 | 把每个 PIBT 智能体驱动一跳（`MoveTo`），并决定哪些智能体退出片段（`ExitJointResolver`）。 |
| `Advance` | 联合驱动之后、闸门之前 | 忠实调度下每智能体的 `stall-reroute` / `head-on-yield`（`YieldAndReplan` + 一条迎面 `Diagnostic`）。除非 `ScheduleFaithful` 否则无操作。 |

`LivenessPolicy`（`.../Application/Policy/LivenessPolicy.cs`）按 `snapshot.Phase` 把 `Evaluate` 分派到 `EvaluateBeforePlanning` / `EvaluateClusterFormation` / `EvaluateJointDrive` / `EvaluateAdvance` 之一，并持有一个记忆化的反向 BFS 跳数缓存（`HopDistances.To`，每目标一份）作为贪心的下一跳启发。

---

## 5. 联合求解器（PIBT / CBS）与集群检测

联合求解器是物理僵持集群的所有者——每个集群恰好一个，由 `JointResolverKind`（`.../Policy/LivenessOptions.cs`）选定：

```csharp
public enum JointResolverKind { None = 0, Pibt = 1, Cbs = 2 }
```

> 这个单一枚举是面向用户的开关；它此前是两个互斥的 `UsePibt` / `UseCbs` 请求布尔值。仿真 HTTP 请求现在携带一个 `JointResolver` 字段，映射到 `LivenessOptions.JointResolver`。

### 集群检测 —— `StuckClusterDetector`

`StuckClusterDetector.Assemble`（`.../Detection/StuckClusterDetector.cs`）是一个静态的、与预约无关的检测器。它从每个智能体的 INTENDED（意图的）下一格 + 位姿 + 一个统一的停滞计数（`StuckAgentSnapshot`）出发，用并查集把候选者（其意图格被物理占据的活跃寻目标智能体）连到阻塞它们的占据者，并返回规模 ≥ 2、且含有处于/超过触发阈值成员的分量——按最小 id 排序（确定性）。以*意图 + 位姿*（而非在途预约标志）为键，正是让一个其成员已跌入 pending/被封死状态的迎面互换仍能被识别为集群的原因。

### PIBT —— `PibtZoneResolver`

对于 `JointResolverKind.Pibt`，`ClusterFormation` 为每个集群成员发出一条 `EnterJointResolver`，而 `JointDrive` 调用 `PibtZoneResolver.Resolve`（`.../Resolution/PibtZoneResolver.cs`）：

```csharp
public static IReadOnlyDictionary<string, string> Resolve(
    IReadOnlyList<PibtAgentView> cluster,
    IReadOnlySet<string> blockedCells,
    RoadmapGraph graph,
    Func<string, IReadOnlyDictionary<string, int>> hopsToGoal);
```

它以**带回溯的优先级继承（PIBT）**为集群规划一次联合跳：处理顺序为等待最久者优先（防活锁），其次静态优先级，再次序数 id；每个智能体按到目标的跳数排序尝试其出邻居 + 原地不动，暂定占据一个目标，并递归地把优先级更低的占据者推开，失败则回溯。保证：下一格点顶点互异、无即时 2-环互换、最高优先级智能体获得其可达的最佳格。非集群智能体占据的格作为 `blockedCells`（不可移动）传入。当智能体到达目标、被持过久（`JointResolverHeldExitThreshold`）或其驱动预算耗尽时，该智能体的片段结束（`ExitJointResolver`）——将其交还给带优先级的 SIPP。

### CBS —— `SolveClusterJointly`

对于 `JointResolverKind.Cbs`，`ClusterFormation` 为每个多成员集群发出一条 `SolveClusterJointly`。由*执行器*（而非策略）释放成员的租约并调用其集群规划器（一个完备/最优的本地 Conflict-Based Search，复用 SIPP 作为受约束的底层，并通过它遵守滚动视界窗口），随后原子地预约无冲突结果并恢复忠实调度执行。CBS 能攻克贪心 PIBT 无法解决的密集互换/链，但代价更高。因此 CBS **需要 SIPP 规划器**（它返回的是只有忠实调度执行器才能执行的时间轴路径）——由 `SimulationService.Validate` 强制。

---

## 6. 执行器如何消费策略

消费者是 Simulation 上下文中的 `FleetLoopDriver`（`Simulation/SwarmRoute.Simulation.Application/FleetLoopDriver.cs`）。它接收一个可选的 `ILivenessPolicy`（默认 `NoOpLivenessPolicy.Instance`），并恰好**每个 tick 查询四次**——每个 `LivenessPhase` 一次 `Evaluate`，各在其机制点——并通过修改自身的 `RunAgent` 状态来执行返回的指令：

```
每个 tick：
  ── BeforePlanning ──  RestoreGoal → 清除改道；  RelocateParked → 让停车者让位、重置被封死计数
  （plan + reserve）
  ── ClusterFormation ──  EnterJointResolver → 释放租约、开始 PIBT；  SolveClusterJointly → 释放 + 集群规划 + 预约
  （调度解析哪些在途智能体前进）
  ── JointDrive ──  MoveTo → 走一步 PIBT 跳；  ExitJointResolver → 停车 / 解散回 pending
  ── Advance ──  YieldAndReplan(stall-reroute|head-on-yield) → 在闸门处释放 + 重新规划；  Diagnostic → 记录
  （通行权闸门让被授予的智能体前进一步）
```

在构造上，执行器是指令的机械执行者：策略拥有*全部*的僵持决策/PIBT/集群逻辑。驱动器对 Liveness 的项目引用是 `Application.Contract`（接缝）+ `Application`（具体的 `LivenessPolicy`）；它不再引用 `Liveness.Domain`——PIBT/集群代码是策略的事。

`SimulationService`（`Simulation/.../SimulationService.cs`）按请求为每次运行构建策略：`new LivenessPolicy(field.Graph, new LivenessOptions { JointResolver = request.JointResolver, StepAside = request.StepAside })`。

---

## 7. 组装 / 接线

`DeadlockNativeInjectorBootStrapper.RegisterServices(...)`（`.../Infra.CrossCutting.IoC/DeadlockNativeInjectorBootStrapper.cs`）只注册存活下来的原语：

```csharp
services.AddScoped<IDeadlockDetector, RagCycleDetector>();   // 事后检测角色
```

**预防**角色（`IWouldCloseCycleDetector`）**不**在这里接线——它是按运行选择启用的，由仿真引擎工厂接线。`InMemorySimulationEngineFactory`（`host/SwarmRoute.Host/Adapters/InMemorySimulationEngineFactory.cs`）*仅当* `PreventDeadlockCycles` 开启时，在 TrafficControl 引导程序**之前**预先注册 `AddSingleton<IWouldCloseCycleDetector, RagCycleDetector>()`，使得 TrafficControl 的 `TryAddSingleton` Null 默认让位给它——为那个隔离的、按请求的容器开启预防。（旧的宿主适配器 `RagWouldCloseCycleDetector.cs` 已删除；Liveness 领域的 `RagCycleDetector` 现在直接承担该角色。）

`ILivenessPolicy` **完全不**在 DI 中注册：它是 sim/执行器作用域的，由 `SimulationService` 按运行构造，因为它绑定本次运行的路网图 + 选项。生产环境没有执行器循环，因此它没有 `ILivenessPolicy`。

---

## 8. 测试

**`SwarmRoute.Liveness.Tests`**（纯内存）：

| 文件 | 覆盖 |
| --- | --- |
| `DeadlockDetectorTests` | `RagCycleDetector.Detect`：2/3/4-智能体环、无环 → 无、自环、两个独立环分别报告、额外的非环等待者被排除、跨多次运行的确定性。 |
| `ResourceAllocationGraphTests` | RAG 构建（agent/`occupySite`/`applySite` 顶点 + ownership/wait 边；键含 `Kind`；按边集判等）以及所构建的图正是 `CyclesDetector` 所标记的。 |
| `LivenessPolicyTests` | 纯 `LivenessPolicy` 在各阶段：迎面让步、集群形成 + PIBT 进入、停车让位、守门者恢复、忠实调度的 stall-reroute。输入手工构造的 `LivenessSnapshot` → 断言输出的指令。 |

**`SwarmRoute.Simulation.Tests`**（与活性相关）：

| 文件 | 覆盖 |
| --- | --- |
| `PibtZoneResolverTests` | 纯 `PibtZoneResolver`：迎面解决、旋转、优先级继承、确定性排序、僵局下限、车道方向性、阻塞格闸门。 |
| `StuckClusterDetectorTests` | `StuckClusterDetector.Assemble`：僵持分量隔离、自由智能体/单点排除、阈值触发。 |

**`SwarmRoute.Integration.Tests`**（经由真实引擎的端到端，行使策略接缝）：`PibtClosedLoopTests`、`CbsClosedLoopTests`（含 `CBS requires Planner=Sipp` 校验）、`SwapStandoffDetectionTests`、`WalledLoneSurvivorTests`、`PibtHeldExitTests`，以及 `LivenessDeterminismTests`（一个固定的密集 PIBT 场景运行两次字节级一致——经由策略接缝的可复现性）。

---

### 跨上下文依赖（小结）

- **实现（供 TrafficControl）：** `IWouldCloseCycleDetector` —— 授予时的否决，在 `ReservationTable.TryGrant` 内被查询。
- **实现（供执行器）：** `ILivenessPolicy` —— 同步的物理僵持策略，由 `FleetLoopDriver` 每个 tick 的每个阶段查询。
- **消费：** 冻结的 Kernel `ResourceAllocationGraphSnapshot`（RAG 读取）、PathPlanning 的 `RoadmapGraph`（解决算法所遍历的表面）。
- **共享 kernel：** `SwarmRoute.SpatioTemporal.Kernel`（`ResourceRef`、`ResourceKind`、`ResourceAllocationGraphSnapshot`）。
