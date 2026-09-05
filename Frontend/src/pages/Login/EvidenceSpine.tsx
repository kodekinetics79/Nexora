import React, { useEffect, useRef } from 'react';
import { Box, Typography } from '@mui/material';
import { keyframes } from '@emotion/react';
import styled from '@emotion/styled';
import { CheckRounded as CheckIcon } from '@mui/icons-material';
import BrandMark from '../../components/common/BrandMark';

/**
 * The sign-in page's left half: the commercial spine, shown working.
 *
 * A quotation is not a form; it is a record that moves. This panel replays one record's journey
 * — an enquiry arriving by email, becoming a lead, an RFQ, a comparison, a sent quote, a
 * delivered order, a paid invoice — as a ledger that writes itself while the visitor signs in.
 * Every line carries a time and an owner, which is the product's actual promise: nothing changes
 * hands without keeping who did it and when.
 *
 * The three stages are the semantic content (an ordered list, always visible). The ledger lines,
 * the rail that fills between stages and the brass signal that rides it are decorative and
 * aria-hidden. One 16-second cycle drives all of them from the same clock, so they cannot drift
 * apart; each element's keyframes are generated from the moment it should appear. The resting
 * CSS is the *completed* state, so under the OS "reduce motion" setting (which the theme honours
 * with a single selector on `data-decorative-motion`) the panel shows the whole story at once.
 */

export const evidenceStages = [
  { title: 'Capture & reconcile', detail: 'Customer email → Canonical Lead' },
  { title: 'Approve & source', detail: 'Participation → Formal RFQ' },
  { title: 'Fulfil & collect', detail: 'Order → Delivery evidence → Payment' },
] as const;

interface LedgerLine {
  when: string;
  event: string;
  owner: string;
  /** The last line of the story: the money arrived. Drawn in brass. */
  final?: boolean;
}

// One record, start to finish. Amounts and references are illustrative; the shape is real.
const ledger: ReadonlyArray<ReadonlyArray<LedgerLine>> = [
  [
    { when: '09:12', event: 'Enquiry received by email', owner: 'Intake' },
    { when: '09:31', event: 'Lead L-0912 approved', owner: 'Sales Manager' },
  ],
  [
    { when: '09:48', event: 'RFQ-0912 sent to 3 suppliers', owner: 'Buyer' },
    { when: '11:05', event: 'Best quote SAR 48,200', owner: 'Buyer' },
    { when: '11:20', event: 'QT-0912 sent · valid 30 days', owner: 'Sales' },
  ],
  [
    { when: '12 Sep', event: 'Delivered · proof attached', owner: 'Logistics' },
    { when: '14 Sep', event: 'INV-000217 issued', owner: 'Finance' },
    { when: '28 Sep', event: 'Paid SAR 48,200 · matched', owner: 'Finance', final: true },
  ],
];

// The clock. Percentages of one cycle; the cycle is CYCLE_MS long.
const CYCLE_MS = 16000;
const LIT = [3, 24, 55] as const;          // when each stage lights up
const LINES = [[6, 13], [28, 36, 44], [59, 67, 75]] as const; // when each ledger line appears
const HOLD_END = 93;                        // the finished record holds until here, then clears

const easeOut = 'cubic-bezier(0.2, 0.7, 0.2, 1)';

const appear = (at: number) => keyframes`
  0%, ${at}% { opacity: 0; transform: translateY(6px); }
  ${at + 3}%, ${HOLD_END}% { opacity: 1; transform: none; }
  ${HOLD_END + 4}%, 100% { opacity: 0; transform: none; }
`;

const light = (at: number) => keyframes`
  0%, ${at}% { background: #14171d; border-color: rgba(224, 161, 0, 0.42); color: rgba(224, 161, 0, 0.55); box-shadow: none; transform: scale(1); }
  ${at + 1.5}% { transform: scale(1.14); }
  ${at + 3}%, ${HOLD_END}% { background: linear-gradient(160deg, #fff1c9 0%, #e0a100 48%, #c9931a 100%); border-color: #fff1c9; color: #14171d; box-shadow: 0 0 0 5px rgba(224, 161, 0, 0.14), 0 10px 26px -12px rgba(224, 161, 0, 0.95); transform: scale(1); }
  ${HOLD_END + 4}%, 100% { background: #14171d; border-color: rgba(224, 161, 0, 0.42); color: rgba(224, 161, 0, 0.55); box-shadow: none; }
`;

const fill = (from: number, to: number) => keyframes`
  0%, ${from}% { height: 0%; opacity: 1; }
  ${to}%, ${HOLD_END}% { height: 100%; opacity: 1; }
  ${HOLD_END + 4}%, 100% { height: 100%; opacity: 0; }
`;

