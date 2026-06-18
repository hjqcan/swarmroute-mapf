# swarmroute-mapf — 多機路徑規劃系統 DDD 開發計劃

## Context（為什麼做這件事）

`swarmroute-mapf` 是一個**起步階段的全新專案**，目標是做一套**多機（AGV/AMR）路徑規劃與車隊協調系統**。repo 根目錄目前只有空的 `docs/ context/ host/` 與一個 `third-party/` 參考資料夾，尚無任何 commit。

要解決的問題：把 `third-party/AJR.MAPF`（非常初步、真正代碼散在 `AJR.Platform.*`）的 MAPF 邏輯，**重構成一套乾淨、可演進、業界水準的 DDD 架構**，比照 `third-party/grukirbs`（成熟的多限界上下文 Clean Architecture .NET 方案）的整套作法，並切成使用者指定的 **4 個領域（限界上下文）**：

| 領域（中文） | 限界上下文 | 職責 |
|---|---|---|
| 資源 / 地圖 | **Map** | 路網拓撲（站點/路段/區塊、干涉關係、圖） |
| 路徑規劃 | **PathPlanning** | 為單機/多機找出可行（時空）路線 |
| 交通管制 | **TrafficControl** | 時空預約 / 路權 / 防碰撞，**擁有即時佔用狀態** |
| 死鎖處理 | **Deadlock** | 偵測 / 避免 / 解除循環等待 |

**現況診斷（業界視角）**：現有引擎是「**整條路徑空間上鎖 + 剪枝 Dijkstra + 事後 RAG 死鎖偵測**」的第一代 AGV 交管——`GraphMap.GeneratePath()` 規劃完直接把整條路線的站點/路段/區塊鎖給一台車，沒有時間維度，因此兩台車不能錯時共用走廊、吞吐受限、且天生易死鎖（才需要事後偵測補救）。業界已演進到**時空預約表 / 安全區間規劃（SIPP）**、**滾動時窗的終身重規劃（RHCR）**、**優先級規劃 / PIBT / CBS**；參考車隊管理架構為 **OpenTCS**（Router＝路徑規劃、Scheduler＝資源配置/交管、Dispatcher＝派工）。本計劃把現有引擎當 v0 基線移植進乾淨架構，再沿 v1→v3 演進。

**預期成果（本計劃涵蓋範圍）**：先交付「**可編譯的 4 上下文 DDD 方案 + v0 可跑多機基線**」，並把 v1–v3 演進寫成路線圖。

### 已鎖定的關鍵決策（來自確認）

1. **建置位置**：在 **repo 根目錄建全新方案 `SwarmRoute.Mapf.sln`**，從 `third-party/AJR.*` 移植邏輯、把圖演算法庫當 project reference 重用；`third-party/` 維持唯讀參考。
2. **技術堆疊**：**比照 grukirbs** — .NET 8、EF Core 8 + PostgreSQL(Npgsql)、CAP（跨上下文整合事件 / Outbox + RabbitMQ）、Hangfire（背景任務）、內建 DI、AutoMapper、NetDevPack。**不**沿用 AJR.Platform.Minimal 的 SqlSugar/Autofac/Quartz/SignalR/IdentityServer4（該平台僅作參考）。
3. **DDD 基底**：**內嵌 grukirbs 的 NetDevPack fork**（見下方「NetDevPack 內嵌」一節 — 須取得來源）。
4. **首個里程碑**：**腳手架 + v0 基線**（移植現有引擎成可跑多機基線），v1–v3 作為路線圖。

---

## 目標架構

### 根命名空間與方案佈局

根命名空間 **`SwarmRoute`**；方案檔 `SwarmRoute.Mapf.sln`。每個限界上下文 `{Ctx}` 比照 grukirbs 採 `{Root}.{Ctx}.{Layer}` 分層命名。

