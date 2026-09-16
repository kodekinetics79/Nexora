using System.Net;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The rep must be able to review — and change — what the customer receives.
///
/// <para>The "Email quote" dialog showed only a recipient box and "Send quote": the subject and
/// body were composed server-side and the rep never saw them (D26, owner ask 2026-09-15). These
/// pin the contract behind the dialog: <c>GET {id}/email-draft</c> returns the exact default the
/// send would use, in plain text a rep can edit; and whatever the rep sends back is what is
/// queued, not a server default that quietly replaced it.</para>
/// </summary>
public sealed class QuoteEmailDraftTests
{
    private const long Tenant = 96_401;
    private const long QuoteId = 96_411;

    [Fact]
    public async Task The_draft_the_rep_reviews_is_the_body_the_customer_receives()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);
        await Attest(context);

        var draft = await service.GetEmailDraftAsync(QuoteId, Tenant);

        // The default the server has always composed, now visible before the send.
        Assert.Equal("Quote #Q-DRAFT-1 from Noor Sons LLC", draft.Subject);
        Assert.Contains("Dear Acme Trading", draft.Body);
        Assert.Contains("PO-REQ-7781", draft.Body);
        Assert.Contains("USD 4,600.00", draft.Body);
        Assert.Contains("Valid until: 30 September 2026", draft.Body);
        Assert.Contains("Noor Sons LLC", draft.Body);
        // Plain text for a text box, not markup a rep would have to edit around.
        Assert.DoesNotContain("<p>", draft.Body);
        Assert.DoesNotContain("<br", draft.Body);
        // What is attached, named beside the body so the rep knows what rides along.
        Assert.Equal("Quote_Q-DRAFT-1.pdf", draft.AttachmentFileName);
        Assert.Equal("buyer@acme.example", draft.RecipientEmail);

        // Sending with nothing edited queues the SAME words: every line of the draft is in the
        // queued body. A draft that differed from the send would be a preview of nothing.
        await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@acme.example");
        var delivery = await context.QuoteDeliveryRequests.AsNoTracking().SingleAsync();
        Assert.Equal(draft.Subject, delivery.Subject);
        foreach (var line in draft.Body.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0))
            Assert.Contains(WebUtility.HtmlEncode(line), delivery.Body);
    }

    [Fact]
    public async Task An_edited_subject_and_body_are_what_gets_queued()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = new QuoteService(context, new RecordingEmailService(), null!);
        await Attest(context);

        await service.SendQuoteEmailAsync(QuoteId, Tenant, "buyer@acme.example",
            customSubject: "Re: your tender 77 — our quotation Q-DRAFT-1",
            customBody: "Dear Ahmed,\n\nAs discussed on the phone, 35 pieces on line 2.\n\nRegards,\nSalim");

        var delivery = await context.QuoteDeliveryRequests.AsNoTracking().SingleAsync();
        Assert.Equal("Re: your tender 77 — our quotation Q-DRAFT-1", delivery.Subject);
        Assert.Contains("Dear Ahmed,", delivery.Body);
        Assert.Contains("35 pieces on line 2.", delivery.Body);
        Assert.Contains("Salim", delivery.Body);
        // The server default did not sneak back in beside the rep's words.
        Assert.DoesNotContain("Please find attached", delivery.Body);
        // Line breaks the rep typed survive as line breaks in the mail.
        Assert.Contains("<br", delivery.Body);
    }

    // ------------------------------------------------------------------------ test plumbing

    private static Task Attest(ErpRfqAutomationContext context) =>
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            QuoteId, Tenant,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-DRAFT", null, "tests", default);

    private sealed class RecordingEmailService : ERP_RFQ_Automation.Services.Interfaces.IEmailService
    {
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => Task.FromResult(MailboxPollReport.Empty);
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }

    private static void Seed(ErpRfqAutomationContext context)
    {
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = Tenant, BusinessUnitCode = "QD", BusinessUnitName = "Noor Sons LLC",
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.Customers.Add(new Customer
        {
            Id = 96_421, Buid = Tenant, Name = "Acme Trading", ContactEmail = "buyer@acme.example",
            ImageUrl = string.Empty, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.Currencies.Add(new Currency
        {
            Id = 96_431, BusinessUnitId = Tenant, Code = "USD", CurrencyName = "US Dollar",
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        context.Rfqs.Add(new Rfq
        {
            Id = 96_441, BusinessUnitId = Tenant, Rfqno = "RFQ-DRAFT-1", CustomerRfqReference = "PO-REQ-7781",
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(new Quote
        {
            Id = QuoteId, QuoteNo = "Q-DRAFT-1", BusinessUnitId = Tenant, CustomerId = 96_421, CurrencyId = 96_431,
            Rfqid = 96_441, QuoteDate = DateTime.UtcNow, ValidUntil = new DateTime(2026, 9, 30), TotalAmount = 4600m,
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.SaveChanges();
    }
}
