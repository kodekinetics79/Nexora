using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadIdentityApplicationServiceTests
{
    [Fact]
    public async Task New_duplicate_revision_and_separate_inquiry_preserve_one_canonical_identity()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(71);
        Seed.BusinessUnit(context, 71); Seed.EmailConfig(context, 7101, 71); Seed.EmailIngest(context, 7201, 7101, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var first = Candidate(71, 7201, "RFQ-ACME-9", "buyer@acme.test", 10);
        var created = await service.ReconcileAsync(first, Intake("first", "hash-a", Guid.NewGuid()));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);
        Assert.Equal(1, created.RevisionNumber);
        Assert.NotEqual(0, created.LeadId);
        Assert.False(string.IsNullOrWhiteSpace(created.NexoraSerial));

        context.ChangeTracker.Clear();
        var duplicate = await service.ReconcileAsync(Candidate(71, 7201, "RFQ-ACME-9", "buyer@acme.test", 10),
            Intake("resend", "hash-a", Guid.NewGuid()));
        Assert.Equal(LeadOccurrenceClassification.ExactDuplicate, duplicate.Classification);
        Assert.Equal(created.LeadId, duplicate.LeadId);
        Assert.Equal(created.NexoraSerial, duplicate.NexoraSerial);
        Assert.False(duplicate.ShouldRoute);

        context.ChangeTracker.Clear();
        var revision = await service.ReconcileAsync(Candidate(71, 7201, "RFQ-ACME-9", "buyer@acme.test", 15),
            Intake("changed", "hash-b", Guid.NewGuid()));
        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(created.NexoraSerial, revision.NexoraSerial);
        Assert.Equal(2, revision.RevisionNumber);
        Assert.False(revision.ShouldRoute);
        Assert.Equal(15, (await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId)).LeadItems.Single().Quantity);

        context.ChangeTracker.Clear();
        var separate = await service.ReconcileAsync(Candidate(71, 7201, "RFQ-ACME-10", "buyer@acme.test", 10),
            Intake("separate", "hash-c", Guid.NewGuid()));
        Assert.Equal(LeadOccurrenceClassification.New, separate.Classification);
        Assert.NotEqual(created.LeadId, separate.LeadId);

        Assert.Equal(2, await context.Leads.CountAsync());
        Assert.Equal(4, await context.Set<LeadIngestionOccurrence>().CountAsync());
        Assert.Equal(3, await context.Set<LeadRevision>().CountAsync());
        var analytics = await service.GetAnalyticsAsync(71, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1));
        Assert.Equal(4, analytics.Metrics.Single(x => x.Key == "ingestion-volume").Numerator);
        Assert.Equal(2, analytics.Metrics.Single(x => x.Key == "leads-received").Numerator);
        Assert.Equal(1, analytics.Metrics.Single(x => x.Key == "duplicate-rate").Numerator);
        Assert.Equal(1, analytics.Metrics.Single(x => x.Key == "revision-rate").Numerator);
    }

    [Fact]
    public async Task Unresolved_similar_submission_requires_review_and_cross_customer_hash_does_not_merge()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(72);
        Seed.BusinessUnit(context, 72); Seed.EmailConfig(context, 7201, 72); Seed.EmailIngest(context, 7301, 7201, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var a = await service.ReconcileAsync(Candidate(72, 7301, "CUSTOMER-A", "a@customer.test", 10), Intake("a", "same-hash", Guid.NewGuid()));
        context.ChangeTracker.Clear();
        var b = await service.ReconcileAsync(Candidate(72, 7301, "CUSTOMER-B", "b@customer.test", 10), Intake("b", "same-hash", Guid.NewGuid()));
        Assert.Equal(LeadOccurrenceClassification.New, b.Classification);
        Assert.NotEqual(a.LeadId, b.LeadId);

        context.ChangeTracker.Clear();
        var unresolved = Candidate(72, 7301, null, null, 10);
        var review = await service.ReconcileAsync(unresolved, Intake("unknown", "unknown-hash", Guid.NewGuid(), sender: null));
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        var match = Assert.Single(await context.Set<LeadMatchCandidate>().ToListAsync());
        context.ChangeTracker.Clear();
        var decision = new MatchDecisionRequest("revision", match.CandidateLeadId, match.Version, "Customer confirmed this update.", "review-decision-1");
        var decided = await service.DecideMatchAsync(72, review.OccurrenceId, decision, "reviewer");
        Assert.Equal(LeadOccurrenceClassification.Revision, decided.Classification);
        Assert.Equal(match.CandidateLeadId, decided.LeadId);
        var replay = await service.DecideMatchAsync(72, review.OccurrenceId, decision, "reviewer");
        Assert.Equal(decided.RevisionId, replay.RevisionId);
        Assert.Equal(2, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == match.CandidateLeadId));
    }

    [Fact]
    public async Task External_processing_is_fail_closed_above_ten_percent()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(73);
        Seed.BusinessUnit(context, 73); Seed.EmailConfig(context, 7301, 73); Seed.EmailIngest(context, 7401, 7301, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);
        var external = Intake("external", "h-ext", Guid.NewGuid()) with
        { ProcessingPath = LeadProcessingPath.ExternalModel, ExternalAiUsed = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReconcileAsync(Candidate(73, 7401, "RFQ-X", "x@test", 1), external));
        Assert.Empty(await context.Set<LeadIngestionOccurrence>().ToListAsync());
    }

    [Fact]
    public async Task Revision_persists_added_removed_modified_and_unchanged_evidence()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(74);
        Seed.BusinessUnit(context, 74); Seed.EmailConfig(context, 7401, 74); Seed.EmailIngest(context, 7501, 7401, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);
        var original = Candidate(74, 7501, "RFQ-DIFF", "buyer@diff.test", 10);
        original.LeadItems.Add(new LeadItem { LineItemNo = "2", ManufacturerPartNumber = "REMOVE-ME", Quantity = 5, UnitOfMeasure = "EA" });
        await service.ReconcileAsync(original, Intake("diff-original", "diff-a", Guid.NewGuid(), "buyer@diff.test"));

        context.ChangeTracker.Clear();
        var changed = Candidate(74, 7501, "RFQ-DIFF", "buyer@diff.test", 12);
        changed.LeadItems.Add(new LeadItem { LineItemNo = "3", ManufacturerPartNumber = "ADDED", Quantity = 1, UnitOfMeasure = "EA" });
        var result = await service.ReconcileAsync(changed, Intake("diff-revision", "diff-b", Guid.NewGuid(), "buyer@diff.test"));

        var differences = await context.Set<LeadRevisionDifference>().Where(x => x.LeadRevisionId == result.RevisionId).ToListAsync();
        Assert.Contains(differences, x => x.Scope == "Line" && x.ChangeType == LeadRevisionChangeType.Modified);
        Assert.Contains(differences, x => x.Scope == "Line" && x.ChangeType == LeadRevisionChangeType.Removed);
        Assert.Contains(differences, x => x.Scope == "Line" && x.ChangeType == LeadRevisionChangeType.Added);
        Assert.Contains(differences, x => x.Scope == "Field" && x.ChangeType == LeadRevisionChangeType.Unchanged);
    }

    private static Lead Candidate(long bu, long ingestId, string? rfq, string? email, int quantity)
    {
        var lead = new Lead { Rfqno = rfq, BuyersName = email is null ? null : "Buyer", RecDate = DateTime.UtcNow,
            LeadSource = "ManualUpload", CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = bu,
            EmailIngestsId = ingestId, Clientemail = email, RequiresCommercialReview = true };
        lead.LeadItems.Add(new LeadItem { LineItemNo = "1", ManufacturerPartNumber = "PN-100", ProductShortDescription = "Valve", Quantity = quantity, UnitOfMeasure = "EA" });
        return lead;
    }

    private static LeadIntakeDescriptor Intake(string key, string hash, Guid batch, string? sender = "buyer@acme.test") => new(
        batch, "ManualUpload", key, null, null, "test", sender, "RFQ", $"{key}.xlsx",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 100, hash.PadRight(64, '0')[..64],
        null, null, null, DateTimeOffset.UtcNow, LeadProcessingPath.Deterministic, false, 0, "User", "tester", $"test:{key}");
}
