import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import pkg from './package.json' with { type: 'json' }

export default defineConfig({
  plugins: [react()],
  define: {
    __APP_VERSION__: JSON.stringify(pkg.version),
  },
  server: {
    host: '127.0.0.1',
    proxy: {
      '/health': 'http://127.0.0.1:8080',
      '/api': 'http://127.0.0.1:8080',
    },
  },
})
