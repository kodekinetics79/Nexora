import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));

import SearchField from './SearchField';

describe('SearchField', () => {
  it('does not advertise the global command-menu shortcut for a local filter', () => {
    render(<SearchField value="" onChange={vi.fn()} placeholder="Search customers" />);

    expect(screen.getByRole('textbox', { name: 'Search customers' })).toBeInTheDocument();
    expect(screen.queryByText('⌘ K')).not.toBeInTheDocument();
  });

  it('names the clear action and clears through both callbacks', () => {
    const onChange = vi.fn();
    const onClear = vi.fn();
    render(
      <SearchField
        value="Acme"
        onChange={onChange}
        onClear={onClear}
        placeholder="Search customers"
      />,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Clear search' }));

    expect(onChange).toHaveBeenCalledWith('');
    expect(onClear).toHaveBeenCalledOnce();
  });
});
