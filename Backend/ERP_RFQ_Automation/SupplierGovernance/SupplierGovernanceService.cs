using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.SupplierGovernance;

public sealed record GovernSupplierCommand(
    long BusinessUnitId,
    long SupplierId,
    string GovernanceStatus,
    string VerificationStatus,
    string ComplianceStatus,
    string RiskStatus,
    string ReadinessStatus,
    Guid ExpectedConcurrencyToken,
    string Reason,
    string Actor,
    string IdempotencyKey,
    string CorrelationId);

public sealed record GovernedSupplierResult(
    long SupplierId,
    string GovernanceStatus,
    string VerificationStatus,
    string ComplianceStatus,
    string RiskStatus,
    string ReadinessStatus,
    Guid ConcurrencyToken,
    bool Replayed);

public sealed class SupplierGovernanceService(ErpRfqAutomationContext db)
{
    public async Task<GovernedSupplierResult> GovernAsync(
        GovernSupplierCommand command,
        CancellationToken cancellationToken = default)
    {
        Validate(command);
        var requestHash = Hash(new
        {
            command.SupplierId,
            command.GovernanceStatus,
            command.VerificationStatus,
            command.ComplianceStatus,
            command.RiskStatus,
            command.ReadinessStatus,
            command.ExpectedConcurrencyToken,
            Reason = command.Reason.Trim()
        });

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            var replay = await db.ProcurementEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == command.BusinessUnitId
                && x.EventType == "SUPPLIER_GOVERNANCE_DECIDED"
                && x.IdempotencyKey == command.IdempotencyKey.Trim(), cancellationToken);
            if (replay is not null)
            {
                using var payload = JsonDocument.Parse(replay.PayloadJson);
                if (payload.RootElement.GetProperty("requestHash").GetString() != requestHash
                    || replay.AggregateId != command.SupplierId)
                    throw new SupplierGovernanceConflictException(
                        "The idempotency key was already used for a different Supplier decision.");
                var current = await RequireSupplierAsync(command.BusinessUnitId, command.SupplierId,
                    cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return ToResult(current, true);
            }

            var supplier = await RequireSupplierAsync(command.BusinessUnitId, command.SupplierId,
                cancellationToken);
            if (supplier.ConcurrencyToken != command.ExpectedConcurrencyToken)
                throw new SupplierGovernanceConflictException(
                    "The Supplier changed after it was loaded. Refresh and review the latest evidence.");

            supplier.GovernanceStatus = command.GovernanceStatus;
            supplier.VerificationStatus = command.VerificationStatus;
            supplier.ComplianceStatus = command.ComplianceStatus;
            supplier.RiskStatus = command.RiskStatus;
            supplier.ReadinessStatus = command.ReadinessStatus;
            supplier.GovernanceReviewedBy = command.Actor.Trim();
            supplier.GovernanceReviewedOn = DateTime.UtcNow;
            supplier.ModifiedBy = command.Actor.Trim();
            supplier.ModifiedOn = supplier.GovernanceReviewedOn;
            supplier.ConcurrencyToken = Guid.NewGuid();
            if (command.GovernanceStatus == SupplierGovernanceStatuses.Inactive)
                supplier.IsActive = false;

            db.ProcurementEvents.Add(new ProcurementEvent
            {
                BusinessUnitId = command.BusinessUnitId,
                AggregateType = "Supplier",
                AggregateId = supplier.Id,
                AggregateVersion = 1,
                EventType = "SUPPLIER_GOVERNANCE_DECIDED",
                Actor = command.Actor.Trim(),
                CorrelationId = command.CorrelationId.Trim(),
                IdempotencyKey = command.IdempotencyKey.Trim(),
                PayloadJson = JsonSerializer.Serialize(new
                {
                    requestHash,
                    supplier.GovernanceStatus,
                    supplier.VerificationStatus,
                    supplier.ComplianceStatus,
                    supplier.RiskStatus,
                    supplier.ReadinessStatus,
                    Reason = command.Reason.Trim()
                }),
                OccurredOn = supplier.GovernanceReviewedOn.Value
            });
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ToResult(supplier, false);
        });
    }

    private async Task<Supplier> RequireSupplierAsync(long businessUnitId, long supplierId,
        CancellationToken cancellationToken) =>
        await db.Suppliers.SingleOrDefaultAsync(x => x.Buid == businessUnitId && x.Id == supplierId,
            cancellationToken)
        ?? throw new SupplierGovernanceNotFoundException("The Supplier was not found in this tenant.");

    private static void Validate(GovernSupplierCommand command)
    {
        if (command.BusinessUnitId <= 0 || command.SupplierId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Tenant and Supplier identities must be positive.");
        Required(command.Actor, 255, nameof(command.Actor));
        Required(command.Reason, 1_000, nameof(command.Reason));
        Required(command.IdempotencyKey, 160, nameof(command.IdempotencyKey));
        Required(command.CorrelationId, 160, nameof(command.CorrelationId));
        OneOf(command.GovernanceStatus, nameof(command.GovernanceStatus),
            SupplierGovernanceStatuses.Discovered, SupplierGovernanceStatuses.Unverified,
            SupplierGovernanceStatuses.ReviewRequired, SupplierGovernanceStatuses.Provisional,
            SupplierGovernanceStatuses.Approved, SupplierGovernanceStatuses.Preferred,
            SupplierGovernanceStatuses.Restricted, SupplierGovernanceStatuses.Blocked,
            SupplierGovernanceStatuses.Inactive);
        OneOf(command.VerificationStatus, nameof(command.VerificationStatus),
            SupplierGovernanceUnknown.Unknown, SupplierVerificationStatuses.Pending,
            SupplierVerificationStatuses.Verified, SupplierVerificationStatuses.Failed,
            SupplierVerificationStatuses.Expired);
        OneOf(command.ComplianceStatus, nameof(command.ComplianceStatus),
            SupplierGovernanceUnknown.Unknown, SupplierComplianceStatuses.Pending,
            SupplierComplianceStatuses.Cleared, SupplierComplianceStatuses.Restricted,
            SupplierComplianceStatuses.Blocked, SupplierComplianceStatuses.Failed);
        OneOf(command.RiskStatus, nameof(command.RiskStatus), SupplierGovernanceUnknown.Unknown,
            SupplierRiskStatuses.Low, SupplierRiskStatuses.Medium, SupplierRiskStatuses.High,
            SupplierRiskStatuses.Blocked);
        OneOf(command.ReadinessStatus, nameof(command.ReadinessStatus),
            SupplierReadinessStatuses.ReviewRequired, SupplierReadinessStatuses.Ready,
            SupplierReadinessStatuses.Restricted, SupplierReadinessStatuses.Blocked);

        if (command.GovernanceStatus is SupplierGovernanceStatuses.Approved
            or SupplierGovernanceStatuses.Preferred)
        {
            if (command.VerificationStatus != SupplierVerificationStatuses.Verified
                || command.ComplianceStatus != SupplierComplianceStatuses.Cleared
                || command.RiskStatus is not (SupplierRiskStatuses.Low or SupplierRiskStatuses.Medium)
                || command.ReadinessStatus != SupplierReadinessStatuses.Ready)
                throw new ArgumentException(
                    "Approved and preferred Suppliers require verified identity, cleared compliance, acceptable risk, and READY outreach status.");
        }
        if (command.GovernanceStatus == SupplierGovernanceStatuses.Provisional
            && (command.ComplianceStatus is SupplierComplianceStatuses.Blocked
                or SupplierComplianceStatuses.Failed or SupplierComplianceStatuses.Restricted
                || command.RiskStatus is SupplierRiskStatuses.High or SupplierRiskStatuses.Blocked))
            throw new ArgumentException("A provisional Supplier cannot override compliance or high-risk blocks.");
        if (command.GovernanceStatus == SupplierGovernanceStatuses.Blocked
            && command.ReadinessStatus != SupplierReadinessStatuses.Blocked)
            throw new ArgumentException("A blocked Supplier must have BLOCKED readiness.");
        if (command.GovernanceStatus == SupplierGovernanceStatuses.Restricted
            && command.ReadinessStatus != SupplierReadinessStatuses.Restricted)
            throw new ArgumentException("A restricted Supplier must have RESTRICTED readiness.");
    }

    private static GovernedSupplierResult ToResult(Supplier supplier, bool replayed) => new(
        supplier.Id, supplier.GovernanceStatus, supplier.VerificationStatus, supplier.ComplianceStatus,
        supplier.RiskStatus, supplier.ReadinessStatus,
        supplier.ConcurrencyToken ?? throw new InvalidOperationException("Supplier concurrency token is missing."),
        replayed);

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value)))).ToLowerInvariant();

    private static void Required(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new ArgumentException($"{name} is required and must not exceed {maximum} characters.", name);
    }

    private static void OneOf(string value, string name, params string[] allowed)
    {
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new ArgumentException($"{name} has an unsupported value.", name);
    }
}

public sealed class SupplierGovernanceConflictException(string message) : InvalidOperationException(message);
public sealed class SupplierGovernanceNotFoundException(string message) : InvalidOperationException(message);
