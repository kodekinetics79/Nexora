import { jwtDecode } from 'jwt-decode';
import platformHttp from './platformHttp';
import { BILLING_MODES } from '../types';
import type { EmailProviderCapability, MailConnectionTestResult } from '../../email/types';
import type {
  AuditEntry,
  AiProviderAuthorization,
  AiProviderTrustView,
  AuthorizeAiProviderInput,
  BillingMode,
  BillingMeterCatalogEntry,
  BillingReadiness,
  DocumentCoveragePolicy,
  DocumentCoverageSegment,
  BillingStatement,
  BillingStatementSummary,
  CreatePlatformOperatorInput,
  CreateSubscriptionInvoiceInput,
  CreateRateCardInput,
  CandidatePlatformEmailSettings,
  CreateSupportTicketInput,
  ExtractionJob,
  OutboundEmailStatus,
  ImpersonationSession,
  ImpersonationTicket,
  JobStatus,
  OverviewMetrics,
  PagedResult,
  PendingTenantDeletion,
  Plan,
  PlatformAuditAction,
  PlatformAuditEntry,
  PlatformAuditEntryDetail,
  PlatformConnectionTestInput,
  PlatformEmailSettings,
  PlatformOperator,
  PlatformOperatorRole,
  ProvisionedBaseline,
  ProvisionedBilling,
  ProvisioningDraftSummary,
  ProvisioningExecution,
  ProvisionTenantInput,
  ProvisionTenantRequestBody,
  ProvisionTenantResult,
  QueueStats,
  RecoverPlatformDeadLetterInput,
  RecoverPlatformDeadLetterResult,
  RateCard,
  RevenueRiskReport,
  SavePlatformEmailSettingsInput,
  SetAccountContactInput,
  SetCommercialTermsInput,
  SlugAvailability,
  SubmitProvisioningResult,
  SupportTicketDetail,
  SupportTicketSeverity,
  SupportTicketSummary,
  SupportTicketTimeline,
  SubscriptionCreditNote,
  SubscriptionInvoice,
  SubscriptionRevenueAction,
  SubscriptionRevenueActionKind,
  SubscriptionPayment,
  TenantActivationDataDecision,
  TenantActivationDecision,
  ActivationControlEvidenceReceipt,
  Tenant,
  TenantAdminInvitation,
  TenantDataAsset,
  TenantDataRecoveryEvidence,
  TenantDeletionCertificationDecision,
  TenantDeletionCertificate,
  TenantBillingProfile,
  TenantCostReport,
  TenantErasureResult,
  TenantExportDownload,
  TenantOffboardingStatus,
  TenantLegalHold,
  TenantAiPolicy,
  TenantOperationsSummary,
  TenantPurgePreview,
  TenantPurgeResult,
  TenantRevenueRisk,
  TenantTimelineEntry,
  TenantUsageReadout,
  TestOutboundEmailResult,
  UpdateRateCardInput,
  UpdateTenantAiPolicyInput,
  UpdateTenantDataRegionInput,
  UpdateTenantProfileInput,
  ResendTenantAdminInvitationResult,
  UsageRatingCorrection,
  RegisterTenantDataAssetInput,
  VerifyTenantDataAssetInput,
  RecordActivationControlEvidenceInput,
  RecordTenantDataRecoveryEvidenceInput,
  UpsertPlanInput,
} from '../types';

export interface AuditQuery {
  action?: string;
  tenantId?: string;
  result?: 'success' | 'failure';
  search?: string;
}

export interface JobQuery {
  tenantId?: string;
  status?: JobStatus | 'all';
}

export interface StatementQuery {
  tenantId?: string;
  status?: string;
}

export interface RevenueRiskQuery {
  onlyAtRisk?: boolean;
  includeArchived?: boolean;
  onlyCommercialConfigurationRequired?: boolean;
}

export interface SupportTicketQuery {
  tenantId?: string;
  status?: string[];
  severity?: string[];
  assignedToPlatformUserId?: string;
  unassigned?: boolean;
  includeFinished?: boolean;
  search?: string;
  fromUtc?: string;
  toUtc?: string;
  page?: number;
  pageSize?: number;
}

export interface AuditExplorerQuery {
  tenantId?: string;
  actorPlatformUserId?: string;
  action?: string[];
  actionPrefix?: string;
  targetType?: string;
  targetId?: string;
  result?: string;
  fromUtc?: string;
  toUtc?: string;
  search?: string;
  page?: number;
  pageSize?: number;
}

export interface PlatformApi {
  getOverview(): Promise<OverviewMetrics>;
  listTenants(): Promise<Tenant[]>;
  getTenant(id: string): Promise<Tenant>;
  updateTenantProfile(id: string, input: UpdateTenantProfileInput): Promise<Tenant>;
  updateTenantDataRegion(id: string, input: UpdateTenantDataRegionInput): Promise<Tenant>;
  listTenantAdminInvitations(id: string): Promise<TenantAdminInvitation[]>;
  resendTenantAdminInvitation(
    id: string,
    input: { userId?: string | null; reason: string },
  ): Promise<ResendTenantAdminInvitationResult>;
  markTenantPastDue(id: string, reason: string): Promise<Tenant>;
  resolveTenantPastDue(id: string, reason: string): Promise<Tenant>;
  listTenantDataAssets(tenantId: string): Promise<TenantDataAsset[]>;
  getTenantActivationDataDecision(tenantId: string): Promise<TenantActivationDataDecision>;
  registerTenantDataAsset(tenantId: string, input: RegisterTenantDataAssetInput): Promise<TenantDataAsset>;
  verifyTenantDataAsset(
    tenantId: string,
    assetId: string,
    input: VerifyTenantDataAssetInput,
  ): Promise<TenantDataAsset>;
  getTenantActivationDecision(tenantId: string): Promise<TenantActivationDecision>;
  activateTenant(tenantId: string): Promise<TenantActivationDecision>;
  recordTenantActivationEvidence(
    tenantId: string,
    controlCode: string,
    input: RecordActivationControlEvidenceInput,
  ): Promise<ActivationControlEvidenceReceipt>;
  listTenantRecoveryEvidence(tenantId: string): Promise<TenantDataRecoveryEvidence[]>;
  recordTenantRecoveryEvidence(
    tenantId: string,
    input: RecordTenantDataRecoveryEvidenceInput,
  ): Promise<TenantDataRecoveryEvidence>;
  getTenantDeletionCertificationDecision(tenantId: string): Promise<TenantDeletionCertificationDecision>;
  certifyTenantDeletion(tenantId: string, reason: string): Promise<TenantDeletionCertificate>;
  provisionTenant(input: ProvisionTenantInput): Promise<ProvisionTenantResult>;
  suspendTenant(id: string, reason: string): Promise<Tenant>;
  resumeTenant(id: string, reason: string): Promise<Tenant>;
  archiveTenant(id: string, reason: string): Promise<Tenant>;
  restoreTenant(id: string, reason: string): Promise<Tenant>;
  changeTenantPlan(id: string, planId: string, reason?: string): Promise<Tenant>;
  impersonateTenant(id: string, reason: string): Promise<ImpersonationTicket>;
  listImpersonationSessions(): Promise<ImpersonationSession[]>;
  revokeImpersonation(jti: string): Promise<ImpersonationSession>;
  getQueueStats(): Promise<QueueStats>;
  listJobs(query?: JobQuery): Promise<ExtractionJob[]>;
  recoverPlatformDeadLetter(
    tenantId: string,
    input: RecoverPlatformDeadLetterInput,
  ): Promise<RecoverPlatformDeadLetterResult>;
  listPlans(): Promise<Plan[]>;
  createPlan(input: UpsertPlanInput): Promise<Plan>;
  updatePlan(id: string, input: UpsertPlanInput): Promise<Plan>;
  listPlatformUsers(): Promise<PlatformOperator[]>;
  createPlatformUser(input: CreatePlatformOperatorInput): Promise<PlatformOperator>;
  changePlatformUserRole(id: string, role: PlatformOperatorRole): Promise<PlatformOperator>;
  deactivatePlatformUser(id: string): Promise<PlatformOperator>;
  reactivatePlatformUser(id: string): Promise<PlatformOperator>;
  resetPlatformUserPassword(id: string, newPassword: string): Promise<PlatformOperator>;
  resetPlatformUserMfa(id: string): Promise<void>;
  getBillingUsage(tenantId: string, period: string): Promise<TenantUsageReadout>;
  listBillingMeterCatalog(): Promise<BillingMeterCatalogEntry[]>;
  getBillingReadiness(tenantId: string, period: string): Promise<BillingReadiness>;
  correctUsageRating(usageEventId: string, idempotencyKey: string, reason: string): Promise<UsageRatingCorrection>;
  proposeDocumentCoverage(
    tenantId: string,
    period: string,
    midPeriodCutoverUtc: string | null,
    reason: string,
  ): Promise<DocumentCoveragePolicy>;
  approveDocumentCoverage(tenantId: string, period: string, reason: string): Promise<DocumentCoverageSegment[]>;
  listRateCards(): Promise<RateCard[]>;
  createRateCard(input: CreateRateCardInput): Promise<RateCard>;
  updateRateCard(id: string, input: UpdateRateCardInput): Promise<RateCard>;
  listStatements(query?: StatementQuery): Promise<BillingStatement[]>;
  computeStatement(tenantId: string, period: string): Promise<BillingStatement>;
  finalizeStatement(id: string): Promise<BillingStatement>;
  getBillingCost(tenantId: string, period: string): Promise<TenantCostReport>;
  listAudit(query?: AuditQuery): Promise<AuditEntry[]>;

