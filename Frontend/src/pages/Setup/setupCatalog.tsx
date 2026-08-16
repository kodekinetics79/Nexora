import type { ReactNode } from 'react';
import {
  Apartment as BusinessUnitIcon,
  Place as LocationIcon,
  Warehouse as WarehouseIcon,
  CurrencyExchange as CurrencyIcon,
  Straighten as UomIcon,
  Percent as PriceStructureIcon,
  Policy as CommercialPolicyIcon,
  Article as QuoteFormatIcon,
  AltRoute as RoutingIcon,
  AlternateEmail as MailboxIcon,
  NotificationsActive as SlaIcon,
  ScheduleSend as ScheduledReportIcon,
  DynamicForm as CustomFieldIcon,
  FormatListBulleted as ListsIcon,
  Badge as RoleIcon,
  People as UsersIcon,
  VerifiedUser as PermissionsIcon,
  Hub as IntegrationIcon,
  Storage as RetentionIcon,
} from '@mui/icons-material';

/**
 * The one register of everything Setup Master governs.
 *
 * Every setup surface — the hub page, the in-page quick switcher, the sidebar's visibility rule —
 * reads this file and nothing else. That is the point: a configuration screen can be listed once
 * or not at all, which is what stops the same feature reappearing under two names as the module
 * grows. `setupCatalog.test.ts` fails the build if an entry is duplicated, if a `/setup/*` route
 * in `App.tsx` is missing here, or if an entry points at a route that does not exist.
 */
export interface SetupEntry {
  /** Stable identity — used for React keys and for the route-coverage test. */
  key: string;
  label: string;
  /**
   * i18n key for the label, where one already exists. The sidebar used to render these seven names
   * through `t()`, so an Arabic or Urdu workspace read them in its own language; dropping the keys
   * along with the sidebar list would have quietly made this module English-only. `label` stays the
   * default, so an entry without a key — or a locale without that string — still reads.
   */
  labelKey?: string;
  /** What this screen decides, in the operator's words. One sentence, no feature-speak. */
  description: string;
  path: string;
  icon: ReactNode;
  /** Permission module gating the destination. Must match the route's `PermissionGuard`. */
  moduleName?: string;
  /** Extra search terms — the words someone types when they don't know our label. */
  keywords?: string[];
  /**
   * A neighbouring screen that shows the same nouns for a different purpose. Rendered on the card
   * so nobody has to discover the difference by trial: the ambiguity is answered where it arises.
   */
  seeAlso?: { label: string; path: string; note: string };
  /**
   * Set on entries that live outside `/setup`. They are listed for findability and never counted
   * as Setup's own surface: the hub links out to the owning module rather than re-implementing it.
   */
  external?: boolean;
}

export interface SetupGroup {
  key: string;
  title: string;
  /** Why these screens sit together — the question this group answers. */
  caption: string;
  entries: SetupEntry[];
}

export const SETUP_ROOT = '/setup';

/**
 * Setup types in `Setup_Master` that a dedicated screen owns. The generic list editor shows their
 * rows read-only and links to the owner, so the same values are never editable in two places.
 * Keyed by the lower-cased setup type; `Setup_Master.SetupType` is stored inconsistently cased
 * (see Backend `Authorization/SetupTypes.cs`), so callers must normalise before lookup.
 */
export const SETUP_TYPES_OWNED_ELSEWHERE: Record<string, { label: string; path: string }> = {
  price_structure: { label: 'Price Structure', path: '/setup/price-structure' },
};

/** Normalises a stored setup type the way the backend does before comparing it. */
export const normaliseSetupType = (setupType: string | null | undefined): string =>
  (setupType ?? '').replace(/\s+/g, '').toLowerCase();

