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
/// <para><b>What changed.</b> The fence used to HOLD every component, because there was nowhere
/// durable to put an extraction result. There is now, so a component's result is recorded and
/// the component completes; the fence's remaining job is the one it always had — no Lead is
/// created per component. Ownership is read from the job's own EmailInquiryComponentId rather
/// than from a back-reference written after the insert, which a worker could claim ahead of.</para>
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

        public readonly List<(long Bu, long ComponentId, long JobId,
            EmailInquiryComponentResultPayload Payload)> Results = [];

        /// <summary>What Reevaluate will claim the message became. Default: still extracting.</summary>
        public EmailInquiryAssemblyStatus EvaluatesTo { get; set; } =
            EmailInquiryAssemblyStatus.Extracting;

        public Task<EmailInquiryAssemblyEvaluation> RecordComponentResultAsync(
            long businessUnitId, long componentId, long extractionJobId,
            EmailInquiryComponentResultPayload payload, CancellationToken ct = default)
        {
            Results.Add((businessUnitId, componentId, extractionJobId, payload));
            return Task.FromResult(new EmailInquiryAssemblyEvaluation(EvaluatesTo, 1, 1, null));
        }

        public readonly List<(long Bu, long AssemblyId, string ReasonCode, string Detail)> Holds = [];

        public Task HoldForReviewAsync(long bu, long assemblyId, string reasonCode,
            string reasonDetail, CancellationToken ct = default)
        {
            Holds.Add((bu, assemblyId, reasonCode, reasonDetail));
            return Task.CompletedTask;
        }

        public Task MarkAssembledAsync(long bu, long assemblyId, long leadId,
            CancellationToken ct = default) => Task.CompletedTask;

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

    private static ExtractionJob Job(
        ExtractionSourceType sourceType, long id = 1, long? componentId = null) => new()
    {
        Id = id,
        EmailInquiryComponentId = componentId,
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

    private async Task<(long AssemblyId, long ComponentId, string Key)> SeedComponentAsync(long jobId)
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

        var component = assembly.Components.First();
        return (assembly.Id, component.Id, component.ComponentKey);
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
    public async Task An_email_job_with_NO_component_and_no_assembly_is_refused_with_a_reason()
    {
        // THE CUTOVER FENCE, and the history behind it.
        //
        // A blanket "fail closed on SourceType == Email" once returned 0 for every email job,
        // because capture was not wired so NO email job had a component. Mail was polled, jobs
        // ran, nothing became an RFQ — and the whole suite stayed green. The branch was removed
        // for that reason and the reason is now gone: both producers go through
        // EmailInquiryIntakeService, and ScheduleAsync writes the component id with the job row.
        //
        // So a component-less email job is legacy in-flight work or a scheduler/worker
        // disagreement, and either way a per-document Lead from one part of a message is the
        // defect the barrier exists to remove. It is refused, with a sentence an operator can
        // act on and no identifiers beyond the job and the tenant.
        var coordinator = new RecordingCoordinator();
        var job = Job(ExtractionSourceType.Email);

        await using var ctx = _db.ContextFor(null);
        await SeedSourceAsync(ctx, job);
        var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>(),
            emailAssemblies: coordinator);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => persister.PersistAsync(job, Outcome()));

        Assert.Contains("no inquiry component", error.Message);
        Assert.Contains("cutover", error.Message);
        // Non-sensitive: the reason names the job and the tenant, never the file, the sender or
        // where any of it is stored.
        Assert.DoesNotContain(job.StoragePath, error.Message);
        Assert.DoesNotContain(job.FileName!, error.Message);

        await using var assertCtx = _db.ContextFor(null);
        Assert.Equal(0, await assertCtx.Leads.CountAsync());
        Assert.Empty(coordinator.Outcomes);
        Assert.Empty(coordinator.Holds);
    }

    [Fact]
    public async Task An_email_job_with_NO_component_holds_the_message_when_its_assembly_is_known()
    {
        // The other half: the message DOES exist as an aggregate, so the operator gets a
        // message-level hold they can see on the inbound mail screen rather than a queue row
        // nobody reads. Held, not lost — every sibling component and the raw evidence are
        // untouched, and no Lead is minted from this fragment.
        var coordinator = new RecordingCoordinator();
        var seeded = await SeedComponentAsync(jobId: 31);

        long ingestId;
        await using (var lookup = _db.ContextFor(null))
        {
            ingestId = await lookup.Set<EmailInquiryAssembly>()
                .Where(x => x.Id == seeded.AssemblyId).Select(x => x.EmailIngestId).SingleAsync();
        }

        // A job for the same MESSAGE that names no component — exactly what a legacy job or a
        // scheduler that skipped ScheduleAsync produces.
        var job = Job(ExtractionSourceType.Email, id: 32);
        job.StoragePath = Path.Combine(Path.GetTempPath(), $"fence-{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(job.StoragePath, "one part of a message");
        await new ExtractionJobMetadata { EmailIngestId = ingestId, LeadSource = "Email" }
            .SaveAsync(job.StoragePath, Bu);

        try
        {
            await using var ctx = _db.ContextFor(null);
            await SeedSourceAsync(ctx, job);
            var leadId = await new LeadPersister(ctx, new NoopLogger<LeadPersister>(),
                emailAssemblies: coordinator).PersistAsync(job, Outcome());

            Assert.Equal(0, leadId);
        }
        finally
        {
            File.Delete(job.StoragePath);
            File.Delete(ExtractionJobMetadata.SidecarPath(job.StoragePath, Bu));
        }

        var hold = Assert.Single(coordinator.Holds);
        Assert.Equal(Bu, hold.Bu);
        Assert.Equal(seeded.AssemblyId, hold.AssemblyId);
        Assert.Equal(EmailInquiryHoldReasons.OwnershipUnresolved, hold.ReasonCode);
        Assert.Equal(EmailInquiryHoldReasons.OwnershipUnresolvedDetail, hold.Detail);

        await using var assertCtx = _db.ContextFor(null);
        Assert.Equal(0, await assertCtx.Leads.CountAsync());
    }

    [Fact]
    public async Task An_email_job_WITH_a_component_records_a_durable_result_and_creates_no_Lead()
    {
        var coordinator = new RecordingCoordinator();
        var seeded = await SeedComponentAsync(jobId: 7);
        var job = Job(ExtractionSourceType.Email, id: 7, componentId: seeded.ComponentId);

        await using (var ctx = _db.ContextFor(null))
        {
            var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>(),
                emailAssemblies: coordinator);
            await persister.PersistAsync(job, Outcome());
        }

        // No per-component Lead. This is the fence's entire remaining purpose.
        await using var assertCtx = _db.ContextFor(null);
        Assert.Equal(0, await assertCtx.Leads.CountAsync());

        // The result went somewhere durable, attributed to the component the JOB names.
        var recorded = Assert.Single(coordinator.Results);
        Assert.Equal(Bu, recorded.Bu);
        Assert.Equal(seeded.ComponentId, recorded.ComponentId);
        Assert.Equal(job.Id, recorded.JobId);
    }

    [Fact]
    public async Task The_recorded_result_carries_the_extracted_lines_and_not_an_empty_payload()
    {
        // The failure this pins is silent result loss: a component marked done while its
        // extraction output went nowhere, so the barrier later assembles a Lead from whatever
        // parts happened to survive. The payload must genuinely contain the work.
        var coordinator = new RecordingCoordinator();
        var seeded = await SeedComponentAsync(jobId: 11);
        var job = Job(ExtractionSourceType.Email, id: 11, componentId: seeded.ComponentId);

        await using (var ctx = _db.ContextFor(null))
        {
            await new LeadPersister(ctx, new NoopLogger<LeadPersister>(), emailAssemblies: coordinator)
                .PersistAsync(job, Outcome());
        }

        var payload = Assert.Single(coordinator.Results).Payload;
        Assert.Contains("RFQ-1", payload.PayloadJson);
        Assert.Equal(2, payload.ExtractedItemCount);
        Assert.Equal(2, payload.ExpectedItemCount);
        Assert.False(string.IsNullOrWhiteSpace(payload.ProcessingPath));

        // And the fence did NOT fall back to the old hold.
        Assert.DoesNotContain(coordinator.Outcomes,
            o => o.ReasonCode == EmailInquiryHoldReasons.AssemblyResultStorePending);
    }

    [Fact]
    public async Task Manual_upload_is_untouched_by_the_fence()
    {
        // Non-email ingestion must keep its existing behaviour, including when an unrelated
        // component row exists in the database for some other job.
        var coordinator = new RecordingCoordinator();
        await SeedComponentAsync(jobId: 999);
        // No EmailInquiryComponentId: a manual upload can never carry one — the database CHECK
        // constraint refuses it — so the fence must not engage.
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
        Assert.Empty(coordinator.Results);
    }

    [Fact]
    public async Task Without_the_coordinator_the_fence_is_inert_and_does_not_stop_processing()
    {
        // The production graph must register the coordinator, but an unregistered one must not
        // silently stop ingestion — it must degrade to the pre-fence behaviour, which for an
        // email job is the ordinary provenance requirement rather than a silent zero.
        var seeded = await SeedComponentAsync(jobId: 21);
        var job = Job(ExtractionSourceType.Email, id: 21, componentId: seeded.ComponentId);

        await using var ctx = _db.ContextFor(null);
        await SeedSourceAsync(ctx, job);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LeadPersister(ctx, new NoopLogger<LeadPersister>()).PersistAsync(job, Outcome()));
    }
}
