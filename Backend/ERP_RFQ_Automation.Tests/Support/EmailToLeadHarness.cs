using System.Text;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using MimeKit;
using Npgsql;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// ONE definition of the email→Lead production graph, shared by every PostgreSQL test that
/// drives the real pipeline.
///
/// <para>It is shared rather than copied on purpose. The single most expensive defect this
/// module has produced was a test that differed from production in exactly the dimension under
/// test — the retry policy — and a second copy of this composition is how that happens again
/// without anyone editing the copy that matters.</para>
///
/// <para><b>What is real:</b> the migrated database, the queue and its advisory-lock claim, the
/// ingestion gateway, the document reader against real evidence storage, the persister, the
/// coordinator, the assembler and the recovery sweep. <b>What is substituted:</b> the language
/// model, and only the language model — <see cref="RefusingLlm"/> throws on any call, so a test
/// that reaches it fails rather than silently becoming non-deterministic.</para>
/// </summary>
public static class EmailToLeadHarness
{
    public const string BodyText =
        "Dear Nexora,\n\nPlease quote the attached requirements for our Jubail expansion. "
        + "Delivery to Jubail Industrial City, DDP, quotation validity 30 days.\n\nRegards,\nBuyer";

    /// <summary>
    /// Builds the graph.
    /// </summary>
    /// <param name="configure">
    /// Applied LAST, so a test can replace exactly one registration — the assembler, to control
    /// the instant of a simulated crash. It cannot fabricate the persisted outcome: everything
    /// that writes to the database stays real.
    /// </param>
    /// <param name="withRlsInterceptor">
    /// Registers <see cref="TenantRlsCommandInterceptor"/>, which is what makes the connection
    /// switch to <c>nexora_pipeline_app</c> or <c>nexora_tenant_app</c> per statement.
    ///
    /// <para>Off by default because the shared container connects as a SUPERUSER, and a
    /// superuser bypasses every policy unconditionally — the interceptor would run but prove
    /// nothing. It is switched on by the production-role lane, which owns a container with the
    /// real role topology.</para>
    /// </param>
    public static ServiceProvider BuildGraph(
        string connectionString,
        string storageRoot,
        RefusingLlm llm,
        Action<IServiceCollection>? configure = null,
        bool withRlsInterceptor = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddDbContext<ErpRfqAutomationContext>((sp, options) =>
            // EnableRetryOnFailure is NOT decoration. It installs NpgsqlRetryingExecutionStrategy,
            // under which a user-initiated BeginTransactionAsync outside
            // CreateExecutionStrategy().ExecuteAsync throws outright. Production configures it,
            // and omitting it here once left every one of these tests blind to a defect that
            // would have thrown on the first real message.
            {
                options.UseNpgsql(connectionString,
                        npgsql => npgsql.EnableRetryOnFailure(
                            maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10),
                            errorCodesToAdd: null))
                    .EnableDetailedErrors();
                if (withRlsInterceptor)
                    // The three-argument constructor, deliberately. With a null
                    // IHttpContextAccessor the interceptor cannot distinguish "background work,
                    // no tenant" from "no role switch at all" and leaves the connection on the
                    // login role — so the unscoped enumeration would never reach
                    // nexora_pipeline_app and the lane would prove the wrong thing.
                    options.AddInterceptors(new TenantRlsCommandInterceptor(
                        sp.GetRequiredService<ITenantContext>(),
                        sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
                        new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
            });

        services.AddSingleton<IWebHostEnvironment>(new TestEnvironment(storageRoot));
        services.AddSingleton<IFileStorage>(new LocalFileStorage(storageRoot, storageRoot));
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
        services.AddScoped<IEmailInquiryIntakeService, EmailInquiryIntakeService>();
        services.AddSingleton<ICanonicalRfqNormalizer, CanonicalRfqNormalizer>();
        services.AddSingleton<ILLMService>(llm);
        services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
        services.AddScoped<IConversationalExtractionService, DeterministicBodyExtractor>();

        // REGISTERED, not omitted — and its absence was itself a defect in this harness.
        //
        // LeadPersister takes UsageMeteringService as an OPTIONAL constructor argument, so a graph
        // that leaves it out silently skips the whole metering block inside the persist
        // transaction. Production registers it (BillingServiceExtensions), which means every test
        // built on this harness was exercising a persist path production does not have: no
        // platform."Tenants" read, no platform."UsageEvents" write. That is precisely why the
        // production-role lane could not see that the metering block runs as nexora_tenant_app,
        // which has no grant on either table.
        //
        // Metering still does nothing unless a platform."Tenants" row names this business unit as
        // its PrimaryBusinessUnitId, so tests that seed only the tenant plane are unaffected.
        services.AddScoped<ERP_RFQ_Automation.Billing.Metering.UsageMeteringService>();

        // MinimumAge zero: the guard exists so the sweep does not queue behind healthy in-flight
        // work in production, and a test that waited a minute for it would be proving the clock.
        //
        // StrandedComponentSweepMinutes zero for the same reason, and it is SAFE rather than
        // merely convenient: the age decides only which components are looked at, and what
        // happens to one is decided by the durable state of its job. A component queued a
        // microsecond ago has a Pending job with every attempt left, which the sweep leaves
        // strictly alone — so this cannot make a test pass that production would fail.
        services.AddSingleton(new EmailInquiryAssemblyRecoveryOptions
        {
            Interval = TimeSpan.FromSeconds(30),
            BatchSizePerTenant = 50,
            MinimumAge = TimeSpan.Zero,
            StrandedComponentSweepMinutes = 0
        });
        services.AddScoped<IEmailInquiryAssemblyRecoveryService, EmailInquiryAssemblyRecoveryService>();
        // The sweep now REQUIRES the gate, so it must be present. This one admits everyone;
        // RefusingWorkGate is substituted by the test that proves the sweep honours a refusal,
        // which is the only property of the gate this suite can assert without standing up the
        // whole platform-access plane.
        services.AddScoped<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate, AdmitAllWorkGate>();
        services.AddSingleton<IBackgroundWorkerHeartbeats, BackgroundWorkerHeartbeats>();

        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>A covering note and two priced schedules, in two ordinary column layouts the
    /// deterministic normalizer recognizes — so extraction is reproducible byte-for-byte and
    /// needs no model.</summary>
    /// <param name="extraParts">
    /// Appended after the two priced schedules, for a test whose subject is what an ADDITIONAL
    /// part does to an otherwise perfect message — a terms-and-conditions PDF, a signature image.
    /// The three parts above are unchanged, so every existing assertion about them still holds.
    /// </param>
    public static MimeMessage BuildMessage(
        string messageId,
        string subject = "RFQ 88-2410 Jubail expansion",
        params MimeEntity[] extraParts)
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

        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = BodyText } };
        mixed.Add(CsvAttachment("valves.csv", valves));
        mixed.Add(CsvAttachment("gaskets.csv", gaskets));
        foreach (var part in extraParts) mixed.Add(part);

