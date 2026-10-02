import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

const PUBLIC_ICON_PATTERN = /node_modules\/@mui\/icons-material\/(?:esm\/)?(?:ArrowForwardRounded|CheckRounded|CheckCircleOutlined|DarkMode|LightMode|LockOutlined|MailOutlined|MarkEmailReadOutlined|RadioButtonUnchecked|ReportProblemOutlined|Refresh|SettingsOutlined|VerifiedUserOutlined|Visibility|VisibilityOff)\.m?js$/

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    port: 3000,
    // Live Playwright writes screenshots, traces and HTML reports inside Frontend while the
    // browser is running. Watching those artifacts makes Vite force a full-page reload between
    // assertions, leaving lazy routes on their loading spinner and invalidating the journey.
    watch: {
      ignored: ['**/test-results/**', '**/playwright-report*/**'],
    },
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
          // Icon modules are tiny but numerous. Keep the handful used by public auth separate so
          // `/login` never inherits the tenant catalog, then combine workspace icons into one
          // cached request instead of making the browser negotiate dozens of sub-kilobyte files.
          if (PUBLIC_ICON_PATTERN.test(id)) {
            return 'mui-public-icons';
          }
          if (id.includes('node_modules/@mui/icons-material/')) {
            return 'mui-workspace-icons';
          }
        },
      },
    },
  },
})
