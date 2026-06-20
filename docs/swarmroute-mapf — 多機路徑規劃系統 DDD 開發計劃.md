# swarmroute-mapf — 多機路徑規劃系統 DDD 開發計劃

> _已被取代（Superseded）：本文描述的反應式死鎖解除流程已被 `Liveness` 上下文取代（授予時預防 + 同步的僵局解除策略）。詳見 `Liveness/README.md`。_

## Context（為什麼做這件事）

`swarmroute-mapf` 是一套**多機（AGV/AMR）路徑規劃與車隊協調系統**。本文件最初用於從空 repo 啟動 DDD 重構；截至目前，repo 已具備可編譯的 `SwarmRoute.Mapf.sln`、四個限界上下文、Coordination/Simulation/Host/Web，以及 v0 + v1 的閉環驗證。

要解決的問題：把 `third-party/AJR.MAPF`（非常初步、真正代碼散在 `AJR.Platform.*`）的 MAPF 邏輯，**重構成一套乾淨、可演進、業界水準的 DDD 架構**，比照 `third-party/grukirbs`（成熟的多限界上下文 Clean Architecture .NET 方案）的整套作法，並切成使用者指定的 **4 個領域（限界上下文）**：

| 領域（中文） | 限界上下文 | 職責 |
|---|---|---|
| 資源 / 地圖 | **Map** | 路網拓撲（站點/路段/區塊、干涉關係、圖） |
| 路徑規劃 | **PathPlanning** | 為單機/多機找出可行（時空）路線 |
| 交通管制 | **TrafficControl** | 時空預約 / 路權 / 防碰撞，**擁有即時佔用狀態** |
| 死鎖處理 | **Deadlock** | 偵測 / 避免 / 解除循環等待 |

**現況診斷（業界視角）**：原始引擎是「**整條路徑空間上鎖 + 剪枝 Dijkstra + 事後 RAG 死鎖偵測**」的第一代 AGV 交管——`GraphMap.GeneratePath()` 規劃完直接把整條路線的站點/路段/區塊鎖給一台車，沒有時間維度，因此兩台車不能錯時共用走廊、吞吐受限、且天生易死鎖（才需要事後偵測補救）。本 repo 已完成 v0 基線移植，並已完成 v1 **時空預約 + SIPP + schedule-faithful 執行**閉環；後續 v2/v3 才是優先級/RHCR、CBS/PIBT、連續時間 SIPPwRT 的深化。

**已交付成果**：**可編譯的 4 上下文 DDD 方案 + v0 可跑多機基線 + v1 SIPP 閉環**。v2–v3 保留為下一階段路線圖。

### 已鎖定的關鍵決策（來自確認）

