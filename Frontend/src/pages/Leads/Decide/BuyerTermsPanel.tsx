import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { Box, Button, Stack, Tooltip, Typography } from '@mui/material';
import leadDecisionService, { type LeadDecisionEvidenceDTO } from '../../../api/services/leadDecisionService';

/**
 * The retained documents that can state terms: Word files, and portal prints saved as .doc or
 * .html (every SEC print is an Ariba HTML page named .doc). Mirrors BuyerTerms.CanRead.
 */
const termSources = (evidence: LeadDecisionEvidenceDTO[]): LeadDecisionEvidenceDTO[] =>
  evidence.filter((item) => item.sourceAvailable && item.sourceDocumentId != null
    && (/\.(docx?|html?)$/i.test(item.name) || /wordprocessingml|text\/html/i.test(item.mediaType ?? '')));

/**
 * What the buyer requires, as the document states it: when bidding closes, delivery terms, where
 * to deliver, the agreement length, the currencies a quote may use, how long the price must hold,
 * VAT, and what must be accepted or attached. A quote that ignores these is rejected, so they sit
 * above the lines the rep decides on — folded to one row of the terms that decide the bid, laid
 * out like the header strip (label over value), so the lines keep the screen; one click opens them all. Each value shows the buyer's own sentence on
 * hover. Nothing renders when the document states no terms.
 */
/** The terms that decide the bid, in the one folded line. */
const HEADLINE = ['closes', 'delivery_terms', 'quote_currency', 'validity', 'agreement'];

const BuyerTermsPanel: React.FC<{ evidence: LeadDecisionEvidenceDTO[] }> = ({ evidence }) => {
  const source = termSources(evidence)[0] ?? null;
  const [open, setOpen] = React.useState(false);
  const terms = useQuery({
    queryKey: ['buyer-terms', source?.sourceDocumentId],
    queryFn: () => leadDecisionService.getBuyerTerms(source!.sourceDocumentId!),
    enabled: source != null,
    staleTime: Infinity,
    retry: false,
  });
  const list = terms.data ?? [];
  if (!source || list.length === 0) return null;

  const headline = list.filter((term) => HEADLINE.includes(term.key));

  // Folded, the terms read like the header strip above them: a small label over each value, one
  // column per term, so the eye lands on "8 Oct 2026" rather than hunting through one run-on line.
  const labelSx = { display: 'block', fontSize: 11, fontWeight: 700, letterSpacing: '0.08em', textTransform: 'uppercase', color: 'text.secondary', whiteSpace: 'nowrap' } as const;

  return (
    <Box component="section" aria-labelledby="decide-buyer-terms" sx={{ px: { xs: 2, sm: 3 }, pt: 1.25, pb: open ? 1.5 : 1.25 }}>
      <Stack direction="row" sx={{ alignItems: 'center', minWidth: 0, gap: 2 }}>
        <Typography id="decide-buyer-terms" component="h2" variant="subtitle2" sx={{ fontWeight: 800, whiteSpace: 'nowrap', flexShrink: 0 }}>
          Buyer requires
        </Typography>
        {open ? (
          <Typography variant="caption" color="text.secondary" noWrap sx={{ flex: 1, minWidth: 0 }}>From {source.name}</Typography>
        ) : (
          <Stack direction="row" data-testid="buyer-terms-headline" sx={{ flex: 1, minWidth: 0, overflow: 'hidden' }}>
            {headline.map((term) => (
              <Box key={term.key} sx={{ minWidth: 0, flex: '0 1 auto', maxWidth: 360, px: 2, borderLeft: 1, borderColor: 'divider' }}>
                <Box component="span" sx={labelSx}>{term.label}</Box>
                <Tooltip title={`“${term.quote}”`} placement="bottom-start" enterDelay={300}>
                  <Typography tabIndex={0} noWrap sx={{ fontSize: '0.9rem', fontWeight: 600, cursor: 'help', lineHeight: 1.4 }}>
                    {term.value}
                  </Typography>
                </Tooltip>
              </Box>
            ))}
          </Stack>
        )}
        <Button size="small" onClick={() => setOpen((value) => !value)} aria-expanded={open} sx={{ fontWeight: 700, whiteSpace: 'nowrap', flexShrink: 0 }}>
          {open ? 'Fewer' : `All ${list.length}`}
        </Button>
      </Stack>
      {open ? (
        <Box
          component="dl"
          sx={{
            m: 0,
            mt: 1,
            display: 'grid',
            gridTemplateColumns: { xs: 'minmax(96px, max-content) 1fr', md: 'minmax(110px, max-content) 1fr minmax(110px, max-content) 1fr' },
            columnGap: 2,
            rowGap: 0.75,
            '& dt': { color: 'text.secondary', fontSize: '0.8125rem' },
            '& dd': { m: 0, fontSize: '0.875rem', fontWeight: 600, minWidth: 0, overflowWrap: 'anywhere' },
          }}
        >
          {list.map((term) => (
            <React.Fragment key={term.key}>
              <Box component="dt">{term.label}</Box>
              <Box component="dd">
                <Tooltip title={`“${term.quote}”`} placement="bottom-start" enterDelay={300}>
                  <Typography component="span" tabIndex={0} data-term={term.key} sx={{ font: 'inherit', cursor: 'help', borderBottom: 1, borderColor: 'divider', borderBottomStyle: 'dotted' }}>
                    {term.value}
                  </Typography>
                </Tooltip>
              </Box>
            </React.Fragment>
          ))}
        </Box>
      ) : null}
    </Box>
  );
};

export default BuyerTermsPanel;
