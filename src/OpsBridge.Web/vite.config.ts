import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [
    react(),
    tailwindcss(),
  ],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        // 127.0.0.1, NOT "localhost": on macOS "localhost" resolves to the
        // IPv6 loopback (::1) first, and the AirPlay receiver owns [::1]:5000,
        // so the proxy hop would hit the AirPlay receiver instead of the API
        // unless every Node process was forced to resolve IPv4 first
        // (NODE_OPTIONS=--dns-result-order=ipv4first). Pointing the proxy at
        // the literal IPv4 address makes the hop work without that flag.
        //
        // OPSBRIDGE_API_URL overrides the target for a local run on a
        // different port (CI uses the default 127.0.0.1:5000).
        target: process.env.OPSBRIDGE_API_URL ?? 'http://127.0.0.1:5000',
        changeOrigin: true,
        secure: false,
      },
    },
  },
})
