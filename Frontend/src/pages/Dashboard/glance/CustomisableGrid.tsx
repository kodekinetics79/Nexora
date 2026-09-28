import { useState, type ReactNode } from 'react';
import { Box, ButtonBase, Stack, Tooltip, Typography } from '@mui/material';
import {
  Add as ShowIcon, ChevronLeft as EarlierIcon, ChevronRight as LaterIcon, DragIndicator as DragIcon,
  VisibilityOff as HideIcon, WidthFull as WideIcon, WidthNormal as NormalIcon,
} from '@mui/icons-material';
import { BAND_LABELS, useBandLayout, useChartChoice, type BandKey } from './chartPrefs';
import { NEU_TRANSITION, neuFocus, neuKey } from './neumorphic';

/**
 * The bands as the reader has arranged them.
 *
 * Outside Edit layout this is just the grid. In Edit layout, each band carries a small bar: drag it
 * (or use the arrows) to move the band, make it wide or normal, or hide it. Hidden bands wait in
 * a row at the bottom and come back with one click. Every change is saved to the reader's profile
 * the moment it is made.
 */
export default function CustomisableGrid({ bands, editing }: { bands: Record<BandKey, ReactNode>; editing: boolean }) {
  const { order, save } = useBandLayout();
  const [dragKey, setDragKey] = useState<BandKey | null>(null);
  const [overKey, setOverKey] = useState<BandKey | null>(null);

  /** Puts `key` just before (or after) `target`. */
  const move = (key: BandKey, target: BandKey, after = false) => {
    const next = order.filter(b => b.key !== key);
    const moving = order.find(b => b.key === key)!;
    next.splice(next.findIndex(b => b.key === target) + (after ? 1 : 0), 0, moving);
    save(next);
  };
  const setVisible = (key: BandKey, visible: boolean) => save(order.map(b => (b.key === key ? { ...b, visible } : b)));

  const shown = order.filter(b => b.visible);
  const hidden = order.filter(b => !b.visible);

  return (
    <>
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: 'repeat(auto-fit, minmax(min(100%, 380px), 1fr))',
          gap: 2.5,
          alignItems: 'stretch',
        }}
      >
        {shown.map((b, i) => (
          <Slot
            key={b.key}
            band={b.key}
            editing={editing}
            first={i === 0}
            last={i === shown.length - 1}
            dragging={dragKey === b.key}
            over={overKey === b.key && dragKey !== b.key}
            onEarlier={() => i > 0 && move(b.key, shown[i - 1].key)}
            onLater={() => i < shown.length - 1 && move(b.key, shown[i + 1].key, true)}
            onHide={() => setVisible(b.key, false)}
            onDragStart={() => setDragKey(b.key)}
            onDragEnd={() => { setDragKey(null); setOverKey(null); }}
            onDragOver={() => setOverKey(b.key)}
            onDrop={() => { if (dragKey && dragKey !== b.key) move(dragKey, b.key); setDragKey(null); setOverKey(null); }}
          >
            {bands[b.key]}
          </Slot>
        ))}
      </Box>

      {hidden.length > 0 && (
        <Stack direction="row" sx={{ mt: 2, gap: 1, flexWrap: 'wrap', alignItems: 'center' }} data-testid="hidden-bands">
          <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700 }}>Hidden:</Typography>
          {hidden.map(b => (
            <ButtonBase
              key={b.key}
              onClick={() => setVisible(b.key, true)}
              aria-label={`Show ${BAND_LABELS[b.key]}`}
              sx={theme => ({ ...neuKey(theme.palette.mode), ...neuFocus, px: 1.25, py: 0.5, borderRadius: '10px', fontSize: 12, fontWeight: 700, gap: 0.5 })}
            >
              <ShowIcon sx={{ fontSize: 16 }} />
              {BAND_LABELS[b.key]}
            </ButtonBase>
          ))}
        </Stack>
      )}
    </>
  );
}

