import type { ReactNode } from 'react';
import {
  Inbox as InboxIcon,
  TrendingUp as LeadIcon,
  ReceiptLong as RfqIcon,
  RequestQuote as QuoteIcon,
  Settings as SetupIcon,
  Apps as AllScreensIcon,
  // Advanced groups
  Dashboard as DashboardIcon,
  InsightsOutlined as InsightsIcon,
  CalendarMonth as DeadlineIcon,
  QueryStats as PerformanceIcon,
  Groups as TeamIcon,
  Person as RepIcon,
  AltRoute as RoutingIcon,
  AssignmentTurnedIn as FollowUpIcon,
  WarningAmber as ExceptionIcon,
  FactCheck as BoqIcon,
  AutoAwesome as CopilotIcon,
  RuleFolder as ApprovalsIcon,
  History as ActivityIcon,
  Psychology as MemoryIcon,
  AccountTree as CaseIcon,
  Handshake as SupplierIcon,
  MoveToInbox as SupplierInboxIcon,
  Sell as QuotedItemsIcon,
  ShoppingCart as PurchaseOrderIcon,
  Forum as CommercialInboxIcon,
  Assignment as OrderIcon,
  LocalShipping as ShipmentIcon,
  AccountBalance as FinanceIcon,
  SwapHoriz as HandoffIcon,
  Description as ClientPoIcon,
  People as CustomerIcon,
  Badge as AccountOwnerIcon,
  Inventory2 as InventoryIcon,
  Category as CategoryIcon,
  Warehouse as WarehouseIcon,
  EventAvailable as AvailabilityIcon,
  Bookmark as ReservationIcon,
  FlightLand as IncomingIcon,
  CompareArrows as MovementIcon,
  ShowChart as DemandIcon,
  Straighten as LevelsIcon,
  NotificationsActive as ReorderIcon,
  Rule as VarianceIcon,
  HourglassBottom as AgeingIcon,
  Link as ResourcesIcon,
  QrCode2 as LotIcon,
  Troubleshoot as TraceIcon,
  MarkEmailRead as MailIcon,
  FolderSpecial as WatchedFolderIcon,
  ContentCopy as DuplicateIcon,
  JoinInner as MatchIcon,
  Difference as RevisionIcon,
  UploadFile as UploadIcon,
  RateReview as ReviewIcon,
  Business as AdminOpsIcon,
} from '@mui/icons-material';

/**
 * The one register of the tenant navigation.
 *
 * Nexora's rail grew to 17 top-level rows over 69 destinations — about 1,095px of rail against
 * 836px of laptop viewport — and the pilot answered that with a second allow-list that hid 50 of
 * them. Two lists disagreeing about one rail is the same defect as two rails: a screen can be
 * routed, permitted, and still unfindable, and nobody can tell which list is wrong.
 *
 * So the rail now reads from here and nothing else, and it carries FIVE rows — the five nouns a
 * rep uses to describe their own job:
 *
 *     Inbox -> Leads -> RFQs -> Quotes,  plus Setup for everything that is settled once.
 *
 * Everything else is RELOCATED, never removed: it keeps its route, its permission gate, its page
 * title, its deep links and its tests, and it is listed — with a sentence saying what it decides —
 * on the "All screens" directory at `/advanced`. `navCatalog.test.ts` fails the build if a
 * destination that used to be on the rail stops being reachable from either place.
 *
 * Modelled deliberately on `pages/Setup/setupCatalog.tsx`, which already solved this problem for
 * the 25 administrative screens. There is no second pattern here to learn.
 */

/** A destination the user can be sent to, described in the words they would use for it. */
export interface NavEntry {
  /** Stable identity — React keys, and the relocation test. */
  key: string;
  label: string;
  /** i18n key where one already exists; `label` is the default. */
  labelKey?: string;
  /** What this screen decides or shows, in one sentence, in the operator's words. */
  description: string;
  path: string;
  icon: ReactNode;
  /** Permission module gating the destination. Must match the route's `PermissionGuard`. */
  moduleName?: string;
  /** Extra search terms — the words someone types when they don't know our label. */
  keywords?: string[];
  /** Hidden from non-managers, matching the rail's old `isManager` branches. */
  managerOnly?: boolean;
  /** A neighbour that shows the same nouns for a different purpose. */
  seeAlso?: { label: string; path: string; note: string };
}

export interface NavGroup {
  key: string;
  title: string;
  /** Why these screens sit together — the question this group answers. */
  caption: string;
  entries: NavEntry[];
}

