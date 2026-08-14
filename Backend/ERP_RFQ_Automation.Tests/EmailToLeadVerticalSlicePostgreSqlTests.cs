using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE acceptance test for email → Lead. One message goes in; the durable state a
/// customer's inquiry is made of comes out.
///
/// <para><b>Why it is built this way.</b> Every other test on this path proves one seam
/// against doubles. Three independent reviewers landed on the same gap: nothing runs
/// capture → schedule → queue → worker → barrier against a real database in one pass, so
/// each seam can be individually green while the pipeline as a whole moves no message at
/// all. That is not hypothetical — it is exactly what production did.</para>
///
/// <para><b>What is real here.</b> A migrated PostgreSQL container, the production
/// composition (<see cref="EmailInquiryAssemblyServiceCollectionExtensions.AddEmailInquiryAssembly"/>),
/// the real <see cref="DocumentIngestionService"/>, the real <see cref="ExtractionQueue"/>
/// with its advisory-lock claim, the real <see cref="ProductionDocumentReader"/> reading
/// bytes back out of evidence storage, the real <see cref="ExtractionWorker"/> loop, and
/// the real <see cref="LeadPersister"/>. No recording queue, no recording persister, no
/// seeded result, no SQLite.</para>
///
/// <para><b>What is substituted, and why that is honest.</b> Exactly one boundary: the
/// language model. <see cref="RefusingLlm"/> throws on any call, which is an assertion —
/// the two CSV attachments MUST take the deterministic structured path. The message body
/// is prose and genuinely needs a model, so <see cref="DeterministicBodyExtractor"/> stands
/// in for it. Everything between the mailbox and the Lead is production code.</para>
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
        await SeedTenantAsync();
        var llm = new RefusingLlm();
        await using var services = BuildProductionGraph(llm);

        // ---- 1. CAPTURE: the message becomes durable before anything may touch it. ----
        var message = BuildMessage();
        long assemblyId;
        int expectedComponents;
        EmailScheduleResult schedule;

        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(BusinessUnitId);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == BusinessUnitId);
            var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == MessageId);

            var capture = await scope.ServiceProvider.GetRequiredService<IEmailInquiryCaptureService>()
                .CaptureAsync(message, ingest, configuration, BodyText);

            Assert.NotNull(capture.Assembly);
            Assert.False(capture.AlreadyCaptured);
            Assert.True(capture.SafeToMarkSeen,
                "Capture must be durable before the mailbox is told the message was read.");

            assemblyId = capture.Assembly!.Id;
            expectedComponents = capture.Assembly.ExpectedComponentCount;

            // Body + two CSVs. If the planner ever stops seeing one of them the rest of this
            // test would still pass, so the count is asserted rather than derived.
            Assert.Equal(3, expectedComponents);

            // ---- 2. SCHEDULE: one durable job per processable component. ----
            var components = await context.EmailInquiryComponents
                .Where(c => c.AssemblyId == assemblyId).OrderBy(c => c.Ordinal).ToListAsync();
            var plan = await EmailInquiryManifestPlanner.PlanAsync(message, capture.Assembly.MessageKey, BodyText);

            schedule = await EmailIngestEnqueuer.ScheduleAsync(
                capture.Assembly, components, plan, ingest, "buyer@customer.example",
                scope.ServiceProvider.GetRequiredService<IDocumentIngestion>(),
                new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
                scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>(),
                scope.ServiceProvider.GetRequiredService<ILogger<EmailToLeadVerticalSlicePostgreSqlTests>>());
        }

        Assert.Equal(3, schedule.Scheduled);
        Assert.Equal(0, schedule.Held);
        Assert.True(schedule.FullyScheduled, $"Manifest verdict was {schedule.Verdict}.");

        // ---- 3. DRAIN: the real worker, claiming through the real queue. ----
        await DrainQueueAsync(services);

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
        await DrainQueueAsync(services);
        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(BusinessUnitId);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            Assert.Equal(1, await context.Leads.AsNoTracking()
                .CountAsync(l => l.BusinessUnitId == BusinessUnitId));
        }
    }

    // ------------------------------------------------------------------ the message

    private const string BodyText =
        "Dear Nexora,\n\nPlease quote the attached requirements for our Jubail expansion. "
        + "Delivery to Jubail Industrial City, DDP, quotation validity 30 days.\n\nRegards,\nBuyer";

    private static MimeMessage BuildMessage()
    {
        // Two ordinary column layouts the deterministic normalizer recognizes, so the
        // extraction is reproducible byte-for-byte and needs no model.
        const string valves =
            "Part Number,Description,Quantity,Unit\n"
            + "VLV-1001,Ball valve DN50 PN16 stainless,12,EA\n"
            + "VLV-1002,Gate valve DN80 PN16 carbon steel,4,EA\n";
        const string gaskets =
            "Part Number,Description,Quantity,Unit\n"
            + "GSK-3007,Spiral wound gasket DN50 CL150,60,EA\n"
            + "GSK-3008,Spiral wound gasket DN80 CL150,25,EA\n"
            + "GSK-3009,Ring joint gasket R-24 soft iron,8,EA\n";

        var body = new TextPart("plain") { Text = BodyText };
        var mixed = new Multipart("mixed") { body };
        mixed.Add(CsvAttachment("valves.csv", valves));
        mixed.Add(CsvAttachment("gaskets.csv", gaskets));

        var message = new MimeMessage { Subject = "RFQ 88-2410 Jubail expansion", Body = mixed };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
        message.To.Add(new MailboxAddress("Nexora", "rfq@nexora.example"));
        message.MessageId = MessageId;
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

    // ------------------------------------------------------------------ the graph

    private ServiceProvider BuildProductionGraph(RefusingLlm llm)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddDbContext<ErpRfqAutomationContext>(options =>
            // EnableRetryOnFailure is NOT decoration. It installs NpgsqlRetryingExecutionStrategy,
            // under which a user-initiated BeginTransactionAsync outside
            // CreateExecutionStrategy().ExecuteAsync throws outright. Production configures it;
            // omitting it here would leave this test blind to the single most likely way for the
            // barrier to fail in production while passing locally — the exact substituted-boundary
            // mistake this whole test exists to stop making.
            options.UseNpgsql(_database.ConnectionString,
                    npgsql => npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null))
                .EnableDetailedErrors());

        services.AddSingleton<IWebHostEnvironment>(new TestEnvironment(_storageRoot));
        services.AddSingleton<IFileStorage>(new LocalFileStorage(_storageRoot, _storageRoot));
        services.AddSingleton<IEvidenceObjectStorage>(sp =>
            new LocalEvidenceObjectStorage(sp.GetRequiredService<IFileStorage>()));
        services.AddSingleton<IMalwareScanner, NoThreatScanner>();
        services.AddSingleton<IFileInspectionService>(sp =>
            new DocumentFileInspectionService(
                sp.GetRequiredService<IMalwareScanner>(), new DocumentInspectionOptions()));
        services.AddSingleton<IOptions<MalwareVerdictPolicyOptions>>(
            Options.Create(new MalwareVerdictPolicyOptions()));

        services.AddEmailInquiryAssembly();
        services.AddScoped<IExtractionQueue, ExtractionQueue>();
        services.AddScoped<IDocumentIngestion, DocumentIngestionService>();
        services.AddScoped<IExtractionDocumentReader, ProductionDocumentReader>();
        services.AddScoped<ILeadPersister, LeadPersister>();
        services.AddScoped<IEmailInquiryLeadAssembler, EmailInquiryLeadAssembler>();
        services.AddSingleton<ICanonicalRfqNormalizer, CanonicalRfqNormalizer>();
        services.AddSingleton<ILLMService>(llm);
        services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
        services.AddScoped<IConversationalExtractionService, DeterministicBodyExtractor>();

        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>
    /// Runs the production worker until this tenant's work is genuinely finished.
    ///
    /// <para><b>An empty queue is not the finish line.</b> The worker completes a job and
    /// only THEN assembles the message, so there is a real window where every job reads
    /// Succeeded and the message is still mid-assembly. Waiting on the queue alone made
    /// this test read the database while the assembler was still writing to it. The wait
    /// is therefore on the message's own state, which is what the pipeline is for.</para>
    ///
    /// <para>The worker is a background loop, so the test drives it exactly as the host
    /// does and waits on observable database state rather than on a sleep.</para>
    /// </summary>
    private async Task DrainQueueAsync(ServiceProvider services)
    {
        var worker = new ExtractionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ExtractionWorkerOptions
            {
                WorkerCount = 1,
                MaxConcurrentLlmCalls = 1,
                PerTenantConcurrencyCap = 4,
                LeaseDuration = TimeSpan.FromSeconds(60),
                IdlePollDelay = TimeSpan.FromMilliseconds(25)
            },
            services.GetRequiredService<ILogger<ExtractionWorker>>(),
            services.GetRequiredService<ITenantScopeAccessor>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TestWaits.Liveness;
            while (DateTime.UtcNow < deadline)
            {
                using var scope = services.CreateScope();
                using var tenant = scope.ServiceProvider
                    .GetRequiredService<ITenantScopeAccessor>().Push(BusinessUnitId);
                var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                var open = await context.Set<ExtractionJob>().AsNoTracking().CountAsync(j =>
                    j.BusinessUnitId == BusinessUnitId
                    && j.Status != ExtractionStatus.Succeeded
                    && j.Status != ExtractionStatus.DeadLetter
                    && j.Status != ExtractionStatus.Duplicate
                    && j.Status != ExtractionStatus.Failed);
                var unsettled = await context.EmailInquiryAssemblies.AsNoTracking().CountAsync(a =>
                    a.BusinessUnitId == BusinessUnitId
                    && a.Status != EmailInquiryAssemblyStatus.Assembled
                    && a.Status != EmailInquiryAssemblyStatus.NeedsReview
                    && a.Status != EmailInquiryAssemblyStatus.NoInquiry
                    && a.Status != EmailInquiryAssemblyStatus.RejectedSecurity);

                if (open == 0 && unsettled == 0)
                {
                    // A job that failed or dead-lettered has left the queue too, so "drained"
                    // alone is not success. Surfacing the recorded error here is the difference
                    // between a diagnosable failure and a downstream assertion that says only
                    // that a component never finished.
                    var broken = await context.Set<ExtractionJob>().AsNoTracking()
                        .Where(j => j.BusinessUnitId == BusinessUnitId
                                    && (j.Status == ExtractionStatus.Failed
                                        || j.Status == ExtractionStatus.DeadLetter))
                        .Select(j => new { j.Id, j.FileName, j.Status, j.LastError })
                        .ToListAsync();
                    Assert.True(broken.Count == 0, "Extraction jobs failed: " + string.Join(
                        " | ", broken.Select(b => $"{b.FileName}#{b.Id} {b.Status}: {b.LastError}")));
                    return;
                }
                await Task.Delay(100);
            }

            Assert.Fail("The queue did not drain and the message did not settle within the "
                + "liveness window.");
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
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

    // ------------------------------------------------------------------ tenant seed

    private async Task SeedTenantAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO public."BusinessUnits" ("ID","BusinessUnitCode","BusinessUnitName","IsActive","CreatedBy","CreatedOn")
            VALUES ({BusinessUnitId}, 'SLICE', 'Vertical Slice', true, 'test', now())
            ON CONFLICT DO NOTHING;

            INSERT INTO public."Email_Configurations"
                ("ID","BusinessUnitID","ConfigurationName","EmailAddress","Protocol","Host","Port",
                 "Username","Password","UseSSL","PollingInterval","IsActive","CreatedOn")
            VALUES ({BusinessUnitId}, {BusinessUnitId}, 'Inbound', 'rfq@nexora.example', 'IMAP',
                    'imap.secureserver.net', 993, 'rfq@nexora.example', 'not-a-real-credential',
                    true, 5, true, now())
            ON CONFLICT DO NOTHING;

            INSERT INTO public."EmailIngests"
                ("MessageID","EmailSubject","FromEmail","ToEmail","EmailConfigurationID","CreatedOn")
            VALUES ('{MessageId}', 'RFQ 88-2410 Jubail expansion', 'buyer@customer.example',
                    'rfq@nexora.example', {BusinessUnitId}, now())
            ON CONFLICT DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------ the two doubles

    /// <summary>
    /// Any call is a test failure by construction: the CSV attachments must take the
    /// deterministic path. This is an assertion wearing a stub's clothes.
    /// </summary>
    private sealed class RefusingLlm : ILLMService
    {
        private int _calls;
        public int CallCount => Volatile.Read(ref _calls);
        public AiProviderClass ProviderClass => AiProviderClass.Local;

        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException(
                "The structured extraction path reached the language model. "
                + "A recognized CSV layout must never require one.");
        }

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("The slice must not draft a BOQ.");
        }
    }

    /// <summary>
    /// Stands in for the model on the ONE component that genuinely needs prose
    /// understanding: the sender's covering note. It returns a header-only result, which is
    /// what a covering note legitimately contributes — the priced lines come from the
    /// attachments.
    /// </summary>
    private sealed class DeterministicBodyExtractor : IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
            => Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                Result = Ext.Result([], 0.95),
                ExpectedItemCount = 0,
                ExtractedItemCount = 0,
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
    }

    private sealed class NoThreatScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken ct = default)
            => Task.FromResult(MalwareScanResult.Clean("test-no-threat"));
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
