using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// An invoice must state the currency it is payable in, or it is not an invoice.
///
/// <para><b>The defect.</b> <c>Order.CurrencyId</c> is nullable and the manual order screen
/// never sets it. <c>CreateInvoiceAsync</c> copied it into the draft unchecked and
/// <c>IssueCoreAsync</c> numbered the draft unchecked. Production (2026-09-04) holds
/// <c>INV-2026-000001</c>: Issued, 1,000.00, <c>CurrencyId NULL</c>, from order 3 (MANUAL,
/// <c>CurrencyID NULL</c>). Nothing refused it — and nothing can ever settle it, because
/// <c>PostPaymentAsync</c> requires <c>document.CurrencyId == request.CurrencyId</c>, which a
/// NULL never satisfies. The rule that a payment must match the invoice's currency lived at
/// the payment; the rule that an invoice must HAVE a currency lived nowhere.</para>
/// </summary>
public sealed class InvoiceCurrencyGateTests
{
    [Fact]
    public async Task An_order_with_no_currency_cannot_be_drafted_into_an_invoice()
    {
        // PRODUCTION'S SHAPE: order 3 — CONFIRMED-eligible (SHIPPED), one line, CurrencyID NULL.
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db, currencyId: null);
        var service = new CommercialFinanceApplicationService(db);

        var refused = await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateInvoiceAsync(
            BusinessUnitId, order.Id, "no-currency-invoice", new CreateInvoiceRequest(null, null, null), "invoice-maker@test"));

        Assert.Contains("no currency", refused.Message);
        Assert.Contains(order.OrderNo, refused.Message);
        Assert.Empty(await db.ReceivableDocuments.ToListAsync());
    }

    [Fact]
    public async Task A_draft_that_reached_the_table_without_a_currency_is_refused_at_issue()
    {
        // Guard at the point of no return as well: a draft can be written by an older build or
        // a backfill, and issue is what turns it into a numbered legal document.
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db, currencyId: CurrencyId);
        var service = new CommercialFinanceApplicationService(db);
        var draft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "orphaned-currency-invoice",
            new CreateInvoiceRequest(null, null, null), "invoice-maker@test");

        // What INV-2026-000001 looks like the row before it was numbered.
        await db.ReceivableDocuments.Where(x => x.Id == draft.Id)
            .ExecuteUpdateAsync(x => x.SetProperty(d => d.CurrencyId, (long?)null));
        db.ChangeTracker.Clear();

        var refused = await Assert.ThrowsAsync<FinanceConflictException>(() => service.IssueAsync(
            BusinessUnitId, draft.Id, new IssueDocumentRequest(draft.Version), "invoice-checker@test"));

        Assert.Contains("no currency", refused.Message);
        var stored = await db.ReceivableDocuments.AsNoTracking().SingleAsync(x => x.Id == draft.Id);
        Assert.Equal(ReceivableDocumentStatuses.Draft, stored.Status);
        Assert.Null(stored.IssuedOn);
    }

    [Fact]
    public async Task An_order_with_a_currency_is_invoiced_and_issued_in_that_currency()
    {
        // THE CONTROL: the gate refuses the absence of a currency, not invoicing.
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db, currencyId: CurrencyId);
        var service = new CommercialFinanceApplicationService(db);

        var draft = await service.CreateInvoiceAsync(BusinessUnitId, order.Id, "currency-invoice",
            new CreateInvoiceRequest(null, null, null), "invoice-maker@test");
        var issued = await service.IssueAsync(BusinessUnitId, draft.Id,
            new IssueDocumentRequest(draft.Version), "invoice-checker@test");

        Assert.Equal(ReceivableDocumentStatuses.Issued, issued.Status);
        Assert.Equal(CurrencyId, issued.CurrencyId);
    }

    [Fact]
    public async Task A_draft_manual_order_is_still_refused_until_it_is_confirmed()
    {
        // THE CONTROL for QuoteToCashScenarioRegressionTests.An_order_raised_from_a_confirmed_client_PO_is_invoiceable_while_still_DRAFT:
        // the relaxation is for orders the customer accepted through a Client PO, not for every
        // draft. A manual draft carries no such acceptance.
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var order = SeedOrder(db, currencyId: CurrencyId, statusCode: "DRAFT");
        var service = new CommercialFinanceApplicationService(db);

        var refused = await Assert.ThrowsAsync<FinanceConflictException>(() => service.CreateInvoiceAsync(
            BusinessUnitId, order.Id, "manual-draft-invoice", new CreateInvoiceRequest(null, null, null), "invoice-maker@test"));

        Assert.Contains("must be confirmed", refused.Message);
        Assert.Empty(await db.ReceivableDocuments.ToListAsync());
    }

    // ------------------------------------------------------------------------ test plumbing

    private static Order SeedOrder(ErpRfqAutomationContext db, long? currencyId, string statusCode = "SHIPPED")
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "AR Customer");
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId,
            Code = "SAR",
            CurrencyName = "Saudi Riyal",
            Symbol = "SAR",
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
            SetupCode = statusCode,
            SetupValue = statusCode,
            BusinessUnitId = BusinessUnitId,
            IsActive = true,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        var order = new Order
        {
            OrderNo = $"ORD-CUR-{Guid.NewGuid():N}",
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            StatusId = StatusId,
            CurrencyId = currencyId,
            SourceType = OrderSourceTypes.Manual,
            OrderDate = DateTime.UtcNow,
            SubTotal = 1000m,
            DiscountAmount = 0m,
            TaxAmount = 0m,
            TotalAmount = 1000m,
            BalanceAmount = 1000m,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true,
            OrderItems =
            [
                new OrderItem
                {
                    ProductId = ProductId,
                    Description = "Invoice product",
                    Quantity = 1m,
                    UnitPrice = 1000m,
                    Discount = 0m,
                    TaxAmount = 0m,
                    TotalAmount = 1000m,
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

    private const long BusinessUnitId = 95_101;
    private const long CustomerId = 95_102;
    private const long CurrencyId = 95_103;
    private const long ProductId = 95_104;
    private const long StatusId = 95_105;
}
