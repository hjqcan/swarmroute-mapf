# SwarmRoute Web (多機路徑規劃模擬前端)

> 简体中文 · English version: [README.md](README.md)

_一个调度台 Web 应用：配置场地 + AGV 车队，运行真实的多机器人路径规划引擎，并回放车队无碰撞地从 A 行驶到 B。_

SwarmRoute Web 是 SwarmRoute MAPF（多智能体路径搜索）系统的基于浏览器的模拟器 / 可视化工具。你设定一个栅格场地尺寸和一个 AGV 数量，按下 **Run**，.NET 引擎便会为每个 AGV 规划路径并驱动其抵达目标。结果会在一个动画场地画布上回放，并配有同步的时空 **时空预约带**，让无碰撞保证在视觉上不言自明。

---

## 1. 用途

- **配置** 一个 `width × height` 栅格场地以及 AGV 数量（可选 RNG 种子）—— `src/components/ControlRail.tsx`。
- **运行** 针对后端引擎的一次模拟 —— `simStore.run()` → `POST /api/simulation/run`。
- **观看** 每个 AGV 沿其规划路径从起点（`A`）行驶到目标（`B`），标记会在引擎 tick 之间平滑插值 —— `src/components/FieldCanvas.tsx`。
- **验证** 引擎是否诚实：时空预约带展示了 _谁 · 在哪里 · 在何时_，因此你可以确认任意两个 AGV 永不在同一 tick 列中占据同一个控制点(CP) —— `src/components/ReservationRibbon.tsx`。运行状态横幅（`Completed` / `CollisionDetected` / `DidNotConverge`）即为裁定结果 —— `src/components/TelemetryColumn.tsx`。