const ride = (from: number, to: number) => keyframes`
  0%, ${from - 0.5}% { opacity: 0; }
  ${from}%, ${to}% { opacity: 1; }
  ${to + 0.5}%, 100% { opacity: 0; }
`;

const settle = keyframes`
  0%, 100% { transform: translate3d(0, 0, 0); }
  50% { transform: translate3d(0, -6px, 0); }
`;

const turn = keyframes`
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
`;

const Stages = styled.ol`
  list-style: none;
  padding: 0;
  margin: 28px 0 0;
  max-width: 400px;
  display: grid;
  gap: 0;

  @media (max-width: 1280px) { max-width: 340px; }

  /* Stacked above the form: the three stages side by side, each given room for its title. */
  @media (max-width: 1024px) {
    margin-top: 18px;
    max-width: 760px;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    gap: 20px;
  }
  @media (max-width: 599.95px) { display: none; }
`;

const Stage = styled.li`
  position: relative;
  display: grid;
  grid-template-columns: 34px minmax(0, 1fr);
  column-gap: 16px;
  padding-bottom: 22px;
  min-width: 0;

  @media (max-width: 1024px) {
    grid-template-columns: 30px minmax(0, 1fr);
    column-gap: 10px;
    padding-bottom: 0;
    align-items: start;
  }
`;

const Node = styled.span<{ at: number }>`
  grid-column: 1;
  grid-row: 1;
  width: 32px;
  height: 32px;
  margin-left: 1px;
  border-radius: 50%;
  border: 1px solid #fff1c9;
  display: grid;
  place-items: center;
  background: linear-gradient(160deg, #fff1c9 0%, #e0a100 48%, #c9931a 100%);
  color: #14171d;
  box-shadow: 0 0 0 5px rgba(224, 161, 0, 0.14), 0 10px 26px -12px rgba(224, 161, 0, 0.95);
  z-index: 1;
  animation: ${(p) => light(p.at)} ${CYCLE_MS}ms ${easeOut} infinite both;

  @media (max-width: 1024px) { width: 28px; height: 28px; }
`;

const Rail = styled.span`
  grid-column: 1;
  grid-row: 1 / span 2;
  position: absolute;
  left: 16px;
  top: 34px;
  bottom: 0;
  width: 2px;
  background: rgba(224, 161, 0, 0.16);
  border-radius: 1px;

  @media (max-width: 1024px) { display: none; }
`;

const Fill = styled.span<{ from: number; to: number }>`
  position: absolute;
  left: 0;
  top: 0;
  width: 100%;
  height: 100%;
  background: linear-gradient(#fff1c9, #e0a100);
  border-radius: 1px;
  animation: ${(p) => fill(p.from, p.to)} ${CYCLE_MS}ms linear infinite both;
`;

const Signal = styled.span<{ from: number; to: number }>`
  position: absolute;
  left: 50%;
  bottom: 0;
  width: 9px;
  height: 9px;
  margin-left: -4.5px;
  margin-bottom: -4.5px;
  border-radius: 50%;
  background: #fff1c9;
  box-shadow: 0 0 0 4px rgba(224, 161, 0, 0.16), 0 0 18px 2px rgba(224, 161, 0, 0.9);
  opacity: 0;
  animation: ${(p) => ride(p.from, p.to)} ${CYCLE_MS}ms linear infinite both;
`;

const Ledger = styled.ul`
  grid-column: 2;
  grid-row: 2;
  list-style: none;
  margin: 8px 0 0;
  padding: 0;
  display: grid;
  gap: 5px;

  @media (max-width: 1024px) { display: none; }
`;

const Line = styled.li<{ at: number; final?: boolean }>`
  display: grid;
  grid-template-columns: 46px minmax(0, 1fr);
  column-gap: 10px;
  align-items: baseline;
  font-size: 13px;
  line-height: 1.35;
  color: ${(p) => (p.final ? '#fff1c9' : 'rgba(232, 228, 218, 0.82)')};
  font-variant-numeric: tabular-nums;
  animation: ${(p) => appear(p.at)} ${CYCLE_MS}ms ${easeOut} infinite both;

  & > b {
    font-weight: 600;
    color: ${(p) => (p.final ? '#fff1c9' : '#e0a100')};
    letter-spacing: 0.01em;
    white-space: nowrap;
  }
  & > span {
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  & > span > i {
    font-style: normal;
    color: rgba(232, 228, 218, 0.5);
  }
`;

