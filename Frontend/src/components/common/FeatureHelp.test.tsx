import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import FeatureHelp from './FeatureHelp';

describe('FeatureHelp', () => {
  it('exposes client education to pointer and keyboard users', async () => {
    render(
      <FeatureHelp
        label="retention window"
        title="Retention window"
        description="The minimum waiting period before permanent deletion can be approved."
      />,
    );

    const trigger = screen.getByRole('button', { name: 'Learn more about retention window' });
    fireEvent.mouseOver(trigger);

    expect(await screen.findByRole('tooltip')).toHaveTextContent('Retention window');
    expect(screen.getByRole('tooltip')).toHaveTextContent('minimum waiting period');

    fireEvent.mouseLeave(trigger);
    trigger.focus();
    expect(await screen.findByRole('tooltip')).toBeVisible();
  });
});
