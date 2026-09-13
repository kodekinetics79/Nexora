import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import NextStepPanel from './NextStepPanel';

describe('NextStepPanel', () => {
  it('carries an accessible name only when the screen gives it one', () => {
    const { rerender } = render(<NextStepPanel tone="info" title="Next step" sentence="Open the RFQ." />);
    // Unnamed by default, so a screen that repeats the sentence beside its button names one copy.
    expect(screen.queryByRole('status', { name: 'Next step' })).toBeNull();
    rerender(<NextStepPanel tone="info" title="Next step" sentence="Open the RFQ." ariaLabel="Next step" />);
    expect(screen.getByRole('status', { name: 'Next step' })).toHaveTextContent('Open the RFQ.');
  });
});
