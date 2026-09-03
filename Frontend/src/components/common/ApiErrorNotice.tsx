import { useState } from 'react';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Collapse,
  IconButton,
  Stack,
  Tooltip,
  Typography,
} from '@mui/material';
import {
  ContentCopy as CopyIcon,
  Done as DoneIcon,
  ExpandLess as CollapseIcon,
  ExpandMore as ExpandIcon,
  Refresh as RefreshIcon,
} from '@mui/icons-material';
import {
  supportDetailText,
  toPresentableError,
  type ApiErrorContext,
  type PresentableError,
} from '../../utils/apiErrors';

interface ApiErrorNoticeProps {
  /** The raw thrown value. Mapped through `toPresentableError`; ignored when `presented` is given. */
  error?: unknown;
  /** A pre-mapped error, for callers that already resolved one. */
  presented?: PresentableError;
  /** Domain-specific wording used when the server supplies nothing safe to render. */
  fallbackMessage?: string;
  /** `'list'` when the failed request was for a list: a 404 then reads "could not be loaded". */
  context?: ApiErrorContext;
  onRetry?: () => void;
  retryLabel?: string;
  sx?: object;
}

/**
 * The single user-facing surface for a failed request.
 *
 * Backend diagnostics never reach the headline: `toPresentableError` decides what is product copy,
 * and everything else is folded into the "Technical detail (for support)" disclosure, which is
 * collapsed by default and copyable in one click.
 */
export default function ApiErrorNotice({
  error,
  presented,
  fallbackMessage,
  context,
  onRetry,
  retryLabel = 'Try again',
  sx,
}: ApiErrorNoticeProps) {
  const [expanded, setExpanded] = useState(false);
  const [copied, setCopied] = useState(false);
  const detail = presented ?? toPresentableError(error, { fallbackMessage, context });

  const copy = () => {
    void navigator.clipboard?.writeText(supportDetailText(detail)).then(
      () => {
        setCopied(true);
        window.setTimeout(() => setCopied(false), 2000);
      },
      () => setCopied(false),
    );
  };

  return (
    <Alert
      severity={detail.severity}
      role="alert"
      aria-live="polite"
      sx={sx}
      action={
        onRetry && detail.isRetryable ? (
          <Button color="inherit" size="small" startIcon={<RefreshIcon />} onClick={onRetry}>
            {retryLabel}
          </Button>
        ) : undefined
      }
    >
      <AlertTitle sx={{ fontWeight: 800 }}>{detail.title}</AlertTitle>
      <Typography variant="body2">{detail.message}</Typography>

      {detail.technicalDetail && (
        <Box sx={{ mt: 1 }}>
          <Button
            size="small"
            color="inherit"
            onClick={() => setExpanded((open) => !open)}
            endIcon={expanded ? <CollapseIcon /> : <ExpandIcon />}
            aria-expanded={expanded}
            sx={{ textTransform: 'none', fontWeight: 700, px: 0.5 }}
          >
            Technical detail (for support)
          </Button>
          <Collapse in={expanded} unmountOnExit>
            <Stack
              direction="row"
              spacing={1}
              sx={{ mt: 0.5, alignItems: 'flex-start', justifyContent: 'space-between' }}
            >
              <Typography
                variant="caption"
                component="pre"
                sx={{
                  flex: 1,
                  m: 0,
                  p: 1,
                  borderRadius: 1,
                  bgcolor: 'action.hover',
                  fontFamily: 'monospace',
                  whiteSpace: 'pre-wrap',
                  overflowWrap: 'anywhere',
                }}
              >
                {detail.technicalDetail}
              </Typography>
              <Tooltip title={copied ? 'Copied' : 'Copy for support'}>
                <IconButton size="small" onClick={copy} aria-label="Copy technical detail for support">
                  {copied ? <DoneIcon fontSize="small" /> : <CopyIcon fontSize="small" />}
                </IconButton>
              </Tooltip>
            </Stack>
          </Collapse>
        </Box>
      )}
    </Alert>
  );
}
