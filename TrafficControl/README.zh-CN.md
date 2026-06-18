# Traffic Control (交通管制)

> 简体中文 · English version: [README.md](README.md)

*为车队持有路权：谁可以在哪个时间区间占用哪个路网资源——授予、拒绝、释放——并将争用情况上报给 Deadlock。它不做路径规划，也不运行车队循环。*

---

## 1. 目的与职责

Traffic Control 是仲裁路网**时空占用**的限界上下文。它的单一事实来源是一个内存中的聚合根 `ReservationTable`（`SwarmRoute.TrafficControl.Domain/Aggregates/ReservationTable.cs:29`），持有一组活跃的**区间租约**：`(resource × time interval × agent)` 三元组。围绕它的是若干无状态领域服务，回答三个问题：

- **现在能否为该 agent 预约这条完整路径？** → `IResourceAllocator.Allocate` / `ReservationTable.TryGrant`（`ReservationTable.cs:129`）。
- **哪些是空闲的，何时空闲？** → `IReservationCalendar` / `FreeIntervals` / `IsFree`（`ReservationTable.cs:271`、`:323`），以只读的 `IReservationView` 暴露给 PathPlanning。
- **谁在阻塞谁？** → `ITrafficControlSnapshotProvider` 构建 Deadlock 上下文扫描的 `ResourceAllocationGraphSnapshot`（Owns/Waits 边）。

它是原引擎可变 `GraphMap`（`_sites/_lines/_blocks` 状态 + `_agvPathDic`）的 DDD 继任者，从二元的 `Locked/Unlocked` 标志重新表达为真正的时间区间租约（`ReservationTable.cs:11-16`）。它刻意**不**承担两项职责:路径搜索（PathPlanning）和控制/执行循环（Coordination + Simulation）。Traffic Control 只负责说*是/排队/阻塞*以及*已释放*。

### 核心设计决策（"为什么"）

| 决策 | 理由 | 位置 |
|---|---|---|
| **区间租约，而非简单锁** | 锁回答的是"它被持有了吗?";租约回答的是"被谁持有、在哪个时间窗口、处于哪个生命周期状态"。这是 v0 引擎缺失的时间轴，也是 SIPP 在 v1 无需改模型即可使用的数据。 | `ResourceLease.cs:7-17`、`TimeInterval.cs` |
| **单写者单例聚合根** | 一个车队、一个时钟、一张权威表 → 无分布式锁、无合并冲突。以 `AddSingleton` 注册（不变式 I5）。 | `TrafficControlNativeInjectorBootStrapper.cs:73-74` |
| **热点路径用内存;EF 仅用于审计** | 预约的授予/释放在每次规划中会发生数千次;一次数据库往返就会成为主导开销。EF 在热点路径之外持久化快照/审计行（ADR-002 / R2）。 | `ReservationAuditRecord.cs:3-9`、`TrafficControlDbContext.cs:11-16` |
| **拓扑闭包抽象出 Domain 之外** | 授予/释放时"还有哪些资源必须随之移动"的集合（父区块 + 干涉）属于 Map 的知识;通过 `IResourceTopology` 抽象它，可使 `TrafficControl.Domain` 不依赖任何 Map。 | `IResourceTopology.cs:5-19` |
| **争用请求即 RAG 的"Waits"边** | 将拒绝记录为 `ReservationRequest`，使 Deadlock 能看到等待图，并让升级作业对等待者做老化处理 → 避免饥饿。 | `ReservationTable.cs:39`、`ReservationRequest.cs:13-17` |

---

## 2. 分层与项目

标准的 grukirbs/NetDevPack 洋葱架构。依赖指向内层;Domain 仅引用 Kernel 与抽象。

```
SwarmRoute.TrafficControl.Domain.Shared      enums + error codes, zero deps
        ▲
SwarmRoute.TrafficControl.Domain             aggregate, value objects, domain services, state machine
   refs: SpatioTemporal.Kernel, Domain.Abstractions, StateMachine.Core, NetDevPack, Domain.Shared
        ▲
SwarmRoute.TrafficControl.Application.Contract   frozen cross-context seams + DTOs
   refs: SpatioTemporal.Kernel, Domain.Shared
        ▲
SwarmRoute.TrafficControl.Application         app services, SystemFleetClock, topology adapter, subscriber
   refs: Domain, Application.Contract, PathPlanning.Domain (to implement IReservationQuery)
        ▲                                  ▲                                  ▲
Infra.Data (EF audit)        Infra.BackgroundJobs (Hangfire)        Infra.CrossCutting.IoC (composition)
        ▲                                  ▲                                  ▲
                              SwarmRoute.TrafficControl.Api (operator HTTP)
```

