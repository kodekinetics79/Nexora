import { useEffect, useMemo, useRef, useState, type PointerEvent as ReactPointerEvent } from 'react';
import { alpha, Box, Button, ButtonBase, Menu, MenuItem, Paper, Stack, Typography, useTheme } from '@mui/material';
import { ArrowDropDown as DropIcon, Close as CloseIcon, OpenInNew as OpenInNewIcon } from '@mui/icons-material';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import listViewService, { type ListViewColumnsResponse } from '../../api/services/listViewService';
import type { PerformanceDTO } from '../../api/services/commercialIntelligenceService';

/**
 * The team as dots: one per rep, placed by the two measures the reader picks.
 *
 * Three things make it a tool rather than a picture, and each stays on the chart:
 *  - Either axis name is a menu. Picking a measure moves the dots there; the choice is saved to
 *    the reader's own profile (list-view store, `sales.performance.chart`) so it follows them.
 *  - Dragging a box over dots picks those reps, and the table below shows only them.
 *  - Clicking a dot opens that rep's breakdown beside the chart, without leaving the page.
 *
 * A rep with no figure for a chosen measure (win rate under the minimum sample, no customer
 * reply yet) is named under the chart rather than drawn at 0: a missing figure is not a zero.
 */
export type Rep = PerformanceDTO['representatives'][number];

type Unit = 'count' | 'percent' | 'hours';

interface Measure {
  key: string;
  label: string;
  unit: Unit;
  get: (rep: Rep) => number | null;
  /** Why a rep is off the chart on this measure. */
  missing?: string;
}

export const MEASURES: readonly Measure[] = [
  { key: 'quoteSent', label: 'Quotes sent', unit: 'count', get: r => r.quoteSent },
  { key: 'wonQuotes', label: 'Won', unit: 'count', get: r => r.wonQuotes },
  { key: 'lostQuotes', label: 'Lost', unit: 'count', get: r => r.lostQuotes },
  {
    key: 'conversionRate', label: 'Win rate', unit: 'percent',
    get: r => (r.conversionEligible && r.conversionRate != null ? r.conversionRate : null),
    missing: 'not enough decided quotes',
  },
  {
    key: 'averageResponseHours', label: 'Hours to customer reply', unit: 'hours',
    get: r => r.averageResponseHours ?? null, missing: 'no customer reply yet',
  },
  { key: 'opportunities', label: 'Opportunities', unit: 'count', get: r => r.opportunities },
  { key: 'activityCount', label: 'Activities', unit: 'count', get: r => r.activityCount },
  { key: 'overdueFollowUps', label: 'Overdue follow-ups', unit: 'count', get: r => r.overdueFollowUps },
  { key: 'openRfqs', label: 'Open RFQs', unit: 'count', get: r => r.openRfqs },
  { key: 'activeLeads', label: 'Active leads', unit: 'count', get: r => r.activeLeads },
];

const DEFAULT_X = 'quoteSent';
const DEFAULT_Y = 'wonQuotes';
const VIEW_KEY = 'sales.performance.chart' as const;

const measureOf = (key: string | undefined, fallback: string): Measure =>
  MEASURES.find(m => m.key === key) ?? MEASURES.find(m => m.key === fallback)!;

export const formatValue = (v: number, unit: Unit): string =>
  unit === 'percent' ? `${v.toFixed(0)}%` : unit === 'hours' ? `${v.toFixed(1)} h` : v.toLocaleString();

/**
 * The top of an axis: the smallest round number at or above `v` whose half is also whole, so the
 * three ticks (0, middle, top) never print a fractional count. Tight enough that the busiest rep
 * sits near the top rather than halfway up.
 */
const niceCeil = (v: number, unit: Unit): number => {
  if (unit === 'percent') return 100;
  if (!Number.isFinite(v) || v <= 0) return 4;
  const magnitude = 10 ** Math.floor(Math.log10(v));
  const step = magnitude >= 10 ? magnitude / 2 : 1;
  return Math.max(2, Math.ceil(v / (2 * step)) * 2 * step);
};

