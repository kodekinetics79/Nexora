using System.Collections.Concurrent;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// P1 lead-loss regression + drift guard: the email intake attachment filter and the
/// document-inspection allow-list had drifted apart in BOTH directions — email accepted
/// .pptx (which inspection then rejected, a confusing tenant-facing rejection) and
/// silently dropped .xls/.csv attachments (which inspection accepts) — losing supplier
/// quotes and customer RFQs that routinely arrive in those formats. The fix makes every
/// intake filter DERIVE from <see cref="DocumentIntakeAllowList"/>; these tests pin that
/// contract so the lists can never drift again.
/// </summary>
public sealed class DocumentIntakeAllowListTests
{
    // ------------------------------------------------------------------ P1 regression

    [Theory]
    [InlineData(".xls")]
    [InlineData(".csv")]
    [InlineData(".XLS")] // filters receive lower-cased extensions, but the set itself is case-insensitive
    public void EmailAttachmentFilter_Accepts_LegacyExcel_And_Csv(string extension)
    {
        Assert.True(EmailService.IsSupportedExtension(extension));
    }

    [Fact]
    public void EmailAttachmentFilter_Refuses_Pptx_AtTheFilter()
    {
        // Refused up front — never accepted-then-quarantined by inspection.
        Assert.False(EmailService.IsSupportedExtension(".pptx"));
        Assert.False(DocumentIntakeAllowList.Extensions.Contains(".pptx"));
    }

    // ------------------------------------------------------------------ drift guards

    // A probe universe: everything on the allow-list plus formats that must stay out.
    //
    // The probe list is deliberately allowed to NAME an extension that later becomes
    // admissible — .msg/.eml/.html did exactly that when the email-container readers landed.
    // When that happens the two loops collide, xUnit drops the duplicate theory rows AT
    // DISCOVERY, and the run still reports "Skipped: 0" while the drift guard silently
    // shrinks. De-duplicating here keeps every probe executed exactly once no matter which
    // side of the allow-list an extension is on.
    private static readonly string[] MustStayOut =
        { ".pptx", ".ppt", ".exe", ".zip", ".msg", ".eml", ".html", ".js", ".svg", ".dll", ".rar", ".bat", "" };

