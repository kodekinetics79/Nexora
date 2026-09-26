using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The customer's copy prints only what is true, and a Saudi quotation does not leave without
/// the seller's registrations.
///
/// <para><b>The defect (D28, D29).</b> QT-0926-0001's PDF read "CR: not on file" and "VAT: not on
/// file" in the seller header, and "N/A" plus a stray "," in BILL TO / SHIP TO because the customer
/// had no address. A gap named on the customer's copy is the seller advertising its own omission;
/// a gap that BLOCKS the send is the seller being told before the customer is.</para>
///
/// <para>So: registration lines are omitted when unset, address blocks print only the parts that
/// exist ("Address not on file" when none), and send-readiness carries
/// <c>SELLER_REGISTRATION_INCOMPLETE</c> naming the screen where CR and VAT live.</para>
/// </summary>
public class QuoteSellerRegistrationTests
{
    private const long Tenant = 98_601;
    private const long DraftStatusId = 98_610;

    [Fact]
    public async Task Missing_CR_and_VAT_block_the_send_and_name_where_they_live()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, cr: null, vat: null, customerAddress: true);

        var readiness = await new QuoteService(context, null!, Configured()).EvaluateSendReadinessAsync(quoteId, Tenant);

        Assert.False(readiness.CanSend);
        var blocker = Assert.Single(readiness.Blockers, x => x.Code == "SELLER_REGISTRATION_INCOMPLETE");
        Assert.Contains("CR", blocker.Message);
        Assert.Contains("VAT", blocker.Message);
        Assert.Contains("not on file", blocker.Message);
        Assert.Equal("Setup → Business Units", blocker.SetupLabel);
        Assert.Equal("/setup/business-unit", blocker.SetupPath);
    }

    [Fact]
    public async Task A_registered_seller_is_not_blocked_on_registration()
    {
        // THE CONTROL: a blocker that always fires would pass the test above and stop every send.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, cr: "1010123456", vat: "300000000000003", customerAddress: true);

        var readiness = await new QuoteService(context, null!, Configured()).EvaluateSendReadinessAsync(quoteId, Tenant);

        Assert.DoesNotContain(readiness.Blockers, x => x.Code == "SELLER_REGISTRATION_INCOMPLETE");
    }

    [Fact]
    public void The_blocker_names_only_what_is_missing()
    {
        Assert.Null(QuoteService.SellerRegistrationBlocker("1010123456", "300000000000003"));

        var vatOnly = QuoteService.SellerRegistrationBlocker("1010123456", null);
        Assert.NotNull(vatOnly);
        Assert.Contains("VAT registration number is not on file", vatOnly!.Value.Message);
        Assert.DoesNotContain("CR", vatOnly.Value.Message.Replace("Setup", string.Empty));

        var crOnly = QuoteService.SellerRegistrationBlocker(" ", "300000000000003");
        Assert.NotNull(crOnly);
        Assert.Contains("commercial registration (CR) number is not on file", crOnly!.Value.Message);
        Assert.DoesNotContain("VAT", crOnly.Value.Message);
    }

    [Fact]
    public async Task The_document_omits_registration_lines_that_are_unset_instead_of_printing_not_on_file()
    {
        // The rep's own preview must still render: the SEND is what the blocker refuses. But the
        // document never says "not on file" — a buyer's finance clerk reads that as a seller who
        // does not know its own registrations.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, cr: null, vat: null, customerAddress: true);

        var text = PdfText(await new QuoteService(context, null!, Configured()).GenerateQuotePdfAsync(quoteId, Tenant));

        Assert.DoesNotContain("not on file", text);
        Assert.DoesNotContain("CR:", text);
        Assert.DoesNotContain("VAT:", text);
    }

    [Fact]
    public async Task A_registered_seller_prints_both_registrations()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, cr: "1010123456", vat: "300000000000003", customerAddress: true);

        var text = PdfText(await new QuoteService(context, null!, Configured()).GenerateQuotePdfAsync(quoteId, Tenant));

        Assert.Contains("CR: 1010123456", text);
        Assert.Contains("VAT: 300000000000003", text);
    }

    [Fact]
    public void Address_blocks_print_only_the_parts_that_exist()
    {
        // The renderer's input model, without a PDF: what each block's lines are for each shape.
        Assert.Equal(new[] { "Address not on file" }, QuoteService.AddressLines(null, null, null, null));
        Assert.Equal(new[] { "Address not on file" }, QuoteService.AddressLines("", " ", null, ""));

        // City and country are one line, joined only when both exist — never ", Saudi Arabia".
        Assert.Equal(new[] { "Saudi Arabia" }, QuoteService.AddressLines(null, null, null, "Saudi Arabia"));
        Assert.Equal(new[] { "Jubail" }, QuoteService.AddressLines(null, null, "Jubail", null));
        Assert.Equal(new[] { "P.O. Box 11133", "Jubail Industrial City", "Jubail, Saudi Arabia" },
            QuoteService.AddressLines("P.O. Box 11133", "Jubail Industrial City", "Jubail", "Saudi Arabia"));
    }

    [Fact]
    public async Task A_customer_with_no_address_gets_neither_NA_nor_a_stray_comma_on_the_document()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, cr: "1010123456", vat: "300000000000003", customerAddress: false);

        var text = PdfText(await new QuoteService(context, null!, Configured()).GenerateQuotePdfAsync(quoteId, Tenant));

        Assert.DoesNotContain("N/A", text);
        Assert.Contains("Address not on file", text);
        Assert.Contains("Marafiq", text);
    }

    // ------------------------------------------------------------------------ test plumbing

    private static long SeedQuote(ErpRfqAutomationContext context, string? cr, string? vat, bool customerAddress)
    {
        var unit = Seed.EnsureBusinessUnit(context, Tenant);
        unit.LegalName = "Noor and Sons Trading Co.";
        unit.CommercialRegistrationNumber = cr;
        unit.TaxRegistrationNumber = vat;
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = DraftStatusId, BusinessUnitId = Tenant, SetupType = "QuoteStatus",
            SetupCode = "DRAFT", SetupValue = "Draft", CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        context.Currencies.Add(new Currency
        {
            Id = 98_611, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        context.Customers.Add(new Customer
        {
            Id = 98_621, Buid = Tenant, Name = "Marafiq", ImageUrl = string.Empty,
            BillingAddressLine1 = customerAddress ? "P.O. Box 11133" : null,
            BillingCity = customerAddress ? "Jubail" : null,
            BillingCountry = customerAddress ? "Saudi Arabia" : null,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        var quote = new Quote
        {
            Id = 98_631,
            QuoteNo = "QT-REG-9861",
            BusinessUnitId = Tenant,
            StatusId = DraftStatusId,
            CustomerId = 98_621,
            CurrencyId = 98_611,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 57.50m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = 98_641,
            ItemDescription = "Gasket spiral wound",
            Quantity = 5m,
            UnitOfMeasure = "EA",
            UnitPrice = 10m,
            TaxAmount = 7.50m,
            TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
            TaxRatePercentApplied = 15m,
            TotalAmount = 57.50m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        context.SaveChanges();
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quote.Id, Tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-REG", null, "tests", default).GetAwaiter().GetResult();
        return quote.Id;
    }

    private static IQuoteConfigurationRepository Configured() => new StubConfig(new QuoteConfiguration
    {
        BusinessUnitId = Tenant,
        CompanyAddress = "King Fahd Road, Al Khobar 34423",
        CompanyPhone = "+966 13 800 0000",
        CompanyEmail = "sales@noorandsons.example"
    });

    private sealed class StubConfig(QuoteConfiguration? configuration) : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId) => Task.FromResult(configuration);
        public Task AddAsync(QuoteConfiguration c) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration c) => Task.CompletedTask;
    }

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }
}
