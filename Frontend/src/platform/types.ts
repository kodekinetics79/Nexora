// ---------------------------------------------------------------------------
// Nexora Platform Owner Console — domain types
//
// These types mirror the platform API contract (`/api/platform/*`). They are
// the single source of truth shared by the typed API client and every page in
// `src/platform`. Casing matches the backend JSON (camelCased DTOs).
// ---------------------------------------------------------------------------

/**
 * A plan's tier is its own lowercased plan code straight from the backend
 * ("free", "pro", "enterprise", custom codes, …). Tenants without a plan are
 * reported as "none" — nothing is ever silently bucketed.
 */
export type PlanTier = string;

export type TenantStatus =
  | 'active'
  | 'trial'
  | 'past_due'
  | 'suspended'
  | 'provisioning'
  | 'archived';

export type HealthStatus = 'healthy' | 'degraded' | 'down';

export type JobStatus =
  | 'queued'
  | 'in_flight'
  | 'succeeded'
  | 'failed'
  | 'dead_letter';

export type AuditResult = 'success' | 'failure';

// --- Plans & entitlements ---------------------------------------------------

export interface Plan {
  id: string;
  name: string;
  /** Canonical lowercased plan code (unique). */
  code: string;
  tier: PlanTier;
  /** Scheduling weight used by the extraction dispatcher (higher = more share). */
  weight: number;
  /** Max concurrent extraction jobs allowed for a tenant on this plan. */
  concurrencyCap: number;
  /** Monthly document processing quota. `null` = unlimited. */
  monthlyDocQuota: number | null;
  /** Seat quota. `null` = unlimited. */
  seatQuota: number | null;
  priceMonthlyUsd: number | null;
  isActive: boolean;
  /** Feature-flag keys enabled (true) in the plan's features JSON. */
  entitlements: string[];
}

/** Body for POST /api/platform/plans and PUT /api/platform/plans/{id}. */
export interface UpsertPlanInput {
  code: string;
  name: string;
  weight: number;
  maxConcurrentExtractionJobs: number;
  maxDocsPerMonth: number;
  maxSeats: number;
  monthlyPriceUsd: number | null;
  /** JSON object of feature entitlements, e.g. `{"copilot": true}`. */
  features: string;
  isActive: boolean;
}

// --- Tenants ----------------------------------------------------------------

/**
 * How a tenant is charged. Anything other than `Billable` is service given away,
 * so the platform records a written reason alongside it and the console refuses
 * to create one without.
 */
export type BillingMode = 'Billable' | 'Trial' | 'Internal' | 'Partner';

export const BILLING_MODES: BillingMode[] = ['Billable', 'Trial', 'Internal', 'Partner'];

/**
 * The company behind a tenant. Every field is nullable because the registry is
 * older than these columns: a tenant provisioned before them holds nothing, and
 * the console renders "—" rather than inventing a value.
 */
export interface TenantCompanyProfile {
  legalName: string | null;
  registrationNumber: string | null;
  taxNumber: string | null;
  /** ISO-3166 alpha-2, upper-cased. */
  countryCode: string | null;
  industry: string | null;
  website: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  stateProvince: string | null;
  postalCode: string | null;
  phone: string | null;
  contactEmail: string | null;
  logoUrl: string | null;
}

/** The money side of a tenant: who pays, on what terms, from when. */
export interface TenantCommercialTerms {
  billingMode: BillingMode | null;
  billingModeReason: string | null;
  rateCardId: string | null;
  billingStartsOn: string | null; // ISO date
  /** Set only for trials. A trial without one is an unbounded giveaway. */
  trialEndsOn: string | null; // ISO date
  contractStartOn: string | null; // ISO date
  contractEndOn: string | null; // ISO date
  paymentTermsDays: number | null;
  purchaseOrderReference: string | null;
  billingContactName: string | null;
  billingContactEmail: string | null;
  billingAddress: string | null;
  /** Internal owner of the commercial relationship. */
  accountOwnerEmail: string | null;
  /** ISO-4217. */
  baseCurrencyCode: string | null;
  /** IANA time zone id. */
  timeZoneId: string | null;
  /** BCP-47 language tag. */
  locale: string | null;
  dataRegion: string | null;
}

/**
 * What a tenant's deployment is FOR, and therefore which production prerequisites its
 * activation decision may record as deferred.
 *
 * `PRODUCTION` is the default and the strict one: nothing is deferrable on it, ever. The
 * console never decides this — it renders what the server says, and the server is the only
 * thing that can change it.
 */
export type TenantDeploymentProfile = 'PRODUCTION' | 'LOCAL_TEST' | 'DEMO';

export const TENANT_DEPLOYMENT_PROFILES: TenantDeploymentProfile[] = ['PRODUCTION', 'LOCAL_TEST', 'DEMO'];

/** Set through the Owner-gated endpoint, which is the only writer of the approval fields. */
export interface SetTenantDeploymentProfileInput {
  profile: TenantDeploymentProfile;
  /** Required for anything other than PRODUCTION; at least 15 characters. */
  reason: string | null;
}

export interface Tenant extends TenantCompanyProfile, TenantCommercialTerms {
  id: string;
  deploymentProfile: TenantDeploymentProfile;
  deploymentProfileReason: string | null;
  /** Absent on a DEMO tenant means UNAPPROVED: the server defers nothing for it. */
  deploymentProfileApprovedBy: string | null;
  deploymentProfileApprovedOn: string | null;
  name: string;
  slug: string;
  planId: string | null;
  /** Lowercased plan code, or null when the tenant has no plan. */
  planCode: string | null;
  status: TenantStatus;
  statusReason: string | null;
  createdAt: string | null; // ISO
}

export interface UpdateTenantProfileInput extends TenantCompanyProfile {
  name: string;
  timeZoneId: string | null;
  locale: string | null;
  reason: string;
}

export interface TenantAdminInvitation {
  id: string;
  userId: string;
  email: string;
  status: string;
  issuedAtUtc: string;
  expiresAtUtc: string;
  redeemedAtUtc: string | null;
  revokedAtUtc: string | null;
  revokedBy: string | null;
  revocationReason: string | null;
  lastSentAtUtc: string | null;
  sendCount: number;
  issuedBy: string;
}

export interface ResendTenantAdminInvitationResult {
  invitation: TenantAdminInvitation;
  emailDispatched: boolean;
  /** Returned exactly once when the outbound provider did not transmit the message. */
  activationUrl: string | null;
}

/**
 * One account inside a customer's workspace — the customer's own staff, not a platform operator.
 * `PlatformOperator` is the other thing entirely, and the two must never be shown on one list.
 */
export interface TenantUser {
  id: string;
  firstName: string;
  middleName: string | null;
  lastName: string;
  email: string;
  roleId: string | null;
  roleCode: string | null;
  roleName: string | null;
  /** Setup_Master.RoleRank. 30 = Owner, 20 = Admin, 10 = Manager, 0 = Member. */
  roleRank: number | null;
  isActive: boolean;
  deactivatedAtUtc: string | null;
  lastLogin: string | null;
  createdOn: string;
  /** The most recent activation invitation for this account, whatever its status. */
  invitation: TenantAdminInvitation | null;
  /**
   * Invited and never redeemed: the account holds no credential anybody knows, so returning it
   * to service does not let the person sign in. Reissuing their invitation does.
   */
  awaitingActivation: boolean;
}

/** An assignable role from the tenant's own Setup_Master. */
export interface TenantRole {
  id: string;
  code: string | null;
  name: string;
  description: string | null;
  rank: number;
  rankLabel: string;
  activeUserCount: number;
  /** False when the signed-in operator's platform role is not senior enough to grant it. */
  grantable: boolean;
  notGrantableReason: string | null;
}

export interface CreateTenantUserInput {
  email: string;
  firstName: string;
  middleName?: string | null;
  lastName: string;
  roleId: string;
  timezone?: string | null;
  /** 'invite' (default) mails a single-use link; 'password' is Owner-only and audited as such. */
  activation?: 'invite' | 'password';
  password?: string | null;
  reason: string;
}

export interface CreateTenantUserResult {
  user: TenantUser;
  invitation: TenantAdminInvitation | null;
  emailDispatched: boolean;
  /** Returned exactly once, to an Owner, when the provider did not transmit the message. */
  activationUrl: string | null;
}

export interface BillingMeterCatalogEntry {
  eventType: string;
  billingMeterKey: string;
  unit: string;
  certification: 'BillingCertified' | 'Blocked' | 'NotImplemented';
}

export interface BillingReadinessFailure {
  code: string;
  meterKey: string;
  detail: string;
}

export interface BillingReadiness {
  ready: boolean;
  failures: BillingReadinessFailure[];
  manifestJson: string;
  manifestSha256: string;
}

export interface UsageRatingCorrection {
  id: string;
  tenantId: string;
  usageEventId: string;
  attemptNumber: number;
  status: string;
  reasonCode: string | null;
  rateCardId: string | null;
  rateCardLineId: string | null;
  rateCardVersion: number | null;
  currency: string;
  allowanceApplied: number;
  overageQuantity: number;
  unitPrice: number | null;
  ratedAmount: number | null;
  ratedAtUtc: string;
  ratedBy: string;
}

export interface DocumentCoveragePolicy {
  tenantId: string;
  meterKey: string;
  mode: string;
  proposedEffectiveAtUtc: string;
  proposedBy: string;
  proposedAtUtc: string;
  version: number;
}

