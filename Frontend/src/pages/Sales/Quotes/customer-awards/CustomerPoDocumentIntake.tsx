import React from 'react';
import { useMutation } from '@tanstack/react-query';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Paper,
  Stack,
  Table,
  TableBody,
  TableCell,
  TableContainer,
  TableHead,
  TableRow,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  UploadFile as UploadIcon,
  PlaylistAddCheck as ApplyIcon,
  WarningAmber as ReviewIcon,
  Description as DocumentIcon,
} from '@mui/icons-material';
import customerAwardService, {
  type CustomerPurchaseOrderDocumentExtraction,
} from '../../../../api/services/customerAwardService';

const ACCEPTED = '.pdf,.docx,.doc,.xlsx,.xlsm,.xls,.csv';
const MAX_BYTES = 25 * 1024 * 1024;

export interface CustomerPoDocumentIntakeProps {
  disabled?: boolean;
  /** Called when the reviewer accepts what was read out of the document. */
  onApply: (extraction: CustomerPurchaseOrderDocumentExtraction) => void;
}

interface IntakeFailure {
  detail: string;
  code?: string;
}

const readFailure = (error: unknown): IntakeFailure => {
  const data = (error as { response?: { data?: unknown } })?.response?.data;
  if (data && typeof data === 'object') {
    const problem = data as { detail?: string; message?: string; title?: string; code?: string };
    return {
      detail: problem.detail || problem.message || problem.title
        || 'The purchase-order document could not be read.',
      code: problem.code,
    };
  }
  if (typeof data === 'string' && data.trim()) return { detail: data };
  return {
    detail: error instanceof Error ? error.message : 'The purchase-order document could not be read.',
  };
};

const shownValue = (value: number | null | undefined, raw: string | null | undefined) => {
  if (value !== null && value !== undefined) return String(value);
  // Never render a substituted number in place of one the document did not state. Showing the
  // buyer's own unreadable text is what lets a reviewer decide; showing "0" is what hides it.
  return raw ? `"${raw}" — not read` : 'Not stated';
};

/**
 * FR-COM-01. Read the customer's purchase order instead of retyping it.
 *
 * Everything shown here comes out of the BUYER's file. The panel deliberately shows what could
 * NOT be read as prominently as what could, because a purchase order whose quantity or price
 * quietly defaulted is a purchase order that agrees with our quotation for no reason.
 */