  // --- durable provisioning ---
  submitProvisioning(
    input: ProvisionTenantInput,
    options: { idempotencyKey: string; fromDraftId?: string | null },
  ): Promise<SubmitProvisioningResult>;
  getProvisioningExecution(id: string): Promise<ProvisioningExecution>;
  listProvisioningExecutions(query?: { state?: string; take?: number }): Promise<ProvisioningExecution[]>;
  retryProvisioning(id: string, body: { step?: string | null; reason: string }): Promise<ProvisioningExecution>;
  cancelProvisioning(id: string, reason: string): Promise<ProvisioningExecution>;
  checkSlug(params: { slug?: string; name?: string }): Promise<SlugAvailability>;
  listReservedSlugs(): Promise<string[]>;
  listProvisioningDrafts(): Promise<ProvisioningDraftSummary[]>;
  getProvisioningDraft(id: string): Promise<ProvisioningDraftSummary>;
  createProvisioningDraft(name: string, payload: ProvisionTenantRequestBody): Promise<ProvisioningDraftSummary>;
  updateProvisioningDraft(
    id: string,
    name: string,
    payload: ProvisionTenantRequestBody,
    version: number,
  ): Promise<ProvisioningDraftSummary>;
  deleteProvisioningDraft(id: string): Promise<void>;

  // --- offboarding ---
  getOffboarding(tenantId: string): Promise<TenantOffboardingStatus>;
  listPendingDeletions(): Promise<PendingTenantDeletion[]>;
  getPurgePreview(tenantId: string): Promise<TenantPurgePreview>;
  exportTenantData(
    tenantId: string,
    body: { reason: string; confirmation: string },
  ): Promise<TenantExportDownload>;
  scheduleTenantDeletion(
    tenantId: string,
    body: { reason: string; retentionDays?: number | null },
  ): Promise<TenantOffboardingStatus>;
  cancelTenantDeletion(tenantId: string, reason: string): Promise<TenantOffboardingStatus>;
  purgeTenant(tenantId: string, body: { reason: string; confirmation: string }): Promise<TenantPurgeResult>;
  eraseTenantPersonalData(
    tenantId: string,
    body: { reason: string; confirmation: string },
  ): Promise<TenantErasureResult>;
  listTenantLegalHolds(tenantId: string): Promise<TenantLegalHold[]>;
  placeTenantLegalHold(
    tenantId: string,
    body: { scope: string; authority: string; reason: string; evidenceReference: string },
  ): Promise<TenantLegalHold>;
  releaseTenantLegalHold(tenantId: string, holdId: string, reason: string): Promise<TenantLegalHold>;
  getTenantAiPolicy(tenantId: string): Promise<TenantAiPolicy>;
  updateTenantAiPolicy(tenantId: string, body: UpdateTenantAiPolicyInput): Promise<TenantAiPolicy>;
  getTenantAiProviders(tenantId: string): Promise<AiProviderTrustView>;
  authorizeTenantAiProvider(tenantId: string, body: AuthorizeAiProviderInput): Promise<AiProviderAuthorization>;
  revokeTenantAiProvider(tenantId: string, authorizationId: string, reason: string): Promise<AiProviderAuthorization>;

  // --- billing console ---
  getRevenueRisk(query?: RevenueRiskQuery): Promise<RevenueRiskReport>;
  getTenantBillingProfile(tenantId: string): Promise<TenantBillingProfile>;
  listSubscriptionInvoices(tenantId: string): Promise<SubscriptionInvoice[]>;
  createSubscriptionInvoice(input: CreateSubscriptionInvoiceInput): Promise<SubscriptionInvoice>;
  finalizeSubscriptionInvoice(id: string): Promise<SubscriptionInvoice>;
  creditSubscriptionInvoice(id: string, amount: number, reason: string): Promise<SubscriptionCreditNote>;
  recordSubscriptionPayment(
    id: string,
    amount: number,
    externalReference: string,
    receivedAtUtc: string,
  ): Promise<SubscriptionPayment>;
  proposeSubscriptionRevenueAction(id: string, body: {
    kind: SubscriptionRevenueActionKind; amount: number; currency: string; reason: string;
    evidenceSha256: string; externalReference: string | null;
  }): Promise<SubscriptionRevenueAction>;
  approveSubscriptionRevenueAction(actionId: string): Promise<SubscriptionRevenueAction>;
  setTenantRateCard(
    tenantId: string,
    body: { rateCardId: string | null; reason: string },
  ): Promise<TenantBillingProfile>;
  setTenantCommercialTerms(tenantId: string, body: SetCommercialTermsInput): Promise<TenantBillingProfile>;
  setTenantAccountContact(tenantId: string, body: SetAccountContactInput): Promise<TenantBillingProfile>;
  getStatement(id: string): Promise<BillingStatement>;

  // --- support desk ---
  listSupportTickets(query?: SupportTicketQuery): Promise<PagedResult<SupportTicketSummary>>;
  getSupportTicket(id: string): Promise<SupportTicketDetail>;
  getSupportTicketTimeline(id: string): Promise<SupportTicketTimeline>;
  createSupportTicket(input: CreateSupportTicketInput): Promise<SupportTicketDetail>;
  addSupportTicketNote(
    id: string,
    body: { body: string; isInternal: boolean; expectedVersion?: number },
  ): Promise<SupportTicketDetail>;
  transitionSupportTicket(
    id: string,
    body: { status: string; reason: string; resolution?: string | null; expectedVersion?: number },
  ): Promise<SupportTicketDetail>;
  assignSupportTicket(
    id: string,
    body: { assignToPlatformUserId: string | null; reason?: string; expectedVersion?: number },
  ): Promise<SupportTicketDetail>;
  changeSupportTicketSeverity(
    id: string,
    body: { severity: SupportTicketSeverity; reason: string; expectedVersion?: number },
  ): Promise<SupportTicketDetail>;

  // --- audit explorer ---
  queryAudit(query?: AuditExplorerQuery): Promise<PagedResult<PlatformAuditEntry>>;
  getAuditEntry(id: string): Promise<PlatformAuditEntryDetail>;
  listAuditActions(query?: { tenantId?: string; fromUtc?: string }): Promise<PlatformAuditAction[]>;
  getTenantTimeline(
    tenantId: string,
    query?: { fromUtc?: string; toUtc?: string; limit?: number },
  ): Promise<TenantTimelineEntry[]>;

  // --- one-call tenant operations summary ---
  getTenantOperationsSummary(tenantId: string): Promise<TenantOperationsSummary>;

  // --- outbound email ---
  getEmailSettings(): Promise<PlatformEmailSettings>;
  getEmailStatus(): Promise<OutboundEmailStatus>;
  saveEmailSettings(input: SavePlatformEmailSettingsInput): Promise<PlatformEmailSettings>;
  listEmailProviders(): Promise<EmailProviderCapability[]>;
  testEmailConnection(input: PlatformConnectionTestInput): Promise<MailConnectionTestResult>;
  testSendEmail(body: {
    recipient: string;
    settings?: CandidatePlatformEmailSettings;
  }): Promise<TestOutboundEmailResult>;
}

// --- backend wire shapes (ids arrive as numbers) ----------------------------

type BackendTenant = {
  id: string | number;
  name: string;
  slug: string;
  status?: string | null;
  planId?: string | number | null;
  planCode?: string | null;
  createdOn?: string | null;
  statusReason?: string | null;
  // Company + commercial columns. Optional on the wire because the tenant list
  // predates them: an older DTO simply omits them and every consumer already
  // treats a missing value as "not recorded".
  legalName?: string | null;
  registrationNumber?: string | null;
  taxNumber?: string | null;
  countryCode?: string | null;
  industry?: string | null;
  website?: string | null;
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  stateProvince?: string | null;
  postalCode?: string | null;
  phone?: string | null;
  contactEmail?: string | null;
  logoUrl?: string | null;
  billingMode?: string | null;
  billingModeReason?: string | null;
  rateCardId?: string | number | null;
  billingStartsOn?: string | null;
  trialEndsOn?: string | null;
  contractStartOn?: string | null;
  contractEndOn?: string | null;
  paymentTermsDays?: number | null;
  purchaseOrderReference?: string | null;
  billingContactName?: string | null;
  billingContactEmail?: string | null;
  billingAddress?: string | null;
  accountOwnerEmail?: string | null;
  baseCurrencyCode?: string | null;
  timeZoneId?: string | null;
  locale?: string | null;
  dataRegion?: string | null;
};

