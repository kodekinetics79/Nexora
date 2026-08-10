using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-COM-04, wiring contract failure #12: <b>a number compared without its unit</b>.
///
/// The defect
/// ----------
/// <c>CustomerPurchaseOrderLine.UomId</c> existed on the entity and in the create command, and
/// nothing wrote it: the capture screen omitted it from every payload and the document extractor
/// read the buyer's unit word, displayed it, and discarded it. <c>LineDifferences</c> then compared
/// quantities and prices as bare decimals. A buyer PO for <b>"10 boxes"</b> against our quote of
/// <b>"10 each"</b> classified as <c>EXACT_MATCH</c>, raised no discrepancy, and produced a sales
/// order for 10 EACH at our per-each price — because <c>ConvertToOrderAsync</c> ended in
/// <c>UomId = poLine.UomId ?? Rfqitem.UomId ?? Product.UomId</c>, a fallback chain whose first link
/// was always null and which therefore always answered with OUR unit.
///
/// What these tests prove
/// ----------------------
/// Not that <c>UomId</c> round-trips. They assert the DEPENDENCE: the same two documents get a
/// different answer from the discrepancy report, from the conversion gate, and from the sales order
/// line, according to the unit the buyer stated.
/// </summary>
public sealed class CustomerPoUnitOfMeasureTests
{
    private const decimal Quantity = 10m;

    /// <summary>
    /// The headline case. Delete the UOM branch in <c>LineDifferences</c> and this fails on the
    /// first assertion, because everything else about the two lines agrees exactly.
    /// </summary>
    [Fact]
    public async Task Buyer_ordering_in_a_unit_we_did_not_quote_is_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();

        var match = await MatchAsync(fixture, "boxes", CustomerAwardTestFixture.BoxUomId);

