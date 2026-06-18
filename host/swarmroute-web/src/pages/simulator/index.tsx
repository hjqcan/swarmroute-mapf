import AppHeader from '@/components/AppHeader'
import ControlRail from '@/components/ControlRail'
import FieldCanvas from '@/components/FieldCanvas'
import PlaybackControls from '@/components/PlaybackControls'
import ReservationRibbon from '@/components/ReservationRibbon'
import TelemetryColumn from '@/components/TelemetryColumn'
import { usePlaybackLoop } from '@/hooks/usePlaybackLoop'

/**
 * The single simulator page. Dispatcher's-console layout:
 *   header
 *   ┌──────────┬───────────────────────────┬───────────┐
 *   │ control  │ field (hero) + playback   │ telemetry │
 *   │ rail     │ ───────────────────────── │           │
 *   │ ~280px   │ reservation ribbon ~160px │  ~300px   │
 *   └──────────┴───────────────────────────┴───────────┘
 * On narrow screens the three columns stack vertically.
 */
export default function SimulatorPage() {
  // Single shared playback clock for the field + ribbon.
  usePlaybackLoop()

  return (
    <div className="flex h-full min-h-0 flex-col bg-base text-text-primary">
      <AppHeader />

      <div className="flex min-h-0 flex-1 flex-col lg:flex-row">
        {/* Left control rail */}
        <aside className="shrink-0 border-b border-hairline bg-panel p-4 lg:w-[280px] lg:border-b-0 lg:border-r">
          <ControlRail />
        </aside>

        {/* Center: field (hero) + playback transport + reservation ribbon */}
        <main className="flex min-h-0 min-w-0 flex-1 flex-col gap-3 p-4">
          <div className="min-h-[280px] flex-1">
            <FieldCanvas />
          </div>
          <div className="shrink-0 rounded-lg border border-hairline bg-panel px-4 py-3">
            <PlaybackControls />
          </div>
          <div className="h-[160px] shrink-0">
            <ReservationRibbon />
          </div>
        </main>

        {/* Right telemetry column */}
        <aside className="shrink-0 overflow-y-auto border-t border-hairline bg-panel p-4 lg:w-[300px] lg:border-l lg:border-t-0">
          <TelemetryColumn />
        </aside>
      </div>
    </div>
  )
}
