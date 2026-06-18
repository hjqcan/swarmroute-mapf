# SwarmRoute Web (多機路徑規劃模擬前端)

_A dispatcher's-console web app to configure a field + AGV fleet, run the real multi-robot path-planning engine, and replay the fleet travelling A→B collision-free._

SwarmRoute Web is the browser-based simulator / visualizer for the SwarmRoute MAPF (multi-agent path-finding) system. You set a grid field size and an AGV count, press **Run**, and the .NET engine plans and drives every AGV to its goal. The result is replayed on an animated field canvas with a synchronized space-time **reservation ribbon** that makes the collision-free guarantee visually self-evident.

---

## 1. Purpose

- **Configure** a `width × height` grid field and a number of AGVs (with an optional RNG seed) — `src/components/ControlRail.tsx`.
- **Run** one simulation against the backend engine — `simStore.run()` → `POST /api/simulation/run`.
- **Watch** each AGV travel from its start (`A`) to its goal (`B`) along its planned path, with markers interpolated smoothly between engine ticks — `src/components/FieldCanvas.tsx`.
- **Verify** the engine is honest: the reservation ribbon shows _who · where · when_, so you can confirm no two AGVs ever occupy the same control point in the same tick column — `src/components/ReservationRibbon.tsx`. The run status banner (`Completed` / `CollisionDetected` / `DidNotConverge`) is the verdict — `src/components/TelemetryColumn.tsx`.

