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
    [Fact]
    public async Task QuoteCalculator_IncludesDisplayedTaxInPersistedTotals()
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
                    TaxAmount = 19m,
                    TotalAmount = 1m
                }
            ]
        });

        Assert.Equal(219m, quote.TotalAmount);
        Assert.Equal(219m, Assert.Single(quote.QuoteItems).TotalAmount);
        Assert.Equal(2, (await db.Quotes.SingleAsync()).FinancialCalculationVersion);
    }

    [Fact]
    public async Task QuoteConversion_RecomputesHeaderTotalsAndIsIdempotent()
    {
        using var database = new TestDb();
        await using var db = database.ContextFor(BusinessUnitId);
        SeedCommercialParents(db);
        var quote = new Quote
        {
            QuoteNo = "QT-FIN-1",
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
                    TotalAmount = 209m,
                    CreatedBy = "test",
                    CreatedDate = DateTime.UtcNow
                }
            ]
        };
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
        SeedCommercialParents(db);
        var quote = new Quote
        {
            QuoteNo = "QT-FIN-LEGACY",
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
                    TotalAmount = 100m,
                    CreatedBy = "test",
                    CreatedDate = DateTime.UtcNow
                }
            ]
        };
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
        SeedCommercialParents(db);
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("QuotationTemplate");
        for (var column = 1; column <= 11; column++)
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
        await using var stream = new MemoryStream(await package.GetAsByteArrayAsync());
        var uploader = new QuotationUploaderService(
            db, NullLogger<QuotationUploaderService>.Instance);

        var upload = await uploader.UploadTemplateAsync(stream, BusinessUnitId, "test");
        Assert.True(upload.Success, upload.Message);
        var quote = await db.Quotes.Include(q => q.QuoteItems).SingleAsync();
        var order = await new OrderService(new OrderRepository(db), db)
            .CreateOrderFromQuoteAsync(quote.Id, BusinessUnitId);

        Assert.Equal(2, quote.FinancialCalculationVersion);
        Assert.Equal(105m, quote.TotalAmount);
        Assert.Equal(105m, Assert.Single(quote.QuoteItems).TotalAmount);
        Assert.Equal(105m, order.TotalAmount);
        Assert.Equal(105m, Assert.Single(order.Items).TotalAmount);
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

    private static void SeedCommercialParents(ErpRfqAutomationContext db)
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
        db.SaveChanges();
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

    private const long BusinessUnitId = 73_001;
    private const long CustomerId = 73_002;
    private const long CurrencyId = 73_003;
    private const long ProductId = 73_004;
    private const long DraftStatusId = 73_005;
    private const long UnpaidStatusId = 73_006;
    private const long OrderedStatusId = 73_007;
    private const long QuoteDraftStatusId = 42;
}
