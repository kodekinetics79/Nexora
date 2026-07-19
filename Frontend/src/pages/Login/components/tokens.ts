/**
 * Design tokens for the pre-auth landing/login stage.
 *
 * This page intentionally commits to a dark, brand-fixed look regardless of the
 * in-app theme mode (it renders before any user preference is meaningful), so
 * every color here is hardcoded rather than pulled from the MUI palette.
 *
 * Accent system: one confident electric-blue family (sky/blue-600) with violet
 * and cyan as supporting aurora hues. Text-on-dark pairs are AA-checked:
 * ACCENT_SOFT (#7DD3FC) on the darkest surface (#060A18) is ~11:1; white on
 * the CTA gradient's lightest stop (#2563EB) is ~5.2:1.
 */

/** Brand accent (electric sky) — glows, icon chips, gradient highlights. */
export const ACCENT = '#38BDF8';
/** Deeper accent (blue-600) for the CTA gradient body / pressed states. */
export const ACCENT_DEEP = '#2563EB';
/** Supporting violet for aurora depth and gradient tails. */
export const VIOLET = '#8B5CF6';
/** Supporting cyan for aurora sparkle. */
export const CYAN = '#22D3EE';
/** Lightened accent for text and icons on dark glass (≥ 10:1 contrast). */
export const ACCENT_SOFT = '#7DD3FC';
/** Near-white accent tint for focus rings and gradient hot spots. */
export const ACCENT_TINT = '#BAE6FD';

/** Deep stage background behind the aurora field. */
export const PAGE_BG = '#060A18';
/** Opaque card fill used when backdrop-filter is unavailable. */
export const SOLID_CARD_BG = '#0D1730';

export const TEXT_HI = 'rgba(255, 255, 255, 0.96)';
export const TEXT_MID = 'rgba(226, 236, 255, 0.78)';
export const TEXT_LOW = 'rgba(203, 216, 240, 0.58)';
export const HAIRLINE = 'rgba(255, 255, 255, 0.12)';

/** @supports guard: true only where backdrop-filter is NOT implemented. */
export const NO_BACKDROP_FILTER =
  '@supports not ((backdrop-filter: blur(4px)) or (-webkit-backdrop-filter: blur(4px)))';

/**
 * Liquid-glass surface: translucent fill + blur, lighter 1px border, inset
 * top-edge highlight, and a solid dark fallback where backdrop-filter is
 * unsupported (text stays AA either way).
 */
export const glassSx = {
  backgroundColor: 'rgba(11, 18, 38, 0.52)',
  backdropFilter: 'blur(26px) saturate(170%)',
  WebkitBackdropFilter: 'blur(26px) saturate(170%)',
  border: `1px solid ${HAIRLINE}`,
  boxShadow: 'inset 0 1px 0 rgba(255, 255, 255, 0.10)',
  [NO_BACKDROP_FILTER]: {
    backgroundColor: SOLID_CARD_BG,
  },
};

/** Visible keyboard focus ring with ≥3:1 contrast against the dark glass. */
export const focusRingSx = {
  '&:focus-visible': {
    outline: `2px solid ${ACCENT_TINT}`,
    outlineOffset: '2px',
  },
};

/** Dark-glass treatment for outlined MUI text fields. */
export const glassFieldSx = {
  '& .MuiOutlinedInput-root': {
    color: TEXT_HI,
    backgroundColor: 'rgba(148, 186, 255, 0.06)',
    borderRadius: '14px',
    transition: 'background-color 0.2s ease, box-shadow 0.2s ease',
    '& .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(186, 210, 255, 0.22)' },
    '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(186, 210, 255, 0.40)' },
    '&.Mui-focused': {
      backgroundColor: 'rgba(148, 186, 255, 0.09)',
      boxShadow: '0 0 0 3px rgba(56, 189, 248, 0.28)',
    },
    '&.Mui-focused .MuiOutlinedInput-notchedOutline': {
      borderColor: ACCENT,
      borderWidth: '1px',
    },
  },
  '& .MuiInputLabel-root': { color: 'rgba(226, 236, 255, 0.72)' },
  '& .MuiInputLabel-root.Mui-focused': { color: ACCENT_SOFT },
  // Cover hover/focus too — Chrome re-applies its yellow autofill fill on
  // those states unless they are styled explicitly.
  '& input:-webkit-autofill, & input:-webkit-autofill:hover, & input:-webkit-autofill:focus': {
    WebkitBoxShadow: '0 0 0 1000px #101B36 inset',
    WebkitTextFillColor: '#ffffff',
    caretColor: '#ffffff',
    borderRadius: 'inherit',
  },
};

/** Dark-glass treatment for the outlined MUI Select (multi-org chooser). */
export const glassSelectSx = {
  color: TEXT_HI,
  backgroundColor: 'rgba(148, 186, 255, 0.06)',
  borderRadius: '14px',
  '& .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(186, 210, 255, 0.22)' },
  '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(186, 210, 255, 0.40)' },
  '&.Mui-focused': { boxShadow: '0 0 0 3px rgba(56, 189, 248, 0.28)' },
  '&.Mui-focused .MuiOutlinedInput-notchedOutline': { borderColor: ACCENT },
  '& .MuiSelect-icon': { color: 'rgba(226, 236, 255, 0.72)' },
};

/** Info alert restyled for the dark glass card. */
export const infoAlertSx = {
  mb: 3,
  borderRadius: '14px',
  backgroundColor: 'rgba(56, 189, 248, 0.14)',
  border: '1px solid rgba(56, 189, 248, 0.35)',
  color: '#CFEBFC',
  '& .MuiAlert-icon': { color: ACCENT_SOFT },
};

/** Error alert restyled for the dark glass card. */
export const errorAlertSx = {
  mb: 3,
  borderRadius: '14px',
  backgroundColor: 'rgba(244, 63, 94, 0.14)',
  border: '1px solid rgba(244, 63, 94, 0.34)',
  color: '#FECDD3',
  '& .MuiAlert-icon': { color: '#FDA4AF' },
};