```
/Users/hjqcan/workspace/swarmroute-mapf/
├─ SwarmRoute.Mapf.sln
├─ Directory.Build.props            # net8.0, ImplicitUsings, Nullable, analyzers
├─ Directory.Packages.props         # 集中套件版本 (EF 8, CAP, Hangfire, AutoMapper)
│
├─ Shared/                          # 共享核心（對應 grukirbs/Shared）
│  ├─ SwarmRoute.Domain.Abstractions/      # IBaseRepository<T>；EventBus 抽象 IIntegrationEvent/IIntegrationEventPublisher
│  ├─ SwarmRoute.EventBus/                 # CAP 整合：CapIntegrationEventPublisher、DomainEventToIntegrationEventConverter、AddEventBus()
│  ├─ SwarmRoute.Infra.Data.Core/          # BaseDbContext : IUnitOfWork（收集領域事件→SaveChanges→分發本地+發佈整合事件）；BaseRepository<T>
│  ├─ SwarmRoute.Infra.BackgroundJobs.Core/# Hangfire JobBase、IRecurringJobConfigurator
│  ├─ SwarmRoute.StateMachine.Core/        # IStateMachine/IStateGuard（Stateless 封裝；對應 grukirbs StateMachine+Guards）
│  ├─ SwarmRoute.SpatioTemporal.Kernel/  ★ 跨上下文「時空預約」共享語彙（純型別，無行為，見下）
│  └─ vendor/NetDevPack/                   # 內嵌的 NetDevPack fork（project，見「NetDevPack 內嵌」）
│
├─ Map/                             # 上下文1 資源/地圖 — 完整持久化上下文
│  ├─ SwarmRoute.Map.Domain.Shared/        # MapResourceStatus、MapSiteType、MapLineType、錯誤碼
│  ├─ SwarmRoute.Map.Domain/               # Roadmap 聚合根；MapSite/MapLine/MapBlock；RoadmapGraph VO；IRoadmapRepository；事件
│  ├─ SwarmRoute.Map.Application.Contract/ # DTO + IMapAppService、IRoadmapQueryService
│  ├─ SwarmRoute.Map.Application/          # MapAppService、RoadmapGraphProvider、Mapping、Subscribers
│  ├─ SwarmRoute.Map.Infra.Data/          # MapDbContext : BaseDbContext、repo、Migrations
│  ├─ SwarmRoute.Map.Infra.CrossCutting.IoC/  # MapNativeInjectorBootStrapper
│  ├─ SwarmRoute.Map.Api/                  # MapsController（拓撲匯入/CRUD）
│  └─ SwarmRoute.Map.Tests/
│
├─ PathPlanning/                    # 上下文2 路徑規劃 — 精簡純計算（無 EF、初期無 Api）
│  ├─ SwarmRoute.PathPlanning.Domain.Shared/
│  ├─ SwarmRoute.PathPlanning.Domain/      # AgentPlan 聚合；SpaceTimePath VO；IPathPlanner（Dijkstra→SIPP）；IReservationQuery（消費端）
│  ├─ SwarmRoute.PathPlanning.Application.Contract/
│  ├─ SwarmRoute.PathPlanning.Application/
│  ├─ SwarmRoute.PathPlanning.Infra.CrossCutting.IoC/
│  └─ SwarmRoute.PathPlanning.Tests/       # ← 演算法正確性測試最重
│
├─ TrafficControl/                  # 上下文3 交通管制 — 完整；擁有即時預約/配置狀態
│  ├─ SwarmRoute.TrafficControl.Domain.Shared/
│  ├─ SwarmRoute.TrafficControl.Domain/    # ReservationTable 聚合根；ResourceLease/ReservationRequest；IResourceAllocator/IReservationCalendar/IConflictDetector；StateMachine+Guards；事件
│  ├─ SwarmRoute.TrafficControl.Application.Contract/  # IReservationService(實作 PathPlanning.IReservationQuery)、ITrafficCoordinatorAppService
│  ├─ SwarmRoute.TrafficControl.Application/
│  ├─ SwarmRoute.TrafficControl.Infra.Data/           # 快照/稽核用 DbContext、repo、Migrations
│  ├─ SwarmRoute.TrafficControl.Infra.BackgroundJobs/ # 租約過期清掃、等待升級（Hangfire）
│  ├─ SwarmRoute.TrafficControl.Infra.CrossCutting.IoC/
│  ├─ SwarmRoute.TrafficControl.Api/
│  └─ SwarmRoute.TrafficControl.Tests/
│
├─ Deadlock/                        # 上下文4 死鎖處理 — 精簡計算+反應式（無 EF、初期無 Api）
│  ├─ SwarmRoute.Deadlock.Domain.Shared/
│  ├─ SwarmRoute.Deadlock.Domain/          # ResourceAllocationGraph VO；DeadlockCycle；IDeadlockDetector/IDeadlockResolver；AvoidancePlan；事件
│  ├─ SwarmRoute.Deadlock.Application.Contract/
│  ├─ SwarmRoute.Deadlock.Application/     # 訂閱 TrafficControl 事件、反應式解除
│  ├─ SwarmRoute.Deadlock.Infra.CrossCutting.IoC/
│  └─ SwarmRoute.Deadlock.Tests/
│
├─ Coordination/                    # ★ 終身控制迴圈（車隊協調 = OpenTCS 的 Dispatcher）
│  └─ SwarmRoute.Coordination.Application/ # FleetCoordinationLoop : IHostedService；plan↔reserve↔deadlock 編排
│
├─ Host/                            # 組合根（單一可部署）
│  └─ SwarmRoute.Host/                     # Program.cs：AddEventBus + 各上下文 IoC + Hangfire + Coordination
│
└─ third-party/                     # （既有）唯讀參考 + 可重用演算法庫
   └─ algorithms/                         # 由 AJR.Platform.Algorithms(.DataStructures) retarget net8.0 後 vendoring 進來
```

