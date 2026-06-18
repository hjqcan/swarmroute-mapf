import { ConfigProvider, theme } from 'antd'
import zhCN from 'antd/locale/zh_CN'
import enUS from 'antd/locale/en_US'
import { IntlProvider } from 'react-intl'
import { useSystemState } from '@/store/system'
import { languageMessage } from '@/lang'
import { COLORS } from '@/utils/palette'
import SimulatorPage from '@/pages/simulator'

const FONT_SANS = 'Inter, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif'

/**
 * App shell: AntD dark theme tuned to the Dispatcher's-console palette so the
 * controls inherit our tokens (NOT default AntD blue), plus react-intl for
 * zh-CN (default) / en-US.
 */
export default function App() {
  const lang = useSystemState((s) => s.lang)

  return (
    <ConfigProvider
      locale={lang === 'zh-CN' ? zhCN : enUS}
      theme={{
        algorithm: theme.darkAlgorithm,
        token: {
          colorPrimary: COLORS.accent, // warm amber
          colorInfo: COLORS.accent,
          colorError: COLORS.danger,
          colorBgBase: COLORS.base,
          colorBgContainer: COLORS.panel,
          colorBgElevated: COLORS.panel,
          colorBorder: COLORS.hairline,
          colorBorderSecondary: COLORS.hairline,
          colorText: COLORS.textPrimary,
          colorTextSecondary: COLORS.textMuted,
          colorTextTertiary: COLORS.textMuted,
          colorTextPlaceholder: COLORS.textMuted,
          fontFamily: FONT_SANS,
          borderRadius: 8,
          controlHeight: 36,
        },
        components: {
          Button: { primaryColor: COLORS.base, fontWeight: 600 },
          Slider: {
            trackBg: COLORS.accent,
            trackHoverBg: COLORS.accent,
            handleColor: COLORS.accent,
            handleActiveColor: COLORS.accent,
            railBg: COLORS.hairline,
            railHoverBg: COLORS.hairline,
            dotActiveBorderColor: COLORS.accent,
          },
          Segmented: {
            itemSelectedBg: COLORS.accent,
            itemSelectedColor: COLORS.base,
            trackBg: COLORS.base,
          },
          InputNumber: { activeBorderColor: COLORS.accent, hoverBorderColor: COLORS.accent },
        },
      }}
    >
      <IntlProvider
        locale={lang}
        messages={languageMessage[lang]}
        defaultLocale="zh-CN"
        onError={() => undefined}
      >
        <SimulatorPage />
      </IntlProvider>
    </ConfigProvider>
  )
}
