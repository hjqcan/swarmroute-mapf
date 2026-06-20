import { Switch } from 'antd'
import { useIntl } from 'react-intl'
import { CheckCircle2, AlertTriangle, CircleSlash, Eye, EyeOff } from 'lucide-react'
import { useSimStore } from '@/store/simStore'
import { hueFor } from '@/utils/palette'
import { sortedAgents } from '@/utils/simModel'
import type { RunState, Stats, LifelongMetrics } from '@/types'

/**
 * Right telemetry column: the run status banner (Completed / CollisionDetected /
 * DidNotConverge — never hidden), the headline metrics, and a per-agent list with
 * a hue swatch, id, and live state. The status banner is the honest verdict.
 */
export default function TelemetryColumn() {
  const intl = useIntl()
  const result = useSimStore((s) => s.result)
  const error = useSimStore((s) => s.error)

  return (
    <div className="flex h-full flex-col gap-4">
      <h2 className="font-display text-sm font-semibold uppercase tracking-wider text-text-muted">
        {intl.formatMessage({ id: 'telemetry.title' })}
      </h2>

      {error && (
        <div className="rounded-lg border border-danger/40 bg-danger-soft px-3 py-2.5 text-sm text-danger">
          {error}
        </div>
      )}

      {!result && !error && (
        <p className="text-sm text-text-muted">
          {intl.formatMessage({ id: 'telemetry.empty' })}
        </p>
      )}

      {result && (
        <>
          <StatusBanner stats={result.stats} lifelong={result.lifelong} />
          <BenchmarkTable />
          <Metrics stats={result.stats} agentTotal={result.agents.length} />
          <LabMetrics />
          <LifelongMetricsPanel />
          <NonConvergencePanel stats={result.stats} />
          <AgentList />
        </>
      )}
    </div>
  )
}

function StatusBanner({ stats, lifelong }: { stats: Stats; lifelong?: LifelongMetrics | null }) {
  const intl = useIntl()

  if (stats.status === 'Completed') {
    return (
      <div className="rounded-lg border border-accent/40 bg-accent-soft px-3.5 py-3">
        <div className="flex items-center gap-2 text-accent">
          <CheckCircle2 size={18} />
          <span className="font-display text-base font-semibold">
            {intl.formatMessage({
              id: lifelong ? 'telemetry.status.lifelong.title' : 'telemetry.status.completed.title',
            })}
          </span>
        </div>
        <p className="mt-1 text-sm text-text-primary/90">
          {lifelong
            ? intl.formatMessage(
                { id: 'telemetry.status.lifelong.desc' },
                {
                  horizon: lifelong.horizonTicks,
                  done: lifelong.tasksCompleted,
                  total: lifelong.tasksReleased,
                }
              )
            : intl.formatMessage({ id: 'telemetry.status.completed.desc' })}
        </p>
      </div>
    )
  }

  if (stats.status === 'CollisionDetected') {
    const agents = stats.collisionAgentIds?.join(', ') ?? '—'
    return (
      <div className="sr-collision-pulse rounded-lg border border-danger/50 bg-danger-soft px-3.5 py-3">
        <div className="flex items-center gap-2 text-danger">
          <AlertTriangle size={18} />
          <span className="font-display text-base font-semibold">
            {intl.formatMessage({ id: 'telemetry.status.collision.title' })}
          </span>
        </div>
        <p className="mt-1 text-sm text-text-primary/90">
          {intl.formatMessage(
            { id: 'telemetry.status.collision.desc' },
            { tick: stats.collisionTick ?? '—', agents }
          )}
        </p>
      </div>
    )
  }

  // DidNotConverge — muted warning.
  return (
    <div className="rounded-lg border border-hairline bg-base px-3.5 py-3">
      <div className="flex items-center gap-2 text-text-muted">
        <CircleSlash size={18} />
        <span className="font-display text-base font-semibold text-text-primary">
          {intl.formatMessage({ id: 'telemetry.status.didNotConverge.title' })}
        </span>
      </div>
      <p className="mt-1 text-sm text-text-muted">
        {intl.formatMessage({ id: 'telemetry.status.didNotConverge.desc' })}
      </p>
    </div>
  )
}

