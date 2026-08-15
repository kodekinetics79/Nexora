using System.Collections.Concurrent;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using System.Security.Cryptography;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE CORPUS, PROVED FILE BY FILE AGAINST ITS MANIFEST.
///
/// Every file under <c>Corpus/</c> carries expectations written in advance in
/// <c>corpus-manifest.json</c>. These tests drive each file through the REAL production
/// component that first judges it — <see cref="EmailService.ProcessSingleEmailAsync"/> +
/// the deterministic triage gate for emails, <see cref="DocumentFileInspectionService"/> +
/// <see cref="ProductionDocumentReader"/> for documents — and assert the manifest's verdicts.
/// The OCR-heavy and failure-path corpus items are covered HERE, at reader level, so the
/// journey lane (<c>AcceptanceJourneyTests</c>) stays fast.
/// </summary>
public sealed class CorpusAcceptanceTests : IDisposable
{
    private const long Tenant = 3;
    private readonly TestDb _db = new();
    private readonly string _temp = Path.Combine(
        Path.GetTempPath(), "nexora-corpus-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_temp, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> EmailFiles()
    {
        var data = new TheoryData<string>();
        foreach (var email in CorpusManifest.Load().Emails) data.Add(email.File);
        return data;
    }

    public static TheoryData<string> DocumentFiles()
    {
        var data = new TheoryData<string>();
        foreach (var document in CorpusManifest.Load().Documents) data.Add(document.File);
        return data;
    }

    // --------------------------------------------------------------- emails through triage

    [Theory]
    [MemberData(nameof(EmailFiles))]
    public async Task Every_corpus_email_is_triaged_and_fanned_out_exactly_as_the_manifest_says(string file)
    {
        var expected = CorpusManifest.Load().Email(file);
        await SeedMailboxAsync();
        var message = await MimeMessage.LoadAsync(CorpusManifest.PathOf(file));
        var ingestion = new RecordingIngestion();
        await using var ctx = _db.ContextFor(null);
        var config = await ctx.EmailConfigurations.SingleAsync(x => x.Id == 1);

        var persisted = await Service().ProcessSingleEmailAsync(
            message, config, ctx, new StubLlm(), ingestion);

        Assert.True(persisted, "every corpus message must be durably captured");
        var ingest = await ctx.EmailIngests.AsNoTracking()
            .SingleAsync(x => x.EmailConfigurationId == 1);
        Assert.Equal(expected.MessageId, ingest.MessageId);
        Assert.Equal(expected.ExpectedTriageOutcome, ingest.TriageOutcome);
        Assert.Equal(expected.ExpectedParseStatus, ingest.ParseStatus);
        Assert.NotNull(ingest.TriageDecidedOn);

        var reasons = JsonSerializer.Deserialize<string[]>(ingest.TriageReasonJson ?? "[]")!;
        Assert.Equal(
            expected.ExpectedTriageReasons.OrderBy(x => x, StringComparer.Ordinal),
            reasons.OrderBy(x => x, StringComparer.Ordinal));

        // Fan-out: the number of durable extraction jobs the manifest promises, and no other.
        Assert.Equal(expected.ExpectedJobCount, ingestion.Calls.Count);

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
        var ingestion = new RecordingIngestion();
        await using var ctx = _db.ContextFor(null);
        var config = await ctx.EmailConfigurations.SingleAsync(x => x.Id == 1);
        var service = Service();

        var first = await service.ProcessSingleEmailAsync(
            await Load(bytes), config, ctx, new StubLlm(), ingestion);
        var second = await service.ProcessSingleEmailAsync(
            await Load(bytes), config, ctx, new StubLlm(), ingestion);

        Assert.True(first);
        Assert.True(second); // durable record exists -> safe to mark \Seen, NOT re-ingested
        Assert.Equal(1, await ctx.EmailIngests.CountAsync(x => x.EmailConfigurationId == 1));
        Assert.Single(ingestion.Calls); // the second delivery enqueued nothing
    }

    // ------------------------------------------------------- documents through inspection

    [Theory]
    [MemberData(nameof(DocumentFiles))]
    public async Task Every_corpus_document_is_inspected_and_read_exactly_as_the_manifest_says(string file)
    {
        var expected = CorpusManifest.Load().Document(file);
        var bytes = CorpusManifest.Bytes(file);

        // 1) The REAL inspection gate (real sniffing/archive checks; only malware is stubbed).
        var inspection = new DocumentFileInspectionService(new AlwaysCleanScanner());
        var verdict = await inspection.InspectAsync(new FileInspectionRequest(
            new MemoryStream(bytes), file, DeclaredLength: bytes.LongLength));
        Assert.Equal(expected.ExpectedInspection, verdict.Status.ToString());

        if (expected.ExpectedReaderOutcome == "InspectionRejected")
        {
            Assert.False(verdict.IsCleared);
            if (expected.ExpectedFailureContains is not null)
                Assert.Contains(expected.ExpectedFailureContains, verdict.Reason);
            return; // never reaches a reader — exactly the point
        }

        // 2) The REAL production reader over the cleared bytes.
        var reader = new ProductionDocumentReader(
            NullLogger<ProductionDocumentReader>.Instance,
            new StubEnvironment(AppContext.BaseDirectory),
            new MemoryStorage(bytes),
            inspection);
        var job = new ExtractionJob
        {
            Id = 1,
            BusinessUnitId = Tenant,
            StoragePath = "memory://corpus/" + file,
            ContentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            FileName = file,
            FileType = Path.GetExtension(file).TrimStart('.')
        };

        if (expected.ExpectedReaderOutcome == "ParseFailure")
        {
            var failure = await Assert.ThrowsAnyAsync<DocumentParsingException>(
                () => reader.ReadAsync(job));
            if (expected.ExpectedFailureContains is not null)
                Assert.Contains(expected.ExpectedFailureContains, failure.Message);
            return;
        }

        var input = await reader.ReadAsync(job);
        Assert.NotEqual(ExtractionProcessingPath.ExternalFallback, input.ProcessingPath);

        switch (expected.ExpectedReaderOutcome)
        {
            case "Structured":
                Assert.True(input.IsStructured);
                Assert.Equal(ExtractionProcessingPath.DeterministicRules, input.ProcessingPath);
                Assert.Equal(expected.ExpectedStructuredRows, input.StructuredRows!.Count);
                AssertMarkers(expected, StructuredText(input));
                break;
            case "Ocr":
                Assert.Equal(ExtractionProcessingPath.LocalOcr, input.ProcessingPath);
                if (input.OcrStatus == ExtractionOcrStatus.Completed)
                {
                    Assert.True(input.OcrPageCount > 0);
                    AssertMarkers(expected, ProseText(input));
                }
                else
                {
                    // The reader may fail OCR honestly — but it must never claim the value.
                    Assert.Equal(ExtractionOcrStatus.Failed, input.OcrStatus);
                }
                break;
            case "Prose":
                Assert.False(input.IsStructured);
                AssertMarkers(expected, ProseText(input));
                break;
            default:
                Assert.Fail($"Unknown manifest reader outcome '{expected.ExpectedReaderOutcome}'.");
                break;
        }
    }

    private static void AssertMarkers(CorpusDocument expected, string text)
    {
        foreach (var marker in expected.ExpectedTextMarkers)
            Assert.Contains(marker, text, StringComparison.OrdinalIgnoreCase);
    }

    private static string StructuredText(DocumentExtractionInput input)
        => string.Join(' ', input.StructuredRows!.SelectMany(row => new[]
        {
            row.RfqNo, row.BuyerName, row.ProductName, row.ManufacturerPartNumber,
            row.Quantity, row.UnitOfMeasure, row.Currency
        }).Where(value => value != null));

    private static string ProseText(DocumentExtractionInput input)
        => input.HeaderText + "\n" + string.Join('\n', input.LineItemRegions);

    // ------------------------------------------------------------------------ test plumbing

    private static Task<MimeMessage> Load(byte[] bytes)
        => MimeMessage.LoadAsync(new MemoryStream(bytes));

    private async Task SeedMailboxAsync()
    {
        await using var ctx = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        Seed.EmailConfig(ctx, 1, Tenant);
        await ctx.SaveChangesAsync();
    }

    private EmailService Service()
    {
        Directory.CreateDirectory(_temp);
        return new EmailService(
            context: _db.ContextFor(null),
            env: new StubEnvironment(_temp),
            logger: new NoopLogger<EmailService>(),
            llmService: new StubLlm(),
            scopeFactory: new UnusedScopeFactory(),
            configuration: new ConfigurationBuilder().Build(),
            storage: new TempStorage(_temp));
    }

    private sealed class RecordingIngestion : IDocumentIngestion
    {
        private long _nextJobId;
        public ConcurrentQueue<string> Calls { get; } = new();

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            CancellationToken ct = default)
        {
            Calls.Enqueue(fileName);
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

    private sealed class AlwaysCleanScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
            => Task.FromResult(MalwareScanResult.Clean("test-scanner"));
    }

    private sealed class MemoryStorage(byte[] content) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default)
            => Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("The corpus tests never resolve a scope.");
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

    private sealed class TempStorage : IFileStorage
    {
        private readonly string _root;
        public TempStorage(string root) => _root = root;
        public string RootPath => _root;
        public string ResolvePath(string storagePath) => Path.Combine(_root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([_root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("The corpus triage tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The corpus triage tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The corpus triage tests never delete storage.");
    }
}
