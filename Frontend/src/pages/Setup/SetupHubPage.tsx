import React, { useMemo, useState } from 'react';
import { Link as RouterLink } from 'react-router-dom';
import {
  Box,
  Card,
  CardActionArea,
  Chip,
  Divider,
  InputAdornment,
  Link,
  Paper,
  TextField,
  Typography,
} from '@mui/material';
import { alpha } from '@mui/material/styles';
import {
  Search as SearchIcon,
  ChevronRight as ChevronIcon,
  TravelExplore as NoResultIcon,
  LockOutlined as LockedIcon,
} from '@mui/icons-material';
import { useTranslation } from 'react-i18next';
import { useAuth } from '../../context/AuthContext';
import {
  SETUP_GROUPS,
  entryMatches,
  setupEntryLabel,
  type SetupEntry,
  type SetupGroup,
} from './setupCatalog';

/**
 * Setup Master's front door.
 *
 * It replaces a fourteen-item sidebar list that had outgrown the rail: the list gave every screen
 * the same weight, said nothing about what any of them did, and pushed everything below it out of
 * reach. Here the same fourteen screens are grouped by the question they answer, described in a
 * sentence each, and searchable — so the sidebar can carry one entry and this page carries the rest.
 */

const SetupCard: React.FC<{ entry: SetupEntry; groupLabel?: string }> = ({ entry, groupLabel }) => {
  const { t } = useTranslation();
  return (
  <Card
    variant="outlined"
    sx={{
      height: '100%',
      borderRadius: 3,
      display: 'flex',
      flexDirection: 'column',
      transition: 'border-color .2s, box-shadow .2s, transform .2s',
      '&:hover': {
        borderColor: 'primary.main',
        transform: 'translateY(-2px)',
        boxShadow: (theme) => `0 10px 24px ${alpha(theme.palette.common.black, 0.06)}`,
      },
      '&:focus-within': {
        borderColor: 'primary.main',
      },
    }}
  >
    <CardActionArea
      component={RouterLink}
      to={entry.path}
      // The card is one link; `seeAlso` sits outside this area because a link inside a link is
      // neither valid markup nor reachable by keyboard.
      sx={{
        flex: 1,
        p: 2.25,
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'flex-start',
        // Cards in a row stretch to the tallest. CardActionArea centres its content by default,
        // which left every title floating at a different height along the row.
        justifyContent: 'flex-start',
        '&:focus-visible': { outline: (theme) => `3px solid ${theme.palette.primary.main}`, outlineOffset: -3 },
      }}
    >
      {/* Search results are one flat grid, so each card names the group it came from — otherwise a
          result has lost the context the section heading was carrying. */}
      {groupLabel && (
        <Typography
          variant="caption"
          sx={{ display: 'block', mb: 0.75, fontWeight: 700, letterSpacing: '0.06em', textTransform: 'uppercase', color: 'text.disabled' }}
        >
          {groupLabel}
        </Typography>
      )}
      <Box sx={{ display: 'flex', alignItems: 'center', gap: 1.5, width: '100%', mb: 1.25 }}>
        <Box
          aria-hidden
          sx={{
            width: 38,
            height: 38,
            borderRadius: 2,
            display: 'grid',
            placeItems: 'center',
            flexShrink: 0,
            color: 'primary.main',
            backgroundColor: (theme) => alpha(theme.palette.primary.main, theme.palette.mode === 'dark' ? 0.18 : 0.1),
            '& .MuiSvgIcon-root': { fontSize: 20 },
          }}
        >
          {entry.icon}
        </Box>
        <Typography variant="subtitle1" sx={{ fontWeight: 700, lineHeight: 1.25, flex: 1, minWidth: 0 }}>
          {setupEntryLabel(entry, t)}
        </Typography>
        <ChevronIcon sx={{ fontSize: 18, color: 'text.disabled' }} aria-hidden />
      </Box>
      <Typography variant="body2" color="text.secondary" sx={{ lineHeight: 1.55 }}>
        {entry.description}
      </Typography>
    </CardActionArea>

    {entry.seeAlso && (
      <>
        <Divider sx={{ opacity: 0.6 }} />
        <Box sx={{ px: 2.25, py: 1.25 }}>
          <Typography variant="caption" color="text.secondary" sx={{ display: 'block', lineHeight: 1.5 }}>
            Not to be confused with{' '}
            <Link component={RouterLink} to={entry.seeAlso.path} sx={{ fontWeight: 600 }}>
              {entry.seeAlso.label}
            </Link>
            , which {entry.seeAlso.note}.
          </Typography>
        </Box>
      </>
    )}
  </Card>
  );
};

/** A responsive card grid — three across on a desk, one on a phone. */
const CardGrid: React.FC<{ entries: SetupEntry[]; groupLabels?: Record<string, string> }> = ({
  entries,
  groupLabels,
}) => (
  <Box
    sx={{
      display: 'grid',
      gap: 2,
      gridTemplateColumns: {
        xs: '1fr',
        sm: 'repeat(2, minmax(0, 1fr))',
        lg: 'repeat(3, minmax(0, 1fr))',
      },
    }}
  >
    {entries.map((entry) => (
      <SetupCard key={entry.key} entry={entry} groupLabel={groupLabels?.[entry.key]} />
    ))}
  </Box>
);

