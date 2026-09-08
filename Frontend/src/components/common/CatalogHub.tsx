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

/**
 * A searchable directory of screens, grouped by the question each group answers.
 *
 * This was `SetupHubPage`, which solved the problem once for the 25 administrative screens: a flat
 * rail list gave every screen the same weight, said nothing about what any of them did, and pushed
 * everything past the fold out of reach. Replacing it with grouped, described, searchable cards let
 * the rail carry one row instead of fourteen.
 *
 * The main navigation had exactly the same problem at three times the size, so the component is the
 * same component rather than a second one that drifts. Setup Master and "All screens" are both this
 * page with a different catalogue.
 */

/** The shape both catalogues satisfy — `Setup/setupCatalog.tsx` and `layout/navCatalog.tsx`. */
export interface CatalogEntry {
  key: string;
  label: string;
  labelKey?: string;
  description: string;
  path: string;
  icon: React.ReactNode;
  moduleName?: string;
  keywords?: string[];
  managerOnly?: boolean;
  seeAlso?: { label: string; path: string; note: string };
}

export interface CatalogGroup<E extends CatalogEntry = CatalogEntry> {
  key: string;
  title: string;
  caption: string;
  entries: E[];
}

const EntryCard: React.FC<{ entry: CatalogEntry; groupLabel?: string; label: string }> = ({
  entry,
  groupLabel,
  label,
}) => (
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
      '&:focus-within': { borderColor: 'primary.main' },
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
        '&:focus-visible': {
          outline: (theme) => `3px solid ${theme.palette.primary.main}`,
          outlineOffset: -3,
        },
      }}
    >
      {/* Search results are one flat grid, so each card names the group it came from — otherwise a
          result has lost the context the section heading was carrying. */}
      {groupLabel && (
        <Typography
          variant="caption"
          sx={{
            display: 'block',
            mb: 0.75,
            fontWeight: 700,
            letterSpacing: '0.06em',
            textTransform: 'uppercase',
            color: 'text.disabled',
          }}
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
            backgroundColor: (theme) =>
              alpha(theme.palette.primary.main, theme.palette.mode === 'dark' ? 0.18 : 0.1),
            '& .MuiSvgIcon-root': { fontSize: 20 },
          }}
        >
          {entry.icon}
        </Box>
        <Typography variant="subtitle1" sx={{ fontWeight: 700, lineHeight: 1.25, flex: 1, minWidth: 0 }}>
          {label}
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

/** A responsive card grid — three across on a desk, one on a phone. */
const CardGrid: React.FC<{
  entries: CatalogEntry[];
  groupLabels?: Record<string, string>;
  labelFor: (entry: CatalogEntry) => string;
}> = ({ entries, groupLabels, labelFor }) => (
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
      <EntryCard
        key={entry.key}
        entry={entry}
        groupLabel={groupLabels?.[entry.key]}
        label={labelFor(entry)}
      />
    ))}
  </Box>
);

export interface CatalogHubProps {
  title: string;
  intro: string;
  groups: CatalogGroup[];
  /** Distinguishes this hub's section ids and search field from any other on the page. */
  idPrefix: string;
  searchPlaceholder: string;
  searchAriaLabel: string;
  /** e.g. `(n) => \`${n} settings you can open\`` — the count shown while not searching. */
  availableLabel: (count: number) => string;
  /** Case-insensitive match over label, description and keywords. */
  matches: (entry: CatalogEntry, query: string) => boolean;
  /** Copy for the "you may open nothing here" state, which is a permission answer, not a search one. */
  noAccessTitle: string;
  noAccessMessage: string;
  /** Rendered above the groups — used by "All screens" to name the five rows it is NOT listing. */
  children?: React.ReactNode;
}

