import { Button, InputNumber, Segmented, Slider, Switch } from 'antd'
import { Dices, Play, Loader2, Repeat, Sparkles, BarChart3, FileDown, Package, Anchor } from 'lucide-react'
import { useIntl } from 'react-intl'
import { useSimStore } from '@/store/simStore'
import type { PlannerKind, ScenarioKind, AssignmentPolicy, ScenarioMode } from '@/types'

const FIELD_MIN = 4
const FIELD_MAX = 24
const AGV_MIN = 1
const AGV_MAX = 24
const HORIZON_MIN = 1
const HORIZON_MAX = 64
const DEFAULT_RHCR_WINDOW_MS = 8

// (FMS-V3) A lifelong-dispatch run wants a larger, sparser grid (the well-formed warehouse carves a parking/
// workstation ring around a transit core), so selecting it nudges a small grid up to this size; the default
// horizon over which throughput is measured. Both are nudges — the operator can still adjust width/height/horizon.
const LIFELONG_MIN_FIELD = 12
const DEFAULT_LIFELONG_HORIZON_TICKS = 400
const HORIZON_TICKS_MIN = 50
const HORIZON_TICKS_MAX = 4000

/** The mutually-exclusive local standoff resolver (cluster owner): none, fast greedy PIBT, or complete CBS. */
type ResolverMode = 'off' | 'pibt' | 'cbs'

/**
 * Left control rail: field width/height, AGV count, optional seed (+ a shuffle
 * button), and the primary Run button. Each numeric field pairs a slider with an
 * InputNumber so it works on touch and with the keyboard.
 */
