import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';

// Unit tests only. `vite.config.ts` stays untouched so the production build is unchanged; vitest
// picks this file up in preference to it.
export default defineConfig({
  plugins: [react()],
  test: {
    // Scoped to src/ deliberately: the e2e/ directory holds Playwright specs, and vitest's default
    // include pattern would otherwise collect and fail on them.
    include: ['src/**/*.test.{ts,tsx}'],
    environment: 'jsdom',
    setupFiles: ['src/test/setup.ts'],
    css: false,
  },
});
