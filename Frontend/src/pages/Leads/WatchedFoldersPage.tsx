import React, { useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Link as RouterLink } from 'react-router-dom';
import {
  Alert,
  AlertTitle,
  Box,
  Button,
  Chip,
  CircularProgress,
  Divider,
  Grid,
  Link,
  List,
  ListItem,
  ListItemText,
  Paper,
  Stack,
  Typography,
} from '@mui/material';
import {
  FolderShared as FolderIcon,
  HelpOutlined as UnknownIcon,
  Info as InfoIcon,
  OpenInNew as OpenIcon,
  PlayCircleOutlined as SweepIcon,
  UploadFile as UploadIcon,
} from '@mui/icons-material';
import dayjs from 'dayjs';
import { useSnackbar } from 'notistack';
import leadService, {
  type FolderSweepReportDTO,
  type LeadResponseDTO,
} from '../../api/services/leadService';
import { useAuth } from '../../context/AuthContext';
import ApiErrorNotice from '../../components/common/ApiErrorNotice';
import { presentableErrorMessage } from '../../utils/apiErrors';

/**
 * The watched-folder intake channel, made visible.
 *
 * Nexora runs on the customer's own server and sweeps directories on that host — which may be a
 * mounted network share fed by a portal export (Etimad, SAP Ariba, Oracle iSupplier). The sweep is
 * real (Backend/ERP_RFQ_Automation/Services/FolderService.cs, driven from EmailBackgroundService),
 * but until now nothing in the product admitted the channel existed.
 *
 * EVERY VALUE ON THIS PAGE COMES FROM AN ENDPOINT THAT ALREADY EXISTS:
 *   - GET  /api/Lead?leadSource=…            inquiries that actually arrived through each folder
 *   - POST /api/Email/process-all-folder-leads  run the sweep now, real per-run counts
 *   - POST /api/Email/upload-leads-folder    place documents into a watched folder
 *
 * What the API does NOT publish is stated as "not available" and never guessed:
 *   - the time of the last AUTOMATIC sweep (the background service only logs it), and
 *   - the documents sitting in a folder right now, before a sweep has claimed them.
 * See the "What this screen cannot show yet" panel below.
 */

interface WatchedFolderSpec {
  /** `folderType` accepted by FolderService.GetTenantFolderPath / the upload endpoint. */
  key: 'Shared' | 'SEC' | 'Aramco';
  title: string;
  purpose: string;
  /** Exact `LeadSource` the sweep stamps on every inquiry from this folder. */
  leadSource: string;
  /** Extensions this door accepts (FolderService.IsAllowedUploadExtension). */
  extensions: string[];
}

/**
 * The three doors the backend actually implements. FolderService rejects any other folder type,
 * and each door's accepted extensions are deliberately narrow — a file the folder does not accept
 * is quarantined by the sweep rather than ingested.
 */
const WATCHED_FOLDERS: WatchedFolderSpec[] = [
  {
    key: 'Shared',
    title: 'Shared folder',
    purpose:
      'The general door. Use it for RFQ documents from any customer, and for scheduled exports dropped here by a portal or another system.',
    leadSource: 'Shared Leads',
    extensions: ['.pdf', '.doc', '.docx', '.xls', '.xlsx'],
  },
  {
    key: 'SEC',
    title: 'SEC folder',
    purpose:
      'A dedicated door for one customer that sends legacy Word documents. Anything that is not a .doc file is quarantined instead of ingested.',
    leadSource: 'SEC Leads',
    extensions: ['.doc'],
  },
  {
    key: 'Aramco',
    title: 'Aramco folder',
    purpose:
      'A dedicated door for Aramco RFP documents. Anything that is not a .docx file is quarantined instead of ingested.',
    leadSource: 'Aramco Leads',
    extensions: ['.docx'],
  },
];

/** Backend limit: FolderService rejects anything larger (MAX_ATTACHMENT_SIZE). */
const MAX_FILE_BYTES = 25 * 1024 * 1024;
const RECENT_LIMIT = 5;

const extensionOf = (name: string): string => {
  const separator = name.lastIndexOf('.');
  return separator >= 0 ? name.slice(separator).toLowerCase() : '';
};

