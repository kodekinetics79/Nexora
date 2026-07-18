/**
 * Design tokens for the pre-auth landing/login stage.
 *
 * This page intentionally commits to a dark, brand-fixed look regardless of the
 * in-app theme mode (it renders before any user preference is meaningful), so
 * every color here is hardcoded rather than pulled from the MUI palette.
 */

/** Brand accent (steel blue) — fills, glows, gradients. */
export const ACCENT = '#4682B4';
/** Deeper accent for gradient tails / pressed states. */
export const ACCENT_DEEP = '#2E5E8F';
/** Lightened accent for text and icons on dark glass (≥ 8:1 contrast). */
export const ACCENT_SOFT = '#8FBEDF';
/** Near-white accent tint for focus rings and gradient highlights. */
export const ACCENT_TINT = '#C9DFF0';

/** Deep stage background behind the aurora field. */
export const PAGE_BG = '#070B14';
/** Opaque card fill used when backdrop-filter is unavailable. */
export const SOLID_CARD_BG = '#0E1626';

export const TEXT_HI = 'rgba(255, 255, 255, 0.95)';
export const TEXT_MID = 'rgba(255, 255, 255, 0.74)';
export const TEXT_LOW = 'rgba(255, 255, 255, 0.60)';
export const HAIRLINE = 'rgba(255, 255, 255, 0.10)';

/** @supports guard: true only where backdrop-filter is NOT implemented. */
export const NO_BACKDROP_FILTER =
  '@supports not ((backdrop-filter: blur(4px)) or (-webkit-backdrop-filter: blur(4px)))';

/**
 * Liquid-glass surface: translucent fill + blur, hairline border, and a solid
 * dark fallback where backdrop-filter is unsupported (text stays AA either way).
 */
export const glassSx = {
  backgroundColor: 'rgba(13, 22, 40, 0.60)',
  backdropFilter: 'blur(22px) saturate(150%)',
  WebkitBackdropFilter: 'blur(22px) saturate(150%)',
  border: `1px solid ${HAIRLINE}`,
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
    backgroundColor: 'rgba(255, 255, 255, 0.05)',
    borderRadius: '14px',
    transition: 'background-color 0.2s ease, box-shadow 0.2s ease',
    '& .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(255, 255, 255, 0.18)' },
    '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(255, 255, 255, 0.34)' },
    '&.Mui-focused': {
      backgroundColor: 'rgba(255, 255, 255, 0.08)',
      boxShadow: '0 0 0 3px rgba(143, 190, 223, 0.30)',
    },
    '&.Mui-focused .MuiOutlinedInput-notchedOutline': {
      borderColor: ACCENT_SOFT,
      borderWidth: '1px',
    },
  },
  '& .MuiInputLabel-root': { color: 'rgba(255, 255, 255, 0.70)' },
  '& .MuiInputLabel-root.Mui-focused': { color: ACCENT_SOFT },
  // Cover hover/focus too — Chrome re-applies its yellow autofill fill on
  // those states unless they are styled explicitly.
  '& input:-webkit-autofill, & input:-webkit-autofill:hover, & input:-webkit-autofill:focus': {
    WebkitBoxShadow: '0 0 0 1000px #101B30 inset',
    WebkitTextFillColor: '#ffffff',
    caretColor: '#ffffff',
    borderRadius: 'inherit',
  },
};

/** Dark-glass treatment for the outlined MUI Select (multi-org chooser). */
export const glassSelectSx = {
  color: TEXT_HI,
  backgroundColor: 'rgba(255, 255, 255, 0.05)',
  borderRadius: '14px',
  '& .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(255, 255, 255, 0.18)' },
  '&:hover .MuiOutlinedInput-notchedOutline': { borderColor: 'rgba(255, 255, 255, 0.34)' },
  '&.Mui-focused': { boxShadow: '0 0 0 3px rgba(143, 190, 223, 0.30)' },
  '&.Mui-focused .MuiOutlinedInput-notchedOutline': { borderColor: ACCENT_SOFT },
  '& .MuiSelect-icon': { color: 'rgba(255, 255, 255, 0.70)' },
};

/** Info alert restyled for the dark glass card. */
export const infoAlertSx = {
  mb: 3,
  borderRadius: '14px',
  backgroundColor: 'rgba(70, 130, 180, 0.16)',
  border: '1px solid rgba(70, 130, 180, 0.35)',
  color: '#CFE3F2',
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
