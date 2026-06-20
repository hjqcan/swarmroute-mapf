先生，我給出的解法很明確：**不要再把 7×7/16 的問題當成 solver 問題處理，而是把它升級成「任務點 / 停車點 / 讓路點」的場景語義問題處理。**

目前最核心的錯誤語義是：

```text
任意隨機格子 = 任務目標
車到目標 = 永久停車
永久停車 = 後續規劃的靜態障礙
```

這套語義在 7×7/16 這種高密度小圖下，一定會頻繁製造「最後幾台到不了」。現在代碼也確實是這樣：fresh run 直接 shuffle 全部格子，前 N 個當 start，後 N 個當 goal；車到達後會 `_parkedCells.Add(here)`，後續規劃又把 parked/waiting physical blockers 餵給 planner。

所以我的方案是四層，按優先級來。

---

# 方案總覽

```text
第一層：場景生成修正
        隨機目標 → well-formed endpoints / 任務點池

第二層：到達語義修正
        到達即永久停車 → 任務完成後清場 / 去 parking buffer

第三層：停車車輛可重派
        parked obstacle → movable idle robot / gatekeeper relocation

第四層：v3 solver 保持原職責
        CBS / PIBT 解 live standoff，不背鍋處理所有 parked endpoint 問題
```

最重要一句話：

> **AGV 不應該永遠停在任意任務目標點；它完成任務後，要麼接下一單，要麼去非阻塞停車點。**

這才是 FMS 的真實語義。

---

# 第一階段：把 goal 從「任意格子」改成「合法 endpoint」

現在的 fresh run 是：

```text
allCells shuffle
starts = shuffled[0..N)
goals  = shuffled[N..2N)
```

這只是保證 start/goal 不重複，不保證 goal 是可以長期停車的位置。代碼裡 validate 也只檢查 `Width*Height >= 2*AgvCount`，沒有檢查 endpoint 是否 well-formed。

這裡要新增一個 `ScenarioGenerator`，不要直接從所有格子抽目標。

## 1.1 新增 SiteRole

Map 裡要把點分成不同角色：

```csharp
public enum SiteRole
{
    Transit,        // 純通行點，不允許長期停車
    Workstation,    // 任務點：取貨 / 放貨 / 工位
    Parking,        // 合法停車點
    Charger,        // 充電點
    Buffer,         // 臨時等待 / 緩衝點
}
```

然後規定：

```text
任務 goal 可以是 Workstation / Charger / Buffer
長期 parked 只能是 Parking / Buffer / Charger
Transit 永遠不能成為永久 parked goal
```

這一步非常關鍵。現在你是「所有格子都可能成為終點」，這在算法測試裡很方便，但在 FMS 語義裡是錯的。

---

## 1.2 Well-formed endpoint 檢查

MAPD / lifelong MAPF 相關研究裡，well-formed instance 是很核心的條件。Ma、Li、Koenig 的 MAPD 論文就明確說，不是所有 MAPD instance 都可解；他們的 Token Passing 類方法保證的是 well-formed MAPD instances。([arXiv][1]) 後續帶 kinematic constraints 的 TP-SIPPwRT 也仍然是在 well-formed MAPD instances 上給 completeness 結果。([arXiv][2])

你這裡可以做一個工程化版本：

```csharp
public sealed record EndpointSet(
    IReadOnlySet<string> Workstations,
    IReadOnlySet<string> Parkings,
    IReadOnlySet<string> Buffers);

public interface IEndpointPolicy
{
    EndpointSet BuildEndpoints(RoadmapGraph graph, int agvCount, int seed);
}
```

合法 endpoint set 至少滿足：

```text
1. TransitCore = V - longTermParkingEndpoints 仍然連通
2. 每個 endpoint 至少有一個鄰接 TransitCore 的出口
3. 任意 endpoint 到任意 endpoint 可以經由 TransitCore 連通
4. Parking / Buffer 不放在 articulation point / 主幹瓶頸上
5. Workstation 允許短停，但不允許永久停車
```

檢查偽代碼：

```csharp
bool IsValidEndpointSet(RoadmapGraph graph, HashSet<string> endpoints)
{
    var transit = graph.Vertices.Where(v => !endpoints.Contains(v)).ToHashSet();

    if (!IsConnected(graph, transit))
        return false;

    foreach (var e in endpoints)
    {
        var hasEgress = graph.Neighbours(e).Any(n => transit.Contains(n));
        if (!hasEgress)
            return false;
    }

    return true;
}
```

### 7×7/16 的特殊處理

7×7 只有 49 格，16 台車很密。你不應該再要求：

```text
16 個 start + 16 個 goal 都是任意長期 endpoint
```

更合理的是：

