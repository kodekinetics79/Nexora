using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The worker's assembly fence, exercised for the first time.
///
/// <para><b>Why this class exists.</b> Every other `LeadPersister` construction in the suite omits
/// the `emailAssemblies` argument, so the fence's outer guard was false in 100% of tests and the
/// entire block — including a branch that refused a Lead for every email job — was dead code
/// under test. That is precisely how a change that silently stopped all email-to-Lead processing
/// shipped with 4911 tests green and was found by a product owner looking at a screen.</para>
///
/// <para>These run on `TestDb`: the real model, SQLite in memory, foreign keys and unique indexes
/// enforced.</para>
/// </summary>
public class LeadPersisterEmailFenceTests : IDisposable
{
    private readonly TestDb _db = new();
    public void Dispose() => _db.Dispose();

    private const long Bu = 1;

    /// <summary>Records what the worker actually passes, so assertions run on real arguments.</summary>
    private sealed class RecordingCoordinator : IEmailInquiryAssemblyCoordinator
    {
        public readonly List<(long Bu, long AssemblyId, string Key, EmailInquiryComponentStatus Status,
            string? ReasonCode, string? Detail, long? OccurrenceId)> Outcomes = [];

        public Task RecordComponentOutcomeAsync(long businessUnitId, long assemblyId, string componentKey,
            EmailInquiryComponentStatus status, string? reasonCode, string? reasonDetail,
            long? sourceDocumentOccurrenceId, CancellationToken ct = default)
        {
            Outcomes.Add((businessUnitId, assemblyId, componentKey, status, reasonCode,
                reasonDetail, sourceDocumentOccurrenceId));
            return Task.CompletedTask;
        }

