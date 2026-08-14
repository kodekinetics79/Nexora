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
    public static ServiceProvider BuildGraph(
        string connectionString,
        string storageRoot,
        RefusingLlm llm,
        Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped<ITenantContext, HttpTenantContext>();
        services.AddDbContext<ErpRfqAutomationContext>(options =>
            // EnableRetryOnFailure is NOT decoration. It installs NpgsqlRetryingExecutionStrategy,
            // under which a user-initiated BeginTransactionAsync outside
            // CreateExecutionStrategy().ExecuteAsync throws outright. Production configures it,
            // and omitting it here once left every one of these tests blind to a defect that
            // would have thrown on the first real message.
            options.UseNpgsql(connectionString,
                    npgsql => npgsql.EnableRetryOnFailure(
                        maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null))
                .EnableDetailedErrors());

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
        services.AddSingleton<ICanonicalRfqNormalizer, CanonicalRfqNormalizer>();
        services.AddSingleton<ILLMService>(llm);
        services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
        services.AddScoped<IConversationalExtractionService, DeterministicBodyExtractor>();

        // MinimumAge zero: the guard exists so the sweep does not queue behind healthy in-flight
        // work in production, and a test that waited a minute for it would be proving the clock.
        services.AddSingleton(new EmailInquiryAssemblyRecoveryOptions
        {
            Interval = TimeSpan.FromSeconds(30),
            BatchSizePerTenant = 50,
            MinimumAge = TimeSpan.Zero
        });
        services.AddScoped<IEmailInquiryAssemblyRecoveryService, EmailInquiryAssemblyRecoveryService>();
        services.AddSingleton<IBackgroundWorkerHeartbeats, BackgroundWorkerHeartbeats>();

        configure?.Invoke(services);
        return services.BuildServiceProvider(validateScopes: true);
    }

    /// <summary>A covering note and two priced schedules, in two ordinary column layouts the
    /// deterministic normalizer recognizes — so extraction is reproducible byte-for-byte and
    /// needs no model.</summary>
    public static MimeMessage BuildMessage(string messageId, string subject = "RFQ 88-2410 Jubail expansion")
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
    public static async Task<(long AssemblyId, EmailScheduleResult Schedule)> CaptureAndScheduleAsync(
        ServiceProvider services, long businessUnitId, MimeMessage message)
    {
        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var configuration = await context.EmailConfigurations.SingleAsync(c => c.Id == businessUnitId);
        var ingest = await context.EmailIngests.SingleAsync(i => i.MessageId == message.MessageId);

        var capture = await scope.ServiceProvider.GetRequiredService<IEmailInquiryCaptureService>()
            .CaptureAsync(message, ingest, configuration, BodyText);

        var assembly = capture.Assembly!;
        var components = await context.EmailInquiryComponents
            .Where(c => c.AssemblyId == assembly.Id).OrderBy(c => c.Ordinal).ToListAsync();
        var plan = await EmailInquiryManifestPlanner.PlanAsync(message, assembly.MessageKey, BodyText);

        var schedule = await EmailIngestEnqueuer.ScheduleAsync(
            assembly, components, plan, ingest, "buyer@customer.example",
            scope.ServiceProvider.GetRequiredService<IDocumentIngestion>(),
            new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
            scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>(),
            scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>());

        return (assembly.Id, schedule);
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

            Assert.Fail("The queue did not drain and the message did not settle within the "
                + "liveness window.");
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