```text
start = 車當前位置，可以在 Transit / Parking / Buffer
goal  = 任務點，只能從 Workstation / Buffer / Charger 池抽
任務完成後 = 不長期停在 Workstation，而是清場去 Parking / Buffer
```

所以 fresh run 應該變成：

```text
starts = 從非目標佔用格 / 當前車位抽
taskGoals = 從任務點池抽
parkingGoals = 從 parking/buffer 池抽，供任務完成後清場
```

---

# 第二階段：把「到達目標」拆成「任務完成」和「車輛停妥」

這是最大修正。

現在代碼是：

```text
到達 real goal
→ Done = true
→ EnRoute = false
→ _parkedCells.Add(here)
→ Release all reservations
```

也就是目標點直接變永久障礙。

我建議改成狀態機：

```text
MovingToTask
    ↓
ServicingAtTask
    ↓
TaskCompleted
    ↓
NeedsClearance
    ↓
MovingToParking
    ↓
IdleParked
```

## 2.1 新增 AgentMissionState

```csharp
public enum AgentMissionState
{
    MovingToTask,
    ServicingAtTask,
    NeedsClearance,
    MovingToParking,
    IdleParked,
    Reassigned
}
```

## 2.2 到任務點不等於 Done

到達 Workstation / 任務點時：

```text
1. 記錄 task completed
2. 停留 serviceTime ticks
3. 進入 NeedsClearance
4. Dispatcher / ParkingManager 分配最近合法 parking/buffer
5. 車離開任務點
```

偽代碼：

```csharp
if (ag.ReachedEffectiveGoal)
{
    if (IsTaskSite(here))
    {
        ag.MissionState = AgentMissionState.ServicingAtTask;
        ag.ServiceTicksRemaining = SiteServiceTime(here);
        _taskCompletions++;
        // 不加入 _parkedCells 作永久障礙
    }
    else if (IsParkingSite(here))
    {
        ag.MissionState = AgentMissionState.IdleParked;
        ag.Done = true;
        _parkedCells.Add(here);
    }
}
```

## 2.3 任務點 service 完成後強制清場

```csharp
if (ag.MissionState == AgentMissionState.ServicingAtTask)
{
    ag.ServiceTicksRemaining--;

    if (ag.ServiceTicksRemaining <= 0)
    {
        var parking = _parkingManager.AssignParking(ag, graph, parkedCells, reservations);
        ag.RedirectTarget = parking;
        ag.MissionState = AgentMissionState.MovingToParking;
        ag.Done = false;
        ag.EnRoute = false;
    }
}
```

這樣「任務完成」和「車輛停在終點」就不再混在一起。

---

# 第三階段：新增 ParkingManager，而不是靠 StepAside 硬救

現在 StepAside 是必要補丁，但它不是完整解。它的配置也能看出來：`GatekeeperUnblockThreshold = 10`，`GatekeeperYieldWindow = 20`，也就是「堵住別人時，暫時挪開 20 tick，然後再回去」。

這會有兩個問題：

```text
1. 20 tick 後它回來，可能再次堵住
2. 多台 parked 車一起堵時，單台 step-aside 不夠
```

我的建議是：**把 StepAside 升級成 ParkingManager + persistent relocation。**

## 3.1 ParkingManager 職責

```csharp
public interface IParkingManager
{
    string? AssignParking(
        RunAgent agent,
        RoadmapGraph graph,
        IReadOnlySet<string> parkedCells,
        IReadOnlySet<string> reservedOrOccupiedCells);

    IReadOnlyList<ParkingRelocation> FindRelocationsForWalledAgent(
        RunAgent walledAgent,
        IReadOnlyList<RunAgent> fleet,
        RoadmapGraph graph,
        IReadOnlySet<string> parkedCells);
}
```

它負責：

```text
1. 任務完成後，把車派去不阻塞的 parking/buffer
2. 發現某台車被 parked blocker 擋住時，找需要挪走的 parked 車
3. 給被挪車分配新的 buffer，不是只挪 20 tick
4. 等 corridor 不再需要時，才允許它回原 parking
```

---

## 3.2 StepAside 的語義要改

現在的語義：

```text
parked blocker 挪到旁邊
等待 GatekeeperYieldWindow = 20 tick
再回原 goal
```

我建議改成：

```text
parked blocker 挪到 buffer
直到 blocked agent 完成通過 / 沒有 unresolved route 依賴它讓出的 corridor
再由 ParkingManager 決定是否回原 parking
```

也就是：

```text
GatekeeperYieldWindow 不再是「自動回來倒計時」
而是「最短保持讓路時間」
```

狀態機：

