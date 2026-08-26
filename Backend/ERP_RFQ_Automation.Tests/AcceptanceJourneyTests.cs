using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE END-TO-END ACCEPTANCE JOURNEYS THE EXECUTABLE SPECIFICATION WAS MISSING.
///
/// Until this file, no test anywhere proved the pipeline READS: every "success path" test
/// pointed MailKit at 127.0.0.1:1 (so only failure paths ran) and wrote the EmailIngest
/// ledger by hand. Here the REAL service graph runs end to end: the real
/// <see cref="EmailService"/> dials a live loopback IMAP server (<see cref="FakeImapServer"/>)
/// through the Development-only <see cref="MailEndpointPolicy"/> allowance, downloads REAL
/// corpus bytes, triages them with the real gate, fans out through the real
/// <see cref="DocumentIngestionService"/> (real inspection, real evidence zones) into the
/// real PostgreSQL-backed <see cref="ExtractionQueue"/>, and the real
/// <see cref="ExtractionWorker"/> + <see cref="LeadPersister"/> +
/// <see cref="LeadIdentityApplicationService"/> produce the Lead. Only two things are
/// doubles: the LLM (scripted from <c>Corpus/corpus-manifest.json</c>) and the malware
/// scanner (always clean).
///
/// PostgreSQL because the claim path (<c>FOR UPDATE SKIP LOCKED</c> + the intake-guard
/// trigger) is production-dialect-only; each test gets its own migrated database on the
/// shared container, per the established pattern.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class AcceptanceJourneyTests : IAsyncLifetime
{
    private const long Tenant = 940_777;
    private static readonly DateTime MailboxBaseline = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly PostgreSqlTestDatabase _server;
    private string _databaseName = null!;
    private string _root = null!;
    private DbContextOptions<ErpRfqAutomationContext> _dbOptions = null!;
    private ServiceProvider _provider = null!;
    private ILLMService _llm = new CorpusManifestLlm();

    public AcceptanceJourneyTests(PostgreSqlTestDatabase server) => _server = server;

    public async Task InitializeAsync()
    {
        _databaseName = $"nexora_journey_{Guid.NewGuid():N}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{_databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(_server.ConnectionString)
        {
            Database = _databaseName
        }.ConnectionString;
        _dbOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .Options;
        await using (var context = ContextFor(null))
        {
            await context.Database.MigrateAsync();
        }

        _root = Path.Combine(Path.GetTempPath(), "nexora-journey", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _provider = BuildServiceGraph();

        // The Development-only allowance: the environment is a PARAMETER to the call, so this
        // is the same grant Program.cs performs on a Development host — nothing is bypassed.
        Assert.True(MailEndpointPolicy.EnableLoopbackForLocalDevelopment(
            isDevelopmentEnvironment: true, requested: true));
    }

    public async Task DisposeAsync()
    {
        MailEndpointPolicy.ResetLoopbackAllowance();
        if (_provider is not null) await _provider.DisposeAsync();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
    }

    // =====================================================================================
    // 1. Mailbox → capture → triage → inspect → extract → lead, end to end.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_rfq_email_with_a_pdf_attachment_travels_the_whole_pipeline_to_a_reviewable_lead()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        var uid = imap.AddMessage(CorpusManifest.Bytes("email-with-pdf.eml"));
        await SeedMailboxAsync(imap);

        var report = await PollAsync();
        Assert.True(report.AllSucceeded, report.FailureSummary);
        Assert.Equal(1, report.Mailboxes.Single().MessagesDownloaded);

        // Durable capture happened BEFORE any downstream verdict, and \Seen only after it.
        await using (var ctx = ContextFor(null))
        {
            var ingest = await ctx.EmailIngests.AsNoTracking().SingleAsync();
            Assert.Equal("corpus-with-pdf@corpus.nexora.example", ingest.MessageId);
            Assert.Equal("Inquiry", ingest.TriageOutcome);
            Assert.Equal("Queued", ingest.ParseStatus);
            Assert.True(File.Exists(ingest.RawEmailPath), "raw .eml must be retained");
        }
        Assert.Contains(uid, imap.SeenUids);

        // The real worker drains the real queue.
        var worker = StartWorker();
        try
        {
            var lead = await WaitForLeadAsync(l => l.Rfqno == "RFQ-CORPUS-003");

            var script = CorpusManifest.Load().Llm.Single(s =>
                s.Markers.Count == 1 && s.Markers[0] == "RFQ-CORPUS-003");
            await using var ctx = ContextFor(null);
            var persisted = await ctx.Leads.AsNoTracking()
                .Include(l => l.LeadItems)
                .SingleAsync(l => l.Id == lead.Id);
            Assert.Equal(script.BuyerName, persisted.BuyersName);
            Assert.Equal("Email", persisted.LeadSource);
            Assert.Equal(script.Lines.Count, persisted.LeadItems.Count);
            foreach (var line in script.Lines)
            {
                var item = persisted.LeadItems.Single(i => i.ManufacturerPartNumber == line.PartNumber);
                Assert.Equal(line.Quantity, item.Quantity);
            }

            // The message's lifecycle status resolved to the reviewable state, not "Queued".
            var ingest = await ctx.EmailIngests.AsNoTracking().SingleAsync();
            Assert.Equal("NeedsReview", ingest.ParseStatus);
            Assert.Equal(ingest.Id, persisted.EmailIngestsId);

            // Evidence of the inspection journey: quarantine + cleared zones both hold bytes.
            var job = await ctx.Set<ExtractionJob>().AsNoTracking().SingleAsync();
            Assert.Equal(ExtractionStatus.Succeeded, job.Status);
            Assert.Contains("cleared", job.StoragePath);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    // =====================================================================================
    // 2. Byte-identical duplicate delivery → exactly one ingest, one lead.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_same_message_delivered_twice_produces_exactly_one_ingest_and_one_lead()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        var bytes = CorpusManifest.Bytes("email-duplicate.eml");
        imap.AddMessage(bytes);
        imap.AddMessage(bytes); // byte-identical second delivery, distinct UID
        await SeedMailboxAsync(imap);

        var report = await PollAsync();
        Assert.True(report.AllSucceeded, report.FailureSummary);

        var worker = StartWorker();
        try
        {
            await WaitForLeadAsync(l => l.Rfqno == "RFQ-CORPUS-004");
        }
        finally
        {
            await StopWorkerAsync(worker);
        }

        // A second poll cycle sees the ledger, not the \Seen flag, and downloads nothing.
        var second = await PollAsync();
        Assert.True(second.AllSucceeded, second.FailureSummary);
        Assert.Equal(0, second.Mailboxes.Single().MessagesDownloaded);
        Assert.Equal(2, second.Mailboxes.Single().MessagesAlreadyIngested);

        await using var ctx = ContextFor(null);
        Assert.Equal(1, await ctx.EmailIngests.CountAsync());
        Assert.Equal(1, await ctx.Leads.CountAsync());
        Assert.Equal(1, await ctx.Set<ExtractionJob>().CountAsync());
    }

    // =====================================================================================
    // 3. Revised RFQ (same reference, changed quantity) → revision N+1, not a new lead.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_revised_rfq_becomes_revision_two_of_the_canonical_lead_not_a_second_lead()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        imap.AddMessage(CorpusManifest.Bytes("email-simple-english.eml"));
        await SeedMailboxAsync(imap);
        Assert.True((await PollAsync()).AllSucceeded);

        var worker = StartWorker();
        try
        {
            var original = await WaitForLeadAsync(l => l.Rfqno == "RFQ-CORPUS-001");
            Assert.Equal(1, original.CurrentRevisionNumber);

            // The amendment arrives, threaded onto the original conversation.
            imap.AddMessage(CorpusManifest.Bytes("email-revised-rfq.eml"));
            Assert.True((await PollAsync()).AllSucceeded);

            await WaitUntilAsync(async () =>
            {
                await using var ctx = ContextFor(null);
                var lead = await ctx.Leads.AsNoTracking().SingleOrDefaultAsync(l => l.Id == original.Id);
                var amendmentJob = await ctx.Set<ExtractionJob>().AsNoTracking()
                    .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
                if (amendmentJob is not null && !string.IsNullOrWhiteSpace(amendmentJob.LastError))
                    throw new InvalidOperationException(
                        $"The amendment extraction failed in {amendmentJob.Status}: {amendmentJob.LastError}");
                return lead?.CurrentRevisionNumber == 2;
            }, "the canonical lead never reached revision 2");

            await using (var ctx = ContextFor(null))
            {
                // One canonical lead — the amendment did NOT mint a second one.
                Assert.Equal(1, await ctx.Leads.CountAsync());
                var lead = await ctx.Leads.AsNoTracking()
                    .Include(l => l.LeadItems)
                    .SingleAsync(l => l.Id == original.Id);
                Assert.Equal(2, lead.CurrentRevisionNumber);
                // The current projection carries the AMENDED quantity...
                Assert.Equal(60, Assert.Single(lead.LeadItems).Quantity);
                // ...and both revisions exist as durable evidence.
                Assert.Equal(2, await ctx.Set<LeadRevision>().CountAsync(r => r.LeadId == lead.Id));

                // Both messages were ingested in their own right.
                Assert.Equal(2, await ctx.EmailIngests.CountAsync());
            }
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_quoted_only_thread_reply_is_retained_without_a_phantom_revision()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        imap.AddMessage(CorpusManifest.Bytes("email-simple-english.eml"));
        await SeedMailboxAsync(imap);
        Assert.True((await PollAsync()).AllSucceeded);

        var worker = StartWorker();
        try
        {
            var original = await WaitForLeadAsync(l => l.Rfqno == "RFQ-CORPUS-001");
            imap.AddMessage(BuildQuotedOnlyReply());
            Assert.True((await PollAsync()).AllSucceeded);
            await WaitUntilAsync(async () =>
            {
                await using var ctx = ContextFor(null);
                var reply = await ctx.EmailIngests.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.MessageId == "corpus-acknowledgement@corpus.nexora.example");
                return reply is not null && !string.Equals(reply.ParseStatus, "Queued", StringComparison.OrdinalIgnoreCase);
            }, "the quoted-only reply never reached a terminal intake state");

            await using var ctx = ContextFor(null);
            var lead = await ctx.Leads.AsNoTracking().SingleAsync(x => x.Id == original.Id);
            Assert.Equal(1, await ctx.Leads.CountAsync());
            Assert.Equal(1, lead.CurrentRevisionNumber);
            Assert.Equal(1, await ctx.Set<LeadRevision>().CountAsync(x => x.LeadId == lead.Id));
            var reply = await ctx.EmailIngests.AsNoTracking()
                .SingleAsync(x => x.MessageId == "corpus-acknowledgement@corpus.nexora.example");
            Assert.Equal("corpus-simple-en@corpus.nexora.example", reply.InReplyToMessageId);
            Assert.Contains("corpus-simple-en@corpus.nexora.example", reply.ReferencesJson);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    private static byte[] BuildQuotedOnlyReply()
    {
        const string raw = """
            From: Ahmed Al Farsi <ahmed@alnoortrading.ae>
            Date: Mon, 10 Aug 2026 11:00:00 +0400
            Subject: RE: RFQ RFQ-CORPUS-001 - cable tray
            Message-Id: <corpus-acknowledgement@corpus.nexora.example>
            To: Intake <intake@tenant.example>
            In-Reply-To: <corpus-simple-en@corpus.nexora.example>
            References: <corpus-simple-en@corpus.nexora.example>
            MIME-Version: 1.0
            Content-Type: text/plain; charset=utf-8

            Thank you, received.

            On Mon, 10 Aug 2026, Ahmed Al Farsi wrote:
            > Dear team,
            > Please quote 40 nos cable tray 300mm hot dip galvanized (part CT-300-HDG), delivery Jebel Ali by 15 September 2026.
            > Reference: RFQ-CORPUS-001
            """;
        return System.Text.Encoding.UTF8.GetBytes(raw.Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    // =====================================================================================
    // 4. Noise → REJECTED with reason codes, raw .eml retained, no extraction work.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Noise_messages_are_rejected_with_reason_codes_and_retained_but_never_enqueued()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        var noise = new[]
        {
            "email-noise-newsletter.eml", "email-noise-auto-reply.eml", "email-noise-no-reply.eml"
        };
        foreach (var file in noise) imap.AddMessage(CorpusManifest.Bytes(file));
        await SeedMailboxAsync(imap);

        var report = await PollAsync();
        Assert.True(report.AllSucceeded, report.FailureSummary);
        Assert.Equal(3, report.Mailboxes.Single().MessagesDownloaded);

        await using var ctx = ContextFor(null);
        foreach (var file in noise)
        {
            var expected = CorpusManifest.Load().Email(file);
            var ingest = await ctx.EmailIngests.AsNoTracking()
                .SingleAsync(i => i.MessageId == expected.MessageId);
            Assert.Equal("Noise", ingest.TriageOutcome);
            Assert.Equal("Rejected", ingest.ParseStatus);
            foreach (var reason in expected.ExpectedTriageReasons)
                Assert.Contains(reason, ingest.TriageReasonJson ?? "");
            Assert.True(File.Exists(ingest.RawEmailPath),
                $"{file}: the raw message must be retained for replay even when rejected");
        }

        // No extraction job, no evidence-store occurrence — the gate refused BEFORE spend.
        Assert.Equal(0, await ctx.Set<ExtractionJob>().CountAsync());
        Assert.Equal(0, await ctx
            .Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>()
            .CountAsync());
        // And the mailbox copies were still flagged \Seen: a durable verdict exists for each.
        Assert.Equal(3, imap.SeenUids.Distinct().Count());
    }

    // =====================================================================================
    // 5. Password-protected PDF → terminal per-attachment disposition. The readable half is
    //    durably extracted and the batch is NOT sunk, but the message is held for a human
    //    instead of auto-assembling a Lead: an attachment nobody could open is sometimes a
    //    logo and sometimes the bill of quantities, and only a person can tell.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_password_protected_attachment_dead_letters_truthfully_without_sinking_its_batch()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        imap.AddMessage(CorpusManifest.Bytes("email-mixed-protected.eml"));
        await SeedMailboxAsync(imap);
        Assert.True((await PollAsync()).AllSucceeded);

        var worker = StartWorker();
        try
        {
            // The message settles into human review rather than minting a Lead, because one
            // sender-attached part went unread (EmailInquiryAssemblyStateMachine.Evaluate:
            // unread > 0 outranks captured > 0).
            await WaitUntilAsync(async () =>
            {
                await using var ctx = ContextFor(null);
                return await ctx.Set<EmailInquiryAssembly>().AnyAsync(a =>
                    a.Status == EmailInquiryAssemblyStatus.NeedsReview && a.AssembledLeadId == null);
            }, "the message never reached NeedsReview");
            // …and the protected attachment reaches its terminal disposition.
            await WaitUntilAsync(async () =>
            {
                await using var ctx = ContextFor(null);
                return await ctx.Set<ExtractionJob>()
                    .AnyAsync(j => j.Status == ExtractionStatus.DeadLetter);
            }, "the password-protected job never dead-lettered");
        }
        finally
        {
            await StopWorkerAsync(worker);
        }

        await using (var ctx = ContextFor(null))
        {
            var jobs = await ctx.Set<ExtractionJob>().AsNoTracking().ToListAsync();
            Assert.Equal(2, jobs.Count);

            var dead = jobs.Single(j => j.Status == ExtractionStatus.DeadLetter);
            Assert.Contains("password-protected", dead.LastError);

            var good = jobs.Single(j => j.Status == ExtractionStatus.Succeeded);
            // No per-job Lead on the email path — the barrier owns Lead creation.
            Assert.Null(good.ResultLeadId);

            // THE BATCH WAS NOT SUNK, which is this journey's real claim: the readable
            // attachment's component completed and its extracted line is durably stored, ready
            // for the reviewer, even though no Lead was auto-created.
            var components = await ctx.Set<EmailInquiryComponent>().AsNoTracking().ToListAsync();
            var readable = components.Single(c => c.Status == EmailInquiryComponentStatus.Completed);
            var result = await ctx.Set<EmailInquiryComponentResult>().AsNoTracking()
                .SingleAsync(r => r.ComponentId == readable.Id);
            Assert.Equal(1, result.ExtractedItemCount);
            Assert.Contains(components, c => c.Status == EmailInquiryComponentStatus.Skipped);

            // ...and nothing was quietly assembled from half a message.
            Assert.Equal(0, await ctx.Leads.CountAsync());
            var assembly = await ctx.Set<EmailInquiryAssembly>().AsNoTracking().SingleAsync();
            Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, assembly.Status);
            Assert.Contains("could not be read", assembly.StatusReason);

            // The message is visible in a resolved state — whichever of the two verdicts
            // (dead-letter writeback / persist) landed last, it must never still claim
            // in-flight progress.
            var ingest = await ctx.EmailIngests.AsNoTracking().SingleAsync();
            Assert.NotEqual("Queued", ingest.ParseStatus);
            Assert.NotEqual("Pending", ingest.ParseStatus);
        }
    }

    // =====================================================================================
    // 6. Restart resilience: kill the worker mid-extraction; a fresh worker completes the
    //    message exactly once.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_worker_killed_mid_extraction_loses_nothing_and_a_fresh_worker_completes_exactly_once()
    {
        var blocking = new BlockingFirstCallLlm();
        _llm = blocking; // resolved lazily by every scope the workers create

        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress);
        imap.AddMessage(CorpusManifest.Bytes("email-simple-english.eml"));
        await SeedMailboxAsync(imap);
        Assert.True((await PollAsync()).AllSucceeded);

        // Worker 1 claims the job and blocks inside the model call — then "crashes": its
        // cancellation is ignored by the in-flight call (as a real crash would ignore it),
        // no Fail/Complete is ever written, and the lease is simply never renewed again.
        var lease = TimeSpan.FromSeconds(3);
        var worker1 = StartWorker(leaseDuration: lease);
        await blocking.FirstCallStarted.Task.WaitAsync(TestWaits.Liveness);
        await KillAsync(worker1);

        await using (var ctx = ContextFor(null))
        {
            Assert.Equal(0, await ctx.Leads.CountAsync()); // nothing persisted by the dead worker
            var job = await ctx.Set<ExtractionJob>().AsNoTracking().SingleAsync();
            Assert.Equal(ExtractionStatus.Extracting, job.Status);
        }

        // A fresh worker reclaims the expired lease and completes the SAME job.
        var worker2 = StartWorker(leaseDuration: lease);
        try
        {
            var lead = await WaitForLeadAsync(l => l.Rfqno == "RFQ-CORPUS-001");
            await using var ctx = ContextFor(null);
            Assert.Equal(1, await ctx.Leads.CountAsync()); // exactly once — no duplicate
            Assert.Equal(1, await ctx.EmailIngests.CountAsync());
            var job = await ctx.Set<ExtractionJob>().AsNoTracking().SingleAsync();
            Assert.Equal(ExtractionStatus.Succeeded, job.Status);
            Assert.True(job.Attempts >= 2, "the reclaim must be a recorded second attempt");
            // An email component job mints no Lead of its own: the assembly fence returns 0 so
            // CompleteAsync stores NULL, and the message-level barrier builds the Lead after the
            // job is already Succeeded — deliberately without writing back, because a tracked
            // write to ExtractionJobs from inside the assembly-lock transaction would close an
            // ABBA cycle with the worker (EmailInquiryLeadAssembler.cs:466-468). The durable
            // job-to-lead links are the assembly and the ingestion occurrence, asserted here.
            Assert.Null(job.ResultLeadId);
            Assert.Equal(lead.Id, await ctx.Set<EmailInquiryAssembly>().AsNoTracking()
                .Select(a => a.AssembledLeadId).SingleAsync());
            Assert.True(await ctx.Set<LeadIngestionOccurrence>().AsNoTracking()
                .AnyAsync(o => o.LeadId == lead.Id && o.ExtractionJobId == job.Id));
            Assert.Equal("NeedsReview",
                (await ctx.EmailIngests.AsNoTracking().SingleAsync()).ParseStatus);
        }
        finally
        {
            await StopWorkerAsync(worker2);
        }
    }

    // =====================================================================================
    // 7. Crash-window recovery: EmailIngest committed at Pending, no jobs (crash between
    //    commit and enqueue) → the poller's sweeper re-enqueues it and it completes.
    // =====================================================================================

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_pending_row_stranded_by_a_crash_before_enqueue_is_swept_back_in_and_completes()
    {
        await using var imap = new FakeImapServer(CorpusGenerator.IntakeAddress); // mailbox is EMPTY: recovery needs no re-download
        await SeedMailboxAsync(imap);

        // The exact crash shape: durable row + retained raw bytes, and nothing else.
        var rawDir = Path.Combine(_root, "Raw_Emails");
        Directory.CreateDirectory(rawDir);
        var rawPath = Path.Combine(rawDir, $"{Guid.NewGuid():N}.eml");
        await File.WriteAllBytesAsync(rawPath, CorpusManifest.Bytes("email-simple-arabic.eml"));
        await using (var ctx = ContextFor(null))
        {
            ctx.EmailIngests.Add(new EmailIngest
            {
                MessageId = "corpus-simple-ar@corpus.nexora.example",
                EmailSubject = "RFQ RFQ-CORPUS-002",
                FromEmail = CorpusGenerator.CustomerAddress,
                ToEmail = CorpusGenerator.IntakeAddress,
                EmailConfigurationId = 1,
                CreatedOn = DateTime.UtcNow.AddHours(-1), // stranded, not in-flight
                ParseStatus = "Pending",
                RawEmailPath = rawPath
            });
            await ctx.SaveChangesAsync();
        }

        // One ordinary poller cycle: the mailbox yields nothing, and the sweeper — running
        // inside the same cycle — re-runs the cut-off routing step. The worker starts AFTER
        // the cycle so the sweep's own status writes cannot race the persister's.
        var report = await PollAsync();
        Assert.True(report.AllSucceeded, report.FailureSummary);

        var worker = StartWorker();
        try
        {
            var lead = await WaitForLeadAsync(l => l.Rfqno == "RFQ-CORPUS-002");

            await using var ctx = ContextFor(null);
            var ingest = await ctx.EmailIngests.AsNoTracking().SingleAsync();
            Assert.Equal("Inquiry", ingest.TriageOutcome); // the sweep ran the REAL routing
            Assert.Equal("NeedsReview", ingest.ParseStatus);
            Assert.Equal(1, await ctx.Leads.CountAsync()); // completed exactly once
            Assert.Equal(1, await ctx.Set<ExtractionJob>().CountAsync());
            var item = Assert.Single((await ctx.Leads.Include(l => l.LeadItems)
                .SingleAsync(l => l.Id == lead.Id)).LeadItems);
            Assert.Equal(250, item.Quantity);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    // ===================================================================== the service graph

    private ServiceProvider BuildServiceGraph()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions();

        // Tenant scoping exactly as a background worker runs: the ambient pushed scope IS
        // the tenant, captured by the ITenantContext at scope-resolution time.
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped<ITenantContext>(sp =>
            new AmbientTenantContext(sp.GetRequiredService<ITenantScopeAccessor>()));
        services.AddScoped(sp =>
            new ErpRfqAutomationContext(_dbOptions, sp.GetRequiredService<ITenantContext>()));

        // The ONLY two doubles in the graph.
        services.AddScoped<ILLMService>(_ => _llm);
        services.AddSingleton<IMalwareScanner, AlwaysCleanScanner>();

        // Real storage, real inspection, real ingestion, real queue, real extraction.
        services.AddSingleton<IWebHostEnvironment>(new JourneyEnvironment(AppContext.BaseDirectory));
        services.AddSingleton<IFileStorage>(new LocalFileStorage(_root, _root));
        services.AddSingleton<IEvidenceObjectStorage>(sp =>
            new LocalEvidenceObjectStorage(sp.GetRequiredService<IFileStorage>()));
        services.AddSingleton<IFileInspectionService>(sp =>
            new DocumentFileInspectionService(sp.GetRequiredService<IMalwareScanner>()));
        services.AddScoped<IExtractionQueue, ExtractionQueue>();
        services.AddScoped<IDocumentIngestion, DocumentIngestionService>();
        services.AddScoped<IExtractionDocumentReader>(sp => new ProductionDocumentReader(
            sp.GetRequiredService<ILogger<ProductionDocumentReader>>(),
            sp.GetRequiredService<IWebHostEnvironment>(),
            sp.GetRequiredService<IEvidenceObjectStorage>(),
            sp.GetRequiredService<IFileInspectionService>()));
        services.AddScoped<ICanonicalRfqNormalizer, CanonicalRfqNormalizer>();
        services.AddScoped<IChunkedExtractionService>(sp => new ChunkedExtractionService(
            sp.GetRequiredService<ILLMService>(),
            sp.GetRequiredService<ICanonicalRfqNormalizer>(),
            sp.GetRequiredService<ILogger<ChunkedExtractionService>>()));
        services.AddScoped<IConversationalExtractionService>(sp => new ConversationalExtractionService(
            sp.GetRequiredService<ILLMService>(),
            sp.GetRequiredService<ILogger<ConversationalExtractionService>>()));
        services.AddScoped<ILeadIdentityApplicationService, LeadIdentityApplicationService>();
        services.AddScoped<ILeadPersister, LeadPersister>();

        // THE canonical intake, mirroring Program.cs:694-703 and :794. Without these the poller's
        // `intake` resolves to null and EVERY journey silently falls through to the legacy
        // direct-extraction path — which pre-checks duplicates on (Rfqno, BuyersName) and never
        // calls ReconcileAsync, so an amendment is discarded instead of becoming revision 2.
        // That is precisely the failure this harness exists to catch, so the harness must
        // register the same graph production boots with, or it asserts against a shape
        // production never emits.
        ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyServiceCollectionExtensions
            .AddEmailInquiryAssembly(services);
        services.AddScoped<ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService,
            ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryIntakeService>();
        services.AddScoped<ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryLeadAssembler,
            ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryLeadAssembler>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true
        });
    }

    private ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(_dbOptions, new StubTenant(businessUnitId));

    private async Task SeedMailboxAsync(FakeImapServer imap)
    {
        await using var ctx = ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        ctx.EmailConfigurations.Add(new EmailConfiguration
        {
            Id = 1,
            BusinessUnitId = Tenant,
            ConfigurationName = "journey-intake",
            EmailAddress = CorpusGenerator.IntakeAddress,
            Protocol = "IMAP",
            Host = "127.0.0.1",
            Port = imap.Port,
            Username = imap.Username,
            Password = imap.Password,
            UseSsl = false,
            PollingInterval = 300,
            IsActive = true,
            // A fixed recovery point BEFORE the corpus date (2026-08-10), combined with a
            // wide lookback cap below, keeps the SENTSINCE window covering the corpus no
            // matter when this suite runs.
            LastSuccessfulPollOn = MailboxBaseline,
            CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>One real poller cycle over the journey mailbox — mailbox loop AND sweeper.</summary>
    private async Task<MailboxPollReport> PollAsync()
    {
        await using var discovery = ContextFor(null);
        var service = new EmailService(
            context: discovery,
            env: new JourneyEnvironment(_root),
            logger: NullLogger<EmailService>.Instance,
            llmService: _llm,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>(),
            configuration: new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    // Wide cap so the fixed corpus dates stay inside the lookback forever.
                    ["Ingestion:Email:MaxLookbackDays"] = "36500",
                    // ...and a wide FLOOR, which the cap alone does not give. After the first
                    // successful poll, ResolveLookbackWindow clamps SinceUtc FORWARD to
                    // UtcNow - MinLookbackDays (default 1 day), so a second cycle issues
                    // SENTSINCE <yesterday> and the corpus — frozen at 10 Aug 2026 — returns
                    // nothing at all. Journey 2 then "passes" its no-duplicate assertion for the
                    // wrong reason: nothing was delivered to duplicate. The ledger, not the wall
                    // clock, must be what prevents re-ingestion.
                    ["Ingestion:Email:MinLookbackDays"] = "36500"
                }).Build(),
            storage: _provider.GetRequiredService<IFileStorage>(),
            pollerHealth: null,
            workGate: null,
            tenantScope: _provider.GetRequiredService<ITenantScopeAccessor>());
        return await service.FetchAndSaveLeadsAsync(Tenant);
    }

    private ExtractionWorker StartWorker(TimeSpan? leaseDuration = null)
    {
        var worker = new ExtractionWorker(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            new ExtractionWorkerOptions
            {
                WorkerCount = 2,
                MaxConcurrentLlmCalls = 4,
                PerTenantConcurrencyCap = 4,
                LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes(5),
                IdlePollDelay = TimeSpan.FromMilliseconds(200)
            },
            _provider.GetRequiredService<ILogger<ExtractionWorker>>(),
            _provider.GetRequiredService<ITenantScopeAccessor>());
        worker.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return worker;
    }

    private static async Task StopWorkerAsync(ExtractionWorker worker)
    {
        await worker.StopAsync(CancellationToken.None);
        worker.Dispose();
    }

    /// <summary>Simulates a crash: cancellation is requested but NOT awaited — a loop stuck
    /// in an in-flight call stays stuck, exactly like a killed process, and no graceful
    /// completion/fail write ever happens.</summary>
    private static async Task KillAsync(ExtractionWorker worker)
    {
        try
        {
            await worker.StopAsync(new CancellationToken(canceled: true));
        }
        catch (OperationCanceledException)
        {
            // expected: we refused to wait for the loops to drain
        }
        worker.Dispose();
    }

    private async Task<Lead> WaitForLeadAsync(Func<Lead, bool> predicate)
    {
        var deadline = DateTime.UtcNow + TestWaits.Liveness;
        while (DateTime.UtcNow < deadline)
        {
            await using (var ctx = ContextFor(null))
            {
                var leads = await ctx.Leads.AsNoTracking().ToListAsync();
                var match = leads.FirstOrDefault(predicate);
                if (match is not null) return match;
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("The expected lead never appeared within the liveness bound.");
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TestWaits.Liveness;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException(what);
    }

    private async Task ExecuteAdminAsync(string sql)
    {
        await using var connection = await _server.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    // ------------------------------------------------------------------------------ doubles

    /// <summary>The worker equivalent of HttpTenantContext: the ambient pushed scope IS the
    /// tenant, read once at construction — exactly like the production implementations.</summary>
    private sealed class AmbientTenantContext(ITenantScopeAccessor scope) : ITenantContext
    {
        public long? BusinessUnitId { get; } = scope.BusinessUnitId;
    }

    private sealed class AlwaysCleanScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
            => Task.FromResult(MalwareScanResult.Clean("journey-scanner"));
    }

    /// <summary>Scripted LLM whose FIRST call blocks forever and ignores cancellation — the
    /// shape of a worker that dies mid-model-call. Every later call answers from the corpus
    /// manifest, so the reclaiming worker completes normally.</summary>
    private sealed class BlockingFirstCallLlm : ILLMService
    {
        private readonly CorpusManifestLlm _inner = new();
        private int _calls;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AiProviderClass ProviderClass => AiProviderClass.Local;

        public async Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                FirstCallStarted.TrySetResult();
                await new TaskCompletionSource().Task; // never completes; ct deliberately ignored
            }
            return await _inner.ExtractLeadDataAsync(fullText, context, cancellationToken);
        }

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }

    private sealed class JourneyEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "journey-tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
    }
}
