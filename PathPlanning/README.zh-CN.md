# Path Planning (路徑規劃)

> 简体中文 · English version: [README.md](README.md)

*负责单智能体路径计算：给定已构建的路网图和一个预约视图，产出某个智能体的、考虑碰撞的 `SpaceTimePath`(或一个带类型的失败结果)。*

---

## 1. 目的与职责

这个限界上下文只回答一个问题:**"在路网 _R_ 上,把智能体 _X_ 从站点 _A_ 路由到站点 _B_,出发时间不早于 _t_ —— 给我时空路径,或者告诉我为什么不行。"** 它是一个纯计算上下文 —— 没有持久化、没有 DbContext、没有后台循环(`PathPlanningNativeInjectorBootStrapper.cs:16-18`)。

它**拥有**:

- 单智能体规划器策略接缝 `IPathPlanner`(`Planners/IPathPlanner.cs:13`)及其 v0 实现 `DijkstraPathPlanner`(`Planners/DijkstraPathPlanner.cs:27`)。
- 请求/结果词汇表:`PlanRequest`、`PlanResult`、`PlanCost`、`WaitAction`(`ValueObjects/`)。
- `AgentPlan` 聚合(`Aggregates/AgentPlan.cs:21`),它建模单台车辆当前计划的生命周期并触发集成事件。
- **预约读取接缝 `IReservationQuery` 的声明**(`Reservations/IReservationQuery.cs:23`)—— 在*此处*由 PathPlanning 声明,但由 TrafficControl 实现(已冻结的跨上下文契约;见 §5)。

它明确**不**拥有:

- **预约表。** PathPlanning 只*读取*一个 `IReservationView`;TrafficControl 拥有权威的表以及写入接缝(`TryReserve`/`Release`)(`IReservationQuery.cs:14-21`、Kernel `IReservationView.cs:8-11`)。
- **多智能体循环。** 对多个智能体进行排序、优先级排序、剪枝重规划、以及提交预约,这些都位于 **Coordination** 上下文的 `CoordinationCycleService` 中(见 §5)。PathPlanning 每次调用只规划*一个*智能体,且是无状态的。
- **路网图。** 该图由 **Map** 上下文构建并缓存;PathPlanning 以只读方式将其作为 `RoadmapGraph` 消费。

代码中明确体现了其血统:`DijkstraPathPlanner` 移植了第一代引擎的 `AJR.MAPF.XCBS.CBS.SearchPath` —— 后者运行 `new DijkstraShortestPaths(graph, start).ShortestPathTo(end)` 并返回一个站点序列或 `null` —— 并**把这个扁平序列提升为带时间维度的 `SpaceTimePath`**(`DijkstraPathPlanner.cs:8-15`)。时空层正是 v0 引擎所缺的那个维度(Kernel `SpaceTimeCell.cs:5-7`)。

---

## 2. 分层与项目

标准的 grukirbs/DDD 洋葱结构。六个项目:

| 项目 | 角色 | 依赖于 |
|---|---|---|
| `SwarmRoute.PathPlanning.Domain.Shared` | 叶子原语:`PlannerKind`、`PlanStatus` 枚举,`PathPlanningErrorCodes`(`PP-001`…`PP-005`)。 | *(无)* —— 纯叶子。 |
| `SwarmRoute.PathPlanning.Domain` | 核心:`IPathPlanner` + `DijkstraPathPlanner`、各值对象、`AgentPlan` 聚合 + 事件,以及 `IReservationQuery`/`NullReservationQuery`/`AlwaysFreeReservationView` 预约接缝。 | Kernel、`Domain.Abstractions`(事件总线)、NetDevPack(`ValueObject`/`Entity`/`DomainEvent`)、内嵌(vendored)的 `SwarmRoute.Algorithms`、**`Map.Domain`**(用于 `RoadmapGraph`)、Domain.Shared。 |
| `SwarmRoute.PathPlanning.Application.Contract` | 传输面:`IPathPlanningAppService` + `PlanResultDto`。 | 仅 Kernel。 |
| `SwarmRoute.PathPlanning.Application` | `PathPlanningAppService` 编排 + AutoMapper 的 `PathPlanningMappingProfile`。 | Domain、Application.Contract、**`Map.Application.Contract`**(用于 `IRoadmapQueryService`)、AutoMapper。 |
| `SwarmRoute.PathPlanning.Infra.CrossCutting.IoC` | 组合根 `PathPlanningNativeInjectorBootStrapper`。 | Application;`FrameworkReference Microsoft.AspNetCore.App`(用于 `WebApplicationBuilder`)。 |
| `SwarmRoute.PathPlanning.Tests` | xUnit 测试(见 §7)。 | Domain、Application、Application.Contract、IoC。 |

