using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Proof that a message whose parts all finished but whose Lead was never built is recovered.
///
/// <para><b>The window, precisely.</b> The extraction worker commits the queue job and assembles
/// the message afterwards. That order is correct — assembling first and completing second means a
/// crash in between is retried and builds the Lead twice — but it leaves a real gap: a process
/// that dies in between leaves every component job <c>Succeeded</c>, every component result
/// durable, and the message at <c>ReadyForAssembly</c> with no Lead, no error log, no dead letter
/// and nothing that would ever look at it again. The customer's RFQ silently does not exist, and
/// the window is entered on every deploy, not only on crashes.</para>
///
/// <para><b>How the crash is injected.</b> One registration is replaced — the assembler — so the
/// worker's assemble step throws at the instant a dying process would simply stop. Nothing that
/// WRITES is substituted: the stranded state these tests assert on is produced by the real queue,
/// the real worker, the real persister and the real coordinator against real PostgreSQL under the
/// production retry policy. The seam controls WHEN the process stops, never WHAT was persisted.
/// Process loss itself is then simulated by disposing the entire ServiceProvider — every
/// DbContext, connection pool and change tracker goes with it — and recovery runs on a brand new
/// graph that shares nothing but the database.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailInquiryAssemblyRecoveryPostgreSqlTests(PostgreSqlTestDatabase database) : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-recovery-" + Guid.NewGuid().ToString("N")[..12]);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    // =====================================================================================
    // THE CENTRAL PROOF
    // =====================================================================================

    [Fact]
    public async Task A_message_stranded_by_a_crash_after_the_commit_is_recovered_into_exactly_one_Lead()
    {
        const long bu = 941_001;
        const string messageId = "recovery-0001@buyer.example";
        await SeedAsync(bu, messageId);

        // ---- 1. Run the REAL pipeline, and stop the process where the crash happens. ----
        long assemblyId;
        await using (var crashing = BuildCrashingGraph(out var crashProbe))
        {
            var (id, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
                crashing, bu, EmailToLeadHarness.BuildMessage(messageId));
            assemblyId = id;
            Assert.Equal(3, schedule.Scheduled);

            // waitForAssemblySettlement: false — the state inside the window IS the subject.
            await EmailToLeadHarness.DrainQueueAsync(
                crashing, bu, assertNoFailures: true, waitForAssemblySettlement: false);

            Assert.True(crashProbe.Crashes > 0,
                "The crash seam never fired, so this test proved nothing about recovery.");

            // ---- 2. The stranded state, asserted in full. ----
            await AssertStrandedAsync(bu, assemblyId);
        }
        // ---- 3. Process loss: every context, pool and tracker is gone. ----

        // ---- 4. A brand new production-configured graph sweeps. ----
        await using var recovered = BuildGraph();
        var result = await SweepAsync(recovered);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.StillRecoverable);
        Assert.Equal(0, result.Unexpected);

        // ---- 5. Exactly one Lead, and the message names it. ----
        var leadId = await AssertRecoveredAsync(bu, assemblyId);

        // ---- 6. Sweeping again is a no-op: no second Lead, and no extraction re-run. ----
        EmailToLeadHarness.DeterministicBodyExtractor.ResetCallCount();
        var second = await SweepAsync(recovered);

        Assert.Equal(0, second.Candidates);
        Assert.Equal(0, second.Recovered);
        Assert.Equal(1, await CountLeadsAsync(bu));
        Assert.Equal(leadId, await AssembledLeadIdAsync(bu, assemblyId));

        // Nothing was re-extracted. The component results were already durable, which is the
        // entire reason for having built them: recovery combines, it does not reprocess.
        Assert.Equal(0, EmailToLeadHarness.DeterministicBodyExtractor.CallCount);
    }

    // =====================================================================================
    // CONCURRENCY AND CRASH BEHAVIOUR
    // =====================================================================================

    [Fact]
    public async Task Two_recovery_instances_running_at_once_produce_one_Lead_and_agree_on_its_id()
    {
        const long bu = 941_002;
        const string messageId = "recovery-0002@buyer.example";
        await SeedAsync(bu, messageId);
        var assemblyId = await StrandAsync(bu, messageId);

        // Two independent graphs, as two pods would be. The advisory lease may let only one
        // scan, but correctness must not depend on that: the assembler's FOR UPDATE on the
        // assembly row is the boundary, and it holds whether the second caller is another sweep
        // instance or a live extraction worker.
        await using var first = BuildGraph();
        await using var second = BuildGraph();

        var results = await Task.WhenAll(SweepAsync(first), SweepAsync(second));

        Assert.Equal(1, await CountLeadsAsync(bu));

        var leadId = await AssembledLeadIdAsync(bu, assemblyId);
        Assert.NotNull(leadId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(bu, assemblyId));

        // Between them exactly one recovered it; the other either skipped on the lease or found
        // it already complete. Neither may report a failure.
        Assert.Equal(1, results.Sum(r => r.Recovered));
        Assert.Equal(0, results.Sum(r => r.Failed));
        Assert.Equal(0, results.Sum(r => r.Unexpected));
    }

    [Fact]
    public async Task A_crash_DURING_the_assembler_transaction_leaves_no_partial_Lead_and_stays_recoverable()
    {
        const long bu = 941_003;
        const string messageId = "recovery-0003@buyer.example";
        await SeedAsync(bu, messageId);
        var assemblyId = await StrandAsync(bu, messageId);

        // Cancel mid-flight. The assembler holds the assembly row and writes the Lead and the
        // Assembled transition in ONE transaction, so an abort must leave neither.
        await using (var cancelling = BuildGraph())
        {
            using var cts = new CancellationTokenSource();
            using var scope = cancelling.CreateScope();
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(bu);
            var assembler = scope.ServiceProvider.GetRequiredService<IEmailInquiryLeadAssembler>();

            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => assembler.AssembleAsync(bu, assemblyId, cts.Token));
        }

        // No half-built Lead, and the message is exactly where it was.
        Assert.Equal(0, await CountLeadsAsync(bu));
        Assert.Null(await AssembledLeadIdAsync(bu, assemblyId));
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, await StatusAsync(bu, assemblyId));

        // And it is still recoverable, which is the property that matters: a crash inside the
        // transaction must cost a retry, never a message.
        await using var recovering = BuildGraph();
        Assert.Equal(1, (await SweepAsync(recovering)).Recovered);
        Assert.Equal(1, await CountLeadsAsync(bu));
    }

    [Fact]
    public async Task A_failure_AFTER_a_successful_assembly_replays_as_a_harmless_no_op()
    {
        const long bu = 941_004;
        const string messageId = "recovery-0004@buyer.example";
        await SeedAsync(bu, messageId);
        var assemblyId = await StrandAsync(bu, messageId);

        await using var services = BuildGraph();
        Assert.Equal(1, (await SweepAsync(services)).Recovered);
        var leadId = await AssembledLeadIdAsync(bu, assemblyId);

        // The instant after a successful assemble, the process dies before anything downstream
        // records it. Whatever re-drives the message next — a sweep, a replayed worker — must
        // find the work already done and do nothing, rather than build a second Lead.
        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(bu);
            var assembler = scope.ServiceProvider.GetRequiredService<IEmailInquiryLeadAssembler>();

            // It returns the SAME Lead rather than null: "already done" must be distinguishable
            // from "nothing to do", or a caller cannot tell success from silence.
            Assert.Equal(leadId, await assembler.AssembleAsync(bu, assemblyId));
            Assert.Equal(leadId, await assembler.AssembleAsync(bu, assemblyId));
        }

        Assert.Equal(1, await CountLeadsAsync(bu));
    }

    // =====================================================================================
    // ISOLATION
    // =====================================================================================

    [Fact]
    public async Task One_failing_assembly_does_not_stop_another_tenants_ready_assembly()
    {
        const long healthy = 941_005;
        const long poisoned = 941_006;
        await SeedAsync(healthy, "recovery-0005@buyer.example");
        await SeedAsync(poisoned, "recovery-0006@buyer.example");

        var healthyAssembly = await StrandAsync(healthy, "recovery-0005@buyer.example");
        var poisonedAssembly = await StrandAsync(poisoned, "recovery-0006@buyer.example");

        // Poison the second message at the database, not in a stub: its component result now
        // claims a payload contract version this build cannot read. That is a real refusal the
        // assembler must make, and it must not take the other tenant down with it.
        await ExecuteAsync($"""
            UPDATE public."EmailInquiryComponentResults"
            SET "PayloadContractVersion" = 999
            WHERE "AssemblyId" = {poisonedAssembly};
            """);

        await using var services = BuildGraph();
        var result = await SweepAsync(services);

        // The healthy tenant is finished.
        Assert.Equal(1, await CountLeadsAsync(healthy));
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(healthy, healthyAssembly));

        // The poisoned one is HELD for review — visibly refused, never silently dropped and
        // never counted as recovered.
        Assert.Equal(0, await CountLeadsAsync(poisoned));
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, await StatusAsync(poisoned, poisonedAssembly));
        Assert.Equal(2, result.Candidates);
        Assert.Equal(1, result.Recovered);
        Assert.Equal(1, result.HeldForReview);
    }

    [Fact]
    public async Task A_tenant_can_neither_discover_nor_recover_another_tenants_stranded_message()
    {
        const long owner = 941_007;
        const long neighbour = 941_008;
        await SeedAsync(owner, "recovery-0007@buyer.example");
        await SeedAsync(neighbour, "recovery-0008@buyer.example");
        var ownerAssembly = await StrandAsync(owner, "recovery-0007@buyer.example");

        await using var services = BuildGraph();
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(neighbour);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // Cannot DISCOVER it: the per-tenant query runs through the normal filters.
        Assert.Empty(await context.EmailInquiryAssemblies.AsNoTracking()
            .Where(a => a.Status == EmailInquiryAssemblyStatus.ReadyForAssembly)
            .ToListAsync());

        // Cannot RECOVER it either, even naming the id outright — the assembler resolves the
        // assembly by (tenant, id), so the neighbour's attempt finds nothing rather than
        // building the owner's Lead into the neighbour's books.
        var assembler = scope.ServiceProvider.GetRequiredService<IEmailInquiryLeadAssembler>();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => assembler.AssembleAsync(neighbour, ownerAssembly));

        Assert.Equal(0, await CountLeadsAsync(neighbour));
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly, await StatusAsync(owner, ownerAssembly));
    }

    [Fact]
    public async Task Messages_in_every_other_state_are_left_alone()
    {
        const long bu = 941_009;
        await SeedAsync(bu, "recovery-0009@buyer.example");
        var assemblyId = await StrandAsync(bu, "recovery-0009@buyer.example");

        // Each of these is a state the sweep must NOT touch: Captured and Extracting are still
        // in flight, NeedsReview is a human's decision, and Assembled is finished. A sweep that
        // grabbed any of them would race live work or re-open a settled message.
        foreach (var status in new[]
                 {
                     EmailInquiryAssemblyStatus.Captured,
                     EmailInquiryAssemblyStatus.Extracting,
                     EmailInquiryAssemblyStatus.NeedsReview,
                     EmailInquiryAssemblyStatus.Assembled
                 })
        {
            await ExecuteAsync($"""
                UPDATE public."EmailInquiryAssemblies"
                SET "Status" = '{status}' WHERE "Id" = {assemblyId};
                """);

            await using var services = BuildGraph();
            var result = await SweepAsync(services);

            Assert.True(result.Candidates == 0,
                $"The sweep picked up an assembly in {status}, which it must never do.");
            Assert.Equal(0, await CountLeadsAsync(bu));
            Assert.Equal(status, await StatusAsync(bu, assemblyId));
        }
    }

    [Fact]
    public async Task Legacy_email_jobs_and_non_email_jobs_are_invisible_to_the_sweep()
    {
        const long bu = 941_010;
        await SeedAsync(bu, "recovery-0010@buyer.example");

        // A legacy per-attachment email job and a manual upload: neither owns a component, so
        // neither can produce an assembly, so neither is reachable from a status the sweep reads.
        // The sweep's work item is the ASSEMBLY, and this asserts the consequence.
        await ExecuteAsync($"""
            INSERT INTO public."ExtractionJobs"
                ("BusinessUnitId","BatchId","SourceType","ContentHash","StoragePath","FileName",
                 "FileType","Status","Priority","SchedulerTag","Attempts","MaxAttempts",
                 "NextAttemptAt","CreatedOn","UpdatedOn")
            VALUES
                ({bu}, gen_random_uuid(), 'Email', repeat('a', 64), 'memory://legacy', 'legacy.csv',
                 'csv', 'Succeeded', 0, 0, 1, 5, now(), now(), now()),
                ({bu}, gen_random_uuid(), 'ManualUpload', repeat('b', 64), 'memory://manual',
                 'manual.csv', 'csv', 'Succeeded', 0, 0, 1, 5, now(), now(), now());
            """);

        await using var services = BuildGraph();
        var result = await SweepAsync(services);

        Assert.Equal(0, result.Candidates);
        Assert.Equal(0, result.Recovered);
        Assert.Equal(0, await CountLeadsAsync(bu));

        // And neither job was touched.
        Assert.Equal(2, await ScalarAsync($"""
            SELECT count(*) FROM public."ExtractionJobs"
            WHERE "BusinessUnitId" = {bu} AND "Status" = 'Succeeded'
              AND "EmailInquiryComponentId" IS NULL;
            """));
    }

    // =====================================================================================
    // Harness
    // =====================================================================================

    private ServiceProvider BuildGraph() =>
        EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm());

    /// <summary>
    /// The production graph with ONE registration replaced: the assembler throws instead of
    /// assembling, which is where a dying process stops. Everything that writes stays real.
    /// </summary>
    private ServiceProvider BuildCrashingGraph(out CrashProbe probe)
    {
        var crashProbe = new CrashProbe();
        probe = crashProbe;
        return EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            services => services.AddScoped<IEmailInquiryLeadAssembler>(_ => new CrashingAssembler(crashProbe)));
    }

    private sealed class CrashProbe
    {
        private int _crashes;
        public int Crashes => Volatile.Read(ref _crashes);
        public void Record() => Interlocked.Increment(ref _crashes);
    }

    /// <summary>Stops the process exactly where the worker would begin assembling.</summary>
    private sealed class CrashingAssembler(CrashProbe probe) : IEmailInquiryLeadAssembler
    {
        public Task<long?> AssembleAsync(long businessUnitId, long assemblyId, CancellationToken ct = default)
        {
            probe.Record();
            throw new InvalidOperationException("Simulated process loss before assembly.");
        }
    }

    private async Task SeedAsync(long businessUnitId, string messageId)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
    }

    /// <summary>
    /// Drives the real pipeline to the stranded state and returns the assembly id. Used by the
    /// tests whose subject is what happens NEXT; the central proof asserts the state itself.
    /// </summary>
    private async Task<long> StrandAsync(long businessUnitId, string messageId)
    {
        await using var crashing = BuildCrashingGraph(out var probe);
        var (assemblyId, _) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            crashing, businessUnitId, EmailToLeadHarness.BuildMessage(messageId));
        await EmailToLeadHarness.DrainQueueAsync(
            crashing, businessUnitId, assertNoFailures: true, waitForAssemblySettlement: false);

        Assert.True(probe.Crashes > 0, "The crash seam never fired.");
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly,
            await StatusAsync(businessUnitId, assemblyId));
        return assemblyId;
    }

    private static async Task<EmailInquiryRecoverySweepResult> SweepAsync(ServiceProvider services)
    {
        // await INSIDE the using. `using var scope` around a returned Task disposes the scope
        // before the sweep runs, which is its own quiet defect and would make every assertion
        // below a statement about a disposed container.
        using var scope = services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IEmailInquiryAssemblyRecoveryService>()
            .SweepOnceAsync();
    }

    /// <summary>Everything the crash window is supposed to leave behind, asserted in SQL.</summary>
    private async Task AssertStrandedAsync(long businessUnitId, long assemblyId)
    {
        // Every job finished successfully — this is not a failed extraction.
        Assert.Equal(3, await ScalarAsync($"""
            SELECT count(*) FROM public."ExtractionJobs" j
            JOIN public."EmailInquiryComponents" c ON c."Id" = j."EmailInquiryComponentId"
            WHERE c."AssemblyId" = {assemblyId} AND j."Status" = 'Succeeded';
            """));

        // The work is durable: three results, three completed components.
        Assert.Equal(3, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponentResults"
            WHERE "AssemblyId" = {assemblyId} AND "PayloadJson" IS NOT NULL;
            """));
        Assert.Equal(3, await ScalarAsync($"""
            SELECT count(*) FROM public."EmailInquiryComponents"
            WHERE "AssemblyId" = {assemblyId} AND "Status" = 'Completed';
            """));

        // And the message owes a Lead that does not exist.
        Assert.Equal(EmailInquiryAssemblyStatus.ReadyForAssembly,
            await StatusAsync(businessUnitId, assemblyId));
        Assert.Null(await AssembledLeadIdAsync(businessUnitId, assemblyId));
        Assert.Equal(0, await CountLeadsAsync(businessUnitId));
    }

    private async Task<long> AssertRecoveredAsync(long businessUnitId, long assemblyId)
    {
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, await StatusAsync(businessUnitId, assemblyId));

        var leadId = await AssembledLeadIdAsync(businessUnitId, assemblyId);
        Assert.NotNull(leadId);
        Assert.Equal(1, await CountLeadsAsync(businessUnitId));

        // The recorded id is the Lead that actually exists, not merely non-null.
        Assert.Equal(1, await ScalarAsync($"""
            SELECT count(*) FROM public."Leads"
            WHERE "ID" = {leadId!.Value} AND "BusinessUnitID" = {businessUnitId};
            """));

        // And it carries every line from both attachments, in the order the buyer wrote them —
        // recovery must produce the SAME Lead the live path would have.
        var parts = await QueryAsync($"""
            SELECT "ManufacturerPartNumber" FROM public."LeadItems"
            WHERE "LeadID" = {leadId.Value} ORDER BY "ID";
            """);
        Assert.Equal(["VLV-1001", "VLV-1002", "GSK-3007", "GSK-3008", "GSK-3009"], parts);

        return leadId.Value;
    }

    // ---- raw readers, deliberately past EF and every filter ----------------------------

    private async Task<EmailInquiryAssemblyStatus> StatusAsync(long businessUnitId, long assemblyId)
    {
        var values = await QueryAsync($"""
            SELECT "Status" FROM public."EmailInquiryAssemblies"
            WHERE "Id" = {assemblyId} AND "BusinessUnitId" = {businessUnitId};
            """);
        return Enum.Parse<EmailInquiryAssemblyStatus>(Assert.Single(values)!);
    }

    private async Task<long?> AssembledLeadIdAsync(long businessUnitId, long assemblyId)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT "AssembledLeadId" FROM public."EmailInquiryAssemblies"
            WHERE "Id" = {assemblyId} AND "BusinessUnitId" = {businessUnitId};
            """;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    private Task<int> CountLeadsAsync(long businessUnitId) => ScalarAsync(
        $"""SELECT count(*) FROM public."Leads" WHERE "BusinessUnitID" = {businessUnitId};""");

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
