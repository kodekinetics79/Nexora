using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Proof that a PART whose extraction job died can no longer hold a message open forever.
///
/// <para><b>The population this is about.</b> Before the barrier learned to close itself, a
/// component whose job dead-lettered stayed at <c>Extracting</c> for good. The state machine will
/// not finalize a message until EVERY component is terminal, so the message reported "2 of 3
/// parts assembled" in perpetuity: no lead, no review item, no error, nothing that would ever
/// look at it again. A single unreadable attachment silently swallowed an entire RFQ whose body
/// and other attachments had extracted perfectly — and every message stranded that way is still
/// sitting there, because the fix that stops it happening again does nothing for the ones it
/// already happened to.</para>
///
/// <para><b>How the strandings are produced.</b> The real pipeline runs — the real queue, the
/// real worker, the real persister, the real coordinator — and a job is dead-lettered or a
/// component's job reference is broken at the DATABASE before the worker starts, which is exactly
/// the shape the old code left behind. Nothing that decides an outcome is substituted, so the
/// state the sweep is handed is state the production pipeline actually produces.</para>
///
/// <para><b>The rule under test is not "old parts get closed".</b> It is "a part is resolved by
/// consulting the durable state of the job that owes it". A succeeded job means the result exists
/// and the component is reconciled. A stopped job is closed exactly as the live path closes it,
/// infrastructure faults holding and content faults finalizing. A job with attempts left is
/// GENUINELY IN FLIGHT and is left strictly alone — which is the assertion that stops this sweep
/// becoming a mechanism for discarding a customer's attachment because it was slow.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailInquiryStrandedComponentSweepPostgreSqlTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    // EXCLUSIVE to this class, and it has to be: DisposeAsync deletes the whole range, so a range
    // shared with another class in this collection makes each of them delete the other's rows
    // mid-run. 941_0xx belongs to the assembly-recovery lane and 943_0xx to the identical-content
    // lane; this one owns 947_0xx.
    private const long FirstBu = 947_000;
    private const long LastBu = 947_099;

    /// <summary>A dead letter whose prose carries the closed marker an evidence outage stamps.</summary>
    private const string InfrastructureError =
        "Evidence integrity failure: [EVIDENCE_OBJECT_MISSING] The stored evidence object is no "
        + "longer present in storage, so its integrity could not be verified.";

    /// <summary>A dead letter about the DOCUMENT. Retrying cannot change it.</summary>
    private const string ContentError =
        "The file passed inspection but no reader in this deployment can parse it.";

    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-stranded-" + Guid.NewGuid().ToString("N")[..12]);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Deletes every row this class created. The sweep is PLATFORM-wide, so a leaked stranded
    /// component is an eligible candidate for every later test in the suite.
    /// </summary>
    public async Task DisposeAsync()
    {
        await ExecuteAsync($"""
            SET session_replication_role = replica;
            DELETE FROM public."LeadItems" WHERE "LeadID" IN
                (SELECT "ID" FROM public."Leads" WHERE "BusinessUnitID" BETWEEN {FirstBu} AND {LastBu});
            DELETE FROM public."LeadStatusHistories" WHERE "LeadID" IN
                (SELECT "ID" FROM public."Leads" WHERE "BusinessUnitID" BETWEEN {FirstBu} AND {LastBu});
            DELETE FROM public."Leads" WHERE "BusinessUnitID" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."EmailInquiryComponentResults" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."EmailInquiryComponents" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."EmailInquiryAssemblies" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."ExtractionJobs" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."EmailIngests" WHERE "EmailConfigurationID" BETWEEN {FirstBu} AND {LastBu};
            SET session_replication_role = origin;
            """);

        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch (IOException) { }
    }

    // =====================================================================================
    // A STOPPED JOB — the two dispositions, and they are not interchangeable
    // =====================================================================================

    [Fact]
    public async Task An_infrastructure_dead_letter_holds_the_message_instead_of_quoting_without_the_document()
    {
        const long bu = 947_001;
        const string messageId = "stranded-0001@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var assemblyId = await CaptureAsync(services, bu, messageId);
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, InfrastructureError);
        await DrainAsync(services, bu);

        // THE STRANDED STATE, asserted before anything is swept. Two parts read, one part with a
        // dead job and a component nothing ever closed, and a message that will wait for it
        // forever.
        Assert.Equal(EmailInquiryComponentStatus.Extracting, await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, await StatusAsync(assemblyId));
        Assert.Equal(0, await CountLeadsAsync(bu));

        var result = await SweepAsync(services);

        Assert.Equal(1, result.StrandedComponents.Examined);
        Assert.Equal(1, result.StrandedComponents.Held);
        Assert.Equal(0, result.StrandedComponents.Skipped);
        Assert.Equal(0, result.StrandedComponents.Failed);

        // HELD, not finalized. The document still exists and is presumed readable once the
        // storage fault is fixed; a lead built now would be priced without it.
        Assert.Equal(EmailInquiryComponentStatus.FailedRecoverable,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryHoldReasons.StrandedInfrastructureFault,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, await StatusAsync(assemblyId));
        Assert.Equal(0, await CountLeadsAsync(bu));
    }

    [Fact]
    public async Task A_content_dead_letter_finalizes_the_message_into_review_with_everything_that_was_read()
    {
        const long bu = 947_002;
        const string messageId = "stranded-0002@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var assemblyId = await CaptureAsync(services, bu, messageId);
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, ContentError);
        await DrainAsync(services, bu);

        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, await StatusAsync(assemblyId));

        var result = await SweepAsync(services);

        Assert.Equal(1, result.StrandedComponents.Examined);
        Assert.Equal(1, result.StrandedComponents.Skipped);
        Assert.Equal(0, result.StrandedComponents.Held);

        // Terminal and commercially significant: the message FINALIZES, into a human's hands,
        // and says plainly that one part could not be read.
        Assert.Equal(EmailInquiryComponentStatus.Skipped,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryHoldReasons.StrandedJobStopped,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));

        // AND NOTHING THAT WAS READ IS LOST. The two parts that extracted keep their durable
        // results, so the reviewer is looking at a message with its content attached rather than
        // at an empty shell — this is what "with everything that was read" has to mean when the
        // assembler deliberately builds no lead for a message a human still has to judge.
        Assert.Equal(2, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponentResults"
            WHERE "AssemblyId" = {assemblyId} AND "PayloadJson" IS NOT NULL;
            """));
        Assert.Equal(2, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "Status" = 'Completed';
            """));

        // The stranded part is visible to the operator by name, not merely by count.
        Assert.Contains("could not be read",
            await TextAsync($"""
                SELECT "StatusReason" FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
                """) ?? string.Empty);
    }

    // =====================================================================================
    // NO JOB AT ALL — nothing will ever produce this part
    // =====================================================================================

    [Fact]
    public async Task A_component_whose_job_never_existed_or_is_gone_is_Skipped_rather_than_waited_for()
    {
        const long bu = 947_003;
        const string messageId = "stranded-0003@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var assemblyId = await CaptureAsync(services, bu, messageId);

        // Two different shapes of the same fact, so one code path cannot cover for the other.
        // valves.csv never recorded a job at all — a crash between planning and scheduling.
        // gaskets.csv names a job whose row is gone — a purge, or a job that was never committed.
        await KillJobAsync(assemblyId, "valves.csv", ExtractionStatus.DeadLetter, ContentError);
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, ContentError);
        await ExecuteAsync($"""
            UPDATE public."EmailInquiryComponents" SET "ExtractionJobId" = NULL
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'valves.csv';
            UPDATE public."EmailInquiryComponents" SET "ExtractionJobId" = 999999999
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv';
            """);
        await DrainAsync(services, bu);

        var result = await SweepAsync(services);

        Assert.Equal(2, result.StrandedComponents.Examined);
        Assert.Equal(2, result.StrandedComponents.Skipped);
        Assert.Equal(0, result.StrandedComponents.Held);

        // Skipped, and for two DIFFERENT recorded reasons — an operator deciding whether to
        // reprocess the message needs to know which of the two happened.
        Assert.Equal(EmailInquiryComponentStatus.Skipped, await ComponentStatusAsync(assemblyId, "valves.csv"));
        Assert.Equal(EmailInquiryHoldReasons.StrandedWithoutJob, await ComponentReasonAsync(assemblyId, "valves.csv"));
        Assert.Equal(EmailInquiryComponentStatus.Skipped, await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryHoldReasons.StrandedJobMissing, await ComponentReasonAsync(assemblyId, "gaskets.csv"));

        // And the message is finished rather than waiting on work nobody is doing.
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));
    }

    // =====================================================================================
    // A SUCCEEDED JOB — reconciled, and the message finishes in the SAME cycle
    // =====================================================================================

    [Fact]
    public async Task A_component_whose_job_actually_succeeded_is_reconciled_and_its_message_becomes_a_Lead()
    {
        const long bu = 947_004;
        const string messageId = "stranded-0004@buyer.example";
        await SeedAsync(bu, messageId);

        // Run the whole pipeline for real, then rewind ONE component to the state a lost
        // completion leaves behind: the job succeeded, the result is durable, and the component
        // never heard. That is the case where closing it as Skipped would throw away content the
        // customer sent and that we already have.
        await using var crashing = BuildGraph(s =>
            s.AddScoped<IEmailInquiryLeadAssembler, StoppedAssembler>());
        var assemblyId = await CaptureAsync(crashing, bu, messageId);
        await DrainAsync(crashing, bu);

        await ExecuteAsync($"""
            UPDATE public."EmailInquiryComponents" SET "Status" = 'Extracting'
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv';
            UPDATE public."EmailInquiryAssemblies" SET "Status" = 'Extracting' WHERE "Id" = {assemblyId};
            """);

        await using var services = BuildGraph();
        var result = await SweepAsync(services);

        Assert.Equal(1, result.StrandedComponents.Examined);
        Assert.Equal(1, result.StrandedComponents.Reconciled);
        Assert.Equal(0, result.StrandedComponents.Skipped);
        Assert.Equal(0, result.StrandedComponents.Held);

        Assert.Equal(EmailInquiryComponentStatus.Completed,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));

        // ONE CYCLE, not two. Resolving the part is what MADE the message ready, so the same
        // sweep finishes it — the minimum-age grace exists to keep the sweep off work a live
        // worker is holding, and nothing is holding a message this sweep just settled.
        Assert.Equal(1, result.Recovered);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
        Assert.Equal(1, await CountLeadsAsync(bu));

        // And it is the SAME lead the live path would have built — every line from both
        // attachments, in the order the buyer wrote them.
        var leadId = await ScalarAsync($"""
            SELECT COALESCE("AssembledLeadId", 0) FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            """);
        Assert.Equal(
            ["VLV-1001", "VLV-1002", "GSK-3007", "GSK-3008", "GSK-3009"],
            await QueryAsync($"""
                SELECT "ManufacturerPartNumber" FROM public."LeadItems"
                WHERE "LeadID" = {leadId} ORDER BY "ID";
                """));
    }

    // =====================================================================================
    // GENUINELY IN FLIGHT — the assertion that keeps this sweep from destroying work
    // =====================================================================================

    [Fact]
    public async Task A_component_whose_job_still_has_attempts_left_is_not_touched()
    {
        const long bu = 947_005;
        const string messageId = "stranded-0005@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        // Captured and scheduled, and deliberately NOT drained: three components at Extracting
        // with three Pending jobs that have every attempt left. The sweep's threshold is zero in
        // this harness, so all three are LOOKED at — which is the whole point. What protects them
        // is the state of their jobs, not the clock.
        var assemblyId = await CaptureAsync(services, bu, messageId);

        var result = await SweepAsync(services);

        Assert.Equal(3, result.StrandedComponents.Examined);
        Assert.Equal(3, result.StrandedComponents.LeftInFlight);
        Assert.Equal(0, result.StrandedComponents.Resolved);
        Assert.Equal(0, result.StrandedComponents.Failed);

        Assert.Equal(3, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "Status" = 'Extracting';
            """));
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, await StatusAsync(assemblyId));

        // THE POSITIVE CONTROL. Without it every zero above could hold because the sweep does
        // nothing at all. The same three components, with their jobs exhausted, are resolved.
        await ExecuteAsync($"""
            UPDATE public."ExtractionJobs" SET "Status" = 'DeadLetter', "Attempts" = "MaxAttempts",
                "LastError" = '{ContentError}'
            WHERE "EmailInquiryComponentId" IN
                (SELECT "Id" FROM public."EmailInquiryComponents" WHERE "AssemblyId" = {assemblyId});
            """);
        var second = await SweepAsync(services);
        Assert.Equal(3, second.StrandedComponents.Skipped);
        Assert.Equal(0, second.StrandedComponents.LeftInFlight);
    }

    // =====================================================================================
    // IDEMPOTENCE AND FAULT ISOLATION
    // =====================================================================================

    [Fact]
    public async Task Sweeping_twice_changes_nothing_the_first_sweep_settled()
    {
        const long bu = 947_006;
        const string messageId = "stranded-0006@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var assemblyId = await CaptureAsync(services, bu, messageId);
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, ContentError);
        await DrainAsync(services, bu);

        var first = await SweepAsync(services);
        Assert.Equal(1, first.StrandedComponents.Skipped);
        var settled = await FingerprintAsync(assemblyId);

        var second = await SweepAsync(services);

        // Nothing left to examine — the components are terminal, so the candidate query does not
        // even see them. That is what makes the sweep safe to run every two minutes forever.
        Assert.Equal(0, second.StrandedComponents.Examined);
        Assert.Equal(0, second.StrandedComponents.Resolved);
        Assert.Equal(0, second.StrandedComponents.Failed);
        Assert.Equal(settled, await FingerprintAsync(assemblyId));
        Assert.Equal(0, await CountLeadsAsync(bu));
    }

    [Fact]
    public async Task One_unresolvable_part_does_not_stop_the_rest_of_the_platform_being_swept()
    {
        // The poisoned tenant sorts FIRST. Sorted second, the healthy one would already be done
        // before anything could go wrong and the test would pass with the isolation removed.
        const long poisoned = 947_007;
        const long healthy = 947_008;
        await SeedAsync(poisoned, "stranded-0007@buyer.example");
        await SeedAsync(healthy, "stranded-0008@buyer.example");

        // ONE TENANT AT A TIME, ALL THE WAY THROUGH — capture, strand, drain, then the next.
        //
        // Capturing both first and draining twice afterwards is a race this test lost reliably on
        // a slower runner. DrainQueueAsync runs a worker over the WHOLE queue and stops it the
        // moment the NAMED tenant's jobs are terminal, so the first drain can stop a worker that
        // is mid-flight on the SECOND tenant's job. A stopped worker abandons its lease by design
        // ("Leave the lease to expire; another worker reclaims it after shutdown"), and the claim
        // SQL will not reclaim a Leased row until LeaseExpiresAt passes — 60 seconds here, which
        // is exactly TestWaits.Liveness. The second drain then spends its entire window unable to
        // claim the row, and reports every job terminal because the lease expired and the job
        // finished while the failure message was being built.
        //
        // Draining each tenant to completion before the next one has any jobs removes the
        // cross-tenant in-flight work entirely. It is also the pattern the assembly-recovery lane
        // already uses, for the same reason.
        var poisonedAssembly = await StrandAsync(poisoned, "stranded-0007@buyer.example");
        var healthyAssembly = await StrandAsync(healthy, "stranded-0008@buyer.example");

        // The write the sweep makes for the poisoned tenant throws — a lock timeout, a connection
        // fault, a constraint nobody predicted all look like this from here. Injected at the ONE
        // seam that produces an unhandled failure per ROW, which is the boundary under test;
        // everything the sweep reads and every other tenant's write stays real.
        await using var services = BuildGraph(s => s.AddScoped<IEmailInquiryAssemblyCoordinator>(sp =>
            new ThrowingForTenantCoordinator(
                ActivatorUtilities.CreateInstance<EmailInquiryAssemblyCoordinator>(sp), poisoned)));

        var result = await SweepAsync(services);

        // The poisoned row is COUNTED, not swallowed, and it did not take the platform with it.
        Assert.Equal(1, result.StrandedComponents.Failed);
        Assert.Equal(1, result.StrandedComponents.Skipped);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(healthyAssembly));

        // The poisoned message is exactly where it was — a throw costs a cycle, never a message.
        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(poisonedAssembly, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, await StatusAsync(poisonedAssembly));
    }

    /// <summary>Real in every respect except that one tenant's component writes throw.</summary>
    private sealed class ThrowingForTenantCoordinator(
        IEmailInquiryAssemblyCoordinator inner, long poisonedBusinessUnitId)
        : IEmailInquiryAssemblyCoordinator
    {
        public Task<EmailInquiryComponent?> FindComponentAsync(
            long businessUnitId, long assemblyId, string componentKey, CancellationToken ct = default)
            => inner.FindComponentAsync(businessUnitId, assemblyId, componentKey, ct);

        public Task RecordComponentQueuedAsync(
            long businessUnitId, long assemblyId, string componentKey, long extractionJobId,
            CancellationToken ct = default)
            => inner.RecordComponentQueuedAsync(businessUnitId, assemblyId, componentKey, extractionJobId, ct);

        public Task RecordComponentOutcomeAsync(
            long businessUnitId, long assemblyId, string componentKey,
            EmailInquiryComponentStatus status, string? reasonCode, string? reasonDetail,
            long? sourceDocumentOccurrenceId, CancellationToken ct = default)
            => businessUnitId == poisonedBusinessUnitId
                ? throw new InvalidOperationException("Simulated write failure for one component.")
                : inner.RecordComponentOutcomeAsync(businessUnitId, assemblyId, componentKey, status,
                    reasonCode, reasonDetail, sourceDocumentOccurrenceId, ct);

        public Task<EmailInquiryAssemblyEvaluation> RecordComponentResultAsync(
            long businessUnitId, long componentId, long extractionJobId,
            EmailInquiryComponentResultPayload payload, CancellationToken ct = default)
            => inner.RecordComponentResultAsync(businessUnitId, componentId, extractionJobId, payload, ct);

        public Task<EmailInquiryAssemblyEvaluation> ReevaluateAsync(
            long assemblyId, long businessUnitId, CancellationToken ct = default)
            => inner.ReevaluateAsync(assemblyId, businessUnitId, ct);

        public Task MarkNoInquiryAsync(
            EmailInquiryAssembly assembly, string reason, CancellationToken ct = default)
            => inner.MarkNoInquiryAsync(assembly, reason, ct);

        public Task HoldForReviewAsync(
            long businessUnitId, long assemblyId, string reasonCode, string reasonDetail,
            CancellationToken ct = default)
            => inner.HoldForReviewAsync(businessUnitId, assemblyId, reasonCode, reasonDetail, ct);

        public Task MarkAssembledAsync(
            long businessUnitId, long assemblyId, long leadId, CancellationToken ct = default)
            => inner.MarkAssembledAsync(businessUnitId, assemblyId, leadId, ct);

        public Task<bool> DurableJobBelongsToComponentAsync(
            long businessUnitId, long extractionJobId, Guid expectedBatchId, string componentKey,
            CancellationToken ct = default)
            => inner.DurableJobBelongsToComponentAsync(
                businessUnitId, extractionJobId, expectedBatchId, componentKey, ct);
    }

    [Fact]
    public async Task A_tenant_the_work_gate_refuses_keeps_its_stranded_parts_untouched()
    {
        // The gate is what keeps a suspended or archived tenant out of a platform-wide sweep, and
        // the component phase must honour it exactly as the assembly phase does.
        const long refused = 947_009;
        await SeedAsync(refused, "stranded-0009@buyer.example");
        var assemblyId = await StrandAsync(refused, "stranded-0009@buyer.example");

        await using (var gated = BuildGraph(s => s.AddScoped<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>(
                         _ => new EmailToLeadHarness.RefusingWorkGate(refused))))
        {
            var result = await SweepAsync(gated);
            Assert.Equal(0, result.TenantsSwept);
            Assert.Equal(0, result.StrandedComponents.Examined);
        }

        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));

        // Positive control: the same stranded part, with the gate admitting, is resolved.
        await using var admitted = BuildGraph();
        Assert.Equal(1, (await SweepAsync(admitted)).StrandedComponents.Skipped);
    }

    [Fact]
    public async Task The_threshold_keeps_the_sweep_off_recent_parts_and_is_not_what_decides_their_fate()
    {
        // Every other test in this class runs with the threshold at zero, so the predicate could
        // be deleted and none of them would notice.
        const long bu = 947_010;
        await SeedAsync(bu, "stranded-0010@buyer.example");

        await using var services = BuildGraph();
        var assemblyId = await CaptureAsync(services, bu, "stranded-0010@buyer.example");
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, ContentError);
        await DrainAsync(services, bu);

        await using var patient = BuildGraph(s => s.AddSingleton(new EmailInquiryAssemblyRecoveryOptions
        {
            MinimumAge = TimeSpan.Zero, StrandedComponentSweepMinutes = 60
        }));
        Assert.Equal(0, (await SweepAsync(patient)).StrandedComponents.Examined);
        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));

        // The same part, aged past the threshold, is resolved — so the zero above is about the
        // threshold and not about the part being unresolvable.
        await ExecuteAsync($"""
            UPDATE public."EmailInquiryComponents"
            SET "UpdatedAtUtc" = now() - interval '2 hours'
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv';
            """);
        Assert.Equal(1, (await SweepAsync(patient)).StrandedComponents.Skipped);
        Assert.Equal(EmailInquiryComponentStatus.Skipped,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
    }

    // =====================================================================================
    // BOILERPLATE, END TO END — the second half of the invariant
    // =====================================================================================

    [Fact]
    public async Task A_terms_and_conditions_attachment_no_longer_downgrades_a_perfectly_good_RFQ()
    {
        const long bu = 947_011;
        const string messageId = "stranded-0011@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        // The same RFQ every other test in this suite uses, plus the file the buyer attaches to
        // every mail they send. Before this change the T&C PDF became a component nobody could
        // read and the whole message went to a human.
        var message = EmailToLeadHarness.BuildMessage(
            messageId, "RFQ 88-2410 Jubail expansion",
            Attachment("Terms & Conditions.pdf", "application/pdf"));

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, bu, message, expectedComponentCount: 4);

        // Four parts recorded, THREE scheduled. The boilerplate is a row — "we ignored it" and
        // "it was never there" stay different observations — but it costs no extraction job.
        Assert.Equal(3, schedule.Scheduled);
        Assert.Equal(EmailInquiryComponentStatus.Ignored,
            await ComponentStatusAsync(assemblyId, "Terms & Conditions.pdf"));
        Assert.Equal(EmailInquirySkipReasons.NonCommercialBoilerplate,
            await ComponentReasonAsync(assemblyId, "Terms & Conditions.pdf"));

        await DrainAsync(services, bu, assertNoFailures: true, waitForSettlement: true);

        // A CLEAN LEAD. Not NeedsReview, and with every priced line the buyer sent.
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
        Assert.Equal(1, await CountLeadsAsync(bu));

        var leadId = await ScalarAsync($"""
            SELECT COALESCE("AssembledLeadId", 0) FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            """);
        Assert.Equal(
            ["VLV-1001", "VLV-1002", "GSK-3007", "GSK-3008", "GSK-3009"],
            await QueryAsync($"""
                SELECT "ManufacturerPartNumber" FROM public."LeadItems"
                WHERE "LeadID" = {leadId} ORDER BY "ID";
                """));

        // The ignored part is NOT recorded as a loss: this list is what a reviewer is shown, and
        // a legal notice in it on every message is how the list stops being read.
        Assert.Null(await TextAsync($"""
            SELECT "SkippedPartsJson" FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            """));
    }

    [Fact]
    public async Task A_spreadsheet_named_like_boilerplate_still_reaches_extraction_on_a_real_message()
    {
        // The mirror of the test above, and the more expensive failure of the two: a bill of
        // quantities called "Terms and Conditions.xlsx" must be READ, not ignored.
        const long bu = 947_012;
        const string messageId = "stranded-0012@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var message = EmailToLeadHarness.BuildMessage(
            messageId, "RFQ 88-2410 Jubail expansion",
            Attachment("Terms and Conditions.csv", "text/csv",
                "Part Number,Description,Quantity,Unit\nBLT-9001,Stud bolt M20 A193 B7,200,EA\n"));

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, bu, message, expectedComponentCount: 4);

        // FOUR scheduled, not three. Nothing about that name bought it an exemption.
        Assert.Equal(4, schedule.Scheduled);
        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(assemblyId, "Terms and Conditions.csv"));

        await DrainAsync(services, bu, assertNoFailures: true, waitForSettlement: true);

        // And its line is ON the lead. Scheduling alone would prove only that a job was created;
        // this proves the priced content the file actually carried reached the inquiry.
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
        var leadId = await ScalarAsync($"""
            SELECT COALESCE("AssembledLeadId", 0) FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            """);
        Assert.Contains("BLT-9001", await QueryAsync($"""
            SELECT "ManufacturerPartNumber" FROM public."LeadItems" WHERE "LeadID" = {leadId};
            """));
    }

    // =====================================================================================
    // Harness
    // =====================================================================================

    private ServiceProvider BuildGraph(Action<IServiceCollection>? configure = null) =>
        EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(), configure);

    /// <summary>Stops the process where the worker would begin assembling.</summary>
    private sealed class StoppedAssembler : IEmailInquiryLeadAssembler
    {
        public Task<long?> AssembleAsync(long businessUnitId, long assemblyId, CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated process loss before assembly.");
    }

    private static MimePart Attachment(string fileName, string mime, string? content = null)
    {
        var slash = mime.IndexOf('/');
        return new MimePart(mime[..slash], mime[(slash + 1)..])
        {
            FileName = fileName,
            Content = new MimeContent(new MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(content ?? "%PDF-1.4 standard conditions"))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
            ContentTransferEncoding = ContentEncoding.Base64
        };
    }

    private async Task SeedAsync(long businessUnitId, string messageId)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
    }

    /// <summary>
    /// Drives ONE tenant to the stranded state and returns its assembly id: capture, strand the
    /// gaskets part with a content dead letter, then drain to completion.
    ///
    /// <para>Self-contained on purpose. The worker it starts covers the whole queue, so a caller
    /// that interleaved two tenants' captures and drains would let one drain stop a worker
    /// holding the other's job — see the note at the call site.</para>
    /// </summary>
    private async Task<long> StrandAsync(long businessUnitId, string messageId)
    {
        await using var services = BuildGraph();
        var assemblyId = await CaptureAsync(services, businessUnitId, messageId);
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, ContentError);
        await DrainAsync(services, businessUnitId);

        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, await StatusAsync(assemblyId));
        return assemblyId;
    }

    private static async Task<long> CaptureAsync(
        ServiceProvider services, long businessUnitId, string messageId)
    {
        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, EmailToLeadHarness.BuildMessage(messageId));
        Assert.Equal(3, schedule.Scheduled);
        return assemblyId;
    }

    /// <param name="waitForSettlement">
    /// False for the stranded-part tests, whose whole subject is the state INSIDE the window
    /// where a message has not settled and never will. True for the boilerplate tests, which are
    /// about a message that settles normally — an empty queue is not the finish line, because the
    /// worker completes the job and only then assembles.
    /// </param>
    private static Task DrainAsync(
        ServiceProvider services, long businessUnitId,
        bool assertNoFailures = false, bool waitForSettlement = false)
        => EmailToLeadHarness.DrainQueueAsync(
            services, businessUnitId, assertNoFailures, waitForSettlement);

    private static async Task<EmailInquiryRecoverySweepResult> SweepAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IEmailInquiryAssemblyRecoveryService>().SweepOnceAsync();
    }

    /// <summary>
    /// Leaves a component exactly as the OLD code left it: its job stopped, and nothing anywhere
    /// told the component. Written before the worker runs so the queue never claims the row.
    /// </summary>
    private Task KillJobAsync(long assemblyId, string fileName, ExtractionStatus status, string error)
        => ExecuteAsync($"""
            UPDATE public."ExtractionJobs"
            SET "Status" = '{status}', "Attempts" = "MaxAttempts",
                "LastError" = '{error.Replace("'", "''")}', "UpdatedOn" = now()
            WHERE "EmailInquiryComponentId" IN
                (SELECT "Id" FROM public."EmailInquiryComponents"
                 WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName}');
            """);

    /// <summary>Every component's status and reason, so a test can assert nothing moved.</summary>
    private async Task<string> FingerprintAsync(long assemblyId) =>
        string.Join(";", await QueryAsync($"""
            SELECT COALESCE("FileName",'-') || ':' || "Status" || ':' || COALESCE("ReasonCode",'-')
            FROM public."EmailInquiryComponents" WHERE "AssemblyId" = {assemblyId} ORDER BY "Ordinal";
            """));

    private async Task<EmailInquiryAssemblyStatus> StatusAsync(long assemblyId)
    {
        var values = await QueryAsync($"""
            SELECT "Status" FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            """);
        return Enum.Parse<EmailInquiryAssemblyStatus>(Assert.Single(values)!);
    }

    private async Task<EmailInquiryComponentStatus> ComponentStatusAsync(long assemblyId, string fileName)
    {
        var values = await QueryAsync($"""
            SELECT "Status" FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName.Replace("'", "''")}';
            """);
        return Enum.Parse<EmailInquiryComponentStatus>(Assert.Single(values)!);
    }

    private async Task<string?> ComponentReasonAsync(long assemblyId, string fileName)
    {
        var values = await QueryAsync($"""
            SELECT "ReasonCode" FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName.Replace("'", "''")}';
            """);
        return Assert.Single(values);
    }

    private Task<int> CountLeadsAsync(long businessUnitId) => ScalarAsync(
        $"""SELECT count(*) FROM public."Leads" WHERE "BusinessUnitID" = {businessUnitId};""");

    private async Task<string?> TextAsync(string sql) => Assert.Single(await QueryAsync(sql));

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task<List<string?>> QueryAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string?>();
        while (await reader.ReadAsync())
            values.Add(reader.IsDBNull(0) ? null : reader.GetValue(0).ToString());
        return values;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
