using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using InventoryRow = ERP_RFQ_Automation.Models.Inventory;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 6 — FR-INV-04. Minimum and maximum stock levels, and the reorder alert engine that reads
/// them.
///
/// <para><b>What these tests are for.</b> Not one of them asserts that a level round-trips. The
/// failure this requirement invites is an alert that fires on everything: a maximum backfilled to
/// zero declares every item overstocked on the first sweep, a shortage measured on on-hand misses
/// the warehouse whose entire stock is already promised, and an alert with no resolve step fires
/// once in the lifetime of a stock row and is silent for every shortage after it. Each test below
/// is one of those, and each fails if the corresponding rule is removed.</para>
///
/// <para>PostgreSQL CHECK constraints are not exercised here — the portable lane runs SQLite with
/// <c>PRAGMA ignore_check_constraints = ON</c>. Every invariant they express is also enforced in
/// <see cref="StockLedgerService"/> and <see cref="ReorderAlertService"/>, and that is what these
/// tests hit; the constraint text itself is certified in the PostgreSQL lane by
/// <c>Gate6ReorderAlertPostgreSqlTests</c>.</para>
/// </summary>
public sealed class Gate6ReorderAlertTests
{
    private const long Tenant = ProcurementTestData.Tenant;
    private const long Product = ProcurementTestData.Product;
    private const long Warehouse = ProcurementTestData.Warehouse;
    private const long StockRowId = ProcurementTestData.Inventory;

    // ============================================ the first sweep must not mail the whole catalogue

    [Fact]
    public async Task A_tenant_that_has_configured_no_levels_raises_no_alerts_at_all()
    {
        using var scenario = new ReorderScenario();
        // Exactly the state every tenant is in on the morning of the deploy: stock exists, and
        // nothing has been configured against it. The backfill leaves MinimumLevel and MaximumLevel
        // NULL and the code default is NULL, and both mean "not configured".
        await using var context = scenario.Context();
        var (summary, raised) = await new ReorderAlertService(context).ReconcileAsync(Tenant);

        Assert.Equal(0, summary.Raised);
        Assert.Empty(raised);
    }

    [Fact]
    public async Task A_maximum_of_zero_does_not_declare_every_item_overstocked()
    {
        using var scenario = new ReorderScenario();
        // The trap this requirement sets. A maximum backfilled or keyed as 0 would mean "any stock
        // at all is too much", and the first sweep would raise an overstock alert against every row
        // in every warehouse — after which the channel is filtered and every real alert is lost.
        // Zero is read as "not configured", exactly as a non-positive SLA window is.
        await scenario.SetLevelsAsync(minimum: null, maximum: 0m);

        var conditions = await scenario.EvaluateAsync();
        Assert.Null(Assert.Single(conditions, x => x.InventoryId == StockRowId).Kind);
    }

    [Fact]
    public async Task A_row_with_no_level_set_is_reported_as_unmonitored_rather_than_as_healthy()
    {
        using var scenario = new ReorderScenario();

        var row = Assert.Single(await scenario.EvaluateAsync(), x => x.InventoryId == StockRowId);

        Assert.Null(row.Kind);
        // The distinction the screen depends on: "nothing is wrong" and "nobody is looking" are
        // different answers, and only one of them is a reason to relax.
        Assert.False(row.IsConfigured);
    }

    // ================================================ what makes the alert actionable rather than noise

    [Fact]
    public async Task The_shortage_is_measured_on_what_can_be_promised_not_on_what_is_in_the_building()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(100m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        // Every one of the hundred units is spoken for. On-hand says the shelf is full; the
        // warehouse can supply nobody, and the next enquiry would be quoted against stock that is
        // not there.
        await scenario.ReserveAsync(100m);

        var row = Assert.Single(await scenario.EvaluateAsync(), x => x.InventoryId == StockRowId);

        Assert.Equal(100m, row.OnHand);
        Assert.Equal(0m, row.Available);
        Assert.Equal(ReorderAlertKinds.OutOfStock, row.Kind);
    }

