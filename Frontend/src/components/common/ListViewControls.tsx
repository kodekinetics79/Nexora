import { useState, type ReactNode } from 'react';
import {
  Box, Button, Collapse, Stack, ToggleButton, ToggleButtonGroup, Tooltip, Typography,
} from '@mui/material';
import { Tune as TuneIcon } from '@mui/icons-material';

/**
 * The Simple / Spreadsheet switch and the Display fold, lifted from the Leads list so every list
 * that offers two ways to read its rows offers them in the same place and the same words.
 *
 * Both choices are per user and per browser: rendering comforts with no server contract. A list
 * passes its own storage base (Leads keeps `nexora.leadsPage`, so nobody's saved choice moves).
 */

export type ListViewChoice = 'simple' | 'spreadsheet';
export type ListDensityChoice = 'comfortable' | 'standard' | 'compact';

const userScopedKey = (base: string): string => {
  try {
    const raw = localStorage.getItem('userData');
    if (raw) {
      const parsed: unknown = JSON.parse(raw);
      if (parsed && typeof parsed === 'object' && 'id' in parsed) {
        const id = (parsed as { id?: unknown }).id;
        if (typeof id === 'number' || typeof id === 'string') return `${base}:user-${id}`;
      }
    }
  } catch {
    // Corrupted userData: fall back to a global preference key.
  }
  return `${base}:global`;
};

const read = (key: string): string | null => {
  try {
    return localStorage.getItem(userScopedKey(key));
  } catch {
    return null;
  }
};

const write = (key: string, value: string) => {
  try {
    localStorage.setItem(userScopedKey(key), value);
  } catch {
    // Storage unavailable: the choice just won't persist.
  }
};

/** The view and density a user last chose on this list. `storageBase` e.g. `nexora.quotesPage`. */
export function useListViewChoice(storageBase: string) {
  const viewKey = `${storageBase}.view`;
  const densityKey = `${storageBase}.density`;
  const [view, setViewState] = useState<ListViewChoice>(() =>
    (read(viewKey) === 'spreadsheet' ? 'spreadsheet' : 'simple'));
  const [density, setDensityState] = useState<ListDensityChoice>(() => {
    const stored = read(densityKey);
    return stored === 'compact' || stored === 'standard' ? stored : 'comfortable';
  });
  const setView = (value: ListViewChoice) => {
    setViewState(value);
    write(viewKey, value);
  };
  const setDensity = (value: ListDensityChoice) => {
    setDensityState(value);
    write(densityKey, value);
  };
  return { view, setView, density, setDensity };
}

export interface ListViewControlsProps {
  view: ListViewChoice;
  onViewChange: (value: ListViewChoice) => void;
  density: ListDensityChoice;
  onDensityChange: (value: ListDensityChoice) => void;
  /** Hover words on the two view buttons. */
  simpleHint?: string;
  spreadsheetHint?: string;
  /**
   * The column chooser for the Spreadsheet view, when the list has saved column layouts. Without
   * one the Display fold holds row density only.
   */
  columnsControl?: ReactNode;
}

/**
 * Renders as siblings (toggle, Display button, the fold) so it drops into the list's existing
 * wrapping filter row, exactly where Leads has them.
 */
export default function ListViewControls({
  view, onViewChange, density, onDensityChange,
  simpleHint = 'Plain columns, no sideways scrolling.',
  spreadsheetHint = 'Every field.',
  columnsControl,
}: ListViewControlsProps) {
  const [displayOpen, setDisplayOpen] = useState(false);
  return (
    <>
      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
        <ToggleButtonGroup
          size="small"
          exclusive
          value={view}
          onChange={(_e, value: ListViewChoice | null) => {
            if (value) onViewChange(value);
          }}
          aria-label="List view"
        >
          <Tooltip title={simpleHint} describeChild><ToggleButton value="simple" aria-label="Simple view">Simple</ToggleButton></Tooltip>
          <Tooltip title={spreadsheetHint} describeChild><ToggleButton value="spreadsheet" aria-label="Spreadsheet view">Spreadsheet</ToggleButton></Tooltip>
        </ToggleButtonGroup>
      </Stack>
      <Button
        size="small"
        variant="text"
        startIcon={<TuneIcon />}
        onClick={() => setDisplayOpen((open) => !open)}
        aria-expanded={displayOpen}
        sx={{ fontWeight: 700, textTransform: 'none' }}
      >
        Display
      </Button>
      <Collapse in={displayOpen} sx={{ width: '100%' }}>
        <Box sx={{ display: 'flex', flexWrap: 'wrap', gap: 1.5, alignItems: 'center', pt: 1.5, borderTop: '1px solid', borderColor: 'divider' }}>
          {columnsControl && (view === 'spreadsheet' ? columnsControl : (
            <Typography variant="caption" color="text.secondary">Switch to the Spreadsheet view to choose and order columns.</Typography>
          ))}
          <ToggleButtonGroup
            size="small"
            exclusive
            value={density}
            onChange={(_e, value: ListDensityChoice | null) => {
              if (value) onDensityChange(value);
            }}
            aria-label="Row density"
          >
            <ToggleButton value="comfortable" aria-label="Comfortable rows">Comfortable</ToggleButton>
            <ToggleButton value="standard" aria-label="Standard rows">Standard</ToggleButton>
            <ToggleButton value="compact" aria-label="Compact rows">Compact</ToggleButton>
          </ToggleButtonGroup>
        </Box>
      </Collapse>
    </>
  );
}
