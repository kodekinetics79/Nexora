import { Box, Button, List, ListItem, ListItemButton, Stack, Typography, useTheme } from '@mui/material';
import { ArrowForwardRounded as GoIcon, EventBusyOutlined as NothingScheduledIcon } from '@mui/icons-material';
import { useQuery } from '@tanstack/react-query';
import { useNavigate } from 'react-router-dom';
import dayjs, { type Dayjs } from 'dayjs';
import commercialIntelligenceService, { type CommercialAttentionItem } from '../../../api/services/commercialIntelligenceService';
import { useAuth } from '../../../context/AuthContext';
import { toPresentableError } from '../../../utils/apiErrors';
import { parseDateSafe } from '../../../utils/dates';
import BandShell from './BandShell';
import { scopeWords } from './scopeWords';
import { seriesVar } from './tokens';

/**
 * Band 5 — what needs you today.
 *
 * Rows rather than a chart, because the answer to "what needs me" is a list of records a person
 * opens, not a shape. What the chart language buys instead is the column on the right: one shared
 * micro-axis with a fixed centre rule, drawn identically on every row, so lateness is a POSITION
 * the eye compares down the column. Three dots bunched to the left of the rule says "three things
 * are badly late" before a single date has been read. The words are still on every row — the axis
 * is the comparison, never the source of the fact.
 *
 * DO NOT PRINT A TOTAL COUNT OF ANYTHING HERE, and do not render the sales-today `metrics`.
 * The endpoint counts follow-ups AFTER a `.Take(100)`, so its open-follow-ups metric saturates at
 * exactly 100 and its overdue figure is computed off the truncated list. A total from this payload
 * is a wrong number wearing a confident face; the rows are individually true, so we show rows and
 * a way through to the full list, and let the Sales today screen own the totals.
 */
const MAX_ROWS = 5;

/** How far from "now" each end of the shared axis reaches. Fixed, so rows are comparable. */
export const AXIS_DAYS = 14;

const AXIS_WIDTH = 120;
const AXIS_HEIGHT = 26;
const AXIS_PAD = 8;
const ROW_HEIGHT = 64;
/** The list holds five rows' worth of height whether it has five rows, one, or none. */
const LIST_MIN_HEIGHT = ROW_HEIGHT * MAX_ROWS;

/**
 * The exact sentence for the empty state, kept as a constant because it is load-bearing product
 * copy. An empty "on fire" band is the most dangerous state on this screen: read as "everything is
 * under control", when on a tenant this young it overwhelmingly means nobody has scheduled
 * anything yet. So it says which of the two it is.
 */
export const NOTHING_SCHEDULED_SENTENCE =
  'Nothing has your name on it and a date attached. That is not the same as nothing to do — '
  + 'it means no follow-up has been scheduled yet.';

export interface DueReading {
  /** The row's own words, e.g. "3 days late" or "due in 2 h". */
  text: string;
  overdue: boolean;
  /** Signed days from the axis origin: negative is late, positive is still to come. */
  days: number;
}

const MINUTES_PER_DAY = 60 * 24;

/**
 * Turns a due date into the row's words and its place on the axis, measured from `now`.
 *
 * `parseDateSafe` first: a DateTime.MinValue arriving as a due date would otherwise plant a dot at
 * the far left and read as two thousand years overdue.
 */
export const readDue = (dueAt: string | null | undefined, now: Dayjs): DueReading | null => {
  const parsed = parseDateSafe(dueAt);
  if (!parsed) return null;
  const minutes = dayjs(parsed).diff(now, 'minute');
  const days = minutes / MINUTES_PER_DAY;
  if (Math.abs(minutes) < 1) return { text: 'due now', overdue: false, days };
  const overdue = minutes < 0;
  const magnitude = Math.abs(minutes);
  let span: string;
  if (magnitude < 60) span = `${magnitude} min`;
  else if (magnitude < MINUTES_PER_DAY) span = `${Math.round(magnitude / 60)} h`;
  else {
    const wholeDays = Math.round(magnitude / MINUTES_PER_DAY);
    span = `${wholeDays} day${wholeDays === 1 ? '' : 's'}`;
  }
  return { text: overdue ? `${span} late` : `due in ${span}`, overdue, days };
};

export interface AxisPlacement {
  x: number;
  /** True when the date is further out than the axis reaches, so the mark must not read as exact. */
  clamped: boolean;
}

export const axisPlacement = (days: number): AxisPlacement => {
  const reach = AXIS_WIDTH / 2 - AXIS_PAD;
  const ratio = days / AXIS_DAYS;
  const bounded = Math.max(-1, Math.min(1, ratio));
  return { x: AXIS_WIDTH / 2 + bounded * reach, clamped: Math.abs(ratio) > 1 };
};

/**
 * One row's place on the shared axis. Every row draws the same rule at the same width so the
 * column reads as a single instrument — including the rows with no date, which draw the rule and
 * simply put nothing on it.
 *
 * Depth here is light, not geometry: the mark is a flat circle with a soft halo behind it, so it
 * sits above the rule without any perspective that could shift where the value appears to be.
 */
