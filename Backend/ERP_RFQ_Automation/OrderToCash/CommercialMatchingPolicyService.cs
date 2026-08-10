using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.OrderToCash;

/// <summary>What a caller may change on the tenant's commercial policy, and why.</summary>
/// <param name="SupplierInputTaxRecoverablePercent">
/// 0–100. Null leaves the stored value alone (patch semantics).
/// </param>
/// <param name="OutputTaxRatePercent">
/// 0–100. Null leaves the stored value alone; to say "we have no rate" send
/// <paramref name="ClearOutputTaxRate"/> instead, because null on the wire cannot mean both
/// "unchanged" and "erase this".
/// </param>
/// <param name="ClearOutputTaxRate">
/// Erases the output tax rate. Every quote in the tenant then fails its send gate until a rate is
/// set again — which is the point: it is the one honest state for a tenant that does not know its
/// rate, and it is deliberately loud.
/// </param>
/// <param name="Reason">
/// Mandatory. This row re-bases every landed cost and every derived tax computed after it; a
/// change with no reason, no author and no date is not defensible to an auditor.
/// </param>
public sealed record UpdateCommercialMatchingPolicyCommand(
    decimal? SupplierInputTaxRecoverablePercent,
    decimal? OutputTaxRatePercent,
    bool ClearOutputTaxRate,
    decimal? PriceTolerancePercent,
    decimal? PriceToleranceMinimumAmount,
    decimal? QuantityTolerancePercent,
    string Reason);

/// <summary>
/// The tenant's commercial policy as the settings screen shows it.
///
/// <para><c>IsDefault</c> is true while the tenant has never saved the policy and is running on the
/// entity's defaults — the screen says so rather than presenting inherited values as chosen ones.
/// <c>TaxCategories</c> ships the permitted quote-line categories so no client hardcodes the
/// list.</para>
/// </summary>
public sealed record CommercialMatchingPolicyView(
    long BusinessUnitId,
    decimal SupplierInputTaxRecoverablePercent,
    decimal? OutputTaxRatePercent,
    decimal PriceTolerancePercent,
    decimal PriceToleranceMinimumAmount,
    decimal QuantityTolerancePercent,
    long Version,
    DateTime? ModifiedOn,
    string? ModifiedBy,
    bool IsDefault,
    IReadOnlyList<string> TaxCategories);

public sealed class CommercialPolicyValidationException(string message) : InvalidOperationException(message);

/// <summary>
/// The one way to CHANGE a tenant's <see cref="CommercialMatchingPolicy"/>.
///
/// <para><b>Why this exists.</b> Every field on that row was unreachable. The entity was referenced
/// by the model builder, the tenancy query filter and a read-only resolver, and by nothing else —
/// no controller, no service write, not one frontend reference. Its own <c>Version</c>,
/// <c>ModifiedOn</c> and <c>ModifiedBy</c> columns had never been written by anything. The product
/// owner's direction was explicit — "keep it custom and let the customer set this... rather than
/// collecting figures for each of the country" — and the customer could not set it.</para>
///
/// <para><b>Why a reason is mandatory.</b> Input-tax recoverability re-bases every landed cost
/// computed after it, and the output tax rate re-bases every derived tax. Both flow through
/// <c>landed / (1 - margin)</c> into customer prices. A change of that reach with no author, no
/// date and no stated reason cannot be explained to an auditor a year later, so the write refuses
/// without one and records it in the tenant governance ledger alongside a before/after snapshot.</para>
///
/// <para>Patch semantics throughout: an omitted field is unchanged, so a screen that only edits the
/// tolerances cannot silently reset a tax rate it never displayed.</para>
/// </summary>
public sealed class CommercialMatchingPolicyService(ErpRfqAutomationContext db)
{
    private const string Area = "CommercialPolicy";
    public const string ActionPolicyUpdated = "COMMERCIAL_POLICY_UPDATED";

    /// <summary>Bounds shared with the database check constraints and the settings screen.</summary>
    public const decimal MaximumPercent = 100m;

    /// <summary>
    /// A ceiling on the PO price tolerance. Above this the tolerance stops being rounding noise and
    /// starts being a blanket instruction to accept whatever the customer ordered at, which is the
    /// discrepancy control switched off by the back door.
    /// </summary>
    public const decimal MaximumTolerancePercent = 25m;