```text
IdleParked
    ↓ 被判定 blocking
RelocatingForClearance
    ↓ 到 buffer
HoldingClearance
    ↓ corridor clear
ReturnAllowed / StayParked
```

這能直接解決目前「讓一下又回來堵」的問題。

---

# 第四階段：CBS / PIBT 不要被迫處理 parked endpoint 問題

v3 的 CBS / PIBT 應該保持現在的定位：**解 live standoff cluster**。

你的 `StuckClusterDetector` 寫得很清楚：parked / finished blocker 不是 joint resolver 的 live standoff，而是 step-aside / relocation 問題。

我同意這個邊界。不要把 parked 車硬塞進 CBS，否則 CBS 的問題規模會突然膨脹，而且語義會變髒：

```text
CBS 解的是「正在追任務的車互相卡住」
ParkingManager 解的是「已完成 / idle 車擋住任務流」
```

後面可以新增一個專門的 `RelocationClusterSolver`，但它不是普通 CBS，而是：

```text
輸入：
- walled agent
- blocking parked agents
- candidate buffers
- current physical blockers

輸出：
- 哪些 parked agents 需要移走
- 移去哪個 buffer
- walled agent 何時可以重試
```

簡化版可以先不用聯合求解，先 greedy：

```text
1. 找 walled agent 到 goal 的一條最短路
2. 找路上 parked blockers
3. 對每個 blocker 找最近 buffer
4. 發 relocation task
5. walled agent 等待 / 下輪重規劃
```

---

# 第五階段：把 simulation 分成兩種模式

你現在的 7×7/16 其實同時承擔了兩個目的：

```text
1. 壓力測試 solver 在極端隨機場景下能不能不碰撞
2. 展示 FMS 場景下能不能高完成率
```

這兩個不該混在一起。

我建議加一個 `ScenarioMode`：

```csharp
public enum ScenarioMode
{
    RandomStress,        // 現在這種：任意 start/goal，允許 DidNotConverge
    WarehouseWellFormed, // 合法 endpoint + parking/buffer，目標是高完成率
    LifelongDispatch,    // 任務流，不追求全部停死，而是看 throughput
}
```

## RandomStress

保留現在行為，用來測：

```text
- 0 collision
- 不死循環
- DidNotConverge 可重現
- v3 resolver 能讓多少 seed 從失敗變成功
```

這個模式下，DidNotConverge 是正常結果。

## WarehouseWellFormed

新增模式，用來測：

```text
- 任務點只從合法 endpoint 抽
- 車完成任務後去 parking/buffer
- parked 不阻塞 transit core
- 7×7/16 追求高完成率
```

這個模式下，DidNotConverge 才應該被視為需要分析的問題。

## LifelongDispatch

更接近真實 FMS：

```text
- 沒有「所有車到達固定終點」這種結束條件
- 每完成一單就派下一單
- 指標是 throughput / wait time / queue length / deadlock rate
```

這也跟 MAPD 的問題設定更一致：agent 持續接收 pickup-delivery task，而不是一次性到終點後永久停死。([arXiv][1])

---

# 我建議的具體落地順序

## Step 1：先加 NonConvergenceReason，不急著改 planner

你要先讓每次 DidNotConverge 自動分類：

```csharp
public enum NonConvergenceReason
{
    TickBudgetExceeded,
    ParkedGoalBlocker,
    NoWellFormedEndpointPath,
    LiveStandoffUnresolved,
    ParkingSaturation,
    SolverTimeout,
    Unknown
}
```

分類邏輯：

```text
1. 找沒到的 agent
2. 對每台 agent，在 graph - parkedCells 上看 goal 是否可達
3. 不可達 → ParkedGoalBlocker / NoWellFormedEndpointPath
4. 可達但長期 blocked → LiveStandoffUnresolved
5. 已完成任務但找不到 parking → ParkingSaturation
```

這個東西非常值錢，因為它會把「solver 沒解開」和「場景本身不該這樣建模」分開。

---

## Step 2：加 SiteRole + WarehouseWellFormed 生成器

先不要動 v3 solver。

只改場景生成：

```text
RandomStress：保持原樣
WarehouseWellFormed：
    - 生成 transit core
    - 生成 workstation endpoints
    - 生成 parking/buffer endpoints
    - starts 從 current positions / transit / parking 抽
    - task goals 只從 workstation 抽
```

對 7×7，我會用很簡單的 layout：

```text
外圈/邊緣：Parking / Buffer / Workstation
中間 5×5：Transit core
避免把四角和主入口全部塞滿 parked 車
```

但注意：角落如果兩個鄰居也都是 endpoint，角落 parked 車可能變成死點，所以 endpoint 生成器要檢查 egress。

---

## Step 3：把到達 Workstation 後的 Done 改成 NeedClearance

