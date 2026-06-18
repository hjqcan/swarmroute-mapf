# Map / Resources (資源・地圖)

> 简体中文 · English version: [README.md](README.md)

*拥有车队静态路网的限界上下文——这是一张持久化的图,由控制点(站点)和有向车道(路段)构成,所有其他上下文都在其之上进行规划、预约与行驶。*

---

## 1. Purpose & bounded-context responsibility

Map 上下文是车队作业区域**静态拓扑的唯一可信来源**。它拥有:

- **路网聚合**——一组具名、带版本的站点、有向线段与互斥块(`Roadmap`,位于 `Map/SwarmRoute.Map.Domain/Aggregates/Roadmap.cs:23`)。
- **构建好的读模型**——一张内存中的有向加权图(`RoadmapGraph`,位于 `Map/SwarmRoute.Map.Domain/ValueObjects/RoadmapGraph.cs:23`),下游规划器在其上运行 Dijkstra/SIPP。
- **导入 / 发布生命周期**——校验拓扑、持久化它,并通过集成事件对外通告。
- **静态干涉几何**——哪些站点/线段在物理上相互重叠(`InterferenceCalculator`、`InterferenceSet`)。

它明确**不**拥有:

| Concern | Owner |
|---|---|
| Reservations / locks / occupancy (`Locked`/`OccupiedBy`) | **TrafficControl** (reservation table) |
| Path search / agent plans | **PathPlanning** |
| The per-tick coordination loop, goal selection | **Coordination** |
| Per-agent blacklists, runtime resource state | **TrafficControl / Host** (runtime) |
| Deadlock detection | **Deadlock** |

这一拆分是刻意为之且承载关键职责的。第一代引擎将拓扑与实时占用融合在 `MapSite`/`MapResource` 上;本次移植在这里只保留*静态*字段。`MapSite`(`Map/SwarmRoute.Map.Domain/Entities/MapSite.cs:13`)携带类型、位姿、启用标志与干涉引用——**没有**动态锁状态。即便是移植过来的 `MapResourceStatus` 枚举也注明:`Locked`/`Belong` 仅为保持导入保真度而保留;权威副本存放在 TrafficControl(`Map/SwarmRoute.Map.Domain.Shared/Enums/MapResourceStatus.cs:13`)。

该上下文将上游**三份**重复的 `GraphMap` 实现(`AJR.MAPF.Map`、`AJR.Platform.GraphMapDP`、……)整合为单一聚合,并顺带修复了两个源码缺陷:`MapSiteType` 重复值冲突,以及 `MapLine.Distince` 的拼写错误(见 §3、§8)。

---

## 2. Layers & projects

标准的 grukirbs 风格 DDD 洋葱架构。依赖方向**向内**;API 与 IoC 位于最顶层。

```
Domain.Shared  ←  Domain  ←  Application.Contract  ←  Application  ←  Infra.CrossCutting.IoC  ←  Api
                    ↑                                       ↑              ↑
                    └──────────── Infra.Data ───────────────┴──────────────┘
```

| Project | Role |
|---|---|
| `SwarmRoute.Map.Domain.Shared` | Leaf: enums (`MapSiteType`, `MapLineType`, `MapResourceStatus`) and `MapErrorCodes` (`MAP-001..008`). No dependencies. |
| `SwarmRoute.Map.Domain` | The model: `Roadmap` aggregate, `MapSite`/`MapLine`/`MapBlock` entities, `MapPosition`/`RoadmapGraph`/`InterferenceSet` value objects, domain services (`IRoadmapGraphFactory`, `IInterferenceCalculator`), `IRoadmapRepository`, integration events. References `SwarmRoute.SpatioTemporal.Kernel`, `SwarmRoute.Domain.Abstractions`, `NetDevPack`, and the vendored `SwarmRoute.Algorithms` graph library. |
| `SwarmRoute.Map.Application.Contract` | The published surface: `IMapAppService`, **`IRoadmapQueryService`** (the cross-context read seam), and transport DTOs. References `Map.Domain` *on purpose* so the returned `RoadmapGraph` VO is visible to consumers (see the comment at `Map/SwarmRoute.Map.Application.Contract/SwarmRoute.Map.Application.Contract.csproj`). |
| `SwarmRoute.Map.Application` | `MapAppService` (import/read/publish/delete), `RoadmapGraphProvider` (cached read seam impl), `RoadmapFactory` (DTO→domain via validating ctors), `MapMappingProfile` (domain→DTO via AutoMapper), and the `RoadmapPublishedCacheInvalidator` event handler. |
| `SwarmRoute.Map.Infra.Data` | `MapDbContext` (EF Core + Npgsql), `RoadmapRepository`, the `InitialCreate` migration, and the design-time `MapDbContextFactory`. |
| `SwarmRoute.Map.Infra.CrossCutting.IoC` | `MapNativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)` — the composition root. |
| `SwarmRoute.Map.Api` | `MapsController` + a thin standalone `Program.cs`. In production the controller is hosted by `SwarmRoute.Host` as an MVC application part. |
| `SwarmRoute.Map.Tests` | xUnit; references only `Map.Domain` (pure unit tests, no DB). |

