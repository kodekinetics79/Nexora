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
        /// <summary>
        /// Not this stub's subject. The resume path is proved against the real intake service on
        /// PostgreSQL; a stand-in here would only assert its own return value.
        /// </summary>
        public Task<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryResumeResult> ResumeSchedulingAsync(
            long businessUnitId, long assemblyId, CancellationToken ct = default,
            ERP_RFQ_Automation.Ingestion.Assembly.EmailInquirySchedulingGrant? grant = null)
            => Task.FromResult(new ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryResumeResult(
                ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryResumeOutcome.NothingToResume, 0, 0));
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

    // ---- the message-level state, and where the numbers on it come from ----------------------

    /// <summary>
    /// A captured message: one body, one attachment the pipeline read, one it refused. The
    /// attachment carries a digest and an evidence object; the refused one does not.
    /// </summary>
    private void SeedCapturedMessage(long ingestId = 7030)
    {
        using var context = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 5001);
        Seed.EmailConfig(context, 6001, 5001);
        var ingest = Ingest(context, ingestId, 6001, "Inquiry",
            new[] { EmailTriageReasonCodes.QtyUomPattern }, "RFQ 4711 — cable tray", "Queued");
        context.SaveChanges();

        var assembly = new ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssembly
        {
            BusinessUnitId = 5001,
            EmailIngestId = ingest.Id,
            EmailConfigurationId = 6001,
            MessageKey = ingest.MessageId,
            ManifestContractVersion = ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryManifestPlanner.ContractVersion,
            ExpectedComponentCount = 3,
            CompletedComponentCount = 2,
            Status = ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyStatus.NeedsReview,
            StatusReason = "component_skipped: one part could not be read.",
            RawEvidenceUri = "s3://nexora-evidence/raw-mail/5001/abc.eml",
            RawEvidenceSha256 = new string('f', 64),
            ReceivedAtUtc = new DateTimeOffset(2026, 8, 4, 8, 30, 0, TimeSpan.Zero),
            CreatedAtUtc = new DateTimeOffset(2026, 8, 4, 8, 31, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 4, 9, 15, 0, TimeSpan.Zero)
        };
        assembly.Components.Add(Component(0, "body", ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentKind.Body,
            null, ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.Completed, null, null, "text/plain", 412, digest: true, evidence: true));
        assembly.Components.Add(Component(1, "attachment:1", ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentKind.Attachment,
            "boq.xlsx", ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.Completed, null, null,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 20480, digest: true, evidence: true));
        assembly.Components.Add(Component(2, "attachment:2", ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentKind.Attachment,
            "drawing.dwg", ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus.Skipped,
            "unsupported_file_type", "This attachment type is not read by the pipeline.", "image/vnd.dwg", 91021,
            digest: false, evidence: false));
        context.Add(assembly);
        context.SaveChanges();
    }

    private static ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponent Component(
        int ordinal, string key, ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentKind kind,
        string? fileName, ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryComponentStatus status,
        string? reasonCode, string? reasonDetail, string mimeType, long size, bool digest, bool evidence)
        => new()
        {
            BusinessUnitId = 5001,
            ComponentKey = $"email:msg-7030:{key}",
            Ordinal = ordinal,
            Kind = kind,
            FileName = fileName,
            MimeType = mimeType,
            ByteSize = size,
            ContentHash = digest ? new string('a', 64) : null,
            EvidenceUri = evidence ? $"s3://nexora-evidence/cleared/5001/{key}" : null,
            Status = status,
            ReasonCode = reasonCode,
            ReasonDetail = reasonDetail,
            CreatedAtUtc = new DateTimeOffset(2026, 8, 4, 8, 31, 0, TimeSpan.Zero),
            UpdatedAtUtc = new DateTimeOffset(2026, 8, 4, 9, 15, 0, TimeSpan.Zero)
        };

    [Fact]
    public async Task TheAttachmentCountComesFromThePersistedManifestNotFromAStoredFile()
    {
        // This number used to be produced by opening the stored .eml and counting
        // `message.Attachments` — the same walk that was deleted from the fan-out, wrong twice
        // over: it is not the MIME tree (a forwarded enquiry counted zero), and the path it read
        // is container-local storage that does not survive a deploy, so it silently degraded to
        // "unknown" for historic mail. The manifest the pipeline actually planned is the answer,
        // and the message here has no RawEmailPath at all.
        SeedCapturedMessage();
        using var context = _db.ContextFor(5001);

        var row = Assert.Single((await NewService(context).ListAsync(5001, null, 1, 25)).Items);

        Assert.Equal(2, row.AttachmentCount);   // the body is not an attachment
        Assert.True(row.HasAttachments);
    }

    [Fact]
    public async Task TheListCarriesTheMessageLevelStateAndItsTimestamps()
    {
        SeedCapturedMessage();
        using var context = _db.ContextFor(5001);

        var row = Assert.Single((await NewService(context).ListAsync(5001, null, 1, 25)).Items);

        Assert.Equal("NeedsReview", row.AssemblyState);
        Assert.Contains("could not be read", row.AssemblyReason);
        Assert.Equal(3, row.ExpectedComponentCount);
        Assert.Equal(2, row.CompletedComponentCount);
        Assert.True(row.RawEvidenceStored);
        Assert.True(row.RawEvidenceVerifiable);
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 8, 31, 0, TimeSpan.Zero), row.IngestedAtUtc);
        // The recovery sweep moves this and nothing else, which is how "stuck" is told from "busy".
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 9, 15, 0, TimeSpan.Zero), row.LastUpdatedAtUtc);
        Assert.Equal(new DateTimeOffset(2026, 8, 4, 8, 30, 0, TimeSpan.Zero), row.SenderSentAtUtc);
        // The list never claims to have looked at the parts; the detail does.
        Assert.Null(row.Components);
    }

    [Fact]
    public async Task TheDetailSurfaceNamesEveryPartAndWhyItStopped()
    {
        SeedCapturedMessage();
        using var context = _db.ContextFor(5001);

        var detail = await NewService(context).GetAsync(5001, 7030);

        Assert.Equal("RFQ 4711 — cable tray", detail.Subject);
        // The SAME row the list renders, with its parts filled in — not a second shape.
        Assert.Equal("NeedsReview", detail.AssemblyState);
        Assert.NotNull(detail.Components);
        var components = detail.Components!;
        Assert.Equal(3, components.Count);

        var refused = components.Single(c => c.FileName == "drawing.dwg");
        Assert.Equal("Skipped", refused.State);
        Assert.Equal("unsupported_file_type", refused.ReasonCode);
        Assert.Contains("not read by the pipeline", refused.Reason);
        Assert.Equal("image/vnd.dwg", refused.ContentType);
        Assert.Equal(91021, refused.SizeBytes);
        // It was refused before anything was stored for it, and the screen says so honestly.
        Assert.False(refused.HasContentDigest);
        Assert.False(refused.EvidenceStored);

        var boq = components.Single(c => c.FileName == "boq.xlsx");
        Assert.Equal("Attachment", boq.Kind);
        Assert.True(boq.HasContentDigest);
        Assert.True(boq.EvidenceStored);
    }

    [Fact]
    public async Task NoSurfaceEverLeaksWhereTheEvidenceIsStored()
    {
        // The one rule this whole surface is bound by: state, identity and counts leave the
        // server; storage locations do not. A URI on this screen is useless to the reader and a
        // map of the evidence layout to everyone else.
        SeedCapturedMessage();
        using var context = _db.ContextFor(5001);
        var service = NewService(context);

        var json = System.Text.Json.JsonSerializer.Serialize(await service.ListAsync(5001, null, 1, 25))
            + System.Text.Json.JsonSerializer.Serialize(await service.GetAsync(5001, 7030));

        Assert.DoesNotContain("s3://", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nexora-evidence", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("raw-mail", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TheDetailSurfaceCannotReachAnotherTenantsMessage()
    {
        SeedCapturedMessage();
        using var context = _db.ContextFor(null);

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => NewService(context).GetAsync(9999, 7030));
    }

    [Fact]
    public void CorruptReasonJsonNeverBreaksTheAuditSurface()
    {
        Assert.Empty(EmailTriageService.ParseReasonCodes("{not json"));
        Assert.Empty(EmailTriageService.ParseReasonCodes(null));
        Assert.Equal(new[] { "no_signal" }, EmailTriageService.ParseReasonCodes("[\"no_signal\"]"));
    }
}