這一步是根治。

現在：

```text
arrive goal → Done → parkedCells.Add(goal)
```

改成：

```text
arrive workstation → TaskCompleted → service → route to parking
arrive parking     → Done / IdleParked → parkedCells.Add(parking)
```

這會直接打掉「任務點永久堵路」這個主因。

---

## Step 4：ParkingManager 替代 20 tick StepAside

先做最小版：

```text
當 parked 車被判定堵住某 active agent：
    不要 20 tick 後必回原 goal
    而是派它去最近 buffer
    buffer 被佔滿才退回臨時 step-aside
```

這裡可以沿用你現在的 `RelocateParked` directive，只是把恢復條件改掉。現在 LivenessPolicy 在 BeforePlanning 階段會根據 `ParkedRelocationSelector.Select(...)` 發 `RelocateParked`。 這條 seam 很好，不需要推翻。

---

## Step 5：保留 v3 resolver，只改評估方式

v3 的 `jointResolver: Cbs` 可以繼續預設開。前端現在也確實預設 7×7 / 16、SIPP、StepAside=true、CBS resolver。

但評估時要分開看：

```text
RandomStress:
    看安全性、更多抵達、是否可診斷

WarehouseWellFormed:
    看完成率、吞吐、等待時間

LifelongDispatch:
    看長時間 throughput、P95 latency、parking saturation
```

不要拿 RandomStress 的 DidNotConverge 去否定 v3，也不要拿 v3 去硬解錯誤 endpoint 語義。

---

# 不建議的方案

## 1. 不建議單純加大 MaxTicks

`MaxTicks` 現在是 `(W+H)*(N+1)*2 + 100`，7×7/16 就是 576。

加大 tick 只能讓某些偶然 seed 多拖一點時間，但解不了：

```text
parked 車永久堵住
endpoint 不 well-formed
parking/buffer 不夠
```

所以它只能作為 debug 參數，不是解法。

## 2. 不建議繼續加強 CBS

CBS 解 live cluster 有用，但 parked blocker 不進 cluster。你的 detector 明確把 parked blocker 排除在 joint resolver 外。

所以「再強 CBS」也只是解另一類問題。

## 3. 不建議把完成車直接 disappear

這可以作為 solver benchmark mode，但不是真實 AGV/FMS。真車不會消失，它必須去某個 parking / buffer / 下一個任務點。

可以加：

```text
ArrivalPolicy.Disappear
```

但只用來做算法壓力測試，不作為產品語義。

---

# 最終我推薦的版本命名

我會把這個做成：

```text
v3.6 Endpoint & Parking Semantics
```

或者更產品化一點：

```text
v4.0 Warehouse Scenario Semantics
```

交付內容：

```text
1. SiteRole：Transit / Workstation / Parking / Buffer / Charger
2. ScenarioMode：RandomStress / WarehouseWellFormed / LifelongDispatch
3. WellFormedEndpointGenerator
4. MissionState：TaskCompleted != Done
5. ParkingManager：任務完成後清場
6. Persistent StepAside：parked blocker 挪去 buffer，不是 20 tick 後必回
7. NonConvergenceReason：自動分類 DidNotConverge 根因
```

---

# 驗收標準

我會這樣定：

## RandomStress 模式

```text
7×7 / 16：
- 0 collision
- 不崩潰
- DidNotConverge 有清楚 reason
- v3 CBS / PIBT 能提升 arrived 數，但不要求 100% completed
```

## WarehouseWellFormed 模式

```text
7×7 / 16：
- 0 collision
- 大部分 seed completed
- DidNotConverge 主要只剩 parking saturation / tick budget
- parked blocker reason 明顯下降
```

## LifelongDispatch 模式

```text
長時間任務流：
- throughput 穩定
- parking/buffer 不飽和
- P95 wait time 可控
- 無永久堵塞
```

---

# 最後結論

先生，我的解法不是「再修 CBS」，也不是「把 MaxTicks 調大」。

真正解法是：

> **把終點從任意格子改成合法任務點，把到達後永久停車改成任務完成後清場，把 parked 車從靜態障礙改成可被 ParkingManager 重派的 idle robot。**

這會從根上解掉 7×7/16 的主要 non-convergence 來源，而且方向完全貼近你們真實 AGV 場景：AGV 做完一個任務不會永遠堵在任務點，它會去下一單、等待位、充電位或緩衝區。v3 solver 保持負責交通僵局，FMS 層負責 endpoint / parking / dispatch，這才是正確分工。