| 项目 | 角色 | 关键依赖 |
|---|---|---|
| **Domain.Shared** | `AllocationOutcome`、`ConflictType`、`LeaseState`、`TrafficControlErrorCodes`。无项目引用。 | — |
| **Domain** | `ReservationTable` 聚合根;值对象（`ResourceLease`、`ReservationRequest`、`Conflict`、`RightOfWay`）;领域服务（`ResourceAllocator`、`ConflictDetector`、`ReservationCalendar`）及其接口;`IResourceTopology`;租约的 `TrafficControlStateMachine` 及守卫。 | `SpatioTemporal.Kernel`、`StateMachine.Core`、`NetDevPack` |
| **Application.Contract** | 三个冻结的接缝:`ITrafficCoordinatorAppService`（写）、`ITrafficControlSnapshotProvider`（读 → Deadlock）、`ITrafficControlOperatorAppService`（操作员）;DTO。 | `SpatioTemporal.Kernel` |
| **Application** | `TrafficCoordinatorAppService`、`TrafficControlSnapshotProvider`、`TrafficControlOperatorAppService`、`ReservationService`（实现 PathPlanning 的 `IReservationQuery`）、`SystemFleetClock`、`DictionaryResourceTopology`、`ReplanTriggerSubscriber`。 | `Domain`、`Application.Contract`、**`PathPlanning.Domain`** |
| **Infra.Data** | `TrafficControlDbContext` + `ReservationAuditRecord` —— **仅快照/审计**。EF Core + Npgsql。 | `Domain`、`Infra.Data.Core` |
| **Infra.BackgroundJobs** | `LeaseExpirySweepJob`、`StaleRequestEscalationJob`（Hangfire 周期作业）。 | `Application`、`Hangfire.Core` |
| **Infra.CrossCutting.IoC** | `TrafficControlNativeInjectorBootStrapper` —— 组合根。 | 上述三个 Infra/App 项目 |
| **Api** | `TrafficController` —— 操作员 HTTP 端点（占用、分配图、解锁）。热点路径的 reserve/release **不**通过 HTTP 暴露。 | `Application.Contract`、IoC、`EventBus` |

所有项目均面向 **net10.0 / `LangVersion=latest`（C# 14）**，启用 nullable + 隐式 using（`Directory.Build.props`、各 `.csproj`）。按团队策略不使用中央包管理。

---

## 3. 领域模型

### 3.1 `ReservationTable` 聚合根 —— 权威的活跃状态

一个由单个 `lock (_sync)` 守护的 `sealed` `Entity, IAggregateRoot`（单写者;所有变更方法和快照读取都获取该锁）。它维护一个**双重索引**（`ReservationTable.cs:33-37`）：

- `_byResource : Dictionary<ResourceRef, List<ResourceLease>>` —— 保持**按 `Interval.StartMs` 排序**，使空闲区间计算和冲突检查局限在某个资源的桶内。
- `_byAgent : Dictionary<string, List<ResourceLease>>` —— 使释放和 RAG 快照为 `O(agent's leases)`。
- `_contended : List<ReservationRequest>` —— 排队的请求，即"Waits"边。

**不变式（I）：**不存在两个相互**冲突**的租约共存 —— *相同（或闭包/反向车道）资源、区间重叠、不同 agent*。同一 agent 在同一资源上重叠/相接的窗口会被**合并**，而非重复。每个变更方法都保持该不变式并调用 `Touch()`（`ReservationTable.cs:677`），它会递增 `StateVersion`（乐观并发）并打上 `StateChangedAtUtc` 时间戳。

该不变式在最底层由 `Insert`（`ReservationTable.cs:467-518`）强制执行:在添加租约前，它扫描 `LeasesConflictingWith(resource)`，若不同 agent 发生重叠则**抛出** `TrafficControlErrorCodes.ConflictingLease`（`:470-477`）。Insert 也是发生同一 agent 合并的地方 —— 同一 agent+资源的重叠/相接窗口会坍缩为一个并集租约，而完全覆盖的重复则是返回 `false` 的空操作（`:493-517`）。正是这一点使得 `TryGrant` 对未变更的重复预约是幂等的。

