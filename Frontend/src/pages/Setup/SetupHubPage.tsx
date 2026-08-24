import React from 'react';
import CatalogHub, { type CatalogEntry } from '../../components/common/CatalogHub';
import { SETUP_GROUPS, entryMatches, type SetupEntry } from './setupCatalog';

/**
 * Setup Master's front door.
 *
 * It replaced a fourteen-item sidebar list that had outgrown the rail: the list gave every screen
 * the same weight, said nothing about what any of them did, and pushed everything below it out of
 * reach. Here the same screens are grouped by the question they answer, described in a sentence
 * each, and searchable — so the sidebar can carry one entry and this page carries the rest.
 *
 * The rendering lives in `components/common/CatalogHub`, because the main navigation had the same
 * problem at three times the size and is now solved by the same component (`/advanced`). Two
 * catalogues, one page.
 */
const SetupHubPage: React.FC = () => (
  <CatalogHub
    title="Setup Master"
    intro="Everything the platform treats as settled: the entities you trade as, the numbers every quote inherits, and the work that runs without being asked. Change something here and it applies to every new record from that moment on."
    idPrefix="setup"
    groups={SETUP_GROUPS as { key: string; title: string; caption: string; entries: SetupEntry[] }[]}
    searchPlaceholder="Search settings — try “tax”, “mailbox”, “role”"
    searchAriaLabel="Search settings"
    availableLabel={(count) => `${count} settings you can open`}
    matches={(entry: CatalogEntry, query: string) => entryMatches(entry as SetupEntry, query)}
    noAccessTitle="No setup screens are open to your role."
    noAccessMessage="Setup is governed per module. Ask an administrator to grant the modules you need — they are listed against each role under People & Access → Roles & Permissions."
  />
);

export default SetupHubPage;
