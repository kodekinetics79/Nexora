using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialFinanceTests
{
    [Fact]
    public void Controller_UsesDedicatedFinancePermissions()
    {
        AssertPermission(nameof(CommercialFinanceController.CreateInvoice), "Accounts Receivable", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.Issue), "Accounts Receivable", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.Cancel), "Accounts Receivable", PermissionAction.Edit);
        AssertPermission(nameof(CommercialFinanceController.GetDocuments), "Accounts Receivable", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.PostPayment), "Customer Payments", PermissionAction.Create);
        AssertPermission(nameof(CommercialFinanceController.GetPayments), "Customer Payments", PermissionAction.View);
        AssertPermission(nameof(CommercialFinanceController.ReversePayment), "Customer Payments", PermissionAction.Edit);
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
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(draft.Version, "Duplicate draft"), "finance@test"));
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.CancelAsync(
            BusinessUnitId, draft.Id, new CancelDocumentRequest(cancelled.Version, "Different reason"), "finance@test"));

        Assert.Equal(ReceivableDocumentStatuses.Cancelled, cancelled.Status);
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
        await Assert.ThrowsAsync<FinanceConflictException>(() => service.IssueAsync(
            BusinessUnitId, draft.Id, new IssueDocumentRequest(draft.Version), "issuer@test"));

        Assert.Equal(issued.DocumentNumber, issueReplay.DocumentNumber);
        Assert.StartsWith($"INV-{DateTime.UtcNow.Year}-", issued.DocumentNumber);
        Assert.Equal(ReceivableDocumentStatuses.Issued, issued.Status);

        var payment = await service.PostPaymentAsync(BusinessUnitId, "payment-post-1", new PostPaymentRequest(
            CustomerId, null, CurrencyId, DateTime.UtcNow, 100m, "BankTransfer", "BANK-1",
            [new PaymentAllocationRequest(issued.Id, 100m)]), "collector@test");
        var openAfterPayment = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow));
        Assert.Equal(109m, openAfterPayment.OutstandingAmount);
        Assert.Equal("1-30", openAfterPayment.AgingBucket);

        var reversed = await service.ReversePaymentAsync(BusinessUnitId, payment.Id,
            new ReversePaymentRequest(payment.Version, "Bank returned payment"), "collector@test");
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
        var payment = await service.PostPaymentAsync(BusinessUnitId, "history-payment", new PostPaymentRequest(
            CustomerId, null, CurrencyId, DateTime.UtcNow.AddDays(-10), 100m, "BankTransfer", null,
            [new PaymentAllocationRequest(invoice.Id, 100m)]), "collector@test");
        await service.ReversePaymentAsync(BusinessUnitId, payment.Id,
            new ReversePaymentRequest(payment.Version, "Correction"), "collector@test");

        var historical = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow.Date.AddDays(-1)));
        var current = Assert.Single(await service.GetOpenItemsAsync(BusinessUnitId, DateTime.UtcNow.Date));
        Assert.Equal(109m, historical.OutstandingAmount);
        Assert.Equal(209m, current.OutstandingAmount);
        Assert.Single(await service.GetPaymentsAsync(BusinessUnitId, CustomerId, CustomerPaymentStatuses.Reversed));
    }

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
}