**冲突关系**（`ReservationTable.cs:613-614`）：两个资源在 `Equals` **或** `IsReversedLane`（`"a-b"` 对 `"b-a"`，仅限车道类型 —— `:622-638`）时冲突。`LeasesConflictingWith`（`:601-611`）遍历每个与查询资源冲突的已持有资源，这就是为什么对车道 `A-B` 的请求能正确看到 `B-A` 上的在位者。

#### 关键操作

| 方法 | 契约 | 说明 |
|---|---|---|
| `TryGrant(path, agentId, priority)` | 整条路径加锁 → `Granted` / `Queued` / `Blocked` | `:129` —— 见 §5 |
| `ReleaseBehind(agentId, passedResources)` | 释放已通过资源上的租约**及其闭包**;返回已释放者;部分释放 | `:206` —— `UnlockPath` 泄漏修复 |
| `ReleaseAll(agentId)` | 释放每一条租约 + 移除该 agent 的争用请求 | `:239` —— 中止/到达 |
| `FreeIntervals(resource)` | 在 `[0, long.MaxValue)` 上的最大安全区间 | `:271` —— 租约并集的补集 |
| `IsFree(resource, interval)` | 与 agent 无关:任何人是否有重叠? | `:323` —— 视图语义 |
| `IsFreeForExcept(resource, interval, agentId)` | 忽略该 agent 自身租约后是否空闲 | `:336` —— 用于分配器剪枝 |
| `Refresh(nowMs)` | 驱逐已完全过去的租约;剪除过期争用 | `:354` —— 清扫作业的安全网 |
| `RecordContention` / `ReplaceContended` / `EscalateStaleRequests` | 管理 Waits 集合 + 老化 | `:375`、`:386`、`:405` |
| `DrainDomainEvents()` | 在锁内原子地复制+清空缓冲的事件 | `:88` —— 由应用层发布 |
| `CreateSnapshotView()` | 使用相同闭包语义的不可变 `IReservationView` | `:106` —— 交给规划器 |

#### 空闲区间计算

`FreeIntervals`（`ReservationTable.cs:271-317`）按起始时间排序扫描该资源的冲突租约，合并重叠以找出空隙，并为每个空隙发出一个 `SafeInterval`，外加一个尾部 `[cursor, long.MaxValue)`。由于区间是**半开区间**（`TimeInterval.Overlaps` 为 `Start < otherEnd && otherStart < End`，`TimeInterval.cs:39`），相接的租约（`[0,100)` 接着 `[100,200)`）**不会**把空隙合并掉，也**不会**冲突 —— 一辆车可以在下一辆车进入的同一时刻离开某个单元格。`IReservationCalendar.EarliestFreeStart`（`ReservationCalendar.cs:27-44`）遍历这些区间，找出第一个能容纳所请求时长的区间 —— 即 v1 时 SIPP "最早到达"步骤的雏形。

### 3.2 值对象

- **`ResourceLease`**（`ResourceLease.cs:18`）—— 不可变的 `(Resource, AgentId, Interval, State)`;按全部四项判等。`MapResource.OccupiedBy + Status` 的继任者。`ConflictsWith`（`:50`）、`WithState`（`:59`）、`HasExpiredAt(nowMs)`（`:62`）。
- **`ReservationRequest`**（`ReservationRequest.cs:18`）—— 一个争用请求:移植自 `AJR.MAPF.Map.ResourceRequest`（`AgentId`、`Resource`、`RequestTime`、`EstimateTime`、`HadWaitedTime`），并新增 v0+ 的 `Requested` `TimeInterval` 与显式的 `Priority`。`AgedBy(seconds)`（`:91`）与 `MergedWith`（`:97`）将重复的等待保持为单条边。
- **`Conflict`**（`Conflict.cs:16`）—— 一个被检测到的 `(Type, AgentA, AgentB, ResourceA, ResourceB)`。对于 vertex/following，两个资源相等;对于 edge-swap，它们是相对的两条车道;对于 interference，则是相互干涉的那一对。
- **`RightOfWay`**（`RightOfWay.cs:16`）—— 确定性的平局裁决规则:**Priority 降序 → HadWaitedTime 降序 → AgentId 序数**。第三档保证*全序、稳定*的次序（不靠掷硬币），这正是使循环免于活锁的关键（两个 agent 绝不能反复相互让行）。无状态单例（`Default`）。

### 3.3 资源类型与闭包

