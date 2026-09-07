import { execSync } from 'node:child_process'
import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

/**
 * Which build is this?
 *
 * Nobody could answer that — not from the running app, not from a screenshot. So "nothing
 * changed" was unanswerable without driving a browser and comparing pixels, and a stale bundle
 * was indistinguishable from a change that had not been made. A build has to be able to say what
 * it is.
 *
 * Falls back rather than failing: a build from a tarball with no git history still produces a
 * usable stamp, because breaking the build to learn its own commit would be a poor trade.
 */
function buildStamp() {
  const at = new Date().toISOString()
  try {
    const sha = execSync('git rev-parse --short HEAD', { stdio: ['ignore', 'pipe', 'ignore'] })
      .toString().trim()
    const dirty = execSync('git status --porcelain', { stdio: ['ignore', 'pipe', 'ignore'] })
      .toString().trim().length > 0
    return { sha: dirty ? `${sha}+` : sha, at }
  } catch {
    return { sha: 'unknown', at }
  }
}

const stamp = buildStamp()

// https://vitejs.dev/config/
export default defineConfig({
  plugins: [react()],
  define: {
    __BUILD_SHA__: JSON.stringify(stamp.sha),
    __BUILT_AT__: JSON.stringify(stamp.at),
  },
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
          if (id.includes('node_modules/@mui/') || id.includes('node_modules/@emotion/')) {
            return 'mui-vendor';
          }
        },
      },
    },
  },
})