[1]: https://arxiv.org/abs/1705.10868?utm_source=chatgpt.com "Lifelong Multi-Agent Path Finding for Online Pickup and Delivery Tasks"
[2]: https://arxiv.org/abs/1812.06355?utm_source=chatgpt.com "Lifelong Path Planning with Kinematic Constraints for Multi-Agent Pickup and Delivery"


問：
我知道到達目的地後不要永久停車是對的，可以先挪開讓別的車過再回來，但是在真實的工廠環境下，一般是到達目的地後就立刻開始進行某些工作，例如agv上有個機械臂，給cnc上料，一旦agv開始工作，它就是無法移動的，而工作完成後它本身可能就有新的目的地，規劃器或者可能有一個統籌者？它要統籌全局，可能有些任務進行時間非常長，例如1小時，在這些站點可能堵塞後面車的情況下，應該先讓後面車先過，才過去進行任務，你能明白我的意思嗎

答：
先生，我完全明白，而且你這個補充把問題說到**真正工業級 FMS 的核心**了。

你說的不是「車到目的地後不要永久停車」這麼簡單，而是：

> **AGV 在開始作業前是可移動資源；一旦開始作業，它就變成不可移動的長時間佔用資源。
> 所以系統不能只規劃“怎麼到達目的地”，還要決定“什麼時候允許它進入工位並開始作業”。**

這個角色不應該由單純的 path planner 來做，而應該由一個更上層的 **Dispatcher / Station Scheduler / Dock Admission Controller** 來統籌。

---

# 正確抽象：目的地不是終點，而是「作業資源」

比如 CNC 上料場景，AGV 的目標不應該只是：

```text
AGV-A 去 CNC-01
```

而應該拆成：

```text
AGV-A 去 CNC-01 前等待點
等待工位准入
獲准進入 CNC-01 docking 位
開始機械臂作業
作業期間不可移動
作業完成後接下一任務 / 離開 / 去停車點
```

這裡最關鍵的是：

```text
到達工位 ≠ 可以立刻開始工作
```

而是要先問全局調度器：

```text
如果我現在進去，並且 1 小時不能動，
會不會堵住後面更重要的車？
會不會封掉主通道？
會不會讓某些任務無法完成？
有沒有車需要先通過？
有沒有 buffer 可以讓我先等？
```

所以你說的「應該先讓後面車先過，才過去進行任務」完全正確。

---

# 我建議的核心方案：Dock Admission Control

我會把這個機制叫做：

```text
Dock Admission Control
工位准入控制 / 作業准入控制
```

它的職責不是找路，而是決定：

```text
哪台 AGV
在什麼時間
可以進入哪個工位
並且開始多久不可移動的作業
```

整體架構應該變成：

```text
Task Dispatcher / 任務調度器
        ↓
Station Scheduler / 工位排程器
        ↓
Dock Admission Controller / 工位准入控制
        ↓
PathPlanning / 路徑規劃
        ↓
TrafficControl / 交通管制與資源預約
        ↓
AGV Controller / 仙工控制器
```

也就是說：

```text
PathPlanner 解「怎麼走」
TrafficControl 解「路權是否衝突」
StationScheduler 解「現在能不能進去幹活」
Dispatcher 解「整體任務順序與優先級」
```

---

# AGV 狀態要重新拆分

現在不能只有：

```text
Moving
Done
Parked
```

要拆成更細的任務狀態：

```csharp
public enum AgvMissionState
{
    Idle,                  // 空閒，可派新任務
    MovingToPreDockBuffer, // 去工位前等待點
    WaitingDockAdmission,  // 等待工位准入
    Docking,               // 進入工位
    InService,             // 作業中，不可移動
    Undocking,             // 作業完成，離開工位
    MovingToNextTask,      // 去下一任務
    MovingToParking,       // 去停車 / buffer
    Faulted                // 異常
}
```

最重要的是這三個狀態：

```text
WaitingDockAdmission：可移動，可讓路
Docking：正在進工位，短時間佔用
InService：不可移動，硬佔用
```

你的機械臂上料場景就是：

```text
WaitingDockAdmission → Docking → InService
```

**只有進入 InService 之前，AGV 才能被要求讓後面車先過。**

一旦進入 InService，它就不能再被 StepAside、PIBT、CBS、重規劃隨便挪走。這時候它應該被視為：

```text
硬障礙 + 長時間資源預約
```

---

# 地圖上要區分三種點

你的點拓撲裡，不是所有點都應該一樣。

我會把工位附近拆成：

```text
Transit Point       通行點
PreDock Buffer      工位前等待點
Dock Point          作業點 / 對接點
```

例如：

```text
主通道:  A ─ B ─ C ─ D ─ E

CNC-01:
          P1  ← PreDock Buffer，車可以在這裡等
          |
          D1  ← Dock Point，進去後開始上料，不可移動
```

正常流程應該是：