/** The chart draws at its own measured width so 11px labels stay 11px. */
function useWidth(initial: number) {
  const ref = useRef<SVGSVGElement>(null);
  const [width, setWidth] = useState(initial);
  useEffect(() => {
    const el = ref.current;
    if (!el || typeof ResizeObserver === 'undefined') return;
    const ro = new ResizeObserver(([entry]) => setWidth(Math.round(entry.contentRect.width) || initial));
    ro.observe(el);
    return () => ro.disconnect();
  }, [initial]);
  return [ref, width] as const;
}

const H = 300;
const PAD = { l: 44, r: 20, t: 16, b: 30 };
const DOT = 8;

/** The chosen pair, read from and written to the reader's profile. */
function useAxisChoice() {
  const client = useQueryClient();
  const key = ['list-views', VIEW_KEY];
  const stored = useQuery({
    queryKey: key,
    queryFn: () => listViewService.getColumns(VIEW_KEY),
    staleTime: Infinity,
    retry: 0,
    meta: { silenceGlobalError: true },
  });
  const save = useMutation({
    mutationFn: ([x, y]: [string, string]) => listViewService.saveColumns(VIEW_KEY, [
      { key: x, visible: true },
      { key: y, visible: true },
      ...MEASURES.filter(m => m.key !== x && m.key !== y).map(m => ({ key: m.key, visible: false })),
    ]),
    onSuccess: data => client.setQueryData<ListViewColumnsResponse>(key, data),
  });
  const [local, setLocal] = useState<[string, string] | null>(null);
  const visible = stored.data?.columns.filter(c => c.visible).map(c => c.key) ?? [];
  const pair: [string, string] = local ?? [visible[0] ?? DEFAULT_X, visible[1] ?? DEFAULT_Y];
  const choose = (next: [string, string]) => {
    setLocal(next);
    save.mutate(next);
  };
  return { x: measureOf(pair[0], DEFAULT_X), y: measureOf(pair[1], DEFAULT_Y), choose };
}

function AxisMenu({ measure, other, onPick, label }: { measure: Measure; other: Measure; onPick: (key: string) => void; label: string }) {
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  return (
    <>
      <ButtonBase
        aria-label={`${label}: ${measure.label}. Change measure`}
        aria-haspopup="menu"
        onClick={e => setAnchor(e.currentTarget)}
        sx={{
          px: 1, py: 0.25, borderRadius: 1, fontSize: 13, fontWeight: 700, color: 'text.primary',
          border: 1, borderColor: 'divider', bgcolor: 'background.paper',
          '&:hover': { borderColor: 'primary.main' },
          '&:focus-visible': { outline: '2px solid', outlineColor: 'primary.main' },
        }}
      >
        {measure.label}
        <DropIcon fontSize="small" sx={{ ml: 0.25, color: 'text.secondary' }} />
      </ButtonBase>
      <Menu anchorEl={anchor} open={!!anchor} onClose={() => setAnchor(null)}>
        {MEASURES.filter(m => m.key !== other.key).map(m => (
          <MenuItem key={m.key} selected={m.key === measure.key} onClick={() => { onPick(m.key); setAnchor(null); }}>
            {m.label}
          </MenuItem>
        ))}
      </Menu>
    </>
  );
}

interface Placed { rep: Rep; x: number; y: number; px: number; py: number }

export interface TeamChartProps {
  reps: Rep[];
  selected: ReadonlySet<number>;
  onSelect: (ids: Set<number>) => void;
  onOpenRecords?: (userId: number) => void;
}