这是一个面向后端规划器的 **验证 / 可观测性工具**,而非生产级车队仪表盘。后端在构造上即无碰撞;碰撞可视化作为回归 / 安全指示器而存在(参见 [说明](#9-说明))。

---

## 2. 技术栈与约定

| 关注点 | 选型 |
| --- | --- |
| 框架 | **React 19**（`react`、`react-dom` `^19.1`),采用自动 JSX 运行时 |
| 语言 | **TypeScript 5.8**,`strict` 模式完全开启(`tsconfig.json`:`noUnusedLocals`、`noImplicitReturns`、`strictNullChecks`,……) |
| 构建 / 开发 | **Vite 7** —— 一个标准 Web 应用(非 Electron)。`vite.config.ts` |
| 样式 | **Tailwind CSS 3**(`tailwind.config.js`、`postcss.config.js`)+ 以 CSS 变量形式提供的设计令牌(`src/assets/styles/index.css`) |
| UI 控件 | **Ant Design 6**,通过 `src/App.tsx` 中单一的 `ConfigProvider` 设定主题。AntD **仅用于表单控件**(Button、Slider、InputNumber、Segmented)—— 所有布局 / 视觉均为 Tailwind + 原生画布 |
| 状态 | **Zustand 5**(`src/store/simStore.ts`、`src/store/system.ts`),配合 `devtools`(语言偏好另加 `persist`) |
| i18n | **react-intl 7** —— `zh-CN`(默认)与 `en-US`(`src/lang/`) |
| 图标 | **lucide-react** |
| 字体 | **Space Grotesk**(展示)、**Inter**(正文)、**JetBrains Mono**(数据),通过 `@fontsource/*` 自托管并在 `src/main.tsx` 中导入 |
| 包管理器 | **npm**(存在 `package-lock.json`) |

**约定**

- **路径别名** `@/` → `src/`(`vite.config.ts` 的 `resolve.alias` + `tsconfig.json` 的 `paths`)。可写作 `@/store/simStore`、`@/utils/palette` 等。
- **API base** 为 `VITE_API_BASE`,在 `.env.development` 中设为 `/api`。在开发环境下,Vite 将 `/api` 代理至 `http://localhost:5062`(即 .NET Host),因此浏览器永远不会触发 CORS —— `vite.config.ts` 的 `server.proxy`。
- **无 2D 图表库。** 场地和预约带均使用原生 HTML5 `<canvas>` 配合 `requestAnimationFrame` 绘制。辅助函数位于 `src/utils/canvas.ts`。
- **严格的 lint**,通过 flat-config 的 ESLint 9 + Prettier(`eslint.config.mjs`);保留一份遗留的 `.eslintrc.cjs` 以兼容较旧的编辑器工具。

---

## 3. 快速上手

### 前置条件

1. **Node.js ≥ 20** 与 **npm**(仓库锁定了较新的 `@types/node` 24;为 Vite 7 推荐使用 Node 20+)。
2. **.NET Host 必须运行在 `http://localhost:5062`。** 这是前端代理所指向的模拟 API。在仓库根目录下:

   ```bash
   dotnet run --project host/SwarmRoute.Host
   ```

   Host 监听 `http://localhost:5062`(以及 `https://localhost:7040`)—— 参见 `host/SwarmRoute.Host/Properties/launchSettings.json`。模拟端点(`api/simulation/run`)**无需数据库**;它针对每个请求在内存中运行真实的 PathPlanning + TrafficControl + Coordination 引擎(`host/SwarmRoute.Host/Controllers/SimulationController.cs`、`host/SwarmRoute.Host/Program.cs`)。

### 安装与运行

```bash
cd host/swarmroute-web
npm install

npm run dev        # Vite dev server on http://localhost:5173, /api proxied to :5062
```

打开 `http://localhost:5173`。如果 Host 未运行,**Run** 会显示一条友好的 "Cannot reach the simulation service" 错误(开发代理返回 502)—— `src/api/client.ts`。

### 其他脚本(`package.json`)

```bash
npm run build      # tsc --noEmit (typecheck) then vite build  → dist/
npm run preview    # serve the production build locally
npm run typecheck  # tsc --noEmit only
npm run lint       # eslint . --ext .ts,.tsx,.js,.jsx
npm run format     # prettier --write .
```

> 注意:`npm run preview` 会提供静态构建产物,但 **不会** 代理 `/api`。要让已构建的包能访问到引擎,需将其部署在某个会把 `/api` 转发到 Host 的服务之后(或在构建时将 `VITE_API_BASE` 指向一个绝对的 Host URL)。

---

## 4. 项目结构

```
host/swarmroute-web/
├─ index.html                 # lang="zh-CN", dark color-scheme, #root mount
├─ vite.config.ts             # @/ alias; dev server :5173 + /api → :5062 proxy
├─ tailwind.config.js         # Dispatcher's-console palette + fonts + shadows
├─ tsconfig.json              # strict TS, @/* → ./src/*
├─ eslint.config.mjs          # flat ESLint 9 config (+ legacy .eslintrc.cjs)
├─ .env.development           # VITE_API_BASE=/api
└─ src/
   ├─ main.tsx                # createRoot + StrictMode; @fontsource font imports
   ├─ App.tsx                 # AntD ConfigProvider (dark theme) + IntlProvider shell
   ├─ vite-env.d.ts
   ├─ api/
   │  ├─ client.ts            # fetch wrapper: timeout (AbortController), JSON, HttpError + ProblemDetails
   │  └─ simulation.ts        # runSimulation(req) → POST /simulation/run
   ├─ store/
   │  ├─ simStore.ts          # params · run lifecycle · playback cursor/speed/playing (Zustand)
   │  └─ system.ts            # language preference (persisted to localStorage)
   ├─ types/
   │  └─ index.ts             # API contract types (mirror SimulationResultDto)
   ├─ utils/
   │  ├─ palette.ts           # structural COLORS + AGENT_HUES ring + alpha helpers
   │  ├─ canvas.ts            # HiDPI canvas setup + grid→pixel projector
   │  └─ simModel.ts          # interpolatePositions, occupancyByAgent, tickAtCursor/tickRange/collisionFrameIndex
   ├─ hooks/
   │  ├─ useElementSize.ts    # ResizeObserver-based element measurement
   │  ├─ usePlaybackLoop.ts   # single RAF clock that advances the store cursor
   │  └─ usePrefersReducedMotion.ts
   ├─ components/
   │  ├─ AppHeader.tsx        # product mark + zh-CN ⇄ en-US toggle
   │  ├─ ControlRail.tsx      # width/height/agvCount/seed form + Run button
   │  ├─ FieldCanvas.tsx      # HERO: grid, lanes, planned paths, A/B markers, animated AGVs
   │  ├─ PlaybackControls.tsx # play/pause · scrub slider · speed (0.5/1/2/4×)
   │  ├─ ReservationRibbon.tsx# SIGNATURE: space-time chart with a synced playhead
   │  └─ TelemetryColumn.tsx  # status banner · metrics · per-agent live state
   ├─ pages/
   │  └─ simulator/index.tsx  # the single page; mounts usePlaybackLoop() + the 3-column layout
   ├─ lang/
   │  ├─ index.ts             # languageMessage map
   │  ├─ zh-cn.json           # default
   │  └─ en.json
   └─ assets/styles/index.css # Tailwind layers + CSS-var tokens + reduced-motion + focus rings
```

本应用是 **单页面**(`pages/simulator/index.tsx`)—— 没有路由。布局(顶部为 `AppHeader`;左侧为控制栏,中间为场地 + 播放 + 预约带,右侧为遥测)在低于 `lg` 断点时会纵向堆叠。

---

## 5. 数据层与 API 契约

### Fetch 封装 —— `src/api/client.ts`

一个小巧、无依赖的 `fetch` 封装:

- 从 `import.meta.env.VITE_API_BASE` 读取 `API_BASE`(默认 `/api`);`buildUrl` 会为相对路径添加前缀,绝对 URL 则原样透传。
- 通过 `AbortController` 实现 15 秒超时(`DEFAULT_TIMEOUT_MS`),并桥接一个外部 `signal`。
- **响应体本身就是 DTO** —— 这些端点 **直接** 返回 camelCase JSON,而非包裹在 `{ code, msg, data }` 中。`request<T>()` 会按原样解析并返回。
- 在非 2xx 时抛出 `HttpError { status, detail }`,当存在 ASP.NET Core 的 `ProblemDetails` 时优先采用其 `title`/`detail`/`errors`。网络故障(例如代理无法访问 Host)会抛出 `status: 0` 且带双语消息的 `HttpError`。
- 导出带类型的 `get` / `post` 辅助函数。

### 唯一的调用 —— `src/api/simulation.ts`

```ts
export function runSimulation(req: SimulationRequest): Promise<SimulationResult> {
  return post<SimulationResult>('/simulation/run', req)
}
```

### 请求 / 响应结构 —— `src/types/index.ts`

这些类型与 `SwarmRoute.Simulation.Application.SimulationResultDto` 完全一致。

**Request** —— `SimulationRequest`:

```ts
{ width: number; height: number; agvCount: number; seed?: number }
```

**Response** —— `SimulationResult`:

```ts
{
  field: { width, height, sites: Site[], lanes: Lane[] }            // FieldDto
  agents: AgentDto[]   // { id, startSiteId, goalSiteId, colorIndex, pathSiteIds: string[] }
  timeline: {
    tickCount: number
    frames: { tick: number; positions: Position[] }[]               // one frame per tick
  }
  stats: {
    ticks, collisions, arrived, replans,
    status: 'Completed' | 'CollisionDetected' | 'DidNotConverge',
    collisionTick: number | null,
    collisionAgentIds: string[] | null
  }
}
```

- `Site` = `{ id, x (col), y (row), type }`;`Lane` = `{ id, from, to }`(有向边)。
- `Position` = `{ agentId, siteId, x, y, state }`,其中 `state ∈ { 'Waiting', 'Moving', 'Arrived' }`(`RunState`)。
- **重要:** `frame.tick` 是 **引擎 tick 编号**,它不必等于该帧在数组中的索引。前端始终从 `frame.tick` 读取标签,而绝不从游标索引读取(参见 §7)。

### 自动播放 —— `src/store/simStore.ts`

在一次成功的 `run()` 之后,store 会将 `cursor: 0` 复位,并设置 `playing: result.timeline.frames.length > 1`,因此一次完成的运行会立即开始回放,无需第二次点击即可看到验证过程。

---

## 6. 状态(Zustand)

### `src/store/simStore.ts` —— 模拟 store

一个 store 承载三项关注点:

**请求表单**
- `params: SimulationRequest`(默认 `{ width: 10, height: 8, agvCount: 6 }` —— `DEFAULT_PARAMS`)。
- `setParam(key, value)` —— 按键(per-key)的带类型 setter;`randomizeSeed()` —— 设置一个随机种子。

**运行生命周期**
- `result: SimulationResult | null`、`loading: boolean`、`error: string | null`。
- `run()` —— 设置 `loading`,调用 `runSimulation(params)`,存储结果(并自动播放),或将一个 `HttpError`(含 `detail`)映射为一个双语 `error` 字符串。

**播放**
- `cursor: number` —— 一个 **基于 0 的、覆盖 `timeline.frames` 的浮点索引**。整数部分是"起始"帧;小数部分是朝下一帧的插值 `t`。
- `playing: boolean`、`speed: PlaybackSpeed`(`0.5 | 1 | 2 | 4`)。
- `setPlaying` / `togglePlaying` / `setSpeed`。
- `setCursor(c)` —— 钳制到 `[0, frames.length - 1]`。
- `advance(dtSeconds)` —— 播放时由 RAF 循环调用:`cursor += dt * TICKS_PER_SECOND(=2) * speed`;到达最后一帧时停留其上并暂停。

### `src/store/system.ts` —— UI 偏好

`lang: 'zh-CN' | 'en-US'`(默认 `zh-CN`),持久化到 `localStorage`(`persist` 中间件,`partialize` → `{ lang }`)。`App.tsx` 读取它来同时驱动 AntD 的 locale 与 `IntlProvider`。

---

## 7. 渲染

中间列由两个原生画布可视化加上一个传输控件组成,全部由 **同一个共享的播放时钟** 驱动。

### 唯一的时钟 —— `src/hooks/usePlaybackLoop.ts`

在 `pages/simulator/index.tsx` 中挂载一次。一个单一的 `requestAnimationFrame` 循环,在 `playing` 时按真实流逝的 `dt`(钳制到 0.1 秒,以免后台标签页发生跳变)推进 store 的游标。由于场地和预约带读取的是同一个 `cursor`,它们会以完美的同步节奏一同动画。

### FieldCanvas(主角)—— `src/components/FieldCanvas.tsx`

一个原生 HTML5 `<canvas>`(通过 `setupHiDpiCanvas` 进行 HiDPI 缩放),在一次 `draw()` 过程中绘制:

1. **车道** —— 淡淡的有向边(`field.lanes`)。
2. **规划路径** —— 沿 `agent.pathSiteIds` 的逐 agent 折线,以该 agent 的色相低 alpha 绘制(`trailFor`)。
3. **站点节点** —— 在每个控制点(CP)处绘制的小型、以面板色填充的圆点。
4. **A / B 标记** —— 起点为标注 `A` 的彩色环;目标为标注 `B` 的旋转菱形,使两者一眼即可区分。
5. **动画 AGV 标记** —— 位置来自 `interpolatePositions(result, cursor, snap)`;`Moving` 的 AGV 带有柔和光晕,`Waiting` 的 AGV 中心为空,且各自携带一个简短的 id 标签。
6. **碰撞闪烁**(仅作回归指示器)—— 当停留在 `collisionFrameIndex` 之上 / 之后时,涉事 AGV 及其争用的控制点(CP)会闪烁红色。

**RAF 模型。** `draw()` 读取 **实时的** store 游标(`useSimStore.getState().cursor`),因此 React **不会** 在每帧重渲染 —— 只有画布会重绘。在 `playing` 时,一个常驻的 RAF 循环会每帧重绘;在暂停时,每当 `cursor`(拖动)、尺寸、结果或动态偏好发生变化时,它会重绘一次。该循环刻意依赖于 `[playing, draw]`,而 **不** 依赖 `cursor` —— 依赖 `cursor` 会以约 60 次/秒的频率拆除并重建循环,从而冻结场地(在 `FieldCanvas.tsx:203` 处有内联说明)。

### ReservationRibbon(标志性元素)—— `src/components/ReservationRibbon.tsx`

一张时空图:**每个 AGV 一行**(以其色相),一条水平的 **tick 轴**,以及每个 tick 一个单元格,显示该 AGV 在那一刻占据的控制点(`occupancyByAgent`)。停留(与上一 tick 相同的 CP)渲染得更暗;CP 变化则渲染得更亮并带有一个小连接符。一个暖琥珀色 **playhead** 定位在 `xForTick(liveCursor)` —— 与场地相同的那个 store 游标 —— 因此场地与预约带如同一体般移动。它是"无碰撞"背后 _谁 · 在哪里 · 在何时_ 的证据:**任意两行永不在同一列中填入同一个控制点。** 该画布是一个可交互的 `role="slider"`:点击 / 拖动会拖动游标(并暂停)。它采用与场地相同的 常驻 RAF / 暂停时只绘制一次 模型。

### PlaybackControls —— `src/components/PlaybackControls.tsx`

播放 / 暂停(当停留在末尾时会变为一个 **Replay** 按钮)、一个绑定到 `cursor` 的精细拖动 `Slider`(`step 0.01`)、一个 `current / last` 的 tick 读数,以及一个 `Segmented` 速度选择器(`0.5/1/2/4×`)。拖动会暂停播放并移动所有内容。

### 游标 ↔ 引擎-tick 映射 —— `src/utils/simModel.ts`

由于 **游标是一个帧数组索引**,而引擎在 `frame.tick` 中为 tick 编号(且 `stats.collisionTick` 是一个引擎 tick),UI 必须在两者之间转换,否则标签和(罕见的)碰撞高亮会落到错误的列上。三个辅助函数完成此事:

- **`tickAtCursor(result, cursor)`** —— 某个游标处所显示的引擎 tick;读取 `frames[round(cursor)].tick`。由 `PlaybackControls` 使用,使其计数器与预约带轴及碰撞横幅("第 N 节拍")相匹配。
- **`tickRange(result)`** —— 为 `current / total` 读数提供首 / 末引擎 tick 编号。
- **`collisionFrameIndex(result)`** —— 通过 `frames.findIndex(f => f.tick === collisionTick)` 将 `stats.collisionTick`(引擎 tick)映射到其 **帧数组索引**,使场地闪烁和预约带的红色块 / 标记在正确的游标位置触发。除非 `status === 'CollisionDetected'`,否则返回 `null`。

`interpolatePositions(result, cursor, snap)` 完成逐帧的混合:整数部分 =“起始”帧,小数部分 = 朝下一帧的混合;当 `snap`(减少动态)时,它会取整到最近的帧且不做混合。状态是离散的(取自下取整帧)。

共享的画布数学位于 `src/utils/canvas.ts`:`makeProjector(cols, rows, w, h, margin)` 将栅格 `(col, row)` → 以均匀间距居中的像素;`setupHiDpiCanvas` 按 `devicePixelRatio` 设置后备存储的尺寸,并返回一个预先按 CSS 像素缩放的上下文。

---

## 8. 设计系统

**“调度台”** —— 一种深色、仪表盘式的美学。令牌在 `src/assets/styles/index.css` 中作为 CSS 变量定义一次,在 `tailwind.config.js` 中镜像一份,并在 `src/utils/palette.ts`(`COLORS`)中第三次镜像 —— 因为原生 Canvas2D 无法读取 CSS 变量。

| 令牌 | 取值 | 用途 |
| --- | --- | --- |
| `base` | `#0B1220` | 应用背景 / 画布背景 |
| `panel` | `#141C2B` | 面板、卡片、预约带背景 |
| `hairline` | `#25324A` | 边框、车道、网格线 |
| `text-primary` | `#C7D2E2` | 主要文本 |
| `text-muted` | `#6B7A93` | 标签、次要文本 |
| `accent` | `#FFB020`(暖琥珀色) | 主操作、playhead、焦点环 |
| `danger` | `#FF5C5C` | 仅用于碰撞 / 警告 |

**Agent 色相环** —— `palette.ts` 中的 `AGENT_HUES`:一个 24 项、暖 / 冷交替的调色板,按 `colorIndex % length`(`hueFor`)分配,使得最多 24 个 AGV 各自呈现为一种独特、互不冲突的色相。`withAlpha` / `trailFor` 派生出半透明的轨迹 / 填充颜色。

**排版** —— Space Grotesk(展示:标题、A/B 标签)、Inter(正文)、JetBrains Mono(表格化数据:tick 计数器、id、指标)。通过 `@fontsource/*` 自托管(在 `main.tsx` 中导入)。

**AntD 主题** —— `src/App.tsx` 中单一的 `ConfigProvider` 运行 `theme.darkAlgorithm` 并将令牌覆写为该调色板(`colorPrimary`/`colorInfo` → 琥珀色,`colorError` → danger,背景 → base/panel,边框 → hairline),外加针对 Button/Slider/Segmented/InputNumber 的逐组件覆写。其结果是:控件继承调度台调色板,**绝不出现 AntD 默认的蓝色**。AntD 的 locale 也在此处切换(`zh_CN` / `en_US`)。

**无障碍**
- **减少动态** —— `usePrefersReducedMotion()`(实时 `matchMedia`)使 `interpolatePositions` 取整到离散帧(不做混合)并停止环境光晕;运行仍会播放。CSS 也会在 `prefers-reduced-motion: reduce` 下全局禁用过渡 / 动画(`index.css`)。
- **键盘 / 焦点** —— 所有可交互元素上有一个可见的琥珀色焦点环(`index.css` 中的 `:focus-visible`);预约带是一个可聚焦的 `role="slider"`,带有 `aria-valuemin/max/now`。
- **i18n** —— 每一个面向用户的字符串都流经 `react-intl`(`intl.formatMessage`);`zh-CN`(默认)⇄ `en-US` 可从页头(`AppHeader.tsx`)切换并被持久化。

---

## 9. 说明

- **后端引擎在构造上即无碰撞。** 一次正常运行返回 `status: 'Completed'` 且 `collisions: 0`。时空预约带的存在是为了让该性质 _可见且可审计_,而非在正常条件下查找故障。
- **碰撞可视化是一个回归 / 安全指示器。** 红色场地闪烁、虚线碰撞标记以及 `CollisionDetected` 横幅,只有在引擎发生回归(或针对刻意设计的对抗性输入)时才会出现。`collisionFrameIndex` 管线确保该高亮一旦触发即正确无误。`DidNotConverge` 同样用于标记一次耗尽其 tick 预算的运行。
- **该端点无数据库。** `POST /api/simulation/run` 针对每个请求在内存中运行真实的 PathPlanning + TrafficControl + Coordination 引擎(`host/SwarmRoute.Host/Program.cs`),因此唯一的运行时前置条件就是 Host 进程位于 `:5062`。

---

## 后端 API 契约(被依赖)

```
POST /api/simulation/run        (Host on http://localhost:5062; dev-proxied from /api)
Content-Type: application/json

Request   { "width": number, "height": number, "agvCount": number, "seed"?: number }
          Constraint: width*height >= 2*agvCount  (the frontend pre-validates this in ControlRail)

200 OK    SimulationResult (camelCase JSON, returned DIRECTLY — no { code, msg, data } envelope):
          {
            field:    { width, height, sites: [{id,x,y,type}], lanes: [{id,from,to}] },
            agents:   [{ id, startSiteId, goalSiteId, colorIndex, pathSiteIds: string[] }],
            timeline: { tickCount, frames: [{ tick, positions: [{agentId,siteId,x,y,state}] }] },
            stats:    { ticks, collisions, arrived, replans,
                        status: "Completed"|"CollisionDetected"|"DidNotConverge",
                        collisionTick: number|null, collisionAgentIds: string[]|null }
          }
          state ∈ { "Waiting", "Moving", "Arrived" }

400       application/problem+json  ProblemDetails { title, detail, errors? }  (invalid request)
```
