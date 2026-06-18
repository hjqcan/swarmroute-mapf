import { Button } from 'antd'
import { Languages, Waypoints } from 'lucide-react'
import { useIntl } from 'react-intl'
import { useSystemState } from '@/store/system'

/** Top bar: product mark + title, and a language toggle (zh-CN ⇄ en-US). */
export default function AppHeader() {
  const intl = useIntl()
  const lang = useSystemState((s) => s.lang)
  const setLang = useSystemState((s) => s.setLang)

  return (
    <header className="flex shrink-0 items-center justify-between border-b border-hairline bg-panel px-4 py-2.5 sm:px-6">
      <div className="flex items-center gap-3">
        <span className="flex h-8 w-8 items-center justify-center rounded-md bg-accent-soft text-accent">
          <Waypoints size={18} />
        </span>
        <div className="leading-tight">
          <h1 className="font-display text-base font-semibold text-text-primary">
            {intl.formatMessage({ id: 'app.title' })}
          </h1>
          <p className="text-2xs uppercase tracking-[0.2em] text-text-muted">
            {intl.formatMessage({ id: 'app.subtitle' })}
          </p>
        </div>
      </div>
      <Button
        type="text"
        size="small"
        icon={<Languages size={15} />}
        onClick={() => setLang(lang === 'zh-CN' ? 'en-US' : 'zh-CN')}
        className="!text-text-muted hover:!text-text-primary"
      >
        {intl.formatMessage({ id: 'lang.toggle' })}
      </Button>
    </header>
  )
}
