import React, { useEffect, useMemo, useState } from 'react';
import { useParams, useNavigate } from 'react-router-dom';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import {
  Box, Typography, Paper, Button, Stack, Chip, TextField, CircularProgress,
  Alert, Collapse, IconButton, MenuItem, Tooltip, Divider, Breadcrumbs, Link,
} from '@mui/material';
import {
  ArrowBack as BackIcon,
  ExpandMore as ExpandIcon,
  ExpandLess as CollapseIcon,
  Add as AddIcon,
  DeleteOutlined as DeleteIcon,
  Save as SaveIcon,
  TaskAlt as ApproveIcon,
  FileDownloadOutlined as ExportIcon,
  CallSplit as ExplodeIcon,
  NavigateNext as NextIcon,
} from '@mui/icons-material';
import { useSnackbar } from 'notistack';
import boqService from '../../api/services/boqService';
import type {
  BoqDocumentDto, BoqItemType, BoqUpdateRequest,
} from '../../api/services/boqService';
import { ConfidenceChip, formatMoney, parseUserNumber } from '../Intelligence/common';
import { categoryLabel } from './BoqListPage';

// ─── Local editable model ────────────────────────────────────────────────────
// The grid edits a local copy; Save sends the review-workbench-style upsert.

interface EditItem {
  id: number | null;
  key: string; // stable React key for new rows
  itemCode: string | null;
  description: string;
  unit: string;
  quantityText: string; // raw text so users can type freely; parsed on the fly
  itemType: BoqItemType;
  rateText: string;
  isTbd: boolean;
  source: string;
  confidence: number | null;
  assemblyCode: string | null;
  canExplode: boolean;
  evidenceNote: string | null;
}

interface EditSection {
  id: number | null;
  key: string;
  title: string;
  items: EditItem[];
}

let keyCounter = 0;
const nextKey = () => `new-${++keyCounter}`;

const TYPE_LABELS: Record<BoqItemType, string> = {
  Material: 'Material',
  Labor: 'Labor',
  Equipment: 'Equipment',
  Subcontract: 'Subcontractor',
};

const SOURCE_LABELS: Record<string, string> = {
  extracted: 'Read from the request',
  assembly: 'From your rate library',
  manual: 'Entered by you',
};

const toEditState = (doc: BoqDocumentDto): EditSection[] =>
  doc.sections.map((s) => ({
    id: s.id,
    key: `s-${s.id}`,
    title: s.title,
    items: s.items.map((i) => ({
      id: i.id,
      key: `i-${i.id}`,
      itemCode: i.itemCode,
      description: i.description,
      unit: i.unit,
      quantityText: i.isTbd && i.quantity === 0 ? '' : String(i.quantity),
      itemType: i.itemType,
      rateText: i.unitRate != null ? String(i.unitRate) : '',
      isTbd: i.isTbd,
      source: i.source,
      confidence: i.confidence != null ? Number(i.confidence) : null,
      assemblyCode: i.assemblyCode,
      canExplode: i.canExplode,
      evidenceNote: i.evidenceNote,
    })),
  }));

const itemNeedsDetails = (item: EditItem): boolean => {
  const qty = parseUserNumber(item.quantityText);
  return item.isTbd || qty == null || qty <= 0;
};

const lineTotal = (item: EditItem): number | null => {
  if (itemNeedsDetails(item)) return null;
  const qty = parseUserNumber(item.quantityText);
  const rate = parseUserNumber(item.rateText);
  if (qty == null || rate == null) return null;
  return qty * rate;
};

// ─── Page ────────────────────────────────────────────────────────────────────