/** One of the tabs a primary destination offers. Exactly one level, never nested. */
export interface NavView {
  key: string;
  label: string;
  /** May carry a query string — the tab strip compares those too. */
  path: string;
  moduleName?: string;
  /** Extra addresses that should still light this tab (a detail page under the list). */
  activePrefixes?: string[];
}

export interface PrimaryNavItem {
  key: string;
  label: string;
  labelKey?: string;
  description: string;
  icon: ReactNode;
  path: string;
  moduleName?: string;
  /** Addresses at which this row counts as "here", beyond its own path and its views'. */
  activePrefixes?: string[];
  /** The one level of tabs this destination offers. Absent means the screen has no tabs. */
  views?: NavView[];
}

export const INBOX_ROOT = '/inbox';
export const ADVANCED_ROOT = '/advanced';

/**
 * The five rows.
 *
 * Ordered by the journey, not by module name: work arrives (Inbox), becomes an enquiry (Leads),
 * becomes a request we can price (RFQs), becomes an offer (Quotes). Setup is last because it is
 * the only one a rep touches less than daily.
 */
export const PRIMARY_NAV: PrimaryNavItem[] = [
  {
    key: 'inbox',
    label: 'Inbox',
    description: 'Everything waiting on you, most urgent first — and every door work arrives through.',
    icon: <InboxIcon />,
    path: INBOX_ROOT,
    activePrefixes: [
      INBOX_ROOT,
      '/procurement/extraction/review',
      '/procurement/leads/inbound-mail',
      '/procurement/leads/manual-upload',
      '/procurement/leads/intelligence',
      '/procurement/leads/ingestion',
    ],
    views: [
      { key: 'inbox-queue', label: 'Needs you', path: INBOX_ROOT },
      {
        key: 'inbox-review',
        label: 'Documents to check',
        path: '/procurement/extraction/review',
        moduleName: 'Leads',
        activePrefixes: ['/procurement/extraction/review'],
      },
      {
        key: 'inbox-mail',
        label: 'Inbound mail',
        path: '/procurement/leads/inbound-mail',
        moduleName: 'Leads',
      },
      {
        key: 'inbox-upload',
        label: 'Upload documents',
        path: '/procurement/leads/manual-upload',
        moduleName: 'Leads',
        activePrefixes: ['/procurement/leads/ingestion', '/procurement/leads/intelligence'],
      },
    ],
  },
  {
    key: 'leads',
    label: 'Leads',
    labelKey: 'lead_management',
    description: 'Canonical customer enquiries being owned, assessed and given a participation decision.',
    icon: <LeadIcon />,
    path: '/procurement/leads/all',
    moduleName: 'Leads',
    activePrefixes: ['/procurement/leads/view', '/procurement/leads/', '/leads/view', '/commercial-cases'],
    views: [
      {
        key: 'leads-all',
        label: 'All inquiries',
        path: '/procurement/leads/all',
        moduleName: 'Leads',
        activePrefixes: ['/procurement/leads/view', '/leads/view'],
      },
      {
        key: 'leads-outstanding',
        label: 'Unassigned',
        path: '/procurement/leads/outstanding',
        moduleName: 'Leads',
      },
      {
        key: 'leads-assigned',
        label: 'Assigned',
        path: '/procurement/leads/assigned',
        moduleName: 'Leads',
        // An assigned Lead's fit and participation work happens at its numeric workbench route.
        // Exact list routes win before this fallback, so Unassigned/Revisions keep their own tab.
        activePrefixes: ['/procurement/leads/'],
      },
      {
        key: 'leads-revisions',
        label: 'Revisions',
        path: '/procurement/leads/all?view=revisions',
        moduleName: 'Leads',
      },
    ],
  },
  {
    key: 'rfqs',
    label: 'RFQs',
    labelKey: 'rfq_management',
    description: 'Formal requests promoted from approved Lead lines — pricing, sourcing and supplier responses.',
    icon: <RfqIcon />,
    path: '/procurement/rfqs/all',
    moduleName: 'RFQ Management',
    activePrefixes: [
      '/procurement/rfqs/view',
      '/procurement/rfqs/process',
      '/rfqs/view',
      '/procurement/sourcing-cases',
    ],
    views: [
      {
        key: 'rfqs-all',
        label: 'All RFQs',
        path: '/procurement/rfqs/all',
        moduleName: 'RFQ Management',
        activePrefixes: ['/procurement/rfqs/view', '/procurement/rfqs/process', '/rfqs/view'],
      },
      {
        key: 'rfqs-draft',
        label: 'Drafts',
        path: '/procurement/rfqs/draft',
        moduleName: 'RFQ Management',
      },
      {
        key: 'rfqs-ready',
        label: 'Ready for quote',
        path: '/procurement/rfqs/all?state=ready-for-quote',
        moduleName: 'RFQ Management',
      },
    ],
  },
  {
    key: 'quotes',
    label: 'Quotes',
    description: 'The offers you have made — drafts to finish, sent quotes to chase, and outcomes.',
    icon: <QuoteIcon />,
    path: '/sales/quotes',
    moduleName: 'Quotations',
    activePrefixes: ['/sales/quotes/view', '/sales/quotes/edit', '/sales/quotes/create'],
    views: [
      {
        key: 'quotes-draft',
        label: 'Drafts',
        path: '/sales/quotes?state=draft',
        moduleName: 'Quotations',
        activePrefixes: ['/sales/quotes/view', '/sales/quotes/edit', '/sales/quotes/create'],
      },
      { key: 'quotes-sent', label: 'Sent', path: '/sales/quotes?state=sent', moduleName: 'Quotations' },
      {
        key: 'quotes-follow-up',
        label: 'Follow-up due',
        path: '/sales/quotes?state=follow-up',
        moduleName: 'Quotations',
      },
      {
        key: 'quotes-outcomes',
        label: 'Won / lost',
        path: '/sales/quotes?state=outcomes',
        moduleName: 'Quotations',
      },
    ],
  },
  {
    key: 'setup',
    label: 'Setup',
    labelKey: 'setup_master',
    description: 'Everything the platform treats as settled — company, rates, formats, people and access.',
    icon: <SetupIcon />,
    path: '/setup',
    // Setup is 'here' on every address it governs, not only its own URL space.
    activePrefixes: ['/setup', '/security', '/admin/platform'],
  },
];

