import React, { useState } from 'react';
import {
  Box, Popover, Typography, Stack, Chip, IconButton, Divider, Tooltip,
} from '@mui/material';
import {
  ContentCopy as CopyIcon,
  Check as CopiedIcon,
} from '@mui/icons-material';
import {
  parseTransformations,
  type LeadFieldEvidenceEntry,
} from '../../api/services/extractionReviewService';
import { describeCertainty } from './fieldCertainty';

// ---------------------------------------------------------------------------
// Glass box: where did this value come from?
//
// The join (FieldEvidence -> DocumentRegion.SourceAddress -> DocumentPage) is
// fully persisted and was rendered nowhere. This shows it: the address inside
// the customer's own workbook, what was literally in that cell, what we turned
// it into, and every step in between.
//
// No bounding-box overlay. Spreadsheet regions carry cell indices, not page
// geometry, and the PDF/OCR path discards word boxes entirely — an overlay
// would have to be invented for most documents. When there is no ledger entry
// the panel says exactly that instead of implying the value is unsourced.
// ---------------------------------------------------------------------------

const VALIDATION_META: Record<string, { label: string; color: 'success' | 'warning' | 'error' | 'default' }> = {
  valid: { label: 'Passed source checks', color: 'success' },
  warning: { label: 'Flagged by source checks', color: 'warning' },
  invalid: { label: 'Failed source checks', color: 'error' },
  unvalidated: { label: 'Not checked', color: 'default' },
};

const validationMeta = (status: string | null | undefined) =>
  VALIDATION_META[(status ?? '').toLowerCase()] ?? { label: 'Not checked', color: 'default' as const };

const shown = (value: string | null | undefined, fallback = 'Not recorded'): string =>
  value == null || value.trim() === '' ? fallback : value;

const CopyButton: React.FC<{ value: string; label: string }> = ({ value, label }) => {
  const [copied, setCopied] = useState(false);
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      setCopied(true);
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      // Clipboard blocked (insecure origin / permission) — the address is
      // already on screen and selectable, so this is not worth an error toast.
    }
  };
  return (
    <Tooltip title={copied ? 'Copied' : label}>
      <IconButton size="small" onClick={copy} aria-label={label}>
        {copied ? <CopiedIcon sx={{ fontSize: 15 }} color="success" /> : <CopyIcon sx={{ fontSize: 15 }} />}
      </IconButton>
    </Tooltip>
  );
};

const Row: React.FC<{ label: string; children: React.ReactNode }> = ({ label, children }) => (
  <Box sx={{ minWidth: 0 }}>
    <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700, textTransform: 'uppercase', fontSize: '0.6rem', letterSpacing: '0.04em' }}>
      {label}
    </Typography>
    <Box sx={{ mt: 0.25 }}>{children}</Box>
  </Box>
);

export interface FieldEvidencePopoverProps {
  anchorEl: HTMLElement | null;
  onClose: () => void;
  /** Human label of the column that was clicked, e.g. "Quantity". */
  fieldLabel: string;
  /** Row identity for the header line, e.g. "Line 7". */
  lineLabel?: string;
  /** The ledger entry, when the document's path recorded one. */
  entry: LeadFieldEvidenceEntry | null | undefined;
  /**
   * True when the whole document has no source-address ledger (PDF/OCR path, or
   * an older extraction) rather than this one field being unmapped.
   */
  documentUnmapped: boolean;
  /** True while the field-evidence request is still in flight. */
  loading?: boolean;
}

