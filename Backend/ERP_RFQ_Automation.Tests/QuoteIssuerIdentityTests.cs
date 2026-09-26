using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using UglyToad.PdfPig;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A quotation must say who sent it, and must never say the customer did.
///
/// <para><b>The defect these pin.</b> The seller's email was resolved as
/// <c>config?.CompanyEmail ?? quote.Rfq?.Lead?.Clientemail ?? "sales@company.com"</c>. The middle
/// term is the address the ENQUIRY ARRIVED FROM. So a tenant whose seller email had never been
/// filled in sent its customer a quotation naming that customer as the sender — beside
/// "123 Business Rd, Tech City, 54321" and "+1 800 555 0199" on a Saudi deal. Nothing about the
/// document looked wrong.</para>
///
/// <para>And it was not the rare case it sounds: <c>TenantBaselineSeeder</c> deliberately leaves
/// <c>CompanyEmail</c> null, reasoning that "a blank sender line is a visible omission somebody
/// will fix". It was never blank, so nobody ever fixed it. Every provisioned tenant was one
/// unfilled field away from this.</para>
/// </summary>
public class QuoteIssuerIdentityTests
{
    private const long Tenant = 9701;

    [Fact]
    public async Task The_customers_own_address_is_never_printed_as_the_sender()
    {
        // THE REGRESSION. The lead carries the buyer's address, exactly as the mailbox recorded
        // it; the seller has its own. Neither the buyer's address nor the old placeholders may
        // appear as the issuer.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, buyerEmail: "procurement@aramco.example");

        var service = new QuoteService(context, null!, Configured());
        var text = PdfText(await service.GenerateQuotePdfAsync(quoteId, Tenant));

        Assert.DoesNotContain("procurement@aramco.example", text);
        Assert.DoesNotContain("sales@company.com", text);
        Assert.DoesNotContain("123 Business Rd", text);
        Assert.DoesNotContain("+1 800 555 0199", text);
        Assert.Contains("sales@noorandsons.example", text);
    }

    [Fact]
    public async Task A_configured_seller_with_no_email_still_never_borrows_the_customers()
    {
        // THE SHAPE PRODUCTION ACTUALLY HAS, and the one a happier fixture misses.
        // TenantBaselineSeeder writes a QuoteConfiguration row and deliberately leaves
        // CompanyEmail null, reasoning that the PDF would then show "a blank sender line somebody
        // will fix". It showed the CUSTOMER'S address instead. So the row exists, the address and
        // telephone are right, and exactly one column is empty — which is precisely when the old
        // `??` chain reached for Lead.Clientemail.
        //
        // The document must simply carry no email line. It must not refuse either: an address and
        // a telephone are enough to reach a seller.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, buyerEmail: "procurement@aramco.example");

        var service = new QuoteService(context, null!, new StubConfig(new QuoteConfiguration
        {
            BusinessUnitId = Tenant,
            CompanyAddress = "King Fahd Road, Al Khobar 34423",
            CompanyPhone = "+966 13 800 0000",
            CompanyEmail = null
        }));
        var text = PdfText(await service.GenerateQuotePdfAsync(quoteId, Tenant));

        Assert.DoesNotContain("procurement@aramco.example", text);
        Assert.DoesNotContain("E: ", text); // no sender-email line at all, not an empty one
        Assert.Contains("King Fahd Road", text);
    }

    [Fact]
    public async Task A_quote_that_cannot_say_how_to_reach_the_sender_is_refused_not_invented()
    {
        // Fail closed, and say which screen fixes it. A refusal is visible in one second; a
        // plausible wrong document goes to a buyer and cannot be recalled.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, buyerEmail: "procurement@aramco.example");

        var service = new QuoteService(context, null!, Unconfigured());

        var refusal = await Assert.ThrowsAsync<QuoteIssuerIdentityMissingException>(
            () => service.GenerateQuotePdfAsync(quoteId, Tenant));
        Assert.Contains("Quote Format", refusal.Message);
    }

    [Fact]
    public async Task A_missing_registration_is_omitted_from_the_document_and_refused_at_the_send()
    {
        // This used to print "VAT: not on file" on the face of the customer's copy, reasoning that
        // silence was worse. Driving QT-0926-0001 (D28) showed what that reads as to a buyer: a
        // seller advertising that it does not know its own registrations. The document now omits
        // the line; the send-readiness check is where the gap is named, to the seller, with the
        // screen that fixes it (QuoteSellerRegistrationTests). The preview still renders.
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, buyerEmail: "procurement@aramco.example");