        var message = new MimeMessage { Subject = subject, Body = mixed };
        message.From.Add(new MailboxAddress("Buyer", "buyer@customer.example"));
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

    /// <summary>Seeds the real parent chain: business unit → mailbox → ingest.</summary>
    public static async Task SeedTenantAsync(
        NpgsqlConnection connection, long businessUnitId, params string[] messageIds)
    {
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = $"""
                INSERT INTO public."BusinessUnits"
                    ("ID","BusinessUnitCode","BusinessUnitName","IsActive","CreatedBy","CreatedOn")
                VALUES ({businessUnitId}, 'SLICE{businessUnitId}', 'Slice {businessUnitId}', true, 'test', now())
                ON CONFLICT DO NOTHING;

                INSERT INTO public."Email_Configurations"
                    ("ID","BusinessUnitID","ConfigurationName","EmailAddress","Protocol","Host","Port",
                     "Username","Password","UseSSL","PollingInterval","IsActive","CreatedOn")
                VALUES ({businessUnitId}, {businessUnitId}, 'Inbound', 'rfq@nexora.example', 'IMAP',
                        'imap.secureserver.net', 993, 'rfq@nexora.example', 'not-a-real-credential',
                        true, 5, true, now())
                ON CONFLICT DO NOTHING;
                """;
            await seed.ExecuteNonQueryAsync();
        }

        foreach (var messageId in messageIds)
        {
            await using var ingest = connection.CreateCommand();
            ingest.CommandText = $"""
                INSERT INTO public."EmailIngests"
                    ("MessageID","EmailSubject","FromEmail","ToEmail","EmailConfigurationID","CreatedOn")
                VALUES ('{messageId}', 'RFQ 88-2410 Jubail expansion', 'buyer@customer.example',
                        'rfq@nexora.example', {businessUnitId}, now())
                ON CONFLICT DO NOTHING;
                """;
            await ingest.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Captures the message and schedules every processable component through the real
    /// ingestion gateway. Returns the assembly id and the derived batch id.
    /// </summary>
    /// <summary>
    /// Captures and schedules, asserting the capture-level properties EVERY caller depends on.
    ///
    /// <para>These assertions live here rather than in one test because extracting this method
    /// silently dropped them, and the most valuable of them —
    /// <c>SafeToMarkSeen</c> — is the "do not tell IMAP the message was read before it is
    /// durable" property, whose failure loses a customer's email irrecoverably.</para>
    /// </summary>
    public static async Task<(EmailInquiryCaptureResult Capture, long AssemblyId, EmailScheduleResult Schedule)>
        CaptureAndScheduleAsync(
            ServiceProvider services, long businessUnitId, MimeMessage message,
            int expectedComponentCount = 3)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == businessUnitId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);