### 各上下文分層矩陣（哪些用完整 grukirbs 分層、哪些精簡）

| 上下文 | Domain.Shared | Domain | App.Contract | App | Infra.Data | BackgroundJobs | IoC | Api | Tests | 理由 |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|---|
| **Map** | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | 拓撲是耐久主檔，需 CRUD + EF + migration（如 grukirbs `Order`） |
| **PathPlanning** | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌→後期 | ✅✅ | **純計算**：plan 是（拓撲+預約）的瞬時函數，重算便宜、不該持久化；無 DbContext/UoW |
| **TrafficControl** | ✅ | ✅ | ✅ | ✅ | ✅(快照/稽核) | ✅ | ✅ | ✅ | ✅ | **擁有即時預約狀態**——系統最熱的可變聚合；需 job、Api（人工解鎖）、EF（崩潰復原快照） |
| **Deadlock** | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌→後期 | ✅ | **計算+反應式**：由 TrafficControl 快照建 RAG 跑環偵測；死鎖是瞬時的、無自有耐久狀態 |

> grukirbs 本身就有「精簡上下文」先例：`Alarm` 無 Api/BackgroundJobs/Tests、`Order` 無 BackgroundJobs。故按上下文裁減分層是慣例、非偏離。**只有 Map 與 TrafficControl 是全分層**；PathPlanning 與 Deadlock 刻意做成無 EF/無 Api 的純領域計算庫（直接對應 OpenTCS 把 Router/Scheduler/Dispatcher 做成服務而非資料庫）。

### 沿用 grukirbs 的戰術慣例（已驗證）

- **聚合根**：`public class Roadmap : Entity, IAggregateRoot`（NetDevPack）。私有 EF 無參建構子；公開建構子驗不變條件並 `throw ArgumentException`；屬性 `private set`；行為方法改狀態並維持不變條件 + `IncrementStateVersion()`（樂觀並行）。`Entity` 提供 `Id/DomainEvents/AddDomainEvent()/ClearDomainEvents()`。範本：`third-party/grukirbs/Robot/Gurki.RBS.Robot.Domain/Entities/Robot.cs`。
- **值物件**：`public sealed class MapPosition : ValueObject` 覆寫 `GetEqualityComponents()`、唯讀屬性、`static Empty`。範本：`.../Robot.Domain/ValueObjects/RobotDimensions.cs`。
- **領域事件**：`class MapRoadmapPublishedEvent : DomainEvent, IIntegrationEvent`，`EventName => "Map.Roadmap.Published"`、`Version => "v1"`，命名 `Ctx.Aggregate.Action`。本地處理器放 `Application/Handlers/`，跨上下文訂閱者放 `Application/Subscribers/`（`[CapSubscribe("...")]`）。範本：`third-party/grukirbs/Alarm/.../Events/AlarmRaisedEvent.cs` + `Robot.Application/.../RobotAlarmSubscriber.cs`。
- **UoW + 事件分發**：每個 `*.Infra.Data` 的 DbContext 繼承 `BaseDbContext`，`Commit()` 從 EF ChangeTracker 收集聚合 `DomainEvents`→`SaveChangesAsync()`→經 NetDevPack `IDomainEventDispatcher` 分發本地事件 + 經 `IIntegrationEventPublisher` 發佈整合事件到 CAP。範本：`third-party/grukirbs/Shared/Gurki.RBS.Infra.Data.Core/Context/BaseDbContext.cs`（**直接照搬**）。
- **IoC 啟動器**：每個上下文一個 `*.Infra.CrossCutting.IoC/*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)`，Host 的 `Program.cs` 依序呼叫（對應 grukirbs `RobotNativeInjectorBootStrapper`）。

---

## 共享核心：`SwarmRoute.SpatioTemporal.Kernel`（最關鍵的設計決定）

MAPF 天生把「規劃 + 預約 + 死鎖」耦在一起；切錯會得到貧血/碎嘴的設計。對策：**建一個只定義跨上下文語彙、不含行為與狀態的共享核心**（類比 grukirbs `Domain.Abstractions`），被 Map/PathPlanning/TrafficControl/Deadlock 的 Domain 引用：

- `ResourceRef`（VO）：`(kind ∈ {CP, Lane, Block, Zone}, id)`——資源的型別化身分（沿用現有字串 id，Lane 用 `"start-end"`）。
- `TimeInterval`（VO）：`[t_enter, t_exit)` 半開區間，單一車隊單調時鐘（ms）。
- `SpaceTimeCell` = `(ResourceRef, TimeInterval)`；`SpaceTimePath` = 有序 `SpaceTimeCell`。**這是規劃與預約之間流動的核心物件**（現有系統完全缺這層，直接從 `List<string>` 跳到整路上鎖）。
- `SafeInterval`（VO，衍生）：某資源對某車「無衝突」的最大連續區間——SIPP 的安全區間；**由 TrafficControl 從預約表算出、交給 PathPlanning 讀**，PathPlanning 永不寫。
- `IReadOnlyReservationView`（查詢介面）：`FreeIntervals(ResourceRef)`、`IsFree(ResourceRef, TimeInterval)`——SIPP 規劃迴圈內讀的東西。
- `ResourceAllocationGraphSnapshot`（不可變 DTO）：佔用 + 等待邊，讓 Deadlock 不必伸手進 TrafficControl 內部。