关键依赖说明:**Domain** 层直接引用 `Map.Domain`(`SwarmRoute.PathPlanning.Domain.csproj`),因为规划器在具体的 `RoadmapGraph` 值对象上操作 —— 该读模型是共享的,而非复制的。内嵌的 `SwarmRoute.Algorithms` 引用是为 Map 图所封装的同一个 `DijkstraShortestPaths` 提供的传递性管道。

---

## 3. 规划器 —— `DijkstraPathPlanner`

`Plan(RoadmapGraph graph, PlanRequest request, IReservationView reservations)`(`DijkstraPathPlanner.cs:36`)。函数体分为两个阶段 —— **(a) 找到一个站点序列**,然后 **(b) 把它提升为一条时间线**(§4)。

### 最短路径(剪枝的 Dijkstra)

`ShortestPath`(`DijkstraPathPlanner.cs:57-100`)是一个手写的、感知黑名单的 Dijkstra,运行在有向加权图上 —— *并非*委托给 `RoadmapGraph.ShortestPath`。它在本地重新实现,恰恰是因为 v0 需要**在搜索过程中剪掉被列入黑名单的转移**,而这正是 Map 图那个朴素的 `DijkstraShortestPaths` 封装做不到的。

- **状态**:`distances`(`Dictionary<string,long>`,序数比较)、用于回溯的 `previous`,以及一个按累计距离作键的 `PriorityQueue<string,long>`(`:64-70`)。经典的惰性删除保护(`currentDistance > knownDistance → continue`)处理过期的队列条目(`:74-75`)。
- **边权重**:`graph.EdgeWeight(current, next) ?? 1`,并钳制到 `>= 1`(`:85-87`)。权重为 `round(Distance_metres × 1000)` —— 即 Map 图的 `WeightScale = 1000d`(`Map .../RoadmapGraph.cs:25`、`:54`)。所以 1.0 m 的车道 = 权重 `1000`。
- **黑名单剪枝** —— `IsBlacklistedTransition`(`:102-110`):若邻居 `next` 是被列入黑名单的**控制点(CP)**(按原始 id *或* `SiteRef`),则跳过它,**除非**它是该智能体自己的起点/终点(永不剪枝,否则按构造终点会变得不可达);并且,若有向**车道(Lane)** `from→next` 被列入黑名单,也跳过该转移(`RoadmapGraph.LaneRef`)。这就是 v0 的避让机制(§5)。
- **终止**:在出队终点时返回 `Reconstruct(...)`(`:112-127`,从终点沿 `previous` 走回起点,再反转);当队列耗尽时返回 `null`。
- **平凡情形**:`start == goal` 短路返回 `[start]`(`:61-62`)。

邻居扩展按 `OrderBy(id, StringComparer.Ordinal)` 排序(`:80`)—— 见下方的**确定性**。

### 失败分支

移植自 `SearchPath` 返回 `null`,但拆分为两个*截然不同、可据以行动的*原因(`:42-51`):

| 条件 | 结果 | 错误码 |
|---|---|---|
| `!graph.HasSite(FromSiteId)` 或 `!graph.HasSite(ToSiteId)` | `PlanResult.Failed(...)` | `PP-002` `UnknownSite` |
| `ShortestPath` 返回 `null`/空(不可达 / 端点被阻塞) | `PlanResult.Failed(...)` | `PP-003` `NoRoute` |

