import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  // Relative asset URLs let the same build run at the domain root or an isolated
  // fallback path such as /erp/ when the primary domain is awaiting ICP approval.
  base: './',
  plugins: [react()],
  server: {
    proxy: {
      '/api': 'http://localhost:5153',
      '/openapi': 'http://localhost:5153',
    },
  },
})
