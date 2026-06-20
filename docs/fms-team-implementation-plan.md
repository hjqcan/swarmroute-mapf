# SwarmRoute FMS — 工業級車隊管理團隊開發計劃（Team Implementation Plan）

> 狀態：**Draft / 待核可（Pending Approval）** ｜ 目標框架：**.NET 10 (C# 14)** ｜ 設計依據：[`工業級 FMS.md`](./工業級%20FMS.md)
> 上游路線圖：v0–v3（solver stack）已交付、v4 = SwarmRoute Lab（Metrics/Guidance/Benchmark/ScenarioBench）進行中。
>
> 本文是「**怎麼做、誰做、何時做、做到什麼算完成**」。設計「做什麼、為什麼」見 `工業級 FMS.md`。
> 核可後由**多代理工作流**執行（見 §11），每個 squad = 一組 worktree 隔離的 agent。

---

## 0. 一句話定位

> **把 SwarmRoute 從「MAPF solver」升級成「工廠級 AGV/FMS 交通大腦」：終點不再是任意格子而是合法任務點；到達不再永久停車而是任務完成後清場；停著的車不再是靜態障礙而是可被調度的資源；而「不可移動的長時作業」必須在開始前做全局准入控制（先讓後車過，再關門作業）。**

v3 solver 維持原職責（解 live standoff）；FMS 層負責 endpoint / parking / dispatch / dock-admission。兩者**分工**，不互相背鍋。

---

## 1. 目標與交付定義（三版 FMS）

承 `工業級 FMS.md`「實作優先級」，本計劃交付**完整三版**，每版獨立可驗收、每個開關 **opt-in 預設關 → byte-identical**（沿用本專案 v2/v3 既有紀律：回歸鎖死）。

| 版本 | 名稱 | 核心交付 | 買到什麼 | 里程碑 |
|---|---|---|---|---|
| **FMS-V1** | 最小可用：站點語義 + Dock 准入 | `SiteRole`、`StationDefinition`(serviceDuration/blockingClosure/preDockBuffers)、`AgvMissionState`(WaitingDockAdmission/InService)、`MobilityClass`、最小 `DockAdmissionController`（blockingClosure 上有近期車流就 hold 在 buffer，否則 grant docking+service 預約）、**InService 車不可被 StepAside/PIBT/CBS 挪動** | 「車到工位 ≠ 立刻開工」；不可移動作業前先評估會不會堵全局；7×7/16 主因（任務點永久堵路）開始被治理 | **M-F1** |
| **FMS-V2** | 交通影響分析 + 場景語義 | `ITrafficImpactAnalyzer`（affected-vehicle 偵測）、**clearance-before-service** 批次放行、priority-aware admission、`NonConvergenceReason` 自動分類、`ParkingManager`（persistent relocation，取代 20-tick 必回）、`ScenarioMode.WarehouseWellFormed` + `WellFormedEndpointGenerator` | 「先讓幾台後車過、再放 AGV 進工位」；DidNotConverge 可歸因；高完成率場景與壓力場景分開評估 | **M-F2** |
| **FMS-V3** | 全局排程 + Lifelong | `ITaskDispatcher`（可插拔分派）、`IStationScheduler`、`IStationResourceCalendar`（長租約日曆）、**cost-based admission**（servicePriority·urgency − blockedPenalty − …）、`ScenarioMode.LifelongDispatch`（任務流、throughput/P95/queue 指標）、站點三型（Non/Soft/Hard-blocking）、`IRelocationClusterSolver` | 真正的 FMS Dispatcher：多工位多 AGV 任務排序 + 准入代價權衡 + 長時作業日曆 | **M-F3** |

**里程碑驗收（Demo）**

| # | 名稱 | 驗收 |
|---|---|---|
| **M-F1** | Dock 准入貫通 | 1 個 Hard-blocking 工位（serviceDuration 長）：AGV 先到 PreDockBuffer、被 hold；後方車先過 blockingClosure；清空後才 grant；作業期間該車是 `InService`，StepAside/PIBT/CBS 都不挪它；全程 0 碰撞。opt-in 關閉時 byte-identical。 |
| **M-F2** | WarehouseWellFormed 高完成率 | 7×7/16 WarehouseWellFormed：任務點只從 endpoint 抽、完成後清場去 parking/buffer；大部分 seed `Completed`；殘餘 DidNotConverge 由 `NonConvergenceReason` 標為 `ParkingSaturation`/`TickBudgetExceeded`（而非 `ParkedGoalBlocker`）。 |
| **M-F3** | Lifelong throughput | 長時任務流：throughput 穩定、parking/buffer 不飽和、P95 wait 可控、無永久堵塞；cost-based admission 在「CNC 快停機 vs 後車去空等位」時做出正確讓行/搶先決策。 |

---

## 2. 架構與爆炸半徑（Blast Radius）

### 2.1 新增限界上下文

新增**一個**上下文，置於 `Coordination` 之上：

```
SwarmRoute.Dispatch/                      ★ FMS 統籌層（Task + Station + Dock Admission）
├─ SwarmRoute.Dispatch.Domain.Shared/     # SiteRole? (見 ADR-F1)、MobilityClass、AgvMissionState、ScenarioMode、NonConvergenceReason、StationType
├─ SwarmRoute.Dispatch.Domain/            # StationDefinition、EndpointSet、ServiceAdmission*、TrafficImpact、IDockAdmissionController/IStationScheduler/ITaskDispatcher/IStationResourceCalendar/ITrafficImpactAnalyzer/IRelocationClusterSolver、純策略實作
├─ SwarmRoute.Dispatch.Application.Contract/
├─ SwarmRoute.Dispatch.Application/        # 具體服務（同步、確定性）
├─ SwarmRoute.Dispatch.Infra.CrossCutting.IoC/  # AddDispatch()/AddStationScheduling()
└─ SwarmRoute.Dispatch.Tests/
```

> 既有 `Coordination.Application/Dispatch/`（`TransportOrder`/`OrderBook`/`VehicleRegistry`/`DispatcherService`/`ICoordinationGoalSource`）是 v1 Track-B 的雛形——FMS `ITaskDispatcher`/`IStationScheduler` **建構於其上**，不重寫；`DispatcherService` 的 nearest-idle 分派被 `ITaskDispatcher` 策略接管。

### 2.2 上下文改動矩陣

| 上下文 | 改動 | 既有可重用（探勘已證實） | 凍結（off 時 byte-identical） |
|---|---|---|---|
| **Map** | `SiteRole` 列舉 + `MapSite` 加 optional ctor 參數（預設 `Transit`）+ EF string 欄位 + migration；`IEndpointPolicy`/`WellFormedEndpointGenerator`（讀 `RoadmapGraph` 連通/egress） | `MapSiteType{CPSite,WorkSite,RelaySite,AvoidSite,DockSite}`、`MapSite` 驗證 ctor、`RoadmapGraph.{Vertices,Neighbours,ShortestPath,DistanceTo,HasSite}` | `Roadmap` 聚合不變條件、`RoadmapGraph.Build` |
| **TrafficControl** | `IStationResourceCalendar`（app 層便利封裝）+ blockingClosure 經 `MapResourceTopologyAdapter` 接成 `Zone` 閉包 | **長租約 + 閉包擴張 + 衝突偵測全部現成**（`ReservationTable.TryGrant`/`ReleaseBehind`/`FreeIntervals`、`TimeInterval` 任意 ms、`ResourceKind.Zone` 已存在） | `ITrafficCoordinatorAppService`、`IReservationView`、`AllocationOutcome`、`LeaseState`、`IResourceTopology.ClosureOf` 語義、Kernel 詞彙 |
| **Kernel** | 見 **ADR-F2**：建議**不改**（站點用 CP lease + Zone 閉包模型）；若採顯式 `ResourceKind.Station` 則為唯一改動 | `ResourceRef/ResourceKind{CP,Lane,Block,Zone}/TimeInterval/SpaceTimePath/IReservationView` | 整個 Kernel（跨上下文凍結契約） |
| **Coordination** | `FleetCoordinationLoop.RunOnceAsync` 插入 admission：goals → `IDockAdmissionController` 過濾 goals + 算 `blockedResources` → `RunCycleAsync(admitted, blocked)` | **`IFleetCoordinationCycle.RunCycleAsync` 已有 `blockedResources` 參數**（admission 的天然 hook）、`CoordinationCycleService` 已處理 null | `IFleetCoordinationCycle` 所有簽章、`CoordinationCycleService`、`RunCycleAsync` 在 blocked 空時的行為 |
| **Liveness** | `MobilityClass` 閘：`ParkedRelocationSelector.Select` + `LivenessPolicy`(ClusterFormation/Advance) 跳過 `InService` 車；`ParkingManager` 用 persistent relocation（改 `RelocateParked` 的恢復條件，不是 20-tick 必回） | `RelocateParked`/`RestoreGoal` directive、`ParkedRelocationSelector`、`LivenessPolicy` 4-phase、`LivenessOptions` | PIBT/CBS resolver 本體、`StuckClusterDetector`（已正確排除 parked blocker） |
| **Simulation** | `ScenarioMode` 新軸（RandomStress/WarehouseWellFormed/LifelongDispatch）+ `ArrivalPolicy`(Disappear/PermanentPark/ClearToParking)；executor 接 `AgvMissionState`（到 Workstation≠Done→service→去 parking）、`NonConvergenceReason` 接 DidNotConverge 路徑；lifelong 任務流 runner + per-mode metrics | `SimulationRequest{…,Scenario,Assignment,EmitTrace}`、`ScenarioKind{Open,Bottleneck,Obstacles}`、`AssignmentPolicy`、`FleetLoopRun`(`_parkedCells`/`RunAgent`/`AgentMotionState`)、`SimulationMetricsCalculator` | 離散/連續 executor 在 FMS off 時的逐位元行為（回歸鎖） |
| **Host** | 註冊新 `Dispatch` 上下文（`AddCoordination` 之後、`AddSimulation` 之前）；`IDockAdmissionController` 注入 loop | `Program.cs` 註冊順序、`MapResourceTopologyAdapter`、`AddSwarmRouteDispatcher`(opt-in) | 既有註冊順序與 DB-less 預設 |
| **Frontend** | `ScenarioMode` 切換、station/PreDockBuffer/DockPoint/Parking 渲染、`MissionState`/`MobilityClass` 著色、admission/clearance 視覺化、per-mode 指標面板 | `ControlRail`/`FieldCanvas`/`types`/`store`、ScenarioKind/heatmap 既有渲染 | 既有 toggle/playback |

### 2.3 軸線釐清（避免和剛落地的 ScenarioBench 撞名）

代碼**已有**兩個軸，FMS 的 `ScenarioMode` 是**第三個**正交軸：

| 軸 | 列舉 | 管什麼 | 狀態 |
|---|---|---|---|
| 地圖佈局 | `ScenarioKind {Open,Bottleneck,Obstacles}` | 障礙/牆/貨架 | 已存在（v4 ScenarioBench） |
| 起終點分派 | `AssignmentPolicy {Random,…}` | start/goal 怎麼抽 | 已存在（你剛加） |
| **任務生命週期** | **`ScenarioMode {RandomStress,WarehouseWellFormed,LifelongDispatch}`** | 到達語義/清場/任務流/驗收口徑 | **新增（FMS）** |

`WellFormedEndpointGenerator` 實作為 `AssignmentPolicy.WellFormed`（新值），由 `ScenarioMode` 編排；`ArrivalPolicy` 控制到達後行為（壓測可 `Disappear`、真實用 `ClearToParking`）。

---

## 3. 契約優先（Sprint-F1 凍結）

並行關鍵：S-F1 先把以下型別/介面**定義、凍結**，各 squad 對介面開發。任何變更走 ADR + TL 核可。

### 3.1 共享型別（Dispatch.Domain.Shared / Map.Domain.Shared）

| 型別 | 位置 | 摘要 |
|---|---|---|
| `enum SiteRole` | `Map.Domain.Shared`（ADR-F1） | `Transit/Workstation/Parking/Charger/Buffer/PreDockBuffer/DockPoint` |
| `enum MobilityClass` | `Dispatch.Domain.Shared` | `Movable/MovableWithCost/ImmovableUntilServiceComplete/Faulted`（只有前二者可被 relocation） |
| `enum AgvMissionState` | `Dispatch.Domain.Shared` | `Idle/MovingToPreDockBuffer/WaitingDockAdmission/Docking/InService/Undocking/MovingToNextTask/MovingToParking/IdleParked/Faulted` |
| `enum ScenarioMode` | `Simulation.Application` | `RandomStress/WarehouseWellFormed/LifelongDispatch` |
| `enum ArrivalPolicy` | `Simulation.Application` | `Disappear/PermanentPark/ClearToParking` |
| `enum NonConvergenceReason` | `Simulation.Application` | `TickBudgetExceeded/ParkedGoalBlocker/NoWellFormedEndpointPath/LiveStandoffUnresolved/ParkingSaturation/SolverTimeout/Unknown` |
| `enum StationType` | `Dispatch.Domain.Shared` | `NonBlocking/SoftBlocking/HardBlocking` |
| `record StationDefinition` | `Dispatch.Domain` | `StationId, DockPoint, PreDockBuffers[], BlockingClosure(IReadOnlySet<ResourceRef>), ServiceDurationMs, StationType` |
| `record EndpointSet` | `Dispatch.Domain` | `Workstations, Parkings, Buffers, Chargers` |
| `record ServiceAdmissionRequest` | `Dispatch.Domain` | `AgentId, StationId, PreDockBuffer, DockPoint, ServiceDurationMs, Priority, EarliestStartMs, DeadlineMs?` |
| `record ServiceAdmissionDecision` | `Dispatch.Domain` | `Granted, ServiceStartMs?, Reason, VehiclesToClearFirst[]` |
| `record TrafficImpact` | `Dispatch.Domain` | `AffectedAgentIds[], BlocksTransitCore, HasBypass, EstWaitTicks` |

### 3.2 介面（凍結簽章）

| 介面 | 擁有者 | 位置 | 消費者 |
|---|---|---|---|
| `IEndpointPolicy.BuildEndpoints(graph, agvCount, seed) → EndpointSet` | Map | Map.Domain | Simulation 生成器 |
| `IDockAdmissionController.EvaluateAdmissionAsync(roadmapId, goals, ct) → (admittedGoals, blockedResources)` | FMS 平台 | Dispatch.App.Contract | Coordination loop |
| `IStationScheduler.RequestDockAdmissionAsync(ServiceAdmissionRequest) → ServiceAdmissionDecision` | FMS 平台 | Dispatch.App.Contract | DockAdmissionController |
| `IStationResourceCalendar.{CanReserveServiceWindow, ReserveServiceWindowAsync, FreeWindows}` | TrafficControl | Dispatch.App.Contract（impl 在 TC 之上） | StationScheduler |
| `ITrafficImpactAnalyzer.AnalyzeBlockingImpact(blockingClosure, serviceWindow) → TrafficImpact` | FMS 平台 | Dispatch.Domain | StationScheduler |
| `ITaskDispatcher.AssignNextAsync(pending, idleVehicles, ct) → AgentGoal?` | FMS 平台 | Dispatch.App.Contract | Coordination/Dispatcher |
| `IParkingManager.{AssignParking, FindRelocationsForWalledAgent}` | 執行層 | Dispatch.Domain | Liveness/executor |
| `IRelocationClusterSolver.Solve(walled, blockers, buffers, blockers') → relocations` | 執行層 | Dispatch.Domain | ParkingManager（V3） |

### 3.3 決策記錄（ADR，S-F1 產出）

- **ADR-F1 — SiteRole 落點**：放 `Map.Domain.Shared`，`MapSite` 加 optional ctor 參數預設 `Transit`（向後相容、聚合不變條件不破、EF 加 string 欄位 + 預設值 migration）。✅ 建議。
- **ADR-F2 — 站點資源建模（關鍵）**：**建議「不改 Kernel」**——工位作業 = 一筆 `ResourceKind.CP`(DockPoint) 長租約 + 一筆 `ResourceKind.Zone`(blockingClosure) 長租約，經既有 `ReservationTable.TryGrant`/閉包達成（探勘證實**零 domain 改動**）。`ResourceKind.Station` 列為「日後若日曆/可觀測性需要顯式型別時再加（純 append、低風險）」的備案，V1–V3 不採。
- **ADR-F3 — 准入插入點**：`FleetCoordinationLoop.RunOnceAsync` 在 `RunCycleAsync` 前呼叫 `IDockAdmissionController`，透過**既有** `blockedResources` 參數封站；admission off 時回 `(原 goals, 空 blocked)` ⇒ byte-identical。
- **ADR-F4 — ScenarioMode 為第三正交軸**（見 §2.3），不動 `ScenarioKind`/`AssignmentPolicy`。
- **ADR-F5 — InService 不可移動**：`MobilityClass.ImmovableUntilServiceComplete` 在 `ParkedRelocationSelector` 與 `LivenessPolicy`(ClusterFormation/Advance) 一律跳過；executor 把 InService 車視為「硬障礙 + 長租約」。

---

## 4. 團隊結構與 RACI

建議 **1 TL + 6 squad + 1 QA**（4–8 人伸縮）。folder 即 owner（CODEOWNERS）。**核可後每個 squad 對應一組 worktree 隔離的執行 agent（見 §11）。**

| 角色 | 擁有 folder / 職責 |
|---|---|
| **TL / 架構師** | 契約凍結、ADR、`Dispatch` 上下文架構、frozen-seam 守門、最終 review |
| **FMS 平台** | `Dispatch/`：`DockAdmissionController`/`StationScheduler`/`TaskDispatcher`/`TrafficImpactAnalyzer`、cost-based admission |
| **Map / 拓撲** | `Map/`：`SiteRole`、`IEndpointPolicy`/`WellFormedEndpointGenerator`、站點 layout、egress/articulation 檢查 |
| **TrafficControl** | `TrafficControl/` + `MapResourceTopologyAdapter`：`IStationResourceCalendar`、blockingClosure→Zone 閉包接線、長租約驗證 |
| **執行層 / Liveness** | `Liveness/` + `Simulation/.../FleetLoopRun*`：`AgvMissionState` 接入 executor、`MobilityClass` 閘、`ParkingManager` persistent relocation、`IRelocationClusterSolver` |
| **Simulation / 場景** | `Simulation/`：`ScenarioMode`/`ArrivalPolicy`、生成器接線、`NonConvergenceReason` 分類、lifelong runner、per-mode metrics |
| **前端** | `host/swarmroute-web/`：ScenarioMode 切換、站點/buffer/dock 渲染、MissionState/MobilityClass 著色、admission/clearance 視覺化、per-mode 指標 |
| **QA** | 三模式驗收套件、安全不變條件回歸、byte-identical 回歸鎖 |

### RACI（工作流 × 角色）

| 工作流 | TL | FMS平台 | Map | TC | 執行/Liveness | Sim | FE | QA |
|---|:--:|:--:|:--:|:--:|:--:|:--:|:--:|:--:|
| WS-F0 契約/ADR | **A/R** | C | C | C | C | C | C | I |
| WS-F1 SiteRole+Endpoint | C | C | **A/R** | I | I | C | I | C |
| WS-F2 Station 租約+Calendar | A | C | C | **A/R** | I | I | I | C |
| WS-F3 Dispatch 上下文 | A | **A/R** | I | C | C | C | I | C |
| WS-F4 MissionState+Mobility+Parking | A | C | I | C | **A/R** | C | I | C |
| WS-F5 ScenarioMode+診斷+指標 | C | C | C | I | C | **A/R** | I | C |
| WS-F6 前端 | C | I | I | I | I | C | **A/R** | C |
| WS-FQ 場景/驗收 | C | I | C | C | C | C | I | **A/R** |
| WS-FX 整合/Host | **A/R** | R | C | C | C | C | C | C |

---

## 5. 工作流（Epics）

| ID | 工作流 | 版本 | Owner | 主要依賴 | DoD 摘要 |
|---|---|---|---|---|---|
| **WS-F0** | 契約 + ADR | V1 | TL | — | §3 型別/介面凍結、ADR-F1..F5 入 `docs/adr/` |
| **WS-F1** | Map SiteRole + Endpoint | V1→V2 | Map | WS-F0 | `SiteRole` + migration；`WellFormedEndpointGenerator` 通過連通/egress 單測 |
| **WS-F2** | TrafficControl Station 租約 + Calendar | V1 | TC | WS-F0 | 長租約 + Zone blockingClosure 衝突偵測；`IStationResourceCalendar` 單測；釋放無洩漏 |
| **WS-F3** | Dispatch 上下文（Admission/Scheduler/TaskDispatcher/ImpactAnalyzer） | V1→V3 | FMS平台 | WS-F0,F2 | 最小 DockAdmission（V1）→ impact 分析（V2）→ cost-based + lifelong（V3） |
| **WS-F4** | MissionState + MobilityClass + ParkingManager | V1→V2 | 執行/Liveness | WS-F0,F1 | executor 接 MissionState；InService 不被挪（單測）；persistent relocation 取代 20-tick |
| **WS-F5** | ScenarioMode + NonConvergenceReason + per-mode 指標 | V2→V3 | Sim | WS-F1,F3,F4 | 三模式可跑；DidNotConverge 自動歸因；lifelong throughput 指標 |
| **WS-F6** | 前端 | V1→V3 | FE | WS-F3,F5 | 站點/buffer/dock 渲染、MissionState 著色、admission/clearance 視覺化、per-mode 面板 |
| **WS-FQ** | 場景/驗收套件 | 全程 | QA | WS-F0 | 三模式驗收、I1–I8 + InService 不變條件、byte-identical 回歸鎖 |
| **WS-FX** | 整合 + Host 接線 | 收尾 | 全體 | F1–F6 | Host 註冊 Dispatch、loop 接 admission、端到端三模式可跑 |

---

## 6. Sprint 排程（2 週/Sprint）

```
        S-F1     S-F2       S-F3        S-F4        S-F5        S-F6
        契約     V1 核心    V1 收尾     V2          V2→V3       V3 收尾
TL/FMS  F0 ───   F3a ────   F3a/FX ──   F3b ────    F3c ────    FX
Map              F1 ───     F1(endpt)   F1(well)
TC               F2 ───     F2 ──
執行/Live        F4 ───     F4 ──       F4(parking) F4(reloc)
Sim                                     F5 ───      F5(lifelong)
FE                          F6a         F6b ───     F6c ───     F6
QA               FQ ──      FQ ──       FQ ──       FQ ──       FQ
里程碑                      ▲M-F1                   ▲M-F2       ▲M-F3
```

| Sprint | 重點 | Exit Criteria |
|---|---|---|
| **S-F1** 契約 | WS-F0 + F1/F2/F4 起步 | §3 凍結；ADR 入庫；`SiteRole`/`MobilityClass`/`AgvMissionState` 編譯綠 |
| **S-F2** V1 核心 | F1 endpoint、F2 租約、F4 MissionState、F3a 最小 Admission | Station 長租約單測；executor MissionState 接入；InService 不被挪 |
| **S-F3** V1 收尾 | F3a 接 loop、F6a、FX | **M-F1**：Dock 准入貫通；off=byte-identical |
| **S-F4** V2 | F3b impact、F4 ParkingManager、F5 ScenarioMode、F6b | clearance-before-service；WarehouseWellFormed 跑通 |
| **S-F5** V2→V3 | F5 診斷+指標、F3c cost-based | **M-F2**：WarehouseWellFormed 高完成率 + NonConvergenceReason |
| **S-F6** V3 | F3c lifelong、F6c、FX、FQ | **M-F3**：Lifelong throughput + cost-based admission |

**關鍵路徑**：F0 → F2（租約）/F1（endpoint）→ F3（admission）→ F4（executor）→ F5（mode）→ FX。
**並行**：F1/F2/F4-infra 在 F0 後即可並行；F6 對 stub 從 S-F3 起。

---

## 7. 任務卡背包（Backlog，估算＝工程師-日 ED）

> 每卡隱含「單元測試 + CI 綠 + review + opt-in 預設關 byte-identical 回歸鎖」。檔案座標取自探勘結果。

### WS-F0 — 契約 + ADR（≈5 ED）
| 卡 | 任務 | ED | 驗收 |
|---|---|:--:|---|
| F0-1 | 建 `SwarmRoute.Dispatch.*` 專案骨架（Domain.Shared/Domain/App.Contract/App/IoC/Tests）入 sln | 1 | 空專案編譯綠 |
| F0-2 | §3.1 列舉 + §3.2 介面簽章（無實作）凍結 | 2 | 編譯綠；TL 簽核 |
| F0-3 | ADR-F1..F5 入 `docs/adr/` | 1 | 入庫 |
| F0-4 | `MobilityClass`/`AgvMissionState` 加進 `AgentGoal`(+欄位預設 Movable/Idle) 與 `RunAgent`，全預設安全值 | 1 | 既有測試全綠（additive） |

### WS-F1 — Map SiteRole + Endpoint（≈11 ED）
| 卡 | 任務 | ED | 座標/驗收 |
|---|---|:--:|---|
| F1-1 | `SiteRole` 列舉 | 1 | `Map.Domain.Shared/Enums/SiteRole.cs` |
| F1-2 | `MapSite` 加 `SiteRole`（optional ctor，private set） | 1 | `Map.Domain/Entities/MapSite.cs:34`；既有 ctor 呼叫不破 |
| F1-3 | EF 設定 + migration（string 欄位、預設 Transit） | 2 | `Map.Infra.Data/Context/MapDbContext.cs` ConfigureSite；migration 套用 |
| F1-4 | DTO + `RoadmapFactory` 映射（nullable→Transit） | 1 | API 向後相容 |
| F1-5 | `IEndpointPolicy` + `WellFormedEndpointGenerator`（TransitCore 連通、egress、endpoint-to-endpoint 可達、不放 articulation） | 4 | 用 `RoadmapGraph.{Vertices,Neighbours,ShortestPath}`；7×7 layout 單測（角落 egress 檢查） |
| F1-6 | `IsValidEndpointSet` 連通/egress 檢查 | 2 | 偽碼對拍；反例（封死 TransitCore）被拒 |

### WS-F2 — TrafficControl Station 租約 + Calendar（≈10 ED）
| 卡 | 任務 | ED | 座標/驗收 |
|---|---|:--:|---|
| F2-1 | blockingClosure → `Zone` 閉包接線（`MapResourceTopologyAdapter.FromRoadmap`：DockPoint→blockingClosure Zone 成員） | 3 | `host/.../Adapters/MapResourceTopologyAdapter.cs:114`；reserve DockPoint 自動鎖 Zone |
| F2-2 | `IStationResourceCalendar` + impl（封裝 `IReservationView.FreeIntervals` + `TryReserveAsync`，長租約 `[t,t+serviceMs)`） | 3 | `Dispatch.App.Contract` + impl 在 TC 之上；單測：1 小時租約 grant/conflict/free-window |
| F2-3 | 長租約衝突/釋放回歸（service 結束 `ReleaseBehind` 含 Zone 閉包、無洩漏） | 2 | `ReservationTable` 既有閉包對稱釋放；無洩漏單測 |
| F2-4 | （選）啟用 `IWouldCloseCycleDetector` 防 AGV↔station 環 | 2 | v2 detector 接線；seeded 環被 avert |

### WS-F3 — Dispatch 上下文（≈22 ED，跨 V1–V3）
| 卡 | 任務 | ED | 版本/驗收 |
|---|---|:--:|---|
| F3-1 | 最小 `IDockAdmissionController`（blockingClosure 上有近期車流→hold buffer；否則 grant docking+service） | 4 | V1；回 `(admittedGoals, blockedResources)`；off=空 |
| F3-2 | `IStationScheduler.RequestDockAdmissionAsync`（station 空閒 + closure 可預約→grant，否則 deny+reason） | 3 | V1；`ServiceAdmissionDecision` 單測 |
| F3-3 | `ITrafficImpactAnalyzer`（affected vehicles = planned path 用到 blockingClosure 的車；BlocksTransitCore；HasBypass） | 4 | V2；對拍 RoadmapGraph 可達/繞行 |
| F3-4 | clearance-before-service 批次（先放 affected 車過、再 grant） + priority-aware | 3 | V2；M-F2 場景：後車先過再開工 |
| F3-5 | `ITaskDispatcher`（接管 `DispatcherService` nearest-idle，可插拔：priority/deadline/capability） | 3 | V3；建構於 `OrderBook`/`VehicleRegistry`；不破既有 dispatcher 測 |
| F3-6 | cost-based admission（`score = servicePriority·urgency − blockedCount·penalty − highPrioBlocked·penalty − noBypassPenalty`） | 3 | V3；CNC-快停機 vs 後車空等位 決策單測 |
| F3-7 | 站點三型（Non/Soft/Hard-blocking）策略分流 | 2 | V3；Hard-blocking → 作業期間通道視為關閉 |

### WS-F4 — MissionState + MobilityClass + ParkingManager（≈16 ED）
| 卡 | 任務 | ED | 座標/驗收 |
|---|---|:--:|---|
| F4-1 | executor 接 `AgvMissionState`：到 Workstation→`ServicingAtTask`(serviceTicks)→`NeedsClearance`（**不** `_parkedCells.Add`）；到 Parking→`IdleParked`+park | 4 | `FleetLoopRun.Discrete.cs:453-496` 到達臂；單測：任務點不成永久障礙 |
| F4-2 | `MobilityClass` 閘：`ParkedRelocationSelector.Select` + `LivenessPolicy`(ClusterFormation/Advance) 跳過 `ImmovableUntilServiceComplete` | 3 | `Liveness.Domain/Resolution/ParkedRelocationSelector.cs:48`；InService 永不被選為 relocation/PIBT/CBS 成員（單測） |
| F4-3 | `IParkingManager.AssignParking`（任務完成派最近非阻塞 parking/buffer） | 3 | `Dispatch.Domain`；service 完→去 parking 單測 |
| F4-4 | persistent relocation：改 `RelocateParked` 恢復條件（corridor clear 才回，非 20-tick 必回） | 3 | `LivenessOptions.GatekeeperYieldWindow` 改為「最短保持讓路」；「讓一下又回堵」消失 |
| F4-5 | `IRelocationClusterSolver`（greedy：walled→goal 最短路上 parked blockers→各派最近 buffer→walled 重試） | 3 | V3；多 parked 圍堵案例解開 |

### WS-F5 — ScenarioMode + 診斷 + 指標（≈12 ED）
| 卡 | 任務 | ED | 座標/驗收 |
|---|---|:--:|---|
| F5-1 | `ScenarioMode` + `ArrivalPolicy` + `SimulationRequest` 欄位（預設 RandomStress/現行為） | 2 | additive；off=byte-identical |
| F5-2 | `WarehouseWellFormed` 生成（endpoint-only goals、starts 從 transit/parking、完成清場） | 3 | 接 `WellFormedEndpointGenerator`；7×7 layout |
| F5-3 | `NonConvergenceReason` 分類器（graph−parkedCells 上 goal 可達性 → ParkedGoalBlocker/NoWellFormedEndpointPath/LiveStandoffUnresolved/ParkingSaturation） | 3 | 接 DidNotConverge 路徑（`SimulationService.cs:123`）；每台未到車有 reason |
| F5-4 | `LifelongDispatch` runner（無「全到固定終點」結束條件；每完成一單派下一單） | 2 | V3；接 `ITaskDispatcher` |
| F5-5 | per-mode 指標（throughput/P95 wait/queue length/parking saturation/deadlock rate） | 2 | 擴 `SimulationMetricsCalculator`；lifelong 指標 |

### WS-F6 — 前端（≈10 ED）
| 卡 | 任務 | ED | 驗收 |
|---|---|:--:|---|
| F6-1 | `ScenarioMode` 切換（Segmented）+ types/store/ControlRail | 2 | 三模式可選 |
| F6-2 | 站點渲染：Workstation/Parking/Buffer/PreDockBuffer/DockPoint 圖示 + SiteRole 著色 | 3 | `FieldCanvas` |
| F6-3 | MissionState/MobilityClass 著色（InService 醒目、不可挪標記） | 2 | 車輛狀態可視 |
| F6-4 | admission/clearance 視覺化（hold-in-buffer、先讓誰過、service 倒數）+ per-mode 指標面板 | 3 | M-F1/F2/F3 demo 可看 |

### WS-FQ — 場景/驗收（≈12 ED，全程）
| 卡 | 任務 | ED | 驗收 |
|---|---|:--:|---|
| FQ-1 | byte-identical 回歸鎖（每個 FMS 開關 off → 與當前逐位元一致） | 3 | golden-master |
| FQ-2 | InService 安全不變條件（InService 車絕不被 StepAside/PIBT/CBS/重規劃挪走；其 blockingClosure 全程被尊重） | 3 | 專測 |
| FQ-3 | M-F1 Dock 准入端到端（hold→clear→grant→service→0 碰撞） | 2 | 整合測 |
| FQ-4 | M-F2 WarehouseWellFormed 完成率 + reason 分佈（ParkedGoalBlocker 顯著下降） | 2 | 整合測 |
| FQ-5 | M-F3 LifelongDispatch throughput 穩定 + cost-based 讓行/搶先正確 | 2 | 整合測 |

### WS-FX — 整合 + Host（≈6 ED）
| 卡 | 任務 | ED | 驗收 |
|---|---|:--:|---|
| FX-1 | Host 註冊 `Dispatch` 上下文（`Program.cs` `AddCoordination` 後、`AddSimulation` 前） | 2 | `Dispatch.Infra.CrossCutting.IoC.AddDispatch()` |
| FX-2 | `FleetCoordinationLoop.RunOnceAsync` 接 admission（goals→controller→`RunCycleAsync(admitted,blocked)`） | 2 | `FleetCoordinationLoop.cs:115-126`；off=byte-identical |
| FX-3 | sim path 同樣接 admission（`SimulationService`→`FleetLoopRun` 經 cycle） | 2 | 三模式端到端 |

**估算合計 ≈ 104 ED**（不含 review/緩衝）。

---

## 8. 工程流程與規範

- **分支**：單一 `main`（per [[main-only-branching]]）；多代理執行時每 agent 一個 **git worktree**（§11），完成 green-gate 後逐一 merge 回 main。
- **每卡 DoD**：程式 + 單測 + CI 綠 + review + XML 註解 + 零新警告 + 符合 grukirbs 戰術慣例 + **opt-in 預設關 byte-identical**（沿用 v2/v3 紀律）。
- **凍結接縫**（§2.2 最右欄）任何改動須 ADR + TL 核可。
- **測試**：單元（各上下文）｜整合（三模式閉環）｜安全不變條件（I1–I8 + InService 不可移動）｜場景對拍。
- **CI**：`build -c Release`(net10) → test → `dotnet format` → 前端 tsc+lint。

---

## 9. 風險登記

| ID | 風險 | 等級 | 緩解 |
|---|---|:--:|---|
| RF1 | Hard-blocking 工位封死唯一通道 1 小時——非 planner 能解 | 高 | 文檔已明示：layout/dispatch 問題；策略=作業前清場+通道視為關閉+依賴該通道任務禁入；提供 bypass/buffer 建議（產線設計） |
| RF2 | 多代理並行改同檔（你正在 live-edit Simulation） | 中 | worktree 隔離 + green-gate 串行 merge；F4/F5 對 `FleetLoopRun*` 改動由**單一** squad 擁有，避免雙寫 |
| RF3 | Kernel 被迫改（ResourceKind.Station） | 低 | ADR-F2 採 CP+Zone 模型，Kernel 不改 |
| RF4 | admission 引入新 livelock（hold→放→再 hold 震盪） | 中 | clearance 條件用「corridor clear」非倒數；`NonConvergenceReason` 監測；FQ livelock 專測 |
| RF5 | 長租約讓 `ReservationTable` 變爭用瓶頸 | 低 | 探勘證實長租約現成；規模化按 zone 分片（既有 R4 緩解路線） |
| RF6 | ScenarioMode 與剛落地 ScenarioBench/AssignmentPolicy 撞語義 | 中 | §2.3 三正交軸；ADR-F4 |

---

## 10. 驗收標準（三模式，承 `工業級 FMS.md`）

**RandomStress**（保留現行）：0 碰撞、不崩、DidNotConverge 可重現且有 reason、v3 resolver 提升 arrived（不要求 100%）。
**WarehouseWellFormed**（7×7/16）：0 碰撞、大部分 seed `Completed`、殘餘 DidNotConverge 主要為 `ParkingSaturation`/`TickBudgetExceeded`、`ParkedGoalBlocker` 顯著下降。
**LifelongDispatch**：throughput 穩定、parking/buffer 不飽和、P95 wait 可控、無永久堵塞、cost-based admission 決策正確。

---

## 11. 多代理執行映射（核可後）

核可後以 **Workflow** 執行；每 squad = 一組 worktree 隔離 agent，依賴定序、每階段 green-gate 後 merge。

```
Phase 0  契約凍結（1 agent）            : WS-F0 → 產出 §3 型別/介面/ADR（gate：編譯綠 + TL 簽核）
Phase 1  並行地基（worktree 隔離）      : WS-F1(Map) ∥ WS-F2(TC) ∥ WS-F4-infra(MissionState/Mobility)
Phase 2  FMS 平台                      : WS-F3(Dispatch：V1 admission)  ← 依賴 F1/F2
Phase 3  場景 + 執行整合               : WS-F4(Parking) ∥ WS-F5(ScenarioMode/診斷)
Phase 4  前端 + Host 整合              : WS-F6 ∥ WS-FX
Phase 5  驗收 + 對抗式驗證             : WS-FQ（三模式整合測 + InService 不變條件 + byte-identical 回歸鎖；
                                          對每個「修好」宣稱派獨立 skeptic agent 復驗）
```

- 每階段結束跑全套 + 三模式煙霧；綠才 merge worktree。
- V1/V2/V3 可分三輪 Workflow（一輪一版），讓你在每版之間 review。
- 規模可隨 budget 調整（finder/verifier 數）。

---

## 12. 待你拍板的小決策

1. **版本命名**：本 FMS 軌要叫 **v5（Warehouse Scenario & Dock Admission）** 還是沿用文檔的 **v3.6 / v4.0**？（v4 已是 SwarmRoute Lab，建議命名 v5 以免撞號。）
2. **首輪範圍**：核可後第一輪 Workflow 是只做 **FMS-V1**（最小可用，最快看到 Dock 准入效果），還是 **一次跑完三版**？（建議 V1 先行、逐版 review。）
3. **ADR-F2**：station 採「CP+Zone 閉包（不改 Kernel）」還是顯式 `ResourceKind.Station`？（建議前者。）