export const SETUP_GROUPS: SetupGroup[] = [
  {
    key: 'company',
    title: 'Company & Locations',
    caption: 'Who you trade as, and the places you hold stock and ship from.',
    entries: [
      {
        key: 'business-unit',
        label: 'Business Units',
        labelKey: 'business_unit',
        description: 'The trading entities that issue quotes and invoices, each with its own registration details.',
        path: '/setup/business-unit',
        icon: <BusinessUnitIcon />,
        moduleName: 'Business Units',
        keywords: ['business units', 'company', 'entity', 'legal', 'branch', 'division', 'vat', 'tax registration'],
      },
      {
        key: 'locations',
        label: 'Locations',
        labelKey: 'locations',
        description: 'The countries, states and cities used for addresses, delivery terms and routing.',
        path: '/setup/locations',
        icon: <LocationIcon />,
        moduleName: 'Locations',
        keywords: ['country', 'state', 'city', 'region', 'address', 'geography'],
      },
      {
        key: 'warehouse',
        label: 'Warehouses',
        labelKey: 'warehouse',
        description: 'The physical stores stock can sit in — codes, addresses and which ones are still in use.',
        path: '/setup/warehouse',
        icon: <WarehouseIcon />,
        moduleName: 'Warehouse',
        keywords: ['warehouses', 'store', 'depot', 'site', 'stock location'],
        seeAlso: {
          label: 'Inventory → Warehouses',
          path: '/inventory/warehouses',
          note: 'shows what is currently in each one',
        },
      },
    ],
  },
  {
    key: 'commercial',
    title: 'Commercial Standards',
    caption: 'The units, rates and formats every quote and order inherits.',
    entries: [
      {
        key: 'currency',
        label: 'Currencies',
        labelKey: 'currency',
        description: 'The currencies you buy and sell in, and the symbols and decimals they print with.',
        path: '/setup/currency',
        icon: <CurrencyIcon />,
        moduleName: 'Currency',
        // The label is the product's own word for it ('Currency', as the supplier grid heads the
        // column). The plural and the spelled-out names people actually type live here instead,
        // so the terse label costs nothing at the search box.
        keywords: ['currencies', 'fx', 'exchange', 'money', 'sar', 'usd', 'rate'],
      },
      {
        key: 'uom',
        label: 'Units of Measure',
        labelKey: 'uom',
        description: 'The units lines are quoted in, and the short codes extraction is allowed to recognise.',
        path: '/setup/uom',
        icon: <UomIcon />,
        moduleName: 'UOM',
        keywords: ['units of measure', 'unit of measure', 'units', 'measure', 'each', 'metre', 'kg', 'quantity'],
      },
      {
        key: 'price-structure',
        label: 'Price Structure',
        description: 'The named margin and mark-up structures a quote line can be priced against.',
        path: '/setup/price-structure',
        icon: <PriceStructureIcon />,
        moduleName: 'UOM',
        keywords: ['margin', 'markup', 'pricing', 'uplift', 'cost plus'],
      },
      {
        key: 'commercial-policy',
        label: 'Commercial Policy',
        description: 'Tax rates, rounding and tolerance, and the weights that decide how suppliers rank.',
        path: '/setup/commercial-policy',
        icon: <CommercialPolicyIcon />,
        moduleName: 'UOM',
        keywords: ['tax', 'vat', 'zatca', 'tolerance', 'rounding', 'supplier scoring', 'weights'],
      },
      {
        key: 'quote-format',
        label: 'Quote Format',
        labelKey: 'quote_format',
        description: 'What the customer actually sees — numbering, headers, terms and the document layout.',
        path: '/setup/quote-format',
        icon: <QuoteFormatIcon />,
        moduleName: 'Quote Configuration',
        keywords: ['quote format', 'template', 'pdf', 'numbering', 'terms', 'layout', 'branding'],
      },
    ],
  },
  {
    key: 'automation',
    title: 'Automation & Delivery',
    caption: 'What the platform does on its own — where work arrives, who it goes to, when it chases.',
    entries: [
      {
        key: 'routing-rules',
        label: 'RFQ Routing Rules',
        description: 'Which rep or team an incoming enquiry lands with, by customer, category or business unit.',
        path: '/setup/routing-rules',
        icon: <RoutingIcon />,
        moduleName: 'Customers',
        keywords: ['assignment', 'ownership', 'territory', 'round robin', 'lead routing'],
      },
      {
        key: 'mailboxes',
        label: 'Email Inboxes',
        description: 'The mailboxes the platform polls for enquiries and quotes, and the address replies are sent from.',
        path: '/setup/mailboxes',
        icon: <MailboxIcon />,
        moduleName: 'Email & SMTP',
        keywords: ['imap', 'smtp', 'mail', 'ingestion', 'inbox', 'credentials', 'polling'],
        seeAlso: {
          label: 'Lead Management → Inbound Mail',
          path: '/procurement/leads/inbound-mail',
          note: 'shows what each poll decided about a message',
        },
      },
      {
        key: 'sla',
        label: 'Deadlines & Alerts',
        description: 'How long each stage may sit before it is late, and who hears about it when it is.',
        path: '/setup/sla',
        icon: <SlaIcon />,
        moduleName: 'UOM',
        keywords: ['sla', 'due date', 'escalation', 'reminder', 'ageing', 'breach'],
      },
      {
        key: 'scheduled-reports',
        label: 'Scheduled Reports',
        description: 'Recurring deliveries of dashboard content to a list of recipients on a fixed schedule.',
        path: '/setup/scheduled-reports',
        icon: <ScheduledReportIcon />,
        moduleName: 'Dashboard',
        keywords: ['digest', 'subscription', 'cron', 'weekly', 'email report'],
      },
    ],
  },
  {
    key: 'data-model',
    title: 'Records & Lists',
    caption: 'What your records can hold, and the values every dropdown offers.',
    entries: [
      {
        key: 'custom-fields',
        label: 'Custom Fields',
        description: 'Extra fields your team needs on leads, quotes and orders that the standard record has no place for.',
        path: '/setup/custom-fields',
        icon: <CustomFieldIcon />,
        moduleName: 'UOM',
        keywords: ['attribute', 'extra field', 'metadata', 'form', 'tenant field'],
      },
      {
        key: 'master',
        label: 'Lists & Picklists',
        description: 'The values behind the dropdowns — statuses, reasons and categories used across the platform.',
        path: '/setup/master',
        icon: <ListsIcon />,
        moduleName: 'UOM',
        // Not "taxonomy": it belongs to Platform Governance's Taxonomy & Skills, and it makes a
        // search for "tax" answer with this screen instead of Commercial Policy.
        keywords: ['setup master', 'master sub', 'lookup', 'dropdown', 'reason', 'status', 'picklist'],
      },
      {
        key: 'roles',
        label: 'Roles',
        description: 'The job titles people can be given, and the authority tier each one carries.',
        // Roles are Setup_Master rows (SetupType "Role") — creating one is a Setup act, granting it
        // permissions is a Security act. Deep-linking the filtered list keeps the two honest instead
        // of building a second role editor: see the Access group for the permissions half.
        path: '/setup/master?type=role',
        icon: <RoleIcon />,
        moduleName: 'UOM',
        keywords: ['role', 'job title', 'authority', 'rank', 'tier', 'manager', 'administrator'],
        seeAlso: {
          label: 'User & Access → Roles & Permissions',
          path: '/security/roles',
          note: 'decides what each role may do',
        },
      },
    ],
  },
];