---

## 各上下文戰術設計與來源對應

### 上下文1 — Map（資源/地圖）
**來源**：`third-party/AJR.MAPF/AJR.MAPF.Map/{MapSite,MapLine,MapBlock,MapPos,MapResource,MapResourceStaus,MapSiteType}.cs`；較完整的 `third-party/AJR.Infrastructure/AJR.Platform.GraphMapDP/GraphMap.cs`（取 `Init()` 建圖）、`InterferenceResourceDetection.cs`。

- **聚合根 `Roadmap`**：整個拓撲為一個一致性邊界（sites+lines+blocks 一起載入/驗證，對應 `GraphMap.Init(sites,lines,blocks)`）。`StateVersion` 隨拓撲編輯遞增（供規劃端快取失效）。
- **實體（Roadmap 子實體，非聚合根）**：`MapSite`（`SiteType/Pos/Angle/Enable/干涉清單`）、`MapLine`（`Start/EndStation/Distance/LineType/控制點`，有向邊、權重=距離×1000）、`MapBlock`（互斥區，原子上鎖）。
- **值物件**：`MapPosition`(X,Y,θ)、`RoadmapGraph`（不可變包住 `DirectedWeightedSparseGraph<string>`，移植 `GraphMap.Init` 建圖；規劃端消費的成品）、`InterferenceSet`（移植半徑重疊運算）。
- **Domain.Shared 列舉**：`MapResourceStatus {Locked,Belong,Unlocked,Unable}`、`MapSiteType`、`MapLineType`。
- **倉儲**：`IRoadmapRepository : IBaseRepository<Roadmap>`。
- **領域服務**：`IRoadmapGraphFactory`（sites/lines→`RoadmapGraph`）、`IInterferenceCalculator`。
- **領域事件**：`Map.Roadmap.Published`（拓撲變更→消費者重載）、`Map.Roadmap.Imported`。
- **App 服務**：`IMapAppService`（匯入/CRUD）、`IRoadmapQueryService`（唯讀拓撲 + 快取 `RoadmapGraph`）。

### 上下文2 — PathPlanning（路徑規劃）
**來源**：`third-party/AJR.MAPF/AJR.MAPF.XCBS/CBS.cs`（單機 Dijkstra）、`MapResourceManager.GenerateDijkstraShortestPath/DistanceTo`、`third-party/algorithms`（DijkstraShortestPaths、DirectedWeightedSparseGraph）。

- **聚合根 `AgentPlan`**：一台車當前計劃（有序 `SpaceTimePath`、目標、產生時間、狀態）；瞬時（記憶體）。行為 `Replan()/Invalidate(reason)`。
- **值物件**：`SpaceTimePath`（來自 Kernel）、`PlanCost`、`PlanRequest`(agentId, from, to, releaseTime, agentClass{footprint,v_max,a_max}, blacklist)、`WaitAction`（在站點明確等待——SIPP 必需）。
- **領域服務（演算法縫）**：`IPathPlanner`，實作 `DijkstraPathPlanner`（移植 `CBS.SearchPath`，v0）→ `SippPathPlanner`（v1，讀安全區間）。簽章為關鍵縫：`PlanResult Plan(RoadmapGraph graph, PlanRequest req, IReadOnlyReservationView reservations)`。
- **消費端介面（此處宣告，TrafficControl 實作）**：`IReservationQuery`/`IReadOnlyReservationView`。
- **領域事件**：`PathPlanning.AgentPlan.Computed`、`PathPlanning.AgentPlan.Failed`（無路徑/終點被佔，移植 `GraphMap` 的 `endSite.Status==Locked→null` 分支）。
- **無倉儲、無 DbContext。**

### 上下文3 — TrafficControl（交通管制）— 擁有即時預約/配置狀態
**來源**：`GraphMap.cs`（整路上鎖迴圈、`Lock/Unlock{Site,Line,Block}`、`UnlockPath`、剪枝-Dijkstra 資源過濾）、`AJR.MAPF.Map/{ResourceRequest,MapResourceUsage}.cs`、`AJR.MAPF.ConflictResolve/{ISolver,ConflictSolveStateMachine}.cs`。