    [Fact]
    public async Task A_shortage_already_covered_by_an_order_in_flight_is_not_alerted_on()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(2m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        // The buyer has already acted. Telling them again every half hour until the goods land is
        // precisely how a mailbox rule gets written and every subsequent alert is lost with it.
        await scenario.AddIncomingAsync(20m);

        await using var context = scenario.Context();
        var (summary, raised) = await new ReorderAlertService(context).ReconcileAsync(Tenant);

        Assert.Empty(raised);
        // Counted rather than merely skipped: "we chose not to tell you about this one" is
        // information, and an absence is not.
        Assert.Equal(1, summary.Suppressed);
    }

    [Fact]
    public async Task One_short_row_produces_one_alert_and_not_one_for_every_level_it_is_under()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(0m);
        await scenario.SetReorderPointAsync(25m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: 50m);

        await using var context = scenario.Context();
        var (_, raised) = await new ReorderAlertService(context).ReconcileAsync(Tenant);

        // Out of stock, below minimum and under the reorder point are the same fact three times
        // over. The ladder stops at the worst rung.
        var alert = Assert.Single(raised);
        Assert.Equal(ReorderAlertKinds.OutOfStock, alert.Kind);
    }

    [Fact]
    public async Task An_overstock_is_measured_on_physical_units_because_promised_stock_still_fills_the_shelf()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(80m);
        await scenario.SetLevelsAsync(minimum: null, maximum: 50m);
        await scenario.ReserveAsync(80m);

        var row = Assert.Single(await scenario.EvaluateAsync(), x => x.InventoryId == StockRowId);

        // Measuring the maximum on available would call a full warehouse empty the moment its stock
        // was promised, and the capital tied up in it would never be reported.
        Assert.Equal(ReorderAlertKinds.Overstock, row.Kind);
        Assert.Equal(30m, row.ShortfallQuantity);
    }

    // ================================================================== send-once, and send again later

    [Fact]
    public async Task An_alert_that_is_already_open_is_not_raised_again_by_the_next_sweep()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(0m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);

        Assert.Single(await scenario.ReconcileAsync());
        Assert.Empty(await scenario.ReconcileAsync());
        Assert.Empty(await scenario.ReconcileAsync());

        await using var verify = scenario.Context();
        Assert.Equal(1, await verify.Set<InventoryReorderAlert>().CountAsync());
    }

    [Fact]
    public async Task An_alert_resolves_when_the_stock_recovers_and_the_same_row_can_alert_again_later()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(0m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        var first = Assert.Single(await scenario.ReconcileAsync());

        await scenario.SetOnHandAsync(40m);
        Assert.Empty(await scenario.ReconcileAsync());

        await using (var verify = scenario.Context())
        {
            var settled = await verify.Set<InventoryReorderAlert>().SingleAsync(x => x.Id == first.Id);
            Assert.Equal(ReorderAlertStatuses.Resolved, settled.Status);
            Assert.NotNull(settled.ResolvedOn);
        }

        // The transition is what lets it fire again. Without it an alert fires once in the lifetime
        // of a stock row and every shortage after the first is silent — which is worse than having
        // no alert at all, because the screen looks calm.
        await scenario.SetOnHandAsync(0m);
        var second = Assert.Single(await scenario.ReconcileAsync());
        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public async Task A_row_that_gets_worse_supersedes_its_alert_rather_than_adding_a_second_one()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(6m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        var below = Assert.Single(await scenario.ReconcileAsync());
        Assert.Equal(ReorderAlertKinds.BelowMinimum, below.Kind);

        await scenario.SetOnHandAsync(0m);
        var worse = Assert.Single(await scenario.ReconcileAsync());

        Assert.Equal(ReorderAlertKinds.OutOfStock, worse.Kind);
        await using var verify = scenario.Context();
        var superseded = await verify.Set<InventoryReorderAlert>().SingleAsync(x => x.Id == below.Id);
        Assert.Equal(ReorderAlertStatuses.Resolved, superseded.Status);
        Assert.Contains(ReorderAlertKinds.OutOfStock, superseded.ResolutionReason);
    }

    // ============================================================================ the write path

    [Fact]
    public async Task Setting_a_minimum_level_is_what_makes_the_alert_engine_see_the_row()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(2m);
        Assert.Empty(await scenario.ReconcileAsync());

        // If this push-down stops happening — the level saved on a screen and never reaching the
        // stock row the sweep reads — the setting exists, the field saves, and nothing alerts.
        // That is exactly the defect the reorder point carried into this gate.
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);

        Assert.Single(await scenario.ReconcileAsync());
    }

    [Fact]
    public async Task A_maximum_below_the_minimum_is_refused_by_the_server()
    {
        using var scenario = new ReorderScenario();
        await using var context = scenario.Context();

        var refusal = await Assert.ThrowsAsync<StockLedgerException>(() =>
            new StockLedgerService(context).SetStockLevelsAsync(
                Tenant, Product, Warehouse, minimumLevel: 40m, maximumLevel: 10m, actor: "qa"));

        // No quantity would satisfy both, so the row would carry a shortage alert and an overstock
        // alert simultaneously and permanently. The refusal renders the arithmetic.
        Assert.Contains("40", refusal.Message);
        Assert.Contains("10", refusal.Message);
    }

    [Fact]
    public async Task A_negative_level_is_refused()
    {
        using var scenario = new ReorderScenario();
        await using var context = scenario.Context();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new StockLedgerService(context).SetStockLevelsAsync(
                Tenant, Product, Warehouse, minimumLevel: -1m, maximumLevel: null, actor: "qa"));
    }

    [Fact]
    public async Task Clearing_a_level_leaves_the_item_unmonitored_rather_than_at_zero()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(0m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        Assert.Single(await scenario.ReconcileAsync());

        await scenario.SetLevelsAsync(minimum: null, maximum: null);

        var row = Assert.Single(await scenario.EvaluateAsync(), x => x.InventoryId == StockRowId);
        Assert.Null(row.MinimumLevel);
        Assert.Null(row.Kind);
        Assert.False(row.IsConfigured);
    }

    // ========================================================================= the acknowledgement

    [Fact]
    public async Task An_acknowledgement_with_no_reason_is_refused()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(0m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        var alert = Assert.Single(await scenario.ReconcileAsync());

        await using var context = scenario.Context();
        // An acknowledgement with no reason is a mute button with nobody's name on it, and a mute
        // button is how an alert channel dies without anybody deciding to kill it.
        await Assert.ThrowsAsync<ArgumentException>(() => new ReorderAlertService(context)
            .AcknowledgeAsync(Tenant, alert.Id, alert.Version, "buyer@qa", "   "));
    }

    [Fact]
    public async Task An_acknowledged_alert_still_resolves_itself_when_the_stock_recovers()
    {
        using var scenario = new ReorderScenario();
        await scenario.SetOnHandAsync(0m);
        await scenario.SetLevelsAsync(minimum: 10m, maximum: null);
        var alert = Assert.Single(await scenario.ReconcileAsync());

        await using (var acknowledge = scenario.Context())
        {
            await new ReorderAlertService(acknowledge).AcknowledgeAsync(
                Tenant, alert.Id, alert.Version, "buyer@qa", "PO 4417 raised with the mill.");
        }

        await scenario.SetOnHandAsync(40m);
        await scenario.ReconcileAsync();

        await using var verify = scenario.Context();
        var settled = await verify.Set<InventoryReorderAlert>().SingleAsync(x => x.Id == alert.Id);
        // Acknowledging says "I own this", not "stop watching it". If an acknowledgement ended the
        // alert's life the ledger would forget that the shortage was real and how it ended.
        Assert.Equal(ReorderAlertStatuses.Resolved, settled.Status);
        Assert.Equal("buyer@qa", settled.AcknowledgedBy);
        Assert.Equal("PO 4417 raised with the mill.", settled.AcknowledgementReason);
    }

    // ==================================================================== the worker's tenant scope

    [Fact]
    public async Task The_sweep_refuses_to_run_for_a_tenant_the_DbContext_did_not_scope_to()
    {
        using var database = new TestDb();
        using (var seed = database.ContextFor(null))
        {
            ProcurementTestData.SeedGraph(seed, Tenant, 0);
            seed.SaveChanges();
        }

        // A background worker has no HttpContext, so it runs under the BYPASSRLS pipeline role, and
        // with a null tenant the EF global filters are no-ops — BOTH isolation layers off at once.
        // Here the context resolves a DIFFERENT tenant from the one being swept, which is what a
        // scope that failed to propagate looks like from inside the worker.
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StubTenant(ProcurementTestData.OtherTenant));
        services.AddScoped(_ => database.ContextFor(ProcurementTestData.OtherTenant));
        services.AddScoped<IReorderAlertService, ReorderAlertService>();
        services.AddSingleton<ISlaNotifications, SilentNotifications>();
        using var provider = services.BuildServiceProvider();

        var worker = new ReorderAlertSweepWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor(),
            NullLogger<ReorderAlertSweepWorker>.Instance);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            worker.SweepTenantAsync(Tenant, CancellationToken.None));

        Assert.Contains("Tenant scope is mandatory", refusal.Message);
    }

    /// <summary>An <see cref="ISlaNotifications"/> that never sends. The scope test must fail before
    /// any transport is reached, so a fake that recorded sends would be measuring the wrong thing.</summary>
    private sealed class SilentNotifications : ISlaNotifications
    {
        public Task<SlaSendResult> SendDeadlineAlertAsync(string toEmail, string? toName, string level,
            string entityLabel, string headline, string detail, long businessUnitId, CancellationToken ct = default)
            => throw new InvalidOperationException("The sweep must refuse before it reaches a transport.");

        public Task<SlaSendResult> SendStaleQuotesDigestAsync(string toEmail, string? toName,
            IReadOnlyList<StaleQuoteDigestLine> lines, long businessUnitId, CancellationToken ct = default)
            => throw new InvalidOperationException("The sweep must refuse before it reaches a transport.");
    }
}