export interface DocumentCoverageSegment {
  id: string;
  tenantId: string;
  meterKey: string;
  startUtc: string;
  endUtc: string;
  source: string;
  completeness: string;
  eventCount: number;
  quantityTotal: number;
  allowanceAppliedTotal: number;
  overageQuantityTotal: number;
  ratedAmountTotal: number;
  currency: string;
  reconciliation: string;
  evidenceSha256: string;
  rateLineageSha256: string;
}

export interface TenantDataAsset {
  id: string;
  tenantId: string;
  logicalKey: string;
  assetType:
    | 'PostgreSqlTenantScope' | 'ObjectStorage' | 'SearchIndex' | 'EmbeddingStore'
    | 'Cache' | 'QueuePayload' | 'GeneratedExport' | 'AiOcrProvider' | 'Subprocessor';
  opaqueProviderReference: string;
  region: string;
  classification: 'CustomerData' | 'DerivedCustomerData' | 'OperatorEvidence';
  disposition:
    | 'BackupRetainedUntilExpiryThenDestroy' | 'DestroyOnTenantPurge'
    | 'ProviderDeletionRequired' | 'PreserveOperatorEvidence';
  backupPolicyReference: string;
  backupPolicyVersion: number;
  status: 'Registered' | 'Verified';
  verifiedBusinessUnitId: string | null;
  verificationEvidenceReference: string | null;
  verificationEvidenceSha256: string | null;
  verificationVersion: number;
  verifiedOn: string | null;
  verifiedBy: string | null;
  version: number;
}

/**
 * One data boundary as the DEPLOYMENT declares it in `Platform:DataBoundaries`.
 *
 * Not a per-tenant fact and not editable from the console: it describes the estate this
 * installation runs on, it is identical for every tenant, and it is the answer the register form
 * used to ask an operator to remember.
 */
export interface PlatformDataBoundary {
  assetType: TenantDataAsset['assetType'];
  logicalKey: string;
  opaqueProviderReference: string;
  region: string;
  classification: TenantDataAsset['classification'];
  disposition: TenantDataAsset['disposition'];
  backupPolicyReference: string;
  backupPolicyVersion: number;
}

/**
 * What the running server can read about its OWN database — the Neon endpoint id and region an
 * operator would otherwise have to know. Always present; `isUsable` is false when the host shape
 * says nothing (a self-hosted box, an IP), and then `basis` is the sentence explaining what was
 * read and why nothing could be taken from it.
 */
export interface DatabaseSelfObservation {
  host: string | null;
  providerName: string | null;
  opaqueProviderReference: string | null;
  region: string | null;
  basis: string;
  isUsable: boolean;
}

export interface PlatformDataBoundaryManifest {
  /** False when this deployment has declared nothing, which is what keeps the manual form. */
  configured: boolean;
  /** Where the answer came from: an Owner in the console, deployment configuration, or nowhere. */
  source: 'console' | 'configuration' | 'none';
  primaryPostgreSqlScope: PlatformDataBoundary | null;
  boundaries: PlatformDataBoundary[];
  /** Declarations the server refused, with the reason. Empty is the normal case. */
  defects: { assetType: string; reason: string }[];
  observation: DatabaseSelfObservation;
  recordedBy: string | null;
  recordedOn: string | null;
  /** `observed-and-confirmed` when an Owner accepted what the server read; `entered` when typed. */
  recordedBasis: string | null;
  configurationKey: string;
}

/**
 * Recording it. Provider reference and region are omitted on the one-click path, which means
 * "what the server observed" and is recorded as such; sending them means the Owner typed them.
 */
export interface RecordPlatformDataBoundaryInput {
  opaqueProviderReference?: string | null;
  region?: string | null;
  backupPolicyReference: string;
  backupPolicyVersion: number;
  reason?: string | null;
}

/** What applying the manifest to one tenant actually did. */
export interface ApplyPlatformDataBoundariesResult {
  /** Non-null when the tenant carried no region and the deployment's was recorded. */
  dataRegionRecorded: string | null;
  primaryScopeState: string | null;
  evidenceReference: string | null;
  registeredLogicalKeys: string[];
  alreadyRegisteredLogicalKeys: string[];
  decision: TenantActivationDataDecision;
}

export interface TenantActivationDataDecision {
  tenantId: string;
  dataGateReady: boolean;
  decision: 'DataGateReady' | 'Blocked';
  blockers: string[];
  postgreSqlTenantScope: TenantDataAsset | null;
  boundary: string;
}

export interface RegisterTenantDataAssetInput {
  logicalKey: 'postgresql.primary';
  opaqueProviderReference: string;
  region: string;
  classification: 'CustomerData';
  disposition: 'BackupRetainedUntilExpiryThenDestroy';
  backupPolicyReference: string;
  backupPolicyVersion: number;
  reason: string;
}

export interface VerifyTenantDataAssetInput {
  expectedVersion: number;
  observedBusinessUnitId: string;
  observedRegion: string;
  evidenceReference: string;
  evidenceSha256: string;
  reason: string;
}

/**
 * What an unsatisfied control MEANS for this tenant's deployment profile. It never says
 * anything about whether the control passed — `satisfied` is still the strict answer.
 */
export type ActivationControlDisposition =
  | 'SATISFIED'
  | 'BLOCKING'
  /** Deferred: something this side could stand up and has not. Still a production blocker. */
  | 'DEFERRED'
  /** Deferred: the prerequisite belongs to a third party. Still a production blocker. */
  | 'EXTERNALLY_BLOCKED'
  /**
   * Not an activation gate in ANY profile, including PRODUCTION, and still a production blocker.
   * The tenant identity plane persists no MFA assurance, so `security.privileged-mfa-policy` gated
   * every switch-on on an attestation about a capability that does not exist. It is now a
   * certification requirement: the tenant can be activated without it and cannot be called
   * production-ready without it.
   */
  | 'CERTIFICATION_ONLY';

/** The console screen that owns the fix. Mirrors `ActivationRemediationSurfaces`. */
export type ActivationRemediationSurface =
  | 'tenant.activation'
  | 'tenant.profile-access'
  | 'tenant.commercial'
  | 'tenant.data-storage'
  | 'tenant.modules'
  | 'platform.plans';

/** The existing edit to take. Mirrors `ActivationRemediationActions`. */
export type ActivationRemediationAction =
  | 'tenant.profile-identity'
  | 'tenant.plan-assignment'
  | 'tenant.account-contact'
  | 'tenant.rate-card-pin'
  | 'tenant.commercial-terms'
  | 'tenant.data-asset-boundary'
  | 'tenant.activation-evidence'
  | 'tenant.module-grants'
  | 'platform.plan-entitlements';

/**
 * The server policy that will decide the remedy request. Mirrors
 * `ActivationRemediationAuthorities`, and the console gates its Resolve button on it rather
 * than guessing which role owns which control.
 */
export type ActivationRemediationAuthority = 'Owner' | 'Billing' | 'TenantAdmin' | 'OwnerMfa';

/**
 * Where an operator goes to fix one blocking control.
 *
 * The activation decision used to name its blockers as bare codes and left the operator to
 * work out which of eleven tabs owned each one. Every endpoint behind these already existed;
 * this is the sentence saying where.
 */
export interface ActivationControlRemediation {
  surface: ActivationRemediationSurface;
  action: ActivationRemediationAction;
  label: string;
  requiredAuthority: ActivationRemediationAuthority;
  hint: string;
}

export interface ActivationControlDecision {
  code: string;
  satisfied: boolean;
  detail: string;
  evidenceReferences: string[];
  disposition: ActivationControlDisposition;
  /** True for every unsatisfied control, in every profile. A deferral never clears it. */
  blocksProduction: boolean;
  /** The deployment-prerequisite key that explains a deferral, when there is one. */
  deferralKey: string | null;
  /** What production actually needs. Rendered verbatim next to a deferred control. */
  productionRequirement: string | null;
  /**
   * Null for a satisfied control — nothing to fix — and for the four controls that have no
   * resolver by design, where the server records the reason rather than leaving the absence to
   * be read as an oversight.
   */
  remediation: ActivationControlRemediation | null;
}

export interface DeploymentPrerequisiteStatus {
  key: string;
  title: string;
  /** Null when the prerequisite governs no activation control — it is answered for here only. */
  controlCode: string | null;
  satisfied: boolean;
  disposition: ActivationControlDisposition;
  productionRequirement: string;
  detail: string;
}

export interface ProductionReadinessCertification {
  /** True only when every control passes AND every deployment prerequisite is evidenced. */
  certifiable: boolean;
  blockingControls: string[];
  prerequisites: DeploymentPrerequisiteStatus[];
  detail: string;
}

export interface TenantActivationDecision {
  tenantId: string;
  /** Whether the tenant may be activated UNDER ITS OWN PROFILE. Never a production claim. */
  ready: boolean;
  commercialState: string;
  accessState: string;
  dataState: string;
  legalHoldState: string;
  controls: ActivationControlDecision[];
  blockingControls: string[];
  warnings: string[];
  policyVersion: string;
  evaluatedAtUtc: string;
  deploymentProfile: TenantDeploymentProfile;
  deploymentProfileDetail: string;
  /** Every unsatisfied control, regardless of profile. */
  productionBlockingControls: string[];
  deferredControls: string[];
  externallyBlockedControls: string[];
  /**
   * Controls that do not gate activation in any profile but do block certification. Overlaps
   * `productionBlockingControls` on purpose: "does not stop switch-on" and "stops certification"
   * are different answers and the panel needs both.
   */
  certificationOnlyControls: string[];
  productionReadiness: ProductionReadinessCertification;
}

