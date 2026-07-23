using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.BankReconciliation;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialFinanceTests
{
    [Fact]
    public void Controller_UsesDedicatedFinancePermissions()
    {
        AssertPermission(nameof(CommercialFinanceController.CreateInvoice), "Accounts Receivable", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.CreateAdjustment), "Receivable Adjustments", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.Issue), "Accounts Receivable", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.IssueAdjustment), "Receivable Adjustments", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.Cancel), "Accounts Receivable", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.CancelAdjustment), "Receivable Adjustments", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.GetDocuments), "Accounts Receivable", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.PostPayment), "Customer Payments", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.GetPayments), "Customer Payments", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.ReversePayment), "Customer Payments", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.GetWriteOffEligibility), "Receivable Write-offs", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.CreateWriteOff), "Receivable Write-offs", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.GetWriteOffs), "Receivable Write-offs", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.PostWriteOff), "Receivable Write-offs", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.CancelWriteOff), "Receivable Write-offs", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.ReverseWriteOff), "Receivable Write-offs", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.GetRefundEligibility), "Customer Refunds", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.CreateRefund), "Customer Refunds", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.GetRefunds), "Customer Refunds", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.ApproveRefund), "Customer Refunds", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.ReleaseRefund), "Customer Refunds", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.ConfirmRefundDisbursement), "Customer Refunds", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.FailRefundDisbursement), "Customer Refunds", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.CancelRefund), "Customer Refunds", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.ReverseRefund), "Customer Refunds", PermissionAction.Edit);
    }

    [Fact]
    public async Task WriteOff_CreateReplaysIdempotentlyAndRejectsAlteredRequest()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await AllowSqliteWriteOffSnapshotsAsync(db);
        var service = new CommercialFinanceApplicationService(db);
        var invoice = await CreateIssuedInvoiceAsync(db, service, "write-off-idempotency");
        var request = WriteOffRequest(invoice.Id, 40m);

        var created = await service.CreateWriteOffAsync(
            BusinessUnitId, "write-off-create-1", request, "write-off-maker@test");
        var replay = await service.CreateWriteOffAsync(
            BusinessUnitId, "write-off-create-1", request, "write-off-maker@test");

        Assert.Equal(created.Id, replay.Id);
        Assert.Equal(FinanceExceptionStatuses.Draft, created.Status);
        Assert.Equal(40m, created.TotalAmount);
        Assert.Equal(209m, Assert.Single(created.Allocations).BalanceBefore);
        Assert.Equal(169m, Assert.Single(created.Allocations).BalanceAfter);
        Assert.Single(await db.ReceivableWriteOffs.ToListAsync());
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateWriteOffAsync(
            BusinessUnitId, "write-off-create-1", WriteOffRequest(invoice.Id, 41m), "write-off-maker@test"));
    }

    [Fact]
    public async Task WriteOff_PostRequiresCheckerAndReversalRestoresOpenBalance()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await AllowSqliteWriteOffSnapshotsAsync(db);
        var service = new CommercialFinanceApplicationService(db);
        var invoice = await CreateIssuedInvoiceAsync(db, service, "write-off-lifecycle");
        var draft = await service.CreateWriteOffAsync(BusinessUnitId, "write-off-lifecycle-1",
            WriteOffRequest(invoice.Id, 60m), "write-off-maker@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.PostWriteOffAsync(
            BusinessUnitId, draft.Id, new FinanceExceptionActionRequest(draft.Version), "write-off-maker@test"));

        var posted = await service.PostWriteOffAsync(BusinessUnitId, draft.Id,
            new FinanceExceptionActionRequest(draft.Version), "write-off-checker@test");
        var afterPost = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow));

        Assert.Equal(FinanceExceptionStatuses.Posted, posted.Status);
        Assert.StartsWith("WOF-", posted.WriteOffNumber);
        Assert.Equal(149m, afterPost.OutstandingAmount);
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReverseWriteOffAsync(
            BusinessUnitId, posted.Id,
            new FinanceExceptionActionRequest(posted.Version, "Approved balance correction reversal", "CASE-REV-1"),
            "write-off-checker@test"));

        var reversed = await service.ReverseWriteOffAsync(BusinessUnitId, posted.Id,
            new FinanceExceptionActionRequest(posted.Version, "Approved balance correction reversal", "CASE-REV-1"),
            "write-off-reverser@test");
        var afterReversal = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow));

        Assert.Equal(FinanceExceptionStatuses.Reversed, reversed.Status);
        Assert.Equal(209m, afterReversal.OutstandingAmount);
    }

    [Fact]
    public async Task WriteOff_RejectsOverWriteOffAndStaleDraftBalance()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        await AllowSqliteWriteOffSnapshotsAsync(db);
        var service = new CommercialFinanceApplicationService(db);
        var invoice = await CreateIssuedInvoiceAsync(db, service, "write-off-ceiling");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateWriteOffAsync(
            BusinessUnitId, "write-off-over-1", WriteOffRequest(invoice.Id, 210m), "write-off-maker@test"));

        var draft = await service.CreateWriteOffAsync(BusinessUnitId, "write-off-stale-1",
            WriteOffRequest(invoice.Id, 100m), "write-off-maker@test");
        await service.PostPaymentAsync(BusinessUnitId, "write-off-stale-payment",
            new PostPaymentRequest(CustomerId, null, CurrencyId, null, 10m, "BankTransfer", "BANK-WOF-1",
                [new PaymentAllocationRequest(invoice.Id, 10m)]), "collector@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.PostWriteOffAsync(
            BusinessUnitId, draft.Id, new FinanceExceptionActionRequest(draft.Version), "write-off-checker@test"));
    }

    [Fact]
    public async Task Refund_CreateReplaysIdempotentlyAndRejectsAlteredRequest()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var service = new CommercialFinanceApplicationService(db);
        var payment = await CreateUnappliedPaymentAsync(db, service, "refund-idempotency-payment", 150m);
        var request = RefundRequest(payment.Id, 50m);

        var created = await service.CreateRefundAsync(
            BusinessUnitId, "refund-create-1", request, "refund-maker@test");
        var replay = await service.CreateRefundAsync(
            BusinessUnitId, "refund-create-1", request, "refund-maker@test");

        Assert.Equal(created.Id, replay.Id);
        Assert.Equal(FinanceExceptionStatuses.Draft, created.Status);
        Assert.Equal(150m, (await service.GetRefundEligibilityAsync(BusinessUnitId, payment.Id)).AvailableAmount);
        Assert.Single(await db.CustomerRefunds.ToListAsync());
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateRefundAsync(
            BusinessUnitId, "refund-create-1", RefundRequest(payment.Id, 51m), "refund-maker@test"));
        var rawDestination = RefundRequest(payment.Id, 10m) with { DestinationReference = "GB82 WEST 1234 5698 7654 32" };
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateRefundAsync(
            BusinessUnitId, "refund-raw-destination", rawDestination, "refund-maker@test"));
    }

    [Fact]
    public async Task Refund_RequiresIndependentApproverAndReleaserAndReversalRestoresUnapplied()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var service = new CommercialFinanceApplicationService(db);
        var payment = await CreateUnappliedPaymentAsync(db, service, "refund-lifecycle-payment", 150m);
        var draft = await service.CreateRefundAsync(BusinessUnitId, "refund-lifecycle-1",
            RefundRequest(payment.Id, 60m), "refund-maker@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ApproveRefundAsync(
            BusinessUnitId, draft.Id, new FinanceExceptionActionRequest(draft.Version), "refund-maker@test"));
        var approved = await service.ApproveRefundAsync(BusinessUnitId, draft.Id,
            new FinanceExceptionActionRequest(draft.Version), "refund-approver@test");
        var reservedPayment = Assert.Single(await service.GetPaymentsAsync(
            BusinessUnitId, CustomerId, CustomerPaymentStatuses.Posted));

        Assert.Equal(FinanceExceptionStatuses.Approved, approved.Status);
        Assert.Equal(90m, reservedPayment.UnappliedAmount);
        Assert.Equal(60m, (await service.GetRefundEligibilityAsync(BusinessUnitId, payment.Id)).ReservedAmount);
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReleaseRefundAsync(
            BusinessUnitId, approved.Id, new FinanceExceptionActionRequest(approved.Version), "refund-approver@test"));
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReleaseRefundAsync(
            BusinessUnitId, approved.Id, new FinanceExceptionActionRequest(approved.Version), "refund-maker@test"));

        var released = await service.ReleaseRefundAsync(BusinessUnitId, approved.Id,
            new FinanceExceptionActionRequest(approved.Version), "refund-releaser@test");
        Assert.StartsWith("RFD-", released.RefundNumber);
        Assert.Equal(90m, Assert.Single(await service.GetPaymentsAsync(
            BusinessUnitId, CustomerId, CustomerPaymentStatuses.Posted)).UnappliedAmount);
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReverseRefundAsync(
            BusinessUnitId, released.Id,
            new FinanceExceptionActionRequest(released.Version, "Bank rejected refund disbursement", "BANK-RETURN-1"),
            "refund-releaser@test"));

        var failed = await service.FailRefundDisbursementAsync(BusinessUnitId, released.Id,
            new RefundDisbursementRequest(released.Version, "provider:failed-1001",
                "Bank rejected the submitted refund transfer."), "refund-reconciler@test");

        var reversed = await service.ReverseRefundAsync(BusinessUnitId, failed.Id,
            new FinanceExceptionActionRequest(failed.Version, "Bank rejected refund disbursement", "BANK-RETURN-1"),
            "refund-reverser@test");

        Assert.Equal(FinanceExceptionStatuses.Reversed, reversed.Status);
        Assert.Equal(150m, Assert.Single(await service.GetPaymentsAsync(
            BusinessUnitId, CustomerId, CustomerPaymentStatuses.Posted)).UnappliedAmount);
        Assert.Equal(150m, (await service.GetRefundEligibilityAsync(BusinessUnitId, payment.Id)).AvailableAmount);
    }

    [Fact]
    public async Task Refund_CancelRulesReleaseAnyReservation()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var service = new CommercialFinanceApplicationService(db);
        var payment = await CreateUnappliedPaymentAsync(db, service, "refund-cancel-payment", 150m);
        var draft = await service.CreateRefundAsync(BusinessUnitId, "refund-cancel-draft",
            RefundRequest(payment.Id, 20m), "refund-maker@test");

        var cancelledDraft = await service.CancelRefundAsync(BusinessUnitId, draft.Id,
            new FinanceExceptionActionRequest(draft.Version, "Duplicate refund request cancelled"),
            "refund-maker@test");
        Assert.Equal(FinanceExceptionStatuses.Cancelled, cancelledDraft.Status);

        var secondDraft = await service.CreateRefundAsync(BusinessUnitId, "refund-cancel-approved",
            RefundRequest(payment.Id, 30m), "refund-maker@test");
        var approved = await service.ApproveRefundAsync(BusinessUnitId, secondDraft.Id,
            new FinanceExceptionActionRequest(secondDraft.Version), "refund-approver@test");
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CancelRefundAsync(
            BusinessUnitId, approved.Id,
            new FinanceExceptionActionRequest(approved.Version, "Approved request cancellation"),
            "refund-maker@test"));

        var cancelledApproved = await service.CancelRefundAsync(BusinessUnitId, approved.Id,
            new FinanceExceptionActionRequest(approved.Version, "Approved request cancellation"),
            "refund-approver@test");

        Assert.Equal(FinanceExceptionStatuses.Cancelled, cancelledApproved.Status);
        Assert.Equal(150m, Assert.Single(await service.GetPaymentsAsync(
            BusinessUnitId, CustomerId, CustomerPaymentStatuses.Posted)).UnappliedAmount);
    }

    [Fact]
    public async Task Refund_SettledDisbursementRemainsConsumedAndCannotBeReversed()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var service = new CommercialFinanceApplicationService(db);
        var payment = await CreateUnappliedPaymentAsync(db, service, "refund-settlement-payment", 150m);
        var draft = await service.CreateRefundAsync(BusinessUnitId, "refund-settlement",
            RefundRequest(payment.Id, 60m), "refund-maker@test");
        Assert.Equal("Verified provider destination", draft.DestinationReference);
        var approved = await service.ApproveRefundAsync(BusinessUnitId, draft.Id,
            new(draft.Version), "refund-approver@test");
        var released = await service.ReleaseRefundAsync(BusinessUnitId, approved.Id,
            new(approved.Version), "refund-releaser@test");
        var settled = await service.ConfirmRefundDisbursementAsync(BusinessUnitId, released.Id,
            new(released.Version, "provider:settled-1001"), "refund-reconciler@test");

        Assert.Equal("Settled", settled.PostingStatus);
        Assert.Equal("provider:settled-1001", settled.JournalReference);
        Assert.Equal(90m, Assert.Single(await service.GetPaymentsAsync(
            BusinessUnitId, CustomerId, CustomerPaymentStatuses.Posted)).UnappliedAmount);
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReverseRefundAsync(
            BusinessUnitId, settled.Id,
            new(settled.Version, "Attempted reversal after confirmed settlement", "BANK-RECOVERY-1"),
            "refund-reverser@test"));
    }

    [Fact]
    public async Task PaymentReversal_RejectsReceiptWithApprovedRefund()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var service = new CommercialFinanceApplicationService(db);
        var payment = await CreateUnappliedPaymentAsync(db, service, "refund-payment-reversal", 150m);
        var draft = await service.CreateRefundAsync(BusinessUnitId, "refund-before-payment-reversal",
            RefundRequest(payment.Id, 50m), "refund-maker@test");
        await service.ApproveRefundAsync(BusinessUnitId, draft.Id,
            new FinanceExceptionActionRequest(draft.Version), "refund-approver@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.ReversePaymentAsync(
            BusinessUnitId, payment.Id,
            new ReversePaymentRequest(payment.Version, "Receipt reversal requested after refund approval"),
            "collector@test"));
    }

    [Fact]
    public async Task CreditAndDebitNotes_DriveSignedArAndReplayIdempotently()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var invoiceDraft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "adjustment-invoice",
            new CreateInvoiceRequest(null, null, null), "invoice-maker@test");
        var invoice = await service.IssueAsync(BusinessUnitId, invoiceDraft.Id,
            new IssueDocumentRequest(invoiceDraft.Version), "invoice-checker@test");
        var invoiceLine = Assert.Single(invoice.Lines);

        var creditRequest = new CreateAdjustmentRequest(ReceivableDocumentTypes.CreditNote, null, null,
            "RETURN", "Customer returned half the shipment", [new(invoiceLine.Id, 1m)]);
        var creditDraft = await service.CreateAdjustmentAsync(BusinessUnitId, invoice.Id,
            "credit-note-one", creditRequest, "credit-maker@test");
        var creditReplay = await service.CreateAdjustmentAsync(BusinessUnitId, invoice.Id,
            "credit-note-one", creditRequest, "credit-maker@test");
        var credit = await service.IssueAdjustmentAsync(BusinessUnitId, creditDraft.Id,
            new IssueDocumentRequest(creditDraft.Version), "credit-checker@test");

        Assert.Equal(creditDraft.Id, creditReplay.Id);
        Assert.Equal(104.50m, credit.TotalAmount);
        Assert.StartsWith("CRN-", credit.DocumentNumber);
        Assert.Equal(invoice.Id, credit.ParentDocumentId);
        Assert.Equal(invoiceLine.Id, Assert.Single(credit.Lines).ParentDocumentLineId);
        var afterCredit = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow));
        Assert.Equal(invoice.Id, afterCredit.DocumentId);
        Assert.Equal(104.50m, afterCredit.OutstandingAmount);

        var debitDraft = await service.CreateAdjustmentAsync(BusinessUnitId, invoice.Id,
            "debit-note-one", new CreateAdjustmentRequest(ReceivableDocumentTypes.DebitNote, null, null,
                "PRICE_CORRECTION", "Approved underbilling correction", [new(invoiceLine.Id, 1m)]),
            "debit-maker@test");
        var debit = await service.IssueAdjustmentAsync(BusinessUnitId, debitDraft.Id,
            new IssueDocumentRequest(debitDraft.Version), "debit-checker@test");
        var openItems = await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow);

        Assert.StartsWith("DBN-", debit.DocumentNumber);
        Assert.Equal(2, openItems.Count);
        Assert.Contains(openItems, x => x.DocumentId == invoice.Id && x.OutstandingAmount == 104.50m);
        Assert.Contains(openItems, x => x.DocumentId == debit.Id && x.OutstandingAmount == 104.50m);
    }

    [Fact]
    public async Task CreditNote_EnforcesMakerCheckerAndLiveOutstandingCeiling()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "credit-limit-invoice",
            new CreateInvoiceRequest(null, null, null), "invoice-maker@test");
        var invoice = await service.IssueAsync(BusinessUnitId, draft.Id,
            new IssueDocumentRequest(draft.Version), "invoice-checker@test");
        await service.PostPaymentAsync(BusinessUnitId, "credit-limit-payment", new PostPaymentRequest(
            CustomerId, null, CurrencyId, null, 150m, "BankTransfer", null,
            [new(invoice.Id, 150m)]), "collector@test");

        var creditDraft = await service.CreateAdjustmentAsync(BusinessUnitId, invoice.Id,
            "credit-over-live-balance", new CreateAdjustmentRequest(ReceivableDocumentTypes.CreditNote,
                null, null, "RETURN", "Full return after partial payment", [new(invoice.Lines.Single().Id, 2m)]),
            "credit-maker@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.IssueAdjustmentAsync(BusinessUnitId,
            creditDraft.Id, new IssueDocumentRequest(creditDraft.Version), "credit-maker@test"));
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.IssueAdjustmentAsync(BusinessUnitId,
            creditDraft.Id, new IssueDocumentRequest(creditDraft.Version), "credit-checker@test"));
    }

    [Fact]
    public async Task CancelDraft_RequiresCurrentVersionAndReasonAndBecomesImmutableState()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(
            BusinessUnitId, order.Id, "cancel-draft-1", new CreateInvoiceRequest(null, null, null), "finance@test");

        await Assert.ThrowsAsync<ArgumentException>(() => service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(draft.Version, "  "), "finance@test"));
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(draft.Version + 1, "Duplicate draft"), "finance@test"));

        var cancelled = await service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(draft.Version, " Duplicate draft "), "finance@test");
        var replay = await service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(cancelled.Version, "Duplicate draft"), "finance@test");
        var staleReplay = await service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(draft.Version, "Duplicate draft"), "finance@test");
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(cancelled.Version, "Different reason"), "finance@test"));

        Assert.Equal(ReceivableDocumentStatuses.Cancelled, cancelled.Status);
        Assert.Equal(cancelled.Version, staleReplay.Version);
        Assert.Equal(draft.Version + 1, cancelled.Version);
        Assert.Null(cancelled.DocumentNumber);
        Assert.NotNull(cancelled.VoidedOn);
        Assert.Equal("Duplicate draft", cancelled.VoidReason);
        Assert.Equal("finance@test", cancelled.VoidedBy);
        Assert.Equal(cancelled.Id, replay.Id);
        Assert.Equal(2, await db.CommercialFinanceAudits.CountAsync(x => x.AggregateId == draft.Id));
        Assert.Contains(await db.CommercialFinanceAudits.ToListAsync(),
            x => x.AggregateId == draft.Id && x.Action == "DraftCancelled");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.IssueAsync(
            BusinessUnitId, draft.Id, new IssueDocumentRequest(cancelled.Version), "issuer@test"));
    }

    [Fact]
    public async Task CancelDraft_CannotCrossTenantBoundary()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(
            BusinessUnitId, order.Id, "cancel-tenant-1", new CreateInvoiceRequest(null, null, null), "finance@test");

        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.CancelAsync(
            BusinessUnitId + 1, draft.Id, new CancelDocumentRequest(draft.Version, "Wrong tenant"), "finance@test"));
    }

    [Fact]
    public async Task InvoiceDraft_SnapshotsOrderMoneyAndReplaysIdempotently()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var request = new CreateInvoiceRequest(DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddDays(30), null);

        var created = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "invoice-create-1", request, "finance@test");
        var replay = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "invoice-create-1", request, "finance@test");

        Assert.Equal(created.Id, replay.Id);
        Assert.Null(created.DocumentNumber);
        Assert.Equal(ReceivableDocumentStatuses.Draft, created.Status);
        Assert.Equal(200m, created.SubTotal);
        Assert.Equal(10m, created.DiscountAmount);
        Assert.Equal(19m, created.TaxAmount);
        Assert.Equal(209m, created.TotalAmount);
        Assert.Equal("AED", created.CurrencyCode);
        Assert.Equal(209m, Assert.Single(created.Lines).LineTotal);
        Assert.Single(await db.CommercialFinanceAudits.ToListAsync());
        Assert.Equal("finance.receivable.draft-created",
            (await db.FinanceOutboxMessages.SingleAsync()).EventType);

        var changed = request with { DueDate = DateTime.UtcNow.Date.AddDays(31) };
        await Assert.ThrowsAsync<FinanceConflictException>(() =>
            service.CreateInvoiceAsync(BusinessUnitId, order.Id, "invoice-create-1", changed, "finance@test"));
        await Assert.ThrowsAsync<FinanceConflictException>(() =>
            service.CreateInvoiceAsync(BusinessUnitId, order.Id + 999, "invoice-create-1", request, "finance@test"));
    }

    [Fact]
    public async Task Issue_RechecksOrderQuantityAndRejectsCompetingDraft()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var request = new CreateInvoiceRequest(null, null, null);
        var first = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "competing-draft-1", request, "finance@test");
        var second = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "competing-draft-2", request, "finance@test");

        await service.IssueAsync(BusinessUnitId, first.Id, new IssueDocumentRequest(first.Version), "issuer@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() =>
            service.IssueAsync(BusinessUnitId, second.Id, new IssueDocumentRequest(second.Version), "issuer@test"));
    }

    [Fact]
    public async Task IssuePaymentAndReversal_DriveDerivedOpenBalance()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(
            BusinessUnitId, order.Id, "invoice-create-2",
            new CreateInvoiceRequest(DateTime.UtcNow.Date.AddDays(-45), DateTime.UtcNow.Date.AddDays(-15), null),
            "finance@test");

        var issued = await service.IssueAsync(BusinessUnitId, draft.Id, new IssueDocumentRequest(draft.Version), "issuer@test");
        var issueReplay = await service.IssueAsync(BusinessUnitId, draft.Id, new IssueDocumentRequest(issued.Version), "issuer@test");
        var staleIssueReplay = await service.IssueAsync(
            BusinessUnitId, draft.Id, new IssueDocumentRequest(draft.Version), "issuer@test");

        Assert.Equal(issued.DocumentNumber, issueReplay.DocumentNumber);
        Assert.Equal(issued.DocumentNumber, staleIssueReplay.DocumentNumber);
        Assert.StartsWith($"INV-{DateTime.UtcNow.Year}-", issued.DocumentNumber);
        Assert.Equal(ReceivableDocumentStatuses.Issued, issued.Status);

        var payment = await service.PostPaymentAsync(BusinessUnitId, "payment-post-1", new PostPaymentRequest(
            CustomerId, null, CurrencyId, DateTime.UtcNow, 100m, "BankTransfer", "BANK-1",
            [new PaymentAllocationRequest(issued.Id, 100m)]), "collector@test");
        var openAfterPayment = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow));
        Assert.Equal(109m, openAfterPayment.OutstandingAmount);
        Assert.Equal("1-30", openAfterPayment.AgingBucket);

        var reversed = await service.ReversePaymentAsync(BusinessUnitId, payment.Id,
            new ReversePaymentRequest(payment.Version, "Bank returned payment after treasury review"), "controller@test");
        var openAfterReversal = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow));
        Assert.Equal(CustomerPaymentStatuses.Reversed, reversed.Status);
        Assert.Equal(0m, reversed.UnappliedAmount);
        Assert.Equal(209m, openAfterReversal.OutstandingAmount);
        var eventTypes = await db.FinanceOutboxMessages.OrderBy(x => x.Id).Select(x => x.EventType).ToListAsync();
        Assert.Contains("finance.receivable.draft-created", eventTypes);
        Assert.Contains("finance.receivable.issued", eventTypes);
        Assert.Contains("finance.payment.posted", eventTypes);
        Assert.Contains("finance.payment.reversed", eventTypes);
    }

    [Fact]
    public async Task FinanceOutbox_LeasesFenceCompletionAndDeadLetterFailures()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        db.FinanceOutboxMessages.Add(new FinanceOutboxMessage
        {
            BusinessUnitId = BusinessUnitId,
            AggregateType = "ReceivableDocument",
            AggregateId = 42,
            AggregateVersion = 1,
            EventType = "finance.test",
            Payload = "{}",
            OccurredOn = DateTime.UtcNow,
            AvailableOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        var store = new FinanceOutboxStore(db);

        var first = Assert.Single(await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(1), default));
        await Assert.ThrowsAsync<FinanceOutboxLeaseConflictException>(() =>
            store.CompleteAsync(first.Id, "worker-a", Guid.NewGuid(), default));
        await store.FailAsync(first.Id, "worker-a", first.LeaseToken, "downstream unavailable",
            TimeSpan.FromSeconds(1), 1, default);

        db.ChangeTracker.Clear();
        var failed = await db.FinanceOutboxMessages.IgnoreQueryFilters().SingleAsync(x => x.Id == first.Id);
        Assert.NotNull(failed.DeadLetteredOn);
        Assert.Null(failed.LeaseOwner);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Empty(await store.ClaimAsync("worker-b", 10, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task Payment_RejectsOverAllocation()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "invoice-create-3",
            new CreateInvoiceRequest(null, null, null), "finance@test");
        var invoice = await service.IssueAsync(BusinessUnitId, draft.Id, new IssueDocumentRequest(1), "issuer@test");

        await Assert.ThrowsAsync<FinanceConflictException>(() => service.PostPaymentAsync(
            BusinessUnitId, "payment-over", new PostPaymentRequest(
                CustomerId, null, CurrencyId, null, 250m, "BankTransfer", null,
                [new PaymentAllocationRequest(invoice.Id, 250m)]), "collector@test"));
    }

    [Fact]
    public async Task DraftOrder_IsNotInvoiceEligible()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var status = await db.SetupMasters.SingleAsync(x => x.SetupId == StatusId);
        status.SetupCode = "DRAFT";
        status.SetupValue = "Draft";
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<FinanceConflictException>(() =>
            new CommercialFinanceApplicationService(db).CreateInvoiceAsync(
                BusinessUnitId, order.Id, "draft-order", new CreateInvoiceRequest(null, null, null), "finance@test"));
    }

    [Fact]
    public async Task InvoiceAndPayment_NormalizeCurrencyScaleBeforePersisting()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var line = Assert.Single(order.OrderItems);
        line.Quantity = 1.5m;
        line.UnitPrice = 0.33m;
        line.Discount = 0m;
        line.TaxAmount = 0m;
        await db.SaveChangesAsync();
        var service = new CommercialFinanceApplicationService(db);

        var draft = await service.CreateInvoiceAsync(
            BusinessUnitId, order.Id, "fractional-invoice", new CreateInvoiceRequest(null, null, null), "finance@test");
        Assert.Equal(0.50m, draft.SubTotal);
        Assert.Equal(0.50m, draft.TotalAmount);

        await Assert.ThrowsAsync<ArgumentException>(() => service.PostPaymentAsync(
            BusinessUnitId, "precision-payment", new PostPaymentRequest(
                CustomerId, null, CurrencyId, null, 1.004m, "BankTransfer", null,
                [new PaymentAllocationRequest(123, 1.005m)]), "collector@test"));
    }

    [Fact]
    public async Task HistoricalAging_UsesPaymentAndReversalEffectiveTimes()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "history-invoice",
            new CreateInvoiceRequest(DateTime.UtcNow.Date.AddDays(-30), DateTime.UtcNow.Date.AddDays(-20), null), "finance@test");
        var invoice = await service.IssueAsync(BusinessUnitId, draft.Id, new IssueDocumentRequest(1), "issuer@test");
        db.ReceivableDocuments.Single(x => x.Id == invoice.Id).IssuedOn = DateTime.UtcNow.AddDays(-30);
        await db.SaveChangesAsync();
        var payment = await service.PostPaymentAsync(BusinessUnitId, "history-payment", new PostPaymentRequest(
            CustomerId, null, CurrencyId, DateTime.UtcNow.AddDays(-10), 100m, "BankTransfer", null,
            [new PaymentAllocationRequest(invoice.Id, 100m)]), "collector@test");
        await service.ReversePaymentAsync(BusinessUnitId, payment.Id,
            new ReversePaymentRequest(payment.Version, "Correction approved by treasury controller"), "controller@test");

        var historical = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow.Date.AddDays(-1)));
        var current = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow.Date));
        Assert.Equal(109m, historical.OutstandingAmount);
        Assert.Equal(209m, current.OutstandingAmount);
        Assert.Single(await service.GetPaymentsAsync(BusinessUnitId, CustomerId, CustomerPaymentStatuses.Reversed));
    }

    private static async Task<ReceivableDocumentDto> CreateIssuedInvoiceAsync(
        ErpRfqAutomationContext db, CommercialFinanceApplicationService service, string keyPrefix)
    {
        var order = SeedOrder(db);
        var draft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, $"{keyPrefix}-invoice",
            new CreateInvoiceRequest(null, null, null), "invoice-maker@test");
        return await service.IssueAsync(BusinessUnitId, draft.Id,
            new IssueDocumentRequest(draft.Version), "invoice-checker@test");
    }

    private static Task<int> AllowSqliteWriteOffSnapshotsAsync(ErpRfqAutomationContext db)
        // SQLite maps decimals to text, so its round(decimal) equality rejects valid snapshots.
        => db.Database.ExecuteSqlRawAsync("PRAGMA ignore_check_constraints = ON;");

    private static async Task<CustomerPaymentDto> CreateUnappliedPaymentAsync(
        ErpRfqAutomationContext db, CommercialFinanceApplicationService service, string idempotencyKey, decimal amount)
    {
        SeedOrder(db);
        return await service.PostPaymentAsync(BusinessUnitId, idempotencyKey,
            new PostPaymentRequest(CustomerId, null, CurrencyId, null, amount, "BankTransfer", "BANK-UNAPPLIED",
                Array.Empty<PaymentAllocationRequest>()), "collector@test");
    }

    private static CreateWriteOffRequest WriteOffRequest(long documentId, decimal amount)
        => new(null, "SMALL_BALANCE", "Approved immaterial receivable balance write-off", "CASE-WOF-1",
            [new WriteOffAllocationRequest(documentId, amount)]);

    private static CreateRefundRequest RefundRequest(long paymentId, decimal amount)
        => new(paymentId, null, amount, "BankTransfer", "token:acct_test_4242", true,
            "CUSTOMER_REFUND", "Approved return of unapplied customer funds", "CASE-RFD-1");

    private static Order SeedOrder(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "AR Customer");
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId,
            Code = "AED",
            CurrencyName = "UAE Dirham",
            Symbol = "AED",
            ExchangeRate = 1m,
            IsBaseCurrency = true,
            IsActive = true,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            BusinessUnitId = BusinessUnitId
        });
        db.Products.Add(new Product
        {
            Id = ProductId,
            ProductName = "Invoice product",
            PartNo = "AR-1",
            Buid = BusinessUnitId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        db.SetupMasters.Add(new SetupMaster
        {
            SetupId = StatusId,
            SetupType = "OrderStatus",
            SetupCode = "CONFIRMED",
            SetupValue = "Confirmed",
            BusinessUnitId = BusinessUnitId,
            IsActive = true,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        var order = new Order
        {
            OrderNo = $"ORD-AR-{Guid.NewGuid():N}",
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            StatusId = StatusId,
            CurrencyId = CurrencyId,
            OrderDate = DateTime.UtcNow,
            SubTotal = 200m,
            DiscountAmount = 10m,
            TaxAmount = 19m,
            TotalAmount = 209m,
            BalanceAmount = 209m,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true,
            OrderItems =
            [
                new OrderItem
                {
                    ProductId = ProductId,
                    Description = "Invoice product",
                    Quantity = 2m,
                    UnitPrice = 100m,
                    Discount = 10m,
                    TaxAmount = 19m,
                    TotalAmount = 209m,
                    CreatedBy = "test",
                    CreatedDate = DateTime.UtcNow,
                    IsActive = true
                }
            ]
        };
        db.Orders.Add(order);
        var cash = new LedgerAccount { Id = CashAccountId, BusinessUnitId = BusinessUnitId, Code = "1010", Name = "Operating cash", Category = LedgerAccountCategories.Asset, NormalBalance = LedgerNormalBalances.Debit, CurrencyId = CurrencyId, IsControlAccount = true, AllowsManualPosting = false, IdempotencyKey = "test-cash", RequestHash = new string('1', 64), CreatedBy = "test", CreatedOn = DateTime.UtcNow };
        var receivables = new LedgerAccount { Id = ReceivablesAccountId, BusinessUnitId = BusinessUnitId, Code = "1100", Name = "Trade receivables", Category = LedgerAccountCategories.Asset, NormalBalance = LedgerNormalBalances.Debit, IsControlAccount = true, AllowsManualPosting = false, IdempotencyKey = "test-ar", RequestHash = new string('2', 64), CreatedBy = "test", CreatedOn = DateTime.UtcNow };
        var unapplied = new LedgerAccount { Id = UnappliedAccountId, BusinessUnitId = BusinessUnitId, Code = "2100", Name = "Unapplied cash", Category = LedgerAccountCategories.Liability, NormalBalance = LedgerNormalBalances.Credit, IsControlAccount = false, AllowsManualPosting = false, IdempotencyKey = "test-unapplied", RequestHash = new string('3', 64), CreatedBy = "test", CreatedOn = DateTime.UtcNow };
        db.LedgerAccounts.AddRange(cash, receivables, unapplied);
        db.LedgerBooks.Add(new LedgerBook { Id = LedgerBookId, BusinessUnitId = BusinessUnitId, Name = "Test ledger", FunctionalCurrencyId = CurrencyId, TimeZoneId = "UTC", FiscalYearStartMonth = 1, ReceivablesControlAccountId = ReceivablesAccountId, UnappliedCashAccountId = UnappliedAccountId, IdempotencyKey = "test-book", RequestHash = new string('4', 64), CreatedBy = "test", CreatedOn = DateTime.UtcNow });
        db.AccountingPeriods.Add(new AccountingPeriod { Id = PeriodId, BusinessUnitId = BusinessUnitId, FiscalYear = DateTime.UtcNow.Year, PeriodNumber = 1, Name = "Test period", StartsOn = DateTime.UtcNow.Date.AddYears(-2), EndsOn = DateTime.UtcNow.Date.AddYears(2), Status = AccountingPeriodStatuses.Open, IdempotencyKey = "test-period", RequestHash = new string('5', 64), CreatedBy = "test", CreatedOn = DateTime.UtcNow });
        db.BankAccounts.Add(new BankAccount { Id = BankAccountId, BusinessUnitId = BusinessUnitId, Name = "Operating bank", InstitutionName = "Test bank", MaskedAccountNumber = "****4242", AccountFingerprint = new string('6', 64), CurrencyId = CurrencyId, LedgerAccountId = CashAccountId, Status = BankAccountStatuses.Active, OpeningDate = DateTime.UtcNow.Date.AddYears(-2), IdempotencyKey = "test-bank", RequestHash = new string('7', 64), CreatedBy = "test", CreatedOn = DateTime.UtcNow });
        db.SaveChanges();
        return order;
    }

    private static void AssertPermission(string methodName, string moduleName, PermissionAction action)
    {
        var attribute = typeof(CommercialFinanceController).GetMethod(methodName)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), inherit: true)
            .Cast<RequireModulePermissionAttribute>().Single();
        Assert.Equal(moduleName, attribute.ModuleName);
        Assert.Equal(action, attribute.Action);
    }

    private const long BusinessUnitId = 95_001;
    private const long CustomerId = 95_002;
    private const long CurrencyId = 95_003;
    private const long ProductId = 95_004;
    private const long StatusId = 95_005;
    private const long CashAccountId = 95_006;
    private const long ReceivablesAccountId = 95_007;
    private const long UnappliedAccountId = 95_008;
    private const long LedgerBookId = 95_009;
    private const long PeriodId = 95_010;
    private const long BankAccountId = 95_011;
}