- **聚合根 `ReservationTable`**：車隊唯一即時配置狀態（GraphMap `_sites/_lines/_blocks` 狀態 + `_agvPathDic` 的 DDD 歸宿）。所有 grant/release 經此，集中維持不變條件（「同一資源同一區間至多一車持有」）。重度用 `IncrementStateVersion()`。**內部雙索引**：`ResourceRef→依區間排序的 Reservation 串列`（gap-walk 直接得 SafeInterval）、`AgentId→其持有的 Reservation`。
- **實體/VO**：`ResourceLease`（agent→resource over `[t0,t1)`；`MapResource.OccupiedBy/Status` 的時空化）、`ReservationRequest`（升級 `ResourceRequest.cs`：保留 `RequestTime/EstimateTime/HadWaitedTime` 作飢餓/優先信號，加 `TimeInterval/Priority/kind`）、`RightOfWay/PriorityRule`（確定性 tie-break：Priority→`HadWaitedTime`→agentId）、`Conflict {VertexSame,EdgeSwap,Following,Interference}`。
- **領域服務**：`IResourceAllocator`（移植 GraphMap 上鎖/剪枝過濾）、`IReservationCalendar`（自由區間運算→SIPP 所需）、`IConflictDetector`（候選 `SpaceTimePath` 的點/邊/swap 衝突）。
- **狀態機 + Guards**（移植 `ConflictSolveStateMachine`）：`{Free→Requested→Reserved→InTransit→Releasing}`，guards `ResourceAvailableGuard/NoConflictGuard/NotBlacklistedGuard`（移植 `AGVBlackList.Contains(agvId)` 過濾）。
- **提供端**：`ReservationService : IReservationQuery`、`ITrafficCoordinatorAppService.TryReserve(SpaceTimePath)→AllocationOutcome`、`Release(agentId, passedCells)`（移植 `UnlockPath`）。
- **領域事件**：`TrafficControl.Reservation.Granted/Denied/Released`、`TrafficControl.Allocation.Contended`（等待超閾值/形成等待邊——Deadlock 訂閱的觸發）。
- **BackgroundJobs**：`LeaseExpirySweepJob`、`StaleRequestEscalationJob`（遞增 `HadWaitedTime`、發 `Allocation.Contended`）。

### 上下文4 — Deadlock（死鎖處理）
**來源**：`third-party/AJR.MAPF/AJR.MAPF.ConflictDetect/{IndependenceDetection,MapResourceAllocationGraph,DeadLockQueue}.cs`、`algorithms/CyclesDetector.cs`、`AJR.MAPF.ConflictResolve/ISolver.cs`。

- **聚合根 `DeadlockCase`**：一個偵測到的環與其解除生命週期 `Detected→Resolving→Resolved/Escalated`。
- **值物件**：`ResourceAllocationGraph`（移植 `MapResourceAllocationGraph.GenerateGraph`——頂點 `agent_/occupySite_/applySite_`，佔用+等待邊；由 TrafficControl 快照按需建）、`DeadlockCycle`（`CyclesDetector.CyclicVertices(graph,"agent_")` 的環）。
- **列舉**：`DeadlockKind {Cyclic,Livelock}`、`ResolutionStrategy {SendToAvoidSite,Preempt,Requeue}`。
- **領域服務**：`IDeadlockDetector`（移植 `IndependenceDetection.DeadlockDetect()`）、`IDeadlockResolver`（移植 `ISolver` — 選犧牲車、選避讓點、發重規劃意圖）。
- **聚合 `AvoidancePlan`**（實作目前只有註解的 `ConflictSolveStateMachine`）：`SelectVictim→SelectAvoidancePoint→ReserveDetour→DispatchToAvoid→ConfirmCleared→Recover`。
- **領域事件**：`Deadlock.Case.Detected`、`Deadlock.Case.ResolutionRequested`（犧牲車 + 避讓目標→Coordination 消費去重規劃）、`Deadlock.Case.Resolved`。
- **無 EF。**

---

## 上下文間契約（最難的部分）

**規則：一個規劃週期內 = 行程內介面；跨週期/觸發他子系統節奏 = CAP 整合事件。** 這讓內迴圈（plan↔reserve）零訊息匯流排延遲，又保留 grukirbs 的微服務化事件縫。

| 縫 | 機制 | 方向 | 為何 |
|---|---|---|---|
| **Map → 全體（讀拓撲）** | 行程內介面 `IRoadmapQueryService`（回快取 `RoadmapGraph`）；快取由 `Map.Roadmap.Published` **CAP 事件**失效 | 同步讀 + 非同步失效 | 規劃每週期同步要圖；拓撲少變→推播失效用 CAP 恰當 |
| **PathPlanning ⇄ TrafficControl（預約）** | **行程內介面**：PathPlanning 宣告 `IReservationQuery`/`IReadOnlyReservationView`（在 Kernel），TrafficControl 實作 `ReservationService`；提交為 `TryReserve(SpaceTimePath)→AllocationOutcome` | 同步、雙向、同行程 | MAPF 最緊耦合：SIPP 在搜尋迴圈內每次讀安全區間（每次規劃數千讀）；用 CAP 會荒謬。**必須行程內** |
| **TrafficControl → Deadlock（避免+偵測觸發）** | `TrafficControl.Allocation.Contended` **CAP 事件**觸發；Deadlock 經行程內 `ITrafficControlSnapshotProvider` 讀一致快照 | 非同步觸發 + 同步快照讀 | 偵測是週期/觸發式、非每週期；非同步解耦保持預約熱路徑快 |
| **Deadlock → PathPlanning/Coordination（解除→重規劃）** | `Deadlock.Case.ResolutionRequested` **CAP 事件**（犧牲車 + 避讓目標）；Coordination 訂閱後發重規劃 | 非同步 | 解除是車隊級反應，解耦讓 Deadlock 維持純分析器 |

