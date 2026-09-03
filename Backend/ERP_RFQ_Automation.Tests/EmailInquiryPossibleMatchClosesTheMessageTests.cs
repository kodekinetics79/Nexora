using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A message parked by a possible match must finish when a person decides the match.
///
/// <para><b>Measured on the live tenant (mailbox 9 / business unit 7) on 2026-09-03.</b> Of 332
/// ingested messages, 249 were real candidates and 77 reached no Lead by either link. Eight of
/// those 77 were this exact shape — three of them with <c>ParseStatus = Success</c>,
/// <c>TriageOutcome = Inquiry</c> and every component terminal:</para>
///
/// <code>
///  ID  | ParseStatus | TriageOutcome | asm |   Status    | StatusReason                   | AssembledLeadId
///  967 | Success     | Inquiry       |  17 | NeedsReview | assembly_lead_not_produced: ... | (null)
/// 1189 | Success     | Inquiry       | 241 | NeedsReview | assembly_lead_not_produced: ... | (null)
/// 1198 | Success     | Inquiry       | 250 | NeedsReview | assembly_lead_not_produced: ... | (null)
/// </code>
///
/// <para>Each had a <c>LeadIngestionOccurrence</c> classified
/// <c>PossibleMatchReviewRequired</c> at confidence 1.00000, a pending
/// <c>LeadMatchCandidate</c> naming a real Lead (450, 611, 606), and no way to ever leave that
/// state: <c>DecideMatchAsync</c> resolved the occurrence and the Lead and never touched the
/// assembly. The gate itself is correct — refusing to write a second commercial record for a
/// message that looks like one already held is the product working. It was ONE-WAY, which is the
/// defect: the human answer had nowhere to land.</para>
///
/// <para><b>Fixture fidelity.</b> The occurrence's <c>ExtractionJobId</c> is the ONLY route from
/// a match decision back to the message, and the shared <c>Intake(...)</c> helper in
/// <c>LeadIdentityApplicationServiceTests</c> leaves it null — so every existing decision test
/// exercises a shape in which this code cannot run at all. These fixtures set it, and set the
/// job's <c>EmailInquiryComponentId</c>, because that is what production writes (verified:
/// jobs 113/353/366 -> components 28/279/292 -> assemblies 17/241/250).</para>
/// </summary>
public sealed class EmailInquiryPossibleMatchClosesTheMessageTests
{
    private const long Bu = 91;
    private const long ConfigId = 9101;
    private const long IngestId = 9201;
    private const long JobId = 9301;

    [Fact]
    public async Task Confirming_the_match_links_the_parked_message_to_the_lead_it_belongs_to()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        var service = await SeedAsync(context);

