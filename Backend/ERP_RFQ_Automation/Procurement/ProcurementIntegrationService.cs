using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Procurement;

public interface IProcurementIntegrationService
{
    Task<ProcurementIntegrationStatusView> GetStatusAsync(long businessUnitId,
        CancellationToken cancellationToken = default);
    Task<ProcurementCallbackResult> ApplyCallbackAsync(long businessUnitId, string timestamp,
        string signature, string correlationId, string rawBody, ProcurementStatusCallbackCommand command,
        CancellationToken cancellationToken = default);
}

public sealed class ProcurementIntegrationAuthenticationException(string message) : Exception(message);

public sealed class ProcurementIntegrationService(
    ErpRfqAutomationContext db,
    IConfiguration configuration) : IProcurementIntegrationService
{
    private static readonly HashSet<string> SupportedStatuses =
    [
        ProcurementHandoffStatuses.ExternalPoCreated,
        ProcurementHandoffStatuses.SupplierConfirmed,
        ProcurementHandoffStatuses.Dispatched,
        ProcurementHandoffStatuses.Delivered,
        ProcurementHandoffStatuses.PartiallyReceived,
        ProcurementHandoffStatuses.Received,
        ProcurementHandoffStatuses.Cancelled
    ];

    private static readonly IReadOnlyDictionary<string, HashSet<string>> AllowedTransitions =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [ProcurementHandoffStatuses.Created] =
                [ProcurementHandoffStatuses.ExternalPoCreated, ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.ExternalPoCreated] =
                [ProcurementHandoffStatuses.ExternalPoCreated, ProcurementHandoffStatuses.SupplierConfirmed,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.SupplierConfirmed] =
                [ProcurementHandoffStatuses.SupplierConfirmed, ProcurementHandoffStatuses.Dispatched,
                    ProcurementHandoffStatuses.PartiallyReceived, ProcurementHandoffStatuses.Received,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.Dispatched] =
                [ProcurementHandoffStatuses.Dispatched, ProcurementHandoffStatuses.Delivered,
                    ProcurementHandoffStatuses.PartiallyReceived, ProcurementHandoffStatuses.Received,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.Delivered] =
                [ProcurementHandoffStatuses.Delivered, ProcurementHandoffStatuses.PartiallyReceived,
                    ProcurementHandoffStatuses.Received, ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.PartiallyReceived] =
                [ProcurementHandoffStatuses.PartiallyReceived, ProcurementHandoffStatuses.Received,
                    ProcurementHandoffStatuses.Cancelled],
            [ProcurementHandoffStatuses.Received] = [ProcurementHandoffStatuses.Received],
            [ProcurementHandoffStatuses.Cancelled] = [ProcurementHandoffStatuses.Cancelled]
        };

    public async Task<ProcurementIntegrationStatusView> GetStatusAsync(long businessUnitId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var connector = ResolveConnector(businessUnitId);
        var now = DateTime.UtcNow;
        var lastSync = await db.ProcurementCallbackReceipts.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId
                && x.Status == ProcurementCallbackReceiptStatuses.Applied)
            .MaxAsync(x => (DateTime?)x.AppliedOn, cancellationToken);
        var awaiting = await db.ProcurementHandoffs.CountAsync(x => x.BusinessUnitId == businessUnitId
            && !x.IsAuthoritative, cancellationToken);
        var staleThreshold = now.AddHours(-24);
        var stale = await db.ProcurementHandoffs.CountAsync(x => x.BusinessUnitId == businessUnitId
            && (!x.IsAuthoritative && x.CreatedOn < staleThreshold
                || x.LastSynchronizedOn < staleThreshold), cancellationToken);
        var handoffDifferences = await db.ProcurementHandoffs.CountAsync(x => x.BusinessUnitId == businessUnitId
            && (x.ExternalOrderedQuantity != null && x.ExternalOrderedQuantity != x.RequiredQuantity
                || x.ExternalApprovedUnitCost != null && x.ExternalApprovedUnitCost != x.SelectedUnitCost),
            cancellationToken);
        var rejectedDifferences = await db.ProcurementCallbackReceipts.CountAsync(x =>
            x.BusinessUnitId == businessUnitId
            && x.RejectionCode == "COMMERCIAL_RECONCILIATION_REQUIRED"
            && !db.ProcurementCallbackReceipts.Any(applied =>
                applied.BusinessUnitId == businessUnitId
                && applied.ProcurementHandoffId == x.ProcurementHandoffId
                && applied.Status == ProcurementCallbackReceiptStatuses.Applied
                && applied.ReceivedOn > x.ReceivedOn), cancellationToken);
        var dispatch = await db.ProcurementOutboxMessages.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Pending = group.Count(x => x.Status == ProcurementOutboxStatuses.Pending && x.AttemptCount == 0),
                Retrying = group.Count(x => x.Status == ProcurementOutboxStatuses.Pending && x.AttemptCount > 0),
                Failed = group.Count(x => x.Status == ProcurementOutboxStatuses.Failed),
                Dead = group.Count(x => x.Status == ProcurementOutboxStatuses.DeadLettered)
            }).SingleOrDefaultAsync(cancellationToken);

        var connectorStatus = !connector.IsConfigured ? "NOT_INTEGRATED"
            : (dispatch?.Failed ?? 0) > 0 || (dispatch?.Dead ?? 0) > 0
                || handoffDifferences > 0 || rejectedDifferences > 0 ? "DEGRADED"
            : stale > 0 ? "STALE"
            : lastSync.HasValue ? "SYNCHRONIZED" : "AWAITING_SYNCHRONIZATION";
        return new ProcurementIntegrationStatusView(connector.IsConfigured, connector.SourceSystem,
            connectorStatus, lastSync, awaiting, dispatch?.Pending ?? 0, dispatch?.Retrying ?? 0,
            dispatch?.Failed ?? 0, dispatch?.Dead ?? 0, stale, handoffDifferences + rejectedDifferences, now);
    }

    public async Task<ProcurementCallbackResult> ApplyCallbackAsync(long businessUnitId, string timestamp,
        string signature, string correlationId, string rawBody, ProcurementStatusCallbackCommand command,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var connector = ResolveConnector(businessUnitId);
        ValidateEnvelope(connector, timestamp, signature, rawBody);
        if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Trim().Length > 160)
            throw new ArgumentException("A valid callback correlation ID is required.");
        if (command.HandoffId <= 0 || string.IsNullOrWhiteSpace(command.ExternalEventId)
            || command.ExternalEventId.Trim().Length > 160)
            throw new ArgumentException("Handoff and external event identity are required.");

        var payloadHash = Hash(rawBody);
        var externalEventId = command.ExternalEventId.Trim();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            return await strategy.ExecuteAsync(async () =>
            {
                // An execution-strategy retry reuses this scoped context. Remove entities left Added/Modified
                // by a rolled-back attempt before rebuilding the atomic callback transition.
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable,
                    cancellationToken);
                var replay = await FindReceiptAsync(businessUnitId, connector.Identity, externalEventId,
                    cancellationToken);
                if (replay is not null)
                {
                    EnsureReplayMatches(replay, command.HandoffId, payloadHash);
                    await transaction.CommitAsync(cancellationToken);
                    return ToReplayResult(replay);
                }

                var handoff = await db.ProcurementHandoffs.SingleOrDefaultAsync(x =>
                    x.BusinessUnitId == businessUnitId && x.Id == command.HandoffId, cancellationToken)
                    ?? throw new KeyNotFoundException("Procurement handoff was not found in this tenant.");
                var now = DateTime.UtcNow;
                var receipt = new ProcurementCallbackReceipt
                {
                    BusinessUnitId = businessUnitId,
                    ProcurementHandoffId = handoff.Id,
                    SourceSystem = connector.Identity,
                    ExternalEventId = externalEventId,
                    PayloadHash = payloadHash,
                    CorrelationId = correlationId.Trim(),
                    Status = ProcurementCallbackReceiptStatuses.Rejected,
                    ObservedQuantity = command.OrderedQuantity,
                    ObservedUnitCost = command.ApprovedUnitCost,
                    ObservedStatus = command.Status?.Trim().ToUpperInvariant() ?? string.Empty,
                    ObservedOn = command.ObservedOn,
                    ReceivedOn = now
                };
                db.ProcurementCallbackReceipts.Add(receipt);

                var rejectionCode = ValidateBusinessCallback(handoff, command);
                if (rejectionCode is not null)
                {
                    receipt.RejectionCode = rejectionCode;
                    await db.SaveChangesAsync(cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                    return new ProcurementCallbackResult(handoff.Id, externalEventId, receipt.Status,
                        false, false, rejectionCode);
                }

                var status = command.Status.Trim().ToUpperInvariant();
                handoff.ExternalSupplierPoNumber = command.ExternalSupplierPoNumber.Trim();
                handoff.ExternalSupplierPoLineNumber = command.ExternalSupplierPoLineNumber.Trim();
                handoff.ExternalSalesOrderNumber = command.ExternalSalesOrderNumber?.Trim();
                handoff.ExternalOrderedQuantity = command.OrderedQuantity;
                handoff.ExternalApprovedUnitCost = command.ApprovedUnitCost;
                handoff.ExternalExpectedOn = command.ExpectedOn;
                handoff.ExternalStatus = status;
                handoff.Status = status;
                handoff.SupplierConfirmedOn ??= status is ProcurementHandoffStatuses.SupplierConfirmed
                    or ProcurementHandoffStatuses.Dispatched or ProcurementHandoffStatuses.Delivered
                    or ProcurementHandoffStatuses.PartiallyReceived or ProcurementHandoffStatuses.Received
                    ? command.ObservedOn : null;
                handoff.DispatchedOn ??= status is ProcurementHandoffStatuses.Dispatched
                    or ProcurementHandoffStatuses.Delivered ? command.ObservedOn : null;
                handoff.DeliveredOn ??= status == ProcurementHandoffStatuses.Delivered ? command.ObservedOn : null;
                handoff.LastExternalEventId = externalEventId;
                handoff.LastCorrelationId = correlationId.Trim();
                handoff.LastSynchronizedOn = command.ObservedOn;
                handoff.SourceOfTruth = connector.SourceSystem;
                handoff.IsAuthoritative = true;
                handoff.Version++;
                handoff.ModifiedOn = now;
                handoff.ModifiedBy = $"integration:{connector.SourceSystem}";
                receipt.Status = ProcurementCallbackReceiptStatuses.Applied;
                receipt.AppliedOn = now;
                db.ProcurementEvents.Add(new ProcurementEvent
                {
                    BusinessUnitId = businessUnitId,
                    AggregateType = "ProcurementHandoff",
                    AggregateId = handoff.Id,
                    AggregateVersion = handoff.Version,
                    EventType = "PROCUREMENT_HANDOFF_PROVIDER_STATUS_APPLIED",
                    Actor = $"integration:{connector.SourceSystem}",
                    CorrelationId = correlationId.Trim(),
                    IdempotencyKey = externalEventId,
                    PayloadJson = JsonSerializer.Serialize(new
                    {
                        sourceSystem = connector.SourceSystem,
                        externalEventId,
                        payloadHash,
                        handoff.ExternalStatus,
                        handoff.ExternalSupplierPoNumber,
                        handoff.ExternalSupplierPoLineNumber,
                        handoff.IsAuthoritative
                    }),
                    OccurredOn = now
                });
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ProcurementCallbackResult(handoff.Id, externalEventId, receipt.Status,
                    true, false, null);
            });
        }
        catch (DbUpdateException exception) when (IsReceiptIdentityConflict(exception))
        {
            db.ChangeTracker.Clear();
            var replay = await FindReceiptAsync(businessUnitId, connector.Identity, externalEventId,
                cancellationToken);
            if (replay is null) throw;
            EnsureReplayMatches(replay, command.HandoffId, payloadHash);
            return ToReplayResult(replay);
        }
    }

    private Task<ProcurementCallbackReceipt?> FindReceiptAsync(long businessUnitId, string sourceSystem,
        string externalEventId, CancellationToken cancellationToken) =>
        db.ProcurementCallbackReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.SourceSystem == sourceSystem
            && x.ExternalEventId == externalEventId, cancellationToken);

    private static void EnsureReplayMatches(ProcurementCallbackReceipt receipt, long handoffId,
        string payloadHash)
    {
        if (receipt.ProcurementHandoffId != handoffId || receipt.PayloadHash != payloadHash)
            throw new InvalidOperationException(
                "The external event ID was already used with a different callback payload.");
    }

    private static ProcurementCallbackResult ToReplayResult(ProcurementCallbackReceipt receipt) =>
        new(receipt.ProcurementHandoffId, receipt.ExternalEventId, receipt.Status,
            receipt.Status == ProcurementCallbackReceiptStatuses.Applied, true, receipt.RejectionCode);

    private static bool IsReceiptIdentityConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            TableName: "procurement_callback_receipts"
        } postgres && postgres.ConstraintName?.StartsWith(
            "IX_procurement_callback_receipts_BusinessUnitId_SourceSystem_E",
            StringComparison.Ordinal) == true;

    private string? ValidateBusinessCallback(ProcurementHandoff handoff,
        ProcurementStatusCallbackCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ExternalSupplierPoNumber)
            || command.ExternalSupplierPoNumber.Trim().Length > 160
            || string.IsNullOrWhiteSpace(command.ExternalSupplierPoLineNumber)
            || command.ExternalSupplierPoLineNumber.Trim().Length > 80
            || command.ExternalSalesOrderNumber?.Trim().Length > 160)
            return "INVALID_EXTERNAL_REFERENCE";
        if (command.OrderedQuantity <= 0 || command.ApprovedUnitCost < 0)
            return "INVALID_COMMERCIAL_VALUE";
        if (command.OrderedQuantity != handoff.RequiredQuantity
            || command.ApprovedUnitCost != handoff.SelectedUnitCost)
            return "COMMERCIAL_RECONCILIATION_REQUIRED";
        if (command.ObservedOn.Kind != DateTimeKind.Utc
            || command.ObservedOn > DateTime.UtcNow.AddMinutes(5)
            || command.ObservedOn < handoff.CreatedOn.AddMinutes(-5))
            return "INVALID_OBSERVED_TIME";
        if (handoff.LastSynchronizedOn.HasValue && command.ObservedOn < handoff.LastSynchronizedOn.Value)
            return "STALE_PROVIDER_EVENT";
        var status = command.Status?.Trim().ToUpperInvariant();
        if (status is null || !SupportedStatuses.Contains(status))
            return "UNSUPPORTED_STATUS";
        var observed = handoff.ExternalStatus ?? handoff.Status;
        if (!AllowedTransitions.TryGetValue(observed, out var transitions) || !transitions.Contains(status))
            return "INVALID_STATUS_TRANSITION";
        return null;
    }

    private void ValidateEnvelope(ConnectorConfiguration connector, string timestamp,
        string signature, string rawBody)
    {
        if (!connector.IsConfigured)
            throw new ProcurementIntegrationAuthenticationException(
                "Procurement integration is not configured for this tenant.");
        if (!long.TryParse(timestamp, out var unixSeconds)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > 300)
            throw new ProcurementIntegrationAuthenticationException("Callback timestamp is invalid or expired.");
        if (string.IsNullOrWhiteSpace(signature) || signature.Length != 64)
            throw new ProcurementIntegrationAuthenticationException("Callback signature is invalid.");
        byte[] supplied;
        try { supplied = Convert.FromHexString(signature); }
        catch (FormatException)
        {
            throw new ProcurementIntegrationAuthenticationException("Callback signature is invalid.");
        }
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(connector.SharedSecret!),
            Encoding.UTF8.GetBytes($"{timestamp}\n{rawBody}"));
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
            throw new ProcurementIntegrationAuthenticationException("Callback signature is invalid.");
    }

    private ConnectorConfiguration ResolveConnector(long businessUnitId)
    {
        // The predicate itself lives in ProcurementIntegrationConfiguration because the activation
        // gate now asks the same question — "does this tenant have an ERP connector at all?" — and
        // two copies of "configured" that could drift by one length check is how a tenant ends up
        // activated as having no integration while the callback endpoint happily authenticates one.
        var configured = ProcurementIntegrationConfiguration.TryResolve(
            configuration, businessUnitId, out var source, out var secret);
        return new ConnectorConfiguration(configured, configured ? $"procurement:{businessUnitId}" : "not-integrated",
            configured ? source! : "Not integrated", configured ? secret : null);
    }

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || db.ScopedTenantId != businessUnitId)
            throw new ArgumentException("The authenticated tenant context is required.");
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record ConnectorConfiguration(
        bool IsConfigured,
        string Identity,
        string SourceSystem,
        string? SharedSecret);
}
