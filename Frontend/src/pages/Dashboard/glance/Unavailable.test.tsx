import { render, screen } from '@testing-library/react';
import { describe, expect, it } from 'vitest';
import Unavailable from './Unavailable';
import InsufficiencyPips from './InsufficiencyPips';

describe('Unavailable', () => {
  it('puts the server reason crisp over a defocused frame that keeps its height', () => {
    const { container } = render(
      <Unavailable reason="No base currency is set for this business unit, so order value cannot be stated.">
        <div style={{ height: 180 }}>chart frame</div>
      </Unavailable>,
    );

    expect(screen.getByText('No base currency is set for this business unit, so order value cannot be stated.')).toBeInTheDocument();
    const ghost = container.querySelector('[aria-hidden="true"]') as HTMLElement;
    expect(ghost).toHaveStyle({ opacity: '0.4', filter: 'blur(2px)' });
    // The frame is still in the tree — that is what stops the band reflowing when the figure
    // arrives — but it is out of the accessibility tree, because its numbers are not being stated.
    expect(ghost).toHaveTextContent('chart frame');
  });

  it('carries a follow-on when the reason is a sample threshold', () => {
    render(
      <Unavailable
        reason="A win rate needs 5 decided quotes."
        action={<InsufficiencyPips have={2} need={5} label="A win rate" />}
      >
        <div>chart frame</div>
      </Unavailable>,
    );

    expect(screen.getByText(/You have 2\./)).toBeInTheDocument();
    expect(screen.getByRole('img', { name: '2 of 5' })).toBeInTheDocument();
  });

  it('says Not available in words, never as a dash', () => {
    const { container } = render(
      <Unavailable reason="The aggregate has no scope for this reader."><div>frame</div></Unavailable>,
    );

    expect(screen.getByText('Not available')).toBeInTheDocument();
    expect(container.textContent).not.toContain('—');
  });
});
