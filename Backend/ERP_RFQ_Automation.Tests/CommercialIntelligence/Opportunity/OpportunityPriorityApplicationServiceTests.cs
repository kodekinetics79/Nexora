using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialIntelligence.Opportunity;

public sealed class OpportunityPriorityApplicationServiceTests
{
    private const long TenantId = 72_001;

    [Fact]
    public async Task Reconcile_UsesOnlyExactDecisionGradeSignalsAndPersistsExcludedEvidence()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 101, ownerId: 201, canonicalCustomerId: 301);
        lead.BidClosingDate = new DateTime(2035, 1, 15, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();

        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, Brief(
            totalItems: 2,
            new CoverageItem
            {
                LeadItemId = 1001, Matched = true, MatchType = "code", ProductId = 501,
                InStock = true, HasCatalogOnHand = true, CatalogQtyOnHand = 999m,
                UnitPrice = 5_000m, PriceSource = "catalog"
            },
            new CoverageItem
            {
                LeadItemId = 1002, Matched = true, MatchType = "name", ProductId = 502,
                InStock = true, HasCatalogOnHand = true, CatalogQtyOnHand = 888m,
                UnitPrice = 7_000m, PriceSource = "catalog"
            }));
        var service = Service(context, decisions);

        var result = await service.ReconcileAsync(TenantId, Reconcile("safe-signals"), default);
        var recommendation = await context.OpportunityRecommendations.AsNoTracking().SingleAsync();
        using var evidence = JsonDocument.Parse(recommendation.EvidenceSnapshotJson);

        Assert.Equal(1, result.Created);
        Assert.Equal(1, recommendation.SampleSize);
        Assert.Equal("RESOLVE_UNMATCHED_PARTS", recommendation.RecommendedActionCode);
        Assert.Equal(50m, evidence.RootElement.GetProperty("exactCoveragePct").GetDecimal());
        Assert.Single(evidence.RootElement.GetProperty("exactMatchedItems").EnumerateArray());
        Assert.Equal(
            ["ambiguous_name_match", "raw_qty_on_hand", "mixed_currency_value", "margin"],
            evidence.RootElement.GetProperty("excludedSignals").EnumerateArray()
                .Select(x => x.GetString()!).ToArray());
        Assert.DoesNotContain("catalogQtyOnHand", recommendation.EvidenceSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("unitPrice", recommendation.EvidenceSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("marginPotential", recommendation.EvidenceSnapshotJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_RefusesAmbiguousMixedAndUnresolvedEvidenceAsPrioritySignals()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 102, ownerId: 202);
        await context.SaveChangesAsync();

        var brief = Brief(
            totalItems: 1,
            new CoverageItem
            {
                LeadItemId = 1101, Matched = true, MatchType = "name", ProductId = 601,
                CatalogQtyOnHand = 10_000m, UnitPrice = 1_000_000m, PriceSource = "catalog"
            });
        brief.Currency = null;
        brief.EstimatedValue = null;
        brief.MarginPotentialPct = null;
        brief.Customer = new CustomerHistory
        {
            CustomerId = 999,
            IsExistingCustomer = true,
            IdentityEvidence = CustomerIdentityEvidence.HeuristicAmbiguous,
            IsDecisionGradeIdentity = false
        };
        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, brief);
        var service = Service(context, decisions);

        await service.ReconcileAsync(TenantId, Reconcile("uncertain-evidence"), default);
        var item = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Equal(0, item.PriorityScore);
        Assert.Equal(0, item.SampleSize);
        Assert.Equal("Low", item.PriorityBand);
        Assert.True(item.InsufficientEvidence);
        Assert.Equal("SOURCE_UNKNOWN_PARTS", item.RecommendedActionCode);
        Assert.Contains(item.Reasons, reason => reason.Contains("canonical identity is unresolved", StringComparison.Ordinal));
        Assert.Contains(item.Reasons, reason => reason.Contains("ambiguous name matches", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_WithUnchangedEvidenceKeepsOneImmutableStableRecommendation()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 103, ownerId: 203, canonicalCustomerId: 303);
        lead.Aiconfidence = 0.88m;
        await context.SaveChangesAsync();

        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, Brief(1, ExactItem(1201, 701)));
        var service = Service(context, decisions);

        var first = await service.ReconcileAsync(TenantId, Reconcile("stable-first"), default);
        var persisted = await context.OpportunityRecommendations.AsNoTracking().SingleAsync();
        var second = await service.ReconcileAsync(TenantId, Reconcile("stable-second"), default);
        var replayed = await context.OpportunityRecommendations.AsNoTracking().SingleAsync();

        Assert.Equal(1, first.Created);
        Assert.Equal(0, first.Replayed);
        Assert.Equal(0, second.Created);
        Assert.Equal(1, second.Replayed);
        Assert.Equal(persisted.Id, replayed.Id);
        Assert.Equal(persisted.EvidenceHash, replayed.EvidenceHash);
        Assert.Equal(persisted.GeneratedAtUtc, replayed.GeneratedAtUtc);
        Assert.Single(await context.OpportunityEvents.AsNoTracking()
            .Where(x => x.EventType == "OpportunityRecommendation.Generated").ToListAsync());
    }

    [Fact]
    public async Task Feedback_RejectsStaleRecommendationAndReplaysIdenticalIdempotentRequest()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 104, ownerId: 204, canonicalCustomerId: 304);
        await context.SaveChangesAsync();

        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, Brief(1, ExactItem(1301, 801)));
        var service = Service(context, decisions);
        await service.ReconcileAsync(TenantId, Reconcile("feedback-initial"), default);
        var initial = await context.OpportunityRecommendations.AsNoTracking().SingleAsync();

        decisions.Set(lead.Id, Brief(2, ExactItem(1301, 801)));
        await service.ReconcileAsync(TenantId, Reconcile("feedback-revised"), default);
        var current = await context.OpportunityRecommendations.AsNoTracking()
            .OrderByDescending(x => x.Id).FirstAsync();
        Assert.NotEqual(initial.Id, current.Id);

        await Assert.ThrowsAsync<OpportunityPriorityConflictException>(() => service.RecordFeedbackAsync(
            TenantId,
            initial.Id,
            Feedback(initial.Id, "stale-feedback"),
            OpportunityPriorityAccessScope.ForOwner(204),
            default));

        var command = Feedback(current.Id, "accepted-feedback");
        var first = await service.RecordFeedbackAsync(
            TenantId, current.Id, command, OpportunityPriorityAccessScope.ForOwner(204), default);
        var replay = await service.RecordFeedbackAsync(
            TenantId, current.Id, command, OpportunityPriorityAccessScope.ForOwner(204), default);

        Assert.Equal(first, replay);
        Assert.Single(await context.OpportunityFeedback.AsNoTracking().ToListAsync());
        Assert.Single(await context.OpportunityOperations.AsNoTracking()
            .Where(x => x.OperationType == "Feedback").ToListAsync());
    }

    [Fact]
    public async Task OwnerScope_ReturnsOnlyAssignedRecommendationsAndDeniesOtherOwnerCase()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var mine = SeedLead(context, 105, ownerId: 205, canonicalCustomerId: 305);
        var other = SeedLead(context, 106, ownerId: 206, canonicalCustomerId: 306);
        await context.SaveChangesAsync();

        var decisions = new StubLeadDecisionService();
        decisions.Set(mine.Id, Brief(1, ExactItem(1401, 901)));
        decisions.Set(other.Id, Brief(1, ExactItem(1402, 902)));
        var service = Service(context, decisions);
        await service.ReconcileAsync(TenantId, Reconcile("owner-scope"), default);

        var page = await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForOwner(205), default);

        var item = Assert.Single(page.Items);
        Assert.Equal(mine.Id, item.LeadId);
        Assert.Equal(205, item.OwnerUserId);
        Assert.Equal("assigned_to_me", page.AccessScope);
        await Assert.ThrowsAsync<OpportunityPriorityNotFoundException>(() => service.GetForCommercialCaseAsync(
            TenantId, other.CommercialCaseId, OpportunityPriorityAccessScope.ForOwner(205), default));
    }

    [Fact]
    public async Task Reconcile_UsesBoundedCursorBatchesWithoutDroppingEligibleLeads()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var firstLead = SeedLead(context, 107, ownerId: 207, canonicalCustomerId: 307);
        var secondLead = SeedLead(context, 108, ownerId: 208, canonicalCustomerId: 308);
        await context.SaveChangesAsync();

        var decisions = new StubLeadDecisionService();
        decisions.Set(firstLead.Id, Brief(1, ExactItem(1501, 1001)));
        decisions.Set(secondLead.Id, Brief(1, ExactItem(1502, 1002)));
        var service = Service(context, decisions);

        var first = await service.ReconcileAsync(
            TenantId,
            new ReconcileOpportunityPrioritiesCommand("correlation-batch-1", "batch-1", "test-actor", null, 1),
            default);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextAfterCommercialCaseId);
        Assert.Equal(1, first.Evaluated);

        var second = await service.ReconcileAsync(
            TenantId,
            new ReconcileOpportunityPrioritiesCommand(
                "correlation-batch-2", "batch-2", "test-actor", first.NextAfterCommercialCaseId, 1),
            default);

        Assert.False(second.HasMore);
        Assert.Equal(1, second.Evaluated);
        Assert.Equal(2, await context.OpportunityRecommendations.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task Reconcile_SkipsTerminalCaseWithoutGeneratingRecommendation()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 109, ownerId: 209, canonicalCustomerId: 309);
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = 1601,
            BusinessUnitId = TenantId,
            SetupType = "QuoteStatus",
            SetupCode = "REJECTED",
            SetupValue = "Not accepted",
            IsActive = true,
            CreatedBy = "opportunity-priority-tests",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var rfq = new Rfq
        {
            Id = 1602,
            Rfqno = "RFQ-TERMINAL-1602",
            RecDate = DateTime.UtcNow,
            LeadId = lead.Id,
            BusinessUnitId = TenantId,
            CreatedBy = "opportunity-priority-tests",
            CreatedDate = DateTime.UtcNow
        };
        rfq.InheritCommercialIdentity(lead);
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();
        var quote = new Quote
        {
            Id = 1603,
            QuoteNo = "QT-TERMINAL-1603",
            Rfqid = rfq.Id,
            BusinessUnitId = TenantId,
            StatusId = 1601,
            QuoteDate = DateTime.UtcNow,
            CreatedBy = "opportunity-priority-tests",
            CreatedDate = DateTime.UtcNow
        };
        quote.InheritCommercialIdentity(rfq);
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        var result = await Service(context, new StubLeadDecisionService())
            .ReconcileAsync(TenantId, Reconcile("terminal-case"), default);

        Assert.Equal(1, result.TerminalCasesSkipped);
        Assert.Equal(0, result.Created);
        Assert.Empty(await context.OpportunityRecommendations.AsNoTracking().ToListAsync());
    }

    [Theory]
    [InlineData("ACCEPTED", "Not accepted", OpportunityOutcomeCode.QuoteWon)]
    [InlineData("ORDERED", "Order received", OpportunityOutcomeCode.QuoteWon)]
    [InlineData("REJECTED", "Accepted by customer was false", OpportunityOutcomeCode.QuoteLost)]
    [InlineData("EXPIRED", "Accepted previously", OpportunityOutcomeCode.QuoteExpired)]
    [InlineData("SENT", "Not accepted", null)]
    public void QuoteOutcomeCode_UsesOnlyCanonicalExactStatusCode(
        string statusCode,
        string statusValue,
        string? expected)
        => Assert.Equal(expected, OpportunityPriorityApplicationService.QuoteOutcomeCode(statusCode, statusValue));

    private static OpportunityPriorityApplicationService Service(
        ErpRfqAutomationContext context,
        ILeadDecisionService decisions)
        => new(context, new StubTenant(TenantId), decisions);

    private static Lead SeedLead(
        ErpRfqAutomationContext context,
        long leadId,
        long ownerId,
        long? canonicalCustomerId = null)
    {
        context.Users.Add(new User
        {
            Id = ownerId,
            FirstName = "Owner",
            LastName = ownerId.ToString(),
            Email = $"owner-{ownerId}@test.invalid",
            PasswordHash = "not-used",
            ImageUrl = "n/a",
            Buid = TenantId,
            IsActive = true,
            CreatedBy = "opportunity-priority-tests",
            CreatedOn = new DateTime(2026, 7, 29, 0, 0, 0, DateTimeKind.Utc)
        });
        var lead = Seed.Lead(context, leadId, TenantId);
        lead.AssignTo = ownerId;
        if (canonicalCustomerId.HasValue)
        {
            Seed.Customer(context, canonicalCustomerId.Value, TenantId, $"Customer {canonicalCustomerId}");
            lead.ResolveCommercialIdentity(canonicalCustomerId.Value, null, "VERIFIED");
        }
        return lead;
    }

    private static LeadDecisionBrief Brief(int totalItems, params CoverageItem[] items)
        => new()
        {
            Coverage = new CatalogCoverage
            {
                TotalItems = totalItems,
                CoveredItems = items.Count(x => x.Matched),
                Items = [.. items]
            },
            Customer = new CustomerHistory(),
            Deadline = new DeadlineFeasibility()
        };

    private static CoverageItem ExactItem(long leadItemId, long productId)
        => new()
        {
            LeadItemId = leadItemId,
            Matched = true,
            MatchType = "code",
            ProductId = productId
        };

    private static ReconcileOpportunityPrioritiesCommand Reconcile(string key)
        => new($"correlation-{key}", key, "test-actor");

    private static RecordOpportunityFeedbackCommand Feedback(long recommendationId, string key)
        => new(
            recommendationId,
            OpportunityFeedbackDecision.Accepted,
            null,
            "Confirmed against the current commercial evidence.",
            null,
            $"correlation-{key}",
            key,
            "owner-204",
            false);

    private sealed class StubLeadDecisionService : ILeadDecisionService
    {
        private readonly Dictionary<long, LeadDecisionBrief> _briefs = [];

        public void Set(long leadId, LeadDecisionBrief brief) => _briefs[leadId] = brief;

        public Task<LeadDecisionBrief> GetBriefAsync(
            long leadId,
            long businessUnitId,
            CancellationToken cancellationToken)
            => Task.FromResult(_briefs[leadId]);

        public Task<Dictionary<long, LeadDecisionSummary>> GetSummariesAsync(
            IEnumerable<long> leadIds,
            long businessUnitId,
            CancellationToken cancellationToken)
            => Task.FromResult(new Dictionary<long, LeadDecisionSummary>());
    }
}
