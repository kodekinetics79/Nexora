using System.Text.Json;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The commercial policy is settable by the customer, and every change is attributable.
///
/// The defect
/// ----------
/// <c>CommercialMatchingPolicy</c> was referenced by the model builder, the tenancy query filter
/// and a read-only resolver — and by nothing else. No controller, no service write, not one
/// frontend reference. Its own <c>Version</c>, <c>ModifiedOn</c> and <c>ModifiedBy</c> columns had
/// never been written by any code path. The product owner's direction was explicit — "keep it
/// custom and let the customer set this... rather than collecting figures for each of the country"
/// — and the customer could not set it, so every tenant silently ran the KSA defaults.
///
/// Why the reason is not optional
/// ------------------------------
/// Input-tax recoverability re-bases every landed cost computed after it, and the output tax rate
/// re-bases every derived tax. Both reach customer prices through <c>landed / (1 - margin)</c>. A
/// change of that reach with no author, no date and no stated reason cannot be explained to an
/// auditor a year later — so the write refuses without one and records a before/after snapshot in
/// the tenant governance ledger.
/// </summary>
public sealed class CommercialPolicySettingsTests
{
    private const long Tenant = 98_101;
    private const long Actor = 4_471;

    [Fact]
    public async Task A_tenant_with_no_row_reads_the_defaults_and_says_so()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var view = await new CommercialMatchingPolicyService(context).GetAsync(Tenant);