export interface RecordActivationControlEvidenceInput {
  disposition: 'approved' | 'deferred';
  evidenceReference: string;
  evidenceSha256: string;
  effectiveFromUtc: string;
  effectiveToUtc: string | null;
  reason: string;
}

export interface ActivationControlEvidenceReceipt {
  tenantId: string;
  controlCode: string;
  disposition: 'approved' | 'deferred';
  evidenceReference: string;
  effectiveFromUtc: string;
  effectiveToUtc: string | null;
  policyVersion: string;
}

export type TenantDataRecoveryEvidenceType =
  | 'BackupSetObserved'
  | 'RestoreDrillCompleted'
  | 'TombstoneReapplied'
  | 'BackupDestructionConfirmed'
  | 'SubprocessorDeletionRequested'
  | 'SubprocessorDeletionConfirmed'
  | 'ResidencyVerified';

export interface TenantDataRecoveryEvidence {
  id: string;
  tenantId: string;
  tenantDataAssetId: string | null;
  scopeKey: string;
  evidenceType: TenantDataRecoveryEvidenceType;
  opaqueProviderReference: string;
  opaqueBackupSetReference: string | null;
  recoveryPointUtc: string | null;
  operationStartedUtc: string | null;
  completedUtc: string;
  configuredRpoSeconds: number | null;
  configuredRtoSeconds: number | null;
  actualRecoverySeconds: number | null;
  retainUntilUtc: string | null;
  customerRowsObserved: number | null;
  evidenceReference: string;
  evidenceSha256: string;
  correlationId: string;
  actorEmail: string;
  reason: string;
  recordedUtc: string;
}

export interface RecordTenantDataRecoveryEvidenceInput {
  tenantDataAssetId: number | null;
  scopeKey: string;
  evidenceType: TenantDataRecoveryEvidenceType;
  opaqueProviderReference: string;
  opaqueBackupSetReference: string | null;
  recoveryPointUtc: string | null;
  operationStartedUtc: string | null;
  completedUtc: string;
  configuredRpoSeconds: number | null;
  configuredRtoSeconds: number | null;
  retainUntilUtc: string | null;
  customerRowsObserved: number | null;
  evidenceReference: string;
  evidenceSha256: string;
  correlationId: string;
  idempotencyKey: string;
  reason: string;
}

export interface TenantDeletionCertificationDecision {
  tenantId: string;
  ready: boolean;
  blockers: string[];
  evidenceIds: string[];
  evaluatedUtc: string;
  boundary: string;
}

export interface TenantDeletionCertificate {
  id: string;
  tenantId: string;
  tenantSlug: string;
  purgedUtc: string;
  certifiedUtc: string;
  actorEmail: string;
  evidenceManifestSha256: string;
  evidenceIds: string[];
  reason: string;
}

// --- Platform operators (control-plane accounts) ----------------------------

export type PlatformOperatorRole =
  | 'Owner'
  | 'SupportAdmin'
  | 'BillingAdmin'
  | 'ReadOnlyOps';

export const PLATFORM_OPERATOR_ROLES: PlatformOperatorRole[] = [
  'Owner',
  'SupportAdmin',
  'BillingAdmin',
  'ReadOnlyOps',
];

export interface PlatformOperator {
  id: string;
  email: string;
  platformRole: string;
  isActive: boolean;
  displayName: string | null;
  lastLogin: string | null; // ISO
  createdOn: string; // ISO
}

export interface CreatePlatformOperatorInput {
  email: string;
  password: string;
  role: PlatformOperatorRole;
  displayName?: string;
}

// --- Extraction pipeline ----------------------------------------------------

export interface QueueStats {
  queueDepth: number;
  inFlight: number;
  deadLetter: number;
  processedLast24h: number;
  /** Null when nothing succeeded in the window — an idle pipeline is not a 0ms one. */
  avgLatencyMs: number | null;
  /** 0–1, or null when nothing reached a terminal state. Null is NOT 0%. */
  successRate: number | null;
}

export interface ExtractionJob {
  id: string;
  tenantId: string;
  tenantName: string;
  documentName: string;
  status: JobStatus;
  attempts: number;
  maxAttempts: number;
  enqueuedAt: string; // ISO
  updatedAt: string; // ISO
  latencyMs: number | null;
  /** Present when status is `failed` or `dead_letter`. */
  error: string | null;
}

export type PlatformDeadLetterQueue = 'extraction' | 'supplier-rfq' | 'quote-delivery';

export interface RecoverPlatformDeadLetterInput {
  queue: PlatformDeadLetterQueue;
  itemId: string;
  reason: string;
  idempotencyKey: string;
}

export interface RecoverPlatformDeadLetterResult {
  queue: PlatformDeadLetterQueue;
  itemId: string;
  tenantId: string;
  status: string;
  idempotentReplay: boolean;
}

// --- Audit ------------------------------------------------------------------

export interface AuditEntry {
  id: string;
  timestamp: string; // ISO
  actor: string;
  actorEmail: string;
  action: string;
  targetType: string;
  targetId: string;
  tenantId: string | null;
  tenantName: string | null;
  ipAddress: string;
  result: AuditResult;
  detail?: string;
  metadataDisclosed?: boolean;
  requiredPolicy?: string;
}

// --- Overview / system health ----------------------------------------------

export interface ServiceHealth {
  key: string;
  name: string;
  status: HealthStatus;
  latencyMs: number;
  detail: string;
}

/** Windows the overview endpoint accepts. Anything else is refused server-side. */
export const OVERVIEW_WINDOWS = [7, 14, 30, 90] as const;
export type OverviewWindow = (typeof OVERVIEW_WINDOWS)[number];

export type TenantLifecycle = 'Provisioning' | 'Active' | 'Suspended' | 'Archived' | 'PastDue';

export interface OverviewTenantActivity {
  tenantId: number;
  name: string;
  slug: string;
  status: TenantLifecycle;
  plan: string | null;
  docs: number;
  failures: number;
  rfqs: number;
  quotes: number;
  orders: number;
}

export interface OverviewCommercial {
  leadsCaptured: number;
  rfqsCaptured: number;
  quotesIssued: number;
  ordersWon: number;
  /** Share of RFQs raised in the window that now carry at least one quote. Null = empty cohort. */
  rfqsQuotedPct: number | null;
  /** Share of quotes issued in the window that now carry at least one order. Null = empty cohort. */
  quotesOrderedPct: number | null;
  /** Grouped BY currency and never summed across them — a fleet total would be fiction. */
  orderValueByCurrency: { currency: string; orders: number; amount: number }[];
}

export interface OverviewMetrics {
  /** When the server computed these figures. */
  asOfUtc: string;
  windowDays: OverviewWindow;
  windowStartUtc: string;

  tenantCount: number;
  activeTenants: number;
  /** Every lifecycle bucket, zero-filled — an empty bucket and an absent one differ. */
  tenantsByStatus: { status: TenantLifecycle; count: number }[];
  newTenantsInWindow: number;

  docsProcessedMtd: number;
  docsProcessedInWindow: number;
  /** Jobs that terminated in Failed or DeadLetter inside the window. */
  failuresInWindow: number;
  /** 0–1, or null when no job has ever reached a terminal state. Null is NOT 0%. */
  extractionSuccessRate: number | null;
  /** 0–1 over the selected window, or null when nothing terminated inside it. */
  extractionSuccessRateWindow: number | null;
  queueDepth: number;
  inFlight: number;
  deadLetter: number;
  /** Age of the oldest still-pending job; null when the queue is empty. */
  oldestPendingMinutes: number | null;

  llmCostMtdUsd: number;
  llmCostTrendPct: number | null; // vs prior comparable period
  /** Fleet-wide total of active tenant users across ALL business units. */
  activeUsersFleetWide: number;

  commercial: OverviewCommercial;

  health: { worst: HealthStatus; healthy: number; degraded: number; down: number };
  services: ServiceHealth[];

  /** Documents processed per day across the selected window. */
  throughput: { date: string; docs: number; failures: number }[];
  /** LLM spend per day (USD) across the selected window. */
  costTrend: { date: string; costUsd: number }[];
  /** Real plan codes present in the fleet; plan-less tenants appear as "none". */
  tenantsByPlan: { tier: PlanTier; count: number }[];
  /** Busiest tenants in the window, most active first. */
  topTenants: OverviewTenantActivity[];
}

// --- Impersonation ----------------------------------------------------------

export interface ImpersonationTicket {
  tenantId: string;
  /** Short-lived read-only tenant token. */
  token: string;
  expiresAt: string; // ISO
  /** Revocation key decoded from the token's `jti` claim (null if unreadable). */
  jti: string | null;
}

export type ImpersonationSessionStatus = 'active' | 'expired' | 'revoked';

export interface ImpersonationSession {
  jti: string;
  tenantId: string;
  tenantName: string | null;
  actorPlatformUserId: string;
  actorEmail: string | null;
  reason: string;
  issuedAtUtc: string; // ISO
  expiresAtUtc: string; // ISO
  revokedAtUtc: string | null;
  revokedBy: string | null;
  status: ImpersonationSessionStatus;
}

// --- Billing ----------------------------------------------------------------

export interface MeterReading {
  meterKey: string;
  quantity: number;
  unit: string;
  sourceNote: string;
}

export interface TenantUsageReadout {
  tenantId: string;
  businessUnitId: string | null;
  period: string; // YYYY-MM
  periodStartUtc: string;
  periodEndUtc: string;
  meters: MeterReading[];
}

export interface RateCardLine {
  id: string;
  meterKey: string;
  includedQuantity: number;
  unitPrice: number;
  unit: string;
  tierNote: string | null;
}

