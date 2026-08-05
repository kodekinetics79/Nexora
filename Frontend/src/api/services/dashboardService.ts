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
  value: number;
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
  roleScope: { scope: string; ownerUserId?: number | null };
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
  value: number;
}

export interface PipelineLossReasonDTO {
  reason: string;
  count: number;
  value: number;
}

export interface PipelineAnalyticsDTO {
  funnel: PipelineStageDTO[];
  lossReasons: PipelineLossReasonDTO[];
  weightedForecast: number;
  awaitingResponseQuotes: number;
  awaitingResponseValue: number;
  respondedQuotes: number;
  respondedValue: number;
  /** Null when no quote line has floor (cost) data. */
  avgMarginPct: number | null;
  marginSampleLines: number;
  totalQuoteLines: number;
  generatedAt: string;
}

// ─── GET /api/analytics/brand-demand ────────────────────────────────────────
// Which manufacturers the tenant's customers are actually asking for, grouped
// from LeadItems by normalised manufacturer name. Every figure carries its
// denominator: lines, units, and the number of DISTINCT documents behind it —
// concentration read off lines alone is misleading when two documents supply
// most of the volume.

export interface BrandDemandRowDTO {
  /** Normalised manufacturer name, e.g. "CROUSE HINDS/EATON". */
  manufacturer: string;
  /** Raw spellings folded into this row — shown so the grouping is auditable. */
  variants?: string[] | null;
  lineCount: number;
  totalQuantity: number | null;
  /** Documents (leads) this manufacturer appears on. The honest weight. */
  documentCount: number;
}

export interface BrandDemandDTO {
  rows: BrandDemandRowDTO[];
  /** Denominators for the whole window, so shares can be stated honestly. */
  totalLines: number;
  linesWithManufacturer: number;
  distinctManufacturers: number;
  totalDocuments: number;
  generatedAt: string;
  filter?: { from: string; to: string } | null;
}

export interface BrandDemandParams {
  from?: string;
  to?: string;
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

  /** WP-B2: stage funnel, loss reasons, weighted forecast and margin proxy. */
  getPipelineAnalytics: async (): Promise<PipelineAnalyticsDTO> => {
    const r = await axiosInstance.get<PipelineAnalyticsDTO>('/api/dashboard/pipeline-analytics');
    return r.data;
  },

  /**
   * Brand demand concentration. Returns null — never throws — when the endpoint
   * is not deployed yet (404/501) or the caller is not entitled to it (403), so
   * the page shows an honest "not available yet" state instead of an error.
   */
  getBrandDemand: async (params: BrandDemandParams = {}): Promise<BrandDemandDTO | null> => {
    try {
      const r = await axiosInstance.get<BrandDemandDTO>('/api/analytics/brand-demand', { params });
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
};

export default dashboardService;