function Metrics({ stats, agentTotal }: { stats: Stats; agentTotal: number }) {
  const intl = useIntl()
  const items: { label: string; value: string; warn?: boolean }[] = [
    { label: intl.formatMessage({ id: 'telemetry.metric.ticks' }), value: String(stats.ticks) },
    {
      label: intl.formatMessage({ id: 'telemetry.metric.arrived' }),
      value: `${stats.arrived} / ${agentTotal}`,
    },
    {
      label: intl.formatMessage({ id: 'telemetry.metric.collisions' }),
      value: String(stats.collisions),
      warn: stats.collisions > 0,
    },
    {
      label: intl.formatMessage({ id: 'telemetry.metric.replans' }),
      value: String(stats.replans),
    },
  ]
  return (
    <div className="grid grid-cols-2 gap-2">
      {items.map((it) => (
        <div key={it.label} className="rounded-lg border border-hairline bg-base px-3 py-2.5">
          <div className="text-2xs uppercase tracking-wider text-text-muted">{it.label}</div>
          <div
            className={`mt-0.5 font-mono text-xl tabular-nums ${
              it.warn ? 'text-danger' : 'text-text-primary'
            }`}
          >
            {it.value}
          </div>
        </div>
      ))}
    </div>
  )
}

/**
 * (v4 SwarmRoute Lab) Planner-portfolio comparison: the same map / fleet / seed run under v0 Dijkstra, v1 SIPP and
 * v3 SIPPwRT, comparing the UNIT-FREE metrics (completion, wait ratio, fairness) — directly comparable across the
 * discrete and continuous clocks — so it honestly shows which planner serves this scenario best.
 */
