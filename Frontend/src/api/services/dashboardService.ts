import axios from 'axios';
import axiosInstance from './../axiosInstance';
import type { OrderStatsDTO } from './orderService';

// ─── GET /api/Dashboard/{businessUnitId} ────────────────────────────────────
// Mirrors Backend DTOs/Dashboard/DashboardDTOs.cs (serialized camelCase).

export interface StatTrendDTO {
  /** Pre-formatted magnitude, e.g. "12.3%" (30d vs the prior 30d). */
  value: string;
  isUp: boolean;
}

export interface ConversionRatesDTO {
  leadToRfq: number;
  rfqToQuote: number;
  quoteToOrder: number;
}

export interface DashboardStatsDTO {
  totalLeads: number;
  activeLeads: number;
  totalRfqs: number;
  rfqsQuoted: number;
  bidRatio: number;
  totalLineItems: number;
  l1Quoted: number;
  totalOrderValue: number;
  winVolumeRatio: number;
  avgQuoteValue: number;
  avgOrderValue: number;
  customerCount: number;
  conversionRates: ConversionRatesDTO;
  leadsTrend?: StatTrendDTO;
  rfqsTrend?: StatTrendDTO;
  ordersTrend?: StatTrendDTO;
}

/** One point per month, oldest→newest, last 6 months. count = RFQs created, value = order total. */
export interface MonthlyTrendDTO {
  month: string;
  count: number;
  /**
   * Null — not zero — when the business unit has no single active base currency, which is the
   * default state of a new tenant. A reader that treats null as 0 draws a line along the axis and
   * calls it a measurement; treat it as "the server could not state this" and show the reason.
   */
  value: number | null;
  valueCurrency?: string | null;
  valueUnavailableReason?: string | null;
}

export interface CategoryDistributionDTO {
  categoryName: string;
  count: number;
  percentage: number;
}

/**
 * Radar entries from the backend. The "AI Accuracy" subject carries the real
 * average lead extraction confidence (0–100) — the only server-side aggregate
 * of Lead.Aiconfidence available today.
 */
export interface RadarDataDTO {
  subject: string;
  a: number;
  b: number;
}

export interface RecentItemDTO {
  id: string;
  type: string;
  description: string;
  status: string;
  date: string;
}

export interface DashboardDataDTO {
  stats: DashboardStatsDTO;
  volumeTrend: MonthlyTrendDTO[];
  statusDistribution: CategoryDistributionDTO[];
  efficiencyVelocity: CategoryDistributionDTO[];
  operationalHealth: RadarDataDTO[];
  recentItems: RecentItemDTO[];
  sourceDistribution: CategoryDistributionDTO[];
}

// GET /api/dashboard/release-01
// One server-generated snapshot. Unlike the legacy dashboard endpoints, every
// value and drill-down shares the same tenant, cohort window, and freshness.
export type Release01KpiState = 'available' | 'insufficient_data';
export type Release01KpiUnit = 'count' | 'percentage' | 'currency' | 'hours' | 'score' | 'weighted_work';

export interface Release01DrillDownIdentifierDTO {
  recordType: string;
  recordId: number;
  commercialCaseId: number;
  nexoraSerial: string;
  classification?: string | null;
  occurredAt?: string | null;
  durationHours?: number | null;
}

export interface Release01KpiDTO {
  key: string;
  label: string;
  value: number | null;
  state: Release01KpiState;
  unit: Release01KpiUnit;
  numerator?: number | null;
  denominator?: number | null;
  definition: string;
  insufficientDataReason?: string | null;
  drillDownIdentifiers: Release01DrillDownIdentifierDTO[];
}

export interface Release01DashboardDTO {
  definitionVersion: 'release-01';
  generatedAt: string;
  filter: { from: string; to: string; boundary: '[from,to)' };
  /**
   * FR-DSH-05 — three tiers, not two. `scope` is 'tenant' | 'managed_scope' | 'assigned_accounts';
   * the middle value is new and did not previously exist on this contract, so a supervisor was
   * served either the whole tenant or one person's work.
   *
   * `accountTeamIds` is empty on the tenant tier because that tier is not scoped by team at all —
   * which is a different fact from a caller who is on no team, and the two are told apart by
   * `scope`, never by the length of the list.
   */
  roleScope: {
    scope: 'tenant' | 'managed_scope' | 'assigned_accounts' | string;
    ownerUserId?: number | null;
    accountTeamIds?: number[];
    scopedUserIds?: number[];
  };
  kpis: Release01KpiDTO[];
}

