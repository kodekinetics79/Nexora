import React from 'react';
import { useQuery } from '@tanstack/react-query';
import { Box, Card, CardContent, Chip, Divider, Skeleton, Stack, Typography } from '@mui/material';
import { History as HistoryIcon } from '@mui/icons-material';
import intelligenceService from '../../../api/services/intelligenceService';
import { statusLabel } from '../../../utils/statusLabels';

interface CustomerContextPanelProps {
  /** The customer currently selected on the quote form; null hides the panel. */
  customerId: number | null;
}

const money = (n: number) =>
  `$${n.toLocaleString(undefined, { maximumFractionDigits: n >= 1000 ? 0 : 2 })}`;

const agoLabel = (monthsAgo: number | null): string => {
  if (monthsAgo === null) return '';
  if (monthsAgo <= 0) return 'this month';
  if (monthsAgo === 1) return '1 month ago';
  if (monthsAgo < 24) return `${monthsAgo} months ago`;
  return `${Math.round(monthsAgo / 12)} years ago`;
};

/**
 * WP-B2: compact "This customer" side panel for the quote form. Answers, at a
 * glance: do we win with this customer, what do we usually charge them, and
 * what did we last sell them at ("Last sold at $X, 4 months ago"). Renders
 * nothing until a customer is picked; fails silent if the endpoint errors so
 * quoting is never blocked by analytics.
 */
const CustomerContextPanel: React.FC<CustomerContextPanelProps> = ({ customerId }) => {
  const context = useQuery({
    queryKey: ['customer-context', customerId],
    queryFn: () => intelligenceService.getCustomerContext(customerId!),
    enabled: customerId !== null && customerId > 0,
    staleTime: 60_000,
    retry: 1,
  });

  if (!customerId) return null;
  if (context.isError) return null;

  const data = context.data;
  // A customer we have already turned down (or been turned down by) before quoting is not new,
  // even when no quote was ever raised.
  const isNewCustomer = data && data.totalQuotes === 0 && data.ordersLast24Months === 0
    && !data.leadStageLosses;
  // Prefer the rate whose denominator includes inquiries dropped before quoting.
  const winRate = data ? data.inquiryWinRatePct ?? data.winRatePct : null;
  const leadLossReasons = (data?.recentLeadLosses ?? [])
    .map((loss) => loss.outcomeReasonName ?? 'Reason not recorded');

  return (
    <Card sx={{ borderRadius: 2, border: '1px solid', borderColor: 'divider', boxShadow: 'none', mt: 2 }}>
      <CardContent sx={{ p: 2, '&:last-child': { pb: 2 } }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1.5 }}>
          <HistoryIcon fontSize="small" color="primary" />
          <Typography variant="subtitle2" sx={{ fontWeight: 800 }}>
            This customer
          </Typography>
        </Stack>

        {context.isLoading ? (
          <Stack spacing={1}>
            <Skeleton variant="rounded" height={22} />
            <Skeleton variant="rounded" height={22} />
            <Skeleton variant="rounded" height={22} />
          </Stack>
        ) : isNewCustomer ? (
          <Typography variant="body2" sx={{ color: 'text.secondary' }}>
            First quote for this customer — no history with us yet.
          </Typography>
        ) : data ? (
          <Stack spacing={1.25}>
            {/* Win rate + volume */}
            <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'baseline' }}>
              <Typography variant="body2" color="text.secondary">
                Win rate
              </Typography>
              {winRate !== null && winRate !== undefined ? (
                <Chip
                  size="small"
                  label={`${winRate.toFixed(0)}%`}
                  color={winRate >= 50 ? 'success' : winRate >= 25 ? 'warning' : 'error'}
                  sx={{ fontWeight: 800 }}
                />
              ) : (
                <Typography variant="body2" sx={{ fontWeight: 600, color: 'text.secondary' }}>
                  No decisions yet
                </Typography>
              )}
            </Box>
            {data.leadStageLosses > 0 && (
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">
                  Dropped before quoting
                </Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }} title={leadLossReasons.join(', ')}>
                  {data.leadStageLosses}
                  {leadLossReasons[0] ? ` · ${leadLossReasons[0]}` : ''}
                </Typography>
              </Box>
            )}
            <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
              <Typography variant="body2" color="text.secondary">
                Quotes sent
              </Typography>
              <Typography variant="body2" sx={{ fontWeight: 700 }}>
                {data.totalQuotes}
                {data.wonQuotes > 0 ? ` (${data.wonQuotes} won)` : ''}
              </Typography>
            </Box>
            {data.ordersLast24Months > 0 && (
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">
                  Orders, last 2 years
                </Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {data.ordersLast24Months} · {data.orderValueLast24Months == null
                    ? statusLabel(data.orderValueStatus)
                    : money(data.orderValueLast24Months)}
                </Typography>
              </Box>
            )}
            {data.avgMarginPct !== null && (
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">
                  Usual room above cost
                </Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  ~{data.avgMarginPct.toFixed(0)}%
                </Typography>
              </Box>
            )}
            {data.avgQuoteTotal !== null && data.avgQuoteTotal > 0 && (
              <Box sx={{ display: 'flex', justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary">
                  Typical quote size
                </Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {money(data.avgQuoteTotal)}
                </Typography>
              </Box>
            )}

            {/* Last-sold prices */}
            {data.recentItemPrices.length > 0 && (
              <>
                <Divider />
                <Box>
                  <Typography
                    variant="caption"
                    sx={{ fontWeight: 700, color: 'text.secondary', display: 'block', mb: 0.5 }}
                  >
                    Last prices for this customer
                  </Typography>
                  <Stack spacing={0.5}>
                    {data.recentItemPrices.slice(0, 4).map((item, i) => (
                      <Box key={`${item.productId ?? 'd'}-${i}`} sx={{ minWidth: 0 }}>
                        <Typography
                          variant="caption"
                          sx={{
                            display: 'block',
                            color: 'text.primary',
                            overflow: 'hidden',
                            textOverflow: 'ellipsis',
                            whiteSpace: 'nowrap',
                          }}
                          title={item.description ?? undefined}
                        >
                          {item.description ?? 'Item'}
                        </Typography>
                        <Typography variant="caption" sx={{ color: 'text.secondary' }}>
                          Last sold at {money(item.unitPrice)}
                          {item.monthsAgo !== null ? `, ${agoLabel(item.monthsAgo)}` : ''}
                        </Typography>
                      </Box>
                    ))}
                  </Stack>
                </Box>
              </>
            )}
          </Stack>
        ) : null}
      </CardContent>
    </Card>
  );
};

export default CustomerContextPanel;
