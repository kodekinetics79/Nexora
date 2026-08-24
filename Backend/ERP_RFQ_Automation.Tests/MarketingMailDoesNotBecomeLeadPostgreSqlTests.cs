using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Conversational;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;
using QuestPDF.Fluent;
using QuestPDF.Infrastructure;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE assertion pair for "email ingestion is intelligent ingestion, not ingest-all logic".
///
/// <para><b>The defect.</b> On the live tenant, 19 of 22 leads had no client. They were not
/// mis-matched customers — they had no lines at all. Marketing mail ("Contact Us: Digital
/// Marketing", "Your competition started Yelp Ads") reached the conversational extractor, the
/// extractor correctly reported "No requestable items found in message body", and
/// <see cref="EmailInquiryLeadAssembler"/> merged that into an empty item list, hard-coded
/// <c>ExtractionOutcomeStatus.Ok</c> and minted a Lead anyway. The sales pipeline was carrying
/// agency mail as work.</para>
///
/// <para><b>Why both tests are here and neither is sufficient alone.</b> Any rule that stops
/// marketing can be made to pass on its own by stopping more mail; the expensive failure mode of
/// this product is the opposite one, a real enquiry that never becomes a Lead. So the marketing
/// case and the first-time-buyer case are asserted against the SAME pipeline, the same unknown
/// sender shape and — pinned explicitly below — the same triage verdict. The only thing that
/// differs between them is what the extractor found, which is the only thing that legitimately
/// can differ before a human has read the message.</para>
///
/// <para>Everything is real except the model: the migrated database, the queue and its advisory
/// -lock claim, the ingestion gateway, evidence storage, the coordinator, the persister and the
/// assembler. See <see cref="EmailToLeadHarness"/>.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class MarketingMailDoesNotBecomeLeadPostgreSqlTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _database = database;
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), "nexora-marketing-" + Guid.NewGuid().ToString("N")[..12]);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        try { if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true); }
        catch (IOException) { /* a temp directory that outlives the run is not a test failure */ }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Verbatim shape of the message that produced Lead 488 in production: a person at a digital
    /// agency, writing prose, to a person. No List-Unsubscribe, no Auto-Submitted, no bulk
    /// Precedence — there is nothing here for a header rule to see.
    /// </summary>
    private const string MarketingSubject = "Contact Us: Digital Marketing";

    private const string MarketingBody =
        "Hi there,\n\nI came across your website and noticed a few opportunities to improve your "
        + "search ranking and social presence. We have helped distributors in your sector grow "
        + "inbound enquiries significantly.\n\nWould you be open to a short call this week?\n\n"
        + "Best regards,\nJordan\nGrowth Studio";

    /// <summary>
    /// A first-time buyer. Unknown sender, no RFQ vocabulary, no quantity, no unit of measure,
    /// no request verb, nothing attached — and unambiguously a deal.
    /// </summary>
    private const string BuyerSubject = "Question about your switchgear";

    private const string BuyerBody =
        "Do you carry Schneider NSX250N MCCBs? We are building a plant in Dammam and would like "
        + "to know whether you can supply them.";

    // =====================================================================================
    // 1. THE REGRESSION. Marketing mail must not manufacture a Lead.
    // =====================================================================================

    [Fact]
    public async Task Marketing_mail_that_asks_for_nothing_is_held_in_triage_and_makes_no_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"marketing-{Guid.NewGuid():N}@growthstudio.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations
                .AddScoped<IConversationalExtractionService, NoRequestableItemsExtractor>());

        var message = BuildProseMessage(
            messageId, "agency@growthstudio.example", MarketingSubject, MarketingBody);

        // The decision the real gate reaches for this message, not a stand-in. It is Uncertain,
        // which is correct and is deliberately NOT the discriminator — see the pinned Theory in
        // EmailTriageNoReplyProcurementTests for why nothing at this layer can separate this
        // message from the buyer below.
        var triage = TriageOf(message, MarketingBody);
        Assert.Equal(EmailTriageOutcome.Uncertain, triage.Outcome);

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 1,
            triage: triage, clientEmail: "agency@growthstudio.example");
        Assert.Equal(1, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // THE ASSERTION THE PRODUCT OWNER IS ASKING FOR. No Lead, and no Lead line either — a
        // zero-line Lead is the exact artefact that filled the pipeline.
        Assert.Empty(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());

        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(a => a.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, assembly.Status);
        Assert.Null(assembly.AssembledLeadId);

        // CAPTURED AND HELD, NOT DROPPED. The reason is typed and the operator sentence is the
        // one from EmailInquiryHoldReasons, so the Email Intake screen renders an explanation
        // rather than a blank held row.
        Assert.NotNull(assembly.StatusReason);
        Assert.StartsWith(EmailInquiryHoldReasons.NoRequestableContent, assembly.StatusReason!,
            StringComparison.Ordinal);
        Assert.Contains(EmailInquiryHoldReasons.NoRequestableContentDetail, assembly.StatusReason!,
            StringComparison.Ordinal);

        // NOTHING IS LOST. The raw message is in durable evidence storage with a digest, and the
        // component that was read is still Completed with its result row intact — so a reviewer
        // sees what was read, and a later decision has evidence to work from.
        Assert.NotNull(assembly.RawEvidenceUri);
        Assert.NotNull(assembly.RawEvidenceSha256);
        var component = Assert.Single(await context.EmailInquiryComponents.AsNoTracking()
            .Where(c => c.AssemblyId == assemblyId).ToListAsync());
        Assert.Equal(EmailInquiryComponentStatus.Completed, component.Status);
        Assert.Equal(1, await context.Set<EmailInquiryComponentResult>().AsNoTracking()
            .CountAsync(r => r.AssemblyId == assemblyId));
    }

    // =====================================================================================
    // 2. THE CONTROL, AND THE ONE THAT MATTERS MOST. A real enquiry must survive.
    //
    // This test passes both before and after the change: it is not a regression test, it is the
    // guard that proves the regression test above was not bought by suppressing mail. Its value
    // is that it fails loudly against the OTHER candidate fix — a triage-time rule stopping an
    // Uncertain message that carries no commercial vocabulary — because this message carries
    // none: no "rfq", no "tender", no "please quote", no quantity-with-unit, no attachment. The
    // triage assertion below is what makes that concrete rather than a claim in a commit message.
    // =====================================================================================

    [Fact]
    public async Task A_first_time_buyer_with_no_RFQ_vocabulary_still_becomes_a_Lead()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"first-buyer-{Guid.NewGuid():N}@newbuyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations
                .AddScoped<IConversationalExtractionService, OneAnchoredItemExtractor>());

        var message = BuildProseMessage(
            messageId, "hello@newbuyer.example", BuyerSubject, BuyerBody);

        // IDENTICAL TRIAGE TO THE MARKETING MAIL — same outcome, same single reason code. If a
        // rule anywhere upstream of extraction ever starts stopping mail on this shape, this
        // assertion is the one that will be standing in its way, and it should be.
        var triage = TriageOf(message, BuyerBody);
        Assert.Equal(EmailTriageOutcome.Uncertain, triage.Outcome);
        Assert.Equal(new[] { EmailTriageReasonCodes.NoSignal }, triage.ReasonCodes);

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 1,
            triage: triage, clientEmail: "hello@newbuyer.example");
        Assert.Equal(1, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var lead = Assert.Single(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());
        var line = Assert.Single(await context.LeadItems.AsNoTracking()
            .Where(i => i.LeadId == lead.Id).ToListAsync());
        Assert.Contains("NSX250N", line.ProductShortName!, StringComparison.Ordinal);

        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(a => a.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
        Assert.Equal(lead.Id, assembly.AssembledLeadId);
    }

    // =====================================================================================
    // 3. One extracted line ANYWHERE in the message keeps the Lead — the guard reads the whole
    // merged message, not the body it happened to be triggered by.
    //
    // "Please see attached" is the commonest real RFQ shape there is: the covering note yields
    // nothing on its own, and every priced line is in the attachment. A guard placed on the body
    // extractor's verdict rather than on the merge would destroy exactly this message, so the
    // distinction is asserted rather than assumed.
    // =====================================================================================

    [Fact]
    public async Task A_covering_note_with_nothing_in_it_still_becomes_a_Lead_when_an_attachment_has_lines()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"see-attached-{Guid.NewGuid():N}@newbuyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations
                .AddScoped<IConversationalExtractionService, NoRequestableItemsExtractor>());

        const string note = "Hello,\n\nPlease see attached.\n\nRegards,\nProcurement";
        var message = BuildProseMessage(
            messageId, "hello@newbuyer.example", "Our requirement", note);
        var csv = EmailToLeadHarness.Attachment(
            "requirement.csv", "text/csv",
            System.Text.Encoding.UTF8.GetBytes(
                "Part Number,Description,Quantity,Unit\n"
                + "ATT-4200,Cable tray 300mm perforated,40,EA\n"));
        message.Body = new Multipart("mixed") { message.Body, csv };

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 2,
            triage: TriageOf(message, note), clientEmail: "hello@newbuyer.example");
        Assert.Equal(2, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        var lead = Assert.Single(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());
        var line = Assert.Single(await context.LeadItems.AsNoTracking()
            .Where(i => i.LeadId == lead.Id).ToListAsync());
        Assert.Equal(40, line.Quantity);

        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(a => a.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, assembly.Status);
    }

    // =====================================================================================
    // 4. THE TWO REASONS A MESSAGE CAN NAME ZERO LINES ARE NOT THE SAME REASON, AND THE
    //    OPERATOR IS THE ONE WHO HAS TO TELL THEM APART.
    //
    // Test 1 above is a message that WAS read and asked for nothing. This is a message we FAILED
    // TO READ: a scanned RFQ whose OCR came back partial. ChunkedExtractionService reports that
    // honestly — NeedsReview, a non-null result, zero items, and a POSITIVE expected count
    // ("OCR was incomplete; omitted content requires review") — and ExtractionWorker diverts only
    // on Failed or a null result, so the outcome is recorded and the assembler runs on it.
    //
    // Both messages are held, and both should be. What must differ is the sentence, because the
    // sentence is the whole of what the operator acts on: "they asked for nothing" sends them to
    // chase a buyer who did their part, when the document is sitting in the message waiting to be
    // re-read. Before this branch existed, a real customer RFQ was held with the marketing
    // sentence on it.
    // =====================================================================================

    [Fact]
    public async Task A_document_we_could_not_read_is_not_held_with_the_sentence_for_a_message_that_asked_for_nothing()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"partial-ocr-{Guid.NewGuid():N}@newbuyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        // The BODY is a covering note that genuinely asks for nothing — that is the real shape of
        // "please see attached", and it is what makes the attachment the only thing that could
        // have carried the request. The chunked extractor is the one substitution: OCR quality is
        // not reproducible from a byte stream, so the outcome it produces on a bad scan is stated
        // directly. Everything else — capture, the queue, the real PDF reader, the coordinator,
        // the assembler — is real.
        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations
                .AddScoped<IConversationalExtractionService, NoRequestableItemsExtractor>()
                .AddScoped<IChunkedExtractionService, PartiallyReadDocumentExtractor>());

        const string note = "Hi,\n\nOur requirement is attached, scanned from the signed original."
            + "\n\nRegards,\nProcurement";
        var message = BuildProseMessage(
            messageId, "hello@newbuyer.example", "Our requirement (scanned)", note);
        message.Body = new Multipart("mixed")
        {
            message.Body,
            EmailToLeadHarness.Attachment("scanned-requirement.pdf", "application/pdf", ScannedPdf())
        };

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 2,
            triage: TriageOf(message, note), clientEmail: "hello@newbuyer.example");
        Assert.Equal(2, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // STILL HELD, AND STILL NO LEAD. Nothing about this change lowers the bar for minting one.
        Assert.Empty(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(a => a.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, assembly.Status);
        Assert.Null(assembly.AssembledLeadId);

        // THE ASSERTION THIS TEST EXISTS FOR. A different typed reason and a different sentence —
        // and, said explicitly because it is the defect, NOT the one that tells the operator this
        // sender asked for nothing.
        Assert.NotNull(assembly.StatusReason);
        Assert.StartsWith(EmailInquiryHoldReasons.ContentNotRecovered, assembly.StatusReason!,
            StringComparison.Ordinal);
        Assert.Contains(EmailInquiryHoldReasons.ContentNotRecoveredDetail, assembly.StatusReason!,
            StringComparison.Ordinal);
        Assert.DoesNotContain(EmailInquiryHoldReasons.NoRequestableContentDetail,
            assembly.StatusReason!, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 5. THE SAME UNREADABLE DOCUMENT, IN THE OTHER SHAPE PRODUCTION EMITS — AND THE ONE THE
    //    ORIGINAL DISCRIMINATOR GOT BACKWARDS.
    //
    // Test 4 above pins a partial-OCR scan whose parser still found text regions, so the outcome
    // carries a POSITIVE ExpectedItemCount and the old `expected > 0` test happened to classify
    // it correctly. That is only one of the two shapes ChunkedExtractionService produces, and it
    // is the kinder one.
    //
    // When OCR degrades far enough that NO line-item region is parsed — the normal outcome for a
    // bad scan, not an exotic one — the service takes its whole-document branch
    // (`if (expected == 0)`) and sets `ExpectedItemCount = items0.Count`: the MODEL'S OWN item
    // count. For a page it could not read, that is zero. The outcome is therefore NeedsReview,
    // non-null result, zero items, zero expected, "OCR was incomplete" — indistinguishable from
    // marketing mail to any test that only looks at the count.
    //
    // So the customer's scanned RFQ was held with the marketing sentence: "read in full and
    // names no product, quantity or specification anywhere". It was not read in full. The whole
    // point of splitting the two sentences is what the operator does next, and that one sends
    // them to chase a buyer who did their part.
    // =====================================================================================

    [Fact]
    public async Task A_scan_whose_OCR_recovered_no_regions_is_not_held_with_the_asked_for_nothing_sentence()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"whole-doc-ocr-{Guid.NewGuid():N}@newbuyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations
                .AddScoped<IConversationalExtractionService, NoRequestableItemsExtractor>()
                .AddScoped<IChunkedExtractionService, UnreadableScanExtractor>());

        const string note = "Hi,\n\nOur requirement is attached, scanned from the signed original."
            + "\n\nRegards,\nProcurement";
        var message = BuildProseMessage(
            messageId, "hello@newbuyer.example", "Our requirement (scanned)", note);
        message.Body = new Multipart("mixed")
        {
            message.Body,
            EmailToLeadHarness.Attachment("scanned-requirement.pdf", "application/pdf", ScannedPdf())
        };

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 2,
            triage: TriageOf(message, note), clientEmail: "hello@newbuyer.example");
        Assert.Equal(2, schedule.Scheduled);

        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // Still no Lead — the bar for minting one is untouched.
        Assert.Empty(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(a => a.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, assembly.Status);
        Assert.Null(assembly.AssembledLeadId);

        // THE FIXTURE'S HONESTY, ASSERTED. Both stored results carry a ZERO expected count, so
        // the old discriminator saw exactly what marketing mail looks like. If a future change
        // makes this fixture carry a positive count, this test stops proving anything and this
        // assertion is what will say so.
        var stored = await context.Set<EmailInquiryComponentResult>().AsNoTracking()
            .Where(r => r.AssemblyId == assemblyId).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.All(stored, r => Assert.Equal(0, r.ExpectedItemCount));
        Assert.All(stored, r => Assert.Equal(0, r.ExtractedItemCount));

        // THE ASSERTION THIS TEST EXISTS FOR.
        Assert.NotNull(assembly.StatusReason);
        Assert.StartsWith(EmailInquiryHoldReasons.ContentNotRecovered, assembly.StatusReason!,
            StringComparison.Ordinal);
        Assert.DoesNotContain(EmailInquiryHoldReasons.NoRequestableContentDetail,
            assembly.StatusReason!, StringComparison.Ordinal);
    }

    // =====================================================================================
    // 6. THE DANGEROUS DIRECTION, PROVED EXPLICITLY: AN EXTRACTION THAT FAILED IS NOT AN
    //    EXTRACTION THAT FOUND NOTHING.
    //
    // "Zero lines" is only allowed to mean "the reader ran and the message asks for nothing".
    // If a refusal, a timeout or a dead letter could reach the same branch, today's visible-but-
    // wrong outcome would become an invisible lost RFQ — strictly worse, and the exact class of
    // defect this integration branch exists to remove.
    //
    // The guarantee is structural rather than a rule: ExtractionWorker diverts on
    // `Status == Failed || Result is null` BEFORE any component result is written
    // (ExtractionWorker.cs, the failure branch), so a failed read leaves no row for the
    // assembler's zero-line gate to read. That is asserted here directly — no result rows, no
    // Lead, and above all NOT the sentence that tells a customer their message asked for
    // nothing.
    // =====================================================================================

    [Fact]
    public async Task An_extraction_that_failed_is_never_disposed_of_as_a_message_that_asked_for_nothing()
    {
        var businessUnitId = UniqueBusinessUnitId();
        var messageId = $"failed-read-{Guid.NewGuid():N}@newbuyer.example";
        await using (var connection = await _database.OpenConnectionAsync())
            await EmailToLeadHarness.SeedTenantAsync(connection, businessUnitId, messageId);

        // A body that is unmistakably a real enquiry. The point of the test is that the SYSTEM
        // could not read it, so the message must never be closed against the sender.
        await using var services = EmailToLeadHarness.BuildGraph(
            _database.ConnectionString, _storageRoot, new EmailToLeadHarness.RefusingLlm(),
            registrations => registrations
                .AddScoped<IConversationalExtractionService, FailedReadExtractor>());

        var message = BuildProseMessage(
            messageId, "hello@newbuyer.example", BuyerSubject, BuyerBody);

        var (_, assemblyId, schedule) = await EmailToLeadHarness.CaptureAndScheduleAsync(
            services, businessUnitId, message, expectedComponentCount: 1,
            triage: TriageOf(message, BuyerBody), clientEmail: "hello@newbuyer.example");
        Assert.Equal(1, schedule.Scheduled);

        // The job dead-letters by design, so the harness's "no failed jobs" guard is off. That is
        // the condition under test, not an accident of the fixture.
        await EmailToLeadHarness.DrainQueueAsync(services, businessUnitId, assertNoFailures: false);

        using var scope = services.CreateScope();
        using var tenant = scope.ServiceProvider
            .GetRequiredService<ITenantScopeAccessor>().Push(businessUnitId);
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // The read really did fail: the job is dead-lettered.
        Assert.Contains(
            await context.Set<ExtractionJob>().AsNoTracking()
                .Where(j => j.BusinessUnitId == businessUnitId).Select(j => j.Status).ToListAsync(),
            status => status == ExtractionStatus.DeadLetter);

        // No result row exists, so the zero-line gate is structurally unreachable for this
        // message. This is the assertion that makes the guarantee a property rather than a hope.
        Assert.Empty(await context.Set<EmailInquiryComponentResult>().AsNoTracking()
            .Where(r => r.AssemblyId == assemblyId).ToListAsync());

        // No Lead was invented from a failed read either.
        Assert.Empty(await context.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId).ToListAsync());

        // And the message is held for a person WITHOUT being told the sender asked for nothing,
        // and without being closed. NoInquiry here would be the invisible lost RFQ.
        var assembly = await context.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(a => a.Id == assemblyId);
        Assert.NotEqual(EmailInquiryAssemblyStatus.NoInquiry, assembly.Status);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, assembly.Status);
        Assert.Null(assembly.AssembledLeadId);
        Assert.DoesNotContain(EmailInquiryHoldReasons.NoRequestableContent,
            assembly.StatusReason ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(EmailInquiryHoldReasons.NoRequestableContentDetail,
            assembly.StatusReason ?? string.Empty, StringComparison.Ordinal);

        // Nothing was thrown away: the original message is still in durable evidence storage.
        Assert.NotNull(assembly.RawEvidenceUri);
    }

    // ---- helpers ---------------------------------------------------------------------------

    /// <summary>
    /// The real classifier's verdict for this message, built through the same normalizer the
    /// poller uses. Party type is null on purpose: none of these senders is in master data,
    /// which is the whole point — a first-time buyer never is either.
    /// </summary>
    private static EmailTriageDecision TriageOf(MimeMessage message, string body)
    {
        var parts = EmailBodyNormalizer.Normalize(body);
        var from = message.From.Mailboxes.First().Address;
        return DeterministicEmailTriage.Evaluate(new EmailTriageSignals
        {
            Subject = message.Subject,
            FreshBody = parts.Fresh ?? string.Empty,
            FromAddress = from,
            FromDomain = from.Split('@').Last(),
            SenderPartyType = null,
            HasAttachments = message.Attachments.Any(),
            BodyEmptyAfterStrip = parts.BodyEmptyAfterStrip,
            HasInReplyTo = !string.IsNullOrWhiteSpace(message.InReplyTo),
            HasReferences = message.References?.Count > 0
        });
    }

    private static MimeMessage BuildProseMessage(
        string messageId, string from, string subject, string body)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Body = new TextPart("plain") { Text = body }
        };
        message.From.Add(new MailboxAddress(from.Split('@').First(), from));
        message.To.Add(new MailboxAddress("Nexora", "rfq@nexora.example"));
        message.MessageId = messageId;
        message.Date = new DateTimeOffset(2026, 8, 16, 16, 12, 0, TimeSpan.Zero);
        return message;
    }

    private static long UniqueBusinessUnitId()
        => 943_000_000L + Random.Shared.Next(1, 900_000);

    /// <summary>
    /// The production shape of "I read this and it asks for nothing", copied from
    /// <c>ConversationalExtractionService</c>: <see cref="ExtractionOutcomeStatus.NeedsReview"/>
    /// with a NON-NULL result carrying an empty item list, and that exact review sentence.
    ///
    /// <para>The status matters to the fixture's honesty. <c>ExtractionWorker</c> diverts only on
    /// <c>Failed</c> or a null result, so this outcome is persisted and the assembler runs — which
    /// is precisely how the empty Lead was reached. A double returning <c>Ok</c> here would
    /// exercise a shape the conversational path never emits.</para>
    /// </summary>
    private sealed class NoRequestableItemsExtractor : IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
            => Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.NeedsReview,
                Result = Ext.Result([], 0.9),
                ExpectedItemCount = 0,
                ExtractedItemCount = 0,
                // THE CONSTANT, not a copy of it. The assembler's "read in full" sentence is
                // gated on this exact string, so a fixture holding its own copy would keep
                // passing after the two drifted apart — which is the only way this gate can
                // silently start telling customers the wrong thing.
                ReviewReason = ConversationalExtractionService.NoRequestableItemsReviewReason,
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
    }

    /// <summary>The same message read by a model that DID find the request in it.</summary>
    private sealed class OneAnchoredItemExtractor : IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
            => Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                Result = Ext.Result([Ext.Item(0.94, "Schneider NSX250N MCCB", 1)], 0.9),
                ExpectedItemCount = 1,
                ExtractedItemCount = 1,
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
    }

    /// <summary>
    /// A scanned page whose OCR came back partial, in the exact shape
    /// <c>ChunkedExtractionService</c> emits for one: <see cref="ExtractionOutcomeStatus.NeedsReview"/>
    /// with a NON-NULL result, an empty item list, and — the discriminator — an
    /// <c>ExpectedItemCount</c> above zero, because text regions WERE parsed off the page and
    /// simply produced no line.
    /// </summary>
    private sealed class PartiallyReadDocumentExtractor : IChunkedExtractionService
    {
        private static ChunkedExtractionOutcome PartialOcr() => new()
        {
            Status = ExtractionOutcomeStatus.NeedsReview,
            Result = Ext.Result([], 0.4),
            ExpectedItemCount = 14,
            ExtractedItemCount = 0,
            ReviewReason = "OCR was incomplete; omitted content requires review.",
            ProcessingPath = ExtractionProcessingPath.LocalOcr
        };

        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
            => Task.FromResult(PartialOcr());

        public Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
            => Task.FromResult(PartialOcr());

        public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
            IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName,
            CancellationToken ct = default, string? documentNarrative = null)
            => Task.FromResult(PartialOcr());
    }

    /// <summary>
    /// The OTHER production shape of a scan that could not be read, and the one the count-based
    /// discriminator could not see.
    ///
    /// <para>Copied from <c>ChunkedExtractionService</c>'s whole-document branch, which is taken
    /// when the parser recovers NO line-item regions: <c>ExpectedItemCount</c> is set to the
    /// model's own item count, so a document the model could not read reports ZERO — the same
    /// number marketing mail reports. The only thing separating the two is the review reason,
    /// which is why the reason is what the gate now reads.</para>
    /// </summary>
    private sealed class UnreadableScanExtractor : IChunkedExtractionService
    {
        private static ChunkedExtractionOutcome WholeDocumentPartialOcr() => new()
        {
            Status = ExtractionOutcomeStatus.NeedsReview,
            Result = Ext.Result([], 0.4),
            ExpectedItemCount = 0,
            ExtractedItemCount = 0,
            ReviewReason = "OCR was incomplete; omitted content requires review.",
            OcrStatus = ExtractionOcrStatus.Partial,
            ProcessingPath = ExtractionProcessingPath.LocalOcr
        };

        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
            => Task.FromResult(WholeDocumentPartialOcr());

        public Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(
            DocumentExtractionInput input, CancellationToken ct = default)
            => Task.FromResult(WholeDocumentPartialOcr());

        public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
            IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName,
            CancellationToken ct = default, string? documentNarrative = null)
            => Task.FromResult(WholeDocumentPartialOcr());
    }

    /// <summary>
    /// A read that FAILED, in the shape every failing path produces: <c>Failed</c> with a NULL
    /// result. Permanent so the job dead-letters on its first attempt instead of spending the
    /// test's liveness window on backoff — the disposition under test is identical either way,
    /// because ExtractionWorker diverts on the status, not on the retry budget.
    /// </summary>
    private sealed class FailedReadExtractor : IConversationalExtractionService
    {
        public Task<ChunkedExtractionOutcome> ExtractAsync(
            DocumentExtractionInput input, bool threadContinuation, CancellationToken ct = default)
            => Task.FromResult(new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Failed,
                PermanentFailure = true,
                Result = null,
                ExpectedItemCount = 0,
                ExtractedItemCount = 0,
                ReviewReason = "The model returned no usable result for this message body.",
                ProcessingPath = ExtractionProcessingPath.NativeParser
            });
    }

    /// <summary>A real PDF, so the reader, the storage path and the queue all do real work; its
    /// text layer is irrelevant because the extractor above states the outcome directly.</summary>
    private static byte[] ScannedPdf()
    {
        QuestPDF.Settings.License = LicenseType.Community;
        return QuestPDF.Fluent.Document.Create(container => container.Page(page =>
        {
            page.Margin(30);
            page.Content().Text(
                "REQUEST FOR QUOTATION 44-1180. Scanned from the signed original. The schedule of "
                + "requirements continues over the following pages and is reproduced here at a "
                + "quality the page reader recovers only in part.").FontSize(14);
        })).GeneratePdf();
    }
}