/// <summary>
/// A tenant with one stocked product in one warehouse, and the levers a reorder test needs: on-hand,
/// the reorder point, the two levels, a reservation and an inbound order.
/// </summary>
internal sealed class ReorderScenario : IDisposable
{
    private readonly TestDb _database = new();
    private int _sequence;

    public ReorderScenario()
    {
        using var seed = _database.ContextFor(null);
        seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
        ProcurementTestData.SeedGraph(seed, ProcurementTestData.Tenant, 0);
        seed.SaveChanges();
    }

    public ErpRfqAutomationContext Context(long? tenant = null)
        => _database.ContextFor(tenant ?? ProcurementTestData.Tenant);

    public async Task SetOnHandAsync(decimal onHand)
    {
        await using var context = Context();
        var row = await context.Set<InventoryRow>().SingleAsync(x => x.Id == ProcurementTestData.Inventory);
        row.QtyOnHand = onHand;
        await context.SaveChangesAsync();
    }

    public async Task SetReorderPointAsync(decimal reorderPoint)
    {
        await using var context = Context();
        var row = await context.Set<InventoryRow>().SingleAsync(x => x.Id == ProcurementTestData.Inventory);
        row.ReorderPoint = reorderPoint;
        await context.SaveChangesAsync();
    }

    /// <summary>Through the sanctioned writer, so the test exercises the path a user takes.</summary>
    public async Task SetLevelsAsync(decimal? minimum, decimal? maximum)
    {
        await using var context = Context();
        await new StockLedgerService(context).SetStockLevelsAsync(
            ProcurementTestData.Tenant, ProcurementTestData.Product, ProcurementTestData.Warehouse,
            minimum, maximum, "qa");
    }

