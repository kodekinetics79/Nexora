using System.Collections.Concurrent;
using System.Text;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The seam between recognition and extraction: what the intake door actually hands to the
/// queue. Three facts have to hold or the whole conversational path is decorative — only the
/// sender's FRESH text is submitted, the body job is marked as prose, and a supplier document
/// hint reaches every job of the message so the worker completes it WITHOUT a customer Lead.
/// </summary>
public class EmailIngestEnqueuerTests
{
    private sealed class RecordingIngestion : IDocumentIngestion
    {
        public sealed record Call(string FileName, string Text, ExtractionJobMetadata? Metadata);

        public ConcurrentQueue<Call> Calls { get; } = new();
        private long _nextJobId;

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            CancellationToken ct = default)
        {
            Calls.Enqueue(new Call(fileName, Encoding.UTF8.GetString(bytes), metadata));
            return Task.FromResult(new IngestedDocument
            {
                JobId = Interlocked.Increment(ref _nextJobId),
                SourceDocumentOccurrenceId = 1,
                BatchId = batchId ?? Guid.NewGuid(),
                ContentHash = new string('b', 64),
                StoragePath = "test",
                Outcome = EnqueueOutcome.Enqueued
            });
        }
    }

    private static MimeMessage Message(string subject, string? body,
        params (string FileName, string ContentType)[] attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ahmed", "ahmed@alnoortrading.ae"));
        message.To.Add(new MailboxAddress("Intake", "intake@tenant.example"));
        message.Subject = subject;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        var builder = new BodyBuilder();
        if (body != null) builder.TextBody = body;
        foreach (var (fileName, contentType) in attachments)
            builder.Attachments.Add(fileName, new byte[] { 1, 2, 3 }, ContentType.Parse(contentType));
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static Task<EmailEnqueueResult> Enqueue(
        MimeMessage message, RecordingIngestion ingestion, EmailTriageDecision triage)
        => EmailIngestEnqueuer.EnqueueAsync(
            message,
            new EmailIngest { Id = 11, MessageId = message.MessageId!, FromEmail = "ahmed@alnoortrading.ae" },
            businessUnitId: 3, clientEmail: "intake@tenant.example",
            ingestion, triage,
            EmailBodyNormalizer.Normalize(message.GetTextBody(MimeKit.Text.TextFormat.Plain)),
            new NoopLogger<EmailIngestEnqueuerTests>());

    private static EmailTriageDecision Inquiry(bool threadContinuation = false)
        => new(EmailTriageOutcome.Inquiry,
            new[] { EmailTriageReasonCodes.QtyUomPattern }, null, threadContinuation);

    [Fact]
    public async Task OnlyTheSendersOwnWordsAreSubmittedForExtraction()
    {
        // A forwarded thread must not be extracted three times over.
        var ingestion = new RecordingIngestion();
        var message = Message("FW: cable tray", string.Join("\n", new[]
        {
            "Please quote 40 nos cable tray 300mm.",
            "",
            "On Tue, 4 Aug 2026 at 09:12, Sara <sara@gulfmep.ae> wrote:",
            "> Original request: 40 nos cable tray 300mm for Jebel Ali."
        }));

        await Enqueue(message, ingestion, Inquiry());

        var body = Assert.Single(ingestion.Calls);
        Assert.Contains("Please quote 40 nos cable tray 300mm.", body.Text);
        Assert.DoesNotContain("Original request", body.Text);
        Assert.DoesNotContain("Jebel Ali", body.Text);
    }

    [Fact]
    public async Task TheBodyJobIsMarkedAsProseAndTheAttachmentJobIsNot()
    {
        var ingestion = new RecordingIngestion();
        var message = Message("RFQ 4711", "Please quote the attached BOQ.",
            ("boq.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        var result = await Enqueue(message, ingestion, Inquiry());

        Assert.Equal(2, result.Queued);
        var calls = ingestion.Calls.ToList();
        var body = Assert.Single(calls, c => c.FileName.EndsWith("_body.txt", StringComparison.Ordinal));
        var attachment = Assert.Single(calls, c => c.FileName == "boq.xlsx");
        Assert.Equal("prose", body.Metadata?.BodyShape);
        Assert.Null(attachment.Metadata?.BodyShape); // a document keeps structured routing
        Assert.Equal("Inquiry", body.Metadata?.TriageOutcome);
        Assert.Equal(new[] { EmailTriageReasonCodes.QtyUomPattern }, body.Metadata?.TriageReasonCodes);
    }

    [Fact]
    public async Task ASupplierQuotationEmailNeverBecomesACustomerLead()
    {
        var ingestion = new RecordingIngestion();
        var message = Message("Our quotation QTN-8891",
            "Please find our supplier quotation attached. Unit price USD 42.00, incoterms CIF.",
            ("quotation.pdf", "application/pdf"));
        var triage = new EmailTriageDecision(EmailTriageOutcome.CommercialNonInquiry,
            new[] { EmailTriageReasonCodes.SupplierQuoteTerms },
            EmailTriageDocumentHints.SupplierQuote, ThreadContinuation: false);

        await Enqueue(message, ingestion, triage);

        // EVERY job of the message carries the hint, and the hint is the mechanism the
        // extraction worker uses to complete a job without creating a Lead.
        Assert.All(ingestion.Calls, call =>
        {
            Assert.Equal(EmailTriageDocumentHints.SupplierQuote, call.Metadata?.CommercialDocumentTypeHint);
            Assert.True(ExtractionJobMetadata.IsNonLeadCommercialType(call.Metadata?.CommercialDocumentTypeHint));
        });
    }

    [Fact]
    public async Task AReplyWithNoNewTextStillEnqueuesItsAttachments()
    {
        // The body adds nothing, but the attachment is the RFQ. Losing it would be the exact
        // failure the old attachment pre-scan was supposed to prevent and sometimes caused.
        var ingestion = new RecordingIngestion();
        var message = Message("RE: RFQ 4711",
            "\nOn Tue, 4 Aug 2026 at 09:12, Sara <sara@gulfmep.ae> wrote:\n> Please quote.",
            ("boq.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        var result = await Enqueue(message, ingestion, Inquiry());

        Assert.Equal(1, result.Queued);
        Assert.Equal("boq.xlsx", Assert.Single(ingestion.Calls).FileName);
    }

    [Fact]
    public async Task ThreadContinuationTravelsWithTheJob()
    {
        var ingestion = new RecordingIngestion();
        var message = Message("RE: cable tray", "Please quote 40 nos cable tray 300mm as discussed.");

        await Enqueue(message, ingestion, Inquiry(threadContinuation: true));

        Assert.True(Assert.Single(ingestion.Calls).Metadata?.ThreadContinuation);
    }

    [Fact]
    public async Task TheBodyJobKeepsTheEnvelopeContextTheExtractorNeeds()
    {
        var ingestion = new RecordingIngestion();
        var message = Message("Cable tray requirement", "Please quote 40 nos cable tray 300mm.");

        await Enqueue(message, ingestion, Inquiry());

        var body = Assert.Single(ingestion.Calls);
        Assert.StartsWith("Subject: Cable tray requirement", body.Text);
        Assert.Contains("From: ", body.Text);
        Assert.Equal("ahmed@alnoortrading.ae", body.Metadata?.FromEmail);
        Assert.Equal(11, body.Metadata?.EmailIngestId);
    }
}
