import { useLayoutEffect, useRef, useState } from 'react';

/**
 * The rendered width of an element, in CSS pixels, kept current as its cell resizes.
 *
 * The glance charts draw their SVG at exactly this width (viewBox width = measured width), so one
 * unit is one pixel and an 11px label renders at 11px in any cell. A fixed viewBox scaled with
 * `width="100%"` shrank every label with the band — to 7px at 1280 wide.
 *
 * Until the first measurement (and wherever ResizeObserver does not exist, as in jsdom) the
 * design width stands in, so geometry is stable in tests and on first paint.
 */
export function useMeasuredWidth<T extends Element>(fallback: number, min = 240) {
  const ref = useRef<T>(null);
  const [width, setWidth] = useState(fallback);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element || typeof ResizeObserver === 'undefined') return undefined;
    const observer = new ResizeObserver(([entry]) => {
      const next = Math.round(entry.contentRect.width);
      if (next > 0) setWidth(Math.max(min, next));
    });
    observer.observe(element);
    return () => observer.disconnect();
  }, [min]);
  return [ref, width] as const;
}