interface SlotProps {
  band: BandKey;
  editing: boolean;
  first: boolean;
  last: boolean;
  dragging: boolean;
  over: boolean;
  children: ReactNode;
  onEarlier: () => void;
  onLater: () => void;
  onHide: () => void;
  onDragStart: () => void;
  onDragEnd: () => void;
  onDragOver: () => void;
  onDrop: () => void;
}

function Slot({ band, editing, first, last, dragging, over, children, onEarlier, onLater, onHide, onDragStart, onDragEnd, onDragOver, onDrop }: SlotProps) {
  const [size, setSize] = useChartChoice(`size.${band}`, ['normal', 'wide'] as const, 'normal');
  const name = BAND_LABELS[band];
  return (
    <Box
      data-band={band}
      draggable={editing}
      onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', band); onDragStart(); }}
      onDragEnd={onDragEnd}
      onDragOver={e => { if (!editing) return; e.preventDefault(); onDragOver(); }}
      onDrop={e => { e.preventDefault(); onDrop(); }}
      sx={{
        position: 'relative',
        minWidth: 0,
        display: 'flex',
        flexDirection: 'column',
        gridColumn: size === 'wide' ? { xs: 'auto', lg: 'span 2' } : 'auto',
        opacity: dragging ? 0.4 : 1,
        transition: 'opacity 160ms, outline-color 160ms',
        outline: editing ? '2px dashed' : 'none',
        outlineColor: over ? 'var(--nx-glance-seal-rim, #8A6A2A)' : editing ? 'rgba(128,128,128,0.35)' : 'transparent',
        outlineOffset: 4,
        borderRadius: '24px',
        cursor: editing ? 'grab' : 'auto',
        '& > .band-body': { flex: 1, display: 'flex', flexDirection: 'column', '& > *': { flex: 1 } },
      }}
    >
      {editing && (
        <Stack
          direction="row"
          role="toolbar"
          aria-label={`Arrange ${name}`}
          sx={{ position: 'absolute', top: -14, right: 16, zIndex: 2, gap: 0.5 }}
        >
          <ToolKey label={`Drag to move ${name}`} decorative><DragIcon sx={{ fontSize: 16 }} /></ToolKey>
          <ToolKey label={`Move ${name} earlier`} onClick={onEarlier} disabled={first}><EarlierIcon sx={{ fontSize: 16 }} /></ToolKey>
          <ToolKey label={`Move ${name} later`} onClick={onLater} disabled={last}><LaterIcon sx={{ fontSize: 16 }} /></ToolKey>
          <ToolKey
            label={size === 'wide' ? `Make ${name} normal width` : `Make ${name} wide`}
            onClick={() => setSize(size === 'wide' ? 'normal' : 'wide')}
          >
            {size === 'wide' ? <NormalIcon sx={{ fontSize: 16 }} /> : <WideIcon sx={{ fontSize: 16 }} />}
          </ToolKey>
          <ToolKey label={`Hide ${name}`} onClick={onHide}><HideIcon sx={{ fontSize: 16 }} /></ToolKey>
        </Stack>
      )}
      <Box className="band-body" sx={{ pointerEvents: editing ? 'none' : 'auto' }}>{children}</Box>
    </Box>
  );
}

function ToolKey({ label, onClick, disabled = false, decorative = false, children }: {
  label: string; onClick?: () => void; disabled?: boolean; decorative?: boolean; children: ReactNode;
}) {
  return (
    <Tooltip title={label}>
      <span>
        <ButtonBase
          aria-label={label}
          aria-hidden={decorative || undefined}
          tabIndex={decorative ? -1 : 0}
          onClick={onClick}
          disabled={disabled}
          sx={theme => ({
            ...neuKey(theme.palette.mode), ...neuFocus, ...NEU_TRANSITION,
            width: 28, height: 28, borderRadius: '9px', color: 'text.primary',
            cursor: decorative ? 'grab' : 'pointer',
            '&.Mui-disabled': { opacity: 0.35 },
          })}
        >
          {children}
        </ButtonBase>
      </span>
    </Tooltip>
  );
}
