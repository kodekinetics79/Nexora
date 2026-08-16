using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// BOTH former producers of email work now enter the ONE canonical door.
///
/// <para><b>Why this class exists.</b> The cutover deleted a 210-line fan-out that walked
/// <c>message.Attachments</c> — not the MIME tree, so a forwarded enquiry was invisible — and
/// produced one Lead per file. The deletion is only half of the guarantee. Nothing in the suite
/// asserted that the mailbox poller and the manual reprocess endpoint actually CALL
/// <see cref="IEmailInquiryIntakeService"/>, so a future edit could quietly reintroduce a second
/// path to the queue and every test would stay green — which is exactly how the two callers
/// drifted apart the first time.</para>
///
/// <para>These tests hold the two properties that make "poll" and "reprocess" the same operation
/// with a different trigger: they both go through capture, and neither of them reads the original
/// message off a local disk path.</para>
/// </summary>
public sealed class EmailCallerCutoverTests : IDisposable
{
    private readonly TestDb _db = new();
    private readonly List<string> _tempRoots = [];

    public void Dispose()
    {
        _db.Dispose();
        foreach (var root in _tempRoots)
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    private const long Bu = 5101;
    private const long ConfigId = 6101;

    // ---- A. THE MAILBOX POLLER ---------------------------------------------------------------

    [Fact]
    public async Task The_poller_hands_the_message_to_the_canonical_intake()
    {
        await SeedMailboxAsync();
        var intake = new RecordingIntake();
        var (service, config) = PollerFor(intake);

        await using var context = _db.ContextFor(Bu);
        var message = Enquiry("RFQ 4711 — cable tray", "Kindly send your best price for 40 nos cable tray 300mm.");

        var acknowledged = await service.ProcessSingleEmailAsync(
            message, config, context, new StubLlm(), intake);

        Assert.True(acknowledged);

        // ONE call, carrying the message itself and the DURABLE ingest row the poller had just
        // written — not a synthetic one, which is what made produced leads point at mail nobody
        // could find.
        var call = Assert.Single(intake.Calls);
        Assert.Same(message, call.Message);
        Assert.True(call.Ingest.Id > 0);
        Assert.Equal(ConfigId, call.Configuration.Id);
        // The gate's decision travels WITH the message. Deciding it twice is how the poller and
        // the reprocess endpoint came to disagree about the same message.
        Assert.NotEqual(EmailTriageOutcome.Noise, call.Triage.Outcome);
    }

    [Fact]
    public async Task The_poller_does_NOT_acknowledge_a_message_whose_capture_failed()
    {
        // THE non-negotiable. A message marked \Seen whose bytes were never durably stored is
        // unrecoverable: the mailbox was the only other copy. Acknowledgement is decided by
        // durable capture and by nothing else — not by "did an extraction job get created".
        await SeedMailboxAsync();
        var intake = new RecordingIntake { Result = EmailInquiryIntakeResult.Refused("capture_failed") };
        var (service, config) = PollerFor(intake);

        await using var context = _db.ContextFor(Bu);
        var acknowledged = await service.ProcessSingleEmailAsync(
            Enquiry("RFQ 4712", "Please quote 12 pcs of gland kits."), config, context,
            new StubLlm(), intake);

        Assert.False(acknowledged);
        Assert.Single(intake.Calls);

        // And the message is not lost while it waits: the ingest row is already durable, so the
        // next cycle re-fetches the message rather than starting from nothing.
        await using var assertContext = _db.ContextFor(Bu);
        var ingest = Assert.Single(await assertContext.EmailIngests.ToListAsync());
        Assert.Equal("Pending", ingest.ParseStatus);

        // The checkpoint is not a tombstone. The same message on the next poll must re-enter
        // canonical capture, then acknowledge only after that succeeds.
        intake.Result = Captured(assemblyId: 42, scheduled: 1);
        await using var retryContext = _db.ContextFor(Bu);
        var retried = await service.ProcessSingleEmailAsync(
            EnquiryWithMessageId("RFQ 4712", "Please quote 12 pcs of gland kits.", ingest.MessageId),
            config, retryContext, new StubLlm(), intake);

        Assert.True(retried);
        Assert.Equal(2, intake.Calls.Count);
        await using var finalContext = _db.ContextFor(Bu);
        Assert.Single(await finalContext.EmailIngests.ToListAsync());
    }

    [Fact]
    public async Task A_crash_after_capture_before_scheduling_repolls_and_resumes_the_same_ingest()
    {
        await SeedMailboxAsync();
        const string messageId = "captured-before-schedule@gulf.example";
        await using (var seed = _db.ContextFor(null))
        {
            var ingest = Seed.EmailIngest(seed, 7_199, ConfigId, "Pending");
            ingest.MessageId = messageId;
            var now = DateTimeOffset.UtcNow;
            var assembly = new EmailInquiryAssembly
            {
                BusinessUnitId = Bu,
                EmailIngestId = ingest.Id,
                EmailConfigurationId = ConfigId,
                MessageKey = messageId,
                RawEvidenceUri = "evidence://raw/captured.eml",
                RawEvidenceSha256 = new string('a', 64),
                ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
                ExpectedComponentCount = 1,
                Status = EmailInquiryAssemblyStatus.Captured,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            assembly.Components.Add(new EmailInquiryComponent
            {
                BusinessUnitId = Bu,
                ComponentKey = "body",
                Kind = EmailInquiryComponentKind.Body,
                Ordinal = 0,
                MimeType = "text/plain",
                ContentHash = new string('b', 64),
                Status = EmailInquiryComponentStatus.Pending,
                ExtractionJobId = null,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
            seed.Add(assembly);
            await seed.SaveChangesAsync();
        }

        var intake = new RecordingIntake { Result = Captured(assemblyId: 7_199, scheduled: 1) };
        var (service, config) = PollerFor(intake);
        await using var context = _db.ContextFor(Bu);
        var acknowledged = await service.ProcessSingleEmailAsync(
            EnquiryWithMessageId("RFQ captured before scheduling", "Please quote 10 sets.", messageId),
            config, context, new StubLlm(), intake);

        Assert.True(acknowledged);
        Assert.Single(intake.Calls);
        Assert.Equal(7_199, intake.Calls[0].Ingest.Id);
        await using var finalContext = _db.ContextFor(Bu);
        Assert.Single(await finalContext.EmailIngests.Where(x => x.MessageId == messageId).ToListAsync());
    }

    [Fact]
    public async Task Noise_is_durably_captured_then_terminalized_without_extraction()
    {
        // The gate is still the gate. A stopped message is recorded with its reasons and its raw
        // bytes are retained — it simply does not enter the pipeline, and the poller must still
        // acknowledge it or it is re-downloaded forever.
        await SeedMailboxAsync();
        var intake = new RecordingIntake();
        var (service, config) = PollerFor(intake);

        var autoReply = Enquiry("Automatic reply: out of office", "I am away until Sunday.");
        autoReply.Headers.Add("Auto-Submitted", "auto-replied");

        await using var context = _db.ContextFor(Bu);
        var acknowledged = await service.ProcessSingleEmailAsync(
            autoReply, config, context, new StubLlm(), intake);

        Assert.True(acknowledged);
        var capture = Assert.Single(intake.Calls);
        Assert.Equal(EmailTriageOutcome.Noise, capture.Triage.Outcome);

        await using var assertContext = _db.ContextFor(Bu);
        var ingest = Assert.Single(await assertContext.EmailIngests.ToListAsync());
        Assert.Equal(EmailTriageOutcome.Noise.ToString(), ingest.TriageOutcome);
    }

    [Fact]
    public async Task Poller_resumes_a_committed_manual_override_without_reapplying_noise_rules()
    {
        await SeedMailboxAsync();
        const string messageId = "governed-reprocess-crash@buyer.example";
        await using (var seed = _db.ContextFor(null))
        {
            var ingest = Seed.EmailIngest(seed, 7_205, ConfigId,
                EmailTriagePersistenceStatuses.Reprocessing);
            ingest.MessageId = messageId;
            ingest.TriageOutcome = EmailTriageOutcome.Uncertain.ToString();
            ingest.TriageReasonJson = "[\"manual_reprocess\"]";
            await seed.SaveChangesAsync();
        }

        var intake = new RecordingIntake();
        var (service, config) = PollerFor(intake);
        var autoReply = EnquiryWithMessageId(
            "Automated-looking RFQ", "Please quote 5 sets.", messageId);
        autoReply.Headers.Add("Auto-Submitted", "auto-generated");

        await using var context = _db.ContextFor(Bu);
        Assert.True(await service.ProcessSingleEmailAsync(
            autoReply, config, context, new StubLlm(), intake));

        var call = Assert.Single(intake.Calls);
        Assert.Equal(EmailTriageOutcome.Uncertain, call.Triage.Outcome);
        Assert.Equal([EmailTriageReasonCodes.ManualReprocess], call.Triage.ReasonCodes);
    }

    // ---- B. THE MANUAL REPROCESS ENDPOINT ----------------------------------------------------

    [Fact]
    public async Task Reprocess_reads_the_original_through_the_evidence_reader_and_not_from_disk()
    {
        // The reprocess endpoint used to File.OpenRead(ingest.RawEmailPath). That path is local
        // container storage — ephemeral on the managed target, gone on the next deploy, and
        // unverified, so a truncated or substituted file would have been replayed as though it
        // were the message the customer sent.
        //
        // The proof is a DISAGREEMENT: the local file on RawEmailPath is a different message
        // from the one the reader returns. Whatever reaches the canonical intake names which of
        // the two the endpoint actually trusted.
        await SeedMailboxAsync();
        var stalePath = Path.Combine(NewTempRoot(), "stale.eml");
        Enquiry("STALE LOCAL COPY", "This is the file on the local disk.").WriteTo(stalePath);

        long ingestId;
        await using (var seed = _db.ContextFor(null))
        {
            var ingest = Seed.EmailIngest(seed, 7101, ConfigId, "Rejected");
            ingest.RawEmailPath = stalePath;
            ingest.TriageOutcome = EmailTriageOutcome.Noise.ToString();
            await seed.SaveChangesAsync();
            ingestId = ingest.Id;
        }

        var intake = new RecordingIntake { Result = Captured(assemblyId: 42, scheduled: 2) };
        var reader = new RecordingRawEmailReader(
            Enquiry("DURABLE EVIDENCE COPY", "Kindly send your best price for 40 nos cable tray."));

        await using var context = _db.ContextFor(Bu);
        var result = await new EmailTriageService(
                context, intake, reader, new NoopLogger<EmailTriageService>())
            .ReprocessAsync(Bu, ingestId, "operator@tenant.example", "It was a real enquiry", "key-1");

        // The reader was consulted, and the message that entered the pipeline is ITS copy.
        var readerCall = Assert.Single(reader.Calls);
        Assert.Equal(Bu, readerCall.BusinessUnitId);
        Assert.Equal(ingestId, readerCall.IngestId);
        var intakeCall = Assert.Single(intake.Calls);
        Assert.Equal("DURABLE EVIDENCE COPY", intakeCall.Message.Subject);

        // And the replay is a REPLAY: forced Uncertain, so the rules that stopped it do not get
        // to stop it again, and never silently promoted to a clean Inquiry either.
        Assert.Equal(EmailTriageOutcome.Uncertain, intakeCall.Triage.Outcome);
        Assert.Contains(EmailTriageReasonCodes.ManualReprocess, intakeCall.Triage.ReasonCodes);
        Assert.Equal(2, result.Enqueued);
        Assert.Equal("Queued", result.Status);
        Assert.False(result.Replayed);
    }

    [Fact]
    public async Task Reprocess_reports_a_full_idempotent_replay_instead_of_claiming_new_work()
    {
        await SeedMailboxAsync();
        long ingestId;
        await using (var seed = _db.ContextFor(null))
        {
            var ingest = Seed.EmailIngest(seed, 7103, ConfigId, "Rejected");
            ingest.TriageOutcome = EmailTriageOutcome.Noise.ToString();
            await seed.SaveChangesAsync();
            ingestId = ingest.Id;
        }

        var intake = new RecordingIntake
        {
            Result = Captured(assemblyId: 42, scheduled: 0, alreadyScheduled: 2)
        };
        var reader = new RecordingRawEmailReader(
            Enquiry("DURABLE EVIDENCE COPY", "Kindly send your best price for 40 nos cable tray."));

        await using var context = _db.ContextFor(Bu);
        var result = await new EmailTriageService(
                context, intake, reader, new NoopLogger<EmailTriageService>())
            .ReprocessAsync(Bu, ingestId, "operator@tenant.example", "Replay proof", "key-replay");

        Assert.Equal(2, result.Enqueued);
        Assert.True(result.Replayed);
    }

    [Fact]
    public async Task Reprocess_rejects_same_idempotency_key_with_a_different_request()
    {
        await SeedMailboxAsync();
        long ingestId;
        await using (var seed = _db.ContextFor(null))
        {
            var ingest = Seed.EmailIngest(seed, 7104, ConfigId, "Rejected");
            ingest.TriageOutcome = EmailTriageOutcome.Noise.ToString();
            await seed.SaveChangesAsync();
            ingestId = ingest.Id;
        }

        var intake = new RecordingIntake();
        var reader = new RecordingRawEmailReader(
            Enquiry("DURABLE EVIDENCE COPY", "Please quote 5 sets."));
        await using var context = _db.ContextFor(Bu);
        var service = new EmailTriageService(
            context, intake, reader, new NoopLogger<EmailTriageService>());

        await service.ReprocessAsync(
            Bu, ingestId, "operator", "Buyer confirmed this is an RFQ.", "same-key");
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReprocessAsync(
                Bu, ingestId, "operator", "A different justification.", "same-key"));

        Assert.Contains("different reprocess request", conflict.Message);
        Assert.Single(await context.IamAuditEvents.AsNoTracking().Where(x =>
            x.BusinessUnitId == Bu && x.Action == "EmailTriageReprocessed"
            && x.CorrelationId == "same-key").ToListAsync());
    }

    [Fact]
    public async Task Reprocess_refuses_when_capture_did_not_complete()
    {
        // Same posture as the poller: nothing is claimed to have been reprocessed unless the
        // message is durably captured. Reporting success here would tell an operator their
        // rescued enquiry is on its way when nothing holds it.
        await SeedMailboxAsync();
        long ingestId;
        await using (var seed = _db.ContextFor(null))
        {
            var ingest = Seed.EmailIngest(seed, 7102, ConfigId, "Rejected");
            ingest.TriageOutcome = EmailTriageOutcome.Noise.ToString();
            await seed.SaveChangesAsync();
            ingestId = ingest.Id;
        }

        var intake = new RecordingIntake { Result = EmailInquiryIntakeResult.Refused("capture_failed") };
        var reader = new RecordingRawEmailReader(Enquiry("RFQ 9001", "Please quote 5 sets."));

        await using var context = _db.ContextFor(Bu);
        var service = new EmailTriageService(context, intake, reader, new NoopLogger<EmailTriageService>());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ReprocessAsync(Bu, ingestId, "operator", "reason", "key-2"));
        Assert.Contains("could not be captured durably", error.Message);
    }

    [Fact]
    public void No_production_type_reopens_a_second_door_to_the_queue()
    {
        // The structural half. ScheduleAsync is the only scheduler and the deleted fan-out must
        // not return under any name: a second walk of the message is how the two callers came to
        // disagree about what a message contained.
        var enqueuer = typeof(EmailIngestEnqueuer);
        Assert.Null(enqueuer.GetMethod("EnqueueAsync"));
        Assert.NotNull(enqueuer.GetMethod("ScheduleAsync"));
    }

    // ---- test plumbing -----------------------------------------------------------------------

    private sealed class RecordingIntake : IEmailInquiryIntakeService
    {
        public sealed record Call(
            MimeMessage Message, EmailIngest Ingest, EmailConfiguration Configuration,
            string? FreshBodyText, EmailTriageDecision Triage, string? ClientEmail);

        public List<Call> Calls { get; } = [];

        /// <summary>Defaults to a clean capture with one scheduled component.</summary>
        public EmailInquiryIntakeResult Result { get; set; } = Captured(assemblyId: 1, scheduled: 1);

        public Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
        {
            Calls.Add(new Call(message, ingest, configuration, freshBodyText, triage, clientEmail));
            return Task.FromResult(Result);
        }
    }

    private static EmailInquiryIntakeResult Captured(
        long assemblyId, int scheduled, int alreadyScheduled = 0)
        => new(assemblyId, Guid.NewGuid(), scheduled, alreadyScheduled, 0,
            scheduled + alreadyScheduled,
            AlreadyCaptured: false, SafeToAcknowledge: true, FailureReason: null);

    private sealed class RecordingRawEmailReader : IRawEmailEvidenceReader
    {
        private readonly MimeMessage? _message;
        public RecordingRawEmailReader(MimeMessage? message) => _message = message;

        public List<(long BusinessUnitId, long IngestId)> Calls { get; } = [];

        public Task<MimeMessage?> TryLoadAsync(
            long businessUnitId, EmailIngest ingest, CancellationToken ct = default)
        {
            Calls.Add((businessUnitId, ingest.Id));
            return Task.FromResult(_message);
        }
    }

    private async Task SeedMailboxAsync()
    {
        await using var context = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Bu);
        Seed.EmailConfig(context, ConfigId, Bu);
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// The real <see cref="EmailService"/>, minus the IMAP socket. Everything under test lives
    /// after the fetch, so the connection is the only thing worth substituting — replacing the
    /// service itself would assert the shape of a double.
    /// </summary>
    private (EmailService Service, EmailConfiguration Config) PollerFor(IEmailInquiryIntakeService intake)
    {
        var root = NewTempRoot();
        var service = new EmailService(
            context: null!, // ProcessSingleEmailAsync takes the tenant-scoped context as a parameter
            env: new StubEnvironment(root),
            logger: new NoopLogger<EmailService>(),
            llmService: new StubLlm(),
            scopeFactory: new UnusedScopeFactory(),
            configuration: new ConfigurationBuilder().Build(),
            storage: new TempStorage(root));

        using var context = _db.ContextFor(null);
        var config = context.EmailConfigurations.AsNoTracking().Single(x => x.Id == ConfigId);
        return (service, config);
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-cutover-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static MimeMessage Enquiry(string subject, string body)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Buyer", "buyer@gulf.example"));
        message.To.Add(new MailboxAddress("Intake", $"inbox{ConfigId}@example.com"));
        message.Subject = subject;
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId();
        message.Body = new BodyBuilder { TextBody = body }.ToMessageBody();
        return message;
    }

    private static MimeMessage EnquiryWithMessageId(string subject, string body, string messageId)
    {
        var message = Enquiry(subject, body);
        message.MessageId = messageId;
        return message;
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
            => throw new InvalidOperationException("These tests never resolve a scope.");
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public StubEnvironment(string root)
        {
            WebRootPath = root;
            ContentRootPath = root;
        }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class TempStorage : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
    {
        private readonly string _root;
        public TempStorage(string root) => _root = root;
        public string RootPath => _root;
        public string ResolvePath(string storagePath) => Path.Combine(_root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([_root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests never delete storage.");
    }
}
