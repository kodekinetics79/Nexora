using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
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
            Assert.Equal("213", snapshot.RootElement.GetProperty("line").GetString());

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
    public async Task Acknowledged_warning_corrections_and_partial_no_bid_are_immutable_and_only_bid_lines_promote()
    {
        var scenario = await CreateScenarioAsync([
            Line("00010", 0, null, null, "UNMATCHED-CORRECTED"),
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

        await Assert.ThrowsAsync<InvalidOperationException>(() => new RfqPromotionService(context,
                new ExactEvidenceStorage(scenario.StorageUri, scenario.EvidenceHash, scenario.EvidenceBytes))
            .PromoteAsync(Tenant, scenario.LeadId, Promotion(scenario, decision, "full-no-bid")));
        Assert.Empty(await context.Set<RfqPromotion>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
        Assert.Empty(await context.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadId == scenario.LeadId).ToListAsync());
    }

    private async Task<Scenario> CreateScenarioAsync(IReadOnlyList<LeadItem> lines, string suffix)
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
            Assert.Equal(2, reconciled.RevisionNumber);
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
        await SeedEvidenceAsync(leadId, suffix, evidenceBytes, evidenceHash);

        await using var read = database.ContextFor(Tenant);
        var current = await read.Leads.AsNoTracking().SingleAsync(x => x.Id == leadId);
        var lineIds = await read.Set<LeadItemRevision>().AsNoTracking()
            .Where(x => x.BusinessUnitId == Tenant && x.LeadRevisionId == current.CurrentRevisionId)
            .OrderBy(x => x.LineNumber).Select(x => x.Id).ToArrayAsync();
        return new Scenario(leadId, current.CurrentRevisionId!.Value, current.CurrentRevisionNumber,
            lineIds, storageUri, evidenceHash, evidenceBytes);
    }

    private async Task SeedEvidenceAsync(long leadId, string suffix, byte[] bytes, string hash)
    {
        await using var context = database.ContextFor(Tenant);
        var lead = await context.Leads.Include(x => x.LeadItems).SingleAsync(x => x.Id == leadId);
        var revision = await context.Set<LeadRevision>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == lead.CurrentRevisionId);
        var occurrence = await context.Set<LeadIngestionOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == Tenant && x.Id == revision.EstablishedByOccurrenceId);
        var corpus = DocumentCorpus.Create(Tenant, occurrence.BatchId, CorpusSourceType.ManualUpload);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var location = $"participation-warning/{suffix}-{leadId}.xlsx";
        var job = new ExtractionJob
        {
            BatchId = occurrence.BatchId, BusinessUnitId = Tenant,
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
            var evidence = FieldEvidence.ForLineItem(Tenant, region.Id, canonical.Id, "requestedLine",
                item.ProductShortDescription, item.ItemMaterialCode, 1m,
                "participation-warning-test", runId, validationStatus: FieldValidationStatus.Valid);
            context.Add(evidence);
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
}
