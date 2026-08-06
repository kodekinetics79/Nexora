using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadIdentityApplicationServiceTests
{
    [Fact]
    public async Task Batch_reports_scanner_outage_as_awaiting_without_rejected_kpi()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(70);
        Seed.BusinessUnit(context, 70);
        var batchId = Guid.NewGuid();
        context.Add(new LeadIngestionBatch
        {
            Id = batchId,
            BusinessUnitId = 70,
            SourceChannel = "ManualUpload",
            CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        var corpus = DocumentCorpus.Create(70, batchId, CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(70, corpus.Id, new string('a', 64), "customer-rfq.doc",
            "application/msword", "evidence", "quarantine/rfq.doc", "v1", 128);
        source.MarkSecurityStatus(DocumentSecurityStatus.Quarantined);
        context.Add(source);
        await context.SaveChangesAsync();
        var intake = SourceDocumentOccurrence.Create(70, source.Id, corpus.Id, "batch-rejected",
            "{\"fileName\":\"customer-rfq.doc\"}");
        intake.MarkAwaitingSecurityScan("security_scanner_unavailable",
            "{\"status\":\"Quarantined\",\"reason\":\"Malware scanner unavailable; the file remains quarantined.\"}");
        context.Add(intake);
        await context.SaveChangesAsync();

        var result = await new LeadIdentityApplicationService(context).GetBatchAsync(70, batchId);

        Assert.NotNull(result);
        Assert.Equal(1, result.FilesReceived);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(1, result.AwaitingSecurityScan);
        var item = Assert.Single(result.Items);
        Assert.Equal("Pending", item.Classification);
        Assert.Equal("AwaitingSecurityScan", item.IntakeStatus);
        Assert.Equal("security_scanner_unavailable", item.ErrorCode);
        Assert.Equal("Quarantined", item.SecurityStatus);
        Assert.Contains(item.Reasons, reason => reason.Contains("scanner unavailable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Batch_presents_legacy_scanner_quarantine_as_recoverable_without_rewriting_history()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(72);
        Seed.BusinessUnit(context, 72);
        var batchId = Guid.NewGuid();
        context.Add(new LeadIngestionBatch
        {
            Id = batchId,
            BusinessUnitId = 72,
            SourceChannel = "ManualUpload",
            CreatedBy = "test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        var corpus = DocumentCorpus.Create(72, batchId, CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(72, corpus.Id, new string('b', 64), "legacy-rfq.pdf",
            "application/pdf", "evidence", "quarantine/legacy-rfq.pdf", "v1", 256);
        source.MarkSecurityStatus(DocumentSecurityStatus.Quarantined);
        context.Add(source);
        await context.SaveChangesAsync();
        var intake = SourceDocumentOccurrence.Create(72, source.Id, corpus.Id, "legacy-rejected",
            "{\"fileName\":\"legacy-rfq.pdf\",\"inspection\":{\"ScannerSignature\":null}}");
        intake.MarkRejected("SecurityInspection", "document_quarantined",
            "{\"status\":\"Quarantined\",\"reason\":\"Malware scanner unavailable.\"}");
        context.Add(intake);
        await context.SaveChangesAsync();

        var result = await new LeadIdentityApplicationService(context).GetBatchAsync(72, batchId);

        Assert.NotNull(result);
        Assert.Equal(0, result.Rejected);
        Assert.Equal(1, result.AwaitingSecurityScan);
        var item = Assert.Single(result.Items);
        Assert.Equal("AwaitingSecurityScan", item.IntakeStatus);
        Assert.Equal("security_scanner_unavailable", item.ErrorCode);
        Assert.Equal(IntakeOccurrenceStatus.Rejected,
            (await context.Set<SourceDocumentOccurrence>().SingleAsync()).IntakeStatus);
    }

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
    public async Task Resending_an_older_revision_is_an_exact_duplicate_without_reverting_current_projection()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(78);
        Seed.BusinessUnit(context, 78); Seed.EmailConfig(context, 7801, 78); Seed.EmailIngest(context, 7901, 7801, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(78, 7901, "RFQ-HISTORY", "buyer@history.test", 10),
            Intake("history-original", "history-a", Guid.NewGuid(), "buyer@history.test"));
        context.ChangeTracker.Clear();
        var revised = await service.ReconcileAsync(Candidate(78, 7901, "RFQ-HISTORY", "buyer@history.test", 15),
            Intake("history-revision", "history-b", Guid.NewGuid(), "buyer@history.test"));
        context.ChangeTracker.Clear();
        var resend = await service.ReconcileAsync(Candidate(78, 7901, "RFQ-HISTORY", "buyer@history.test", 10),
            Intake("history-resend", "history-c", Guid.NewGuid(), "buyer@history.test"));

        Assert.Equal(LeadOccurrenceClassification.ExactDuplicate, resend.Classification);
        Assert.Equal(created.LeadId, resend.LeadId);
        Assert.Equal(created.RevisionId, resend.RevisionId);
        Assert.Equal(2, resend.RevisionNumber);
        Assert.Equal(revised.NexoraSerial, resend.NexoraSerial);
        Assert.Equal(2, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == created.LeadId));
        Assert.Equal(15, (await context.Leads.Include(x => x.LeadItems)
            .SingleAsync(x => x.Id == created.LeadId)).LeadItems.Single().Quantity);
    }

    [Fact]
    public async Task Unresolved_submission_with_part_overlap_requires_review_despite_quantity_changes_and_extra_lines()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(79);
        Seed.BusinessUnit(context, 79); Seed.EmailConfig(context, 7901, 79); Seed.EmailIngest(context, 8001, 7901, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(79, 8001, "RFQ-PARTS", "buyer@parts.test", 14);
        original.LeadItems.Add(new LeadItem { LineItemNo = "2", ManufacturerPartNumber = "ACTUATOR", ProductShortDescription = "Actuator", Quantity = 2, UnitOfMeasure = "EA" });
        var created = await service.ReconcileAsync(original,
            Intake("parts-original", "parts-a", Guid.NewGuid(), "buyer@parts.test"));

        context.ChangeTracker.Clear();
        var unresolved = Candidate(79, 8001, null, null, 16);
        unresolved.LeadItems.Add(new LeadItem { LineItemNo = "2", ManufacturerPartNumber = "ACTUATOR", ProductShortDescription = "Actuator", Quantity = 2, UnitOfMeasure = "EA" });
        unresolved.LeadItems.Add(new LeadItem { LineItemNo = "3", ManufacturerPartNumber = "SENSOR", ProductShortDescription = "Sensor", Quantity = 1, UnitOfMeasure = "EA" });
        unresolved.LeadItems.Add(new LeadItem { LineItemNo = "4", ManufacturerPartNumber = "EXTRA", ProductShortDescription = "Extra", Quantity = 1, UnitOfMeasure = "EA" });
        var review = await service.ReconcileAsync(unresolved,
            Intake("parts-possible", "parts-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, match.CandidateLeadId);
        Assert.True(match.Confidence >= .65m);
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
    public async Task External_ai_result_is_persisted_and_recorded_not_destroyed_at_reconciliation()
    {
        // This test previously asserted the opposite: that reconciliation THROWS when
        // external-AI usage exceeds a hardcoded 10% of recent occurrences. That guard was
        // removed on 2026-08-06 after it destroyed 1,133 successful, paid-for AI calls in
        // production without producing a single lead.
        //
        // The ceiling is real, but it belongs where it can prevent egress:
        // AiGovernanceService.ReserveAsync, which runs BEFORE the model call, honours the
        // tenant's configured ExternalDependencyCeilingPercent, and exempts endpoints the
        // tenant explicitly authorized (on a deployment with no local model the external
        // ratio is permanently 100%). By the time reconciliation runs, the call is already
        // authorized, made and billed — refusing to persist prevents no egress, it only
        // loses the work and guarantees the retry repeats it.
        //
        // Contract pinned here: the occurrence persists, and ExternalAiUsed is recorded so
        // the Trust Center's dependency reporting stays truthful.
        using var db = new TestDb();
        await using var context = db.ContextFor(73);
        Seed.BusinessUnit(context, 73); Seed.EmailConfig(context, 7301, 73); Seed.EmailIngest(context, 7401, 7301, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);
        var external = Intake("external", "h-ext", Guid.NewGuid()) with
        { ProcessingPath = LeadProcessingPath.ExternalModel, ExternalAiUsed = true };

        var result = await service.ReconcileAsync(
            Candidate(73, 7401, "RFQ-X", "x@test", 1), external);

        Assert.NotNull(result);
        var occurrences = await context.Set<LeadIngestionOccurrence>().ToListAsync();
        var persisted = Assert.Single(occurrences);
        Assert.True(persisted.ExternalAiUsed);
        Assert.NotNull(persisted.LeadId);
    }

    [Fact]
    public async Task Repeated_external_ai_occurrences_all_persist()
    {
        // The removed guard compared the last 100 occurrences, so it began refusing at
        // roughly the eleventh external document and never recovered — every subsequent
        // document in the tenant was lost. Ten in a row must all survive.
        using var db = new TestDb();
        await using var context = db.ContextFor(83);
        Seed.BusinessUnit(context, 83); Seed.EmailConfig(context, 8301, 83); Seed.EmailIngest(context, 8401, 8301, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        for (var i = 0; i < 10; i++)
        {
            var intake = Intake($"external-{i}", $"h-ext-{i}", Guid.NewGuid()) with
            { ProcessingPath = LeadProcessingPath.ExternalModel, ExternalAiUsed = true };
            await service.ReconcileAsync(
                Candidate(83, 8401, $"RFQ-EXT-{i}", $"buyer{i}@test", 1), intake);
            context.ChangeTracker.Clear();
        }

        var occurrences = await context.Set<LeadIngestionOccurrence>().ToListAsync();
        Assert.Equal(10, occurrences.Count);
        Assert.All(occurrences, x => Assert.True(x.ExternalAiUsed));
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

    [Fact]
    public async Task Corroborated_logical_group_creates_revision_of_canonical_lead()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(75);
        Seed.BusinessUnit(context, 75); Seed.EmailConfig(context, 7501, 75); Seed.EmailIngest(context, 7601, 7501, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(75, 7601, null, "buyer@group.test", 10);
        original.BidClosingDate = DateTime.UtcNow.AddDays(7);
        var created = await service.ReconcileAsync(original,
            Intake("group-original", "group-a", Guid.NewGuid(), "buyer@group.test") with { LogicalGroupKey = "email:group-75" });

        context.ChangeTracker.Clear();
        var changed = Candidate(75, 7601, null, "buyer@group.test", 10);
        changed.BidClosingDate = DateTime.UtcNow.AddDays(14);
        var revision = await service.ReconcileAsync(changed,
            Intake("group-revision", "group-b", Guid.NewGuid(), "buyer@group.test") with { LogicalGroupKey = "email:group-75" });

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(created.NexoraSerial, revision.NexoraSerial);
        Assert.Contains("Corroborated logical document group", revision.Reasons.Single());
    }

    [Fact]
    public async Task Ambiguous_logical_group_requires_possible_match_review()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(76);
        Seed.BusinessUnit(context, 76); Seed.EmailConfig(context, 7601, 76); Seed.EmailIngest(context, 7701, 7601, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(76, 7701, null, "known@group.test", 10);
        original.BidClosingDate = DateTime.UtcNow.AddDays(7);
        var created = await service.ReconcileAsync(original,
            Intake("ambiguous-original", "ambiguous-a", Guid.NewGuid(), "known@group.test") with { LogicalGroupKey = "email:group-76" });

        context.ChangeTracker.Clear();
        var uncertain = Candidate(76, 7701, null, null, 10);
        uncertain.BidClosingDate = DateTime.UtcNow.AddDays(14);
        var review = await service.ReconcileAsync(uncertain,
            Intake("ambiguous-copy", "ambiguous-b", Guid.NewGuid(), sender: null) with { LogicalGroupKey = "email:group-76" });

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        var candidate = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, candidate.CandidateLeadId);
        Assert.Equal(LeadMatchReviewState.Pending, candidate.ReviewState);
        Assert.Contains("share a logical group", review.Reasons.Single());
    }

    [Fact]
    public async Task Unrelated_documents_with_same_group_key_remain_separate_leads()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(77);
        Seed.BusinessUnit(context, 77); Seed.EmailConfig(context, 7701, 77); Seed.EmailIngest(context, 7801, 7701, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var first = Candidate(77, 7801, "RFQ-GROUP-A", "a@group.test", 10);
        var created = await service.ReconcileAsync(first,
            Intake("unrelated-a", "unrelated-a", Guid.NewGuid(), "a@group.test") with { LogicalGroupKey = "email:group-77" });

        context.ChangeTracker.Clear();
        var unrelated = Candidate(77, 7801, "RFQ-GROUP-B", "b@group.test", 3);
        var line = unrelated.LeadItems.Single();
        line.ManufacturerPartNumber = "PN-UNRELATED";
        line.ProductShortDescription = "Unrelated motor";
        var separate = await service.ReconcileAsync(unrelated,
            Intake("unrelated-b", "unrelated-b", Guid.NewGuid(), "b@group.test") with { LogicalGroupKey = "email:group-77" });

        Assert.Equal(LeadOccurrenceClassification.New, separate.Classification);
        Assert.NotEqual(created.LeadId, separate.LeadId);
        Assert.Equal(2, await context.Leads.CountAsync());
        Assert.Empty(await context.Set<LeadMatchCandidate>().ToListAsync());
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