关于分层完整性的说明:这里**没有 `Application.Authorization`、没有 `HttpApi.Client`、也没有独立的 `EntityFrameworkCore` 配置程序集**——EF 映射直接写在 `MapDbContext.OnModelCreating` 中。对于一个写侧仅为低频导入/发布流程的上下文而言,这样做是合适的。

---

## 3. Domain model

### Aggregate: `Roadmap`

`Roadmap`(`Map/SwarmRoute.Map.Domain/Aggregates/Roadmap.cs:23`)是唯一的聚合根。站点、线段与块都是**边界内部的实体**——只能通过聚合根来修改。聚合 id 是 EF 代理键 `Entity.Id`(Guid);拓扑 id(`SiteId`、`LineId`、`BlockId`)则是*独立*的稳定字符串键,供图以及跨上下文的 `ResourceRef` 使用。

**不变式**(在构造函数和 `ReplaceTopology` 中强制执行,两者都经由 `:153` 处的 `Validate`;违反时抛出携带 `MAP-xxx` 码的 `ArgumentException`):

1. 至少一个站点(`MAP-001`)。
2. 站点 id 唯一(`MAP-002`);线段 id 唯一(`MAP-003`)。
3. 每条线段的 `StartStationId`/`EndStationId` 都能解析到某个站点——不存在悬空端点(`MAP-004`)。
4. 每个块所含的站点/线段 id 都能解析到成员——不存在悬空成员(`MAP-005`)。

`StateVersion` 是一个乐观并发计数器(EF 并发令牌),每次编辑时递增;`ReplaceTopology` 在清除旧集合*之前*先校验*新*集合,因此被拒绝的替换会让聚合保持原样(已由测试证明)。`MarkImported()`/`MarkPublished()` 将集成事件入队(§4)。`Rename`、`CheckVersion`、`FindSite`、`FindLine` 补全了 API。

### Value object: `RoadmapGraph`

`RoadmapGraph`(`Map/SwarmRoute.Map.Domain/ValueObjects/RoadmapGraph.cs:23`)封装了来自内置 `SwarmRoute.Algorithms` 库的 `DirectedWeightedSparseGraph<string>`。它是一个不可变、按结构判等的值对象,并且是进程内**唯一的**读模型。构建规则(`Build`,位于 `:37`,与 `GraphMap.Init` 保持一致):

- **顶点** = **已启用**站点(`s.Enable`)的 id。禁用的站点被排除。
- **边** = 已启用的线段,方向为 `StartStationId → EndStationId`。若某条线段的端点不是顶点,则出于防御性考虑被跳过(例如它指向了一个被禁用的站点)。
- **权重** = `round(Distance * 1000)`(`WeightScale = 1000`,`MidpointRounding.AwayFromZero`)。Distance 以米为单位;×1000 使内置的整数权重 Dijkstra 在毫米分辨率下保持精确。
- **零长度钳制**:内置图将权重 `0` 视为"无边",因此退化的零距离线段被钳制为权重 `1`,以保留该边。

查询接口(全部按拓扑 `SiteId` 进行):

