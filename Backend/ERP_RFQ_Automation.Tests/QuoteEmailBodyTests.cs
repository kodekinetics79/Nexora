using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the customer actually reads.
///
/// <para>The first quotation this product ever delivered, on 2026-09-04, said only "Dear Customer,
/// please find attached the quote #QT-0826-0002, thank you for your business." Everything a buyer
/// needs in order to act on it — what it is worth, how long it stands, and which of THEIR enquiries
/// it answers — was already known to the sender and left out of the message. A procurement desk
/// receiving that has to open the attachment to learn what it is for.</para>
///
/// <para>These tests pin the body a customer sees, not the fact that a send happened. They also pin
/// the omissions: a quote with no validity date must not render "Valid until:" followed by nothing,
/// which is worse than saying less.</para>
/// </summary>
public sealed class QuoteEmailBodyTests
{
    private const long Tenant = 96_001;

    [Fact]
    public async Task The_body_states_what_the_quote_is_worth_how_long_it_stands_and_which_enquiry_it_answers()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context, withCustomer: true, withCurrency: true, withValidity: true, customerReference: "PO-REQ-7781");
        var email = new RecordingEmailService();
        var service = new QuoteService(context, email, null!);
        await Attest(context, 96_011, Tenant);

        await service.SendQuoteEmailAsync(96_011, Tenant, "buyer@nexora.invalid");

        var delivery = await context.QuoteDeliveryRequests.AsNoTracking().SingleAsync();
        var body = delivery.Body ?? string.Empty;

        // The buyer's own number, so the quote can be matched to the request they raised without
        // opening the attachment. This one matters more than our reference does.
        Assert.Contains("PO-REQ-7781", body);
        // What it is worth, with the currency named — a bare number is not a price.
        Assert.Contains("USD", body);
        Assert.Contains("4,600.00", body);
        // How long it stands. A quotation with no stated expiry is a commercial exposure.
        Assert.Contains("Valid until", body);
        // Addressed to the company, not to nobody.
        Assert.Contains("Acme Trading", body);
        Assert.DoesNotContain("Dear Customer", body);
    }

    [Fact]
    public async Task A_missing_value_is_omitted_rather_than_printed_empty()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant + 1);
        Seed(context, withCustomer: false, withCurrency: false, withValidity: false, customerReference: null,
            tenant: Tenant + 1, quoteId: 96_012);
        var email = new RecordingEmailService();
        var service = new QuoteService(context, email, null!);
        await Attest(context, 96_012, Tenant + 1);

        await service.SendQuoteEmailAsync(96_012, Tenant + 1, "buyer@nexora.invalid");

        var delivery = await context.QuoteDeliveryRequests.AsNoTracking().SingleAsync();
        var body = delivery.Body ?? string.Empty;

        // The fallback greeting is used rather than "Dear ," with an empty name.
        Assert.Contains("Dear Customer", body);
        // None of the optional lines appear as a label with nothing after it.
        Assert.DoesNotContain("Your reference", body);
        Assert.DoesNotContain("Valid until", body);
        Assert.DoesNotContain("Total:", body);
        // The quote is still identified and the sender still named.
        Assert.Contains("Q-BODY-2", body);
        Assert.Contains("Quote Body Tests", body);
    }

    // The real attestation service, not a hand-set field: the send path re-reads the price
    // fingerprint, so a fixture that stamps columns directly passes for the wrong reason.
    private static Task Attest(ErpRfqAutomationContext context, long quoteId, long tenant) =>
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            quoteId, tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-BODY", null, "tests", default);

    private sealed class RecordingEmailService : ERP_RFQ_Automation.Services.Interfaces.IEmailService
    {
        public int SendCount { get; private set; }
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }

    private static void Seed(
        ErpRfqAutomationContext context,
        bool withCustomer,
        bool withCurrency,
        bool withValidity,
        string? customerReference,
        long tenant = Tenant,
        long quoteId = 96_011)
    {
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = tenant,
            BusinessUnitCode = $"QB{tenant}",
            BusinessUnitName = withCustomer ? "Noor Sons LLC" : "Quote Body Tests",
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });

        long? customerId = null;
        if (withCustomer)
        {
            customerId = 96_101;
            context.Customers.Add(new Customer
            {
                Id = customerId.Value, Buid = tenant, Name = "Acme Trading",
                ImageUrl = string.Empty, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        }

        long? currencyId = null;
        if (withCurrency)
        {
            currencyId = 96_201;
            context.Currencies.Add(new Currency
            {
                Id = currencyId.Value, BusinessUnitId = tenant, Code = "USD",
                CurrencyName = "US Dollar", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        }

        long? rfqId = null;
        if (customerReference is not null)
        {
            rfqId = 96_301;
            context.Rfqs.Add(new Rfq
            {
                Id = rfqId.Value, BusinessUnitId = tenant, Rfqno = "RFQ-BODY-1",
                CustomerRfqReference = customerReference, CreatedBy = "tests", CreatedDate = DateTime.UtcNow
            });
        }

        context.Quotes.Add(new Quote
        {
            Id = quoteId,
            QuoteNo = withCustomer ? "Q-BODY-1" : "Q-BODY-2",
            BusinessUnitId = tenant,
            CustomerId = customerId,
            CurrencyId = currencyId,
            Rfqid = rfqId,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = withValidity ? new DateTime(2026, 9, 30) : null,
            TotalAmount = withCurrency ? 4600m : null,
            CreatedBy = "tests",
            CreatedDate = DateTime.UtcNow
        });
        context.SaveChanges();
    }
}
