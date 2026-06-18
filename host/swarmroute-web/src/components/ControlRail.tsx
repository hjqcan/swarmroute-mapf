import { Button, InputNumber, Segmented, Slider } from 'antd'
import { Dices, Play, Loader2 } from 'lucide-react'
import { useIntl } from 'react-intl'
import { useSimStore } from '@/store/simStore'
import type { PlannerKind } from '@/types'

const FIELD_MIN = 4
const FIELD_MAX = 24
const AGV_MIN = 1
const AGV_MAX = 24

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

  // Guard against the engine's constraint: width*height >= 2*agvCount.
  const capacity = params.width * params.height
  const agvMax = Math.min(AGV_MAX, Math.floor(capacity / 2))
  const agvTooMany = params.agvCount > agvMax

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
          plans in time — converges where Dijkstra stalls). Defaults to SIPP; flip to Dijkstra to
          reproduce a standoff and watch the stuck AGVs' forward routes on the field. */}
      <div>
        <div className="mb-1.5 flex items-baseline justify-between">
          <label className="text-sm text-text-muted">
            {intl.formatMessage({ id: 'controls.planner' })}
          </label>
          <span className="font-mono text-xs text-text-muted">
            {intl.formatMessage({
              id: (params.planner ?? 'Sipp') === 'Dijkstra'
                ? 'controls.planner.dijkstraTag'
                : 'controls.planner.sippTag',
            })}
          </span>
        </div>
        <Segmented
          block
          value={params.planner ?? 'Sipp'}
          onChange={(v) => setParam('planner', v as PlannerKind)}
          options={[
            { label: 'SIPP', value: 'Sipp' },
            { label: 'Dijkstra', value: 'Dijkstra' },
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
