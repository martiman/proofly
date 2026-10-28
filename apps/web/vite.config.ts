import vue from '@vitejs/plugin-vue'
import { defineConfig, loadEnv } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = { ...loadEnv(mode, process.cwd(), ''), ...process.env }
  // Dev: the browser calls /api/v1 on the Vite origin; Vite forwards to the real API (scripts/dev-backend.sh prints its port).
  const apiTarget = env.PROOFLY_API_URL ?? `http://127.0.0.1:${env.PROOFLY_API_PORT ?? 5080}`
  return {
    plugins: [vue()],
    server: {
      port: env.WEB_PORT ? Number(env.WEB_PORT) : undefined,
      proxy: { '/api': { target: apiTarget, changeOrigin: false } },
    },
  }
})