| Member | Meaning |
|---|---|
| `VertexCount` / `EdgeCount` / `Vertices` | Graph size & vertex set |
| `HasSite(id)` / `HasLine(from,to)` | Membership |
| `Neighbours(siteId)` | Out-successors (one directed hop); empty if unknown |
| `EdgeWeight(from,to)` | Scaled weight, or `null` |
| `DistanceTo(start,end)` | Dijkstra shortest-path **cost**, or `null` if absent/unreachable (mirrors `GraphMap.DistanceTo`) |
| `ShortestPath(start,end)` | Ordered inclusive site-id list, or `null`; trivial `[start]` when `start == end` |

内核互操作辅助方法将图元素映射到**冻结的** `ResourceRef` 契约(`Shared/SwarmRoute.SpatioTemporal.Kernel/ResourceRef.cs:30`):

- `SiteRef(siteId)` → `ResourceRef(ResourceKind.CP, siteId)`
- `LaneId(from,to)` → the `"{from}-{to}"` convention
- `LaneRef(from,to)` → `ResourceRef(ResourceKind.Lane, "{from}-{to}")`

TrafficControl / Deadlock 正是通过这些方法来命名 Map 所拥有的同一批物理资源。原始的 `Graph` 也向高级消费者暴露(供运行自有 Dijkstra/SIPP 的规划器使用)。

### Entities & supporting VOs

- **`MapSite`**(`…/Entities/MapSite.cs:17`):`SiteId`、`SiteType`、`Pos`(`MapPosition`)、`Enable` 以及干涉 id 列表。`SetEnabled`/`SetInterference*` 为 `internal`(仅能通过聚合修改)。`Angle` 是 `Pos.Angle` 的便捷快捷方式。
- **`MapLine`**(`…/Entities/MapLine.cs:16`):有向 `StartStationId → EndStationId`、`Distance`(≥ 0,否则 `MAP-008`)、`LineType`、可选的贝塞尔 `ControlPos1/2`、干涉列表。移植修复了原始的 `Distince` 拼写错误,并用稳定字符串 id 替换了对象导航。
- **`MapBlock`**(`…/Entities/MapBlock.cs:12`):一个互斥组——所含的站点/线段 id 加上一个 AABB(`MinPos`/`MaxPos`)。
- **`MapPosition`**(`…/ValueObjects/MapPosition.cs:10`):一个二维位姿 `(X, Y, Angle°)`;值相等;平面欧氏 `DistanceTo`(忽略朝向);`Empty` 占位符。将原始的 `MapPos`(X/Y)与单独存储的每站点角度整合到一起。
- **`InterferenceSet`**(`…/ValueObjects/InterferenceSet.cs:11`):一个不可变的**对称** id→ids 邻接结构。`FromPairs` 忽略自配对,并双向建立链接。由 `InterferenceCalculator.ComputeSiteInterference` 通过两两轮廓重叠计算得出。

### Enums (`Domain.Shared`)

- **`MapSiteType`**(`…/Enums/MapSiteType.cs:14`):`CPSite=1, WorkSite=2, RelaySite=3, AvoidSite=4, DockSite=5`。**缺陷修复:**上游枚举中 `RelaySite=3` *和* `AvoidSite=3` 发生冲突,且 `DockSite=4` 占用了 `AvoidSite` 本应使用的槽位。现已重新编号为连续且互不重复;有一个测试守护它。
- **`MapLineType`**:`Straight=0`、`Bezier=1`。
- **`MapResourceStatus`**:`Locked/Belong/Unlocked/Unable`——为保持导入保真度而保留;对 Map 而言只有 `Unlocked`/`Unable` 有意义(其余属于 TrafficControl)。

### Domain services

- **`IRoadmapGraphFactory`** / `RoadmapGraphFactory`(`…/Services/RoadmapGraphFactory.cs:8`):对 `RoadmapGraph.Build` 的轻量抽象(从集合构建,或从一个 `Roadmap` 构建),使调用方依赖于接口而非静态工厂。注册为单例(无状态)。
- **`IInterferenceCalculator`** / `InterferenceCalculator`(`…/Services/InterferenceCalculator.cs:16`):圆形重叠判定 `AreInterfering`。**矫正**了上游被反转的谓词:当且仅当轮廓*部分*重叠时返回 `true`——`|rA − rB| < d < rA + rB`。相切、包含与相离都**不**算干涉(每种情况都由测试固定)。

