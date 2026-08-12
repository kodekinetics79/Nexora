using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.SupplierEvaluation;

/// <summary>
/// A new supplier comparison weight set, sent whole.
///
/// <para>All four weights are required together. Patch semantics would be wrong here and not merely
/// inconvenient: the four are one decision constrained to total 100, so "raise price to 80" is
/// meaningless until the caller says what it comes out of. Nullable on the wire only so that an
/// omitted field is refused loudly instead of arriving as a silent 0.</para>
/// </summary>
/// <param name="Reason">
/// Mandatory. This row re-ranks every supplier comparison computed after it; a change with no
/// reason, no author and no date is not defensible to an auditor.
/// </param>
public sealed record UpdateSupplierComparisonWeightsCommand(
    int? PriceWeight,
    int? LeadTimeWeight,
    int? WarrantyWeight,
    int? PaymentTermsWeight,
    string Reason);

/// <summary>
/// The tenant's supplier comparison weights as the settings screen shows them.
///
/// <para><c>IsDefault</c> is true while the tenant has never saved and is running on the entity's
/// defaults — the screen says so rather than presenting inherited values as chosen ones.
/// <c>RequiredTotal</c> ships the invariant so no client hardcodes it.</para>
/// </summary>
public sealed record SupplierComparisonWeightsView(
    long BusinessUnitId,
    int PriceWeight,
    int LeadTimeWeight,
    int WarrantyWeight,
    int PaymentTermsWeight,
    long Version,
    DateTime? ModifiedOn,
    string? ModifiedBy,
    bool IsDefault,
    int RequiredTotal);

public sealed class SupplierComparisonWeightsValidationException(string message)
    : InvalidOperationException(message);

/// <summary>
/// The one way to READ or CHANGE a tenant's <see cref="SupplierComparisonWeights"/>.
///
/// <para>Modelled one-for-one on <c>CommercialMatchingPolicyService</c>, which is the repo's only
/// end-to-end example of "a setting is real only if it has a UI and an audit trail": create on
/// demand, mandatory reason, Idempotency-Key replay checked against the tenant governance ledger, a
/// before/after snapshot, and <c>Version++</c> inside a Serializable transaction.</para>
///
/// <para><b>Why a reason is mandatory.</b> These four numbers decide which supplier a sales engineer
/// is shown as the best offer on every RFQ line after the change. Two comparisons of the same quotes
/// can name different winners either side of an edit, and the only thing that explains that to an
/// auditor — or to the engineer who awarded last week — is the record of who changed what, when and
/// why.</para>
/// </summary>
public sealed class SupplierComparisonWeightsService(ErpRfqAutomationContext db)
{
    private const string Area = "SupplierComparison";
    public const string ActionWeightsUpdated = "SUPPLIER_COMPARISON_WEIGHTS_UPDATED";

