import React from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Stack, Chip, Switch,
  TextField, MenuItem, CircularProgress, Alert, Breadcrumbs, Link, Divider,
} from '@mui/material';
import {
  AutoAwesome as SparkleIcon,
  ArrowBack as BackIcon,
  NavigateNext as NextIcon,
  ErrorOutlined as AttentionIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import intelligenceService from '../../api/services/intelligenceService';
import type { ConversionPreviewItem } from '../../api/services/intelligenceService';
import { ConfidenceChip, parseUserNumber } from './common';
import CommercialLineIntelligence from '../../components/common/CommercialLineIntelligence';
import leadService from '../../api/services/leadService';

/** Sentinel for "None of these — leave unmatched" in the product dropdown. */
const NO_MATCH = 'none';

interface LineEdit {
  include: boolean;
  /** null = deliberately unmatched. */
  productId: number | null;
  quantity: string;
  unitOfMeasure: string;
}

const formatDate = (dateStr: string | null): string => {
  if (!dateStr) return '—';
  const d = new Date(dateStr);
  return isNaN(d.getTime())
    ? '—'
    : d.toLocaleDateString('en-GB', { day: '2-digit', month: 'short', year: 'numeric' });
};

const HeaderField: React.FC<{ label: string; value: string | null }> = ({ label, value }) => (
  <Box>
    <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', display: 'block', fontSize: '0.65rem' }}>
      {label}
    </Typography>
    <Typography sx={{ fontWeight: 800, fontSize: '0.85rem' }}>{value || '—'}</Typography>
  </Box>
);

const LeadConvertPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const leadId = Number(id);

  const { data: preview, isLoading, isError, refetch } = useQuery({
    queryKey: ['lead-conversion-preview', leadId],
    queryFn: () => intelligenceService.getConversionPreview(leadId),
    enabled: !!id && Number.isFinite(leadId),
  });
  const { data: lead } = useQuery({
    queryKey: ['lead-detail', leadId],
    queryFn: () => leadService.getById(leadId),
    enabled: !!id && Number.isFinite(leadId),
  });

  const [edits, setEdits] = React.useState<Record<number, LineEdit>>({});
  const [notes, setNotes] = React.useState('');

  // Pre-fill editable state once the preview arrives (normalized values win).
  React.useEffect(() => {
    if (!preview) return;
    setEdits((prev) => {
      if (Object.keys(prev).length > 0) return prev;
      const next: Record<number, LineEdit> = {};
      for (const item of preview.items) {
        const qty = item.normalizedQuantity ?? item.quantity;
        next[item.leadItemId] = {
          include: true,
          productId: item.needsAttention ? null : item.bestMatchProductId,
          quantity: qty != null ? String(qty) : '',
          unitOfMeasure: item.normalizedUom ?? item.unitOfMeasure ?? '',
        };
      }
      return next;
    });
  }, [preview]);

  const convertMutation = useMutation({
    mutationFn: () => {
      const items = (preview?.items ?? []).map((item) => {
        const edit = edits[item.leadItemId];
        const qty = edit ? parseUserNumber(edit.quantity) : null;
        const uom = edit?.unitOfMeasure.trim();
        return {
          leadItemId: item.leadItemId,
          include: edit?.include ?? true,
          productId: edit?.productId ?? undefined,
          quantity: qty ?? undefined,
          unitOfMeasure: uom || undefined,
        };
      });
      return intelligenceService.convertLead(leadId, {
        items,
        notes: notes.trim() || undefined,
      });
    },
    onSuccess: (result) => {
      enqueueSnackbar('RFQ created — here it is.', { variant: 'success' });
      navigate(`/procurement/rfqs/view/${result.rfqId}`);
    },
    onError: () => {
      enqueueSnackbar("Couldn't create the RFQ. Nothing was changed — please try again.", { variant: 'error' });
    },
  });

  const updateEdit = (leadItemId: number, patch: Partial<LineEdit>) => {
    setEdits((prev) => ({
      ...prev,
      [leadItemId]: { ...(prev[leadItemId] ?? { include: true, productId: null, quantity: '', unitOfMeasure: '' }), ...patch },
    }));
  };

  // ── Loading / error / empty states ─────────────────────────────────────────

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', flexDirection: 'column', alignItems: 'center', justifyContent: 'center', height: '60vh', gap: 2 }}>
        <CircularProgress />
        <Typography color="text.secondary" sx={{ fontWeight: 600 }}>
          Reading the lead and finding product matches…
        </Typography>
      </Box>
    );
  }

  if (isError || !preview) {
    return (
      <Box sx={{ p: 4, maxWidth: 640, mx: 'auto' }}>
        <Alert severity="error" sx={{ borderRadius: 2, mb: 2 }}>
          We couldn't prepare this lead for conversion. It may still be processing, or the service may be busy.
        </Alert>
        <Stack direction="row" spacing={1.5}>
          <Button variant="contained" onClick={() => refetch()} sx={{ fontWeight: 800, borderRadius: 2 }}>
            Try again
          </Button>
          <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(`/procurement/leads/view/${leadId}`)} sx={{ fontWeight: 800, borderRadius: 2 }}>
            Back to lead
          </Button>
        </Stack>
      </Box>
    );
  }

  if (preview.items.length === 0) {
    return (
      <Box sx={{ p: 4, maxWidth: 640, mx: 'auto', textAlign: 'center' }}>
        <SparkleIcon sx={{ fontSize: 48, color: 'text.disabled', mb: 1 }} />
        <Typography variant="h6" sx={{ fontWeight: 800, mb: 1 }}>Nothing to convert yet</Typography>
        <Typography color="text.secondary" sx={{ mb: 3 }}>
          This lead has no line items we can turn into an RFQ.
        </Typography>
        <Button variant="outlined" startIcon={<BackIcon />} onClick={() => navigate(`/procurement/leads/view/${leadId}`)} sx={{ fontWeight: 800, borderRadius: 2 }}>
          Back to lead
        </Button>
      </Box>
    );
  }

  // ── Derived values ──────────────────────────────────────────────────────────

  // Lines that need a human look float to the top; original order otherwise.
  const sortedItems: ConversionPreviewItem[] = [...preview.items].sort(
    (a, b) => Number(b.needsAttention) - Number(a.needsAttention)
  );

  const attentionCount = preview.items.filter((i) => i.needsAttention).length;
  const includedCount = preview.items.filter((i) => edits[i.leadItemId]?.include ?? true).length;

  // An included line with text in the qty box that isn't a number blocks submit.
  const hasInvalidQty = preview.items.some((i) => {
    const e = edits[i.leadItemId];
    if (!e || !e.include) return false;
    return e.quantity.trim() !== '' && parseUserNumber(e.quantity) == null;
  });

  const canSubmit = includedCount > 0 && !hasInvalidQty && !convertMutation.isPending;

  // ── Render ──────────────────────────────────────────────────────────────────

  return (
    <Box sx={{ p: 3, maxWidth: 1100, mx: 'auto' }}>
      <Breadcrumbs separator={<NextIcon sx={{ fontSize: 14 }} />} sx={{ mb: 2 }}>
        <Link component="button" variant="caption" onClick={() => navigate('/procurement/leads/all')} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          Leads
        </Link>
        <Link component="button" variant="caption" onClick={() => navigate(`/procurement/leads/view/${leadId}`)} sx={{ color: 'text.secondary', fontWeight: 700, textDecoration: 'none', textTransform: 'uppercase' }}>
          {preview.header.rfqno || `Lead #${preview.leadId}`}
        </Link>
        <Typography variant="caption" sx={{ color: 'primary.main', fontWeight: 900, textTransform: 'uppercase' }}>
          Review & Create RFQ
        </Typography>
      </Breadcrumbs>

      {/* Page header */}
      <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'flex-start', mb: 3, gap: 2, flexWrap: 'wrap' }}>
        <Box>
          <Typography variant="h5" sx={{ fontWeight: 950, letterSpacing: '-0.02em', mb: 0.5 }}>
            Review inquiry and create RFQ
          </Typography>
          <Typography variant="body2" color="text.secondary" sx={{ fontWeight: 600 }}>
            Confirm the requested lines and required customer data, then create one traceable RFQ.
          </Typography>
        </Box>
        <ConfidenceChip score={preview.overallConfidence} />
      </Box>

      {/* Lead summary */}
      <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider', mb: 2 }}>
        <Stack direction="row" spacing={4} sx={{ flexWrap: 'wrap', rowGap: 2 }}>
          <HeaderField label="RFQ #" value={preview.header.rfqno} />
          <HeaderField label="Buyer" value={preview.header.buyersName} />
          <HeaderField label="Customer" value={lead?.customerName ?? null} />
          <HeaderField label="Account Owner" value={lead?.accountOwnerName ?? null} />
          <HeaderField label="Opportunity Owner" value={lead?.assignedToFullName ?? null} />
          <HeaderField label="Received" value={formatDate(preview.header.recDate)} />
          <HeaderField label="Bid closing" value={formatDate(preview.header.bidClosingDate)} />
          <HeaderField label="Lines" value={String(preview.items.length)} />
        </Stack>
      </Paper>

      {attentionCount > 0 && (
        <Alert severity="warning" icon={<AttentionIcon />} sx={{ borderRadius: 2, mb: 2, fontWeight: 600 }}>
          {attentionCount === 1
            ? '1 line needs a quick look — it’s at the top.'
            : `${attentionCount} lines need a quick look — they’re at the top.`}
        </Alert>
      )}

      <Box sx={{ mb: 2 }}><CommercialLineIntelligence stage="lead" recordId={leadId} /></Box>

      {/* Line cards */}
      <Stack spacing={2} sx={{ mb: 3 }}>
        {sortedItems.map((item, idx) => {
          const edit = edits[item.leadItemId] ?? {
            include: true,
            productId: item.bestMatchProductId,
            quantity: '',
            unitOfMeasure: '',
          };
          const selectedMatch = edit.productId != null
            ? item.matches.find((m) => m.productId === edit.productId)
            : undefined;
          const qtyInvalid = edit.quantity.trim() !== '' && parseUserNumber(edit.quantity) == null;

          return (
            <Paper
              key={item.leadItemId}
              sx={{
                p: 2.5,
                borderRadius: 3,
                border: '1px solid',
                borderColor: item.needsAttention && edit.include ? 'warning.main' : 'divider',
                bgcolor: item.needsAttention && edit.include ? 'rgba(255, 167, 38, 0.06)' : 'background.paper',
                opacity: edit.include ? 1 : 0.55,
                transition: 'opacity 0.2s, border-color 0.2s',
              }}
            >
              {/* Card header: include toggle + confidence */}
              <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', mb: 1.5, gap: 1, flexWrap: 'wrap' }}>
                <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                  <Switch
                    checked={edit.include}
                    onChange={(e) => updateEdit(item.leadItemId, { include: e.target.checked })}
                    slotProps={{ input: { 'aria-label': `Include line ${idx + 1} in the RFQ` } }}
                    size="small"
                  />
                  <Typography sx={{ fontWeight: 800, fontSize: '0.8rem', color: edit.include ? 'text.primary' : 'text.disabled' }}>
                    {edit.include ? 'Included' : 'Left out'}
                  </Typography>
                  {item.needsAttention && (
                    <Chip
                      icon={<AttentionIcon sx={{ fontSize: 14 }} />}
                      label="Needs a look"
                      size="small"
                      color="warning"
                      sx={{ fontWeight: 800, fontSize: '0.65rem' }}
                    />
                  )}
                </Stack>
                <ConfidenceChip score={item.confidence} />
              </Box>

              {/* Source text */}
              <Typography variant="caption" sx={{ fontWeight: 800, color: 'text.disabled', textTransform: 'uppercase', fontSize: '0.65rem', display: 'block' }}>
                From the request
              </Typography>
              <Typography sx={{ fontSize: '0.85rem', fontWeight: 600, mb: 1, whiteSpace: 'pre-wrap' }}>
                {item.sourceText || 'No original text was captured for this line.'}
              </Typography>

              {item.needsAttention && item.attentionReason && (
                <Typography sx={{ fontSize: '0.8rem', fontWeight: 700, color: 'warning.dark', mb: 1.5 }}>
                  {item.attentionReason}
                </Typography>
              )}

              <Divider sx={{ my: 1.5 }} />

              {/* Editable fields */}
              <Stack direction={{ xs: 'column', md: 'row' }} spacing={2}>
                <TextField
                  select
                  fullWidth
                  size="small"
                  label="Matched product"
                  value={edit.productId != null ? String(edit.productId) : NO_MATCH}
                  onChange={(e) => updateEdit(item.leadItemId, {
                    productId: e.target.value === NO_MATCH ? null : Number(e.target.value),
                  })}
                  disabled={!edit.include}
                  helperText={
                    edit.productId == null
                      ? 'This line will go on the RFQ without a linked product.'
                      : selectedMatch?.reason || ' '
                  }
                  sx={{ flex: 2 }}
                >
                  {item.matches.map((m) => (
                    <MenuItem key={m.productId} value={String(m.productId)}>
                      <Box>
                        <Typography sx={{ fontSize: '0.85rem', fontWeight: 700 }}>
                          {m.productName || `Product #${m.productId}`}
                        </Typography>
                        <Typography variant="caption" color="text.secondary">
                          {[m.materialCode, m.manufacturerPartNumber].filter(Boolean).join(' · ') || 'No product code on file'}
                        </Typography>
                      </Box>
                    </MenuItem>
                  ))}
                  <MenuItem value={NO_MATCH}>
                    <Typography sx={{ fontSize: '0.85rem', fontWeight: 700, color: 'text.secondary' }}>
                      None of these — leave unmatched
                    </Typography>
                  </MenuItem>
                </TextField>

                <TextField
                  size="small"
                  label="Quantity"
                  value={edit.quantity}
                  onChange={(e) => updateEdit(item.leadItemId, { quantity: e.target.value })}
                  disabled={!edit.include}
                  error={qtyInvalid}
                  helperText={qtyInvalid ? 'Please enter a number.' : ' '}
                  slotProps={{ input: { inputMode: 'decimal' } }}
                  sx={{ flex: 1, minWidth: 120 }}
                />

                <TextField
                  size="small"
                  label="Unit"
                  value={edit.unitOfMeasure}
                  onChange={(e) => updateEdit(item.leadItemId, { unitOfMeasure: e.target.value })}
                  disabled={!edit.include}
                  helperText=" "
                  sx={{ flex: 1, minWidth: 120 }}
                />
              </Stack>
            </Paper>
          );
        })}
      </Stack>

      {/* Notes + actions */}
      <Paper sx={{ p: 2.5, borderRadius: 3, border: '1px solid', borderColor: 'divider' }}>
        <TextField
          fullWidth
          multiline
          minRows={2}
          label="Anything the team should know? (optional)"
          value={notes}
          onChange={(e) => setNotes(e.target.value)}
          sx={{ mb: 2 }}
        />
        <Box sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', gap: 2, flexWrap: 'wrap' }}>
          <Typography sx={{ fontWeight: 700, fontSize: '0.85rem', color: 'text.secondary' }}>
            {includedCount === 0
              ? 'Include at least one line to create the RFQ.'
              : `${includedCount} of ${preview.items.length} lines will go on the RFQ.`}
          </Typography>
          <Stack direction="row" spacing={1.5}>
            <Button
              variant="outlined"
              onClick={() => navigate(`/procurement/leads/view/${leadId}`)}
              disabled={convertMutation.isPending}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3 }}
            >
              Cancel
            </Button>
            <Button
              variant="contained"
              startIcon={convertMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <SparkleIcon />}
              onClick={() => convertMutation.mutate()}
              disabled={!canSubmit}
              sx={{ fontWeight: 800, borderRadius: 2, px: 4 }}
            >
              {convertMutation.isPending ? 'Creating…' : 'Create RFQ'}
            </Button>
          </Stack>
        </Box>
      </Paper>
    </Box>
  );
};

export default LeadConvertPage;
