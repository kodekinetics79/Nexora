import { useId } from 'react';
import { seriesVar } from './tokens';

/**
 * One hatch, defined once.
 *
 * Hatch means "not decided yet / not measured" — money still in the air. It is the only fill on
 * the screen that is not a solid colour, so it needs no legend: a solid bar is a fact and a hatched
 * bar is a thing still open. Every band draws it from this definition rather than declaring its own
 * <pattern>, because two hatches at different angles or weights would read as two different
 * meanings.
 *
 * Two bands can be mounted at once and SVG ids are document-global, so the id comes from React's
 * useId and is sanitised: useId produces colons, which are legal in an id but break any CSS
 * selector written against it later.
 */
export const useHatchPatternId = (): string => `nx-hatch-${useId().replace(/[^a-zA-Z0-9]/g, '')}`;

/** The fill value for a shape that should read as "still open". */
export const hatchFill = (id: string): string => `url(#${id})`;

/** The sentence a band puts next to its hatched mark, so the mark never needs a legend. */
export const HATCH_MEANING = 'Hatched: not decided yet';

export interface HatchPatternProps {
  id: string;
  /** Override only if a band needs the hatch on an unusual ground; the default is brass mark. */
  color?: string;
}

/**
 * The <defs> block. Drop it inside the band's own <svg>, above the shapes that reference it.
 * 35% brass mark at 45 degrees: the hatch has to clear 3:1 against the clay to carry its meaning
 * (WCAG 1.4.11), and it still reads as texture next to the solid marks beside it.
 */
export function HatchPattern({ id, color }: HatchPatternProps) {
  const stroke = color ?? seriesVar('brassMark');
  return (
    <defs>
      <pattern id={id} width={6} height={6} patternUnits="userSpaceOnUse" patternTransform="rotate(45)">
        <rect width={6} height={6} fill="none" />
        <line x1={0} y1={0} x2={0} y2={6} stroke={stroke} strokeWidth={2} strokeOpacity={0.35} />
      </pattern>
    </defs>
  );
}

export default HatchPattern;
