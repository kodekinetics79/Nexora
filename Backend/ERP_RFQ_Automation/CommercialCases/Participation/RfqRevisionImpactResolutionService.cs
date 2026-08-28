using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Participation;

public interface IRfqRevisionImpactResolutionService
{
    Task<RfqRevisionImpactResolutionResult> ResolveAsync(
        long businessUnitId, long leadId, ResolveRfqRevisionImpactCommand command,
        CancellationToken ct = default);
}

/// <summary>
/// Records a human review of customer amendments that arrived after RFQ promotion.
///
/// <para>The source impact, Lead revision and RFQ lineage are append-only. Resolution is therefore
/// a new audit event correlated to the impact; it never edits or deletes historical commercial
/// evidence and it never creates a replacement RFQ.</para>
/// </summary>
public sealed class RfqRevisionImpactResolutionService : IRfqRevisionImpactResolutionService
{
    public const string ResolutionEventType = "RFQ_REVISION_IMPACT_RESOLVED";
    public const string CorrelationPrefix = "rfq-impact:";

    private readonly ErpRfqAutomationContext _db;

    public RfqRevisionImpactResolutionService(ErpRfqAutomationContext db) => _db = db;

    public Task<RfqRevisionImpactResolutionResult> ResolveAsync(
        long businessUnitId, long leadId, ResolveRfqRevisionImpactCommand command,
        CancellationToken ct = default)
    {
        Validate(businessUnitId, leadId, command);
        if (!_db.Database.IsRelational() || _db.Database.CurrentTransaction is not null)
            return ResolveCoreAsync(businessUnitId, leadId, command, ct);

        var strategy = _db.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(() =>
        {
            _db.ChangeTracker.Clear();
            return ResolveCoreAsync(businessUnitId, leadId, command, ct);
        });
    }

    private async Task<RfqRevisionImpactResolutionResult> ResolveCoreAsync(
        long businessUnitId, long leadId, ResolveRfqRevisionImpactCommand command,
        CancellationToken ct)
    {
        await using var transaction = _db.Database.CurrentTransaction is null
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
            : null;

        if (_db.Database.IsNpgsql())
        {
            var lockKey = $"rfq-impact-resolution:{businessUnitId}:{leadId}:{command.RfqId}";
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        }

        var normalizedReason = command.ReconciliationReason.Trim();
        var normalizedKey = command.IdempotencyKey.Trim();
        var requestHash = Hash(new
        {
            businessUnitId,
            leadId,
            command.RfqId,
            command.ExpectedLeadRevisionId,
            reconciliationReason = normalizedReason,
            command.ConfirmedHistoricalRfqUnchanged
        });

        var replayEvents = await _db.Set<LeadIdentityAuditEvent>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                && x.EventType == ResolutionEventType
                && x.IdempotencyKey.StartsWith(normalizedKey + ":"))
            .OrderBy(x => x.Id)
            .ToListAsync(ct);
        if (replayEvents.Count > 0)
        {
            if (replayEvents.Any(x => PayloadRequestHash(x.PayloadJson) != requestHash))
                throw new InvalidOperationException(
                    "This idempotency key is already bound to a different RFQ amendment review.");

            if (transaction is not null) await transaction.CommitAsync(ct);
            return new(command.RfqId, command.ExpectedLeadRevisionId, replayEvents.Count, true);
        }

