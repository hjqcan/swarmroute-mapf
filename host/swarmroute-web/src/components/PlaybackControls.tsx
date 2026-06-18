import { Segmented, Slider } from 'antd'
import { Pause, Play, RotateCcw } from 'lucide-react'
import { useIntl } from 'react-intl'
import { useSimStore, type PlaybackSpeed } from '@/store/simStore'
import { tickAtCursor, tickRange } from '@/utils/simModel'

const SPEEDS: PlaybackSpeed[] = [0.5, 1, 2, 4]

/**
 * Playback transport for the field + ribbon: play/pause, a scrub slider over the
 * timeline frames (one frame = one tick), and a speed selector. The slider and
 * the field/ribbon all read the same store cursor, so scrubbing moves everything.
 */
export default function PlaybackControls() {
  const intl = useIntl()
  const result = useSimStore((s) => s.result)
  const playing = useSimStore((s) => s.playing)
  const cursor = useSimStore((s) => s.cursor)
  const speed = useSimStore((s) => s.speed)
  const setCursor = useSimStore((s) => s.setCursor)
  const setSpeed = useSimStore((s) => s.setSpeed)
  const setPlaying = useSimStore((s) => s.setPlaying)
  const togglePlaying = useSimStore((s) => s.togglePlaying)

  const frameCount = result?.timeline.frames.length ?? 0
  const maxIndex = Math.max(0, frameCount - 1)
  const disabled = frameCount === 0
  const atEnd = cursor >= maxIndex - 0.001

  const onPlayPause = () => {
    if (disabled) return
    // If we're parked at the end, a play press restarts from 0.
    if (!playing && atEnd) {
      setCursor(0)
      setPlaying(true)
      return
    }
    togglePlaying()
  }

  // Label from the engine tick carried in the frame, not the cursor index, so the
  // counter matches the collision banner ("第 N 节拍") and the ribbon axis.
  const currentTick = result ? tickAtCursor(result, cursor) : 0
  const lastTick = result ? tickRange(result).last : 0

  return (
    <div className="flex items-center gap-4">
      <button
        type="button"
        onClick={onPlayPause}
        disabled={disabled}
        aria-label={intl.formatMessage({ id: playing ? 'playback.pause' : 'playback.play' })}
        className="flex h-10 w-10 shrink-0 items-center justify-center rounded-full bg-accent text-base transition-opacity hover:opacity-90 disabled:cursor-not-allowed disabled:opacity-30"
      >
        {atEnd && !playing ? (
          <RotateCcw size={18} />
        ) : playing ? (
          <Pause size={18} fill="currentColor" />
        ) : (
          <Play size={18} fill="currentColor" className="translate-x-0.5" />
        )}
      </button>

      <div className="flex min-w-[3.5rem] items-baseline gap-1.5">
        <span className="text-2xs uppercase tracking-wider text-text-muted">
          {intl.formatMessage({ id: 'playback.tick' })}
        </span>
        <span className="font-mono text-base tabular-nums text-text-primary">{currentTick}</span>
      </div>

      <Slider
        className="flex-1"
        min={0}
        max={maxIndex || 1}
        step={0.01}
        value={cursor}
        disabled={disabled}
        onChange={(v) => {
          setPlaying(false)
          setCursor(Number(v))
        }}
        tooltip={{ open: false }}
      />

      <span className="hidden font-mono text-2xs tabular-nums text-text-muted sm:inline">
        {intl.formatMessage(
          { id: 'playback.frameOf' },
          { current: currentTick, total: lastTick }
        )}
      </span>

      <Segmented
        size="small"
        value={speed}
        disabled={disabled}
        onChange={(v) => setSpeed(v as PlaybackSpeed)}
        options={SPEEDS.map((s) => ({ label: `${s}×`, value: s }))}
      />
    </div>
  )
}
