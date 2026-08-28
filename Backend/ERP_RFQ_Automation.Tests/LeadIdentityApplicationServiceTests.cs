using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Sla;
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
        Assert.Equal(15, (await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId)).LeadItems.Single(x => x.IsCurrentRevisionProjection).Quantity);

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
    public async Task Buyer_named_amendment_of_resolved_customer_versions_promoted_lead_and_marks_quote_stale()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(74);
        Seed.BusinessUnit(context, 74); Seed.EmailConfig(context, 7401, 74); Seed.EmailIngest(context, 7501, 7401, "NeedsReview");
        Seed.Customer(context, 7601, 74, "Saudi Electricity Company");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(74, 7501, "E2E-GOLDEN-A-PARTIAL", "bids@sec.test", 25);
        original.BuyersName = "SEC Bid Desk";
        var created = await service.ReconcileAsync(original,
            Intake("resolved-customer-original", "resolved-a", Guid.NewGuid(), "bids@sec.test"));

        var canonical = await context.Leads.SingleAsync(x => x.Id == created.LeadId);
        canonical.ResolveCommercialIdentity(7601, null, "CUSTOMER_CONFIRMED");
        var rfq = new Rfq
        {
            Rfqno = original.Rfqno!, BuyersName = original.BuyersName,
            RecDate = DateTime.UtcNow, LeadId = canonical.Id,
            CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = 74
        };
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();
        var quote = new Quote
        {
            QuoteNo = "QT-RESOLVED-AMEND", Rfqid = rfq.Id,
            BusinessUnitId = 74, CreatedBy = "test", CreatedDate = DateTime.UtcNow
        };
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        context.ChangeTracker.Clear();
        var amendment = Candidate(74, 7501, "E2E-GOLDEN-A-PARTIAL", null, 26);
        amendment.BuyersName = "SEC Bid Desk";
        var revised = await service.ReconcileAsync(amendment,
            Intake("resolved-customer-amendment", "resolved-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.Revision, revised.Classification);
        Assert.Equal(created.LeadId, revised.LeadId);
        Assert.Equal(2, revised.RevisionNumber);
        Assert.Contains(await context.Set<LeadRevisionImpact>().Where(x => x.LeadRevisionId == revised.RevisionId).ToListAsync(),
            impact => impact.AggregateType == "RFQ" && impact.AggregateId == rfq.Id && impact.ImpactType == "RFQ_REVISION_REQUIRED");
        Assert.Contains(await context.Set<LeadRevisionImpact>().Where(x => x.LeadRevisionId == revised.RevisionId).ToListAsync(),
            impact => impact.AggregateType == "QUOTE" && impact.AggregateId == quote.Id && impact.ImpactType == "DRAFT_STALE_REVIEW_REQUIRED");

        var quoteRead = await new QuoteRepository(context).GetByIdAsync(quote.Id, 74);
        Assert.Equal("DRAFT_STALE_REVIEW_REQUIRED", quoteRead.RevisionImpact);

        var workbenchService = new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context));
        var blocked = await workbenchService.GetAsync(74, created.LeadId);
        Assert.Contains(blocked.Blockers, blocker => blocker.Code == "RFQ_REVISION_REQUIRED");

        const string reason = "Customer quantity amendment reviewed against every original RFQ line.";
        var resolution = new RfqRevisionImpactResolutionService(context);
        var command = new ResolveRfqRevisionImpactCommand(rfq.Id, revised.RevisionId!.Value,
            reason, true, "rfq-amendment-review:resolved-customer", "commercial.owner@test");
        var firstResolution = await resolution.ResolveAsync(74, created.LeadId, command);
        var replay = await resolution.ResolveAsync(74, created.LeadId, command);

        Assert.Equal(1, firstResolution.ResolvedImpactCount);
        Assert.False(firstResolution.Replayed);
        Assert.Equal(1, replay.ResolvedImpactCount);
        Assert.True(replay.Replayed);
        var retainedImpact = await context.Set<LeadRevisionImpact>().AsNoTracking().SingleAsync(x =>
            x.AggregateType == "RFQ" && x.AggregateId == rfq.Id && x.ImpactType == "RFQ_REVISION_REQUIRED");
        Assert.Equal("OPEN", retainedImpact.Status);
        Assert.Null(retainedImpact.ResolvedAtUtc);
        var resolutionEvent = await context.Set<LeadIdentityAuditEvent>().AsNoTracking().SingleAsync(x =>
            x.EventType == RfqRevisionImpactResolutionService.ResolutionEventType
            && x.CorrelationId == RfqRevisionImpactResolutionService.CorrelationPrefix + retainedImpact.Id);
        Assert.Equal("commercial.owner@test", resolutionEvent.ActorId);
        Assert.Contains(reason, resolutionEvent.PayloadJson, StringComparison.Ordinal);
        Assert.DoesNotContain((await workbenchService.GetAsync(74, created.LeadId)).Blockers,
            blocker => blocker.Code == "RFQ_REVISION_REQUIRED");

        var mismatchedReplay = command with { ReconciliationReason = "A different review outcome must not reuse the command key." };
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolution.ResolveAsync(74, created.LeadId, mismatchedReplay));
        Assert.Contains("different RFQ amendment review", conflict.Message, StringComparison.OrdinalIgnoreCase);

        var noOpenImpact = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolution.ResolveAsync(74, created.LeadId, command with
            {
                IdempotencyKey = "rfq-amendment-review:already-closed"
            }));
        Assert.Contains("no unresolved RFQ revision impact", noOpenImpact.Message,
            StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<KeyNotFoundException>(() => resolution.ResolveAsync(
            75, created.LeadId, command with { IdempotencyKey = "rfq-amendment-review:wrong-tenant" }));
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
            .SingleAsync(x => x.Id == created.LeadId)).LeadItems.Single(x => x.IsCurrentRevisionProjection).Quantity);
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
        var line = unrelated.LeadItems.Single(x => x.IsCurrentRevisionProjection);
        line.ManufacturerPartNumber = "PN-UNRELATED";
        line.ProductShortDescription = "Unrelated motor";
        var separate = await service.ReconcileAsync(unrelated,
            Intake("unrelated-b", "unrelated-b", Guid.NewGuid(), "b@group.test") with { LogicalGroupKey = "email:group-77" });

        Assert.Equal(LeadOccurrenceClassification.New, separate.Classification);
        Assert.NotEqual(created.LeadId, separate.LeadId);
        Assert.Equal(2, await context.Leads.CountAsync());
        Assert.Empty(await context.Set<LeadMatchCandidate>().ToListAsync());
    }

    [Fact]
    public async Task Human_revision_decision_keeps_the_buyers_verbatim_values_and_drops_no_columns()
    {
        // The match snapshot is a HASH INPUT: normalised, five item properties out of twenty-two.
        // Projecting it onto the canonical lead rewrote RFQ-2026/0012 as rfq20260012 and deleted
        // UnitPrice, Currency, CustomerRfqno and six other columns on one operator click.
        using var db = new TestDb();
        await using var context = db.ContextFor(84);
        Seed.BusinessUnit(context, 84); Seed.EmailConfig(context, 8401, 84); Seed.EmailIngest(context, 8501, 8401, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = VerbatimCandidate(84, 8501, "RFQ-2026/0012", "buyer@verbatim.test", buyer: "Buyer", quantity: 10);
        var created = await service.ReconcileAsync(original,
            Intake("verbatim-original", "verbatim-a", Guid.NewGuid(), "buyer@verbatim.test"));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);

        context.ChangeTracker.Clear();
        // Same customer reference, unresolved sender: a possible match for a human, not an
        // automatic revision.
        var amended = VerbatimCandidate(84, 8501, "RFQ-2026/0012", clientEmail: null, buyer: "John Smith", quantity: 25);
        var review = await service.ReconcileAsync(amended,
            Intake("verbatim-amendment", "verbatim-b", Guid.NewGuid(), sender: null));
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);

        context.ChangeTracker.Clear();
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.False(string.IsNullOrWhiteSpace(match.ProposedLeadSnapshotJson));

        context.ChangeTracker.Clear();
        var decided = await service.DecideMatchAsync(84, review.OccurrenceId,
            new MatchDecisionRequest("revision", match.CandidateLeadId, match.Version,
                "Buyer confirmed the amendment.", "verbatim-decision-1"), "reviewer");
        Assert.Equal(LeadOccurrenceClassification.Revision, decided.Classification);
        Assert.Equal(created.LeadId, decided.LeadId);

        context.ChangeTracker.Clear();
        var canonical = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal("RFQ-2026/0012", canonical.Rfqno);
        Assert.Equal("John Smith", canonical.BuyersName);
        var line = Assert.Single(canonical.LeadItems, x => x.IsCurrentRevisionProjection);
        Assert.Equal("AB-123/X", line.ManufacturerPartNumber);
        Assert.Equal("1/2\" SS Ball Valve, 300#", line.ProductShortDescription);
        Assert.Equal(25, line.Quantity);
        Assert.Equal(1234.56m, line.UnitPrice);
        Assert.Equal("SAR", line.Currency);
        Assert.Equal("CUST-REF/77", line.CustomerRfqno);
        Assert.Equal("MAT-9/1", line.ItemMaterialCode);
        Assert.Equal("Acme Valves", line.ManufacturerName);
        Assert.Equal(14, line.LeadTime);
        Assert.Equal(.91m, line.Aiconfidence);
        Assert.Equal("EA", line.UnitOfMeasure);

        // The new revision indexes the reference the amendment carries, not a superseded one.
        var newest = await context.Set<LeadRevision>().Where(x => x.LeadId == created.LeadId)
            .OrderByDescending(x => x.RevisionNumber).FirstAsync();
        Assert.Equal("RFQ-2026/0012", newest.CustomerRfqReference);
        Assert.Equal("rfq20260012", newest.NormalizedCustomerRfqReference);
    }

    [Fact]
    public async Task Match_review_without_a_verbatim_snapshot_changes_nothing_on_the_canonical_lead()
    {
        // Candidates raised before the verbatim column existed carry only normalised hash text.
        // Refusing to project is the honest outcome; projecting it destroys the buyer's values.
        using var db = new TestDb();
        await using var context = db.ContextFor(85);
        Seed.BusinessUnit(context, 85); Seed.EmailConfig(context, 8501, 85); Seed.EmailIngest(context, 8601, 8501, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(
            VerbatimCandidate(85, 8601, "RFQ-2026/0013", "buyer@legacy.test", buyer: "Buyer", quantity: 10),
            Intake("legacy-original", "legacy-a", Guid.NewGuid(), "buyer@legacy.test"));
        context.ChangeTracker.Clear();
        var review = await service.ReconcileAsync(
            VerbatimCandidate(85, 8601, "RFQ-2026/0013", clientEmail: null, buyer: "John Smith", quantity: 25),
            Intake("legacy-amendment", "legacy-b", Guid.NewGuid(), sender: null));
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);

        var match = await context.Set<LeadMatchCandidate>().SingleAsync(x => x.OccurrenceId == review.OccurrenceId);
        match.ProposedLeadSnapshotJson = null;
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var decided = await service.DecideMatchAsync(85, review.OccurrenceId,
            new MatchDecisionRequest("revision", match.CandidateLeadId, match.Version,
                "Confirmed.", "legacy-decision-1"), "reviewer");

        Assert.Equal(LeadOccurrenceClassification.Revision, decided.Classification);
        Assert.Contains(decided.Reasons, x => x.Contains("no commercial values were applied", StringComparison.Ordinal));
        context.ChangeTracker.Clear();
        var canonical = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal("RFQ-2026/0013", canonical.Rfqno);
        var line = Assert.Single(canonical.LeadItems, x => x.IsCurrentRevisionProjection);
        Assert.Equal("AB-123/X", line.ManufacturerPartNumber);
        Assert.Equal(10, line.Quantity);
        Assert.Equal(1234.56m, line.UnitPrice);
    }

    [Fact]
    public async Task Same_customer_and_reference_with_a_changed_quantity_versions_the_existing_rfq()
    {
        // FR-RFQ-05: an amendment is a VERSION of the existing RFQ, with a difference row and an
        // audit event — never a second canonical lead with a second client-facing reference.
        using var db = new TestDb();
        await using var context = db.ContextFor(86);
        Seed.BusinessUnit(context, 86); Seed.EmailConfig(context, 8601, 86); Seed.EmailIngest(context, 8701, 8601, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(86, 8701, "RFQ-2026/0014", "buyer@version.test", 10),
            Intake("version-original", "version-a", Guid.NewGuid(), "buyer@version.test"));
        context.ChangeTracker.Clear();
        var revision = await service.ReconcileAsync(Candidate(86, 8701, "RFQ-2026/0014", "buyer@version.test", 25),
            Intake("version-amendment", "version-b", Guid.NewGuid(), "buyer@version.test"));

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(created.NexoraSerial, revision.NexoraSerial);
        Assert.Equal(2, revision.RevisionNumber);
        Assert.Equal(1, await context.Leads.CountAsync());
        Assert.Contains(await context.Set<LeadRevisionDifference>()
                .Where(x => x.LeadRevisionId == revision.RevisionId).ToListAsync(),
            x => x.Scope == "Line" && x.ChangeType == LeadRevisionChangeType.Modified);
        Assert.True(await context.Set<LeadIdentityAuditEvent>()
            .AnyAsync(x => x.LeadId == created.LeadId && x.EventType == "LEAD_REVISION_CREATED"));
        Assert.Equal(25, (await context.Leads.Include(x => x.LeadItems)
            .SingleAsync(x => x.Id == created.LeadId)).LeadItems.Single(x => x.IsCurrentRevisionProjection).Quantity);
    }

    [Fact]
    public async Task Amended_reference_from_a_known_customer_versions_the_existing_rfq()
    {
        // The reported production failure: the buyer resends as "Rev B", both customer scopes
        // resolve, similarity is 1.00 — and the match used to be discarded for a brand-new RFQ.
        using var db = new TestDb();
        await using var context = db.ContextFor(87);
        Seed.BusinessUnit(context, 87); Seed.EmailConfig(context, 8701, 87); Seed.EmailIngest(context, 8801, 8701, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(87, 8801, "RFQ-4471", "buyer@amend.test", 10),
            Intake("amend-original", "amend-a", Guid.NewGuid(), "buyer@amend.test"));
        context.ChangeTracker.Clear();
        var revision = await service.ReconcileAsync(Candidate(87, 8801, "RFQ-4471 Rev B", "buyer@amend.test", 25),
            Intake("amend-revb", "amend-b", Guid.NewGuid(), "buyer@amend.test"));

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(created.NexoraSerial, revision.NexoraSerial);
        Assert.Equal(2, revision.RevisionNumber);
        Assert.Equal(1, await context.Leads.CountAsync());
        Assert.True(await context.Set<LeadIdentityAuditEvent>()
            .AnyAsync(x => x.LeadId == created.LeadId && x.EventType == "LEAD_REVISION_CREATED"));
        var canonical = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal("RFQ-4471 Rev B", canonical.Rfqno);
        Assert.Equal(25, canonical.LeadItems.Single(x => x.IsCurrentRevisionProjection).Quantity);
    }

    [Fact]
    public async Task Original_arriving_after_its_amendment_is_reviewed_rather_than_rolled_back()
    {
        // The two references are just as related in this direction, but applying the older
        // document would silently revert the canonical record to superseded commercial values.
        using var db = new TestDb();
        await using var context = db.ContextFor(90);
        Seed.BusinessUnit(context, 90); Seed.EmailConfig(context, 9001, 90); Seed.EmailIngest(context, 9101, 9001, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(90, 9101, "RFQ-4471 Rev B", "buyer@order.test", 25),
            Intake("order-amendment", "order-a", Guid.NewGuid(), "buyer@order.test"));
        context.ChangeTracker.Clear();
        var late = await service.ReconcileAsync(Candidate(90, 9101, "RFQ-4471", "buyer@order.test", 10),
            Intake("order-original", "order-b", Guid.NewGuid(), "buyer@order.test"));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, late.Classification);
        Assert.Equal(1, await context.Leads.CountAsync());
        var canonical = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal("RFQ-4471 Rev B", canonical.Rfqno);
        Assert.Equal(25, canonical.LeadItems.Single(x => x.IsCurrentRevisionProjection).Quantity);
    }

    [Fact]
    public async Task Known_customer_resend_without_a_reference_is_reviewed_not_silently_new()
    {
        // 12 of 47 production leads carry no RFQ reference at all. Same buyer, identical line
        // identity, no reference to confirm it: a human decides. It must never mint a second RFQ.
        using var db = new TestDb();
        await using var context = db.ContextFor(88);
        Seed.BusinessUnit(context, 88); Seed.EmailConfig(context, 8801, 88); Seed.EmailIngest(context, 8901, 8801, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(88, 8901, "RFQ-9000", "buyer@absent.test", 10),
            Intake("absent-original", "absent-a", Guid.NewGuid(), "buyer@absent.test"));
        context.ChangeTracker.Clear();
        var review = await service.ReconcileAsync(Candidate(88, 8901, null, "buyer@absent.test", 12),
            Intake("absent-amendment", "absent-b", Guid.NewGuid(), "buyer@absent.test"));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        Assert.Equal(1, await context.Leads.CountAsync());
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, match.CandidateLeadId);
        Assert.Equal(LeadMatchReviewState.Pending, match.ReviewState);
        Assert.True(await context.Set<LeadIdentityAuditEvent>()
            .AnyAsync(x => x.OccurrenceId == review.OccurrenceId && x.EventType == "POSSIBLE_MATCH_RAISED"));
    }

    [Fact]
    public async Task A_second_reference_from_the_same_buyer_stays_new_and_records_the_rejected_candidate()
    {
        // The buyer's own reference is an identity statement. Two references means two inquiries,
        // however identical the commodity lines look — but the rejected candidate is named, so
        // the decision is explainable rather than silent.
        using var db = new TestDb();
        await using var context = db.ContextFor(89);
        Seed.BusinessUnit(context, 89); Seed.EmailConfig(context, 8901, 89); Seed.EmailIngest(context, 9001, 8901, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(89, 9001, "RFQ-7001", "buyer@repeat.test", 10),
            Intake("repeat-first", "repeat-a", Guid.NewGuid(), "buyer@repeat.test"));
        context.ChangeTracker.Clear();
        var second = await service.ReconcileAsync(Candidate(89, 9001, "RFQ-7002", "buyer@repeat.test", 10),
            Intake("repeat-second", "repeat-b", Guid.NewGuid(), "buyer@repeat.test"));

        Assert.Equal(LeadOccurrenceClassification.New, second.Classification);
        Assert.NotEqual(created.LeadId, second.LeadId);
        Assert.Equal(2, second.Reasons.Count);
        Assert.Contains(second.Reasons, x => x.Contains(created.NexoraSerial, StringComparison.Ordinal)
            && x.Contains("different inquiry", StringComparison.Ordinal));
        Assert.Empty(await context.Set<LeadMatchCandidate>().ToListAsync());
    }

    // FR-RFQ-05: "When a closing-date amendment is received, version the existing RFQ (v1, v2,
    // etc.) rather than create a duplicate." The four tests below pin the arm that does it, and
    // the three neighbours it must NOT reach.
    //
    // The amendment here carries no address of its own and arrives with no sender — the manually
    // uploaded amendment PDF. That is the shape that used to reach the human queue: when the
    // sender resolves on both sides the scope-plus-reference arm already versions it.
    [Fact]
    public async Task Closing_date_amendment_versions_the_existing_rfq_instead_of_queueing_a_review()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(93);
        Seed.BusinessUnit(context, 93); Seed.EmailConfig(context, 9301, 93); Seed.EmailIngest(context, 9401, 9301, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(93, 9401, "TENDER-5500", "tenders@closing.test", 10);
        original.BidClosingDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var created = await service.ReconcileAsync(original,
            Intake("closing-original", "closing-a", Guid.NewGuid(), "tenders@closing.test"));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);

        context.ChangeTracker.Clear();
        var amendment = Candidate(93, 9401, "TENDER-5500", null, 10);
        amendment.BidClosingDate = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var revision = await service.ReconcileAsync(amendment,
            Intake("closing-amendment", "closing-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(created.NexoraSerial, revision.NexoraSerial);
        Assert.Equal(2, revision.RevisionNumber);
        // No duplicate lead row, and no review item standing in for one.
        Assert.Equal(1, await context.Leads.CountAsync());
        Assert.Empty(await context.Set<LeadMatchCandidate>().ToListAsync());
        Assert.Equal(2, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == created.LeadId));
        Assert.True(await context.Set<LeadIdentityAuditEvent>()
            .AnyAsync(x => x.LeadId == created.LeadId && x.EventType == "LEAD_REVISION_CREATED"));
        // The reviewer has to be able to read WHY it auto-linked, with both deadlines named.
        var reason = Assert.Single(revision.Reasons);
        Assert.Contains("Closing-date amendment", reason, StringComparison.Ordinal);
        Assert.Contains("2026-09-01 12:00", reason, StringComparison.Ordinal);
        Assert.Contains("2026-09-20 12:00", reason, StringComparison.Ordinal);
        var canonical = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), canonical.BidClosingDate);
    }

    // An earlier deadline is an amendment too. The arm must not read "later" as the only direction.
    [Fact]
    public async Task Closing_date_brought_forward_versions_the_existing_rfq()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(94);
        Seed.BusinessUnit(context, 94); Seed.EmailConfig(context, 9401, 94); Seed.EmailIngest(context, 9501, 9401, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(94, 9501, "TENDER-5501", "tenders@earlier.test", 10);
        original.BidClosingDate = new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);
        var created = await service.ReconcileAsync(original,
            Intake("earlier-original", "earlier-a", Guid.NewGuid(), "tenders@earlier.test"));

        context.ChangeTracker.Clear();
        var amendment = Candidate(94, 9501, "TENDER-5501", null, 10);
        amendment.BidClosingDate = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var revision = await service.ReconcileAsync(amendment,
            Intake("earlier-amendment", "earlier-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(2, revision.RevisionNumber);
        Assert.Equal(1, await context.Leads.CountAsync());
    }

    // Nothing about the deadline changed, so the closing-date arm has no business firing: an
    // unresolved sender with a matching reference stays the human decision it already was.
    [Fact]
    public async Task Same_reference_and_items_with_an_unchanged_closing_date_still_requires_review()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(95);
        Seed.BusinessUnit(context, 95); Seed.EmailConfig(context, 9501, 95); Seed.EmailIngest(context, 9601, 9501, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var closing = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var original = Candidate(95, 9601, "TENDER-5502", "tenders@same.test", 10);
        original.BidClosingDate = closing;
        var created = await service.ReconcileAsync(original,
            Intake("same-original", "same-a", Guid.NewGuid(), "tenders@same.test"));

        context.ChangeTracker.Clear();
        var resend = Candidate(95, 9601, "TENDER-5502", null, 12);
        resend.BidClosingDate = closing;
        var review = await service.ReconcileAsync(resend,
            Intake("same-resend", "same-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        Assert.Equal(1, await context.Leads.CountAsync());
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, match.CandidateLeadId);
    }

    // A moved deadline does not make two different item sets one inquiry. Sameness is decided by
    // the same similarity machinery every other revision arm uses.
    [Fact]
    public async Task Materially_different_items_with_a_changed_closing_date_are_not_auto_versioned()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(96);
        Seed.BusinessUnit(context, 96); Seed.EmailConfig(context, 9601, 96); Seed.EmailIngest(context, 9701, 9601, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(96, 9701, "TENDER-5503", "tenders@scope.test", 10);
        original.BidClosingDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var created = await service.ReconcileAsync(original,
            Intake("scope-original", "scope-a", Guid.NewGuid(), "tenders@scope.test"));

        context.ChangeTracker.Clear();
        // Same reference and a moved deadline, but the buyer has re-scoped the package: one of the
        // two lines is a part we were never asked about.
        var rescoped = Candidate(96, 9701, "TENDER-5503", null, 10);
        rescoped.BidClosingDate = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        rescoped.LeadItems.Add(new LeadItem { LineItemNo = "2", ManufacturerPartNumber = "PN-999",
            ProductShortDescription = "Gearbox", Quantity = 4, UnitOfMeasure = "EA" });

        var review = await service.ReconcileAsync(rescoped,
            Intake("scope-rescoped", "scope-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        Assert.Equal(1, await context.Leads.CountAsync());
        Assert.Equal(1, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == created.LeadId));
    }

    // FR-RFQ-06 is not weakened: a contradicting buyer never auto-links, however well the
    // reference, the line items and the moved deadline line up.
    [Fact]
    public async Task Different_buyer_with_a_changed_closing_date_is_not_auto_versioned()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(97);
        Seed.BusinessUnit(context, 97); Seed.EmailConfig(context, 9701, 97); Seed.EmailIngest(context, 9801, 9701, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(97, 9801, "TENDER-5504", "tenders@first.test", 10);
        original.BidClosingDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var created = await service.ReconcileAsync(original,
            Intake("buyer-original", "buyer-a", Guid.NewGuid(), "tenders@first.test"));

        context.ChangeTracker.Clear();
        // A second reseller chasing the same public tender number, with the amended deadline.
        var other = Candidate(97, 9801, "TENDER-5504", "tenders@second.test", 10);
        other.BidClosingDate = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var review = await service.ReconcileAsync(other,
            Intake("buyer-second", "buyer-b", Guid.NewGuid(), "tenders@second.test"));

        Assert.NotEqual(LeadOccurrenceClassification.Revision, review.Classification);
        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(1, await context.Set<LeadRevision>().CountAsync(x => x.LeadId == created.LeadId));
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, match.CandidateLeadId);
    }

    /// <summary>A candidate carrying the commercial columns the human decision path used to drop.</summary>
    private static Lead VerbatimCandidate(long bu, long ingestId, string? rfq, string? clientEmail, string? buyer, int quantity)
    {
        var lead = new Lead { Rfqno = rfq, BuyersName = buyer, RecDate = DateTime.UtcNow,
            LeadSource = "ManualUpload", CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = bu,
            EmailIngestsId = ingestId, Clientemail = clientEmail, RequiresCommercialReview = true };
        lead.LeadItems.Add(new LeadItem
        {
            LineItemNo = "1", ManufacturerPartNumber = "AB-123/X",
            ProductShortDescription = "1/2\" SS Ball Valve, 300#", Quantity = quantity, UnitOfMeasure = "EA",
            UnitPrice = 1234.56m, Currency = "SAR", CustomerRfqno = "CUST-REF/77", ItemMaterialCode = "MAT-9/1",
            ManufacturerName = "Acme Valves", LeadTime = 14, Aiconfidence = .91m
        });
        return lead;
    }

    // FR-RFQ-06: "Flag possible duplicates based on the same buyer, same item and overlapping
    // dates for human review BEFORE a new record is created." The pair below is deliberately
    // built so commercial-content similarity stays under the match threshold — the second
    // extraction describes the same part with different wording and a different unit — which is
    // exactly the case the similarity-only path used to let through as a brand new inquiry.
    [Fact]
    public async Task Same_buyer_overlapping_deadline_and_matching_part_is_held_for_review_before_any_lead_is_created()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(91);
        Seed.BusinessUnit(context, 91); Seed.EmailConfig(context, 9101, 91); Seed.EmailIngest(context, 9201, 9101, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var first = Candidate(91, 9201, "TENDER-8801", "procurement@buyer.test", 10);
        first.BidClosingDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var created = await service.ReconcileAsync(first, Intake("dup-first", "dup-hash-a", Guid.NewGuid(), "procurement@buyer.test"));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);

        context.ChangeTracker.Clear();
        var second = DifferentlyWordedSameParts(91, 9201, "procurement@buyer.test",
            new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));

        var review = await service.ReconcileAsync(second, Intake("dup-second", "dup-hash-b", Guid.NewGuid(), "procurement@buyer.test"));

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        Assert.Equal(1, await context.Leads.CountAsync());
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, match.CandidateLeadId);
        Assert.Contains(review.Reasons, r => r.Contains("Same buyer", StringComparison.OrdinalIgnoreCase));
    }

    // The date dimension has to carry weight, or the rule degrades into "same buyer ever bought
    // this part", which would hold a legitimate repeat order for review every time.
    [Fact]
    public async Task Same_buyer_and_part_with_deadlines_far_apart_is_a_new_inquiry()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(92);
        Seed.BusinessUnit(context, 92); Seed.EmailConfig(context, 9201, 92); Seed.EmailIngest(context, 9301, 9201, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var first = Candidate(92, 9301, "TENDER-9901", "procurement@repeat.test", 10);
        first.BidClosingDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        await service.ReconcileAsync(first, Intake("repeat-first", "repeat-hash-a", Guid.NewGuid(), "procurement@repeat.test"));

        context.ChangeTracker.Clear();
        var second = DifferentlyWordedSameParts(92, 9301, "procurement@repeat.test",
            new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc));

        var repeat = await service.ReconcileAsync(second, Intake("repeat-second", "repeat-hash-b", Guid.NewGuid(), "procurement@repeat.test"));

        Assert.Equal(LeadOccurrenceClassification.New, repeat.Classification);
        Assert.Equal(2, await context.Leads.CountAsync());
    }

    /// <summary>
    /// Same manufacturer part as <see cref="Candidate"/>, described differently and in another
    /// unit. Line identity hashes part + description + UoM, so this scores 0 on similarity while
    /// the FR-RFQ-06 rule — which keys on the part number alone — still sees a match.
    /// </summary>
    private static Lead DifferentlyWordedSameParts(long bu, long ingestId, string email, DateTime closing)
    {
        var lead = new Lead { Rfqno = null, BuyersName = "Buyer", RecDate = DateTime.UtcNow,
            LeadSource = "ManualUpload", CreatedBy = "test", CreatedDate = DateTime.UtcNow, BusinessUnitId = bu,
            EmailIngestsId = ingestId, Clientemail = email, RequiresCommercialReview = true, BidClosingDate = closing };
        lead.LeadItems.Add(new LeadItem { LineItemNo = "1", ManufacturerPartNumber = "PN-100",
            ProductShortDescription = "Ball valve, two inch, flanged", Quantity = 10, UnitOfMeasure = "PCS" });
        return lead;
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

    // ===================================================================== thread identity
    // FR-RFQ-05/06 via mail threading: a reply whose In-Reply-To/References chain names the
    // message an existing lead was ingested from is strong evidence for that lead — treated
    // exactly like the logical-group signal: it lowers the similarity bar only WITH a
    // corroborating customer or reference, and never merges on the thread alone.

    [Fact]
    public async Task Thread_linkage_with_corroborating_customer_versions_the_existing_rfq()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(201);
        Seed.BusinessUnit(context, 201); Seed.EmailConfig(context, 2011, 201); Seed.EmailIngest(context, 2021, 2011, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        // The original inquiry has NO customer reference — the arm under test must work from
        // thread + customer identity, because a reference would already match without either.
        var created = await service.ReconcileAsync(Candidate(201, 2021, null, "buyer@thread.test", 10),
            Intake("thread-original", "thread-a", Guid.NewGuid(), "buyer@thread.test")
                with { EmailThreadId = "email:origin-msg@customer.example" });
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);

        context.ChangeTracker.Clear();
        // The amendment arrives as a REPLY: its own Message-Id differs, so the logical group
        // key cannot match — only the persisted In-Reply-To/References chain reaches back.
        var revision = await service.ReconcileAsync(Candidate(201, 2021, null, "buyer@thread.test", 25),
            Intake("thread-amendment", "thread-b", Guid.NewGuid(), "buyer@thread.test")
                with
            {
                EmailThreadId = "email:reply-msg@customer.example",
                ThreadReferencedMessageIds = new[] { "email:origin-msg@customer.example" }
            });

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(2, revision.RevisionNumber);
        Assert.Equal(1, await context.Leads.CountAsync());
        Assert.Contains("email thread", revision.Reasons.Single(), StringComparison.Ordinal);
        Assert.Equal(25, (await context.Leads.Include(x => x.LeadItems)
            .SingleAsync(x => x.Id == created.LeadId)).LeadItems.Single(x => x.IsCurrentRevisionProjection).Quantity);
    }

    [Fact]
    public async Task Thread_linkage_alone_without_corroboration_is_reviewed_not_merged()
    {
        // Subjects and threads get reused: a forwarded thread can carry a different company's
        // inquiry. With no corroborating customer identity and no reference, the thread hit
        // must reach a human, never auto-link.
        using var db = new TestDb();
        await using var context = db.ContextFor(202);
        Seed.BusinessUnit(context, 202); Seed.EmailConfig(context, 2021, 202); Seed.EmailIngest(context, 2031, 2021, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var created = await service.ReconcileAsync(Candidate(202, 2031, null, "buyer@threadonly.test", 10),
            Intake("threadonly-original", "threadonly-a", Guid.NewGuid(), "buyer@threadonly.test")
                with { EmailThreadId = "email:root@threadonly.example" });

        context.ChangeTracker.Clear();
        var review = await service.ReconcileAsync(Candidate(202, 2031, null, null, 10),
            Intake("threadonly-reply", "threadonly-b", Guid.NewGuid(), sender: null)
                with { ThreadReferencedMessageIds = new[] { "email:root@threadonly.example" } });

        Assert.Equal(LeadOccurrenceClassification.PossibleMatchReviewRequired, review.Classification);
        Assert.Equal(0, review.LeadId);
        Assert.Equal(1, await context.Leads.CountAsync());
        var match = Assert.Single(await context.Set<LeadMatchCandidate>()
            .Where(x => x.OccurrenceId == review.OccurrenceId).ToListAsync());
        Assert.Equal(created.LeadId, match.CandidateLeadId);
        Assert.Contains("email thread", review.Reasons.Single(), StringComparison.Ordinal);
    }

    // ================================================================ beyond the recency window
    // Matching used to score only the 250 most recently created leads, so an amendment to any
    // older inquiry silently became a brand-new, unlinked lead. The targeted candidate probes
    // (normalized reference / content hash / customer scope / thread) must find the canonical
    // lead however deep it sits.

    [Fact]
    public async Task Amendment_to_a_lead_older_than_the_recency_window_is_still_versioned()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(203);
        Seed.BusinessUnit(context, 203); Seed.EmailConfig(context, 2031, 203); Seed.EmailIngest(context, 2041, 2031, "NeedsReview");
        await context.SaveChangesAsync();
        var service = new LeadIdentityApplicationService(context);

        var original = Candidate(203, 2041, "TENDER-OLD-7", "tenders@old.test", 10);
        original.BidClosingDate = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        original.CreatedDate = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
        var created = await service.ReconcileAsync(original,
            Intake("old-original", "old-a", Guid.NewGuid(), "tenders@old.test"));
        Assert.Equal(LeadOccurrenceClassification.New, created.Classification);

        // 260 newer leads push the canonical inquiry far outside the 250-lead recency window.
        context.ChangeTracker.Clear();
        for (var i = 0; i < 260; i++)
            context.Leads.Add(new Lead
            {
                Rfqno = $"FILLER-{i}", BuyersName = "Filler Buyer", RecDate = DateTime.UtcNow,
                LeadSource = "Seed", CreatedBy = "seed",
                CreatedDate = new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                BusinessUnitId = 203, EmailIngestsId = 2041
            });
        await context.SaveChangesAsync();

        // The manually uploaded amendment PDF: same reference and lines, no sender or buyer of
        // its own, and a moved closing date — the exact FR-RFQ-05 shape, months later.
        context.ChangeTracker.Clear();
        var amendment = Candidate(203, 2041, "TENDER-OLD-7", null, 10);
        amendment.BidClosingDate = new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);
        var revision = await service.ReconcileAsync(amendment,
            Intake("old-amendment", "old-b", Guid.NewGuid(), sender: null));

        Assert.Equal(LeadOccurrenceClassification.Revision, revision.Classification);
        Assert.Equal(created.LeadId, revision.LeadId);
        Assert.Equal(2, revision.RevisionNumber);
        // 1 canonical + 260 fillers — and no 262nd lead standing in for the amendment.
        Assert.Equal(261, await context.Leads.CountAsync());
        var canonical = await context.Leads.SingleAsync(x => x.Id == created.LeadId);
        Assert.Equal(new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc), canonical.BidClosingDate);
    }
}
