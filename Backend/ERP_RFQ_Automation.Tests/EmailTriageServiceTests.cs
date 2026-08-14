using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE NON-NEGOTIABLE: a message the gate stopped is RECORDED with its classification and its
/// reason, and it stays retrievable. A misclassified enquiry is a lost deal, so the audit
/// surface — not the log file — has to be able to show it to a human and hand it back.
/// </summary>
public class EmailTriageServiceTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    private sealed class NoIngestion : IDocumentIngestion
    {
        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null,
            CancellationToken ct = default)
            => throw new NotSupportedException("The list surface must never ingest.");
    }

    private EmailTriageService NewService(ErpRfqAutomationContext context)
        => new(context, new NoIntake(), new NoRawEmail(), new NoopLogger<EmailTriageService>());

    /// <summary>The LIST surface must never enter the pipeline. Any call is a defect.</summary>
    private sealed class NoIntake : ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService
    {
        public Task<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeKit.MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
            => throw new NotSupportedException("The list surface must never capture or schedule.");
    }

    private sealed class NoRawEmail : ERP_RFQ_Automation.Ingestion.Assembly.IRawEmailEvidenceReader
    {
        // Null is the reader's documented "no copy survives" answer, which is exactly the state
        // the reprocess-when-the-message-is-gone test needs.
        public Task<MimeKit.MimeMessage?> TryLoadAsync(
            long businessUnitId, EmailIngest ingest, CancellationToken ct = default)
            => Task.FromResult<MimeKit.MimeMessage?>(null);
    }

    private static EmailIngest Ingest(
        ErpRfqAutomationContext context, long id, long configId, string outcome, string[] reasons,
        string subject, string parseStatus)
    {
        var ingest = Seed.EmailIngest(context, id, configId, parseStatus);
        ingest.EmailSubject = subject;
        ingest.TriageOutcome = outcome;
        ingest.TriageReasonJson = System.Text.Json.JsonSerializer.Serialize(reasons);
        ingest.TriageDecidedOn = new DateTime(2026, 8, 4, 9, 0, 0, DateTimeKind.Utc);
        return ingest;
    }

    private void SeedThreeDecisions()
    {
        using var context = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 5001);
        Seed.EmailConfig(context, 6001, 5001);
        Ingest(context, 7001, 6001, "Noise",
            new[] { EmailTriageReasonCodes.AutoSubmittedHeader }, "Automatic reply: RFQ 4711", "Rejected");
        Ingest(context, 7002, 6001, "Inquiry",
            new[] { EmailTriageReasonCodes.QtyUomPattern }, "Cable tray requirement", "Queued");
        Ingest(context, 7003, 6001, "CommercialNonInquiry",
            new[] { EmailTriageReasonCodes.SupplierQuoteTerms }, "Our quotation QTN-8891", "Queued");
        context.SaveChanges();
    }

    [Fact]
    public async Task ARejectedMessageIsListedWithTheReasonItWasRejected()
    {
        SeedThreeDecisions();
        using var context = _db.ContextFor(5001);

        var page = await NewService(context).ListAsync(5001, "Noise", 1, 25);

        var row = Assert.Single(page.Items);
        Assert.Equal(7001, row.Id);
        Assert.Equal("Noise", row.Outcome);
        Assert.Equal(new[] { EmailTriageReasonCodes.AutoSubmittedHeader }, row.ReasonCodes);
        Assert.Equal("Automatic reply: RFQ 4711", row.Subject);
        Assert.Equal("Rejected", row.ParseStatus);
        // Nothing was enqueued for it: no batch, no lead, no AI spend.
        Assert.Null(row.LinkedBatchId);
        Assert.Null(row.LeadId);
        Assert.False(row.BodySubmitted);
    }

    [Fact]
    public async Task EveryDecisionIsListedWhenNoFilterIsGiven()
    {
        SeedThreeDecisions();
        using var context = _db.ContextFor(5001);

        var page = await NewService(context).ListAsync(5001, outcome: null, page: 1, pageSize: 25);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.Items.Count);
        Assert.Equal(new[] { "CommercialNonInquiry", "Inquiry", "Noise" },
            page.Items.Select(x => x.Outcome).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task AnotherTenantsMailIsNeverListed()
    {
        SeedThreeDecisions();
        using (var context = _db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, 5002);
            Seed.EmailConfig(context, 6002, 5002);
            Ingest(context, 7004, 6002, "Noise",
                new[] { EmailTriageReasonCodes.NoreplySender }, "Other tenant", "Rejected");
            context.SaveChanges();
        }

        using var reader = _db.ContextFor(5001);
        var page = await NewService(reader).ListAsync(5001, outcome: null, page: 1, pageSize: 25);

        Assert.DoesNotContain(page.Items, x => x.Id == 7004);
    }

    [Fact]
    public async Task ADroppedAttachmentIsVisibleOnTheMessageItArrivedWith()
    {
        // ING-06: a durable record nobody can see is only half a fix. This screen is where a
        // human goes to find mail the system did not process, so the skipped attachment and its
        // reason have to be ON the row — not only in a log line and a column.
        using (var context = _db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, 5001);
            Seed.EmailConfig(context, 6001, 5001);
            var ingest = Ingest(context, 7020, 6001, "Inquiry",
                new[] { EmailTriageReasonCodes.QtyUomPattern }, "RE: RFQ 4711", "Queued");
            EmailIngestEnqueuer.RecordSkippedAttachments(ingest,
                new[] { "forwarded.msg (unsupported file type '.msg')" });
            context.SaveChanges();
        }

        using var reader = _db.ContextFor(5001);
        var page = await NewService(reader).ListAsync(5001, outcome: null, page: 1, pageSize: 25);

        var row = Assert.Single(page.Items);
        Assert.Equal("forwarded.msg (unsupported file type '.msg')", Assert.Single(row.SkippedAttachments));
    }

    [Fact]
    public async Task AMessageWithNothingSkippedClaimsNoLoss()
    {
        SeedThreeDecisions();
        using var context = _db.ContextFor(5001);

        var page = await NewService(context).ListAsync(5001, "Inquiry", 1, 25);

        Assert.Empty(Assert.Single(page.Items).SkippedAttachments);
    }

    [Fact]
    public async Task MailIngestedBeforeTheGateExistedReadsAsLegacyRatherThanAsADecision()
    {
        using (var context = _db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, 5001);
            Seed.EmailConfig(context, 6001, 5001);
            Seed.EmailIngest(context, 7010, 6001, "Success"); // no triage columns
            context.SaveChanges();
        }

        using var context2 = _db.ContextFor(5001);
        var page = await NewService(context2).ListAsync(5001, "Legacy", 1, 25);

        var row = Assert.Single(page.Items);
        Assert.Equal("Legacy", row.Outcome);
        Assert.Empty(row.ReasonCodes);
    }

    [Fact]
    public async Task ReprocessingRefusesWithoutAReasonOrAnIdempotencyKey()
    {
        SeedThreeDecisions();
        using var context = _db.ContextFor(5001);
        var service = NewService(context);

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReprocessAsync(5001, 7001, "tester", "  ", "key-1"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ReprocessAsync(5001, 7001, "tester", "It was a real enquiry", " "));
    }

    [Fact]
    public async Task ReprocessingAnotherTenantsMessageIsNotFound()
    {
        SeedThreeDecisions();
        using var context = _db.ContextFor(null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => NewService(context).ReprocessAsync(9999, 7001, "tester", "reason", "key"));
    }

    [Fact]
    public async Task ReprocessingSaysSoHonestlyWhenTheStoredMessageIsGone()
    {
        SeedThreeDecisions();
        using var context = _db.ContextFor(5001);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewService(context).ReprocessAsync(5001, 7001, "tester", "It was a real enquiry", "key-1"));

        Assert.Contains("no longer available", error.Message);
    }

    [Fact]
    public void CorruptReasonJsonNeverBreaksTheAuditSurface()
    {
        Assert.Empty(EmailTriageService.ParseReasonCodes("{not json"));
        Assert.Empty(EmailTriageService.ParseReasonCodes(null));
        Assert.Equal(new[] { "no_signal" }, EmailTriageService.ParseReasonCodes("[\"no_signal\"]"));
    }
}