This is a **verification / observability tool** for the backend planner, not a production fleet dashboard. The backend is collision-free by construction; the collision visualization exists as a regression / safety indicator (see [Notes](#9-notes)).

---

## 2. Stack & conventions

| Concern | Choice |
| --- | --- |
| Framework | **React 19** (`react`, `react-dom` `^19.1`) with the automatic JSX runtime |
| Language | **TypeScript 5.8**, `strict` mode fully on (`tsconfig.json`: `noUnusedLocals`, `noImplicitReturns`, `strictNullChecks`, …) |
| Build / dev | **Vite 7** — a standard web app (not Electron). `vite.config.ts` |
| Styling | **Tailwind CSS 3** (`tailwind.config.js`, `postcss.config.js`) + design tokens as CSS vars (`src/assets/styles/index.css`) |
| UI controls | **Ant Design 6**, themed via a single `ConfigProvider` in `src/App.tsx`. AntD is used **only for form controls** (Button, Slider, InputNumber, Segmented) — all layout/visuals are Tailwind + raw canvas |
| State | **Zustand 5** (`src/store/simStore.ts`, `src/store/system.ts`) with `devtools` (+ `persist` for language) |
| i18n | **react-intl 7** — `zh-CN` (default) and `en-US` (`src/lang/`) |
| Icons | **lucide-react** |
| Fonts | **Space Grotesk** (display), **Inter** (body), **JetBrains Mono** (data), self-hosted via `@fontsource/*` and imported in `src/main.tsx` |
| Package manager | **npm** (`package-lock.json` present) |

**Conventions**

- **Path alias** `@/` → `src/` (`vite.config.ts` `resolve.alias` + `tsconfig.json` `paths`). Import as `@/store/simStore`, `@/utils/palette`, etc.
- **API base** is `VITE_API_BASE`, set to `/api` in `.env.development`. In dev, Vite proxies `/api` → `http://localhost:5062` (the .NET Host), so the browser never hits CORS — `vite.config.ts` `server.proxy`.
- **No 2D charting library.** Both the field and the ribbon are drawn on raw HTML5 `<canvas>` with `requestAnimationFrame`. Helpers live in `src/utils/canvas.ts`.
- **Strict lint** via flat-config ESLint 9 + Prettier (`eslint.config.mjs`); a legacy `.eslintrc.cjs` is kept for older editor tooling.

---

## 3. Getting started

### Prerequisites

1. **Node.js ≥ 20** and **npm** (the repo pins modern `@types/node` 24; Node 20+ is recommended for Vite 7).
2. **The .NET Host must be running on `http://localhost:5062`.** This is the simulation API the frontend proxies to. From the repo root:

   ```bash
   dotnet run --project host/SwarmRoute.Host
   ```

   The Host listens on `http://localhost:5062` (and `https://localhost:7040`) — see `host/SwarmRoute.Host/Properties/launchSettings.json`. The simulation endpoint (`api/simulation/run`) needs **no database**; it runs the real PathPlanning + TrafficControl + Coordination engine in-memory per request (`host/SwarmRoute.Host/Controllers/SimulationController.cs`, `host/SwarmRoute.Host/Program.cs`).

### Install & run

```bash
cd host/swarmroute-web
npm install

npm run dev        # Vite dev server on http://localhost:5173, /api proxied to :5062
```

Open `http://localhost:5173`. If the Host is not running, **Run** surfaces a friendly "Cannot reach the simulation service" error (the dev proxy returns 502) — `src/api/client.ts`.

### Other scripts (`package.json`)

```bash
npm run build      # tsc --noEmit (typecheck) then vite build  → dist/
npm run preview    # serve the production build locally
npm run typecheck  # tsc --noEmit only
npm run lint       # eslint . --ext .ts,.tsx,.js,.jsx
npm run format     # prettier --write .
```

> Note: `npm run preview` serves the static build but does **not** proxy `/api`. For a built bundle to reach the engine, serve it behind something that forwards `/api` to the Host (or point `VITE_API_BASE` at an absolute Host URL at build time).

---

## 4. Project structure

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

The app is a **single page** (`pages/simulator/index.tsx`) — there is no router. Layout (`AppHeader` on top; left control rail, center field+playback+ribbon, right telemetry) stacks vertically below the `lg` breakpoint.

---

## 5. Data layer & API contract

### Fetch wrapper — `src/api/client.ts`

A small, dependency-free wrapper around `fetch`:

- Reads `API_BASE` from `import.meta.env.VITE_API_BASE` (default `/api`); `buildUrl` prefixes relative paths, passes absolute URLs through.
- 15 s timeout via `AbortController` (`DEFAULT_TIMEOUT_MS`), bridges an external `signal`.
- **The response body IS the DTO** — these endpoints return camelCase JSON **directly**, not wrapped in `{ code, msg, data }`. `request<T>()` parses and returns it as-is.
- On non-2xx it throws `HttpError { status, detail }`, preferring an ASP.NET Core `ProblemDetails` `title`/`detail`/`errors` when present. Network failures (e.g. proxy can't reach the Host) throw `HttpError` with `status: 0` and a bilingual message.
- Exports typed `get` / `post` helpers.

### The one call — `src/api/simulation.ts`

```ts
export function runSimulation(req: SimulationRequest): Promise<SimulationResult> {
  return post<SimulationResult>('/simulation/run', req)
}
```

### Request / response shape — `src/types/index.ts`

These types mirror `SwarmRoute.Simulation.Application.SimulationResultDto` exactly.

**Request** — `SimulationRequest`:

```ts
{ width: number; height: number; agvCount: number; seed?: number; planner?: 'Dijkstra' | 'Sipp' }
```

**Response** — `SimulationResult`:

```ts
{
  field: { width, height, sites: Site[], lanes: Lane[] }            // FieldDto
  agents: AgentDto[]   // { id, startSiteId, goalSiteId, colorIndex, pathSiteIds, remainingSiteIds }
  timeline: {
    tickCount: number
    frames: { tick: number; positions: Position[] }[]               // one frame per tick
  }
  stats: {
    ticks, collisions, arrived, replans,
    status: 'Completed' | 'CollisionDetected' | 'DidNotConverge',
    collisionTick: number | null,
    collisionAgentIds: string[] | null,
    redirects: number,
    recoveries: number,
    flowtimeTicks: number
  }
}
```

- `Site` = `{ id, x (col), y (row), type }`; `Lane` = `{ id, from, to }` (directed edge).
- `PlannerKind` = `'Dijkstra' | 'Sipp'`; the UI defaults to SIPP and keeps Dijkstra available for A/B comparison.
- `AgentDto.pathSiteIds` is the occupied trail; `remainingSiteIds` is the road still ahead when a run did not finish.
- `Position` = `{ agentId, siteId, x, y, state }` where `state ∈ { 'Waiting', 'Moving', 'Arrived' }` (`RunState`).
- **Important:** `frame.tick` is the **engine tick number**, which need not equal the frame's array index. The frontend always reads labels from `frame.tick`, never from the cursor index (see §7).

### Auto-play — `src/store/simStore.ts`

On a successful `run()`, the store resets `cursor: 0` and sets `playing: result.timeline.frames.length > 1`, so a finished run starts replaying immediately and the verification is visible without a second click.

---

## 6. State (Zustand)

### `src/store/simStore.ts` — the simulation store

One store holds three concerns:

**Request form**
- `params: SimulationRequest` (default `{ width: 10, height: 8, agvCount: 6, planner: 'Sipp' }` — `DEFAULT_PARAMS`).
- `setParam(key, value)` — typed per-key setter; `randomizeSeed()` — sets a random seed.

**Run lifecycle**
- `result: SimulationResult | null`, `loading: boolean`, `error: string | null`.
- `run()` — sets `loading`, calls `runSimulation(params)`, stores the result (and auto-plays), or maps an `HttpError` (including `detail`) to a bilingual `error` string.

**Playback**
- `cursor: number` — a **0-based float index over `timeline.frames`**. The integer part is the "from" frame; the fraction is the interpolation `t` toward the next frame.
- `playing: boolean`, `speed: PlaybackSpeed` (`0.5 | 1 | 2 | 4`).
- `setPlaying` / `togglePlaying` / `setSpeed`.
- `setCursor(c)` — clamps to `[0, frames.length - 1]`.
- `advance(dtSeconds)` — called by the RAF loop while playing: `cursor += dt * TICKS_PER_SECOND(=2) * speed`; on reaching the last frame it settles on it and pauses.

### `src/store/system.ts` — UI preferences

`lang: 'zh-CN' | 'en-US'` (default `zh-CN`), persisted to `localStorage` (`persist` middleware, `partialize` → `{ lang }`). `App.tsx` reads it to drive both AntD's locale and `IntlProvider`.

---

## 7. Rendering

The center column is two raw-canvas visualizations plus a transport, all driven from **one shared playback clock**.

### One clock — `src/hooks/usePlaybackLoop.ts`

Mounted once in `pages/simulator/index.tsx`. A single `requestAnimationFrame` loop that, while `playing`, advances the store cursor by real elapsed `dt` (clamped to 0.1 s so a backgrounded tab doesn't jump). Because both the field and the ribbon read the same `cursor`, they animate in perfect lockstep.

### FieldCanvas (the hero) — `src/components/FieldCanvas.tsx`

A raw HTML5 `<canvas>` (HiDPI-scaled via `setupHiDpiCanvas`) that, in one `draw()` pass, paints:

1. **Lanes** — faint directed edges (`field.lanes`).
2. **Planned paths** — per-agent polylines along `agent.pathSiteIds`, drawn in the agent's hue at low alpha (`trailFor`).
3. **Site nodes** — small panel-filled circles at every control point.
4. **A / B markers** — start as a hued ring labelled `A`; goal as a rotated diamond labelled `B`, so the two read differently at a glance.
5. **Animated AGV markers** — positions come from `interpolatePositions(result, cursor, snap)`; a `Moving` AGV gets a soft halo, a `Waiting` one a hollow center, and each carries a short id label.
6. **Collision flash** (regression indicator only) — when parked on/after `collisionFrameIndex`, the involved AGVs and their contended control point blink red.

**RAF model.** `draw()` reads the **live** store cursor (`useSimStore.getState().cursor`) so React is **not** re-rendered per frame — only the canvas repaints. While `playing`, a persistent RAF loop repaints every frame; while paused, it repaints once whenever `cursor` (scrub), size, result, or motion preference changes. The loop deliberately depends on `[playing, draw]` and **not** on `cursor` — depending on `cursor` would tear the loop down ~60×/s and freeze the field (documented inline at `FieldCanvas.tsx:203`).

### ReservationRibbon (the signature element) — `src/components/ReservationRibbon.tsx`

A space-time chart: **one row per AGV** (in its hue), a horizontal **tick axis**, and one cell per tick showing which control point that AGV occupies (`occupancyByAgent`). Holds (same CP as the previous tick) render dimmer; CP changes render brighter with a small connector. A warm-amber **playhead** is positioned at `xForTick(liveCursor)` — the same store cursor as the field — so the field and ribbon move as one. It is the _who · where · when_ evidence behind "collision-free": **no two rows ever fill the same control point in the same column.** The canvas is an interactive `role="slider"`: click/drag scrubs the cursor (and pauses). Uses the same persistent-RAF / paint-once-while-paused model as the field.

### PlaybackControls — `src/components/PlaybackControls.tsx`

Play/pause (which becomes a **Replay** button when parked at the end), a fine scrub `Slider` (`step 0.01`) bound to `cursor`, a `current / last` tick readout, and a `Segmented` speed selector (`0.5/1/2/4×`). Scrubbing pauses playback and moves everything.

### The cursor ↔ engine-tick mapping — `src/utils/simModel.ts`

Because the **cursor is a frame-array index** but the engine numbers ticks in `frame.tick` (and `stats.collisionTick` is an engine tick), the UI must translate between the two or labels and the (rare) collision highlight land on the wrong column. Three helpers do this:

- **`tickAtCursor(result, cursor)`** — the displayed engine tick at a cursor; reads `frames[round(cursor)].tick`. Used by `PlaybackControls` so its counter matches the ribbon axis and the collision banner ("第 N 节拍").
- **`tickRange(result)`** — first/last engine tick numbers for the `current / total` readout.
- **`collisionFrameIndex(result)`** — maps `stats.collisionTick` (engine tick) to its **frame-array index** via `frames.findIndex(f => f.tick === collisionTick)`, so the field flash and the ribbon's red block/marker fire at the correct cursor position. Returns `null` unless `status === 'CollisionDetected'`.

`interpolatePositions(result, cursor, snap)` does the per-frame blend: integer part = "from" frame, fraction = blend toward the next; when `snap` (reduced motion) it rounds to the nearest frame and does not blend. State is discrete (taken from the floor frame).

Shared canvas math lives in `src/utils/canvas.ts`: `makeProjector(cols, rows, w, h, margin)` maps grid `(col, row)` → centered pixels with uniform spacing; `setupHiDpiCanvas` sizes the backing store for `devicePixelRatio` and returns a context pre-scaled to CSS pixels.

---

## 8. Design system

**"Dispatcher's console"** — a dark, instrument-panel aesthetic. Tokens are defined once as CSS variables in `src/assets/styles/index.css`, mirrored in `tailwind.config.js`, and mirrored a third time in `src/utils/palette.ts` (`COLORS`) because raw Canvas2D cannot read CSS vars.

| Token | Value | Use |
| --- | --- | --- |
| `base` | `#0B1220` | app background / canvas backdrop |
| `panel` | `#141C2B` | panels, cards, ribbon backdrop |
| `hairline` | `#25324A` | borders, lanes, gridlines |
| `text-primary` | `#C7D2E2` | primary text |
| `text-muted` | `#6B7A93` | labels, secondary text |
| `accent` | `#FFB020` (warm amber) | primary action, playhead, focus ring |
| `danger` | `#FF5C5C` | collision / warning only |

**Agent hue ring** — `AGENT_HUES` in `palette.ts`: a 24-entry warm/cool-alternating palette, assigned by `colorIndex % length` (`hueFor`), so up to 24 AGVs each read as a distinct, non-clashing hue. `withAlpha` / `trailFor` derive translucent trail/fill colours.

**Typography** — Space Grotesk (display: titles, A/B labels), Inter (body), JetBrains Mono (tabular data: tick counters, ids, metrics). Self-hosted via `@fontsource/*` (imported in `main.tsx`).

**AntD theming** — a single `ConfigProvider` in `src/App.tsx` runs `theme.darkAlgorithm` and overrides tokens to the palette (`colorPrimary`/`colorInfo` → amber, `colorError` → danger, backgrounds → base/panel, borders → hairline), plus per-component overrides for Button/Slider/Segmented/InputNumber. The result: controls inherit the console palette, **never default AntD blue**. AntD's locale is also switched here (`zh_CN` / `en_US`).

**Accessibility**
- **Reduced motion** — `usePrefersReducedMotion()` (live `matchMedia`) makes `interpolatePositions` snap to discrete frames (no blending) and stops the ambient halo; the run still plays. CSS also globally kills transitions/animations under `prefers-reduced-motion: reduce` (`index.css`).
- **Keyboard / focus** — a visible amber focus ring on all interactive elements (`:focus-visible` in `index.css`); the ribbon is a focusable `role="slider"` with `aria-valuemin/max/now`.
- **i18n** — every user-facing string flows through `react-intl` (`intl.formatMessage`); `zh-CN` (default) ⇄ `en-US` toggled from the header (`AppHeader.tsx`) and persisted.

---

## 9. Notes

- **The backend engine is collision-free by construction.** A normal run returns `status: 'Completed'` with `collisions: 0`. The reservation ribbon exists to make that property _visible and auditable_, not to find faults under nominal conditions.
- **The collision visualization is a regression / safety indicator.** The red field flash, the dashed collision marker, and the `CollisionDetected` banner only ever appear if the engine were to regress (or for a deliberately adversarial input). The `collisionFrameIndex` plumbing keeps that highlight correct should it ever fire. `DidNotConverge` similarly flags a run that exhausted its tick budget.
- **No database for this endpoint.** `POST /api/simulation/run` runs the real PathPlanning + TrafficControl + Coordination engine in-memory per request (`host/SwarmRoute.Host/Program.cs`), so the only runtime prerequisite is the Host process on `:5062`.

---

## Backend API contract (depended upon)

```
POST /api/simulation/run        (Host on http://localhost:5062; dev-proxied from /api)
Content-Type: application/json

Request   { "width": number, "height": number, "agvCount": number,
            "seed"?: number, "planner"?: "Dijkstra"|"Sipp" }
          Constraint: width*height >= 2*agvCount  (the frontend pre-validates this in ControlRail)

200 OK    SimulationResult (camelCase JSON, returned DIRECTLY — no { code, msg, data } envelope):
          {
            field:    { width, height, sites: [{id,x,y,type}], lanes: [{id,from,to}] },
            agents:   [{ id, startSiteId, goalSiteId, colorIndex,
                         pathSiteIds: string[], remainingSiteIds: string[] }],
            timeline: { tickCount, frames: [{ tick, positions: [{agentId,siteId,x,y,state}] }] },
            stats:    { ticks, collisions, arrived, replans,
                        status: "Completed"|"CollisionDetected"|"DidNotConverge",
                        collisionTick: number|null, collisionAgentIds: string[]|null,
                        redirects: number, recoveries: number, flowtimeTicks: number }
          }
          state ∈ { "Waiting", "Moving", "Arrived" }

400       application/problem+json  ProblemDetails { title, detail, errors? }  (invalid request)
```
