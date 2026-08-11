using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Worker-level regression for the live production dead-letter bug: an .xls whose layout
/// the deterministic mapper does not recognize must ride the unstructured path, and when
/// the tenant has no authorized external provider the allow-list gate must fail closed.
/// Recognized layouts must still complete deterministically without any LLM involvement.
///
/// <para>
/// The DISPOSITION of that refusal changed. It used to be a retryable hold, on the
/// reasoning that a document should never dead-letter on attempt 1. But the gate is a
/// decision, not a condition: it is seeded FALSE for every tenant and cannot change
/// between attempts, so the job re-asked the same closed gate five times on exponential
/// backoff — about an hour — and arrived at the same dead-letter with a reason nobody could
/// act on. It is now permanent on the first attempt and carries a named, actionable
/// category. Nothing about the gate itself is weakened: zero bytes still leave the
/// boundary, and it still refuses by default.
/// </para>
/// </summary>
public sealed class ExtractionWorkerSpreadsheetFallbackTests
{
    [Fact]
    public async Task UnrecognizedXls_UnauthorizedExternalProvider_DeadLettersImmediately_WithAnActionableReason()
    {
        // Headers with no commercial meaning, so the deterministic parser genuinely maps no
        // column. Ordinary RFQ spellings — and title blocks above the header — are now read
        // structurally, so this path needs a document that really is unreadable to reach the
        // external-provider gate.
        var fixture = UnrecognizableWorkbook();
        var queue = new RecordingQueue(CreateJob(801, "enquiry.xlsx", "xlsx"));
        var llm = new StubLlm(AiProviderClass.External, Ext.Result(Ext.Items(3, 0.9), 0.9));
        using var services = BuildServices(queue, fixture, llm, new RecordingPersister());
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var recordedError = await queue.PermanentFailure.Task.WaitAsync(TestWaits.Liveness);

            // Terminal state contract: no retry against a deterministically closed gate.
            // Reverting this puts the job back on five attempts of exponential backoff.
            Assert.False(queue.RetryableFailure.Task.IsCompleted);

            // The recorded reason is honest and specific: the spreadsheet was read, the
            // layout was not recognized, and the fail-closed refusal explains what is next.
            Assert.Contains("The XLSX spreadsheet was read successfully", recordedError);
            Assert.Contains("column layout was not recognized", recordedError);
            Assert.Contains("blocked for unstructured documents", recordedError);
            Assert.Contains("human review", recordedError);

            // ...and it is CLASSIFIABLE. Without the marker the tenant-facing category
            // function matches none of its rules and the single most common real-world
            // failure surfaces as generic EXTRACTION_FAILURE, indistinguishable from a
            // model timeout.
            Assert.Contains(ChunkedExtractionService.AiNotAuthorizedCode, recordedError);
            Assert.Equal(ExtractionDeadLetterService.AiNotAuthorizedCategory,
                ExtractionDeadLetterService.ClassifyFailure(recordedError));
            Assert.Contains("AI trust centre",
                ExtractionDeadLetterService.OperatorAction(
                    ExtractionDeadLetterService.AiNotAuthorizedCategory)!);

            // Fail-closed means zero bytes of document content left the boundary.
            Assert.Equal(0, llm.CallCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task RecognizedXls_StillCompletesDeterministically_WithoutTouchingTheLlm()
    {
        var fixture = ReadFixture("recognized-layout-rfq.xls");
        var queue = new RecordingQueue(CreateJob(802, "recognized.xls"));
        var llm = new StubLlm(AiProviderClass.External); // any call would return null and fail the run
        var persister = new RecordingPersister();
        using var services = BuildServices(queue, fixture, llm, persister);
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var outcome = await persister.Persisted.Task.WaitAsync(TestWaits.Liveness);

            Assert.Equal(0, llm.CallCount); // structured fast-path fully preserved
            Assert.NotEqual(ExtractionOutcomeStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.CanonicalImport);
            Assert.Equal(ExtractionProcessingPath.DeterministicRules, outcome.ProcessingPath);
            Assert.False(queue.RetryableFailure.Task.IsCompleted);
            Assert.False(queue.PermanentFailure.Task.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task FailedExtraction_StoresThePerChunkDiagnosticsInTheRecordedError()
    {
        // Dead-letter truth: the reason recorded through queue.FailAsync becomes
        // ExtractionJobs.LastError. It used to carry only the flattened summary while the
        // extractor's per-chunk diagnostics were discarded, so a dead-lettered job could
        // not say which stage failed. The stored error must now carry both.
        var document = "line one\nline two\nline three\nline four\nline five\n"u8.ToArray();
        var job = CreateJob(803, "plain-enquiry.txt");
        job.FileType = "txt";
        var queue = new RecordingQueue(job);
        var llm = new StubLlm(AiProviderClass.Local); // no scripted responses -> every chunk fails
        using var services = BuildServices(queue, document, llm, new RecordingPersister());
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var recordedError = await queue.RetryableFailure.Task.WaitAsync(TestWaits.Liveness);

            Assert.StartsWith("All chunks failed; no data extracted.", recordedError);
            Assert.Contains("[diagnostics:", recordedError);
            Assert.Contains("Document split into 1 chunk(s)", recordedError);
            Assert.Contains("Chunk 1/1 failed", recordedError);
            Assert.Contains("attempts_exhausted", recordedError);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public void ComposeFailureReason_CarriesGovernanceRefusalDiagnosticsIntoTheStoredError()
    {
        // The job-33 shape: every chunk refused by governance before any model call. The
        // stored error must lead with the honest summary and keep each chunk's refusal.
        const string reason = "AI governance refused every request before any model call was made "
            + "(duplicate_request). No chunk was extracted.";
        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Failed,
            ReviewReason = reason,
            Diagnostics = new List<string>
            {
                "Document split into 2 chunk(s) for 24 line item(s).",
                "Chunk 1/2 refused by AI governance before any model call (duplicate_request); 12 item(s) not extracted.",
                "Chunk 2/2 refused by AI governance before any model call (duplicate_request); 12 item(s) not extracted.",
                reason
            }
        };

        var stored = ExtractionWorker.ComposeFailureReason(outcome, structuredFallbackNote: null);

        Assert.StartsWith("AI governance refused every request", stored);
        Assert.Contains("Chunk 1/2 refused by AI governance", stored);
        Assert.Contains("Chunk 2/2 refused by AI governance", stored);
        Assert.DoesNotContain("All chunks failed", stored);
    }

    [Fact]
    public void ComposeFailureReason_KeepsTheSummaryFirst_AndBoundsTheDigestUnderTheColumnLimit()
    {
        // ExtractionQueue trims LastError at 4,000 characters keeping the START, so the
        // digest must be bounded separately or a verbose document would truncate away
        // nothing — but an unbounded one could push its own summary into the trimmed tail
        // of follow-on consumers. The summary always survives.
        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Failed,
            ReviewReason = "All chunks failed; no data extracted.",
            Diagnostics = Enumerable.Range(1, 200)
                .Select(i => $"Chunk {i}/200 failed ({new string('x', 100)}).")
                .ToList()
        };

        var stored = ExtractionWorker.ComposeFailureReason(
            outcome, "The XLSX spreadsheet was read successfully.");

        Assert.StartsWith(
            "The XLSX spreadsheet was read successfully. All chunks failed; no data extracted.", stored);
        Assert.Contains("[diagnostics:", stored);
        Assert.True(stored.Length <= 4_000,
            $"stored error is {stored.Length} chars; it must survive the 4,000-char LastError column intact");
    }

    // ---- harness ----------------------------------------------------------

    /// <summary>
    /// The journey the demo depends on: a real Word RFQ from the 120-document sample set goes
    /// through the worker end to end, produces every line, and never touches a model. If this
    /// test ever needs an authorized AI provider to pass, the deterministic path has regressed
    /// and every one of those documents is back to dead-lettering.
    /// </summary>
    [Fact]
    public async Task WordTableRfq_CompletesDeterministically_WithEveryLineIntact()
    {
        var fixture = ReadFixture("rfq-table.docx");
        var queue = new RecordingQueue(CreateJob(804, "RFQ-260011_Omega_Oil.docx", "docx"));
        // External provider on purpose: any model call at all would be refused and fail the run.
        var llm = new StubLlm(AiProviderClass.External);
        var persister = new RecordingPersister();
        using var services = BuildServices(queue, fixture, llm, persister);
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var outcome = await persister.Persisted.Task.WaitAsync(TestWaits.Liveness);

            Assert.Equal(0, llm.CallCount);
            Assert.Equal(ExtractionProcessingPath.DeterministicRules, outcome.ProcessingPath);
            Assert.NotEqual(ExtractionOutcomeStatus.Failed, outcome.Status);
            Assert.False(queue.RetryableFailure.Task.IsCompleted);
            Assert.False(queue.PermanentFailure.Task.IsCompleted);

            var document = Assert.Single(outcome.CanonicalImport!.Documents);
            Assert.Equal("RFQ-260011", document.RfqNo.Value);
            Assert.Equal("Omega Oil", document.BuyerName.Value);
            Assert.Equal(8, document.LineItems.Count);

            // An optional field that could not be parsed must never condemn the document.
            Assert.NotEqual(
                ERP_RFQ_Automation.DTOs.DocumentIntelligence.ValidationStatus.Invalid,
                document.ValidationStatus);

            var first = document.LineItems[0];
            Assert.Equal("1", first.LineItemNo.Value);              // not the header's row number
            Assert.Equal("SKU-2244", first.ManufacturerPartNumber.Value);
            Assert.Equal("Safety Relay", first.ProductName.Value);
            Assert.Equal(57, first.Quantity.Value);
            Assert.Equal("Urgent requirement", first.ItemText.Value);

            // A lead time we never read must stay unset — 0 would mean "deliver immediately".
            Assert.All(document.LineItems, line => Assert.NotEqual(
                ERP_RFQ_Automation.DTOs.DocumentIntelligence.CanonicalValueKind.Normalized,
                line.LeadTimeDays.Kind));

            Assert.Equal(8, outcome.ExtractedItemCount);

            // Every line of this document is correctly read, so NO line may ask for a check.
            // The document states no unit price, no currency, no unit of measure, no brand and
            // no lead time anywhere — it is a REQUEST for them, and its own closing paragraph
            // says so in words. Marking their absence a defect flagged all 641 lines of the
            // 120-document sample set and buried the lines that were genuinely wrong.
            Assert.All(document.LineItems, line => Assert.Equal(
                ERP_RFQ_Automation.DTOs.DocumentIntelligence.ValidationStatus.Valid,
                line.ValidationStatus));
            Assert.False(first.UnitPrice.StatedInDocument);
            Assert.False(first.Currency.StatedInDocument);
            Assert.False(first.LeadTimeDays.StatedInDocument);
            Assert.False(first.UnitOfMeasure.StatedInDocument);
            Assert.True(first.ProductName.StatedInDocument);
            Assert.True(first.Quantity.StatedInDocument);

            // ...and confidence, computed over the fields the document actually asserts, is
            // above the 0.60 acceptance threshold. Averaging in the five solicited fields put
            // every document in the sample set at 0.557 with nothing misread.
            Assert.True(outcome.Result!.OverallConfidence >= 0.60,
                $"document confidence is {outcome.Result.OverallConfidence:F3}; "
                + "the fields the document never states must not be averaged in");

            // The prose AROUND the table is retained. This is where the required warranty,
            // validity, country of origin, Incoterms and submission method live, and it
            // reached the lead for none of the 120 documents.
            Assert.NotNull(outcome.DocumentNarrative);
            Assert.Contains("country of origin", outcome.DocumentNarrative!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("warranty", outcome.DocumentNarrative!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("validity (30 days)", outcome.DocumentNarrative!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Incoterms", outcome.DocumentNarrative!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Submit quotation by email", outcome.DocumentNarrative!, StringComparison.OrdinalIgnoreCase);
            // The table's own rows are NOT duplicated into it — this is the surrounding prose.
            Assert.DoesNotContain("SKU-2244", outcome.DocumentNarrative!);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    /// <summary>A workbook the deterministic parser cannot map a single column of.</summary>
    private static byte[] UnrecognizableWorkbook()
    {
        OfficeOpenXml.ExcelPackage.LicenseContext = OfficeOpenXml.LicenseContext.NonCommercial;
        using var package = new OfficeOpenXml.ExcelPackage();
        var worksheet = package.Workbook.Worksheets.Add("Enquiry");
        worksheet.Cells[1, 1].Value = "Section";
        worksheet.Cells[1, 2].Value = "Narrative";
        worksheet.Cells[1, 3].Value = "Owner";
        worksheet.Cells[2, 1].Value = "MAT-88001";
        worksheet.Cells[2, 2].Value = "Ball valve DN50 PN16 stainless";
        worksheet.Cells[2, 3].Value = "Jubail Plant";
        return package.GetAsByteArray();
    }

    private static byte[] ReadFixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ExtractionJob CreateJob(long id, string fileName, string fileType = "xls") => new()
    {
        Id = id,
        BusinessUnitId = 7,
        BatchId = Guid.NewGuid(),
        SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = new string('e', 64),
        StoragePath = "memory://evidence/object",
        FileName = fileName,
        FileType = fileType,
        Status = ExtractionStatus.Leased,
        Attempts = 1,
        MaxAttempts = 5,
        NextAttemptAt = DateTime.UtcNow
    };

    private static ServiceProvider BuildServices(
        RecordingQueue queue, byte[] documentBytes, StubLlm llm, RecordingPersister persister)
        => new ServiceCollection()
            .AddLogging()
            .AddSingleton<IExtractionQueue>(queue)
            .AddSingleton<IExtractionDocumentReader>(new ProductionDocumentReader(
                NullLogger<ProductionDocumentReader>.Instance,
                new TestEnvironment(AppContext.BaseDirectory),
                new MemoryStorage(documentBytes)))
            .AddSingleton<IChunkedExtractionService>(new ChunkedExtractionService(
                llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>()))
            .AddSingleton<ILeadPersister>(persister)
            .BuildServiceProvider();

    private static ExtractionWorker CreateWorker(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        new ExtractionWorkerOptions
        {
            WorkerCount = 1,
            MaxConcurrentLlmCalls = 1,
            PerTenantConcurrencyCap = 1,
            LeaseDuration = TimeSpan.FromSeconds(30),
            IdlePollDelay = TimeSpan.FromMilliseconds(25)
        },
        services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>(),
        new TenantScopeAccessor());

    /// <summary>
    /// Hands out one job, then records which failure primitive the worker used.
    /// FailPermanentlyAsync is implemented EXPLICITLY — the interface's default member
    /// delegates to FailAsync, which would make dead-letters indistinguishable here.
    /// </summary>
    private sealed class RecordingQueue : IExtractionQueue
    {
        private readonly ExtractionJob _job;
        private int _claimed;

        public RecordingQueue(ExtractionJob job) => _job = job;

        public TaskCompletionSource<string> RetryableFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> PermanentFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExtractionJob?> ClaimAsync(
            string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
                return Task.FromResult<ExtractionJob?>(null);
            _job.LeaseExpiresAt = DateTime.UtcNow.Add(leaseDuration);
            return Task.FromResult<ExtractionJob?>(_job);
        }

        public Task<bool> RenewLeaseAsync(
            long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SetStatusAsync(
            long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> FailAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            RetryableFailure.TrySetResult(error);
            return Task.FromResult(true);
        }

        public Task<bool> FailPermanentlyAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            PermanentFailure.TrySetResult(error);
            return Task.FromResult(true);
        }

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> CompleteAsync(
            long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class RecordingPersister : ILeadPersister
    {
        public TaskCompletionSource<ChunkedExtractionOutcome> Persisted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> PersistAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long?> PersistAndCompleteAsync(
            ExtractionJob job,
            ChunkedExtractionOutcome outcome,
            IExtractionQueue queue,
            string workerId,
            int leaseAttempt,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            Persisted.TrySetResult(outcome);
            return Task.FromResult<long?>(55);
        }
    }

    private sealed class MemoryStorage(byte[] content) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    private sealed class TestEnvironment(string? contentRootPath = null) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath ?? Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
