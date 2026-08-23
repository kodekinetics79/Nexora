using System.Collections.Concurrent;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
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
/// ING-09 — A MESSAGE THE SYSTEM DURABLY CAPTURED MUST NOT BE ABLE TO GO NOWHERE.
///
/// The email door persists in two steps: commit the EmailIngest row (ParseStatus "Pending"),
/// then triage + enqueue and flip the status. A crash between them left a row nothing would
/// ever touch again — the ledger pre-check skips any Message-Id already in EmailIngests, so
/// the message was never re-downloaded, never enqueued, and never surfaced anywhere. The
/// sweeper re-runs the cut-off step from the retained raw .eml, through the SAME
/// EmailIngestEnqueuer fan-out a fresh message takes.
///
/// These tests pin the sweep's contract: a stranded row is re-enqueued exactly once, an
/// in-flight (young) row is left alone, a row whose enqueue already ran gets only the missing
/// status flip, a row whose raw message is gone gets a terminal honest state instead of an
/// unpayable promise, and one bad row never stops the sweep.
/// </summary>
public sealed class StrandedPendingSweepTests : IDisposable
{
    private const long Tenant = 3;
    private static readonly TimeSpan OlderThanThreshold = TimeSpan.FromHours(1);

    private readonly TestDb _db = new();
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "nexora-stranded-sweep-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task AStrandedPendingRowIsReEnqueuedExactlyOnce()
    {
        await SeedMailboxAsync();
        var ingestId = await SeedStrandedIngestAsync(
            "stuck-1@sender.example", age: OlderThanThreshold,
            rawEmailPath: WriteRawEmail("stuck-1@sender.example",
                "Please quote 40 nos cable tray 300mm, delivery Jebel Ali."));
        var ingestion = new RecordingIntake();
        var service = CreateService();

        await using var ctx = _db.ContextFor(null);
        var recovered = await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);