export interface RateCard {
  id: string;
  code: string;
  currency: string;
  effectiveFromUtc: string;
  effectiveToUtc: string | null;
  isActive: boolean;
  createdOn: string;
  createdBy: string | null;
  version: number;
  lines: RateCardLine[];
}

export interface RateCardLineInput {
  meterKey: string;
  includedQuantity: number;
  unitPrice: number;
  unit: string;
  tierNote: string | null;
}

export interface CreateRateCardInput {
  code: string;
  currency: string;
  effectiveFromUtc: string;
  effectiveToUtc: string | null;
  isActive: boolean;
  lines: RateCardLineInput[];
}

export type UpdateRateCardInput = Omit<CreateRateCardInput, 'code'>;

export type BillingStatementStatus = 'Draft' | 'Final';

export interface BillingStatementLine {
  meterKey: string;
  description: string;
  meteredQuantity: number;
  includedQuantity: number;
  billableQuantity: number;
  unitPrice: number;
  amount: number;
  sourceNote: string | null;
  /**
   * The meter's signal-coverage caveat, separate from provenance so a priced line still
   * visibly carries its "not billing ready" warning. Null when the signal is complete.
   */
  coverageNote: string | null;
}

export interface BillingStatement {
  id: string;
  tenantId: string;
  periodStartUtc: string;
  periodEndUtc: string;
  rateCardId: string;
  currency: string;
  status: string;
  totalAmount: number;
  computedAtUtc: string;
  computedBy: string;
  finalizedAtUtc: string | null;
  finalizedBy: string | null;
  lines: BillingStatementLine[];
}

/**
 * Cost vs revenue for a tenant-period. `aiCostTotal` and `grossMargin` are
 * null (never fabricated) whenever any settled AI request in the period is
 * unpriceable — the UI must render an honest "not priced" state.
 */
export interface TenantCostReport {
  tenantId: string;
  businessUnitId: string | null;
  period: string;
  statementTotal: number | null;
  statementStatus: string | null;
  statementCurrency: string | null;
  settledAiRequestCount: number;
  unpricedAiRequestCount: number;
  pricedAiCostSubtotal: number;
  aiCostTotal: number | null;
  grossMargin: number | null;
  note: string;
}

// --- Command inputs ---------------------------------------------------------

/**
 * How the founding administrator gets their first credential.
 *
 * `invite` sends them a single-use activation link and no password is ever created
 * platform-side, so nothing secret has to travel through the operator. `password`
 * keeps the older hand-carried path for customers who cannot receive mail from us.
 */
export type AdminActivationMode = 'invite' | 'password';

export interface ProvisionTenantInput extends TenantCompanyProfile {
  name: string;
  slug: string;

  /**
   * Persisted plan id. Null is only legitimate for a non-Billable tenant — a
   * billable workspace without a plan runs without quotas AND without a price.
   */
  planId: string | null;
  billingMode: BillingMode;
  /** Required for every mode except `Billable`; this is the giveaway's paper trail. */
  billingModeReason: string | null;
  rateCardId: string | null;
  billingStartsOn: string | null;
  trialEndsOn: string | null;
  contractStartOn: string | null;
  contractEndOn: string | null;
  paymentTermsDays: number | null;
  purchaseOrderReference: string | null;
  billingContactName: string | null;
  billingContactEmail: string | null;
  billingAddress: string | null;
  accountOwnerEmail: string | null;
  baseCurrencyCode: string | null;
  timeZoneId: string | null;
  locale: string | null;
  dataRegion: string | null;

  /**
   * Which activation gates this tenant is held to. Null means PRODUCTION, where nothing is
   * deferrable — the only safe default. Anything else is Owner-only on the server and needs
   * `deploymentProfileReason`, because it decides that catalogued production prerequisites
   * (a customer's storage estate, their identity provider, a tax authority) may be recorded as
   * deferred rather than blocking.
   */
  deploymentProfile: TenantDeploymentProfile | null;
  /** The approval recorded on the tenant. Required for every profile except PRODUCTION. */
  deploymentProfileReason: string | null;

  /**
   * The tenant's founding Super Administrator. Required: a tenant without one is a shell
   * nobody can log into, which is the state every portal-provisioned tenant used to land in.
   */
  adminEmail: string;
  adminFirstName: string;
  adminLastName: string;
  adminJobTitle: string | null;
  adminPhone: string | null;
  adminActivation: AdminActivationMode;
  /** Only meaningful on the `password` path; null there asks the server to generate one. */
  adminPassword: string | null;
}

export interface FoundingAdminInvitation {
  expiresAtUtc: string; // ISO
  /**
   * Single-use link the administrator opens to choose their own password.
   *
   * NULL on the ordinary path. The server populates it ONLY when `emailSent` is
   * false — a live activation link is a bearer credential, and it is put in front
   * of an operator only when the mail did not go out and somebody has to deliver
   * it another way. Declaring it non-nullable is what made the handover screen
   * render an empty box under an "Activation link" heading, with a Copy button
   * that copied `undefined`, on every successful invite.
   */
  activationUrl: string | null;
  /** Whether the configured mail provider accepted the invitation for delivery. */
  emailSent: boolean;
}

export interface FoundingAdmin {
  userId: string;
  email: string;
  roleName: string;
  /**
   * Present ONLY when the server generated the password, and only in the provisioning
   * response. It is stored as a BCrypt hash and can never be retrieved again — if it is lost
   * before handover, the credential must be reset rather than looked up.
   */
  generatedPassword: string | null;
  /** Present ONLY on the invite path, where no password exists yet. */
  invitation: FoundingAdminInvitation | null;
}

/**
 * What provisioning actually seeded. The operator needs this to answer "is the
 * workspace usable?" without signing in as the customer to check.
 */
export interface ProvisionedBaseline {
  quoteConfiguration: boolean;
  baseCurrency: string | null;
  unitsOfMeasure: number;
  roles: number;
  permissionGrants: number;
  leadReferencePrefix: string | null;
}

/** The server's own reading of the commercial terms, including what it objected to. */
export interface ProvisionedBilling {
  mode: string;
  planCode: string | null;
  rateCardCode: string | null;
  billingStartsOn: string | null;
  /** Revenue risks the server detected but did not block on. Always shown to the operator. */
  warnings: string[];
}

export interface ProvisionTenantResult {
  tenant: Tenant;
  foundingAdmin: FoundingAdmin;
  /** Null when the server did not report one — the checklist then says so instead of claiming success. */
  baseline: ProvisionedBaseline | null;
  billing: ProvisionedBilling | null;
}

// --- Durable provisioning ---------------------------------------------------

/**
 * The wire body of `ProvisionTenantRequest`. It is the payload of both the durable
 * submit and a saved draft, so it is named rather than inlined: a draft loaded back
 * into the wizard has to be reconstructed from exactly these fields.
 */
export interface ProvisionTenantRequestBody {
  name: string;
  deploymentProfile?: string | null;
  deploymentProfileReason?: string | null;
  slug: string | null;
  legalName: string | null;
  registrationNumber: string | null;
  taxNumber: string | null;
  countryCode: string | null;
  industry: string | null;
  website: string | null;
  addressLine1: string | null;
  addressLine2: string | null;
  city: string | null;
  stateProvince: string | null;
  postalCode: string | null;
  phone: string | null;
  contactEmail: string | null;
  logoUrl: string | null;

  baseCurrencyCode: string | null;
  timeZoneId: string | null;
  locale: string | null;
  dataRegion: string | null;

  /** Numeric on the wire; the console keeps ids as strings everywhere else. */
  planId: number | null;
  billingMode: string | null;
  billingModeReason: string | null;
  rateCardId: number | null;
  billingStartsOn: string | null;
  trialEndsOn: string | null;
  contractStartOn: string | null;
  contractEndOn: string | null;
  paymentTermsDays: number | null;
  purchaseOrderReference: string | null;
  billingContactName: string | null;
  billingContactEmail: string | null;
  billingAddress: string | null;
  accountOwnerEmail: string | null;

  adminEmail: string;
  adminFirstName: string;
  adminLastName: string;
  adminJobTitle: string | null;
  adminPhone: string | null;
  adminActivation: string | null;
  adminPassword: string | null;
}

export type ProvisioningExecutionState =
  | 'Pending'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Cancelled';

/**
 * `Skipped` is a real outcome, not an absence: the invitation step does not run on the
 * password path, and a blank row there would look identical to a step that never started.
 */
export type ProvisioningStepStatus =
  | 'Pending'
  | 'Running'
  | 'Succeeded'
  | 'Failed'
  | 'Skipped'
  | 'Cancelled';

export interface ProvisioningStep {
  /** Stable step code, e.g. "founding-admin". The retry request names this. */
  step: string;
  /** Sentence-case label, served by the API so every client says the same thing. */
  label: string;
  ordinal: number;
  status: ProvisioningStepStatus;
  attemptCount: number;
  startedOn: string | null;
  completedOn: string | null;
  durationMs: number | null;
  failureCode: string | null;
  failureReason: string | null;
  /** Raw JSON evidence the step produced — row counts, ids, expiries. */
  detail: string | null;
  /**
   * False when re-running would duplicate rather than repair. The console must never
   * offer a retry that makes the situation worse.
   */
  isRetriable: boolean;
}

