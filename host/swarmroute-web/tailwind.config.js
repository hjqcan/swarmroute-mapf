/** @type {import('tailwindcss').Config} */
export default {
  content: ['./index.html', './src/**/*.{js,ts,jsx,tsx}'],
  theme: {
    extend: {
      colors: {
        // Dispatcher's console palette
        base: '#0B1220',
        panel: '#141C2B',
        hairline: '#25324A',
        'text-primary': '#C7D2E2',
        'text-muted': '#6B7A93',
        accent: '#FFB020', // warm amber primary
        'accent-soft': 'rgba(255, 176, 32, 0.14)',
        danger: '#FF5C5C', // warning / collision
        'danger-soft': 'rgba(255, 92, 92, 0.14)',
      },
      fontFamily: {
        display: ['"Space Grotesk"', 'system-ui', 'sans-serif'],
        sans: ['Inter', 'system-ui', '-apple-system', 'sans-serif'],
        mono: ['"JetBrains Mono"', 'ui-monospace', 'SFMono-Regular', 'monospace'],
      },
      fontSize: {
        '2xs': ['0.625rem', { lineHeight: '0.875rem' }],
      },
      boxShadow: {
        panel: '0 1px 0 0 rgba(255,255,255,0.02), 0 8px 24px -12px rgba(0,0,0,0.6)',
        'accent-focus': '0 0 0 2px rgba(255, 176, 32, 0.28)',
      },
    },
  },
  plugins: [],
}