---

## 4. Read seam — how other contexts consume the graph

```
                     (frozen Kernel contract: ResourceRef)
   PathPlanning ──┐
                  ├─▶ IRoadmapQueryService ──▶ RoadmapGraph (VO)   [synchronous, per planning tick]
   Coordination ──┘            ▲
                               │  Map.Roadmap.Published  ──▶  Invalidate(roadmapId)   [out-of-band]
```

**`IRoadmapQueryService`**(`Map/SwarmRoute.Map.Application.Contract/Services/IRoadmapQueryService.cs:14`)是**冻结的跨上下文契约**,也是其他上下文触达 Map 读模型的唯一途径。它暴露 `GetGraphAsync` / `TryGetGraphAsync` / `Invalidate(roadmapId)`——消费者接收一个构建好的 `RoadmapGraph`,永远看不到 EF 实体、`DbContext` 或聚合本身。

该契约是经使用验证的,而非纸上谈兵:`PathPlanning.Application` 的 `PathPlanningAppService` 正是通过**这个确切的接口**(`using SwarmRoute.Map.Application.Contract.Services;`)解析构建好的图,Coordination 的应用层也出于同样目的引用 Map。契约还规定了预期的节奏:规划器**每个 tick(节拍)同步**读取图(热路径),而拓扑*变更*则通过 `Map.Roadmap.Published` 集成事件**带外**推送,该事件使缓存失效,从而让下一次读取重新构建。

**生产实现——`RoadmapGraphProvider`**(`Map/SwarmRoute.Map.Application/Services/RoadmapGraphProvider.cs:19`):一个持有 `ConcurrentDictionary<Guid, RoadmapGraph>` 的**单例**。无锁读取;未命中时,它会通过一个*全新的 DI 作用域*进行构建(单例无法持有作用域内的 `IRoadmapRepository`),调用 `GetWithTopologyAsync` 加载,再用 `GetOrAdd`,使并发的首次访问产出同一个实例。`Invalidate` 只是移除对应的键。该缓存在发布时由 **`RoadmapPublishedCacheInvalidator`**(`…/Application/Events/RoadmapPublishedCacheInvalidator.cs:11`,一个 `IDomainEventHandler<MapRoadmapPublishedEvent>`)丢弃。

这个读缝是可插拔的。**Simulation** 上下文提供了一个 `InMemoryRoadmapQueryService`,它基于预先构建好的 `RoadmapGraph` 实现同一接口,且仿真 Host 在 Map *之后*注册它,从而在无数据库运行时覆盖 `RoadmapGraphProvider`。PathPlanning 的测试以同样方式使用 `FakeRoadmapQueryService`。

集成事件(`…/Domain/Events/`),均为 `DomainEvent, IIntegrationEvent`:

| Event | `EventName` / `Version` | Carries | Consumed by |
|---|---|---|---|
| `MapRoadmapImportedEvent` | `Map.Roadmap.Imported` / `v1` | id, name, version, site/line/block counts | observability / bookkeeping |
| `MapRoadmapPublishedEvent` | `Map.Roadmap.Published` / `v1` | id, name, version | cache invalidators (Map's provider; Host's topology adapter) |

> **关于运行时闭包读缝(Host)的说明:**在装配后的系统中,Host 将 TrafficControl 的 `IResourceTopology` 绑定到一个 `MapResourceTopologyAdapter`,后者**直接读取** `Roadmap` 聚合(通过 `IRoadmapRepository`),并使用 `RoadmapGraph.SiteRef`/`LaneRef` 推导每个资源的锁闭包(干涉集 + 父级块)。同样地,`MapAvoidancePointSelector` 按 `MapSiteType` 读取站点以挑选 `AvoidSite`。因此 Map 的*领域*(聚合 + `RoadmapGraph` 辅助方法)被消费的范围超出了 `IRoadmapQueryService` 这个图读缝——但始终是只读的,且由它派生出的动态状态存放在 TrafficControl,而非 Map。

---

## 5. Persistence

