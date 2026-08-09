using System.Data;
using System.Security.Claims;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.QuoteDelivery;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Operations;

public static class PlatformDeadLetterQueues
{
    public const string Extraction = "extraction";
    public const string SupplierRfq = "supplier-rfq";
    public const string QuoteDelivery = "quote-delivery";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Extraction, SupplierRfq, QuoteDelivery
    };
}

public sealed record RecoverPlatformDeadLetterCommand(
    string Queue,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] long ItemId,
    string Reason,
    string IdempotencyKey);

public sealed record RecoverPlatformDeadLetterResult(
    string Queue,
    long ItemId,
    long TenantId,
    string Status,
    bool IdempotentReplay);

/// <summary>
/// Fleet recovery command for the three queues exposed by platform operations readiness.
/// Recovery never edits payloads or recipient/provider data: it only releases the existing,
/// tenant-bound durable intent after an MFA Owner supplies a reason and idempotency key.
/// </summary>
public sealed class PlatformDeadLetterRecoveryService(
    ErpRfqAutomationContext db,
    IPlatformAuditService audit,
    ExtractionDeadLetterService extraction)
{
    public const string AuditAction = "platform.dead-letter.recover";

    public async Task<RecoverPlatformDeadLetterResult> RecoverAsync(
        long tenantId,
        RecoverPlatformDeadLetterCommand command,
        ClaimsPrincipal actor,
        HttpContext? httpContext,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var queue = Required(command.Queue, 32, "Queue").ToLowerInvariant();
        if (!PlatformDeadLetterQueues.All.Contains(queue))
            throw new ArgumentException($"Unknown platform dead-letter queue '{queue}'.");
        if (command.ItemId <= 0) throw new ArgumentException("A positive dead-letter item ID is required.");
        var reason = Required(command.Reason, 1000, "Reason");
        var key = Required(command.IdempotencyKey, 128, "Idempotency key");

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == tenantId, ct)
            ?? throw new KeyNotFoundException("Tenant was not found.");
        if (tenant.PrimaryBusinessUnitId is not long businessUnitId)
            throw new InvalidOperationException("Tenant has no provisioned business-unit boundary.");

        if (queue == PlatformDeadLetterQueues.Extraction)
        {
            var actorId = actor.FindFirst("sub")?.Value
                ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? throw new InvalidOperationException("A platform actor ID is required.");
            var result = await extraction.RecoverAsync(businessUnitId, command.ItemId, actorId,
                new RecoverExtractionDeadLetterCommand(reason, key), ct);
            if (!await FindReplayAsync(tenantId, queue, command.ItemId, key, ct))
                await audit.WriteAsync(actor, AuditAction, queue, command.ItemId.ToString(), new
                {
                    queue,
                    itemId = command.ItemId,
                    idempotencyKey = key,
                    reason,
                    previousState = "DeadLetter",
                    newState = result.Status
                }, tenantId, httpContext, ct);
            return new(queue, result.JobId, tenantId, result.Status, result.IdempotentReplay);
        }

        return await db.Database.CreateExecutionStrategy()
            .ExecuteAsync<RecoverPlatformDeadLetterResult>(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var replay = await FindReplayAsync(tenantId, queue, command.ItemId, key, ct);
            if (replay)
            {
                await tx.CommitAsync(ct);
                return new(queue, command.ItemId, tenantId, "RetryQueued", true);
            }

            if (queue == PlatformDeadLetterQueues.SupplierRfq)
            {
                var row = await db.ProcurementOutboxMessages.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == command.ItemId, ct)
                    ?? throw new KeyNotFoundException("Supplier RFQ dead-letter item was not found.");
                if (row.Status != ProcurementOutboxStatuses.DeadLettered || row.DeadLetteredOn is null)
                    throw new InvalidOperationException("Only a dead-lettered supplier RFQ can be recovered.");
                EnsureNotLeased(row.LeaseOwner, row.LeaseToken, row.LeaseUntil);
                row.Status = ProcurementOutboxStatuses.Pending;
                row.AttemptCount = 0;
                row.NextAttemptOn = DateTime.UtcNow;
                row.DeadLetteredOn = null;
                row.LastErrorCode = null;
                row.UpdatedOn = DateTime.UtcNow;
            }
            else
            {
                var row = await db.QuoteDeliveryRequests.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == command.ItemId, ct)
                    ?? throw new KeyNotFoundException("Quote-delivery dead-letter item was not found.");
                if (row.DeadLetteredOn is null || row.CompletedOn is not null)
                    throw new InvalidOperationException("Only a dead-lettered quote delivery can be recovered.");
                EnsureNotLeased(row.LeaseOwner, row.LeaseToken, row.LeaseUntil);
                row.AttemptCount = 0;
                row.AvailableOn = DateTime.UtcNow;
                row.DeadLetteredOn = null;
                row.LastErrorCode = null;
                row.Version++;
            }

            await db.SaveChangesAsync(ct);
            await audit.WriteAsync(actor, AuditAction, queue, command.ItemId.ToString(), new
            {
                queue,
                itemId = command.ItemId,
                idempotencyKey = key,
                reason,
                previousState = "DeadLetter",
                newState = "RetryQueued"
            }, tenantId, httpContext, ct);
            await tx.CommitAsync(ct);
            return new(queue, command.ItemId, tenantId, "RetryQueued", false);
            });
    }

    private async Task<bool> FindReplayAsync(long tenantId, string queue, long itemId, string key, CancellationToken ct)
    {
        // Metadata is JSON, but an exact serialized token is enough for a bounded replay lookup.
        // Materialize only the already-bounded target rows before inspecting JSON. PostgreSQL maps
        // Metadata as jsonb, where translating string.Contains would otherwise attempt a text
        // operator against jsonb. Tenant/action/type/id still constrain this to one queue item.
        var token = $"\"idempotencyKey\":\"{JsonToken(key)}\"";
        var metadata = await db.Set<PlatformAuditLog>().AsNoTracking().Where(x =>
            x.ActAsTenantId == tenantId && x.Action == AuditAction && x.TargetType == queue
            && x.TargetId == itemId.ToString() && x.Metadata != null)
            .Select(x => x.Metadata!).ToListAsync(ct);
        return metadata.Any(value => value.Contains(token, StringComparison.Ordinal));
    }

    private static string JsonToken(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static void EnsureNotLeased(string? owner, Guid? token, DateTime? until)
    {
        if (owner is not null || token is not null || until is not null)
            throw new InvalidOperationException("A leased dead-letter item cannot be recovered.");
    }

    private static string Required(string? value, int maximumLength, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException($"{label} is required.");
        if (normalized.Length > maximumLength) throw new ArgumentException($"{label} exceeds {maximumLength} characters.");
        return normalized;
    }
}
