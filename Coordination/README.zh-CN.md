# Coordination (協調)

> 简体中文 · English version: [README.md](README.md)

*车队的滚动时域控制回路——这个协调器将 Map + PathPlanning + TrafficControl 组合为一个 RHCR 风格的"规划 → 预约 → 剪枝重规划" tick(节拍),并在车队的整个生命周期内持续推进它。*

---

## 1. 目的与职责

Coordination 是整个系统的**协调器**:多机器人路径规划问题正是在这里被在线*求解*的,一次一个滚动时域 tick(节拍)。它**不拥有任何属于自己的领域状态**——没有路网图、没有预约表、没有规划器。它持有的是*对其他限界上下文契约(接口)的引用*,并在它们之间编排调用顺序:

1. 向 **Map** 请求路网图,
2. 向 **TrafficControl** 请求当前的预约视图,
3. 向 **PathPlanning** 请求在该图 + 视图上规划一条单智能体路径,
4. 向 **TrafficControl** 为整条路径获取路权,以及
5. 在被拒绝时,剪枝掉发生竞争的资源并重规划——有界。

这是第一代引擎中 CBS"无法锁定路径 → 等待 / 重规划"行为的整洁架构后裔(`IFleetCoordinationCycle`,`Coordination/SwarmRoute.Coordination.Application/IFleetCoordinationCycle.cs:3-10`)。该契约(接口)被刻意拆分为一个**可测试的周期主体**(`IFleetCoordinationCycle`)和一个**贯穿生命周期的托管驱动器**(`FleetCoordinationLoop`),这样控制逻辑无需定时器、无需宿主、无需墙钟时间即可断言。

整个上下文就是一个应用层服务,加上它的 DTO 和 IoC 引导器。它是纯粹的编排:每一项 MAPF *机制*(搜索、预约、拓扑)都隐藏在另一个上下文的契约(接口)之后。

---

## 2. 分层与项目

这里恰好只有**一个项目**:`SwarmRoute.Coordination.Application`(`Coordination/SwarmRoute.Coordination.Application/`)。**没有 `Coordination.Domain`**——已确认:Coordination 没有属于自己的不变式或聚合;它的"领域"就是编排本身,而它所强制保证的规则(确定性、有界重试)是*回路*的属性,而非任何实体的属性。在严格的 DDD 意义上,这是应用层编排。

| 文件 | 角色 |
|---|---|
| `IFleetCoordinationCycle.cs` | 可测试的回路主体契约(接口):`RunCycleAsync`(一个 tick)+ `ReleaseAsync`(资源归还)。 |
| `CoordinationCycleService.cs` | 默认实现——将四个上下文接入一个周期(`:37`)。 |
| `AgentGoal.cs` | 一个智能体在一个周期内的目标:`(AgentId, FromSiteId, ToSiteId, Priority)`(`:9`)。 |
| `CycleReport.cs` | `AgentCycleResult`(单智能体的结果)+ `CycleReport`(周期的结果,包含 `ReservedAgentIds` / `ContendedAgentIds` / `UnplannableAgentIds` 投影)。 |
| `FleetCoordinationLoop.cs` | 托管的 `BackgroundService` 看门狗 + `CoordinationLoopOptions`。 |
| `ICoordinationGoalSource.cs` | 目标簿接缝 + `InMemoryCoordinationGoalSource` 默认实现。 |
| `CoordinationServiceCollectionExtensions.cs` | 组合根:`AddCoordination(...)`。 |

`.csproj` 引用了 Map、PathPlanning、TrafficControl 的契约(接口)+ domain-shared 程序集,加上 Kernel(以及一个目前未被 Coordination 代码使用的 `Deadlock.Application.Contract` 引用——Coordination 从不调用 Deadlock;死锁处理是反应式 / 事件驱动的,而非从这个回路中发起)。它只引入 `Microsoft.Extensions.*` 抽象(Hosting、DI、Logging、Options)——不依赖任何具体基础设施。

---

## 3. 周期——`RunCycleAsync`

一次 `RunCycleAsync` 调用就是针对一组目标的**一个滚动时域 tick(节拍)**(`CoordinationCycleService.cs:66`)。它是这个上下文的核心。

**每周期初始化**(`:72-97`):

