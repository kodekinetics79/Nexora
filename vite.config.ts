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
        manualChunks(id) {
          if (!id.includes('node_modules')) return undefined;
          if (id.includes('react') || id.includes('react-dom') || id.includes('react-router-dom')) return 'react';
          if (id.includes('@mui/x-data-grid')) return 'dataGrid';
          if (id.includes('@mui') || id.includes('@emotion')) return 'mui';
          if (id.includes('@tanstack')) return 'query';
          if (id.includes('recharts')) return 'charts';
          if (id.includes('i18next') || id.includes('react-i18next')) return 'i18n';
          if (id.includes('xlsx')) return 'xlsx';
          if (id.includes('axios') || id.includes('dayjs') || id.includes('lodash') || id.includes('jwt-decode')) {
            return 'utilities';
          }
          return 'vendor';
        },
      },
    },
  },
})