type BackendProvisionResult = {
  tenant: BackendTenant;
  foundingAdmin: {
    userId: string | number;
    email: string;
    roleName: string;
    generatedPassword?: string | null;
    invitation?: { expiresAtUtc: string; activationUrl: string } | null;
  };
  baseline?: ProvisionedBaseline | null;
  billing?: (Omit<ProvisionedBilling, 'warnings'> & { warnings?: string[] | null }) | null;
};

type BackendImpersonation = {
  tenantId: string | number;
  token: string;
  expiresAtUtc: string;
};

type BackendPlan = Omit<Plan, 'id'> & { id: string | number };

type BackendOperator = Omit<PlatformOperator, 'id'> & { id: string | number };

type BackendSession = Omit<ImpersonationSession, 'tenantId' | 'actorPlatformUserId'> & {
  tenantId: string | number;
  actorPlatformUserId: string | number;
};

type BackendRateCard = Omit<RateCard, 'id' | 'lines'> & {
  id: string | number;
  lines: (Omit<RateCard['lines'][number], 'id'> & { id: string | number })[];
};

type BackendStatement = Omit<BillingStatement, 'id' | 'tenantId' | 'rateCardId'> & {
  id: string | number;
  tenantId: string | number;
  rateCardId: string | number;
};

type BackendUsage = Omit<TenantUsageReadout, 'tenantId' | 'businessUnitId'> & {
  tenantId: string | number;
  businessUnitId: string | number | null;
};

type BackendCost = Omit<TenantCostReport, 'tenantId' | 'businessUnitId'> & {
  tenantId: string | number;
  businessUnitId: string | number | null;
};

// --- normalizers ------------------------------------------------------------

const normalizeTenantStatus = (status?: string | null): Tenant['status'] => {
  const normalized = (status ?? 'provisioning').toLowerCase();
  if (normalized === 'pastdue' || normalized === 'past_due') return 'past_due';
  if (normalized === 'active' || normalized === 'trial' || normalized === 'suspended' || normalized === 'archived') return normalized;
  return 'provisioning';
};

/**
 * A billing mode is only trusted when it is one the console actually knows how
 * to reason about. An unrecognised string becomes null so the UI shows "—"
 * rather than a chip claiming a commercial posture nobody defined.
 */
const normalizeBillingMode = (mode?: string | null): BillingMode | null => {
  const match = BILLING_MODES.find((known) => known.toLowerCase() === (mode ?? '').toLowerCase());
  return match ?? null;
};

/** Blank strings on the wire mean "not recorded", exactly like an absent field. */
const orNull = (value?: string | null): string | null => {
  const trimmed = (value ?? '').trim();
  return trimmed.length > 0 ? trimmed : null;
};

/**
 * Maps the backend TenantSummaryDto 1:1. Fields the backend does not send
 * (usage, pipeline health) are NOT fabricated here — the UI renders "—" for
 * anything it does not have.
 */
const normalizeTenant = (tenant: BackendTenant): Tenant => ({
  id: String(tenant.id),
  name: tenant.name,
  slug: tenant.slug,
  planId: tenant.planId == null ? null : String(tenant.planId),
  planCode: tenant.planCode ? tenant.planCode.toLowerCase() : null,
  status: normalizeTenantStatus(tenant.status),
  statusReason: tenant.statusReason ?? null,
  createdAt: tenant.createdOn ?? null,

  legalName: orNull(tenant.legalName),
  registrationNumber: orNull(tenant.registrationNumber),
  taxNumber: orNull(tenant.taxNumber),
  // Upper-cased because ISO-3166 alpha-2 is, and because the country filter and
  // Intl.DisplayNames both key off the canonical form.
  countryCode: orNull(tenant.countryCode)?.toUpperCase() ?? null,
  industry: orNull(tenant.industry),
  website: orNull(tenant.website),
  addressLine1: orNull(tenant.addressLine1),
  addressLine2: orNull(tenant.addressLine2),
  city: orNull(tenant.city),
  stateProvince: orNull(tenant.stateProvince),
  postalCode: orNull(tenant.postalCode),
  phone: orNull(tenant.phone),
  contactEmail: orNull(tenant.contactEmail),
  logoUrl: orNull(tenant.logoUrl),

  billingMode: normalizeBillingMode(tenant.billingMode),
  billingModeReason: orNull(tenant.billingModeReason),
  rateCardId: tenant.rateCardId == null ? null : String(tenant.rateCardId),
  billingStartsOn: orNull(tenant.billingStartsOn),
  trialEndsOn: orNull(tenant.trialEndsOn),
  contractStartOn: orNull(tenant.contractStartOn),
  contractEndOn: orNull(tenant.contractEndOn),
  paymentTermsDays: tenant.paymentTermsDays ?? null,
  purchaseOrderReference: orNull(tenant.purchaseOrderReference),
  billingContactName: orNull(tenant.billingContactName),
  billingContactEmail: orNull(tenant.billingContactEmail),
  billingAddress: orNull(tenant.billingAddress),
  accountOwnerEmail: orNull(tenant.accountOwnerEmail),
  baseCurrencyCode: orNull(tenant.baseCurrencyCode)?.toUpperCase() ?? null,
  timeZoneId: orNull(tenant.timeZoneId),
  locale: orNull(tenant.locale),
  dataRegion: orNull(tenant.dataRegion),
});

const normalizePlan = (plan: BackendPlan): Plan => ({ ...plan, id: String(plan.id) });

const normalizeOperator = (user: BackendOperator): PlatformOperator => ({
  ...user,
  id: String(user.id),
});

const normalizeSession = (session: BackendSession): ImpersonationSession => ({
  ...session,
  tenantId: String(session.tenantId),
  actorPlatformUserId: String(session.actorPlatformUserId),
});

const normalizeRateCard = (card: BackendRateCard): RateCard => ({
  ...card,
  id: String(card.id),
  lines: card.lines.map((line) => ({ ...line, id: String(line.id) })),
});

const normalizeStatement = (statement: BackendStatement): BillingStatement => ({
  ...statement,
  id: String(statement.id),
  tenantId: String(statement.tenantId),
  rateCardId: String(statement.rateCardId),
});

const readJti = (token: string): string | null => {
  try {
    const { jti } = jwtDecode<{ jti?: string }>(token);
    return typeof jti === 'string' && jti.length > 0 ? jti : null;
  } catch {
    return null;
  }
};

// --- id normalisation -------------------------------------------------------
// Every platform id is a `long` on the wire. The console keeps them as strings so
// nothing rounds at 2^53 and so a react key, a route param and a DTO field all
// compare with `===`.

const asId = (value: string | number): string => String(value);
const asIdOrNull = (value: string | number | null | undefined): string | null =>
  value === null || value === undefined ? null : String(value);

/**
 * The one place the console's `ProvisionTenantInput` becomes the server's
 * `ProvisionTenantRequest`. Shared by the legacy synchronous endpoint and the durable
 * submit so the two can never drift into sending different tenants for the same form.
 */
export const toProvisionRequestBody = (input: ProvisionTenantInput): ProvisionTenantRequestBody => ({
  name: input.name.trim(),
  slug: orNull(input.slug),
  legalName: orNull(input.legalName),
  registrationNumber: orNull(input.registrationNumber),
  taxNumber: orNull(input.taxNumber),
  countryCode: orNull(input.countryCode)?.toUpperCase() ?? null,
  industry: orNull(input.industry),
  website: orNull(input.website),
  addressLine1: orNull(input.addressLine1),
  addressLine2: orNull(input.addressLine2),
  city: orNull(input.city),
  stateProvince: orNull(input.stateProvince),
  postalCode: orNull(input.postalCode),
  phone: orNull(input.phone),
  contactEmail: orNull(input.contactEmail),
  logoUrl: orNull(input.logoUrl),

  baseCurrencyCode: orNull(input.baseCurrencyCode)?.toUpperCase() ?? null,
  timeZoneId: orNull(input.timeZoneId),
  locale: orNull(input.locale),
  dataRegion: orNull(input.dataRegion),

  planId: input.planId == null ? null : Number(input.planId),
  billingMode: input.billingMode,
  billingModeReason: orNull(input.billingModeReason),
  rateCardId: input.rateCardId == null ? null : Number(input.rateCardId),
  billingStartsOn: orNull(input.billingStartsOn),
  trialEndsOn: orNull(input.trialEndsOn),
  contractStartOn: orNull(input.contractStartOn),
  contractEndOn: orNull(input.contractEndOn),
  paymentTermsDays: input.paymentTermsDays,
  purchaseOrderReference: orNull(input.purchaseOrderReference),
  billingContactName: orNull(input.billingContactName),
  billingContactEmail: orNull(input.billingContactEmail),
  billingAddress: orNull(input.billingAddress),
  accountOwnerEmail: orNull(input.accountOwnerEmail),

  adminFirstName: input.adminFirstName.trim(),
  adminLastName: input.adminLastName.trim(),
  adminEmail: input.adminEmail.trim(),
  adminJobTitle: orNull(input.adminJobTitle),
  adminPhone: orNull(input.adminPhone),
  adminActivation: input.adminActivation,
  // A password must never ride along on the invite path, where the whole point is that no
  // credential exists until the administrator sets one themselves.
  adminPassword: input.adminActivation === 'password' ? input.adminPassword || null : null,
});