/**
 * Everything the rail used to carry, relocated — grouped by the question it answers and described
 * in a sentence, the way Setup Master already lists its own 25 screens.
 *
 * Nothing here was deleted. Every path below is a live route in `App.tsx`, permission-gated
 * exactly as before, and reachable by URL, bookmark, deep link and global search.
 */
export const ADVANCED_GROUPS: NavGroup[] = [
  {
    key: 'day',
    title: 'Daily role views',
    caption: 'The one-screen summary each role starts the day on.',
    entries: [
      {
        key: 'today-sales',
        label: 'Sales rep today',
        description: 'Your own priority queue, coaching findings and recoverable work for the last 90 days.',
        path: '/sales/today',
        icon: <RepIcon />,
        moduleName: 'Leads',
        keywords: ['today', 'my day', 'priority', 'queue', 'coaching'],
      },
      {
        key: 'today-sales-manager',
        label: 'Sales manager control tower',
        description: 'The team view: who is loaded, what is overdue and where deals are stalling.',
        path: '/sales/team',
        icon: <TeamIcon />,
        // CommercialIntelligenceController.TeamOverview and the route both authorize Leads:view.
        moduleName: 'Leads',
        managerOnly: true,
        keywords: ['manager', 'control tower', 'team', 'workload', 'team overview', 'overview'],
      },
      {
        key: 'today-sourcing',
        label: 'Sourcing today',
        description: 'Lines still without a supplier answer, and the solicitations waiting on a reply.',
        path: '/sourcing/today',
        icon: <SupplierIcon />,
        moduleName: 'Supplier History',
        keywords: ['sourcing', 'today', 'coverage', 'solicitation'],
      },
      {
        key: 'today-inventory',
        label: 'Inventory today',
        description: 'What is on hand, what is committed and what is arriving, in one page.',
        path: '/inventory/today',
        icon: <InventoryIcon />,
        moduleName: 'Products',
        keywords: ['stock', 'today', 'overview'],
      },
      {
        key: 'today-executive',
        label: 'Executive RFQ-to-revenue',
        description: 'The whole funnel end to end, for someone who does not work a single deal.',
        path: '/executive/today',
        icon: <InsightsIcon />,
        moduleName: 'Dashboard',
        keywords: ['executive', 'funnel', 'revenue', 'overview'],
      },
      {
        key: 'today-admin',
        label: 'Tenant admin operations',
        description: 'Operational health of this workspace — jobs, mailboxes, and what needs an administrator.',
        path: '/admin/operations',
        icon: <AdminOpsIcon />,
        moduleName: 'Users',
        keywords: ['admin', 'operations', 'health', 'jobs'],
      },
    ],
  },
  {
    key: 'analytics',
    title: 'Dashboards & analytics',
    caption: 'Numbers computed from what this workspace already holds.',
    entries: [
      {
        key: 'analytics-deadlines',
        label: 'Deadline board',
        description: 'Every open enquiry bucketed by how long is left before its closing date.',
        path: '/analytics/deadlines',
        icon: <DeadlineIcon />,
        moduleName: 'Leads',
        keywords: ['deadline', 'closing date', 'due', 'overdue', 'board'],
      },
      {
        key: 'dashboard-overview',
        label: 'Dashboard',
        labelKey: 'dashboard',
        description: 'Headline KPIs for the workspace. Reads "insufficient data" until there is enough history.',
        path: '/dashboard',
        icon: <DashboardIcon />,
        moduleName: 'Dashboard',
        keywords: ['kpi', 'home', 'overview', 'stats'],
      },
      {
        key: 'dashboard-team',
        label: 'Team workload',
        labelKey: 'team_workload',
        description: 'Open and overdue work per person, including the unassigned bucket.',
        path: '/dashboard/team',
        icon: <TeamIcon />,
        moduleName: 'Dashboard',
        managerOnly: true,
        keywords: ['workload', 'capacity', 'assignment', 'team'],
      },
      {
        key: 'analytics-brand-demand',
        label: 'Brand demand',
        description: 'Which manufacturers customers are actually asking for, by volume and value.',
        path: '/analytics/brand-demand',
        icon: <InsightsIcon />,
        moduleName: 'Leads',
        managerOnly: true,
        keywords: ['brand', 'manufacturer', 'demand', 'trend'],
      },
      {
        key: 'sales-performance',
        label: 'Performance',
        description: 'Win rate, cycle time and value per rep over a chosen window.',
        path: '/sales/performance',
        icon: <PerformanceIcon />,
        moduleName: 'Dashboard',
        keywords: ['performance', 'win rate', 'conversion', 'scorecard'],
      },
    ],
  },
  {
    key: 'intake',
    title: 'Intake channels & exceptions',
    caption: 'The doors work arrives through, and what got stuck in one.',
    entries: [
      {
        key: 'leads-review',
        label: 'Documents to check',
        description: 'Extractions a person still has to verify before they move downstream.',
        path: '/procurement/extraction/review',
        icon: <ReviewIcon />,
        moduleName: 'Leads',
        keywords: ['needs review', 'extraction', 'verify', 'check', 'confidence'],
        seeAlso: { label: 'Inbox', path: INBOX_ROOT, note: 'shows this queue alongside everything else waiting on you' },
      },
      {
        key: 'leads-inbound-mail',
        label: 'Inbound mail',
        description: 'What the mailbox decided about every message it polled, and why.',
        path: '/procurement/leads/inbound-mail',
        icon: <MailIcon />,
        moduleName: 'Leads',
        keywords: ['email', 'triage', 'mailbox', 'imap', 'rejected', 'noise'],
        seeAlso: {
          label: 'Setup → Email Inboxes',
          path: '/setup/mailboxes',
          note: 'configures which mailboxes are polled',
        },
      },
      {
        key: 'leads-bulk',
        label: 'Upload documents',
        description: 'Read RFQ documents from your machine — PDF, Word or Excel, up to 50 at a time.',
        path: '/procurement/leads/manual-upload',
        icon: <UploadIcon />,
        moduleName: 'Leads',
        keywords: ['upload', 'manual', 'bulk', 'pdf', 'excel', 'word', 'batch', 'ingest'],
      },
      {
        key: 'leads-watched-folders',
        label: 'Watched folders',
        description: 'Folders the server sweeps on a schedule, and what each sweep found.',
        path: '/procurement/leads/watched-folders',
        icon: <WatchedFolderIcon />,
        moduleName: 'Leads',
        keywords: ['folder', 'watch', 'drop', 'sweep', 'share'],
      },
      {
        key: 'leads-duplicates',
        label: 'Duplicate uploads',
        description: 'Documents held back because the same content had already been ingested.',
        path: '/procurement/leads/duplicates',
        icon: <DuplicateIcon />,
        moduleName: 'Leads',
        keywords: ['duplicate', 'repeat', 'same file', 'hash'],
      },
      {
        key: 'leads-matches',
        label: 'Possible matches',
        description: 'Enquiries the platform could not confidently attach to a customer record.',
        path: '/procurement/leads/possible-matches',
        icon: <MatchIcon />,
        moduleName: 'Leads',
        keywords: ['match', 'customer', 'client', 'ambiguous', 'resolve'],
      },
      {
        // Distinct key from the Leads tab that addresses the same view: the tab is the door, this
        // card is the search index entry that finds it when somebody types "amendment".
        key: 'intake-revisions',
        label: 'Revised enquiries',
        description: 'Enquiries a customer has since amended, with what changed between versions.',
        path: '/procurement/leads/all?view=revisions',
        icon: <RevisionIcon />,
        moduleName: 'Leads',
        keywords: ['revision', 'amendment', 'version', 'changed', 'resend'],
      },
      {
        key: 'commercial-cases',
        label: 'Commercial cases',
        description: 'One enquiry followed end to end — its lead, RFQs, quotes, orders and shipments.',
        path: '/commercial-cases',
        icon: <CaseIcon />,
        moduleName: 'Leads',
        keywords: ['case', 'traceability', 'nexora serial', 'end to end', 'workspace'],
      },
    ],
  },
  {
    key: 'sourcing',
    title: 'Suppliers & sourcing',
    caption: 'Who you buy from, what they quoted, and what you ordered.',
    entries: [
      {
        key: 'suppliers',
        label: 'Suppliers',
        labelKey: 'suppliers',
        description: 'The supplier record — contacts, terms, tier and trading history.',
        path: '/suppliers',
        icon: <SupplierIcon />,
        moduleName: 'Suppliers',
        keywords: ['vendor', 'supplier', 'partner', 'contact'],
      },
      {
        key: 'sourcing-cases',
        label: 'RFQs needing sourcing',
        description: 'RFQs with lines that stock cannot cover, so a supplier has to be asked.',
        path: '/procurement/rfqs/all?state=requires-sourcing',
        icon: <RoutingIcon />,
        moduleName: 'RFQ Management',
        keywords: ['sourcing', 'shortfall', 'coverage', 'case', 'requires sourcing'],
      },
      {
        key: 'supplier-quote-inbox',
        label: 'Supplier quote inbox',
        description: 'Supplier responses waiting to be read, checked and accepted onto an RFQ line.',
        path: '/procurement/supplier-quotes',
        icon: <SupplierInboxIcon />,
        moduleName: 'Supplier History',
        keywords: ['supplier quote', 'offer', 'bid', 'response', 'capture'],
      },
      {
        key: 'commercial-inbox',
        label: 'Commercial inbox',
        description: 'Inbound commercial documents that are not enquiries — supplier invoices and the like.',
        path: '/procurement/commercial-inbox',
        icon: <CommercialInboxIcon />,
        moduleName: 'Supplier History',
        keywords: ['commercial', 'inbox', 'invoice', 'document'],
      },
      {
        key: 'quoted-items',
        label: 'Quoted items history',
        labelKey: 'quoted_items',
        description: 'Every price a supplier has ever given you for a part, so you can sanity-check a new one.',
        path: '/suppliers/quoted-items',
        icon: <QuotedItemsIcon />,
        moduleName: 'Supplier History',
        keywords: ['price history', 'last price', 'part', 'benchmark'],
      },
      {
        key: 'purchase-orders',
        label: 'Supplier purchase orders',
        labelKey: 'purchase_orders',
        description: 'What you have committed to buy, and where each order stands.',
        path: '/suppliers/purchase-orders',
        icon: <PurchaseOrderIcon />,
        moduleName: 'Orders',
        keywords: ['po', 'purchase order', 'buy', 'commitment'],
      },
    ],
  },
  {
    key: 'fulfilment',
    title: 'Orders & fulfilment',
    caption: 'What happens after a customer says yes.',
    entries: [
      {
        key: 'client-po-inbox',
        label: 'Client PO inbox',
        description: 'Customer purchase orders received, matched line by line against the quote they accept.',
        path: '/sales/client-pos',
        icon: <ClientPoIcon />,
        moduleName: 'Customer Awards',
        keywords: ['customer po', 'award', 'purchase order', 'match', 'accept'],
      },
      {
        key: 'orders',
        label: 'Customer orders',
        description: 'Confirmed sales orders and what each one still owes the customer.',
        path: '/sales/orders',
        icon: <OrderIcon />,
        moduleName: 'Orders',
        keywords: ['sales order', 'order', 'confirmed', 'so'],
      },
      {
        key: 'shipments',
        label: 'Shipments',
        labelKey: 'shipments',
        description: 'Outbound deliveries to the customer, with their documents.',
        path: '/sales/shipments',
        icon: <ShipmentIcon />,
        moduleName: 'Shipments',
        keywords: ['delivery', 'dispatch', 'shipment', 'waybill'],
      },
      {
        key: 'procurement-handoffs',
        label: 'Procurement handoffs',
        description: 'Sales orders passed to procurement to buy against, and their acknowledgement.',
        path: '/procurement/handoffs',
        icon: <HandoffIcon />,
        moduleName: 'Orders',
        keywords: ['handoff', 'procurement', 'buy', 'fulfilment'],
      },
      {
        key: 'accounts-receivable',
        label: 'Accounts receivable',
        description: 'Issued invoices, what is overdue, and the credit and debit notes against them.',
        path: '/sales/finance',
        icon: <FinanceIcon />,
        moduleName: 'Accounts Receivable',
        keywords: ['ar', 'invoice', 'collections', 'aging', 'receivable', 'payment'],
      },
    ],
  },
  {
    key: 'customers',
    title: 'Customers & ownership',
    caption: 'Who you sell to, and which of your people owns them.',
    entries: [
      {
        key: 'customers',
        label: 'Customers',
        labelKey: 'customers',
        description: 'The customer record — contacts, registration details, addresses and history.',
        path: '/customers',
        icon: <CustomerIcon />,
        moduleName: 'Customers',
        keywords: ['client', 'account', 'buyer', 'company'],
      },
      {
        key: 'sales-accounts',
        label: 'Account ownership',
        description: 'Which rep owns which customer, and when that ownership was last reviewed.',
        path: '/sales/accounts',
        icon: <AccountOwnerIcon />,
        moduleName: 'Customers',
        keywords: ['ownership', 'account manager', 'assignment', 'territory'],
      },
      {
        key: 'sales-routing',
        label: 'Routing queue',
        description: 'Incoming enquiries waiting to be routed to an owner, and why routing paused.',
        path: '/sales/routing',
        icon: <RoutingIcon />,
        moduleName: 'Leads',
        keywords: ['routing', 'assign', 'queue', 'unrouted'],
        seeAlso: {
          label: 'Setup → RFQ Routing Rules',
          path: '/setup/routing-rules',
          note: 'sets the rules this queue applies',
        },
      },
    ],
  },
  {
    key: 'team',
    title: 'Team & exceptions',
    caption: 'Your people, their follow-ups, and the work that fell out of the normal path.',
    entries: [
      {
        key: 'sales-reps',
        label: 'Sales reps',
        description: 'The directory of reps, their coverage and their current load.',
        path: '/sales/reps',
        icon: <RepIcon />,
        moduleName: 'Users',
        keywords: ['rep', 'people', 'directory', 'salesperson'],
      },
      // NOTE: the old rail carried `/sales/team` TWICE — once as "Sales Manager Control Tower"
      // under Today and once as "Team Overview" here. Two labels for one screen is the exact
      // duplication that makes a rail untrustworthy, so it is listed once, under Daily role views,
      // carrying the keywords that used to find it from either place.
      {
        key: 'sales-follow-ups',
        label: 'Follow-ups',
        description: 'Quotes sent with no answer yet, ordered by how long they have been silent.',
        path: '/sales/follow-ups',
        icon: <FollowUpIcon />,
        moduleName: 'Quotations',
        keywords: ['follow up', 'chase', 'reminder', 'no reply', 'stale'],
      },
      {
        key: 'sales-exceptions',
        label: 'Commercial exceptions',
        description: 'Deals blocked by a rule — below-floor pricing, missing attestation, currency conflicts.',
        path: '/sales/exceptions',
        icon: <ExceptionIcon />,
        moduleName: 'Leads',
        keywords: ['exception', 'blocked', 'override', 'approval', 'breach'],
      },
      {
        key: 'human-actions',
        label: 'Human actions',
        description: 'Steps the platform deliberately will not take on its own and is waiting on a person for.',
        path: '/sales/actions',
        icon: <ApprovalsIcon />,
        moduleName: 'Leads',
        keywords: ['action', 'human', 'waiting', 'manual', 'intervention'],
      },
    ],
  },
  {
    key: 'tools',
    title: 'Assistants & tools',
    caption: 'Optional help. Nothing here is required to complete a quote.',
    entries: [
      {
        key: 'copilot-chat',
        label: 'Copilot',
        labelKey: 'copilot',
        description: 'Ask about sourcing in plain language; it answers from this workspace’s own data.',
        path: '/copilot',
        icon: <CopilotIcon />,
        moduleName: 'Dashboard',
        keywords: ['copilot', 'assistant', 'ai', 'chat', 'ask'],
      },
      {
        key: 'copilot-approvals',
        label: 'Copilot approvals',
        labelKey: 'approvals',
        description: 'Actions Copilot proposed that need a person to approve before they run.',
        path: '/copilot/approvals',
        icon: <ApprovalsIcon />,
        moduleName: 'Dashboard',
        keywords: ['approval', 'copilot', 'authorise', 'pending'],
      },
      {
        key: 'copilot-activity',
        label: 'Copilot activity',
        labelKey: 'activity',
        description: 'The record of everything Copilot has done and what it read to do it.',
        path: '/copilot/activity',
        icon: <ActivityIcon />,
        moduleName: 'Dashboard',
        keywords: ['activity', 'log', 'copilot', 'history', 'audit'],
      },
      {
        key: 'service-boqs',
        label: 'Service BOQs',
        labelKey: 'service_boqs',
        description: 'Bills of quantity for service work, priced the same way a product quote is.',
        path: '/services/boq',
        icon: <BoqIcon />,
        moduleName: 'Quotations',
        keywords: ['boq', 'bill of quantity', 'service', 'labour', 'scope'],
      },
      {
        key: 'commercial-memory',
        label: 'Commercial memory',
        description: 'What the platform has learned about products, suppliers, reps and customer outcomes.',
        path: '/intelligence/commercial-memory',
        icon: <MemoryIcon />,
        moduleName: 'Quotations',
        keywords: ['memory', 'learning', 'evaluation', 'intelligence', 'outcomes'],
      },
    ],
  },
  {
    key: 'stock',
    title: 'Catalogue & stock',
    caption: 'What you sell, what you hold, and where it is.',
    entries: [
      {
        key: 'products',
        label: 'Products',
        labelKey: 'products',
        description: 'The catalogue you quote against — part numbers, descriptions and default pricing.',
        path: '/inventory/products',
        icon: <InventoryIcon />,
        moduleName: 'Products',
        keywords: ['catalogue', 'product', 'part', 'item', 'sku'],
      },
      {
        key: 'categories',
        label: 'Product categories',
        labelKey: 'categories',
        description: 'The top-level grouping products are filed under.',
        path: '/inventory/categories',
        icon: <CategoryIcon />,
        moduleName: 'Product Categories',
        keywords: ['category', 'group', 'family'],
      },
      {
        key: 'sub-categories',
        label: 'Product sub-categories',
        labelKey: 'sub_categories',
        description: 'The second level of the product grouping.',
        path: '/inventory/sub-categories',
        icon: <CategoryIcon />,
        moduleName: 'Product Categories',
        keywords: ['sub category', 'subcategory', 'group'],
      },
      {
        key: 'inventory-overview',
        label: 'Stock overview',
        description: 'On hand, committed and incoming across every warehouse, in one figure each.',
        path: '/inventory/overview',
        icon: <InventoryIcon />,
        moduleName: 'Products',
        keywords: ['stock', 'overview', 'on hand', 'summary'],
      },
      {
        key: 'inventory-availability',
        label: 'Availability',
        description: 'What can actually be promised on a date, after reservations are taken off.',
        path: '/inventory/availability',
        icon: <AvailabilityIcon />,
        moduleName: 'Products',
        keywords: ['atp', 'available', 'promise', 'free stock'],
      },
      {
        key: 'inventory-warehouses',
        label: 'Warehouse stock',
        description: 'What is currently sitting in each warehouse.',
        path: '/inventory/warehouses',
        icon: <WarehouseIcon />,
        moduleName: 'Products',
        keywords: ['warehouse', 'store', 'location', 'depot'],
        seeAlso: {
          label: 'Setup → Warehouses',
          path: '/setup/warehouse',
          note: 'creates and retires the warehouses themselves',
        },
      },
      {
        key: 'inventory-reservations',
        label: 'Reservations',
        description: 'Stock held against a specific quote or order and not available to anyone else.',
        path: '/inventory/reservations',
        icon: <ReservationIcon />,
        moduleName: 'Products',
        keywords: ['reserve', 'allocation', 'committed', 'hold'],
      },
      {
        key: 'inventory-incoming',
        label: 'Incoming stock',
        description: 'Ordered from suppliers and not yet received, with expected dates.',
        path: '/inventory/incoming',
        icon: <IncomingIcon />,
        moduleName: 'Products',
        keywords: ['incoming', 'on order', 'inbound', 'eta'],
      },
      {
        key: 'inventory-movements',
        label: 'Stock movements',
        description: 'Every receipt, issue and transfer, with who did it and when.',
        path: '/inventory/movements',
        icon: <MovementIcon />,
        moduleName: 'Products',
        keywords: ['movement', 'transaction', 'receipt', 'issue', 'transfer', 'ledger'],
      },
      {
        key: 'inventory-demand',
        label: 'Demand',
        description: 'What has been asked for over time, so reorder levels can be set from evidence.',
        path: '/inventory/demand',
        icon: <DemandIcon />,
        moduleName: 'Products',
        keywords: ['demand', 'consumption', 'usage', 'forecast'],
      },
      {
        key: 'inventory-levels',
        label: 'Stock levels',
        description: 'The minimum and maximum each product should be held at.',
        path: '/inventory/levels',
        icon: <LevelsIcon />,
        moduleName: 'Products',
        keywords: ['min', 'max', 'reorder level', 'safety stock'],
      },
      {
        key: 'inventory-reorder-alerts',
        label: 'Reorder alerts',
        description: 'Products that have fallen below their minimum and what was done about it.',
        path: '/inventory/reorder-alerts',
        icon: <ReorderIcon />,
        moduleName: 'Products',
        keywords: ['reorder', 'alert', 'low stock', 'replenish'],
      },
      {
        key: 'inventory-count-variance',
        label: 'Count variance',
        description: 'Where a physical count disagreed with the system, and by how much.',
        path: '/inventory/count-variance',
        icon: <VarianceIcon />,
        moduleName: 'Products',
        keywords: ['count', 'variance', 'stocktake', 'discrepancy', 'audit'],
      },
      {
        key: 'inventory-ageing',
        label: 'Stock ageing',
        description: 'How long stock has been sitting, banded, so slow lines can be seen.',
        path: '/inventory/ageing',
        icon: <AgeingIcon />,
        moduleName: 'Products',
        keywords: ['ageing', 'aging', 'slow moving', 'obsolete', 'old stock'],
      },
      {
        key: 'inventory-resources',
        label: 'Related resources',
        description: 'Datasheets, drawings and certificates attached to catalogue items.',
        path: '/inventory/resources',
        icon: <ResourcesIcon />,
        moduleName: 'Products',
        keywords: ['datasheet', 'attachment', 'document', 'drawing', 'certificate'],
      },
      {
        key: 'inventory-lots',
        label: 'Lots & traceability',
        description: 'Batch and lot records, and what each one was consumed by.',
        path: '/inventory/lots',
        icon: <LotIcon />,
        moduleName: 'Products',
        keywords: ['lot', 'batch', 'serial', 'traceability', 'recall'],
      },
      {
        key: 'inventory-order-trace',
        label: 'Where-used trace',
        description: 'Given an order, every lot and movement that fulfilled it.',
        path: '/inventory/order-trace',
        icon: <TraceIcon />,
        moduleName: 'Products',
        keywords: ['trace', 'where used', 'genealogy', 'order trace'],
      },
    ],
  },
];

