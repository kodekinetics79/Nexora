using System.Text.Json;
using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.LeadIdentity;
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
            ["ambiguous_name_match", "raw_qty_on_hand", "mixed_currency_value", "unverified_margin"],
            evidence.RootElement.GetProperty("excludedSignals").EnumerateArray()
                .Select(x => x.GetString()!).ToArray());
        Assert.DoesNotContain("catalogQtyOnHand", recommendation.EvidenceSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("unitPrice", recommendation.EvidenceSnapshotJson, StringComparison.Ordinal);
        Assert.DoesNotContain("marginPotential", recommendation.EvidenceSnapshotJson, StringComparison.Ordinal);
        using var componentDocument = JsonDocument.Parse(recommendation.ComponentsJson);
        Assert.Equal(7, componentDocument.RootElement.GetProperty("signals").GetArrayLength());
        Assert.Equal("insufficient_evidence", componentDocument.RootElement.GetProperty("status").GetString());
        Assert.Null(recommendation.ExpectedCommercialValue);
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
    public async Task Reconcile_DoesNotRewardInvalidCanonicalCustomerIdentity()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 109, ownerId: 209, canonicalCustomerId: 309);
        await context.SaveChangesAsync();

        var brief = Brief(1, ExactItem(1690, 1691));
        brief.Customer = new CustomerHistory
        {
            IdentityEvidence = CustomerIdentityEvidence.CanonicalInvalid,
            IsDecisionGradeIdentity = false
        };
        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, brief);
        var service = Service(context, decisions);

        await service.ReconcileAsync(TenantId, Reconcile("invalid-canonical-customer"), default);
        var item = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Equal(55, item.PriorityScore);
        Assert.Equal("RESOLVE_CUSTOMER_IDENTITY", item.RecommendedActionCode);
        Assert.Contains("unresolved or invalid", item.CurrentBlocker, StringComparison.Ordinal);
        Assert.Contains(item.Reasons, reason => reason.Contains("unresolved or invalid", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reconcile_WithExactPartsButMissingFulfilmentRecommendsEvidenceRefresh()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 108, ownerId: 208, canonicalCustomerId: 308);
        await context.SaveChangesAsync();

        var brief = Brief(1, ExactItem(1680, 1681));
        brief.Customer = new CustomerHistory
        {
            CustomerId = 308,
            IdentityEvidence = CustomerIdentityEvidence.Canonical,
            IsDecisionGradeIdentity = true
        };
        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, brief);
        var service = Service(context, decisions);

        await service.ReconcileAsync(TenantId, Reconcile("missing-fulfilment-action"), default);
        var item = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Equal("REFRESH_FULFILMENT_EVIDENCE", item.RecommendedActionCode);
        Assert.Contains("current immutable revision", item.CurrentBlocker, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_ComputesCurrencySafeShadowValueFromCompletePersistedEvidence()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 110, ownerId: 210, canonicalCustomerId: 310);
        lead.BidClosingDate = new DateTime(2035, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();
        await SeedCompleteFulfilmentAsync(context, lead, leadLineId: 1710, requested: 10m, available: 10m);

        var brief = Brief(1, ExactItem(1710, 1711));
        brief.EstimatedValue = 1_000m;
        brief.Currency = "USD";
        brief.ValueConfidence = "high";
        brief.MarginPotentialPct = 20m;
        brief.MarginCostedItems = 1;
        brief.IsMarginComplete = true;
        brief.Customer = new CustomerHistory
        {
            CustomerId = 310,
            IsExistingCustomer = true,
            IsDecisionGradeIdentity = true,
            IdentityEvidence = CustomerIdentityEvidence.Canonical,
            Quotes = 4,
            Orders = 2
        };
        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, brief);
        var service = Service(context, decisions);

        await service.ReconcileAsync(TenantId, Reconcile("measured-components"), default);
        var item = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Equal(70m, item.ExpectedCommercialValue);
        Assert.Equal("USD", item.ExpectedCommercialValueCurrency);
        Assert.Equal("shadow_unvalidated", item.ExpectedCommercialValueStatus);
        Assert.Equal("No evidence blocker is active.", item.CurrentBlocker);
        Assert.Equal("OPEN_OPPORTUNITY", item.RecommendedActionCode);
        Assert.Equal(7, item.Components.Count);
        Assert.Equal(11, item.AvailableActions.Count);
        Assert.Equal("evidenced_proxy", item.Components.Single(x => x.Code == "win_likelihood").Status);
        Assert.Equal(1m, item.Components.Single(x => x.Code == "fulfilment_confidence").Value);

        brief.MarginPotentialPct = 10m;
        decisions.Set(lead.Id, brief);
        await service.ReconcileAsync(TenantId, Reconcile("measured-low-margin"), default);
        var lowMargin = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);
        Assert.Equal("ESCALATE_APPROVAL", lowMargin.RecommendedActionCode);
        Assert.Equal(35m, lowMargin.ExpectedCommercialValue);
    }

    [Fact]
    public async Task Reconcile_DoesNotUseFulfilmentEvidenceFromSupersededLeadRevision()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 111, ownerId: 211, canonicalCustomerId: 311);
        lead.BidClosingDate = new DateTime(2035, 2, 2, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();
        await SeedCompleteFulfilmentAsync(context, lead, leadLineId: 1810, requested: 10m, available: 10m);
        await SeedCompleteFulfilmentAsync(context, lead, leadLineId: 1811, requested: 10m, available: null);

        var brief = Brief(1, ExactItem(1811, 1812));
        brief.EstimatedValue = 1_000m;
        brief.Currency = "USD";
        brief.ValueConfidence = "high";
        brief.MarginPotentialPct = 20m;
        brief.MarginCostedItems = 1;
        brief.IsMarginComplete = true;
        brief.Customer = new CustomerHistory
        {
            CustomerId = 311,
            IsExistingCustomer = true,
            IsDecisionGradeIdentity = true,
            IdentityEvidence = CustomerIdentityEvidence.Canonical,
            Quotes = 4,
            Orders = 2
        };
        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, brief);
        var service = Service(context, decisions);

        await service.ReconcileAsync(TenantId, Reconcile("current-revision-only"), default);
        var item = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Null(item.ExpectedCommercialValue);
        var fulfilment = item.Components.Single(x => x.Code == "fulfilment_confidence");
        Assert.Equal("unavailable", fulfilment.Status);
        Assert.Contains("has not been created", fulfilment.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reconcile_DoesNotTreatUnqualifiedIncomingSupplyAsDeadlineReady()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 112, ownerId: 212, canonicalCustomerId: 312);
        lead.BidClosingDate = new DateTime(2035, 2, 3, 0, 0, 0, DateTimeKind.Utc);
        await context.SaveChangesAsync();
        await SeedCompleteFulfilmentAsync(
            context, lead, leadLineId: 1910, requested: 10m, available: 0m,
            incoming: 10m, classification: CommercialResolutionClassification.KnownIncoming);

        var brief = Brief(1, ExactItem(1910, 1911));
        brief.EstimatedValue = 1_000m;
        brief.Currency = "USD";
        brief.ValueConfidence = "high";
        brief.MarginPotentialPct = 20m;
        brief.MarginCostedItems = 1;
        brief.IsMarginComplete = true;
        brief.Customer = new CustomerHistory
        {
            CustomerId = 312,
            IsDecisionGradeIdentity = true,
            IdentityEvidence = CustomerIdentityEvidence.Canonical,
            Quotes = 4,
            Orders = 2
        };
        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, brief);
        var service = Service(context, decisions);

        await service.ReconcileAsync(TenantId, Reconcile("incoming-not-qualified"), default);
        var item = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Equal(0m, item.ExpectedCommercialValue);
        Assert.Equal("SEARCH_KNOWN_SUPPLIERS", item.RecommendedActionCode);
        Assert.Contains("incoming supply is excluded", item.Components
            .Single(x => x.Code == "fulfilment_confidence").Evidence, StringComparison.Ordinal);
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
    public async Task Reconcile_SupersedesLegacyBackfillEvenWhenEvidenceHashIsUnchanged()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = SeedLead(context, 113, ownerId: 213, canonicalCustomerId: 313);
        await context.SaveChangesAsync();

        var decisions = new StubLeadDecisionService();
        decisions.Set(lead.Id, Brief(1, ExactItem(2010, 2011)));
        var service = Service(context, decisions);
        await service.ReconcileAsync(TenantId, Reconcile("legacy-component-initial"), default);
        var original = await context.OpportunityRecommendations.SingleAsync();
        original.PolicyVersion = "opportunity-priority-shadow-v2";
        original.RecommendationKey = $"{original.CommercialCaseId}:opportunity-priority-shadow-v2:{original.EvidenceHash}";
        original.ComponentsJson = """
            {"signals":[],"expectedCommercialValue":null,"currency":null,"status":"legacy_reconcile_required","responseDeadline":null,"currentBlocker":"Reconcile to generate commercial components."}
            """;
        await context.SaveChangesAsync();

        var result = await service.ReconcileAsync(TenantId, Reconcile("legacy-component-reconcile"), default);
        var current = Assert.Single((await service.QueryAsync(
            TenantId, new OpportunityPriorityQuery(), OpportunityPriorityAccessScope.ForTenant(), default)).Items);

        Assert.Equal(1, result.Created);
        Assert.Equal(0, result.Replayed);
        Assert.NotEqual(original.Id, current.RecommendationId);
        Assert.Equal(7, current.Components.Count);
        Assert.Equal(2, await context.OpportunityRecommendations.CountAsync());
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
        var invalidReplacement = command with
        {
            Decision = OpportunityFeedbackDecision.Replaced,
            ReplacementActionCode = "FREE_TEXT_ACTION"
        };
        await Assert.ThrowsAsync<ArgumentException>(() => service.RecordFeedbackAsync(
            TenantId, current.Id, invalidReplacement, OpportunityPriorityAccessScope.ForOwner(204), default));
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

    private static async Task SeedCompleteFulfilmentAsync(
        ErpRfqAutomationContext context,
        Lead lead,
        long leadLineId,
        decimal requested,
        decimal? available,
        decimal incoming = 0m,
        CommercialResolutionClassification classification = CommercialResolutionClassification.KnownInStock)
    {
        var now = DateTime.UtcNow;
        var revisionNumber = Math.Max(1, lead.CurrentRevisionNumber + 1);
        var batch = new LeadIngestionBatch
        {
            Id = Guid.NewGuid(),
            BusinessUnitId = TenantId,
            SourceChannel = "Test",
            CreatedBy = "opportunity-priority-tests",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var occurrence = new LeadIngestionOccurrence
        {
            BusinessUnitId = TenantId,
            Batch = batch,
            SourceChannel = "Test",
            IdempotencyKey = $"opportunity-occurrence-{lead.Id}-{revisionNumber}",
            LogicalInquiryFingerprint = new string((char)('a' + revisionNumber), 64),
            Classification = LeadOccurrenceClassification.New,
            ProcessingPath = LeadProcessingPath.Deterministic,
            IngestedAtUtc = DateTimeOffset.UtcNow,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ActorId = "opportunity-priority-tests",
            CorrelationId = $"opportunity-correlation-{lead.Id}-{revisionNumber}"
        };
        var revision = new LeadRevision
        {
            BusinessUnitId = TenantId,
            Lead = lead,
            RevisionNumber = revisionNumber,
            EstablishedByOccurrence = occurrence,
            LogicalInquiryFingerprint = new string((char)('b' + revisionNumber), 64),
            SnapshotJson = "{}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "opportunity-priority-tests",
            ProcessingPath = LeadProcessingPath.Deterministic
        };
        var line = new LeadItemRevision
        {
            Id = leadLineId,
            BusinessUnitId = TenantId,
            LineNumber = 1,
            LineFingerprint = new string('c', 64),
            SnapshotJson = JsonSerializer.Serialize(new { part = "TEST-1710", quantity = requested })
        };
        revision.Items.Add(line);
        context.Add(revision);
        await context.SaveChangesAsync();
        lead.CurrentRevisionId = revision.Id;
        lead.CurrentRevisionNumber = revisionNumber;
        if (available.HasValue)
            context.Set<LeadLineCommercialResolution>().Add(new LeadLineCommercialResolution
        {
            BusinessUnitId = TenantId,
            LeadId = lead.Id,
            LeadRevisionId = revision.Id,
            LeadLineId = line.Id,
            ResolutionBatchId = Guid.NewGuid(),
            ResourceLimit = 10,
            RequestedPartNumber = "TEST-1710",
            RequestedQuantity = requested,
            Classification = classification,
            AvailableToPromise = available.Value,
            IncomingAvailable = incoming,
            FulfilmentJson = "{}",
            RelatedResourcesJson = "[]",
            ProductResolutionJson = "{}",
            ResolutionMethod = "LocalDeterministicTest",
            InventoryAsOfUtc = now,
            ResolvedOn = now
        });
        await context.SaveChangesAsync();
    }

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
