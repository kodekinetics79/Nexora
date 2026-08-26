using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OfficeOpenXml;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

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

            // (h) Assembly preserved the deterministic attachment lineage rather than only
            //     copying commercial values into LeadItems. This is what the later promotion
            //     gate reads: exact raw cells, exact source addresses and the canonical line
            //     bound to the final message-level Lead line.
            var canonicalLines = await context.Set<CanonicalLineItem>().AsNoTracking()
                .Where(x => x.Inquiry.LeadId == lead.Id)
                .OrderBy(x => x.LineNumber)
                .ToListAsync();
            Assert.Equal(5, canonicalLines.Count);
            Assert.All(canonicalLines, x => Assert.NotNull(x.LeadItemId));

            var exactEvidence = await context.Set<FieldEvidence>().AsNoTracking()
                .Include(x => x.Region).ThenInclude(x => x.Page).ThenInclude(x => x.Document)
                .Where(x => x.LineItem != null && x.LineItem.Inquiry.LeadId == lead.Id)
                .ToListAsync();
            Assert.Equal(25, exactEvidence.Count);
            Assert.Contains(exactEvidence, x =>
                x.FieldName == "ManufacturerPartNumber"
                && x.RawValue == "VLV-1001"
                && x.NormalizedValue == "VLV-1001"
                && x.Region.SourceAddress == "'CSV'!A2"
                && x.Region.Page.Document.OriginalFileName == "valves.csv");
            Assert.Contains(exactEvidence, x =>
                x.FieldName == "Quantity"
                && x.RawValue == "60"
                && x.NormalizedValue == "60"
                && x.Region.SourceAddress == "'CSV'!C2"
                && x.Region.Page.Document.OriginalFileName == "gaskets.csv");

            // (i) The evidence chain is intact: the raw .eml is still addressable and the
            //     hash recorded at capture still describes it.
            Assert.False(string.IsNullOrWhiteSpace(assembly.RawEvidenceUri));
            Assert.Equal(64, assembly.RawEvidenceSha256?.Length);
            var evidence = scope.ServiceProvider.GetRequiredService<IEvidenceObjectStorage>();
            await using var raw = await evidence.OpenVerifiedReadAsync(
                assembly.RawEvidenceUri!, assembly.RawEvidenceSha256!);
            Assert.True(raw.Length > 0);
        }

        // (j) The CSVs never reached a model. If they had, the run is neither deterministic
        //     nor cheap, and the structured fast path has silently regressed.
        Assert.Equal(0, llm.CallCount);

        // ---- 5. REPLAY: draining again must not manufacture a second Lead or evidence. ----
        await EmailToLeadHarness.DrainQueueAsync(services, BusinessUnitId);
        using (var scope = services.CreateScope())
        {
            using var tenant = scope.ServiceProvider
                .GetRequiredService<ITenantScopeAccessor>().Push(BusinessUnitId);
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            Assert.Equal(1, await context.Leads.AsNoTracking()
                .CountAsync(l => l.BusinessUnitId == BusinessUnitId));
            Assert.Equal(25, await context.Set<FieldEvidence>().AsNoTracking()
                .CountAsync(x => x.BusinessUnitId == BusinessUnitId));
        }
    }

    [Fact]
    public async Task Body_only_inquiry_becomes_one_Lead_with_body_evidence_and_no_customer_file_attachments()
    {
        var businessUnitId = UniqueBusinessUnitId();
        const string messageId = "vertical-body-only-0001@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        var llm = new EmailToLeadHarness.RefusingLlm();
        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, llm,
            registrations => registrations.AddScoped<IConversationalExtractionService, BodyOnlyExtractor>());

        var message = EmailToLeadHarness.BuildBodyOnlyMessage(messageId);
        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 1);

        Assert.Equal(1, schedule.Scheduled);
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking().SingleAsync(a => a.Id == assemblyId);
        var lead = Assert.Single(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());
        var line = Assert.Single(await context.LeadItems.AsNoTracking()
            .Where(i => i.LeadId == lead.Id).ToListAsync());

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.Equal(lead.Id, assembly.AssembledLeadId);
        Assert.Contains("BODY-ONLY-700", line.ProductShortName);
        Assert.Equal(7, line.Quantity);
        var evidence = Assert.Single(await context.Attachments.AsNoTracking()
            .Where(a => a.ParentType == "Lead" && a.ParentId == lead.Id).ToListAsync());
        Assert.EndsWith("_body.txt", evidence.FileName);
        Assert.NotNull(evidence.ContentSha256);

        var canonicalLine = Assert.Single(await context.Set<CanonicalLineItem>().AsNoTracking()
            .Where(item => item.BusinessUnitId == businessUnitId && item.LeadItemId == line.Id)
            .ToListAsync());
        var fieldEvidence = Assert.Single(await context.Set<FieldEvidence>().AsNoTracking()
            .Include(item => item.Region)
            .Where(item => item.BusinessUnitId == businessUnitId
                           && item.LineItemId == canonicalLine.Id)
            .ToListAsync());
        Assert.Equal("SourceSpan", fieldEvidence.FieldName);
        Assert.Equal("Please quote 7 EA BODY-ONLY-700 pressure transmitters, delivery DDP Jubail.",
            fieldEvidence.RawValue);
        Assert.Equal("message-body:verified-span:1", fieldEvidence.Region.SourceAddress);
    }

    [Fact]
    public async Task Unverified_model_span_stays_visible_but_never_becomes_promotion_evidence()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"vertical-unverified-span-{Guid.NewGuid():N}@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations.AddScoped<IConversationalExtractionService, UnverifiedBodyOnlyExtractor>());
        await EmailToLeadHarness.CaptureAndScheduleAsync(services, businessUnitId,
            EmailToLeadHarness.BuildBodyOnlyMessage(messageId), expectedComponentCount: 1);
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var lead = await context.Leads.AsNoTracking().SingleAsync(x => x.BusinessUnitId == businessUnitId);
        var leadItemId = await context.LeadItems.AsNoTracking()
            .Where(x => x.LeadId == lead.Id).Select(x => x.Id).SingleAsync();
        var canonicalLineId = await context.Set<CanonicalLineItem>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.LeadItemId == leadItemId)
            .Select(x => x.Id).SingleAsync();

        Assert.False(await context.Set<FieldEvidence>().AsNoTracking().AnyAsync(x =>
            x.BusinessUnitId == businessUnitId && x.LineItemId == canonicalLineId));
    }

    [Fact]
    public async Task Governed_dead_letter_retry_runs_the_real_worker_and_assembles_exactly_one_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"vertical-dead-letter-recovery-{Guid.NewGuid():N}@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations.AddScoped<IConversationalExtractionService, BodyOnlyExtractor>());

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, EmailToLeadHarness.BuildBodyOnlyMessage(messageId),
            expectedComponentCount: 1);
        Assert.Equal(1, schedule.Scheduled);

        long jobId;
        long occurrenceId;
        string componentKey;
        using (var failedScope = services.CreateScope())
        using (failedScope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = failedScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var component = await db.EmailInquiryComponents.SingleAsync(x => x.AssemblyId == assemblyId);
            componentKey = component.ComponentKey;
            var job = await db.Set<ExtractionJob>().SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.EmailInquiryComponentId == component.Id);
            jobId = job.Id;
            occurrenceId = Assert.IsType<long>(job.SourceDocumentOccurrenceId);

            // Establish the same durable terminal state produced when the worker exhausts its
            // retry budget. The PostgreSQL job-status trigger must move the owned intake
            // occurrence to DeadLetter in the same save; governed recovery refuses stale or
            // unrelated lineage.
            job.Status = ExtractionStatus.DeadLetter;
            job.Attempts = job.MaxAttempts;
            job.LastError = "simulated transient extraction dependency timeout";
            job.LeasedBy = null;
            job.LeaseExpiresAt = null;
            job.UpdatedOn = DateTime.UtcNow;
            await db.SaveChangesAsync();

            await failedScope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>()
                .RecordComponentOutcomeAsync(
                    businessUnitId, assemblyId, componentKey,
                    EmailInquiryComponentStatus.Skipped,
                    "processing_timeout",
                    "Extraction stopped after its retry budget.",
                    occurrenceId);

            Assert.Equal(IntakeOccurrenceStatus.DeadLetter,
                await db.Set<SourceDocumentOccurrence>().Where(x => x.Id == occurrenceId)
                    .Select(x => x.IntakeStatus).SingleAsync());
            Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview,
                await db.EmailInquiryAssemblies.Where(x => x.Id == assemblyId)
                    .Select(x => x.Status).SingleAsync());
        }

        const string recoveryKey = "email-to-lead-full-recovery-1";
        using (var recoveryScope = services.CreateScope())
        using (recoveryScope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = recoveryScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var recovery = new ExtractionDeadLetterService(
                db,
                recoveryScope.ServiceProvider.GetRequiredService<IEvidenceObjectStorage>(),
                recoveryScope.ServiceProvider.GetRequiredService<IMalwareScanner>());
            var command = new RecoverExtractionDeadLetterCommand(
                "The transient extraction dependency is healthy and the source was reverified.",
                recoveryKey);

            var queued = await recovery.RecoverAsync(
                businessUnitId, jobId, "email-recovery-operator", command, default);
            var replay = await recovery.RecoverAsync(
                businessUnitId, jobId, "email-recovery-operator", command, default);

            Assert.Equal("RetryQueued", queued.Status);
            Assert.False(queued.IdempotentReplay);
            Assert.Equal("RetryQueued", replay.Status);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(ExtractionStatus.Pending,
                await db.Set<ExtractionJob>().Where(x => x.Id == jobId).Select(x => x.Status).SingleAsync());
            Assert.Equal(IntakeOccurrenceStatus.Queued,
                await db.Set<SourceDocumentOccurrence>().Where(x => x.Id == occurrenceId)
                    .Select(x => x.IntakeStatus).SingleAsync());
            Assert.Equal(EmailInquiryComponentStatus.Pending,
                await db.EmailInquiryComponents.Where(x => x.ComponentKey == componentKey)
                    .Select(x => x.Status).SingleAsync());
            Assert.Equal(EmailInquiryAssemblyStatus.Extracting,
                await db.EmailInquiryAssemblies.Where(x => x.Id == assemblyId)
                    .Select(x => x.Status).SingleAsync());
            Assert.Equal(1, await db.ExtractionDeadLetterEvents.CountAsync(x =>
                x.BusinessUnitId == businessUnitId
                && x.ExtractionJobId == jobId
                && x.IdempotencyKey == recoveryKey
                && x.Action == ExtractionDeadLetterAction.RetryQueued));
        }

        // This is the production queue and worker, not a direct call to the assembler. Its
        // successful completion must close the component, cross the message barrier and create
        // one Lead. A second drain proves the queue replay cannot manufacture another Lead.
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var verify = services.CreateScope();
        using var tenant = verify.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = verify.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking().SingleAsync(x => x.Id == assemblyId);
        var componentAfter = await context.EmailInquiryComponents.AsNoTracking()
            .SingleAsync(x => x.AssemblyId == assemblyId);
        var jobAfter = await context.Set<ExtractionJob>().AsNoTracking().SingleAsync(x => x.Id == jobId);
        var lead = Assert.Single(await context.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId).ToListAsync());
        var line = Assert.Single(await context.LeadItems.AsNoTracking()
            .Where(x => x.LeadId == lead.Id).ToListAsync());

        Assert.Equal(ExtractionStatus.Succeeded, jobAfter.Status);
        Assert.Equal(EmailInquiryComponentStatus.Completed, componentAfter.Status);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.Equal(lead.Id, assembly.AssembledLeadId);
        Assert.Contains("BODY-ONLY-700", line.ProductShortName);
        Assert.Equal(7, line.Quantity);
        Assert.Equal(1, await context.ExtractionDeadLetterEvents.CountAsync(x =>
            x.BusinessUnitId == businessUnitId
            && x.ExtractionJobId == jobId
            && x.IdempotencyKey == recoveryKey));
    }

    [Fact]
    public async Task Unsupported_commercial_attachment_holds_the_whole_message_without_a_partial_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        const string messageId = "vertical-unsupported-0001@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        var llm = new EmailToLeadHarness.RefusingLlm();
        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, llm);
        var unsupported = EmailToLeadHarness.Attachment(
            "commercial-drawing.dwg", "application/acad", [0x41, 0x43, 0x31, 0x30]);
        var message = EmailToLeadHarness.BuildMessage(messageId, extraParts: unsupported);

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 4);
        Assert.Equal(3, schedule.Scheduled);
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking().SingleAsync(a => a.Id == assemblyId);
        var refused = await context.EmailInquiryComponents.AsNoTracking().SingleAsync(c =>
            c.AssemblyId == assemblyId && c.FileName == "commercial-drawing.dwg");

        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, assembly.Status);
        Assert.Null(assembly.AssembledLeadId);
        Assert.Equal(EmailInquiryComponentStatus.Skipped, refused.Status);
        Assert.Equal(EmailInquirySkipReasons.UnsupportedFileType, refused.ReasonCode);
        Assert.Equal(0, await context.Leads.AsNoTracking()
            .CountAsync(l => l.BusinessUnitId == businessUnitId));
    }

    [Fact]
    public async Task Native_PDF_and_XLSX_in_one_email_become_one_Lead_with_every_attachment_line()
    {
        var businessUnitId = UniqueBusinessUnitId();
        const string messageId = "vertical-pdf-xlsx-0001@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        var refusingLlm = new EmailToLeadHarness.RefusingLlm();
        var documents = new NativeDocumentExtractor();
        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, refusingLlm,
            registrations => registrations.AddScoped<IChunkedExtractionService>(_ => documents));

        var pdf = EmailToLeadHarness.Attachment(
            "native-requirement.pdf", "application/pdf", NativePdf());
        var xlsx = EmailToLeadHarness.Attachment(
            "priced-schedule.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            NativeXlsx());
        var message = EmailToLeadHarness.BuildBodyOnlyMessage(
            messageId,
            "Please quote the attached native PDF requirement and priced Excel schedule.");
        // SeedTenant creates the durable ingest row the mailbox poller would ordinarily create.
        // Keep its subject aligned with the MIME message so the provenance assertion is real.
        message.Subject = "RFQ 88-2410 Jubail expansion";
        message.Body = new MimeKit.Multipart("mixed") { message.Body, pdf, xlsx };

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 3);
        Assert.Equal(3, schedule.Scheduled);
        Assert.Equal(0, schedule.Held);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking().SingleAsync(a => a.Id == assemblyId);
        var lead = Assert.Single(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());
        var lines = await context.LeadItems.AsNoTracking()
            .Where(i => i.LeadId == lead.Id).OrderBy(i => i.Id).ToListAsync();

        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.Equal(lead.Id, assembly.AssembledLeadId);
        Assert.Equal(2, lines.Count);
        var pdfLine = Assert.Single(lines.Where(l =>
            l.ProductShortName != null && l.ProductShortName.Contains("PDF-900", StringComparison.Ordinal)));
        Assert.Equal(9, pdfLine.Quantity);
        var xlsxLine = Assert.Single(lines.Where(l => l.ManufacturerPartNumber == "XLSX-800"));
        Assert.Equal(8, xlsxLine.Quantity);
        Assert.True(documents.NativePdfSeen);
        Assert.Equal("native-requirement.pdf", documents.NativePdfDocumentName);
        Assert.Equal(ExtractionProcessingPath.NativeParser, documents.NativePdfProcessingPath);
        Assert.Contains("PDF-900", documents.NativePdfText, StringComparison.Ordinal);
        Assert.True(documents.NativeXlsxSeen);
        Assert.Equal("priced-schedule.xlsx", documents.NativeXlsxDocumentName);
        Assert.Equal(0, refusingLlm.CallCount);

        var ingest = await context.EmailIngests.AsNoTracking().SingleAsync(i => i.Id == assembly.EmailIngestId);
        Assert.Equal(assembly.EmailIngestId, lead.EmailIngestsId);
        Assert.Equal(messageId, ingest.MessageId);
        Assert.Equal(message.Subject, ingest.EmailSubject);
        Assert.Equal("buyer@customer.example", ingest.FromEmail);
        Assert.Equal(message.Date, assembly.ReceivedAtUtc);

        var components = await context.EmailInquiryComponents.AsNoTracking()
            .Where(c => c.AssemblyId == assemblyId).OrderBy(c => c.Ordinal).ToListAsync();
        Assert.EndsWith("_body.txt", components[0].FileName, StringComparison.Ordinal);
        Assert.Equal("native-requirement.pdf", components[1].FileName);
        Assert.Equal("priced-schedule.xlsx", components[2].FileName);
        Assert.All(components, c => Assert.Equal(EmailInquiryComponentStatus.Completed, c.Status));
        Assert.Equal(3, await context.Set<EmailInquiryComponentResult>().AsNoTracking()
            .CountAsync(r => r.AssemblyId == assemblyId));

        Assert.All(components, component =>
            Assert.False(string.IsNullOrWhiteSpace(component.EvidenceUri)));
    }

    [Fact]
    public async Task Scheduling_outage_stays_unacknowledged_then_repoll_resumes_to_exactly_one_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"vertical-scheduling-retry-{Guid.NewGuid():N}@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
        var message = EmailToLeadHarness.BuildMessage(messageId);
        long assemblyId;

        await using (var failing = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations =>
            {
                registrations.RemoveAll<IDocumentIngestion>();
                registrations.AddScoped<IDocumentIngestion, RefusingDocumentIngestion>();
            }))
        using (var scope = failing.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            Assert.IsType<RefusingDocumentIngestion>(
                scope.ServiceProvider.GetRequiredService<IDocumentIngestion>());
            var result = await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
                .CaptureAndScheduleAsync(
                    message,
                    await db.EmailIngests.SingleAsync(x => x.MessageId == messageId),
                    await db.EmailConfigurations.SingleAsync(x => x.Id == businessUnitId),
                    EmailToLeadHarness.BodyText,
                    new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
                    "buyer@customer.example");
            Assert.False(result.SafeToAcknowledge);
            Assert.Equal(3, result.Held);
            assemblyId = result.AssemblyId!.Value;
            var held = await db.EmailInquiryComponents.AsNoTracking()
                .Where(x => x.AssemblyId == assemblyId).ToListAsync();
            Assert.All(held, component => Assert.Null(component.ExtractionJobId));
        }

        var capturedLog = new CapturingLogger<EmailInquiryIntakeService>();
        await using var recovered = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations.AddSingleton<ILogger<EmailInquiryIntakeService>>(capturedLog));
        using (var scope = recovered.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var result = await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
                .CaptureAndScheduleAsync(
                    message,
                    await db.EmailIngests.SingleAsync(x => x.MessageId == messageId),
                    await db.EmailConfigurations.SingleAsync(x => x.Id == businessUnitId),
                    EmailToLeadHarness.BodyText,
                    new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
                    "buyer@customer.example");
            Assert.True(result.SafeToAcknowledge);
            Assert.True(result.AlreadyCaptured);
            Assert.True(result.Scheduled == 3,
                $"Expected three resumed jobs; got scheduled={result.Scheduled}, "
                + $"already={result.AlreadyScheduled}, held={result.Held}, failure={result.FailureReason}; "
                + $"errors={string.Join(" | ", capturedLog.Exceptions.Select(x => x.ToString()))}.");
            Assert.Equal(0, result.Held);
        }

        await EmailToLeadHarness.DrainQueueAsync(recovered, businessUnitId);
        using var verify = recovered.CreateScope();
        using var tenant = verify.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = verify.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking().SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.NotNull(assembly.AssembledLeadId);
        Assert.Equal(1, await context.Leads.CountAsync(x => x.BusinessUnitId == businessUnitId));
    }

    [Fact]
    public async Task Noise_is_durably_captured_terminalized_and_invisible_to_stranded_recovery()
    {
        var businessUnitId = UniqueBusinessUnitId();
        const string messageId = "vertical-noise-0001@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm());
        long assemblyId;
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var configuration = await context.EmailConfigurations.SingleAsync(x => x.Id == businessUnitId);
            var ingest = await context.EmailIngests.SingleAsync(x => x.MessageId == messageId);
            var result = await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
                .CaptureAndScheduleAsync(
                    EmailToLeadHarness.BuildMessage(messageId), ingest, configuration,
                    EmailToLeadHarness.BodyText,
                    new EmailTriageDecision(
                        EmailTriageOutcome.Noise, [EmailTriageReasonCodes.AutoSubmittedHeader], null, false),
                    "buyer@customer.example");
            Assert.True(result.SafeToAcknowledge);
            Assert.Equal(0, result.Scheduled);
            assemblyId = result.AssemblyId!.Value;
        }

        EmailInquiryRecoverySweepResult sweep;
        using (var sweepScope = services.CreateScope())
            sweep = await sweepScope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyRecoveryService>()
                .SweepOnceAsync();
        Assert.Equal(0, sweep.StrandedComponents.Examined);

        using var verify = services.CreateScope();
        using var tenant = verify.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var db = verify.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await db.EmailInquiryAssemblies.AsNoTracking().SingleAsync(x => x.Id == assemblyId);
        var components = await db.EmailInquiryComponents.AsNoTracking()
            .Where(x => x.AssemblyId == assemblyId).ToListAsync();
        Assert.Equal(EmailInquiryAssemblyStatus.NoInquiry, assembly.Status);
        Assert.All(components, component => Assert.True(component.IsTerminal));
        Assert.All(components, component => Assert.Equal(EmailInquiryComponentStatus.Ignored, component.Status));
        Assert.Equal(0, await db.Leads.CountAsync(x => x.BusinessUnitId == businessUnitId));
    }

    [Fact]
    public async Task Governed_manual_reprocess_reopens_durable_noise_and_creates_exactly_one_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        const string messageId = "vertical-noise-reprocess-0001@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm());
        long ingestId;
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var ingest = await db.EmailIngests.SingleAsync(x => x.MessageId == messageId);
            ingestId = ingest.Id;
            var result = await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
                .CaptureAndScheduleAsync(
                    EmailToLeadHarness.BuildMessage(messageId), ingest,
                    await db.EmailConfigurations.SingleAsync(x => x.Id == businessUnitId),
                    EmailToLeadHarness.BodyText,
                    new EmailTriageDecision(
                        EmailTriageOutcome.Noise, [EmailTriageReasonCodes.AutoSubmittedHeader], null, false),
                    "buyer@customer.example");
            Assert.True(result.SafeToAcknowledge);
        }

        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var service = new EmailTriageService(
                scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>(),
                scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>(),
                scope.ServiceProvider.GetRequiredService<IRawEmailEvidenceReader>(),
                new NoopLogger<EmailTriageService>());
            var result = await service.ReprocessAsync(
                businessUnitId, ingestId, "operator@tenant.example",
                "Buyer confirmed this automated-looking message is an RFQ.", "noise-reopen-1");
            Assert.Equal(3, result.Enqueued);
        }

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);
        using var verify = services.CreateScope();
        using var tenant = verify.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = verify.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(x => x.EmailIngestId == ingestId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.NotNull(assembly.AssembledLeadId);
        Assert.Equal(1, await context.Leads.CountAsync(x => x.BusinessUnitId == businessUnitId));
        var checkpointBeforeReplay = await context.EmailIngests.AsNoTracking()
            .Where(x => x.Id == ingestId).Select(x => x.ParseStatus).SingleAsync();
        var replayService = new EmailTriageService(
            context,
            verify.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>(),
            verify.ServiceProvider.GetRequiredService<IRawEmailEvidenceReader>(),
            new NoopLogger<EmailTriageService>());
        var replay = await replayService.ReprocessAsync(
            businessUnitId, ingestId, "operator@tenant.example",
            "Buyer confirmed this automated-looking message is an RFQ.", "noise-reopen-1");
        Assert.True(replay.Replayed);
        Assert.Equal(0, replay.Enqueued);
        Assert.Equal(checkpointBeforeReplay, replay.Status);
        Assert.Equal(checkpointBeforeReplay, await context.EmailIngests.AsNoTracking()
            .Where(x => x.Id == ingestId).Select(x => x.ParseStatus).SingleAsync());
        Assert.Equal(1, await context.Leads.CountAsync(x => x.BusinessUnitId == businessUnitId));
        var audit = Assert.Single(await context.IamAuditEvents.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId
            && x.Action == "EmailTriageReprocessed"
            && x.TargetId == ingestId
            && x.CorrelationId == "noise-reopen-1").ToListAsync());
        Assert.Equal("operator@tenant.example", audit.TargetLabel);
        Assert.Contains("requestHash", audit.AfterJson);
    }

    /// <summary>
    /// THE stranded-hold defect, stated end to end.
    ///
    /// <para>A message held by an infrastructure fault had exactly one control offered for it —
    /// "Reprocess as inquiry" — and the endpoint refused it with a 422, because the governed
    /// reopen authority admitted <c>NoInquiry</c> only. Nothing else touches a held message:
    /// the recovery sweep claims only ReadyForAssembly assemblies with
    /// Pending/Inspecting/Extracting components, so the customer's enquiry stayed on the screen
    /// forever with no way out. This drives the REAL graph — the same
    /// <see cref="EmailTriageService"/> the controller calls, the real queue, the real worker,
    /// the real barrier — and requires the message to reach a Lead.</para>
    /// </summary>
    [Fact]
    public async Task Governed_manual_reprocess_releases_an_infrastructure_hold_and_creates_exactly_one_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"vertical-hold-reprocess-{Guid.NewGuid():N}@buyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);
        var message = EmailToLeadHarness.BuildMessage(messageId);
        long ingestId;
        long assemblyId;

        // 1. The outage. Every component is held with no durable job, and the message as a whole
        //    is FailedRecoverable — the exact row the screen renders as "Held — service unavailable".
        await using (var failing = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations =>
            {
                registrations.RemoveAll<IDocumentIngestion>();
                registrations.AddScoped<IDocumentIngestion, RefusingDocumentIngestion>();
            }))
        using (var scope = failing.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var ingest = await db.EmailIngests.SingleAsync(x => x.MessageId == messageId);
            ingestId = ingest.Id;
            var result = await scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>()
                .CaptureAndScheduleAsync(
                    message, ingest,
                    await db.EmailConfigurations.SingleAsync(x => x.Id == businessUnitId),
                    EmailToLeadHarness.BodyText,
                    new EmailTriageDecision(EmailTriageOutcome.Inquiry, [], null, false),
                    "buyer@customer.example");
            assemblyId = result.AssemblyId!.Value;
            Assert.Equal(3, result.Held);
        }

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm());
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var held = await db.EmailInquiryAssemblies.AsNoTracking()
                .Include(x => x.Components).SingleAsync(x => x.Id == assemblyId);
            Assert.Equal(EmailInquiryAssemblyStatus.FailedRecoverable, held.Status);
            Assert.All(held.Components, component =>
                Assert.Equal(EmailInquiryComponentStatus.FailedRecoverable, component.Status));
        }

        // 2. The one control the screen offers. This is what used to 422.
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var service = new EmailTriageService(
                scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>(),
                scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>(),
                scope.ServiceProvider.GetRequiredService<IRawEmailEvidenceReader>(),
                new NoopLogger<EmailTriageService>());
            var result = await service.ReprocessAsync(
                businessUnitId, ingestId, "operator@tenant.example",
                "Storage came back; put the buyer's enquiry through again.", "hold-reopen-1");
            Assert.Equal(3, result.Enqueued);
            Assert.False(result.Replayed);
        }

        // 3. A double-click. The SAME command, twice: one audit row, nothing queued twice.
        using (var scope = services.CreateScope())
        using (scope.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId))
        {
            var service = new EmailTriageService(
                scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>(),
                scope.ServiceProvider.GetRequiredService<IEmailInquiryIntakeService>(),
                scope.ServiceProvider.GetRequiredService<IRawEmailEvidenceReader>(),
                new NoopLogger<EmailTriageService>());
            var again = await service.ReprocessAsync(
                businessUnitId, ingestId, "operator@tenant.example",
                "Storage came back; put the buyer's enquiry through again.", "hold-reopen-1");
            // Replayed, not re-queued: every job it reports is one the first command created.
            Assert.True(again.Replayed);
            Assert.Equal(3, again.Enqueued);
            Assert.Equal(3, await scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>()
                .Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == businessUnitId));
        }

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);
        using var verify = services.CreateScope();
        using var tenant = verify.ServiceProvider.GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = verify.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking().SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.NotNull(assembly.AssembledLeadId);
        Assert.Equal(1, await context.Leads.CountAsync(x => x.BusinessUnitId == businessUnitId));
        Assert.Single(await context.IamAuditEvents.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId
            && x.Action == "EmailTriageReprocessed"
            && x.TargetId == ingestId).ToListAsync());
    }

    private sealed class BodyOnlyExtractor : IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
            => Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                Result = Ext.Result([Ext.Item(0.98, "BODY-ONLY-700 pressure transmitter", 7) with
                {
                    SourceSpan = "Please quote 7 EA BODY-ONLY-700 pressure transmitters, delivery DDP Jubail.",
                    SourceSpanVerified = true
                }], 0.98),
                ExpectedItemCount = 1,
                ExtractedItemCount = 1,
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
    }

    private sealed class UnverifiedBodyOnlyExtractor : IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
            => Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.NeedsReview,
                ReviewReason = "The quoted source text could not be located.",
                Result = Ext.Result([Ext.Item(0.98, "INVENTED-999 switchgear", 2) with
                {
                    SourceSpan = "2 EA INVENTED-999 switchgear",
                    SourceSpanVerified = false
                }], 0.98),
                ExpectedItemCount = 1,
                ExtractedItemCount = 1,
                ProcessingPath = ExtractionProcessingPath.LocalModel
            });
    }

    /// <summary>
    /// The one intentional semantic-boundary test double in this vertical test. The real PDF
    /// reader must first recover the native text layer, then the real worker routes that input
    /// here. XLSX must arrive as structured rows, proving the real workbook reader ran.
    /// </summary>
    private sealed class NativeDocumentExtractor : IChunkedExtractionService
    {
        private int _nativePdfSeen;
        private int _nativeXlsxSeen;
        public bool NativePdfSeen => Volatile.Read(ref _nativePdfSeen) == 1;
        public bool NativeXlsxSeen => Volatile.Read(ref _nativeXlsxSeen) == 1;
        public string? NativePdfDocumentName { get; private set; }
        public string? NativeXlsxDocumentName { get; private set; }
        public string NativePdfText { get; private set; } = string.Empty;
        public ExtractionProcessingPath NativePdfProcessingPath { get; private set; }

        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
            => input.IsStructured && input.StructuredRows is { Count: > 0 }
                ? ExtractStructuredAsync(input.StructuredRows, input.BusinessUnitId,
                    input.SourceDocumentName, ct, input.DocumentNarrative)
                : ExtractUnstructuredAsync(input, ct);

        public Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
        {
            NativePdfDocumentName = input.SourceDocumentName;
            NativePdfProcessingPath = input.ProcessingPath;
            NativePdfText = string.Join('\n',
                new[] { input.HeaderText }.Concat(input.LineItemRegions ?? []));
            Assert.Equal("native-requirement.pdf", input.SourceDocumentName);
            Assert.Equal(ExtractionProcessingPath.NativeParser, input.ProcessingPath);
            Assert.Contains("PDF-900", NativePdfText, StringComparison.Ordinal);
            Interlocked.Exchange(ref _nativePdfSeen, 1);
            return Task.FromResult(Success(Ext.Item(0.99, "PDF-900 pressure gauge", 9),
                ExtractionProcessingPath.NativeParser));
        }

        public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
            IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName,
            CancellationToken ct = default, string? documentNarrative = null)
        {
            NativeXlsxDocumentName = sourceName;
            Assert.Equal("priced-schedule.xlsx", sourceName);
            var row = Assert.Single(rows);
            Assert.Equal("XLSX-800", row.ManufacturerPartNumber);
            Assert.Equal("8", row.Quantity);
            Assert.Equal("EA", row.UnitOfMeasure);
            Interlocked.Exchange(ref _nativeXlsxSeen, 1);
            var item = Ext.Item(0.99, row.ProductName, int.Parse(row.Quantity!)) with
            {
                ManufacturerPartNumber = row.ManufacturerPartNumber,
                UnitOfMeasure = row.UnitOfMeasure
            };
            return Task.FromResult(Success(item, ExtractionProcessingPath.DeterministicRules));
        }

        private static ChunkedExtractionOutcome Success(
            LeadItemData item, ExtractionProcessingPath path) => new()
        {
            Status = ExtractionOutcomeStatus.Ok,
            Result = Ext.Result([item], 0.99),
            ExpectedItemCount = 1,
            ExtractedItemCount = 1,
            ProcessingPath = path
        };
    }

    private static byte[] NativePdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(30);
            page.Content().Text(
                "Customer RFQ PDF-2026-900. Please supply nine pressure gauges, manufacturer "
                + "part PDF-900, quantity 9 EA, stainless steel wetted parts, delivery DDP Jubail. "
                + "This paragraph is intentionally long enough to constitute a genuine native "
                + "PDF text layer rather than a footer or signature artefact.").FontSize(14);
        })).GeneratePdf();
    }

    private static byte[] NativeXlsx()
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("Priced Schedule");
        sheet.Cells[1, 1].Value = "Part Number";
        sheet.Cells[1, 2].Value = "Description";
        sheet.Cells[1, 3].Value = "Quantity";
        sheet.Cells[1, 4].Value = "Unit";
        sheet.Cells[2, 1].Value = "XLSX-800";
        sheet.Cells[2, 2].Value = "Temperature transmitter with thermowell";
        sheet.Cells[2, 3].Value = 8;
        sheet.Cells[2, 4].Value = "EA";
        return package.GetAsByteArray();
    }

    private sealed class RefusingDocumentIngestion : IDocumentIngestion
    {
        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated durable queue outage");
    }

    private static long UniqueBusinessUnitId()
        => 941_000_000L + Random.Shared.Next(1, 900_000);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<Exception> Exceptions { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exceptions.Add(exception);
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