const CatalogHub: React.FC<CatalogHubProps> = ({
  title,
  intro,
  groups,
  idPrefix,
  searchPlaceholder,
  searchAriaLabel,
  availableLabel,
  matches,
  noAccessTitle,
  noAccessMessage,
  children,
}) => {
  const { t } = useTranslation();
  const { userData, hasPermission } = useAuth();
  const [query, setQuery] = useState('');
  const isManager = userData?.isManager === true;

  const permitted = useMemo(
    () => (entry: CatalogEntry) =>
      (!entry.managerOnly || isManager) && (!entry.moduleName || hasPermission(entry.moduleName)),
    [hasPermission, isManager],
  );

  const labelFor = useMemo(
    () => (entry: CatalogEntry) => (entry.labelKey ? t(entry.labelKey, entry.label) : entry.label),
    [t],
  );

  /** Groups with nothing this user may open disappear entirely — an empty section is noise. */
  const visibleGroups = useMemo(
    () =>
      groups
        .map((group) => ({
          ...group,
          entries: group.entries.filter((entry) => permitted(entry) && matches(entry, query)),
        }))
        .filter((group) => group.entries.length > 0),
    [groups, permitted, matches, query],
  );

  const ownedCount = useMemo(
    () => groups.flatMap((group) => group.entries).filter(permitted).length,
    [groups, permitted],
  );

  /** Every match in one list, in group order. */
  const matched = useMemo(() => visibleGroups.flatMap((group) => group.entries), [visibleGroups]);

  /** Which section each match came from, so a flat result still says where it lives. */
  const matchGroupLabels = useMemo(() => {
    const labels: Record<string, string> = {};
    for (const group of visibleGroups) {
      for (const entry of group.entries) labels[entry.key] = group.title;
    }
    return labels;
  }, [visibleGroups]);

  const matchCount = matched.length;
  const isSearching = query.trim().length > 0;

  return (
    <Box sx={{ p: { xs: 2, md: 3 }, maxWidth: 1400, mx: 'auto' }}>
      <Typography variant="h4" component="h1" sx={{ fontWeight: 800, letterSpacing: '-0.02em' }}>
        {title}
      </Typography>
      <Typography variant="body2" color="text.secondary" sx={{ mt: 0.75, maxWidth: 760, lineHeight: 1.6 }}>
        {intro}
      </Typography>

      {children}

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
          placeholder={searchPlaceholder}
          sx={{ flex: 1, minWidth: 260, '& .MuiOutlinedInput-root': { borderRadius: 2 } }}
          slotProps={{
            // On TextField, `aria-label` names the wrapping FormControl rather than the input that
            // takes focus — the name has to go on the html input (SC 4.1.2).
            htmlInput: { 'aria-label': searchAriaLabel },
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
            : availableLabel(ownedCount)}
        </Typography>
      </Paper>

      <Box sx={{ display: 'flex', flexDirection: 'column', gap: 5 }}>
        {/* Searching flattens the page. Sections earn their keep while browsing; on a query they
            become a column of one-card headings you have to scroll through to read three results. */}
        {isSearching && matchCount > 0 && (
          <CardGrid entries={matched} groupLabels={matchGroupLabels} labelFor={labelFor} />
        )}

        {!isSearching &&
          visibleGroups.map((group) => (
            <Box key={group.key} component="section" aria-labelledby={`${idPrefix}-group-${group.key}`}>
              <Box sx={{ display: 'flex', alignItems: 'baseline', gap: 1.5, mb: 0.5, flexWrap: 'wrap' }}>
                <Typography
                  id={`${idPrefix}-group-${group.key}`}
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
              <CardGrid entries={group.entries} labelFor={labelFor} />
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
              Try a plainer word — the search reads each screen's description as well as its name.
            </Typography>
          </Box>
        )}

        {matchCount === 0 && !isSearching && (
          <Box sx={{ textAlign: 'center', py: 8 }}>
            <LockedIcon sx={{ fontSize: 56, color: 'action.disabled', mb: 1.5 }} />
            <Typography variant="h6" sx={{ fontWeight: 700 }}>
              {noAccessTitle}
            </Typography>
            <Typography variant="body2" color="text.secondary" sx={{ mt: 0.5, maxWidth: 520, mx: 'auto' }}>
              {noAccessMessage}
            </Typography>
          </Box>
        )}
      </Box>
    </Box>
  );
};

export default CatalogHub;