这个未知端点检查是*防御性且有意为之的* —— `ShortestPath` 本身对不存在的顶点也会返回 `null`,但 `:42-43` 处的注释指出,不应把这个原因与真正的"无路径"混为一谈。`null` 参数会在最前面抛出 `ArgumentNullException`(`:38-40`);即便 v0 从不读取 `reservations`,也对它做了非空检查(`:40` —— "在 v0 中被读取但当作始终空闲处理")。

### 确定性(序数打破平局)

给定固定的图 + 请求,规划器是完全确定性的。两处序数打破平局保证在等代价路径中产生稳定的单一结果:

1. 邻居迭代顺序为 `StringComparer.Ordinal`(`:80`)。
2. 松弛规则 `candidate >= best → continue`(`:90-91`)在平局时保留**第一个**(序数最靠前的)等代价前驱,而非覆盖。

这一点很重要,因为整条车队流水线依赖可复现的运行 —— Coordination 确定性地对目标排序,而 `FleetLoopDriver` 宣称"在确定性输入下是确定性的"(`CoordinationCycleService.cs:25-31`、`FleetLoopDriver.cs:89`)。一个非确定性的规划器会破坏这个保证。

---

## 4. 时空时间线 —— `BuildTimeline`

`BuildTimeline(graph, sites, releaseTimeMs)`(`DijkstraPathPlanner.cs:134-178`)正是扁平站点列表变成 Kernel `SpaceTimePath`(`= IReadOnlyList<SpaceTimeCell>`,Kernel `SpaceTimePath.cs:8`)的地方。每个 `SpaceTimeCell` 是一个 `ResourceRef`,在一个左闭右开的 `TimeInterval` 内被占用(Kernel `SpaceTimeCell.cs:10`)。

### CP + Lane 的"每跳两格"模型

对每一跳 `site[i] → site[i+1]`,规划器发出**两个共享同一区间的格子**(`:153-167`):

```
hop i: weight = EdgeWeight(site[i], site[i+1])  (clamped >= 1)
       traversal = [cursor, cursor + weight)
       cell  ⟨CP:   site[i]      , traversal⟩      ← the control point
       cell  ⟨Lane: site[i]-[i+1], traversal⟩      ← the directed lane
       cursor += weight
```

```
sites:   A ───────► B ───────► C ───────► D
ms:    rel        rel+w0     +w0+w1    +…+w2   (+GoalDwellMs)
cells: CP:A          CP:B       CP:C      CP:D
       Lane:A-B      Lane:B-C   Lane:C-D
       [rel,+w0) ... contiguous, half-open, non-overlapping ...
```

**为什么 CP 与 Lane 有意重叠同一时间窗**(`DijkstraPathPlanner.cs:18-22`):在穿越某段时,车辆物理上同时占据*车道*和(保守地)它正驶向的那个控制点。v0 出于安全起见把两者都预约 —— 这是最简单的、可靠的过度近似(over-approximation)。资源词汇表(`ResourceKind.CP` / `.Lane`,Kernel `ResourceRef.cs:8-21`)现在就被固定下来,这样 v1 的 SIPP 日后可以细化*精确的*进入/退出时刻(更紧、可能不重叠的拆分),**而无需改变所预约的内容** —— 只改变时刻。

下游消费者读回作为路线的,正是仅 CP 的投影:`AgentPlan.ExtractSiteSequence` 和映射 profile 都过滤 `Resource.Kind == CP`(`AgentPlan.cs:174-178`、`PathPlanningMappingProfile.cs:27-33`)。

### 终点停留(goal dwell)与终端格子

`TimeInterval` 要求 `Start <= End`(Kernel `TimeInterval.cs:25-26`),而退化的 `[t,t)` 格子时长为零。因此终端(终点)站点会获得一个**单位停留**:`[cursor, cursor + GoalDwellMs)`,其中 `GoalDwellMs = 1`(`DijkstraPathPlanner.cs:33`、`:170-173`)。这"纯粹是为了让产出的 `SpaceTimePath` 类型在 v0 中良构(well-formed)"(`:30-32`)—— 它*不是*对真实停留时间的建模。`start == goal` 的单站点计划是同样的形状:一个 `[rel, rel+GoalDwellMs)` 的 CP 格子,零代价(`:142-148`)。

