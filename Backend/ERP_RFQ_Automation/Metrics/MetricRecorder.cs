using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Metrics;

/// <summary>
/// Append-only writer for <see cref="MetricEvent"/> rows (WP-B4).
///
/// Contract: NEVER throws and never disturbs the caller's unit of work — a
/// metrics failure must be invisible to the business flow it instruments. Each
/// write therefore runs on its own short-lived DI scope / DbContext (so a hook
/// firing mid-transaction cannot flush half-finished caller state), and every
/// exception (including cancellation) is logged and swallowed.
///
/// Stable event types are collected in <see cref="MetricEventTypes"/>.
/// </summary>
public interface IMetricRecorder
{
    /// <summary>Appends one metric event. Never throws; failures are logged and dropped.</summary>
    Task RecordAsync(long businessUnitId, string type, long? entityId, object payload, CancellationToken ct = default);
}

/// <summary>Stable <see cref="MetricEvent.Type"/> values (WP-B4 wire/reporting contract).</summary>
public static class MetricEventTypes
{
    /// <summary>Pricing applied to an RFQ: per-line recommended vs applied + totals.</summary>
    public const string PricingApplied = "pricing_applied";

    /// <summary>A human corrected an extracted lead during review: which fields changed.</summary>
    public const string ExtractionCorrected = "extraction_corrected";

    /// <summary>A quote reached a terminal outcome (won/lost/expired): reason + cycle times.</summary>
    public const string OutcomeRecorded = "outcome_recorded";
}

public sealed class MetricRecorder : IMetricRecorder
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MetricRecorder> _log;

    public MetricRecorder(IServiceScopeFactory scopeFactory, ILogger<MetricRecorder> log)
    {
        _scopeFactory = scopeFactory;
        _log = log;
    }

    public async Task RecordAsync(long businessUnitId, string type, long? entityId, object payload, CancellationToken ct = default)
    {
        try
        {
            if (businessUnitId <= 0 || string.IsNullOrWhiteSpace(type)) return;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

            db.Set<MetricEvent>().Add(new MetricEvent
            {
                BusinessUnitId = businessUnitId,
                Type = type,
                EntityId = entityId,
                PayloadJson = payload is null ? "{}" : JsonSerializer.Serialize(payload, JsonOpts),
                CreatedOn = DateTime.UtcNow
            });

            // CancellationToken.None on purpose: an aborted request should not lose
            // the (already-happened) business fact this row describes.
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Passive by contract — any failure (serialization, DB, cancellation) is
            // logged and dropped; the business flow must never notice.
            _log.LogWarning(ex, "MetricRecorder: failed to record '{Type}' for BU {Bu}; event dropped.", type, businessUnitId);
        }
    }
}