`MapDbContext`(`Map/SwarmRoute.Map.Infra.Data/Context/MapDbContext.cs:24`)是一个基于 `BaseDbContext` 的 EF Core 10 / Npgsql 工作单元,会在提交时派发领域/集成事件。它恰好持久化一个聚合 `Roadmap`,连同其三个被拥有的子集合:

| Table | Maps | Key / index |
|---|---|---|
| `Roadmaps` | aggregate | PK `Id` (never generated); unique `IX_Roadmaps_Name`; `StateVersion` is the concurrency token |
| `RoadmapSites` | `OwnsMany(Sites)` | shadow `Id` PK + FK `RoadmapId`; unique `(RoadmapId, SiteId)` |
| `RoadmapLines` | `OwnsMany(Lines)` | unique `(RoadmapId, LineId)` |
| `RoadmapBlocks` | `OwnsMany(Blocks)` | unique `(RoadmapId, BlockId)` |

值转换的取舍:枚举 → `string`(varchar 32);每个 `MapPosition` 和每个字符串 id 列表(`Interference*`、`Contained*`)→ 一个 **`jsonb`** 列,通过自定义的 `ValueConverter` + `ValueComparer` 实现。`MapPosition` 的私有构造函数使其不适合直接用于 `System.Text.Json`,因此用一个 `PositionJson` 代理记录来回转换它(`…/Context/Persistence/PositionJson.cs:9`)。导航使用 `PropertyAccessMode.Field`,以便通过封装的实体写入底层的 `List<>`。该模式被捕获在 `InitialCreate` 迁移中;`MapDbContextFactory`(一个设计期 `IDesignTimeDbContextFactory`)让 `dotnet ef` 无需宿主即可构建上下文(占位连接串、空操作事件派发器)。

`RoadmapRepository`(`…/Repositories/RoadmapRepository.cs:10`)继承自 `BaseRepository<MapDbContext, Roadmap>`。`GetWithTopologyAsync` 返回一个**被跟踪的**聚合(被拥有的子项随属主自动加载)以供编辑;`GetByNameAsync` 为 `AsNoTracking`。

**设计意图:**持久化是*快照 / 导入存储*,而非热路径。写侧是低频的导入/发布;规划期使用的图由 `RoadmapGraph.Build` **在内存中**构建并由 provider 缓存。将小型、以读为主的 id 列表与位姿集合存为 `jsonb` 是一种刻意的简化(对实质上的内嵌数据不使用连接表)。

---

## 6. Composition / wiring

`MapNativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)`(`Map/SwarmRoute.Map.Infra.CrossCutting.IoC/MapNativeInjectorBootStrapper.cs:23`)是组合根:

| Registration | Lifetime | Notes |
|---|---|---|
| `MapDbContext` (Npgsql) | scoped | connection string key `MapDatabase`; **guarded** — if absent, the context is registered without a provider so the Host boots DB-less |
| `IRoadmapRepository` → `RoadmapRepository` | scoped | |
| `IRoadmapGraphFactory` → `RoadmapGraphFactory` | singleton | stateless |
| `IInterferenceCalculator` → `InterferenceCalculator` | singleton | stateless |
| `IMapAppService` → `MapAppService` | scoped | |
| `IRoadmapQueryService` → `RoadmapGraphProvider` | **singleton** | cache must survive across requests |
| `IDomainEventHandler<MapRoadmapPublishedEvent>` → `RoadmapPublishedCacheInvalidator` | scoped | |
| AutoMapper (`MapMappingProfile`) | — | scanned from the Application assembly |

该 bootstrapper 遵循仓库通用的 `*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)` 约定。`SwarmRoute.Host` 的 `Program.cs` 调用它(`MapNativeInjectorBootStrapper.RegisterServices(builder)`),将 `MapsController` 作为 MVC application part 添加进来,然后在其上叠加 Host 适配器(由 Map 支撑的 `IResourceTopology` 与避让选择器)。Map 自己的 `Program.cs` 也是同一个调用,外加控制器/OpenAPI,以便独立运行该 Api。这个连接串守卫意味着*只有* Map 和 TrafficControl 注册 `DbContext`,且二者都容忍其缺失,因此无数据库的冒烟测试永远不会触及持久化。

---

## 7. Tests

