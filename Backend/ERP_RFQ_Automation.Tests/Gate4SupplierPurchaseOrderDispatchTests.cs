using ERP_RFQ_Automation.InboundLogistics;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-SPO-04. The split of ISSUED into APPROVED + SENT, in data rather than only in the constant
/// list.
///
/// <para>SENT was declared, sat in the database CHECK constraint, and was read by four separate
/// guard sets — and nothing assigned it. Release wrote the legacy conflated ISSUED, so the split
/// existed everywhere except in the rows, and every SLA test seeded SENT by hand against a state
/// production could not produce.</para>
///
/// <para><b>The trap these tests exist to nail shut:</b> OpenForReceipt did not contain SENT. Adding
/// the writer without adding SENT to that set breaks goods receipt for every dispatched order —
/// the same regression ACKNOWLEDGED caused once already. The two changes are inseparable, so the
/// first test below fails if either one is reverted.</para>
/// </summary>
public sealed class Gate4SupplierPurchaseOrderDispatchTests
{
    [Fact]
    public async Task Release_writes_SENT_and_a_dispatched_order_is_still_open_for_receipt()
    {
        using var fixture = new ProcurementScenario();

        var released = await fixture.CreatePurchaseOrderAsync("dispatch-receive", quantity: 8m);

        // Reverting the writer makes this ISSUED and fails here.
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, released.Status);
        await using (var verify = fixture.Context())
        {
            var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == released.Id);
            Assert.Equal(SupplierPurchaseOrderStatuses.Sent, row.Status);
            Assert.NotNull(row.SentToSupplierOn);
        }

        // Removing SENT from OpenForReceipt makes this throw and fails here. Goods receipt against a
        // dispatched-but-unacknowledged order is the ordinary case, not the exception.
        var lineId = await fixture.PurchaseOrderLineIdAsync(released.Id);
        var receipt = await fixture.Execute(service => service.PostGoodsReceiptAsync(
            fixture.Receipt(released.Id, lineId, 8m, released.Version, "dispatch-receive-gr", "GR-DISPATCH")));

        Assert.Equal(SupplierPurchaseOrderStatuses.Received, receipt.PurchaseOrderStatus);
    }

    /// <summary>
    /// The membership assertion the set itself owes. Named separately from the flow test above so a
    /// reviewer removing SENT from the set sees which invariant they broke, not only that a receipt
    /// stopped working.
    /// </summary>
    [Fact]
    public void SENT_and_the_legacy_ISSUED_are_the_same_state_to_every_status_set()
    {
        foreach (var dispatched in new[]
                 {
                     SupplierPurchaseOrderStatuses.Sent, SupplierPurchaseOrderStatuses.Issued
                 })
        {
            Assert.Contains(dispatched, SupplierPurchaseOrderStatuses.WithSupplier);
            // Goods can be received against a dispatched order under either spelling.
            Assert.Contains(dispatched, SupplierPurchaseOrderStatuses.OpenForReceipt);
            // The buyer can still withdraw a dispatched order under either spelling.
            Assert.Contains(dispatched, SupplierPurchaseOrderStatuses.Cancellable);
            // An inbound shipment can be raised against a dispatched order under either spelling.
            Assert.Contains(dispatched, InboundShipmentApplicationService.ShippableOrderStatuses);
        }

        // And the states that are deliberately NOT dispatched stay out, so the assertion above is a
        // statement about SENT/ISSUED rather than about a set that contains everything.
        Assert.DoesNotContain(SupplierPurchaseOrderStatuses.Draft, SupplierPurchaseOrderStatuses.OpenForReceipt);
        Assert.DoesNotContain(SupplierPurchaseOrderStatuses.Approved, SupplierPurchaseOrderStatuses.OpenForReceipt);
        Assert.DoesNotContain(SupplierPurchaseOrderStatuses.Draft, SupplierPurchaseOrderStatuses.WithSupplier);
        Assert.DoesNotContain(SupplierPurchaseOrderStatuses.Approved, SupplierPurchaseOrderStatuses.WithSupplier);
    }

    /// <summary>
    /// A dispatched order is committed supply. If the committed-supply predicate kept only the
    /// legacy word, every order released after this gate would stop netting off its RFQ line and the
    /// same quantity would be sourced and bought a second time.
    /// </summary>
    [Fact]
    public async Task A_dispatched_order_still_covers_the_demand_it_was_raised_against()
    {
        using var fixture = new ProcurementScenario();
        var released = await fixture.CreatePurchaseOrderAsync("dispatch-cover", quantity: 8m);
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, released.Status);

        var refusal = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(
            service => service.CreateSolicitationAsync(fixture.Solicitation("dispatch-cover-resource"))));

        Assert.Contains("fully covered", refusal.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decision register R31. A supplier who says no releases the demand they were covering.
    ///
    /// <para>This is the inverse of the test above, and it is the one that catches the defect the
    /// acknowledgement feature introduced. Recording REJECTED does not move the order off SENT —
    /// only an ACCEPTED answer advances the status — so before this fix the order stayed inside
    /// the committed-supply set and kept netting its full quantity off the RFQ line. At the same
    /// time both SLA sweeps deliberately skip a rejected order, because chasing a supplier who has
    /// already refused is noise. Three alarms went quiet, the line still reported itself covered,
    /// and re-sourcing was refused as "already fully covered". The material was never bought and
    /// nothing anywhere said so.</para>
    ///
    /// <para>Asserting on the refusal rather than on a status is deliberate: the failure mode is
    /// commercial, not structural. The order can sit at SENT forever without hurting anyone — what
    /// hurts is a buyer being told there is nothing to do. Delete the acknowledgement clause from
    /// <c>SupplierPurchaseOrderStatuses.IsCommittedSupply</c> and this test fails on a
    /// <c>ProcurementConflictException</c> that should no longer be thrown.</para>
    /// </summary>
    [Fact]
    public async Task A_rejected_order_releases_the_demand_so_the_line_can_be_sourced_again()
    {
        using var fixture = new ProcurementScenario();
        var released = await fixture.CreatePurchaseOrderAsync("reject-release", quantity: 8m);
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, released.Status);

        var rejected = await fixture.Execute(service => service.AcknowledgePurchaseOrderAsync(
            new AcknowledgeSupplierPurchaseOrderCommand(
                fixture.BusinessUnitId, released.Id, released.Version,
                SupplierAcknowledgementStatuses.Rejected,
                // SYNTHETIC supplier contact, not a real person or company.
                "Supplier contact (synthetic)", "reject-release-ack", "buyer@tenant.test",
                "corr-reject-release", null, null, "Cannot supply at the agreed price.")));

        // The rejection is recorded, and the order deliberately does NOT change status: there is no
        // REJECTED state on the ladder, and inventing one would need a migration. The coverage
        // question is answered by the acknowledgement column instead.
        Assert.Equal(SupplierAcknowledgementStatuses.Rejected, rejected.AcknowledgementStatus);
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, rejected.Status);

        // The behaviour that matters: the buyer can now go and buy the material somewhere else.
        var resourced = await fixture.Execute(
            service => service.CreateSolicitationAsync(fixture.Solicitation("reject-release-resource")));
        Assert.NotNull(resourced);
    }
}
