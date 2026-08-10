using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

public sealed class OrderFinancialIntegrityTests
{
    /// <summary>
    /// R17 changed this test's numbers, and the change is the point.
    ///
    /// <para>It used to submit <c>TaxAmount = 19</c> and assert 219, which certified that the line
    /// tax a CLIENT typed reaches the persisted totals. That is exactly the defect the finance panel
    /// found: nothing derived output tax, so whatever arrived on the wire — 19, zero, or nothing at
    /// all — became the tax on a customer document.</para>
    ///
    /// <para>The submitted 19 is now discarded and the tax is derived from the business unit's
    /// <c>OutputTaxRatePercent</c>, which defaults to the KSA standard 15%:</para>
    /// <code>
    ///   base   2 x 100.00        = 200.00
    ///   tax    200.00 x 15%      =  30.00      (was: 19.00, because someone typed 19)
    ///   line   200.00 + 30.00    = 230.00      (was: 219.00)
    /// </code>
    /// <para>The invariant the test was written to protect — that the line's tax IS included in the
    /// persisted line and header totals rather than being displayed and dropped — is unchanged and
    /// still asserted. Only the source of the number moved, from the request to the policy.</para>
    /// </summary>
    [Fact]
    public async Task QuoteCalculator_IncludesDerivedTaxInPersistedTotals()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedCommercialParents(db);
        var service = new QuoteService(db, null!, null!);

        var quote = await service.CreateQuoteAsync(new QuoteCreateRequestDTO
        {
            QuoteNo = "QT-FIN-TAX",
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            CreatedBy = "test",
            QuoteDate = DateTime.UtcNow,
            TotalAmount = 1m,
            QuoteItems =
            [
                new QuoteItemCreateRequestDTO
                {
                    ProductId = ProductId,
                    ItemDescription = "Taxed item",
                    Quantity = 2m,
                    UnitPrice = 100m,
                    // Ignored. Kept in the request precisely to prove it is ignored.
                    TaxAmount = 19m,
                    TotalAmount = 1m
                }
            ]
        });

