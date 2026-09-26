import { describe, expect, it } from 'vitest';
import { getContrastRatio } from '@mui/material/styles';
import { NEU_SURFACE, clayInk, neuInset, neuRaised } from './neumorphic';
import { SEAL_WORST_GROUND, glanceCssVariables } from './tokens';

// The theme's text tokens (ThemeContext.tsx). Stated rather than imported: the clay is only
// acceptable if these still clear AA on it, so a change to either side should fail here.
const TEXT = {
  light: { primary: '#15181e', secondary: '#5f6673' },
  dark: { primary: '#eceef2', secondary: '#a3a9b5' },
} as const;

const blend = (overHex: string, alpha: number, underHex: string): string => {
  const ch = (hex: string, i: number) => parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16);
  const out = [0, 1, 2].map((i) => Math.round(alpha * ch(overHex, i) + (1 - alpha) * ch(underHex, i)));
  return `#${out.map((v) => v.toString(16).padStart(2, '0')).join('').toUpperCase()}`;
};

describe('dashboard clay', () => {
  for (const mode of ['light', 'dark'] as const) {
    it(`keeps body text at AA on the ${mode} clay`, () => {
      expect(getContrastRatio(TEXT[mode].primary, NEU_SURFACE[mode])).toBeGreaterThanOrEqual(4.5);
      expect(getContrastRatio(TEXT[mode].secondary, NEU_SURFACE[mode])).toBeGreaterThanOrEqual(4.5);
    });

    it(`keeps the brass seal ink at AA on the governed seal over ${mode} clay`, () => {
      const ink = glanceCssVariables(mode)['--nx-glance-seal-ink'];
      expect(getContrastRatio(ink, SEAL_WORST_GROUND[mode])).toBeGreaterThanOrEqual(4.5);
      expect(getContrastRatio(ink, NEU_SURFACE[mode])).toBeGreaterThanOrEqual(4.5);
    });
  }

  // The exact inks axe failed in CI on PR #219 (theme brass link, theme success green, both derived
  // against white): re-derived for the clay they must clear AA, in both modes.
  it.each(['#8c6612', '#257e58', '#c9931a', '#2f9e6e', '#c62828'])('re-derives %s to AA on the clay', (color) => {
    for (const mode of ['light', 'dark'] as const) {
      expect(getContrastRatio(clayInk(color, mode), NEU_SURFACE[mode])).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('states the governed seal ground as it actually composites onto the clay', () => {
    expect(blend('#C9931A', 0.14, NEU_SURFACE.light)).toBe(SEAL_WORST_GROUND.light);
    expect(blend('#E3BE71', 0.18, NEU_SURFACE.dark)).toBe(SEAL_WORST_GROUND.dark);
  });

  it('lights raised objects from the top-left and presses wells in', () => {
    expect(neuRaised('light', 8)).toMatch(/^inset 1px 1px 0 .*, -8px -8px 12px .*, 8px 8px 12px /);
    expect(neuInset('dark', 2)).toMatch(/^inset 1px 1px 0 .*, inset 2px 2px 3px .*, inset -2px -2px 3px /);
  });
});