export interface ProvisioningExecution {
  id: string;
  state: ProvisioningExecutionState;
  slug: string;
  name: string;
  adminEmail: string;
  adminActivation: string;
  currentStep: string | null;
  failedStep: string | null;
  failureReason: string | null;
  /** True when retrying is pointless — the slug or address now belongs to somebody else. */
  failureIsTerminal: boolean;
  tenantId: string | null;
  provisionedBusinessUnitId: string | null;
  foundingUserId: string | null;
  correlationId: string;
  requestedBy: string;
  createdOn: string;
  startedOn: string | null;
  completedOn: string | null;
  attemptCount: number;
  cancelledBy: string | null;
  cancellationReason: string | null;
  steps: ProvisioningStep[];
  completedStepCount: number;
  totalStepCount: number;
}

export interface SubmitProvisioningResult {
  execution: ProvisioningExecution;
  /** False when an identical request had already been accepted under the same key. */
  created: boolean;
  /**
   * Present ONLY on the call that created the execution, and nowhere else ever: the
   * server stores a BCrypt hash and a replay deliberately returns null rather than
   * turning an idempotency key into a credential-retrieval endpoint.
   */
  generatedPassword: string | null;
  /** Explains a null password or activation link so it never reads as a failure. */
  secretNotice: string | null;
}

// --- Provisioning diagnostics ------------------------------------------------
//
// The read model that replaced "Provisioning failed." Every field below was already
// persisted and none of it reached a human, which is why one sentence had to stand in
// for four unrelated causes with four different owners.

/** Whose problem this is — the only question that decides who has to act. */
export type ProvisioningIssueClassification =
  /** Execution is queued, running, or complete without a provisioning failure. */
  | 'NO_FAILURE'
  /** An operator cancelled execution; it cannot be resumed. */
  | 'CANCELLED'
  /** The submitted request has to change. No retry helps. */
  | 'CUSTOMER_INPUT'
  /** This deployment is wired wrong: a missing grant, a worker that is switched off. */
  | 'PLATFORM_CONFIGURATION'
  /** Something outside this process refused or never answered. */
  | 'EXTERNAL_DEPENDENCY'
  /** Unclassified and non-terminal. Retrying is the correct first move. */
  | 'RETRYABLE_SYSTEM_FAILURE';

export interface ProvisioningStepDiagnostic {
  step: string;
  label: string;
  ordinal: number;
  status: ProvisioningStepStatus;
  attemptCount: number;
  startedOn: string | null;
  completedOn: string | null;
  durationMs: number | null;
  failureCode: string | null;
  failureReason: string | null;
}

export interface ProvisioningRecoveryAction {
  action: 'resume' | 'retry-step';
  step: string | null;
  /** False when the server would refuse outright. The console must say why, never just disable. */
  available: boolean;
  /** False when the server would accept it but it cannot succeed or would not help. */
  safe: boolean;
  detail: string;
}

export interface ProvisioningBlocker {
  code: string;
  scope: TenantDeploymentProfile;
  disposition: ActivationControlDisposition;
  detail: string;
  productionRequirement: string | null;
}

export interface TenantProvisioningDiagnostics {
  tenantId: string | null;
  tenantName: string | null;
  tenantStatus: string | null;
  deploymentProfile: TenantDeploymentProfile;
  executionId: string | null;
  /** An execution state, or `NotStarted` when the tenant has no durable execution at all. */
  status: ProvisioningExecutionState | 'NotStarted';
  currentStep: string | null;
  steps: ProvisioningStepDiagnostic[];
  completedSteps: string[];
  failedStep: ProvisioningStepDiagnostic | null;
  failureReason: string | null;
  failureCode: string | null;
  /** The one thing that has to exist before this can succeed, named. */
  missingPrerequisite: string | null;
  classification: ProvisioningIssueClassification;
  classificationDetail: string;
  correlationId: string | null;
  attemptCount: number;
  completedStepCount: number;
  totalStepCount: number;
  startedOn: string | null;
  completedOn: string | null;
  recoveryActions: ProvisioningRecoveryAction[];
  productionBlockers: ProvisioningBlocker[];
  localTestBlockers: ProvisioningBlocker[];
  /** Why the two lists are empty for a reason other than "there are none". */
  blockersUnavailableReason: string | null;
  evaluatedAtUtc: string;
}

export interface SlugAvailability {
  /** The normalised address the request would actually get. */
  slug: string | null;
  isAvailable: boolean;
  /** A `SlugRefusalReason` name; "None" when available. */
  reason: string;
  message: string | null;
}

export interface ProvisioningDraftSummary {
  id: string;
  name: string;
  ownerEmail: string;
  createdOn: string;
  updatedOn: string;
  /** Sent back on save so two tabs on one draft cannot silently overwrite each other. */
  version: number;
  submittedExecutionId: string | null;
  /** Populated on load, omitted on list. */
  payload: ProvisionTenantRequestBody | null;
}

// --- Tenant offboarding -----------------------------------------------------

/** The destruction axis. Orthogonal to `TenantStatus`, which stays Archived throughout. */
export type TenantOffboardingStage = 'NotScheduled' | 'PendingDeletion' | 'Purged';

export interface TenantLifecycleEvent {
  id: string;
  action: string;
  fromStage: string | null;
  toStage: string | null;
  tenantStatus: string;
  reason: string;
  actorEmail: string;
  detail: string | null;
  occurredOn: string;
}

export interface TenantExportReceipt {
  id: string;
  requestedOn: string;
  completedOn: string;
  requestedBy: string;
  totalRows: number;
  sizeBytes: number;
  /** SHA-256 of the bytes handed back. The proof of what was exported. */
  contentSha256: string;
  format: string;
  sections: string;
}

export interface TenantLegalHold {
  id: string;
  tenantId: string;
  scope: string;
  authority: string;
  reason: string;
  evidenceReference: string;
  placedOn: string;
  placedBy: string;
  isActive: boolean;
  releasedOn: string | null;
  releasedBy: string | null;
  releaseReason: string | null;
}

export type SubscriptionInvoiceStatus =
  | 'Draft'
  | 'Finalized'
  | 'PartiallyPaid'
  | 'Paid'
  | 'Void'
  | 'Corrected';

export interface SubscriptionCreditNote {
  id: string;
  creditNumber: string;
  amount: number;
  reason: string;
  createdBy: string;
  createdAtUtc: string;
}

export interface SubscriptionPayment {
  id: string;
  externalReference: string;
  amount: number;
  receivedAtUtc: string;
  recordedBy: string;
}

export type SubscriptionRevenueActionKind = 'Void' | 'Refund' | 'PaymentReversal' | 'WriteOff' | 'Dunning';
export interface SubscriptionRevenueAction {
  id: string;
  invoiceId: string;
  kind: SubscriptionRevenueActionKind;
  status: 'Proposed' | 'Approved' | 'Completed' | 'Failed';
  amount: number;
  currency: string;
  reason: string;
  evidenceSha256: string;
  externalReference: string | null;
  proposedByPlatformUserId: number | null;
  proposedAtUtc: string;
  approvedByPlatformUserId: number | null;
  approvedAtUtc: string | null;
  completedAtUtc: string | null;
}

export interface SubscriptionInvoice {
  id: string;
  tenantId: string;
  statementId: string;
  invoiceNumber: string;
  status: SubscriptionInvoiceStatus;
  currency: string;
  subtotal: number;
  taxRatePercent: number;
  taxAmount: number;
  totalAmount: number;
  creditedAmount: number;
  paidAmount: number;
  refundedAmount: number;
  reversedPaymentAmount: number;
  writtenOffAmount: number;
  outstandingAmount: number;
  issuedAtUtc: string;
  dueAtUtc: string;
  taxTreatment: string;
  taxJurisdictionCode: string | null;
  taxRuleId: string | null;
  taxRuleVersion: number | null;
  taxEvidenceSha256: string | null;
  taxDeterminedAtUtc: string | null;
  sourceEvidenceSha256: string;
  createdBy: string;
  createdAtUtc: string;
  finalizedBy: string | null;
  finalizedAtUtc: string | null;
  version: number;
  credits: SubscriptionCreditNote[];
  payments: SubscriptionPayment[];
  revenueActions: SubscriptionRevenueAction[];
}

export interface CreateSubscriptionInvoiceInput {
  statementId: string;
  taxRatePercent: number;
  taxTreatment: string;
  sellerLegalName: string;
  sellerTaxNumber: string;
  taxJurisdictionCode: string;
}

export interface TenantAiPolicy {
  businessUnitId: string;
  isEnabled: boolean;
  externalProcessingAllowed: boolean;
  allowedPurposes: string[];
  allowedProvider: string | null;
  allowedModel: string | null;
  monthlySoftTokenLimit: number | null;
  monthlyHardTokenLimit: number | null;
  maxTokensPerDocument: number | null;
  externalInputCostPerMillionTokens: number | null;
  externalOutputCostPerMillionTokens: number | null;
  externalCostCurrency: string | null;
  externalPricingVersion: string | null;
  externalDependencyCeilingPercent: number;
  redactionRequired: boolean;
  allowedDataClassifications: string;
  egressPolicy: string;
  dataResidency: string;
  retentionDays: number;
  inputOutputAuditAllowed: boolean;
  privacyReviewRequired: boolean;
  localComputeCostPerHour: number | null;
  ocrCostPerPage: number | null;
  localCostCurrency: string | null;
  version: number;
  updatedOn: string;
  updatedBy: string;
}

export type UpdateTenantAiPolicyInput = Omit<TenantAiPolicy, 'businessUnitId' | 'updatedOn' | 'updatedBy'> & {
  reason: string;
};

export interface AiProviderAuthorization {
  id: string;
  provider: string;
  endpoint: string;
  model: string;
  allowedPurposes: string;
  unstructuredDocumentsAllowed: boolean;
  justification: string;
  authorizedByUserId: string;
  authorizedBy: string;
  authorizedOn: string;
  expiresOn: string | null;
  revokedOn: string | null;
  revokedBy: string | null;
  revocationReason: string | null;
  isActive: boolean;
  version: number;
  updatedOn: string;
}