- 空目标集 → `CycleReport.Empty` 短路返回(`:73-74`)。
- **Map**:为整个周期一次性读取图——`_roadmaps.GetGraphAsync(roadmapId, …)`(`:77`);已缓存且在进程内。
- **整个周期使用一个时间戳**:`cycleReleaseTimeMs = _clock.NowMs`,**只读取一次**(`:80`)。本周期内规划出的每一个区间都表达在这唯一一个车队时钟时刻上,因此该周期的所有预约都落在同一条时间轴上。执行器注入一个由 tick 驱动的时钟(见 §7),使区间与执行 tick 对齐。
- **确定性顺序**:目标按 `Priority` 升序、再按 `AgentId` 序数排序(`OrderBy(g => g.Priority).ThenBy(g => g.AgentId, StringComparer.Ordinal)`,`:83-86`)。`Priority` 越低 = 路权越高 = 越先规划/预约。
- 随后目标被**顺序**处理,每个目标的预约在下一个目标规划之前就已提交(`:89-94`)——这就是优先级规划:较早智能体的租约对较晚的智能体可见。

**单智能体内层回路**——`PlanAndReserveAsync`(`:99-198`),即有界的 规划→预约→剪枝→重规划 回路:

```
pruned ← blockedResources (the parked/obstacle cells), or ∅          (:110-112)
for attempt = 1 .. MaxReplanAttempts (= 8):                          (:118, :40)
    view    ← TrafficControl.GetView(roadmapId)        # re-read each attempt   (:120)
    request ← PlanRequest(roadmap, agent, from, to,
                          releaseTimeMs = cycle ts,
                          blacklistedResources = pruned)             (:121-127)
    plan    ← PathPlanning.Plan(graph, request, view)                (:129)
    if not plan.Success:                                             (:130)
        return UNPLANNABLE  (no route — maybe every alt was pruned)  (:131-145)
    outcome ← TrafficControl.TryReserveAsync(plan.Path, agent)       (:150)
    if outcome == Granted:
        return RESERVED  ✔                                           (:153-166)
    # Denied / Queued / Blocked:
    for r in TrafficControl.BlockedResources(plan.Path, agent):      (:170)
        if r is the agent's own start/goal CP:  skip                 (:173-176)
        pruned.add(r)                                                (:177)
    if pruned didn't grow:  break   # replanning would be a no-op    (:185-186)
# fell out of the loop:
return CONTENDED  (planned but no right-of-way; retried next tick)   (:190-197)
```

显著的设计要点,全部体现在代码中:

- **`MaxReplanAttempts = 8`**(`:40`)——"1 次初始 + 至多 7 次剪枝重规划"。这一预算限定了内层回路的上界。
- **视图在每次尝试时都重新读取**(`:120`),使每次重规划都能看到最新的预约状态(本周期中较早的智能体已经提交)。
- **剪枝是外科手术式的,而非整条路径式的。** 只有 `TrafficControl.BlockedResources(path, agent)` 报告的*具体阻塞*的 CP/Lane 资源才会被加入黑名单(`:170-177`)——而非整条失败的路径。这正是测试 `M2_Retry_PrunesOnlyBlockedResource_AndKeepsSharedPrefix` 所固定的:一条与失败路径共享前缀的绕行路线必须保持可达。
- **闭塞 → 可剪枝单元投影。** TrafficControl 在内部检测阻塞/干涉闭塞冲突,但只回交*规划器可剪枝*的 CP/Lane 资源(`ITrafficCoordinatorAppService.BlockedResources`,契约(接口)位于 `TrafficControl/.../ITrafficCoordinatorAppService.cs:30-35`)。因此一个 `Block:Z` 闭塞冲突会作为待删除的候选 CP 回到 Coordination——见 `M2_Retry_ProjectsClosureBlockConflict_ToPlannerPrunableCell`。
- **起点/终点永不被剪枝**(`:173-176`)——剪掉一个智能体自己的端点会在构造上使目标不可达。
- **空操作保护**(`:185-186`):如果某次尝试没有向 `pruned` 添加任何新内容,那么下一次规划将完全相同,因此回路提前停止,并将该智能体报告为发生竞争。

