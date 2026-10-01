import { useEffect, useState } from 'react';
import type { CustomerAwardCompletion, CustomerAwardQuote } from '../Quotes/customer-awards';
import { CustomerAwardDialog } from '../Quotes/customer-awards';
import ChooseQuoteForClientPoDialog from './ChooseQuoteForClientPoDialog';

export interface ClientPoUploadFlowProps {
  open: boolean;
  onClose: () => void;
  onCompleted?: (result: CustomerAwardCompletion) => void;
}

/**
 * The single upload door shared by Client Orders and the Client PO review queue.
 *
 * A Client PO must first name the submitted quotation it answers. Everything after that choice is
 * the governed customer-award workspace: the buyer document is retained, its lines are compared
 * with the quotation, differences need a person, and only the confirmed award becomes an order.
 * Keeping those two existing screens behind one component prevents the Orders screen from growing
 * a second, weaker implementation of the same commercial gate.
 */
export default function ClientPoUploadFlow({ open, onClose, onCompleted }: ClientPoUploadFlowProps) {
  const [quote, setQuote] = useState<CustomerAwardQuote | null>(null);

  useEffect(() => {
    if (!open) setQuote(null);
  }, [open]);

  const close = () => {
    setQuote(null);
    onClose();
  };

  return <>
    <ChooseQuoteForClientPoDialog
      open={open && quote === null}
      onClose={close}
      onChosen={setQuote}
    />
    <CustomerAwardDialog
      open={open && quote !== null}
      quote={quote}
      onClose={close}
      onCompleted={onCompleted}
    />
  </>;
}