        Assert.Equal(1, recovered);
        // The fresh body produced exactly one extraction job through the shared fan-out.
        Assert.Single(ingestion.Calls);
        var row = await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId);
        Assert.Equal("Queued", row.ParseStatus);
        // The recovery ran the REAL routing step, so the triage decision was recorded too.
        Assert.NotNull(row.TriageOutcome);
        Assert.NotNull(row.TriageDecidedOn);

        // Exactly once: the row is no longer Pending, so a second sweep finds nothing.
        var secondPass = await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);
        Assert.Equal(0, secondPass);
        Assert.Single(ingestion.Calls);
    }

    [Fact]
    public async Task ARowYoungerThanTheThresholdIsLeftAlone()
    {
        // "Pending" for a few seconds is a message in flight, not a crash remnant. Sweeping
        // it would race the poll cycle that is processing it right now.
        await SeedMailboxAsync();
        var ingestId = await SeedStrandedIngestAsync(
            "young-1@sender.example", age: TimeSpan.FromMinutes(5),
            rawEmailPath: WriteRawEmail("young-1@sender.example", "Please quote 12 nos gate valve."));
        var ingestion = new RecordingIntake();
        var service = CreateService();

        await using var ctx = _db.ContextFor(null);
        var recovered = await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);

        Assert.Equal(0, recovered);
        Assert.Empty(ingestion.Calls);
        Assert.Equal("Pending",
            (await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId)).ParseStatus);
    }

    [Fact]
    public async Task ARowWhoseEnqueueAlreadyRanGetsOnlyTheMissingStatusFlip()
    {
        // The OTHER crash window: the fan-out completed but the flip to "Queued" did not.
        // The message's extraction occurrences already exist under its logical group key, so
        // the sweep must restore the status without re-reading or re-enqueuing anything.
        await SeedMailboxAsync();
        var ingestId = await SeedStrandedIngestAsync(
            "flip-1@sender.example", age: OlderThanThreshold,
            rawEmailPath: WriteRawEmail("flip-1@sender.example", "Please quote the attached BOQ."));
        await SeedExtractionOccurrenceAsync("email:flip-1@sender.example");
        var ingestion = new RecordingIntake();
        var service = CreateService();

        await using var ctx = _db.ContextFor(null);
        var recovered = await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);

        Assert.Equal(1, recovered);
        Assert.Empty(ingestion.Calls); // nothing re-enqueued — the work already exists
        Assert.Equal("Queued",
            (await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId)).ParseStatus);
    }

    [Fact]
    public async Task ARowWithNoRetainedRawMessageBecomesTerminallyFailedNotSilentlyStuck()
    {
        // Without the bytes there is nothing to replay — not here and not via the manual
        // reprocess endpoint either. Leaving it Pending would promise a recovery nothing can
        // deliver; the honest state is a visible terminal failure.
        await SeedMailboxAsync();
        var ingestId = await SeedStrandedIngestAsync(
            "lost-1@sender.example", age: OlderThanThreshold,
            rawEmailPath: Path.Combine(_temp, "no-such-file.eml"));
        var ingestion = new RecordingIntake();
        var service = CreateService();

        await using var ctx = _db.ContextFor(null);
        await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);

        Assert.Empty(ingestion.Calls);
        var row = await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId);
        Assert.Equal(EmailService.STATUS_RAW_MESSAGE_LOST, row.ParseStatus);
        Assert.NotNull(row.ParsedAt);

        // Terminal means terminal: the next sweep does not pick it up again.
        Assert.Equal(0, await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow));
    }

    [Fact]
    public async Task OneUnreadableMessageDoesNotStopTheSweep()
    {
        // Per-row fault isolation: the poison row stays where it is (retried next cycle);
        // the healthy row behind it is still recovered THIS cycle.
        await SeedMailboxAsync();
        var poisonPath = Path.Combine(_temp, "poison.eml");
        Directory.CreateDirectory(_temp);
        await File.WriteAllBytesAsync(poisonPath, Array.Empty<byte>()); // parses as no message
        var poisonId = await SeedStrandedIngestAsync(
            "poison-1@sender.example", age: OlderThanThreshold + TimeSpan.FromMinutes(1),
            rawEmailPath: poisonPath);
        var healthyId = await SeedStrandedIngestAsync(
            "healthy-1@sender.example", age: OlderThanThreshold,
            rawEmailPath: WriteRawEmail("healthy-1@sender.example",
                "Please quote 40 nos cable tray 300mm."));
        var ingestion = new RecordingIntake();
        var service = CreateService();

        await using var ctx = _db.ContextFor(null);
        await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);

        var healthy = await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == healthyId);
        Assert.Equal("Queued", healthy.ParseStatus);
        Assert.Single(ingestion.Calls);
        // The poison row was not laundered into "Queued" — whatever state it landed in, the
        // sweep never claims progress it did not make.
        var poison = await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == poisonId);
        Assert.NotEqual("Queued", poison.ParseStatus);
    }

    [Fact]
    public async Task AReEnqueueThatResolvesAsDuplicateStillCountsAsHandledWork()
    {
        // Idempotency contract: when the crash happened AFTER some content reached the
        // evidence store, the re-enqueue resolves as Duplicate through the existing
        // (BusinessUnitId, ContentHash) idempotency — which is handled work, so the row
        // still flips to Queued instead of failing or double-processing.
        await SeedMailboxAsync();
        var ingestId = await SeedStrandedIngestAsync(
            "dup-1@sender.example", age: OlderThanThreshold,
            rawEmailPath: WriteRawEmail("dup-1@sender.example", "Please quote 5 nos flange DN80."));
        var ingestion = new RecordingIntake(alreadyCaptured: true);
        var service = CreateService();

        await using var ctx = _db.ContextFor(null);
        var recovered = await service.SweepStrandedPendingIngestsAsync(
            ctx, ingestion, new StubLlm(), Tenant, DateTime.UtcNow);

        Assert.Equal(1, recovered);
        Assert.Single(ingestion.Calls);
        Assert.Equal("Queued",
            (await ctx.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId)).ParseStatus);
    }

    // ------------------------------------------------------------------------ test plumbing

    private async Task SeedMailboxAsync()
    {
        await using var ctx = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        Seed.EmailConfig(ctx, 1, Tenant);
        await ctx.SaveChangesAsync();
    }

    private async Task<long> SeedStrandedIngestAsync(string messageId, TimeSpan age, string? rawEmailPath)
    {
        await using var ctx = _db.ContextFor(null);
        var ingest = new EmailIngest
        {
            MessageId = messageId,
            EmailSubject = "RFQ",
            FromEmail = "ahmed@alnoortrading.ae",
            ToEmail = "inbox1@example.com",
            EmailConfigurationId = 1,
            CreatedOn = DateTime.UtcNow - age,
            ParseStatus = "Pending",
            RawEmailPath = rawEmailPath
        };
        ctx.EmailIngests.Add(ingest);
        await ctx.SaveChangesAsync();
        return ingest.Id;
    }

    private async Task SeedExtractionOccurrenceAsync(string logicalGroupKey)
    {
        await using var ctx = _db.ContextFor(null);
        var batchId = Guid.NewGuid();
        var corpus = DocumentCorpus.Create(Tenant, batchId, CorpusSourceType.Email);
        ctx.Add(corpus);
        await ctx.SaveChangesAsync();
        var source = SourceDocument.Create(Tenant, corpus.Id, new string('c', 64),
            "boq.xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "test-evidence", "quarantine/boq.xlsx", "v1", 10);
        ctx.Add(source);
        await ctx.SaveChangesAsync();
        var occurrence = SourceDocumentOccurrence.Create(
            Tenant, source.Id, corpus.Id, $"sweep-test:{logicalGroupKey}", "{}");
        occurrence.SetLogicalGroup(logicalGroupKey);
        ctx.Add(occurrence);
        await ctx.SaveChangesAsync();
    }

    private string WriteRawEmail(string messageId, string body)
    {
        Directory.CreateDirectory(_temp);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ahmed", "ahmed@alnoortrading.ae"));
        message.To.Add(new MailboxAddress("Intake", "inbox1@example.com"));
        message.Subject = "RFQ";
        message.MessageId = messageId;
        message.Body = new BodyBuilder { TextBody = body }.ToMessageBody();
        var path = Path.Combine(_temp, $"{Guid.NewGuid():N}.eml");
        message.WriteTo(path);
        return path;
    }

    private EmailService CreateService()
    {
        Directory.CreateDirectory(_temp);
        return new EmailService(
            context: _db.ContextFor(null),
            env: new StubEnvironment(_temp),
            logger: new NoopLogger<EmailService>(),
            llmService: new StubLlm(),
            scopeFactory: new UnusedScopeFactory(), // the sweep itself never resolves a scope
            configuration: new ConfigurationBuilder().Build(),
            storage: new TempStorage(_temp));
    }

    /// <summary>
    /// Records what the sweep re-routed. The sweep replays the canonical intake — the same step
    /// a fresh message takes — so this stands in for that, not for the raw document queue.
    /// </summary>
    private sealed class RecordingIntake : ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService
    {
        private readonly bool _alreadyCaptured;

        public RecordingIntake(bool alreadyCaptured = false) => _alreadyCaptured = alreadyCaptured;

        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
        {
            Calls.Enqueue(ingest.MessageId ?? message.MessageId ?? "<no-id>");
            // AlreadyCaptured is the intake's expression of the idempotency contract the old
            // Duplicate enqueue-outcome carried: content that already reached the evidence store
            // is handled work, so the row still flips to Queued rather than failing.
            return Task.FromResult(new EmailInquiryIntakeResult(
                AssemblyId: 1, BatchId: Guid.NewGuid(),
                Scheduled: _alreadyCaptured ? 0 : 1, AlreadyScheduled: _alreadyCaptured ? 1 : 0,
                Held: 0, ExpectedComponents: 1,
                AlreadyCaptured: _alreadyCaptured, SafeToAcknowledge: true, FailureReason: null));
        }
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("The sweep tests never resolve a scope.");
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
            => throw new InvalidOperationException("The sweep tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The sweep tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The sweep tests never delete storage.");
    }
}