/**
 * Configuration that Setup deliberately does not own. Listed so the search finds it and nobody
 * builds a second copy inside Setup, but every one of these links out to the module that governs it.
 */
export const SETUP_ELSEWHERE: SetupEntry[] = [
  {
    key: 'users',
    label: 'Users',
    labelKey: 'users',
    description: 'Accounts, the role each person holds, and whether they can still sign in.',
    path: '/security/users',
    icon: <UsersIcon />,
    moduleName: 'Users',
    keywords: ['account', 'people', 'staff', 'login', 'invite', 'deactivate'],
    external: true,
  },
  {
    key: 'roles-permissions',
    label: 'Roles & Permissions',
    labelKey: 'roles_and_permissions',
    description: 'What each role may see, create, edit and delete, module by module.',
    path: '/security/roles',
    icon: <PermissionsIcon />,
    moduleName: 'Roles & Permissions',
    keywords: ['permission', 'access', 'grant', 'module', 'rbac'],
    external: true,
  },
  {
    key: 'integrations',
    label: 'Integration Hub',
    description: 'Versioned connections to outside systems, their mappings and delivery controls.',
    path: '/admin/platform/integrations',
    icon: <IntegrationIcon />,
    moduleName: 'Users',
    keywords: ['connector', 'api', 'webhook', 'erp', 'sync'],
    external: true,
  },
  {
    key: 'retention',
    label: 'Storage & Retention',
    description: 'Where documents are stored and how long the platform keeps them.',
    path: '/admin/platform/retention',
    icon: <RetentionIcon />,
    moduleName: 'Users',
    keywords: ['archive', 's3', 'storage', 'retention', 'purge', 'documents'],
    external: true,
  },
];

/** Every entry Setup itself owns, flattened — hub search, quick switcher and tests all read this. */
export const SETUP_ENTRIES: SetupEntry[] = SETUP_GROUPS.flatMap((group) => group.entries);

/** The group an entry belongs to, for breadcrumbs. */
export const groupOfEntry = (entryKey: string): SetupGroup | undefined =>
  SETUP_GROUPS.find((group) => group.entries.some((entry) => entry.key === entryKey));

/** Path without its query string — entries may deep-link into a shared screen (Roles does). */
const pathnameOf = (path: string) => path.split('?')[0];

/**
 * The entry a URL is currently on. An entry whose path carries a query (Roles) only matches when
 * the query matches too, so `/setup/master` and `/setup/master?type=role` read as different places
 * — which is what the breadcrumb and the switcher have to show.
 */
export const entryForLocation = (pathname: string, search = ''): SetupEntry | undefined => {
  const params = new URLSearchParams(search);
  const exact = SETUP_ENTRIES.find((entry) => {
    const [entryPath, entryQuery] = entry.path.split('?');
    if (entryPath !== pathname) return false;
    if (!entryQuery) return false;
    const entryParams = new URLSearchParams(entryQuery);
    return [...entryParams.entries()].every(
      ([key, value]) => (params.get(key) ?? '').toLowerCase() === value.toLowerCase(),
    );
  });
  return exact ?? SETUP_ENTRIES.find((entry) => pathnameOf(entry.path) === pathname);
};

/**
 * The entry's name in the reader's language, falling back to the English label.
 *
 * Takes i18next's `t` rather than calling a hook, so the catalogue stays plain data that a test or
 * the sidebar's permission rule can import without a React context.
 */
export const setupEntryLabel = (
  entry: SetupEntry,
  t: (key: string, defaultValue: string) => string,
): string => (entry.labelKey ? t(entry.labelKey, entry.label) : entry.label);

/** Case-insensitive match over label, description and keywords. */
export const entryMatches = (entry: SetupEntry, query: string): boolean => {
  const needle = query.trim().toLowerCase();
  if (!needle) return true;
  const haystack = [entry.label, entry.description, ...(entry.keywords ?? [])].join(' ').toLowerCase();
  return needle.split(/\s+/).every((term) => haystack.includes(term));
};