export default function TeamChart({ reps, selected, onSelect, onOpenRecords }: TeamChartProps) {
  const theme = useTheme();
  const { x: mx, y: my, choose } = useAxisChoice();
  const [measureRef, width] = useWidth(640);
  const [focusId, setFocusId] = useState<number | null>(null);
  const [hoverId, setHoverId] = useState<number | null>(null);
  const [brush, setBrush] = useState<{ x0: number; y0: number; x1: number; y1: number } | null>(null);
  const dragging = useRef(false);

  const { placed, off, xMax, yMax } = useMemo(() => {
    const on: Array<{ rep: Rep; x: number; y: number }> = [];
    const offList: Rep[] = [];
    for (const rep of reps) {
      const x = mx.get(rep);
      const y = my.get(rep);
      if (x == null || y == null) offList.push(rep);
      else on.push({ rep, x, y });
    }
    const xm = niceCeil(Math.max(0, ...on.map(p => p.x)), mx.unit);
    const ym = niceCeil(Math.max(0, ...on.map(p => p.y)), my.unit);
    const plotW = Math.max(120, width - PAD.l - PAD.r);
    const plotH = H - PAD.t - PAD.b;
    // Reps on the same spot fan out in a small ring so each stays clickable.
    const bySpot = new Map<string, typeof on>();
    for (const p of on) {
      const k = `${p.x}|${p.y}`;
      bySpot.set(k, [...(bySpot.get(k) ?? []), p]);
    }
    const out: Placed[] = [];
    for (const group of bySpot.values()) {
      group.forEach((p, i) => {
        const cx = PAD.l + (p.x / xm) * plotW;
        const cy = PAD.t + plotH - (p.y / ym) * plotH;
        const ring = group.length > 1 ? DOT + 3 : 0;
        const angle = (2 * Math.PI * i) / group.length - Math.PI / 2;
        out.push({ ...p, px: cx + ring * Math.cos(angle), py: cy + ring * Math.sin(angle) });
      });
    }
    return { placed: out, off: offList, xMax: xm, yMax: ym };
  }, [reps, mx, my, width]);

  const plotW = Math.max(120, width - PAD.l - PAD.r);
  const plotH = H - PAD.t - PAD.b;
  const focus = reps.find(r => r.userId === focusId) ?? null;
  const hasSelection = selected.size > 0;

  const local = (e: ReactPointerEvent<SVGSVGElement>) => {
    const box = e.currentTarget.getBoundingClientRect();
    return { x: e.clientX - box.left, y: e.clientY - box.top };
  };

  const onPointerDown = (e: ReactPointerEvent<SVGSVGElement>) => {
    if ((e.target as Element).closest('[data-dot]')) return;
    const p = local(e);
    e.currentTarget.setPointerCapture?.(e.pointerId);
    dragging.current = true;
    setBrush({ x0: p.x, y0: p.y, x1: p.x, y1: p.y });
  };
  const onPointerMove = (e: ReactPointerEvent<SVGSVGElement>) => {
    if (!dragging.current) return;
    const p = local(e);
    setBrush(b => (b ? { ...b, x1: p.x, y1: p.y } : b));
  };
  const onPointerUp = () => {
    if (!dragging.current || !brush) return;
    dragging.current = false;
    const [l, r] = [Math.min(brush.x0, brush.x1), Math.max(brush.x0, brush.x1)];
    const [t, b] = [Math.min(brush.y0, brush.y1), Math.max(brush.y0, brush.y1)];
    setBrush(null);
    // A click on empty ground, not a drag: let go of the current pick.
    if (r - l < 4 && b - t < 4) { onSelect(new Set()); return; }
    onSelect(new Set(placed.filter(p => p.px >= l && p.px <= r && p.py >= t && p.py <= b).map(p => p.rep.userId)));
  };

  const ink = theme.palette.primary.main;
  const ticks = (max: number) => [0, max / 2, max];

  return (
    <Paper variant="outlined" sx={{ p: 2, mb: 2.5 }}>
      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
        <Box sx={{ flex: 1, minWidth: 0 }}>
          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 1, minHeight: 32, flexWrap: 'wrap', rowGap: 1 }}>
            <Typography sx={{ fontWeight: 800, fontSize: 15 }}>Team at a glance</Typography>
            <Box sx={{ flex: 1 }} />
            {hasSelection ? (
              <Button size="small" startIcon={<CloseIcon />} onClick={() => onSelect(new Set())}>
                {selected.size} picked · show all
              </Button>
            ) : (
              <Typography variant="caption" color="text.secondary">Drag across dots to pick reps</Typography>
            )}
          </Stack>

          <Stack direction="row" spacing={1} sx={{ alignItems: 'center', mb: 0.5 }}>
            <Typography variant="caption" color="text.secondary" aria-hidden>↑</Typography>
            <AxisMenu label="Up axis" measure={my} other={mx} onPick={k => choose([mx.key, k])} />
          </Stack>

          <svg
            ref={measureRef}
            width="100%"
            height={H}
            role="group"
            aria-label={`Reps by ${mx.label} and ${my.label}`}
            onPointerDown={onPointerDown}
            onPointerMove={onPointerMove}
            onPointerUp={onPointerUp}
            onPointerCancel={() => { dragging.current = false; setBrush(null); }}
            style={{ display: 'block', touchAction: 'none', cursor: 'crosshair', userSelect: 'none' }}
            className="team-chart-plot"
          >
            <style>{'@media (prefers-reduced-motion: reduce){.team-chart-plot [data-dot]{transition:none!important}}'}</style>
            {ticks(yMax).map(v => {
              const y = PAD.t + plotH - (v / yMax) * plotH;
              return (
                <g key={`y${v}`}>
                  <line x1={PAD.l} x2={PAD.l + plotW} y1={y} y2={y} stroke={theme.palette.divider} strokeDasharray={v ? '3 4' : undefined} />
                  <text x={PAD.l - 8} y={y + 4} textAnchor="end" fontSize={11} fill={theme.palette.text.secondary}>{formatValue(v, my.unit)}</text>
                </g>
              );
            })}
            {ticks(xMax).map(v => (
              <text key={`x${v}`} x={PAD.l + (v / xMax) * plotW} y={H - 10} textAnchor="middle" fontSize={11} fill={theme.palette.text.secondary}>
                {formatValue(v, mx.unit)}
              </text>
            ))}

            {placed.map(p => {
              const id = p.rep.userId;
              const picked = selected.has(id);
              const faded = hasSelection && !picked;
              const active = hoverId === id || focusId === id;
              const showName = active || picked || placed.length <= 12;
              return (
                <g
                  key={id}
                  data-dot
                  role="button"
                  tabIndex={0}
                  aria-pressed={focusId === id}
                  aria-label={`${p.rep.name}: ${mx.label} ${formatValue(p.x, mx.unit)}, ${my.label} ${formatValue(p.y, my.unit)}. Open breakdown`}
                  onClick={() => setFocusId(focusId === id ? null : id)}
                  onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); setFocusId(focusId === id ? null : id); } }}
                  onPointerEnter={() => setHoverId(id)}
                  onPointerLeave={() => setHoverId(null)}
                  style={{
                    transform: `translate(${p.px}px, ${p.py}px)`,
                    transition: 'transform 520ms cubic-bezier(.2,.8,.2,1), opacity 200ms',
                    opacity: faded ? 0.28 : 1,
                    cursor: 'pointer',
                    outline: 'none',
                  }}
                >
                  <circle r={active ? DOT + 5 : 0} fill={alpha(ink, 0.18)} style={{ transition: 'r 160ms' }} />
                  <circle
                    r={DOT}
                    fill={picked || focusId === id ? ink : theme.palette.background.paper}
                    stroke={ink}
                    strokeWidth={2.5}
                  />
                  {showName && (
                    <text x={DOT + 5} y={4} fontSize={12} fontWeight={active ? 700 : 500} fill={theme.palette.text.primary}
                      style={{ paintOrder: 'stroke', stroke: theme.palette.background.paper, strokeWidth: 3 }}>
                      {p.rep.name}
                    </text>
                  )}
                </g>
              );
            })}

            {brush && (
              <rect
                x={Math.min(brush.x0, brush.x1)} y={Math.min(brush.y0, brush.y1)}
                width={Math.abs(brush.x1 - brush.x0)} height={Math.abs(brush.y1 - brush.y0)}
                fill={alpha(ink, 0.1)} stroke={ink} strokeDasharray="4 3" rx={4}
              />
            )}
          </svg>

          <Stack direction="row" sx={{ justifyContent: 'center', mt: 0.5 }}>
            <AxisMenu label="Across axis" measure={mx} other={my} onPick={k => choose([k, my.key])} />
            <Typography variant="caption" color="text.secondary" sx={{ ml: 1, alignSelf: 'center' }} aria-hidden>→</Typography>
          </Stack>

          {off.length > 0 && (
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
              Not on the chart ({[mx, my].map(m => m.missing).filter(Boolean).join(' / ')}): {off.map(r => r.name).join(', ')}
            </Typography>
          )}
        </Box>

        <Breakdown rep={focus} onClose={() => setFocusId(null)} onOpenRecords={onOpenRecords} />
      </Stack>
    </Paper>
  );
}

