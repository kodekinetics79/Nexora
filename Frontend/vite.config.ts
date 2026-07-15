import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
  },
  build: {
    rollupOptions: {
      output: {
        // FE-09: split heavy vendors into their own chunks so the entry bundle
        // stays small and vendor code is cached independently of app code.
        manualChunks(id: string) {
          if (!id.includes('node_modules')) return;
          if (
            id.includes('node_modules/react/') ||
            id.includes('node_modules/react-dom/') ||
            id.includes('node_modules/scheduler/') ||
            id.includes('node_modules/react-router') ||
            id.includes('node_modules/react-router-dom')
          ) {
            return 'react-vendor';
          }
          if (id.includes('node_modules/@mui/') || id.includes('node_modules/@emotion/')) {
            return 'mui-vendor';
          }
          if (id.includes('node_modules/recharts/') || id.includes('node_modules/d3-')) {
            return 'charts-vendor';
          }
          if (id.includes('node_modules/xlsx/')) {
            return 'xlsx-vendor';
          }
        },
      },
    },
  },
})