function BenchmarkTable() {
  const intl = useIntl()
  const benchmark = useSimStore((s) => s.benchmark)
  if (!benchmark || benchmark.length === 0) return null

  const cell = (v: number | undefined, fmt: (n: number) => string) => (v === undefined ? '—' : fmt(v))
  return (
    <div className="rounded-lg border border-accent/40 bg-base p-2.5">
      <div className="mb-2 text-2xs uppercase tracking-wider text-accent">
        {intl.formatMessage({ id: 'benchmark.title' })}
      </div>
      <table className="w-full text-xs">
        <thead>
          <tr className="text-text-muted">
            <th className="pb-1 text-left font-normal">{intl.formatMessage({ id: 'benchmark.planner' })}</th>
            <th className="pb-1 text-right font-normal">{intl.formatMessage({ id: 'metrics.completion' })}</th>
            <th className="pb-1 text-right font-normal">{intl.formatMessage({ id: 'metrics.waitRatio' })}</th>
            <th className="pb-1 text-right font-normal">{intl.formatMessage({ id: 'metrics.fairness' })}</th>
          </tr>
        </thead>
        <tbody className="font-mono tabular-nums">
          {benchmark.map((e) => {
            const m = e.metrics ?? undefined
            const done = m !== undefined && m.completionRate >= 1
            return (
              <tr key={e.planner} className="border-t border-hairline/50">
                <td className="py-1 text-text-primary">{e.planner}</td>
                <td className={`py-1 text-right ${done ? 'text-accent' : 'text-text-primary'}`}>
                  {cell(m?.completionRate, (n) => `${Math.round(n * 100)}%`)}
                </td>
                <td className="py-1 text-right text-text-primary">
                  {cell(m?.meanWaitRatio, (n) => `${Math.round(n * 100)}%`)}
                </td>
                <td className="py-1 text-right text-text-primary">
                  {cell(m?.fairnessIndex, (n) => n.toFixed(2))}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}

/**
 * (v4 SwarmRoute Lab) The quantitative run metrics: throughput, travel-time tail (P50/P95/P99), wait ratio,
 * fairness, and the makespan — plus the bottleneck ranking and a toggle for the field congestion heatmap. This is
 * the "is it good?" layer that makes one planner / policy / map comparable to another.
 */
function LabMetrics() {
  const intl = useIntl()
  const result = useSimStore((s) => s.result)
  const showHeatmap = useSimStore((s) => s.showHeatmap)
  const setShowHeatmap = useSimStore((s) => s.setShowHeatmap)
  const m = result?.metrics
  if (!m) return null

  const guidance = result?.guidance
  const trace = result?.trace
  const robustness = result?.robustness
  const delayResilience = result?.delayResilience
  const orders = result?.orderDispatch
  const pct = (v: number) => `${Math.round(v * 100)}%`
  const secs = (ms: number) => `${(ms / 1000).toFixed(1)}s`

  const downloadTrace = () => {
    if (!trace) return
    const blob = new Blob([JSON.stringify(trace, null, 2)], { type: 'application/json' })
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = 'swarmroute-trace.json'
    a.click()
    URL.revokeObjectURL(url)
  }
  const items: { label: string; value: string; warn?: boolean }[] = [
    { label: intl.formatMessage({ id: 'metrics.throughput' }), value: m.throughputPerThousandTicks.toFixed(1) },
    { label: intl.formatMessage({ id: 'metrics.completion' }), value: pct(m.completionRate), warn: m.completionRate < 1 },
    { label: intl.formatMessage({ id: 'metrics.travelP50' }), value: String(m.travelTime.p50) },
    { label: intl.formatMessage({ id: 'metrics.travelP95' }), value: String(m.travelTime.p95) },
    { label: intl.formatMessage({ id: 'metrics.travelP99' }), value: String(m.travelTime.p99) },
    { label: intl.formatMessage({ id: 'metrics.makespan' }), value: String(m.makespanTicks) },
    { label: intl.formatMessage({ id: 'metrics.waitRatio' }), value: pct(m.meanWaitRatio), warn: m.meanWaitRatio > 0.4 },
    { label: intl.formatMessage({ id: 'metrics.fairness' }), value: m.fairnessIndex.toFixed(2), warn: m.fairnessIndex < 0.7 },
  ]
  if (robustness) {
    // (v4 Robust Execution) Coupling = inter-AGV cell-handoffs; Slack = the largest single delay the plan absorbs
    // before a naive collision (0 = a back-to-back handoff exists → needs dependency-following execution).
    items.push(
      { label: intl.formatMessage({ id: 'metrics.coupling' }), value: String(robustness.handoffDependencies) },
      { label: intl.formatMessage({ id: 'metrics.slack' }), value: String(robustness.minSlackTicks), warn: robustness.minSlackTicks === 0 },
    )
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="flex items-center justify-between">
        <div className="text-2xs uppercase tracking-wider text-text-muted">
          {intl.formatMessage({ id: 'metrics.title' })}
        </div>
        <label className="flex items-center gap-1.5 text-2xs text-text-muted">
          {intl.formatMessage({ id: 'metrics.heatmap' })}
          <Switch size="small" checked={showHeatmap} onChange={setShowHeatmap} />
        </label>
      </div>

      {guidance && (
        <div className="rounded-lg border border-accent/40 bg-accent-soft px-3 py-2">
          <div className="text-2xs uppercase tracking-wider text-accent">
            {intl.formatMessage({ id: 'metrics.guided' }, { lanes: guidance.adjustedLanes })}
          </div>
          <div className="mt-1.5 grid grid-cols-3 gap-2">
            <Delta label={intl.formatMessage({ id: 'metrics.completion' })} from={guidance.baseline.completionRate} to={m.completionRate} pct higherIsBetter />
            <Delta label={intl.formatMessage({ id: 'metrics.waitRatio' })} from={guidance.baseline.meanWaitRatio} to={m.meanWaitRatio} pct higherIsBetter={false} />
            <Delta label={intl.formatMessage({ id: 'metrics.throughput' })} from={guidance.baseline.throughputPerThousandTicks} to={m.throughputPerThousandTicks} higherIsBetter />
          </div>
        </div>
      )}

      <div className="grid grid-cols-2 gap-2">
        {items.map((it) => (
          <div key={it.label} className="rounded-lg border border-hairline bg-base px-3 py-2">
            <div className="text-2xs uppercase tracking-wider text-text-muted">{it.label}</div>
            <div
              className={`mt-0.5 font-mono text-lg tabular-nums ${it.warn ? 'text-danger' : 'text-text-primary'}`}
            >
              {it.value}
            </div>
          </div>
        ))}
      </div>

      {m.bottleneckSiteIds.length > 0 && (
        <div className="rounded-lg border border-hairline bg-base px-3 py-2">
          <div className="text-2xs uppercase tracking-wider text-text-muted">
            {intl.formatMessage({ id: 'metrics.bottlenecks' })}
          </div>
          <div className="mt-1 flex flex-wrap gap-1">
            {m.bottleneckSiteIds.slice(0, 6).map((sid) => (
              <span
                key={sid}
                className="rounded bg-danger-soft px-1.5 py-0.5 font-mono text-2xs text-danger"
              >
                {sid}
              </span>
            ))}
          </div>
        </div>
      )}

      {delayResilience && (
        // (v4 Robust Execution) The ADG/TPG-following executor what-if: inject a delay into the most brittle AGV, then
        // re-execute the plan naively (by timestamps → collides) vs following the dependency graph (→ 0 collisions,
        // paying makespan). The concrete case for executing on dependencies rather than the clock.
        <div className="rounded-lg border border-accent/40 bg-accent-soft px-3 py-2">
          <div className="text-2xs uppercase tracking-wider text-accent">
            {intl.formatMessage(
              { id: 'metrics.delay.title' },
              { delay: delayResilience.delayTicks, agent: delayResilience.delayedAgent },
            )}
          </div>
          <div className="mt-1.5 grid grid-cols-3 gap-2">
            <div>
              <div className="text-2xs uppercase tracking-wider text-text-muted">
                {intl.formatMessage({ id: 'metrics.delay.naive' })}
              </div>
              <div className="mt-0.5 font-mono text-lg tabular-nums text-danger">{delayResilience.naiveCollisions}</div>
            </div>
            <div>
              <div className="text-2xs uppercase tracking-wider text-text-muted">
                {intl.formatMessage({ id: 'metrics.delay.adg' })}
              </div>
              <div className="mt-0.5 font-mono text-lg tabular-nums text-accent">{delayResilience.adgCollisions}</div>
            </div>
            <div>
              <div className="text-2xs uppercase tracking-wider text-text-muted">
                {intl.formatMessage({ id: 'metrics.delay.cost' })}
              </div>
              <div className="mt-0.5 font-mono text-lg tabular-nums text-text-primary">
                +{delayResilience.adgMakespanInflation}
              </div>
            </div>
          </div>
        </div>
      )}

      {orders && (
        // (v4 Order/Dispatch context) The lifelong online-dispatch KPIs over the same field + fleet. Toggle the
        // Assignment policy (Random → Optimal) and watch on-time delivery climb while latency / utilization fall.
        <div className="rounded-lg border border-hairline bg-base px-3 py-2">
          <div className="text-2xs uppercase tracking-wider text-text-muted">
            {intl.formatMessage(
              { id: 'orders.title' },
              { policy: orders.policy, done: orders.ordersCompleted, total: orders.ordersTotal },
            )}
          </div>
          <div className="mt-1.5 grid grid-cols-3 gap-2">
            <OrderStat label={intl.formatMessage({ id: 'orders.onTime' })} value={pct(orders.onTimeRate)} warn={orders.onTimeRate < 0.8} />
            <OrderStat label={intl.formatMessage({ id: 'orders.meanLatency' })} value={secs(orders.meanLatencyMs)} />
            <OrderStat label={intl.formatMessage({ id: 'orders.p95Latency' })} value={secs(orders.p95LatencyMs)} />
            <OrderStat label={intl.formatMessage({ id: 'orders.utilization' })} value={pct(orders.fleetUtilization)} />
            <OrderStat label={intl.formatMessage({ id: 'orders.queue' })} value={String(orders.maxQueueDepth)} />
            <OrderStat label={intl.formatMessage({ id: 'orders.charging' })} value={String(orders.chargingStops)} />
          </div>
        </div>
      )}

      {trace && trace.length > 0 && (
        <button
          type="button"
          onClick={downloadTrace}
          className="rounded-lg border border-hairline bg-base px-3 py-2 text-xs text-text-muted transition-colors hover:border-accent/50 hover:text-text-primary"
        >
          {intl.formatMessage({ id: 'trace.download' }, { count: trace.length })}
        </button>
      )}
    </div>
  )
}

/**
 * (FMS-V3) The lifelong-dispatch continuous-operation metrics, present only on a horizon-bounded LifelongDispatch run:
 * the headline throughput, tasks turned over, backlog wait (mean / P95), peak queue depth, parking capacity / peak /
 * saturation, and a first-vs-second-half split (a sustained-progress / no-late-stall check). Mirrors the LabMetrics
 * panel style. Hidden on every other run.
 */
function LifelongMetricsPanel() {
  const intl = useIntl()
  const result = useSimStore((s) => s.result)
  const l = result?.lifelong
  if (!l) return null

  const pct = (v: number) => `${Math.round(v * 100)}%`
  const items: { label: string; value: string; warn?: boolean }[] = [
    { label: intl.formatMessage({ id: 'lifelong.throughput' }), value: l.throughputPerHundredTicks.toFixed(2) },
    { label: intl.formatMessage({ id: 'lifelong.completed' }), value: `${l.tasksCompleted} / ${l.tasksReleased}` },
    { label: intl.formatMessage({ id: 'lifelong.meanWait' }), value: l.meanWaitTicks.toFixed(1) },
    { label: intl.formatMessage({ id: 'lifelong.p95Wait' }), value: String(l.p95WaitTicks) },
    { label: intl.formatMessage({ id: 'lifelong.maxQueue' }), value: String(l.maxQueueDepth) },
    {
      label: intl.formatMessage({ id: 'lifelong.parking' }),
      value: `${l.peakParkedCount} / ${l.parkingCapacity}`,
      warn: l.parkingSaturation >= 1,
    },
    { label: intl.formatMessage({ id: 'lifelong.saturation' }), value: pct(l.parkingSaturation), warn: l.parkingSaturation >= 0.9 },
    {
      label: intl.formatMessage({ id: 'lifelong.halves' }),
      value: `${l.tasksCompletedFirstHalf} → ${l.tasksCompletedSecondHalf}`,
      warn: l.tasksCompletedSecondHalf < l.tasksCompletedFirstHalf / 2,
    },
  ]
  return (
    <div className="flex flex-col gap-2">
      <div className="text-2xs uppercase tracking-wider text-accent">
        {intl.formatMessage({ id: 'lifelong.title' }, { horizon: l.horizonTicks })}
      </div>
      <div className="grid grid-cols-2 gap-2">
        {items.map((it) => (
          <div key={it.label} className="rounded-lg border border-hairline bg-base px-3 py-2">
            <div className="text-2xs uppercase tracking-wider text-text-muted">{it.label}</div>
            <div
              className={`mt-0.5 font-mono text-lg tabular-nums ${it.warn ? 'text-danger' : 'text-text-primary'}`}
            >
              {it.value}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

/**
 * (FMS-V2) Why a DidNotConverge run's AGVs failed to arrive: the dominant reason plus the per-agent breakdown. Present
 * only when the run carries `stats.nonConvergence` (a non-converged run with at least one stranded agent); hidden on a
 * converged / collision run. The honest diagnostic behind a "did not converge" verdict.
 */
function NonConvergencePanel({ stats }: { stats: Stats }) {
  const intl = useIntl()
  const nc = stats.nonConvergence
  if (!nc) return null

  const entries = Object.entries(nc.perAgentReasons).sort((a, b) => a[0].localeCompare(b[0]))
  return (
    <div className="rounded-lg border border-hairline bg-base px-3 py-2">
      <div className="text-2xs uppercase tracking-wider text-text-muted">
        {intl.formatMessage({ id: 'nonconv.title' }, { reason: nc.dominantReason })}
      </div>
      {entries.length > 0 && (
        <ul className="mt-1.5 space-y-1">
          {entries.map(([agentId, reason]) => (
            <li key={agentId} className="flex items-center justify-between gap-2 text-xs">
              <span className="font-mono text-text-primary">{agentId}</span>
              <span className="rounded bg-danger-soft px-1.5 py-0.5 font-mono text-2xs text-danger">{reason}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  )
}

/** (v4 SwarmRoute Lab — Order/Dispatch) One labelled dispatch KPI; turns red when an on-time figure is below target. */
function OrderStat({ label, value, warn }: { label: string; value: string; warn?: boolean }) {
  return (
    <div>
      <div className="text-2xs uppercase tracking-wider text-text-muted">{label}</div>
      <div className={`mt-0.5 font-mono text-sm tabular-nums ${warn ? 'text-danger' : 'text-text-primary'}`}>{value}</div>
    </div>
  )
}

/** (v4 SwarmRoute Lab) One baseline→guided metric in the guidance comparison, coloured by whether it improved. */
function Delta({
  label,
  from,
  to,
  pct,
  higherIsBetter,
}: {
  label: string
  from: number
  to: number
  pct?: boolean
  higherIsBetter: boolean
}) {
  const fmt = (v: number) => (pct ? `${Math.round(v * 100)}%` : v.toFixed(1))
  const color =
    to === from ? 'text-text-muted' : (higherIsBetter ? to > from : to < from) ? 'text-accent' : 'text-danger'
  return (
    <div>
      <div className="text-2xs uppercase tracking-wider text-text-muted">{label}</div>
      <div className={`mt-0.5 font-mono text-sm tabular-nums ${color}`}>
        {fmt(from)}
        <span className="text-text-muted"> → </span>
        {fmt(to)}
      </div>
    </div>
  )
}

function AgentList() {
  const intl = useIntl()
  const result = useSimStore((s) => s.result)
  const cursor = useSimStore((s) => s.cursor)
  const hiddenPaths = useSimStore((s) => s.hiddenPaths)
  const togglePath = useSimStore((s) => s.togglePath)
  if (!result) return null

  const agents = sortedAgents(result.agents)
  const frames = result.timeline.frames
  const frameIdx = Math.max(0, Math.min(frames.length - 1, Math.round(cursor)))
  const frame = frames[frameIdx]
  const stateById = new Map<string, RunState>()
  frame?.positions.forEach((p) => stateById.set(p.agentId, p.state))

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="mb-2 text-2xs uppercase tracking-wider text-text-muted">
        {intl.formatMessage({ id: 'telemetry.agents' })}
      </div>
      <ul className="flex-1 space-y-1 overflow-y-auto pr-1">
        {agents.map((a) => {
          const hue = hueFor(a.colorIndex)
          const state = stateById.get(a.id) ?? 'Waiting'
          const hidden = hiddenPaths.has(a.id)
          return (
            <li key={a.id}>
              <button
                type="button"
                onClick={() => togglePath(a.id)}
                aria-pressed={hidden}
                title={intl.formatMessage({
                  id: hidden ? 'telemetry.agent.showPath' : 'telemetry.agent.hidePath',
                })}
                className="flex w-full items-center justify-between rounded-md border border-hairline/60 bg-base px-2.5 py-1.5 text-left transition-colors hover:border-hairline hover:bg-panel"
              >
                <span className={`flex items-center gap-2 transition-opacity ${hidden ? 'opacity-40' : ''}`}>
                  <span
                    className="h-2.5 w-2.5 shrink-0 rounded-full"
                    style={{ backgroundColor: hue }}
                  />
                  <span className="font-mono text-sm text-text-primary">{a.id}</span>
                </span>
                <span className="flex items-center gap-2">
                  {hidden ? (
                    <EyeOff size={14} className="shrink-0 text-text-muted" />
                  ) : (
                    <Eye size={14} className="shrink-0 text-text-muted/40" />
                  )}
                  <StateChip state={state} />
                </span>
              </button>
            </li>
          )
        })}
      </ul>
    </div>
  )
}

function StateChip({ state }: { state: RunState }) {
  const intl = useIntl()
  const tone =
    state === 'Arrived'
      ? 'text-accent border-accent/40'
      : state === 'Moving'
        ? 'text-text-primary border-hairline'
        : 'text-text-muted border-hairline/60'
  return (
    <span className={`rounded-full border px-2 py-0.5 text-2xs uppercase tracking-wide ${tone}`}>
      {intl.formatMessage({ id: `state.${state}` })}
    </span>
  )
}