        var line = Assert.Single(match.Lines);
        Assert.Contains(CustomerPurchaseOrderDifferences.UomDiscrepancy, line.Differences);
        Assert.NotEqual("EXACT_MATCH", line.MatchStatus);
        Assert.Equal(1, match.Header.DiscrepancyCount);
        Assert.Equal("ACCEPTED_WITH_DIFFERENCES", match.Header.MatchOutcome);
    }

    [Fact]
    public async Task Buyer_ordering_in_the_unit_we_quoted_is_not_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();

        var match = await MatchAsync(fixture, "each", CustomerAwardTestFixture.EachUomId);

        var line = Assert.Single(match.Lines);
        Assert.DoesNotContain(CustomerPurchaseOrderDifferences.UomDiscrepancy, line.Differences);
        Assert.Equal("EXACT_MATCH", line.MatchStatus);
    }

    /// <summary>
    /// Silence is not disagreement. A purchase order that names no unit gives nothing to contradict
    /// our quotation with — the same rule the part-number comparison already applies — so it must
    /// not manufacture a difference out of the buyer's omission.
    /// </summary>
    [Fact]
    public async Task Buyer_stating_no_unit_at_all_is_not_a_discrepancy()
    {
        using var fixture = new CustomerAwardTestFixture();

        var match = await MatchAsync(fixture, "silent", null);

        Assert.DoesNotContain(CustomerPurchaseOrderDifferences.UomDiscrepancy,
            Assert.Single(match.Lines).Differences);
    }

    /// <summary>
    /// The reviewer has to be able to SEE what they are comparing. The match view carried bare
    /// numbers, so "10" against "10" read as agreement whichever unit either side meant.
    /// </summary>
    [Fact]
    public async Task Match_view_states_the_unit_on_both_sides_of_the_comparison()
    {
        using var fixture = new CustomerAwardTestFixture();

        var match = await MatchAsync(fixture, "units-shown", CustomerAwardTestFixture.BoxUomId);

        var line = Assert.Single(match.Lines);
        Assert.Equal(CustomerAwardTestFixture.BoxUomId, line.PurchaseOrderUomId);
        Assert.Equal("BOX", line.PurchaseOrderUomCode);
        Assert.Equal(CustomerAwardTestFixture.EachUomId, line.QuotedUomId);
        Assert.Equal("EA", line.QuotedUomCode);
    }

    /// <summary>
    /// A purchase order that stated no unit is rendered as a GAP, never as a blank the reader fills
    /// in from the quoted column beside it.
    /// </summary>
    [Fact]
    public async Task Match_view_shows_an_unstated_buyer_unit_as_absent_rather_than_borrowing_ours()
    {
        using var fixture = new CustomerAwardTestFixture();

        var match = await MatchAsync(fixture, "units-gap", null);

        var line = Assert.Single(match.Lines);
        Assert.Null(line.PurchaseOrderUomId);
        Assert.Null(line.PurchaseOrderUomCode);
        Assert.Equal("EA", line.QuotedUomCode);
    }

    /// <summary>
    /// The end of the story the <c>??</c> chain used to hide. The buyer ordered boxes, somebody
    /// accepted that difference deliberately, and the sales order is raised in BOXES — not silently
    /// converted to the unit we happened to quote in.
    /// </summary>
    [Fact]
    public async Task Sales_order_line_carries_the_buyers_unit_when_their_po_states_one()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("uom-order", Quantity,
            CustomerAwardTestFixture.BoxUomId);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "uom-order-award", Quantity);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "uom-order-confirm", "corr-uom-order-confirm", new(award.Version), "tests");
        var poVersion = await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
            .Where(x => x.Id == purchaseOrder.Id).Select(x => x.Version).SingleAsync();
        await fixture.Service.AcceptPurchaseOrderDifferencesAsync(fixture.BusinessUnitId, purchaseOrder.Id,
            "uom-order-accept", "corr-uom-order-accept",
            new(poVersion, award.Id, "Buyer's box is our each; confirmed with the buyer by phone."), "tests");

        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "uom-order-convert", "corr-uom-order-convert", new(confirmed.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var item = Assert.Single((await fixture.Context.Orders.Include(x => x.OrderItems)
            .SingleAsync(x => x.Id == order.Id)).OrderItems);
        Assert.Equal(CustomerAwardTestFixture.BoxUomId, item.UomId);
    }

    /// <summary>
    /// And the other branch, stated rather than inherited from a chain: when the buyer's document
    /// names no unit there is nothing of theirs to carry, so the order is raised in the unit we
    /// quoted in. This is a two-branch decision now, not a three-link fallback.
    /// </summary>
    [Fact]
    public async Task Sales_order_line_falls_back_to_the_quoted_unit_only_when_the_po_states_none()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("uom-order-silent", Quantity);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "uom-silent-award", Quantity);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "uom-silent-confirm", "corr-uom-silent-confirm", new(award.Version), "tests");

        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "uom-silent-convert", "corr-uom-silent-convert", new(confirmed.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var item = Assert.Single((await fixture.Context.Orders.Include(x => x.OrderItems)
            .SingleAsync(x => x.Id == order.Id)).OrderItems);
        Assert.Equal(CustomerAwardTestFixture.EachUomId, item.UomId);
    }

    /// <summary>
    /// A unit belonging to another tenant is not a unit of measure, it is a cross-tenant reference.
    /// </summary>
    [Fact]
    public async Task Purchase_order_line_cannot_name_another_tenants_unit()
    {
        using var fixture = new CustomerAwardTestFixture();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => fixture.CreatePurchaseOrderAsync(
            "uom-foreign", Quantity, CustomerAwardTestFixture.BoxUomId + 9_000));

        Assert.Contains("units of measure", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Captures a PO in <paramref name="uomId"/>, awards it, and returns the reviewer's view.</summary>
    private static async Task<ClientPurchaseOrderMatchView> MatchAsync(CustomerAwardTestFixture fixture,
        string key, int? uomId)
    {
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync($"uom-{key}", Quantity, uomId);
        var award = await fixture.CreateAwardAsync(purchaseOrder, $"uom-award-{key}", Quantity);
        await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id, $"uom-confirm-{key}",
            $"corr-uom-confirm-{key}", new(award.Version), "tests");
        fixture.Context.ChangeTracker.Clear();
        return await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId, purchaseOrder.Id);
    }
}