### `releaseTimeMs`

时间线的原点。第一跳恰好从 `request.ReleaseTimeMs` 开始(`:150`,`cursor = releaseTimeMs`);测试断言格子 `[0]` 从此处开始(`DijkstraPathPlannerTests.cs:161`)。`PlanRequest` 校验 `ReleaseTimeMs >= 0`(`PlanRequest.cs:45-46`、`PP-004`)。Coordination 传入一个由本周期内每个智能体共享的 `cycleReleaseTimeMs = _clock.NowMs`(`CoordinationCycleService.cs:79-80`)。

### `PlanCost`

`new PlanCost(totalDistance, hopCount, durationMs)`(`:175-177`):`DistanceUnits` = Σ 缩放后的边权重(在测试中与 `RoadmapGraph.DistanceTo` 交叉校验),`HopCount` = `sites.Count - 1`,`DurationMs` = `cursor + GoalDwellMs - releaseTimeMs`。在 v0 中 `DurationMs == DistanceUnits + GoalDwellMs`,因为**边权重被直接当作代理时长使用**(`PlanCost.cs:6-10`)—— 目前还没有速度模型。

### 区间是车队时钟单位 —— 以及 Simulation 为何使用 *tick* 时钟

每个区间都以**车队时钟毫秒**为单位,对应那个唯一的单调时钟 `IFleetClock`(Kernel `IFleetClock.cs:7-11`、`TimeInterval.cs:4`)。左闭右开的 `[Start,End)` 语义意味着一台车辆可以恰好在下一台进入时退出某资源 —— 端点相接并不算重叠(`TimeInterval.cs:5-8`、`:39`),这正是这条连续时间线无碰撞的原因(`DijkstraPathPlannerTests.cs:173-176`)。

有一个微妙之处由系统在本上下文之外解决,这里值得记录,因为它正是*规划器区间单位之所以重要*的原因:

- **生产环境**把车队时钟映射到**墙钟毫秒**(`SystemFleetClock`)。但一个协调周期在亚毫秒内就跑完,因此表所认为"在时间上已分隔"的两个预约,可能落在**同一执行步上的同一个 CP** —— 预约轴与执行轴是解耦的(`ManualFleetClock.cs:11-16`)。
- 因此 **Simulation** 驱动一个**离散 tick 时钟** `ManualFleetClock`,在*每个规划周期之前*把它推进到当前整数 tick(`ManualFleetClock.cs:18-25`;在 `FleetLoopDriver.cs:189-192` 处驱动)。当一个 tick = 一次 CP 跳时,**规划出的区间 == 执行 tick**,于是预约表那些"区间排他"的租约就在执行时变成了真正的无碰撞保证(`FleetLoopDriver.cs:75-98`)。

PathPlanning 对*哪个*时钟在起作用是无感知的 —— 它只是在被交予的任何 `releaseTimeMs` 原点上发出区间。tick-还是-墙钟的决定属于 **Coordination/Simulation**;本上下文只是保证这些区间在那条轴上是单调、连续且不重叠的。

---

## 5. 预约感知

### 已接线,但在 v0 中始终空闲

`IPathPlanner.Plan` 接受一个 `IReservationView`(`IPathPlanner.cs:21-26`),所以每个调用点都已经传入一个 —— 接缝端到端已接线。但 **v0 的规划器并不搜索安全区间**。桩视图 `AlwaysFreeReservationView` 对任何资源都报告 `IsFree → true` 以及单一的最大空闲区间 `[0, long.MaxValue)`(`AlwaysFreeReservationView.cs:14-27`);`DijkstraPathPlanner` 甚至从不调用它(`DijkstraPathPlanner.cs:40` —— "被读取但当作始终空闲处理")。`WaitAction`(`ValueObjects/WaitAction.cs:16`)—— move 的对偶,即一个感知预约的规划器为让另一台车辆通过而插入的动作 —— 现在已被定义,但 v0 **从不发出**它("它的时间线只含移动",`WaitAction.cs:11-14`)。