`SwarmRoute.Map.Tests`(xUnit,仅引用 `Map.Domain`——快速、无数据库):

- **`RoadmapGraphTests`** —— 图构建(顶点/边数、有向性)、`EdgeWeight` = `Distance×1000`、禁用站点的排除、`DistanceTo` 与手工计算的最短路径对照**并**与同一张图上独立的 `DijkstraShortestPaths` 交叉校验、`ShortestPath` 排序、不可达 → `null`、`Neighbours` 出向后继、工厂 ≡ 静态构建(结构相等),以及零长度→权重 1 的钳制。夹具使用一个 4 节点的"菱形"路网(`Builders.DiamondRoadmap`)。
- **`RoadmapInvariantTests`** —— 每一条聚合不变式(悬空起/止端点、重复的站点/线段 id、空站点集、悬空块成员、空名称)、`Rename` 版本递增、`ReplaceTopology` 被拒时的回滚,以及 `MarkPublished` 触发 `Map.Roadmap.Published`。
- **`InterferenceTests`** —— `AreInterfering` 的重叠 / 相离 / 包含 / 相切各情形,以及对称集合的构建(含对自配对的拒绝)。
- **`MapPositionTests`** —— `Empty`、值相等、平面欧氏距离(忽略朝向)。
- **`MapSiteTypeTests`** —— 守护重复值缺陷修复(所有值与名称互不相同;Avoid/Relay/Dock 彼此分离)。

这里(刻意)未覆盖的部分——它们需要基础设施:EF 映射/往返、`RoadmapGraphProvider` 的缓存/失效、`MapAppService` 编排,以及控制器。PathPlanning 自己的测试套件通过其 fake 端到端地演练了 `IRoadmapQueryService` 读缝。

---

## 8. v0 status & notes

**已实现**

- 带完整不变式校验与乐观版本控制的 `Roadmap` 聚合。
- `RoadmapGraph` 构建 + Dijkstra 距离/路径/邻居、权重缩放、零长度钳制;`ResourceRef`(CP/Lane)互操作辅助方法。
- 静态干涉几何(`InterferenceCalculator`、`InterferenceSet`)。
- 应用服务:导入 / 获取 / 列表 / 发布 / 删除;面向 API 的图摘要投影。
- 带缓存的读缝(`RoadmapGraphProvider`)+ 由发布驱动的失效;被 PathPlanning、Coordination 与 Host 适配器消费。
- EF Core + Npgsql 持久化,带一个初始迁移;位姿与 id 列表使用 `jsonb`。
- IoC bootstrapper、REST 控制器(`api/maps`),以及 Host 集成。

**相对上游引擎的缺陷修复** —— `MapSiteType` 重复值冲突(现已互不相同)、`MapLine.Distince` → `Distance`,以及被反转的干涉谓词(现在 `true` ⇔ 部分重叠)。三份重复的 `GraphMap` 实现被统一进这一个聚合。

**延后 / 备注**

- **尚无拓扑*编辑* API。** `Roadmap.ReplaceTopology` 与各站点的修改器已存在于聚合上,但 `IMapAppService` 只暴露导入(创建)/ 发布 / 删除——没有原地更新的端点。重新导入是当前的编辑途径。
- **块 AABB 已存储但未被计算/校验** —— `MinPos`/`MaxPos` 在导入时按给定值接受;没有任何逻辑从所含成员推导它们。
- **干涉被计算但未作为派生集持久化** —— `InterferenceCalculator` 产出一个 `InterferenceSet`,但干涉只以导入时提供的原始每站点/线段 id 列表存储;运行时使用的闭包由 Host 的 `MapResourceTopologyAdapter` 重建。
- **集成事件传输是进程内的。** `Program.cs`/Host 接入 `AddEventBus()` 用于进程内派发;CAP/RabbitMQ 是一个已记录的 TODO,因此 `Map.Roadmap.Published` 目前在同一进程内使缓存失效。
- **`MapResourceStatus` 仅用于导入保真度** —— 动态状态不属于 Map;TrafficControl 才是权威。
- **Map 中没有每智能体黑名单** —— Host 的拓扑适配器将其留空(属于 v1 的运行时关注点)。