`ResourceRef(ResourceKind Kind, string Id)` 是冻结的 Kernel 契约（`ResourceRef.cs:30`）。类型:**CP**（控制点/站点）、**Lane**（有向边/路段）、**Block**（互斥区块）、**Zone**（区域/区域）。

资源的**闭包**是"必须与它一起加锁/释放的一切" —— 资源本身 + 它的父区块 + 它的干涉集 —— 由 `IResourceTopology.ClosureOf`（`IResourceTopology.cs:20-27`，必须包含资源本身）与 `IsBlacklisted`（移植自 `MapResource.AGVBlackList`）建模。两个实现:

- `IResourceTopology.Empty`（`IResourceTopology.cs:39-44`）—— 恒等闭包，无黑名单;v0 默认值与测试基线。
- `DictionaryResourceTopology`（`DictionaryResourceTopology.cs:19`）—— 数据驱动，带流式 `Builder`。位于 **Application**，而非 Domain，以保持 Domain 不依赖 Map。Host 从 Map 发布的干涉/所含站点/黑名单数据来填充它（实践中经由 `MapResourceTopologyAdapter`）。

> **释放泄漏修复。**原 `GraphMap.GeneratePath` 锁定每个路径资源*以及其 `ParentBlock`*（剪枝还会拉入干涉闭包），但 `GraphMap.UnlockPath` 把对 ParentBlock/干涉的释放**注释掉了**，于是区块和被干涉的资源会永久泄漏。在这里，授予（`ExpandClosure`，`ReservationTable.cs:439-453`）与释放（`ReleaseBehind`，`:217-223`）**都**经由*同一个* `ClosureOf`，因此二者在构造上是对称的。该回归由 `ReleaseBehind_frees_parent_block_and_interference_closure_no_leak`（`ReservationTableTests.cs:118`）锁定。

---

## 4. 冲突分类法

`ConflictDetector`（`ConflictDetector.cs:27`）是无状态的（单例安全），它对一个候选单元格（资源 `R`、区间 `I`、agent `A`）与一个由**不同** agent 在重叠区间上持有的在位租约之间的每次冲突进行分类。它查询 `IResourceTopology` 获取干涉信息，从而与 Map 解耦。

```
                 same resource?                       reversed lane?            closure member held?
candidate cell ──────┬───────────────────────┐     ┌──────────────┐          ┌────────────────────┐
                     │                        │     │              │          │                    │
        enters ≤ incumbent        enters > incumbent  "a-b" vs "b-a"      member ≠ R held by other
              │                        │                  │                        │
         VertexSame                Following           EdgeSwap                Interference
```

| 类型 | 谓词 | 含义 | 来源 | 代码 |
|---|---|---|---|---|
| **VertexSame** | 同一资源、重叠、候选 `StartMs ≤` 在位者 `StartMs` | 对头/同时占用同一单元格 | MAPF 顶点冲突 | `ConflictDetector.cs:54-60` |
| **Following** | 同一资源、重叠、候选 `StartMs >` 在位者 `StartMs` | 尾随进入尚未清空的单元格 | MAPF 跟随冲突 | `ConflictDetector.cs:57-59` |
| **EdgeSwap** | 二者均为 Lane、id 互为反向（`"a-b"`/`"b-a"`）、重叠 | 两辆 AGV 在同一物理边上交换位置 | MAPF 边/交换冲突 | `ConflictDetector.cs:62-65` |
| **Interference** | `R` 的某个闭包成员（≠ `R`）被他人在重叠区间内持有 | 相互干涉的资源被同时占用 | AGV 干涉站点/线路 | `ConflictDetector.cs:69-81` |

**反向车道语义。**车道 id 遵循引擎的 `"start-end"` 约定，因此 `"a-b"` 的反向是 `"b-a"`。`IsReversedLane`（`ConflictDetector.cs:92-108`，在聚合根中于 `ReservationTable.cs:622-638` 处镜像）按第一个 `-` 拆分，并检查 `aStart==bEnd && aEnd==bStart`。它被应用于**每一处资源匹配** —— 授予、`FreeIntervals`、`IsFreeForExcept` 以及快照 —— 因此一个反向车道的预约被视为同一条物理边。关键在于,记录的 Waits 边指向**在位者**的车道 id，而非请求者的（`ReservationTable.cs:155-160`;由 `Reversed_lane_overlap_is_queued...`（`ReservationTableTests.cs:101`）和 `Reversed_lane_contention_waits_on_the_blocking_owned_lane`（`SnapshotProviderTests.cs:70`）锁定）。

