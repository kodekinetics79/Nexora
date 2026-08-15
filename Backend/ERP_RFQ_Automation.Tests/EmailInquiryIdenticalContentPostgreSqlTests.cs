using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Two defects that only appear once a SECOND message exists, both observed on a live stack.
///
/// <para><b>1. Identical attachment content stranded the second message.</b> Documents are
/// content-addressed, and the ingestion gateway reused the extraction JOB of the first occurrence
/// of a given content hash. Two emails carrying byte-identical attachments — the same buyer
/// re-sending a schedule, two buyers attaching the same standard form — therefore had the second
/// message's components bound to jobs that had ALREADY succeeded for the first. A finished job
/// never runs again, so those components never received a result and the message waited at the
/// barrier at 1-of-3 forever: no error, no dead letter, nothing to sweep, and no Lead. Deduping
/// the stored BYTES is the saving worth having; deduping the JOB is what breaks the barrier.</para>
///
/// <para><b>2. A message was marked Assembled with AssembledLeadId = 0.</b> The persister returns
/// the id of the Lead it created, and returns a non-positive value when it created none — which
/// happens for an ordinary reason: identity reconciliation classified the merged inquiry as a
/// possible match against an existing Lead and raised it for a human instead of writing a second
/// commercial record. The assembler stored that value verbatim and declared success, so the
/// message read as finished and the UI offered "open lead" for a lead that does not exist.</para>
///
/// <para>The composition is <see cref="EmailToLeadHarness"/>'s — the real database, queue,
/// ingestion gateway, worker, persister, coordinator and assembler. The ONE substitution is the
/// model standing in for prose understanding of the covering note, which the harness already
/// substitutes; this class replaces it with a version that reads the note it was given rather than
/// stamping one constant RFQ number on every message, because with a constant every second message
/// in a tenant carries the first one's customer reference.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EmailInquiryIdenticalContentPostgreSqlTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    private const long FirstBu = 943_000;
    private const long LastBu = 943_099;

    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-identical-" + Guid.NewGuid().ToString("N")[..12]);

    /// <summary>The bytes BOTH messages attach, unchanged. This is the whole point.</summary>
    private const string SharedValves =
        "Part Number,Description,Quantity,Unit\n"
        + "VLV-1001,Ball valve DN50 PN16 stainless,12,EA\n"
        + "VLV-1002,Gate valve DN80 PN16 carbon steel,4,EA\n";

    private const string SharedGaskets =
        "Part Number,Description,Quantity,Unit\n"
        + "GSK-3007,Spiral wound gasket DN50 CL150,60,EA\n"
        + "GSK-3008,Spiral wound gasket DN80 CL150,25,EA\n"
        + "GSK-3009,Ring joint gasket R-24 soft iron,8,EA\n";

    /// <summary>
    /// The same five requirements, written out in the opposite order. Different BYTES, identical
    /// commercial content — which is what puts the second message in front of identity
    /// reconciliation as a possible match without involving content-addressed job reuse at all.
    /// </summary>
    private const string ReorderedValves =
        "Part Number,Description,Quantity,Unit\n"
        + "VLV-1002,Gate valve DN80 PN16 carbon steel,4,EA\n"
        + "VLV-1001,Ball valve DN50 PN16 stainless,12,EA\n";

    private const string ReorderedGaskets =
        "Part Number,Description,Quantity,Unit\n"
        + "GSK-3009,Ring joint gasket R-24 soft iron,8,EA\n"
        + "GSK-3008,Spiral wound gasket DN80 CL150,25,EA\n"
        + "GSK-3007,Spiral wound gasket DN50 CL150,60,EA\n";

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Deletes every row this class created. The recovery sweep is PLATFORM-wide, so a message
    /// left behind here is an eligible candidate for a later test in another class.
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
        catch (IOException) { /* a temp directory that outlives the run is not a test failure */ }
    }

    // =====================================================================================
    // DEFECT 1 — byte-identical attachments across two messages
    // =====================================================================================

    [Fact]
    public async Task Two_messages_carrying_byte_identical_attachments_each_become_their_own_Lead()
    {
        const long bu = 943_001;
        const string firstMessageId = "identical-content-first@buyer-a.example";
        const string secondMessageId = "identical-content-second@buyer-b.example";

        await SeedAsync(bu, firstMessageId, secondMessageId);

        var llm = new EmailToLeadHarness.RefusingLlm();
        await using var services = BuildGraph(llm);

        // Two DIFFERENT buyers sending the SAME standard schedules, each under their own RFQ
        // reference — one of the two scenarios the execution ledger names for this trap.
        var firstBody =
            "Dear Nexora,\n\nPlease quote the attached standard schedules under our reference "
            + "RFQ-AAA-8801.\n\nRegards,\nBuyer A";
        var secondBody =
            "Dear Nexora,\n\nPlease quote the attached standard schedules under our reference "
            + "RFQ-BBB-9902.\n\nRegards,\nBuyer B";

        var first = BuildMessage(firstMessageId, "Standard schedules A", "buyer-a@customer-a.example",
            firstBody, SharedValves, SharedGaskets);
        var second = BuildMessage(secondMessageId, "Standard schedules B", "buyer-b@customer-b.example",
            secondBody, SharedValves, SharedGaskets);

        // ---- 1. The first message runs to completion, so its jobs are Succeeded. ----
        var (firstAssemblyId, firstSchedule) = await CaptureAndScheduleAsync(services, bu, first, firstBody);
        Assert.Equal(3, firstSchedule.Scheduled);

        // IDEMPOTENCY IS NOT WEAKENED, and this is where it can still be asked. While the
        // components are in flight they are genuinely re-schedulable, so running the canonical
        // scheduler again over the SAME message is a real replay rather than a no-op skip over
        // finished work. One job per component, however many times it runs: giving each component
        // its own job must not cost the guarantee that a component has only one.
        var replay = await ScheduleAsync(services, bu, first, firstBody, firstAssemblyId);
        Assert.Equal(0, replay.Scheduled);
        Assert.Equal(3, replay.AlreadyScheduled);
        Assert.Equal(3, await ScalarAsync($"""
            SELECT count(*) FROM public."ExtractionJobs" WHERE "BusinessUnitId" = {bu};
            """));

        await EmailToLeadHarness.DrainQueueAsync(services, bu);

        // ---- 2. The second message arrives with the SAME attachment bytes. ----
        var (secondAssemblyId, secondSchedule) = await CaptureAndScheduleAsync(services, bu, second, secondBody);
        Assert.Equal(3, secondSchedule.Scheduled);
        // Settlement is waited for, not skipped. With the defect present the queue empties and the
        // message still never settles — which is precisely the failure, so the wait timing out IS
        // the red, and the assertions below then say which message never became an inquiry.
        await EmailToLeadHarness.DrainQueueAsync(services, bu);

        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(bu);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

            var assemblies = await context.EmailInquiryAssemblies.AsNoTracking()
                .Where(a => a.BusinessUnitId == bu).OrderBy(a => a.Id).ToListAsync();
            Assert.Equal(2, assemblies.Count);

            // (a) NEITHER message is stranded. This is the defect: the second one used to sit at
            //     Extracting, 1 of 3, forever — its two attachment components bound to jobs that
            //     had already succeeded for the first message.
            var components = await context.EmailInquiryComponents.AsNoTracking()
                .Where(c => c.BusinessUnitId == bu).ToListAsync();
            var stalled = components.Where(c => !c.IsTerminal)
                .Select(c => $"assembly {c.AssemblyId} component {c.ComponentKey}={c.Status}")
                .ToList();
            Assert.True(stalled.Count == 0,
                "Components never reached a terminal state: " + string.Join("; ", stalled));

            // (b) Both messages became a Lead of their OWN.
            foreach (var assembly in assemblies)
            {
                Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
                Assert.Equal(assembly.ExpectedComponentCount, assembly.CompletedComponentCount);
                Assert.True(assembly.AssembledLeadId is > 0,
                    $"Assembly {assembly.Id} is {assembly.Status} with AssembledLeadId "
                    + $"{assembly.AssembledLeadId?.ToString() ?? "null"}.");
            }
            Assert.Equal(2, assemblies.Select(a => a.AssembledLeadId).Distinct().Count());

            // (c) Two Leads for two messages — not one, and not three.
            var leads = await context.Leads.AsNoTracking()
                .Where(l => l.BusinessUnitId == bu).ToListAsync();
            Assert.Equal(2, leads.Count);

            // (d) …and each carries EVERY line of its own message. A Lead built from whichever
            //     part happened to run is the commercial defect the barrier exists to prevent.
            foreach (var assembly in assemblies)
            {
                var lines = await context.LeadItems.AsNoTracking()
                    .Where(i => i.LeadId == assembly.AssembledLeadId!.Value).ToListAsync();
                Assert.Equal(
                    ["VLV-1001", "VLV-1002", "GSK-3007", "GSK-3008", "GSK-3009"],
                    lines.OrderBy(i => i.Id).Select(i => i.ManufacturerPartNumber).ToArray());
            }
        }

        // (e) EVERY component owns its own job — six components, six distinct jobs, and no job
        //     without an owner. Asserted in SQL, past EF and past every filter.
        Assert.Equal(6, await ScalarAsync($"""
            SELECT count(DISTINCT j."Id") FROM public."ExtractionJobs" j
            JOIN public."EmailInquiryComponents" c
              ON c."Id" = j."EmailInquiryComponentId" AND c."BusinessUnitId" = j."BusinessUnitId"
            WHERE j."BusinessUnitId" = {bu};
            """));
        Assert.Equal(0, await ScalarAsync($"""
            SELECT count(*) FROM public."ExtractionJobs"
            WHERE "BusinessUnitId" = {bu} AND "EmailInquiryComponentId" IS NULL;
            """));

        // (f) The EVIDENCE is still content-addressed and still deduplicated. Splitting the JOB
        //     must not split the stored object: the two identical schedules remain ONE source
        //     document with two occurrences — the saving that was always correct. Deduping bytes
        //     is right; deduping the job is what broke the barrier.
        var sharedHash = Sha256Hex(SharedValves);
        Assert.Equal(1, await ScalarAsync($"""
            SELECT count(*) FROM public.source_documents
            WHERE business_unit_id = {bu} AND content_hash = '{sharedHash}';
            """));
        Assert.Equal(2, await ScalarAsync($"""
            SELECT count(*) FROM public.source_document_occurrences o
            JOIN public.source_documents d
              ON d.id = o.source_document_id AND d.business_unit_id = o.business_unit_id
            WHERE o.business_unit_id = {bu} AND d.content_hash = '{sharedHash}';
            """));

        Assert.Equal(0, llm.CallCount);
        Assert.NotEqual(firstAssemblyId, secondAssemblyId);
    }

    // =====================================================================================
    // DEFECT 2 — Assembled with no Lead
    // =====================================================================================

    [Fact]
    public async Task A_message_whose_persist_produced_no_Lead_is_held_for_review_not_marked_assembled()
    {
        const long bu = 943_002;
        const string firstMessageId = "no-lead-first@buyer.example";
        const string secondMessageId = "no-lead-second@buyer.example";

        await SeedAsync(bu, firstMessageId, secondMessageId);

        var llm = new EmailToLeadHarness.RefusingLlm();
        await using var services = BuildGraph(llm);

        // The SAME buyer, twice, with the same requirements written out in a different order and
        // a different covering note — and neither note states a customer RFQ reference. Identity
        // reconciliation has corroborating customer identity, matching commercial content and no
        // reference to tell it whether this is an amendment, so it raises a possible match for a
        // human and creates NO second Lead. That is correct behaviour, and it is the path that
        // returns a non-positive lead id.
        //
        // The attachments are deliberately byte-DIFFERENT here, so this test is about the
        // disposition alone and does not depend on the content-addressing fix above.
        var firstBody =
            "Dear Nexora,\n\nPlease quote the attached requirements for the Jubail expansion.\n\n"
            + "Regards,\nBuyer";
        var secondBody =
            "Hello again,\n\nResending the requirements for Jubail in case the first note was "
            + "missed. Same scope.\n\nRegards,\nBuyer";

        var first = BuildMessage(firstMessageId, "Jubail requirements", "buyer@customer.example",
            firstBody, SharedValves, SharedGaskets);
        var second = BuildMessage(secondMessageId, "Jubail requirements (resend)", "buyer@customer.example",
            secondBody, ReorderedValves, ReorderedGaskets);

        var (firstAssemblyId, _) = await CaptureAndScheduleAsync(services, bu, first, firstBody);
        await EmailToLeadHarness.DrainQueueAsync(services, bu);

        var (secondAssemblyId, _) = await CaptureAndScheduleAsync(services, bu, second, secondBody);
        await EmailToLeadHarness.DrainQueueAsync(services, bu);

        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(bu);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

            // The first message is an ordinary success and stays one.
            var firstAssembly = await context.EmailInquiryAssemblies.AsNoTracking()
                .SingleAsync(a => a.Id == firstAssemblyId);
            Assert.Equal(EmailInquiryAssemblyStatus.Assembled, firstAssembly.Status);
            Assert.True(firstAssembly.AssembledLeadId is > 0);

            // The second produced no Lead, so it is HELD — with a reason a person can act on —
            // rather than declared complete against an id that is not an id.
            var secondAssembly = await context.EmailInquiryAssemblies.AsNoTracking()
                .SingleAsync(a => a.Id == secondAssemblyId);
            Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, secondAssembly.Status);
            Assert.Null(secondAssembly.AssembledLeadId);
            Assert.Contains(
                EmailInquiryHoldReasons.LeadNotProduced, secondAssembly.StatusReason ?? string.Empty);

            // No Lead was invented for it, and the first one was not silently overwritten.
            Assert.Equal(1, await context.Leads.AsNoTracking().CountAsync(l => l.BusinessUnitId == bu));

            // The human decision the reconciliation raised is durable and joinable — the message
            // is held for a question that exists, not for a mystery.
            Assert.Equal(1, await ScalarAsync($"""
                SELECT count(*) FROM public."LeadIngestionOccurrences"
                WHERE "BusinessUnitId" = {bu}
                  AND "Classification" = 'PossibleMatchReviewRequired' AND "LeadId" IS NULL;
                """));
        }

        // THE INVARIANT ITSELF, stated once and checked platform-wide: Assembled always names a
        // Lead. Nothing in this table may claim a message became an inquiry it cannot point at.
        Assert.Equal(0, await ScalarAsync("""
            SELECT count(*) FROM public."EmailInquiryAssemblies"
            WHERE "Status" = 'Assembled'
              AND ("AssembledLeadId" IS NULL OR "AssembledLeadId" <= 0);
            """));

        Assert.Equal(0, llm.CallCount);
    }

    // =====================================================================================
    // Composition and message construction
    // =====================================================================================

    private ServiceProvider BuildGraph(EmailToLeadHarness.RefusingLlm llm)
        => EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, llm,
            services =>
            {
                services.AddScoped<
                    ERP_RFQ_Automation.Extraction.Conversational.IConversationalExtractionService,
                    CoveringNoteExtractor>();

                // THE REAL IDENTITY SERVICE, as Program.cs registers it.
                //
                // LeadPersister takes it as an OPTIONAL constructor argument, so a graph that
                // omits it silently takes a different branch: every merged message is simply
                // added as a new Lead row and reconciliation never runs. That branch cannot
                // produce the outcome under test here — "the message was read in full and no
                // Lead was created" is reconciliation's decision, and a graph without it can
                // only ever answer "a Lead was created". Registering it is what makes the second
                // message meet the first one the way it does in production.
                services.AddScoped<
                    ERP_RFQ_Automation.LeadIdentity.ILeadIdentityApplicationService,
                    ERP_RFQ_Automation.LeadIdentity.LeadIdentityApplicationService>();
            });

    /// <summary>
    /// Stands in for the model on the one component that genuinely needs prose understanding: the
    /// sender's covering note.
    ///
    /// <para>It differs from the harness's version in one respect that this class cannot do
    /// without — it reads the note it was handed. The harness's stamps a single constant RFQ
    /// number on every message, which makes every second message in a tenant carry the first's
    /// customer reference and turns the identity decision under test into an artefact of the
    /// double. Here the reference is the one written in the note, and a note that states none
    /// yields none.</para>
    /// </summary>
    private sealed class CoveringNoteExtractor
        : ERP_RFQ_Automation.Extraction.Conversational.IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
        {
            var text = input.HeaderText + "\n" + string.Join("\n", input.LineItemRegions);
            var reference = text
                .Split([' ', '\n', '\r', '\t', '.', ','], StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(word => word.StartsWith("RFQ-", StringComparison.Ordinal));

            return Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                // Header only. A covering note contributes the header; the priced lines come from
                // the attachments, which is exactly what the barrier merges.
                Result = Ext.Result([], 0.95) with { Rfqno = reference },
                ExpectedItemCount = 0,
                ExtractedItemCount = 0,
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
        }
    }

    private static MimeMessage BuildMessage(
        string messageId, string subject, string sender, string body,
        string firstAttachment, string secondAttachment)
    {
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = body } };
        mixed.Add(CsvAttachment("valves.csv", firstAttachment));
        mixed.Add(CsvAttachment("gaskets.csv", secondAttachment));

        var message = new MimeMessage { Subject = subject, Body = mixed };
        message.From.Add(new MailboxAddress("Buyer", sender));
        message.To.Add(new MailboxAddress("Nexora", "rfq@nexora.example"));
        message.MessageId = messageId;
        message.Date = new DateTimeOffset(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);
        return message;
    }

    private static MimePart CsvAttachment(string fileName, string content) =>
        new("text", "csv")
        {
            Content = new MimeContent(new MemoryStream(Encoding.UTF8.GetBytes(content))),
            ContentDisposition = new ContentDisposition(ContentDisposition.Attachment) { FileName = fileName },
            ContentTransferEncoding = ContentEncoding.Base64,
            FileName = fileName
        };

    /// <summary>
    /// Captures and schedules through the real services.
    ///
    /// <para>Not <see cref="EmailToLeadHarness.CaptureAndScheduleAsync"/> because that one
    /// captures every message with the same body text, and both tests here turn on two messages
    /// whose covering notes differ.</para>
    /// </summary>
    private static async Task<(long AssemblyId, EmailScheduleResult Schedule)> CaptureAndScheduleAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message, string bodyText)
    {
        long assemblyId;
        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == businessUnitId);
            var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);

            var capture = await scope.ServiceProvider.GetRequiredService<IEmailInquiryCaptureService>()
                .CaptureAsync(message, ingest, configuration, bodyText);

            Assert.NotNull(capture.Assembly);
            Assert.False(capture.AlreadyCaptured,
                "A fresh message must not resolve to an existing assembly.");
            Assert.True(capture.SafeToMarkSeen,
                "Capture must be durable before the mailbox is told the message was read.");
            Assert.Equal(3, capture.Assembly!.ExpectedComponentCount);
            assemblyId = capture.Assembly.Id;
        }

        var schedule = await ScheduleAsync(services, businessUnitId, message, bodyText, assemblyId);
        return (assemblyId, schedule);
    }

    /// <summary>Runs the canonical scheduler over an already-captured message.</summary>
    private static async Task<EmailScheduleResult> ScheduleAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message, string bodyText,
        long assemblyId)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var assembly = await context.EmailInquiryAssemblies.SingleAsync(a => a.Id == assemblyId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);
        var components = await context.EmailInquiryComponents
            .Where(c => c.AssemblyId == assemblyId).OrderBy(c => c.Ordinal).ToListAsync();
        var plan = await EmailInquiryManifestPlanner.PlanAsync(message, assembly.MessageKey, bodyText);

        return await EmailIngestEnqueuer.ScheduleAsync(
            assembly, components, plan, ingest, null,
            scope.ServiceProvider.GetRequiredService<IDocumentIngestion>(),
            new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
            scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>());
    }

    private async Task SeedAsync(long businessUnitId, params string[] messageIds)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageIds);
    }

    /// <summary>The evidence store's own address for a document: lowercase hex SHA-256.</summary>
    private static string Sha256Hex(string content)
        => Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();

    private async Task<int> ScalarAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