        var service = new QuoteService(context, null!, Configured());
        var text = PdfText(await service.GenerateQuotePdfAsync(quoteId, Tenant));

        // The registration gap is gone from the document. (This fixture has no customer, so the
        // address block correctly reads "Address not on file" — a different, true statement.)
        Assert.DoesNotContain("CR: not on file", text);
        Assert.DoesNotContain("VAT: not on file", text);
        Assert.DoesNotContain("VAT:", text);
        Assert.DoesNotContain("CR:", text);
        Assert.Contains((await service.EvaluateSendReadinessAsync(quoteId, Tenant)).Blockers,
            x => x.Code == "SELLER_REGISTRATION_INCOMPLETE");
    }

    [Fact]
    public async Task A_registered_seller_prints_its_VAT_number()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var quoteId = SeedQuote(context, buyerEmail: "procurement@aramco.example",
            sellerVat: "300000000000003");

        var service = new QuoteService(context, null!, Configured());
        var text = PdfText(await service.GenerateQuotePdfAsync(quoteId, Tenant));

        Assert.Contains("300000000000003", text);
        Assert.DoesNotContain("VAT: not on file", text);
    }

    // ------------------------------------------------------------------------ test plumbing

    private static long SeedQuote(
        ErpRfqAutomationContext context, string buyerEmail, string? sellerVat = null)
    {
        var unit = Seed.EnsureBusinessUnit(context, Tenant);
        unit.TaxRegistrationNumber = sellerVat;

        var lead = Seed.Lead(context, 97011, Tenant);
        lead.Rfqno = "SA-RFQ-2026-771";
        lead.Clientemail = buyerEmail; // the mailbox's record of who wrote in
        context.Rfqs.Add(new Rfq
        {
            Id = 97012,
            Rfqno = "SA-RFQ-2026-771",
            LeadId = lead.Id,
            BusinessUnitId = Tenant,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });

        // A currency on record: the document gate now refuses a currency-less quote (it once
        // printed "USD" for a null currency), and these tests are about the SELLER identity on
        // the PDF, not the currency, so the quote must carry one to reach the rule under test.
        context.Currencies.Add(new Currency
        {
            Id = 97060, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        var quote = new Quote
        {
            Id = 97013,
            QuoteNo = "QT-ID-9701",
            Rfqid = 97012,
            BusinessUnitId = Tenant,
            CurrencyId = 97060,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 57.50m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            Id = 97014,
            ItemDescription = "Gasket spiral wound",
            Quantity = 5m,
            UnitOfMeasure = "EA",
            UnitPrice = 10m,
            // R17: the PDF gate refuses a line whose output tax was never derived.
            TaxAmount = 7.50m,
            TaxCategory = ERP_RFQ_Automation.OrderToCash.QuoteLineTaxCategories.Standard,
            TaxRatePercentApplied = 15m,
            TotalAmount = 57.50m,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        context.SaveChanges();

        // R5: rendering the commercial document requires a recorded price attestation. These
        // tests are about the issuer block, not that gate, so they satisfy it explicitly.
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quote.Id, Tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-TEST", null, "tests", default).GetAwaiter().GetResult();
        return quote.Id;
    }

    private static IQuoteConfigurationRepository Configured() => new StubConfig(new QuoteConfiguration
    {
        BusinessUnitId = Tenant,
        CompanyAddress = "King Fahd Road, Al Khobar 34423",
        CompanyPhone = "+966 13 800 0000",
        CompanyEmail = "sales@noorandsons.example"
    });

    private static IQuoteConfigurationRepository Unconfigured() => new StubConfig(null);

    private sealed class StubConfig(QuoteConfiguration? config) : IQuoteConfigurationRepository
    {
        public Task<QuoteConfiguration?> GetByBusinessUnitIdAsync(long businessUnitId)
            => Task.FromResult(config);
        public Task AddAsync(QuoteConfiguration c) => Task.CompletedTask;
        public Task UpdateAsync(QuoteConfiguration c) => Task.CompletedTask;
    }

    private static string PdfText(byte[] pdf)
    {
        using var document = PdfDocument.Open(pdf);
        return string.Join("\n", document.GetPages().Select(page => page.Text));
    }
}
