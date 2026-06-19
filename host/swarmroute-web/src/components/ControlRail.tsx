import { Button, InputNumber, Segmented, Slider, Switch } from 'antd'
import { Dices, Play, Loader2, Repeat } from 'lucide-react'
import { useIntl } from 'react-intl'
import { useSimStore } from '@/store/simStore'
import type { PlannerKind } from '@/types'

const FIELD_MIN = 4
const FIELD_MAX = 24
const AGV_MIN = 1
const AGV_MAX = 24
const HORIZON_MIN = 1
const HORIZON_MAX = 64
const DEFAULT_RHCR_WINDOW_MS = 8

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
  const loading = useSimStore((s) => s.loading)
  const autoLoop = useSimStore((s) => s.autoLoop)
  const setAutoLoop = useSimStore((s) => s.setAutoLoop)

  // Guard against the engine's constraint: width*height >= 2*agvCount.
  const capacity = params.width * params.height
  const agvMax = Math.min(AGV_MAX, Math.floor(capacity / 2))
  const agvTooMany = params.agvCount > agvMax
  const horizonEnabled = params.horizonWindowMs !== undefined
  const horizonWindow = params.horizonWindowMs ?? DEFAULT_RHCR_WINDOW_MS

  // The local standoff resolver (PIBT/CBS) is SIPP-only: v0 Dijkstra and the continuous SIPPwRT executor don't run
  // it, and the backend rejects useCbs with a non-SIPP planner. So it is a single mutually-exclusive choice mapped
  // onto the two backend flags (never both true), available only under SIPP.
  const planner: PlannerKind = params.planner ?? 'Sipp'
  const resolverEnabled = planner === 'Sipp'
  const resolverMode: ResolverMode = params.useCbs ? 'cbs' : params.usePibt ? 'pibt' : 'off'
  const setResolver = (mode: ResolverMode) => {
    setParam('usePibt', mode === 'pibt')
    setParam('useCbs', mode === 'cbs')
  }
  // Switching to a non-SIPP planner clears the SIPP-only resolver so a stale useCbs/usePibt can't ride along (the
  // backend 400s on useCbs + non-SIPP, and the continuous executor ignores both).
  const setPlanner = (next: PlannerKind) => {
    setParam('planner', next)
    if (next !== 'Sipp') setResolver('off')
  }
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
              { id: horizonEnabled ? 'controls.horizon.rhcrTag' : 'controls.horizon.unboundedTag' },
              { value: horizonWindow }
            )}
          </span>
        </div>
        <Segmented
          block
          value={horizonEnabled ? 'rhcr' : 'full'}
          onChange={(v) =>
            setParam('horizonWindowMs', String(v) === 'rhcr' ? horizonWindow : undefined)
          }
          options={[
            { label: intl.formatMessage({ id: 'controls.horizon.full' }), value: 'full' },
            { label: intl.formatMessage({ id: 'controls.horizon.rhcr' }), value: 'rhcr' },
          ]}
        />
        {horizonEnabled && (
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

      {/* Local standoff resolver (SIPP-only): the mutually-exclusive cluster owner for physical standoffs.
          Off = raw baseline; PIBT = fast greedy priority-inheritance (v3); CBS = complete local conflict-based
          search (v4), which cracks the corridor swaps / blocking chains PIBT's greedy ordering can't. */}
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
            { label: 'PIBT', value: 'pibt' },
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
