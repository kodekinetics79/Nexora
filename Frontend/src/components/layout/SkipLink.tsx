import React from 'react';
import { Box } from '@mui/material';

/** id of the `<main>` element every layout renders; the skip-link target. */
export const MAIN_CONTENT_ID = 'main-content';

interface SkipLinkProps {
  /** id of the element to jump to. Defaults to the main landmark. */
  targetId?: string;
  children?: React.ReactNode;
}

/**
 * "Skip to main content" link — WCAG 2.1 SC 2.4.1 (Bypass Blocks).
 *
 * Rendered as the first focusable element in each layout so keyboard and
 * switch users can jump past the sidebar (which repeats ~60 links on every
 * page) straight to page content.
 *
 * It is off-screen rather than `display: none` so it stays in the tab order,
 * and slides into view on focus. Clicking/activating it moves real DOM focus to
 * the target (`preventDefault` avoids pushing a `#hash` history entry that the
 * router would then have to unwind).
 */
const SkipLink: React.FC<SkipLinkProps> = ({
  targetId = MAIN_CONTENT_ID,
  children = 'Skip to main content',
}) => {
  const handleActivate = (event: React.MouseEvent<HTMLAnchorElement>) => {
    const target = document.getElementById(targetId);
    if (!target) return; // let the browser attempt the default hash jump
    event.preventDefault();
    target.focus();
    target.scrollIntoView({ block: 'start' });
  };

  return (
    // eslint-disable-next-line jsx-a11y/click-events-have-key-events, jsx-a11y/no-static-element-interactions -- `Box` is mapped to `div` for jsx-a11y, but `component="a"` renders a real <a href>, which handles Enter natively.
    <Box
      component="a"
      href={`#${targetId}`}
      onClick={handleActivate}
      sx={{
        position: 'fixed',
        top: 8,
        left: 8,
        px: 2.5,
        py: 1.25,
        borderRadius: 2,
        border: '2px solid',
        borderColor: 'primary.main',
        backgroundColor: 'background.paper',
        color: 'text.primary',
        fontSize: '0.9rem',
        fontWeight: 700,
        textDecoration: 'none',
        boxShadow: 6,
        // Off-screen until focused — stays in the tab order the whole time.
        transform: 'translateY(calc(-100% - 16px))',
        transition: (theme) =>
          theme.transitions.create('transform', { duration: theme.transitions.duration.shortest }),
        zIndex: (theme) => theme.zIndex.tooltip + 1,
        '&:focus': {
          transform: 'translateY(0)',
          outline: '3px solid',
          outlineColor: 'primary.main',
          outlineOffset: 2,
        },
        '@media (prefers-reduced-motion: reduce)': {
          transition: 'none',
        },
      }}
    >
      {children}
    </Box>
  );
};

export default SkipLink;