> `ConflictDetector` 是租约状态机的 `NoConflictGuard` 所用的*分类器*，也可用于诊断;而*授予*路径（`TryGrant`）会做自己更快的空闲/黑名单检查，而非调用该检测器。它们共享相同的冲突关系，因此结论一致。

---

## 5. 分配流程

### TryReserve（整条路径，全有或全无）

`ITrafficCoordinatorAppService.TryReserveAsync` → `ResourceAllocator.Allocate` → `ReservationTable.TryGrant`（`TrafficCoordinatorAppService.cs:37`、`ResourceAllocator.cs:21`、`ReservationTable.cs:129`）。`TryGrant` 内部的流程:

```
TryGrant(path, agent, priority)
  1. empty path?                         → Blocked
  2. ExpandClosure(path)                 → list of (member, interval) cells (parent block + interference)
  3. for each cell:
       blacklisted(member, agent)?       → record contended, mark blacklisted
       another agent overlaps (closure)? → record contended, mark contended  (Waits edge points at the blocker)
  4. blacklisted → Blocked ;  contended → Queued
        (emit ReservationDenied + AllocationContended, create NO lease)
  5. all free → Insert() every closure cell  (Reserved)            ← whole-path lock
        drop this agent's stale Waits + prune now-satisfied waits
        emit ReservationGranted
        → Granted
```

这是原引擎整条路径加锁的忠实移植:一条路径**只有在其每个资源（闭包展开后）对该 agent 均空闲时**才被授予;否则*不锁定任何东西*，请求被排队（`WholePath_grant_then_crossing_path_is_queued`，`ReservationTableTests.cs:58`）。结果（`AllocationOutcome.cs`）：**Granted**、**Queued**（争用;记为 Waits）、**Blocked**（被黑名单 —— 按原样永不可授予）。`Preempted` 为 v1 预留，目前永不产生。

一次成功授予还会**剪除该 agent 的过期等待边**（`ReservationTable.cs:182-183`），使 RAG 反映当前争用，而非重试历史（`Successful_retry_removes_the_agents_stale_wait_edges`，`ReservationTableTests.cs:87`）。

### 后向释放（单调,随 agent 前进）

当 agent 驶过资源时，Coordination 调用 `ReleaseAsync(agentId, passedResources)` → `ReleaseBehind`（`TrafficCoordinatorAppService.cs:57`、`ReservationTable.cs:206`）。它将每个已通过的资源展开为其闭包，释放该 agent 匹配的租约，剪除现已可满足的争用请求，并发出 `ReservationReleasedEvent(partial: true)`。它只会释放*过去*的部分（单调;不变式 I6）—— agent 仍保持对前方单元格的持有。

### ReleaseAll（到达/中止）

`ReleaseAll(agentId)`（`ReservationTable.cs:239`）释放该 agent 持有的每一条租约,**并**移除其争用请求（它不再等待任何东西），发出 `ReservationReleasedEvent(partial: false)`。

### BlockedResources（剪枝并重规划）

`ResourceAllocator.BlockedResources`（`ResourceAllocator.cs:29-45`）回答"规划器应当删除并重新搜索哪些候选路径资源?"它在完整闭包上检测阻塞（`CellIsBlocked` 对每个闭包成员检查黑名单 + `IsFreeForExcept`，`:47-59`），但**仅**返回规划器可剪枝的 CP/Lane 资源（`IsPlannerPrunable`，`:61-62`）—— 区块/干涉资源对规划器不可见，因此该冲突被映射*回*规划器实际可剪枝的 CP/Lane。由 `BlockedResources_maps_block_contention_back_to_candidate_cp/lane`（`ResourceAllocatorTests.cs:11`、`:30`）锁定。

---

## 6. 车队时钟与时间轴

每个 `TimeInterval` 都针对一个单调的**车队时钟** `IFleetClock.NowMs`（Kernel，`IFleetClock.cs:7-11`）来表达。存在两个实现，二者的差异是关键所在:

- **`SystemFleetClock`**（`Application/Services/SystemFleetClock.cs:9`）—— 生产默认值:车队时间 = `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`。以 `AddSingleton<IFleetClock>` 注册（`TrafficControlNativeInjectorBootStrapper.cs:71`）。一个共享实例为整个车队提供唯一的挂钟时间轴。它驱动 `LeaseExpirySweepJob`（`LeaseExpirySweepJob.cs:31`）。

