using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// R17. Order-from-quote may not turn "tax was never derived" into zero VAT.
///
/// The defect
/// ----------
/// <c>OutputTaxFormula</c> makes null mean NOT CONFIGURED — refuse to issue — and both the quote PDF
/// and the quote send enforce it through <c>QuoteService.TaxDerivationBlocker</c>. Order creation
/// from a quote applied no gate at all and read the same nulls as <c>TaxAmount ?? 0m</c>, so the
/// sales order — and the AR invoice pro-rated from it — carried SAR 0.00 VAT on a standard-rated
/// supply. Under KSA law a document with no VAT separately stated is deemed VAT-INCLUSIVE, so the
/// seller owes 15/115 ≈ 13.04% of the price out of its own margin.
///
/// What these tests prove
/// ----------------------
/// That null and zero are different values on this path. A line that was never derived blocks; a
/// line genuinely derived at 0% (a zero-rated export) does not. Removing the gate fails
/// <see cref="Quote_whose_tax_was_never_derived_cannot_become_an_order"/>; restoring
/// <c>?? 0m</c> fails it too, because the assertion is that no order exists afterwards.
/// </summary>
public sealed class OrderFromQuoteTaxGateTests
{
    [Fact]
    public async Task Quote_whose_tax_was_never_derived_cannot_become_an_order()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        // TaxAmount null and TaxRatePercentApplied null: nothing ever derived this line's tax.
        var quote = SeedQuote(db, rfq, "QT-TAX-NULL", taxAmount: null, taxRatePercentApplied: null);
        await db.SaveChangesAsync();
        var service = new OrderService(new OrderRepository(db), db);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId));

        Assert.Contains("QT-TAX-NULL", error.Message);
        Assert.Contains("no derived tax", error.Message, StringComparison.OrdinalIgnoreCase);
        // The order is REFUSED, not created with zero. This is the assertion the old `?? 0m`
        // could never satisfy.
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Quote_in_a_tenant_with_no_output_tax_rate_cannot_become_an_order()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        var quote = SeedQuote(db, rfq, "QT-TAX-NORATE", taxAmount: null, taxRatePercentApplied: null);
        await db.SaveChangesAsync();
        await new CommercialMatchingPolicyService(db).UpdateAsync(BusinessUnitId, 9_001,
            "policy@example.com", "clear-rate", new UpdateCommercialMatchingPolicyCommand(
                SupplierInputTaxRecoverablePercent: null, OutputTaxRatePercent: null,
                ClearOutputTaxRate: true, PriceTolerancePercent: null,
                PriceToleranceMinimumAmount: null, QuantityTolerancePercent: null,
                Reason: "We do not know our output tax rate yet."));
        db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new OrderService(new OrderRepository(db), db)
                .CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId));

        // The same sentence the quote screen shows, pointing at the setting that fixes it.
        Assert.Contains("no output tax rate configured", error.Message);
        Assert.Contains("Commercial Policy settings", error.Message);
        Assert.Empty(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task Quote_with_derived_tax_becomes_an_order_that_states_the_vat()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        // 2 x 100.00 = 200.00 base, derived at the KSA standard 15% = 30.00.
        var quote = SeedQuote(db, rfq, "QT-TAX-DERIVED", taxAmount: 30m, taxRatePercentApplied: 15m,
            headerTotal: 230m, lineTotal: 230m);
        await db.SaveChangesAsync();

        var order = await new OrderService(new OrderRepository(db), db)
            .CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);

        Assert.Equal(30m, order.TaxAmount);
        Assert.NotEqual(0m, order.TaxAmount);
        Assert.Equal(230m, order.TotalAmount);
        Assert.Equal(30m, Assert.Single(order.Items).TaxAmount);
    }

    /// <summary>
    /// Zero is a positive assertion and must still pass. A zero-rated export derives 0 at a stated
    /// 0% rate, which is a different record from a line nobody ever taxed — the whole point of
    /// keeping null null.
    /// </summary>
    [Fact]
    public async Task Zero_rated_export_derived_at_zero_still_becomes_an_order()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        var quote = SeedQuote(db, rfq, "QT-TAX-ZERO", taxAmount: 0m, taxRatePercentApplied: 0m,
            headerTotal: 200m, lineTotal: 200m);
        var line = quote.QuoteItems.Single();
        line.TaxCategory = QuoteLineTaxCategories.ZeroRatedExport;
        line.TaxCategoryReason = "Exported to Bahrain; bill of lading on file.";
        await db.SaveChangesAsync();

        var order = await new OrderService(new OrderRepository(db), db)
            .CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);

        Assert.Equal(0m, order.TaxAmount);
        Assert.Equal(200m, order.TotalAmount);
    }

    /// <summary>
    /// A category that departs from the standard rate without a stated reason is refused on the
    /// order path for the same reason it is refused on the send path — the evidence is the whole
    /// justification for not charging 15%.
    /// </summary>
    [Fact]
    public async Task Zero_rated_line_with_no_stated_reason_cannot_become_an_order()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        var quote = SeedQuote(db, rfq, "QT-TAX-NOREASON", taxAmount: 0m, taxRatePercentApplied: 0m,
            headerTotal: 200m, lineTotal: 200m);
        quote.QuoteItems.Single().TaxCategory = QuoteLineTaxCategories.ZeroRatedExport;
        await db.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new OrderService(new OrderRepository(db), db)
                .CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId));

        Assert.Contains("stated reason", error.Message);
        Assert.Empty(await db.Orders.ToListAsync());
    }

    /// <summary>
    /// The same refusal on the route that is actually live. Direct quote conversion is retired
    /// (<c>POST api/Order/from-quote</c> returns 409), so the production path to a sales order is
    /// customer PO → award → convert, and it inherits the quote line's tax through
    /// <c>CustomerAwardLineAllocation.TaxSnapshot</c>, which is computed as <c>TaxAmount ?? 0m</c>.
    /// Gating only the retired path would have left the money leak exactly where it was.
    /// </summary>
    [Fact]
    public async Task Award_over_a_quote_line_whose_tax_was_never_derived_cannot_become_an_order()
    {
        using var fixture = new CustomerAwardTestFixture();
        var line = await fixture.Context.QuoteItems.SingleAsync(x => x.Id == fixture.QuoteItemId);
        line.TaxRatePercentApplied = null;
        await fixture.Context.SaveChangesAsync();
        fixture.Context.ChangeTracker.Clear();

        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-untaxed", 10m);
        var award = await fixture.CreateAwardAsync(purchaseOrder, "award-untaxed", 10m);
        var confirmed = await fixture.Service.ConfirmAwardAsync(fixture.BusinessUnitId, award.Id,
            "confirm-untaxed", "corr-confirm-untaxed", new(award.Version), "tests");

        var error = await Assert.ThrowsAsync<CustomerAwardConflictException>(() =>
            fixture.Service.ConvertToOrderAsync(fixture.BusinessUnitId, award.Id, "convert-untaxed",
                "corr-convert-untaxed", new(confirmed.Version), "tests"));

        Assert.Contains("no derived tax", error.Message, StringComparison.OrdinalIgnoreCase);
        fixture.Context.ChangeTracker.Clear();
        Assert.Empty(await fixture.Context.Orders.ToListAsync());
    }

    private static Quote SeedQuote(ErpRfqAutomationContext db, Rfq rfq, string quoteNo,
        decimal? taxAmount, decimal? taxRatePercentApplied, decimal headerTotal = 200m,
        decimal lineTotal = 200m)
    {
        var quote = new Quote
        {
            QuoteNo = quoteNo,
            Rfqid = rfq.Id,
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            CurrencyId = CurrencyId,
            FinancialCalculationVersion = 2,
            TotalAmount = headerTotal,
            QuoteDate = DateTime.UtcNow,
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow,
            QuoteItems =
            [
                new QuoteItem
                {
                    ProductId = ProductId,
                    ItemDescription = "Standard-rated item",
                    Quantity = 2m,
                    UnitPrice = 100m,
                    Discount = 0m,
                    TaxAmount = taxAmount,
                    TaxRatePercentApplied = taxRatePercentApplied,
                    TotalAmount = lineTotal,
                    CreatedBy = "test",
                    CreatedDate = DateTime.UtcNow
                }
            ]
        };
        quote.InheritCommercialIdentity(rfq);
        db.Quotes.Add(quote);
        return quote;
    }

    private static Rfq SeedCommercialParents(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Tax Gate Customer");
        db.Currencies.Add(new Currency
        {
            Id = CurrencyId, Code = "SAR", CurrencyName = "Saudi Riyal", Symbol = "SAR",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "test",
            CreatedOn = DateTime.UtcNow, BusinessUnitId = BusinessUnitId
        });
        db.Products.Add(new Product
        {
            Id = ProductId, ProductName = "Standard-rated item", PartNo = "TAX-ITEM-1",
            Buid = BusinessUnitId, CreatedBy = "test", CreatedOn = DateTime.UtcNow, IsActive = true
        });
        db.SetupMasters.AddRange(
            Setup(DraftStatusId, "OrderStatus", "DRAFT"),
            Setup(UnpaidStatusId, "PaymentStatus", "UNPAID"),
            Setup(QuoteDraftStatusId, "QuoteStatus", "DRAFT"),
            Setup(OrderedStatusId, "QuoteStatus", "ORDERED"));
        var lead = Seed.Lead(db, LeadId, BusinessUnitId, buyersName: "Tax Gate Buyer");
        db.SaveChanges();
        lead.ResolveCommercialIdentity(CustomerId, null, "CONFIRMED");
        var rfq = new Rfq
        {
            Id = RfqId, Rfqno = "RFQ-TAX-1", RecDate = DateTime.UtcNow,
            BusinessUnitId = BusinessUnitId, LeadId = lead.Id, CreatedBy = "test",
            CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        db.Rfqs.Add(rfq);
        db.SaveChanges();
        return rfq;
    }

    private static SetupMaster Setup(long id, string type, string code) => new()
    {
        SetupId = id, SetupType = type, SetupCode = code, SetupValue = code,
        BusinessUnitId = BusinessUnitId, IsActive = true, CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    private const long BusinessUnitId = 74_001;
    private const long CustomerId = 74_002;
    private const long CurrencyId = 74_003;
    private const long ProductId = 74_004;
    private const long DraftStatusId = 74_005;
    private const long UnpaidStatusId = 74_006;
    private const long OrderedStatusId = 74_007;
    private const long LeadId = 74_051;
    private const long RfqId = 74_061;
    private const long QuoteDraftStatusId = 42;
}