    public async Task<SupplierComparisonWeightsView> GetAsync(long tenantId, CancellationToken ct = default)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var stored = await db.SupplierComparisonWeightSets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);
        return Map(stored ?? SupplierComparisonWeights.DefaultFor(tenantId), stored is null);
    }

    /// <summary>
    /// The weight set the scorer should use for this tenant, already validated. A tenant with no row
    /// gets the defaults rather than an exception, because a comparison must still be computable
    /// before anyone has visited the settings screen.
    /// </summary>
    public async Task<SupplierScoringWeights> ResolveAsync(long tenantId, CancellationToken ct = default)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var stored = await db.SupplierComparisonWeightSets.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);
        return stored is null ? SupplierScoringWeights.Defaults : SupplierScoringWeights.From(stored);
    }

    public async Task<SupplierComparisonWeightsView> UpdateAsync(long tenantId, long actorUserId,
        string actor, string idempotencyKey, UpdateSupplierComparisonWeightsCommand command,
        CancellationToken ct = default)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        actor = PlatformGovernanceService.Required(actor, 255, "An authenticated actor is required.");
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        var reason = PlatformGovernanceService.Required(command.Reason, 1000,
            "A reason is required: these weights decide which supplier is recommended on every RFQ line "
            + "compared after the change.");

        var price = Weight(command.PriceWeight, "The price weight");
        var leadTime = Weight(command.LeadTimeWeight, "The lead time weight");
        var warranty = Weight(command.WarrantyWeight, "The warranty weight");
        var paymentTerms = Weight(command.PaymentTermsWeight, "The payment terms weight");

        var total = price + leadTime + warranty + paymentTerms;
        if (total != SupplierComparisonWeights.RequiredTotal)
            throw new SupplierComparisonWeightsValidationException(
                $"The four weights must total {SupplierComparisonWeights.RequiredTotal}; these total {total}. "
                + "The score is presented to the operator as a share of "
                + $"{SupplierComparisonWeights.RequiredTotal}, and it is not one unless they do.");

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            // Replaying the same key returns the state that key produced rather than applying the
            // change twice. A retried save must not advance Version a second time — the version is
            // what a concurrent editor's optimistic check is standing on.
            if (await db.TenantGovernanceAuditEvents.AsNoTracking().AnyAsync(
                    x => x.BusinessUnitId == tenantId && x.IdempotencyKey == idempotencyKey, ct))
            {
                await tx.RollbackAsync(ct);
                return await GetAsync(tenantId, ct);
            }

            var now = DateTime.UtcNow;
            var weights = await db.SupplierComparisonWeightSets
                .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);
            // Create on demand. A tenant running on the defaults has no row, and the first edit is
            // exactly when one should exist — the read path deliberately never writes.
            var before = weights is null ? null : Map(weights, false);
            if (weights is null)
            {
                weights = SupplierComparisonWeights.DefaultFor(tenantId);
                weights.CreatedOn = now;
                db.SupplierComparisonWeightSets.Add(weights);
            }

            weights.PriceWeight = price;
            weights.LeadTimeWeight = leadTime;
            weights.WarrantyWeight = warranty;
            weights.PaymentTermsWeight = paymentTerms;
            weights.Version++;
            weights.ModifiedOn = now;
            weights.ModifiedBy = actor;

            var after = Map(weights, false);
            db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
            {
                BusinessUnitId = tenantId,
                Area = Area,
                AggregateType = nameof(SupplierComparisonWeights),
                AggregateReference = $"tenant:{tenantId}",
                Action = ActionWeightsUpdated,
                Reason = reason,
                // Before AND after. "Price is worth 40" tells an auditor nothing; "it was 70 and this
                // person made it 40 on this date for this reason" is the record that answers why the
                // comparison recommended a different supplier last month.
                EvidenceJson = JsonSerializer.Serialize(new { before, after }),
                IdempotencyKey = idempotencyKey,
                ActorUserId = actorUserId,
                OccurredOn = now
            });

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            db.ChangeTracker.Clear();
            return after;
        });
    }

    private static int Weight(int? value, string subject)
    {
        // Absent, not zero. A screen that posts three of four fields would otherwise set the fourth
        // to 0 and, if the remainder happened to total 100, save a weight nobody chose.
        if (value is null)
            throw new SupplierComparisonWeightsValidationException(
                $"{subject} is required. Send all four weights together: they are one decision that "
                + $"must total {SupplierComparisonWeights.RequiredTotal}.");
        if (value < 0 || value > SupplierComparisonWeights.RequiredTotal)
            throw new SupplierComparisonWeightsValidationException(
                $"{subject} must be between 0 and {SupplierComparisonWeights.RequiredTotal}.");
        return value.Value;
    }

    private static SupplierComparisonWeightsView Map(SupplierComparisonWeights weights, bool isDefault) =>
        new(weights.BusinessUnitId, weights.PriceWeight, weights.LeadTimeWeight, weights.WarrantyWeight,
            weights.PaymentTermsWeight, weights.Version, weights.ModifiedOn, weights.ModifiedBy,
            isDefault, SupplierComparisonWeights.RequiredTotal);
}