const CustomerPoDocumentIntake: React.FC<CustomerPoDocumentIntakeProps> = ({ disabled, onApply }) => {
  const inputRef = React.useRef<HTMLInputElement | null>(null);
  const [extraction, setExtraction] = React.useState<CustomerPurchaseOrderDocumentExtraction | null>(null);
  const [failure, setFailure] = React.useState<IntakeFailure | null>(null);
  const [applied, setApplied] = React.useState(false);

  const uploadMutation = useMutation({
    mutationFn: (file: File) => customerAwardService.extractPurchaseOrderDocument(file),
    onSuccess: (result) => {
      setExtraction(result);
      setFailure(null);
      setApplied(false);
    },
    onError: (error) => {
      setExtraction(null);
      setFailure(readFailure(error));
    },
  });

  const choose = (event: React.ChangeEvent<HTMLInputElement>) => {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    if (file.size === 0 || file.size > MAX_BYTES) {
      setExtraction(null);
      setFailure({ detail: 'The purchase-order document must be between 1 byte and 25 MB.' });
      return;
    }
    uploadMutation.mutate(file);
  };

  const apply = () => {
    if (!extraction) return;
    onApply(extraction);
    setApplied(true);
  };

  const busy = disabled || uploadMutation.isPending;

  return (
    <Paper variant="outlined" sx={{ p: 2 }}>
      <Stack spacing={2}>
        <Stack
          direction={{ xs: 'column', sm: 'row' }}
          spacing={1.5}
          sx={{ justifyContent: 'space-between', alignItems: { sm: 'center' } }}
        >
          <Box>
            <Typography variant="subtitle1" sx={{ fontWeight: 800 }}>
              Read the customer&apos;s purchase order
            </Typography>
            <Typography variant="body2" color="text.secondary">
              PDF (including scans), Word, Excel or CSV, up to 25 MB. Values are taken from the
              customer&apos;s document, never from our quotation.
            </Typography>
          </Box>
          <Button
            variant="outlined"
            startIcon={uploadMutation.isPending ? <CircularProgress size={17} /> : <UploadIcon />}
            disabled={busy}
            onClick={() => inputRef.current?.click()}
            sx={{ whiteSpace: 'nowrap' }}
          >
            {uploadMutation.isPending ? 'Reading document' : 'Upload PO document'}
          </Button>
          <input
            ref={inputRef}
            type="file"
            accept={ACCEPTED}
            hidden
            aria-label="Customer purchase-order document"
            onChange={choose}
          />
        </Stack>

        {failure && (
          <Alert severity="error">
            <AlertTitle>The purchase order was not read</AlertTitle>
            {failure.detail}
            {failure.code && (
              <Typography variant="caption" sx={{ display: 'block', mt: 0.5, opacity: 0.8 }}>
                Reference: {failure.code}
              </Typography>
            )}
          </Alert>
        )}

        {extraction && (
          <Stack spacing={1.5}>
            <Stack direction="row" spacing={1} sx={{ flexWrap: 'wrap', alignItems: 'center' }}>
              <Chip size="small" icon={<DocumentIcon />} label={extraction.fileName} variant="outlined" />
              <Chip
                size="small"
                label={`PO number ${extraction.externalPoNumber ?? 'not found'}`}
                color={extraction.externalPoNumber ? 'success' : 'warning'}
                variant="outlined"
              />
              <Chip
                size="small"
                label={`PO date ${extraction.poDate ? extraction.poDate.slice(0, 10) : 'not found'}`}
                color={extraction.poDate ? 'success' : 'warning'}
                variant="outlined"
              />
              <Chip size="small" label={`${extraction.lines.length} line(s)`} variant="outlined" />
              {extraction.ocrStatus !== 'NotRequired' && (
                <Tooltip title="Read from a scan by OCR — lower confidence than a native text layer.">
                  <Chip size="small" color="info" label={`OCR ${extraction.ocrStatus}`} variant="outlined" />
                </Tooltip>
              )}
            </Stack>

            {extraction.reviewReasons.length > 0 && (
              <Alert severity="warning">
                <AlertTitle>Confirm these before saving</AlertTitle>
                <ul style={{ margin: 0, paddingInlineStart: '1.1rem' }}>
                  {extraction.reviewReasons.map((reason) => <li key={reason}>{reason}</li>)}
                </ul>
              </Alert>
            )}

            <TableContainer sx={{ border: '1px solid', borderColor: 'divider', borderRadius: 1, overflowX: 'auto' }}>
              <Table size="small" sx={{ minWidth: 880 }}>
                <TableHead>
                  <TableRow sx={{ bgcolor: 'action.hover' }}>
                    <TableCell sx={{ fontWeight: 800, width: 60 }}>#</TableCell>
                    <TableCell sx={{ fontWeight: 800, minWidth: 240 }}>Description in the PO</TableCell>
                    <TableCell sx={{ fontWeight: 800, minWidth: 150 }}>Buyer identity</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 800, width: 130 }}>PO quantity</TableCell>
                    <TableCell align="right" sx={{ fontWeight: 800, width: 140 }}>PO unit price</TableCell>
                    <TableCell sx={{ fontWeight: 800, width: 90 }}>Read</TableCell>
                  </TableRow>
                </TableHead>
                <TableBody>
                  {extraction.lines.map((line) => (
                    <TableRow key={line.lineNumber}>
                      <TableCell>{line.lineNumber}</TableCell>
                      <TableCell>
                        <Typography variant="body2">{line.description ?? 'Not stated'}</Typography>
                        <Typography variant="caption" color="text.secondary">{line.sourceAddress}</Typography>
                      </TableCell>
                      <TableCell>
                        <Typography variant="caption" sx={{ display: 'block' }}>
                          {line.customerItemCode ? `Item ${line.customerItemCode}` : '—'}
                        </Typography>
                        <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                          {[line.manufacturerName, line.manufacturerPartNumber].filter(Boolean).join(' · ') || '—'}
                        </Typography>
                      </TableCell>
                      <TableCell align="right">
                        {shownValue(line.orderedQuantity, line.quantityText)}
                        {line.unitOfMeasure && (
                          <Typography variant="caption" color="text.secondary"> {line.unitOfMeasure}</Typography>
                        )}
                      </TableCell>
                      <TableCell align="right">{shownValue(line.unitPrice, line.unitPriceText)}</TableCell>
                      <TableCell>
                        {line.requiresReview ? (
                          <Tooltip title={line.reviewReasons.join(' ')}>
                            <Chip size="small" color="warning" icon={<ReviewIcon />} label="Check" />
                          </Tooltip>
                        ) : (
                          <Chip size="small" color="success" label="Clear" variant="outlined" />
                        )}
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </TableContainer>

            <Stack direction="row" spacing={1} sx={{ justifyContent: 'flex-end', alignItems: 'center' }}>
              {applied && (
                <Typography variant="caption" color="success.main">
                  Applied to the capture form below — match each line to its quote line, then save.
                </Typography>
              )}
              <Button
                variant="contained"
                startIcon={<ApplyIcon />}
                disabled={busy || extraction.lines.length === 0}
                onClick={apply}
              >
                Use these values
              </Button>
            </Stack>
          </Stack>
        )}
      </Stack>
    </Paper>
  );
};

export default CustomerPoDocumentIntake;