export interface Release01DashboardParams {
  from: string;
  to: string;
}

// ─── Per-module stat endpoints (verified against backend DTOs) ──────────────

/** GET /api/Lead/stats — LeadStatsDTO.cs */
export interface LeadStatsDTO {
  totalActiveLeads: number;
  highConfidenceLeads: number;
  closingSoonLeads: number;
  totalLeadSources: number;
}

/** GET /api/Rfq/stats — RfqStatsDTO.cs */
export interface RfqStatsDTO {
  totalRfqs: number;
  draftRfqs: number;
  submittedRfqs: number;
  closingSoonRfqs: number;
}

/** GET /api/Quote/stats — QuoteStatsDTO.cs */
export interface QuoteStatsDTO {
  totalQuotes: number;
  acceptedQuotes: number;
  pendingQuotes: number;
  expiredQuotes: number;
  totalQuotedAmount: number;
}

/** GET /api/Order/stats — single source of truth lives in orderService. */
export type { OrderStatsDTO } from './orderService';

// ─── POST /api/intelligence/leads/decision-summaries ────────────────────────
// Contract fixed (LeadDecisionController.cs); degrade silently if unavailable.

export type LeadRecommendation = 'bid' | 'review' | 'skip';
export type LeadUrgency = 'overdue' | 'critical' | 'soon' | 'comfortable' | 'unknown';

export interface LeadDecisionSummary {
  leadId: number;
  coveragePct: number;
  estimatedValue: number;
  daysLeft: number | null;
  urgency: LeadUrgency;
  recommendation: LeadRecommendation;
}

export interface DecisionSummariesResponse {
  /** Keyed by leadId (serialized as string keys in JSON). */
  summaries: Record<string, LeadDecisionSummary>;
}

// ─── GET /api/dashboard/workload (WP-B1, managers only) ─────────────────────
// Mirrors Backend DTOs/Dashboard/WaveBAnalyticsDTOs.cs.

export interface TeamWorkloadRowDTO {
  /** Null for the unassigned bucket row. */
  userId: number | null;
  name: string;
  email: string | null;
  openLeads: number;
  overdueLeads: number;
  sentQuotes: number;
  staleQuotes: number;
  isUnassignedBucket: boolean;
}

export interface TeamWorkloadDTO {
  rows: TeamWorkloadRowDTO[];
  /** The BU's stale threshold in days (used for the column hint). */
  staleQuoteDays: number;
  generatedAt: string;
}

// ─── GET /api/dashboard/pipeline-analytics (WP-B2) ──────────────────────────

export interface PipelineStageDTO {
  key: 'leads' | 'accepted' | 'quoted' | 'won';
  label: string;
  count: number;
  /** Null when the stage spans currencies with no approved rate — never a partial sum. */
  value: number | null;
  valueCurrency: string | null;
  valueUnavailableReason: string | null;
}

export interface PipelineLossReasonDTO {
  reason: string;
  count: number;
  value: number | null;
  valueCurrency: string | null;
  valueUnavailableReason: string | null;
}

export interface PipelineAnalyticsDTO {
  funnel: PipelineStageDTO[];
  lossReasons: PipelineLossReasonDTO[];
  weightedForecast: number | null;
  forecastCurrency: string | null;
  forecastUnavailableReason: string | null;
  awaitingResponseQuotes: number;
  awaitingResponseValue: number | null;
  respondedQuotes: number;
  respondedValue: number | null;
  /** 'all_time' — this funnel has never been date-filtered, and now says so. */
  funnelScope: string;
  generatedAt: string;
}

// ─── GET /api/dashboard/gross-margin ────────────────────────────────────────
// Replaces PipelineAnalyticsDTO.avgMarginPct, which was an unweighted mean of per-line
// percentages taken against a product-card cost that is not a landed cost, over every quote
// line ever written. It is gone rather than deprecated: leaving it would have kept a wrong
// number on a live contract.

export interface GrossMarginDTO {
  status: 'available' | 'unavailable';
  /** Null unless status is 'available'. Never a placeholder. */
  marginPercent: number | null;
  revenueTotal: number | null;
  costTotal: number | null;
  /** ISO code both totals are expressed in. Pass to formatMoney; never assume a symbol. */
  currencyCode: string | null;
  sampleLines: number;
  sampleQuotes: number;
  acceptedQuoteLines: number;
  linesWithoutSourcingEvidence: number;
  quotesExcludedForMissingAcceptanceDate: number;
  /** Why the figure is unavailable, in words fit to show a user. */
  reason: string | null;
  costBasisChangedOn: string | null;
  linesOnPriorCostBasis: number;
  linesOnCurrentCostBasis: number;
  marginPercentCurrentBasisOnly: number | null;
  costBasisNote: string | null;
  periodFrom: string;
  periodTo: string;
  periodBoundary: string;
  acceptedDefinition: string;
  generatedAt: string;
}