/**
 * When this inquiry entered Nexora. `ingestedOn` is the server's own ingestion audit value and is
 * preferred; the other two are fallbacks for older rows. Returns null rather than a placeholder
 * date so the caller can say "not recorded" instead of showing something invented.
 */
const enteredOn = (lead: LeadResponseDTO): string | null => {
  const candidate = lead.ingestedOn || lead.ingestedAtUtc || lead.createdDate || null;
  return candidate && dayjs(candidate).isValid() ? candidate : null;
};

const timestampLabel = (value: string): string => {
  const zone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'local time';
  return `${dayjs(value).format('DD MMM YYYY, HH:mm')} (${zone})`;
};

interface FolderCardProps {
  spec: WatchedFolderSpec;
  businessUnitId?: number;
  canAddDocuments: boolean;
}

const FolderCard: React.FC<FolderCardProps> = ({ spec, businessUnitId, canAddDocuments }) => {
  const { enqueueSnackbar } = useSnackbar();
  const inputRef = useRef<HTMLInputElement>(null);
  const [selectionError, setSelectionError] = useState<string | null>(null);

  const { data, isLoading, isError, error, refetch } = useQuery({
    queryKey: ['watched-folder-leads', spec.leadSource],
    queryFn: () =>
      leadService.getAll({ leadSource: spec.leadSource, pageNumber: 1, pageSize: RECENT_LIMIT }),
  });

  const uploadMutation = useMutation({
    mutationFn: (formData: FormData) => leadService.uploadToFolder(formData, spec.key),
    onSuccess: (_result, formData) => {
      const count = formData.getAll('files').length;
      enqueueSnackbar(
        `${count} document${count === 1 ? '' : 's'} placed in the ${spec.title.toLowerCase()}. `
        + 'Nothing is read until the folder is swept — press "Sweep now" above, or wait for the next automatic sweep.',
        { variant: 'success' },
      );
    },
    onError: (mutationError: unknown) => {
      enqueueSnackbar(
        presentableErrorMessage(
          mutationError,
          `The documents could not be placed in the ${spec.title.toLowerCase()}. Nothing was written.`,
        ),
        { variant: 'error' },
      );
    },
  });

  const handleFiles = (incoming: File[]) => {
    if (incoming.length === 0) return;
    const unsupported = incoming.filter((file) => !spec.extensions.includes(extensionOf(file.name)));
    const oversized = incoming.filter((file) => file.size > MAX_FILE_BYTES);
    if (unsupported.length > 0 || oversized.length > 0) {
      const reasons = [
        unsupported.length > 0
          ? `this folder only accepts ${spec.extensions.join(', ')}`
          : null,
        oversized.length > 0
          ? `${oversized.length} file${oversized.length === 1 ? ' is' : 's are'} over 25 MB`
          : null,
      ].filter(Boolean);
      setSelectionError(`Nothing was sent: ${reasons.join('; ')}.`);
      return;
    }
    setSelectionError(null);
    const formData = new FormData();
    incoming.forEach((file) => formData.append('files', file));
    uploadMutation.mutate(formData);
  };

  const leads = data?.items ?? [];
  const total = data?.totalCount ?? 0;
  const location = `Tenants/${businessUnitId ?? '<your business unit id>'}/Watched/${spec.key}`;

  return (
    <Paper
      sx={{
        p: 3,
        borderRadius: 3,
        border: '1px solid',
        borderColor: 'divider',
        height: '100%',
        display: 'flex',
        flexDirection: 'column',
      }}
    >
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 1 }}>
        <FolderIcon color="primary" />
        <Typography variant="h6" sx={{ fontWeight: 800 }}>{spec.title}</Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
        {spec.purpose}
      </Typography>

      <Typography
        sx={{ fontWeight: 800, fontSize: '0.72rem', textTransform: 'uppercase', letterSpacing: '0.04em', color: 'text.secondary' }}
      >
        Where it is on this server
      </Typography>
      <Box
        component="code"
        sx={{
          display: 'block',
          mt: 0.5,
          mb: 0.5,
          p: 1,
          borderRadius: 1,
          bgcolor: 'action.hover',
          fontSize: '0.78rem',
          overflowWrap: 'anywhere',
        }}
      >
        {location}
      </Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mb: 2 }}>
        Relative to the storage root your administrator configured for this installation
        (<Box component="code" sx={{ fontSize: '0.72rem' }}>Storage:RootPath</Box>). Nexora does not
        publish the absolute path to the browser — ask whoever installed the server, or map a
        network share onto this directory.
      </Typography>

      <Typography
        sx={{ fontWeight: 800, fontSize: '0.72rem', textTransform: 'uppercase', letterSpacing: '0.04em', color: 'text.secondary', mb: 0.75 }}
      >
        Accepted file types
      </Typography>
      <Stack direction="row" spacing={0.75} sx={{ flexWrap: 'wrap', gap: 0.75, mb: 2 }}>
        {spec.extensions.map((extension) => (
          <Chip key={extension} label={extension} size="small" variant="outlined" />
        ))}
      </Stack>

      <Divider sx={{ mb: 2 }} />

      <Typography
        sx={{ fontWeight: 800, fontSize: '0.72rem', textTransform: 'uppercase', letterSpacing: '0.04em', color: 'text.secondary', mb: 1 }}
      >
        Inquiries picked up from this folder
      </Typography>

      <Box sx={{ flexGrow: 1 }}>
        {isLoading && (
          <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', py: 2 }}>
            <CircularProgress size={18} />
            <Typography variant="body2" color="text.secondary">Checking…</Typography>
          </Stack>
        )}

        {isError && (
          <ApiErrorNotice
            error={error}
            fallbackMessage={`Nexora could not read the inquiries that came from the ${spec.title.toLowerCase()}. The folder itself is unaffected.`}
            onRetry={() => { void refetch(); }}
            sx={{ mb: 2 }}
          />
        )}

        {!isLoading && !isError && total === 0 && (
          <Alert severity="info" icon={<InfoIcon fontSize="inherit" />} sx={{ mb: 2 }}>
            <Typography variant="body2" sx={{ fontWeight: 700 }}>
              Nothing has come through this folder yet.
            </Typography>
            <Typography variant="caption" sx={{ display: 'block' }}>
              Put a {spec.extensions.join(' or ')} document into the directory above — by hand, from
              a network share, or from a scheduled portal export — then press &quot;Sweep now&quot;.
              If you cannot reach that directory, use &quot;Add documents&quot; below.
            </Typography>
          </Alert>
        )}

        {!isLoading && !isError && total > 0 && (
          <>
            <Typography variant="body2" sx={{ fontWeight: 700, mb: 1 }}>
              {total} inquir{total === 1 ? 'y' : 'ies'} created from this folder so far
              {total > leads.length && ` — the ${leads.length} most recent are listed here`}
            </Typography>
            <List dense disablePadding>
              {leads.map((lead) => {
                const entered = enteredOn(lead);
                return (
                  <ListItem key={lead.id} disableGutters divider>
                    <ListItemText
                      primary={(
                        <Link
                          component={RouterLink}
                          to={`/procurement/leads/view/${lead.id}`}
                          sx={{ fontWeight: 700, fontSize: '0.85rem' }}
                        >
                          {lead.nexoraSerial || lead.rfqno || `Inquiry ${lead.id}`}
                        </Link>
                      )}
                      secondary={(
                        <>
                          {lead.buyersName ? `${lead.buyersName} · ` : ''}
                          {entered ? `entered Nexora ${timestampLabel(entered)}` : 'arrival time not recorded'}
                        </>
                      )}
                      slotProps={{ secondary: { sx: { fontSize: '0.75rem' } } }}
                    />
                  </ListItem>
                );
              })}
            </List>
            {total > leads.length && (
              <>
                <Button
                  component={RouterLink}
                  to="/procurement/leads/all"
                  size="small"
                  endIcon={<OpenIcon fontSize="small" />}
                  sx={{ mt: 1, textTransform: 'none', fontWeight: 700 }}
                >
                  Open All Inquiries
                </Button>
                <Typography variant="caption" color="text.secondary" sx={{ display: 'block' }}>
                  All Inquiries has no folder filter yet — the &quot;Source&quot; column shows
                  &quot;{spec.leadSource}&quot; for documents that came from here.
                </Typography>
              </>
            )}
          </>
        )}
      </Box>

      {selectionError && (
        <Alert severity="warning" sx={{ mt: 2 }}>{selectionError}</Alert>
      )}

      <Box sx={{ mt: 2 }}>
        <input
          ref={inputRef}
          type="file"
          multiple
          hidden
          accept={spec.extensions.join(',')}
          onChange={(event) => {
            handleFiles(Array.from(event.target.files ?? []));
            event.target.value = '';
          }}
          disabled={!canAddDocuments || uploadMutation.isPending}
        />
        <Button
          fullWidth
          variant="outlined"
          startIcon={uploadMutation.isPending ? <CircularProgress size={16} /> : <UploadIcon />}
          disabled={!canAddDocuments || uploadMutation.isPending}
          onClick={() => inputRef.current?.click()}
          sx={{ textTransform: 'none', fontWeight: 700, borderRadius: 2 }}
        >
          {uploadMutation.isPending ? 'Placing documents…' : 'Add documents to this folder'}
        </Button>
        <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.75 }}>
          {canAddDocuments
            ? 'Writes the files straight into the watched folder. They are read on the next sweep, exactly as if they had been copied there on the server.'
            : 'You need the "create leads" permission to place documents in a watched folder.'}
        </Typography>
      </Box>
    </Paper>
  );
};

