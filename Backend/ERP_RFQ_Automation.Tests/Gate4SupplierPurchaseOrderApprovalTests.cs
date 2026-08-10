using System.Text.Json;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// FR-SPO-01. A supplier purchase order drafted from an award must be approved by a buyer before it
/// is released to the supplier, and the buyer who approves it must not be the buyer who approved the
/// sourcing award behind it.
///
/// <para>Before this gate a draft went straight to ISSUED. Award approval and PO issuance were
/// separate permissions but never separate people, so one user could pick a supplier, award to
/// them, and commit the company's money to them without a second pair of eyes ever touching the
/// transaction — and no row anywhere recorded that anyone had approved the spend at all.</para>
/// </summary>
public sealed class Gate4SupplierPurchaseOrderApprovalTests
{
    [Fact]
    public async Task A_draft_purchase_order_cannot_be_released_to_the_supplier()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "draft-release");

        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(fixture.Issue(draft.Id, "draft-release-issue", version: 1))));

        Assert.Contains("approved", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Draft, row.Status);
        // The release side effects must not have happened either: a refused release that still
        // booked inbound supply would promise stock against an order the supplier never received.
        Assert.Empty(await verify.IncomingInventory.Where(x => x.SourceType == "SupplierPurchaseOrderLine").ToListAsync());
        Assert.Empty(await verify.ProcurementEvents.Where(x => x.EventType == "SUPPLIER_PO_ISSUED").ToListAsync());
    }

    [Fact]
    public async Task Approval_records_the_approver_and_the_moment_and_only_then_permits_release()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "approve-records");
        var before = DateTime.UtcNow;

        var approval = await fixture.ApproveAsync(draft.Id, "approve-records-approve");

        Assert.Equal(SupplierPurchaseOrderStatuses.Approved, approval.Status);
        Assert.Equal(ProcurementScenario.PurchaseOrderApproverUserId, approval.ApprovedByUserId);
        Assert.True(approval.SegregationOfDutiesEnforced);
        Assert.False(approval.Replayed);

        await using (var verify = fixture.Context())
        {
            var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
            Assert.Equal(SupplierPurchaseOrderStatuses.Approved, row.Status);
            Assert.Equal(ProcurementScenario.PurchaseOrderApproverUserId, row.ApprovedByUserId);
            Assert.Equal($"buyer-{ProcurementScenario.PurchaseOrderApproverUserId}", row.ApprovedBy);
            Assert.NotNull(row.ApprovedOn);
            Assert.InRange(row.ApprovedOn!.Value, before, DateTime.UtcNow);
            Assert.Equal(2, row.Version);

            var approvalEvent = await verify.ProcurementEvents
                .SingleAsync(x => x.EventType == "SUPPLIER_PO_APPROVED");
            using var payload = JsonDocument.Parse(approvalEvent.PayloadJson);
            Assert.Equal(ProcurementScenario.PurchaseOrderApproverUserId,
                payload.RootElement.GetProperty("approvedByUserId").GetInt64());
            // The policy in force is recorded with the decision, so a later configuration change
            // cannot make this approval look like it passed a check it never faced.
            Assert.True(payload.RootElement.GetProperty("segregationOfDutiesEnforced").GetBoolean());
            Assert.Equal(ProcurementScenario.AwardApproverUserId, Assert.Single(
                payload.RootElement.GetProperty("awardApproverUserIds").EnumerateArray()).GetInt64());
        }

        var released = await fixture.Execute(service => service.IssuePurchaseOrderAsync(
            fixture.Issue(draft.Id, "approve-records-issue", approval.Version)));
        Assert.Equal(SupplierPurchaseOrderStatuses.Sent, released.Status);
    }

    [Fact]
    public async Task A_released_order_cannot_be_released_or_approved_again()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("released-twice", quantity: 8m);

        var reReleased = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(fixture.Issue(purchaseOrder.Id, "released-twice-again", version: 3))));
        Assert.Contains("approved", reReleased.Message, StringComparison.OrdinalIgnoreCase);

        var reApproved = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.ApproveAsync(purchaseOrder.Id, "released-twice-reapprove", version: 3));
        Assert.Contains("draft", reApproved.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Segregation_of_duties_refuses_the_user_who_approved_the_award()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "same-person");

        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.ApproveAsync(draft.Id, "same-person-approve",
                approverUserId: ProcurementScenario.AwardApproverUserId));

        Assert.Contains("segregation of duties", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Draft, row.Status);
        Assert.Null(row.ApprovedByUserId);
        Assert.Empty(await verify.ProcurementEvents.Where(x => x.EventType == "SUPPLIER_PO_APPROVED").ToListAsync());

        // A second buyer clears the same order, which is the point: the control blocks the person,
        // not the order.
        var approval = await fixture.ApproveAsync(draft.Id, "same-person-second-buyer");
        Assert.Equal(SupplierPurchaseOrderStatuses.Approved, approval.Status);
    }

    [Fact]
    public async Task Segregation_of_duties_can_be_stood_down_and_the_setting_used_is_recorded()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "one-buyer");

        // A two-person trading company physically cannot field a second approver. The control is
        // configurable so that it gets stood down deliberately and visibly, rather than by the
        // approval step being abandoned altogether.
        var approval = await fixture.Execute(service => service.ApprovePurchaseOrderAsync(
                fixture.Approve(draft.Id, "one-buyer-approve",
                    approverUserId: ProcurementScenario.AwardApproverUserId)),
            segregationOfDutiesEnforced: false);

        Assert.Equal(SupplierPurchaseOrderStatuses.Approved, approval.Status);
        Assert.Equal(ProcurementScenario.AwardApproverUserId, approval.ApprovedByUserId);
        Assert.False(approval.SegregationOfDutiesEnforced);

        await using var verify = fixture.Context();
        var approvalEvent = await verify.ProcurementEvents.SingleAsync(x => x.EventType == "SUPPLIER_PO_APPROVED");
        using var payload = JsonDocument.Parse(approvalEvent.PayloadJson);
        Assert.False(payload.RootElement.GetProperty("segregationOfDutiesEnforced").GetBoolean());
        Assert.Equal(ProcurementScenario.AwardApproverUserId,
            payload.RootElement.GetProperty("approvedByUserId").GetInt64());
    }

    [Fact]
    public async Task An_award_that_records_no_approver_cannot_be_certified_as_segregated()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "unknown-approver");
        await using (var setup = fixture.Context())
        {
            var award = await setup.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().SingleAsync();
            award.AwardedByUserId = null;
            await setup.SaveChangesAsync();
        }

        // Fail closed. A control that reports success without establishing the fact produces an
        // audit trail claiming a separation nobody verified.
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.ApproveAsync(draft.Id, "unknown-approver-approve"));
        Assert.Contains("cannot be verified", exception.Message, StringComparison.OrdinalIgnoreCase);

        var standDown = await fixture.Execute(service => service.ApprovePurchaseOrderAsync(
            fixture.Approve(draft.Id, "unknown-approver-stand-down")), segregationOfDutiesEnforced: false);
        Assert.Equal(SupplierPurchaseOrderStatuses.Approved, standDown.Status);
    }

    [Fact]
    public async Task Approval_requires_an_authenticated_approver_identity()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "anonymous");

        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.ApprovePurchaseOrderAsync(
                fixture.Approve(draft.Id, "anonymous-approve") with { ApprovedByUserId = null })));

        Assert.Contains("approver identity", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Approval_cannot_reach_across_a_tenant_boundary()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "cross-tenant");

        await using (var otherTenant = fixture.Context(fixture.OtherBusinessUnitId))
        {
            var service = new ProcurementApplicationService(otherTenant);
            await Assert.ThrowsAsync<ProcurementValidationException>(() =>
                service.ApprovePurchaseOrderAsync(fixture.Approve(draft.Id, "cross-tenant-approve")));
        }

        await using var verify = fixture.Context();
        var row = await verify.SupplierPurchaseOrders.SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Draft, row.Status);
        Assert.Null(row.ApprovedByUserId);
    }

    [Fact]
    public async Task Approval_replays_on_its_idempotency_key_and_refuses_changed_content()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "approval-replay");
        var command = fixture.Approve(draft.Id, "approval-replay-approve");

        var first = await fixture.Execute(service => service.ApprovePurchaseOrderAsync(command));
        var replay = await fixture.Execute(service => service.ApprovePurchaseOrderAsync(command));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.ApprovedOn, replay.ApprovedOn);
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.ApprovePurchaseOrderAsync(command with { ApprovedByUserId = 99 })));

        await using var verify = fixture.Context();
        Assert.Single(await verify.ProcurementEvents.Where(x => x.EventType == "SUPPLIER_PO_APPROVED").ToListAsync());
    }

    [Fact]
    public async Task An_approved_but_unreleased_order_can_still_be_cancelled()
    {
        using var fixture = new ProcurementScenario();
        var draft = await DraftAsync(fixture, "approved-cancel");
        var approval = await fixture.ApproveAsync(draft.Id, "approved-cancel-approve");

        // Approval commits nothing physical. Leaving an approved-but-unsent order uncancellable
        // would strand its RFQ line exactly as a stranded DRAFT once did.
        var cancelled = await fixture.Execute(service => service.CancelPurchaseOrderAsync(
            fixture.Cancel(draft.Id, "approved-cancel-command", "Supplier withdrew.", approval.Version)));

        Assert.Equal(SupplierPurchaseOrderStatuses.Cancelled, cancelled.Status);
    }

    /// <summary>
    /// A draft purchase order raised from an award approved by
    /// <see cref="ProcurementScenario.AwardApproverUserId"/>.
    /// </summary>
    private static async Task<PurchaseOrderResult> DraftAsync(ProcurementScenario fixture, string key)
    {
        var award = await fixture.CreateAwardAsync(key, quantity: 8m);
        return await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], $"{key}-po")));
    }
}