        Assert.True(view.IsDefault);
        Assert.Equal(CommercialMatchingPolicy.FullyRecoverablePercent, view.SupplierInputTaxRecoverablePercent);
        Assert.Equal(CommercialMatchingPolicy.KsaStandardOutputTaxRatePercent, view.OutputTaxRatePercent);
        Assert.Equal(2.0m, view.PriceTolerancePercent);
        Assert.Null(view.ModifiedBy);
        // The screen gets the permitted categories from the server rather than hardcoding them.
        Assert.Equal(QuoteLineTaxCategories.All, view.TaxCategories);
    }

    [Fact]
    public async Task A_change_creates_the_row_stamps_the_author_and_appends_an_audit_event()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new CommercialMatchingPolicyService(context);
        var saved = await service.UpdateAsync(Tenant, Actor, "controller@example.com", "policy-key-1",
            new UpdateCommercialMatchingPolicyCommand(
                SupplierInputTaxRecoverablePercent: 70m,
                OutputTaxRatePercent: 5m,
                ClearOutputTaxRate: false,
                PriceTolerancePercent: null,
                PriceToleranceMinimumAmount: null,
                QuantityTolerancePercent: null,
                Reason: "Partly exempt from Q3; ZATCA ruling 2026-114 sets the recovery ratio at 70%."));

        Assert.False(saved.IsDefault);
        Assert.Equal(70m, saved.SupplierInputTaxRecoverablePercent);
        Assert.Equal(5m, saved.OutputTaxRatePercent);
        // Patch semantics: a field the screen never showed is not silently reset.
        Assert.Equal(2.0m, saved.PriceTolerancePercent);
        Assert.Equal("controller@example.com", saved.ModifiedBy);
        Assert.NotNull(saved.ModifiedOn);
        Assert.Equal(2, saved.Version);

        context.ChangeTracker.Clear();
        var audit = await context.TenantGovernanceAuditEvents.SingleAsync(x => x.BusinessUnitId == Tenant);
        Assert.Equal(CommercialMatchingPolicyService.ActionPolicyUpdated, audit.Action);
        Assert.Equal(Actor, audit.ActorUserId);
        Assert.Contains("ZATCA ruling 2026-114", audit.Reason);
        // Before AND after. "The rate is 5" tells an auditor nothing on its own.
        using var evidence = JsonDocument.Parse(audit.EvidenceJson);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("before").ValueKind);
        Assert.Equal(5m, evidence.RootElement.GetProperty("after")
            .GetProperty("OutputTaxRatePercent").GetDecimal());
    }

    [Fact]
    public async Task A_second_change_records_what_it_replaced()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new CommercialMatchingPolicyService(context);
        await service.UpdateAsync(Tenant, Actor, "first@example.com", "policy-key-1",
            Command(outputRate: 5m, reason: "Interim rate pending the ruling."));
        var second = await service.UpdateAsync(Tenant, Actor, "second@example.com", "policy-key-2",
            Command(outputRate: 15m, reason: "Ruling received; back to the standard rate."));

        Assert.Equal(15m, second.OutputTaxRatePercent);
        Assert.Equal(3, second.Version);

        context.ChangeTracker.Clear();
        var audit = await context.TenantGovernanceAuditEvents
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.IdempotencyKey == "policy-key-2");
        using var evidence = JsonDocument.Parse(audit.EvidenceJson);
        // This is the record that explains why a quote issued last month carries a different figure
        // from one issued today.
        Assert.Equal(5m, evidence.RootElement.GetProperty("before")
            .GetProperty("OutputTaxRatePercent").GetDecimal());
        Assert.Equal(15m, evidence.RootElement.GetProperty("after")
            .GetProperty("OutputTaxRatePercent").GetDecimal());
    }

    [Fact]
    public async Task Replaying_one_idempotency_key_does_not_apply_the_change_twice()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new CommercialMatchingPolicyService(context);
        await service.UpdateAsync(Tenant, Actor, "a@example.com", "same-key", Command(5m, "First save."));
        var replayed = await service.UpdateAsync(Tenant, Actor, "a@example.com", "same-key",
            Command(5m, "First save."));

        // Version is what a concurrent editor's optimistic check stands on; a retried save must not
        // advance it a second time.
        Assert.Equal(2, replayed.Version);
        Assert.Equal(1, await context.TenantGovernanceAuditEvents.CountAsync(x => x.BusinessUnitId == Tenant));
    }

    [Fact]
    public async Task A_change_without_a_reason_is_refused()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new CommercialMatchingPolicyService(context);
        await Assert.ThrowsAsync<ERP_RFQ_Automation.PlatformGovernance.PlatformGovernanceValidationException>(
            () => service.UpdateAsync(Tenant, Actor, "a@example.com", "no-reason", Command(15m, "   ")));

        Assert.Empty(await context.CommercialMatchingPolicies.Where(x => x.BusinessUnitId == Tenant).ToListAsync());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public async Task A_recovery_ratio_outside_zero_to_one_hundred_is_refused(decimal percent)
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new CommercialMatchingPolicyService(context);
        await Assert.ThrowsAsync<CommercialPolicyValidationException>(() => service.UpdateAsync(
            Tenant, Actor, "a@example.com", $"bad-{percent}",
            new UpdateCommercialMatchingPolicyCommand(percent, null, false, null, null, null,
                "Attempting an impossible ratio.")));
    }

    [Fact]
    public async Task Clearing_the_output_tax_rate_is_an_explicit_instruction_not_an_omitted_field()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new CommercialMatchingPolicyService(context);

        // Omitting the rate leaves it alone — otherwise a tolerances-only screen would erase it.
        var untouched = await service.UpdateAsync(Tenant, Actor, "a@example.com", "tolerances-only",
            new UpdateCommercialMatchingPolicyCommand(null, null, false, 3m, null, null,
                "Widening the PO price tolerance after repeated rounding discrepancies."));
        Assert.Equal(CommercialMatchingPolicy.KsaStandardOutputTaxRatePercent, untouched.OutputTaxRatePercent);
        Assert.Equal(3m, untouched.PriceTolerancePercent);

        // Erasing it is a separate, deliberate instruction — and every quote in the tenant then
        // fails its send gate, which is the honest outcome for a tenant that does not know its rate.
        var cleared = await service.UpdateAsync(Tenant, Actor, "a@example.com", "clear-rate",
            new UpdateCommercialMatchingPolicyCommand(null, null, true, null, null, null,
                "Rate under review with the tax adviser; quoting is suspended until it is restated."));
        Assert.Null(cleared.OutputTaxRatePercent);
        Assert.Null(await context.ResolveOutputTaxRatePercentAsync(Tenant));

        // Sending both at once is ambiguous and is refused rather than resolved by precedence.
        await Assert.ThrowsAsync<CommercialPolicyValidationException>(() => service.UpdateAsync(
            Tenant, Actor, "a@example.com", "both",
            new UpdateCommercialMatchingPolicyCommand(null, 15m, true, null, null, null, "Ambiguous.")));
    }

    private static UpdateCommercialMatchingPolicyCommand Command(decimal? outputRate, string reason) =>
        new(null, outputRate, false, null, null, null, reason);
}