/** One rep, opened from their dot: the path from opportunity to outcome, as bars that grow in. */
function Breakdown({ rep, onClose, onOpenRecords }: { rep: Rep | null; onClose: () => void; onOpenRecords?: (id: number) => void }) {
  const theme = useTheme();
  if (!rep) {
    return (
      <Box sx={{ width: { md: 260 }, flexShrink: 0, display: 'grid', placeItems: 'center', border: 1, borderStyle: 'dashed', borderColor: 'divider', borderRadius: 2, p: 2, minHeight: 120 }}>
        <Typography variant="body2" color="text.secondary" sx={{ textAlign: 'center' }}>Click a dot to see that rep</Typography>
      </Box>
    );
  }
  const steps = [
    { label: 'Opportunities', value: rep.opportunities },
    { label: 'Quotes sent', value: rep.quoteSent },
    { label: 'Customer replied', value: rep.customerResponses },
    { label: 'Won', value: rep.wonQuotes, tone: theme.palette.success.main },
    { label: 'Lost', value: rep.lostQuotes, tone: theme.palette.error.main },
  ];
  const max = Math.max(1, ...steps.map(s => s.value));
  return (
    <Box key={rep.userId} sx={{ width: { md: 260 }, flexShrink: 0, border: 1, borderColor: 'divider', borderRadius: 2, p: 2 }}>
      <Stack direction="row" sx={{ alignItems: 'center', mb: 1.5 }}>
        <Typography sx={{ fontWeight: 800, flex: 1 }} noWrap>{rep.name}</Typography>
        <ButtonBase aria-label="Close breakdown" onClick={onClose} sx={{ borderRadius: 1, p: 0.25 }}><CloseIcon fontSize="small" /></ButtonBase>
      </Stack>
      <Stack spacing={1}>
        {steps.map((s, i) => (
          <Box key={s.label}>
            <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography variant="caption" color="text.secondary">{s.label}</Typography>
              <Typography variant="caption" sx={{ fontWeight: 700, fontVariantNumeric: 'tabular-nums' }}>{s.value}</Typography>
            </Stack>
            <Box sx={{ height: 8, borderRadius: 4, bgcolor: 'action.hover', overflow: 'hidden' }}>
              <Box
                sx={{
                  height: 1, borderRadius: 4, bgcolor: s.tone ?? 'primary.main',
                  width: `${(s.value / max) * 100}%`,
                  transformOrigin: 'left',
                  animation: `teamChartGrow 480ms ${i * 60}ms cubic-bezier(.2,.8,.2,1) both`,
                  '@keyframes teamChartGrow': { from: { transform: 'scaleX(0)' }, to: { transform: 'scaleX(1)' } },
                  '@media (prefers-reduced-motion: reduce)': { animation: 'none' },
                }}
              />
            </Box>
          </Box>
        ))}
      </Stack>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1.5 }}>
        {rep.overdueFollowUps} overdue follow-up{rep.overdueFollowUps === 1 ? '' : 's'} · {rep.openRfqs} open RFQ{rep.openRfqs === 1 ? '' : 's'}
      </Typography>
      {onOpenRecords && (
        <Button size="small" endIcon={<OpenInNewIcon />} onClick={() => onOpenRecords(rep.userId)} sx={{ mt: 1 }}>Open records</Button>
      )}
    </Box>
  );
}
