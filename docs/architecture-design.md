# SwarmRoute MAPF — 架構設計（Architecture Design）

> 狀態：**Approved（已核可）** ｜ 目標框架：**.NET 10 (LTS, C# 14)** ｜ 配套文件：[`team-implementation-plan.md`](./team-implementation-plan.md)

本文件是 `swarmroute-mapf`（多機 AGV/AMR 路徑規劃與車隊協調系統）的權威架構設計。團隊實作以本文為「**做什麼**」、以團隊實現計劃為「**怎麼做、誰做、何時做**」。

---

## 1. 背景與目標

`swarmroute-mapf` 是起步階段的全新專案，要做一套**多機路徑規劃與車隊協調系統**。本架構把 `third-party/AJR.MAPF`（非常初步、真正代碼散在 `AJR.Platform.*`）的 MAPF 邏輯，**重構成乾淨、可演進、業界水準的 DDD 架構**，比照 `third-party/grukirbs`（成熟的多限界上下文 Clean Architecture .NET 方案）的整套作法，切成 **4 個限界上下文**：

| 領域（中文） | 限界上下文 | 職責 |
|---|---|---|
| 資源 / 地圖 | **Map** | 路網拓撲（站點/路段/區塊、干涉關係、圖） |
| 路徑規劃 | **PathPlanning** | 為單機/多機找出可行（時空）路線 |
| 交通管制 | **TrafficControl** | 時空預約 / 路權 / 防碰撞，**擁有即時佔用狀態** |
| 死鎖處理 | **Deadlock** | 偵測 / 避免 / 解除循環等待 |

**現況診斷（業界視角）**：原始引擎是「**整條路徑空間上鎖 + 剪枝 Dijkstra + 事後 RAG 死鎖偵測**」的第一代 AGV 交管——`GraphMap.GeneratePath()` 規劃完直接把整條路線的站點/路段/區塊鎖給一台車，**沒有時間維度**，因此兩台車不能錯時共用走廊、吞吐受限、且天生易死鎖。業界已演進到**時空預約表 / 安全區間規劃（SIPP）**、**滾動時窗終身重規劃（RHCR）**、**優先級規劃 / PIBT / CBS**；參考車隊管理架構為 **OpenTCS**（Router＝路徑規劃、Scheduler＝資源配置/交管、Dispatcher＝派工）。本架構已完成 **v0 基線**與 **v1 SIPP**，後續沿 v2→v3 深化。

### 已鎖定決策

1. **建置位置**：repo 根目錄建全新方案 `SwarmRoute.Mapf.sln`，從 `third-party/AJR.*` 移植、把圖演算法庫當 project reference 重用；`third-party/` 維持唯讀參考。
2. **技術堆疊**：比照 grukirbs 架構、升級至最新 LTS — **.NET 10 (C# 14)**、EF Core 10 + PostgreSQL(Npgsql)、CAP（整合事件/Outbox + RabbitMQ）、Hangfire（背景任務）、內建 DI、AutoMapper、NetDevPack。**不**沿用 AJR.Platform.Minimal 的 SqlSugar/Autofac/Quartz/SignalR/IdentityServer4（僅作參考）。
3. **DDD 基底**：使用 NetDevPack（使用者已提供於 `lib/NetDevPack`，netstandard2.1、相容 net10、免 retarget），以 ProjectReference 納入。
4. **里程碑狀態**：腳手架 + v0 基線已關閉；v1 SIPP 已關閉；v2–v3 為後續路線圖。

---

## 2. 方案佈局

根命名空間 **`SwarmRoute`**；方案檔 `SwarmRoute.Mapf.sln`。每個限界上下文 `{Ctx}` 採 `SwarmRoute.{Ctx}.{Layer}` 分層命名（比照 grukirbs）。

```
swarmroute-mapf/
├─ SwarmRoute.Mapf.sln
├─ Directory.Build.props            # net10.0, LangVersion 14, ImplicitUsings, Nullable, analyzers
├─ Directory.Packages.props         # 集中套件版本 (EF Core 10, CAP 9.x, Hangfire 1.8.x, AutoMapper)
│
├─ Shared/                          # 共享核心（對應 grukirbs/Shared）
│  ├─ SwarmRoute.Domain.Abstractions/      # IBaseRepository<T>；EventBus 抽象
│  ├─ SwarmRoute.EventBus/                 # CAP 整合：發佈者、DomainEvent→IntegrationEvent 轉換、AddEventBus()
│  ├─ SwarmRoute.Infra.Data.Core/          # BaseDbContext : IUnitOfWork；BaseRepository<T>
│  ├─ SwarmRoute.Infra.BackgroundJobs.Core/# Hangfire JobBase、設定器
│  ├─ SwarmRoute.StateMachine.Core/        # IStateMachine/IStateGuard（Stateless 封裝）
│  └─ SwarmRoute.SpatioTemporal.Kernel/  ★ 跨上下文「時空預約」共享語彙（純型別、無行為）
│
├─ Map/              # 完整持久化上下文（Domain.Shared/Domain/App.Contract/App/Infra.Data/IoC/Api/Tests）
├─ PathPlanning/     # 精簡純計算（無 EF、初期無 Api；Domain/App.Contract/App/IoC/Tests）
├─ TrafficControl/   # 完整 + 擁有即時預約狀態（含 Infra.Data 快照、BackgroundJobs、Api）
├─ Deadlock/         # 精簡計算+反應式（無 EF、初期無 Api）
├─ Coordination/     # ★ 終身控制迴圈（FleetCoordinationLoop = OpenTCS 的 Dispatcher）
├─ Host/             # 組合根（單一可部署）：Program.cs 串接各上下文 IoC + EventBus + Hangfire + Coordination
├─ lib/NetDevPack/   # 使用者提供的 NetDevPack（netstandard2.1, 相容 net10）— DDD 基底，ProjectReference 納入
└─ third-party/      # 唯讀參考 + 可重用演算法庫（algorithms 由 AJR.Platform.Algorithms retarget net10.0）
```

### 各上下文分層矩陣

| 上下文 | Domain.Shared | Domain | App.Contract | App | Infra.Data | BgJobs | IoC | Api | Tests | 理由 |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|---|
| **Map** | ✅ | ✅ | ✅ | ✅ | ✅ | ➖ | ✅ | ✅ | ✅ | 拓撲是耐久主檔，需 CRUD + EF + migration |
| **PathPlanning** | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌→後期 | ✅✅ | 純計算；plan 是瞬時函數，不持久化 |
| **TrafficControl** | ✅ | ✅ | ✅ | ✅ | ✅(快照/稽核) | ✅ | ✅ | ✅ | ✅ | 擁有即時預約狀態；需 job/Api/EF 快照 |
| **Deadlock** | ✅ | ✅ | ✅ | ✅ | ❌ | ❌ | ✅ | ❌→後期 | ✅ | 計算+反應式；死鎖瞬時、無耐久狀態 |

> **只有 Map 與 TrafficControl 全分層**；PathPlanning/Deadlock 刻意做無 EF/無 Api 的純領域計算庫（對應 OpenTCS 把 Router/Scheduler/Dispatcher 做成服務而非資料庫）。grukirbs 已有精簡上下文先例（`Alarm`/`Order`）。

---

## 3. grukirbs 戰術慣例（已驗證、直接沿用）

- **聚合根**：`public class Roadmap : Entity, IAggregateRoot`（NetDevPack）。私有 EF 無參 ctor；公開 ctor 驗不變條件並 `throw ArgumentException`；屬性 `private set`；行為方法改狀態 + `IncrementStateVersion()`（樂觀並行）。範本 `third-party/grukirbs/Robot/Gurki.RBS.Robot.Domain/Entities/Robot.cs`。
- **值物件**：`sealed class MapPosition : ValueObject` 覆寫 `GetEqualityComponents()`、唯讀屬性、`static Empty`。範本 `.../ValueObjects/RobotDimensions.cs`。
- **領域事件**：`class MapRoadmapPublishedEvent : DomainEvent, IIntegrationEvent`，`EventName => "Map.Roadmap.Published"`、`Version => "v1"`（命名 `Ctx.Aggregate.Action`）。本地處理器 `Application/Handlers/`，跨上下文訂閱者 `Application/Subscribers/`（`[CapSubscribe]`）。
- **UoW + 事件分發**：`*.Infra.Data` 的 DbContext 繼承 `BaseDbContext`，`Commit()` 從 EF ChangeTracker 收集聚合 `DomainEvents` → `SaveChangesAsync()` → 經 `IDomainEventDispatcher` 分發本地事件 + 經 `IIntegrationEventPublisher` 發佈整合事件到 CAP。範本 `third-party/grukirbs/Shared/Gurki.RBS.Infra.Data.Core/Context/BaseDbContext.cs`（**直接照搬**）。
- **IoC**：每上下文一個 `*NativeInjectorBootStrapper.RegisterServices(WebApplicationBuilder)`，Host `Program.cs` 依序呼叫。

---

## 4. 共享核心 `SpatioTemporal.Kernel`（最關鍵設計）

MAPF 天生把「規劃 + 預約 + 死鎖」耦在一起；切錯得到貧血/碎嘴設計。對策：**只定義跨上下文語彙、不含行為與狀態的共享核心**，被 4 個 Domain 引用：

- `ResourceRef`(VO)：`(kind ∈ {CP, Lane, Block, Zone}, id)`。
- `TimeInterval`(VO)：`[t_enter, t_exit)` 半開、單一車隊單調時鐘(ms)。
- `SpaceTimeCell` = `(ResourceRef, TimeInterval)`；`SpaceTimePath` = 有序 cells。**規劃↔預約之間流動的核心物件**（現有系統缺這層）。
- `SafeInterval`(VO，衍生)：某資源對某車無衝突的最大連續區間（SIPP 安全區間）；**TrafficControl 算出、PathPlanning 只讀**。
- `IReadOnlyReservationView`：`FreeIntervals(ResourceRef)`、`IsFree(ResourceRef, TimeInterval)`。
- `ResourceAllocationGraphSnapshot`(不可變 DTO)：佔用 + 等待邊，供 Deadlock 建 RAG。

---

## 5. 各上下文設計與來源對應（摘要）

> 完整型別清單、移植來源檔逐一對應，見 [`team-implementation-plan.md`](./team-implementation-plan.md) 的工作流任務卡。

- **Map**：聚合根 `Roadmap`（對應 `GraphMap.Init`）；子實體 `MapSite/MapLine/MapBlock`；VO `MapPosition/RoadmapGraph/InterferenceSet`；服務 `IRoadmapGraphFactory/IInterferenceCalculator`；倉儲 `IRoadmapRepository`；事件 `Map.Roadmap.Published/Imported`；讀模型 `IRoadmapQueryService`。來源 `third-party/AJR.MAPF/AJR.MAPF.Map/*`、`AJR.Infrastructure/AJR.Platform.GraphMapDP/GraphMap.cs`。
- **PathPlanning**：聚合 `AgentPlan`；VO `SpaceTimePath/PlanRequest/WaitAction`；服務 `IPathPlanner`（`DijkstraPathPlanner` v0 → `SippPathPlanner` v1）；消費端 `IReservationQuery`；事件 `PathPlanning.AgentPlan.Computed/Failed`。來源 `AJR.MAPF.XCBS/CBS.cs`、`third-party/algorithms`。
- **TrafficControl**：聚合根 `ReservationTable`（即時佔用權威）；實體/VO `ResourceLease/ReservationRequest/RightOfWay/Conflict`；服務 `IResourceAllocator/IReservationCalendar/IConflictDetector`；狀態機 `{Free→Requested→Reserved→InTransit→Releasing}`；提供端 `ReservationService : IReservationQuery`、`ITrafficCoordinatorAppService.TryReserve/Release`；事件 `Reservation.Granted/Denied/Released`、`Allocation.Contended`；Job `LeaseExpirySweepJob/StaleRequestEscalationJob`。來源 `GraphMap.cs`(鎖/解鎖/剪枝)、`AJR.MAPF.Map/{ResourceRequest,MapResourceUsage}.cs`、`ConflictResolve/*`。
- **Deadlock**：聚合根 `DeadlockCase`；VO `ResourceAllocationGraph/DeadlockCycle`；服務 `IDeadlockDetector/IDeadlockResolver`；聚合 `AvoidancePlan`(`SelectVictim→SelectAvoidancePoint→ReserveDetour→DispatchToAvoid→ConfirmCleared→Recover`)；事件 `Deadlock.Case.Detected/ResolutionRequested/Resolved`。來源 `AJR.MAPF.ConflictDetect/*`、`algorithms/CyclesDetector.cs`、`ConflictResolve/ISolver.cs`。

---

## 6. 上下文間契約（縫）

**規則：一個規劃週期內 = 行程內介面；跨週期/觸發他子系統節奏 = CAP 整合事件。**

| 縫 | 機制 | 為何 |
|---|---|---|
| **Map → 全體（讀拓撲）** | 行程內 `IRoadmapQueryService`（回快取 `RoadmapGraph`）+ `Map.Roadmap.Published` CAP 事件失效快取 | 規劃每週期同步要圖；拓撲少變→推播失效用 CAP |
| **PathPlanning ⇄ TrafficControl（預約）** | **行程內介面** `IReservationQuery`/`IReadOnlyReservationView`（Kernel）+ `TryReserve(SpaceTimePath)` | SIPP 搜尋迴圈內每次讀安全區間（數千讀/規劃）；**必須行程內** |
| **TrafficControl → Deadlock** | `Allocation.Contended` CAP 觸發 + 行程內 `ITrafficControlSnapshotProvider` 讀一致快照 | 偵測週期/觸發式，非同步解耦保持熱路徑快 |
| **Deadlock → Coordination/PathPlanning** | `Deadlock.Case.ResolutionRequested` CAP 事件 | 車隊級反應，解耦讓 Deadlock 維持純分析器 |

---

## 7. 終身控制迴圈（Coordination）

`SwarmRoute.Coordination.Application.FleetCoordinationLoop : IHostedService`——終身線上 MAPF 驅動器，滾動時窗（RHCR 風格）。每 tick / 新目標 / `Allocation.Contended` / `Deadlock.ResolutionRequested`：

1. `IRoadmapQueryService.GetGraph()`（同步、快取）
2. 每台需規劃的車：`IPathPlanner.Plan(graph, req, reservationView)`
3. `TryReserve(path)` → `Denied/Queued` 則剪掉爭用資源重規劃
4. 車通過後 `Release(agentId, passedCells)`（**增量、持續釋放**）
5. 死鎖由訂閱者反應式處理（job 觸發），不在內迴圈輪詢

**Host**：照 grukirbs 順序 `AddEventBus → 各上下文 RegisterServices → Hangfire → AddHostedService<FleetCoordinationLoop>`。僅 Map + TrafficControl 註冊 DbContext；CAP 用 PostgreSQL outbox + RabbitMQ，dev 可 `EventBus:UseInMemory=true`。

---

## 8. 版本狀態與演進路線圖

| 版本 | 狀態 | 變更 | 買到 |
|---|---|---|---|
| **v0** Dijkstra + 預約表 + Deadlock | ✅ 已關閉 | 移植「剪枝 Dijkstra + 整路上鎖 + RAG 偵測 + 最小恢復」 | 乾淨架構 + 正確基線（無資源洩漏） |
| **v1** 時空預約 + SIPP | ✅ 已關閉 | `SippPathPlanner` 搜尋 `SafeIntervals()`；Simulation 可選 `PlannerKind.Sipp` 並 schedule-faithful 執行 | 車輛錯時共用走廊；密集場景收斂與重規劃數優於 v0 |
| **v2** RHCR + 避免式預防 | 🟡 部分交付 | 滾動時窗（RHCR，opt-in `HorizonWindowMs`，預設關閉）；`WouldCloseCycle` 接進 `TryReserve`（opt-in，預設關閉）。**AA-SIPP(m) 顯式版 / h<w 否決；PIBT → v3** | 延遲有界、構造性 liveness（**僅預約環路徑**）；高密度收斂主要由執行層 StepAside 達成 |
| **v3** CBS/CCBS + PIBT + SIPPwRT | 下一階段 | 密集處 PIBT（需新聯合-step 接縫 + 原子聯合提交）；壅塞 Zone 局部 CBS；連續時間 + 運動學 | 最高密度、平滑時間精確運動 |

> v2 決策記錄（AA-SIPP(m)/h<w 否決、PIBT→v3、TrafficControl 讓路為 ordinal-id 非真 priority、WouldCloseCycle 在執行層 sim 中 inert）見 DDD 開發計劃 §「v2 決策記錄」。

---

## 9. 風險與不變條件

**風險**：R1（**已解決**）NetDevPack 由使用者提供於 `lib/NetDevPack`（netstandard2.1、相容 net10），以 ProjectReference 納入；R2 即時預約狀態走**記憶體聚合**（`StateVersion` 樂觀並行），EF 僅快照/稽核；R3 v0 雖移植整路上鎖，但 `ReservationTable/ResourceLease/SpaceTimeCell` 一開始即以時間區間為基礎；R4 單一 `ReservationTable` 為爭用瓶頸（規模化按 zone 分片）；R6 迴圈節奏/重規劃確定性需明確定義避免 livelock。

**移植時順手修正**：`MapSiteType` 重複列舉值 `RelaySite=3/AvoidSite=3`；`UnlockPath` 的 block/interference 釋放洩漏；`MapResourceStaus` 動態狀態移到 TrafficControl、Map 只留靜態拓撲；三份重複 GraphMap 收斂成單一 Map 實作。

**核心安全不變條件**：I1 不碰撞（v0 空間、v1+ 時空）｜I2 車距｜I3 守恆（佔用即持有涵蓋 now 的預約）｜I4 區塊互斥｜I5 預約單一寫者（只 TrafficControl）｜I6 釋放單調（只放過去）｜I7 liveness/無飢餓（`HadWaitedTime` 老化）｜I8 計劃有效（連通、避黑名單、時間單調、落在安全區間內）。