export interface AiProviderTrustView {
  resolvedProvider: {
    provider: string;
    endpoint: string;
    model: string;
    providerClass: string;
    classificationReason: string;
    isResolved: boolean;
  };
  resolvedProviderIsAuthorizedForUnstructured: boolean;
  resolvedProviderDecisionReason: string;
  authorizations: AiProviderAuthorization[];
}

/**
 * How one control in the extraction chain stands. None of the three non-Pass states below is a
 * pass, and none of them is a failure either — telling them apart is what stops the report
 * over-stating the work.
 *
 * `NotApplicable`: the control cannot bite in this configuration at all — a loopback deployment
 * egresses nothing, so its egress controls are greyed rather than ticked.
 *
 * `Blocked`: the control applies and simply was not reached, because one above it is closed.
 * Opening that one settles this row with nobody touching it, so it is reported and never
 * counted as a blocker.
 *
 * `Warn`: open, and still a decision somebody owes — a control satisfied only because nobody
 * set it, where the unset value carries a standing cost. It never makes the report un-ready.
 */
export type AiReadinessStatus = 'Pass' | 'Fail' | 'NotApplicable' | 'Warn' | 'Blocked';

export interface AiExtractionReadinessCheck {
  order: number;
  code: string;
  title: string;
  status: AiReadinessStatus;
  /** The exact code the enforcing layer emits, so a row can be matched against a dead-lettered job. */
  denialReason: string | null;
  currentValue: string;
  requiredValue: string;
  setItIn: string;
  detail: string;
}

/**
 * Every control that must agree before an unstructured RFQ document can be read by AI, in the
 * order it fires. Read-only: the server evaluates and reports, and never remediates.
 */
export interface AiExtractionReadinessReport {
  resolvedProvider: AiProviderTrustView['resolvedProvider'];
  purpose: string;
  unstructuredPayload: boolean;
  ready: boolean;
  firstBlockingReason: string | null;
  /** Root causes only: controls closed on their own account, not ones waiting on those. */
  blockingCount: number;
  /** Open, but carrying a standing decision. Never affects `ready`. */
  warningCount: number;
  evaluatedOnUtc: string;
  checks: AiExtractionReadinessCheck[];
}

export interface AuthorizeAiProviderInput {
  provider: string;
  endpoint: string;
  model: string | null;
  allowedPurposes: string;
  unstructuredDocumentsAllowed: boolean;
  justification: string;
  expiresOn: string | null;
}

export interface TenantOffboardingStatus {
  tenantId: string;
  tenantName: string;
  tenantSlug: string;
  tenantStatus: string;
  stage: TenantOffboardingStage;

  retentionDays: number | null;
  deletionScheduledOn: string | null;
  purgeEligibleOn: string | null;
  isPurgeEligible: boolean;
  daysUntilPurgeEligible: number | null;
  deletionReason: string | null;
  deletionScheduledBy: string | null;

  purgedOn: string | null;
  purgedBy: string | null;
  purgedRowCount: number | null;
  personalDataErasedOn: string | null;
  personalDataErasedBy: string | null;
  erasedIdentityCount: number | null;
  lastExportedOn: string | null;
  lastExportedBy: string | null;

  /**
   * Resolved server-side. The console renders buttons from THESE and never re-derives
   * them: a client that recomputes the state machine is a client that will eventually
   * disagree with the server about whether a purge is legal.
   */
  canScheduleDeletion: boolean;
  canCancelDeletion: boolean;
  canPurge: boolean;
  canErasePersonalData: boolean;

  /**
   * The signed-in operator is the one who scheduled this deletion, so the server will refuse
   * their purge for want of a second approver. Reported separately from `canPurge` on purpose:
   * the tenant IS purgeable, just not by this person, and folding the two together would have the
   * console blame a retention clock that has already run out.
   */
  purgeRequiresDifferentApprover: boolean;

  /** Who scheduled the deletion, from the append-only lifecycle event. */
  deletionApprovedBy: string | null;

  /** The exact string the operator must type to purge or erase — the tenant's name. */
  confirmationRequired: string;

  history: TenantLifecycleEvent[];
  exports: TenantExportReceipt[];
  disclosures: string[];

  /** Server-resolved facts; the client never infers a billing waiver from an empty screen. */
  commercialEvidenceRequired: boolean;
  canAttestNonCustomer: boolean;
  nonCustomerAttestedOn: string | null;
  nonCustomerAttestedBy: string | null;
  billingStatementCount: number;
  subscriptionInvoiceCount: number;
  readinessFailures: Array<{ code: string; detail: string }>;
}

export interface PendingTenantDeletion {
  tenantId: string;
  tenantName: string;
  tenantSlug: string;
  deletionScheduledOn: string | null;
  purgeEligibleOn: string | null;
  isPurgeEligible: boolean;
  daysUntilPurgeEligible: number | null;
  deletionReason: string | null;
  deletionScheduledBy: string | null;
}

export interface TenantPurgeTableCount {
  table: string;
  tenantColumn: string;
  rows: number;
}

export interface TenantPurgePreview {
  tenantId: string;
  businessUnitId: string;
  tables: TenantPurgeTableCount[];
  totalRows: number;
  /** What the purge deliberately leaves standing. */
  preserved: string[];
  /** The same set with the reason each entry survives, so the operator can repeat it. */
  preservedDetail: TenantPurgePreservedTable[];
}

export interface TenantPurgePreservedTable {
  table: string;
  reason: string;
}

export interface TenantPurgeResult {
  tenantId: string;
  tenantSlug: string;
  rowsDeleted: number;
  tablesTouched: number;
  tables: TenantPurgeTableCount[];
  lifecycleEventsRetained: number;
  platformAuditRecordsRetained: number;
  supportTicketsRedacted: number;
  supportNotesErased: number;
  summary: string;
  disclosures: string[];
}

export interface TenantErasureTarget {
  target: string;
  identitiesErased: number;
  description: string;
}

export interface TenantErasureResult {
  tenantId: string;
  identitiesErased: number;
  targets: TenantErasureTarget[];
  summary: string;
  disclosures: string[];
}

/** The export is a download, not a stored artefact — the receipt is what persists. */
export interface TenantExportDownload {
  blob: Blob;
  filename: string;
  /** From `X-Nexora-Export-Sha256`; null when the header did not survive a proxy. */
  sha256: string | null;
  receiptId: string | null;
  totalRows: number | null;
}

// --- Billing: revenue risk & commercial terms -------------------------------

/**
 * The remediation badge, derived server-side from the tenant row rather than stored,
 * so it cannot be dismissed while its cause remains.
 */
export type CommercialConfigurationState = 'complete' | 'plan-missing' | 'exemption-unrecorded';

export interface TenantRevenueRisk {
  tenantId: string;
  tenantName: string;
  tenantSlug: string;
  tenantStatus: string;
  billingMode: string;
  billingModeReason: string | null;
  planId: string | null;
  planCode: string | null;
  planMonthlyPriceUsd: number | null;
  pinnedRateCardId: string | null;
  pinnedRateCardCode: string | null;
  billingStartsOn: string | null;
  trialEndsOn: string | null;
  trialExpired: boolean;
  trialDaysRemaining: number | null;
  lastStatementPeriod: string | null;
  lastStatementStatus: string | null;
  lastStatementTotal: number | null;
  lastStatementComputedAtUtc: string | null;
  lastStatementRateCardId: string | null;
  lastStatementCharged: boolean;
  atRisk: boolean;
  /** Machine-readable causes, e.g. "no-plan", "trial-expired". */
  leakReasons: string[];
  commercialConfigurationState: string;
  commercialConfigurationRequired: boolean;
}

export interface RevenueRiskReport {
  generatedAtUtc: string;
  /** Always over the WHOLE fleet, never the filtered list. */
  tenantCount: number;
  atRiskCount: number;
  expiredTrialCount: number;
  billableTenantsChargedNothingCount: number;
  commercialConfigurationRequiredCount: number;
  tenants: TenantRevenueRisk[];
}

export interface BillingStatementSummary {
  id: string;
  period: string; // YYYY-MM
  periodStartUtc: string;
  periodEndUtc: string;
  rateCardId: string;
  currency: string;
  status: string;
  totalAmount: number;
  computedAtUtc: string;
  computedBy: string;
  finalizedAtUtc: string | null;
  finalizedBy: string | null;
}

export interface TenantBillingProfile {
  tenantId: string;
  name: string;
  slug: string;
  status: string;
  billingMode: string;
  billingModeReason: string | null;
  planId: string | null;
  planCode: string | null;
  planName: string | null;
  planMonthlyPriceUsd: number | null;
  pinnedRateCardId: string | null;
  pinnedRateCardCode: string | null;
  /** The tenant carries a rate-card id that no longer resolves — billing refuses to compute. */
  pinnedRateCardMissing: boolean;
  billingStartsOn: string | null;
  trialEndsOn: string | null;
  contractStartOn: string | null;
  contractEndOn: string | null;
  paymentTermsDays: number | null;
  purchaseOrderReference: string | null;
  billingContactName: string | null;
  billingContactEmail: string | null;
  billingAddress: string | null;
  accountOwnerEmail: string | null;
  revenueRisk: TenantRevenueRisk;
  statements: BillingStatementSummary[];
}

export interface SetCommercialTermsInput {
  billingMode: BillingMode;
  billingModeReason: string | null;
  trialEndsOn: string | null;
  billingStartsOn: string | null;
}

