using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Which supplier purchase orders count as supply already on order — asked in exactly one place,
/// and answered the same way by the screen a buyer decides from and the command path that enforces
/// the decision.
///
/// <para>Two defects made that untrue in opposite directions. The command path
/// (<c>GetNetSourcingRequirementAsync</c>) carried a hand-written ISSUED-or-PARTIALLY_RECEIVED list
/// that was never widened when ACKNOWLEDGED was added, so a supplier <b>accepting</b> an order
/// deleted its own coverage and the same material was sourced, awarded and bought a second time —
/// one user, no concurrency, on the ordinary happy path. The screen
/// (<c>GetWorkbenchAsync</c>) had no status predicate at all, so a DRAFT nobody had released and a
/// CANCELLED order whose awards had already been reverted both still read as incoming: the buyer
/// saw shortfall 0 and never re-sourced the order they had just cancelled.</para>
///
/// <para>The fixture's RFQ line asks for 10 with 2 on hand, so a purchase order for 8 covers it
/// exactly. Every assertion below turns on whether that 8 is counted.</para>
/// </summary>
public sealed class Gate4SupplierPurchaseOrderCommittedSupplyTests
{
    private const decimal OrderedQuantity = 8m;

    /// <summary>
    /// Defect 3. Reverting the set membership makes the acknowledged order invisible to the
    /// requirement calculation, the refusal below does not fire, and a second sourcing case is
    /// raised for material already on order.
    /// </summary>
    [Fact]
    public async Task An_acknowledged_order_still_covers_its_demand_and_raises_no_second_sourcing_case()
    {
        using var fixture = new ProcurementScenario();
        var released = await fixture.CreatePurchaseOrderAsync("committed-ack", OrderedQuantity);
        var sourcingCasesBefore = await CountSourcingCasesAsync(fixture);

        var acknowledged = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            new AcknowledgeSupplierPurchaseOrderCommand(fixture.BusinessUnitId, released.Id,
                released.Version, SupplierAcknowledgementStatuses.Accepted, "Supplier contact",
                "committed-ack-record", "buyer@tenant.test", "corr-committed-ack")));
        Assert.Equal(SupplierPurchaseOrderStatuses.Acknowledged, acknowledged.Status);

        var refusal = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(
            service => service.CreateSolicitationAsync(fixture.Solicitation("committed-ack-resource"))));
        Assert.Contains("fully covered", refusal.Message, StringComparison.OrdinalIgnoreCase);

        // The refusal is the control; the count is the consequence it exists to prevent.
        Assert.Equal(sourcingCasesBefore, await CountSourcingCasesAsync(fixture));
        // And the screen agrees with it, rather than the two disagreeing about the same line.
        Assert.Equal(0m, await ShortfallAsync(fixture));
    }

    /// <summary>
    /// Defect 4, cancellation half. Cancelling reverts the awards precisely so the line goes back to
    /// sourcing; the screen has to say so, or nobody acts on it.
    /// </summary>
    [Fact]
    public async Task A_cancelled_order_stops_covering_its_demand_on_the_screen_and_in_the_command_path()
    {
        using var fixture = new ProcurementScenario();
        var released = await fixture.CreatePurchaseOrderAsync("committed-cancel", OrderedQuantity);
        Assert.Equal(0m, await ShortfallAsync(fixture));

        var cancelled = await fixture.Execute(service => service.CancelPurchaseOrderAsync(
            fixture.Cancel(released.Id, "committed-cancel-cmd", "Supplier withdrew", released.Version)));
        Assert.Equal(SupplierPurchaseOrderStatuses.Cancelled, cancelled.Status);

        var line = await LineAsync(fixture);
        Assert.Equal(OrderedQuantity, line.ShortfallQuantity);
        Assert.NotEqual("COVERED", line.Resolution);

        // The command path must agree: the line is sourceable again rather than "fully covered".
        var resourced = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("committed-cancel-resource")));
        Assert.True(resourced.Id > 0);
    }

    /// <summary>
    /// Defect 4, draft half. The command path has always treated a draft as no commitment; the
    /// screen counted it, so the two disagreed about the same line in the other direction.
    /// </summary>
    [Fact]
    public async Task A_draft_order_is_not_counted_as_incoming_supply_by_the_screen()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("committed-draft", OrderedQuantity);
        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "committed-draft-po")));
        Assert.Equal(SupplierPurchaseOrderStatuses.Draft, draft.Status);

        var line = await LineAsync(fixture);

        Assert.Equal(OrderedQuantity, line.ShortfallQuantity);
        Assert.NotEqual("COVERED", line.Resolution);
    }

    /// <summary>
    /// The set itself. Dropping any member fails here by name, rather than as a mysterious duplicate
    /// purchase order somewhere downstream.
    ///
    /// <para>IN_PRODUCTION and SHIPPED are asserted present even though nothing assigns them yet.
    /// They are the strongest commitments on the ladder, and the day inbound logistics starts
    /// writing them an order in production would otherwise silently stop covering its demand — the
    /// ACKNOWLEDGED double-buy again, one status further along.</para>
    /// </summary>
    [Fact]
    public void CommittedSupply_names_every_state_in_which_stock_is_still_expected_to_arrive()
    {
        foreach (var committed in new[]
                 {
                     SupplierPurchaseOrderStatuses.Sent,
                     SupplierPurchaseOrderStatuses.Issued,
                     SupplierPurchaseOrderStatuses.Acknowledged,
                     SupplierPurchaseOrderStatuses.InProduction,
                     SupplierPurchaseOrderStatuses.Shipped,
                     SupplierPurchaseOrderStatuses.PartiallyReceived
                 })
            Assert.Contains(committed, SupplierPurchaseOrderStatuses.CommittedSupply);

        foreach (var notCommitted in new[]
                 {
                     // Never seen by the supplier: an intention, not supply.
                     SupplierPurchaseOrderStatuses.Draft,
                     SupplierPurchaseOrderStatuses.Approved,
                     // Nothing is still expected to arrive.
                     SupplierPurchaseOrderStatuses.Received,
                     SupplierPurchaseOrderStatuses.Closed,
                     SupplierPurchaseOrderStatuses.Cancelled
                 })
            Assert.DoesNotContain(notCommitted, SupplierPurchaseOrderStatuses.CommittedSupply);
    }

    private static async Task<SourcingLineView> LineAsync(ProcurementScenario fixture)
    {
        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        return workbench.Lines.Single(x => x.Id == fixture.RfqItemId);
    }

    private static async Task<decimal> ShortfallAsync(ProcurementScenario fixture)
        => (await LineAsync(fixture)).ShortfallQuantity;

    private static async Task<int> CountSourcingCasesAsync(ProcurementScenario fixture)
    {
        await using var context = fixture.Context();
        return await context.SourcingCases.CountAsync(x => x.RfqId == fixture.RfqId);
    }
}
