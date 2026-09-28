import { useEffect, useRef, type RefObject } from 'react';

/**
 * Escape closes a band's open drill while focus is anywhere inside the band.
 *
 * Listening on the document rather than putting onKeyDown on the band's container keeps the
 * container a plain, non-interactive element — the buttons inside it stay the only controls.
 */
export function useEscapeWithin<T extends HTMLElement>(active: boolean, onEscape: () => void): RefObject<T | null> {
  const ref = useRef<T | null>(null);
  const latest = useRef(onEscape);
  useEffect(() => { latest.current = onEscape; });
  useEffect(() => {
    if (!active) return undefined;
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || !ref.current) return;
      if (ref.current.contains(document.activeElement)) latest.current();
    };
    document.addEventListener('keydown', onKey);
    return () => document.removeEventListener('keydown', onKey);
  }, [active]);
  return ref;
}