export default function ControlRail() {
  const intl = useIntl()
  const params = useSimStore((s) => s.params)
  const setParam = useSimStore((s) => s.setParam)
  const randomizeSeed = useSimStore((s) => s.randomizeSeed)
  const run = useSimStore((s) => s.run)
  const runBenchmark = useSimStore((s) => s.runBenchmark)
  const loading = useSimStore((s) => s.loading)
  const autoLoop = useSimStore((s) => s.autoLoop)
  const setAutoLoop = useSimStore((s) => s.setAutoLoop)

  // Guard against the engine's constraint: width*height >= 2*agvCount.
  const capacity = params.width * params.height
  const agvMax = Math.min(AGV_MAX, Math.floor(capacity / 2))
  const agvTooMany = params.agvCount > agvMax
  const horizonEnabled = params.horizonWindowMs !== undefined
  const horizonWindow = params.horizonWindowMs ?? DEFAULT_RHCR_WINDOW_MS

  // The local standoff resolver is available under both reservation-aware planners. PIBT is the fast greedy discrete
  // tick-stepper, so it pairs with SIPP only; the continuous SIPPwRT executor resolves standoffs with CCBS
  // (continuous-time CBS), so under SIPPwRT only Off/CBS are offered. v0 Dijkstra runs no resolver. The choice maps
  // directly onto the one `jointResolver` enum (None/Pibt/Cbs); the backend rejects Cbs+Dijkstra and Pibt+SIPPwRT.
  const planner: PlannerKind = params.planner ?? 'Sipp'
  const resolverEnabled = planner === 'Sipp' || planner === 'Sippwrt'
  const pibtEnabled = planner === 'Sipp'
  const resolverMode: ResolverMode =
    params.jointResolver === 'Cbs' ? 'cbs' : params.jointResolver === 'Pibt' ? 'pibt' : 'off'
  const setResolver = (mode: ResolverMode) => {
    setParam('jointResolver', mode === 'cbs' ? 'Cbs' : mode === 'pibt' ? 'Pibt' : 'None')
  }
  // Clear an incompatible resolver when switching planner so a stale selection can't 400: Dijkstra runs none, and
  // SIPPwRT can't run PIBT (it uses CCBS), so a PIBT selection falls back to CBS under SIPPwRT.
  const setPlanner = (next: PlannerKind) => {
    setParam('planner', next)
    if (next === 'Dijkstra') setResolver('off')
    else if (next === 'Sippwrt' && params.jointResolver === 'Pibt') setResolver('cbs')
    // RHCR's window is in discrete-tick units (~1ms/hop); SIPPwRT runs in real ms (~2000ms/edge), so the tiny
    // tick window can't commit even one edge → a 0-tick run. The continuous executor + CCBS already handle
    // standoffs, so RHCR (a discrete throughput lever) is not applicable to SIPPwRT — drop to full-path.
    if (next === 'Sippwrt') setParam('horizonWindowMs', undefined)
  }
  // (FMS) The high-level scenario family. RandomStress is today's random run; WarehouseWellFormed carves a well-formed
  // warehouse; LifelongDispatch runs a horizon-bounded continuous operation (needs lifelongHorizonTicks too). Selecting
  // LifelongDispatch nudges a larger/sparser grid + plants the default horizon so the run is well-posed; leaving it
  // sets the horizon back to undefined so the other modes stay one-shot. M-F1 is a separate fixed-demo button below.
  const scenarioMode: ScenarioMode = params.scenarioMode ?? 'RandomStress'
  const isLifelong = scenarioMode === 'LifelongDispatch'
  const lifelongHorizon = params.lifelongHorizonTicks ?? DEFAULT_LIFELONG_HORIZON_TICKS
  const setScenarioMode = (next: ScenarioMode) => {
    setParam('scenarioMode', next)
    if (next === 'LifelongDispatch') {
      setParam('lifelongHorizonTicks', params.lifelongHorizonTicks ?? DEFAULT_LIFELONG_HORIZON_TICKS)
      // Nudge a small grid up so the warehouse ring + transit core fit; never shrink an already-large grid.
      if (params.width < LIFELONG_MIN_FIELD) setParam('width', LIFELONG_MIN_FIELD)
      if (params.height < LIFELONG_MIN_FIELD) setParam('height', LIFELONG_MIN_FIELD)
    } else {
      // The horizon is inert (and confusing) outside a lifelong run — drop it so the request stays clean.
      setParam('lifelongHorizonTicks', undefined)
    }
  }

  // (FMS-V1) Launch the fixed M-F1 dock-admission demo: a one-shot run override that sets stationScenario='MF1' (the
  // backend then ignores the random grid + scenarioMode and runs the fixed corridor-with-one-blocking-station demo).
  // Passed as a run override so it never persists in params — a subsequent normal Run leaves stationScenario unset.
  const runMf1Demo = () => run({ stationScenario: 'MF1' })

  // RHCR is meaningful only for the discrete executors (1 tick = 1 hop). SIPPwRT plans in real continuous time.
  const horizonApplicable = planner !== 'Sippwrt'
  const plannerTag =
    planner === 'Dijkstra'
      ? 'controls.planner.dijkstraTag'
      : planner === 'Sippwrt'
        ? 'controls.planner.sippwrtTag'
        : 'controls.planner.sippTag'

  return (
    <div className="flex h-full flex-col gap-5">
      <h2 className="font-display text-sm font-semibold uppercase tracking-wider text-text-muted">
        {intl.formatMessage({ id: 'controls.title' })}
      </h2>

      <Field label={intl.formatMessage({ id: 'controls.width' })} value={params.width}>
        <SliderNumber
          min={FIELD_MIN}
          max={FIELD_MAX}
          value={params.width}
          onChange={(v) => setParam('width', v)}
        />
      </Field>

      <Field label={intl.formatMessage({ id: 'controls.height' })} value={params.height}>
        <SliderNumber
          min={FIELD_MIN}
          max={FIELD_MAX}
          value={params.height}
          onChange={(v) => setParam('height', v)}
        />
      </Field>

      <Field
        label={intl.formatMessage({ id: 'controls.agvCount' })}
        value={params.agvCount}
        warn={agvTooMany}
      >
        <SliderNumber
          min={AGV_MIN}
          max={AGV_MAX}
          value={params.agvCount}
          onChange={(v) => setParam('agvCount', v)}
        />
      </Field>

      {/* (FMS) Scenario mode: the high-level run family. RandomStress = today's random start/goal stress run;
          WarehouseWellFormed = a well-formed warehouse (parking/workstation ring, goals at workstations, clear-to-
          parking); LifelongDispatch = a horizon-bounded continuous operation (a task stream the dispatcher hands out,
          re-tasking each AGV that clears to parking). Selecting LifelongDispatch reveals the horizon + nudges a larger
          grid. The fixed M-F1 dock-admission demo is the button below. */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.scenarioMode' })}
          </label>
        </div>
        <Segmented
          block
          value={scenarioMode}
          onChange={(v) => setScenarioMode(v as ScenarioMode)}
          options={[
            { label: intl.formatMessage({ id: 'controls.scenarioMode.random' }), value: 'RandomStress' },
            { label: intl.formatMessage({ id: 'controls.scenarioMode.warehouse' }), value: 'WarehouseWellFormed' },
            { label: intl.formatMessage({ id: 'controls.scenarioMode.lifelong' }), value: 'LifelongDispatch' },
          ]}
        />
        {isLifelong && (
          <div className="mt-3">
            <div className="mb-1.5 flex items-baseline justify-between">
              <label className="text-sm text-text-muted">
                {intl.formatMessage({ id: 'controls.lifelongHorizon' })}
              </label>
              <span className="font-mono text-xs text-text-muted">
                {intl.formatMessage({ id: 'controls.lifelongHorizon.tag' })}
              </span>
            </div>
            <InputNumber
              className="w-full font-tabular"
              min={HORIZON_TICKS_MIN}
              max={HORIZON_TICKS_MAX}
              step={50}
              value={lifelongHorizon}
              onChange={(v) =>
                setParam('lifelongHorizonTicks', v == null ? DEFAULT_LIFELONG_HORIZON_TICKS : Number(v))
              }
            />
          </div>
        )}
      </div>

      {/* (FMS-V1) Fixed M-F1 dock-admission demo: a corridor with one hard-blocking station — one AGV docks (via its
          pre-dock buffer) while transit AGVs cross; the docking AGV holds at the buffer until the corridor clears.
          A one-shot run (stationScenario='MF1') that ignores the random grid + scenario mode above. */}
      <Button
        block
        loading={loading}
        onClick={runMf1Demo}
        icon={<Anchor size={16} />}
        className="font-display"
      >
        {intl.formatMessage({ id: 'controls.mf1Demo' })}
      </Button>

      {/* (v4 SwarmRoute Lab — ScenarioBench) Map layout: Open uniform grid vs a walled bottleneck (one corridor) vs
          a lattice of pillar obstacles — the non-uniform fields where the metrics / heatmap / guidance come alive. */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.scenario' })}
          </label>
        </div>
        <Segmented
          block
          value={params.scenario ?? 'Open'}
          onChange={(v) => setParam('scenario', v as ScenarioKind)}
          options={[
            { label: intl.formatMessage({ id: 'controls.scenario.open' }), value: 'Open' },
            { label: intl.formatMessage({ id: 'controls.scenario.bottleneck' }), value: 'Bottleneck' },
            { label: intl.formatMessage({ id: 'controls.scenario.obstacles' }), value: 'Obstacles' },
          ]}
        />
      </div>

      {/* (v4 SwarmRoute Lab — Dispatcher) Goal assignment: Random pairing vs greedy nearest-robot vs Optimal
          (Hungarian) min-total-travel matching — the dispatcher's core job; the effect shows in the metrics. */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.assignment' })}
          </label>
        </div>
        <Segmented
          block
          value={params.assignment ?? 'Random'}
          onChange={(v) => setParam('assignment', v as AssignmentPolicy)}
          options={[
            { label: intl.formatMessage({ id: 'controls.assignment.random' }), value: 'Random' },
            { label: intl.formatMessage({ id: 'controls.assignment.nearest' }), value: 'Nearest' },
            { label: intl.formatMessage({ id: 'controls.assignment.optimal' }), value: 'Optimal' },
          ]}
        />
      </div>

      {/* Planner: v0 Dijkstra (space-only — can deadlock when dense) vs v1 SIPP (reservation-aware,
          plans in time and reports any remaining standoff honestly). Defaults to SIPP; flip to Dijkstra
          to reproduce a standoff and watch the stuck AGVs' forward routes on the field. */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.planner' })}
          </label>
          <span className="font-mono text-xs text-text-muted">
            {intl.formatMessage({ id: plannerTag })}
          </span>
        </div>
        <Segmented
          block
          value={planner}
          onChange={(v) => setPlanner(v as PlannerKind)}
          options={[
            { label: 'SIPP', value: 'Sipp' },
            { label: 'SIPPwRT', value: 'Sippwrt' },
            { label: 'Dijkstra', value: 'Dijkstra' },
          ]}
        />
      </div>

      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.horizon' })}
          </label>
          <span className="font-mono text-xs text-text-muted">
            {intl.formatMessage(
              { id: horizonEnabled && horizonApplicable ? 'controls.horizon.rhcrTag' : 'controls.horizon.unboundedTag' },
              { value: horizonWindow }
            )}
          </span>
        </div>
        <Segmented
          block
          disabled={!horizonApplicable}
          value={horizonEnabled && horizonApplicable ? 'rhcr' : 'full'}
          onChange={(v) =>
            setParam('horizonWindowMs', String(v) === 'rhcr' ? horizonWindow : undefined)
          }
          options={[
            { label: intl.formatMessage({ id: 'controls.horizon.full' }), value: 'full' },
            { label: intl.formatMessage({ id: 'controls.horizon.rhcr' }), value: 'rhcr' },
          ]}
        />
        {horizonEnabled && horizonApplicable && (
          <div className="mt-3">
            <SliderNumber
              min={HORIZON_MIN}
              max={HORIZON_MAX}
              value={horizonWindow}
              onChange={(v) => setParam('horizonWindowMs', v)}
            />
          </div>
        )}
      </div>

      {/* Local standoff resolver: the mutually-exclusive cluster owner for physical standoffs. Off = raw baseline;
          PIBT = fast greedy priority-inheritance (SIPP only); CBS = complete local conflict-based search, which
          cracks the corridor swaps / blocking chains PIBT's greedy ordering can't — and under SIPPwRT runs as
          continuous-time CCBS. */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.resolver' })}
          </label>
          <span className="font-mono text-xs text-text-muted">
            {intl.formatMessage({ id: `controls.resolver.${resolverMode}Tag` })}
          </span>
        </div>
        <Segmented
          block
          value={resolverMode}
          disabled={!resolverEnabled}
          onChange={(v) => setResolver(v as ResolverMode)}
          options={[
            { label: intl.formatMessage({ id: 'controls.resolver.off' }), value: 'off' },
            { label: 'PIBT', value: 'pibt', disabled: !pibtEnabled },
            { label: 'CBS', value: 'cbs' },
          ]}
        />
      </div>

      {/* Seed (optional) + shuffle */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.seed' })}
          </label>
        </div>
        <div className="flex items-center gap-2">
          <InputNumber
            className="flex-1 font-tabular"
            min={0}
            value={params.seed ?? null}
            placeholder={intl.formatMessage({ id: 'controls.seedPlaceholder' })}
            onChange={(v) => setParam('seed', v == null ? undefined : Number(v))}
          />
          <Button
            icon={<Dices size={16} />}
            onClick={randomizeSeed}
            title={intl.formatMessage({ id: 'controls.shuffle' })}
          >
            {intl.formatMessage({ id: 'controls.shuffle' })}
          </Button>
        </div>
      </div>

      {/* (v4 SwarmRoute Lab) Optimize traffic: run once, re-weight the congested corridors from the measured
          congestion, then re-run the same fleet — the telemetry shows baseline → guided. Steers SIPPwRT / Dijkstra. */}
      <div className="flex items-center justify-between">
        <label className="flex items-center gap-2 text-sm text-text-muted">
          <Sparkles size={14} />
          {intl.formatMessage({ id: 'controls.optimize' })}
        </label>
        <Switch
          checked={params.optimizeGuidance ?? false}
          onChange={(v) => setParam('optimizeGuidance', v)}
        />
      </div>

      {/* (v4 SwarmRoute Lab — TraceEvent) Emit the standardized event log (Planned/Moved/Arrived) on the result so
          it can be downloaded for external analysis. */}
      <div className="flex items-center justify-between">
        <label className="flex items-center gap-2 text-sm text-text-muted">
          <FileDown size={14} />
          {intl.formatMessage({ id: 'controls.trace' })}
        </label>
        <Switch checked={params.emitTrace ?? false} onChange={(v) => setParam('emitTrace', v)} />
      </div>

      {/* (v4 SwarmRoute Lab — Order/Dispatch) Simulate a lifelong order stream over the same field + fleet (online
          assignment, stations, battery, SLA) and report operations KPIs. The Assignment policy above drives it. */}
      <div className="flex items-center justify-between">
        <label className="flex items-center gap-2 text-sm text-text-muted">
          <Package size={14} />
          {intl.formatMessage({ id: 'controls.orders' })}
        </label>
        <Switch checked={params.simulateOrders ?? false} onChange={(v) => setParam('simulateOrders', v)} />
      </div>

      {/* Auto-loop: when a run finishes playing, pick a new seed (the field updates) and run again, forever. */}
      <div className="flex items-center justify-between">
        <label className="flex items-center gap-2 text-sm text-text-muted">
          <Repeat size={14} />
          {intl.formatMessage({ id: 'controls.autoLoop' })}
        </label>
        <Switch checked={autoLoop} onChange={setAutoLoop} />
      </div>

      <Button
        type="primary"
        size="large"
        block
        loading={loading}
        disabled={agvTooMany}
        onClick={() => run()}
        icon={
          loading ? (
            <Loader2 size={16} className="animate-spin" />
          ) : (
            <Play size={16} fill="currentColor" />
          )
        }
        className="!h-11 font-display !font-semibold"
      >
        {intl.formatMessage({ id: loading ? 'controls.running' : 'controls.run' })}
      </Button>

      {/* (v4 SwarmRoute Lab) Planner benchmark: run this exact scenario under v0 Dijkstra / v1 SIPP / v3 SIPPwRT and
          compare their metrics side-by-side in the telemetry column. */}
      <Button
        size="large"
        block
        loading={loading}
        disabled={agvTooMany}
        onClick={() => runBenchmark()}
        icon={<BarChart3 size={16} />}
        className="!h-10 font-display !font-medium"
      >
        {intl.formatMessage({ id: 'controls.benchmark' })}
      </Button>

      <p className="mt-auto text-xs leading-relaxed text-text-muted">
        {intl.formatMessage({ id: 'controls.hint' })}
      </p>
    </div>
  )
}

function Field({
  label,
  value,
  warn,
  children,
}: {
  label: string
  value: number
  warn?: boolean
  children: React.ReactNode
}) {
  return (
    <div>
      <div className="mb-1.5 flex items-baseline justify-between">
        <label className="text-sm text-text-muted">{label}</label>
        <span className={`font-mono text-sm ${warn ? 'text-danger' : 'text-text-primary'}`}>
          {value}
        </span>
      </div>
      {children}
    </div>
  )
}

function SliderNumber({
  min,
  max,
  value,
  onChange,
}: {
  min: number
  max: number
  value: number
  onChange: (v: number) => void
}) {
  return (
    <div className="flex items-center gap-3">
      <Slider
        className="flex-1"
        min={min}
        max={max}
        value={value}
        onChange={onChange}
        tooltip={{ open: false }}
      />
      <InputNumber
        className="w-16 font-tabular"
        size="small"
        min={min}
        max={max}
        value={value}
        onChange={(v) => onChange(Number(v ?? min))}
      />
    </div>
  )
}