        Assert.Equal(230m, quote.TotalAmount);
        var line = Assert.Single(quote.QuoteItems);
        Assert.Equal(230m, line.TotalAmount);
        Assert.Equal(30m, line.TaxAmount);
        Assert.NotEqual(19m, line.TaxAmount);
        Assert.Equal(15m, line.TaxRatePercentApplied);
        Assert.Equal(2, (await db.Quotes.SingleAsync()).FinancialCalculationVersion);
    }

    [Fact]
    public async Task QuoteConversion_RecomputesHeaderTotalsAndIsIdempotent()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        var quote = new Quote
        {
            QuoteNo = "QT-FIN-1",
            Rfqid = rfq.Id,
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            CurrencyId = CurrencyId,
            FinancialCalculationVersion = 2,
            TotalAmount = 189m,
            QuoteDate = DateTime.UtcNow,
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow,
            QuoteItems =
            [
                new QuoteItem
                {
                    ProductId = ProductId,
                    ItemDescription = "Controlled item",
                    Quantity = 2m,
                    UnitPrice = 100m,
                    Discount = 10m,
                    TaxAmount = 19m,
                    // R17: 19.00 on a 190.00 net base is 10%. The rate is stamped because the order
                    // gate now refuses a quote whose tax was never DERIVED, and a line carrying a
                    // hand-written amount with no rate behind it is exactly that. See
                    // OrderFromQuoteTaxGateTests. The subject of this test — header totals and
                    // idempotency — is unchanged.
                    TaxRatePercentApplied = 10m,
                    TotalAmount = 209m,
                    CreatedBy = "test",
                    CreatedDate = DateTime.UtcNow
                }
            ]
        };
        quote.InheritCommercialIdentity(rfq);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        var service = new OrderService(new OrderRepository(db), db);

        var first = await service.CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);
        var replay = await service.CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);

        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(200m, first.SubTotal);
        Assert.Equal(30m, first.DiscountAmount);
        Assert.Equal(19m, first.TaxAmount);
        Assert.Equal(189m, first.TotalAmount);
        Assert.Equal(189m, first.BalanceAmount);
        Assert.Equal(209m, Assert.Single(first.Items).TotalAmount);
        Assert.Equal(CurrencyId, (await db.Orders.SingleAsync()).CurrencyId);
        Assert.Equal(OrderedStatusId, (await db.Quotes.SingleAsync()).StatusId);
        Assert.Single(await db.Orders.ToListAsync());
    }

    [Fact]
    public async Task LegacyTaxExclusiveQuote_ConvertsWithoutInventingHeaderDiscount()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        var quote = new Quote
        {
            QuoteNo = "QT-FIN-LEGACY",
            Rfqid = rfq.Id,
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            CurrencyId = CurrencyId,
            FinancialCalculationVersion = 1,
            TotalAmount = 100m,
            QuoteDate = DateTime.UtcNow,
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow,
            QuoteItems =
            [
                new QuoteItem
                {
                    ProductId = ProductId,
                    ItemDescription = "Legacy taxed item",
                    Quantity = 1m,
                    UnitPrice = 100m,
                    Discount = 0m,
                    TaxAmount = 5m,
                    // R17: 5.00 on a 100.00 base is 5%. Stamped for the same reason as above — the
                    // order gate refuses an underived line, and this test is about the legacy
                    // tax-exclusive header arithmetic, not about the gate.
                    TaxRatePercentApplied = 5m,
                    TotalAmount = 100m,
                    CreatedBy = "test",
                    CreatedDate = DateTime.UtcNow
                }
            ]
        };
        quote.InheritCommercialIdentity(rfq);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        var service = new OrderService(new OrderRepository(db), db);

        var order = await service.CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);

        Assert.Equal(100m, order.SubTotal);
        Assert.Equal(0m, order.DiscountAmount);
        Assert.Equal(5m, order.TaxAmount);
        Assert.Equal(105m, order.TotalAmount);
        Assert.Equal(105m, order.BalanceAmount);
        Assert.Equal(105m, Assert.Single(order.Items).TotalAmount);
    }

    [Fact]
    public async Task SpreadsheetUpload_StampsTaxInclusiveVersionAndConvertsWithoutDoubleTax()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        var rfq = SeedCommercialParents(db);
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("QuotationTemplate");
        for (var column = 1; column <= 14; column++)
            sheet.Cells[1, column].Value = $"Column {column}";
        sheet.Cells[2, 1].Value = "QT-FIN-UPLOAD";
        sheet.Cells[2, 2].Value = "Finance Customer";
        sheet.Cells[2, 3].Value = "2026-07-23";
        sheet.Cells[2, 4].Value = "2026-08-23";
        sheet.Cells[2, 5].Value = "AED";
        sheet.Cells[2, 6].Value = "Controlled item";
        sheet.Cells[2, 7].Value = 1;
        sheet.Cells[2, 8].Value = 100;
        sheet.Cells[2, 9].Value = 5;
        sheet.Cells[2, 10].Value = 0;
        // The upload names the inquiry it answers, so the quotation inherits a real case and the
        // sales order can inherit one from it. Without column 14 the import is refused outright —
        // see QuotationUploaderCommercialCaseTests.
        sheet.Cells[2, 14].Value = rfq.Rfqno;
        await using var stream = new MemoryStream(await package.GetAsByteArrayAsync());
        var uploader = new QuotationUploaderService(
            db, NullLogger<QuotationUploaderService>.Instance);

        var upload = await uploader.UploadTemplateAsync(stream, BusinessUnitId, "test");
        Assert.True(upload.Success, upload.Message);
        var quote = await db.Quotes.Include(q => q.QuoteItems).SingleAsync();
        // KNOWN GAP, asserted rather than hidden: the template uploader takes the tax column
        // verbatim and never derives it, so it leaves TaxRatePercentApplied null and the R17 order
        // gate would refuse this quote. Stamping the rate those numbers imply (5.00 on 100.00 = 5%)
        // keeps this test on its own subject — no double tax on conversion. When the uploader is
        // taught to derive, the Assert.Null below fails and these two lines come out.
        Assert.Null(Assert.Single(quote.QuoteItems).TaxRatePercentApplied);
        quote.QuoteItems.Single().TaxRatePercentApplied = 5m;
        await db.SaveChangesAsync();
        var order = await new OrderService(new OrderRepository(db), db)
            .CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);

        Assert.Equal(2, quote.FinancialCalculationVersion);
        Assert.Equal(105m, quote.TotalAmount);
        Assert.Equal(105m, Assert.Single(quote.QuoteItems).TotalAmount);
        Assert.Equal(105m, order.TotalAmount);
        Assert.Equal(105m, Assert.Single(order.Items).TotalAmount);
        Assert.Equal(rfq.CommercialCaseId, order.CommercialCaseId);
    }

    [Fact]
    public async Task Database_RejectsSecondOrderForSameTenantQuote()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedCommercialParents(db);
        var quote = new Quote
        {
            QuoteNo = "QT-FIN-2",
            CustomerId = CustomerId,
            BusinessUnitId = BusinessUnitId,
            TotalAmount = 10m,
            QuoteDate = DateTime.UtcNow,
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow
        };
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        db.Orders.AddRange(NewOrder("ORD-1", quote.Id), NewOrder("ORD-2", quote.Id));

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private static Order NewOrder(string number, long quoteId) => new()
    {
        OrderNo = number,
        QuoteId = quoteId,
        CustomerId = CustomerId,
        BusinessUnitId = BusinessUnitId,
        StatusId = DraftStatusId,
        OrderDate = DateTime.UtcNow,
        TotalAmount = 10m,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow,
        IsActive = true
    };

    /// <summary>
    /// The commercial parents a quote and an order need, INCLUDING the inquiry the spine starts
    /// at. A quote raised against no RFQ carries no commercial case, and a sales order can no
    /// longer inherit one from it — which is the invariant, not a fixture inconvenience. The
    /// returned RFQ is what production hands to <c>Quote.InheritCommercialIdentity</c>.
    /// </summary>
    private static Rfq SeedCommercialParents(ErpRfqAutomationContext db)
    {
        Seed.EnsureBusinessUnit(db, BusinessUnitId);
        Seed.Customer(db, CustomerId, BusinessUnitId, "Finance Customer");
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
            ProductName = "Controlled item",
            PartNo = "FIN-ITEM-1",
            Buid = BusinessUnitId,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow,
            IsActive = true
        });
        db.SetupMasters.AddRange(
            Setup(DraftStatusId, "OrderStatus", "DRAFT"),
            Setup(UnpaidStatusId, "PaymentStatus", "UNPAID"),
            Setup(QuoteDraftStatusId, "QuoteStatus", "DRAFT"),
            Setup(OrderedStatusId, "QuoteStatus", "ORDERED"));
        var lead = Seed.Lead(db, LeadId, BusinessUnitId, buyersName: "Finance Buyer");
        // Saved first: the commercial case is allocated during SaveChanges, and the RFQ has to
        // inherit a real one.
        db.SaveChanges();
        lead.ResolveCommercialIdentity(CustomerId, null, "CONFIRMED");

        var rfq = new Rfq
        {
            Id = RfqId,
            Rfqno = "RFQ-FIN-1",
            RecDate = DateTime.UtcNow,
            BusinessUnitId = BusinessUnitId,
            LeadId = lead.Id,
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        db.Rfqs.Add(rfq);
        db.SaveChanges();
        return rfq;
    }

    private static SetupMaster Setup(long id, string type, string code) => new()
    {
        SetupId = id,
        SetupType = type,
        SetupCode = code,
        SetupValue = code,
        BusinessUnitId = BusinessUnitId,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    private const long LeadId = 73_051;
    private const long RfqId = 73_061;
    private const long BusinessUnitId = 73_001;
    private const long CustomerId = 73_002;
    private const long CurrencyId = 73_003;
    private const long ProductId = 73_004;
    private const long DraftStatusId = 73_005;
    private const long UnpaidStatusId = 73_006;
    private const long OrderedStatusId = 73_007;
    private const long QuoteDraftStatusId = 42;
}