> 跨上下文事件 payload 用顯式 `IIntegrationDtoConverter`（範本 grukirbs `Order.Domain/Events/Converters/`），做成版本化契約而非裸領域事件。

---

## 終身控制迴圈（Coordination）

**控制迴圈是獨立的 application 服務，不放 Host**（要能脫離 ASP.NET 單元測試；未來微服務化它就是 OpenTCS 的 Dispatcher）。`SwarmRoute.Coordination.Application.FleetCoordinationLoop : IHostedService`——終身線上 MAPF 驅動器，採滾動時窗（RHCR 風格）。每 tick / 新目標 / `Allocation.Contended` / `Deadlock.ResolutionRequested` 時：

1. `IRoadmapQueryService.GetGraph()`（同步、快取）
2. 對每台需規劃的車：`IPathPlanner.Plan(graph, req, reservationView)`（同步）
3. `ITrafficCoordinatorAppService.TryReserve(path)` → 若 `Denied/Queued`，把爭用資源剪掉重規劃（移植 `CBS.cs` 的「拿不到控制權→等待或重規劃」註解）
4. 車通過後 `Release(agentId, passedCells)`（增量釋放，**非任務結束才釋放**）
5. 死鎖由訂閱者反應式處理（job 觸發），不在內迴圈輪詢

**Host 組合根**（`SwarmRoute.Host/Program.cs`，照 grukirbs 順序 `AddEventBus → 各上下文 RegisterServices → Hangfire → AddHostedService<FleetCoordinationLoop>`）。只有 Map + TrafficControl 註冊 DbContext；CAP 用 PostgreSQL outbox + RabbitMQ，dev 可 `EventBus:UseInMemory=true`。

---

## 建置階段（首里程碑 = Phase 0–4：腳手架 + v0 基線）

- **Phase 0 — 骨架與核心（可編譯、空）**：建 `SwarmRoute.Mapf.sln`、`Directory.*.props`、`Shared/` 核心（`Domain.Abstractions/EventBus/Infra.Data.Core/StateMachine.Core/SpatioTemporal.Kernel`）、**內嵌 NetDevPack**（見下）、把 `third-party/algorithms` retarget net8.0 並 vendoring。產出：`dotnet build` 綠燈、無功能。
- **Phase 1 — Map 完整垂直切片**：移植 `MapSite/MapLine/MapBlock/MapPos/MapResourceStatus/MapSiteType` → `Map.Domain`；建 `Roadmap` 聚合 + `RoadmapGraph`（移植 `GraphMap.Init`）；`MapDbContext`、`IRoadmapRepository`、migration、`MapsController` 匯入 + `IRoadmapQueryService`。產出：HTTP 匯入拓撲、查詢圖。
- **Phase 2 — 單機端到端（demo 里程碑）**：PathPlanning 精簡專案 + `DijkstraPathPlanner`（移植 `CBS.SearchPath`）消費 `IRoadmapQueryService`；stub `IReservationQuery`（永遠空閒）；最小 `FleetCoordinationLoop` 規劃單車 A→B。產出：**可編譯垂直切片——拓撲進、路徑出**（證明架構）。
- **Phase 3 — 預約式 TrafficControl（v0 交管）**：建 `ReservationTable` 聚合 + `IResourceAllocator/IReservationCalendar/IConflictDetector`（移植 GraphMap 上鎖/剪枝/`UnlockPath`）；實作真 `ReservationService`；迴圈接 plan→`TryReserve`→拒則重規劃→通過則釋放；加快照 DbContext + `LeaseExpirySweepJob`；發 `Reservation.*` + `Allocation.Contended`。產出：多機不碰撞移動。
- **Phase 4 — 死鎖處理（v0）**：Deadlock 精簡專案：移植 `MapResourceAllocationGraph.GenerateGraph` + `IndependenceDetection`（`CyclesDetector`）；訂閱 `Allocation.Contended`、讀 RAG 快照、偵環、發 `Deadlock.Case.ResolutionRequested`；Coordination 把犧牲車重規劃到避讓點（移植 `ISolver.Solve/Recover`）。產出：偵測 + 解除死鎖的**完整 v0 基線**。

