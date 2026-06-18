import { useIntl } from 'react-intl'
import { CheckCircle2, AlertTriangle, CircleSlash } from 'lucide-react'
import { useSimStore } from '@/store/simStore'
import { hueFor } from '@/utils/palette'
import { sortedAgents } from '@/utils/simModel'
import type { RunState, Stats } from '@/types'

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
          <StatusBanner stats={result.stats} />
          <Metrics stats={result.stats} agentTotal={result.agents.length} />
          <AgentList />
        </>
      )}
    </div>
  )
}

function StatusBanner({ stats }: { stats: Stats }) {
  const intl = useIntl()

  if (stats.status === 'Completed') {
    return (
      <div className="rounded-lg border border-accent/40 bg-accent-soft px-3.5 py-3">
        <div className="flex items-center gap-2 text-accent">
          <CheckCircle2 size={18} />
          <span className="font-display text-base font-semibold">
            {intl.formatMessage({ id: 'telemetry.status.completed.title' })}
          </span>
        </div>
        <p className="mt-1 text-sm text-text-primary/90">
          {intl.formatMessage({ id: 'telemetry.status.completed.desc' })}
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

function AgentList() {
  const intl = useIntl()
  const result = useSimStore((s) => s.result)
  const cursor = useSimStore((s) => s.cursor)
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
          return (
            <li
              key={a.id}
              className="flex items-center justify-between rounded-md border border-hairline/60 bg-base px-2.5 py-1.5"
            >
              <span className="flex items-center gap-2">
                <span
                  className="h-2.5 w-2.5 shrink-0 rounded-full"
                  style={{ backgroundColor: hue }}
                />
                <span className="font-mono text-sm text-text-primary">{a.id}</span>
              </span>
              <StateChip state={state} />
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
