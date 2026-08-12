using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierEvaluation;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The supplier comparison weights are settable by the customer, attributable, and tenant-isolated.
///
/// <para>A configurable score is only configurable if a customer can change it and an auditor can
/// see who did. Four numbers here decide which supplier a sales engineer is shown as the best offer
/// on every RFQ line compared afterwards, so two comparisons of the same quotes can name different
/// winners either side of one edit — and the record of who changed what, when and why is the only
/// thing that explains that later.</para>
/// </summary>
public sealed class SupplierComparisonWeightsSettingsTests
{
    private const long Tenant = 98_301;
    private const long OtherTenant = 98_302;
    private const long Actor = 4_471;

    [Fact]
    public async Task A_tenant_with_no_row_reads_the_defaults_and_says_so()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var view = await new SupplierComparisonWeightsService(context).GetAsync(Tenant);

        Assert.True(view.IsDefault);
        Assert.Equal(80, view.PriceWeight);
        Assert.Equal(20, view.LeadTimeWeight);
        // Both zero on purpose, and this pair of assertions is the guard on the whole feature being
        // usable on day one. Warranty is free text and CreditDays ships in this gate, so neither has
        // a value on any supplier that already exists. A missing weighted criterion is never scored
        // as zero — so any non-zero default here would put "Cannot score" on every row of every
        // comparison until the entire supplier master is hand-filled. The score must be computable
        // from what every quote already carries: landed cost and lead time.
        Assert.Equal(0, view.WarrantyWeight);
        Assert.Equal(0, view.PaymentTermsWeight);
        Assert.Equal(100, view.PriceWeight + view.LeadTimeWeight
            + view.WarrantyWeight + view.PaymentTermsWeight);
        Assert.Equal(100, view.RequiredTotal);
        Assert.Null(view.ModifiedBy);
    }

    /// <summary>
    /// The whole point of the row: what the customer saved is what the scorer uses. The comparison
    /// path resolves through the same service rather than reading constants.
    /// </summary>
    [Fact]
    public async Task What_the_customer_saved_is_what_the_scorer_uses()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new SupplierComparisonWeightsService(context);
        Assert.Equal(80, (await service.ResolveAsync(Tenant)).Price);

        await service.UpdateAsync(Tenant, Actor, "buyer@example.com", "weights-speed",
            new UpdateSupplierComparisonWeightsCommand(40, 50, 0, 10,
                "Our customers award on delivery date; speed now outranks price."));

        var resolved = await service.ResolveAsync(Tenant);
        Assert.Equal(40, resolved.Price);
        Assert.Equal(50, resolved.LeadTime);
    }

    [Fact]
    public async Task A_change_creates_the_row_stamps_the_author_and_appends_an_audit_event()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new SupplierComparisonWeightsService(context);
        var saved = await service.UpdateAsync(Tenant, Actor, "buyer@example.com", "weights-key-1",
            new UpdateSupplierComparisonWeightsCommand(40, 50, 0, 10,
                "Our customers award on delivery date; speed now outranks price."));

        Assert.False(saved.IsDefault);
        Assert.Equal(40, saved.PriceWeight);
        Assert.Equal("buyer@example.com", saved.ModifiedBy);
        Assert.NotNull(saved.ModifiedOn);
        Assert.Equal(2, saved.Version);

        context.ChangeTracker.Clear();
        var audit = await context.TenantGovernanceAuditEvents.SingleAsync(x => x.BusinessUnitId == Tenant);
        Assert.Equal(SupplierComparisonWeightsService.ActionWeightsUpdated, audit.Action);
        Assert.Equal(Actor, audit.ActorUserId);
        Assert.Contains("award on delivery date", audit.Reason);
        using var evidence = JsonDocument.Parse(audit.EvidenceJson);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("before").ValueKind);
        Assert.Equal(40, evidence.RootElement.GetProperty("after").GetProperty("PriceWeight").GetInt32());
    }

    [Fact]
    public async Task A_second_change_records_what_it_replaced()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new SupplierComparisonWeightsService(context);
        await service.UpdateAsync(Tenant, Actor, "first@example.com", "weights-key-1",
            new UpdateSupplierComparisonWeightsCommand(100, 0, 0, 0, "Cheapest wins while we bed in."));
        var second = await service.UpdateAsync(Tenant, Actor, "second@example.com", "weights-key-2",
            new UpdateSupplierComparisonWeightsCommand(70, 20, 0, 10, "Back to a balanced comparison."));

        Assert.Equal(70, second.PriceWeight);
        Assert.Equal(3, second.Version);

        context.ChangeTracker.Clear();
        var audit = await context.TenantGovernanceAuditEvents
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.IdempotencyKey == "weights-key-2");
        using var evidence = JsonDocument.Parse(audit.EvidenceJson);
        // This is the record that explains why the comparison recommended a different supplier last
        // month from the one it recommends today.
        Assert.Equal(100, evidence.RootElement.GetProperty("before").GetProperty("PriceWeight").GetInt32());
        Assert.Equal(70, evidence.RootElement.GetProperty("after").GetProperty("PriceWeight").GetInt32());
    }

    /// <summary>
    /// Zero is a REAL weight — "Cheapest wins" is 100/0/0/0 — and EF omits a property whose value
    /// equals its sentinel from the INSERT, letting the database default supply it. Without an
    /// impossible sentinel this row would come back reading 20 for a lead time the customer set to 0.
    /// </summary>
    [Fact]
    public async Task A_weight_the_customer_set_to_zero_is_stored_as_zero()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        await new SupplierComparisonWeightsService(context).UpdateAsync(Tenant, Actor,
            "buyer@example.com", "cheapest-wins",
            new UpdateSupplierComparisonWeightsCommand(100, 0, 0, 0,
                "Reproducing the pre-Gate-2 recommendation exactly while the team gets used to it."));

        context.ChangeTracker.Clear();
        var stored = await context.SupplierComparisonWeightSets.AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant);
        Assert.Equal(100, stored.PriceWeight);
        Assert.Equal(0, stored.LeadTimeWeight);
        Assert.Equal(0, stored.PaymentTermsWeight);
    }

    [Fact]
    public async Task Replaying_one_idempotency_key_does_not_apply_the_change_twice()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var service = new SupplierComparisonWeightsService(context);
        var command = new UpdateSupplierComparisonWeightsCommand(40, 50, 0, 10, "Speed matters.");
        await service.UpdateAsync(Tenant, Actor, "a@example.com", "same-key", command);
        var replayed = await service.UpdateAsync(Tenant, Actor, "a@example.com", "same-key", command);

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

        await Assert.ThrowsAsync<ERP_RFQ_Automation.PlatformGovernance.PlatformGovernanceValidationException>(
            () => new SupplierComparisonWeightsService(context).UpdateAsync(Tenant, Actor,
                "a@example.com", "no-reason",
                new UpdateSupplierComparisonWeightsCommand(40, 50, 0, 10, "   ")));

        Assert.Empty(await context.SupplierComparisonWeightSets.Where(x => x.BusinessUnitId == Tenant).ToListAsync());
    }

    /// <summary>
    /// A set that does not total 100 is refused. It would still produce a number, and that number
    /// would be presented to an operator as a share of 100 — a score nobody could reconcile against
    /// the per-criterion row shown beside it.
    /// </summary>
    [Theory]
    [InlineData(70, 20, 0, 0)]
    [InlineData(70, 20, 10, 10)]
    [InlineData(0, 0, 0, 0)]
    public async Task A_weight_set_that_does_not_total_one_hundred_is_refused(
        int price, int leadTime, int warranty, int paymentTerms)
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var refusal = await Assert.ThrowsAsync<SupplierComparisonWeightsValidationException>(
            () => new SupplierComparisonWeightsService(context).UpdateAsync(Tenant, Actor,
                "a@example.com", $"bad-{price}-{leadTime}-{warranty}-{paymentTerms}",
                new UpdateSupplierComparisonWeightsCommand(price, leadTime, warranty, paymentTerms,
                    "Attempting an incomplete weight set.")));

        Assert.Contains("must total 100", refusal.Message);
        Assert.Empty(await context.SupplierComparisonWeightSets.Where(x => x.BusinessUnitId == Tenant).ToListAsync());
    }

    /// <summary>
    /// An omitted weight is refused rather than read as 0. A screen posting three of four fields
    /// would otherwise save a fourth weight nobody chose whenever the remainder happened to total 100.
    /// </summary>
    [Fact]
    public async Task An_omitted_weight_is_refused_rather_than_read_as_zero()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();

        var refusal = await Assert.ThrowsAsync<SupplierComparisonWeightsValidationException>(
            () => new SupplierComparisonWeightsService(context).UpdateAsync(Tenant, Actor,
                "a@example.com", "omitted",
                new UpdateSupplierComparisonWeightsCommand(70, 20, null, 10, "Three of four fields.")));

        Assert.Contains("Send all four weights together", refusal.Message);
    }

    /// <summary>
    /// A cross-tenant read of the weights row returns nothing. This is the query filter declared
    /// beside the entity doing its job — the row decides which supplier another company is
    /// recommended, and it is not readable outside the tenant that set it.
    /// </summary>
    [Fact]
    public async Task A_cross_tenant_read_of_the_weights_row_returns_nothing()
    {
        using var database = new TestDb();
        using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.EnsureBusinessUnit(seed, OtherTenant);
            await seed.SaveChangesAsync();
        }

        await using (var owner = database.ContextFor(Tenant))
        {
            await new SupplierComparisonWeightsService(owner).UpdateAsync(Tenant, Actor,
                "owner@example.com", "owner-key",
                new UpdateSupplierComparisonWeightsCommand(40, 50, 0, 10, "Speed matters here."));
        }

        await using var intruder = database.ContextFor(OtherTenant);
        Assert.Empty(await intruder.SupplierComparisonWeightSets.ToListAsync());
        Assert.Null(await intruder.SupplierComparisonWeightSets
            .SingleOrDefaultAsync(x => x.BusinessUnitId == Tenant));
        // …and the other tenant is still on its own defaults rather than inheriting the neighbour's.
        var view = await new SupplierComparisonWeightsService(intruder).GetAsync(OtherTenant);
        Assert.True(view.IsDefault);
        Assert.Equal(80, view.PriceWeight);
    }

    /// <summary>
    /// The tier column exists, permits "not yet classified", and is confined to the three tiers.
    /// Null is the state every existing supplier is in and is a legitimate resting state.
    /// </summary>
    [Fact]
    public async Task A_supplier_tier_is_optional_and_confined_to_the_three_tiers()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        var unclassified = NewSupplier(1, "Unclassified Trading", tier: null, creditDays: null);
        var partner = NewSupplier(2, "Partner Industrial", SupplierTiers.Tier1Partner, creditDays: 60);
        context.Suppliers.AddRange(unclassified, partner);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.Suppliers.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Null(stored[0].Tier);
        Assert.Null(stored[0].CreditDays);
        Assert.Equal(SupplierTiers.Tier1Partner, stored[1].Tier);
        Assert.Equal(60, stored[1].CreditDays);

        // Anything outside the three tiers is refused by the database, not merely by a validator.
        context.Suppliers.Add(NewSupplier(3, "Invented Tier Co", "TIER_0_PLATINUM", creditDays: null));
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    /// <summary>
    /// Credit days of 0 is the positive assertion "cash on delivery" and must survive the round trip
    /// distinct from null, which means NOT CONFIGURED and makes the offer unscorable on payment terms.
    /// </summary>
    [Fact]
    public async Task Zero_credit_days_is_stored_distinctly_from_not_configured()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        context.Suppliers.AddRange(
            NewSupplier(11, "Cash On Delivery Ltd", SupplierTiers.Tier2Extended, creditDays: 0),
            NewSupplier(12, "Terms Unknown Ltd", SupplierTiers.Tier2Extended, creditDays: null));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var stored = await context.Suppliers.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(0, stored[0].CreditDays);
        Assert.Null(stored[1].CreditDays);
    }

    private static Supplier NewSupplier(long id, string name, string? tier, int? creditDays) => new()
    {
        Id = id,
        Name = name,
        ImageUrl = string.Empty,
        Buid = Tenant,
        Tier = tier,
        CreditDays = creditDays,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };
}