/**
 * Who is invoiced, where, on what terms, under which contract, and who owns the account here.
 *
 * Every field is sent every time — the server treats an omitted value as a clear, because on a
 * set where clearing one field stops the customer being invoiced, "left blank" and "meant to be
 * empty" must not look the same.
 */
export interface SetAccountContactInput {
  billingContactName: string | null;
  billingContactEmail: string | null;
  billingAddress: string | null;
  purchaseOrderReference: string | null;
  paymentTermsDays: number | null;
  accountOwnerEmail: string | null;
  contractStartOn: string | null;
  contractEndOn: string | null;
  reason: string;
}

/** A governed correction to the tenant's contractual data region. Owner-only. */
export interface UpdateTenantDataRegionInput {
  dataRegion: string | null;
  reason: string;
}

// --- Support desk -----------------------------------------------------------

export type SupportTicketStatus = 'New' | 'Open' | 'Pending' | 'Resolved' | 'Closed';

export const SUPPORT_TICKET_STATUSES: SupportTicketStatus[] = [
  'New',
  'Open',
  'Pending',
  'Resolved',
  'Closed',
];

/** Ordered most urgent first, which is also how the queue sorts. */
export type SupportTicketSeverity = 'Critical' | 'High' | 'Normal' | 'Low';

export const SUPPORT_TICKET_SEVERITIES: SupportTicketSeverity[] = [
  'Critical',
  'High',
  'Normal',
  'Low',
];

export interface SupportTicketSummary {
  id: string;
  tenantId: string;
  tenantName: string | null;
  tenantSlug: string | null;
  /** Carried on every row: "cannot log in" on a Suspended tenant is an invoice, not a bug. */
  tenantStatus: string | null;
  subject: string;
  severity: string;
  status: string;
  origin: string;
  assignedToPlatformUserId: string | null;
  assignedToEmail: string | null;
  openedByPlatformUserId: string | null;
  openedByEmail: string | null;
  requesterEmail: string | null;
  createdAtUtc: string;
  updatedAtUtc: string;
  firstRespondedAtUtc: string | null;
  resolvedAtUtc: string | null;
  closedAtUtc: string | null;
  noteCount: number;
  linkCount: number;
  isRedacted: boolean;
  /** Optimistic-concurrency token; sent back on every mutation. */
  version: number;
}

export interface SupportTicketNote {
  id: string;
  authorPlatformUserId: string | null;
  authorKind: string;
  authorLabel: string;
  body: string;
  isInternal: boolean;
  createdAtUtc: string;
}

export interface SupportTicketLink {
  id: string;
  kind: string;
  targetKey: string;
  note: string | null;
  linkedByLabel: string;
  linkedAtUtc: string;
  /** Null when the target no longer resolves — information, not an error. */
  targetSummary: string | null;
  targetOccurredAtUtc: string | null;
}

export interface SupportTicketDetail extends SupportTicketSummary {
  body: string | null;
  resolution: string | null;
  requesterTenantUserId: string | null;
  redactedReason: string | null;
  redactedAtUtc: string | null;
  notes: SupportTicketNote[];
  links: SupportTicketLink[];
  /** Served from the server's lifecycle graph; the console never hard-codes it. */
  allowedTransitions: string[];
}

export interface SupportTicketTimelineEntry {
  /** "note" or "audit". */
  kind: string;
  id: string;
  occurredAtUtc: string;
  action: string;
  actor: string | null;
  body: string | null;
  result: string | null;
  metadata: unknown;
}

export interface SupportTicketTimeline {
  ticketId: string;
  tenantId: string;
  entries: SupportTicketTimelineEntry[];
}

export interface CreateSupportTicketInput {
  tenantId: string;
  subject: string;
  body: string;
  severity: SupportTicketSeverity;
  requesterEmail: string | null;
  assignToPlatformUserId: string | null;
}

// --- Audit explorer ---------------------------------------------------------

export interface PagedResult<T> {
  items: T[];
  page: number;
  pageSize: number;
  totalCount: number;
  hasMore: boolean;
}

export interface PlatformAuditEntry {
  id: string;
  occurredAtUtc: string;
  actorPlatformUserId: string;
  actor: string;
  actorEmail: string | null;
  action: string;
  targetType: string | null;
  targetId: string | null;
  tenantId: string | null;
  tenantName: string | null;
  ip: string | null;
  result: string;
  /**
   * Structured JSON, already parsed by the server. Shape varies per action.
   *
   * NULL means one of two very different things, and `metadataDisclosed` is the
   * only way to tell them apart — see below.
   */
  metadata: unknown;
  /**
   * False when this operator may see that the action happened but not what it
   * carried (`PlatformAuditDisclosure` serves a payload only to a caller who
   * could have written it).
   *
   * The backend models this explicitly for one reason: a console must be able to
   * distinguish "restricted" from "this action recorded nothing", and rendering a
   * withheld payload as an empty box tells a ReadOnlyOps operator reconstructing
   * an incident that a `tenant.purge` carried no context at all.
   *
   * Optional so an older backend, or any of the several audit shapes that do not
   * carry the gate, keeps its previous meaning: absent === disclosed.
   */
  metadataDisclosed?: boolean;
  /** The policy that would unlock the payload. Shown next to the withholding. */
  metadataPolicy?: string | null;
}

export interface PlatformAuditFieldChange {
  field: string;
  before: string | null;
  after: string | null;
}

export interface PlatformAuditEntryDetail extends PlatformAuditEntry {
  before: unknown;
  after: unknown;
  /** Empty rather than invented when the row recorded no before/after pair. */
  changes: PlatformAuditFieldChange[];
}

/** The real verb vocabulary present in the log — the filter is built from this. */
export interface PlatformAuditAction {
  action: string;
  count: number;
  lastSeenAtUtc: string;
}

export interface TenantTimelineEntry {
  /** "audit", "impersonation" or "ticket". */
  kind: string;
  id: string;
  occurredAtUtc: string;
  action: string;
  actor: string | null;
  summary: string | null;
  result: string | null;
  metadata: unknown;
}

// --- Tenant operations summary ---------------------------------------------

export interface TenantLifecycleSnapshot {
  tenantId: string;
  name: string;
  slug: string;
  status: string;
  statusReason: string | null;
  planId: string | null;
  planCode: string | null;
  primaryBusinessUnitId: string | null;
  createdOn: string;
  modifiedOn: string | null;
  modifiedBy: string | null;
}

export interface TenantSupportSnapshot {
  openTicketCount: number;
  unassignedOpenTicketCount: number;
  openByStatus: Record<string, number>;
  openBySeverity: Record<string, number>;
  oldestOpenTicketCreatedAtUtc: string | null;
  recentTickets: SupportTicketSummary[];
}

export interface TenantAuditSnapshot {
  entryCountLast30Days: number;
  failureCountLast30Days: number;
  lastActionAtUtc: string | null;
  recentEntries: PlatformAuditEntry[];
}

export interface TenantImpersonationSessionSnapshot {
  jti: string;
  actorPlatformUserId: string;
  actorEmail: string | null;
  reason: string;
  issuedAtUtc: string;
  expiresAtUtc: string;
  revokedAtUtc: string | null;
  revokedBy: string | null;
  status: string;
  /** Empty means an operator entered the account without recording why. */
  linkedTicketIds: string[];
}

export interface TenantImpersonationSnapshot {
  activeSessionCount: number;
  sessionCountLast30Days: number;
  sessions: TenantImpersonationSessionSnapshot[];
}

export interface TenantOperationsSummary {
  generatedAtUtc: string;
  lifecycle: TenantLifecycleSnapshot;
  support: TenantSupportSnapshot;
  audit: TenantAuditSnapshot;
  impersonation: TenantImpersonationSnapshot;
}

// ---------------------------------------------------------------------------
// Outbound email — the platform's own sending identity.
//
// This is how activation links, invitations and every other transactional message
// leave the product. Until it is configured the provider is `console`, which logs
// each message and DISCARDS it: provisioning appears to succeed and no customer ever
// receives anything. That is why `isSending` is the first field the screen reads.
//
// Mirrors `Platform/Notifications/PlatformEmailDtos.cs`.
// ---------------------------------------------------------------------------

/** `console` transmits nothing; `smtp` covers GoDaddy, Microsoft 365, SES, Postmark
 *  and anything else reachable over SMTP; `sendgrid` is the HTTP submission API. */
export type PlatformEmailProvider = 'console' | 'smtp' | 'sendgrid';

/**
 * What to do with mail that is addressed outside the allow list. A non-Live mode is
 * what makes it safe to exercise the product against a database full of plausible
 * addresses without mailing a stranger.
 */
export type OutboundGuardMode = 'Live' | 'AllowListOnly' | 'Redirect' | 'DraftOnly';

/**
 * The stored configuration.
 *
 * No secret appears here and none can be added by accident: the server has no password
 * property to populate, only `hasSmtpPassword`. A masked value would still travel, still
 * land in a browser cache, and tell the operator nothing they did not already know.
 */
export interface PlatformEmailSettings {
  provider: PlatformEmailProvider;
  fromAddress: string;
  fromName: string;
  replyToAddress: string | null;
  appBaseUrl: string;

  smtpHost: string | null;
  smtpPort: number;
  smtpUsername: string | null;
  hasSmtpPassword: boolean;
  smtpEnableSsl: boolean;
  smtpTimeoutMs: number;

  hasSendGridApiKey: boolean;
  sendGridApiBaseUrl: string | null;

