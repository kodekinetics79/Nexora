using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The operator's ONLY self-service recovery control, exercised on the thing it is most often
/// pointed at: an email attachment a malware-scanner outage blocked.
///
/// <para>"Retry blocked files" used to replay such a file through
/// <see cref="IDocumentIngestion"/> without its <c>emailInquiryComponentId</c>. The replayed job
/// therefore owned no component, the extraction worker's cutover fence resolved the message from
/// the sidecar and held it at <see cref="EmailInquiryAssemblyStatus.NeedsReview"/> — a state
/// <c>EmailInquiryLeadAssembler</c> refuses to act on — and the extraction it had just paid for
/// was discarded. The click moved the customer's RFQ FURTHER from becoming a Lead.</para>
///
/// <para>These tests drive the real graph end to end, because that is the only place the defect
/// is visible: every seam involved was individually green while the journey was broken.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class SecurityScanRecoveryEmailComponentPostgreSqlTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    /// <summary>A line that appears only in the second priced schedule (gaskets.csv).</summary>
    private const string GasketMarker = "GSK-3007";

    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-scan-recovery-" + Guid.NewGuid().ToString("N")[..12]);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the run is not a test failure */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// THE journey. A scanner outage blocks one attachment of a three-part inquiry; the operator
    /// clicks "Retry blocked files"; the message must become the ONE Lead carrying every line of
    /// both schedules.
    /// </summary>
    [Fact]
    public async Task Operator_retry_of_a_scanner_blocked_attachment_carries_the_message_to_its_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"scan-recovery-{Guid.NewGuid():N}@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        var scanner = new ScannerOutageOnMarker(GasketMarker, outages: 1);
        await using var services = BuildGraph(scanner);

        // ---- 1. The message arrives. The scanner is down for exactly one attachment. ----
        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, EmailToLeadHarness.BuildMessage(messageId));

        Assert.Equal(1, scanner.BlockedCount);
        Assert.Equal(2, schedule.Scheduled);
        Assert.Equal(1, schedule.Held);
        Assert.False(schedule.FullyScheduled);

        // The two healthy parts run to completion. The message is held, not lost, and no
        // body-only Lead was minted from the covering note.
        await EmailToLeadHarness.DrainQueueAsync(
            services, businessUnitId, waitForAssemblySettlement: false);

        long blockedOccurrenceId;
        long blockedComponentId;
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
                .SingleAsync(x => x.Id == assemblyId);
            Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, assembly.Status);
            Assert.Equal(0, await context.Leads.CountAsync(x => x.BusinessUnitId == businessUnitId));

            var blocked = await context.EmailInquiryComponents.AsNoTracking()
                .SingleAsync(x => x.AssemblyId == assemblyId
                                  && x.Status == EmailInquiryComponentStatus.FailedRecoverable);
            Assert.Null(blocked.ExtractionJobId);
            blockedComponentId = blocked.Id;

            // The intake side holds the exact bytes, replayable from the immutable source object.
            var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == businessUnitId
                                  && x.IntakeStatus == IntakeOccurrenceStatus.AwaitingSecurityScan);
            Assert.StartsWith("email:", occurrence.LogicalGroupKey);
            blockedOccurrenceId = occurrence.Id;
        }

        // ---- 2. THE CLICK. The operator's one self-service control, resolved from the container
        //         exactly as the controller resolves it. ----
        SecurityScanRetryResult retry;
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var recovery = scope.ServiceProvider.GetRequiredService<ISecurityScanRecoveryService>();
            var blockedBatches = await recovery.ListBlockedBatchesAsync(businessUnitId);
            Assert.Equal(schedule.BatchId, Assert.Single(blockedBatches).BatchId);

            retry = await recovery.RetryTenantAsync(businessUnitId);
        }

        Assert.Equal(1, retry.Eligible);
        Assert.True(retry.Queued == 1,
            "The operator's retry queued nothing: "
            + string.Join(" | ", retry.Items.Select(x => $"{x.FileName}={x.Status}({x.ErrorCode})")));

        // ---- 3. The replayed job runs. The message must now reach its Lead. ----
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
                .SingleAsync(x => x.Id == assemblyId);

            // (a) NOT NeedsReview. That is the state the broken retry produced, and it is a dead
            //     end: EmailInquiryLeadAssembler refuses to act on it and the state machine has
            //     no transition back into extraction.
            Assert.True(assembly.Status == EmailInquiryAssemblyStatus.Assembled,
                $"The message is {assembly.Status} ({assembly.StatusReason}), not Assembled.");
            Assert.NotNull(assembly.AssembledLeadId);

            // (b) The replayed job OWNS its component. Without this the worker's cutover fence
            //     discards the result it just paid for.
            var replayedJob = await context.Set<ExtractionJob>().AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == businessUnitId
                                  && x.SourceDocumentOccurrenceId == blockedOccurrenceId);
            Assert.Equal(blockedComponentId, replayedJob.EmailInquiryComponentId);
            Assert.Equal(0, await context.Set<ExtractionJob>().AsNoTracking()
                .CountAsync(x => x.BusinessUnitId == businessUnitId
                                 && x.EmailInquiryComponentId == null));

            // (c) The extraction was NOT discarded: the component completed and carries a
            //     durable result row.
            var recovered = await context.EmailInquiryComponents.AsNoTracking()
                .SingleAsync(x => x.Id == blockedComponentId);
            Assert.Equal(EmailInquiryComponentStatus.Completed, recovered.Status);
            Assert.Equal(replayedJob.Id, recovered.ExtractionJobId);
            Assert.Null(recovered.ReasonCode);
            Assert.Equal(3, await context.Set<EmailInquiryComponentResult>().AsNoTracking()
                .CountAsync(x => x.AssemblyId == assemblyId));

            // (d) ONE Lead, carrying every line of BOTH schedules — including the three the
            //     scanner outage had blocked.
            var lead = Assert.Single(await context.Leads.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId).ToListAsync());
            Assert.Equal(assembly.AssembledLeadId, lead.Id);
            var lines = await context.LeadItems.AsNoTracking()
                .Where(x => x.LeadId == lead.Id).OrderBy(x => x.Id).ToListAsync();
            Assert.Equal(
                ["VLV-1001", "VLV-1002", "GSK-3007", "GSK-3008", "GSK-3009"],
                lines.Select(x => x.ManufacturerPartNumber).ToArray());

            // (e) The intake occurrence is released, and the sweep has nothing left to offer.
            var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == blockedOccurrenceId);
            Assert.NotEqual(IntakeOccurrenceStatus.AwaitingSecurityScan, occurrence.IntakeStatus);
        }

        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var recovery = scope.ServiceProvider.GetRequiredService<ISecurityScanRecoveryService>();
            Assert.Empty(await recovery.ListBlockedBatchesAsync(businessUnitId));
            // Idempotent: a second click must not fork a second job or a second Lead.
            var again = await recovery.RetryTenantAsync(businessUnitId);
            Assert.Equal(0, again.Eligible);
        }
    }

    /// <summary>
    /// The regression control for the content-level job-reuse trap (item D of
    /// docs/EMAIL-TO-LEAD-EXECUTION-LEDGER.md), asserted on the DURABLE FACT rather than on the
    /// caller's argument.
    ///
    /// <para>Two different messages routinely carry byte-identical parts. If an email-owned
    /// occurrence is allowed to take the reuse branch it is bound to a job that has ALREADY
    /// reached Succeeded — a finished job never runs again, so that component never receives a
    /// result and its message waits at the barrier forever, silently.</para>
    ///
    /// <para>The guard must NOT depend on the caller remembering to pass
    /// <c>emailInquiryComponentId</c>: forgetting it is precisely the defect this suite exists
    /// for. So this test omits it deliberately and still requires a fresh job.</para>
    /// </summary>
    [Fact]
    public async Task An_email_owned_occurrence_never_takes_the_content_level_job_reuse_branch()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var bytes = Encoding.UTF8.GetBytes(
            "Part Number,Description,Quantity,Unit\nSHR-9001,Shared standard form line,3,EA\n");
        var root = Path.Combine(_storageRoot, "reuse");
        Directory.CreateDirectory(root);

        await using var context = _database.ContextFor(null);
        SeedBusinessUnit(context, businessUnitId);
        await context.SaveChangesAsync();

        var queue = new ExtractionQueue(context, new NoopLogger<ExtractionQueue>(), new StubTenant(null));
        var storage = new LocalEvidenceObjectStorage(new LocalFileStorage(root, root));
        var ingestion = new DocumentIngestionService(
            queue, storage, new AlwaysCleanInspection(), context,
            new NoopLogger<DocumentIngestionService>());

        // A first door ingests these exact bytes and the content becomes job #1.
        var first = await ingestion.IngestAsync(
            bytes, "standard-form.csv", businessUnitId, ExtractionSourceType.ManualUpload,
            metadata: new ExtractionJobMetadata { SourceOccurrenceId = "manual-standard-form" });
        Assert.Equal(EnqueueOutcome.Enqueued, first.Outcome);

        context.ChangeTracker.Clear();
        Assert.Equal(first.JobId, await context.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .Select(x => x.ExtractionJobId).SingleAsync());

        // A second, DIFFERENT message carries the identical bytes. Its occurrence is email-owned
        // — and the component id is deliberately omitted, as the recovery sweep used to omit it.
        var second = await ingestion.IngestAsync(
            bytes, "standard-form.csv", businessUnitId, ExtractionSourceType.Email,
            metadata: new ExtractionJobMetadata
            {
                SourceOccurrenceId = "email:message-b@buyer.example:attachment:1",
                LogicalGroupKey = "email:message-b@buyer.example"
            });

        Assert.NotEqual(first.JobId, second.JobId);
        Assert.Equal(EnqueueOutcome.Enqueued, second.Outcome);

        context.ChangeTracker.Clear();
        var emailOccurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == businessUnitId && x.ExtractionJobId == second.JobId);
        Assert.Equal("email:message-b@buyer.example", emailOccurrence.LogicalGroupKey);
        // The saving worth having is still taken: one stored object, two occurrences.
        Assert.Equal(1, await context.Set<SourceDocument>().AsNoTracking()
            .CountAsync(x => x.BusinessUnitId == businessUnitId));
        // And the reuse is not claimed in the cost ledger, because it did not happen.
        Assert.False(emailOccurrence.ProcessingReused);

        // The control: a NON-email occurrence of the same content still reuses, so this test is
        // proving an email-specific bypass rather than that reuse is simply broken.
        var third = await ingestion.IngestAsync(
            bytes, "standard-form.csv", businessUnitId, ExtractionSourceType.ManualUpload,
            metadata: new ExtractionJobMetadata { SourceOccurrenceId = "manual-standard-form-copy" });
        Assert.Equal(first.JobId, third.JobId);
        Assert.Equal(EnqueueOutcome.Duplicate, third.Outcome);

        // LEAVE NO CLAIMABLE WORK BEHIND.
        //
        // The PostgreSQL collection shares one container, and PostgreSqlProductionDialectTests
        // claims from the queue WITHOUT a tenant filter — it asserts that the next claimable job
        // in the whole database is its own. A Pending job left here is silently stolen by that
        // assertion and fails a test that has nothing to do with this one. This test's subject is
        // which job id was handed back, which is already settled above; nothing here needs to run.
        foreach (var stranded in await context.Set<ExtractionJob>()
                     .Where(x => x.BusinessUnitId == businessUnitId).ToListAsync())
            stranded.Status = ExtractionStatus.Succeeded;
        await context.SaveChangesAsync();
    }

    private ServiceProvider BuildGraph(IMalwareScanner scanner)
        => EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations =>
            {
                registrations.RemoveAll<IMalwareScanner>();
                registrations.AddSingleton(scanner);
                // Registered exactly as Program.cs registers it, so the container — not the
                // test — has to be able to satisfy the recovery service's dependencies.
                registrations.AddScoped<ISecurityScanRecoveryService, SecurityScanRecoveryService>();
            });

    private static void SeedBusinessUnit(ErpRfqAutomationContext context, long businessUnitId)
        => Seed.EnsureBusinessUnit(context, businessUnitId);

    private static long UniqueBusinessUnitId()
        => 942_000_000L + Random.Shared.Next(1, 900_000);

    /// <summary>
    /// A malware scanner that is unreachable for the first <c>outages</c> documents containing a
    /// marker, and healthy for everything else. That is the production shape of the ClamAV
    /// outage: one attachment of a message is blocked while its siblings sail through.
    /// </summary>
    private sealed class ScannerOutageOnMarker(string marker, int outages) : IMalwareScanner
    {
        private int _remaining = outages;
        private int _blocked;

        public int BlockedCount => Volatile.Read(ref _blocked);

        public async Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, ct);
            var text = Encoding.UTF8.GetString(buffer.ToArray());
            if (text.Contains(marker, StringComparison.Ordinal)
                && Interlocked.Decrement(ref _remaining) >= 0)
            {
                Interlocked.Increment(ref _blocked);
                return MalwareScanResult.Unavailable(
                    "clamav-test", MalwareScannerMessages.ScannerUnreachable,
                    "connection refused by the scanner daemon");
            }

            return MalwareScanResult.Clean("clamav-test");
        }
    }

    private sealed class AlwaysCleanInspection : IFileInspectionService
    {
        public Task<FileInspectionResult> InspectAsync(
            FileInspectionRequest request, CancellationToken ct = default)
            => Task.FromResult(new FileInspectionResult(
                FileInspectionStatus.Cleared, "text/csv", request.DeclaredLength ?? 0,
                "Cleared for test.", "clamav-test", "0")
            {
                MalwareStatus = MalwareScanStatus.Clean,
                ErrorCode = "security_scan_cleared"
            });
    }
}
