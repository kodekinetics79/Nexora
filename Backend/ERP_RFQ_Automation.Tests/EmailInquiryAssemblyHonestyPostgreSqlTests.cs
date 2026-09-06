using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// WHAT THE SYSTEM SAYS ABOUT A MESSAGE MUST BE WHAT BECAME OF IT.
///
/// <para>Two dispositions in this module used to claim more than they had done, and both survived
/// because every test around them stopped one assertion short of the thing an operator actually
/// reads.</para>
///
/// <para><b>The sweep's hold.</b> When the component sweep closes a part whose job dead-lettered
/// on an infrastructure fault, it counts the message as recovered — and the message's ledger row
/// went on reading "Queued" for as long as the row existed, because no sweep queries a held part
/// that kept its job id and no worker will ever claim its queue row again. The Inbound Mail
/// "Stopped" tab, which exists to answer exactly this question, matched on a "Failed" prefix and
/// counted zero. The existing sweep test asserts the component and the assembly and stops there.
/// </para>
///
/// <para><b>The assembler's merged verdict.</b> The outcome handed to the persister carried a
/// hard-coded <c>Ok</c>, so a component extractor's NeedsReview was discarded: the Lead lost the
/// marker the review queue derives its reason from, and became eligible for machine
/// auto-verification. Auto-verify is live in production because the container always supplies an
/// <see cref="IConfiguration"/>; the shared harness supplies none, so these tests register one —
/// otherwise the graph cannot express the half of the defect that costs money.</para>
///
/// <para>The real pipeline runs throughout: the real queue, worker, persister, coordinator,
/// assembler and sweep, and the triage screen's own query. What is substituted is the world
/// outside the process — the model, the malware verdict, and the availability of storage.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailInquiryAssemblyHonestyPostgreSqlTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    // EXCLUSIVE to this class: DisposeAsync deletes the whole range, so a range shared with
    // another class in this collection makes each of them delete the other's rows mid-run.
    private const long FirstBu = 949_000;
    private const long LastBu = 949_099;

    /// <summary>
    /// A dead letter whose prose carries the marker an evidence outage stamps — the shape
    /// <c>ExtractionQueue</c>'s exhausted-lease CTE preserves through
    /// <c>LastError = COALESCE(LastError, ...)</c> with no worker in the loop, and the one
    /// <c>EmailInquiryComponentClosure</c> maps to an INFRASTRUCTURE fault, i.e. a hold.
    /// </summary>
    private const string InfrastructureError =
        "Evidence integrity failure: [EVIDENCE_OBJECT_MISSING] The stored evidence object is no "
        + "longer present in storage, so its integrity could not be verified.";

    /// <summary>
    /// The sentence <c>ConversationalExtractionService</c> emits, verbatim, for a reply inside an
    /// existing thread — one of four reasons it returns NeedsReview WITH a non-empty item list,
    /// which is what makes this the shape that reaches the assembler at all.
    /// </summary>
    private const string ThreadContinuationReviewReason =
        "This message is a reply/forward in an existing thread; confirm it is a new request "
        + "and not a restatement of one already in the system.";

    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-honesty-" + Guid.NewGuid().ToString("N")[..12]);

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
    // 1. A HOLD NOTHING WILL LOOK AT AGAIN IS REPORTED AS STOPPED
    // =====================================================================================

    /// <summary>
    /// The exact population the sweep's Held disposition creates, driven end to end and then asked
    /// the question the operator's screen asks.
    ///
    /// <para>A three-part RFQ is captured and scheduled, so the ledger reads "Queued" (that is
    /// <c>EmailService.RouteIngestAsync</c>'s write once anything is enqueued). One attachment's
    /// job dead-letters on an evidence-integrity fault with no worker in the loop. The sweep holds
    /// the part, the barrier holds the message — and the part keeps its job id, so the sweep's
    /// component query will never see it again and the queue will never run it again. The only
    /// mover left is a person, and a person finds this message through the "Stopped" tab.</para>
    /// </summary>
    [Fact]
    public async Task A_job_bound_hold_stops_reading_as_in_flight_so_the_Stopped_tab_can_find_it()
    {
        const long bu = 949_001;
        const string messageId = "honesty-0001@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph();

        var assemblyId = await CaptureAsync(services, bu, messageId);
        await MarkLedgerQueuedAsync(messageId);
        await KillJobAsync(assemblyId, "gaskets.csv", ExtractionStatus.DeadLetter, InfrastructureError);
        await EmailToLeadHarness.DrainQueueAsync(services, bu, assertNoFailures: false,
            waitForAssemblySettlement: false);

        // THE STRANDED STATE, before anything is swept: the ledger claims progress, and it is
        // right to, because the sweep has not run yet.
        Assert.True(EmailInquiryLedgerReconciliation.ClaimsInFlight(await ParseStatusAsync(messageId)));

        var sweep = await SweepAsync(services);

        // The disposition under test, and the population: HELD, with the job id still on the part,
        // which is what puts it outside every sweep's reach from here on.
        Assert.Equal(1, sweep.StrandedComponents.Held);
        Assert.Equal(EmailInquiryComponentStatus.FailedRecoverable,
            await ComponentStatusAsync(assemblyId, "gaskets.csv"));
        Assert.Equal(1, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv'
              AND "Status" = 'FailedRecoverable' AND "ExtractionJobId" IS NOT NULL;
            """));
        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, await StatusAsync(assemblyId));

        // AND THE LEDGER SAYS SO. This is the assertion the existing sweep test stops one line
        // short of, and its absence is why the sweep could report a successful recovery over a
        // message the screen still showed as in flight.
        Assert.Equal(1, sweep.Ledger.Corrected);
        var parseStatus = await ParseStatusAsync(messageId);
        Assert.Equal(ExtractionWorker.DeadLetterParseStatus, parseStatus);
        Assert.False(EmailInquiryLedgerReconciliation.ClaimsInFlight(parseStatus));

        // THE SCREEN, not a re-implementation of it. EmailTriageService owns the "Stopped"
        // predicate, and its job-bound-hold branch requires this prefix — a row reading "Queued"
        // fails it silently and the customer's RFQ sits behind "0 stopped".
        Assert.Equal(1, await StoppedCountAsync(services, bu));
    }

    /// <summary>
    /// The control that stops the correction above from becoming its own defect: a hold the sweep
    /// WILL look at again must keep claiming progress, because it genuinely has some.
    ///
    /// <para>Storage is unreachable while one part is being scheduled, so that part is held with
    /// no extraction job — the shape <c>EmailIngestEnqueuer</c> writes on an evidence-storage
    /// outage. The sweep claims it (FailedRecoverable with a null job id is the one state it
    /// claims conditionally), re-drives scheduling from the stored original, and will keep doing
    /// so until the resume window closes. Reporting that message as stopped would be the same lie
    /// as the one above, pointing the other way.</para>
    /// </summary>
    [Fact]
    public async Task A_hold_the_sweep_will_still_re_drive_keeps_claiming_progress()
    {
        const long bu = 949_002;
        const string messageId = "honesty-0002@buyer.example";
        await SeedAsync(bu, messageId);

        await using var services = BuildGraph(s => s.AddScoped<IDocumentIngestion>(sp =>
            new StorageOutageForOneFile(
                ActivatorUtilities.CreateInstance<DocumentIngestionService>(sp), "gaskets.csv")));

        var assemblyId = await CaptureAsync(services, bu, messageId, expectedScheduled: 2);
        await MarkLedgerQueuedAsync(messageId);
        await EmailToLeadHarness.DrainQueueAsync(services, bu, assertNoFailures: false,
            waitForAssemblySettlement: false);

        // The precondition: held, and holding NO job — so the sweep still owns it.
        Assert.Equal(1, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "FileName" = 'gaskets.csv'
              AND "Status" = 'FailedRecoverable' AND "ExtractionJobId" IS NULL;
            """));
        Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, await StatusAsync(assemblyId));

        var sweep = await SweepAsync(services);

        Assert.Equal(0, sweep.Ledger.Corrected);
        Assert.Equal(EmailInquiryLedgerReconciliation.InFlightQueued, await ParseStatusAsync(messageId));
    }

    // =====================================================================================
    // 2. A COMPONENT'S NEEDS-REVIEW VERDICT SURVIVES THE MERGE
    // =====================================================================================

    /// <summary>
    /// The buyer replies inside an existing thread with a cleanly anchored line. The
    /// extractor's honest answer is "confirm this is a new request and not a restatement of one
    /// already in the system" — and the assembler used to rewrite that to <c>Ok</c> on its way to
    /// the persister, which reads the field for exactly two decisions.
    ///
    /// <para>So the Lead lost its <c>[NEEDS REVIEW]</c> marker, which is the ONLY thing
    /// <c>LeadRepository.GetNeedsReviewLeadsAsync</c> recovers a reason from, and it cleared the
    /// auto-verify gate: stamped verified by <c>system:auto-verified-high-confidence</c>, review
    /// no longer required, and the message reported as Success. Nobody was ever asked the
    /// question, and the tenant quotes the same request twice.</para>
    /// </summary>
    [Fact]
    public async Task A_component_verdict_of_needs_review_reaches_the_lead_and_stops_auto_verification()
    {
        const long bu = 949_003;
        const string messageId = "honesty-0003@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph(WithAutoVerificationAndBodyVerdict(
            ThreadContinuationReviewReason));

        var assemblyId = await CaptureAndAssembleBodyOnlyAsync(services, bu, messageId);

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));

        // THE MARKER. Without it the review queue renders the row with a blank reason, because
        // ExtractReviewReason returns null unless the remarks begin with exactly this.
        var remarks = await TextAsync($"""
            SELECT "HeaderRemarks" FROM public."Leads" WHERE "BusinessUnitID" = {bu};
            """);
        Assert.StartsWith("[NEEDS REVIEW] ", remarks ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains(ThreadContinuationReviewReason, remarks ?? string.Empty, StringComparison.Ordinal);

        // AND NO MACHINE ANSWERED THE QUESTION. All three of these are written together by the
        // auto-verify block, so all three are asserted.
        Assert.Equal(1, await ScalarAsync($"""
            SELECT count(*) FROM public."Leads"
            WHERE "BusinessUnitID" = {bu} AND "RequiresCommercialReview" = true
              AND "CommercialFactsVerified" = false AND "ReviewApprovedBy" IS NULL;
            """));

        // And the message says the same thing the Lead does. "Success" is written only when every
        // lead of the message needed nobody.
        Assert.NotEqual("Success", await ParseStatusAsync(messageId));
    }

    /// <summary>
    /// THE POSITIVE CONTROL, and it is what stops the test above passing for the wrong reason. The
    /// same message, the same anchored line, the same confidence — with the extractor reporting no
    /// review reason — is still auto-verified.
    ///
    /// <para>Without this, "the lead was not auto-verified" would be satisfied by a fixture that
    /// could never have been auto-verified in the first place, and the change under test would be
    /// indistinguishable from holding every assembled lead forever.</para>
    /// </summary>
    [Fact]
    public async Task An_assembled_message_with_no_review_verdict_is_still_verified_without_a_person()
    {
        const long bu = 949_004;
        const string messageId = "honesty-0004@buyer.example";
        await SeedAsync(bu, messageId);
        await using var services = BuildGraph(WithAutoVerificationAndBodyVerdict(reviewReason: null));

        var assemblyId = await CaptureAndAssembleBodyOnlyAsync(services, bu, messageId);

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(assemblyId));
        Assert.Equal(1, await ScalarAsync($"""
            SELECT count(*) FROM public."Leads"
            WHERE "BusinessUnitID" = {bu} AND "RequiresCommercialReview" = false
              AND "CommercialFactsVerified" = true
              AND "ReviewApprovedBy" = '{LeadPersister.AutoVerifyActor}';
            """));

        var remarks = await TextAsync($"""
            SELECT "HeaderRemarks" FROM public."Leads" WHERE "BusinessUnitID" = {bu};
            """);
        Assert.DoesNotContain("[NEEDS REVIEW]", remarks ?? string.Empty, StringComparison.Ordinal);
    }

    // =====================================================================================
    // Fakes — only the world OUTSIDE the process
    // =====================================================================================

    /// <summary>
    /// The body extractor's verdict, in the shape <c>ConversationalExtractionService</c> produces
    /// it: one line anchored to a verified verbatim span of the submitted text, a confidence the
    /// auto-verify threshold clears, and a review reason set exactly when the status is
    /// NeedsReview — the two are decided together there, which is what makes the assembler's
    /// reconstruction faithful.
    /// </summary>
    private sealed class BodyVerdictExtractor(string? reviewReason) : IConversationalExtractionService
    {
        // The quantity/UOM pair and the part number must each occur exactly ONCE in the span, or
        // CriticalSourceEvidence refuses to derive typed fields from it and no lead of this shape
        // could ever be auto-verified — which would make the control below vacuous. A part number
        // whose digits follow a hyphen reads as a second quantity to that rule.
        internal const string Span = "Please quote 7 EA BODYONLY700 pressure transmitters";

        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
        {
            var item = Ext.Item(0.95, "Pressure transmitter", quantity: 7) with
            {
                ManufacturerPartNumber = "BODYONLY700",
                ManufacturerPartNumberConfidence = 0.95,
                UnitOfMeasure = "EA",
                UnitOfMeasureConfidence = 0.95,
                SourceSpan = Span,
                SourceSpanVerified = true
            };

            return Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = reviewReason is null
                    ? ExtractionOutcomeStatus.Ok
                    : ExtractionOutcomeStatus.NeedsReview,
                Result = Ext.Result([item], 0.95),
                ExpectedItemCount = 1,
                ExtractedItemCount = 1,
                ReviewReason = reviewReason,
                ProcessingPath = ExtractionProcessingPath.LocalModel
            });
        }
    }

    /// <summary>Durable evidence storage is unreachable for the named part of the message.</summary>
    private sealed class StorageOutageForOneFile(IDocumentIngestion inner, params string[] fileNames)
        : IDocumentIngestion
    {
        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string name, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null, CancellationToken ct = default)
            => fileNames.Contains(name, StringComparer.Ordinal)
                ? throw new EvidenceStorageUnavailableException(isConfigurationFault: false)
                : inner.IngestAsync(bytes, name, businessUnitId, sourceType, batchId, priority,
                    metadata, emailInquiryComponentId, ct);
    }

    // =====================================================================================
    // Harness
    // =====================================================================================

    private ServiceProvider BuildGraph(Action<IServiceCollection>? configure = null) =>
        EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(), configure);

    /// <summary>
    /// Registers what PRODUCTION's container always has and this harness deliberately does not.
    ///
    /// <para><c>LeadPersister</c> switches auto-verification off entirely when it is constructed
    /// with a null <see cref="IConfiguration"/>, which is the shared harness's shape — so a test
    /// built on it alone cannot tell "the verdict stopped the machine" from "the machine was never
    /// running". <c>Program</c> registers the persister in a host, so configuration is always
    /// present and the threshold defaults to 0.85.</para>
    /// </summary>
    private static Action<IServiceCollection> WithAutoVerificationAndBodyVerdict(string? reviewReason)
        => services =>
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddScoped<IConversationalExtractionService>(
                _ => new BodyVerdictExtractor(reviewReason));
        };

    private async Task SeedAsync(long businessUnitId, string messageId)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
    }

    private static async Task<long> CaptureAsync(
        ServiceProvider services, long businessUnitId, string messageId, int expectedScheduled = 3)
    {
        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, EmailToLeadHarness.BuildMessage(messageId));
        Assert.Equal(expectedScheduled, schedule.Scheduled);
        return assemblyId;
    }

    /// <summary>Body-only, which is the majority shape of inbound RFQ mail and the only one the
    /// auto-verify gate can reach — it requires a single extraction result.</summary>
    private static async Task<long> CaptureAndAssembleBodyOnlyAsync(
        ServiceProvider services, long businessUnitId, string messageId)
    {
        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, EmailToLeadHarness.BuildBodyOnlyMessage(
                messageId, BodyVerdictExtractor.Span + ", delivery DDP Jubail."),
            expectedComponentCount: 1);
        Assert.Equal(1, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId,
            assertNoFailures: true, waitForAssemblySettlement: true);
        return assemblyId;
    }

    private static async Task<EmailInquiryRecoverySweepResult> SweepAsync(ServiceProvider services)
    {
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IEmailInquiryAssemblyRecoveryService>().SweepOnceAsync();
    }

    /// <summary>
    /// The Inbound Mail screen's own "Stopped" query, run through the service that owns it rather
    /// than restated in SQL here. A copy of the predicate would agree with itself.
    /// </summary>
    private static async Task<int> StoppedCountAsync(ServiceProvider services, long businessUnitId)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ERP_RFQ_Automation.MultiTenancy.ITenantScopeAccessor>()
            .Push(businessUnitId);
        var triage = ActivatorUtilities.CreateInstance<EmailTriageService>(scope.ServiceProvider);
        var page = await triage.ListAsync(
            businessUnitId, outcome: null, page: 1, pageSize: 50, state: EmailTriageStates.Stopped);
        return page.TotalCount;
    }

    /// <summary>
    /// What <c>EmailService.RouteIngestAsync</c> writes the moment anything is enqueued, and the
    /// value this whole lane is about. The shared seed leaves ParseStatus null, which is a shape
    /// production never has once a message has been scheduled.
    ///
    /// <para>The row is aged past the reconciliation window at the same time. Age decides only
    /// which rows are LOOKED at — what happens to one is decided by durable state — so this buys
    /// determinism without proving the clock.</para>
    /// </summary>
    private Task MarkLedgerQueuedAsync(string messageId) => ExecuteAsync($"""
        UPDATE public."EmailIngests"
        SET "ParseStatus" = '{EmailInquiryLedgerReconciliation.InFlightQueued}',
            "CreatedOn" = now() - interval '3 hours'
        WHERE "MessageID" = '{messageId}';
        """);

    /// <summary>
    /// Leaves a component exactly as the queue's own claim statement leaves it: the job stopped,
    /// and nothing anywhere told the component. Written before the worker runs, so the queue never
    /// claims the row and no worker is ever in the loop — which is the population no worker-side
    /// dead-letter annotation covers.
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

    private Task<string?> ParseStatusAsync(string messageId) => TextAsync($"""
        SELECT "ParseStatus" FROM public."EmailIngests" WHERE "MessageID" = '{messageId}';
        """);

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