// --- normalizers for the operations surfaces --------------------------------
// Each takes the wire shape (numeric ids, nullable collections) and produces the
// console's shape. Absent collections become empty arrays; absent scalars stay null,
// because the UI renders "—" for anything the backend did not send.

type WireRecord = Record<string, unknown>;

const num = (value: unknown): number => (typeof value === 'number' ? value : Number(value ?? 0));

const normalizeTenantDataAsset = (wire: WireRecord): TenantDataAsset => ({
  ...(wire as unknown as TenantDataAsset),
  id: asId(wire.id as string | number),
  tenantId: asId(wire.tenantId as string | number),
  verifiedBusinessUnitId: asIdOrNull(wire.verifiedBusinessUnitId as string | number | null),
});

const normalizeTenantActivationDataDecision = (wire: WireRecord): TenantActivationDataDecision => ({
  ...(wire as unknown as TenantActivationDataDecision),
  tenantId: asId(wire.tenantId as string | number),
  blockers: (wire.blockers as string[]) ?? [],
  postgreSqlTenantScope: wire.postgreSqlTenantScope
    ? normalizeTenantDataAsset(wire.postgreSqlTenantScope as WireRecord)
    : null,
});

const normalizeTenantActivationDecision = (wire: WireRecord): TenantActivationDecision => ({
  ...(wire as unknown as TenantActivationDecision),
  tenantId: asId(wire.tenantId as string | number),
  controls: ((wire.controls as WireRecord[]) ?? []).map((control) => ({
    ...(control as unknown as TenantActivationDecision['controls'][number]),
    evidenceReferences: (control.evidenceReferences as string[]) ?? [],
  })),
  blockingControls: (wire.blockingControls as string[]) ?? [],
  warnings: (wire.warnings as string[]) ?? [],
});

const normalizeRecoveryEvidence = (wire: WireRecord): TenantDataRecoveryEvidence => ({
  ...(wire as unknown as TenantDataRecoveryEvidence),
  id: asId(wire.id as string | number),
  tenantId: asId(wire.tenantId as string | number),
  tenantDataAssetId: asIdOrNull(wire.tenantDataAssetId as string | number | null),
});

const normalizeDeletionDecision = (wire: WireRecord): TenantDeletionCertificationDecision => ({
  ...(wire as unknown as TenantDeletionCertificationDecision),
  tenantId: asId(wire.tenantId as string | number),
  blockers: (wire.blockers as string[]) ?? [],
  evidenceIds: ((wire.evidenceIds as Array<string | number>) ?? []).map(asId),
});

const normalizeDeletionCertificate = (wire: WireRecord): TenantDeletionCertificate => ({
  ...(wire as unknown as TenantDeletionCertificate),
  id: asId(wire.id as string | number),
  tenantId: asId(wire.tenantId as string | number),
  evidenceIds: ((wire.evidenceIds as Array<string | number>) ?? []).map(asId),
});

const normalizeExecution = (wire: WireRecord): ProvisioningExecution => ({
  id: asId(wire.id as string | number),
  state: wire.state as ProvisioningExecution['state'],
  slug: (wire.slug as string) ?? '',
  name: (wire.name as string) ?? '',
  adminEmail: (wire.adminEmail as string) ?? '',
  adminActivation: (wire.adminActivation as string) ?? '',
  currentStep: (wire.currentStep as string) ?? null,
  failedStep: (wire.failedStep as string) ?? null,
  failureReason: (wire.failureReason as string) ?? null,
  failureIsTerminal: Boolean(wire.failureIsTerminal),
  tenantId: asIdOrNull(wire.tenantId as string | number | null),
  provisionedBusinessUnitId: asIdOrNull(wire.provisionedBusinessUnitId as string | number | null),
  foundingUserId: asIdOrNull(wire.foundingUserId as string | number | null),
  correlationId: (wire.correlationId as string) ?? '',
  requestedBy: (wire.requestedBy as string) ?? '',
  createdOn: wire.createdOn as string,
  startedOn: (wire.startedOn as string) ?? null,
  completedOn: (wire.completedOn as string) ?? null,
  attemptCount: num(wire.attemptCount),
  cancelledBy: (wire.cancelledBy as string) ?? null,
  cancellationReason: (wire.cancellationReason as string) ?? null,
  steps: ((wire.steps as WireRecord[]) ?? []).map((step) => ({
    step: step.step as string,
    label: step.label as string,
    ordinal: num(step.ordinal),
    status: step.status as ProvisioningExecution['steps'][number]['status'],
    attemptCount: num(step.attemptCount),
    startedOn: (step.startedOn as string) ?? null,
    completedOn: (step.completedOn as string) ?? null,
    durationMs: step.durationMs == null ? null : num(step.durationMs),
    failureCode: (step.failureCode as string) ?? null,
    failureReason: (step.failureReason as string) ?? null,
    detail: (step.detail as string) ?? null,
    isRetriable: Boolean(step.isRetriable),
  })),
  completedStepCount: num(wire.completedStepCount),
  totalStepCount: num(wire.totalStepCount),
});

const normalizeDraft = (wire: WireRecord): ProvisioningDraftSummary => ({
  id: asId(wire.id as string | number),
  name: (wire.name as string) ?? '',
  ownerEmail: (wire.ownerEmail as string) ?? '',
  createdOn: wire.createdOn as string,
  updatedOn: wire.updatedOn as string,
  version: num(wire.version),
  submittedExecutionId: asIdOrNull(wire.submittedExecutionId as string | number | null),
  payload: (wire.payload as ProvisionTenantRequestBody | null) ?? null,
});

const normalizeOffboarding = (wire: WireRecord): TenantOffboardingStatus => ({
  ...(wire as unknown as TenantOffboardingStatus),
  tenantId: asId(wire.tenantId as string | number),
  history: ((wire.history as WireRecord[]) ?? []).map((event) => ({
    ...(event as unknown as TenantOffboardingStatus['history'][number]),
    id: asId(event.id as string | number),
  })),
  exports: ((wire.exports as WireRecord[]) ?? []).map((receipt) => ({
    ...(receipt as unknown as TenantOffboardingStatus['exports'][number]),
    id: asId(receipt.id as string | number),
  })),
  disclosures: (wire.disclosures as string[]) ?? [],
});

const normalizeRevenueRisk = (wire: WireRecord): TenantRevenueRisk => ({
  ...(wire as unknown as TenantRevenueRisk),
  tenantId: asId(wire.tenantId as string | number),
  planId: asIdOrNull(wire.planId as string | number | null),
  pinnedRateCardId: asIdOrNull(wire.pinnedRateCardId as string | number | null),
  lastStatementRateCardId: asIdOrNull(wire.lastStatementRateCardId as string | number | null),
  leakReasons: (wire.leakReasons as string[]) ?? [],
});

const normalizeStatementSummary = (wire: WireRecord): BillingStatementSummary => ({
  ...(wire as unknown as BillingStatementSummary),
  id: asId(wire.id as string | number),
  rateCardId: asId(wire.rateCardId as string | number),
});

const normalizeBillingProfile = (wire: WireRecord): TenantBillingProfile => ({
  ...(wire as unknown as TenantBillingProfile),
  tenantId: asId(wire.tenantId as string | number),
  planId: asIdOrNull(wire.planId as string | number | null),
  pinnedRateCardId: asIdOrNull(wire.pinnedRateCardId as string | number | null),
  revenueRisk: normalizeRevenueRisk(wire.revenueRisk as WireRecord),
  statements: ((wire.statements as WireRecord[]) ?? []).map(normalizeStatementSummary),
});

const normalizeSubscriptionInvoice = (wire: WireRecord): SubscriptionInvoice => ({
  ...(wire as unknown as SubscriptionInvoice),
  id: asId(wire.id as string | number),
  tenantId: asId(wire.tenantId as string | number),
  statementId: asId(wire.statementId as string | number),
  credits: ((wire.credits as WireRecord[]) ?? []).map((credit) => ({
    ...(credit as unknown as SubscriptionCreditNote),
    id: asId(credit.id as string | number),
  })),
  payments: ((wire.payments as WireRecord[]) ?? []).map((payment) => ({
    ...(payment as unknown as SubscriptionPayment),
    id: asId(payment.id as string | number),
  })),
  revenueActions: ((wire.revenueActions as WireRecord[]) ?? []).map((action) => ({
    ...(action as unknown as SubscriptionRevenueAction),
    id: asId(action.id as string | number),
    invoiceId: asId(action.invoiceId as string | number),
  })),
});

const normalizeTicketSummary = (wire: WireRecord): SupportTicketSummary => ({
  ...(wire as unknown as SupportTicketSummary),
  id: asId(wire.id as string | number),
  tenantId: asId(wire.tenantId as string | number),
  assignedToPlatformUserId: asIdOrNull(wire.assignedToPlatformUserId as string | number | null),
  openedByPlatformUserId: asIdOrNull(wire.openedByPlatformUserId as string | number | null),
});