- **`ManualFleetClock`**（Simulation，`Simulation/.../ManualFleetClock.cs:18`）—— 一个离散的、由外部推进的时钟。`FleetLoopDriver` 在每个规划周期之前将其设为当前的整数 **tick**（`SetTick`，`:24`），使每个被预约的区间都落在执行器所推进的*同一*轴上（一个 tick = 一次控制点跳跃）。

> **为什么该时间轴对无碰撞至关重要。**预约表基于区间的保证（"不存在两个冲突租约重叠"）只有在预约与执行共享一条时间轴时，才在执行时刻成为*真正的*保证。在挂钟式 `SystemFleetClock` 下，控制周期以亚毫秒级运行，因此表认为*在时间上分离*的两个预约,可能会在同一 tick 上于同一控制点被执行 —— 模型是正确的，但轴被解耦了。因此仿真**用 tick 时钟覆盖 `IFleetClock`**（记录于 `ManualFleetClock.cs:11-17`），消除这种不匹配。*（交叉引用:Coordination 驱动循环;Simulation 拥有 tick 时钟。Traffic Control 只拥有生产用的 `SystemFleetClock`，并消费 Host 所注册的任意 `IFleetClock`。）*

---

## 7. 事件与集成

### 领域/集成事件

四者都扩展 NetDevPack 的 `DomainEvent` 并实现 `IIntegrationEvent`（版本化为 `"v1"`）。聚合根缓冲它们;应用层经由 `DrainAndPublishAsync` 排空并发布，因为内存热点路径从不触及 `BaseDbContext.Commit`（`TrafficCoordinatorAppService.cs:71-93`）。

| 事件 | 触发时机 | `EventName` |
|---|---|---|
| `ReservationGrantedEvent` | 整条路径授予并创建了租约 | `TrafficControl.Reservation.Granted` |
| `ReservationDeniedEvent` | 授予被排队或被阻塞（携带结果） | `TrafficControl.Reservation.Denied` |
| `ReservationReleasedEvent` | 租约被释放（`partial` = 后向释放 vs 全部释放） | `TrafficControl.Reservation.Released` |
| `AllocationContendedEvent` | 一个请求变为争用 / 被老化（携带 Waits 计数） | `TrafficControl.Allocation.Contended` |

### 集成

- **→ Deadlock（已验证）。** Deadlock 上下文的 `AllocationContendedSubscriber` 绑定到 `"TrafficControl.Allocation.Contended"`（`Deadlock/.../Subscribers/AllocationContendedSubscriber.cs:21`），收到后拉取一份新的 `ResourceAllocationGraphSnapshot`（经由其 `IDeadlockSnapshotProvider`，底层由 Traffic Control 的 `ITrafficControlSnapshotProvider` 支撑）并运行环路检测 —— 从不持有 Traffic Control 的锁。该快照将**活跃租约 → Owns**、**争用请求 → Waits**（`TrafficControlSnapshotProvider.cs:25-36`）。
- **→ Coordination（桩）。** `ReplanTriggerSubscriber`（`Application/Subscribers/ReplanTriggerSubscriber.cs:14`）是 v0 占位实现,集成时它会把 `ReservationDenied` 载荷送入 Coordination 的重规划队列。它可独立编译和测试;仅 CAP 传输绑定为 `TODO(integration)`。

### 防饥饿/升级（无活锁,不变式 I7）

争用请求携带 `HadWaitedTime`。`StaleRequestEscalationJob.Escalate`（`StaleRequestEscalationJob.cs:32`）→ `ReservationTable.EscalateStaleRequests`（`ReservationTable.cs:405-433`）将每个未决请求按 `agingSeconds`（默认 1）老化，使长期等待者的 `RightOfWay` 平局裁决最终胜过更新的、同优先级的争用者。每一轮还会剪除现已可满足的请求，并抛出一个 `AllocationContendedEvent`（主体 = 等待最久者）以促使 Deadlock 重新扫描。由 `StaleRequestEscalationJob_ages_contended_requests_and_emits_event`（`CalendarAndJobsTests.cs:48`）锁定。

### 租约生命周期状态机