### 避让通过请求黑名单(CP/Lane)实现

那么 v0 究竟如何避免争用?通过 `PlanRequest` 的**黑名单**,由对 Dijkstra 搜索的剪枝来强制执行(§3、`DijkstraPathPlanner.cs:102-110`)。`PlanRequest` 携带它的两种视图(`PlanRequest.cs:21-22, 94-104`):

- `BlacklistedResources` —— 覆盖 CP *和* Lane 的规范化 `HashSet<ResourceRef>`。
- `BlacklistedSiteIds` —— 仅含 CP 的便捷视图;站点 id 被归一化为 `ResourceRef(CP, id)`,反之亦然(`PlanRequest.cs:60-76`)。

### 与 Coordination 之间的剪枝重规划契约

这是 v0 设计的核心,由 **`CoordinationCycleService`**(`Coordination/.../CoordinationCycleService.cs`)拥有,而非 PathPlanning。每个智能体的循环(`:99-198`):

1. 读取当前视图(`_reservations.GetView`),用正在累积的 `pruned` 集合作为 `blacklistedResources` 构建一个 `PlanRequest`(`:120-128`)。
2. `_planner.Plan(...)`。失败时 → 报告争用(`:130-145`)。
3. `_traffic.TryReserveAsync(path, ...)`。返回 `Granted` 时 → 完成(`:150-166`)。
4. 返回 `Denied`/`Queued`/`Blocked` 时 → 向 `_traffic.BlockedResources(path, ...)` 询问实际阻塞了这条路径的*具体* CP/Lane 资源,**把它们加入 `pruned`**(绝不加入该智能体自己的起点/终点,`:170-178`),然后**重规划** —— 以 `MaxReplanAttempts = 8` 为上界(`:40`、`:118`)。
5. 若没有新东西可剪,重规划就会是空操作(no-op)→ 停止(`:184-186`)。

每次重规划都**严格缩小搜索空间**(更多被列入黑名单的资源),所以这个内循环可证明会终止 —— 它要么绕开争用,要么以无路径告终,待下个 tick 在某个持有者释放后再重试(`:25-35`)。明确的 v0 说明(`:32-35`):立即重试的避让通过 CP/Lane 黑名单来表达,*正是因为*规划器尚未查阅安全区间;**当 SIPP 在 v1 落地时,这段循环体不变** —— SIPP 额外地在时间上绕开视图。

静态障碍的种子用的是同一机制:`FleetLoopDriver` 把已停泊(已到达)车辆的 CP 作为 `blockedResources` 喂入,使车队其余成员绕开它们(`FleetLoopDriver.cs:202-205`、`CoordinationCycleService.cs:108-112`)。

---

## 6. 组合 / 接线

`PathPlanningNativeInjectorBootStrapper.RegisterServices`(`Infra.CrossCutting.IoC/...:33-52`),并按 grukirbs 的 `*NativeInjectorBootStrapper` 约定提供一个 `WebApplicationBuilder` 重载(`:22-27`),以便 Host 统一接线每个上下文:

```csharp
services.AddSingleton<IPathPlanner, DijkstraPathPlanner>();        // stateless strategy → singleton
services.AddSingleton<IReservationQuery, NullReservationQuery>();  // v0 default read seam
services.AddScoped<IPathPlanningAppService, PathPlanningAppService>();
services.AddLogging();
services.AddAutoMapper(_ => { }, typeof(PathPlanningMappingProfile).Assembly);
```

没有 DbContext / 仓储 / 工作单元 —— PathPlanning 是一个纯计算上下文(`:16-18`)。

