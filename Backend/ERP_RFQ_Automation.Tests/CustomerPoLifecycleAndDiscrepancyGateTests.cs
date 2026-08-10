using ERP_RFQ_Automation.OrderToCash;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-COM-02 and FR-COM-04. The two halves of "there is a way back, and there is a way to be
/// stopped" on a captured customer purchase order.
///
/// The defects
/// -----------
/// <b>No way back.</b> <c>ICustomerAwardApplicationService</c> had no update, confirm or cancel for
/// the purchase order, while <c>OrderToCashCommands</c> declared all three. So
/// <c>CustomerPurchaseOrder.CancellationReason</c> had no writer, its CHECK constraint permitted a
/// value only in the <c>CANCELLED</c> status, and <c>DRAFT</c>/<c>CLOSED</c>/<c>CANCELLED</c> were
/// unreachable — the guard refusing an award on a cancelled PO was reading a state no row could
/// hold. An operator who mis-keyed the buyer's unit price had the workspace confirm the award AND
/// convert it to a sales order in one click, and <c>CancelAwardAsync</c> then refused because the
/// award was <c>ORDERED</c>. Neither document had a path back.
///
/// <b>A flag that blocked nothing.</b> <c>LineDifferences</c> was invoked only by the two read
/// projections. Nothing in create, confirm or convert consulted it, and with one-click
/// confirm-and-convert the review screen was reachable only AFTER the sales order existed.
///
/// What these tests prove
/// ----------------------
/// That the cancellation actually writes the state and the reason and is refused when it would
/// abandon a live commitment; and that the discrepancy report now REFUSES a sales order rather than
/// describing one that was already raised. Remove either wiring and the assertions fail — none of
/// them merely observes a value round-tripping.
/// </summary>
public sealed class CustomerPoLifecycleAndDiscrepancyGateTests
{
    private const long PolicyActor = 7_702;
    private const decimal Quantity = 10m;

    // ---------------------------------------------------------------------------------------
    // FR-COM-02. Cancelling the customer purchase order.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Cancelling_a_customer_po_records_the_state_and_the_reason()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("cancel-po", Quantity);