// ─── GET /api/analytics/brand-demand ────────────────────────────────────────
// Which manufacturers the tenant's customers are actually asking for, grouped
// from LeadItems by normalised manufacturer name. Every figure carries its
// denominator: lines, units, and the number of DISTINCT documents behind it —
// concentration read off lines alone is misleading when two documents supply
// most of the volume.

// These two interfaces MUST mirror BrandDemandDTO / BrandDemandRowDTO in
// Backend/.../DTOs/Dashboard/PilotAnalyticsDTOs.cs. They previously did not: they declared
// `lineCount`, `documentCount`, a `totalDocuments` total and a `variants` string[], none of
// which the API has ever returned. Every row therefore rendered `undefined`, and
// `data.totalDocuments.toLocaleString()` threw — so the sidebar's "Brand Demand" entry led
// to a blank error boundary on a tenant with no data, which is every new tenant.
export interface BrandDemandRowDTO {
  /** Normalised manufacturer name, e.g. "CROUSE HINDS/EATON". */
  manufacturer: string;
  normalizedKey: string;
  /** How many raw spellings were folded into this row. A COUNT, not the list of spellings. */
  variants: number;
  lines: number;
  /** Documents (leads) this manufacturer appears on. The honest weight. */
  documents: number;
  totalQuantity: number | null;
  lineSharePercent: number;
}

export interface BrandDemandDTO {
  generatedAt: string;
  from: string | null;
  to: string | null;
  /** Denominators for the whole window, so shares can be stated honestly. */
  totalLines: number;
  linesWithManufacturer: number;
  linesWithoutManufacturer: number;
  distinctManufacturers: number;
  distinctRawSpellings: number;
  topFiveLineSharePercent: number;
  rows: BrandDemandRowDTO[];
  quantityCaveat: string;
}

export interface BrandDemandParams {
  from?: string;
  to?: string;
}

// ─── GET /api/dashboard/deadline-board ──────────────────────────────────────
// Forward-looking workload: every open enquiry the caller may see, bucketed by how many days
// are left until its bid closing date, with the line-item count each bucket carries.
//
// These interfaces MUST mirror DeadlineBoardDTO / DeadlineBucketDTO / DeadlineLeadDTO in
// Backend/.../DTOs/Dashboard/PilotAnalyticsDTOs.cs. The endpoint has been live since the pilot
// analytics work but had no client here: DeadlineBoardPage bucketises leadService.getAll() in
// the browser instead, which is why it caps at 500 rows and why its buckets are its own. This
// method exists so a screen can read the SERVER's buckets, over the server's own scope.
//
// The window is fixed by the endpoint: it takes no from/to, only how many lead rows to return
// alongside the counts. Callers that draw the buckets never need the rows.

export interface DeadlineBucketDTO {
  /** Stable key, in server order: overdue | today | days_1_3 | days_4_7 | days_8_30 | later | unknown. */
  key: string;
  label: string;
  /** Open leads in this bucket. */
  leads: number;
  /** Line items across those leads — the work the bucket actually represents. */
  lineItems: number;
}

export interface DeadlineLeadDTO {
  leadId: number;
  rfqno: string | null;
  buyersName: string | null;
  bidClosingDate: string | null;
  /** Whole days to the deadline; null when the enquiry states no usable closing date. */
  daysLeft: number | null;
  bucket: string;
  lineItems: number;
  awaitingReview: boolean;
  lateIngested: boolean;
}

export interface DeadlineBoardDTO {
  generatedAt: string;
  openLeads: number;
  openLineItems: number;
  /** Open leads carrying no usable closing date — a data gap, not a comfortable deadline. */
  leadsWithoutClosingDate: number;
  /**
   * Open leads that reached Nexora AFTER their own closing date. They sit in the overdue bucket
   * but are not a handling failure, so the count is published separately rather than inferred.
   */
  lateIngestedExcludedLeads: number;
  buckets: DeadlineBucketDTO[];
  /** Most urgent first, capped by `maxLeads`. Undated leads sort last but are never hidden. */
  leads: DeadlineLeadDTO[];
}

// ─── Service ────────────────────────────────────────────────────────────────