**`NullReservationQuery` 的注册就是那个覆盖接缝。** 它是 v0 的默认值,使 PathPlanning 在没有 TrafficControl 的情况下也能**独立**构建和运行(`NullReservationQuery.cs:5-9`)。当 TrafficControl 处于组合中时,*它的*引导器会注册 `IReservationQuery → ReservationService`(`TrafficControl/.../TrafficControlNativeInjectorBootStrapper.cs:88`),由于在 Host 中它注册在 PathPlanning 之后,因此对 `GetRequiredService` 而言它**胜出**,并提供由实时预约表支撑的视图。无论哪种情形,规划器契约都不变(`IReservationQuery.cs:19-22`)。

### Application 编排

`PathPlanningAppService.PlanForAsync`(`Application/Services/PathPlanningAppService.cs:59-90`):

1. 构建 + 校验 `PlanRequest`(v0 中 release time 为 0)(`:67`)。
2. `_roadmapQuery.GetGraphAsync` —— Map 读取接缝;对未知路网抛出 `KeyNotFoundException`(`:70`)。
3. `_reservationQuery.GetView` —— v0 中始终空闲(`:73`)。
4. `_planner.Plan(...)`(`:75`)。
5. 把结果包进一个新建的 `AgentPlan` 聚合(它会触发 `Computed`/`Failed`)并派发事件(`:78-87`)。
6. `_mapper.Map<PlanResultDto>`(`:89`)。

由于本上下文没有 `BaseDbContext.Commit()` 来冲刷(flush)聚合事件,因此 `IDomainEventDispatcher` 与 `IIntegrationEventPublisher` 是被**可选地**解析并直接调用的;当二者都未注册时(单元测试 / 独立运行),事件会收集在聚合上而干脆不派发(`:26-31`、`:96-111`)。

### `AgentPlan` 聚合与事件

`AgentPlan`(`Aggregates/AgentPlan.cs:21`)把一台车辆的当前计划建模为一个名副其实的、会触发事件的聚合,尽管它从不被持久化 —— "协调器为每台车辆持有一个 `AgentPlan`,并跨 tick 对其重新规划"(`:10-19`)。`Apply(result)` 设置 `Computed`/`Failed` 状态,并触发 `AgentPlanComputedEvent`(携带 CP 序列 + 代价)或 `AgentPlanFailedEvent`(`:143-171`)。`Replan(...)` 和 `Invalidate(...)` 递增 `StateVersion`(乐观并发)并重新触发(`:104-141`)。两个事件都是 `IIntegrationEvent`,分别命名为 `PathPlanning.AgentPlan.Computed` / `.Failed`,版本 `v1`(`Events/AgentPlanComputedEvent.cs:68-71`、`Events/AgentPlanFailedEvent.cs:53-56`)。

---

## 7. 测试 —— `SwarmRoute.PathPlanning.Tests`

xUnit;三个套件 + 两个测试支持 fake。

- **`DijkstraPathPlannerTests`** —— 规划器的正确性(`DijkstraPathPlannerTests.cs`):
  - 最短路径选择:线性链、菱形(两个方向都选更便宜的分支),与 `RoadmapGraph.DistanceTo` 交叉校验(`:41-90`)。
  - 尊重有向性 —— 反向不可达(`:106-115`)。
  - 失败分支:`PP-003` 不可达、`PP-002` 未知起点/终点(`:92-129`)。
  - `start == goal` → 单个非退化格子,零代价(`:131-144`)。
  - **时间线良构性**:4 个 CP + 3 个 Lane 格子,第一个从 release 开始,CP 区间连续 + 严格递增 + 不重叠,移动时长 == 缩放后权重 `1000/2000/3000`,车道为 `A-B/B-C/C-D`(`:146-200`)。
  - **黑名单剪枝**:一个被列入黑名单的中间 CP *以及*一条被列入黑名单的 Lane,各自迫使走另一条分支(`:202-229`)。
  - 空参数保护(`:231-240`)。