**`blockedResources`(停放车辆参数)。** `RunCycleAsync` 接受一个可选的 `IReadOnlySet<ResourceRef>? blockedResources`(`IFleetCoordinationCycle.cs:19-26`,实现位于 `:69`)。它**为周期中的*每个*智能体播种剪枝集**(`:110-112`),因此每次规划都会绕开这些单元——例如被停放/已完成车辆占据的控制点(CP)。其效果是:车队*绕过*已完成的智能体继续流动,而不是堵在它们后面停滞。(执行器从已到达车辆的目标单元来填充该参数——见 §7。)

**结果。** 每个智能体产出一个 `AgentCycleResult(AgentId, Planned, Reserved, Outcome, Attempts, Path, FailureReason)`(`CycleReport.cs:21-28`)。`Attempts` 统计 规划→预约 的尝试次数(1 + 重规划次数),执行器将其用作自己的重规划度量。周期返回一个 `CycleReport`,其 `Results` 按确定性的处理顺序排列,并附带便捷投影 `ReservedAgentIds` / `ContendedAgentIds` / `UnplannableAgentIds`(`CycleReport.cs:39-49`)。

**`ReleaseAsync`**(`:201-205`)是对 `TrafficControl.ReleaseAsync` 的薄转发——对一个智能体已驶过的租约进行增量、单调的归还(仅限过去,不变式 I6;每个资源连同其父阻塞 + 干涉闭塞一起释放)。Coordination 在此不添加任何逻辑;它只是向其调用方暴露这一写接缝。

---

## 4. 贯穿生命周期的回路——`FleetCoordinationLoop`

`RunCycleAsync` 是**一个** tick。`FleetCoordinationLoop`(`FleetCoordinationLoop.cs:36`)是**贯穿生命周期的在线 MAPF 驱动器**,永远地推进它——即 OpenTCS 的"Dispatcher" / RHCR 滚动时域调度器。它是一个 `IHostedService`(`BackgroundService`):

- 在以 `CoordinationLoopOptions.TickInterval` 为周期(默认 1s,`:14`)的 `PeriodicTimer` 看门狗上,它循环执行 `WaitForNextTickAsync` → `RunOnceAsync`(`:67-73`),并在关停时保证取消安全。
- `RunOnceAsync`(`:88-110`)从 `ICoordinationGoalSource` 读取**当前目标簿**(`CurrentRoadmapId`、`CurrentGoals`);当空闲时(无路网图 / 无目标)它是一个**安全的空操作**,返回 `CycleReport.Empty`(`:92-93`)。否则它**每个 tick 开启一个 DI 作用域**,解析出 `IFleetCoordinationCycle`,并恰好运行一个周期(`:97-99`)。
- **韧性**:周期内部的异常会被记录并吞掉(`:105-109`),这样一个糟糕的 tick 永远不会摧毁整个生命周期回路;只有 `OperationCanceledException` 会向上传播。
- `EnableWatchdog = false`(`:21`)关闭定时器,使该回路**仅按需运行**(由测试 / 宿主显式调用 `RunOnceAsync` 来驱动 tick)。

**它与单个周期的区别:** 回路增加了*时间、一个目标源,以及故障隔离*。周期是 `(roadmap, goals, view, clock)` → `CycleReport` 的纯函数;回路提供*何时*(看门狗 tick)、*做什么*(实时目标簿)以及*保活*(吞异常并继续)。依据架构设计 §7,死锁处理是**反应式的**(由订阅者/作业驱动),而非在这个内层回路中轮询(`:28-30`)。

`ICoordinationGoalSource`(`ICoordinationGoalSource.cs:9-16`)是回路与订单簿之间的接缝:`CurrentRoadmapId` + 一个 `CurrentGoals` 快照。默认的 `InMemoryCoordinationGoalSource`(`:22-60`)是一个线程安全的可变簿册,由宿主(或测试)通过 `Set`/`Clear` 向其投喂;若未设置任何内容,回路便空闲。将它与回路分离,正是让目标簿能够被替换(真实调度器)而无需触碰控制逻辑的关键。

