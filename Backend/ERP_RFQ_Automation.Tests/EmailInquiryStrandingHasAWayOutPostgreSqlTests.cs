using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Proof that the four ways a captured message could STOP MOVING all now end somewhere a person
/// can see.
///
/// <para><b>The rule.</b> An ingested email ends at a Lead or at an explicit, user-visible
/// rejection. Never in silence. Each test below drives one shape of message that used to violate
/// that and asserts the message reaches a decision — not merely that some code ran.</para>
///
/// <para><b>The shapes, and why each was invisible.</b>
/// <list type="number">
/// <item>Every part terminal AT CAPTURE: nothing is scheduled, so no worker ever reports, so the
/// barrier is never asked. The message stayed at Captured with zero completed parts and was
/// acknowledged to the mailbox, which suppresses it from every later poll.</item>
/// <item>A part HELD WITH NO JOB: no queue row to exhaust its attempts, no dead letter to
/// recover, and no sweep that queried the state. The manifest CONTRACT bump is the mass-casualty
/// version — every in-flight message captured under the previous planner version lands here at
/// once, and the refusal verdict makes the message SAFE TO ACKNOWLEDGE, so the mailbox releases
/// it.</item>
/// <item>A part the malware scanner REFUSED: recorded as a recoverable hold, which is not a
/// refusal — so the message was never acknowledged and the same infected attachment was
/// re-downloaded and re-scanned on every poll for a day.</item>
/// <item>A part READ and routed away from Lead creation: closed by nothing, so it sat at
/// Extracting for half an hour and then landed in review under a reason that was untrue.</item>
/// </list></para>
///
/// <para>The real pipeline runs throughout — the real queue, worker, persister, coordinator and
/// sweep. What is substituted is only the world outside the process: the malware scanner's
/// verdict and the availability of object storage.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailInquiryStrandingHasAWayOutPostgreSqlTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    // EXCLUSIVE to this class: DisposeAsync deletes the whole range.
    private const long FirstBu = 948_000;
    private const long LastBu = 948_099;

    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-wayout-" + Guid.NewGuid().ToString("N")[..12]);

    public Task InitializeAsync() => Task.CompletedTask;

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
    // 1. EVERY PART TERMINAL AT CAPTURE
    // =====================================================================================

    /// <summary>
    /// A quoted-only reply carrying one unsupported attachment. Nothing to schedule, nothing to
    /// fail, and — before this change — nothing that would ever look at the message again.
    /// </summary>
    [Fact]
    public async Task A_message_with_nothing_to_schedule_reaches_a_person_instead_of_stopping_at_Captured()
    {
        const long bu = 948_001;
        const string messageId = "wayout-0001@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        // No text part at all: the planner sees a quoted-only message, so there is no body
        // component. The .zip is unsupported, so it is recorded Skipped and terminal at capture.
        var mixed = new Multipart("mixed")
        {
            EmailToLeadHarness.Attachment(
                "drawings.zip", "application/zip", "PK not really a zip"u8.ToArray())
        };
        var message = new MimeMessage { Subject = "Re: RFQ 88-2410", Body = mixed };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Nexora", "rfq@nexora.example"));
        message.MessageId = messageId;
        message.Date = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

        var (assemblyId, schedule) = await CaptureAndScheduleAsync(services, bu, message);

        // The precondition that made this invisible: nothing queued, nothing held.
        Assert.Equal(0, schedule.Scheduled);
        Assert.Equal(0, schedule.Held);
        Assert.Null(schedule.FailureReason);

        // AND THE MESSAGE IS DECIDED, on this pass, with no sweep and no worker involved.
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));

        var reason = await AssemblyReasonAsync(assemblyId);
        Assert.False(string.IsNullOrWhiteSpace(reason),
            "A message sent to a person must say why, or the screen shows a blank row.");
        Assert.Contains("No part of this message could be read", reason!, StringComparison.Ordinal);
        AssertNoInternalNames(reason!);
    }

    // =====================================================================================
    // 2. HELD WITH NO JOB — and the manifest-contract wedge that produces it in bulk
    // =====================================================================================

    /// <summary>
    /// THE BLAST RADIUS, reproduced. A message captured under an earlier planner contract is
    /// re-planned by this build, the versions disagree, every non-terminal part is held with no
    /// job — and because the verdict is not Compatible the intake result says the message is SAFE
    /// TO ACKNOWLEDGE, so the mailbox releases it and the only remaining copy is the one we hold.
    ///
    /// <para>That combination is what turned a version bump into silent loss: held, unreachable,
    /// and no longer offered by the mailbox. This test asserts the wedge still forms exactly that
    /// way (it is not a bug in the verifier — refusing to schedule a message we cannot vouch for
    /// is correct) and that the sweep now ENDS it rather than leaving it there.</para>
    /// </summary>
    [Fact]
    public async Task A_manifest_contract_bump_no_longer_strands_the_messages_it_catches_in_flight()
    {
        const long bu = 948_002;
        const string messageId = "wayout-0002@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var message = EmailToLeadHarness.BuildMessage(messageId);
        var (assemblyId, first) = await CaptureAndScheduleAsync(services, bu, message);
        Assert.Equal(3, first.Scheduled);

        // Rewind the assembly to the previous planner contract and unbind its jobs — the exact
        // durable state a deploy leaves behind for a message captured by the OLD build and
        // scheduled by the NEW one.
        await ExecuteAsync($"""
            UPDATE public."EmailInquiryAssemblies"
            SET "ManifestContractVersion" = "ManifestContractVersion" - 1,
                "Status" = 'Captured'
            WHERE "Id" = {assemblyId};
            UPDATE public."EmailInquiryComponents"
            SET "Status" = 'Pending', "ExtractionJobId" = NULL
            WHERE "AssemblyId" = {assemblyId};
            """);

        var second = await ScheduleOnlyAsync(services, bu, message, assemblyId);

        // THE WEDGE, asserted rather than assumed.
        Assert.Equal(EmailManifestVerdict.ManifestVersionUnsupported, second.Verdict);
        Assert.Equal(0, second.Scheduled);
        Assert.Equal(3, second.Held);
        Assert.Equal(3, await CountComponentsAsync(assemblyId,
            "\"Status\" = 'FailedRecoverable' AND \"ExtractionJobId\" IS NULL"));

        // And the message is released by the mailbox on this pass — which is why it could never
        // be re-fetched afterwards. FullyAccepted is false, SafeToAcknowledge is true.
        var intakeResult = await IntakeResultForRefusedManifestAsync(services, bu, message);
        Assert.True(intakeResult.SafeToAcknowledge,
            "Precondition of the wedge: a manifest refusal releases the message from the mailbox.");
        Assert.False(intakeResult.FullyAccepted);

        // NOW THE FIX. The sweep claims a part held with no job — a state it never queried — and
        // ends the message rather than re-driving a disagreement no retry can settle.
        var sweep = await SweepAsync(services);
        Assert.Equal(3, sweep.StrandedComponents.Examined);
        Assert.Equal(3, sweep.StrandedComponents.Skipped);
        Assert.Equal(0, sweep.StrandedComponents.Rescheduled);

        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));
        Assert.Equal(EmailInquiryHoldReasons.SchedulingRefusedByManifest,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));

        var reason = await AssemblyReasonAsync(assemblyId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        AssertNoInternalNames(reason!);

        // Idempotent: the second sweep finds nothing, because the parts are terminal.
        Assert.Equal(0, (await SweepAsync(services)).StrandedComponents.Examined);
    }

    /// <summary>
    /// The recoverable half of the same population: a part held because DURABLE STORAGE was down
    /// while it was being scheduled. Nothing was wrong with the message, so the honest outcome is
    /// to schedule it again from the stored original once the fault clears — and to end up at a
    /// Lead, not at a review item.
    /// </summary>
    [Fact]
    public async Task A_part_held_by_a_storage_outage_is_re_driven_from_the_stored_original_and_becomes_a_Lead()
    {
        const long bu = 948_003;
        const string messageId = "wayout-0003@buyer.example";
        await SeedAsync(bu, messageId);

        var message = EmailToLeadHarness.BuildMessage(messageId);
        long assemblyId;

        // THE OUTAGE. One part cannot be stored, so scheduling holds it with no job. Everything
        // else about the pass is real.
        await using (var during = BuildGraph(s => s.AddScoped<IDocumentIngestion>(sp =>
                         new StorageOutageForOneFile(
                             ActivatorUtilities.CreateInstance<DocumentIngestionService>(sp),
                             "gaskets.csv"))))
        {
            var (id, schedule) = await CaptureAndScheduleAsync(during, bu, message);
            assemblyId = id;
            Assert.Equal(2, schedule.Scheduled);
            Assert.Equal(1, schedule.Held);
        }

        Assert.Equal(EmailInquiryComponentStatus.FailedRecoverable,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Null(await ComponentJobIdAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, await StatusAsync(assemblyId));

        // THE FAULT CLEARS. Storage is back, and the sweep re-drives scheduling from the durable
        // .eml — not from the mailbox, which stopped offering this message long ago.
        await using var after = BuildGraph(s => s.AddSingleton(RecoveryOptions(resumeWindowMinutes: 240)));

        var sweep = await SweepAsync(after);
        Assert.Equal(1, sweep.StrandedComponents.Rescheduled);
        Assert.Equal(0, sweep.StrandedComponents.Skipped);

        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.NotNull(await ComponentJobIdAsync(assemblyId, "gaskets.csv"));

        // AND IT FINISHES. A rescheduled part is worthless if nothing runs it: the real worker
        // drains the queue and the message becomes the same Lead it always should have been,
        // with every line from BOTH schedules.
        await EmailToLeadHarness.DrainQueueAsync(after, bu, assertNoFailures: true);
        await SweepAsync(after);

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
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

    /// <summary>
    /// THE BOUND. Re-driving forever is not recovery — it is a message that is permanently
    /// "recoverable" and permanently undecided. Past its window, the part is refused explicitly
    /// and the message goes to a person.
    /// </summary>
    [Fact]
    public async Task A_part_that_cannot_be_scheduled_is_eventually_refused_rather_than_retried_forever()
    {
        const long bu = 948_004;
        const string messageId = "wayout-0004@buyer.example";
        await SeedAsync(bu, messageId);

        var message = EmailToLeadHarness.BuildMessage(messageId);
        long assemblyId;

        await using (var during = BuildGraph(s => s.AddScoped<IDocumentIngestion>(sp =>
                         new StorageOutageForOneFile(
                             ActivatorUtilities.CreateInstance<DocumentIngestionService>(sp),
                             "gaskets.csv"))))
        {
            var (id, schedule) = await CaptureAndScheduleAsync(during, bu, message);
            assemblyId = id;
            Assert.Equal(1, schedule.Held);
        }

        // Window closed — the same state a part reaches after hours of failed re-drives, and the
        // state EVERY part stranded before this change is already in.
        await using var expired = BuildGraph(s => s.AddSingleton(RecoveryOptions(resumeWindowMinutes: 0)));

        // The two parts that DID schedule run to completion first, so the only thing still holding
        // the message open is the part with no job. Without this the assembly is legitimately
        // Extracting and the assertion below would be about the drain, not about the deadline.
        //
        // waitForAssemblySettlement is false deliberately: this message CANNOT settle on its own
        // — that is the whole subject of the test — so waiting for it would time out on the state
        // being asserted.
        await EmailToLeadHarness.DrainQueueAsync(
            expired, bu, assertNoFailures: true, waitForAssemblySettlement: false);
        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, await StatusAsync(assemblyId));

        var sweep = await SweepAsync(expired);
        Assert.Equal(0, sweep.StrandedComponents.Rescheduled);
        Assert.Equal(1, sweep.StrandedComponents.Skipped);

        Assert.Equal(EmailInquiryComponentStatus.Skipped,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryHoldReasons.SchedulingNotRecovered,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));

        var detail = await ComponentDetailAsync(assemblyId, "gaskets.csv");
        Assert.False(string.IsNullOrWhiteSpace(detail));
        AssertNoInternalNames(detail!);

        // Terminal means terminal: the next sweep does not find it again.
        Assert.Equal(0, (await SweepAsync(expired)).StrandedComponents.Examined);
    }

    // =====================================================================================
    // 3. A MALWARE VERDICT IS A REFUSAL, NOT A HOLD
    // =====================================================================================

    [Fact]
    public async Task An_infected_attachment_refuses_the_message_instead_of_holding_it_for_a_daily_rescan()
    {
        const long bu = 948_005;
        const string messageId = "wayout-0005@buyer.example";
        await SeedAsync(bu, messageId);

        await using var services = BuildGraph(s =>
            s.AddSingleton<IMalwareScanner>(new InfectedScanner()));

        var message = EmailToLeadHarness.BuildMessage(messageId);
        var (assemblyId, schedule) = await CaptureAndScheduleAsync(services, bu, message);

        // NOT HELD. A refusal is terminal, so the message is fully accounted for and the mailbox
        // may release it — which is what stops the same infected attachment being downloaded,
        // decoded and fed to the scanner again on every poll for the next day.
        Assert.Equal(0, schedule.Held);
        Assert.Equal(0, schedule.Scheduled);

        Assert.Equal(EmailInquiryComponentStatus.RefusedSecurity,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailIngestEnqueuer.MalwareRefusedReason,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));

        // The refusal outranks every other part's outcome: the whole message is refused.
        Assert.Equal(EmailInquiryAssemblyStatus.RejectedSecurity, await StatusAsync(assemblyId));

        var reason = await AssemblyReasonAsync(assemblyId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        AssertNoInternalNames(reason!);

        // Absorbing: nothing sweeps it back into processing.
        Assert.Equal(0, (await SweepAsync(services)).StrandedComponents.Examined);
        Assert.Equal(0, await CountLeadsAsync(bu));
    }

    [Fact]
    public async Task A_scanner_outage_still_holds_the_message_and_keeps_the_code_the_recovery_rule_reads()
    {
        // The mirror of the test above, and the reason the two must not share a branch: an
        // unreachable scanner is this deployment's plumbing, so the message is HELD and its
        // content is still readable once the scanner is back. The generic catch-all erased the
        // distinction along with the error code that carries it.
        const long bu = 948_006;
        const string messageId = "wayout-0006@buyer.example";
        await SeedAsync(bu, messageId);

        await using var services = BuildGraph(s =>
            s.AddSingleton<IMalwareScanner>(new UnavailableScanner()));

        var message = EmailToLeadHarness.BuildMessage(messageId);
        var (assemblyId, schedule) = await CaptureAndScheduleAsync(services, bu, message);

        Assert.Equal(3, schedule.Held);
        Assert.Equal(EmailInquiryComponentStatus.FailedRecoverable,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));

        // The CODE, not just the status: EmailInquiryComponentClosure keys the
        // infrastructure-versus-content decision on this exact string, and the generic
        // "scheduling_failed" constant it used to record is a content fault.
        var code = await ComponentReasonAsync(assemblyId, "gaskets.csv");
        Assert.Equal("security_scanner_unavailable", code);
        Assert.True(EmailInquiryComponentClosure.IsInfrastructureFailure(code));

        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, await StatusAsync(assemblyId));
    }

    // =====================================================================================
    // 4. A PART READ AND ROUTED AWAY FROM LEAD CREATION
    // =====================================================================================

    [Fact]
    public async Task A_supplier_document_settles_immediately_and_truthfully_instead_of_after_half_an_hour()
    {
        const long bu = 948_007;
        const string messageId = "wayout-0007@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var message = EmailToLeadHarness.BuildMessage(messageId, "Our quotation for your enquiry");
        var (assemblyId, schedule) = await CaptureAndScheduleAsync(
            services, bu, message,
            triage: new EmailTriageDecision(
                EmailTriageOutcome.CommercialNonInquiry,
                ["supplier_quote"],
                EmailTriageDocumentHints.SupplierQuote,
                ThreadContinuation: false));
        Assert.Equal(3, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, bu, assertNoFailures: true);

        // SETTLED BY THE WORKER, not by the thirty-minute sweep. Every part is terminal the
        // moment its job completes.
        Assert.Equal(0, await CountComponentsAsync(assemblyId, "\"Status\" = 'Extracting'"));
        Assert.Equal(3, await CountComponentsAsync(assemblyId, "\"Status\" = 'Ignored'"));
        Assert.Equal(ExtractionWorker.CommercialNonInquiryReason,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));

        // AND THE REASON IS TRUE. The old path let the sweep close these as
        // "stranded_extraction_result_missing" and told the operator "No part of this message
        // could be read" — of a message every part of which was read and deliberately routed
        // away. A false reason sends someone to chase a sender who did nothing wrong.
        Assert.NotEqual(EmailInquiryHoldReasons.StrandedResultMissing,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));

        var reason = await AssemblyReasonAsync(assemblyId);
        if (!string.IsNullOrWhiteSpace(reason))
        {
            Assert.DoesNotContain("could not be read", reason, StringComparison.OrdinalIgnoreCase);
            AssertNoInternalNames(reason);
        }

        // The sweep has nothing left to find, which is the machine-checkable form of "it settled
        // on its own".
        Assert.Equal(0, (await SweepAsync(services)).StrandedComponents.Examined);
        Assert.Equal(0, await CountLeadsAsync(bu));
    }

    /// <summary>
    /// TWO parts of one message held with no job, which is the ordinary case rather than the
    /// exotic one: a storage outage does not politely stop after a single attachment.
    ///
    /// <para>It is a distinct test because re-driving is per MESSAGE — one call schedules every
    /// held part — while the sweep iterates per PART. The second part is therefore examined from a
    /// candidate list read BEFORE its sibling's re-drive ran, so it looks held when it is already
    /// running. Closing it on that stale read would discard work in flight and drag a message
    /// that was about to become a Lead into review instead.</para>
    /// </summary>
    [Fact]
    public async Task Two_parts_held_by_one_outage_are_both_re_driven_and_the_message_still_becomes_a_Lead()
    {
        const long bu = 948_011;
        const string messageId = "wayout-0011@buyer.example";
        await SeedAsync(bu, messageId);

        // A REPLY, deliberately: its quoted tail is stripped before the body component's hash is
        // computed, so the re-drive has to reproduce that stripping from the stored original. A
        // message with no quoted text would pass even if the two derivations had drifted.
        var message = BuildReplyWithQuotedTail(messageId);
        long assemblyId;

        await using (var during = BuildGraph(s => s.AddScoped<IDocumentIngestion>(sp =>
                         new StorageOutageForOneFile(
                             ActivatorUtilities.CreateInstance<DocumentIngestionService>(sp),
                             "valves.csv", "gaskets.csv"))))
        {
            var (id, schedule) = await CaptureAndScheduleAsync(during, bu, message);
            assemblyId = id;
            Assert.Equal(1, schedule.Scheduled);
            Assert.Equal(2, schedule.Held);
        }

        Assert.Equal(2, await CountComponentsAsync(assemblyId,
            "\"Status\" = 'FailedRecoverable' AND \"ExtractionJobId\" IS NULL"));

        await using var after = BuildGraph();
        var sweep = await SweepAsync(after);

        // BOTH parts, from ONE re-drive. Neither is closed on a stale read of the other's cycle.
        Assert.Equal(2, sweep.StrandedComponents.Rescheduled);
        Assert.Equal(0, sweep.StrandedComponents.Skipped);
        Assert.Equal(0, await CountComponentsAsync(assemblyId, "\"Status\" = 'FailedRecoverable'"));

        await EmailToLeadHarness.DrainQueueAsync(after, bu, assertNoFailures: true);
        await SweepAsync(after);

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
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
    // 5. A MESSAGE ALREADY IN A PERSON'S TRAY, WITH A PART THAT NEVER REACHED THE QUEUE
    // =====================================================================================

    /// <summary>
    /// The shape the old "retry blocked files" control left behind: assembly NeedsReview,
    /// component FailedRecoverable, ExtractionJobId null.
    ///
    /// <para><b>Every door was shut on it.</b> The security sweep no longer sees it (its hold is
    /// not AwaitingSecurityScan or Rejected); automatic scheduling recovery excludes NeedsReview;
    /// governed dead-letter recovery needs a job that does not exist; and the governed triage
    /// reopen covers NoInquiry only. Worse, the hold text told the operator to reprocess the
    /// message, and the reprocess command throws on exactly this shape — the one instruction the
    /// product gave was guaranteed to fail.</para>
    ///
    /// <para><b>Written as durable state on purpose.</b> No code path in this module can produce
    /// it: a held part outranks an unread one, so a message with a hold never reaches NeedsReview
    /// on its own. It got there because something else detached an already-decided message's
    /// component. The sweep's job is to handle the state it is handed, and this is the state.</para>
    /// </summary>
    [Fact]
    public async Task A_message_already_in_review_with_a_part_that_never_reached_the_queue_is_recovered()
    {
        const long bu = 948_012;
        const string messageId = "wayout-0012@buyer.example";
        await SeedAsync(bu, messageId);

        await using var services = BuildGraph();
        var message = EmailToLeadHarness.BuildMessage(messageId);
        var (assemblyId, schedule) = await CaptureAndScheduleAsync(services, bu, message);
        Assert.Equal(3, schedule.Scheduled);

        // THE DAMAGE. The component is detached from the job it owned and the message is already
        // sitting in a human's tray, which is what closed every recovery door at once.
        await ExecuteAsync($"""
            UPDATE public."EmailInquiryComponents"
            SET "Status" = 'FailedRecoverable', "ExtractionJobId" = NULL,
                "ReasonCode" = 'assembly_ownership_unresolved',
                "ReasonDetail" = 'A part of this message was processed without being linked back to the message.'
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv';
            UPDATE public."EmailInquiryAssemblies"
            SET "Status" = 'NeedsReview',
                "StatusReason" = 'assembly_ownership_unresolved: a part was not linked back.'
            WHERE "Id" = {assemblyId};
            """);

        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));
        Assert.Null(await ComponentJobIdAsync(assemblyId, "gaskets.csv"));

        // THE RECOVERY. Governed rather than automatic: the message leaves a person's tray only
        // by a recorded act, and the sweep names itself as the actor rather than having none.
        var sweep = await SweepAsync(services);

        Assert.Equal(0, sweep.StrandedComponents.Failed);
        Assert.Equal(1, sweep.StrandedComponents.Rescheduled);

        Assert.Equal(EmailInquiryComponentStatus.Extracting,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.NotNull(await ComponentJobIdAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, await StatusAsync(assemblyId));

        // THE AUDIT. Who reopened it and why, on the record the operator reads — and on the PART,
        // because the assembly's status reason is recomputed by the barrier on every evaluation
        // and cannot hold anything.
        Assert.Equal(EmailInquiryAssemblyCoordinator.ReopenedReasonCode,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));
        var audit = await ComponentDetailAsync(assemblyId, "gaskets.csv");
        Assert.False(string.IsNullOrWhiteSpace(audit));
        Assert.Contains(EmailInquirySchedulingGrant.RecoverySweepActor, audit!, StringComparison.Ordinal);
        AssertNoInternalNames(audit!);

        // AND IT FINISHES. A reopened part is worth nothing if nothing runs it.
        await EmailToLeadHarness.DrainQueueAsync(services, bu, assertNoFailures: true);
        await SweepAsync(services);

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
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

    /// <summary>
    /// The same shape past its window. A reopen that can never succeed must not keep a message
    /// technically recoverable and permanently undecided — it ends in a refusal a person can read.
    /// </summary>
    [Fact]
    public async Task A_message_in_review_whose_part_cannot_be_reopened_reaches_an_explicit_refusal()
    {
        const long bu = 948_013;
        const string messageId = "wayout-0013@buyer.example";
        await SeedAsync(bu, messageId);

        await using var seeding = BuildGraph();
        var message = EmailToLeadHarness.BuildMessage(messageId);
        var (assemblyId, _) = await CaptureAndScheduleAsync(seeding, bu, message);

        await ExecuteAsync($"""
            UPDATE public."EmailInquiryComponents"
            SET "Status" = 'FailedRecoverable', "ExtractionJobId" = NULL,
                "ReasonCode" = 'assembly_ownership_unresolved'
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv';
            UPDATE public."EmailInquiryAssemblies" SET "Status" = 'NeedsReview' WHERE "Id" = {assemblyId};
            """);

        await using var expired = BuildGraph(s => s.AddSingleton(RecoveryOptions(resumeWindowMinutes: 0)));
        var sweep = await SweepAsync(expired);

        Assert.Equal(0, sweep.StrandedComponents.Failed);
        Assert.Equal(1, sweep.StrandedComponents.Skipped);
        Assert.Equal(EmailInquiryComponentStatus.Skipped,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(EmailInquiryHoldReasons.SchedulingNotRecovered,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));

        // It stays where a person can see it, and the refused part is never revisited. The two
        // siblings ARE examined again and left alone — they hold Pending jobs with every attempt
        // intact, which is the sweep declining to tidy away live work.
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));
        var second = await SweepAsync(expired);
        Assert.Equal(0, second.StrandedComponents.Resolved);
        Assert.Equal(0, second.StrandedComponents.Rescheduled);
        Assert.Equal(2, second.StrandedComponents.LeftInFlight);
    }

    /// <summary>
    /// A message the SCHEDULER IS STILL WORKING ON must not be claimed by the sweep.
    ///
    /// <para><b>The window is real and it is invisible from the rows.</b> Capture commits the
    /// assembly and every component in one transaction, then binds an extraction job to each
    /// component in turn. In between, a perfectly healthy part looks exactly like a part that will
    /// never be scheduled: Pending, no job. Nothing in the data distinguishes them — only time
    /// does.</para>
    ///
    /// <para><b>Why the existing threshold could not serve.</b> The stranded-component threshold
    /// is legitimately set to zero in tests, on the documented reasoning that age decides only
    /// which rows are LOOKED at while the durable state of the job decides what happens to them.
    /// That reasoning holds for a component that HAS a job and fails for one that does not — and
    /// it became load bearing when the barrier learned to move a Captured message to NeedsReview.
    /// Before that, the sweep's verdict on a mid-capture message was an illegal transition and was
    /// thrown away, which hid this race rather than preventing it.</para>
    /// </summary>
    [Fact]
    public async Task A_message_the_scheduler_is_still_binding_jobs_for_is_left_alone()
    {
        const long bu = 948_014;
        const string messageId = "wayout-0014@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        // CAPTURED BUT NOT SCHEDULED — the exact instant between the two halves of intake.
        var assemblyId = await CaptureOnlyAsync(services, bu, EmailToLeadHarness.BuildMessage(messageId));

        Assert.Equal(EmailInquiryAssemblyStatus.Captured, await StatusAsync(assemblyId));
        Assert.Equal(3, await CountComponentsAsync(assemblyId,
            "\"Status\" = 'Pending' AND \"ExtractionJobId\" IS NULL"));

        // The sweep does not even look at it, with every other threshold at zero.
        var sweep = await SweepAsync(services);
        Assert.Equal(0, sweep.StrandedComponents.Examined);
        Assert.Equal(EmailInquiryAssemblyStatus.Captured, await StatusAsync(assemblyId));
        Assert.Equal(3, await CountComponentsAsync(assemblyId, "\"Status\" = 'Pending'"));

        // THE POSITIVE CONTROL. The same rows, past the grace, ARE claimed — so the zero above is
        // the guard doing its job and not the sweep being broken. A message that really did die
        // between capture and scheduling still reaches a person.
        await ExecuteAsync($"""
            UPDATE public."EmailInquiryAssemblies"
            SET "UpdatedAtUtc" = now() - interval '10 minutes' WHERE "Id" = {assemblyId};
            """);

        var second = await SweepAsync(services);
        Assert.Equal(3, second.StrandedComponents.Examined);
        Assert.Equal(3, second.StrandedComponents.Skipped);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));
        Assert.Equal(EmailInquiryHoldReasons.StrandedWithoutJob,
            await ComponentReasonAsync(assemblyId, "gaskets.csv"));
    }

    // =====================================================================================
    // 6. AN UNEXPECTED FAILURE CLOSES ITS PART LIKE EVERY OTHER FAILURE DOES
    // =====================================================================================

    /// <summary>
    /// The worker's catch-all was the ONE failure path that did not close its component. Its
    /// three siblings — extraction failure, parse failure, evidence integrity — all do, so an
    /// unexpected exception was the only way to dead-letter a job and leave the barrier waiting
    /// on a part no worker would ever pick up again.
    /// </summary>
    [Fact]
    public async Task An_unexpected_extraction_failure_closes_its_part_instead_of_leaving_the_message_waiting()
    {
        const long bu = 948_010;
        const string messageId = "wayout-0010@buyer.example";
        await SeedAsync(bu, messageId);

        long assemblyId;
        await using (var healthy = BuildGraph())
        {
            var message = EmailToLeadHarness.BuildBodyOnlyMessage(messageId);
            var (id, schedule) = await CaptureAndScheduleAsync(healthy, bu, message);
            assemblyId = id;
            Assert.Equal(1, schedule.Scheduled);
        }

        // One attempt, so the first unexpected failure is the last one. Retrying five times on
        // exponential backoff would prove the same thing an hour later.
        await ExecuteAsync($"""
            UPDATE public."ExtractionJobs" SET "MaxAttempts" = 1 WHERE "BusinessUnitId" = {bu};
            """);

        // Something nothing anticipated, at the one seam every job passes through. Not a parse
        // failure, not an evidence fault — those have their own handlers, which already close the
        // component. This is the path that had none.
        await using var broken = BuildGraph(s =>
            s.AddScoped<IExtractionDocumentReader>(_ => new UnexpectedlyThrowingReader()));

        await EmailToLeadHarness.DrainQueueAsync(
            broken, bu, assertNoFailures: false, waitForAssemblySettlement: false);

        // CLOSED BY THE WORKER, at the moment the job gave up — not thirty minutes later by the
        // sweep, and not never.
        Assert.Equal(0, await CountComponentsAsync(assemblyId, "\"Status\" = 'Extracting'"));
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));

        var reason = await AssemblyReasonAsync(assemblyId);
        Assert.False(string.IsNullOrWhiteSpace(reason));
        AssertNoInternalNames(reason!);

        // And the sweep, which is still the backstop for the dead letters the queue's own claim
        // statement writes with no worker in the loop, has nothing left to do here.
        Assert.Equal(0, (await SweepAsync(broken)).StrandedComponents.Examined);
    }

    // =====================================================================================
    // 7. THE LEDGER STOPS CLAIMING PROGRESS OVER WORK THAT STOPPED
    // =====================================================================================

    [Fact]
    public async Task An_ingest_reading_Queued_over_a_decided_message_is_corrected_to_what_actually_happened()
    {
        const long bu = 948_008;
        const string messageId = "wayout-0008@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var message = EmailToLeadHarness.BuildMessage(messageId);
        var (assemblyId, _) = await CaptureAndScheduleAsync(services, bu, message);

        // The live shape: every job dead at MaxAttempts (what the queue's own claim statement
        // writes, with no worker in the loop), the assembly already decided by the component
        // sweep, and the ledger row still reporting progress.
        await ExecuteAsync($"""
            UPDATE public."ExtractionJobs" SET "Status" = 'DeadLetter', "Attempts" = "MaxAttempts",
                "LastError" = 'The file passed inspection but no reader in this deployment can parse it.'
            WHERE "BusinessUnitId" = {bu};
            UPDATE public."EmailIngests" SET "ParseStatus" = 'Queued'
            WHERE "MessageID" = '{messageId}';
            """);

        await SweepAsync(services);

        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(assemblyId));
        var parseStatus = await TextAsync($"""
            SELECT "ParseStatus" FROM public."EmailIngests" WHERE "MessageID" = '{messageId}';
            """);
        Assert.False(EmailInquiryLedgerReconciliation.ClaimsInFlight(parseStatus),
            $"The ledger still claims the message is in flight ('{parseStatus}') over an "
            + "assembly that has already been decided.");
        Assert.Equal(EmailInquiryLedgerReconciliation.NeedsReview, parseStatus);
    }

    [Fact]
    public async Task An_ingest_with_live_work_is_never_relabelled_as_failed()
    {
        // The control that stops the correction above becoming its own defect. Reporting a
        // healthy message as failed is the same lie pointing the other way.
        const long bu = 948_009;
        const string messageId = "wayout-0009@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var message = EmailToLeadHarness.BuildMessage(messageId);
        await CaptureAndScheduleAsync(services, bu, message);

        await ExecuteAsync($"""
            UPDATE public."EmailIngests" SET "ParseStatus" = 'Queued'
            WHERE "MessageID" = '{messageId}';
            """);

        var sweep = await SweepAsync(services);

        Assert.Equal(1, sweep.Ledger.Examined);
        Assert.Equal(0, sweep.Ledger.Corrected);
        Assert.Equal("Queued", await TextAsync($"""
            SELECT "ParseStatus" FROM public."EmailIngests" WHERE "MessageID" = '{messageId}';
            """));
    }

    // =====================================================================================
    // Fakes — only the world OUTSIDE the process
    // =====================================================================================

    /// <summary>Durable storage is unreachable for the named parts of the message.</summary>
    private sealed class StorageOutageForOneFile(IDocumentIngestion inner, params string[] fileNames)
        : IDocumentIngestion
    {
        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string name, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null, CancellationToken ct = default)
            => fileNames.Contains(name, StringComparer.Ordinal)
                ? throw new ERP_RFQ_Automation.Infrastructure.Storage.EvidenceStorageUnavailableException(
                    isConfigurationFault: false)
                : inner.IngestAsync(bytes, name, businessUnitId, sourceType, batchId, priority,
                    metadata, emailInquiryComponentId, ct);
    }

    /// <summary>Reports every file as infected — the verdict, not the file, is what is under test.</summary>
    private sealed class InfectedScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken ct = default)
            => Task.FromResult(MalwareScanResult.Infected("test-scanner", "Eicar-Test-Signature"));
    }

    /// <summary>
    /// Fails in a way nothing anticipated, at the seam every job passes through. Deliberately NOT
    /// a DocumentParsingException or an EvidenceIntegrityException — those have their own
    /// handlers, and the whole point is the path that had none.
    /// </summary>
    private sealed class UnexpectedlyThrowingReader : IExtractionDocumentReader
    {
        public Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default)
            => throw new InvalidOperationException(
                "Simulated failure nobody wrote a handler for.");
    }

    /// <summary>The scanner is down. Retryable, and a different outcome from a refusal.</summary>
    private sealed class UnavailableScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken ct = default)
            => Task.FromResult(MalwareScanResult.Unavailable(
                "test-scanner", "The malware scanner could not be reached."));
    }

    // =====================================================================================
    // Harness
    // =====================================================================================

    /// <summary>
    /// The shared graph, with this class's recovery knobs installed LAST so they win.
    ///
    /// <para>Every threshold is zero: the age of a row decides only which rows are LOOKED at, and
    /// what happens to one is decided by durable state — its job, its assembly, its manifest. A
    /// test that waited out a clock would be proving the clock.</para>
    /// </summary>
    private ServiceProvider BuildGraph(Action<IServiceCollection>? configure = null) =>
        EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            services =>
            {
                services.AddSingleton(RecoveryOptions(resumeWindowMinutes: 240));
                configure?.Invoke(services);
            });

    private static EmailInquiryAssemblyRecoveryOptions RecoveryOptions(int resumeWindowMinutes) =>
        new()
        {
            Interval = TimeSpan.FromSeconds(30),
            BatchSizePerTenant = 50,
            MinimumAge = TimeSpan.Zero,
            StrandedComponentSweepMinutes = 0,
            SchedulingResumeWindowMinutes = resumeWindowMinutes,
            LedgerReconciliationMinutes = 0
        };

    private static async Task<(long AssemblyId, EmailInquiryIntakeResult Schedule)> CaptureAndScheduleAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message,
        EmailTriageDecision? triage = null)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == businessUnitId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);

        var result = await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
            .CaptureAndScheduleAsync(
                message, ingest, configuration, ProductionFreshBody(message),
                triage ?? new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
                "buyer@customer.example");

        Assert.NotNull(result.AssemblyId);
        return (result.AssemblyId!.Value, result);
    }

    /// <summary>Re-runs SCHEDULING only, the way the next poll of an already-captured message does.</summary>
    private static async Task<EmailScheduleResult> ScheduleOnlyAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message, long assemblyId)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var assembly = await context.EmailInquiryAssemblies.SingleAsync(a => a.Id == assemblyId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.Id == assembly.EmailIngestId);
        var components = await context.EmailInquiryComponents
            .Where(c => c.AssemblyId == assemblyId).OrderBy(c => c.Ordinal).ToListAsync();

        var plan = await EmailInquiryManifestPlanner.PlanAsync(
            message, assembly.MessageKey, ProductionFreshBody(message));

        return await EmailIngestEnqueuer.ScheduleAsync(
            assembly, components, plan, ingest, "buyer@customer.example",
            scope.ServiceProvider.GetRequiredService<IDocumentIngestion>(),
            new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
            scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>(),
            scope.ServiceProvider.GetRequiredService<ILogger<ExtractionWorker>>());
    }

    /// <summary>The acknowledgement verdict the poller acts on for a manifest-refused message.</summary>
    private static async Task<EmailInquiryIntakeResult> IntakeResultForRefusedManifestAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == businessUnitId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);

        return await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
            .CaptureAndScheduleAsync(
                message, ingest, configuration, ProductionFreshBody(message),
                new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
                "buyer@customer.example");
    }

    /// <summary>
    /// The fresh body EXACTLY as the mailbox poller derives it — quoted text stripped by
    /// EmailBodyNormalizer, HTML flattened when there is no plain part.
    ///
    /// <para>Not <c>message.TextBody</c>, which is what an earlier version of this harness used.
    /// The body component's content hash is computed FROM this text, and the resume path has to
    /// reproduce it byte for byte from the stored original or the manifest verifier refuses. A
    /// fixture that captured under one derivation and resumed under another would prove the
    /// re-drive works on messages with no quoted tail and nothing else — which is not the
    /// population that gets held.</para>
    /// </summary>
    private static string ProductionFreshBody(MimeMessage message)
    {
        var plain = message.GetTextBody(MimeKit.Text.TextFormat.Plain);
        var body = !string.IsNullOrWhiteSpace(plain)
            ? plain
            : message.GetTextBody(MimeKit.Text.TextFormat.Html) is { } html
                ? System.Text.RegularExpressions.Regex.Replace(html, "<.*?>", " ")
                    .Replace("&nbsp;", " ").Replace("\r\n", "\n")
                : string.Empty;
        return EmailBodyNormalizer.Normalize(body).Fresh;
    }

    /// <summary>A reply: fresh text on top, an ordinary quoted tail underneath.</summary>
    private static MimeMessage BuildReplyWithQuotedTail(string messageId)
    {
        const string valves =
            "Part Number,Description,Quantity,Unit\n"
            + "VLV-1001,Ball valve DN50 PN16 stainless,12,EA\n"
            + "VLV-1002,Gate valve DN80 PN16 carbon steel,4,EA\n";
        const string gaskets =
            "Part Number,Description,Quantity,Unit\n"
            + "GSK-3007,Spiral wound gasket DN50 CL150,60,EA\n"
            + "GSK-3008,Spiral wound gasket DN80 CL150,25,EA\n"
            + "GSK-3009,Ring joint gasket R-24 soft iron,8,EA\n";

        var mixed = new Multipart("mixed")
        {
            new TextPart("plain")
            {
                Text = EmailToLeadHarness.BodyText
                    + "\n\nOn 12 August 2026 at 08:00, Nexora <rfq@nexora.example> wrote:\n"
                    + "> Thank you for your enquiry. Please confirm the delivery address.\n"
                    + "> Regards,\n> Nexora\n"
            },
            EmailToLeadHarness.Attachment("valves.csv", "text/csv",
                System.Text.Encoding.UTF8.GetBytes(valves)),
            EmailToLeadHarness.Attachment("gaskets.csv", "text/csv",
                System.Text.Encoding.UTF8.GetBytes(gaskets))
        };

        var message = new MimeMessage { Subject = "Re: RFQ 88-2410 Jubail expansion", Body = mixed };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Nexora", "rfq@nexora.example"));
        message.MessageId = messageId;
        message.Date = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        return message;
    }

    /// <summary>
    /// Durable capture WITHOUT scheduling — the state that exists for as long as the scheduling
    /// half of intake is still running. Nothing is faked: this is the real capture service, and
    /// the rows it writes are the rows production writes.
    /// </summary>
    private static async Task<long> CaptureOnlyAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == businessUnitId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);

        var capture = await scope.ServiceProvider.GetRequiredService<IEmailInquiryCaptureService>()
            .CaptureAsync(message, ingest, configuration, ProductionFreshBody(message));

        Assert.NotNull(capture.Assembly);
        return capture.Assembly!.Id;
    }

    private static async Task<EmailInquiryRecoverySweepResult> SweepAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IEmailInquiryAssemblyRecoveryService>().SweepOnceAsync();
    }

    private static void AssertNoInternalNames(string text)
    {
        foreach (var name in Enum.GetNames<EmailInquiryAssemblyStatus>()
                     .Concat(Enum.GetNames<EmailInquiryComponentStatus>()))
        {
            Assert.DoesNotContain(name, text, StringComparison.Ordinal);
        }
    }

    private async Task SeedAsync(long businessUnitId, string messageId)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
    }

    private async Task<EmailInquiryAssemblyStatus> StatusAsync(long assemblyId)
    {
        var values = await QueryAsync($"""
            SELECT "Status" FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
            """);
        return Enum.Parse<EmailInquiryAssemblyStatus>(Assert.Single(values)!);
    }

    private Task<string?> AssemblyReasonAsync(long assemblyId) => TextAsync($"""
        SELECT "StatusReason" FROM public."EmailInquiryAssemblies" WHERE "Id" = {assemblyId};
        """);

    private async Task<EmailInquiryComponentStatus> ComponentStatusAsync(long assemblyId, string fileName)
    {
        var values = await QueryAsync($"""
            SELECT "Status" FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName.Replace("'", "''")}';
            """);
        return Enum.Parse<EmailInquiryComponentStatus>(Assert.Single(values)!);
    }

    private Task<string?> ComponentReasonAsync(long assemblyId, string fileName) => TextAsync($"""
        SELECT "ReasonCode" FROM public."EmailInquiryComponents"
        WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName.Replace("'", "''")}';
        """);

    private Task<string?> ComponentDetailAsync(long assemblyId, string fileName) => TextAsync($"""
        SELECT "ReasonDetail" FROM public."EmailInquiryComponents"
        WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName.Replace("'", "''")}';
        """);

    private Task<string?> ComponentJobIdAsync(long assemblyId, string fileName) => TextAsync($"""
        SELECT "ExtractionJobId" FROM public."EmailInquiryComponents"
        WHERE "AssemblyId" = {assemblyId} AND "FileName" = '{fileName.Replace("'", "''")}';
        """);

    private Task<int> CountComponentsAsync(long assemblyId, string predicate) => ScalarAsync($"""
        SELECT count(*) FROM public."EmailInquiryComponents"
        WHERE "AssemblyId" = {assemblyId} AND {predicate};
        """);

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
