using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE acceptance test for email → Lead. One message goes in; the durable state a
/// customer's inquiry is made of comes out.
///
/// <para><b>Why it is built this way.</b> Every other test on this path proves one seam
/// against doubles. Three independent reviewers landed on the same gap: nothing ran
/// capture → schedule → queue → worker → barrier against a real database in one pass, so
/// each seam could be individually green while the pipeline as a whole moved no message at
/// all. That is not hypothetical — it is exactly what production did.</para>
///
/// <para>The composition comes from <see cref="EmailToLeadHarness"/> so that every test on
/// this path shares ONE definition of the production graph. See that class for what is real
/// and what is substituted.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailToLeadVerticalSlicePostgreSqlTests(PostgreSqlTestDatabase database) : IAsyncLifetime
{
    private const long BusinessUnitId = 940_101;
    private const string MessageId = "vertical-slice-0001@buyer.example";

    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-slice-" + Guid.NewGuid().ToString("N")[..12]);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the run is not a test failure */ }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task One_email_with_two_priced_attachments_becomes_exactly_one_Lead_carrying_every_line()
    {
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, BusinessUnitId, MessageId);

        var llm = new EmailToLeadHarness.RefusingLlm();
        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, llm);

        // ---- 1-2. CAPTURE the message durably, then SCHEDULE one job per component. ----
        var message = EmailToLeadHarness.BuildMessage(MessageId);
        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, BusinessUnitId, message);

        Assert.Equal(3, schedule.Scheduled);
        Assert.Equal(0, schedule.Held);
        Assert.True(schedule.FullyScheduled, $"Manifest verdict was {schedule.Verdict}.");

        // ---- 3. DRAIN: the real worker, claiming through the real queue. ----
        await EmailToLeadHarness.DrainQueueAsync(services, BusinessUnitId);

        // ---- 4. ASSERT the durable outcome. ----
        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(BusinessUnitId);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

            var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
                .SingleAsync(a => a.Id == assemblyId);
            var components = await context.EmailInquiryComponents.AsNoTracking()
                .Where(c => c.AssemblyId == assemblyId).OrderBy(c => c.Ordinal).ToListAsync();

            // (a) Every component reached a terminal state — nothing is still in flight and
            //     nothing is parked on a hold.
            var stuck = components
                .Where(c => !c.IsTerminal)
                .Select(c => $"{c.ComponentKey}={c.Status}({c.ReasonCode})")
                .ToList();
            Assert.True(stuck.Count == 0, "Components never reached a terminal state: " + string.Join("; ", stuck));

            // (b) Specifically, none is held waiting for a result store that does not exist.
            //     This is the assertion that fails today, and it names the real gap.
            var pending = components
                .Where(c => c.ReasonCode == EmailInquiryHoldReasons.AssemblyResultStorePending)
                .Select(c => c.ComponentKey)
                .ToList();
            Assert.True(pending.Count == 0,
                "Components are held for a missing result store: " + string.Join("; ", pending));

            // (c) Each completed component has a DURABLE result. A component marked complete
            //     with its extraction thrown away is the silent-data-loss failure mode.
            //
            //     Asserted in SQL, not through EF: the claim is that the ROW exists, and a
            //     DbSet assertion cannot distinguish "persisted" from "still in the change
            //     tracker". It also lets the test compile and fail at the real boundary
            //     rather than at the compiler while the store is being built.
            var completed = components.Count(c => c.Status == EmailInquiryComponentStatus.Completed);
            Assert.Equal(completed, await ScalarAsync(
                $"""
                SELECT count(*) FROM public."EmailInquiryComponentResults"
                WHERE "AssemblyId" = {assemblyId}
                  AND "PayloadJson" IS NOT NULL AND "PayloadJson"::text <> 'null'
                  AND "PayloadContractVersion" > 0;
                """));

            // (d) Ownership is single-authority: every job the message produced names its
            //     component. Three ownership fields disagreeing is how a result gets written
            //     against the wrong part of the wrong message.
            Assert.Equal(3, await ScalarAsync(
                $"""
                SELECT count(*) FROM public."ExtractionJobs" j
                JOIN public."EmailInquiryComponents" c
                  ON c."Id" = j."EmailInquiryComponentId" AND c."BusinessUnitId" = j."BusinessUnitId"
                WHERE j."BatchId" = '{schedule.BatchId}' AND c."AssemblyId" = {assemblyId};
                """));
            Assert.Equal(0, await ScalarAsync(
                $"""
                SELECT count(*) FROM public."ExtractionJobs"
                WHERE "BatchId" = '{schedule.BatchId}' AND "EmailInquiryComponentId" IS NULL;
                """));

            // (e) The barrier fired: the assembly is complete, not merely ready.
            Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
            Assert.Equal(assembly.ExpectedComponentCount, assembly.CompletedComponentCount);

            // (f) EXACTLY ONE Lead for the message — not one per attachment, which is what
            //     the legacy per-attachment enqueue produced.
            var leads = await context.Leads.AsNoTracking()
                .Where(l => l.BusinessUnitId == BusinessUnitId).ToListAsync();
            var lead = Assert.Single(leads);

            // (g) …carrying the lines from BOTH attachments. A Lead built from whichever
            //     part finished first is the commercial defect this whole module exists to
            //     prevent: the buyer's second sheet silently disappears.
            var lines = await context.LeadItems.AsNoTracking()
                .Where(i => i.LeadId == lead.Id).ToListAsync();
            Assert.Equal(5, lines.Count);
            // Every line from BOTH sheets, in the order the message presented them. The
            // sequence is asserted, not just membership: a merge that concatenated in
            // completion order would still contain all five and would silently reorder a
            // buyer's schedule, which is how line 3 gets priced as line 5.
            Assert.Equal(
                ["VLV-1001", "VLV-1002", "GSK-3007", "GSK-3008", "GSK-3009"],
                lines.OrderBy(i => i.Id).Select(i => i.ManufacturerPartNumber).ToArray());

            // And the descriptions travelled with them, so the lines are whole rather than
            // a column of codes with the text dropped.
            Assert.Contains(lines, i => (i.ProductShortName ?? "").Contains("Ball valve DN50"));
            Assert.Contains(lines, i => (i.ProductShortName ?? "").Contains("Ring joint gasket"));

            // (h) The evidence chain is intact: the raw .eml is still addressable and the
            //     hash recorded at capture still describes it.
            Assert.False(string.IsNullOrWhiteSpace(assembly.RawEvidenceUri));
            Assert.Equal(64, assembly.RawEvidenceSha256?.Length);
            var evidence = scope.ServiceProvider.GetRequiredService<IEvidenceObjectStorage>();
            await using var raw = await evidence.OpenVerifiedReadAsync(
                assembly.RawEvidenceUri!, assembly.RawEvidenceSha256!);
            Assert.True(raw.Length > 0);
        }

        // (i) The CSVs never reached a model. If they had, the run is neither deterministic
        //     nor cheap, and the structured fast path has silently regressed.
        Assert.Equal(0, llm.CallCount);

        // ---- 5. REPLAY: draining again must not manufacture a second Lead. ----
        await EmailToLeadHarness.DrainQueueAsync(services, BusinessUnitId);
        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(BusinessUnitId);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            Assert.Equal(1, await context.Leads.AsNoTracking()
                .CountAsync(l => l.BusinessUnitId == BusinessUnitId));
        }
    }

    /// <summary>Reads a count straight from PostgreSQL, past EF and past every filter.</summary>
    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }
}