    public async Task ReserveAsync(decimal quantity)
    {
        await using var context = Context();
        await new InventoryAvailabilityService(context).ReserveAsync(
            ProcurementTestData.Tenant, ProcurementTestData.Inventory, quantity,
            $"hold-{++_sequence}", actor: "qa");
    }

    public async Task AddIncomingAsync(decimal quantity)
    {
        await using var context = Context();
        context.IncomingInventory.Add(new IncomingInventory
        {
            BusinessUnitId = ProcurementTestData.Tenant,
            ProductId = ProcurementTestData.Product,
            WarehouseId = ProcurementTestData.Warehouse,
            InventoryId = ProcurementTestData.Inventory,
            OrderedQuantity = quantity,
            ReceivedQuantity = 0m,
            Status = IncomingInventoryStatus.Ordered,
            SourceType = "SupplierPurchaseOrder",
            SourceId = $"PO-{++_sequence}",
            ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(14),
        });
        await context.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<ReorderCondition>> EvaluateAsync()
    {
        await using var context = Context();
        return await new ReorderAlertService(context).EvaluateAsync(ProcurementTestData.Tenant);
    }

    public async Task<IReadOnlyList<InventoryReorderAlert>> ReconcileAsync()
    {
        await using var context = Context();
        var (_, raised) = await new ReorderAlertService(context).ReconcileAsync(ProcurementTestData.Tenant);
        return raised;
    }

    public void Dispose() => _database.Dispose();
}
