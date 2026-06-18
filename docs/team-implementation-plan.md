# SwarmRoute MAPF — 團隊實現計劃（Team Implementation Plan）

> 目標框架：**.NET 10 (LTS, C# 14)** ｜ 範圍：**首個里程碑 = 腳手架 + v0 基線（Phase 0–4）** ｜ 設計依據：[`architecture-design.md`](./architecture-design.md)
>
> 本文是「**怎麼做、誰做、何時做、做到什麼算完成**」。架構「做什麼」見設計文件。

---

## 1. 目標與交付定義

**本計劃交付**：一套可編譯、可執行、跑在 **.NET 10** 上的 4 上下文 DDD 方案 `SwarmRoute.Mapf.sln`，並把現有引擎移植成 **v0 多機基線**（剪枝 Dijkstra 規劃 + 整路上鎖交管 + RAG 死鎖偵測與最小恢復）。v1–v3（時空預約/SIPP、優先級/RHCR、CBS/PIBT）為後續里程碑，不在本計劃範圍但介面已預留。

**里程碑**

| # | 名稱 | Sprint | 驗收（Demo） |
|---|---|---|---|
| **M1** | 架構貫通切片 | S3 末 | HTTP 匯入拓撲 → 單機 A→B 規劃出路徑（拓撲進、路徑出） |
| **M2** | 多機不碰撞 | S5 末 | 多台車交叉路線經預約序列化、不碰撞、通過後資源無洩漏釋放 |
| **M3** | v0 基線 GA | S6 末 | 偵測 2/3/4 車死鎖環並解除恢復；全場景測試綠；Postgres+RabbitMQ 上端到端可跑 |

**完成定義（v0 里程碑 DoD）** 見 §10。

---

## 2. 團隊結構與職責

建議核心團隊 **5–6 名工程師 + 1 Tech Lead + 1 QA**（可在 4–8 人間伸縮，見 §11）。依限界上下文編成 squad，**folder 即 owner**（透過 CODEOWNERS）。

| 角色 | 人數 | 擁有的 folder / 職責 |
|---|:--:|---|
| **Tech Lead / 架構師 (TL)** | 1 | `Shared/SpatioTemporal.Kernel`、`Coordination/`、`Host/`、跨上下文契約、ADR、最終 review |
| **平台工程 (Platform)** | 1 | `Shared/`（BaseDbContext、EventBus、BgJobs.Core、StateMachine.Core）、`lib/NetDevPack`（ProjectReference）、`third-party/algorithms` vendoring、**CI/CD**、Docker Compose |
| **領域工程 A — Map** | 1 | `Map/`（拓撲、圖、干涉、匯入） |
| **領域工程 B — TrafficControl** | 1 | `TrafficControl/`（預約表、配置、衝突、租約 job）— 最重 |
| **演算法工程 — Planning/Deadlock** | 1（強）或 2 | `PathPlanning/`、`Deadlock/`（Dijkstra→SIPP、RAG 偵測、避讓恢復） |
| **QA / 測試工程** | 1 | 測試框架、模擬時鐘、安全不變條件場景庫，跨所有上下文 |

> **Squad 編組**：Foundation（TL+Platform）｜Map（Dom A）｜Traffic（Dom B）｜Planning（Algo）。QA 橫跨。Deadlock 在 v0 階段由 Algo + Traffic 協作。

### RACI（工作流 × 角色）

| 工作流 | TL | Platform | Map | Traffic | Algo | QA |
|---|:--:|:--:|:--:|:--:|:--:|:--:|
| WS0 基礎/DevEx | A | R | C | C | C | C |
| WS-K Kernel 契約 | **A/R** | C | C | C | C | I |
| WS1 Map | C | C | **A/R** | I | C | C |
| WS2 PathPlanning | C | I | C | C | **A/R** | C |
| WS3 Coordination/Host | **A/R** | R | I | C | C | C |
| WS4 TrafficControl | A | C | C | **A/R** | C | C |
| WS5 Deadlock | A | I | I | C | **R** | C |
| WS-Q 測試/場景 | C | I | C | C | C | **A/R** |

（A=當責 R=負責 C=諮詢 I=知會）

---

## 3. 跨團隊契約（Contract-First，**Sprint 1 凍結**）

並行開發的關鍵：在 S1 把以下介面/型別簽章**先定義、凍結**，各 squad 對著介面開發、用 stub/fake 解耦。任何契約變更須走 ADR + TL 核可 + 知會所有 squad。

| 契約 | 擁有者 | 所在 | 消費者 |
|---|---|---|---|
| `ResourceRef` / `TimeInterval` / `SpaceTimeCell` / `SpaceTimePath` / `SafeInterval` | TL | `SpatioTemporal.Kernel` | 全體 |
| `IReadOnlyReservationView` / `IReservationQuery` | TL（PathPlanning 宣告，Traffic 實作） | Kernel + PathPlanning.Domain | PathPlanning↔TrafficControl |
| `IRoadmapQueryService`（回 `RoadmapGraph`） | Map | Map.Application.Contract | Planning / Coordination |
| `ITrafficCoordinatorAppService.TryReserve/Release` → `AllocationOutcome` | Traffic | TrafficControl.App.Contract | Coordination |
| `ResourceAllocationGraphSnapshot` / `ITrafficControlSnapshotProvider` | Traffic | Kernel + Traffic | Deadlock |
| 整合事件名 `Map.Roadmap.Published`、`TrafficControl.Allocation.Contended`、`Deadlock.Case.ResolutionRequested`（+ payload DTO/版本） | TL | EventBus 契約 | 訂閱方 |
| `BaseDbContext` / `IBaseRepository<T>` / 事件分發 | Platform | `Infra.Data.Core` / `Domain.Abstractions` | Map / Traffic |

---

## 4. 工作流（Epics）

| ID | 工作流 | 對應 Phase | Owner | 主要依賴 | 完成定義（DoD 摘要） |
|---|---|---|---|---|---|
| **WS0** | 基礎設施與 DevEx | Phase 0 | Platform/TL | — | 方案編譯綠燈、CI 綠、NetDevPack 內嵌、algorithms retarget net10 |
| **WS-K** | Kernel 契約 | Phase 0 | TL | WS0 | §3 契約全數合併、凍結、有 ADR |
| **WS1** | Map 上下文 | Phase 1 | Map | WS-K | 匯入拓撲、`RoadmapGraph` 查詢、`IRoadmapQueryService` 上線 |
| **WS2** | PathPlanning v0 | Phase 2 | Algo | WS-K（可先 stub）→ WS1 | `DijkstraPathPlanner` 正確、`PlanFor` 端到端 |
| **WS3** | Coordination + Host | Phase 2 | TL/Platform | WS1, WS2 | 單機 E2E（M1）；Host 串接、in-memory CAP 可跑 |
| **WS4** | TrafficControl v0 | Phase 3 | Traffic | WS-K, WS1 | `ReservationTable` + 配置/釋放、`ReservationService`、多機不碰撞（M2） |
| **WS5** | Deadlock v0 | Phase 4 | Algo/Deadlock | WS4 | RAG 偵測 + `AvoidancePlan` 恢復（M3） |
| **WS-Q** | 測試與場景庫 | 全程 | QA | WS-K | 模擬時鐘、安全不變條件套件、回歸 |
| **WS-X** | 整合與固化 | Phase 4 末 | 全體 | WS1–WS5 | Postgres+RabbitMQ 端到端、可觀測性、文件 |

---

## 5. Sprint 排程（2 週/Sprint，6 Sprint ≈ 12 週）

```
        W1-2     W3-4     W5-6     W7-8     W9-10    W11-12
        S1       S2       S3       S4       S5       S6
Found.  WS0/WS-K ───────  WS3 ───  ──────   WS3 ───  WS-X
Map              WS1 ───  WS1(fix) (status 拆分)
Planning         WS2(stub)WS2 ───                    WS5 ───
Traffic                            WS4 ───  WS4 ───  WS-X
QA                        WS-Q ──  WS-Q ──  WS-Q ──  WS-Q
里程碑                     ▲M1               ▲M2      ▲M3
```

| Sprint | 重點 | 主要產出（Exit Criteria） |
|---|---|---|
| **S1** 基礎+契約 | WS0 + WS-K | `dotnet build` 綠；CI 綠；§3 契約凍結；NetDevPack（`lib/`）已納入方案 |
| **S2** Map 切片 | WS1 + WS2(對 stub) | HTTP 匯入拓撲、查 `RoadmapGraph`；`DijkstraPathPlanner` 在樣本圖單測通過 |
| **S3** 單機 E2E | WS2 接真實圖 + WS3 + WS-Q 起步 | **M1**：拓撲進→單機路徑出；Host 可 `dotnet run`；模擬時鐘就緒 |
| **S4** 交管核心 | WS4a + WS1 修正 | `ReservationTable`/`IResourceAllocator`/`ReservationService` 單測；釋放無洩漏單測 |
| **S5** 多機整合 | WS4b + WS3 整合 + WS-Q | **M2**：多機不碰撞；`Reservation.*`/`Allocation.Contended` 事件；租約 job |
| **S6** 死鎖+固化 | WS5 + WS-X + WS-Q | **M3**：偵測+解除死鎖；Postgres+RabbitMQ 端到端；場景庫全綠 |

**關鍵路徑**：WS0/WS-K → WS1（圖）→ WS2（規劃）→ WS3（E2E）→ WS4（交管）→ WS5（死鎖）。
**並行機會**：WS2 從 S2 即可對 fake 圖 + always-free stub 開發；WS4 從 S4 對凍結的 `IReservationQuery` 開發（不必等 E2E）；WS-Q 從 S3 持續擴充。

---

## 6. 任務卡背包（Backlog，估算單位＝工程師-日 ED）

> 每張卡的驗收皆隱含「**單元測試 + CI 綠 + review + 符合 grukirbs 慣例**」（見 §7 DoD）。來源檔對應見設計文件 §5。

### WS0 — 基礎設施與 DevEx（≈10 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS0-1 | 建 `SwarmRoute.Mapf.sln`、`Directory.Build.props`(net10/LangVer14/Nullable)、`Directory.Packages.props` | 1 | — | 空方案編譯綠 |
| WS0-2 | 將使用者提供的 NetDevPack（`lib/NetDevPack/src/NetDevPack/NetDevPack.csproj`，netstandard2.1、**相容 net10、免 retarget**）以 ProjectReference 納入方案 | 1 | — | 引用 `Entity/ValueObject/DomainEvent/IUnitOfWork/IDomainEventDispatcher` 可編譯 |
| WS0-3 | vendoring `third-party/algorithms`(AJR.Platform.Algorithms+DataStructures) retarget net10 | 2 | — | Dijkstra/CyclesDetector 可被引用、原樣測試通過 |
| WS0-4 | `Shared/` 核心專案骨架：Domain.Abstractions、EventBus、Infra.Data.Core(`BaseDbContext` 照搬 grukirbs)、StateMachine.Core | 3 | WS0-2 | `BaseDbContext.Commit()` 事件分發單測 |
| WS0-5 | CI 流水線（restore/build net10/test/`dotnet format` 檢查/EF migration 驗證）+ CODEOWNERS + PR 模板 | 2 | WS0-1 | PR 觸發 CI、紅燈擋合併 |

### WS-K — Kernel 契約（≈4 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS-K1 | `SpatioTemporal.Kernel` 型別：`ResourceRef/TimeInterval/SpaceTimeCell/SpaceTimePath/SafeInterval` | 2 | WS0-4 | 值物件相等性單測；半開區間運算單測 |
| WS-K2 | 介面：`IReadOnlyReservationView/IReservationQuery/IRoadmapQueryService/ITrafficControlSnapshotProvider` + 整合事件名與 payload DTO | 1 | WS-K1 | 契約合併、凍結、TL 簽核 |
| WS-K3 | ADR-001 契約凍結、ADR-002 預約狀態走記憶體、ADR-003 迴圈節奏 | 1 | — | `docs/adr/` 入庫 |

### WS1 — Map（≈15 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS1-1 | 移植 `MapSite/MapLine/MapBlock/MapPos/MapSiteType/MapResourceStatus` 進 `Map.Domain(.Shared)`；**修 `MapSiteType` 重複值** | 3 | WS-K | 列舉無衝突；型別單測 |
| WS1-2 | `Roadmap` 聚合根（驗證 ctor、`StateVersion`）+ `MapPosition/InterferenceSet` VO | 3 | WS1-1 | 不變條件單測（懸空 line 端點丟 `ArgumentException`） |
| WS1-3 | `RoadmapGraph` VO + `IRoadmapGraphFactory`（移植 `GraphMap.Init`）+ `IInterferenceCalculator` | 3 | WS1-2 | 建圖對拍 `GraphMap.Init`；干涉半徑運算單測 |
| WS1-4 | `MapDbContext : BaseDbContext` + `IRoadmapRepository` + 首個 migration | 2 | WS1-2, WS0-4 | migration 套用、CRUD 整合測（Testcontainers PG） |
| WS1-5 | `MapsController` 匯入/CRUD + `IRoadmapQueryService`（快取 + `Map.Roadmap.Published` 失效） | 3 | WS1-3, WS1-4 | `POST /api/maps` 匯入、`GET` 回拓撲、查 `RoadmapGraph` |
| WS1-6 | **狀態歸屬拆分**：動態 Locked/Belong 移出 Map（留給 Traffic），Map 只保留靜態 `Enable/SiteType` | 1 | WS4 起 | Map 實體對佔用狀態不可變 |

### WS2 — PathPlanning v0（≈10 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS2-1 | `PathPlanning` 精簡專案骨架 + `AgentPlan` 聚合 + `PlanRequest/SpaceTimePath` 使用 | 2 | WS-K | 編譯、聚合單測 |
| WS2-2 | `IPathPlanner` + `DijkstraPathPlanner`（移植 `CBS.SearchPath`/`GenerateDijkstraShortestPath`） | 3 | WS2-1, WS0-3 | 樣本圖最短路徑對拍 `GraphMap.GeneratePath` |
| WS2-3 | 對 `IReservationQuery` 的 stub（always-free）+ 規劃迴圈讀介面（為 v1 SIPP 預留） | 1 | WS-K2 | 介面接線、stub 單測 |
| WS2-4 | `IPathPlanningAppService.PlanFor` + `AgentPlan.Computed/Failed` 事件 + Mapping | 2 | WS2-2 | `PlanFor(agent,A,B)` 回站點序列；終點被佔回 Failed |
| WS2-5 | 接真實 `IRoadmapQueryService`（移除 fake 圖） | 2 | WS1-5 | E2E：真實拓撲規劃 |

### WS3 — Coordination + Host（≈8 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS3-1 | `Host/Program.cs`：`AddEventBus`(in-memory) + Map/Planning IoC + 設定 | 2 | WS1, WS2 | `dotnet run` 起服務、Swagger 可見 |
| WS3-2 | `Coordination.FleetCoordinationLoop`（最小：單機 plan）+ 各上下文 `*NativeInjectorBootStrapper` | 3 | WS3-1 | **M1**：單機 A→B 端到端 |
| WS3-3 | 迴圈整合 plan→`TryReserve`→拒則重規劃→`Release` | 3 | WS4 | 多機協調（併入 M2） |

### WS4 — TrafficControl v0（≈20 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS4-1 | `ReservationTable` 聚合根（記憶體權威、雙索引、`StateVersion`）+ `ResourceLease/ReservationRequest` | 4 | WS-K | 不變條件單測（同資源同區間至多一車） |
| WS4-2 | `IResourceAllocator`（移植 `GraphMap` 鎖/剪枝過濾 + 黑名單）| 4 | WS4-1, WS0-3 | 對拍 `GraphMap` 剪枝結果 |
| WS4-3 | `IConflictDetector`（點/邊/swap/干涉）+ `RightOfWay` tie-break（Priority→HadWaitedTime→id）| 3 | WS4-1 | 衝突分類單測；確定性 tie-break |
| WS4-4 | `ReservationService : IReservationQuery` + `ITrafficCoordinatorAppService.TryReserve/Release`（移植 `UnlockPath`，**修釋放洩漏**：含 ParentBlock+干涉）| 4 | WS4-2 | 釋放無洩漏回歸；滿足 `IReservationQuery` |
| WS4-5 | 快照 `TrafficControlDbContext`(僅快照/稽核) + `LeaseExpirySweepJob`/`StaleRequestEscalationJob`（Hangfire）| 3 | WS4-1, WS0-4 | 崩潰復原快照測；過期清掃測 |
| WS4-6 | 事件 `Reservation.Granted/Denied/Released`、`Allocation.Contended` + DTO 轉換 | 2 | WS4-4 | 事件發佈/訂閱整合測 |

### WS5 — Deadlock v0（≈12 ED）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS5-1 | `ResourceAllocationGraph` VO（移植 `MapResourceAllocationGraph.GenerateGraph`）+ `ITrafficControlSnapshotProvider` | 3 | WS4-1 | 由快照建 RAG 對拍原實作 |
| WS5-2 | `IDeadlockDetector`（移植 `IndependenceDetection` + `CyclesDetector`）+ `DeadlockCase` 聚合 | 3 | WS5-1 | 2/3/4 車環偵測單測 |
| WS5-3 | `AvoidancePlan` 狀態機（實作 `ISolver`：選犧牲→選 AvoidSite→預約繞道→派去→恢復）| 4 | WS5-2 | 狀態轉移測；繞道走 `TryReserve` 不致新碰撞 |
| WS5-4 | 訂閱 `Allocation.Contended`、發 `Deadlock.Case.Detected/ResolutionRequested/Resolved`；Coordination 重規劃犧牲車 | 2 | WS5-3, WS3-3 | **M3**：端到端偵測+解除 |

### WS-Q — 測試與場景庫（≈12 ED，全程）
| 卡 | 任務 | ED | 依賴 | 驗收 |
|---|---|:--:|---|---|
| WS-Q1 | 模擬時鐘 + in-memory CAP 測試夾具 + 樣本路網產生器 | 3 | WS-K | 可組裝多機場景 |
| WS-Q2 | 安全不變條件套件（I1–I8）：對頭/走廊、swap、跟車、路口匯聚 | 4 | WS4 | 對應斷言全綠 |
| WS-Q3 | 死鎖/livelock 場景（2/3/4 車環；同 (車,避讓點) 不得連兩次且目標距離須嚴格下降）| 3 | WS5 | livelock 防護測 |
| WS-Q4 | 釋放洩漏回歸 + drift/重規劃 + Map/Traffic 整合測（Testcontainers PG）| 2 | WS4, WS1 | 回歸防護入 CI |

### WS-X — 整合與固化（≈8 ED，S6）
| 卡 | 任務 | ED | 驗收 |
|---|---|:--:|---|
| WS-X1 | Docker Compose（PostgreSQL + RabbitMQ）+ 切換真實 CAP 端到端 | 3 | `docker compose up` 後 M3 場景可跑 |
| WS-X2 | 可觀測性：結構化日誌、基本 metrics（規劃延遲/預約數/死鎖數）、健康檢查 | 3 | 儀表板可見關鍵指標 |
| WS-X3 | 文件：README、執行指南、ADR 收斂、v1 交接說明 | 2 | 新人可照文件跑起來 |

**估算合計 ≈ 99 ED**（不含 review/會議/緩衝）。5 名工程師 × 12 週 ≈ 300 dev-day 容量，留足緩衝與未知。

---

## 7. 工程流程與規範

- **分支策略**：trunk-based，短命 feature 分支 → PR 合 `main`，squash merge。`main` 永遠可編譯/測試綠。
- **PR 審查**：一般 ≥1 reviewer；`Shared/`、`Kernel`、契約、`Coordination` ≥2（含 TL）。CI 綠 + `dotnet format` 乾淨方可合併。CODEOWNERS 依 folder 指派 squad。
- **CI（每 PR）**：`dotnet restore` → `build -c Release`(net10) → `test` → format 檢查 → **EF migration 驗證**（無未產生的 model 變更）→（選）覆蓋率門檻。`main` 另跑整合測（Testcontainers）。
- **DoR（Ready）**：卡有驗收標準、依賴契約已凍結/可用、測試方式已註明。
- **DoD（Done）**：程式 + 單元測試、CI 綠、已 review、XML 註解/必要文件、零新警告、**符合 grukirbs 戰術慣例**（私有 setter、驗證 ctor、ValueObject 相等性、事件命名 `Ctx.Aggregate.Action`、`StateVersion` 樂觀並行）。
- **測試策略**：單元（各上下文 xUnit）｜整合（Map/Traffic + Testcontainers PostgreSQL）｜**場景/安全不變條件**（模擬時鐘 + in-memory CAP，斷言 I1–I8）。演算法卡一律對拍既有 AJR 行為。
- **編碼標準**：`.editorconfig` + analyzers + `dotnet format`；命名/分層比照 grukirbs。
- **節奏**：2 週 Sprint，每日站會、Sprint planning/review/retro；每 Sprint demo 對齊里程碑。

### 環境與工具
.NET 10 SDK｜PostgreSQL 16+（Npgsql）｜RabbitMQ（dev 可 `EventBus:UseInMemory=true`）｜Docker Compose（本地基礎設施）｜Testcontainers（整合測）｜GitHub Actions 或 Azure DevOps（CI）。

---

## 8. 第一週啟動清單（Day-1）

1. ~~[阻塞] 取得 NetDevPack fork 原始碼~~ **已解決**：使用者已提供於 `lib/NetDevPack/`（netstandard2.1、相容 net10）。S1 直接以 ProjectReference 納入。
2. 建 repo skeleton + `Directory.*.props`(net10) + CI 骨架（WS0-1、WS0-5）。
3. TL 主持契約工作坊，凍結 §3 契約（WS-K），產出 ADR-001/002/003。
4. 各 squad 認領 folder + CODEOWNERS；建立看板（WS 卡導入）。
5. Platform 起 Docker Compose（PG+RabbitMQ）供本地與整合測。

---

## 9. 風險登記（Risk Register）

| ID | 風險 | 等級 | 當責 | 緩解 | 觸發/閘門 |
|---|---|:--:|---|---|---|
| ~~R1~~ | ~~NetDevPack fork 不在 workspace~~ **已解決** | — | TL | 使用者已提供於 `lib/NetDevPack/src/NetDevPack`（netstandard2.1、相容 net10、免 retarget），以 ProjectReference 納入 | 已關閉 |
| R2 | 即時預約狀態若每 tick 寫 DB 會跟不上 | 中 | Traffic | 權威狀態走**記憶體聚合**（`StateVersion`），EF 僅快照/稽核（ADR-002） | 設計 review |
| R3 | v0 整路上鎖被寫死、阻礙 v1 SIPP | 中 | TL | `ReservationTable/ResourceLease/SpaceTimeCell` 一開始即以**時間區間**為基礎；鎖模型只是 v0 allocator 策略 | WS4 設計 review |
| R4 | 單一 `ReservationTable` 聚合為爭用瓶頸 | 低（起步） | Traffic | 先讓 `ResourceLease` 可獨立定址；規模化再按 zone 分片 | 壓測指標 |
| R5 | algorithms 庫授權/目標框架 | 低 | Platform | retarget net10 前確認無授權限制 | WS0-3 |
| R6 | 迴圈節奏/重規劃不確定 → livelock | 中 | Algo | ADR-003 定義事件驅動 + watchdog tick + 確定性 tie-break；WS-Q3 專測 livelock | WS-Q3 |
| R7 | 契約churn 拖垮並行 | 中 | TL | S1 凍結契約，變更走 ADR + 全 squad 知會 | 每 Sprint review |
| R8 | 大爆炸整合 | 中 | TL | 自 S3 起以 Coordination 持續整合（非末期才合） | 每 Sprint demo |

---

## 10. 里程碑驗收（v0 基線 DoD）

- ☑ `dotnet build SwarmRoute.Mapf.sln` 在 **.NET 10** 綠燈；CI 全綠。
- ☑ HTTP 匯入路網拓撲；`IRoadmapQueryService` 回 `RoadmapGraph`。
- ☑ 單機 `PlanFor(A,B)` 回正確路徑（對拍 `GraphMap.GeneratePath`）。
- ☑ 多機經 `TryReserve` 序列化、**不碰撞**；通過後資源**無洩漏**釋放（含 ParentBlock + 干涉）。
- ☑ 2/3/4 車死鎖環被 `IDeadlockDetector` 偵測，`AvoidancePlan` 導犧牲車至 AvoidSite 解除並恢復原目標。
- ☑ 安全不變條件套件（I1–I8）與 livelock 防護測全綠。
- ☑ `docker compose up`（PostgreSQL + RabbitMQ）後 M1–M3 場景端到端可跑。
- ☑ 基本可觀測性（日誌/metrics/健康檢查）+ 執行文件齊備。

---

## 11. 假設、相依與伸縮

- **相依（外部）**：NetDevPack 已由使用者提供於 `lib/NetDevPack`（R1 關閉）；使用者提供範例路網（或用 `AJR.Platform.Minimal` 的 `MapInfo.MapJson` 取材）。
- **假設**：團隊熟 .NET/EF/DDD；CI 平台與雲端資源就緒；v1（SIPP）為下一里程碑、本期僅預留介面。
- **團隊伸縮**：
  - **4 人精簡**：TL 兼 Coordination/Host；Platform 兼 QA 自動化；Map 與 Traffic 各 1；Algo 1（Planning+Deadlock 串行）。里程碑順延約 2–3 週。
  - **8 人加速**：Planning 與 Deadlock 拆兩人；加 1 QA 專注場景庫；加 1 DevEx/SRE 顧 CI/可觀測性。WS4 可再切子卡並行。
- **下一里程碑（v1 預告）**：TrafficControl 導 `TimeInterval/Headway` + `SafeIntervals()`；PathPlanning 換 `SippPathPlanner`；介面不變，只換實作——本計劃的契約設計已為此預留。
