import { render } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import HatchPattern, { HATCH_MEANING, hatchFill, useHatchPatternId } from './hatchPattern';

const Band = () => {
  const id = useHatchPatternId();
  return (
    <svg data-testid="band">
      <HatchPattern id={id} />
      <rect width={40} height={10} fill={hatchFill(id)} />
    </svg>
  );
};

describe('hatchPattern', () => {
  it('defines the pattern the referencing shape points at', () => {
    const { getByTestId } = render(<Band />);
    const svg = getByTestId('band');
    const pattern = svg.querySelector('pattern') as SVGPatternElement;
    const rect = svg.querySelector('rect[fill^="url("]') as SVGRectElement;

    expect(pattern).toBeTruthy();
    expect(pattern.getAttribute('patternTransform')).toBe('rotate(45)');
    expect(rect.getAttribute('fill')).toBe(`url(#${pattern.id})`);
  });

  // Two bands can be on screen at once and SVG ids are document-global; a shared literal id would
  // make the second band's hatch resolve to the first band's pattern.
  it('gives every mounted band its own id, with nothing a CSS selector would choke on', () => {
    const { container } = render(<><Band /><Band /></>);
    const ids = Array.from(container.querySelectorAll('pattern')).map((p) => p.id);

    expect(new Set(ids).size).toBe(2);
    ids.forEach((id) => expect(id).toMatch(/^nx-hatch-[a-zA-Z0-9]+$/));
  });

  it('publishes the sentence that keeps the mark off a legend', () => {
    expect(HATCH_MEANING).toBe('Hatched: not decided yet');
  });
});
