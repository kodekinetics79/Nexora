using System;

namespace ERP_RFQ_Automation.Metrics;

/// <summary>
/// One append-only passive AI-metric event (WP-B4). Rows are written by
/// <see cref="IMetricRecorder"/> from small hooks inside existing flows
/// (apply-pricing, extraction review, quote outcomes) and are never updated or
/// deleted — the table is a raw event log an analyst/report can aggregate later.
/// Tenant-scoped via the same fail-closed global filter pattern as the
/// Sla/Agent entities (configured in Models/ErpRfqAutomationContext.Metrics.cs).
/// </summary>
public sealed class MetricEvent
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>
    /// Stable event discriminator, e.g. "pricing_applied", "extraction_corrected",
    /// "outcome_recorded". Plain string on purpose — new event types must never
    /// require a schema change.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The primary entity the event is about (rfqId / leadId / quoteId), when there is one.</summary>
    public long? EntityId { get; set; }

    /// <summary>Free-form event payload (jsonb). Shape is documented per Type in Sla/WAVEB-WIRING.md.</summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime CreatedOn { get; set; }
}
