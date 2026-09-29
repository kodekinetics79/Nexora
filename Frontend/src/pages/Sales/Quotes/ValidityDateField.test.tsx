import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import ValidityDateField from './ValidityDateField';

/** Owner 2026-09-28: validity is 30 / 60 / 90 days in one click, or any date by hand. */
describe('ValidityDateField', () => {
  it('counts 30, 60 and 90 days from the date it is given', () => {
    const onChange = vi.fn();
    render(<ValidityDateField value="" onChange={onChange} from="2026-09-28" />);

    fireEvent.click(screen.getByText('30 days'));
    fireEvent.click(screen.getByText('60 days'));
    fireEvent.click(screen.getByText('90 days'));

    expect(onChange.mock.calls.map(([value]) => value)).toEqual(['2026-10-28', '2026-11-27', '2026-12-27']);
  });

  it('still takes a date typed by hand', () => {
    const onChange = vi.fn();
    render(<ValidityDateField value="" onChange={onChange} from="2026-09-28" label="Valid until" />);

    fireEvent.change(screen.getByLabelText('Valid until'), { target: { value: '2027-01-15' } });

    expect(onChange).toHaveBeenCalledWith('2027-01-15');
  });

  it('shows which period is picked, and offers the buyer date first when there is one', () => {
    render(
      <ValidityDateField
        value="2026-11-27"
        onChange={vi.fn()}
        from="2026-09-28"
        extraChips={[{ label: 'Buyer: 1 Dec 2026', value: '2026-12-01' }]}
      />,
    );

    const chips = screen.getAllByRole('button').map((chip) => chip.textContent);
    expect(chips).toEqual(['Buyer: 1 Dec 2026', '30 days', '60 days', '90 days']);
    expect(screen.getByText('60 days').closest('.MuiChip-root')).toHaveClass('MuiChip-filled');
  });

  it('turns off a period that would land before the earliest allowed date', () => {
    render(<ValidityDateField value="" onChange={vi.fn()} from="2026-09-28" min="2026-11-01" presetLabel={(d) => `+${d} days`} />);

    expect(screen.getByText('+30 days').closest('.MuiChip-root')).toHaveClass('Mui-disabled');
    expect(screen.getByText('+60 days').closest('.MuiChip-root')).not.toHaveClass('Mui-disabled');
  });
});
