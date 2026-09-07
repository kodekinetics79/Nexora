import { describe, expect, it } from 'vitest';
import { SERIES_PALETTE, SERIES_VAR, glanceCssVariables, seriesColor, seriesVar } from './tokens';

describe('glance series tokens', () => {
  // These five values were validated for CVD separation and contrast. If one of them changes the
  // validation is stale, so the test states them rather than reading them back from the source.
  it('holds the validated palette', () => {
    expect(SERIES_PALETTE.brassMark).toEqual({ light: '#9A6F12', dark: '#D9AE55' });
    expect(SERIES_PALETTE.brassBrand).toEqual({ light: '#C9931A', dark: '#E3BE71' });
    expect(SERIES_PALETTE.graphite).toEqual({ light: '#30363D', dark: '#8E99A5' });
    expect(SERIES_PALETTE.oxide).toEqual({ light: '#A33D2B', dark: '#DE7C67' });
    expect(SERIES_PALETTE.muted).toEqual({ light: '#68727E', dark: '#7E8894' });
  });

  it('resolves a token as a literal or as a custom property', () => {
    expect(seriesColor('oxide', 'dark')).toBe('#DE7C67');
    expect(seriesVar('graphite')).toBe(`var(${SERIES_VAR.graphite})`);
  });

  it('publishes every token, plus the seal chrome, for both modes', () => {
    for (const mode of ['light', 'dark'] as const) {
      const vars = glanceCssVariables(mode);
      for (const [token, name] of Object.entries(SERIES_VAR)) {
        expect(vars[name]).toBe(SERIES_PALETTE[token as keyof typeof SERIES_PALETTE][mode]);
      }
      expect(vars['--nx-glance-seal-ground']).toBeTruthy();
      expect(vars['--nx-glance-seal-rim']).toBeTruthy();
      expect(vars['--nx-glance-seal-ink']).toMatch(/^(#|rgb)/);
    }
  });
});
