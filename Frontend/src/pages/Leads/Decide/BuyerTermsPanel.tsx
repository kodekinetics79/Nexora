import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { Box, Button, Stack, Tooltip, Typography } from '@mui/material';
import leadDecisionService, { type LeadDecisionEvidenceDTO } from '../../../api/services/leadDecisionService';

/** The retained documents that can state terms: Word files kept as evidence. */
const termSources = (evidence: LeadDecisionEvidenceDTO[]): LeadDecisionEvidenceDTO[] =>
  evidence.filter((item) => item.sourceAvailable && item.sourceDocumentId != null
    && (/\.docx$/i.test(item.name) || /wordprocessingml/i.test(item.mediaType ?? '')));

/**
 * What the buyer requires, as the document states it: when bidding closes, delivery terms, where
 * to deliver, the agreement length, the currencies a quote may use, how long the price must hold,
 * VAT, and what must be accepted or attached. A quote that ignores these is rejected, so they sit
 * above the lines the rep decides on — folded to one line of the terms that decide the bid, so the
 * lines keep the screen; one click opens them all. Each value shows the buyer's own sentence on
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

  return (
    <Box component="section" aria-labelledby="decide-buyer-terms" sx={{ px: { xs: 2, sm: 3 }, pt: 1.5, pb: open ? 1.5 : 1 }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', minWidth: 0 }}>
        <Typography id="decide-buyer-terms" component="h2" variant="subtitle2" sx={{ fontWeight: 700, whiteSpace: 'nowrap' }}>
          Buyer requires
        </Typography>
        {open ? (
          <Typography variant="caption" color="text.secondary" noWrap sx={{ flex: 1, minWidth: 0 }}>From {source.name}</Typography>
        ) : (
          <Typography variant="body2" noWrap sx={{ flex: 1, minWidth: 0 }} data-testid="buyer-terms-headline">
            {headline.map((term, index) => (
              <React.Fragment key={term.key}>
                {index > 0 ? <Box component="span" sx={{ color: 'text.disabled', mx: 1 }}>·</Box> : null}
                <Box component="span" sx={{ color: 'text.secondary' }}>{term.label} </Box>
                <Tooltip title={`“${term.quote}”`} placement="bottom-start" enterDelay={300}>
                  {/* Focusable so a keyboard reader can reach the tooltip holding the buyer's own words. */}
                  {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex */}
                  <Box component="span" tabIndex={0} sx={{ fontWeight: 600, cursor: 'help' }}>{term.value}</Box>
                </Tooltip>
              </React.Fragment>
            ))}
          </Typography>
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
                  {/* eslint-disable-next-line jsx-a11y/no-noninteractive-tabindex -- keyboard reach to the quote tooltip */}
                  <Box component="span" tabIndex={0} data-term={term.key} sx={{ cursor: 'help', borderBottom: 1, borderColor: 'divider', borderBottomStyle: 'dotted' }}>
                    {term.value}
                  </Box>
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