  outboundGuardMode: OutboundGuardMode;
  outboundGuardRedirectTo: string | null;
  outboundGuardAllowedRecipients: string[];
  outboundGuardAllowedDomains: string[];
  outboundGuardSubjectTag: string | null;

  /** `Configuration` until somebody saves for the first time — "nobody has set this up,
   *  you are looking at appsettings" versus "somebody set it up and chose this". */
  origin: string;
  /** Echo back on save. Null when nothing is stored yet. */
  version: number | null;
  updatedAtUtc: string | null;
  updatedBy: string | null;
  updateReason: string | null;
}

/**
 * A full replacement of the stored configuration.
 *
 * Secret semantics, stated once and applied everywhere: `null`/omitted KEEPS the stored
 * secret, `''` CLEARS it. The console renders an empty password box because it is never
 * given the value — treating that as a deliberate blank would wipe the credential every
 * time an operator changed the port.
 */
export interface SavePlatformEmailSettingsInput {
  provider: PlatformEmailProvider;
  fromAddress: string;
  fromName: string;
  replyToAddress?: string | null;
  appBaseUrl: string;

  smtpHost?: string | null;
  smtpPort: number;
  smtpUsername?: string | null;
  smtpPassword?: string | null;
  smtpEnableSsl: boolean;
  smtpTimeoutMs: number;

  sendGridApiKey?: string | null;
  sendGridApiBaseUrl?: string | null;

  outboundGuardMode: OutboundGuardMode;
  outboundGuardRedirectTo?: string | null;
  outboundGuardAllowedRecipients: string[];
  outboundGuardAllowedDomains: string[];
  outboundGuardSubjectTag?: string | null;

  /** The version the operator was editing. A mismatch is a 409 rather than a silent
   *  overwrite of somebody else's credentials. */
  expectedVersion?: number | null;
  /** Required and audited. "Who changed the From address in March" gets asked. */
  reason: string;
}

/** Unsaved settings to test without committing them. */
export interface CandidatePlatformEmailSettings {
  provider?: PlatformEmailProvider;
  fromAddress?: string;
  fromName?: string;
  replyToAddress?: string | null;
  smtpHost?: string;
  smtpPort?: number;
  smtpUsername?: string;
  smtpPassword?: string;
  smtpEnableSsl?: boolean;
  smtpTimeoutMs?: number;
  sendGridApiKey?: string;
  sendGridApiBaseUrl?: string;
}

export interface TestOutboundEmailResult {
  succeeded: boolean;
  /** False when nothing left the process. The console provider "succeeds" and transmits
   *  nothing, and an operator must not read that as proof that mail works. */
  transmitted: boolean;
  kind: string;
  /** Names the next action. Never the provider's raw text. */
  message: string;
  providerStatus: string | null;
  provider: string;
  outboundGuardMode: string;
  intendedRecipient: string;
  /** Different from the intended address when the outbound guard is redirecting. */
  effectiveRecipient: string;
  acceptanceReference: string | null;
  attemptedAtUtc: string;
  elapsedMs: number;
}

/** "Is mail working?" in one response. */
export interface OutboundEmailStatus {
  summary: string;
  provider: string;
  origin: string;
  /** The fact everything else hangs off: false means messages are logged and discarded. */
  isSending: boolean;
  credentialsSet: boolean;
  fromAddress: string;
  fromName: string;
  replyToAddress: string | null;
  appBaseUrl: string;
  hasSmtpPassword: boolean;
  hasSendGridApiKey: boolean;

  outboundGuardMode: string;
  outboundGuardRedirectTo: string | null;

  lastSuccessfulSendAtUtc: string | null;
  lastSuccessfulSendProvider: string | null;
  lastFailureAtUtc: string | null;
  lastFailureKind: string | null;
  lastFailureReason: string | null;
  consecutiveFailures: number;

  /** Durable, unlike the in-process counters above, which reset on restart. */
  lastVerifiedAtUtc: string | null;
  lastVerifiedBy: string | null;
  lastVerifiedRecipient: string | null;
  lastVerificationFailureAtUtc: string | null;
  lastVerificationFailureKind: string | null;
  lastVerificationFailureReason: string | null;

  configuredAtUtc: string | null;
  configuredBy: string | null;
  /** Non-fatal problems in the settings that are actually in force. */
  warnings: string[];
}

/** What to connect to. Every field is optional: what is supplied overrides the stored
 *  settings for this one attempt, and a blank password means "use the stored one". */
export interface PlatformConnectionTestInput {
  providerKey?: string;
  host?: string;
  port?: number;
  tls?: 'None' | 'StartTls' | 'Implicit';
  username?: string;
  password?: string;
}

// --- platform authentication policy (Platform Admin → Security → Platform Authentication) ---

/** Mirrors `PlatformMfaMode` on the server. Screaming-snake on both sides on purpose: the
 *  value is persisted, typed by an operator into a confirmation box, and read out of an
 *  audit row years later, so the strings must be character-identical everywhere. */
export type PlatformMfaMode = 'REQUIRED' | 'OPTIONAL' | 'DISABLED_TEST_ONLY';

export type PlatformMfaEnvironmentClass = 'Production' | 'StagingOrUat' | 'LocalOrTest';

/**
 * The narrow read every authenticated operator may make, used to decide whether the console
 * owes a persistent banner and whether the MFA enrollment gate applies.
 *
 * It is a REPORT of a server decision. Nothing the console does with it changes what the API
 * allows — every `/api/platform/*` route re-decides on the server from the same policy row.
 */
export interface PlatformEffectiveMfaPolicy {
  mode: PlatformMfaMode;
  /** What the row says, which differs from `mode` exactly when a bypass has expired. */
  declaredMode: PlatformMfaMode;
  environmentClass: PlatformMfaEnvironmentClass;
  environmentName: string;
  effectiveFromUtc: string;
  expiresAtUtc: string | null;
  /** No second factor is enforced anywhere. This is what the banner is owed for. */
  enforcementDisabled: boolean;
  /** A password-only session reaches the privileged plane — OPTIONAL as well as disabled. */
  passwordOnlySessionsPermitted: boolean;
  bypassExpired: boolean;
  changedBy: string | null;
  version: number;
  /** Whether the platform honours remembered browsers at all. False means an EXISTING trust is
   *  refused at the next sign-in, not merely that no new ones are minted. */
  browserTrustEnabled: boolean;
  /** How long a remembered browser suppresses a repeat challenge, in hours. */
  browserTrustHours: number;
}

/**
 * The receipt for a step-up password re-authentication: how long this session may now run
 * high-risk operations for. Mirrors `PlatformReauthenticationResponse`.
 */
export interface PlatformReauthentication {
  validUntilUtc: string;
  windowMinutes: number;
}

/** The Owner read model behind the Platform Authentication screen. */
export interface PlatformMfaPolicy extends PlatformEffectiveMfaPolicy {
  isolatedTestInfrastructure: boolean;
  changeReason: string | null;
  updatedAtUtc: string | null;
  /** Operators with an enrolled second factor — who a change actually affects. */
  enrolledOperatorCount: number;
  activeOperatorCount: number;
  activeMfaBoundSessionCount: number;
  activeBrowserTrustCount: number;
  /** Decided by the BACKEND from its own environment classification. The console renders
   *  only these; anything else is refused server-side regardless of what it sends. */
  availableModes: PlatformMfaMode[];
  confirmationPhrases: Record<string, string>;
  maxBypassHours: number;
  minimumReasonLength: number;
  /** True when an Owner set the window; false when it is still the deployment's seed value. The
   *  screen says which, because a number nobody chose looks exactly like a number somebody did. */
  browserTrustFromPolicyRow: boolean;
  /** The bounds the SERVER will accept, sent rather than hard-coded here so the screen cannot
   *  offer a duration the API refuses. */
  minBrowserTrustHours: number;
  maxBrowserTrustHours: number;
}

export interface ChangePlatformMfaPolicyInput {
  mode: PlatformMfaMode;
  currentPassword: string;
  reason: string;
  expiresAtUtc?: string | null;
  confirmation: string;
  expectedVersion?: number | null;
  /** Omitted (or null) keeps what is stored — the server applies null-keeps, so a change that is
   *  only about the enforcement mode cannot silently reset a control it never mentioned. */
  browserTrustEnabled?: boolean | null;
  browserTrustHours?: number | null;
}

/**
 * One browser the signed-in operator has told the platform to remember.
 *
 * Own-account only, in both directions: the list endpoint is scoped to the caller's user id and so
 * is every revoke. There is no route that reads or revokes somebody else's.
 */
export interface PlatformBrowserTrust {
  id: number;
  /** "Chrome on macOS" — derived from the User-Agent, never the raw header. */
  label: string | null;
  createdAtUtc: string;
  expiresAtUtc: string;
  lastUsedAtUtc: string | null;
}

/**
 * One row of a tenant's Modules screen. Mirrors `TenantModuleGrantDto`.
 *
 * `available` is the honest half: five catalogue keys have no production execution boundary, and
 * the server denies them however the grant reads. The console shows those as "not built" rather
 * than as a switch, because a toggle that grants nothing is worse than no toggle — somebody
 * eventually sells against it.
 */
export interface TenantModuleGrant {
  key: string;
  enabled: boolean;
  available: boolean;
  /** What the tenant's plan declares. Advisory: the plan is a provisioning template, not authority. */
  fromPlanTemplate: boolean | null;
}

/** A tenant's whole module grant, as one read. Mirrors `TenantModulesDto`. */
export interface TenantModules {
  tenantId: number;
  tenantName: string;
  planId: number | null;
  planCode: string | null;
  modules: TenantModuleGrant[];
}