```text
AGV 先去 P1 等
系統確認後方車流已通過 / 主通道可被佔用
再允許 AGV 從 P1 進入 D1
然後開始 1 小時作業
```

而不是：

```text
AGV 直接去 D1
到了立刻開始上料
後面車全部被堵
```

---

# 工位本身也要成為 TrafficControl 的資源

現在 SwarmRoute 已經有 CP、Lane、Block、Zone 這些資源概念。之前代碼裡，TrafficControl 本來就是負責「誰能在什麼時間佔用什麼 resource」的 interval lease。

所以工位作業應該也建模成 resource lease。

例如 CNC-01：

```csharp
ResourceRef Station = new(ResourceKind.Station, "CNC-01");
ResourceRef DockCp = new(ResourceKind.CP, "D1");
ResourceRef ArmZone = new(ResourceKind.Zone, "CNC-01-ArmEnvelope");
ResourceRef BlockedAisle = new(ResourceKind.Zone, "Aisle-C-D");
```

當 AGV 開始上料時，不只是佔用 DockPoint，而是佔用一組資源：

```text
Station:CNC-01
CP:D1
Zone:CNC-01-ArmEnvelope
Zone:可能被車體/機械臂擋住的主通道區
```

時間區間可能是：

```text
[t_start, t_start + serviceDuration + safetyBuffer)
```

如果 serviceDuration 是 1 小時，那就是：

```text
[t_start, t_start + 3600s)
```

這是長租約，不是普通行駛路徑。

---

# 核心算法：先評估「作業佔用會不會堵全局」

當 AGV 要去 CNC-01，不應該立刻給它 DockPoint 作為最終目標，而是先給它：

```text
PreDockBuffer
```

然後 Dock Admission Controller 做判斷。

## 判斷流程

```text
1. AGV 到達 CNC-01 的 PreDockBuffer
2. 系統生成一個候選作業窗口：
   [now, now + serviceDuration)

3. 查詢這段時間內，工位會佔用哪些資源：
   Station / DockPoint / ArmZone / BlockedAisle

4. 檢查：
   - 這些資源現在是否空閒？
   - 這些資源是否會阻擋已經在路上的車？
   - 是否有高優先級任務需要先通過？
   - 是否有替代路徑？
   - 後面車流是否可以先放行？
   - buffer 是否足夠讓 AGV 等？

5. 如果允許：
   - 原子預約 Docking 路徑 + 作業資源
   - 發指令讓 AGV 進工位
   - 開始作業
   - 作業期間不可移動

6. 如果不允許：
   - AGV 繼續在 PreDockBuffer 等
   - 先讓受影響車輛通過
   - 下一輪再評估
```

---

# 也就是「先過車，再關門作業」

你說的「先讓後面車先過」可以具體變成一個策略：

```text
Clearance Before Service
作業前清場
```

流程如下：

```text
AGV-A 要進 CNC-01 作業 1 小時
CNC-01 作業時會阻塞 Lane C-D

系統發現：
AGV-B、AGV-C、AGV-D 已經在上游，且都需要通過 Lane C-D

於是：
1. AGV-A 停在 CNC 前等待點
2. 暫不允許它進 DockPoint
3. 讓 AGV-B/C/D 先通過 C-D
4. 等 C-D 清空後
5. 再讓 AGV-A 進 CNC-01
6. AGV-A 開始 1 小時上料
```

這就是你說的意思。

核心不是「AGV 到了再挪開」，而是：

> **AGV 在開始不可移動作業之前，就不能被放進會堵路的位置。**

---

# 一個簡化的決策模型

可以先不用搞很複雜的全局優化，先做一個可落地的 heuristic。

## 工位准入判斷

```csharp
public sealed record ServiceAdmissionRequest(
    string AgentId,
    string StationId,
    string PreDockBuffer,
    string DockPoint,
    TimeSpan ServiceDuration,
    int Priority,
    DateTimeOffset EarliestStart,
    DateTimeOffset? Deadline);

public sealed record ServiceAdmissionDecision(
    bool Granted,
    DateTimeOffset? ServiceStart,
    string Reason,
    IReadOnlyList<string> VehiclesToClearFirst);
```

## 判斷邏輯

```csharp
if (!StationAvailable(station, serviceWindow))
    return Deny("station busy");

if (BlocksTransitCore(station))
{
    var affected = VehiclesWhosePlannedPathUses(station.BlockingClosure);

    if (affected.Any(v => v.Priority >= request.Priority || v.AlreadyNearBlockedZone))
        return Deny("let affected vehicles pass first", affected);
}

if (!CanReserveServiceClosure(station, serviceWindow))
    return Deny("service closure conflicts");

return Grant(serviceWindow);
```

