using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner ruling 2026-09-27: most customers take quotes through their own portal, so the Send
/// window offers "Download PDF" beside "Send by email", and the rep records the upload here.
/// The recording passes the same gates as the email, because the customer gets the same document.
/// </summary>
public sealed class QuotePortalSubmissionTests
{
    private const long Tenant = 95_501;
    private const long DraftStatus = 95_502;
    private const long SentStatus = 95_503;
    private const long QuoteId = 95_511;

    private static void Seed(ErpRfqAutomationContext context)
    {
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = Tenant, BusinessUnitCode = "QPORTAL", BusinessUnitName = "Portal Submission", CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        });
        foreach (var (id, code, value) in new[] { (DraftStatus, "DRAFT", "Draft"), (SentStatus, "SENT", "Sent") })
            context.SetupMasters.Add(new SetupMaster
            {
                SetupId = id, BusinessUnitId = Tenant, SetupType = "QuoteStatus", SetupCode = code, SetupValue = value,
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
        context.Quotes.Add(new Quote
        {
            Id = QuoteId, QuoteNo = "Q-PORTAL-1", BusinessUnitId = Tenant, StatusId = DraftStatus,
            QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30), TotalAmount = 100,
            CreatedBy = "tests", CreatedDate = DateTime.UtcNow
        });
        context.SaveChanges();
    }

    private static Task Attest(ErpRfqAutomationContext context) =>
        new ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationService(context).AttestAsync(
            QuoteId, Tenant, ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationSources.SupplierQuote,
            "SQ-TEST", null, "tests", default);

    [Fact]
    public async Task A_quote_uploaded_to_the_customers_portal_is_recorded_as_sent_without_any_email()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        await Attest(context);
        var email = new CountingEmailService();
        var service = new QuoteService(context, email, null!);

        var result = await service.RecordPortalSubmissionAsync(QuoteId, Tenant, "rep@nexora.invalid", null, "BID-2291");
        var replay = await service.RecordPortalSubmissionAsync(QuoteId, Tenant, "rep@nexora.invalid", null, "BID-2291");

        context.ChangeTracker.Clear();
        var quote = await context.Quotes.SingleAsync(q => q.Id == QuoteId);
        Assert.True(result.IsSubmitted);
        Assert.True(replay.WasAlreadySent);
        Assert.NotNull(quote.SentOn);
        Assert.Equal(SentStatus, quote.StatusId);
        Assert.Equal(0, email.SendCount);
        Assert.Empty(await context.QuoteDeliveryRequests.ToListAsync());
    }

    [Fact]
    public async Task Without_a_confirmed_price_source_the_portal_submission_is_refused_like_the_email()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed(context);
        var service = new QuoteService(context, new CountingEmailService(), null!);

        var result = await service.RecordPortalSubmissionAsync(QuoteId, Tenant, "rep@nexora.invalid", null, null);

        context.ChangeTracker.Clear();
        Assert.Equal("PRICE_ATTESTATION_REQUIRED", result.BlockCode);
        Assert.Null((await context.Quotes.SingleAsync(q => q.Id == QuoteId)).SentOn);
    }

    private sealed class CountingEmailService : IEmailService
    {
        public int SendCount { get; private set; }
        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null) => Task.FromResult(MailboxPollReport.Empty);
        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null)
        {
            SendCount++;
            return Task.CompletedTask;
        }
    }
}
