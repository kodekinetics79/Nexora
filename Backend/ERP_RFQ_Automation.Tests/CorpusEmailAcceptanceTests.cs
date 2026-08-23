using System.Text.Json;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The email half of the corpus, run against REAL PostgreSQL through the REAL canonical intake.
///
/// <para><b>Why it is not beside the document half.</b> These assertions are claims about what the
/// canonical intake DOES — how many extraction jobs a message fans out to, and the skip evidence
/// it records for a part it refuses (an embedded <c>message/rfc822</c>, a password-protected
/// attachment). Only <see cref="EmailInquiryCaptureService"/> writes those, and it takes
/// PostgreSQL row locks (<c>FOR UPDATE</c>) that SQLite cannot execute. Faking the intake to keep
/// the class on SQLite would leave the manifest's job counts and skip reasons asserting nothing
/// but the fake — the corpus exists precisely to stop that. The document half needs no database
/// at all and stays where it is, so eleven document cases do not pay for a serialized
/// PostgreSQL collection.</para>
///
/// <para>The collection's database is shared and its tests are serialized, so each test isolates
/// itself with its own business unit and mailbox id rather than by owning the schema.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CorpusEmailAcceptanceTests : IDisposable
{
    private readonly PostgreSqlTestDatabase _server;
    private readonly long _tenant;
    private readonly long _mailboxId;
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "nexora-corpus-email", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A distinct tenant and mailbox per test instance. xUnit builds one instance per case and
    /// the collection shares one database, so identity — not schema ownership — is what keeps
    /// two corpus messages from being asserted against each other.
    /// </summary>
    private static long _next = 7_100_000;

    public CorpusEmailAcceptanceTests(PostgreSqlTestDatabase server)
    {
        _server = server;
        _tenant = Interlocked.Increment(ref _next);
        _mailboxId = _tenant;
        Directory.CreateDirectory(_temp);
    }

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> EmailFiles()
    {
        var data = new TheoryData<string>();
        foreach (var email in CorpusManifest.Load().Emails) data.Add(email.File);
        return data;
    }

    [Theory]
    [MemberData(nameof(EmailFiles))]
    public async Task Every_corpus_email_is_triaged_and_fanned_out_exactly_as_the_manifest_says(string file)
    {
        var expected = CorpusManifest.Load().Email(file);
        await SeedMailboxAsync();
        var message = await MimeMessage.LoadAsync(CorpusManifest.PathOf(file));
        await using var ctx = _server.ContextFor(null);
        var config = await ctx.EmailConfigurations.SingleAsync(x => x.Id == _mailboxId);

        var persisted = await Service(ctx).ProcessSingleEmailAsync(
            message, config, ctx, new StubLlm(), RealIntake(ctx));

        Assert.True(persisted, "every corpus message must be durably captured");
        var ingest = await ctx.EmailIngests.AsNoTracking()
            .SingleAsync(x => x.EmailConfigurationId == _mailboxId);
        Assert.Equal(expected.MessageId, ingest.MessageId);
        Assert.Equal(expected.ExpectedTriageOutcome, ingest.TriageOutcome);
        Assert.Equal(expected.ExpectedParseStatus, ingest.ParseStatus);
        Assert.NotNull(ingest.TriageDecidedOn);

        var reasons = JsonSerializer.Deserialize<string[]>(ingest.TriageReasonJson ?? "[]")!;
        Assert.Equal(
            expected.ExpectedTriageReasons.OrderBy(x => x, StringComparer.Ordinal),
            reasons.OrderBy(x => x, StringComparer.Ordinal));

        // Fan-out: the number of durable extraction jobs the manifest promises, and no other.
        // Counted from the rows themselves rather than from calls to a double, so the number
        // means "what the tenant would be billed and shown" and not "what a stub was asked".
        Assert.Equal(expected.ExpectedJobCount,
            await ctx.Set<ExtractionJob>().CountAsync(j => j.BusinessUnitId == _tenant));

        // The raw message is retained on disk for EVERY outcome — including rejection.
        Assert.False(string.IsNullOrWhiteSpace(ingest.RawEmailPath));
        Assert.True(File.Exists(ingest.RawEmailPath), "raw .eml must be retained");

        // Skips (e.g. an embedded message/rfc822) are durable evidence, not log lines.
        foreach (var skipReason in expected.ExpectedSkippedAttachments)
            Assert.Contains(skipReason, ingest.SkippedAttachmentsJson ?? "");
        if (expected.ExpectedSkippedAttachments.Count == 0 && expected.ExpectedJobCount > 0)
            Assert.Null(ingest.SkippedAttachmentsJson);
    }

    [Fact]
    public async Task The_duplicate_pair_yields_one_ingest_row_no_matter_how_often_it_is_processed()
    {
        await SeedMailboxAsync();
        var bytes = CorpusManifest.Bytes("email-duplicate.eml");
        await using var ctx = _server.ContextFor(null);
        var config = await ctx.EmailConfigurations.SingleAsync(x => x.Id == _mailboxId);
        var service = Service(ctx);
        var intake = RealIntake(ctx);

        var first = await service.ProcessSingleEmailAsync(
            await Load(bytes), config, ctx, new StubLlm(), intake);
        var second = await service.ProcessSingleEmailAsync(
            await Load(bytes), config, ctx, new StubLlm(), intake);

        Assert.True(first);
        Assert.True(second); // durable record exists -> safe to mark \Seen, NOT re-ingested
        Assert.Equal(1, await ctx.EmailIngests.CountAsync(x => x.EmailConfigurationId == _mailboxId));
    }

    // ------------------------------------------------------------------------ test plumbing

    /// <summary>
    /// The REAL intake: capture, the assembly coordinator and the document gateway exactly as
    /// Program.cs composes them. Only the malware scanner and the LLM are stubbed.
    /// </summary>
    private IEmailInquiryIntakeService RealIntake(ErpRfqAutomationContext ctx)
    {
        var storage = new LocalFileStorage(_temp, _temp);
        var evidence = new LocalEvidenceObjectStorage(storage);
        var inspection = new DocumentFileInspectionService(new AlwaysCleanScanner());
        var ingestion = new DocumentIngestionService(
            new ExtractionQueue(ctx, NullLogger<ExtractionQueue>.Instance, new StubTenant(_tenant)),
            evidence, inspection, ctx,
            NullLogger<DocumentIngestionService>.Instance);
        return new EmailInquiryIntakeService(
            ctx,
            new EmailInquiryCaptureService(ctx, evidence,
                NullLogger<EmailInquiryCaptureService>.Instance),
            new EmailInquiryAssemblyCoordinator(ctx,
                NullLogger<EmailInquiryAssemblyCoordinator>.Instance),
            ingestion,
            NullLogger<EmailInquiryIntakeService>.Instance);
    }

    private EmailService Service(ErpRfqAutomationContext ctx)
        => new(
            context: ctx,
            env: new StubEnvironment(_temp),
            logger: NullLogger<EmailService>.Instance,
            llmService: new StubLlm(),
            scopeFactory: new UnusedScopeFactory(),
            configuration: new ConfigurationBuilder().Build(),
            storage: new LocalFileStorage(_temp, _temp));

    private async Task SeedMailboxAsync()
    {
        await using var ctx = _server.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, _tenant);
        Seed.EmailConfig(ctx, _mailboxId, _tenant);
        await ctx.SaveChangesAsync();
    }

    private static Task<MimeMessage> Load(byte[] bytes)
        => MimeMessage.LoadAsync(new MemoryStream(bytes));

    private sealed class AlwaysCleanScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(
            Stream content, CancellationToken cancellationToken = default)
            => Task.FromResult(MalwareScanResult.Clean("corpus-scanner"));
    }

    private sealed class UnusedScopeFactory : Microsoft.Extensions.DependencyInjection.IServiceScopeFactory
    {
        public Microsoft.Extensions.DependencyInjection.IServiceScope CreateScope() =>
            throw new InvalidOperationException("The corpus email tests never resolve a scope.");
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
}