const normalizeTicketDetail = (wire: WireRecord): SupportTicketDetail => ({
  ...normalizeTicketSummary(wire),
  ...(wire as unknown as Pick<SupportTicketDetail, 'body' | 'resolution' | 'redactedReason' | 'redactedAtUtc'>),
  requesterTenantUserId: asIdOrNull(wire.requesterTenantUserId as string | number | null),
  notes: ((wire.notes as WireRecord[]) ?? []).map((note) => ({
    ...(note as unknown as SupportTicketDetail['notes'][number]),
    id: asId(note.id as string | number),
    authorPlatformUserId: asIdOrNull(note.authorPlatformUserId as string | number | null),
  })),
  links: ((wire.links as WireRecord[]) ?? []).map((link) => ({
    ...(link as unknown as SupportTicketDetail['links'][number]),
    id: asId(link.id as string | number),
  })),
  allowedTransitions: (wire.allowedTransitions as string[]) ?? [],
});

const normalizeAuditEntry = (wire: WireRecord): PlatformAuditEntry => ({
  ...(wire as unknown as PlatformAuditEntry),
  id: asId(wire.id as string | number),
  actorPlatformUserId: asId(wire.actorPlatformUserId as string | number),
  tenantId: asIdOrNull(wire.tenantId as string | number | null),
});

const normalizePaged = <TWire extends WireRecord, TOut>(
  wire: { items?: TWire[]; page?: number; pageSize?: number; totalCount?: number; hasMore?: boolean },
  map: (item: TWire) => TOut,
): PagedResult<TOut> => ({
  items: (wire.items ?? []).map(map),
  page: wire.page ?? 1,
  pageSize: wire.pageSize ?? 0,
  totalCount: wire.totalCount ?? 0,
  hasMore: Boolean(wire.hasMore),
});

/**
 * Axios serialises `{ status: ['New','Open'] }` as `status[]=New`, which ASP.NET Core's
 * `string[]` binder does not read. Repeating the bare key is what it does understand, so
 * array filters are expanded here rather than at every call site.
 */
const listParams = (query: Record<string, unknown>): URLSearchParams => {
  const params = new URLSearchParams();
  Object.entries(query).forEach(([key, value]) => {
    if (value === undefined || value === null || value === '') return;
    if (Array.isArray(value)) {
      value.forEach((item) => params.append(key, String(item)));
      return;
    }
    params.append(key, String(value));
  });
  return params;
};

/**
 * Pulls the download name out of `Content-Disposition`, falling back to a generated one.
 * The server already names the file after the receipt id; losing that through a proxy
 * must not stop the operator from saving the export.
 */
const filenameFromDisposition = (disposition: string | undefined, fallback: string): string => {
  const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition ?? '');
  return match ? decodeURIComponent(match[1]) : fallback;
};

/**
 * Replaces a Blob error body with the parsed problem object, in place, so the shared
 * message extractor can read it. Silent on failure: an unparseable body means the caller
 * falls back to its own message, which is still better than surfacing raw bytes.
 */
const decodeBlobProblem = async (error: unknown): Promise<void> => {
  const response = (error as { response?: { data?: unknown } } | undefined)?.response;
  if (!response || !(response.data instanceof Blob)) return;
  try {
    response.data = JSON.parse(await response.data.text());
  } catch {
    // Not JSON — leave the Blob alone.
  }
};