    public async Task<CommercialMatchingPolicyView> GetAsync(long tenantId, CancellationToken ct = default)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var stored = await db.CommercialMatchingPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);
        return Map(stored ?? CommercialMatchingPolicy.DefaultFor(tenantId), stored is null);
    }

    public async Task<CommercialMatchingPolicyView> UpdateAsync(long tenantId, long actorUserId,
        string actor, string idempotencyKey, UpdateCommercialMatchingPolicyCommand command,
        CancellationToken ct = default)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        actor = PlatformGovernanceService.Required(actor, 255, "An authenticated actor is required.");
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        var reason = PlatformGovernanceService.Required(command.Reason, 1000,
            "A reason is required: this policy re-bases every landed cost and every derived tax computed after it.");

        if (command.OutputTaxRatePercent.HasValue && command.ClearOutputTaxRate)
            throw new CommercialPolicyValidationException(
                "Send either an output tax rate or the instruction to clear it, not both.");
        var recoverable = Percent(command.SupplierInputTaxRecoverablePercent,
            "Input tax recoverability");
        var outputRate = Percent(command.OutputTaxRatePercent, "The output tax rate");
        var priceTolerance = Bounded(command.PriceTolerancePercent, 0m, MaximumTolerancePercent,
            $"The price tolerance must be between 0 and {MaximumTolerancePercent}%. A wider tolerance " +
            "does not absorb rounding, it accepts a different commercial deal without reporting one.");
        var priceToleranceMinimum = Bounded(command.PriceToleranceMinimumAmount, 0m, decimal.MaxValue,
            "The price tolerance minimum amount cannot be negative.");
        var quantityTolerance = Bounded(command.QuantityTolerancePercent, 0m, MaximumTolerancePercent,
            $"The quantity tolerance must be between 0 and {MaximumTolerancePercent}%.");

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
            var policy = await db.CommercialMatchingPolicies
                .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId, ct);
            // Create on demand. A tenant running on the defaults has no row, and the first edit is
            // exactly when one should exist — the read path deliberately never writes.
            var before = policy is null ? null : Map(policy, false);
            if (policy is null)
            {
                policy = CommercialMatchingPolicy.DefaultFor(tenantId);
                policy.CreatedOn = now;
                db.CommercialMatchingPolicies.Add(policy);
            }

            if (recoverable.HasValue) policy.SupplierInputTaxRecoverablePercent = recoverable.Value;
            if (command.ClearOutputTaxRate) policy.OutputTaxRatePercent = null;
            else if (outputRate.HasValue) policy.OutputTaxRatePercent = outputRate.Value;
            if (priceTolerance.HasValue) policy.PriceTolerancePercent = priceTolerance.Value;
            if (priceToleranceMinimum.HasValue) policy.PriceToleranceMinimumAmount = priceToleranceMinimum.Value;
            if (quantityTolerance.HasValue) policy.QuantityTolerancePercent = quantityTolerance.Value;

            policy.Version++;
            policy.ModifiedOn = now;
            policy.ModifiedBy = actor;

            var after = Map(policy, false);
            db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
            {
                BusinessUnitId = tenantId,
                Area = Area,
                AggregateType = nameof(CommercialMatchingPolicy),
                AggregateReference = $"tenant:{tenantId}",
                Action = ActionPolicyUpdated,
                Reason = reason,
                // Before AND after. "The rate is 15" tells an auditor nothing; "it was 5 and this
                // person made it 15 on this date for this reason" is the record that answers why a
                // quote issued last month carries a different figure from one issued today.
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

    private static decimal? Percent(decimal? value, string subject) =>
        Bounded(value, 0m, MaximumPercent, $"{subject} must be between 0 and 100 percent.");

    private static decimal? Bounded(decimal? value, decimal minimum, decimal maximum, string message)
    {
        if (value is null) return null;
        if (value < minimum || value > maximum) throw new CommercialPolicyValidationException(message);
        return decimal.Round(value.Value, 4, MidpointRounding.AwayFromZero);
    }

    private static CommercialMatchingPolicyView Map(CommercialMatchingPolicy policy, bool isDefault) =>
        new(policy.BusinessUnitId, policy.SupplierInputTaxRecoverablePercent, policy.OutputTaxRatePercent,
            policy.PriceTolerancePercent, policy.PriceToleranceMinimumAmount, policy.QuantityTolerancePercent,
            policy.Version, policy.ModifiedOn, policy.ModifiedBy, isDefault,
            QuoteLineTaxCategories.All);
}