/** The three governed stages, with one record's ledger writing itself beneath them. */
export const EvidenceSpine: React.FC = () => (
  <Stages aria-label="Governed commercial stages">
    {evidenceStages.map((stage, i) => {
      const next = LIT[i + 1];
      return (
        <Stage key={stage.title}>
          <Node at={LIT[i]} aria-hidden="true">
            <CheckIcon sx={{ fontSize: 18 }} />
          </Node>
          {next !== undefined && (
            <Rail aria-hidden="true">
              <Fill from={LIT[i]} to={next}>
                <Signal from={LIT[i]} to={next} />
              </Fill>
            </Rail>
          )}
          <Box sx={{ gridColumn: 2, gridRow: 1, minWidth: 0, alignSelf: 'center' }}>
            <Typography sx={{ color: '#f8fafc', fontSize: { xs: 14, lg: 15 }, fontWeight: 700, lineHeight: 1.2 }}>
              {stage.title}
            </Typography>
            <Typography sx={{ color: '#c2bdb1', fontSize: { xs: 12, lg: 13 }, mt: 0.4, lineHeight: 1.25 }}>
              {stage.detail}
            </Typography>
          </Box>
          <Ledger aria-hidden="true">
            {ledger[i].map((line, j) => (
              <Line key={line.when + line.event} at={LINES[i][j]} final={line.final}>
                <b>{line.when}</b>
                <span>
                  {line.event} <i>· {line.owner}</i>
                </span>
              </Line>
            ))}
          </Ledger>
        </Stage>
      );
    })}
  </Stages>
);

const HeroFrame = styled.div`
  position: absolute;
  right: clamp(24px, 3vw, 44px);
  bottom: clamp(76px, 10vh, 128px);
  width: 168px;
  height: 168px;
  pointer-events: none;
  will-change: transform;

  @media (max-width: 1280px) { width: 136px; height: 136px; }
  @media (max-width: 1024px) { display: none; }
`;

const HeroFloat = styled.div`
  position: absolute;
  inset: 0;
  display: grid;
  place-items: center;
  animation: ${settle} 7s ease-in-out infinite;
`;

const Dial = styled.svg`
  position: absolute;
  inset: 0;
  width: 100%;
  height: 100%;
  animation: ${turn} 140s linear infinite;
`;

/**
 * The mark, standing in the panel like an object: an extruded N over a slowly turning dial,
 * floating a few pixels and leaning toward the pointer. Purely decorative; the brand's name is
 * carried by the wordmark at the top of the panel.
 */
export const BrandHero: React.FC = () => {
  const frame = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const el = frame.current;
    const panel = el?.parentElement;
    if (!el || !panel) return undefined;
    if (window.matchMedia?.('(prefers-reduced-motion: reduce)').matches) return undefined;
    let raf = 0;
    const lean = (event: PointerEvent) => {
      const rect = panel.getBoundingClientRect();
      const dx = ((event.clientX - rect.left) / rect.width - 0.5) * 2;
      const dy = ((event.clientY - rect.top) / rect.height - 0.5) * 2;
      cancelAnimationFrame(raf);
      raf = requestAnimationFrame(() => {
        el.style.transform = `translate3d(${(dx * 10).toFixed(1)}px, ${(dy * 8).toFixed(1)}px, 0)`;
      });
    };
    const rest = () => {
      cancelAnimationFrame(raf);
      el.style.transition = 'transform 600ms cubic-bezier(0.2, 0.7, 0.2, 1)';
      el.style.transform = 'translate3d(0, 0, 0)';
      window.setTimeout(() => { el.style.transition = ''; }, 620);
    };
    panel.addEventListener('pointermove', lean);
    panel.addEventListener('pointerleave', rest);
    return () => {
      cancelAnimationFrame(raf);
      panel.removeEventListener('pointermove', lean);
      panel.removeEventListener('pointerleave', rest);
    };
  }, []);

  return (
    <HeroFrame ref={frame} aria-hidden="true">
      <Dial viewBox="0 0 100 100" fill="none">
        <circle cx="50" cy="50" r="48" stroke="rgba(243, 210, 122, 0.22)" strokeWidth="0.6" strokeDasharray="1.2 3.1" />
        <circle cx="50" cy="50" r="39" stroke="rgba(224, 161, 0, 0.16)" strokeWidth="0.5" />
        <circle cx="50" cy="50" r="48" stroke="url(#nx-hero-arc)" strokeWidth="1.4" strokeDasharray="42 260" strokeLinecap="round" />
        <defs>
          <linearGradient id="nx-hero-arc" x1="0" y1="0" x2="1" y2="1">
            <stop offset="0" stopColor="#fff1c9" />
            <stop offset="1" stopColor="#e0a100" stopOpacity="0" />
          </linearGradient>
        </defs>
      </Dial>
      <HeroFloat>
        <BrandMark size={104} face="#e0a100" title="" />
      </HeroFloat>
    </HeroFrame>
  );
};