const httpPlatformApi: PlatformApi = {
  getOverview: async () => (await platformHttp.get<OverviewMetrics>('/api/platform/overview')).data,
  listTenants: async () =>
    (await platformHttp.get<BackendTenant[]>('/api/platform/tenants')).data.map(normalizeTenant),
  getTenant: async (id) =>
    normalizeTenant((await platformHttp.get<BackendTenant>(`/api/platform/tenants/${id}`)).data),
  updateTenantProfile: async (id, input) =>
    normalizeTenant((await platformHttp.put<BackendTenant>(`/api/platform/tenants/${id}/profile`, input)).data),
  updateTenantDataRegion: async (id, input) =>
    normalizeTenant(
      (await platformHttp.put<BackendTenant>(`/api/platform/tenants/${id}/data-region`, input)).data,
    ),
  listTenantAdminInvitations: async (id) =>
    (await platformHttp.get<TenantAdminInvitation[]>(`/api/platform/tenants/${id}/admin-invitations`)).data
      .map((item) => ({ ...item, id: String(item.id), userId: String(item.userId) })),
  resendTenantAdminInvitation: async (id, input) => {
    const body = { ...input, userId: input.userId == null ? null : Number(input.userId) };
    const result = (await platformHttp.post<ResendTenantAdminInvitationResult>(
      `/api/platform/tenants/${id}/admin-invitations/resend`, body,
    )).data;
    return {
      ...result,
      activationUrl: result.activationUrl ?? null,
      invitation: {
        ...result.invitation,
        id: String(result.invitation.id),
        userId: String(result.invitation.userId),
      },
    };
  },
  markTenantPastDue: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/mark-past-due`, { reason })).data),
  resolveTenantPastDue: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/resolve-past-due`, { reason })).data),
  listTenantDataAssets: async (tenantId) =>
    (await platformHttp.get<WireRecord[]>(`/api/platform/tenants/${tenantId}/data-assets`))
      .data.map(normalizeTenantDataAsset),
  getTenantActivationDataDecision: async (tenantId) =>
    normalizeTenantActivationDataDecision(
      (await platformHttp.get<WireRecord>(
        `/api/platform/tenants/${tenantId}/data-assets/activation-data-decision`,
      )).data,
    ),
  registerTenantDataAsset: async (tenantId, input) =>
    normalizeTenantDataAsset(
      (await platformHttp.post<WireRecord>(`/api/platform/tenants/${tenantId}/data-assets`, input)).data,
    ),
  verifyTenantDataAsset: async (tenantId, assetId, input) =>
    normalizeTenantDataAsset(
      (await platformHttp.post<WireRecord>(
        `/api/platform/tenants/${tenantId}/data-assets/${assetId}/verify`,
        { ...input, observedBusinessUnitId: Number(input.observedBusinessUnitId) },
      )).data,
    ),
  getTenantActivationDecision: async (tenantId) =>
    normalizeTenantActivationDecision(
      (await platformHttp.get<WireRecord>(`/api/platform/tenants/${tenantId}/activation/decision`)).data,
    ),
  activateTenant: async (tenantId) =>
    normalizeTenantActivationDecision(
      (await platformHttp.post<WireRecord>(`/api/platform/tenants/${tenantId}/activation`)).data,
    ),
  recordTenantActivationEvidence: async (tenantId, controlCode, input) =>
    (await platformHttp.post<ActivationControlEvidenceReceipt>(
      `/api/platform/tenants/${tenantId}/activation/controls/${encodeURIComponent(controlCode)}/evidence`,
      input,
    )).data,
  listTenantRecoveryEvidence: async (tenantId) =>
    (await platformHttp.get<WireRecord[]>(`/api/platform/tenants/${tenantId}/data-recovery/evidence`))
      .data.map(normalizeRecoveryEvidence),
  recordTenantRecoveryEvidence: async (tenantId, input) =>
    normalizeRecoveryEvidence(
      (await platformHttp.post<WireRecord>(`/api/platform/tenants/${tenantId}/data-recovery/evidence`, input)).data,
    ),
  getTenantDeletionCertificationDecision: async (tenantId) =>
    normalizeDeletionDecision(
      (await platformHttp.get<WireRecord>(
        `/api/platform/tenants/${tenantId}/data-recovery/deletion-certification`,
      )).data,
    ),
  certifyTenantDeletion: async (tenantId, reason) =>
    normalizeDeletionCertificate(
      (await platformHttp.post<WireRecord>(
        `/api/platform/tenants/${tenantId}/data-recovery/deletion-certification`, { reason },
      )).data,
    ),
  provisionTenant: async (input) => {
    // The response is ProvisionTenantResponse, not a bare tenant: it carries the founding
    // admin and, when the server generated it, a one-time credential that exists in this
    // payload and nowhere else.
    const { data } = await platformHttp.post<BackendProvisionResult>(
      '/api/platform/tenants',
      toProvisionRequestBody(input),
    );
    return {
      tenant: normalizeTenant(data.tenant),
      foundingAdmin: {
        userId: String(data.foundingAdmin.userId),
        email: data.foundingAdmin.email,
        roleName: data.foundingAdmin.roleName,
        generatedPassword: data.foundingAdmin.generatedPassword ?? null,
        invitation: data.foundingAdmin.invitation ?? null,
      },
      baseline: data.baseline ?? null,
      billing: data.billing ? { ...data.billing, warnings: data.billing.warnings ?? [] } : null,
    };
  },
  suspendTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/suspend`, { reason })).data),
  resumeTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/resume`, { reason })).data),
  archiveTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/archive`, { reason })).data),
  restoreTenant: async (id, reason) =>
    normalizeTenant((await platformHttp.post<BackendTenant>(`/api/platform/tenants/${id}/restore`, { reason })).data),
  changeTenantPlan: async (id, planId, reason) =>
    normalizeTenant((await platformHttp.put<BackendTenant>(`/api/platform/tenants/${id}/plan`, {
      planId: Number(planId),
      reason,
    })).data),
  impersonateTenant: async (id, reason) => {
    const response = (await platformHttp.post<BackendImpersonation>(`/api/platform/tenants/${id}/impersonate`, { reason })).data;
    return {
      tenantId: String(response.tenantId),
      token: response.token,
      expiresAt: response.expiresAtUtc,
      jti: readJti(response.token),
    };
  },
  listImpersonationSessions: async () =>
    (await platformHttp.get<BackendSession[]>('/api/platform/impersonation/sessions')).data.map(normalizeSession),
  revokeImpersonation: async (jti) =>
    normalizeSession((await platformHttp.post<BackendSession>(`/api/platform/impersonation/${jti}/revoke`)).data),
  getQueueStats: async () => (await platformHttp.get<QueueStats>('/api/platform/pipeline/queue')).data,
  listJobs: async (query) =>
    (await platformHttp.get<ExtractionJob[]>('/api/platform/pipeline/jobs', { params: query })).data,
  recoverPlatformDeadLetter: async (tenantId, input) => {
    const result = (await platformHttp.post<RecoverPlatformDeadLetterResult>(
      `/api/platform/tenants/${tenantId}/dead-letters/recover`, input,
    )).data;
    return { ...result, itemId: String(result.itemId), tenantId: String(result.tenantId) };
  },
  listPlans: async () =>
    (await platformHttp.get<BackendPlan[]>('/api/platform/plans')).data.map(normalizePlan),
  createPlan: async (input) =>
    normalizePlan((await platformHttp.post<BackendPlan>('/api/platform/plans', input)).data),
  updatePlan: async (id, input) =>
    normalizePlan((await platformHttp.put<BackendPlan>(`/api/platform/plans/${id}`, input)).data),
  listPlatformUsers: async () =>
    (await platformHttp.get<BackendOperator[]>('/api/platform/users')).data.map(normalizeOperator),
  createPlatformUser: async (input) =>
    normalizeOperator((await platformHttp.post<BackendOperator>('/api/platform/users', input)).data),
  changePlatformUserRole: async (id, role) =>
    normalizeOperator((await platformHttp.put<BackendOperator>(`/api/platform/users/${id}/role`, { role })).data),
  deactivatePlatformUser: async (id) =>
    normalizeOperator((await platformHttp.post<BackendOperator>(`/api/platform/users/${id}/deactivate`)).data),
  reactivatePlatformUser: async (id) =>
    normalizeOperator((await platformHttp.post<BackendOperator>(`/api/platform/users/${id}/reactivate`)).data),
  resetPlatformUserPassword: async (id, newPassword) =>
    normalizeOperator((await platformHttp.post<BackendOperator>(`/api/platform/users/${id}/password`, { newPassword })).data),
  resetPlatformUserMfa: async (id) => {
    await platformHttp.post(`/api/platform/users/${id}/mfa/reset`);
  },
  getBillingUsage: async (tenantId, period) => {
    const usage = (await platformHttp.get<BackendUsage>(`/api/platform/billing/usage/${tenantId}`, { params: { period } })).data;
    return {
      ...usage,
      tenantId: String(usage.tenantId),
      businessUnitId: usage.businessUnitId == null ? null : String(usage.businessUnitId),
    };
  },
  listBillingMeterCatalog: async () =>
    (await platformHttp.get<BillingMeterCatalogEntry[]>('/api/platform/billing/meter-catalog')).data,
  getBillingReadiness: async (tenantId, period) =>
    (await platformHttp.get<BillingReadiness>(`/api/platform/billing/readiness/${tenantId}`, {
      params: { period },
    })).data,
  correctUsageRating: async (usageEventId, idempotencyKey, reason) => {
    const wire = (await platformHttp.post<WireRecord>(
      `/api/platform/billing/usage-events/${usageEventId}/rating-corrections`,
      { idempotencyKey, reason },
    )).data;
    return {
      ...(wire as unknown as UsageRatingCorrection),
      id: asId(wire.id as string | number),
      tenantId: asId(wire.tenantId as string | number),
      rateCardId: asIdOrNull(wire.rateCardId as string | number | null),
      rateCardLineId: asIdOrNull(wire.rateCardLineId as string | number | null),
    };
  },
  proposeDocumentCoverage: async (tenantId, period, midPeriodCutoverUtc, reason) => {
    const wire = (await platformHttp.post<WireRecord>(
      `/api/platform/billing/tenants/${tenantId}/document-coverage/proposals`,
      { period, midPeriodCutoverUtc, reason },
    )).data;
    return { ...(wire as unknown as DocumentCoveragePolicy), tenantId: asId(wire.tenantId as string | number) };
  },
  approveDocumentCoverage: async (tenantId, period, reason) =>
    (await platformHttp.post<WireRecord[]>(
      `/api/platform/billing/tenants/${tenantId}/document-coverage/approve`,
      { period, reason },
    )).data.map((wire) => ({
      ...(wire as unknown as DocumentCoverageSegment),
      id: asId(wire.id as string | number),
      tenantId: asId(wire.tenantId as string | number),
    })),
  listRateCards: async () =>
    (await platformHttp.get<BackendRateCard[]>('/api/platform/billing/rate-cards')).data.map(normalizeRateCard),
  createRateCard: async (input) =>
    normalizeRateCard((await platformHttp.post<BackendRateCard>('/api/platform/billing/rate-cards', input)).data),
  updateRateCard: async (id, input) =>
    normalizeRateCard((await platformHttp.put<BackendRateCard>(`/api/platform/billing/rate-cards/${id}`, input)).data),
  listStatements: async (query) =>
    (await platformHttp.get<BackendStatement[]>('/api/platform/billing/statements', { params: query })).data.map(normalizeStatement),
  computeStatement: async (tenantId, period) =>
    normalizeStatement((await platformHttp.post<BackendStatement>('/api/platform/billing/statements/compute', {
      tenantId: Number(tenantId),
      period,
    })).data),
  finalizeStatement: async (id) =>
    normalizeStatement((await platformHttp.post<BackendStatement>(`/api/platform/billing/statements/${id}/finalize`)).data),
  getBillingCost: async (tenantId, period) => {
    const cost = (await platformHttp.get<BackendCost>(`/api/platform/billing/cost/${tenantId}`, { params: { period } })).data;
    return {
      ...cost,
      tenantId: String(cost.tenantId),
      businessUnitId: cost.businessUnitId == null ? null : String(cost.businessUnitId),
    };
  },
  listAudit: async (query) =>
    (await platformHttp.get<AuditEntry[]>('/api/platform/audit', { params: query })).data,

  // --- durable provisioning -------------------------------------------------

  submitProvisioning: async (input, { idempotencyKey, fromDraftId }) => {
    // The key travels as the conventional header. A double-submit — an impatient second
    // click, a retried request after a proxy timeout — then replays the ORIGINAL execution
    // instead of creating a second tenant for the same customer.
    const { data } = await platformHttp.post<{
      execution: WireRecord;
      created?: boolean;
      generatedPassword?: string | null;
      secretNotice?: string | null;
    }>(
      '/api/platform/provisioning/tenants',
      {
        tenant: toProvisionRequestBody(input),
        idempotencyKey,
        fromDraftId: fromDraftId == null ? null : Number(fromDraftId),
      },
      { headers: { 'Idempotency-Key': idempotencyKey } },
    );
    return {
      execution: normalizeExecution(data.execution),
      created: Boolean(data.created),
      generatedPassword: data.generatedPassword ?? null,
      secretNotice: data.secretNotice ?? null,
    };
  },
  getProvisioningExecution: async (id) =>
    normalizeExecution(
      (await platformHttp.get<WireRecord>(`/api/platform/provisioning/executions/${id}`)).data,
    ),
  listProvisioningExecutions: async (query) =>
    (await platformHttp.get<WireRecord[]>('/api/platform/provisioning/executions', { params: query })).data.map(
      normalizeExecution,
    ),
  retryProvisioning: async (id, body) =>
    normalizeExecution(
      (await platformHttp.post<WireRecord>(`/api/platform/provisioning/executions/${id}/retry`, {
        step: body.step ?? null,
        reason: body.reason,
      })).data,
    ),
  cancelProvisioning: async (id, reason) =>
    normalizeExecution(
      (await platformHttp.post<WireRecord>(`/api/platform/provisioning/executions/${id}/cancel`, { reason })).data,
    ),
  checkSlug: async (params) =>
    (await platformHttp.get<SlugAvailability>('/api/platform/provisioning/slug-check', { params })).data,
  listReservedSlugs: async () =>
    (await platformHttp.get<string[]>('/api/platform/provisioning/reserved-slugs')).data,
  listProvisioningDrafts: async () =>
    (await platformHttp.get<WireRecord[]>('/api/platform/provisioning/drafts')).data.map(normalizeDraft),
  getProvisioningDraft: async (id) =>
    normalizeDraft((await platformHttp.get<WireRecord>(`/api/platform/provisioning/drafts/${id}`)).data),
  createProvisioningDraft: async (name, payload) =>
    normalizeDraft(
      (await platformHttp.post<WireRecord>('/api/platform/provisioning/drafts', { name, payload })).data,
    ),
  updateProvisioningDraft: async (id, name, payload, version) =>
    normalizeDraft(
      // The version goes back with every save so two tabs open on one draft cannot
      // silently overwrite each other — the server answers 409 rather than losing work.
      (await platformHttp.put<WireRecord>(`/api/platform/provisioning/drafts/${id}`, {
        name,
        payload,
        version,
      })).data,
    ),
  deleteProvisioningDraft: async (id) => {
    await platformHttp.delete(`/api/platform/provisioning/drafts/${id}`);
  },

  // --- offboarding ----------------------------------------------------------

  getOffboarding: async (tenantId) =>
    normalizeOffboarding(
      (await platformHttp.get<WireRecord>(`/api/platform/tenants/${tenantId}/offboarding`)).data,
    ),
  listPendingDeletions: async () =>
    (await platformHttp.get<WireRecord[]>('/api/platform/tenants/offboarding/pending')).data.map((wire) => ({
      ...(wire as unknown as PendingTenantDeletion),
      tenantId: asId(wire.tenantId as string | number),
    })),
  getPurgePreview: async (tenantId) => {
    const wire = (
      await platformHttp.get<WireRecord>(`/api/platform/tenants/${tenantId}/offboarding/purge-preview`)
    ).data;
    return {
      ...(wire as unknown as TenantPurgePreview),
      tenantId: asId(wire.tenantId as string | number),
      businessUnitId: asId(wire.businessUnitId as string | number),
      tables: (wire.tables as TenantPurgePreview['tables']) ?? [],
      preserved: (wire.preserved as string[]) ?? [],
    };
  },
  exportTenantData: async (tenantId, body) => {
    // The export is a file, not JSON: it is handed to the customer, and the receipt hash
    // in the response headers is what proves the bytes they received are the bytes we sent.
    const response = await platformHttp
      .post<Blob>(
        `/api/platform/tenants/${tenantId}/offboarding/export`,
        body,
        { responseType: 'blob' },
      )
      .catch(async (error: unknown) => {
        // `responseType: 'blob'` applies to the FAILURE body too, so a refusal arrives as
        // a Blob containing `{error: "…"}` and every message extractor downstream sees an
        // object with no `error` property. Decoded here, once, so a refused export reads
        // like every other refused platform call instead of a generic failure line.
        await decodeBlobProblem(error);
        throw error;
      });
    const headers = response.headers as Record<string, string | undefined>;
    const rows = headers['x-nexora-export-rows'];
    return {
      blob: response.data,
      filename: filenameFromDisposition(
        headers['content-disposition'],
        `nexora-export-tenant-${tenantId}.json`,
      ),
      sha256: headers['x-nexora-export-sha256'] ?? null,
      receiptId: headers['x-nexora-export-receipt'] ?? null,
      totalRows: rows == null ? null : Number(rows),
    };
  },
  scheduleTenantDeletion: async (tenantId, body) =>
    normalizeOffboarding(
      (await platformHttp.post<WireRecord>(
        `/api/platform/tenants/${tenantId}/offboarding/schedule-deletion`,
        { reason: body.reason, retentionDays: body.retentionDays ?? null },
      )).data,
    ),
  cancelTenantDeletion: async (tenantId, reason) =>
    normalizeOffboarding(
      (await platformHttp.post<WireRecord>(
        `/api/platform/tenants/${tenantId}/offboarding/cancel-deletion`,
        { reason },
      )).data,
    ),
  purgeTenant: async (tenantId, body) => {
    const wire = (
      await platformHttp.post<WireRecord>(`/api/platform/tenants/${tenantId}/offboarding/purge`, body)
    ).data;
    return {
      ...(wire as unknown as TenantPurgeResult),
      tenantId: asId(wire.tenantId as string | number),
      tables: (wire.tables as TenantPurgeResult['tables']) ?? [],
      disclosures: (wire.disclosures as string[]) ?? [],
    };
  },
  eraseTenantPersonalData: async (tenantId, body) => {
    const wire = (
      await platformHttp.post<WireRecord>(
        `/api/platform/tenants/${tenantId}/offboarding/erase-personal-data`,
        body,
      )
    ).data;
    return {
      ...(wire as unknown as TenantErasureResult),
      tenantId: asId(wire.tenantId as string | number),
      targets: (wire.targets as TenantErasureResult['targets']) ?? [],
      disclosures: (wire.disclosures as string[]) ?? [],
    };
  },
  listTenantLegalHolds: async (tenantId) =>
    (await platformHttp.get<TenantLegalHold[]>(`/api/platform/tenants/${tenantId}/legal-holds`)).data.map(
      (hold) => ({ ...hold, id: String(hold.id), tenantId: String(hold.tenantId) }),
    ),
  placeTenantLegalHold: async (tenantId, body) => {
    const hold = (await platformHttp.post<TenantLegalHold>(
      `/api/platform/tenants/${tenantId}/legal-holds`, body,
    )).data;
    return { ...hold, id: String(hold.id), tenantId: String(hold.tenantId) };
  },
  releaseTenantLegalHold: async (tenantId, holdId, reason) => {
    const hold = (await platformHttp.post<TenantLegalHold>(
      `/api/platform/tenants/${tenantId}/legal-holds/${holdId}/release`, { reason },
    )).data;
    return { ...hold, id: String(hold.id), tenantId: String(hold.tenantId) };
  },
  getTenantAiPolicy: async (tenantId) => {
    const policy = (await platformHttp.get<TenantAiPolicy>(`/api/platform/tenants/${tenantId}/ai-policy`)).data;
    return { ...policy, businessUnitId: String(policy.businessUnitId) };
  },
  updateTenantAiPolicy: async (tenantId, body) => {
    const policy = (await platformHttp.put<TenantAiPolicy>(`/api/platform/tenants/${tenantId}/ai-policy`, body)).data;
    return { ...policy, businessUnitId: String(policy.businessUnitId) };
  },
  getTenantAiProviders: async (tenantId) => {
    const view = (await platformHttp.get<AiProviderTrustView>(`/api/platform/tenants/${tenantId}/ai-providers`)).data;
    return {
      ...view,
      authorizations: view.authorizations.map((item) => ({
        ...item,
        id: String(item.id),
        authorizedByUserId: String(item.authorizedByUserId),
      })),
    };
  },
  authorizeTenantAiProvider: async (tenantId, body) => {
    const result = (await platformHttp.post<{ authorization: AiProviderAuthorization }>(
      `/api/platform/tenants/${tenantId}/ai-providers`, body,
      { headers: { 'Idempotency-Key': crypto.randomUUID() } },
    )).data.authorization;
    return { ...result, id: String(result.id), authorizedByUserId: String(result.authorizedByUserId) };
  },
  revokeTenantAiProvider: async (tenantId, authorizationId, reason) => {
    const result = (await platformHttp.post<{ authorization: AiProviderAuthorization }>(
      `/api/platform/tenants/${tenantId}/ai-providers/${authorizationId}/revoke`,
      { authorizationId: Number(authorizationId), reason },
      { headers: { 'Idempotency-Key': crypto.randomUUID() } },
    )).data.authorization;
    return { ...result, id: String(result.id), authorizedByUserId: String(result.authorizedByUserId) };
  },

  // --- billing console ------------------------------------------------------

  getRevenueRisk: async (query) => {
    const wire = (await platformHttp.get<WireRecord>('/api/platform/billing/revenue-risk', { params: query }))
      .data;
    return {
      ...(wire as unknown as RevenueRiskReport),
      tenants: ((wire.tenants as WireRecord[]) ?? []).map(normalizeRevenueRisk),
    };
  },
  getTenantBillingProfile: async (tenantId) =>
    normalizeBillingProfile(
      (await platformHttp.get<WireRecord>(`/api/platform/billing/tenants/${tenantId}`)).data,
    ),
  listSubscriptionInvoices: async (tenantId) =>
    (await platformHttp.get<WireRecord[]>('/api/platform/billing/invoices', { params: { tenantId } }))
      .data.map(normalizeSubscriptionInvoice),
  createSubscriptionInvoice: async (input) =>
    normalizeSubscriptionInvoice(
      (await platformHttp.post<WireRecord>('/api/platform/billing/invoices', {
        ...input,
        statementId: Number(input.statementId),
      })).data,
    ),
  finalizeSubscriptionInvoice: async (id) =>
    normalizeSubscriptionInvoice(
      (await platformHttp.post<WireRecord>(`/api/platform/billing/invoices/${id}/finalize`)).data,
    ),
  creditSubscriptionInvoice: async (id, amount, reason) => {
    const credit = (await platformHttp.post<WireRecord>(`/api/platform/billing/invoices/${id}/credits`, {
      amount,
      reason,
    }, { headers: { 'Idempotency-Key': crypto.randomUUID() } })).data;
    return { ...(credit as unknown as SubscriptionCreditNote), id: asId(credit.id as string | number) };
  },
  recordSubscriptionPayment: async (id, amount, externalReference, receivedAtUtc) => {
    const payment = (await platformHttp.post<WireRecord>(`/api/platform/billing/invoices/${id}/payments`, {
      amount,
      externalReference,
      receivedAtUtc,
    })).data;
    return { ...(payment as unknown as SubscriptionPayment), id: asId(payment.id as string | number) };
  },
  proposeSubscriptionRevenueAction: async (id, body) => {
    const action = (await platformHttp.post<WireRecord>(
      `/api/platform/billing/invoices/${id}/revenue-actions`, body,
      { headers: { 'Idempotency-Key': crypto.randomUUID() } },
    )).data;
    return { ...(action as unknown as SubscriptionRevenueAction), id: asId(action.id as string | number), invoiceId: asId(action.invoiceId as string | number) };
  },
  approveSubscriptionRevenueAction: async (actionId) => {
    const action = (await platformHttp.post<WireRecord>(
      `/api/platform/billing/invoices/revenue-actions/${actionId}/approve`,
    )).data;
    return { ...(action as unknown as SubscriptionRevenueAction), id: asId(action.id as string | number), invoiceId: asId(action.invoiceId as string | number) };
  },
  setTenantRateCard: async (tenantId, body) =>
    normalizeBillingProfile(
      (await platformHttp.put<WireRecord>(`/api/platform/billing/tenants/${tenantId}/rate-card`, {
        rateCardId: body.rateCardId == null ? null : Number(body.rateCardId),
        reason: body.reason,
      })).data,
    ),
  setTenantCommercialTerms: async (tenantId, body) =>
    normalizeBillingProfile(
      (await platformHttp.put<WireRecord>(
        `/api/platform/billing/tenants/${tenantId}/commercial-terms`,
        body,
      )).data,
    ),
  setTenantAccountContact: async (tenantId, body) =>
    normalizeBillingProfile(
      (await platformHttp.put<WireRecord>(
        `/api/platform/billing/tenants/${tenantId}/account-contact`,
        body,
      )).data,
    ),
  getStatement: async (id) =>
    normalizeStatement((await platformHttp.get<BackendStatement>(`/api/platform/billing/statements/${id}`)).data),

  // --- support desk ---------------------------------------------------------

  listSupportTickets: async (query) =>
    normalizePaged(
      (await platformHttp.get<{ items?: WireRecord[] }>('/api/platform/support/tickets', {
        params: listParams((query ?? {}) as Record<string, unknown>),
      })).data,
      normalizeTicketSummary,
    ),
  getSupportTicket: async (id) =>
    normalizeTicketDetail((await platformHttp.get<WireRecord>(`/api/platform/support/tickets/${id}`)).data),
  getSupportTicketTimeline: async (id) => {
    const wire = (await platformHttp.get<WireRecord>(`/api/platform/support/tickets/${id}/timeline`)).data;
    return {
      ticketId: asId(wire.ticketId as string | number),
      tenantId: asId(wire.tenantId as string | number),
      entries: (wire.entries as SupportTicketTimeline['entries']) ?? [],
    };
  },
  createSupportTicket: async (input) =>
    normalizeTicketDetail(
      (await platformHttp.post<WireRecord>('/api/platform/support/tickets', {
        tenantId: Number(input.tenantId),
        subject: input.subject,
        body: input.body,
        severity: input.severity,
        requesterEmail: input.requesterEmail,
        assignToPlatformUserId:
          input.assignToPlatformUserId == null ? null : Number(input.assignToPlatformUserId),
      })).data,
    ),
  addSupportTicketNote: async (id, body) =>
    normalizeTicketDetail(
      (await platformHttp.post<WireRecord>(`/api/platform/support/tickets/${id}/notes`, body)).data,
    ),
  transitionSupportTicket: async (id, body) =>
    normalizeTicketDetail(
      (await platformHttp.post<WireRecord>(`/api/platform/support/tickets/${id}/transition`, body)).data,
    ),
  assignSupportTicket: async (id, body) =>
    normalizeTicketDetail(
      (await platformHttp.post<WireRecord>(`/api/platform/support/tickets/${id}/assign`, {
        assignToPlatformUserId:
          body.assignToPlatformUserId == null ? null : Number(body.assignToPlatformUserId),
        reason: body.reason,
        expectedVersion: body.expectedVersion,
      })).data,
    ),
  changeSupportTicketSeverity: async (id, body) =>
    normalizeTicketDetail(
      (await platformHttp.post<WireRecord>(`/api/platform/support/tickets/${id}/severity`, body)).data,
    ),

  // --- audit explorer -------------------------------------------------------

  queryAudit: async (query) =>
    normalizePaged(
      (await platformHttp.get<{ items?: WireRecord[] }>('/api/platform/audit/query', {
        params: listParams((query ?? {}) as Record<string, unknown>),
      })).data,
      normalizeAuditEntry,
    ),
  getAuditEntry: async (id) => {
    const wire = (await platformHttp.get<WireRecord>(`/api/platform/audit/entries/${id}`)).data;
    return {
      ...normalizeAuditEntry(wire),
      before: wire.before ?? null,
      after: wire.after ?? null,
      // Empty rather than invented: an explorer that fabricates a plausible diff is worse
      // than one that says the row recorded no before/after pair.
      changes: (wire.changes as PlatformAuditEntryDetail['changes']) ?? [],
    };
  },
  listAuditActions: async (query) =>
    (await platformHttp.get<PlatformAuditAction[]>('/api/platform/audit/actions', { params: query })).data,
  getTenantTimeline: async (tenantId, query) =>
    (await platformHttp.get<TenantTimelineEntry[]>(`/api/platform/audit/tenants/${tenantId}/timeline`, {
      params: query,
    })).data,

  // --- one-call tenant operations summary -----------------------------------

  getTenantOperationsSummary: async (tenantId) => {
    const wire = (
      await platformHttp.get<WireRecord>(`/api/platform/tenants/${tenantId}/operations-summary`)
    ).data;
    const lifecycle = wire.lifecycle as WireRecord;
    const support = wire.support as WireRecord;
    const audit = wire.audit as WireRecord;
    const impersonation = wire.impersonation as WireRecord;
    return {
      generatedAtUtc: wire.generatedAtUtc as string,
      lifecycle: {
        ...(lifecycle as unknown as TenantOperationsSummary['lifecycle']),
        tenantId: asId(lifecycle.tenantId as string | number),
        planId: asIdOrNull(lifecycle.planId as string | number | null),
        primaryBusinessUnitId: asIdOrNull(lifecycle.primaryBusinessUnitId as string | number | null),
      },
      support: {
        openTicketCount: num(support.openTicketCount),
        unassignedOpenTicketCount: num(support.unassignedOpenTicketCount),
        openByStatus: (support.openByStatus as Record<string, number>) ?? {},
        openBySeverity: (support.openBySeverity as Record<string, number>) ?? {},
        oldestOpenTicketCreatedAtUtc: (support.oldestOpenTicketCreatedAtUtc as string) ?? null,
        recentTickets: ((support.recentTickets as WireRecord[]) ?? []).map(normalizeTicketSummary),
      },
      audit: {
        entryCountLast30Days: num(audit.entryCountLast30Days),
        failureCountLast30Days: num(audit.failureCountLast30Days),
        lastActionAtUtc: (audit.lastActionAtUtc as string) ?? null,
        recentEntries: ((audit.recentEntries as WireRecord[]) ?? []).map(normalizeAuditEntry),
      },
      impersonation: {
        activeSessionCount: num(impersonation.activeSessionCount),
        sessionCountLast30Days: num(impersonation.sessionCountLast30Days),
        sessions: ((impersonation.sessions as WireRecord[]) ?? []).map((session) => ({
          ...(session as unknown as TenantOperationsSummary['impersonation']['sessions'][number]),
          actorPlatformUserId: asId(session.actorPlatformUserId as string | number),
          linkedTicketIds: ((session.linkedTicketIds as (string | number)[]) ?? []).map(asId),
        })),
      },
    };
  },

  // --- outbound email --------------------------------------------------------
  //
  // No normalisation layer here, deliberately: this surface carries no ids, so
  // there is nothing to coerce, and a hand-written mapper would only be one more
  // place for a field to be dropped silently.

  getEmailSettings: async () =>
    (await platformHttp.get<PlatformEmailSettings>('/api/platform/notifications/email/settings')).data,

  getEmailStatus: async () =>
    (await platformHttp.get<OutboundEmailStatus>('/api/platform/notifications/email/status')).data,

  saveEmailSettings: async (input) =>
    (await platformHttp.put<PlatformEmailSettings>('/api/platform/notifications/email/settings', input)).data,

  listEmailProviders: async () =>
    (await platformHttp.get<EmailProviderCapability[]>('/api/platform/notifications/email/providers')).data,

  // Always 200, even when the connection is refused: a refusal is a successful
  // diagnosis, not a failed request, and the screen renders the staged report either way.
  testEmailConnection: async (input) =>
    (await platformHttp.post<MailConnectionTestResult>(
      '/api/platform/notifications/email/connection-test',
      input,
    )).data,

  testSendEmail: async (body) =>
    (await platformHttp.post<TestOutboundEmailResult>(
      '/api/platform/notifications/email/test-send',
      body,
    )).data,
};

export const platformApi: PlatformApi = httpPlatformApi;