function MicroAxis({ due }: { due: DueReading | null }) {
  const theme = useTheme();
  const mid = AXIS_HEIGHT / 2;
  const placement = due ? axisPlacement(due.days) : null;
  const colour = due?.overdue ? seriesVar('oxide') : seriesVar('brassMark');

  return (
    <Box sx={{ width: AXIS_WIDTH, flexShrink: 0 }}>
      <svg
        width={AXIS_WIDTH}
        height={AXIS_HEIGHT}
        viewBox={`0 0 ${AXIS_WIDTH} ${AXIS_HEIGHT}`}
        aria-hidden
        focusable="false"
        style={{ display: 'block' }}
      >
        <line
          x1={AXIS_PAD} y1={mid} x2={AXIS_WIDTH - AXIS_PAD} y2={mid}
          stroke={theme.palette.divider} strokeWidth={1}
        />
        <line
          x1={AXIS_WIDTH / 2} y1={4} x2={AXIS_WIDTH / 2} y2={AXIS_HEIGHT - 4}
          stroke={theme.palette.text.disabled} strokeWidth={1.5}
        />
        {placement && (
          placement.clamped ? (
            // Beyond the axis. A dot at the end would claim a position it does not have, so the
            // mark becomes a chevron pointing off the scale: "further than this line goes".
            <path
              data-testid="today-axis-mark"
              data-overdue={due?.overdue ? 'true' : 'false'}
              data-clamped="true"
              d={due?.overdue
                ? `M${AXIS_PAD - 1} ${mid} L${AXIS_PAD + 7} ${mid - 5} L${AXIS_PAD + 7} ${mid + 5} Z`
                : `M${AXIS_WIDTH - AXIS_PAD + 1} ${mid} L${AXIS_WIDTH - AXIS_PAD - 7} ${mid - 5} L${AXIS_WIDTH - AXIS_PAD - 7} ${mid + 5} Z`}
              fill={colour}
            />
          ) : (
            <g data-testid="today-axis-mark" data-overdue={due?.overdue ? 'true' : 'false'} data-clamped="false">
              <circle cx={placement.x} cy={mid} r={8} fill={colour} opacity={0.16} />
              <circle cx={placement.x} cy={mid} r={4.5} fill={colour} />
            </g>
          )
        )}
      </svg>
    </Box>
  );
}

/** The axis is shared, so it is labelled once, at the head of the column, not per row. */
function AxisHeader() {
  return (
    <Box
      sx={{
        width: AXIS_WIDTH,
        flexShrink: 0,
        display: 'grid',
        gridTemplateColumns: '1fr auto 1fr',
        alignItems: 'baseline',
        color: 'text.secondary',
        fontSize: 10.5,
        fontWeight: 700,
        letterSpacing: '0.04em',
        textTransform: 'uppercase',
      }}
    >
      <Box component="span" sx={{ textAlign: 'left' }}>Late</Box>
      <Box component="span" sx={{ textAlign: 'center' }}>Now</Box>
      <Box component="span" sx={{ textAlign: 'right' }}>To come</Box>
    </Box>
  );
}