const FieldEvidencePopover: React.FC<FieldEvidencePopoverProps> = ({
  anchorEl,
  onClose,
  fieldLabel,
  lineLabel,
  entry,
  documentUnmapped,
  loading = false,
}) => {
  const open = Boolean(anchorEl);
  const address = entry?.sourceAddress ?? null;
  const transformations = entry ? parseTransformations(entry) : [];
  const meta = validationMeta(entry?.validationStatus);
  const certainty = describeCertainty(entry);

  return (
    <Popover
      open={open}
      anchorEl={anchorEl}
      onClose={onClose}
      anchorOrigin={{ vertical: 'bottom', horizontal: 'left' }}
      transformOrigin={{ vertical: 'top', horizontal: 'left' }}
      // Non-modal on purpose. This panel opens on every cell click while the
      // reviewer works the grid with the keyboard, so it must not trap focus,
      // must not restore it on close, and must not put a backdrop between the
      // reviewer and the next cell they want to inspect.
      disableAutoFocus
      disableEnforceFocus
      disableRestoreFocus
      disableScrollLock
      hideBackdrop
      sx={{ pointerEvents: 'none' }}
      slotProps={{ paper: { sx: { p: 2, maxWidth: 420, borderRadius: 2, pointerEvents: 'auto' } } }}
    >
      {/* role="status" rather than "dialog": it is announced when it appears
          without claiming a focus contract it deliberately does not honour. */}
      <Box role="status" aria-label={`Source of ${fieldLabel}${lineLabel ? ` on ${lineLabel}` : ''}`}>
        <Stack direction="row" spacing={1} sx={{ alignItems: 'baseline', justifyContent: 'space-between', mb: 1.5 }}>
          <Typography sx={{ fontWeight: 900, fontSize: '0.95rem' }}>{fieldLabel}</Typography>
          {lineLabel && (
            <Typography variant="caption" sx={{ color: 'text.secondary', fontWeight: 700 }}>{lineLabel}</Typography>
          )}
        </Stack>

        {loading ? (
          <Typography variant="body2" color="text.secondary">Looking up the source of this value…</Typography>
        ) : !entry ? (
          <Stack spacing={1}>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>
              {documentUnmapped ? 'Source not mapped for this document' : 'Source not mapped for this field'}
            </Typography>
            <Typography variant="body2" color="text.secondary">
              {documentUnmapped
                ? 'This document was processed on a path that does not record where each value sat in the original file. Open the source attachment to cross-check.'
                : 'The extraction recorded no origin for this particular value. Other fields on this document may still be mapped.'}
            </Typography>
          </Stack>
        ) : (
          <Stack spacing={1.5}>
            <Row label="Source address">
              {address ? (
                <Stack direction="row" spacing={0.5} sx={{ alignItems: 'center' }}>
                  <Typography
                    component="code"
                    sx={{ fontFamily: 'monospace', fontWeight: 800, fontSize: '0.9rem', bgcolor: 'action.hover', px: 0.75, py: 0.25, borderRadius: 1, overflowWrap: 'anywhere' }}
                  >
                    {address}
                  </Typography>
                  <CopyButton value={address} label="Copy source address" />
                </Stack>
              ) : (
                <Typography variant="body2" color="text.secondary">
                  Not recorded{entry.pageNumber != null ? ` · page ${entry.pageNumber}` : ''}
                  {entry.sheetName ? ` · sheet ${entry.sheetName}` : ''}
                </Typography>
              )}
            </Row>

            <Divider />

            <Box sx={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 1.5 }}>
              <Row label="Value in the document">
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem', overflowWrap: 'anywhere' }}>
                  {shown(entry.rawValue, 'Blank cell')}
                </Typography>
              </Row>
              <Row label="Stored as">
                <Typography sx={{ fontFamily: 'monospace', fontSize: '0.8rem', fontWeight: 700, overflowWrap: 'anywhere' }}>
                  {shown(entry.normalizedValue, 'Not stored')}
                </Typography>
              </Row>
            </Box>

            <Row label="Steps applied">
              {transformations.length === 0 ? (
                <Typography variant="body2" color="text.secondary">
                  Stored exactly as it appears in the document.
                </Typography>
              ) : (
                <Stack direction="row" spacing={0.5} useFlexGap sx={{ flexWrap: 'wrap' }}>
                  {transformations.map((step, index) => (
                    <Chip key={`${step}-${index}`} size="small" variant="outlined" label={step} sx={{ height: 20, fontSize: '0.65rem' }} />
                  ))}
                </Stack>
              )}
            </Row>

            {certainty && (
              <Row label="How certain">
                <Stack spacing={0.5}>
                  <Chip
                    size="small"
                    color={certainty.color}
                    variant="outlined"
                    label={certainty.label}
                    sx={{ fontWeight: 700, fontSize: '0.65rem', height: 22, alignSelf: 'flex-start' }}
                  />
                  <Typography variant="body2" color="text.secondary">{certainty.detail}</Typography>
                  {certainty.recorded && (
                    <Typography variant="caption" color="text.disabled">{certainty.recorded}</Typography>
                  )}
                </Stack>
              </Row>
            )}

            <Stack direction="row" spacing={1} useFlexGap sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
              <Chip size="small" color={meta.color} variant="outlined" label={meta.label} sx={{ fontWeight: 700, fontSize: '0.65rem', height: 22 }} />
              {entry.extractor && (
                <Typography variant="caption" color="text.secondary">Read by {entry.extractor}</Typography>
              )}
            </Stack>
          </Stack>
        )}
      </Box>
    </Popover>
  );
};

export default FieldEvidencePopover;
