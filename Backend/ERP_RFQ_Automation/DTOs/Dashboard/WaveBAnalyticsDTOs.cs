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

        /// <summary>
        /// Ingestion-audit fairness: open leads EXCLUDED from the OverdueLeads
        /// aging metric because they were ingested into Nexora after their
        /// business due date (they were already past deadline on arrival, so
        /// counting them would book pre-Nexora losses against Nexora).
        /// Surfaced so the exclusion is visible, never silent.
        /// </summary>
        public int LateIngestedExcludedLeads { get; set; }
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
        /// The window the funnel counts cover. This funnel has never been period-filtered, and a
        /// reader looking at an all-time funnel beside a 90-day margin needs to be told which is
        /// which — so the scope is stated on the contract rather than assumed.
        /// </summary>
        public string FunnelScope { get; set; } = AllTimeScope;

        /// <summary>Every record in the business unit, with no date filter.</summary>
        public const string AllTimeScope = "all_time";

        // AvgMarginPct / MarginSampleLines / MarginLinesExcludedForFx / TotalQuoteLines were
        // REMOVED here, not deprecated. The figure was an unweighted mean of per-line percentages
        // taken against Product.FinalLandedCost — a column that is not a landed cost — over every
        // quote line ever written including drafts and lost bids. Leaving it in place under a
        // warning comment would have kept a wrong number on a live API contract. The replacement is
        // GET /api/dashboard/gross-margin (Reporting/GrossMarginService), which is value-weighted,
        // reads the quote-time sourcing decision, filters to accepted quotes in a stated window,
        // and returns "unavailable" instead of a number when it cannot be computed.

        public DateTime GeneratedAt { get; set; }
    }
}
