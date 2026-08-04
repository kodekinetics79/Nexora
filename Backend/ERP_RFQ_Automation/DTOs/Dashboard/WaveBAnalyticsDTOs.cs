using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.Dashboard
{
    // ─── WP-B1: manager team-workload view ──────────────────────────────────
    // Wire contracts for GET /api/dashboard/workload. Serialized camelCase by
    // the app-wide System.Text.Json defaults (same as every other controller).

    /// <summary>One row per sales rep, plus one "unassigned" bucket row.</summary>
    public class TeamWorkloadRowDTO
    {
        /// <summary>Null for the unassigned bucket row.</summary>
        public long? UserId { get; set; }

        /// <summary>"First Last" for reps; "Unassigned" for the bucket row.</summary>
        public string Name { get; set; } = string.Empty;

        public string? Email { get; set; }

        /// <summary>Accepted, not-rejected leads currently on this rep's plate.</summary>
        public int OpenLeads { get; set; }

        /// <summary>Open leads whose (real, non-sentinel) BidClosingDate is in the past.</summary>
        public int OverdueLeads { get; set; }

        /// <summary>Quotes in SENT status owned by this rep (owner = Quote.CreatedBy matched by email or "First Last").</summary>
        public int SentQuotes { get; set; }

        /// <summary>SENT quotes with no customer response past the BU's stale threshold (same rule as SlaComputed.IsStale).</summary>
        public int StaleQuotes { get; set; }

        /// <summary>True only for the single unassigned bucket row.</summary>
        public bool IsUnassignedBucket { get; set; }
    }

    public class TeamWorkloadDTO
    {
        public List<TeamWorkloadRowDTO> Rows { get; set; } = new();

        /// <summary>The BU's configured stale threshold used for StaleQuotes (days).</summary>
        public int StaleQuoteDays { get; set; }

        public DateTime GeneratedAt { get; set; }
    }

    // ─── WP-B2: pipeline / margin analytics ─────────────────────────────────
    // Wire contracts for GET /api/dashboard/pipeline-analytics.

    /// <summary>One funnel stage: leads → accepted → quoted → won.</summary>
    public class PipelineStageDTO
    {
        /// <summary>Stable key: "leads" | "accepted" | "quoted" | "won".</summary>
        public string Key { get; set; } = string.Empty;

        /// <summary>Plain-language label, e.g. "Requests received".</summary>
        public string Label { get; set; } = string.Empty;

        public int Count { get; set; }

        /// <summary>
        /// Stage value in the business unit's base currency: lead-line estimates for lead stages,
        /// quote totals for quote stages. NULL means "not answerable", never zero — a stage whose
        /// rows span currencies without approved rates cannot be reduced to one figure, and a 0
        /// would read as a real collapse in the funnel.
        /// </summary>
        public decimal? Value { get; set; }

        /// <summary>ISO code <see cref="Value"/> is expressed in.</summary>
        public string? ValueCurrency { get; set; }

        /// <summary>Why <see cref="Value"/> is null, when applicable.</summary>
        public string? ValueUnavailableReason { get; set; }
    }

    /// <summary>Lost/expired quotes grouped by their recorded outcome reason.</summary>
    public class PipelineLossReasonDTO
    {
        public string Reason { get; set; } = string.Empty;
        public int Count { get; set; }

        /// <summary>Lost value in base currency; NULL when this group spans unconvertible currencies.</summary>
        public decimal? Value { get; set; }

        /// <summary>ISO code <see cref="Value"/> is expressed in.</summary>
        public string? ValueCurrency { get; set; }

        /// <summary>Why <see cref="Value"/> is null, when applicable.</summary>
        public string? ValueUnavailableReason { get; set; }
    }

    public class PipelineAnalyticsDTO
    {
        public List<PipelineStageDTO> Funnel { get; set; } = new();

        public List<PipelineLossReasonDTO> LossReasons { get; set; } = new();

        /// <summary>
        /// SENT-not-responded totals × 0.3 + SENT-responded totals × 0.5, in base currency.
        /// NULL when the open pipeline cannot be fully converted — previously this probability-
        /// weighted two mixed-currency baskets and then ADDED them, which made it the single most
        /// misleading number on the dashboard.
        /// </summary>
        public decimal? WeightedForecast { get; set; }

        /// <summary>ISO code the forecast and the two value figures below are expressed in.</summary>
        public string? ForecastCurrency { get; set; }

        /// <summary>Why <see cref="WeightedForecast"/> is null, when applicable.</summary>
        public string? ForecastUnavailableReason { get; set; }

        /// <summary>Open SENT quotes still waiting for any customer response.</summary>
        public int AwaitingResponseQuotes { get; set; }

        /// <summary>Base-currency value of the awaiting bucket; NULL when unconvertible.</summary>
        public decimal? AwaitingResponseValue { get; set; }

        /// <summary>Open SENT quotes where the customer has responded but no outcome is recorded yet.</summary>
        public int RespondedQuotes { get; set; }

        /// <summary>Base-currency value of the responded bucket; NULL when unconvertible.</summary>
        public decimal? RespondedValue { get; set; }

        /// <summary>
        /// Quoted-vs-floor margin proxy: average (unitPrice − cost) / unitPrice over
        /// quote lines whose matched product has a known cost floor
        /// (FinalLandedCost ?? UnitCost — the same cost basis the pricing engine
        /// uses for its floor). Null when no line has floor data.
        /// </summary>
        public decimal? AvgMarginPct { get; set; }

        /// <summary>Quote lines that had floor data and were included in AvgMarginPct.</summary>
        public int MarginSampleLines { get; set; }

        /// <summary>
        /// Quote lines that had floor data but were EXCLUDED from AvgMarginPct because their
        /// quote currency could not be converted to base currency (or the quote carried no
        /// currency). A non-zero value here means the margin sample is incomplete.
        /// </summary>
        public int MarginLinesExcludedForFx { get; set; }

        /// <summary>All priced quote lines in the BU (denominator for floor coverage).</summary>
        public int TotalQuoteLines { get; set; }

        public DateTime GeneratedAt { get; set; }
    }
}
