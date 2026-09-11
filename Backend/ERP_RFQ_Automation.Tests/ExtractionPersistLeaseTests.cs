using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A 1,500-line bid list took six minutes to persist and the five-minute lease fenced its own
/// completion on every attempt, so the job dead-lettered after repeating the full write five
/// times. The lease the persister renews before writing is sized to the document instead.
/// </summary>
public sealed class ExtractionPersistLeaseTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(300, 5)]
    [InlineData(301, 10)]
    [InlineData(1500, 25)]
    [InlineData(1919, 35)]
    public void Persist_lease_grows_with_the_line_count_and_never_shrinks_below_the_base(int lines, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), LeadPersister.PersistLeaseFor(TimeSpan.FromMinutes(5), lines));
    }

    [Fact]
    public void Persist_lease_is_capped_for_absurdly_large_documents()
    {
        Assert.Equal(LeadPersister.MaxPersistLease, LeadPersister.PersistLeaseFor(TimeSpan.FromMinutes(5), 1_000_000));
    }

    [Fact]
    public void Persisted_line_count_takes_the_larger_of_extracted_and_canonical_lines()
    {
        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = Ext.Result(Ext.Items(3, 0.9), 0.9),
            ExtractedItemCount = 3,
            ExpectedItemCount = 3,
        };
        Assert.Equal(3, LeadPersister.PersistedLineCount(outcome));
    }

    [Fact]
    public async Task Persisting_a_large_outcome_renews_the_queue_lease_for_long_enough_to_finish()
    {
        var job = new ExtractionJob
        {
            Id = 7,
            BatchId = Guid.NewGuid(),
            BusinessUnitId = 1,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = new string('b', 64),
            StoragePath = "/nonexistent/extraction/bid-list.docx",
            FileName = "bid-list.docx",
            FileType = "docx",
            Attempts = 1,
        };
        await using (var seed = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            Seed.EmailConfig(seed, 100, 1);
            await seed.SaveChangesAsync();
            var corpus = DocumentCorpus.Create(job.BusinessUnitId, job.BatchId, CorpusSourceType.ManualUpload);
            seed.Add(corpus);
            await seed.SaveChangesAsync();
            var source = SourceDocument.Create(job.BusinessUnitId, corpus.Id, job.ContentHash,
                job.FileName ?? "document", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                "test", $"lead-persister/{job.Id}", "v1", 1);
            source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            seed.Add(source);
            await seed.SaveChangesAsync();
        }

        var outcome = new ChunkedExtractionOutcome
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = Ext.Result(Ext.Items(1500, 0.9), 0.9) with { Rfqno = "RFP-60000010028" },
            ExtractedItemCount = 1500,
            ExpectedItemCount = 1500,
        };
        var queue = new LeaseRecordingQueue();

        await using (var context = _db.ContextFor(null))
        {
            var persister = new LeadPersister(context, new NoopLogger<LeadPersister>());
            var leadId = await persister.PersistAndCompleteAsync(job, outcome, queue, "worker-a", 1, TimeSpan.FromMinutes(5));
            Assert.NotNull(leadId);
        }

        // Renewed once, before the write, for 25 minutes rather than the base five.
        Assert.Equal(new[] { TimeSpan.FromMinutes(25) }, queue.RenewedFor);
        Assert.True(queue.Completed);
    }

    private sealed class LeaseRecordingQueue : IExtractionQueue
    {
        public List<TimeSpan> RenewedFor { get; } = new();
        public bool Completed { get; private set; }

        public Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            RenewedFor.Add(leaseDuration);
            return Task.FromResult(true);
        }

        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
        {
            Completed = true;
            return Task.FromResult(true);
        }

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

/// <summary>What the intake ledger records about how a document was read.</summary>
public sealed class LeadProcessingPathMappingTests
{
    [Theory]
    [InlineData(ExtractionProcessingPath.DeterministicRules, null, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.Deterministic)]
    [InlineData(ExtractionProcessingPath.NativeParser, null, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.Deterministic)]
    [InlineData(ExtractionProcessingPath.LocalModel, null, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.LocalModel)]
    [InlineData(ExtractionProcessingPath.ExternalFallback, null, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.ExternalModel)]
    [InlineData(ExtractionProcessingPath.LegacyUnknown, ERP_RFQ_Automation.AI.AiProviderClass.Local, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.LocalModel)]
    [InlineData(ExtractionProcessingPath.LegacyUnknown, ERP_RFQ_Automation.AI.AiProviderClass.External, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.ExternalModel)]
    public void A_template_read_is_deterministic_and_never_an_external_provider(
        ExtractionProcessingPath path, ERP_RFQ_Automation.AI.AiProviderClass? provider, ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath expected)
    {
        var outcome = new ChunkedExtractionOutcome { Status = ExtractionOutcomeStatus.Ok, ProcessingPath = path, AiProviderClass = provider };
        Assert.Equal(expected, LeadPersister.LeadPathFor(outcome));
    }
}
