using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class V1Gate02CommercialAuthorityTests
{
    [Fact]
    public async Task Latest_review_decision_controls_readiness_and_inbox_count()
    {
        using var fixture = new ProcurementScenario();
        await fixture.CreateEligibleQuoteAsync("latest-review-authority");

        long quoteId;
        long revisionId;
        long evidenceId;
        await using (var setup = fixture.Context())
        {
            var quote = await setup.SupplierQuotes.SingleAsync();
            var revision = await setup.SupplierQuoteRevisions.SingleAsync();
            var evidence = await setup.SupplierQuoteFieldEvidence.OrderBy(x => x.Id).FirstAsync();
            evidence.ReviewRequired = true;
            revision.RequiresReview = true;
            quote.InboxStatus = SupplierQuoteInboxStatuses.ReviewRequired;
            await setup.SaveChangesAsync();
            quoteId = quote.Id;
            revisionId = revision.Id;
            evidenceId = evidence.Id;
        }

        await using (var review = fixture.Context())
        {
            var store = new EfSupplierQuoteStore(review);
            await store.ReviewAsync(new ReviewSupplierQuoteFieldCommand(fixture.BusinessUnitId,
                quoteId, revisionId, evidenceId, SupplierQuoteReviewStatuses.Accepted, null,
                "Verified against the source", "qa", "corr-review-accepted"), default);
            Assert.Equal(SupplierQuoteInboxStatuses.ReadyForComparison,
                (await review.SupplierQuotes.SingleAsync()).InboxStatus);

            await store.ReviewAsync(new ReviewSupplierQuoteFieldCommand(fixture.BusinessUnitId,
                quoteId, revisionId, evidenceId, SupplierQuoteReviewStatuses.Rejected, null,
                "Newer evidence invalidates the prior decision", "qa", "corr-review-rejected"), default);
            Assert.Equal(SupplierQuoteInboxStatuses.ReviewRequired,
                (await review.SupplierQuotes.SingleAsync()).InboxStatus);

            var inbox = Assert.Single(await store.SearchInboxAsync(fixture.BusinessUnitId, null, 20, default));
            Assert.Equal(1, inbox.ReviewRequiredCount);
            var detail = await store.GetDetailAsync(fixture.BusinessUnitId, quoteId, default);
            Assert.Equal(SupplierQuoteReviewStatuses.Rejected,
                Assert.Single(detail!.Revisions.Single().Evidence, x => x.Id == evidenceId).LatestReviewStatus);
        }
    }

    [Fact]
    public async Task Split_stock_and_supplier_award_cannot_price_the_entire_customer_line()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("split-pricing-block", 8m);
        var quoteItemId = await AddDraftCustomerQuoteAsync(fixture, 10m);

        await using var context = fixture.Context();
        var exception = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            new SupplierQuoteCommercialService(context).ApplyPricingAsync(new ApplyCustomerQuotePricingCommand(
                fixture.BusinessUnitId, quoteItemId, award.Id, 20m, "Evidence-backed target margin",
                "split-pricing-block", "qa", "corr-split-pricing")));

        Assert.Contains("blended-cost", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.CustomerQuoteSourcingDecisions.ToListAsync());
        Assert.Equal(0m, (await context.QuoteItems.SingleAsync(x => x.Id == quoteItemId)).UnitPrice);
    }

    [Fact]
    public async Task Customer_pricing_revalidates_latest_supplier_quote_status()
    {
        using var fixture = new ProcurementScenario();
        await SetInventoryOnHandAsync(fixture, 0m);
        var award = await fixture.CreateAwardAsync("pricing-status-revalidation", 10m);
        var quoteItemId = await AddDraftCustomerQuoteAsync(fixture, 10m);
        await using (var setup = fixture.Context())
        {
            var supplierQuote = await setup.SupplierQuotes.SingleAsync();
            supplierQuote.InboxStatus = SupplierQuoteInboxStatuses.ReviewRequired;
            await setup.SaveChangesAsync();
        }

        await using var context = fixture.Context();
        var exception = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            new SupplierQuoteCommercialService(context).ApplyPricingAsync(new ApplyCustomerQuotePricingCommand(
                fixture.BusinessUnitId, quoteItemId, award.Id, 20m, "Evidence-backed target margin",
                "pricing-status-revalidation", "qa", "corr-pricing-status")));
        Assert.Contains("evidence review", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Purchase_order_revalidates_latest_supplier_quote_status()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("po-status-revalidation", 8m);
        await using (var setup = fixture.Context())
        {
            var supplierQuote = await setup.SupplierQuotes.SingleAsync();
            supplierQuote.InboxStatus = SupplierQuoteInboxStatuses.ReviewRequired;
            await setup.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<Procurement.ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreatePurchaseOrderAsync(fixture.PurchaseOrder([award.Id], "po-status-revalidation"))));
        Assert.Contains("authoritative supplier quote", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Purchase_order_revalidates_supplier_governance_after_award()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("po-governance-revalidation", 8m);
        await using (var setup = fixture.Context())
        {
            var supplier = await setup.Suppliers.SingleAsync(x => x.Id == ProcurementTestData.Supplier);
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Blocked;
            supplier.ReadinessStatus = SupplierReadinessStatuses.Blocked;
            await setup.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<Procurement.ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreatePurchaseOrderAsync(fixture.PurchaseOrder([award.Id], "po-governance-revalidation"))));
        Assert.Contains("not eligible", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Correction_after_award_blocks_readiness_pricing_and_procurement_until_new_revision()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("post-award-correction", 8m);
        long supplierQuoteId;
        long revisionId;
        long evidenceId;
        await using (var setup = fixture.Context())
        {
            var quote = await setup.SupplierQuotes.SingleAsync();
            var revision = await setup.SupplierQuoteRevisions.SingleAsync();
            var evidence = await setup.SupplierQuoteFieldEvidence.SingleAsync(x => x.FieldName == "UnitPrice");
            evidence.ReviewRequired = true;
            revision.RequiresReview = true;
            quote.InboxStatus = SupplierQuoteInboxStatuses.ReviewRequired;
            await setup.SaveChangesAsync();
            supplierQuoteId = quote.Id;
            revisionId = revision.Id;
            evidenceId = evidence.Id;
        }

        await using (var review = fixture.Context())
        {
            await new EfSupplierQuoteStore(review).ReviewAsync(new ReviewSupplierQuoteFieldCommand(
                fixture.BusinessUnitId, supplierQuoteId, revisionId, evidenceId,
                SupplierQuoteReviewStatuses.Corrected, "13.50", "Corrected against source",
                "qa", "corr-post-award-correction"), default);
            Assert.Equal(SupplierQuoteInboxStatuses.ReviewRequired,
                (await review.SupplierQuotes.SingleAsync()).InboxStatus);
        }

        var quoteItemId = await AddDraftCustomerQuoteAsync(fixture, 8m);
        await using (var forceReady = fixture.Context())
        {
            (await forceReady.SupplierQuotes.SingleAsync()).InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison;
            await forceReady.SaveChangesAsync();
        }

        await using (var pricing = fixture.Context())
        {
            var pricingException = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
                new SupplierQuoteCommercialService(pricing).ApplyPricingAsync(new ApplyCustomerQuotePricingCommand(
                    fixture.BusinessUnitId, quoteItemId, award.Id, 20m, "Evidence-backed target margin",
                    "post-award-correction-pricing", "qa", "corr-post-award-pricing")));
            Assert.Contains("newer correction", pricingException.Message, StringComparison.OrdinalIgnoreCase);

            var intelligence = await new ERP_RFQ_Automation.CommercialLearning.CommercialLearningService(pricing)
                .GetRfqIntelligenceAsync(fixture.BusinessUnitId, fixture.RfqId);
            Assert.Equal(8m, Assert.Single(intelligence.Lines).UnfulfilledQuantity);
        }

        var poException = await Assert.ThrowsAsync<Procurement.ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreatePurchaseOrderAsync(fixture.PurchaseOrder([award.Id], "post-award-correction-po"))));
        Assert.Contains("authoritative supplier quote", poException.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<long> AddDraftCustomerQuoteAsync(ProcurementScenario fixture, decimal quantity)
    {
        await using var context = fixture.Context();
        const long statusId = 96_099;
        context.SetupMasters.Add(new SetupMaster
        {
            SetupId = statusId,
            SetupType = "QuoteStatus",
            SetupCode = "DRAFT",
            SetupValue = "Draft",
            BusinessUnitId = fixture.BusinessUnitId,
            IsActive = true,
            CreatedBy = "qa",
            CreatedOn = DateTime.UtcNow
        });
        var quote = new Quote
        {
            QuoteNo = $"QT-GATE2-{Guid.NewGuid():N}",
            Rfqid = fixture.RfqId,
            BusinessUnitId = fixture.BusinessUnitId,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(14),
            StatusId = statusId,
            CurrencyId = ProcurementTestData.Currency,
            TotalAmount = 0m,
            CreatedBy = "qa",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            RfqitemId = fixture.RfqItemId,
            ProductId = ProcurementTestData.Product,
            ItemDescription = "QA Product",
            Quantity = quantity,
            UnitPrice = 0m,
            TotalAmount = 0m,
            CreatedBy = "qa",
            CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();
        return quote.QuoteItems.Single().Id;
    }

    private static async Task SetInventoryOnHandAsync(ProcurementScenario fixture, decimal quantity)
    {
        await using var context = fixture.Context();
        var inventory = await context.Set<ERP_RFQ_Automation.Models.Inventory>()
            .SingleAsync(x => x.Id == ProcurementTestData.Inventory);
        inventory.QtyOnHand = quantity;
        await context.SaveChangesAsync();
    }
}