        public Task<EmailInquiryComponent?> FindComponentAsync(long bu, long assemblyId, string key,
            CancellationToken ct = default) => Task.FromResult<EmailInquiryComponent?>(null);
        public Task RecordComponentQueuedAsync(long bu, long assemblyId, string key, long jobId,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<EmailInquiryAssemblyEvaluation> ReevaluateAsync(long assemblyId, long bu,
            CancellationToken ct = default) => Task.FromResult(default(EmailInquiryAssemblyEvaluation));
        public Task MarkNoInquiryAsync(EmailInquiryAssembly assembly, string reason,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DurableJobBelongsToComponentAsync(long bu, long jobId, Guid batchId,
            string key, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static ExtractionJob Job(ExtractionSourceType sourceType, long id = 1) => new()
    {
        Id = id,
        BatchId = Guid.NewGuid(),
        BusinessUnitId = Bu,
        SourceType = sourceType,
        ContentHash = new string('a', 64),
        StoragePath = "/nonexistent/extraction/doc.pdf",
        FileName = "doc.pdf",
        FileType = "pdf",
        Attempts = 1
    };

    private static ChunkedExtractionOutcome Outcome() => new()
    {
        Status = ExtractionOutcomeStatus.Ok,
        Result = Ext.Result(Ext.Items(2, 0.9), 0.9) with { Rfqno = "RFQ-1" },
        ExpectedItemCount = 2,
        ExtractedItemCount = 2
    };

    private async Task<(long AssemblyId, string Key)> SeedComponentAsync(long jobId)
    {
        await using var ctx = _db.ContextFor(null);
        // The real model enforces the chain: an assembly needs an ingest, which needs a mailbox,
        // which needs a business unit. Seeding it properly is the point — a fixture that skips
        // the parents proves nothing about behaviour under real constraints.
        if (!await ctx.BusinessUnits.AnyAsync(x => x.Id == Bu))
        {
            ctx.BusinessUnits.Add(new BusinessUnit
            {
                Id = Bu, BusinessUnitCode = "FENCE", BusinessUnitName = "Fence",
                IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            });
            ctx.EmailConfigurations.Add(new EmailConfiguration
            {
                Id = 1, BusinessUnitId = Bu, ConfigurationName = "Inbound",
                EmailAddress = "rfq@nexora.example", Protocol = "IMAP",
                Host = "imap.secureserver.net", Port = 993, Username = "rfq@nexora.example",
                Password = "unused-by-this-test", UseSsl = true, PollingInterval = 5,
                IsActive = true, CreatedOn = DateTime.UtcNow
            });
            await ctx.SaveChangesAsync();
        }

        var ingest = new EmailIngest
        {
            MessageId = $"m-{jobId}@customer.example",
            FromEmail = "buyer@customer.example",
            EmailConfigurationId = 1,
            CreatedOn = DateTime.UtcNow
        };
        ctx.Add(ingest);
        await ctx.SaveChangesAsync();

        var assembly = new EmailInquiryAssembly
        {
            BusinessUnitId = Bu,
            EmailIngestId = ingest.Id,
            EmailConfigurationId = 1,
            MessageKey = ingest.MessageId,
            ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
            ExpectedComponentCount = 1,
            Status = EmailInquiryAssemblyStatus.Extracting,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        assembly.Components.Add(new EmailInquiryComponent
        {
            BusinessUnitId = Bu,
            ComponentKey = $"email:{ingest.MessageId}:part:1",
            Ordinal = 0,
            Kind = EmailInquiryComponentKind.Attachment,
            Status = EmailInquiryComponentStatus.Extracting,
            ExtractionJobId = jobId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        ctx.Add(assembly);
        await ctx.SaveChangesAsync();

        return (assembly.Id, assembly.Components.First().ComponentKey);
    }

    private static async Task SeedSourceAsync(ErpRfqAutomationContext ctx, ExtractionJob job)
    {
        var corpus = DocumentCorpus.Create(job.BusinessUnitId, job.BatchId, CorpusSourceType.ManualUpload);
        ctx.Add(corpus);
        await ctx.SaveChangesAsync();
        var source = SourceDocument.Create(job.BusinessUnitId, corpus.Id, job.ContentHash,
            job.FileName ?? "document", "application/pdf", "test", $"fence/{job.Id}", "v1", 1);
        source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
        ctx.Add(source);
        await ctx.SaveChangesAsync();
    }

    // ---- the regression this class exists to prevent -----------------------------------------

    [Fact]
    public async Task An_email_job_with_NO_component_is_NOT_swallowed_by_the_fence()
    {
        // THE regression. A blanket "fail closed on SourceType == Email" returned 0 for every
        // email job, because capture is not wired so no email job has a component. Mail was
        // polled, jobs ran, nothing became an RFQ — and the whole suite stayed green.
        //
        // The assertion is that persistence proceeds PAST the fence into ordinary email
        // handling. Here that surfaces as the provenance requirement email jobs genuinely have;
        // under the regression the method returned 0 silently and this would not throw at all.
        var coordinator = new RecordingCoordinator();
        var job = Job(ExtractionSourceType.Email);

        await using var ctx = _db.ContextFor(null);
        await SeedSourceAsync(ctx, job);
        var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>(),
            emailAssemblies: coordinator);

        await Assert.ThrowsAsync<InvalidOperationException>(() => persister.PersistAsync(job, Outcome()));

        // And crucially: the fence recorded nothing, because this job owns no component.
        Assert.Empty(coordinator.Outcomes);
    }

    [Fact]
    public async Task An_email_job_WITH_a_component_creates_no_Lead_and_reports_the_hold()
    {
        var coordinator = new RecordingCoordinator();
        var job = Job(ExtractionSourceType.Email, id: 7);
        var (assemblyId, key) = await SeedComponentAsync(job.Id);

        await using (var ctx = _db.ContextFor(null))
        {
            var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>(),
                emailAssemblies: coordinator);
            await persister.PersistAsync(job, Outcome());
        }

        await using var assertCtx = _db.ContextFor(null);
        Assert.Equal(0, await assertCtx.Leads.CountAsync());

        var outcome = Assert.Single(coordinator.Outcomes);
        Assert.Equal(Bu, outcome.Bu);
        // The AssemblyId must be the one the worker read at its join — passing tenant + key alone
        // binds one message's outcome onto another message's component row.
        Assert.Equal(assemblyId, outcome.AssemblyId);
        Assert.Equal(key, outcome.Key);
        Assert.Equal(EmailInquiryComponentStatus.FailedRecoverable, outcome.Status);
        Assert.Equal(EmailInquiryHoldReasons.AssemblyResultStorePending, outcome.ReasonCode);
        // The detail the operator actually receives, not the constant read back in isolation.
        Assert.Equal(EmailInquiryHoldReasons.AssemblyResultStorePendingDetail, outcome.Detail);
    }

    [Fact]
    public async Task The_held_component_is_never_reported_as_Completed()
    {
        // Completed would tell the barrier the part is done while its extraction output was
        // discarded — a Lead assembled from the remainder.
        var coordinator = new RecordingCoordinator();
        var job = Job(ExtractionSourceType.Email, id: 11);
        await SeedComponentAsync(job.Id);

        await using (var ctx = _db.ContextFor(null))
        {
            await new LeadPersister(ctx, new NoopLogger<LeadPersister>(), emailAssemblies: coordinator)
                .PersistAsync(job, Outcome());
        }

        Assert.DoesNotContain(coordinator.Outcomes,
            o => o.Status == EmailInquiryComponentStatus.Completed);
    }

    [Fact]
    public async Task Manual_upload_is_untouched_by_the_fence()
    {
        // Non-email ingestion must keep its existing behaviour, including when an unrelated
        // component row exists in the database for some other job.
        var coordinator = new RecordingCoordinator();
        await SeedComponentAsync(jobId: 999);
        var job = Job(ExtractionSourceType.ManualUpload, id: 3);

        await using (var ctx = _db.ContextFor(null))
        {
            await SeedSourceAsync(ctx, job);
            await new LeadPersister(ctx, new NoopLogger<LeadPersister>(), emailAssemblies: coordinator)
                .PersistAsync(job, Outcome());
        }

        await using var assertCtx = _db.ContextFor(null);
        Assert.Equal(1, await assertCtx.Leads.CountAsync());
        Assert.Empty(coordinator.Outcomes);
    }

    [Fact]
    public async Task Without_the_coordinator_the_fence_is_inert_and_does_not_stop_processing()
    {
        // The production graph must register the coordinator, but an unregistered one must not
        // silently stop ingestion — it must degrade to the pre-fence behaviour, which for an
        // email job is the ordinary provenance requirement rather than a silent zero.
        var job = Job(ExtractionSourceType.Email, id: 21);
        await SeedComponentAsync(job.Id);

        await using var ctx = _db.ContextFor(null);
        await SeedSourceAsync(ctx, job);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LeadPersister(ctx, new NoopLogger<LeadPersister>()).PersistAsync(job, Outcome()));
    }
}
