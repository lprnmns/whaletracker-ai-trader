import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5090',
        changeOrigin: true,
      },
      '/hubs': {
        target: 'http://localhost:5090',
        ws: true,
        changeOrigin: true,
      },
      '/login.html': {
        target: 'http://localhost:5090',
        changeOrigin: true,
      },
      '/admin.html': {
        target: 'http://localhost:5090',
        changeOrigin: true,
      },
      '/js': {
        target: 'http://localhost:5090',
        changeOrigin: true,
      },
      '/css': {
        target: 'http://localhost:5090',
        changeOrigin: true,
      },
    },
  },
})
