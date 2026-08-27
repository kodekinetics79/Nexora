import eslint from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import jsxA11y from 'eslint-plugin-jsx-a11y';

export default tseslint.config(
  {
    ignores: [
      'dist/**',
      'node_modules/**',
      'playwright-report*/**',
      'test-results/**',
      '*.cjs',
    ],
  },
  eslint.configs.recommended,
  ...tseslint.configs.recommended,
  {
    files: ['**/*.{ts,tsx}'],
    languageOptions: {
      globals: { ...globals.browser, ...globals.es2022 },
    },
    rules: {
      '@typescript-eslint/no-explicit-any': 'off',
      '@typescript-eslint/ban-ts-comment': 'off',
      '@typescript-eslint/no-unused-expressions': 'off',
      '@typescript-eslint/no-unused-vars': 'off',
      'no-useless-assignment': 'off',
    },
  },
  {
    files: ['**/*.{jsx,tsx}'],
    plugins: { 'jsx-a11y': jsxA11y },
    languageOptions: {
      ...jsxA11y.flatConfigs.recommended.languageOptions,
    },
    settings: {
      // jsx-a11y only inspects lowercase DOM elements unless told what a
      // component renders. Almost every "div" in this codebase is a MUI <Box>,
      // so without this mapping the plugin is effectively blind here (it found
      // 13 issues without it, 86 with it — including every mouse-only onClick
      // handler on a non-interactive element).
      'jsx-a11y': {
        components: {
          Box: 'div',
          Paper: 'div',
          Stack: 'div',
          Grid: 'div',
          Button: 'button',
          IconButton: 'button',
        },
      },
    },
    // Accessibility regressions fail CI. The recommended preset deliberately
    // keeps its disabled rules disabled and reports every enabled rule as an error.
    rules: jsxA11y.flatConfigs.recommended.rules,
  },
);