const SetupHubPage: React.FC = () => {
  const { hasPermission } = useAuth();
  const [query, setQuery] = useState('');

  const permitted = useMemo(
    () => (entry: SetupEntry) => !entry.moduleName || hasPermission(entry.moduleName),
    [hasPermission],
  );

  /** Groups with nothing this user may open disappear entirely — an empty section is noise. */
  const visibleGroups: SetupGroup[] = useMemo(
    () =>
      SETUP_GROUPS.map((group) => ({
        ...group,
        entries: group.entries.filter((entry) => permitted(entry) && entryMatches(entry, query)),
      })).filter((group) => group.entries.length > 0),
    [permitted, query],
  );

  const ownedCount = useMemo(
    () => SETUP_GROUPS.flatMap((group) => group.entries).filter(permitted).length,
    [permitted],
  );

  /** Every match in one list, in group order. */
  const matches = useMemo(
    () => visibleGroups.flatMap((group) => group.entries),
    [visibleGroups],
  );

  /** Which section each match came from, so a flat result still says where it lives. */
  const matchGroupLabels = useMemo(() => {
    const labels: Record<string, string> = {};
    for (const group of visibleGroups) {
      for (const entry of group.entries) labels[entry.key] = group.title;
    }
    return labels;
  }, [visibleGroups]);

  const matchCount = matches.length;
  const isSearching = query.trim().length > 0;

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1400, mx: 'auto' }}>
      <Typography variant="h4" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
        Setup Master
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75, maxWidth: 760, lineHeight: 1.6 }}>
        Everything the platform treats as settled: the entities you trade as, the numbers every quote
        inherits, and the work that runs without being asked. Change something here and it applies to
        every new record from that moment on.
      </Typography>

      <Paper
        variant="outlined"
        sx={{
          mt: 3,
          mb: 4,
          p: 1.5,
          borderRadius: 3,
          display: 'flex',
          gap: 2,
          alignItems: 'center',
          flexWrap: 'wrap',
        }}
      >
        <TextField
          size="small"
          value={query}
          onChange={(event) => setQuery(event.target.value)}
          placeholder="Search settings — try “tax”, “mailbox”, “role”"
          sx={{ flex: 1, minWidth: 260, '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
          slotProps={{
            // On TextField, `aria-label` names the wrapping FormControl rather than the input that
            // takes focus — the name has to go on the html input (SC 4.1.2).
            htmlInput: { 'aria-label': 'Search settings' },
            input: {
              startAdornment: (
                <InputAdornment position="start">
                  <SearchIcon fontSize="small" sx={{ color: 'text.secondary' }} />
                </InputAdornment>
              ),
            },
          }}
        />
        <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 600, pr: 1 }} aria-live="polite">
          {isSearching
            ? `${matchCount} ${matchCount === 1 ? 'match' : 'matches'}`
            : `${ownedCount} settings you can open`}
        </Typography>
      </Paper>

      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
        {/* Searching flattens the page. Sections earn their keep while browsing; on a query they
            become a column of one-card headings you have to scroll through to read three results. */}
        {isSearching && matchCount > 0 && (
          <CardGrid entries={matches} groupLabels={matchGroupLabels} />
        )}

        {!isSearching && visibleGroups.map((group) => (
          <Box key={group.key} component="section" aria-labelledby={`setup-group-${group.key}`}>
            <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1.5, mb: 0.5, flexWrap: 'wrap' }}>
              <Typography
                id={`setup-group-${group.key}`}
                variant="h6"
                component="h2"
                sx={{ fontWeight: 800, letterSpacing: '-0.01em' }}
              >
                {group.title}
              </Typography>
              <Chip
                label={group.entries.length}
                size="small"
                sx={{ height: 20, fontSize: '0.7rem', fontWeight: 700, backgroundColor: 'action.selected' }}
              />
            </Box>
            <Typography variant="body2" color="text.secondary" sx={{ mb: 2 }}>
              {group.caption}
            </Typography>
            <CardGrid entries={group.entries} />
          </Box>
        ))}

        {/* Two different nothings. A failed search is the user's word not matching; an empty hub
            with no search is a permission answer, and telling that person to try another word
            sends them looking for a screen they were never going to be shown. */}
        {matchCount === 0 && isSearching && (
          <Box sx={{ textAlign: 'center', py: 8 }}>
            <NoResultIcon sx={{ fontSize: 56, color: 'action.disabled', mb: 1.5 }} />
            <Typography variant="h6" sx={{ fontWeight: 700 }}>
              Nothing here matches “{query.trim()}”.
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5 }}>
              Try a plainer word — the search reads each setting's description as well as its name.
            </Typography>
          </Box>
        )}

        {matchCount === 0 && !isSearching && (
          <Box sx={{ textAlign: 'center', py: 8 }}>
            <LockedIcon sx={{ fontSize: 56, color: 'action.disabled', mb: 1.5 }} />
            <Typography variant="h6" sx={{ fontWeight: 700 }}>
              No setup screens are open to your role.
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 520, mx: 'auto' }}>
              Setup is governed per module. Ask an administrator to grant the modules you need —
              they are listed against each role under User &amp; Access → Roles &amp; Permissions.
            </Typography>
          </Box>
        )}
      </Box>
    </Box>
  );
};

export default SetupHubPage;