        var cancelled = await fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId,
            purchaseOrder.Id, "cancel-po-key", "corr-cancel-po",
            new(purchaseOrder.Version, "  Buyer withdrew the order before we shipped.  "), "reviewer@example.com");

        Assert.Equal(CustomerPurchaseOrderStatuses.Cancelled, cancelled.Status);
        Assert.Equal(purchaseOrder.Version + 1, cancelled.Version);
        fixture.Context.ChangeTracker.Clear();
        var persisted = await fixture.Context.CustomerPurchaseOrders.SingleAsync(x => x.Id == purchaseOrder.Id);
        // The reason is STORED, not merely logged: it is the other half of
        // CK_CustomerPurchaseOrders_Cancellation, and it is what a reviewer reads when the same PO
        // number arrives again.
        Assert.Equal("Buyer withdrew the order before we shipped.", persisted.CancellationReason);
        Assert.Equal("reviewer@example.com", persisted.ModifiedBy);
        var audit = await fixture.Context.OrderToCashAuditEvents.SingleAsync(x =>
            x.CommandType == OrderToCashCommands.CancelPurchaseOrder && x.AggregateId == purchaseOrder.Id);
        Assert.Equal(CustomerPurchaseOrderStatuses.Cancelled, audit.NewState);
        Assert.Equal("Buyer withdrew the order before we shipped.", audit.Reason);
        Assert.Equal("reviewer@example.com", audit.Actor);
    }

    [Fact]
    public async Task Cancelling_a_customer_po_without_a_reason_is_refused()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("cancel-po-no-reason", Quantity);

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.CancelPurchaseOrderAsync(
            fixture.BusinessUnitId, purchaseOrder.Id, "cancel-blank", "corr-cancel-blank",
            new(purchaseOrder.Version, "   "), "tests"));
    }

    /// <summary>
    /// An award is a commitment to the customer, with its own quantity ledger and its own reason.
    /// Withdrawing the paperwork it was read from must not silently release it, so the order of the
    /// two documents is enforced and the award standing in the way is named.
    /// </summary>
    [Fact]
    public async Task Customer_po_cannot_be_cancelled_while_an_award_is_live_and_can_be_once_it_is_not()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("cancel-po-live", Quantity);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "cancel-po-live-award", Quantity);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "cancel-po-live-confirm", "corr-cancel-po-live-confirm", new(award.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var current = await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
            .SingleAsync(x => x.Id == purchaseOrder.Id);
        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId, purchaseOrder.Id,
                "cancel-po-blocked", "corr-cancel-po-blocked",
                new(current.Version, "Mis-keyed the buyer's price."), "tests"));
        Assert.Contains(award.AwardNumber, error.Message, StringComparison.Ordinal);

        await fixture.Service.CancelAwardAsync(fixture.BusinessUnitId, award.Id, "cancel-po-live-award-cancel",
            "corr-cancel-po-live-award-cancel", new(confirmed.Version, "Mis-keyed the buyer's price."), "tests");
        fixture.Context.ChangeTracker.Clear();
        var afterAwardCancel = await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
            .SingleAsync(x => x.Id == purchaseOrder.Id);

        var cancelled = await fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId, purchaseOrder.Id,
            "cancel-po-after", "corr-cancel-po-after",
            new(afterAwardCancel.Version, "Recapturing the PO with the buyer's real price."), "tests");

        Assert.Equal(CustomerPurchaseOrderStatuses.Cancelled, cancelled.Status);
    }

    /// <summary>
    /// The guard that was reading a state no row could hold. With <c>CANCELLED</c> reachable, a
    /// withdrawn purchase order refuses new awards — which is the whole reason the guard exists.
    /// </summary>
    [Fact]
    public async Task Cancelled_customer_po_refuses_a_new_award()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("cancel-po-guard", Quantity);
        var cancelled = await fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId, purchaseOrder.Id,
            "cancel-po-guard-key", "corr-cancel-po-guard", new(purchaseOrder.Version, "Duplicate of PO-1189."),
            "tests");

        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.CreateAwardAsync(purchaseOrder with { Version = cancelled.Version }, "cancel-po-guard-award",
                Quantity));

        Assert.Contains("cancelled", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cancelling_a_customer_po_twice_is_refused_and_the_command_replays()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("cancel-po-twice", Quantity);

        var first = await fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId, purchaseOrder.Id,
            "cancel-twice-key", "corr-cancel-twice", new(purchaseOrder.Version, "Buyer withdrew."), "tests");
        var replay = await fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId, purchaseOrder.Id,
            "cancel-twice-key", "corr-cancel-twice-again", new(purchaseOrder.Version, "Buyer withdrew."), "tests");

        Assert.Equal(first.Version, replay.Version);
        await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.CancelPurchaseOrderAsync(fixture.BusinessUnitId, purchaseOrder.Id,
                "cancel-twice-second", "corr-cancel-twice-second",
                new(first.Version, "Buyer withdrew again."), "tests"));
    }

    // ---------------------------------------------------------------------------------------
    // FR-COM-04. The discrepancy gate on order conversion.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The wiring contract's rule, applied: any control a field feeds must actually BLOCK
    /// something. Remove the gate from <c>ConvertToOrderAsync</c> and this test raises a sales
    /// order priced from a quotation the buyer's PO disagrees with, and passes silently.
    /// </summary>
    [Fact]
    public async Task Price_discrepancy_refuses_to_become_a_sales_order()
    {
        using var fixture = new CustomerAwardTestFixture();
        var confirmed = await ConfirmAwardAtPoPriceAsync(fixture, "gate-price", 130m);

        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, confirmed.Award.Id, "gate-price-convert",
                "corr-gate-price-convert", new(confirmed.Award.Version), "tests"));

        Assert.Contains(CustomerPurchaseOrderDifferences.PriceDiscrepancy, error.Message, StringComparison.Ordinal);
        fixture.Context.ChangeTracker.Clear();
        Assert.Equal(0, await fixture.Context.Orders.CountAsync(x => x.CustomerAwardId == confirmed.Award.Id));
    }

    /// <summary>
    /// The difference the tenant's tolerance absorbs is not a difference, so the gate does not stop
    /// it. The gate reads the SAME policy the report does — hardcode a tolerance in either and the
    /// two stop agreeing.
    /// </summary>
    [Fact]
    public async Task Price_inside_the_tenant_tolerance_passes_the_gate_untouched()
    {
        using var fixture = new CustomerAwardTestFixture();
        await SetPriceTolerancePercentAsync(fixture, "gate-tolerant", 5m);
        var confirmed = await ConfirmAwardAtPoPriceAsync(fixture, "gate-tolerant", 103m);

        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, confirmed.Award.Id,
            "gate-tolerant-convert", "corr-gate-tolerant-convert", new(confirmed.Award.Version), "tests");

        Assert.True(order.Id > 0);
    }

    /// <summary>
    /// An operator must be able to accept a difference DELIBERATELY. The acceptance costs a reason,
    /// names its author, and lands in the same governance ledger as every other command.
    /// </summary>
    [Fact]
    public async Task Accepting_the_difference_with_a_reason_lets_the_order_through_and_is_attributable()
    {
        using var fixture = new CustomerAwardTestFixture();
        var confirmed = await ConfirmAwardAtPoPriceAsync(fixture, "gate-accept", 130m);

        var acceptance = await fixture.Service.AcceptPurchaseOrderDifferencesAsync(fixture.BusinessUnitId,
            confirmed.PurchaseOrder.Id, "gate-accept-key", "corr-gate-accept",
            new(await CurrentPoVersionAsync(fixture, confirmed.PurchaseOrder.Id), confirmed.Award.Id,
                "Buyer's price supersedes ours; renegotiated on 12 Aug."),
            "reviewer@example.com");
        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, confirmed.Award.Id,
            "gate-accept-convert", "corr-gate-accept-convert", new(confirmed.Award.Version), "tests");

        Assert.True(order.Id > 0);
        Assert.Contains(acceptance.AcceptedDifferences,
            key => key.EndsWith($":{CustomerPurchaseOrderDifferences.PriceDiscrepancy}", StringComparison.Ordinal));
        fixture.Context.ChangeTracker.Clear();
        var audit = await fixture.Context.OrderToCashAuditEvents.SingleAsync(x =>
            x.CommandType == OrderToCashCommands.AcceptPurchaseOrderDifferences);
        Assert.Equal("reviewer@example.com", audit.Actor);
        Assert.Equal("Buyer's price supersedes ours; renegotiated on 12 Aug.", audit.Reason);
    }

    /// <summary>
    /// Validation that rejects the wrong values, not merely the impossible ones. An acceptance of
    /// nothing would sit in the ledger looking like someone had reviewed a difference that was
    /// never there — false assurance, which is worse than no record.
    /// </summary>
    [Fact]
    public async Task Accepting_differences_on_a_clean_award_is_refused()
    {
        using var fixture = new CustomerAwardTestFixture();
        var confirmed = await ConfirmAwardAtPoPriceAsync(fixture, "gate-clean", 100m);

        var poVersion = await CurrentPoVersionAsync(fixture, confirmed.PurchaseOrder.Id);
        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.AcceptPurchaseOrderDifferencesAsync(fixture.BusinessUnitId,
                confirmed.PurchaseOrder.Id, "gate-clean-key", "corr-gate-clean",
                new(poVersion, confirmed.Award.Id, "Looks fine to me."), "tests"));

        Assert.Contains("no outstanding", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Accepting_differences_without_a_reason_is_refused()
    {
        using var fixture = new CustomerAwardTestFixture();
        var confirmed = await ConfirmAwardAtPoPriceAsync(fixture, "gate-noreason", 130m);

        var poVersion = await CurrentPoVersionAsync(fixture, confirmed.PurchaseOrder.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.AcceptPurchaseOrderDifferencesAsync(
            fixture.BusinessUnitId, confirmed.PurchaseOrder.Id, "gate-noreason-key", "corr-gate-noreason",
            new(poVersion, confirmed.Award.Id, "  "), "tests"));
    }

    /// <summary>
    /// The acceptance names the buyer LINE as well as the difference, so signing off one line's
    /// price gap can never license another award's — including a second award raised against the
    /// same purchase order.
    /// </summary>
    [Fact]
    public async Task Acceptance_on_one_award_does_not_unblock_another_award_on_the_same_po()
    {
        using var fixture = new CustomerAwardTestFixture();
        var command = fixture.PurchaseOrderCommand("PO-gate-split", 10m) with
        {
            Lines = [new("1", null, "Buyer widget", 10m, null, 130m, 1_300m)]
        };
        var purchaseOrder = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId,
            "gate-split-po", "corr-gate-split-po", command, "tests");
        var first = await fixture.CreateAwardAsync(purchaseOrder, "gate-split-award-1", 4m);
        var firstConfirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, first.Id,
            "gate-split-confirm-1", "corr-gate-split-confirm-1", new(first.Version), "tests");
        await fixture.Service.AcceptPurchaseOrderDifferencesAsync(fixture.BusinessUnitId, purchaseOrder.Id,
            "gate-split-accept-1", "corr-gate-split-accept-1",
            new(await CurrentPoVersionAsync(fixture, purchaseOrder.Id), firstConfirmed.Id,
                "Agreed with the buyer."), "tests");

        fixture.Context.ChangeTracker.Clear();
        var current = await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
            .SingleAsync(x => x.Id == purchaseOrder.Id);
        var second = await fixture.CreateAwardAsync(purchaseOrder with { Version = current.Version },
            "gate-split-award-2", 6m);
        var secondConfirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, second.Id,
            "gate-split-confirm-2", "corr-gate-split-confirm-2", new(second.Version), "tests");

        await Assert.ThrowsAsync<CustomerAwardConflictException>(() => fixture.Service.ConvertToOrderAsync(
            fixture.BusinessUnitId, second.Id, "gate-split-convert-2", "corr-gate-split-convert-2",
            new(secondConfirmed.Version), "tests"));
    }

    /// <summary>
    /// The reviewer meets the gate BEFORE pressing the button. The match view reports the same
    /// blocking set convert-to-order recomputes, so the screen and the server cannot disagree about
    /// whether an order can be raised.
    /// </summary>
    [Fact]
    public async Task Match_view_reports_the_same_blocking_set_the_conversion_gate_applies()
    {
        using var fixture = new CustomerAwardTestFixture();
        var confirmed = await ConfirmAwardAtPoPriceAsync(fixture, "gate-view", 130m);

        fixture.Context.ChangeTracker.Clear();
        var blocked = await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId,
            confirmed.PurchaseOrder.Id);
        Assert.NotNull(blocked.BlockingDifferences);
        Assert.Contains(blocked.BlockingDifferences!,
            key => key.EndsWith($":{CustomerPurchaseOrderDifferences.PriceDiscrepancy}", StringComparison.Ordinal));
        Assert.Equal(confirmed.Award.Version, blocked.AwardVersion);

        await fixture.Service.AcceptPurchaseOrderDifferencesAsync(fixture.BusinessUnitId,
            confirmed.PurchaseOrder.Id, "gate-view-accept", "corr-gate-view-accept",
            new(await CurrentPoVersionAsync(fixture, confirmed.PurchaseOrder.Id), confirmed.Award.Id,
                "Buyer's price agreed."), "tests");
        fixture.Context.ChangeTracker.Clear();
        var cleared = await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId,
            confirmed.PurchaseOrder.Id);

        Assert.Empty(cleared.BlockingDifferences!);
        Assert.NotEmpty(cleared.AcceptedDifferences!);
    }

    /// <summary>
    /// A buyer PO whose document states no price at all is not agreement — it is a comparison
    /// nobody could make, and the sales order would still be raised at OUR price.
    /// </summary>
    [Fact]
    public async Task Customer_po_with_no_stated_price_refuses_to_become_a_sales_order()
    {
        using var fixture = new CustomerAwardTestFixture();
        var command = fixture.PurchaseOrderCommand("PO-gate-noprice", 10m) with
        {
            Lines = [new("1", null, "Buyer widget", 10m, null, null, null)]
        };
        var purchaseOrder = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId,
            "gate-noprice-po", "corr-gate-noprice-po", command, "tests");
        var award = await fixture.CreateAwardAsync(purchaseOrder, "gate-noprice-award", 10m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "gate-noprice-confirm", "corr-gate-noprice-confirm", new(award.Version), "tests");

        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id, "gate-noprice-convert",
                "corr-gate-noprice-convert", new(confirmed.Version), "tests"));

        Assert.Contains(CustomerPurchaseOrderDifferences.PoPriceNotProvided, error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A partial award is the operator's own decision, typed on the capture screen. Blocking on it
    /// would ask somebody to accept what they had just chosen, so it stays a reported difference
    /// and not a gate.
    /// </summary>
    [Fact]
    public async Task Partial_award_is_reported_but_does_not_block_the_order()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("gate-partial", Quantity);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "gate-partial-award", 4m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "gate-partial-confirm", "corr-gate-partial-confirm", new(award.Version), "tests");

        var order = await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id,
            "gate-partial-convert", "corr-gate-partial-convert", new(confirmed.Version), "tests");

        Assert.True(order.Id > 0);
        fixture.Context.ChangeTracker.Clear();
        var match = await fixture.Service.GetPurchaseOrderMatchAsync(fixture.BusinessUnitId, purchaseOrder.Id);
        Assert.Contains(CustomerPurchaseOrderDifferences.PartialQuoteAward,
            Assert.Single(match.Lines).Differences);
    }

    [Fact]
    public async Task Differences_cannot_be_accepted_after_the_award_has_become_a_sales_order()
    {
        using var fixture = new CustomerAwardTestFixture();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("gate-late", Quantity);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "gate-late-award", Quantity);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "gate-late-confirm", "corr-gate-late-confirm", new(award.Version), "tests");
        await fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id, "gate-late-convert",
            "corr-gate-late-convert", new(confirmed.Version), "tests");

        fixture.Context.ChangeTracker.Clear();
        var ordered = await fixture.Context.CustomerAwards.AsNoTracking().SingleAsync(x => x.Id == award.Id);
        var poVersion = await CurrentPoVersionAsync(fixture, purchaseOrder.Id);
        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.AcceptPurchaseOrderDifferencesAsync(fixture.BusinessUnitId, purchaseOrder.Id,
                "gate-late-accept", "corr-gate-late-accept", new(poVersion, ordered.Id, "Too late."), "tests"));

        Assert.Contains("sales order", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The purchase order's version as it stands now, which the accept command requires.</summary>
    private static async Task<long> CurrentPoVersionAsync(CustomerAwardTestFixture fixture, long purchaseOrderId)
        => await fixture.Context.CustomerPurchaseOrders.AsNoTracking()
            .Where(x => x.Id == purchaseOrderId).Select(x => x.Version).SingleAsync();

    private static async Task SetPriceTolerancePercentAsync(CustomerAwardTestFixture fixture, string key,
        decimal percent)
    {
        await new CommercialMatchingPolicyService(fixture.Context).UpdateAsync(
            fixture.BusinessUnitId, PolicyActor, "policy@example.com", key,
            new UpdateCommercialMatchingPolicyCommand(
                SupplierInputTaxRecoverablePercent: null,
                OutputTaxRatePercent: null,
                ClearOutputTaxRate: false,
                PriceTolerancePercent: percent,
                PriceToleranceMinimumAmount: null,
                QuantityTolerancePercent: null,
                Reason: "Buyer ERP rounds unit prices; absorb the rounding."));
        fixture.Context.ChangeTracker.Clear();
    }

    /// <summary>
    /// Captures a PO at <paramref name="poUnitPrice"/> against the seeded 100.00 quotation and
    /// confirms a full award on it. Nothing here is seeded from our quote line.
    /// </summary>
    private static async Task<(CustomerPurchaseOrderView PurchaseOrder, CustomerAwardView Award)>
        ConfirmAwardAtPoPriceAsync(CustomerAwardTestFixture fixture, string key, decimal poUnitPrice)
    {
        var command = fixture.PurchaseOrderCommand($"PO-{key}", Quantity) with
        {
            Lines = [new("1", null, "Buyer widget", Quantity, null, poUnitPrice, Quantity * poUnitPrice)]
        };
        var purchaseOrder = await fixture.Service.CreatePurchaseOrderAsync(fixture.BusinessUnitId,
            $"{key}-po", $"corr-{key}-po", command, "tests");
        var award = await fixture.CreateAwardAsync(purchaseOrder, $"{key}-award", Quantity);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            $"{key}-confirm", $"corr-{key}-confirm", new(award.Version), "tests");
        fixture.Context.ChangeTracker.Clear();
        return (purchaseOrder, confirmed);
    }
}