### v1–v3 演進路線圖（首里程碑後執行，本計劃僅記錄方向）

| 版本 | 變更（哪個上下文） | 買到什麼 |
|---|---|---|
| **v1** 時空預約 + SIPP | TrafficControl 導入 `TimeInterval/Headway` + 安全區間 `SafeIntervals()` + 連續釋放；PathPlanning 換 `SippPathPlanner`（(站點,安全區間) 空間 A*，插 `Wait`） | 車輛**錯時共用走廊**→首次真正吞吐躍升；含明確車距的不碰撞 |
| **v2** 優先級 + 滾動時窗 + 避免式預防 | PathPlanning 優先級 SIPP（AA-SIPP(m)）+ 密集處 PIBT；Coordination 滾動時窗（RHCR）；Deadlock 把 `WouldCloseCycle` 接進 `TryReserve`（grant 前拒環） | 中密度高吞吐、規劃延遲有界、**構造性 liveness**（環在 grant 時就被拒） |
| **v3** CBS/PIBT + 連續時間 SIPPwRT | PathPlanning 對壅塞 Zone 局部 CBS/CCBS 升級；SIPPwRT/TP-SIPPwRT 連續時間 + 運動學（Bezier 上的 v_max/a_max） | 最高密度吞吐、平滑且時間精確的運動 |

> 關鍵：每版獨立可出貨、嚴格優於前版；plan/reserve 契約**從不改變**，只是 `PlanRoute` 與 `SafeIntervals` 背後的智能加深。

---

## NetDevPack 內嵌（須先解決）

grukirbs 把 NetDevPack 以 **單一 ProjectReference** 引到 `..\..\..\gurkinetdevpack\src\NetDevPack\NetDevPack.csproj`（另有 `GurkiPlugins`、`GurkiCommunication`，皆機器人插件/通訊，**車隊協調器不需要**）。實際只用到 4 個命名空間：`NetDevPack.Domain`(34)、`NetDevPack.Messaging`(28)、`NetDevPack.Data`(18)、`NetDevPack.Utilities`(5)——即 `Entity/IAggregateRoot/ValueObject/DomainEvent/Event/IUnitOfWork/IRepository/IDomainEventDispatcher` 等。

**問題**：`gurkinetdevpack` 原始碼**不在 `third-party/` 也不在 workspace 任何位置**（已搜尋確認）。

**行動**：Phase 0 需從使用者的 `gurkinetdevpack` repo 取得該 `src/NetDevPack` 專案，複製進 `Shared/vendor/NetDevPack/` 並以 ProjectReference 引用（與 grukirbs 位元級一致）。**取得前無法編譯**——這是 Phase 0 的前置阻塞。若一時拿不到，退路為改用公開 `NetDevPack` NuGet（fork 即以此為基），介面相容；但這偏離「內嵌 fork」的決定，需再確認。

---

## 移植時順手修正的具體缺陷（業界視角）

1. **`MapSiteType` 重複列舉值**：`RelaySite=3, AvoidSite=3` 衝突；死鎖避讓點選擇要區分兩者，移植時重新編號。
2. **釋放洩漏**：`GraphMap.UnlockPath` 的區塊/干涉釋放被註解掉（block/interference 會洩漏）；且 `AgvTaskStateMachine.ResetStateWhenFinished` 的釋放也被註解、只在任務結束才放。新模型必須**增量、持續 `Release`**，並確實釋放 ParentBlock + 干涉閉包。
3. **狀態歸屬切分**：`MapResourceStaus {Locked,Belong,...}` 現掛在 `MapResource` 上——把*動態*佔用（Locked/Belong）移到 TrafficControl 的 `ResourceLease`；Map 只保留*靜態*拓撲（`Enable/SiteType`），列舉定義留 `Map.Domain.Shared`。
4. **圖/地圖代碼三份重複**：`AJR.MAPF.Map`、`AJR.Infrastructure/AJR.Platform.GraphMapDP`、`AJR.Platform.Algorithms/AJR.Platform.Graph` 各有一份 GraphMap；移植時**收斂成單一** `Map` 上下文實作。

---

## 風險與決策提醒

