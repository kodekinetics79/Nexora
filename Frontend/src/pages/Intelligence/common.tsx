import React from 'react';
import { Chip } from '@mui/material';

// Shared, jargon-free helpers for the Intelligence surfaces.
// Raw model scores (0..1) are never shown to users — only friendly levels.

export type ConfidenceLevel = 'high' | 'medium' | 'low' | 'unknown';

export const confidenceLevel = (score: number | null | undefined): ConfidenceLevel => {
  if (score == null || Number.isNaN(score)) return 'unknown';
  if (score >= 0.8) return 'high';
  if (score >= 0.5) return 'medium';
  return 'low';
};

const LEVEL_META: Record<ConfidenceLevel, { label: string; color: 'success' | 'warning' | 'error' | 'default' }> = {
  high: { label: 'High confidence', color: 'success' },
  medium: { label: 'Medium confidence', color: 'warning' },
  low: { label: 'Low confidence', color: 'error' },
  unknown: { label: 'Confidence unknown', color: 'default' },
};

export const ConfidenceChip: React.FC<{ score: number | null | undefined }> = ({ score }) => {
  const meta = LEVEL_META[confidenceLevel(score)];
  return (
    <Chip
      label={meta.label}
      color={meta.color}
      size="small"
      variant="outlined"
      sx={{ fontWeight: 800, fontSize: '0.68rem' }}
    />
  );
};

/** Formats a money amount. Currency may be null — falls back to a plain number. */
export const formatMoney = (value: number | null | undefined, currency?: string | null): string => {
  if (value == null || Number.isNaN(value)) return '—';
  const plain = value.toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 });
  if (!currency) return plain;
  try {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(value);
  } catch {
    // Currency code the runtime doesn't recognise — show it as a prefix instead.
    return `${currency} ${plain}`;
  }
};

/** Parses a user-typed price/quantity. Returns null for blank or invalid input. */
export const parseUserNumber = (raw: string): number | null => {
  const trimmed = raw.trim();
  if (!trimmed) return null;
  const n = Number(trimmed);
  return Number.isFinite(n) ? n : null;
};