const dashboardService = {
  getRelease01: async (params: Release01DashboardParams): Promise<Release01DashboardDTO> => {
    const r = await axiosInstance.get<Release01DashboardDTO>('/api/dashboard/release-01', { params });
    return r.data;
  },

  getDashboard: async (businessUnitId: number): Promise<DashboardDataDTO> => {
    const r = await axiosInstance.get<DashboardDataDTO>(`/api/Dashboard/${businessUnitId}`);
    return r.data;
  },

  getLeadStats: async (): Promise<LeadStatsDTO> => {
    const r = await axiosInstance.get<LeadStatsDTO>('/api/Lead/stats');
    return r.data;
  },

  getRfqStats: async (): Promise<RfqStatsDTO> => {
    const r = await axiosInstance.get<RfqStatsDTO>('/api/Rfq/stats');
    return r.data;
  },

  getQuoteStats: async (): Promise<QuoteStatsDTO> => {
    const r = await axiosInstance.get<QuoteStatsDTO>('/api/Quote/stats');
    return r.data;
  },

  getOrderStats: async (): Promise<OrderStatsDTO> => {
    const r = await axiosInstance.get<OrderStatsDTO>('/api/Order/stats');
    return r.data;
  },

  /** Batch Bid/Review/Skip cards. Max 100 ids; unknown ids are simply omitted. */
  getDecisionSummaries: async (leadIds: number[]): Promise<DecisionSummariesResponse> => {
    const r = await axiosInstance.post<DecisionSummariesResponse>(
      '/api/intelligence/leads/decision-summaries',
      { leadIds }
    );
    return r.data;
  },

  /** WP-B1: per-rep workload for the caller's BU. Managers/admins only (403 otherwise). */
  getTeamWorkload: async (): Promise<TeamWorkloadDTO> => {
    const r = await axiosInstance.get<TeamWorkloadDTO>('/api/dashboard/workload');
    return r.data;
  },

  /** WP-B2: stage funnel, loss reasons and weighted forecast. */
  getPipelineAnalytics: async (): Promise<PipelineAnalyticsDTO> => {
    const r = await axiosInstance.get<PipelineAnalyticsDTO>('/api/dashboard/pipeline-analytics');
    return r.data;
  },

  /**
   * FR-DSH-02: value-weighted gross margin over accepted quotes in a window.
   *
   * Always returns a payload. `status: 'unavailable'` with a `reason` is a valid answer, not an
   * error — the endpoint deliberately does not 4xx when a figure cannot be evidenced, so the screen
   * can show the explanation instead of a failure state.
   */
  getGrossMargin: async (params: { from?: string; to?: string } = {}): Promise<GrossMarginDTO> => {
    const r = await axiosInstance.get<GrossMarginDTO>('/api/dashboard/gross-margin', { params });
    return r.data;
  },

  /**
   * Brand demand concentration. Returns null — never throws — when the endpoint
   * is not deployed yet (404/501) or the caller is not entitled to it (403), so
   * the page shows an honest "not available yet" state instead of an error.
   */
  getBrandDemand: async (params: BrandDemandParams = {}): Promise<BrandDemandDTO | null> => {
    try {
      // The route is /api/brand-demand (BrandDemandController). It was called as
      // '/api/analytics/brand-demand', which no controller serves and no proxy rewrites, so every
      // request 404'd — and because this catch maps 404 to null, the page rendered its "not
      // available yet" empty state permanently instead of showing the error.
      const r = await axiosInstance.get<BrandDemandDTO>('/api/brand-demand', { params });
      if (r.status === 204 || !r.data) return null;
      return { ...r.data, rows: r.data.rows ?? [] };
    } catch (error: unknown) {
      if (axios.isAxiosError(error)) {
        const status = error.response?.status;
        if (status === 404 || status === 403 || status === 501) return null;
      }
      throw error;
    }
  },

  /**
   * Open enquiries bucketed by time left to their bid closing date, scoped server-side to the
   * caller's account team. `maxLeads` caps only the `leads` array; the bucket counts are always
   * over every open enquiry in scope, so a caller drawing only the buckets can ask for the
   * smallest page and still receive complete figures.
   */
  getDeadlineBoard: async (params: { maxLeads?: number } = {}): Promise<DeadlineBoardDTO> => {
    const r = await axiosInstance.get<DeadlineBoardDTO>('/api/dashboard/deadline-board', { params });
    return { ...r.data, buckets: r.data.buckets ?? [], leads: r.data.leads ?? [] };
  },
};

export default dashboardService;
