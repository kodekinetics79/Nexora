import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import CreateRfqConfirmDialog, { linesIntoRfqSentence } from './CreateRfqConfirmDialog';

/**
 * The question before Create RFQ runs. It writes nothing: it says what the click will do and hands
 * the answer back to the page.
 */
describe('CreateRfqConfirmDialog', () => {
  const renderDialog = (props: Partial<React.ComponentProps<typeof CreateRfqConfirmDialog>> = {}) => {
    const onCancel = vi.fn();
    const onConfirm = vi.fn();
    render(
      <CreateRfqConfirmDialog
        open
        customer="Saudi Electricity Company"
        bidCount={2}
        lineCount={3}
        qualification="transition"
        onCancel={onCancel}
        onConfirm={onConfirm}
        {...props}
      />,
    );
    return { onCancel, onConfirm, dialog: screen.getByRole('dialog', { name: 'Create an RFQ for Saudi Electricity Company?' }) };
  };

  it('says what goes in, what is left out, the status move and the lock, then asks', () => {
    const { dialog, onConfirm, onCancel } = renderDialog();
    expect(dialog).toHaveTextContent('2 of 3 lines go into the RFQ. 1 is left out.');
    expect(dialog).toHaveTextContent('The request is marked qualified.');
    expect(dialog).toHaveTextContent('Concern: none raised.');
    expect(dialog).toHaveTextContent('Once the RFQ exists, these choices are locked on this screen.');
    fireEvent.click(within(dialog).getByRole('button', { name: 'Yes, create the RFQ' }));
    expect(onConfirm).toHaveBeenCalledOnce();
    expect(onCancel).not.toHaveBeenCalled();
  });

  it('goes back without confirming', () => {
    const { dialog, onConfirm, onCancel } = renderDialog();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Go back' }));
    expect(onCancel).toHaveBeenCalledOnce();
    expect(onConfirm).not.toHaveBeenCalled();
  });

  it('says the status truthfully when it is already qualified or could not be read', () => {
    renderDialog({ qualification: 'already' });
    expect(screen.getByText('The request is already qualified.')).toBeInTheDocument();
  });

  it('warns that an unread status may stop the RFQ', () => {
    renderDialog({ qualification: 'unknown' });
    expect(screen.getByText("Nexora couldn't read the request's status, so it isn't marked qualified here. If it isn't qualified already, the RFQ won't be created.")).toBeInTheDocument();
    expect(screen.queryByText('The request is marked qualified.')).toBeNull();
  });

  it('counts lines in plain grammar', () => {
    expect(linesIntoRfqSentence(1, 1)).toBe('1 of 1 line goes into the RFQ.');
    expect(linesIntoRfqSentence(3, 3)).toBe('3 of 3 lines go into the RFQ.');
    expect(linesIntoRfqSentence(1, 4)).toBe('1 of 4 lines goes into the RFQ. 3 are left out.');
  });
});
