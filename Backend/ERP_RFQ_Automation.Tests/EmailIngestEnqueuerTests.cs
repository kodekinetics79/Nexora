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
            long? emailInquiryComponentId = null,
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
        => Enqueue(message, ingestion, triage, NewIngest(message));

    private static Task<EmailEnqueueResult> Enqueue(
        MimeMessage message, RecordingIngestion ingestion, EmailTriageDecision triage, EmailIngest ingest)
        => EmailIngestEnqueuer.EnqueueAsync(
            message, ingest,
            businessUnitId: 3, clientEmail: "intake@tenant.example",
            ingestion, triage,
            EmailBodyNormalizer.Normalize(message.GetTextBody(MimeKit.Text.TextFormat.Plain)),
            new NoopLogger<EmailIngestEnqueuerTests>());

    private static EmailIngest NewIngest(MimeMessage message) =>
        new() { Id = 11, MessageId = message.MessageId!, FromEmail = "ahmed@alnoortrading.ae" };

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

    // ------------------------------------------------- ING-06: a skip is recorded on EVERY path

    [Fact]
    public async Task ASkippedAttachmentIsRecordedEvenWhenTheBodyIsQuotedOnly()
    {
        // THE regression. A reply with no fresh text, one supported attachment and one
        // unsupported one: the .pptx was recorded NOWHERE. The skip list was assigned inside
        // the `else` of "did the body carry fresh text?", so no body job meant no metadata; a
        // job WAS enqueued, so the "nothing queued" ParseStatus summary did not fire either.
        // The dropped file existed only as a Warning in a log nobody reads.
        var ingestion = new RecordingIngestion();
        var ingest = NewIngest(Message("seed", null));
        var message = Message("RE: RFQ 4711",
            "\nOn Tue, 4 Aug 2026 at 09:12, Sara <sara@gulfmep.ae> wrote:\n> Please quote.",
            ("boq.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"),
            ("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"));

        var result = await Enqueue(message, ingestion, Inquiry(), ingest);

        // The supported attachment was still enqueued — the loss is the OTHER one.
        Assert.Equal(1, result.Queued);
        Assert.Equal("boq.xlsx", Assert.Single(ingestion.Calls).FileName);
        // ...and the dropped file is on the durable ingest record, with its reason.
        Assert.NotNull(ingest.SkippedAttachmentsJson);
        Assert.Contains("deck.pptx", ingest.SkippedAttachmentsJson!);
        Assert.Contains("unsupported file type", ingest.SkippedAttachmentsJson!);
    }

    [Fact]
    public async Task ASkippedAttachmentIsRecordedWhenTheBodyDoesProduceAJobToo()
    {
        // The sibling case above covers a quoted-only body. This one covers the path where the
        // body DOES produce a job, because a queued job used to suppress the "nothing queued"
        // summary and take the skip reason down with it.
        //
        // This test originally used .msg as the unsupported attachment. .msg is now ADMITTED
        // (OutlookMsgReader / EmailContainerReader), so it can no longer stand for a skip —
        // see MsgAttachmentIsNowAdmittedRatherThanSkipped below, which pins the new contract.
        // .pptx carries the assertion instead: it is still off DocumentIntakeAllowList, so the
        // "nothing disappears silently" guarantee is what is being asserted here, unchanged.
        var ingestion = new RecordingIngestion();
        var ingest = NewIngest(Message("seed", null));
        var message = Message("RFQ 4711", "Please quote the attached.",
            ("notes.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"));

        var result = await Enqueue(message, ingestion, Inquiry(), ingest);

        Assert.Equal(1, result.Queued); // the body only
        Assert.Contains("notes.pptx", ingest.SkippedAttachmentsJson!);
        // The body job's provenance sidecar carries the same list.
        var body = Assert.Single(ingestion.Calls);
        Assert.Contains(body.Metadata!.SkippedAttachments!, s => s.Contains("notes.pptx"));
    }

    [Fact]
    public async Task MsgAttachmentIsNowAdmittedRatherThanSkipped()
    {
        // The contract change that invalidated the assertion above, pinned so it cannot drift
        // back silently: an Outlook .msg is an envelope we can now read, so it must produce a
        // job of its own — and must NOT be reported as a loss.
        var ingestion = new RecordingIngestion();
        var ingest = NewIngest(Message("seed", null));
        var message = Message("RFQ 4711", "Please quote the attached.",
            ("notes.msg", "application/vnd.ms-outlook"));

        var result = await Enqueue(message, ingestion, Inquiry(), ingest);

        Assert.Equal(2, result.Queued); // the body AND the .msg envelope
        Assert.Contains(ingestion.Calls, c => c.FileName == "notes.msg");
        Assert.True(string.IsNullOrWhiteSpace(ingest.SkippedAttachmentsJson),
            $"nothing was dropped, so no loss may be reported; got: {ingest.SkippedAttachmentsJson}");
    }

    [Fact]
    public async Task AMessageThatSkipsNothingRecordsNothing()
    {
        // The record is written unconditionally, which includes clearing it: a replay that
        // skips nothing must not leave a stale list claiming a loss that no longer applies.
        var ingestion = new RecordingIngestion();
        var ingest = NewIngest(Message("seed", null));
        ingest.SkippedAttachmentsJson = "[\"stale.pptx (unsupported file type '.pptx')\"]";
        var message = Message("RFQ 4711", "Please quote 40 nos cable tray 300mm.");

        await Enqueue(message, ingestion, Inquiry(), ingest);

        Assert.Null(ingest.SkippedAttachmentsJson);
    }

    [Fact]
    public void TheSkipRecordIsTruncatedToFitTheColumnWithoutLosingTheCount()
    {
        // varchar(2000). A pathological message must still record something truthful rather
        // than fail the whole ingest on a column-length error.
        var ingest = new EmailIngest { Id = 11, MessageId = "m-1", FromEmail = "a@b.example" };
        var many = Enumerable.Range(0, 200)
            .Select(i => $"attachment-with-a-fairly-long-name-{i}.msg (unsupported file type '.msg')")
            .ToList();

        EmailIngestEnqueuer.RecordSkippedAttachments(ingest, many);

        Assert.NotNull(ingest.SkippedAttachmentsJson);
        Assert.True(ingest.SkippedAttachmentsJson!.Length <= 2000);
        Assert.Contains("more skipped attachment(s)", ingest.SkippedAttachmentsJson);
    }

    [Fact]
    public void TheSkipRecordIsValidJsonTheTriageSurfaceCanRead()
    {
        var ingest = new EmailIngest { Id = 11, MessageId = "m-1", FromEmail = "a@b.example" };

        EmailIngestEnqueuer.RecordSkippedAttachments(ingest, new[] { "deck.pptx (unsupported file type '.pptx')" });

        var parsed = System.Text.Json.JsonSerializer.Deserialize<string[]>(ingest.SkippedAttachmentsJson!);
        Assert.Equal("deck.pptx (unsupported file type '.pptx')", Assert.Single(parsed!));
    }

    [Fact]
    public async Task AnEmbeddedMessageAttachmentIsRecordedRatherThanDroppedSilently()
    {
        var ingestion = new RecordingIngestion();
        var ingest = NewIngest(Message("seed", null));
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ahmed", "ahmed@alnoortrading.ae"));
        message.To.Add(new MailboxAddress("Intake", "intake@tenant.example"));
        message.Subject = "FW: RFQ";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        var forwarded = new MimeMessage();
        forwarded.From.Add(new MailboxAddress("Sara", "sara@gulfmep.ae"));
        forwarded.To.Add(new MailboxAddress("Ahmed", "ahmed@alnoortrading.ae"));
        forwarded.Subject = "RFQ";
        forwarded.Body = new TextPart("plain") { Text = "Please quote." };
        var multipart = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "See attached." },
            new MessagePart { Message = forwarded, ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) }
        };
        message.Body = multipart;

        await Enqueue(message, ingestion, Inquiry(), ingest);

        Assert.NotNull(ingest.SkippedAttachmentsJson);
        Assert.Contains("embedded email message", ingest.SkippedAttachmentsJson!);
    }

    [Fact]
    public async Task TheLogicalGroupKeyFallsBackToTheDurableIngestKeyNotTheRowId()
    {
        // A message with no Message-Id used to group under its row id while the triage screen
        // looked it up by `email:{EmailIngest.MessageId}` — so its jobs were unreachable from
        // the one screen that exists to find lost mail.
        var ingestion = new RecordingIngestion();
        var message = Message("RFQ", "Please quote 40 nos cable tray 300mm.");
        message.Headers.RemoveAll(HeaderId.MessageId);
        var ingest = new EmailIngest { Id = 11, MessageId = "sha256:deadbeef", FromEmail = "ahmed@alnoortrading.ae" };

        await Enqueue(message, ingestion, Inquiry(), ingest);

        Assert.Equal("email:sha256:deadbeef", Assert.Single(ingestion.Calls).Metadata?.LogicalGroupKey);
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