/** Every relocated destination, flattened — the directory page, search and the tests read this. */
export const ADVANCED_ENTRIES: NavEntry[] = ADVANCED_GROUPS.flatMap((group) => group.entries);

/** Every tab offered by a primary destination, flattened. */
export const PRIMARY_VIEWS: NavView[] = PRIMARY_NAV.flatMap((item) => item.views ?? []);

/** The rail row that owns `/advanced`. Rendered apart from the five, because it is a door, not a job. */
export const ALL_SCREENS_ENTRY = {
  key: 'all-screens',
  label: 'Screen directory',
  description: 'Find any Nexora workspace by name or business purpose.',
  icon: <AllScreensIcon />,
  path: ADVANCED_ROOT,
};

/** Case-insensitive match over label, description and keywords — same rule Setup search uses. */
export const navEntryMatches = (entry: NavEntry, query: string): boolean => {
  const needle = query.trim().toLowerCase();
  if (!needle) return true;
  const haystack = [entry.label, entry.description, ...(entry.keywords ?? [])].join(' ').toLowerCase();
  return needle.split(/\s+/).every((term) => haystack.includes(term));
};

/**
 * The entry's name in the reader's language, falling back to the English label.
 *
 * Takes i18next's `t` rather than calling a hook, so the catalogue stays plain data a test or the
 * sidebar can import without a React context.
 */
export const navEntryLabel = (
  entry: { label: string; labelKey?: string },
  t: (key: string, defaultValue: string) => string,
): string => (entry.labelKey ? t(entry.labelKey, entry.label) : entry.label);

/** Path without its query string — several entries deep-link into a shared screen. */
export const pathnameOf = (path: string): string => path.split('?')[0];