**组合**(`CoordinationServiceCollectionExtensions.cs`):`AddCoordination()` 注册周期(`AddScoped<IFleetCoordinationCycle, CoordinationCycleService>`,`:39`)和默认目标源(`TryAddSingleton`,`:42-44`),但**不**注册托管回路。`AddCoordination(registerHostedLoop: true, …)` 会额外将 `FleetCoordinationLoop` 作为单例托管服务接入(`:51-55`),使按需调用方与托管生命周期共享同一个实例。生产宿主调用 `AddCoordination(registerHostedLoop: true)`(`host/SwarmRoute.Host/Program.cs:74`);集成测试调用纯 `AddCoordination()` 并直接驱动 `RunCycleAsync`。该引导器假定其他四个上下文的引导器已经注册好了它们的契约(接口)(`:9-13`)。

---

## 5. 整洁架构接缝(依赖映射)

Coordination 对其他上下文的依赖**仅**通过它们的 `Application.Contract` 接口 + 共享 Kernel——绝不依赖任何具体实现、EF 或消息代理。这是承重接缝。构造函数(`CoordinationCycleService.cs:49-63`)恰好接受五个协作者:

| 依赖(接口) | 所属上下文 | 用途 |
|---|---|---|
| `IRoadmapQueryService.GetGraphAsync` | **Map**(`Map.Application.Contract.Services`) | 时域的路网图(已缓存,在进程内)。`CoordinationCycleService.cs:77` |
| `IReservationQuery.GetView` | **PathPlanning**(声明)/ **TrafficControl**(实现) | 规划器据以搜索的只读预约视图,每次尝试时重新读取。`:120` |
| `IPathPlanner.Plan` | **PathPlanning**(`PathPlanning.Domain.Planners`) | 在图 + 视图上的单智能体时空路径。`:129` |
| `ITrafficCoordinatorAppService.TryReserveAsync` | **TrafficControl**(`TrafficControl.Application.Contract.Services`) | 为整条路径获取路权;返回 `AllocationOutcome`。`:150` |
| `ITrafficCoordinatorAppService.BlockedResources` | **TrafficControl** | 实际阻塞此路径的、规划器可剪枝的资源。`:170` |
| `ITrafficCoordinatorAppService.ReleaseAsync` | **TrafficControl** | 单调的租约归还(转发)。`:205` |
| `IFleetClock.NowMs` | **Kernel**(`SpatioTemporal.Kernel`) | 每周期唯一的释放时间戳。`:80` |

注意这里刻意的**读/写接缝拆分**:`IReservationQuery`(一个*读*视图)由 **PathPlanning 声明**,这样规划器可以独立地针对一个 `NullReservationQuery` 构建,并由 **TrafficControl 实现**,后者用其基于预约表的视图覆盖了该注册(`IReservationQuery.cs:5-22`)。*写*接缝(`TryReserve`/`Release`)位于 `TrafficControl.Application.Contract`(`ITrafficCoordinatorAppService.cs:6-11`)。Coordination 是唯一同时持有两端的地方——它通过 PathPlanning 的视图来读,通过 TrafficControl 的协调器来写。所有的流通货币都是 Kernel 值类型(`ResourceRef`、`TimeInterval`、`SpaceTimePath`)。

---

## 6. 确定性与活性(无活锁)

这正是此上下文为之存在而要保证的属性——在 XML 文档中被记录为 **R6 / ADR-003**(`CoordinationCycleService.cs:25-35`、`AgentGoal.cs:3-8`)。

**确定性。** 目标按一个*稳定的全序*处理——`Priority` 升序,再按 `AgentId` 序数(`:83-86`)。因为每个智能体的预约都**在下一个智能体规划之前提交**(顺序的 `foreach`,`:89-94`,再加上每次尝试的视图重读),所以对于相同的输入,预约表在每次运行中都以**完全相同的方式**串行化车队。在一个周期内部,不存在源自顺序或并发的非确定性。(测试 `M2_TwoAgents_…` 固定了这一点:优先级为 0 的智能体赢得共享走廊,优先级为 1 的智能体被 `Queued`。)

**有界重规划 ⇒ 内层回路总会终止。** 每次剪枝重规划都严格地*收缩搜索空间*:发生竞争的资源被加入黑名单(`:170-177`)且在周期内永不移除,因此规划器每次尝试都在求解一个被单调地更加约束的问题。回路通过以下三个终止条件之一退出:

1. **Granted** → 已预约,完成(`:153-166`);
2. **无路线** → `plan.Success == false`(可能是因为每条备选都被剪掉了)→ 报告为 `UNPLANNABLE`(`:130-145`);
3. **无新内容可剪** → 空操作保护提前 break(`:185-186`);或者 `MaxReplanAttempts` 预算耗尽(`:118`)→ 报告为**发生竞争**。

因此一个周期**绝不会**永远空转,被拒绝会降级为一个*被报告的* 发生竞争/不可规划 结果,而非挂起。

**拒绝 → 等待 → 下一 tick 重试。** 在一个 tick 内,一个发生竞争的智能体**不会**忙等待——它只是本周期未被授予,并作为发生竞争被返回(`:190-197`)。活性来自*外层*回路:某个持有者释放发生竞争的资源(执行器在驶过时调用 `ReleaseAsync`),而**下一个** tick 重新读取一个如今更空闲的视图,该智能体便被授予(这正是测试 `M2_…_ThenSecondGrantedAfterRelease` 的形态:释放走廊,重新运行周期,排队的智能体如今变为 `Granted`)。滚动时域 = 拒绝是廉价且短暂的,在下一个时钟 tick 上重试——而非靠在一个 tick 内空转来解决。再结合针对*永久性*障碍的静态 `blockedResources` 绕行(停放车辆,§3),车队既不会堵在短暂竞争之后、也不会堵在已完成智能体之后而活锁。

---

## 7. 与执行器(Simulation 上下文)的关系

Coordination 决定**谁可以预约哪条路径以及何时预约**;它**不**移动车辆、也不推进时间。那是**执行器**的工作——即 **Simulation** 上下文中的 `FleetLoopDriver`(`Simulation/SwarmRoute.Simulation.Application/FleetLoopDriver.cs`)。二者组合得很干净:

- 执行器**拥有 tick 和时钟。** 每个 tick,它在规划*之前*推进车队时钟(`advanceClock?.Invoke(tick)`,`FleetLoopDriver.cs:192`;sim 传入 `ManualFleetClock.SetTick`,接线于 `SimulationService.cs:61`)。这正是使每周期的 `_clock.NowMs`(§3)落在执行 tick 轴上的机制——把区间无碰撞性耦合到实际运动上。
- 它**为每个空闲智能体调用 `RunCycleAsync`**,以确定性的优先级顺序进行,并读回已预约的路径(`FleetLoopDriver.cs:205-227`)。新近被预约的智能体进入行进中状态。
- 它施加一个**执行期路权闸门**——"若有车辆占据下一个 CP,则等待"——这是在预约表之上、由构造上使同一 CP 碰撞成为不可能的最后一道停车等待(`FleetLoopDriver.cs:230-294`)。
- 它在每个智能体离开某个 CP/lane 时增量地**调用 `ReleaseAsync`**,并在到达时再次调用以归还整条路径(无泄漏)(`:289-293`、`:304`)。
- 它**把 `blockedResources` 回喂进去**:已到达车辆的目标单元被放入一个 `parkedCells` 集合,作为下一个周期的 `blockedResources` 传入(`:202-205`、`:303`),从而闭合停放车辆的绕行回路。
- 病态的僵持会降级为 `FleetLoopStatus.DidNotConverge`(一个*被报告的*结果),而绝不会崩溃(`:180-185`)。

完整的执行器在 **Simulation** 上下文中有文档说明——本 README 只点明这一接缝。关键要点:**Coordination = 规划器/预约大脑(一个 tick 是纯的);Simulation 的 `FleetLoopDriver` = 推进时钟、拦截碰撞、驱动这些 tick 的执行器。**

---

## 8. 测试

Coordination 的行为由 `SwarmRoute.Integration.Tests` 中的**集成测试**验证(没有 Postgres、没有消息代理——各上下文真实的 DI 引导器跑在一个内存图支撑的 `IRoadmapQueryService` 之上,由 `tests/SwarmRoute.Integration.Tests/TestSupport/CoordinationTestHost.cs` 组装)。

`tests/SwarmRoute.Integration.Tests/CoordinationCycleIntegrationTests.cs`:

| 测试 | 它所固定的内容 |
|---|---|
| `M1_SingleAgent_PlansShortestPath_AndReservesGranted` | 拓扑 → 真实规划器路径 → 真实预约 `Granted`;路径按序经过 `A,B,C,D`。 |
| `Cycle_UsesFleetClockReleaseTime_ForNewReservations` | 每个已预约单元的区间起点 ≥ 注入的 `IFleetClock.NowMs`——即每周期时间戳(§3)。 |
| `M2_TwoAgents_SharingCorridor_AreSerialised_ThenSecondGrantedAfterRelease` | 迎面走廊:优先级 0 `Granted`,优先级 1 `Queued`;在 `ReleaseAsync` + 重新运行之后,第二个变为 `Granted`。确定性 + 拒绝→下一 tick 重试。 |
| `M2_Retry_PrunesOnlyBlockedResource_AndKeepsSharedPrefix` | 外科手术式剪枝:与失败路径共享前缀的绕行路线被预约(`Attempts ≥ 2`),被阻塞的车道 `B-D` 被绕开。 |
| `M2_Retry_ProjectsClosureBlockConflict_ToPlannerPrunableCell` | 一个 `Block:Z` *闭塞*冲突被投影到可剪枝的 CP `B`;重试改道经由 `A-C-D`。 |
| `M3_TrafficControlContention_TriggersDeadlockScanThroughEventBus` | 竞争发布一个事件,由 Deadlock 订阅者作出反应——确认死锁是反应式的,**而非**在 Coordination 的回路中。 |

闭环端到端运行(执行器驱动多个 tick 直至完成)由针对 `FleetLoopDriver`(Simulation 上下文)的 `ClosedLoopIntegrationTests.cs` 覆盖。

---

## 9. v0 现状与 v1 路线图

**v0(当前)。** 周期跑在 `DijkstraPathPlanner` 之上——**仅空间的最短路径**。规划器被*递交*了预约视图,却把一切都当作空闲处理(`IPathPlanner.cs:7-11`、`CoordinationCycleService.cs:32-35`)。因此即时的、同一 tick 内的冲突规避完全通过 **CP/Lane 黑名单**来表达(通过 `TryReserve` 锁定整条路径,然后围绕发生竞争的资源剪枝重规划)。`AllocationOutcome` 在 v0 中产出 `Granted` / `Queued`;`Blocked` / `Preempted` 在冻结的契约(接口)中为 v1+ 保留(`AllocationOutcome.cs:7-12`)。这忠实地把第一代引擎"锁定整条路径;失败时拉黑并重规划"的回路移植进了整洁架构。

**v1(计划中)。** 把 `DijkstraPathPlanner` 替换为一个 **SIPP**(安全区间路径规划,Safe-Interval Path Planning)规划器,它*真正在时间维度上查阅预约视图*,在搜索过程中绕开被占据的区间。接缝已经为此塑形完毕:`IPathPlanner.Plan(graph, request, view)` 在 v0→v1 之间保持不变(`IPathPlanner.cs:8-11`),并且**这个回路主体不会改变**——Coordination 仍然 规划 → 预约 → 剪枝重规划;SIPP 只是让第一次规划具有时间感知能力,从而使更少的尝试发生竞争。XML 文档明确点出了这一点(`CoordinationCycleService.cs:32-35`)。更后续的版本会朝着优先级规划 / 抢占式路权(`Preempted`)扩展,同样无需重塑该周期。

---

### 周期一瞥

```
                RunCycleAsync(roadmap, goals, blockedResources)
                                  │
          graph ← Map.GetGraphAsync(roadmap)          (once / cycle)
          ts    ← clock.NowMs                          (once / cycle)
          order goals by (Priority, AgentId)           (deterministic)
                                  │
          ┌────────── for each goal, in order ─────────┐
          │  pruned ← blockedResources                 │
          │  ┌── attempt 1..8 ──────────────────────┐  │
          │  │ view ← TrafficControl.GetView         │  │
          │  │ plan ← PathPlanning.Plan(graph,req,view)│ │
          │  │ if !plan: → UNPLANNABLE ──────────────┼──┼──▶ result
          │  │ outcome ← TrafficControl.TryReserve    │  │
          │  │ if Granted: → RESERVED ───────────────┼──┼──▶ result
          │  │ else: prune blocking resources; replan │  │
          │  │ if nothing new pruned: break           │  │
          │  └── (budget hit / no-op) → CONTENDED ────┼──┼──▶ result
          └────────────────────────────────────────────┘
                                  │
                          CycleReport(results)
```