`TrafficControlStateMachine`（`Domain/StateMachine/TrafficControlStateMachine.cs:18`）将单条租约建模在 `LeaseState` 之上:`Requested →(Grant)→ Reserved →(Enter)→ InTransit →(Pass)→ Releasing →(Release)→ Free`。`Grant` 转换由可组合的守卫把关，以声明式表达原本"我能锁定它吗?"的谓词:`ResourceAvailableGuard`（无其他 agent 重叠）、`NoConflictGuard`（包裹 `IConflictDetector`）、`NotBlacklistedGuard`（`Domain/StateMachine/Guards.cs`）。聚合根仍是全表不变式的权威;该状态机守卫单条租约的转换,是 v1 的逐租约生命周期挂钩点。

---

## 8. 持久化

预约热点路径上**没有 EF**。权威状态是内存中的单例聚合根。`Infra.Data` 仅为快照/审计而存在（ADR-002 / R2）：

- `ReservationAuditRecord`（`Infra.Data/Entities/ReservationAuditRecord.cs:10`）—— 一个刻意保持**扁平**的行（不是映射的聚合根，因此热点路径不引入 EF 依赖）：`Id`、`ReservationTableId`、`StateVersion`、`AgentId`、`Action`（`Granted`/`Queued`/`Released`…）、`LeaseCount`、可选的 `LeasesJson`（完整快照）、`CreatedAtUtc`。
- `TrafficControlDbContext`（`Infra.Data/Context/TrafficControlDbContext.cs:21`）—— 派生自 `BaseDbContext`（用于标准的 UoW/事件管线），但只映射 `ReservationAudits`;`LeasesJson` 为 `jsonb`，在 `ReservationTableId` 和 `CreatedAtUtc` 上建立索引。`OnModelCreating` 中 `Ignore<Event>()`，因此领域事件从不被持久化。迁移 `20260618070926_InitialCreate` 创建单张 `ReservationAudits` 表。
- `TrafficControlDbContextFactory` —— 仅设计时使用,为 `dotnet ef` 提供占位的 Npgsql 连接字符串。

连接字符串（`"TrafficControlDatabase"`）在开发/设计时可能**缺失** —— 引导程序会注册该上下文，但仅在字符串存在时才调用 `UseNpgsql`（`TrafficControlNativeInjectorBootStrapper.cs:42-47`），因此该上下文（以及整个上下文）可在无数据库的情况下运行。

---

## 9. 组合/装配

`TrafficControlNativeInjectorBootStrapper.RegisterServices`（`Infra.CrossCutting.IoC/TrafficControlNativeInjectorBootStrapper.cs:37`）是组合根,既有 `WebApplicationBuilder` 重载（装配审计 DbContext），也有面向非 Web 宿主/测试的裸 `IServiceCollection` 重载。`RegisterCore`（`:64-96`）注册:

- `IResourceTopology.Empty`（单例）—— v0 的恒等闭包默认值;**Host 用** `MapResourceTopologyAdapter` 覆盖它（后注册者胜出，`host/.../Program.cs:66`）。
- `IFleetClock → SystemFleetClock`（单例）。
- **`ReservationTable` 作为进程级单例**（`:74`）—— 唯一的写者。
- 无状态领域服务作为单例:`IResourceAllocator`、`IReservationCalendar`、`IConflictDetector`。
- 应用服务:`ITrafficCoordinatorAppService` 与 `ITrafficControlOperatorAppService` 为 **scoped**（使每请求的事件发布生效），`ITrafficControlSnapshotProvider` 为单例。
- **关键覆盖:** `IReservationQuery → ReservationService`（`:88`）。PathPlanning 声明了 `IReservationQuery` 并随附一个 `NullReservationQuery`（始终空闲的桩）。由于 Host **先**调用 PathPlanning 的引导程序，**后**调用 Traffic Control 的，这个较晚的注册胜出,于是规划器读取活跃的预约表（`ReservationService.cs:7-13`、`Program.cs:11-17`、host 注释 `Program.cs:53`）。`ReservationService.GetView` 返回一个时间点的 `CreateSnapshotView()`，使 SIPP 式搜索读取真实的安全区间,而从不修改该表。
- `ReplanTriggerSubscriber` 和两个 Hangfire 作业（单例;调度在 Host 中装配）。

Host 还经由 `TrafficDetourReservationAdapter → ITrafficCoordinatorAppService.TryReserveAsync`（`host/.../Adapters/TrafficDetourReservationAdapter.cs`）将 Coordination 的绕行预约路由回本上下文的写接缝。

---

## 10. 测试

`SwarmRoute.TrafficControl.Tests`（xUnit）通过内存聚合根（无 DB）端到端地覆盖该上下文,共享构建器位于 `TestHelpers.cs`（`Cp/Lane/Block`、`CpPath`、`ClosureTopology`）。