1. **建置位置**：在 **repo 根目錄建全新方案 `SwarmRoute.Mapf.sln`**，從 `third-party/AJR.*` 移植邏輯、把圖演算法庫當 project reference 重用；`third-party/` 維持唯讀參考。
2. **技術堆疊**：**比照 grukirbs 架構、升級至最新 LTS** — **.NET 10 (LTS, C# 14)**、EF Core 10 + PostgreSQL(Npgsql)、CAP（跨上下文整合事件 / Outbox + RabbitMQ）、Hangfire（背景任務）、內建 DI、AutoMapper、NetDevPack（使用者已提供於 `lib/NetDevPack`）。grukirbs 本身用 .NET 8，本專案統一拉到 .NET 10。**不**沿用 AJR.Platform.Minimal 的 SqlSugar/Autofac/Quartz/SignalR/IdentityServer4（該平台僅作參考）。
3. **DDD 基底**：**使用 NetDevPack**（使用者已提供於 repo 根 `lib/NetDevPack`，netstandard2.1、相容 net10、免 retarget），以 ProjectReference 納入。
4. **里程碑狀態**：**腳手架 + v0 基線已關閉**；**v1 SIPP 已關閉**；v2–v3 作為後續路線圖。

---

## 目標架構

### 根命名空間與方案佈局

根命名空間 **`SwarmRoute`**；方案檔 `SwarmRoute.Mapf.sln`。每個限界上下文 `{Ctx}` 比照 grukirbs 採 `{Root}.{Ctx}.{Layer}` 分層命名。

```
/Users/hjqcan/workspace/swarmroute-mapf/
├─ SwarmRoute.Mapf.sln
├─ Directory.Build.props            # net10.0, ImplicitUsings, Nullable, analyzers
├─ Directory.Packages.props         # 集中套件版本 (EF Core 10, CAP 9.x, Hangfire 1.8.x, AutoMapper)
│
├─ Shared/                          # 共享核心（對應 grukirbs/Shared）
│  ├─ SwarmRoute.Domain.Abstractions/      # IBaseRepository<T>；EventBus 抽象 IIntegrationEvent/IIntegrationEventPublisher
│  ├─ SwarmRoute.EventBus/                 # CAP 整合：CapIntegrationEventPublisher、DomainEventToIntegrationEventConverter、AddEventBus()
│  ├─ SwarmRoute.Infra.Data.Core/          # BaseDbContext : IUnitOfWork（收集領域事件→SaveChanges→分發本地+發佈整合事件）；BaseRepository<T>
│  ├─ SwarmRoute.Infra.BackgroundJobs.Core/# Hangfire JobBase、IRecurringJobConfigurator
│  ├─ SwarmRoute.StateMachine.Core/        # IStateMachine/IStateGuard（Stateless 封裝；對應 grukirbs StateMachine+Guards）
│  ├─ SwarmRoute.SpatioTemporal.Kernel/  ★ 跨上下文「時空預約」共享語彙（純型別，無行為，見下）
│  （NetDevPack 由使用者提供於 repo 根 lib/NetDevPack，以 ProjectReference 納入）
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
   └─ algorithms/                         # 由 AJR.Platform.Algorithms(.DataStructures) retarget net10.0 後 vendoring 進來
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

## 建置階段

- **Phase 0 — 骨架與核心（已完成）**：`SwarmRoute.Mapf.sln`、`Directory.*.props`、`Shared/` 核心、`lib/NetDevPack` ProjectReference、vendored graph algorithms。
- **Phase 1 — Map 完整垂直切片（已完成）**：`Roadmap`/`RoadmapGraph`、Map EF/API、`IRoadmapQueryService`。
- **Phase 2 — 單機端到端（已完成）**：`DijkstraPathPlanner` + `IPathPlanningAppService` + Map read seam。
- **Phase 3 — 預約式 TrafficControl（已完成）**：`ReservationTable`、真 `ReservationService`、`TryReserve`/`Release`、快照/背景 job。
- **Phase 4 — 死鎖處理（已完成）**：RAG 偵測、`Deadlock.Case.ResolutionRequested`、避讓/恢復 seams、閉環測試。
- **Phase 5 — v1 時空預約 + SIPP（已完成）**：`SelectablePathPlanner` + `PlannerOptions` + `SippPathPlanner`，TrafficControl snapshot `FreeIntervals`/`IsFree` 作為安全區間來源，Simulation request-level `PlannerKind.Sipp`，`FleetExecutionMode.ScheduleFaithful`，Web SIPP/Dijkstra selector。

### 已關閉與後續路線圖

| 版本 | 狀態 | 變更（哪個上下文） | 買到什麼 |
|---|---|---|---|
| **v0** Dijkstra + 預約表 + Deadlock | ✅ 已關閉 | 剪枝 Dijkstra、整路區間預約、RAG 偵測/恢復、greedy 執行器 | 乾淨架構 + 多機無碰撞基線 |
| **v1** 時空預約 + SIPP | ✅ 已關閉 | PathPlanning `SippPathPlanner` 搜尋安全區間；Simulation `PlannerKind.Sipp` + schedule-faithful 執行；Web 可切換 | 車輛錯時共用走廊；密集場景收斂率與重規劃數優於 v0 |
| **v2** 滾動時窗 + 避免式預防 | 🟡 **部分交付** | Coordination 滾動時窗（RHCR）：`HorizonWindowMs`（預設 ∞=關，opt-in）；Deadlock `WouldCloseCycle` 接進 `TryReserve`（grant 前拒環，預設關閉）。**AA-SIPP(m) 顯式版與 h<w 已否決；PIBT 移至 v3**（見下方決策記錄） | 規劃延遲有界；構造性 liveness（**僅在預約層 wait-for 環會形成的路徑**）。高密度收斂主要由執行層 StepAside 恢復達成，非 RHCR |
| **v3** PIBT + 局部 CBS + 連續時間 SIPPwRT + host-seam（壅塞區聯合求解，全三支柱交付） | ✅ **已交付** | **三大支柱全交付**：① Simulation 執行層 **zone-local PIBT**（opt-in `UsePibt`，`PibtZoneResolver` 逐 tick 聯合步進）；② **局部 CBS/CCBS**（opt-in `UseCbs`，`CbsLocalSolver` 於 `PathPlanning.Domain`、重用 `SippPathPlanner` 當低階、經 `IFleetCoordinationCycle.PlanClusterAsync` 原子預約、聯合求解整路）；③ **連續時間 SIPPwRT**（real-ms 軌跡 replay）。共用 `StuckClusterDetector` 偵測互阻僵局簇 → 釋放預約 → 聯合求解 → 回 prioritized-SIPP。PIBT/CBS 是互斥的同層 cluster owner；CBS 只允許 SIPP/schedule-faithful，並沿用 RHCR horizon 與 executor physical blockers。**並含 host-seam**：`IJointStepPlanner` + `ReservationTable.TryGrantJointStep`（原子聯合提交）+ 自治 `FleetCoordinationLoop` 聯合求解器（`JointResolver=Cbs/Pibt`，經 `IFleetCoordinationCycle.ResolveStandoffsAsync`）。皆 executor-triggered、預設關、**不動凍結接縫** | 高密度物理僵局收斂（多 seed DidNotConverge → Completed/更多抵達，全程無碰撞）；CBS 完整局部解法（單測證明攻下 PIBT 攻不下的靜態案例），動態迴圈破部分 seed 但**不主宰 PIBT**；SIPPwRT 連續時間軌跡；host-seam 以表為權威封 cross-tick 健全性、帶 PIBT/CBS 進 autonomous host loop |
| **v4** SwarmRoute Lab + GuidanceGraph Optimizer | 下一階段 | 倉儲級 FMS 評測/優化平台：ScenarioBench（真實場景基準）、TraceEvent 標準、Metrics Engine、GuidanceGraph（交通工程層）、Benchmark Dashboard。詳細計劃見 `docs/下一步計劃研究.md` | 真實倉儲吞吐評測、交通策略優化、可展示/合作/商業化的平台 |

> 關鍵：v1 已證明 plan/reserve 契約可在不改 Coordination 內迴圈的前提下加深智能；v2 仍沿用此契約邊界。**v3 PIBT 為執行層 zone-local 機制**（物理僵局發生在執行層、非預約層），刻意落在 plan/reserve 接縫**之外**；其文檔字面的 `IJointStepPlanner`+原子聯合提交 host-seam 版亦已於 v3 交付。

> **v2 決策記錄（2026-06）**：
> - **AA-SIPP(m)（PathPlanning 內顯式優先級規劃）— 否決**：Coordination 層已是優先級序列化規劃（按 `AgentGoal.Priority` 逐車 plan→reserve、後車避開前車預約），等價於優先級 SIPP；PathPlanning **內部沒有、也不再加**顯式 AA-SIPP(m)，再加是無實測收益的 dead-weight。
> - **h<w lookahead — 否決**：實測小窗的收斂傷害源於 re-plan **churn** 而非每窗暫停，更頻繁重規劃只會更糟；且它想廣化的高密度收斂已由執行層 StepAside 達成。
> - **PIBT — 移至 v3**：與 `Plan()`+`TryReserve` 單-agent 接縫不合（需聯合每-tick 決策 + 原子聯合提交），屬 v3 等級工程。
> - **TrafficControl 端 right-of-way**：grant-時拒環的讓路判定目前用 **ordinal-id**，**非**真 priority/aging（lease 不帶持有者 priority、`TryReserve` 不傳 priority）。優先級 right-of-way 屬 refinement，未交付——本階段只聲稱 **Coordination 層** prioritized SIPP，不聲稱 TrafficControl priority/right-of-way 完成。
> - **WouldCloseCycle 範圍**：正確性已由 seeded 預約環測試證明；在 `FleetLoopDriver` 執行層 sim 中 **inert**（該 sim 的死鎖是物理僵局、非預約環）。sim 的 A/B 開關證明的是 **wiring / non-regression**，不是「sim 中防死鎖觸發有效」。

> **v3 決策記錄（2026-06）**：
> - **PIBT 採 executor-anchored（而非文檔字面的 `IJointStepPlanner` + 預約表原子聯合提交）**：開放問題（物理僵局）就在執行層——agent 各握互斥區間預約卻在執行層對撞，RAG 看不到、WouldCloseCycle 在 sim inert、RHCR 只邊際改善。PIBT 落在 `FleetLoopDriver`，重用既有 per-tick fixpoint 解器不變量與 release-then-replan idiom，**不動任何凍結接縫**、不碰 `ReservationTable`/`IPathPlanner`/Coordination/DI 工廠，純加法於單一 bounded context（3 生產檔 + 4 純新檔 `Pibt/`）。是現有 StepAside/stall-reroute band-aid 的原則化、無死鎖泛化。
> - **CBS/CCBS 局部 + 連續時間 SIPPwRT — 已於 v3 交付**：PIBT 先行後，另兩支柱續於 v3 完成——局部 CBS/CCBS 已交付（見下方「第二支柱」）；連續時間 SIPPwRT 雖打破 `TimeAxis.HopMs` 離散 tick 模型（波及整條時間軸/執行器/幾何），仍以加法、opt-in、預設關的方式於 v3 落地（real-ms 軌跡 replay）。
> - **`IJointStepPlanner` 港 + `TryGrantJointStep` 原子聯合提交 + host-loop 接線 — 已於 v3 交付**：讓預約表為唯一真相、帶 PIBT/CBS 進 autonomous `FleetCoordinationLoop`（`JointResolver=Cbs/Pibt`，經 `IFleetCoordinationCycle.ResolveStandoffsAsync`），並徹底封住下方 cross-tick 健全性邊界。純 `PibtZoneResolver` 即依此設計被原樣包進該港（讀唯讀 `PibtAgentView`、回 `agentId→下一格`）。
> - **健全性邊界（誠實標註）**：簇內 + **同-tick 跨簇**無碰撞由建構保證（解器把非簇 `occupantNow` + 本 tick 排定 `claimedNext` 一併當 reserved）；**cross-tick 跨簇僅緩解、非證明**——PIBT 簇成員 episode 間不持 lease，靠 `physicallyBlockedCells` 使其餘 fleet 繞行 + block(3)/(3b) 安全網（含 `PibtActive`，報 `CollisionDetected`、不靜默）+ 全密度 collision-free sweep 經驗證。完全封口＝v3 的 host-seam `TryGrantJointStep`（表為權威）。
> - **量測收益（7×7/16，以 StepAside 為基線）**：多個 DidNotConverge seed 轉 Completed（全 16 抵達）或抵達數嚴格增加，全程 0 碰撞、確定性；已收斂 seed 逐位元不變（opt-in 預設關＝byte-identical v2，回歸鎖）。仍有 seed 維持 DidNotConverge——PIBT 助大多數而非全部，`DidNotConverge` tick budget 硬底永在（永不崩、永不對撞）。
>
> **v3 第二支柱：局部 CBS（2026-06）**：
> - **executor-triggered，低階重用 SIPP**：`CbsLocalSolver`（`PathPlanning.Domain/Cbs/`）為離散有界 Conflict-Based Search（best-first sum-of-costs、確定性 NodeKey、vertex/edge 衝突鏡射預約表、有界 floor 永不崩）。**低階重用 `SippPathPlanner`**——一條 CBS 約束＝一段 busy 區間，透過 `CbsConstraintView : IReservationView` overlay 餵給 SIPP（不重寫時空/dwell/RHCR 數學、軸一致、輸出可被 `TryReserve` 預約）。由 `FleetLoopDriver` 偵測簇觸發，經新 **default-interface** 方法 `IFleetCoordinationCycle.PlanClusterAsync`（在 Coordination 跑 CBS + 逐路 `TryReserveAsync` 原子提交）接入；opt-in `UseCbs`、預設關＝byte-identical；與 `UsePibt` 互斥，因同一物理僵局簇只能有一個 executor owner。CBS 只在 SIPP/schedule-faithful 下可用，且低階沿用 `HorizonWindowMs` 與無 lease 的 parked/waiting physical blockers。**不動凍結接縫**（`TryGrant`/`TryReserveAsync`/`RunCycleAsync`/`IPathPlanner` 皆未改），且因 CBS 產整路、走既有 schedule-faithful 執行，整合比 PIBT 更輕（無物理驅動碼）。
> - **收益邊界（誠實標註）**：**完整/最佳**性已由單測證明——攻下 4-cycle head-on 等貪婪 PIBT 攻不下的**靜態**案例。閉環中 CBS 攻下部分 baseline-DidNotConverge seed（如 7×7/16 s5 → Completed/16）、總抵達數增、全程無碰撞、不回歸已收斂 seed——但**動態迴圈中不主宰 PIBT**：CBS **一次性**求解局部快照並**預約走廊**（鎖死，後序須繞行），PIBT 則**無鎖、每 tick 持續驅動**，故 PIBT 收斂更多 seed。兩者互補；CBS 不保證攻下每個 seed（含 s2）。完整收斂仍非保證，`DidNotConverge` 硬底永在。

---

## NetDevPack（已就緒）

grukirbs 用 `NetDevPack.Domain`(Entity/IAggregateRoot/ValueObject/DomainEvent)、`NetDevPack.Messaging`(Event/IDomainEventDispatcher)、`NetDevPack.Data`(IUnitOfWork/IRepository)、`NetDevPack.Utilities` 這幾個命名空間（`GurkiPlugins`、`GurkiCommunication` 為機器人插件/通訊，**車隊協調器不需要**）。

**現況（已解決，原 R1 阻塞關閉）**：使用者已將 NetDevPack 放於 repo 根 **`lib/NetDevPack/src/NetDevPack/NetDevPack.csproj`**——目標框架 **netstandard2.1**（與 .NET 10 相容、**免 retarget**），含所有所需型別：`Entity/ValueObject/DomainEvent/IUnitOfWork/IRepository/IDomainEventDispatcher/InMemoryDomainEventDispatcher`（另依賴 FluentValidation 11）。

**行動**：Phase 0 直接把該 `.csproj` 以 ProjectReference 加入方案各 Domain/Infra 專案即可，無前置阻塞。

---

## 移植時順手修正的具體缺陷（業界視角）

1. **`MapSiteType` 重複列舉值**：`RelaySite=3, AvoidSite=3` 衝突；死鎖避讓點選擇要區分兩者，移植時重新編號。
2. **釋放洩漏**：`GraphMap.UnlockPath` 的區塊/干涉釋放被註解掉（block/interference 會洩漏）；且 `AgvTaskStateMachine.ResetStateWhenFinished` 的釋放也被註解、只在任務結束才放。新模型必須**增量、持續 `Release`**，並確實釋放 ParentBlock + 干涉閉包。
3. **狀態歸屬切分**：`MapResourceStaus {Locked,Belong,...}` 現掛在 `MapResource` 上——把*動態*佔用（Locked/Belong）移到 TrafficControl 的 `ResourceLease`；Map 只保留*靜態*拓撲（`Enable/SiteType`），列舉定義留 `Map.Domain.Shared`。
4. **圖/地圖代碼三份重複**：`AJR.MAPF.Map`、`AJR.Infrastructure/AJR.Platform.GraphMapDP`、`AJR.Platform.Algorithms/AJR.Platform.Graph` 各有一份 GraphMap；移植時**收斂成單一** `Map` 上下文實作。

---

## 風險與決策提醒

- **R1（已解決）NetDevPack 來源**：使用者已提供於 `lib/NetDevPack`（netstandard2.1、相容 net10），以 ProjectReference 納入，原阻塞關閉。
- **R2 即時預約狀態：記憶體 vs EF**：`ReservationTable` 每控制週期變動，走 `BaseDbContext.Commit()`/Postgres 每 tick 寫會跟不上。**權威狀態 = 記憶體聚合（singleton，`StateVersion` 樂觀並行）；EF 僅作快照+稽核（週期 + 關機時）供崩潰復原。** TrafficControl 的 `Commit()` 語意因此與其他上下文不同，須在程式碼註明（領域事件仍走正常分發）。
- **R3（v1 已驗證關閉）鎖模型 v0 設計**：v0 移植整路上鎖**但** `ReservationTable/ResourceLease/SpaceTimeCell` 從一開始就以時間區間為基礎；v1 已以 `SippPathPlanner` + `FreeIntervals` + schedule-faithful 執行證明這是策略替換而非重寫。
- **R4 單一 `ReservationTable` 聚合 = 爭用瓶頸**：一個 `StateVersion` 串行化全車隊配置；起步可接受，規模化時按 `MapBlock`/zone 分片（建議先讓 `ResourceLease` 可獨立定址、分片延後）。
- **R5 演算法庫授權/目標框架**：`third-party/algorithms` 為 net6.0 原始碼，retarget net10.0 並 vendoring 前確認無授權限制。
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
- Phase 5 / v1：`SippPathPlannerTests` 覆蓋安全區間、CP/lane 等待、永久阻塞繞路、確定性；`SippClosedLoopTests` 透過真實 Coordination + PathPlanning + TrafficControl + Simulation 驗證 SIPP 收斂、零碰撞、重規劃顯著少於 Dijkstra；HTTP smoke `POST /api/simulation/run`（7×7、16 AGV、`planner=Sipp`）返回 `Completed`、`collisions=0`、全員到達。

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
