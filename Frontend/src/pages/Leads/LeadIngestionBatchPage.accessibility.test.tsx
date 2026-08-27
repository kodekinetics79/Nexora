import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { BatchMetricFilterCard } from './BatchMetricFilterCard';

describe('BatchMetricFilterCard accessibility', () => {
  it('is a named toggle button and accepts keyboard-generated activation', () => {
    const onSelect = vi.fn();
    render(
      <BatchMetricFilterCard
        label="New leads"
        value={4}
        icon={<span aria-hidden="true">+</span>}
        selected={false}
        onSelect={onSelect}
      />,
    );

    const trigger = screen.getByRole('button', { name: 'Filter batch by New leads (4)' });
    expect(trigger.tagName).toBe('BUTTON');
    expect(trigger).toHaveAttribute('type', 'button');
    expect(trigger).toHaveAttribute('aria-pressed', 'false');
    trigger.focus();
    expect(trigger).toHaveFocus();

    fireEvent.click(trigger, { detail: 0 });
    expect(onSelect).toHaveBeenCalledTimes(1);
  });
});