| 文件 | 锁定的内容 |
|---|---|
| `ReservationTableTests.cs` | 不变式（冲突授予 → Queued，仅一条租约）;允许不相交/相接的窗口;整条路径全有或全无;反向车道针对在位车道排队;**释放无泄漏回归**（闭包被释放）;`ReleaseAll` 清空租约+等待;`FreeIntervals`/`IsFree`/`IsFreeForExcept`（含反向车道）;`StateVersion` 递增;`Refresh` 驱逐 + 争用剪枝;幂等的 `RecordContention`;黑名单 → Blocked;同一 agent 合并 + 完全重复的幂等性。 |
| `ConflictDetectorTests.cs` | 全部四种分类（VertexSame、Following、EdgeSwap、经闭包的 Interference）;时间分离时无冲突;与自身租约无自冲突。 |
| `ResourceAllocatorTests.cs` | `BlockedResources` 将区块争用映射回候选 CP / Lane（而非区块）。 |
| `TrafficCoordinatorAppServiceTests.cs` | 授予 → 交叉排队 → 后向释放解除阻塞;释放经接缝释放完整闭包;`ManualUnlock` 排空+发布 `ReservationReleasedEvent`。 |
| `SnapshotProviderTests.cs` | Owns/Waits 映射;空表;已释放的租约从 Owns 中移除;资源类型被保留;反向车道 Waits 指向阻塞的已持有车道。 |
| `ReservationServiceTests.cs` | `ReservationService` 是一个 `IReservationQuery`;该视图是稳定快照（在后续授予后旧视图保持不变）;反向车道 + 闭包语义与写者一致。 |
| `CalendarAndJobsTests.cs` | `EarliestFreeStart` 找到第一个适配的窗口;`LeaseExpirySweepJob` 驱逐过期者;`StaleRequestEscalationJob` 老化等待 + 发出 `AllocationContendedEvent`。 |
| `RightOfWayTests.cs` | Priority → 等待时间 → 序数 id 排序;全序性/反对称性;无论参数顺序如何 `Winner` 都确定。 |
| `TrafficControlStateMachineTests.cs` | 顺利路径 `Requested→Free`;无效转换失败且不改变状态;`Grant` 被 `ResourceAvailable`/`NotBlacklisted` 守卫阻挡;在 `NoConflict` 下成功。 |

---

## 11. v0 状态与 v1 路线图

**v0（当前）。**忠实移植引擎的整条路径空间锁,但建立在真正的区间模型之上:

- `TryGrant` **一次性预约整条路径的时间线**（≈ 原始的整条路径锁）;分配器策略为"全有或全无的整条路径"。
- 仅产生 `Granted` / `Queued` / `Blocked`;`Preempted` 为预留。
- 一张全局 `ReservationTable`（一个车队，一个时钟）;`roadmapId` 为契约形状而被接受,但无论如何都对同一张表做快照（`ReservationService.cs:14-18`）。
- 闭包默认为恒等,除非 Host 装配了由 Map 支撑的拓扑。

**为什么它已经为 SIPP 做好准备。**模型本身没有任何东西是二元锁:租约是 `(resource × interval)`，日历已经计算 `FreeIntervals` / `EarliestFreeStart`，而读接缝（`IReservationView`）恰好把 SIPP 所消费的*每资源安全区间*交给规划器。在 v1 换入安全区间分配是 `IResourceAllocator`/`IReservationCalendar` 背后的一次**策略变更，而非对聚合根的模型变更**（陈述于 `ReservationTable.cs:24-27`、`IReservationCalendar.cs:7-11`）。

**v1 路线图。**
- **安全区间（SIPP）分配** —— 在同一个 `IResourceAllocator` 接口背后,用逐单元格的安全区间预约替换整条路径加锁;规划器已经读取 `FreeIntervals`/`EarliestFreeStart`。
- **优先级规划 / 抢占式路权** —— 使用已就位的 `RightOfWay` 规则产生 `AllocationOutcome.Preempted`。
- **逐租约生命周期** —— 随着执行遥测的到来,经由 `TrafficControlStateMachine` 驱动 `Reserved → InTransit → Releasing`（当前释放是粗粒度的 `ReleaseBehind`）。
- **审计之外的持久化** —— 周期性的完整快照（`LeasesJson`）用于崩溃恢复;模式已经支持它。
