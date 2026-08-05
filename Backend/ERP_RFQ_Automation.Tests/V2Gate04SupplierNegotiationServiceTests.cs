using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class V2Gate04SupplierNegotiationServiceTests
{
    [Fact]
    public void Detector_evaluates_and_can_emit_all_gate_categories_from_current_tenant_evidence()
    {
        var current = Revision(3, 30, currencyId: 1, validUntil: null, incoterms: null,
            paymentTerms: null, price: 100m, available: null, leadDays: 0, isAlternate: true,
            partNumber: null);
        current.Evidence.Add(new SupplierQuoteFieldEvidence
        {
            Id = 301, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteLineId = current.Lines.Single().Id, FieldName = "UnitPrice",
            Confidence = .4m, Method = "LOCAL_RULE", Critical = true, ReviewRequired = true,
            CreatedOn = DateTime.UtcNow.AddDays(-1)
        });
        var previous = Revision(2, 20, currencyId: 1, DateTime.UtcNow.AddDays(10), "FCA",
            "NET 30", 50m, 10m, 5, false, "PN-1");
        var quote = Quote(3, current, previous);
        var competitors = new[]
        {
            Projection(1, 50m, 2, 1), Projection(1, 52m, 3, 1),
            Projection(2, 49m, 2, 2), Projection(2, 51m, 3, 2), Projection(2, 50m, 4, 2)
        };
        var input = new NegotiationInput(quote, current, new Supplier
        {
            GovernanceStatus = SupplierGovernanceStatuses.Unverified,
            VerificationStatus = SupplierVerificationStatuses.Pending
        }, competitors, [new SelectedOfferSnapshot(700, 40m, 1, DateTime.UtcNow.AddDays(-1))],
            [], "USD", DateTime.UtcNow);

        var workspace = SupplierNegotiationService.BuildWorkspace(input);

        Assert.Equal(SupplierBidQualityFlagCodes.All.Order(), workspace.EvaluatedCategories.Order());
        Assert.Equal(SupplierBidQualityFlagCodes.All.Order(), workspace.BidFlags.Select(x => x.Code).Order());
        Assert.All(workspace.BidFlags, flag => Assert.NotEmpty(flag.Evidence));
        Assert.All(workspace.BidFlags, flag => Assert.InRange(flag.Confidence, 0m, 1m));
    }

    [Fact]
    public void Seven_recommendation_types_are_shadow_only_and_unsupported_advice_is_suppressed()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "PREPAID",
            20m, 4m, 10, true, "ALT-1");
        var line = current.Lines.Single();
        current.FreightAmount = 20m;
        var alternateEvidence = new SupplierQuoteFieldEvidence
        {
            Id = 302, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteLineId = line.Id, FieldName = "AlternateAuthorization",
            NormalizedValue = "APPROVED", Confidence = 1m, Method = "MANUAL_ENTRY",
            Critical = true, ReviewRequired = true, CreatedOn = DateTime.UtcNow
        };
        current.Evidence.Add(alternateEvidence);
        current.ReviewDecisions.Add(new SupplierQuoteReviewDecision
        {
            Id = 303, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteFieldEvidenceId = alternateEvidence.Id,
            Status = SupplierQuoteReviewStatuses.Accepted, Reason = "Engineering approved alternate",
            ReviewedBy = "reviewer", ReviewedOn = DateTime.UtcNow, CorrelationId = "corr-alt"
        });
        var input = new NegotiationInput(Quote(1, current), current, new Supplier
        {
            GovernanceStatus = SupplierGovernanceStatuses.Approved,
            VerificationStatus = SupplierVerificationStatuses.Verified
        }, [Projection(1, 5m, 2, 1), Projection(1, 6m, 3, 1)], [], [], "USD", DateTime.UtcNow);

        var workspace = SupplierNegotiationService.BuildWorkspace(input);

        Assert.Equal("USD", workspace.CurrentRound.CurrencyCode);

        Assert.Equal(new[]
        {
            SupplierNegotiationRecommendationCodes.ApprovedAlternate,
            SupplierNegotiationRecommendationCodes.BestAndFinalPrice,
            SupplierNegotiationRecommendationCodes.FasterDelivery,
            SupplierNegotiationRecommendationCodes.FreightInclusiveOffer,
            SupplierNegotiationRecommendationCodes.ImprovedPaymentTerms,
            SupplierNegotiationRecommendationCodes.PartialImmediateAvailability,
            SupplierNegotiationRecommendationCodes.QuantityBreak
        }.Order(), workspace.Recommendations.Select(x => x.Code).Order());
        Assert.All(workspace.Recommendations, recommendation =>
        {
            Assert.Equal("SHADOW", recommendation.Mode);
            Assert.NotEmpty(recommendation.Evidence);
            Assert.Contains(recommendation.Limitations, x => x.Contains("cannot send", StringComparison.Ordinal));
        });

        line.MinimumOrderQuantity = null;
        line.AvailableQuantity = line.Quantity;
        line.IsAlternate = false;
        current.FreightAmount = 0;
        current.PaymentTerms = "NET 30";
        current.Evidence.Clear();
        var suppressed = SupplierNegotiationService.BuildWorkspace(input);
        Assert.DoesNotContain(suppressed.Recommendations, x => x.Code is
            SupplierNegotiationRecommendationCodes.QuantityBreak or
            SupplierNegotiationRecommendationCodes.FreightInclusiveOffer or
            SupplierNegotiationRecommendationCodes.ImprovedPaymentTerms or
            SupplierNegotiationRecommendationCodes.PartialImmediateAvailability or
            SupplierNegotiationRecommendationCodes.ApprovedAlternate);
    }

    [Fact]
    public async Task Decision_is_atomic_idempotent_and_does_not_mutate_offer_price_or_award()
    {
        using var fixture = new NegotiationFixture();
        await using var context = fixture.Context();
        var service = new SupplierNegotiationService(context);
        var command = new SupplierNegotiationCommand(fixture.BusinessUnitId, fixture.SupplierQuoteId,
            1, SupplierNegotiationRecommendationCodes.FreightInclusiveOffer,
            SupplierNegotiationDispositions.Prepared, "Ask the Supplier to confirm an inclusive offer.",
            "negotiation-decision-1", "buyer@example.test", "corr-negotiation-1");

        var created = await service.DecideAsync(command);
        var replay = await service.DecideAsync(command);

        Assert.False(created.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(created.DecisionId, replay.DecisionId);
        Assert.Equal(2, created.ResultingQuoteVersion);
        await using var verify = fixture.Context();
        Assert.Equal(2, await verify.SupplierQuotes.Where(x => x.Id == fixture.SupplierQuoteId)
            .Select(x => x.Version).SingleAsync());
        Assert.Equal(10m, await verify.SupplierQuoteLines.Select(x => x.UnitPrice).SingleAsync());
        Assert.Equal(0, await verify.Set<SourcingAward>().CountAsync());
        var decision = await verify.SupplierNegotiationDecisions.SingleAsync();
        using var evidence = JsonDocument.Parse(decision.EvidenceSnapshotJson);
        Assert.Equal("SHADOW", evidence.RootElement.GetProperty("mode").GetString());
        Assert.Equal(SupplierNegotiationRecommendationCodes.FreightInclusiveOffer,
            evidence.RootElement.GetProperty("recommendation").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Reused_key_changed_payload_and_stale_version_are_rejected_without_second_decision()
    {
        using var fixture = new NegotiationFixture();
        await using var context = fixture.Context();
        var service = new SupplierNegotiationService(context);
        var command = new SupplierNegotiationCommand(fixture.BusinessUnitId, fixture.SupplierQuoteId,
            1, SupplierNegotiationRecommendationCodes.FreightInclusiveOffer,
            SupplierNegotiationDispositions.Prepared, "Request inclusive freight.",
            "negotiation-conflict", "buyer@example.test", "corr-negotiation-conflict");
        await service.DecideAsync(command);

        await Assert.ThrowsAsync<SupplierQuoteConflictException>(() => service.DecideAsync(command with
        {
            Reason = "Different payload"
        }));
        await Assert.ThrowsAsync<SupplierQuoteConflictException>(() => service.DecideAsync(command with
        {
            IdempotencyKey = "negotiation-stale-version"
        }));

        await using var verify = fixture.Context();
        Assert.Equal(1, await verify.SupplierNegotiationDecisions.CountAsync());
        Assert.Equal(2, await verify.SupplierQuotes.Where(x => x.Id == fixture.SupplierQuoteId)
            .Select(x => x.Version).SingleAsync());
    }

    [Fact]
    public async Task Tenant_context_cannot_be_overridden_and_other_tenant_quote_is_not_visible()
    {
        using var fixture = new NegotiationFixture();
        await using var context = fixture.Context();
        var service = new SupplierNegotiationService(context);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetAsync(fixture.OtherBusinessUnitId, fixture.SupplierQuoteId));
        await using var otherTenant = fixture.Context(fixture.OtherBusinessUnitId);
        await Assert.ThrowsAsync<SupplierQuoteNotFoundException>(() =>
            new SupplierNegotiationService(otherTenant).GetAsync(
                fixture.OtherBusinessUnitId, fixture.SupplierQuoteId));
        await using var unscoped = fixture.UnscopedContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new SupplierNegotiationService(unscoped).GetAsync(
                fixture.BusinessUnitId, fixture.SupplierQuoteId));
    }

    [Fact]
    public async Task Capture_anchor_excludes_demand_lines_with_a_different_nexora_serial()
    {
        using var fixture = new NegotiationFixture();
        await using var context = fixture.Context();
        var demand = await context.CommercialDemandLines.SingleAsync(x =>
            x.Id == fixture.CommercialDemandLineId);
        demand.NexoraSerial = "NXR-MISMATCH";
        await context.SaveChangesAsync();

        var anchor = await new EfSupplierQuoteStore(context).ResolveAnchorAsync(
            fixture.BusinessUnitId, ProcurementTestData.Supplier,
            fixture.SupplierSolicitationId, fixture.SourcingCaseId, CancellationToken.None);

        Assert.NotNull(anchor);
        Assert.DoesNotContain(ProcurementTestData.RfqItem, anchor!.DemandLineByRfqItem.Keys);
    }

    [Fact]
    public void Low_price_outlier_is_flagged_without_recommending_a_price_increase()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            5m, 10m, 3, false, "PN-LOW");
        var input = new NegotiationInput(Quote(1, current), current, new Supplier
        {
            GovernanceStatus = SupplierGovernanceStatuses.Approved,
            VerificationStatus = SupplierVerificationStatuses.Verified
        }, [Projection(1, 10m, 3, 1), Projection(1, 12m, 3, 2)], [], [], "USD", DateTime.UtcNow);

        var workspace = SupplierNegotiationService.BuildWorkspace(input);

        Assert.Contains(workspace.BidFlags, x => x.Code == SupplierBidQualityFlagCodes.PriceOutlier);
        Assert.DoesNotContain(workspace.Recommendations,
            x => x.Code == SupplierNegotiationRecommendationCodes.BestAndFinalPrice);
    }

    [Fact]
    public void Alternate_recommendation_uses_the_latest_corrected_review_value()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            20m, 10m, 3, true, "ALT-CORRECTED");
        var line = current.Lines.Single();
        var evidence = new SupplierQuoteFieldEvidence
        {
            Id = 401, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteLineId = line.Id, FieldName = "AlternateAuthorization",
            NormalizedValue = "NO", Confidence = 1m, Method = "MANUAL_REVIEW",
            Critical = true, ReviewRequired = true, CreatedOn = DateTime.UtcNow
        };
        current.Evidence.Add(evidence);
        current.ReviewDecisions.Add(new SupplierQuoteReviewDecision
        {
            Id = 402, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteFieldEvidenceId = evidence.Id,
            Status = SupplierQuoteReviewStatuses.Corrected, CorrectedValue = "APPROVED",
            Reason = "Corrected approval", ReviewedBy = "reviewer",
            ReviewedOn = DateTime.UtcNow, CorrelationId = "corrected-alternate"
        });
        var input = new NegotiationInput(Quote(1, current), current, new Supplier
        {
            GovernanceStatus = SupplierGovernanceStatuses.Approved,
            VerificationStatus = SupplierVerificationStatuses.Verified
        }, [], [], [], "USD", DateTime.UtcNow);

        Assert.Contains(SupplierNegotiationService.BuildWorkspace(input).Recommendations,
            x => x.Code == SupplierNegotiationRecommendationCodes.ApprovedAlternate);

        current.ReviewDecisions.Add(new SupplierQuoteReviewDecision
        {
            Id = 403, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteFieldEvidenceId = evidence.Id,
            Status = SupplierQuoteReviewStatuses.Corrected, CorrectedValue = "NO",
            Reason = "Approval withdrawn", ReviewedBy = "reviewer",
            ReviewedOn = DateTime.UtcNow.AddSeconds(1), CorrelationId = "corrected-alternate-revoked"
        });
        Assert.DoesNotContain(SupplierNegotiationService.BuildWorkspace(input).Recommendations,
            x => x.Code == SupplierNegotiationRecommendationCodes.ApprovedAlternate);
    }

    [Fact]
    public void Zero_price_is_an_explicit_blocking_bid_quality_finding()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            0m, 10m, 3, false, "PN-ZERO");
        var input = new NegotiationInput(Quote(1, current), current, new Supplier
        {
            IsActive = true,
            GovernanceStatus = SupplierGovernanceStatuses.Approved,
            VerificationStatus = SupplierVerificationStatuses.Verified,
            ReadinessStatus = SupplierReadinessStatuses.Ready,
            ComplianceStatus = SupplierComplianceStatuses.Cleared,
            RiskStatus = SupplierRiskStatuses.Low
        }, [], [], [], "USD", DateTime.UtcNow);

        var finding = Assert.Single(SupplierNegotiationService.BuildWorkspace(input).BidFlags,
            x => x.Code == SupplierBidQualityFlagCodes.IncompleteCommercialTerms);
        Assert.True(finding.Blocking);
        Assert.Equal("CRITICAL", finding.Severity);
        Assert.Contains(finding.Evidence, x => x.Contains("price", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Shared_charges_are_allocated_by_commercial_value_across_mixed_units()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            100m, 10m, 3, false, "PN-EA");
        current.FreightAmount = 100m;
        current.Lines.Add(new SupplierQuoteLine
        {
            Id = 701, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id, LineNumber = 2,
            RfqItemId = 701, CommercialDemandLineId = 702, PartNumber = "PN-LOT",
            Description = "Lot-priced component", Quantity = 1m, AvailableQuantity = 1m,
            UnitOfMeasure = "LOT", UnitPrice = 1000m, LeadTimeDays = 3,
            AvailabilityType = "IN_STOCK"
        });
        var input = new NegotiationInput(Quote(1, current), current, EligibleSupplier(),
            [Projection(1, 70m, 3, 2), Projection(1, 72m, 3, 3)], [], [], "USD", DateTime.UtcNow);

        var recommendation = Assert.Single(SupplierNegotiationService.BuildWorkspace(input).Recommendations,
            x => x.Code == SupplierNegotiationRecommendationCodes.BestAndFinalPrice);

        Assert.Contains(recommendation.Evidence, x => x.Contains("current 105.0000", StringComparison.Ordinal));
    }

    [Fact]
    public void Evidence_payloads_are_bounded_while_preserving_the_authoritative_sample_size()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            0m, 10m, 3, false, "PN-0");
        current.Lines.Clear();
        for (var index = 1; index <= 150; index++)
            current.Lines.Add(new SupplierQuoteLine
            {
                Id = 700 + index, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
                LineNumber = index, RfqItemId = 700 + index, CommercialDemandLineId = 800 + index,
                PartNumber = $"PN-{index}", Description = "Component", Quantity = 1m,
                AvailableQuantity = 1m, UnitOfMeasure = "EA", UnitPrice = 0m,
                LeadTimeDays = 3, AvailabilityType = "IN_STOCK"
            });
        var input = new NegotiationInput(Quote(1, current), current, EligibleSupplier(),
            [], [], [], "USD", DateTime.UtcNow);

        var finding = Assert.Single(SupplierNegotiationService.BuildWorkspace(input).BidFlags,
            x => x.Code == SupplierBidQualityFlagCodes.IncompleteCommercialTerms);

        Assert.Equal(150, finding.SampleSize);
        Assert.Equal(SupplierNegotiationService.MaxEvidenceFacts, finding.Evidence.Count);
    }

    [Fact]
    public void Latest_reviewed_corrections_drive_negotiation_without_mutating_source_values()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            100m, 10m, 30, false, "PN-CORRECTED");
        var line = current.Lines.Single();
        var evidence = new SupplierQuoteFieldEvidence
        {
            Id = 901, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteLineId = line.Id, FieldName = "UnitPrice", NormalizedValue = "100",
            Confidence = .5m, Method = "LOCAL_RULE", Critical = true, ReviewRequired = true,
            CreatedOn = DateTime.UtcNow.AddMinutes(-2)
        };
        current.Evidence.Add(evidence);
        current.ReviewDecisions.Add(new SupplierQuoteReviewDecision
        {
            Id = 902, BusinessUnitId = 7, SupplierQuoteRevisionId = current.Id,
            SupplierQuoteFieldEvidenceId = evidence.Id, Status = SupplierQuoteReviewStatuses.Corrected,
            CorrectedValue = "50", Reason = "Reviewed against source", ReviewedBy = "reviewer",
            ReviewedOn = DateTime.UtcNow.AddMinutes(-1), CorrelationId = "corrected-price"
        });
        var input = new NegotiationInput(Quote(1, current), current, EligibleSupplier(),
            [Projection(1, 60m, 5, 2), Projection(1, 62m, 5, 3)], [], [], "USD", DateTime.UtcNow);

        var workspace = SupplierNegotiationService.BuildWorkspace(input);

        Assert.DoesNotContain(workspace.Recommendations,
            x => x.Code == SupplierNegotiationRecommendationCodes.BestAndFinalPrice);
        Assert.Equal(100m, line.UnitPrice);
    }

    [Fact]
    public void Comparison_cohort_uses_one_chronologically_current_offer_per_supplier_and_line()
    {
        var now = DateTime.UtcNow;
        var selected = SupplierNegotiationService.SelectCurrentCompetitors([
            new NegotiationProjection(1, 10, 700, 1, 70m, 70m, 10, 10, 5, 100, 101,
                now.AddDays(-2), true),
            new NegotiationProjection(2, 10, 700, 2, 72m, 72m, 10, 10, 4, 102, 103,
                now.AddDays(-1), true),
            new NegotiationProjection(3, 11, 700, 1, 74m, 74m, 10, 10, 3, 104, 105,
                now.AddHours(-1), true),
            new NegotiationProjection(4, 10, 701, 1, 76m, 76m, 10, 10, 2, 106, 107,
                now, true),
            new NegotiationProjection(5, 12, 700, 1, 1m, 1m, 10, 10, 1, 108, 109,
                now, false)
        ]);

        Assert.Equal(3, selected.Length);
        Assert.Contains(selected, x => x.Id == 2);
        Assert.DoesNotContain(selected, x => x.Id == 1);
        Assert.DoesNotContain(selected, x => x.Id == 5);
    }

    [Fact]
    public void Decision_history_discloses_when_the_returned_rows_are_truncated()
    {
        var current = Revision(1, 30, 1, DateTime.UtcNow.AddDays(30), "FCA", "NET 30",
            100m, 10m, 3, false, "PN-HISTORY");
        var decisions = Enumerable.Range(1, SupplierNegotiationService.MaxPriorDecisions)
            .Select(index => new SupplierNegotiationDecisionView(index, current.Id,
                SupplierNegotiationRecommendationCodes.BestAndFinalPrice,
                SupplierNegotiationDispositions.Prepared, "Reviewed", "V2.4-DETERMINISTIC-1",
                1, "buyer@example.test", DateTime.UtcNow.AddMinutes(-index), $"corr-{index}"))
            .ToArray();
        var input = new NegotiationInput(Quote(1, current), current, EligibleSupplier(), [],
            [], decisions, "USD", DateTime.UtcNow, decisions.Length + 1);

        var workspace = SupplierNegotiationService.BuildWorkspace(input);

        Assert.Equal(decisions.Length + 1, workspace.PriorDecisionTotal);
        Assert.True(workspace.PriorDecisionsTruncated);
        Assert.Equal(SupplierNegotiationService.MaxPriorDecisions, workspace.PriorDecisions.Count);
    }

    [Fact]
    public void Model_and_http_contracts_are_tenant_qualified_and_permission_scoped()
    {
        using var fixture = new NegotiationFixture();
        using var context = fixture.Context();
        var entity = context.Model.FindEntityType(typeof(SupplierNegotiationDecision))!;
        Assert.NotNull(entity.GetQueryFilter());
        Assert.Contains(entity.GetIndexes(), index => index.IsUnique &&
            index.Properties.Select(x => x.Name).SequenceEqual([
                nameof(SupplierNegotiationDecision.BusinessUnitId),
                nameof(SupplierNegotiationDecision.IdempotencyKey)]));
        Assert.Contains(entity.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(x => x.Name).SequenceEqual([
                nameof(SupplierNegotiationDecision.BusinessUnitId),
                nameof(SupplierNegotiationDecision.SupplierQuoteId)]));
        Assert.Contains(entity.GetForeignKeys(), foreignKey =>
            foreignKey.Properties.Select(x => x.Name).SequenceEqual([
                nameof(SupplierNegotiationDecision.BusinessUnitId),
                nameof(SupplierNegotiationDecision.SupplierQuoteId),
                nameof(SupplierNegotiationDecision.SupplierQuoteRevisionId)]));

        AssertPermission(nameof(SupplierQuoteInboxController.GetNegotiation),
            "Supplier History", PermissionAction.View);
        AssertPermission(nameof(SupplierQuoteInboxController.DecideNegotiation),
            "Supplier Negotiation", PermissionAction.Edit);
    }

    private static void AssertPermission(string method, string module, PermissionAction action)
    {
        var attribute = Assert.Single(typeof(SupplierQuoteInboxController).GetMethod(method)!
            .GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>());
        Assert.Equal(module, attribute.ModuleName);
        Assert.Equal(action, attribute.Action);
    }

    private static SupplierQuote Quote(int currentRevision, params SupplierQuoteRevision[] revisions)
    {
        var quote = new SupplierQuote
        {
            Id = 20, BusinessUnitId = 7, SupplierId = 10, SupplierSolicitationId = 11,
            SourcingCaseId = 12, RfqId = 13, NexoraSerial = "NXR-TEST",
            SupplierQuoteReference = "SQ-TEST", CurrentRevisionNumber = currentRevision,
            InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison, Version = 1,
            CreatedBy = "test", UpdatedBy = "test", CreatedOn = DateTime.UtcNow,
            UpdatedOn = DateTime.UtcNow
        };
        foreach (var revision in revisions)
        {
            revision.SupplierQuoteId = quote.Id;
            revision.SupplierQuote = quote;
            quote.Revisions.Add(revision);
        }
        return quote;
    }

    private static SupplierQuoteRevision Revision(int number, long id, long currencyId,
        DateTime? validUntil, string? incoterms, string? paymentTerms, decimal price,
        decimal? available, int? leadDays, bool isAlternate, string? partNumber)
    {
        var revision = new SupplierQuoteRevision
        {
            Id = id, BusinessUnitId = 7, RevisionNumber = number, CaptureChannel = "MANUAL",
            SourceIdentity = $"round-{number}", SourceSha256 = new string('A', 64),
            CurrencyId = currencyId, ValidUntil = validUntil, Incoterms = incoterms,
            PaymentTerms = paymentTerms, CapturedBy = "test", CapturedOn = DateTime.UtcNow,
            IdempotencyKey = $"round-{number}", RequestHash = new string('B', 64),
            CorrelationId = $"corr-{number}"
        };
        revision.Lines.Add(new SupplierQuoteLine
        {
            Id = 700, BusinessUnitId = 7, SupplierQuoteRevisionId = id, LineNumber = 1,
            RfqItemId = 700, CommercialDemandLineId = 701, PartNumber = partNumber,
            Description = "Component", Quantity = 10m, AvailableQuantity = available,
            UnitOfMeasure = "EA", UnitPrice = price, MinimumOrderQuantity = 2m,
            LeadTimeDays = leadDays, AvailabilityType = available.HasValue ? "PARTIAL" : null,
            IsAlternate = isAlternate
        });
        return revision;
    }

    private static NegotiationProjection Projection(long currencyId, decimal price, int leadDays,
        long supplierId) => new(supplierId * 10, supplierId, 700, currencyId, price, price,
        10m, 10m, leadDays, supplierId * 100, supplierId * 100 + 1,
        DateTime.UtcNow.AddMinutes(-supplierId), true);

    private static Supplier EligibleSupplier() => new()
    {
        IsActive = true,
        GovernanceStatus = SupplierGovernanceStatuses.Approved,
        VerificationStatus = SupplierVerificationStatuses.Verified,
        ReadinessStatus = SupplierReadinessStatuses.Ready,
        ComplianceStatus = SupplierComplianceStatuses.Cleared,
        RiskStatus = SupplierRiskStatuses.Low
    };

    private sealed class NegotiationFixture : IDisposable
    {
        private readonly TestDb _database = new();
        public long BusinessUnitId => ProcurementTestData.Tenant;
        public long OtherBusinessUnitId => ProcurementTestData.OtherTenant;
        public long SupplierQuoteId => 97_020;
        public long CommercialDemandLineId => 97_001;
        public long SourcingCaseId => 97_002;
        public long SupplierSolicitationId => 97_010;

        public NegotiationFixture()
        {
            using var seed = _database.ContextFor(null);
            seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
            ProcurementTestData.SeedGraph(seed, BusinessUnitId, 0);
            ProcurementTestData.SeedGraph(seed, OtherBusinessUnitId, 10_000);
            var demand = new CommercialDemandLine
            {
                Id = 97_001, BusinessUnitId = BusinessUnitId, RfqId = ProcurementTestData.Rfq,
                RfqItemId = ProcurementTestData.RfqItem, NexoraSerial = "NXR-NEGOTIATION",
                IdentityKey = "negotiation-line", CreatedBy = "test", CreatedOn = DateTime.UtcNow
            };
            var sourcingCase = new SourcingCase
            {
                Id = 97_002, BusinessUnitId = BusinessUnitId, CommercialDemandLineId = demand.Id,
                RfqId = demand.RfqId, RfqItemId = demand.RfqItemId, ProductId = ProcurementTestData.Product,
                NexoraSerial = demand.NexoraSerial, Description = "Component", RequestedQuantity = 10,
                StockQuantity = 0, UnfulfilledQuantity = 10, SearchLimit = 10,
                Status = SourcingCaseStatuses.ComparisonReady, NextAction = "Review offers",
                ShortageDecisionKey = "negotiation-shortage", IdempotencyKey = "negotiation-case",
                RequestHash = new string('C', 64), CreatedBy = "test", UpdatedBy = "test",
                CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
            };
            var solicitation = new SupplierSolicitation
            {
                Id = 97_010, BusinessUnitId = BusinessUnitId, RfqId = demand.RfqId,
                SupplierId = ProcurementTestData.Supplier, SourcingCaseId = sourcingCase.Id,
                CommercialDemandLineId = demand.Id, NexoraSerial = demand.NexoraSerial,
                SupplierRfqNumber = "SRFQ-NEGOTIATION", IdempotencyKey = "negotiation-solicitation",
                RequestHash = new string('D', 64), RequestedRfqItemIdsJson = $"[{demand.RfqItemId}]",
                Status = SolicitationStatus.Responded, SentOn = DateTime.UtcNow.AddDays(-1),
                RespondedOn = DateTime.UtcNow, CreatedOn = DateTime.UtcNow.AddDays(-1),
                UpdatedOn = DateTime.UtcNow
            };
            var quote = new SupplierQuote
            {
                Id = SupplierQuoteId, BusinessUnitId = BusinessUnitId,
                SupplierId = ProcurementTestData.Supplier, SupplierSolicitationId = solicitation.Id,
                SourcingCaseId = sourcingCase.Id, RfqId = demand.RfqId,
                NexoraSerial = demand.NexoraSerial, SupplierQuoteReference = "SQ-NEGOTIATION",
                CurrentRevisionNumber = 1, InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison,
                Version = 1, CreatedBy = "test", UpdatedBy = "test", CreatedOn = DateTime.UtcNow,
                UpdatedOn = DateTime.UtcNow
            };
            var revision = new SupplierQuoteRevision
            {
                Id = 97_030, BusinessUnitId = BusinessUnitId, SupplierQuoteId = quote.Id,
                RevisionNumber = 1, CaptureChannel = SupplierQuoteCaptureChannels.Manual,
                SourceIdentity = "manual-negotiation", SourceSha256 = new string('E', 64),
                CurrencyId = ProcurementTestData.Currency, ValidUntil = DateTime.UtcNow.AddDays(30),
                Incoterms = "FCA", FreightAmount = 10m, PaymentTerms = "NET 30",
                IdempotencyKey = "negotiation-revision", RequestHash = new string('F', 64),
                CapturedOn = DateTime.UtcNow, CapturedBy = "test", CorrelationId = "corr-revision",
                SupplierQuote = quote
            };
            revision.Lines.Add(new SupplierQuoteLine
            {
                Id = 97_040, BusinessUnitId = BusinessUnitId,
                SupplierQuoteRevisionId = revision.Id, LineNumber = 1,
                RfqItemId = demand.RfqItemId, CommercialDemandLineId = demand.Id,
                PartNumber = "QA-PART", Description = "Component", Quantity = 10m,
                AvailableQuantity = 4m, UnitOfMeasure = "EA", UnitPrice = 10m,
                MinimumOrderQuantity = 2m, LeadTimeDays = 5, AvailabilityType = "PARTIAL"
            });
            quote.Revisions.Add(revision);
            seed.CommercialDemandLines.Add(demand);
            seed.SourcingCases.Add(sourcingCase);
            seed.Set<SupplierSolicitation>().Add(solicitation);
            seed.SupplierQuotes.Add(quote);
            seed.SaveChanges();
        }

        public ErpRfqAutomationContext Context(long? businessUnitId = null) =>
            _database.ContextFor(businessUnitId ?? BusinessUnitId);
        public ErpRfqAutomationContext UnscopedContext() => _database.ContextFor(null);
        public void Dispose() => _database.Dispose();
    }
}
