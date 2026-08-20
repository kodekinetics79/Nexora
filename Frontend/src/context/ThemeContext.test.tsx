import { describe, expect, it } from 'vitest';
import { render } from '@testing-library/react';
import CssBaseline from '@mui/material/CssBaseline';
import { ThemeContextProvider } from './ThemeContext';

/**
 * Every stylesheet emotion has injected into the document, as text.
 *
 * Emotion writes rules as text nodes outside production and via `insertRule` inside it, so both
 * are read: a test that only looked at `textContent` would silently pass on an empty string.
 */
const injectedCss = () =>
  Array.from(document.querySelectorAll('style'))
    .map((el) => {
      const inline = el.textContent ?? '';
      if (inline.length > 0) return inline;
      try {
        return Array.from(el.sheet?.cssRules ?? []).map((rule) => rule.cssText).join('');
      } catch {
        return '';
      }
    })
    .join('');

/**
 * These assert that a global override REACHES THE DOCUMENT, not merely that it was written down.
 *
 * The first attempt at the tabular-figures fix put the rule in `src/theme.ts`, which is imported
 * by nothing — `main.tsx` mounts `ThemeContextProvider`, and the running theme is the
 * `createTheme` call inside it. The rule compiled, read correctly in review, and never loaded. So
 * the test renders the real provider with the real `CssBaseline` and inspects what was injected;
 * moving the override back into an unimported module fails it.
 */
describe('the live theme', () => {
  it('injects the tabular-figures rule through the theme that is actually mounted', () => {
    render(
      <ThemeContextProvider>
        <CssBaseline />
      </ThemeContextProvider>,
    );

    const css = injectedCss();
    expect(css).toContain('font-variant-numeric:tabular-nums');
    // The three selectors the rule is scoped to: right-aligned table cells, right-aligned
    // DataGrid cells, and the explicit opt-in class used where a figure is not in a grid.
    expect(css).toContain('.MuiTableCell-root.MuiTableCell-alignRight');
    expect(css).toContain('.MuiDataGrid-cell--textRight');
    expect(css).toContain('.tabular-nums');
  });

  it('keeps the rule off prose, so it stays a fix rather than a typography change', () => {
    render(
      <ThemeContextProvider>
        <CssBaseline />
      </ThemeContextProvider>,
    );

    // The selector must be scoped. A bare `body`/`*` rule would align digits everywhere,
    // including paragraphs, which is a design decision nobody asked for.
    expect(injectedCss()).not.toMatch(/(?:body|\*)\s*\{[^}]*font-variant-numeric/);
  });
});