- **R1（阻塞）NetDevPack 來源**：見上節，須先取得 fork 原始碼。
- **R2 即時預約狀態：記憶體 vs EF**：`ReservationTable` 每控制週期變動，走 `BaseDbContext.Commit()`/Postgres 每 tick 寫會跟不上。**權威狀態 = 記憶體聚合（singleton，`StateVersion` 樂觀並行）；EF 僅作快照+稽核（週期 + 關機時）供崩潰復原。** TrafficControl 的 `Commit()` 語意因此與其他上下文不同，須在程式碼註明（領域事件仍走正常分發）。
- **R3 鎖模型 v0 設計**：v0 移植整路上鎖**但** `ReservationTable/ResourceLease/SpaceTimeCell` 從一開始就以時間區間為基礎，讓 v1 SIPP 是策略替換而非重寫；**勿**把整路上鎖寫死進聚合不變條件。
- **R4 單一 `ReservationTable` 聚合 = 爭用瓶頸**：一個 `StateVersion` 串行化全車隊配置；起步可接受，規模化時按 `MapBlock`/zone 分片（建議先讓 `ResourceLease` 可獨立定址、分片延後）。
- **R5 演算法庫授權/目標框架**：`third-party/algorithms` 為 net6.0 原始碼，retarget net8.0 並 vendoring 前確認無授權限制。
- **R6 迴圈節奏與確定性**：終身 MAPF 需定義觸發模型（固定 tick vs 事件驅動，建議事件驅動 + watchdog tick）與重規劃確定性（tie-break、`HadWaitedTime` 優先），否則 livelock。v2 引入 RHCR 時窗。

---

## 驗證（如何端到端確認）

**建置/煙霧**
- Phase 0：`dotnet build SwarmRoute.Mapf.sln` 綠燈（NetDevPack 內嵌後）。
- Phase 1：`dotnet run --project Host/SwarmRoute.Host`，`POST /api/maps` 匯入一張拓撲（取材 `AJR.Platform.Minimal` 的 `MapInfo.MapJson` 範例或自造小路網），`GET` 回站點/路段、`IRoadmapQueryService` 回 `RoadmapGraph`。
- Phase 2：呼叫 `IPathPlanningAppService.PlanFor(agent, A, B)` 回站點序列（對拍 `GraphMap.GeneratePath` 同輸入同結果）。

**功能（多機，建議用模擬時鐘 + 記憶體 CAP）**
- Phase 3：兩台車交叉路線→`TryReserve` 序列化、不碰撞；車過後資源釋放（斷言**無洩漏**，含 ParentBlock + 干涉）。
- Phase 4：建構 2/3 車循環等待→`IDeadlockDetector` 回環中車集合（對拍 `IndependenceDetection.DeadlockDetect`）→`AvoidancePlan` 把犧牲車導去 AvoidSite→解除→恢復原目標。

**單元測試場景（`*.Tests`，對應安全不變條件）**：對頭/走廊、swap/邊衝突、跟車（車距）、路口 3–4 車匯聚、2/3/4 車死鎖環、livelock（同一 (車,避讓點) 不得連兩次選且目標距離須嚴格下降）、干涉區重疊（reserve R 必含 `InterferenceClosure(R)`）、釋放洩漏回歸（任務完成後該車預約全數釋放）、drift/重規劃（RHCR：落後車不違反不碰撞、不重約過去）。

**核心不變條件（任何版本恆成立）**：I1 不碰撞（v0 空間、v1+ 時空）、I2 車距、I3 守恆（now 佔用即持有涵蓋 now 的預約）、I4 區塊互斥、I5 預約單一寫者（只有 TrafficControl）、I6 釋放單調（只放過去）、I7 liveness/無飢餓（`HadWaitedTime` 老化）、I8 計劃有效（連通、避黑名單、時間單調、落在回報的安全區間內）。

---

## 主要範本/移植來源檔（執行時對照）

- `third-party/grukirbs/Shared/Gurki.RBS.Infra.Data.Core/Context/BaseDbContext.cs` — UoW + 領域事件分發，**直接照搬**為共享核心。
- `third-party/grukirbs/Robot/Gurki.RBS.Robot.Domain/Entities/Robot.cs` — 聚合根範本（私有 EF ctor、驗證 ctor、`private set`、`StateVersion`），供 `Roadmap/ReservationTable/AgentPlan/DeadlockCase` 照抄。
- `third-party/grukirbs/Robot/.../ValueObjects/RobotDimensions.cs`、`Alarm/.../Events/AlarmRaisedEvent.cs`(+`RobotAlarmSubscriber.cs`) — VO 與（整合）事件/訂閱範本。
- `third-party/AJR.Infrastructure/AJR.Platform.GraphMapDP/GraphMap.cs` — `Init`(→`RoadmapGraph`)、整路上鎖+`Lock/Unlock*`/`UnlockPath`(→`IResourceAllocator`)、剪枝-Dijkstra 過濾的來源。
- `third-party/AJR.MAPF/AJR.MAPF.ConflictDetect/MapResourceAllocationGraph.cs`(+`IndependenceDetection.cs`) — Deadlock `ResourceAllocationGraph` VO + `IDeadlockDetector`。
- `third-party/AJR.MAPF/AJR.MAPF.ConflictResolve/ConflictSolveStateMachine.cs`(+`ISolver.cs`) — 待實作的 `AvoidancePlan` 恢復狀態機。
- `third-party/AJR.MAPF/AJR.MAPF.Map/{MapResource,MapResourceStaus,MapSite,MapLine,MapBlock,ResourceRequest}.cs` — Map 實體 + 狀態切分 + `ReservationRequest` 種子。