        // The canonical inquiry that already exists.
        var original = Candidate("RFQ-2026/5501", "buyer@customer.example", 10);
        var created = await service.ReconcileAsync(original,
            Intake("original", "hash-original", extractionJobId: null));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);

        // The resend/amendment that arrives as an email component. Unresolved sender plus the
        // buyer's own reference is the live shape: corroborating reference, unresolved identity.
        context.ChangeTracker.Clear();
        var (assemblyId, _) = await SeedHeldEmailMessageAsync(context);
        var repeat = Candidate("RFQ-2026/5501", clientEmail: null, quantity: 25);
        var review = await service.ReconcileAsync(repeat,
            Intake("repeat", "hash-repeat", extractionJobId: JobId, sender: null));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);

        // PRODUCTION STATE, reproduced: held for a person, with no lead by either link.
        context.ChangeTracker.Clear();
        var parked = await context.EmailInquiryAssemblies.SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, parked.Status);
        Assert.Null(parked.AssembledLeadId);

        // The person answers the question the reason asked them.
        context.ChangeTracker.Clear();
        var candidate = await context.Set<LeadMatchCandidate>()
            .SingleAsync(x => x.OccurrenceId == review.OccurrenceId);
        var decided = await service.DecideMatchAsync(Bu, review.OccurrenceId,
            new MatchDecisionRequest("link", candidate.CandidateLeadId, candidate.Version,
                "Same request as the original.", "close-the-message-1"),
            "reviewer");
        Assert.Equal(created.LeadId, decided.LeadId);

        // THE ASSERTION THAT FAILS AGAINST THE OLD CODE.
        context.ChangeTracker.Clear();
        var closed = await context.EmailInquiryAssemblies.SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.Assembled, closed.Status);
        Assert.Equal(created.LeadId, closed.AssembledLeadId);
        // The reason asked the operator to decide. They have. It must stop asking.
        Assert.Null(closed.StatusReason);
    }

    [Fact]
    public async Task Rejecting_the_message_stops_it_asking_and_says_so_without_a_reason_code()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        var service = await SeedAsync(context);

        var original = Candidate("RFQ-2026/5502", "buyer@customer.example", 10);
        await service.ReconcileAsync(original, Intake("reject-original", "hash-ra", extractionJobId: null));

        context.ChangeTracker.Clear();
        var (assemblyId, _) = await SeedHeldEmailMessageAsync(context);
        var review = await service.ReconcileAsync(
            Candidate("RFQ-2026/5502", clientEmail: null, quantity: 25),
            Intake("reject-repeat", "hash-rb", extractionJobId: JobId, sender: null));
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);

        context.ChangeTracker.Clear();
        var candidate = await context.Set<LeadMatchCandidate>()
            .SingleAsync(x => x.OccurrenceId == review.OccurrenceId);
        await service.DecideMatchAsync(Bu, review.OccurrenceId,
            new MatchDecisionRequest("reject", candidate.CandidateLeadId, candidate.Version,
                "Not something we quote.", "close-the-message-2"),
            "reviewer");

        context.ChangeTracker.Clear();
        var closed = await context.EmailInquiryAssemblies.SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.NoInquiry, closed.Status);
        Assert.Null(closed.AssembledLeadId);
        Assert.Equal(EmailInquiryHoldReasons.MatchReviewRejectedDetail, closed.StatusReason);
        // Operator-facing text, not a machine code. Nothing snake_cased survives to the screen.
        Assert.DoesNotContain('_', closed.StatusReason!);
    }

    /// <summary>
    /// THE CONTROL. A manual upload raises the same possible match and owns no email message.
    /// It must be decided without touching an assembly and without throwing — this is the arm
    /// that proves the fix is keyed on real ownership rather than firing on everything.
    /// </summary>
    [Fact]
    public async Task A_decision_on_a_document_that_is_not_an_email_component_touches_no_message()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Bu);
        var service = await SeedAsync(context);
        var (assemblyId, _) = await SeedHeldEmailMessageAsync(context);

        await service.ReconcileAsync(Candidate("RFQ-2026/5503", "buyer@customer.example", 10),
            Intake("upload-original", "hash-ua", extractionJobId: null));
        context.ChangeTracker.Clear();
        var review = await service.ReconcileAsync(
            Candidate("RFQ-2026/5503", clientEmail: null, quantity: 25),
            Intake("upload-repeat", "hash-ub", extractionJobId: null, sender: null));
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);

        context.ChangeTracker.Clear();
        var candidate = await context.Set<LeadMatchCandidate>()
            .SingleAsync(x => x.OccurrenceId == review.OccurrenceId);
        await service.DecideMatchAsync(Bu, review.OccurrenceId,
            new MatchDecisionRequest("link", candidate.CandidateLeadId, candidate.Version,
                "Same request.", "close-the-message-3"),
            "reviewer");

        context.ChangeTracker.Clear();
        var untouched = await context.EmailInquiryAssemblies.SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryAssemblyStatus.NeedsReview, untouched.Status);
        Assert.Null(untouched.AssembledLeadId);
    }

    // ------------------------------------------------------------------ fixture

    private static async Task<LeadIdentityApplicationService> SeedAsync(ErpRfqAutomationContext context)
    {
        Seed.BusinessUnit(context, Bu);
        Seed.EmailConfig(context, ConfigId, Bu);
        Seed.EmailIngest(context, IngestId, ConfigId, "Success");
        await context.SaveChangesAsync();
        return new LeadIdentityApplicationService(context);
    }

    /// <summary>
    /// The live shape of assembly 241: every expected component terminal, the message held with
    /// the assembler's own reason string, and an extraction job that names the component.
    /// </summary>
    private static async Task<(long AssemblyId, long ComponentId)> SeedHeldEmailMessageAsync(
        ErpRfqAutomationContext context)
    {
        var existing = await context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.EmailIngestId == IngestId);
        if (existing is not null)
            return (existing.Id, existing.Components.First().Id);

        var assembly = new EmailInquiryAssembly
        {
            BusinessUnitId = Bu,
            EmailIngestId = IngestId,
            EmailConfigurationId = ConfigId,
            MessageKey = $"msg-{IngestId}",
            ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
            ExpectedComponentCount = 1,
            CompletedComponentCount = 1,
            Status = EmailInquiryAssemblyStatus.NeedsReview,
            StatusReason =
                $"{EmailInquiryHoldReasons.LeadNotProduced}: {EmailInquiryHoldReasons.LeadNotProducedDetail}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        assembly.Components.Add(new EmailInquiryComponent
        {
            BusinessUnitId = Bu,
            ComponentKey = $"email:msg-{IngestId}:body",
            Ordinal = 0,
            Kind = EmailInquiryComponentKind.Body,
            Status = EmailInquiryComponentStatus.Completed,
            ExtractionJobId = JobId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        context.Add(assembly);
        await context.SaveChangesAsync();

        var componentId = assembly.Components.First().Id;
        context.Add(new ExtractionJob
        {
            Id = JobId,
            BatchId = Guid.NewGuid(),
            BusinessUnitId = Bu,
            SourceType = ExtractionSourceType.Email,
            // The ownership authority, read straight off the job row — the same column the
            // assembly fence in ExtractionWorker trusts.
            EmailInquiryComponentId = componentId,
            ContentHash = new string('b', 64),
            StoragePath = "/nonexistent/extraction/body.eml",
            FileName = "body.eml",
            FileType = "eml",
            Attempts = 1
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        return (assembly.Id, componentId);
    }

    private static Lead Candidate(string? rfq, string? clientEmail, int quantity)
    {
        var lead = new Lead
        {
            Rfqno = rfq,
            BuyersName = clientEmail is null ? null : "Buyer",
            RecDate = DateTime.UtcNow,
            LeadSource = "Email",
            CreatedBy = "test",
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = Bu,
            EmailIngestsId = IngestId,
            Clientemail = clientEmail,
            RequiresCommercialReview = true
        };
        lead.LeadItems.Add(new LeadItem
        {
            LineItemNo = "1",
            ManufacturerPartNumber = "PN-5500",
            ProductShortDescription = "Ball valve",
            Quantity = quantity,
            UnitOfMeasure = "EA"
        });
        return lead;
    }

    private static LeadIntakeDescriptor Intake(
        string key, string hash, long? extractionJobId, string? sender = "buyer@customer.example")
        => new(
            Guid.NewGuid(), "Email", key, null, null, "test", sender, "RFQ", $"{key}.eml",
            "message/rfc822", 100, hash.PadRight(64, '0')[..64],
            null, extractionJobId, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic, false, 0, "User", "tester", $"test:{key}");
}