- **`AgentPlanTests`** —— 聚合行为:构造触发正确的事件,`Replan`/`Invalidate` 递增版本 + 触发,id/原因保护(`AgentPlanTests.cs`)。
- **`PathPlanningAppServiceTests`** —— 通过**真实**的 `PathPlanningNativeInjectorBootStrapper`(真实规划器 + `NullReservationQuery` + AutoMapper + app service)进行端到端测试,仅把 Map 接缝换成 `FakeRoadmapQueryService`(`PathPlanningAppServiceTests.cs:20-31`):成功时返回有序站点序列 + 代价、`PP-003` 失败 DTO、未知路网的 `KeyNotFoundException`、空 agent id 校验。
- **`TestSupport/`** —— `RoadmapGraphBuilder`(在*真实*的 `RoadmapGraph.Build` 之上的流式构建器,距离→`round(d×1000)` 权重)和 `FakeRoadmapQueryService`(遵守 `KeyNotFoundException` 契约的内存 `IRoadmapQueryService`)。

没有 SIPP / 预约搜索的测试 —— v0 中不存在。

---

## 8. v0 状态与 v1 路线图

| | **v0 —— 已交付** | **v1 —— 计划中** |
|---|---|---|
| `PlannerKind` | `Dijkstra = 1`(`PlannerKind.cs:13-14`) | `Sipp = 2`(预留槽位,`:16-17`) |
| 算法 | 剪枝的单智能体 Dijkstra,**仅空间** | SIPP —— 安全区间路径规划,**在时间上感知预约** |
| 预约视图 | 接受但**从不读取**;`AlwaysFreeReservationView` | 被搜索:`FreeIntervals` / `IsFree` 驱动安全区间扩展 |
| 争用避让 | 请求的 **CP/Lane 黑名单** + Coordination 剪枝重规划 | 以上**外加**在时间上绕开视图(等待) |
| `WaitAction` | 已定义,**从不发出**(只含移动的时间线) | 被发出以占住一个 CP 并让另一台车辆通过 |
| CP/Lane 格子 | 共享一个区间(保守的过度近似) | 可细化精确的进入/退出时刻 —— *相同的资源词汇表* |

接缝经过精心设计,使这个替换是外科手术式的:**`IPathPlanner` 在 v0→v1 之间不变**(`IPathPlanner.cs:7-11`),IoC 那行 `AddSingleton<IPathPlanner, …>` 是唯一会变的注册,而 Coordination 的循环体被明确设计为不变(`CoordinationCycleService.cs:32-35`)。v1 纯粹是对资源被取用的*时机*的一次*增量式*细化,叠加在 v0 已然正确的*内容*之上。

---

## 跨上下文依赖(摘要)

- **Kernel**(`Shared/SwarmRoute.SpatioTemporal.Kernel`)—— 冻结的词汇表:`SpaceTimePath`/`SpaceTimeCell`/`TimeInterval`/`ResourceRef`/`SafeInterval`、读取接缝 `IReservationView`,以及 `IFleetClock`。PathPlanning 产出 `SpaceTimePath`;声明返回 `IReservationView` 的 `IReservationQuery`。
- **Map**(`Map.Domain` + `Map.Application.Contract`)—— 通过 `IRoadmapQueryService.GetGraphAsync` 以只读方式消费 `RoadmapGraph` 值对象(`HasSite`/`Neighbours`/`EdgeWeight`/`SiteRef`/`LaneRef`,`WeightScale = 1000`)。
- **TrafficControl** —— 把 PathPlanning 的 `IReservationQuery` *实现*为 `ReservationService`,并**覆盖** `NullReservationQuery` 的 DI 注册(`TrafficControlNativeInjectorBootStrapper.cs:88`)。拥有预约写入接缝(`TryReserve`/`Release`),它**不**属于 PathPlanning。
- **Coordination** —— 直接*消费* `IPathPlanner`;拥有多智能体滚动时域循环,以及那个通过请求黑名单驱动 v0 争用避让的**剪枝重规划**契约(`CoordinationCycleService`)。
- **Simulation** —— 驱动闭环,并提供那个离散的 `ManualFleetClock`(tick = CP 跳),使规划出的区间 == 执行 tick(`FleetLoopDriver`)。