const WatchedFoldersPage: React.FC = () => {
  const { hasPermission, userData } = useAuth();
  const { enqueueSnackbar } = useSnackbar();
  const queryClient = useQueryClient();
  const canRunSweep = hasPermission('Leads', 'create');
  const [report, setReport] = useState<FolderSweepReportDTO | null>(null);

  const sweepMutation = useMutation({
    mutationFn: () => leadService.processAllFolderLeads(),
    onSuccess: (result) => {
      setReport(result);
      void queryClient.invalidateQueries({ queryKey: ['watched-folder-leads'] });
      const picked = result.enqueued + result.duplicates;
      enqueueSnackbar(
        picked > 0
          ? `Sweep finished: ${picked} document${picked === 1 ? '' : 's'} picked up.`
          : 'Sweep finished: the watched folders held nothing to process.',
        { variant: picked > 0 ? 'success' : 'info' },
      );
    },
    onError: (error: unknown) => {
      setReport(null);
      enqueueSnackbar(
        presentableErrorMessage(error, 'The sweep could not be run. Nothing in the watched folders was changed.'),
        { variant: 'error' },
      );
    },
  });

  const picked = report ? report.enqueued + report.duplicates : 0;

  return (
    <Box sx={{ p: 3, maxWidth: 1280, mx: 'auto' }}>
      <Stack direction="row" spacing={1.5} sx={{ alignItems: 'center', mb: 0.5 }}>
        <FolderIcon color="primary" />
        <Typography variant="h4" sx={{ fontWeight: 900, letterSpacing: '-0.02em' }}>
          Watched folders
        </Typography>
      </Stack>
      <Typography variant="body2" color="text.secondary" sx={{ mb: 3, maxWidth: 820 }}>
        A watched folder is an ordinary directory on the server that runs Nexora — including a
        network share mounted onto that server. Anything dropped into it (by a person, a scheduled
        customer-portal export such as Etimad, SAP Ariba or Oracle iSupplier, or another system)
        is picked up on the next sweep, turned into an inquiry, and moved into a
        &quot;Processed&quot; directory so the same document is never read twice. This is a third
        intake channel alongside email and manual upload — no mailbox and no upload required.
      </Typography>

      <Paper sx={{ p: 3, borderRadius: 3, border: '1px solid', borderColor: 'divider', mb: 3 }}>
        <Grid container spacing={3} sx={{ alignItems: 'flex-start' }}>
          <Grid size={{ xs: 12, md: 7 }}>
            <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 1.5 }}>
              What this screen cannot show yet
            </Typography>
            <Stack spacing={1.5}>
              <Stack direction="row" spacing={1.5} sx={{ alignItems: 'flex-start' }}>
                <UnknownIcon fontSize="small" color="disabled" sx={{ mt: 0.25 }} />
                <Box>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                    Last automatic sweep — not available
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    The server sweeps these folders on its own background cycle, but it records the
                    result only in the server log. No endpoint publishes when it last ran, so this
                    screen will not put a time here it cannot prove. Run a sweep below to see a real
                    result now.
                  </Typography>
                </Box>
              </Stack>
              <Stack direction="row" spacing={1.5} sx={{ alignItems: 'flex-start' }}>
                <UnknownIcon fontSize="small" color="disabled" sx={{ mt: 0.25 }} />
                <Box>
                  <Typography variant="body2" sx={{ fontWeight: 700 }}>
                    Documents waiting in a folder right now — not available
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    Nothing in the API lists the contents of a watched folder, so a document that
                    has arrived but has not yet been swept is invisible here. What each folder has
                    already produced is shown below, and is real.
                  </Typography>
                </Box>
              </Stack>
            </Stack>
          </Grid>

          <Grid size={{ xs: 12, md: 5 }}>
            <Typography sx={{ fontWeight: 900, fontSize: '0.85rem', textTransform: 'uppercase', letterSpacing: '0.03em', color: 'text.secondary', mb: 1.5 }}>
              Sweep on demand
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              Runs the same sweep the server runs automatically, across all three folders, and
              reports exactly what it found.
            </Typography>
            <Button
              variant="contained"
              startIcon={sweepMutation.isPending ? <CircularProgress size={18} color="inherit" /> : <SweepIcon />}
              disabled={!canRunSweep || sweepMutation.isPending}
              onClick={() => sweepMutation.mutate()}
              sx={{ fontWeight: 800, borderRadius: 2, px: 3, textTransform: 'none' }}
            >
              {sweepMutation.isPending ? 'Sweeping…' : 'Sweep now'}
            </Button>
            {!canRunSweep && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 1 }}>
                You need the &quot;create leads&quot; permission to run a sweep. Ask an
                administrator, or wait for the automatic sweep.
              </Typography>
            )}
          </Grid>
        </Grid>

        {report && (
          <Alert
            severity={report.rejected > 0 || report.failed > 0 ? 'warning' : picked > 0 ? 'success' : 'info'}
            sx={{ mt: 3 }}
            action={picked > 0 ? (
              <Button
                component={RouterLink}
                to={`/procurement/leads/ingestion/${encodeURIComponent(report.batchId)}`}
                color="inherit"
                size="small"
                endIcon={<OpenIcon fontSize="small" />}
                sx={{ textTransform: 'none', fontWeight: 700 }}
              >
                See what it picked up
              </Button>
            ) : undefined}
          >
            <AlertTitle sx={{ fontWeight: 800 }}>
              {picked > 0
                ? `Sweep picked up ${picked} document${picked === 1 ? '' : 's'}`
                : 'Sweep ran — the folders held nothing to process'}
            </AlertTitle>
            <Typography variant="body2">
              {report.enqueued} new document{report.enqueued === 1 ? '' : 's'} queued for extraction
              · {report.duplicates} already ingested before
              · {report.rejected} quarantined
              · {report.failed} left in place to retry on the next sweep.
            </Typography>
            {picked === 0 && report.rejected === 0 && report.failed === 0 && (
              <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
                Every watched folder was empty. Put a document into one of the directories below and
                sweep again.
              </Typography>
            )}
            {report.rejected > 0 && (
              <Typography variant="caption" sx={{ display: 'block', mt: 0.5 }}>
                A quarantined document was not ingested — it was the wrong file type for that
                folder, empty, over 25 MB, or it failed to stage three times. It has been moved out
                of the watched folder on the server.
              </Typography>
            )}
            <Typography variant="caption" color="text.secondary" sx={{ display: 'block', mt: 0.5 }}>
              An extracted document becomes an inquiry once the extraction queue reaches it, so the
              lists below may take a moment to catch up.
            </Typography>
          </Alert>
        )}
      </Paper>

      <Grid container spacing={3} sx={{ alignItems: 'stretch' }}>
        {WATCHED_FOLDERS.map((spec) => (
          <Grid size={{ xs: 12, md: 4 }} key={spec.key}>
            <FolderCard
              spec={spec}
              businessUnitId={userData?.businessUnitId}
              canAddDocuments={canRunSweep}
            />
          </Grid>
        ))}
      </Grid>
    </Box>
  );
};

export default WatchedFoldersPage;