function RowBody({ item, due }: { item: CommercialAttentionItem; due: DueReading | null }) {
  return (
    <>
      <Stack spacing={0.25} sx={{ flexGrow: 1, minWidth: 0 }}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', minWidth: 0 }}>
          <Typography
            component="span"
            sx={{
              fontFamily: '"Cambay", "Source Sans 3", sans-serif', fontWeight: 700, fontSize: 15,
              fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap',
            }}
          >
            {item.nexoraSerial || item.reference}
          </Typography>
          <Typography component="span" variant="body2" sx={{ color: 'text.primary', minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
            {item.reason}
          </Typography>
        </Stack>
        <Typography variant="caption" sx={{ color: 'text.secondary', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {item.customerName || 'Customer not recorded'} · {item.ownerName ? `Owner ${item.ownerName}` : 'No owner assigned'}
        </Typography>
      </Stack>
      <Typography
        component="span"
        sx={{
          width: 96, flexShrink: 0, textAlign: 'right', fontSize: 13, fontWeight: 700,
          fontVariantNumeric: 'tabular-nums', color: 'text.primary',
        }}
      >
        {due ? due.text : 'No date set'}
      </Typography>
      <MicroAxis due={due} />
    </>
  );
}

export interface TodayBandProps {
  /** Position in the screen's entrance stagger. */
  index?: number;
}

export default function TodayBand({ index = 0 }: TodayBandProps) {
  const navigate = useNavigate();
  const { hasPermission } = useAuth();
  // Its own query, its own failure. A band that cannot load must not blank its neighbours, so
  // there is no composite endpoint and nothing here is shared with another band's fetch.
  const query = useQuery({
    queryKey: ['commercial-intelligence', 'sales-today'],
    queryFn: commercialIntelligenceService.getSalesToday,
    refetchInterval: 60_000,
    retry: 1,
  });

  const data = query.data;
  const rows = (data?.attentionItems ?? []).slice(0, MAX_ROWS);
  // The axis centre is the server's clock, not the browser's: the seal already states that time,
  // and measuring the dots from anything else would drift the marks away from the freshness the
  // reader was just told. Only when the server states no clock do we fall back to this device.
  const generatedAt = parseDateSafe(data?.generatedAt);
  const now = generatedAt ? dayjs(generatedAt) : dayjs();

  const presented = query.isError ? toPresentableError(query.error, { context: 'list' }) : null;
  const forbidden = presented?.status === 403 ? presented.message : null;

  return (
    <BandShell
      step="5"
      title="What needs you today"
      index={index}
      minHeight={468}
      loading={query.isLoading}
      error={presented && !forbidden ? presented.message : null}
      forbidden={forbidden}
      onRetry={() => void query.refetch()}
      seal={{
        scope: scopeWords(data?.scope),
        window: 'Open right now',
        generatedAt: data?.generatedAt ?? null,
        // Outlined, not filled: sales-today takes no from/to at all, so the period chips above do
        // not reach this band. A filled seal would render "this band follows the period you choose
        // above", which is the one kind of small untruth the seal exists to make impossible.
        governed: false,
      }}
    >
      <Stack spacing={1} sx={{ flexGrow: 1, minWidth: 0 }}>
        <Stack direction="row" spacing={1.5} sx={{ alignItems: 'baseline', px: 1.5 }}>
          <Box sx={{ flexGrow: 1 }} />
          <Box sx={{ width: 96, flexShrink: 0 }} />
          <AxisHeader />
        </Stack>

        {rows.length === 0 ? (
          <Box
            sx={{
              minHeight: LIST_MIN_HEIGHT,
              borderRadius: 2,
              border: '1px dashed',
              borderColor: 'divider',
              display: 'grid',
              placeItems: 'center',
              p: 3,
            }}
          >
            <Stack spacing={1.5} sx={{ maxWidth: 460, alignItems: 'flex-start' }}>
              <NothingScheduledIcon sx={{ color: 'text.secondary' }} />
              <Typography sx={{ lineHeight: 1.5 }}>{NOTHING_SCHEDULED_SENTENCE}</Typography>
              <Button
                variant="outlined"
                size="small"
                endIcon={<GoIcon />}
                onClick={() => navigate('/sales/routing')}
              >
                See unassigned enquiries
              </Button>
            </Stack>
          </Box>
        ) : (
          <List disablePadding sx={{ minHeight: LIST_MIN_HEIGHT }}>
            {rows.map((item) => {
              const due = readDue(item.dueAt, now);
              const route = item.actionRoute && item.actionRoute.startsWith('/') ? item.actionRoute : null;
              const permitted = !item.requiredModule || hasPermission(item.requiredModule);
              const label = [
                item.nexoraSerial || item.reference,
                item.reason,
                item.customerName || 'Customer not recorded',
                item.ownerName ? `Owner ${item.ownerName}` : 'No owner assigned',
                due ? due.text : 'No date set',
              ].join('. ');
              const surface = {
                minHeight: ROW_HEIGHT,
                borderRadius: 2,
                px: 1.5,
                gap: 1.5,
                alignItems: 'center',
              } as const;

              if (!route || !permitted) {
                return (
                  <ListItem key={`${item.recordType}-${item.id}`} disableGutters sx={surface}>
                    <RowBody item={item} due={due} />
                    <Typography variant="caption" sx={{ color: 'text.secondary', flexShrink: 0 }}>
                      {permitted ? 'No link' : 'Permission required'}
                    </Typography>
                  </ListItem>
                );
              }
              return (
                <ListItemButton
                  key={`${item.recordType}-${item.id}`}
                  onClick={() => navigate(route)}
                  aria-label={`${label}. Open`}
                  sx={{
                    ...surface,
                    transition: 'transform 160ms cubic-bezier(0.2, 0.7, 0.2, 1), background-color 160ms ease-out',
                    '&:hover': { transform: 'translateY(-1px)' },
                    '&:active': { transform: 'translateY(1px)' },
                    '@media (prefers-reduced-motion: reduce)': { transition: 'none', '&:hover, &:active': { transform: 'none' } },
                  }}
                >
                  <RowBody item={item} due={due} />
                </ListItemButton>
              );
            })}
          </List>
        )}

        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1}
          sx={{ alignItems: { sm: 'center' }, justifyContent: 'space-between', px: 1.5, pt: 0.5 }}
        >
          <Typography variant="caption" sx={{ color: 'text.secondary', lineHeight: 1.4 }}>
            Left of the line is late, right of it is still to come. Each end of the line is {AXIS_DAYS} days from now.
          </Typography>
          {/* Deliberately a way through, never a count: see the note at the top of this file. */}
          <Button size="small" endIcon={<GoIcon />} onClick={() => navigate('/sales/today')} sx={{ fontWeight: 700, flexShrink: 0 }}>
            See all in Sales today
          </Button>
        </Stack>
      </Stack>
    </BandShell>
  );
}