        var currentRevisionId = await _db.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Id == leadId)
            .Select(x => x.CurrentRevisionId)
            .SingleOrDefaultAsync(ct)
            ?? throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit or has no current revision.");
        if (currentRevisionId != command.ExpectedLeadRevisionId)
            throw new InvalidOperationException(
                "The Lead changed while the RFQ amendment was being reviewed. Refresh and review the current revision.");

        var rfqExists = await _db.Rfqs.AsNoTracking().AnyAsync(x =>
            x.BusinessUnitId == businessUnitId && x.Id == command.RfqId && x.LeadId == leadId, ct);
        if (!rfqExists)
            throw new KeyNotFoundException(
                $"RFQ {command.RfqId} is not linked to Lead {leadId} in this business unit.");

        var unresolved = await _db.Set<LeadRevisionImpact>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId
                && x.AggregateType == "RFQ" && x.AggregateId == command.RfqId
                && x.ImpactType == "RFQ_REVISION_REQUIRED" && x.Status == "OPEN"
                && x.LeadRevisionId <= command.ExpectedLeadRevisionId)
            .Where(impact => !_db.Set<LeadIdentityAuditEvent>().Any(audit =>
                audit.BusinessUnitId == businessUnitId
                && audit.EventType == ResolutionEventType
                && audit.CorrelationId == CorrelationPrefix + impact.Id))
            .OrderBy(x => x.Id)
            .Select(impact => new
            {
                Impact = impact,
                OccurrenceId = _db.Set<LeadRevision>()
                    .Where(revision => revision.BusinessUnitId == businessUnitId
                        && revision.Id == impact.LeadRevisionId)
                    .Select(revision => revision.EstablishedByOccurrenceId)
                    .Single()
            })
            .ToListAsync(ct);

        if (unresolved.Count == 0)
            throw new InvalidOperationException(
                "There is no unresolved RFQ revision impact for this Lead, RFQ, and current revision.");

        foreach (var row in unresolved)
        {
            _db.Add(new LeadIdentityAuditEvent
            {
                BusinessUnitId = businessUnitId,
                LeadId = leadId,
                OccurrenceId = row.OccurrenceId,
                EventType = ResolutionEventType,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    impactId = row.Impact.Id,
                    rfqId = command.RfqId,
                    reviewedThroughLeadRevisionId = command.ExpectedLeadRevisionId,
                    reconciliationReason = normalizedReason,
                    historicalRfqUnchanged = true,
                    requestHash
                }),
                ActorType = "User",
                ActorId = command.Actor.Trim(),
                CorrelationId = CorrelationPrefix + row.Impact.Id,
                IdempotencyKey = $"{normalizedKey}:{row.Impact.Id}",
                OccurredAtUtc = DateTimeOffset.UtcNow
            });
        }

        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(command.RfqId, command.ExpectedLeadRevisionId, unresolved.Count, false);
    }

    private static void Validate(long businessUnitId, long leadId, ResolveRfqRevisionImpactCommand command)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
        if (leadId <= 0) throw new ArgumentOutOfRangeException(nameof(leadId));
        if (command.RfqId <= 0) throw new ArgumentOutOfRangeException(nameof(command.RfqId));
        if (command.ExpectedLeadRevisionId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command.ExpectedLeadRevisionId));
        if (!command.ConfirmedHistoricalRfqUnchanged)
            throw new ArgumentException(
                "Confirm that the historical RFQ and its original promotion lineage will remain unchanged.");
        if (string.IsNullOrWhiteSpace(command.ReconciliationReason)
            || command.ReconciliationReason.Trim().Length < 15)
            throw new ArgumentException("A reconciliation reason of at least 15 characters is required.");
        if (command.ReconciliationReason.Trim().Length > 2000)
            throw new ArgumentException("The reconciliation reason cannot exceed 2000 characters.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            throw new ArgumentException("An idempotency key is required.");
        if (command.IdempotencyKey.Trim().Length > 220)
            throw new ArgumentException("The idempotency key cannot exceed 220 characters.");
        if (string.IsNullOrWhiteSpace(command.Actor))
            throw new ArgumentException("An authenticated actor is required.");
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static string? PayloadRequestHash(string payload)
    {
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("requestHash", out var value) ? value.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public sealed record ResolveRfqRevisionImpactCommand(long RfqId, long ExpectedLeadRevisionId,
    string ReconciliationReason, bool ConfirmedHistoricalRfqUnchanged,
    string IdempotencyKey, string Actor);

public sealed record RfqRevisionImpactResolutionResult(long RfqId, long ReviewedThroughLeadRevisionId,
    int ResolvedImpactCount, bool Replayed);
