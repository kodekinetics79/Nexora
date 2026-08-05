import eslint from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';
import jsxA11y from 'eslint-plugin-jsx-a11y';

/**
 * eslint-plugin-jsx-a11y ships its recommended set at "error". This codebase
 * has a large backlog of pre-existing accessibility violations outside the
 * current a11y workstream, so every rule is downgraded to a warning: issues
 * surface in editors and in `npm run lint:a11y`, but they do not turn an
 * otherwise-green build red. Promote these to "error" once the backlog is
 * cleared.
 */
const asWarnings = (rules) =>
  Object.fromEntries(
    Object.entries(rules ?? {}).map(([name, config]) => {
      const [severity, ...options] = Array.isArray(config) ? config : [config];
      // Preserve rules the preset deliberately disables.
      if (severity === 'off' || severity === 0) return [name, config];
      return [name, options.length > 0 ? ['warn', ...options] : 'warn'];
    }),
  );

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
    rules: asWarnings(jsxA11y.flatConfigs.recommended.rules),
  },
);
