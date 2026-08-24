import React from 'react';
import { Link as RouterLink } from 'react-router-dom';
import { Box, Chip, Stack, Typography } from '@mui/material';
import CatalogHub, { type CatalogEntry } from '../../components/common/CatalogHub';
import { ADVANCED_GROUPS, PRIMARY_NAV, navEntryMatches, type NavEntry } from '../../components/layout/navCatalog';

/**
 * Every screen the rail no longer carries — grouped, described in a sentence, and searchable.
 *
 * This is the other half of the five-row rail, and the reason cutting the rail is a relocation
 * rather than a deletion. Nothing was removed to make the navigation small: each screen below keeps
 * its route, its permission gate, its page title, its deep links and its tests. What changed is
 * that a rep no longer has to scan sixty-nine rail rows to find the four they use daily, and a
 * manager looking for Stock Ageing can type "old stock" instead of knowing which of seventeen
 * inventory rows it hides under.
 *
 * The five rows themselves are named at the top rather than repeated as cards: one door per
 * destination is the rule, and listing Leads here as well as on the rail is exactly the duplication
 * that put Users and Integration Hub in the navigation twice before Setup Master absorbed them.
 */
const AllScreensPage: React.FC = () => (
  <CatalogHub
    title="All screens"
    intro="Everything Nexora can do that is not part of the daily quote-building path. Each screen here is a full destination with its own address — this page is a directory, not a copy."
    idPrefix="advanced"
    groups={ADVANCED_GROUPS as { key: string; title: string; caption: string; entries: NavEntry[] }[]}
    searchPlaceholder="Search screens — try “invoice”, “stock ageing”, “copilot”"
    searchAriaLabel="Search all screens"
    availableLabel={(count) => `${count} screens you can open`}
    matches={(entry: CatalogEntry, query: string) => navEntryMatches(entry as NavEntry, query)}
    noAccessTitle="No additional screens are open to your role."
    noAccessMessage="Everything you can reach is already on the sidebar. Ask an administrator to grant the modules you need — they are listed against each role under Setup → Roles & Permissions."
  >
    <Box sx={{ mt: 2.5 }}>
      <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700, letterSpacing: '0.04em', textTransform: 'uppercase' }}>
        Always on the sidebar
      </Typography>
      <Stack direction="row" spacing={1} sx={{ mt: 1, flexWrap: 'wrap', gap: 1 }}>
        {PRIMARY_NAV.map((item) => (
          <Chip
            key={item.key}
            component={RouterLink}
            to={item.path}
            clickable
            label={item.label}
            size="small"
            sx={{ fontWeight: 700 }}
          />
        ))}
      </Stack>
    </Box>
  </CatalogHub>
);

export default AllScreensPage;