const BoqEditorPage: React.FC = () => {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const boqId = Number(id);

  const { data: doc, isLoading, isError } = useQuery({
    queryKey: ['boq', boqId],
    queryFn: () => boqService.get(boqId),
    enabled: !!id && Number.isFinite(boqId),
  });

  const { data: assemblies } = useQuery({
    queryKey: ['boq-assemblies'],
    queryFn: () => boqService.assemblies(),
  });

  const [sections, setSections] = useState<EditSection[]>([]);
  const [title, setTitle] = useState('');
  const [notes, setNotes] = useState('');
  const [dirty, setDirty] = useState(false);
  const [collapsed, setCollapsed] = useState<Record<string, boolean>>({});

  useEffect(() => {
    if (doc) {
      setSections(toEditState(doc));
      setTitle(doc.title);
      setNotes(doc.notes ?? '');
      setDirty(false);
    }
  }, [doc]);

  const locked = doc?.status === 'Approved';

  // Live totals over the local edit state.
  const totals = useMemo(() => {
    let grand = 0;
    let tbd = 0;
    const bySection: Record<string, number> = {};
    for (const s of sections) {
      let sub = 0;
      for (const i of s.items) {
        if (itemNeedsDetails(i)) tbd += 1;
        const t = lineTotal(i);
        if (t != null) sub += t;
      }
      bySection[s.key] = sub;
      grand += sub;
    }
    return { grand, tbd, bySection };
  }, [sections]);

  const mutateItem = (sKey: string, iKey: string, patch: Partial<EditItem>) => {
    setDirty(true);
    setSections((prev) =>
      prev.map((s) =>
        s.key !== sKey
          ? s
          : { ...s, items: s.items.map((i) => (i.key !== iKey ? i : { ...i, ...patch })) }
      )
    );
  };

  const buildUpdatePayload = (): BoqUpdateRequest => ({
    header: { title: title.trim() || undefined, notes },
    sections: sections.map((s, sIdx) => ({
      id: s.id ?? undefined,
      seq: sIdx + 1,
      title: s.title,
      items: s.items.map((i, iIdx) => {
        const qty = parseUserNumber(i.quantityText);
        return {
          id: i.id ?? undefined,
          seq: iIdx + 1,
          itemCode: i.itemCode,
          description: i.description,
          unit: i.unit,
          quantity: qty != null && qty > 0 ? qty : 0,
          itemType: i.itemType,
          unitRate: parseUserNumber(i.rateText),
          isTbd: qty == null || qty <= 0,
          assemblyCode: i.assemblyCode,
          evidenceNote: i.evidenceNote,
        };
      }),
    })),
  });

  const saveMutation = useMutation({
    mutationFn: () => boqService.update(boqId, buildUpdatePayload()),
    onSuccess: (updated) => {
      queryClient.setQueryData(['boq', boqId], updated);
      queryClient.invalidateQueries({ queryKey: ['boq-list'] });
      enqueueSnackbar('Saved.', { variant: 'success' });
    },
    onError: (err: any) =>
      enqueueSnackbar(err?.response?.data ?? 'Could not save your changes.', { variant: 'error' }),
  });

  const approveMutation = useMutation({
    mutationFn: () => boqService.approve(boqId),
    onSuccess: (updated) => {
      queryClient.setQueryData(['boq', boqId], updated);
      queryClient.invalidateQueries({ queryKey: ['boq-list'] });
      enqueueSnackbar('BOQ approved and locked.', { variant: 'success' });
    },
    onError: (err: any) =>
      enqueueSnackbar(err?.response?.data ?? 'Could not approve this BOQ.', { variant: 'warning' }),
  });

  const explodeMutation = useMutation({
    mutationFn: (itemId: number) => boqService.explodeItem(itemId),
    onSuccess: (updated) => {
      queryClient.setQueryData(['boq', boqId], updated);
      enqueueSnackbar('Item expanded into its components with your library rates.', { variant: 'success' });
    },
    onError: (err: any) =>
      enqueueSnackbar(err?.response?.data ?? 'Could not expand this item.', { variant: 'warning' }),
  });

  if (isLoading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', py: 10 }}>
        <CircularProgress />
      </Box>
    );
  }
  if (isError || !doc) {
    return (
      <Box sx={{ p: 3 }}>
        <Alert severity="error">Couldn't load this BOQ. It may have been removed.</Alert>
      </Box>
    );
  }

  const assemblyCodes = (assemblies ?? []).map((a) => a.code);

  return (
    <Box sx={{ p: 3 }}>
      {/* Header */}
      <Breadcrumbs separator={<NextIcon fontSize="small" />} sx={{ mb: 1 }}>
        <Link
          component="button"
          underline="hover"
          color="inherit"
          onClick={() => navigate('/services/boq')}
          sx={{ display: 'flex', alignItems: 'center', gap: 0.5 }}
        >
          <BackIcon fontSize="small" /> Service BOQs
        </Link>
        <Typography color="text.primary" sx={{ fontSize: '0.85rem' }}>
          {doc.title}
        </Typography>
      </Breadcrumbs>

      <Stack direction={{ xs: 'column', md: 'row' }} spacing={2} sx={{ mb: 2, alignItems: { md: 'flex-start' } }}>
        <Box sx={{ flex: 1 }}>
          <TextField
            value={title}
            onChange={(e) => {
              setTitle(e.target.value);
              setDirty(true);
            }}
            disabled={locked}
            fullWidth
            variant="standard"
            slotProps={{ htmlInput: { style: { fontSize: '1.3rem', fontWeight: 800 } } }}
          />
          <Stack direction="row" spacing={1} sx={{ mt: 1, flexWrap: 'wrap' }} useFlexGap>
            <Chip label={categoryLabel(doc.serviceCategory)} size="small" variant="outlined" />
            <Chip
              label={doc.status === 'Approved' ? 'Approved' : doc.status === 'InReview' ? 'In review' : 'Draft'}
              color={doc.status === 'Approved' ? 'success' : doc.status === 'InReview' ? 'info' : 'default'}
              size="small"
              sx={{ fontWeight: 700 }}
            />
            {doc.overallConfidence != null && <ConfidenceChip score={Number(doc.overallConfidence)} />}
            {totals.tbd > 0 ? (
              <Chip
                label={`${totals.tbd} item${totals.tbd === 1 ? '' : 's'} need${totals.tbd === 1 ? 's' : ''} details`}
                color="warning"
                size="small"
                sx={{ fontWeight: 700 }}
              />
            ) : (
              <Chip label="All lines complete" color="success" size="small" variant="outlined" sx={{ fontWeight: 700 }} />
            )}
            {doc.leadId != null && <Chip label={`From lead #${doc.leadId}`} size="small" variant="outlined" />}
          </Stack>
        </Box>
        <Stack direction="row" spacing={1}>
          <Button startIcon={<ExportIcon />} variant="outlined" onClick={() => boqService.exportCsv(doc.id, doc.title)}>
            Export CSV
          </Button>
          {!locked && (
            <>
              <Button
                startIcon={saveMutation.isPending ? <CircularProgress size={16} color="inherit" /> : <SaveIcon />}
                variant="contained"
                disabled={!dirty || saveMutation.isPending}
                onClick={() => saveMutation.mutate()}
              >
                Save
              </Button>
              <Tooltip
                title={
                  totals.tbd > 0
                    ? 'Fill in the highlighted items first — every line needs a quantity.'
                    : dirty
                      ? 'Save your changes first.'
                      : 'Lock this BOQ as final.'
                }
              >
                <span>
                  <Button
                    startIcon={<ApproveIcon />}
                    color="success"
                    variant="outlined"
                    disabled={totals.tbd > 0 || dirty || approveMutation.isPending}
                    onClick={() => approveMutation.mutate()}
                  >
                    Approve
                  </Button>
                </span>
              </Tooltip>
            </>
          )}
        </Stack>
      </Stack>

      {locked && (
        <Alert severity="success" sx={{ mb: 2 }}>
          This BOQ was approved{doc.approvedBy ? ` by ${doc.approvedBy}` : ''} and is locked.
        </Alert>
      )}
      {doc.notes && (
        <Alert severity="info" sx={{ mb: 2 }}>
          {doc.notes}
        </Alert>
      )}

      <Stack direction={{ xs: 'column', lg: 'row' }} spacing={2} sx={{ alignItems: 'flex-start' }}>
        {/* ── Sections & items grid ── */}
        <Box sx={{ flex: 1, width: '100%' }}>
          {sections.map((section) => {
            const isCollapsed = collapsed[section.key] ?? false;
            return (
              <Paper key={section.key} variant="outlined" sx={{ mb: 2 }}>
                <Stack
                  direction="row"
                  spacing={1}
                  sx={{ px: 2, py: 1, bgcolor: 'action.hover', alignItems: 'center' }}
                >
                  <IconButton
                    size="small"
                    onClick={() => setCollapsed((p) => ({ ...p, [section.key]: !isCollapsed }))}
                  >
                    {isCollapsed ? <ExpandIcon /> : <CollapseIcon />}
                  </IconButton>
                  <TextField
                    value={section.title}
                    onChange={(e) => {
                      setDirty(true);
                      setSections((prev) =>
                        prev.map((s) => (s.key === section.key ? { ...s, title: e.target.value } : s))
                      );
                    }}
                    disabled={locked}
                    variant="standard"
                    sx={{ flex: 1 }}
                    slotProps={{ htmlInput: { style: { fontWeight: 800 } } }}
                  />
                  <Typography sx={{ fontWeight: 800, whiteSpace: 'nowrap' }}>
                    {formatMoney(totals.bySection[section.key] ?? 0)}
                  </Typography>
                  {!locked && (
                    <Tooltip title="Remove this section and its lines">
                      <IconButton
                        size="small"
                        onClick={() => {
                          setDirty(true);
                          setSections((prev) => prev.filter((s) => s.key !== section.key));
                        }}
                      >
                        <DeleteIcon fontSize="small" />
                      </IconButton>
                    </Tooltip>
                  )}
                </Stack>

                <Collapse in={!isCollapsed}>
                  {/* column headers */}
                  <Stack
                    direction="row"
                    spacing={1}
                    sx={{ px: 2, pt: 1.5, display: { xs: 'none', md: 'flex' } }}
                  >
                    <Typography variant="caption" color="text.secondary" sx={{ flex: 3, fontWeight: 700 }}>Description</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ width: 70, fontWeight: 700 }}>Unit</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ width: 90, fontWeight: 700 }}>Quantity</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ width: 130, fontWeight: 700 }}>Type</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ width: 100, fontWeight: 700 }}>Rate</Typography>
                    <Typography variant="caption" color="text.secondary" sx={{ width: 90, fontWeight: 700, textAlign: 'right' }}>Line total</Typography>
                    <Box sx={{ width: 76 }} />
                  </Stack>

                  {section.items.map((item) => {
                    const needs = itemNeedsDetails(item);
                    const total = lineTotal(item);
                    return (
                      <Box
                        key={item.key}
                        sx={{
                          px: 2,
                          py: 1,
                          borderTop: '1px solid',
                          borderColor: 'divider',
                          // TBD rows are first-class: amber highlight, plain words.
                          bgcolor: needs ? 'rgba(255, 167, 38, 0.09)' : 'transparent',
                        }}
                      >
                        <Stack direction={{ xs: 'column', md: 'row' }} spacing={1} sx={{ alignItems: { md: 'center' } }}>
                          <TextField
                            value={item.description}
                            onChange={(e) => mutateItem(section.key, item.key, { description: e.target.value })}
                            disabled={locked}
                            size="small"
                            variant="standard"
                            multiline
                            maxRows={3}
                            sx={{ flex: 3 }}
                          />
                          <TextField
                            value={item.unit}
                            onChange={(e) => mutateItem(section.key, item.key, { unit: e.target.value })}
                            disabled={locked}
                            size="small"
                            variant="standard"
                            sx={{ width: { md: 70 } }}
                          />
                          <TextField
                            value={item.quantityText}
                            onChange={(e) => mutateItem(section.key, item.key, { quantityText: e.target.value, isTbd: false })}
                            disabled={locked}
                            size="small"
                            variant="standard"
                            placeholder="?"
                            error={needs}
                            sx={{ width: { md: 90 } }}
                            slotProps={{ htmlInput: { inputMode: 'decimal', style: { textAlign: 'right' } } }}
                          />
                          <TextField
                            select
                            value={item.itemType}
                            onChange={(e) => mutateItem(section.key, item.key, { itemType: e.target.value as BoqItemType })}
                            disabled={locked}
                            size="small"
                            variant="standard"
                            sx={{ width: { md: 130 } }}
                          >
                            {(Object.keys(TYPE_LABELS) as BoqItemType[]).map((t) => (
                              <MenuItem key={t} value={t}>{TYPE_LABELS[t]}</MenuItem>
                            ))}
                          </TextField>
                          <TextField
                            value={item.rateText}
                            onChange={(e) => mutateItem(section.key, item.key, { rateText: e.target.value })}
                            disabled={locked}
                            size="small"
                            variant="standard"
                            placeholder="rate"
                            sx={{ width: { md: 100 } }}
                            slotProps={{ htmlInput: { inputMode: 'decimal', style: { textAlign: 'right' } } }}
                          />
                          <Typography sx={{ width: { md: 90 }, textAlign: 'right', fontWeight: 700, fontSize: '0.85rem' }}>
                            {total != null ? formatMoney(total) : '—'}
                          </Typography>
                          <Stack direction="row" sx={{ width: { md: 76 }, justifyContent: 'flex-end' }}>
                            {!locked && item.id != null && item.canExplode && (
                              <Tooltip
                                title={
                                  dirty
                                    ? 'Save your changes, then expand this into its components.'
                                    : `Expand into components from your rate library (${item.assemblyCode}).`
                                }
                              >
                                <span>
                                  <IconButton
                                    size="small"
                                    color="primary"
                                    disabled={dirty || explodeMutation.isPending || needs}
                                    onClick={() => explodeMutation.mutate(item.id!)}
                                  >
                                    <ExplodeIcon fontSize="small" />
                                  </IconButton>
                                </span>
                              </Tooltip>
                            )}
                            {!locked && (
                              <IconButton
                                size="small"
                                onClick={() => {
                                  setDirty(true);
                                  setSections((prev) =>
                                    prev.map((s) =>
                                      s.key !== section.key
                                        ? s
                                        : { ...s, items: s.items.filter((i) => i.key !== item.key) }
                                    )
                                  );
                                }}
                              >
                                <DeleteIcon fontSize="small" />
                              </IconButton>
                            )}
                          </Stack>
                        </Stack>

                        {/* plain-language row footnotes */}
                        <Stack direction="row" spacing={1} sx={{ mt: 0.5, flexWrap: 'wrap' }} useFlexGap>
                          {needs && (
                            <Chip label="Needs a quantity" color="warning" size="small" variant="outlined" sx={{ fontWeight: 700 }} />
                          )}
                          {item.source && SOURCE_LABELS[item.source] && (
                            <Typography variant="caption" color="text.secondary">
                              {SOURCE_LABELS[item.source]}
                            </Typography>
                          )}
                          {item.evidenceNote && (
                            <Typography variant="caption" color="text.secondary" sx={{ fontStyle: 'italic' }}>
                              {item.evidenceNote}
                            </Typography>
                          )}
                        </Stack>
                      </Box>
                    );
                  })}

                  {!locked && (
                    <Box sx={{ px: 2, py: 1, borderTop: '1px dashed', borderColor: 'divider' }}>
                      <Stack direction="row" spacing={1} sx={{ alignItems: 'center' }}>
                        <Button
                          size="small"
                          startIcon={<AddIcon />}
                          onClick={() => {
                            setDirty(true);
                            setSections((prev) =>
                              prev.map((s) =>
                                s.key !== section.key
                                  ? s
                                  : {
                                      ...s,
                                      items: [
                                        ...s.items,
                                        {
                                          id: null,
                                          key: nextKey(),
                                          itemCode: null,
                                          description: '',
                                          unit: 'EA',
                                          quantityText: '',
                                          itemType: 'Material',
                                          rateText: '',
                                          isTbd: true,
                                          source: 'manual',
                                          confidence: null,
                                          assemblyCode: null,
                                          canExplode: false,
                                          evidenceNote: null,
                                        },
                                      ],
                                    }
                              )
                            );
                          }}
                        >
                          Add a line
                        </Button>
                        {assemblyCodes.length > 0 && (
                          <TextField
                            select
                            size="small"
                            value=""
                            label="Add from rate library"
                            sx={{ minWidth: 240 }}
                            onChange={(e) => {
                              const asm = (assemblies ?? []).find((a) => a.code === e.target.value);
                              if (!asm) return;
                              setDirty(true);
                              setSections((prev) =>
                                prev.map((s) =>
                                  s.key !== section.key
                                    ? s
                                    : {
                                        ...s,
                                        items: [
                                          ...s.items,
                                          {
                                            id: null,
                                            key: nextKey(),
                                            itemCode: asm.code,
                                            description: asm.name,
                                            unit: asm.unit,
                                            quantityText: '',
                                            itemType: 'Material',
                                            rateText: '',
                                            isTbd: true,
                                            source: 'manual',
                                            confidence: null,
                                            assemblyCode: asm.code,
                                            canExplode: false,
                                            evidenceNote:
                                              'Set a quantity, save, then use the expand button to pull in components and rates.',
                                          },
                                        ],
                                      }
                                )
                              );
                            }}
                          >
                            {(assemblies ?? []).map((a) => (
                              <MenuItem key={a.code} value={a.code}>
                                {a.name} ({a.code})
                              </MenuItem>
                            ))}
                          </TextField>
                        )}
                      </Stack>
                    </Box>
                  )}
                </Collapse>
              </Paper>
            );
          })}

          {!locked && (
            <Button
              startIcon={<AddIcon />}
              variant="outlined"
              onClick={() => {
                setDirty(true);
                setSections((prev) => [
                  ...prev,
                  { id: null, key: nextKey(), title: 'New section', items: [] },
                ]);
              }}
            >
              Add a section
            </Button>
          )}
        </Box>

        {/* ── Right rail: totals, assumptions, notes ── */}
        <Stack spacing={2} sx={{ width: { xs: '100%', lg: 320 }, flexShrink: 0 }}>
          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography sx={{ fontWeight: 800, mb: 1 }}>Totals</Typography>
            {sections.map((s) => (
              <Stack key={s.key} direction="row" sx={{ py: 0.25, justifyContent: 'space-between' }}>
                <Typography variant="body2" color="text.secondary" noWrap sx={{ maxWidth: 200 }}>
                  {s.title}
                </Typography>
                <Typography variant="body2" sx={{ fontWeight: 700 }}>
                  {formatMoney(totals.bySection[s.key] ?? 0)}
                </Typography>
              </Stack>
            ))}
            <Divider sx={{ my: 1 }} />
            <Stack direction="row" sx={{ justifyContent: 'space-between' }}>
              <Typography sx={{ fontWeight: 800 }}>Priced total</Typography>
              <Typography sx={{ fontWeight: 800 }}>{formatMoney(totals.grand)}</Typography>
            </Stack>
            {totals.tbd > 0 && (
              <Typography variant="caption" color="warning.main" sx={{ display: 'block', mt: 0.5 }}>
                {totals.tbd} item{totals.tbd === 1 ? '' : 's'} without details are not included in this total.
              </Typography>
            )}
          </Paper>

          {doc.assumptions.length > 0 && (
            <Paper variant="outlined" sx={{ p: 2 }}>
              <Typography sx={{ fontWeight: 800, mb: 1 }}>Assumptions made</Typography>
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 1 }}>
                Nexora had to assume these while reading the request — please confirm them.
              </Typography>
              <Stack spacing={0.75}>
                {doc.assumptions.map((a, idx) => (
                  <Typography key={idx} variant="body2" sx={{ display: 'flex', gap: 0.75 }}>
                    <span>•</span> {a}
                  </Typography>
                ))}
              </Stack>
            </Paper>
          )}

          <Paper variant="outlined" sx={{ p: 2 }}>
            <Typography sx={{ fontWeight: 800, mb: 1 }}>Notes</Typography>
            <TextField
              value={notes}
              onChange={(e) => {
                setNotes(e.target.value);
                setDirty(true);
              }}
              disabled={locked}
              fullWidth
              multiline
              minRows={3}
              size="small"
              placeholder="Anything the estimator or approver should know…"
            />
          </Paper>
        </Stack>
      </Stack>
    </Box>
  );
};

export default BoqEditorPage;
