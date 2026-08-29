using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Successor coverage for the warning and correction rules that used to live behind the retired
/// intelligence conversion door. These tests enter through the canonical revision workbench,
/// persist the human decision, and promote only its approved lines.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class LeadParticipationWarningGovernancePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long Tenant = 947_201;
    private const long CustomerId = 947_211;
    private const int UomId = 9_472_302;
    private const long CurrencyId = 9_472_303;
    private const long ProductId = 9_472_304;
    private static DateTime Now => DateTime.UtcNow;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Workbench_displays_customer_line_identity_without_changing_normalized_revision_fingerprint()
    {
        var scenario = await CreateScenarioAsync([
            Line("2.1.3", 12, "EA", "SAR", "LINE-PUNCTUATION")
        ], "line-identity");
        await using var context = database.ContextFor(Tenant);

        var revisionLine = await context.Set<LeadItemRevision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == scenario.RevisionId);
        using (var snapshot = JsonDocument.Parse(revisionLine.SnapshotJson))
        {
            Assert.Equal(2, snapshot.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("213", snapshot.RootElement.GetProperty("line").GetString());
            Assert.Equal("2.1.3", snapshot.RootElement.GetProperty("lineItemNo").GetString());
        }

        var workbench = await new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context))
            .GetAsync(Tenant, scenario.LeadId);
        Assert.Equal("2.1.3", Assert.Single(workbench.Lines).LineItemNo);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_committed_bid_still_rejects_missing_hard_commercial_fields()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 0, null, null, "UNMATCHED-HARD")
        ], "hard-fields");
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "hard-fields");

        var quantity = await Assert.ThrowsAsync<ArgumentException>(() => participation.CommitDecisionAsync(
            Tenant, scenario.LeadId, Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0], "Source drawing reviewed by the bid desk.")],
                "hard-fields-quantity")));
        Assert.Contains("positive quantity", quantity.Message, StringComparison.OrdinalIgnoreCase);

        var unit = await Assert.ThrowsAsync<ArgumentException>(() => participation.CommitDecisionAsync(
            Tenant, scenario.LeadId, Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0], "Source drawing reviewed by the bid desk.", quantity: 25)],
                "hard-fields-unit")));
        Assert.Contains("unit of measure", unit.Message, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await context.Set<LeadParticipationDecision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
        Assert.Empty(await context.Rfqs.AsNoTracking().Where(x => x.LeadId == scenario.LeadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Generic_line_evidence_cannot_stand_in_for_quantity_and_uom_provenance()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 4, "EA", "SAR", "MISSING-CRITICAL-EVIDENCE")
        ], "missing-critical-evidence", seedCriticalEvidence: false);
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "missing-critical-evidence");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participation.CommitDecisionAsync(Tenant, scenario.LeadId,
                Decision(scenario, fit.Id,
                    [Bid(scenario.LineRevisionIds[0], "The source line was reviewed by the bid desk.")],
                    "missing-critical-evidence")));

        Assert.Contains("quantity", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unit of measure", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("governed extraction approval", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Exact_prose_citation_alone_cannot_replace_typed_or_human_approved_provenance()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 2, "EA", "SAR", "SPAN-ONLY-A")
        ], "exact-prose-span", seedCriticalEvidence: false, seedSourceSpan: true);
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "exact-prose-span");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participation.CommitDecisionAsync(
                Tenant, scenario.LeadId, Decision(scenario, fit.Id,
                    [Bid(scenario.LineRevisionIds[0], "Exact retained source citation reviewed.")],
                    "exact-prose-span")));

        Assert.Contains("cannot be committed or promoted", error.Message);
        Assert.Contains("quantity, unit of measure", error.Message);
        var workbench = await new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context))
            .GetAsync(Tenant, scenario.LeadId);
        Assert.Equal("NEEDS_CHECK", Assert.Single(workbench.Lines).VerificationStatus);
        Assert.Equal(0, workbench.SourceCoverage?.CoveredLines);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Workbench_checks_the_effective_bid_quantity_not_only_the_canonical_line()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 2, "EA", "SAR", "OVERRIDE-QTY-A")
        ], "effective-bid-quantity");
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "effective-bid-quantity");
        var draftCommand = Decision(scenario, fit.Id,
            [Bid(scenario.LineRevisionIds[0], "Draft quantity differs from the source.", quantity: 3)],
            "effective-bid-quantity") with { Commit = false };
        await participation.CommitDecisionAsync(Tenant, scenario.LeadId, draftCommand);

        var workbench = await new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context))
            .GetAsync(Tenant, scenario.LeadId);

        Assert.Equal("NEEDS_CHECK", Assert.Single(workbench.Lines).VerificationStatus);
        Assert.Contains(workbench.Blockers,
            blocker => blocker.Code == "SOURCE_CRITICAL_FIELDS_UNVERIFIED");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Current_governed_extraction_approval_is_an_exact_provenance_override()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 4, "EA", "SAR", "REVIEW-OVERRIDE")
        ], "review-override", seedCriticalEvidence: false);
        await using var context = database.ContextFor(Tenant);
        var lead = await context.Leads.Include(x => x.LeadItems)
            .SingleAsync(x => x.Id == scenario.LeadId);
        var item = Assert.Single(lead.LeadItems, x => x.IsCurrentRevisionProjection);
        var reviewedOn = DateTime.UtcNow;
        var fromVersion = lead.ReviewVersion;
        lead.ReviewVersion++;
        lead.ReviewApprovedBy = "tests";
        lead.ReviewApprovedOn = reviewedOn;
        context.Add(new LeadReviewAudit
        {
            BusinessUnitId = Tenant,
            LeadId = lead.Id,
            FromVersion = fromVersion,
            ToVersion = lead.ReviewVersion,
            Action = "approve",
            ReviewedBy = "tests",
            Reason = "Reviewer verified the requested identity, quantity, and unit against the source.",
            BeforeJson = JsonSerializer.Serialize(new { reviewVersion = fromVersion }),
            AfterJson = JsonSerializer.Serialize(new
            {
                commercialFactsVerified = true,
                reviewVersion = lead.ReviewVersion,
                items = new[]
                {
                    new
                    {
                        id = item.EvidenceSourceLeadItemId ?? item.Id,
                        projectionId = item.Id,
                        itemMaterialCode = item.ItemMaterialCode,
                        manufacturerPartNumber = item.ManufacturerPartNumber,
                        productShortName = item.ProductShortName,
                        productShortDescription = item.ProductShortDescription,
                        itemText = item.ItemText,
                        quantity = item.Quantity,
                        unitOfMeasure = item.UnitOfMeasure
                    }
                }
            }),
            ReviewedOn = reviewedOn
        });
        await context.SaveChangesAsync();

        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "review-override");
        var decision = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0], "Current extraction approval covers this source line.")],
                "review-override"));

        Assert.True(decision.IsCommitted);
        Assert.Equal(LeadParticipationOutcome.FullBid, decision.Outcome);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Incomplete_or_stale_review_audit_cannot_replace_exact_source_provenance()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 4, "EA", "SAR", "INCOMPLETE-REVIEW-OVERRIDE")
        ], "incomplete-review-override", seedCriticalEvidence: false,
            linkCurrentRevisionDocument: false);
        await using var context = database.ContextFor(Tenant);
        var lead = await context.Leads.Include(x => x.LeadItems)
            .SingleAsync(x => x.Id == scenario.LeadId);
        var item = Assert.Single(lead.LeadItems, x => x.IsCurrentRevisionProjection);
        var reviewedOn = DateTime.UtcNow;
        var fromVersion = lead.ReviewVersion;
        lead.ReviewVersion++;
        lead.ReviewApprovedBy = "tests";
        lead.ReviewApprovedOn = reviewedOn;
        context.Add(new LeadReviewAudit
        {
            BusinessUnitId = Tenant,
            LeadId = lead.Id,
            FromVersion = fromVersion,
            ToVersion = lead.ReviewVersion,
            Action = "approve",
            ReviewedBy = "tests",
            Reason = "This row deliberately omits the governed after-image version.",
            BeforeJson = JsonSerializer.Serialize(new { reviewVersion = fromVersion }),
            AfterJson = JsonSerializer.Serialize(new
            {
                commercialFactsVerified = true,
                items = new[]
                {
                    new
                    {
                        id = item.EvidenceSourceLeadItemId ?? item.Id,
                        projectionId = item.Id,
                        itemMaterialCode = item.ItemMaterialCode,
                        quantity = item.Quantity,
                        unitOfMeasure = item.UnitOfMeasure
                    }
                }
            }),
            ReviewedOn = reviewedOn
        });
        await context.SaveChangesAsync();

        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "incomplete-review-override");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participation.CommitDecisionAsync(Tenant, scenario.LeadId,
                Decision(scenario, fit.Id,
                    [Bid(scenario.LineRevisionIds[0], "An incomplete audit must fail closed.")],
                    "incomplete-review-override")));

        Assert.Contains("governed extraction approval", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Set<LeadParticipationDecision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Real_review_approval_documentless_human_revision_commits_and_promotes_from_governed_audit()
    {
        var seeded = await CreateScenarioAsync([
            Line("00010", 4, "EA", "SAR", "REAL-REVIEW-OVERRIDE")
        ], "real-review-override", linkCurrentRevisionDocument: false);

        await using var context = database.ContextFor(Tenant);
        var beforeReview = await context.Leads.Include(x => x.LeadItems)
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == seeded.LeadId);
        beforeReview.CommercialFactsVerified = false;
        beforeReview.RequiresCommercialReview = true;
        beforeReview.ReviewApprovedBy = null;
        beforeReview.ReviewApprovedOn = null;
        await context.SaveChangesAsync();
        var currentItem = Assert.Single(beforeReview.LeadItems, x => x.IsCurrentRevisionProjection);

        var reviewed = await new LeadRepository(context).SubmitLeadReviewAsync(
            seeded.LeadId,
            Tenant,
            new LeadReviewSubmitDTO
            {
                ExpectedVersion = beforeReview.ReviewVersion,
                Action = "approve",
                Reason = "The reviewer verified identity, quantity and unit against the retained customer document.",
                Items =
                [
                    new LeadItemReviewDTO
                    {
                        Id = currentItem.Id,
                        LineItemNo = currentItem.LineItemNo,
                        ProductShortName = currentItem.ProductShortName,
                        ProductShortDescription = currentItem.ProductShortDescription,
                        CommodityProduct = currentItem.CommodityProduct,
                        ItemMaterialCode = currentItem.ItemMaterialCode,
                        Currency = currentItem.Currency,
                        UnitOfMeasure = currentItem.UnitOfMeasure,
                        UnitPrice = currentItem.UnitPrice,
                        Quantity = currentItem.Quantity,
                        ManufacturerName = currentItem.ManufacturerName,
                        ManufacturerPartNumber = currentItem.ManufacturerPartNumber,
                        AlternateProductName = currentItem.AlternateProductName,
                        AlternatePartNumber = currentItem.AlternatePartNumber,
                        ItemText = currentItem.ItemText,
                        LeadTime = currentItem.LeadTime
                    }
                ]
            },
            "tests");
        Assert.NotNull(reviewed);

        context.ChangeTracker.Clear();
        var approvedLead = await context.Leads.AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == seeded.LeadId);
        var humanRevision = await context.Set<LeadRevision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == approvedLead.CurrentRevisionId);
        Assert.Equal(LeadProcessingPath.HumanReview, humanRevision.ProcessingPath);
        Assert.False(await context.Set<LeadOccurrenceDocument>().AsNoTracking().AnyAsync(x =>
            x.BusinessUnitId == Tenant && x.OccurrenceId == humanRevision.EstablishedByOccurrenceId));
        var humanRevisionLineIds = await context.Set<LeadItemRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == humanRevision.Id)
            .OrderBy(x => x.LineNumber).Select(x => x.Id).ToArrayAsync();
        var scenario = seeded with
        {
            RevisionId = humanRevision.Id,
            RevisionNumber = humanRevision.RevisionNumber,
            LineRevisionIds = humanRevisionLineIds
        };

        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "real-review-override");
        var workbench = await new LeadDecisionWorkbenchService(context, new LeadOutcomeReasons(context))
            .GetAsync(Tenant, scenario.LeadId);
        Assert.Equal("VERIFIED", workbench.VerificationStatus);
        Assert.Equal("VERIFIED", Assert.Single(workbench.Lines).VerificationStatus);
        Assert.Equal(1, workbench.SourceCoverage?.CoveredLines);
        Assert.DoesNotContain(workbench.Blockers, blocker => blocker.Code.StartsWith("SOURCE_"));
        var decision = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0], "Current governed review covers the exact approved line.")],
                "real-review-override"));
        var promoted = await new RfqPromotionService(context, new UnexpectedEvidenceStorage())
            .PromoteAsync(Tenant, scenario.LeadId,
                Promotion(scenario, decision, "real-review-override"));

        Assert.True(decision.IsCommitted);
        Assert.Equal(1, promoted.PromotedLineCount);
        Assert.Equal(1, await context.Rfqitems.AsNoTracking().CountAsync(x => x.Rfqid == promoted.RfqId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Deterministic_customer_resolution_rebinds_source_lineage_and_promotes_the_resolved_customer()
    {
        var scenario = await CreateResolvedCustomerScenarioAsync(
            "customer-resolution-lineage", linkSourceBeforeResolution: true);
        await using var context = database.ContextFor(Tenant);
        var revisions = await context.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId)
            .OrderBy(x => x.RevisionNumber).ToListAsync();
        var current = revisions[^1];
        var previousOccurrenceId = revisions[^2].EstablishedByOccurrenceId;
        Assert.Equal(LeadProcessingPath.HumanReview, current.ProcessingPath);
        Assert.Equal("customer-resolution", current.CreatedBy);

        var previousDocuments = await context.Set<LeadOccurrenceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant
                && x.OccurrenceId == previousOccurrenceId)
            .Select(x => x.SourceDocumentId).OrderBy(x => x).ToArrayAsync();
        var currentDocuments = await context.Set<LeadOccurrenceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant
                && x.OccurrenceId == current.EstablishedByOccurrenceId)
            .Select(x => x.SourceDocumentId).OrderBy(x => x).ToArrayAsync();
        Assert.NotEmpty(previousDocuments);
        Assert.Equal(previousDocuments, currentDocuments);

        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "customer-resolution-lineage");
        var decision = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0], "The retained customer request was verified for bidding.")],
                "customer-resolution-lineage"));
        var promoted = await new RfqPromotionService(context,
                new ExactEvidenceStorage(scenario.StorageUri, scenario.EvidenceHash, scenario.EvidenceBytes))
            .PromoteAsync(Tenant, scenario.LeadId,
                Promotion(scenario, decision, "customer-resolution-lineage"));

        var rfq = await context.Rfqs.AsNoTracking().SingleAsync(x => x.Id == promoted.RfqId);
        Assert.Equal(CustomerId, rfq.CustomerId);
        Assert.Equal(1, promoted.PromotedLineCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_source_link_added_only_to_the_pre_resolution_revision_is_stale_and_fails_closed()
    {
        var scenario = await CreateResolvedCustomerScenarioAsync(
            "customer-resolution-stale-lineage", linkSourceBeforeResolution: false);
        await using var context = database.ContextFor(Tenant);
        var revisions = await context.Set<LeadRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId)
            .OrderBy(x => x.RevisionNumber).ToListAsync();
        Assert.True(revisions.Count >= 2);
        var sourceDocumentId = await context.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.ContentHash == scenario.EvidenceHash)
            .Select(x => x.Id).SingleAsync();
        var previousOccurrenceId = revisions[^2].EstablishedByOccurrenceId;
        var currentOccurrenceId = revisions[^1].EstablishedByOccurrenceId;
        context.Add(new LeadOccurrenceDocument
        {
            BusinessUnitId = Tenant,
            OccurrenceId = previousOccurrenceId,
            SourceDocumentId = sourceDocumentId,
            Role = "Primary",
            Ordinal = 1,
            LinkedAtUtc = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        Assert.False(await context.Set<LeadOccurrenceDocument>().AsNoTracking().AnyAsync(x =>
            x.BusinessUnitId == Tenant && x.OccurrenceId == currentOccurrenceId));

        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "customer-resolution-stale-lineage");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            participation.CommitDecisionAsync(Tenant, scenario.LeadId,
                Decision(scenario, fit.Id,
                    [Bid(scenario.LineRevisionIds[0], "A stale relation must not authorize this bid.")],
                    "customer-resolution-stale-lineage")));

        Assert.Contains("retained source", error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Set<LeadParticipationDecision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
        Assert.Empty(await context.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Incomplete_bid_draft_is_durable_but_cannot_be_committed_or_promoted()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 4, null, null, "DRAFT-INCOMPLETE")
        ], "incomplete-draft");
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "incomplete-draft");
        var line = Bid(scenario.LineRevisionIds[0],
            "The draft retains the buyer line while commercial identity is completed.");

        var draft = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            new CommitLeadParticipationCommand(
                scenario.RevisionId, scenario.RevisionNumber, null, false, fit.Id, [line],
                $"warning-decision:incomplete-draft:{scenario.LeadId}", "tests"));

        Assert.False(draft.IsCommitted);
        Assert.Equal(LeadParticipationOutcome.Pending, draft.Outcome);
        var draftLine = Assert.Single(draft.Lines);
        Assert.Equal(4, draftLine.Quantity);
        Assert.Null(draftLine.UomId);
        Assert.Null(draftLine.CurrencyId);
        Assert.Empty(await context.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());

        var rejected = await Assert.ThrowsAsync<ArgumentException>(() =>
            participation.CommitDecisionAsync(Tenant, scenario.LeadId,
                new CommitLeadParticipationCommand(
                    scenario.RevisionId, scenario.RevisionNumber, draft.Sequence, true, fit.Id, [line],
                    $"warning-decision:incomplete-commit:{scenario.LeadId}", "tests")));
        Assert.Contains("unit of measure", rejected.Message, StringComparison.OrdinalIgnoreCase);

        var persisted = await context.Set<LeadParticipationDecision>().AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId)
            .ToListAsync();
        var immutableDraft = Assert.Single(persisted);
        Assert.False(immutableDraft.IsCommitted);
        Assert.False(Assert.Single(immutableDraft.Lines).DecisionIsCommitted);
        Assert.Empty(await context.Set<RfqPromotion>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Acknowledged_warning_and_partial_no_bid_are_immutable_and_only_bid_lines_promote()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 25, "EA", "SAR", "UNMATCHED-BID"),
            Line("00020", 7, "EA", "SAR", "UNMATCHED-EXCLUDED")
        ], "partial-corrections");
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "partial-corrections");
        var noBid = new LeadLineParticipationCommand(
            scenario.LineRevisionIds[1], LeadLineParticipationChoice.NoBid,
            ReasonCode: "OUT_OF_SCOPE", ReasonNotes: "The second line is outside our approved product scope.");

        var missingNote = await Assert.ThrowsAsync<ArgumentException>(() => participation.CommitDecisionAsync(
            Tenant, scenario.LeadId, Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0], "ok", ProductId, 25, "EA", "SAR"), noBid],
                "partial-missing-note")));
        Assert.Contains("meaningful human acknowledgement", missingNote.Message, StringComparison.OrdinalIgnoreCase);

        var decision = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            Decision(scenario, fit.Id,
                [Bid(scenario.LineRevisionIds[0],
                    "Buyer drawing and catalog substitution were reviewed by the bid desk.",
                    ProductId, 25, "EA", "SAR"), noBid],
                "partial-commit"));

        Assert.Equal(LeadParticipationOutcome.PartialBid, decision.Outcome);
        var bid = Assert.Single(decision.Lines, x => x.Choice == LeadLineParticipationChoice.Bid);
        Assert.Equal(25, bid.Quantity);
        Assert.Equal("EA", bid.UnitOfMeasure);
        Assert.Equal(UomId, bid.UomId);
        Assert.Equal("SAR", bid.Currency);
        Assert.Equal(CurrencyId, bid.CurrencyId);
        Assert.Equal(ProductId, bid.ProductId);
        Assert.Contains("NeedsAttention", bid.WarningSnapshotJson, StringComparison.Ordinal);
        Assert.Contains("No catalog match found", bid.WarningSnapshotJson, StringComparison.OrdinalIgnoreCase);
        Assert.Single(decision.Lines, x => x.Choice == LeadLineParticipationChoice.NoBid);

        var promotionCommand = Promotion(scenario, decision, "partial");
        var promotion = new RfqPromotionService(context,
            new ExactEvidenceStorage(scenario.StorageUri, scenario.EvidenceHash, scenario.EvidenceBytes));
        var promoted = await promotion.PromoteAsync(Tenant, scenario.LeadId, promotionCommand);
        var rfq = await context.Rfqs.AsNoTracking().Include(x => x.Rfqitems)
            .SingleAsync(x => x.Id == promoted.RfqId);
        var promotedLine = Assert.Single(rfq.Rfqitems);
        Assert.Equal(ProductId, promotedLine.ProductId);
        Assert.Equal(25, promotedLine.Quantity);
        Assert.Equal("EA", promotedLine.UnitOfMeasure);
        Assert.Equal("SAR", promotedLine.Currency);
        Assert.Equal(scenario.LineRevisionIds[0], promotedLine.SourceLeadItemRevisionId);

        // A network retry after commit must return the durable receipt, not manufacture a
        // second formal RFQ or a second copy of an approved line.
        var replay = await promotion.PromoteAsync(Tenant, scenario.LeadId, promotionCommand);
        Assert.True(replay.Replayed);
        Assert.Equal(promoted.PromotionId, replay.PromotionId);
        Assert.Equal(promoted.RfqId, replay.RfqId);
        Assert.Equal(promoted.RfqNumber, replay.RfqNumber);

        // A client may lose the first response and generate a fresh transport key. The durable
        // Lead/revision/participation winner remains authoritative even though promotion has
        // already advanced the Lead lifecycle beyond QUALIFIED.
        var freshKeyReplay = await promotion.PromoteAsync(Tenant, scenario.LeadId,
            promotionCommand with { IdempotencyKey = $"warning-promotion:fresh-retry:{scenario.LeadId}" });
        Assert.True(freshKeyReplay.Replayed);
        Assert.Equal(promoted.PromotionId, freshKeyReplay.PromotionId);
        Assert.Equal(promoted.RfqId, freshKeyReplay.RfqId);
        Assert.Equal(1, await context.Set<RfqPromotion>().AsNoTracking()
            .CountAsync(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId));
        Assert.Equal(1, await context.Rfqs.AsNoTracking()
            .CountAsync(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId));
        Assert.Equal(1, await context.Rfqitems.AsNoTracking()
            .CountAsync(x => x.Rfqid == promoted.RfqId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Twenty_acknowledged_lines_commit_and_promote_without_losing_warning_evidence()
    {
        var lines = Enumerable.Range(1, 20)
            .Select(i => Line(i.ToString("D5"), 10 + i, "EA", null, $"UNMATCHED-{i:D2}"))
            .ToArray();
        var scenario = await CreateScenarioAsync(lines, "twenty-lines");
        await using var context = database.ContextFor(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "twenty-lines");
        var decisions = scenario.LineRevisionIds.Select(id => Bid(id,
            "Reviewed against the source bid list and confirmed with the buyer.", currency: "SAR")).ToArray();

        var decision = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            Decision(scenario, fit.Id, decisions, "twenty-lines"));
        Assert.Equal(LeadParticipationOutcome.FullBid, decision.Outcome);
        Assert.Equal(20, decision.Lines.Count);
        Assert.All(decision.Lines, line =>
        {
            Assert.Equal(CurrencyId, line.CurrencyId);
            Assert.Contains("NeedsAttention", line.WarningSnapshotJson, StringComparison.Ordinal);
        });

        var promoted = await new RfqPromotionService(context,
                new ExactEvidenceStorage(scenario.StorageUri, scenario.EvidenceHash, scenario.EvidenceBytes))
            .PromoteAsync(Tenant, scenario.LeadId, Promotion(scenario, decision, "twenty-lines"));
        Assert.Equal(20, promoted.PromotedLineCount);
        Assert.Equal(20, await context.Rfqitems.AsNoTracking().CountAsync(x => x.Rfqid == promoted.RfqId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Full_no_bid_disqualifies_the_lead_and_can_never_create_an_rfq()
    {
        var scenario = await CreateScenarioAsync([
            // Missing/zero source quantity is a valid reason to decline a line. Commercial
            // completeness is a Bid invariant, not a prerequisite for recording No-bid.
            Line("00010", 0, null, null, "DECLINED-01"),
            Line("00020", 5, "EA", "SAR", "DECLINED-02")
        ], "full-no-bid");
        // Exercise the same forced-RLS execution role used by an authenticated tenant request.
        // Owner-role coverage cannot reveal missing grants or policy failures at transaction commit.
        await using var context = database.TenantContextWithRls(Tenant);
        var participation = Service(context);
        var fit = await FitAsync(participation, scenario, "full-no-bid");
        var lines = scenario.LineRevisionIds.Select(id => new LeadLineParticipationCommand(
            id,
            LeadLineParticipationChoice.NoBid,
            ReasonCode: "OUT_OF_SCOPE",
            ReasonNotes: "The requested line is outside the approved product scope."))
            .ToArray();

        var decision = await participation.CommitDecisionAsync(Tenant, scenario.LeadId,
            new CommitLeadParticipationCommand(
                scenario.RevisionId,
                scenario.RevisionNumber,
                null,
                true,
                fit.Id,
                lines,
                $"warning-decision:full-no-bid:{scenario.LeadId}",
                "tests",
                "OUT_OF_SCOPE",
                "The bid desk confirmed that none of the requested scope can be supplied."));

        Assert.Equal(LeadParticipationOutcome.NoBid, decision.Outcome);
        Assert.All(decision.Lines, line => Assert.Equal(LeadLineParticipationChoice.NoBid, line.Choice));
        Assert.Null(decision.Lines.Single(line => line.LeadItemRevisionId == scenario.LineRevisionIds[0]).Quantity);
        Assert.Equal(5, decision.Lines.Single(line => line.LeadItemRevisionId == scenario.LineRevisionIds[1]).Quantity);
        var lead = await context.Leads.AsNoTracking().Include(x => x.LeadStatus)
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == scenario.LeadId);
        Assert.Equal("DISQUALIFIED", lead.LeadStatus?.SetupCode);
        Assert.Empty(await context.Set<RfqPromotion>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
        Assert.Empty(await context.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());

        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FitAsync(participation, scenario, "full-no-bid-without-reopen"));
        Assert.Contains("manager", locked.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reopen", locked.Message, StringComparison.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InvalidOperationException>(() => new RfqPromotionService(context,
                new ExactEvidenceStorage(scenario.StorageUri, scenario.EvidenceHash, scenario.EvidenceBytes))
            .PromoteAsync(Tenant, scenario.LeadId, Promotion(scenario, decision, "full-no-bid")));
        Assert.Empty(await context.Set<RfqPromotion>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
        Assert.Empty(await context.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
    }

    private async Task<Scenario> CreateScenarioAsync(
        IReadOnlyList<LeadItem> lines, string suffix, bool seedCriticalEvidence = true,
        bool linkCurrentRevisionDocument = true, bool seedSourceSpan = false)
    {
        await SeedTenantAsync();
        var batchId = Guid.NewGuid();
        var key = $"participation-warning:{suffix}:{batchId:N}";
        var requiresIncompleteAmendment = lines.Any(line => line.Quantity <= 0);
        var candidate = new Lead
        {
            Rfqno = $"WARN-{suffix}-{batchId:N}", BuyersName = "SEC Bid Desk", RecDate = Now,
            BidClosingDate = Now.AddDays(14), LeadSource = "ParticipationWarningTests",
            CreatedBy = "tests", CreatedDate = Now, BusinessUnitId = Tenant,
            NoOfLineItems = lines.Count
        };
        // A Lead cannot legitimately enter QUALIFIED with an unresolved current quantity. For
        // downstream warning tests, establish a commercially valid revision 1 first; after the
        // governed qualification below, reconcile the requested incomplete values as revision 2.
        // That models the real production risk: an amendment can invalidate previously reviewed
        // commercial facts, and participation/promotion must still refuse unsafe Bid scope.
        foreach (var line in requiresIncompleteAmendment
                     ? lines.Select(QualificationSafeLine)
                     : lines)
            candidate.LeadItems.Add(line);

        long leadId;
        await using (var context = database.ContextFor(Tenant))
        {
            var reconciled = await new LeadIdentityApplicationService(context).ReconcileAsync(candidate,
                new LeadIntakeDescriptor(
                    batchId, "ManualUpload", key, key, null, "ParticipationWarningTests", null,
                    $"RFQ {suffix}", $"{suffix}.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 20480,
                    new string('a', 64), null, null, Now, Now, LeadProcessingPath.Deterministic,
                    false, null, "Service", "tests", key), CancellationToken.None);
            leadId = reconciled.LeadId;
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            lead.ResolveCommercialIdentity(CustomerId, null, "CUSTOMER_CONFIRMED");
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
            await new LeadIdentityApplicationService(context).AppendHumanRevisionAsync(
                Tenant, leadId, "tests", "Test reviewer confirmed the customer identity.",
                $"warning-customer-revision:{Tenant}:{leadId}:{suffix}");
        }

        foreach (var target in new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" })
        {
            await using var context = database.ContextFor(Tenant);
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            await new LifecycleApplicationService(context).TransitionLeadAsync(Tenant, leadId,
                new LifecycleActor("tests", "ParticipationWarningTests"),
                new LifecycleTransitionCommand(target, lead.LifecycleVersion, null, null,
                    "Seed", $"{suffix}-{target}", $"lead-{leadId}",
                    $"warning-{suffix}-{target}:{leadId}"), false, CancellationToken.None);
        }

        if (requiresIncompleteAmendment)
        {
            var amendmentBatchId = Guid.NewGuid();
            var amendmentKey = $"{key}:amendment";
            var amendment = new Lead
            {
                Rfqno = candidate.Rfqno, BuyersName = candidate.BuyersName, RecDate = candidate.RecDate,
                BidClosingDate = candidate.BidClosingDate, LeadSource = candidate.LeadSource,
                CreatedBy = "tests", CreatedDate = Now, BusinessUnitId = Tenant,
                NoOfLineItems = lines.Count
            };
            foreach (var line in lines) amendment.LeadItems.Add(line);

            await using var amendmentContext = database.ContextFor(Tenant);
            var reconciled = await new LeadIdentityApplicationService(amendmentContext).ReconcileAsync(amendment,
                new LeadIntakeDescriptor(
                    amendmentBatchId, "ManualUpload", amendmentKey, amendmentKey, null,
                    "ParticipationWarningTests", null, $"RFQ {suffix} amendment", $"{suffix}-amendment.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 20480,
                    new string('b', 64), null, null, Now, Now, LeadProcessingPath.Deterministic,
                    false, null, "Service", "tests", amendmentKey), CancellationToken.None);
            Assert.Equal(leadId, reconciled.LeadId);
            Assert.Equal(LeadOccurrenceClassification.Revision, reconciled.Classification);
            Assert.Equal(3, reconciled.RevisionNumber);
            var amendedLead = await amendmentContext.Leads
                .Include(lead => lead.LeadStatus)
                .Include(lead => lead.LeadItems)
                .SingleAsync(lead => lead.Id == leadId);
            Assert.Equal("QUALIFIED", amendedLead.LeadStatus?.SetupCode);
            Assert.Equal(reconciled.RevisionId, amendedLead.CurrentRevisionId);
            Assert.Contains(amendedLead.LeadItems,
                line => line.IsCurrentRevisionProjection && line.Quantity <= 0);
        }

        var evidenceBytes = Encoding.UTF8.GetBytes(string.Join('\n', lines.Select(x =>
            $"{x.LineItemNo}|{x.ItemMaterialCode}|{x.Quantity}|{x.UnitOfMeasure}|{x.Currency}")));
        var evidenceHash = Convert.ToHexString(SHA256.HashData(evidenceBytes)).ToLowerInvariant();
        var storageUri = $"memory://participation-warning/{suffix}-{leadId}.xlsx";
        await SeedEvidenceAsync(leadId, suffix, evidenceBytes, evidenceHash, seedCriticalEvidence,
            linkCurrentRevisionDocument, seedSourceSpan);

        await using var read = database.ContextFor(Tenant);
        var current = await read.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
        var lineIds = await read.Set<LeadItemRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == current.CurrentRevisionId)
            .OrderBy(x => x.LineNumber).Select(x => x.Id).ToArrayAsync();
        return new Scenario(leadId, current.CurrentRevisionId!.Value, current.CurrentRevisionNumber,
            lineIds, storageUri, evidenceHash, evidenceBytes);
    }

    private async Task<Scenario> CreateResolvedCustomerScenarioAsync(
        string suffix, bool linkSourceBeforeResolution)
    {
        await SeedTenantAsync();
        var batchId = Guid.NewGuid();
        var domain = $"{suffix}-{batchId:N}.example";
        var key = $"participation-warning:{suffix}:{batchId:N}";
        var candidate = new Lead
        {
            Rfqno = $"WARN-{suffix}-{batchId:N}",
            BuyersName = "Unresolved buyer",
            CustomerBuyerEmailExtracted = $"buyer@{domain}",
            RecDate = Now,
            BidClosingDate = Now.AddDays(14),
            LeadSource = "ParticipationWarningTests",
            CreatedBy = "tests",
            CreatedDate = Now,
            BusinessUnitId = Tenant,
            NoOfLineItems = 1
        };
        candidate.LeadItems.Add(Line("00010", 4, "EA", "SAR", $"RESOLVED-{batchId:N}"));

        long leadId;
        await using (var context = database.ContextFor(Tenant))
        {
            context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = Tenant,
                CustomerId = CustomerId,
                IdentifierType = CustomerIdentifierType.Domain,
                NormalizedValue = domain,
                DisplayValue = domain,
                IsVerified = true,
                Confidence = 0.99m,
                Source = "CustomerProfile",
                EffectiveFrom = Now.AddDays(-1)
            });
            await context.SaveChangesAsync();
            var reconciled = await new LeadIdentityApplicationService(context).ReconcileAsync(candidate,
                new LeadIntakeDescriptor(
                    batchId, "ManualUpload", key, key, null, "ParticipationWarningTests", null,
                    $"RFQ {suffix}", $"{suffix}.xlsx",
                    "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", 20480,
                    new string('c', 64), null, null, Now, Now, LeadProcessingPath.Deterministic,
                    false, null, "Service", "tests", key), CancellationToken.None);
            leadId = reconciled.LeadId;
        }

        var evidenceBytes = Encoding.UTF8.GetBytes($"00010|resolved-customer|4|EA|SAR|{suffix}|{batchId:N}");
        var evidenceHash = Convert.ToHexString(SHA256.HashData(evidenceBytes)).ToLowerInvariant();
        await SeedEvidenceAsync(leadId, suffix, evidenceBytes, evidenceHash,
            seedCriticalEvidence: true, linkCurrentRevisionDocument: linkSourceBeforeResolution);

        var retryOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .EnableDetailedErrors()
            .Options;
        await using (var context = new ErpRfqAutomationContext(retryOptions, new StubTenant(Tenant)))
        {
            var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, leadId);
            Assert.Equal(CustomerId, outcome.CustomerId);
            var lead = await context.Leads.SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == leadId);
            lead.CommercialFactsVerified = true;
            await context.SaveChangesAsync();
        }

        foreach (var target in new[] { "PENDING_IDENTIFICATION", "ASSIGNED", "UNDER_REVIEW", "QUALIFIED" })
        {
            await using var context = database.ContextFor(Tenant);
            var lead = await context.Leads.SingleAsync(x => x.Id == leadId);
            await new LifecycleApplicationService(context).TransitionLeadAsync(Tenant, leadId,
                new LifecycleActor("tests", "ParticipationWarningTests"),
                new LifecycleTransitionCommand(target, lead.LifecycleVersion, null, null,
                    "Seed", $"{suffix}-{target}", $"lead-{leadId}",
                    $"warning-{suffix}-{target}:{leadId}"), false, CancellationToken.None);
        }

        await using var read = database.ContextFor(Tenant);
        var current = await read.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
        var lineIds = await read.Set<LeadItemRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == current.CurrentRevisionId)
            .OrderBy(x => x.LineNumber).Select(x => x.Id).ToArrayAsync();
        return new Scenario(leadId, current.CurrentRevisionId!.Value, current.CurrentRevisionNumber,
            lineIds, $"memory://participation-warning/{suffix}-{leadId}.xlsx",
            evidenceHash, evidenceBytes);
    }

    private async Task SeedEvidenceAsync(
        long leadId, string suffix, byte[] bytes, string hash, bool seedCriticalEvidence,
        bool linkCurrentRevisionDocument, bool seedSourceSpan = false)
    {
        await using var context = database.ContextFor(Tenant);
        var lead = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == leadId);
        var revision = await context.Set<LeadRevision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == lead.CurrentRevisionId);
        var occurrence = await context.Set<LeadIngestionOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == revision.EstablishedByOccurrenceId);
        // A customer correction appends a documentless human revision in a shared correction
        // batch. The synthetic source document still belongs to the last actual intake batch;
        // using the correction batch would collapse otherwise independent test corpora.
        var evidenceBatchId = occurrence.RecordKind == LeadOccurrenceRecordKind.IdentityBaseline
            ? await (from priorRevision in context.Set<LeadRevision>().AsNoTracking()
                     join priorOccurrence in context.Set<LeadIngestionOccurrence>().AsNoTracking()
                         on priorRevision.EstablishedByOccurrenceId equals priorOccurrence.Id
                     where priorRevision.BusinessUnitId == Tenant && priorRevision.LeadId == leadId
                         && priorRevision.RevisionNumber < revision.RevisionNumber
                         && priorOccurrence.RecordKind != LeadOccurrenceRecordKind.IdentityBaseline
                     orderby priorRevision.RevisionNumber descending
                     select priorOccurrence.BatchId).FirstAsync()
            : occurrence.BatchId;
        var corpus = await context.Set<DocumentCorpus>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == Tenant && x.BatchId == evidenceBatchId);
        if (corpus is null)
        {
            corpus = DocumentCorpus.Create(Tenant, evidenceBatchId, CorpusSourceType.ManualUpload);
            context.Add(corpus);
            await context.SaveChangesAsync();
        }
        var location = $"participation-warning/{suffix}-{leadId}.xlsx";
        var job = new ExtractionJob
        {
            BatchId = evidenceBatchId, BusinessUnitId = Tenant,
            SourceType = ExtractionSourceType.ManualUpload, ContentHash = hash,
            StoragePath = $"memory://{location}", FileName = $"{suffix}.xlsx", FileType = "xlsx",
            Status = ExtractionStatus.Succeeded, Priority = 0, SchedulerTag = 0, Attempts = 1,
            MaxAttempts = 5, NextAttemptAt = Now, ResultLeadId = leadId, CreatedOn = Now, UpdatedOn = Now
        };
        context.Add(job);
        await context.SaveChangesAsync();
        var document = SourceDocument.Create(Tenant, corpus.Id, hash, $"{suffix}.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "memory", location, hash, bytes.Length);
        document.ReleaseFromQuarantine("memory", location, hash);
        document.BindExtractionJob(job.Id);
        context.Add(document);
        await context.SaveChangesAsync();
        var sourceOccurrence = SourceDocumentOccurrence.Create(
            Tenant, document.Id, corpus.Id, $"participation-warning:{suffix}:{leadId}", "{}");
        context.Add(sourceOccurrence);
        await context.SaveChangesAsync();
        job.SourceDocumentOccurrenceId = sourceOccurrence.Id;
        sourceOccurrence.BindExtractionJob(job.Id);
        sourceOccurrence.MarkProcessing();
        sourceOccurrence.MarkResolved();
        if (linkCurrentRevisionDocument)
            context.Add(new LeadOccurrenceDocument
        {
            BusinessUnitId = Tenant, OccurrenceId = revision.EstablishedByOccurrenceId,
            SourceDocumentId = document.Id, Role = "Primary", Ordinal = 1,
            LinkedAtUtc = DateTimeOffset.UtcNow
        });
        var runId = Guid.NewGuid();
        var run = ExtractionRun.Create(Tenant, document.Id, runId, job.Id, 1,
            "native-spreadsheet/participation-warning-test", "lead-evidence/v1");
        var page = DocumentPage.Create(Tenant, document.Id, 1, 100, 100);
        var inquiry = CanonicalInquiry.Create(Tenant, corpus.Id, 1);
        inquiry.PopulateHeader(lead.Rfqno, lead.BuyersName, lead.RecDate, lead.BidClosingDate);
        inquiry.BindLead(leadId);
        context.AddRange(run, page, inquiry);
        await context.SaveChangesAsync();
        var region = DocumentRegion.Create(Tenant, page.Id, DocumentRegionType.Table,
            0, 0, 100, 100, Encoding.UTF8.GetString(bytes), 1m);
        context.Add(region);
        await context.SaveChangesAsync();
        var canonicalLines = lead.LeadItems.OrderBy(x => x.LineItemNo).Select((item, index) =>
        {
            var canonical = CanonicalLineItem.Create(Tenant, inquiry.Id, index + 1,
                item.ProductShortDescription ?? item.ItemMaterialCode ?? "Requested line",
                item.Quantity > 0 ? item.Quantity : null, item.UnitOfMeasure);
            canonical.Enrich(null, item.ManufacturerPartNumber, item.Currency, null, null, "{}",
                CanonicalValidationStatus.Valid);
            canonical.BindLeadItem(item.Id);
            return (item, canonical);
        }).ToArray();
        context.AddRange(canonicalLines.Select(x => x.canonical));
        await context.SaveChangesAsync();
        foreach (var (item, canonical) in canonicalLines)
        {
            context.Add(FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "requestedLine",
                item.ProductShortDescription, item.ItemMaterialCode, 1m,
                "participation-warning-test", runId, validationStatus: FieldValidationStatus.Valid));
            if (seedSourceSpan)
            {
                var span = $"Line {item.LineItemNo}: {item.ItemMaterialCode}, quantity "
                    + $"{Convert.ToString(item.Quantity, System.Globalization.CultureInfo.InvariantCulture)} {item.UnitOfMeasure}";
                context.Add(FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "SourceSpan",
                    span, item.ItemMaterialCode, 1m,
                    "participation-warning-test", runId, validationStatus: FieldValidationStatus.Valid));
            }
            if (seedCriticalEvidence)
                context.AddRange(
                    FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "Quantity",
                        Convert.ToString(item.Quantity, System.Globalization.CultureInfo.InvariantCulture),
                        Convert.ToString(item.Quantity, System.Globalization.CultureInfo.InvariantCulture), 1m,
                        "participation-warning-test", runId, valueKind: FieldValueKind.Number,
                        validationStatus: FieldValidationStatus.Valid),
                    FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "UnitOfMeasure",
                        item.UnitOfMeasure, item.UnitOfMeasure, 1m,
                        "participation-warning-test", runId, validationStatus: FieldValidationStatus.Valid));
        }
        await context.SaveChangesAsync();
    }

    private async Task SeedTenantAsync()
    {
        await using var owner = database.ContextFor(null);
        if (await owner.BusinessUnits.AnyAsync(x => x.Id == Tenant)) return;
        var businessUnit = Seed.BusinessUnit(owner, Tenant);
        owner.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, "tests"));
        owner.SetupMasters.Add(new SetupMaster
        {
            SetupId = 9_472_301, BusinessUnitId = Tenant, SetupType = LeadOutcomeReasons.SetupType,
            SetupCode = "OUT_OF_SCOPE", SetupValue = "Out of scope", Description = "Out of scope",
            IsActive = true, CreatedBy = "tests", CreatedOn = Now
        });
        Seed.Customer(owner, CustomerId, Tenant, "Saudi Electricity Company");
        owner.SetUoms.Add(new SetUom
        {
            UomId = UomId, BusinessUnitId = Tenant, UomCode = "EA", UomName = "Each",
            IsActive = true, CreatedBy = "tests", CreatedDate = Now
        });
        owner.Currencies.Add(new Currency
        {
            Id = CurrencyId, BusinessUnitId = Tenant, Code = "SAR", CurrencyName = "Saudi Riyal",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true,
            CreatedBy = "tests", CreatedOn = Now
        });
        owner.Products.Add(new Product
        {
            Id = ProductId, Buid = Tenant, PartNo = "APPROVED-SUBSTITUTE",
            ProductName = "Approved catalog substitute", IsActive = true,
            CreatedBy = "tests", CreatedOn = Now
        });
        await owner.SaveChangesAsync();
    }

    private static LeadParticipationService Service(ErpRfqAutomationContext context) => new(
        context, new LeadDecisionService(context, new GrossMarginService(context)),
        new LeadOutcomeReasons(context));

    private static Task<LeadFitAssessmentResult> FitAsync(
        LeadParticipationService service, Scenario scenario, string suffix) =>
        service.RecordFitAssessmentAsync(Tenant, scenario.LeadId,
            new RecordLeadFitAssessmentCommand(
                scenario.RevisionId, scenario.RevisionNumber, null, "FIT",
                "The reviewer confirmed eligibility, capability, delivery, compliance and commercials.",
                LeadParticipationService.GovernedFitCriterionCodes
                    .Select(code => new LeadFitCriterionCommand(code, "PASS", "Confirmed by the reviewer."))
                    .ToArray(), $"warning-fit:{suffix}:{scenario.LeadId}", "tests"));

    private static CommitLeadParticipationCommand Decision(
        Scenario scenario, long fitId, IReadOnlyList<LeadLineParticipationCommand> lines, string suffix) =>
        new(scenario.RevisionId, scenario.RevisionNumber, null, true, fitId, lines,
            $"warning-decision:{suffix}:{scenario.LeadId}", "tests");

    private static LeadLineParticipationCommand Bid(long revisionLineId, string note,
        long? productId = null, int? quantity = null, string? uom = null, string? currency = null) =>
        new(revisionLineId, LeadLineParticipationChoice.Bid, ReasonNotes: note,
            ProductId: productId, Quantity: quantity, UnitOfMeasure: uom, Currency: currency);

    private static PromoteLeadToRfqCommand Promotion(
        Scenario scenario, LeadParticipationResult decision, string suffix) =>
        new(scenario.RevisionId, scenario.RevisionNumber, decision.Sequence, decision.Id,
            $"warning-promotion:{suffix}:{scenario.LeadId}", "tests");

    private static LeadItem Line(string lineNo, int quantity, string? uom, string? currency, string part) => new()
    {
        LineItemNo = lineNo, ItemMaterialCode = part, ManufacturerPartNumber = part,
        ProductShortDescription = "Ball valve 2IN class 300", Quantity = quantity,
        UnitOfMeasure = uom, Currency = currency
    };

    private static LeadItem QualificationSafeLine(LeadItem line) => new()
    {
        LineItemNo = line.LineItemNo,
        ItemMaterialCode = line.ItemMaterialCode,
        ManufacturerPartNumber = line.ManufacturerPartNumber,
        ProductShortDescription = line.ProductShortDescription,
        Quantity = line.Quantity is > 0 ? line.Quantity : 1,
        UnitOfMeasure = line.UnitOfMeasure,
        Currency = line.Currency
    };

    private sealed record Scenario(long LeadId, long RevisionId, int RevisionNumber,
        IReadOnlyList<long> LineRevisionIds, string StorageUri, string EvidenceHash, byte[] EvidenceBytes);

    private sealed class ExactEvidenceStorage(string storageUri, string hash, byte[] bytes) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceObject(storageUri, "memory", storageUri, hash, null, content.Length));
        public Task<Stream> OpenVerifiedReadAsync(string requestedUri, string requestedHash,
            CancellationToken ct = default)
        {
            Assert.Equal(storageUri, requestedUri);
            Assert.Equal(hash, requestedHash);
            Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
            return Task.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class UnexpectedEvidenceStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new InvalidOperationException("The human-review audit path must not invent a current physical evidence object.");
        public Task<Stream> OpenVerifiedReadAsync(string requestedUri, string requestedHash,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("The documentless human revision must promote through its governed audit.");
    }
}
