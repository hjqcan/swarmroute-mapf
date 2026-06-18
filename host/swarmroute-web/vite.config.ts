import { resolve } from 'path'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// Standard Vite web app (no Electron). The .NET Host runs on http://localhost:5062;
// we proxy /api there in dev so the browser never hits CORS.
export default defineConfig({
  resolve: {
    alias: {
      '@': resolve(__dirname, './src'),
    },
  },
  server: {
    host: '0.0.0.0',
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5062',
        changeOrigin: true,
      },
    },
  },
  plugins: [react()],
})