其中 `station.BlockingClosure` 就是：

```text
這個工位作業時會擋住哪些 CP / Lane / Zone / Block
```

---

# 任務排程不只是路徑規劃，像 Job-Shop + Traffic

這個問題本質上已經不是純 MAPF 了。

它其實是：

```text
任務排程 + 工位資源排程 + 路徑交通管制
```

像一個簡化版 Job-Shop Scheduling：

```text
CNC-01 是 machine resource
AGV 是 mobile resource
道路是 traffic resource
機械臂作業是 service interval
```

所以一個任務不應該只是：

```text
from A to B
```

而應該是：

```text
Task:
  pickup/load/unload
  targetStation = CNC-01
  serviceDuration = 3600s
  requiredResources = [Station:CNC-01, Dock:D1, ArmZone:Z1]
  deadline
  priority
  blockingClosure
```

Dispatcher 應該統籌：

```text
這個任務現在做，會不會讓整體吞吐下降？
先做哪台 CNC？
哪台 AGV 去？
先讓哪些車通過？
哪台車在 buffer 等？
```

---

# 真實工廠裡的三種站點類型

不是所有工位都一樣，我會把站點分成三類。

## 1. Non-blocking Station

工位在支路 / 側邊，AGV 作業時不堵主通道。

```text
這種可以到達後直接作業。
```

模型：

```text
DockPoint 不在 TransitCore 上
ServiceClosure 不包含主通道
```

## 2. Soft-blocking Station

作業時會影響局部通行，但有替代路徑。

```text
這種需要准入控制，但可以容忍。
```

策略：

```text
先檢查近期車流
必要時讓幾台車先過
然後開始作業
```

## 3. Hard-blocking Station

作業時堵住唯一主通道，而且可能持續 1 小時。

```text
這種不是 planner 能完全解的問題，是產線 / 地圖 / 工藝設計問題。
```

策略只能是：

```text
1. 作業前清場
2. 作業期間把此通道視為關閉
3. 後續任務全部繞行或暫停
4. 如果沒有繞行，禁止依賴該通道的任務進入系統
```

如果一個 CNC 上料點會封死唯一通道 1 小時，那再強的 CBS / PIBT / SIPP 都沒用。這時候正確方案是：

```text
增加旁路
增加等待 buffer
把 docking 位移到支線
調整任務順序
限制作業窗口
```

這是 layout / dispatch 問題，不是 path planning 問題。

---

# SwarmRoute 裡我會怎麼落地

我會新增一個上層 context：

```text
Dispatch / StationScheduling
```

它放在 Coordination 上面。

目前 SwarmRoute 的 Coordination 是把 Map、PathPlanning、TrafficControl 串起來，每 tick 做 plan → reserve → replan。 這很好，但它還是在「已經有 agent goal」之後工作。

現在要補的是：

```text
誰應該拿什麼 goal？
什麼時候可以進工位？
什麼時候可以開始不可移動作業？
```

## 新增幾個服務

```csharp
public interface ITaskDispatcher
{
    Task<DispatchDecision> AssignNextTaskAsync(string agentId);
}

public interface IStationScheduler
{
    Task<ServiceAdmissionDecision> RequestDockAdmissionAsync(ServiceAdmissionRequest request);
}

public interface IStationResourceCalendar
{
    bool CanReserveServiceWindow(string stationId, TimeInterval interval);
    Task ReserveServiceWindowAsync(ServiceReservation reservation);
}

public interface ITrafficImpactAnalyzer
{
    TrafficImpact AnalyzeBlockingImpact(
        IReadOnlySet<ResourceRef> blockingClosure,
        TimeInterval proposedServiceWindow);
}
```

---

# 任務流程應該變成這樣

以 CNC 上料為例：

```text
1. Dispatcher 分配任務：
   AGV-07 → CNC-01 上料，預估 1 小時

2. StationScheduler 找 CNC-01 的 PreDockBuffer：
   AGV-07 先去 Buffer-CNC01-A

3. PathPlanning 規劃到 buffer：
   這只是普通移動任務

4. AGV 到 buffer 後，不開始作業：
   狀態 = WaitingDockAdmission

5. StationScheduler 嘗試申請工位窗口：
   Station:CNC-01 [10:00, 11:00)
   Dock:D1 [10:00, 11:00)
   ArmZone:Z1 [10:00, 11:00)
   BlockingZone:MainAisle-3 [10:00, 11:00)

6. TrafficImpactAnalyzer 發現：
   AGV-12、AGV-14 需要先通過 MainAisle-3

7. StationScheduler 拒絕立即作業：
   AGV-07 繼續等
   AGV-12、AGV-14 先過

8. 通道清空後：
   StationScheduler grant

9. AGV-07 進 DockPoint：
   Docking

10. 到位後開始機械臂：
    InService，不可移動

11. 作業完成：
    Dispatcher 給下一目的地
    或 StationScheduler 安排離開到 buffer/parking
```

