import React from 'react';
import { useParams } from 'react-router-dom';
import { QueryClient, QueryClientContext, useQuery } from '@tanstack/react-query';
import { Alert } from '@mui/material';
import leadReadCompletenessService, {
  type LeadReadCompletenessDTO,
} from '../../../api/services/leadReadCompletenessService';

/**
 * "Read 3 of ~42 lines — check the document."
 *
 * A 42-line Aramco print once came back as 3 clean-looking lines and nothing on this screen said
 * so: the rep would have quoted 3 of 42. When Nexora could not read the whole document, the page
 * says it in one line. It informs; it does not block — Create RFQ still works, after one click
 * that has read this line.
 */
export const partialReadSentence = (fact: Pick<LeadReadCompletenessDTO, 'linesRead' | 'linesExpected'>): string => {
  const read = fact.linesRead ?? null;
  const expected = fact.linesExpected ?? null;
  return read !== null && expected !== null && expected > read
    ? `Read ${read} of ~${expected} ${expected === 1 ? 'line' : 'lines'} — check the document`
    : 'Part of this document was not read — check the document';
};

export const readCompletenessKey = (leadId: number) => ['lead-read-completeness', leadId] as const;

// Used only when no QueryClientProvider is mounted (a dialog rendered on its own): the query is
// then disabled and nothing is fetched.
const detachedClient = new QueryClient();

/** The partial-read fact for a request, or null when the whole document was read (or it is not known yet). */
export const usePartialRead = (leadId: number | null | undefined): LeadReadCompletenessDTO | null => {
  const provided = React.useContext(QueryClientContext);
  const valid = typeof leadId === 'number' && Number.isFinite(leadId) && leadId > 0;
  const query = useQuery(
    {
      queryKey: readCompletenessKey(valid ? leadId : 0),
      queryFn: () => leadReadCompletenessService.get(leadId as number),
      enabled: valid && provided !== undefined,
      staleTime: 5 * 60_000,
      retry: false,
    },
    provided ?? detachedClient,
  );
  return query.data?.partial ? query.data : null;
};

/** The same fact for the request on the current Decide route (/:id). */
export const usePartialReadForRoute = (): LeadReadCompletenessDTO | null => {
  const { id } = useParams<{ id: string }>();
  return usePartialRead(id ? Number(id) : null);
};

/** One warning line above the lines, only when part of the document was not read. */
const PartialReadNotice: React.FC<{ leadId: number }> = ({ leadId }) => {
  const partial = usePartialRead(leadId);
  if (!partial) return null;
  return (
    <Alert severity="warning" sx={{ mb: 1.5 }} data-testid="partial-read-notice">
      {partialReadSentence(partial)}
    </Alert>
  );
};

export default PartialReadNotice;