        var capture = await scope.ServiceProvider.GetRequiredService<IEmailInquiryCaptureService>()
            .CaptureAsync(message, ingest, configuration, BodyText);

        Assert.NotNull(capture.Assembly);
        Assert.False(capture.AlreadyCaptured,
            "A fresh message must not resolve to an existing assembly.");
        Assert.True(capture.SafeToMarkSeen,
            "Capture must be durable before the mailbox is told the message was read.");

        var assembly = capture.Assembly!;

        // Asserted, not derived: if the planner ever stops seeing one of the parts, every
        // downstream assertion about "every component" would still pass on the smaller set.
        Assert.Equal(expectedComponentCount, assembly.ExpectedComponentCount);

        var components = await context.EmailInquiryComponents
            .Where(c => c.AssemblyId == assembly.Id).OrderBy(c => c.Ordinal).ToListAsync();
        var plan = await EmailInquiryManifestPlanner.PlanAsync(message, assembly.MessageKey, BodyText);

        var schedule = await EmailIngestEnqueuer.ScheduleAsync(
            assembly, components, plan, ingest, "buyer@customer.example",
            scope.ServiceProvider.GetRequiredService<IDocumentIngestion>(),
            new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
            scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>());

        return (capture, assembly.Id, schedule);
    }

    /// <summary>
    /// Runs the real worker until this tenant's work is finished.
    ///
    /// <para><b>An empty queue is not the finish line.</b> The worker completes a job and only
    /// THEN assembles the message, so there is a real window in which every job reads Succeeded
    /// and the message is still mid-assembly — waiting on the queue alone reads the database
    /// while the assembler is still writing to it.</para>
    ///
    /// <para><paramref name="waitForAssemblySettlement"/> is false for exactly one caller: the
    /// recovery test, whose whole subject is the state that exists inside that window.</para>
    ///
    /// <para><b>DRAIN ONE TENANT COMPLETELY BEFORE THE NEXT ONE HAS ANY JOBS.</b> The worker this
    /// starts covers the WHOLE queue, but the wait watches only <paramref name="businessUnitId"/>
    /// — so a test that captures two tenants and then drains twice lets the first call stop a
    /// worker that is mid-flight on the second tenant's job. A stopped worker abandons its lease
    /// by design (ExtractionWorker: "Leave the lease to expire; another worker reclaims it after
    /// shutdown"), and the claim SQL will not reclaim a Leased row until <c>LeaseExpiresAt</c>
    /// passes — 60 seconds here, which is exactly <see cref="TestWaits.Liveness"/>. The second
    /// drain then burns its entire window unable to claim the row and fails with every job
    /// reading terminal, because the lease expired and the job finished while the failure message
    /// was being built. It passes on a fast machine and fails on a loaded CI runner. Interleave
    /// capture and drain per tenant instead.</para>
    /// </summary>
    public static async Task DrainQueueAsync(
        ServiceProvider services,
        long businessUnitId,
        bool assertNoFailures = true,
        bool waitForAssemblySettlement = true)
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
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>(),
            services.GetRequiredService<ITenantScopeAccessor>());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTime.UtcNow + TestWaits.Liveness;
            while (DateTime.UtcNow < deadline)
            {
                using var scope = services.CreateScope();
                using var tenant = scope.ServiceProvider
                    .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
                var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                var open = await context.Set<ExtractionJob>().AsNoTracking().CountAsync(j =>
                    j.BusinessUnitId == businessUnitId
                    && j.Status != ExtractionStatus.Succeeded
                    && j.Status != ExtractionStatus.DeadLetter
                    && j.Status != ExtractionStatus.Duplicate
                    && j.Status != ExtractionStatus.Failed);
                var unsettled = !waitForAssemblySettlement ? 0
                    : await context.EmailInquiryAssemblies.AsNoTracking().CountAsync(a =>
                        a.BusinessUnitId == businessUnitId
                        && a.Status != EmailInquiryAssemblyStatus.Assembled
                        && a.Status != EmailInquiryAssemblyStatus.NeedsReview
                        && a.Status != EmailInquiryAssemblyStatus.NoInquiry
                        && a.Status != EmailInquiryAssemblyStatus.RejectedSecurity);

                if (open == 0 && unsettled == 0)
                {
                    if (assertNoFailures)
                    {
                        var broken = await context.Set<ExtractionJob>().AsNoTracking()
                            .Where(j => j.BusinessUnitId == businessUnitId
                                        && (j.Status == ExtractionStatus.Failed
                                            || j.Status == ExtractionStatus.DeadLetter))
                            .Select(j => new { j.Id, j.FileName, j.Status, j.LastError })
                            .ToListAsync();
                        Assert.True(broken.Count == 0, "Extraction jobs failed: " + string.Join(
                            " | ", broken.Select(b => $"{b.FileName}#{b.Id} {b.Status}: {b.LastError}")));
                    }
                    return;
                }
                await Task.Delay(100);
            }

            // The state is reported, not just the timeout. A job that keeps failing and being
            // re-leased never reaches a terminal status, so the loop above simply runs out — and
            // "did not drain" on its own says nothing about WHY, which cost real time on the
            // metering-role defect.
            using (var scope = services.CreateScope())
            {
                using var tenant = scope.ServiceProvider
                    .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
                var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
                var stuck = await context.Set<ExtractionJob>().AsNoTracking()
                    .Where(j => j.BusinessUnitId == businessUnitId)
                    .Select(j => new { j.Id, j.FileName, j.Status, j.Attempts, j.LastError })
                    .ToListAsync();
                Assert.Fail("The queue did not drain and the message did not settle within the "
                    + "liveness window. Jobs: " + string.Join(" | ", stuck.Select(
                        j => $"{j.FileName}#{j.Id} {j.Status} attempts={j.Attempts}: {j.LastError ?? "<none>"}")));
            }
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    /// <summary>
    /// Any call is a test failure by construction: the CSV attachments must take the
    /// deterministic path. An assertion wearing a stub's clothes.
    /// </summary>
    public sealed class RefusingLlm : ILLMService
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
    /// Stands in for the model on the ONE component that genuinely needs prose understanding:
    /// the sender's covering note. Header-only, which is what a covering note legitimately
    /// contributes — the priced lines come from the attachments. Counts its calls so a test can
    /// prove recovery re-ran no extraction.
    /// </summary>
    public sealed class DeterministicBodyExtractor : IConversationalExtractionService
    {
        private static int _calls;
        public static int CallCount => Volatile.Read(ref _calls);
        public static void ResetCallCount() => Interlocked.Exchange(ref _calls, 0);

        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                Result = Ext.Result([], 0.95),
                ExpectedItemCount = 0,
                ExtractedItemCount = 0,
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
        }
    }

    /// <summary>Admits every tenant. The sweep requires a gate; this is the neutral one.</summary>
    public sealed class AdmitAllWorkGate : ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate
    {
        public Task<bool> MayConsumeResourcesAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<IReadOnlyList<long>> FilterServiceableAsync(
            IEnumerable<long> businessUnitIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<long>>(businessUnitIds.ToList());
    }

    /// <summary>Refuses the named tenants, as a suspended or archived tenant would be refused.</summary>
    public sealed class RefusingWorkGate(params long[] refused)
        : ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate
    {
        public Task<bool> MayConsumeResourcesAsync(long businessUnitId, CancellationToken ct = default)
            => Task.FromResult(!refused.Contains(businessUnitId));

        public Task<IReadOnlyList<long>> FilterServiceableAsync(
            IEnumerable<long> businessUnitIds, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<long>>(
                businessUnitIds.Where(id => !refused.Contains(id)).ToList());
    }

    /// <summary>
    /// Persists the Lead exactly as production does, then throws — simulating a process that
    /// dies INSIDE the assembler's transaction, after the Lead row is written and before the
    /// Assembled transition commits.
    /// </summary>
    public sealed class ThrowAfterPersistingLeadPersister(ILeadPersister inner) : ILeadPersister
    {
        public Task<long> PersistAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
            => inner.PersistAsync(job, outcome, ct);

        public Task<long?> PersistAndCompleteAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, IExtractionQueue queue,
            string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => inner.PersistAndCompleteAsync(
                job, outcome, queue, workerId, leaseAttempt, leaseDuration, ct);

        public async Task<long> PersistAssembledMessageAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
        {
            await inner.PersistAssembledMessageAsync(job, outcome, ct);
            throw new InvalidOperationException(
                "Simulated process loss inside the assembler transaction, after the lead was written.");
        }

        public Task EnrichAssembledMessageAsync(
            ExtractionJob job, long leadId, CancellationToken ct = default)
            => inner.EnrichAssembledMessageAsync(job, leadId, ct);
    }

    public sealed class NoThreatScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken ct = default)
            => Task.FromResult(MalwareScanResult.Clean("test-no-threat"));
    }

    public sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