---

# 「可以先挪開再回來」只適合兩種情況

你前面說「可以先挪開讓別的車過再回來」，這是對的，但要限定狀態。

## 可以挪

```text
IdleParked
WaitingDockAdmission
PreServiceWaiting
DockedButNotStarted
```

這些狀態車還沒開始不可中斷作業，可以讓路。

## 不可以挪

```text
InService
RobotArmEngaged
LoadingUnloading
LiftedCargo
HumanInteraction
SafetyInterlocked
```

這些狀態絕不能被普通交通策略挪走。

所以 StepAside / RelocateParked 不能對所有 parked 車生效，要先看：

```csharp
public enum MobilityClass
{
    Movable,
    MovableWithCost,
    ImmovableUntilServiceComplete,
    FaultedImmovable
}
```

只有 `Movable` / `MovableWithCost` 才能被 relocation。

---

# 排程策略：不是永遠讓後車先過，而是算代價

你說「應該先讓後面車先過」，大多數情況對，但真實系統還要考慮任務優先級。

例如：

```text
AGV-A 要給 CNC 上料，CNC 已經快停機了
後面 AGV-B 只是去空車等待位
```

這時候也許不應該讓 B 先過，而應該讓 A 立刻作業。

所以准入策略應該算一個 cost：

```text
Cost(startTime) =
    CNC 等待損失
  + AGV 作業延遲
  + 被堵車輛等待時間
  + 高優任務被延遲懲罰
  + 重規劃成本
  + buffer 佔用成本
```

簡化版可以是：

```csharp
score =
    servicePriority * serviceUrgency
  - blockedVehicleCount * blockedPenalty
  - highPriorityBlockedCount * highPriorityPenalty
  - noBypassPenalty;
```

如果 score 高：

```text
允許進站作業
```

如果 score 低：

```text
先讓後方車流通過
```

這才是真正的全局統籌。

---

# 對 7×7/16 問題的對應關係

你現在 7×7/16 的問題是：

```text
任意 goal 到達後就變不可移動 parked obstacle
```

真實工廠版是：

```text
某些工位到達後會變成不可移動 InService obstacle
```

差別是：

```text
7×7/16 裡是隨機產生，沒有准入控制
真實工廠裡是有任務語義、服務時間、工位類型、准入時機
```

所以解法不是「所有到達後都能挪開」，而是：

> **在不可移動之前就做准入控制。
> 一旦作業開始，就把它當成長時間硬佔用。**

---

# 我最推薦的實作優先級

## 第一版：最小可用

先做這幾個就夠有效：

```text
1. SiteRole：Transit / PreDockBuffer / DockPoint / Parking
2. StationDefinition：serviceDuration、blockingClosure、preDockBuffers
3. AgentMissionState：WaitingDockAdmission / InService
4. DockAdmissionController：
   - 如果 blockingClosure 上有近期車輛要通過，就 hold 在 buffer
   - 否則 grant docking + service reservation
5. InService 車不可 StepAside
```

這一版就能解你說的核心問題。

---

## 第二版：交通影響分析

加入：

```text
1. affected vehicles detection
2. blocked zone heatmap
3. pass-first batch
4. priority-aware admission
5. service deadline
```

這樣就能做到：

```text
讓幾台後車先過
再放 AGV 進工位
```

---

## 第三版：全局排程

再做：

```text
1. 多工位任務排序
2. 多 AGV 任務分配
3. CNC 等料成本
4. AGV 空跑成本
5. buffer 容量約束
6. 長時間 service reservation
```

這一版就是真正的 FMS Dispatcher。

---

# 最終結論

先生，你的意思我完全明白，而且這是比「到達後不要永久停車」更準確的工業答案。

正確模型應該是：

```text
AGV 到工位前：
    可移動，可等待，可讓路

AGV 進入工位但未開始：
    仍可中止 / 退出，但有成本

AGV 開始作業後：
    不可移動，視為長時間硬佔用

所以：
    系統必須在開始作業前做全局准入控制
```

也就是：

> **不要讓車先進去再想辦法挪；
> 要在車進去之前判斷它能不能進、什麼時候進、進去後會堵誰、要不要先放別的車過。**

這個「統籌者」就是你後續最需要做的東西：
**Dispatcher + Station Scheduler + Dock Admission Controller**。

這層做出來後，SwarmRoute 才真正從 MAPF solver 變成工廠級 AGV/FMS 交通大腦。