    private static List<string> ProbeUniverse()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var probes = new List<string>();
        foreach (var ext in DocumentIntakeAllowList.Extensions)
            if (seen.Add(ext)) probes.Add(ext);
        foreach (var ext in MustStayOut)
            if (seen.Add(ext)) probes.Add(ext);
        return probes;
    }

    public static TheoryData<string> ProbeExtensions()
    {
        var data = new TheoryData<string>();
        foreach (var ext in ProbeUniverse()) data.Add(ext);
        return data;
    }

    [Fact]
    public void ProbeUniverse_ContainsNoDuplicates_SoNoCaseIsDroppedAtDiscovery()
    {
        // Guards the guard: a duplicate row is invisible in the pass/fail summary, so it is
        // asserted directly rather than left to be noticed in the runner's discovery log.
        var probes = ProbeUniverse();
        Assert.Equal(probes.Count, probes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // and the three that caused the collision are present, exactly once each, as POSITIVES
        foreach (var admitted in new[] { ".msg", ".eml", ".html" })
        {
            Assert.Single(probes, p => string.Equals(p, admitted, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(admitted, DocumentIntakeAllowList.Extensions);
        }
    }

    [Theory]
    [MemberData(nameof(ProbeExtensions))]
    public void EmailFilter_EqualsInspectionAllowList(string extension)
    {
        Assert.Equal(
            DocumentIntakeAllowList.Extensions.Contains(extension),
            EmailService.IsSupportedExtension(extension));
    }

    [Theory]
    [MemberData(nameof(ProbeExtensions))]
    public void ManualUploadFilter_EqualsInspectionAllowList(string extension)
    {
        Assert.Equal(
            DocumentIntakeAllowList.Extensions.Contains(extension),
            ManualUploadService.IsSupportedExtension(extension));
    }

    [Fact]
    public void InspectionService_UsesTheSharedAllowListAsItsExtensionGate()
    {
        // Anything OFF the list is rejected by inspection with the unsupported-extension
        // reason; anything ON the list gets past the extension gate (content checks may
        // still reject garbage bytes, but never for its extension).
        // The rejection sentence deliberately does NOT echo the extension: the extension is
        // caller-controlled filename text, and rejection reasons are rendered verbatim as
        // product copy in the intake UI, so interpolating it would let a crafted filename
        // inject content into an authoritative Nexora sentence.
        var rejected = Inspect([0x01, 0x02], "deck.pptx");
        Assert.Equal(FileInspectionStatus.Rejected, rejected.Status);
        Assert.Contains("not a type Nexora accepts", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".pptx", rejected.Reason, StringComparison.OrdinalIgnoreCase);

        foreach (var ext in DocumentIntakeAllowList.Extensions)
        {
            var result = Inspect([0x01, 0x02], $"probe{ext}");
            Assert.DoesNotContain("not a type Nexora accepts", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void FolderDoors_ReadFromTheInspectionAllowListItself()
    {
        // The per-folder lists are gone. They were narrow on purpose — SEC accepted only legacy
        // .doc, Aramco only .docx — and the narrowness cost real work: the entire Aramco corpus
        // is .doc, so the folder named after that customer discarded that customer's documents
        // without a word. See FolderService.WatchedFolderExtensions.
        //
        // This asserts SAME OBJECT, not merely equal contents. A door that copies the list can
        // drift from it; a door that IS the list cannot.
        Assert.Same(DocumentIntakeAllowList.Extensions, FolderService.WatchedFolderExtensions);
    }

    // ------------------------------------- newly accepted formats clear real inspection

    [Fact]
    public void Inspection_Clears_RealLegacyXls_And_Csv()
    {
        var xls = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-rfq.xls"));
        var csv = "product,qty\n6\" ball valve,10\n"u8.ToArray();

        Assert.Equal(FileInspectionStatus.Cleared, Inspect(xls, "legacy-rfq.xls").Status);
        Assert.Equal(FileInspectionStatus.Cleared, Inspect(csv, "rates.csv").Status);
    }

    // ------------------------------------------- end-to-end email intake filter/skips

    [Fact]
    public async Task EmailIntake_Enqueues_Xls_And_Csv_But_Refuses_Pptx_BeforeIngestion()
    {
        var (service, logger, temp) = CreateEmailService();
        try
        {
            var ingestion = new RecordingIngestion();
            var ingest = new EmailIngest { Id = 42, MessageId = "m-1", FromEmail = "buyer@gulf.example" };
            var message = BuildMessage("RFQ 4711", "Please quote the attached.",
                ("quote.xls", "application/vnd.ms-excel"),
                ("rates.csv", "text/csv"),
                ("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"));

            var intake = new RecordingIntake();
            await service.EnqueueEmailForExtractionAsync(
                message, ingest, Config(), intake, InquiryTriage(), FreshBody(message));

            // The poller reaches the CANONICAL intake, not a per-attachment fan-out. Which parts
            // of the MIME tree become components is decided by EmailInquiryManifestPlanner and is
            // asserted against real messages in EmailInquiryManifestPlannerTests; what matters
            // here is that this caller no longer decides it for itself.
            var call = Assert.Single(intake.Calls);
            Assert.Same(message, call.Message);
            Assert.Same(ingest, call.Ingest);
            // The poller itself now touches the ingestion gateway for NOTHING. It used to call
            // it once per allowed attachment, which is how it came to own the allow-list
            // decision in the first place.
            Assert.Empty(ingestion.Calls);

            // WHERE THE ALLOW-LIST PROPERTY LIVES NOW. .pptx being refused before it reaches
            // ingestion, inspection or quarantine — and the refusal being recorded rather than
            // silent — is EmailInquiryManifestPlanner's behaviour, asserted against real
            // messages in EmailInquiryManifestPlannerTests and on real evidence in the
            // PostgreSQL slice. Re-asserting a weaker version of it here through a double would
            // describe the shape of the old fan-out as though it were a requirement.
            Assert.True(
                ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList
                    .IsAllowed(".xls"),
                ".xls must remain an admitted intake type.");
            Assert.False(
                ERP_RFQ_Automation.Security.DocumentInspection.DocumentIntakeAllowList
                    .IsAllowed(".pptx"),
                ".pptx must remain refused at intake.");

            // The file-type label is still derived the same way; it is now applied by the
            // canonical scheduler from the persisted component rather than by this caller.
            Assert.Equal("Excel", EmailIngestEnqueuer.GetFileTypeLabel(".xls"));
            Assert.Equal("CSV", EmailIngestEnqueuer.GetFileTypeLabel(".csv"));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task EmailIntake_SurfacesSkippedAttachments_OnTheIngestRecord_WhenNothingWasQueued()
    {
        var (service, logger, temp) = CreateEmailService();
        try
        {
            var ingestion = new RecordingIngestion();
            var ingest = new EmailIngest { Id = 7, MessageId = "m-2", FromEmail = "buyer@gulf.example", ParseStatus = "Pending" };
            // No body text and only an unsupported attachment -> nothing enqueuable.
            var message = BuildMessage("RFQ 4712", body: null,
                ("deck.pptx", "application/vnd.openxmlformats-officedocument.presentationml.presentation"));

            var intake = new RecordingIntake();
            await service.EnqueueEmailForExtractionAsync(
                message, ingest, Config(), intake, InquiryTriage(), FreshBody(message));

            // Still one canonical intake call. Whether the message yields anything enqueuable
            // is the planner's answer, not this caller's — the allow-list itself is asserted in
            // DocumentIntakeAllowList's own tests and in the manifest planner's.
            Assert.Single(intake.Calls);
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>Records what the poller handed the canonical intake.</summary>
    private sealed class RecordingIntake : ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService
    {
        public readonly List<(MimeKit.MimeMessage Message, EmailIngest Ingest)> Calls = [];

        public Task<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeKit.MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, ERP_RFQ_Automation.Ingestion.Triage.EmailTriageDecision triage,
            string? clientEmail, CancellationToken ct = default)
        {
            Calls.Add((message, ingest));
            return Task.FromResult(new ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryIntakeResult(
                AssemblyId: 1, BatchId: Guid.Empty, Scheduled: 0, AlreadyScheduled: 0, Held: 0,
                ExpectedComponents: 0, AlreadyCaptured: false, SafeToAcknowledge: true,
                FailureReason: null));
        }
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

    // ------------------------------------------------------------------ test plumbing

    private static FileInspectionResult Inspect(byte[] bytes, string fileName)
    {
        var service = new DocumentFileInspectionService(new EicarMalwareScanner());
        using var stream = new MemoryStream(bytes, writable: false);
        return service.InspectAsync(new FileInspectionRequest(
            stream, fileName, DeclaredLength: bytes.LongLength)).GetAwaiter().GetResult();
    }

    private static EmailConfiguration Config() => new()
    {
        Id = 1,
        BusinessUnitId = 9,
        ConfigurationName = "intake",
        EmailAddress = "intake@tenant.example",
        Protocol = "IMAP",
        Host = "localhost",
        Port = 993,
        Username = "u",
        Password = "p"
    };

    // ING-07: the intake fan-out now takes the gate's decision and the sender's fresh body
    // text. These tests are about the ATTACHMENT filter, so they pass the ordinary
    // "this is an inquiry" decision and the message's own normalized body.
    private static EmailTriageDecision InquiryTriage()
        => new(EmailTriageOutcome.Inquiry,
            new[] { EmailTriageReasonCodes.RequestVerb }, null, false);

    private static EmailBodyParts FreshBody(MimeMessage message)
        => EmailBodyNormalizer.Normalize(message.GetTextBody(MimeKit.Text.TextFormat.Plain));

    private static MimeMessage BuildMessage(
        string subject, string? body, params (string FileName, string ContentType)[] attachments)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Buyer", "buyer@gulf.example"));
        message.To.Add(new MailboxAddress("Intake", "intake@tenant.example"));
        message.Subject = subject;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();

        var builder = new BodyBuilder();
        if (body != null) builder.TextBody = body;
        foreach (var (fileName, contentType) in attachments)
        {
            builder.Attachments.Add(fileName, new byte[] { 0x01, 0x02, 0x03 },
                MimeKit.ContentType.Parse(contentType));
        }
        message.Body = builder.ToMessageBody();
        return message;
    }

    private static (EmailService Service, CapturingLogger Logger, string TempRoot) CreateEmailService()
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexora-intake-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        var logger = new CapturingLogger();
        var service = new EmailService(
            context: null!, // EnqueueEmailForExtractionAsync never touches the DbContext
            env: new StubEnvironment(temp),
            logger: logger,
            llmService: new Support.StubLlm(),
            scopeFactory: new StubScopeFactory(),
            configuration: new ConfigurationBuilder().Build(),
            storage: new TempStorage(temp));
        return (service, logger, temp);
    }

    private sealed class RecordingIngestion : IDocumentIngestion
    {
        public sealed record Call(string FileName, long BusinessUnitId, ExtractionSourceType SourceType,
            ExtractionJobMetadata? Metadata);

        public ConcurrentQueue<Call> Calls { get; } = new();
        private long _nextJobId;

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null,
            CancellationToken ct = default)
        {
            Calls.Enqueue(new Call(fileName, businessUnitId, sourceType, metadata));
            return Task.FromResult(new IngestedDocument
            {
                JobId = Interlocked.Increment(ref _nextJobId),
                SourceDocumentOccurrenceId = 1,
                BatchId = batchId ?? Guid.NewGuid(),
                ContentHash = new string('a', 64),
                StoragePath = "test",
                Outcome = EnqueueOutcome.Enqueued
            });
        }
    }

    private sealed class CapturingLogger : ILogger<EmailService>
    {
        public sealed record Entry(LogLevel Level, string Message);
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
    }

    private sealed class StubEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class StubScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("The intake tests never resolve a scope.");
    }

    private sealed class TempStorage(string root) : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
    {
        public string RootPath => root;
        public string ResolvePath(string storagePath) => Path.Combine(root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("The intake tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The intake tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The intake tests never delete storage.");
    }
}
