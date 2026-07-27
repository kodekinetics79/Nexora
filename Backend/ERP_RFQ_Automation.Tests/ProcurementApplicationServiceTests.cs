using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class ProcurementApplicationServiceTests
{
    [Fact]
    public async Task Legacy_solicitation_endpoint_cannot_bypass_supplier_governance()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var supplier = await setup.Suppliers.SingleAsync(x => x.Id == ProcurementTestData.Supplier);
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Blocked;
            supplier.ReadinessStatus = SupplierReadinessStatuses.Blocked;
            await setup.SaveChangesAsync();
        }

        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() =>
            fixture.Execute(service => service.CreateSolicitationAsync(
                fixture.Solicitation("blocked-governance"))));

        Assert.Contains("not eligible", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var assertContext = fixture.Context();
        Assert.Empty(await assertContext.Set<SupplierSolicitation>().ToListAsync());
    }

    [Fact]
    public async Task Solicitation_replays_same_request_and_rejects_changed_hash()
    {
        using var fixture = new ProcurementScenario();
        var command = fixture.Solicitation("solicitation-replay");

        var first = await fixture.Execute(service => service.CreateSolicitationAsync(command));
        var replay = await fixture.Execute(service => service.CreateSolicitationAsync(command));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Id);

        var changed = command with { RfqItemIds = [fixture.RfqItemId, fixture.OtherRfqItemId] };
        await Assert.ThrowsAsync<ProcurementConflictException>(() =>
            fixture.Execute(service => service.CreateSolicitationAsync(changed)));
    }

    [Fact]
    public async Task Solicitation_and_quote_capture_reject_forged_relationships()
    {
        using var fixture = new ProcurementScenario();

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("forged-line") with
            {
                RfqItemIds = [fixture.OtherRfqItemId]
            })));

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("forged-supplier") with
            {
                SupplierId = fixture.OtherTenantSupplierId
            })));

        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("valid-solicitation")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.CaptureSupplierQuoteAsync(fixture.Quote(solicitation.Id, "forged-quote") with
            {
                Lines = [fixture.QuoteLine() with
                {
                    RfqItemId = fixture.OtherRfqItemId,
                    ProductId = fixture.OtherProductId
                }]
            })));
    }

    [Fact]
    public async Task Comparison_blocks_quotes_with_missing_commercial_evidence()
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("missing-data-solicitation")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "missing-data-response") with
            {
                Lines = [fixture.QuoteLine() with
                {
                    LeadTimeDays = null,
                    AvailableQuantity = null,
                    ReliabilitySnapshot = null
                }]
            }));

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        var line = Assert.Single(comparison.Lines);
        Assert.False(line.Eligible);
        Assert.Null(comparison.RecommendedSupplierQuotedItemId);
        Assert.Contains("lead time missing", line.Blockers);
        Assert.Contains("available quantity insufficient or unknown", line.Blockers);
        Assert.DoesNotContain("reliability evidence missing", line.Blockers);
    }

    [Fact]
    public async Task Award_rejects_stale_quote_version()
    {
        using var fixture = new ProcurementScenario();
        var quoteId = await fixture.CreateEligibleQuoteAsync("stale-award");

        var exception = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.ApproveAwardAsync(fixture.Award(quoteId, "stale-award") with
            {
                ExpectedQuoteVersion = 2
            })));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        await using var verify = fixture.Context();
        Assert.Empty(await verify.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().ToListAsync());
    }

    [Fact]
    public async Task Purchase_order_replays_same_command_and_rejects_duplicate_award_conversion()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("duplicate-po");
        var command = fixture.PurchaseOrder([award.Id], "po-replay");

        var first = await fixture.Execute(service => service.CreatePurchaseOrderAsync(command));
        var replay = await fixture.Execute(service => service.CreatePurchaseOrderAsync(command));

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal($"PO-{DateTime.UtcNow:yyyy}-{first.Id:D10}", first.Number);
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreatePurchaseOrderAsync(command with
            {
                IdempotencyKey = "po-duplicate-award"
            })));

        await using var verify = fixture.Context();
        Assert.Single(await verify.SupplierPurchaseOrders.ToListAsync());
        Assert.Single(await verify.SupplierPurchaseOrderLines.ToListAsync());
    }

    [Fact]
    public async Task Partial_over_and_final_receipts_reconcile_inventory_and_replay_idempotently()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("receipt-flow", quantity: 8m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);

        var partialCommand = fixture.Receipt(purchaseOrder.Id, lineId, 4m, 2, "receipt-partial", "GR-PARTIAL");
        var partial = await fixture.Execute(service => service.PostGoodsReceiptAsync(partialCommand));
        Assert.Equal(SupplierPurchaseOrderStatuses.PartiallyReceived, partial.PurchaseOrderStatus);

        var overCommand = fixture.Receipt(purchaseOrder.Id, lineId, 5m, 3, "receipt-over", "GR-OVER");
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(overCommand)));

        await fixture.AssertReceiptStateAsync(expectedReceipts: 1, expectedMovements: 1,
            expectedReceived: 4m, expectedOnHand: ProcurementTestData.InitialOnHand + 4m);

        var finalCommand = fixture.Receipt(purchaseOrder.Id, lineId, 4m, 3, "receipt-final", "GR-FINAL");
        var final = await fixture.Execute(service => service.PostGoodsReceiptAsync(finalCommand));
        var replay = await fixture.Execute(service => service.PostGoodsReceiptAsync(finalCommand));
        Assert.Equal(SupplierPurchaseOrderStatuses.Received, final.PurchaseOrderStatus);
        Assert.True(replay.Replayed);
        Assert.Equal(final.Id, replay.Id);

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(finalCommand with { ReceiptNumber = "GR-CHANGED" })));
        await fixture.AssertReceiptStateAsync(expectedReceipts: 2, expectedMovements: 2,
            expectedReceived: 8m, expectedOnHand: ProcurementTestData.InitialOnHand + 8m);
    }

    [Fact]
    public async Task Quote_capture_requires_delivery_and_new_revision_supersedes_prior_rows()
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("quote-revision-solicitation")));

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.CaptureSupplierQuoteAsync(fixture.Quote(solicitation.Id, "quote-before-send"))));

        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var first = await fixture.Execute(service =>
            service.CaptureSupplierQuoteAsync(fixture.Quote(solicitation.Id, "quote-revision-one")));
        var second = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "quote-revision-two") with
            {
                Revision = 2,
                Lines = [fixture.QuoteLine() with { UnitPrice = 11m }]
            }));

        await using var verify = fixture.Context();
        var rows = await verify.SupplierQuotedItems.OrderBy(x => x.QuoteRevision).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0].IsActive);
        Assert.True(rows[1].IsActive);
        Assert.Equal(2, rows[1].QuoteRevision);
        Assert.Equal(Assert.Single(second.LineIds), rows[1].Id);
        Assert.Equal(Assert.Single(first.LineIds), rows[0].Id);

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        Assert.Equal(rows[1].Id, Assert.Single(comparison.Lines).SupplierQuotedItemId);
    }

    [Fact]
    public async Task Award_enforces_moq_and_supports_bounded_split_sourcing()
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("split-award-solicitation")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var captured = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "split-award-quote") with
            {
                Lines = [fixture.QuoteLine() with { MinimumOrderQuantity = 4m }]
            }));
        var quoteId = Assert.Single(captured.LineIds);

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.ApproveAwardAsync(fixture.Award(quoteId, "below-moq", 3m))));
        var first = await fixture.Execute(service =>
            service.ApproveAwardAsync(fixture.Award(quoteId, "split-first", 4m)));
        var second = await fixture.Execute(service =>
            service.ApproveAwardAsync(fixture.Award(quoteId, "split-second", 4m)));
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.ApproveAwardAsync(fixture.Award(quoteId, "split-overbuy", 1m))));

        var po = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([first.Id, second.Id], "split-po")));
        await using var verify = fixture.Context();
        Assert.Equal(2, await verify.SupplierPurchaseOrderLines.CountAsync(x => x.SupplierPurchaseOrderId == po.Id));
        Assert.All(await verify.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().ToListAsync(),
            award => Assert.Equal("CONVERTED_TO_PO", award.Status));
    }

    [Fact]
    public async Task Comparison_blocks_quote_when_minimum_order_cannot_be_satisfied()
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("moq-comparison-solicitation")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "moq-comparison-quote") with
            {
                Lines = [fixture.QuoteLine() with { MinimumOrderQuantity = 20m }]
            }));

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        var line = Assert.Single(comparison.Lines);
        Assert.False(line.Eligible);
        Assert.Contains("minimum order quantity cannot be satisfied", line.Blockers);
    }

    [Fact]
    public async Task Comparison_rejects_moq_that_exceeds_requirement_remaining_after_award()
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("remaining-moq-solicitation")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var quote = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "remaining-moq-quote") with
            {
                Lines = [fixture.QuoteLine() with { MinimumOrderQuantity = 4m }]
            }));
        await fixture.Execute(service => service.ApproveAwardAsync(
            fixture.Award(Assert.Single(quote.LineIds), "remaining-moq-award", 6m)));

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));

        var line = Assert.Single(comparison.Lines);
        Assert.False(line.Eligible);
        Assert.Contains("minimum order quantity cannot be satisfied", line.Blockers);
    }

    [Fact]
    public async Task Comparison_and_award_block_an_unresolved_rfq_product()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var rfqItem = await setup.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            rfqItem.ProductId = null;
            await setup.SaveChangesAsync();
        }
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("unresolved-product-solicitation")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var quote = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "unresolved-product-quote") with
            {
                Lines = [fixture.QuoteLine() with { ProductId = null }]
            }));

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        var line = Assert.Single(comparison.Lines);
        Assert.False(line.Eligible);
        Assert.Contains("product unresolved", line.Blockers);
        Assert.Null(comparison.RecommendedSupplierQuotedItemId);

        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.ApproveAwardAsync(fixture.Award(Assert.Single(quote.LineIds), "unresolved-product-award"))));
        Assert.Contains("product unresolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quote_revision_accepts_an_explicit_partial_supplier_response()
    {
        using var fixture = new ProcurementScenario();
        var secondLineId = await fixture.AddRfqLineAsync(3);
        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(
            fixture.Solicitation("atomic-revision-solicitation") with
            {
                RfqItemIds = [fixture.RfqItemId, secondLineId]
            }));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var original = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            fixture.Quote(solicitation.Id, "atomic-revision-one") with
            {
                Lines = [fixture.QuoteLine(), fixture.QuoteLine(3m) with { RfqItemId = secondLineId }]
            }));

        var revision = await fixture.Execute(service =>
            service.CaptureSupplierQuoteAsync(fixture.Quote(solicitation.Id, "atomic-revision-two") with
            {
                Revision = 2,
                Lines = [fixture.QuoteLine()]
            }));

        await using var verify = fixture.Context();
        Assert.Single(revision.LineIds);
        Assert.Single(await verify.SupplierQuotedItems.Where(x => x.IsActive && x.QuoteRevision == 1
            && x.RfqItemId == secondLineId).ToListAsync());
        Assert.Single(await verify.SupplierQuotedItems.Where(x => !x.IsActive && x.QuoteRevision == 1
            && x.RfqItemId == fixture.RfqItemId).ToListAsync());
        Assert.Single(await verify.SupplierQuotedItems.Where(x => x.IsActive && x.QuoteRevision == 2).ToListAsync());

        var omittedLineComparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, secondLineId));
        Assert.Equal(original.LineIds.Last(), Assert.Single(omittedLineComparison.Lines).SupplierQuotedItemId);
    }

    [Fact]
    public async Task Fully_covered_requirement_cannot_be_solicited_or_awarded_again()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("covered-requirement", quantity: 8m);

        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        Assert.Equal(0m, Assert.Single(workbench.Lines).ShortfallQuantity);
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("covered-requirement-again"))));

        var poLine = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        Assert.True(poLine > 0);
    }

    [Fact]
    public async Task Purchase_order_number_is_server_generated_and_expected_date_must_be_after_utc_creation_date()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("server-number", 8m);
        var invalid = fixture.PurchaseOrder([award.Id], "invalid-expected-date") with
        {
            ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow)
        };
        var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.CreatePurchaseOrderAsync(invalid)));
        Assert.Contains("after", exception.Message, StringComparison.OrdinalIgnoreCase);

        var result = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "server-number-po")));
        Assert.Equal($"PO-{DateTime.UtcNow:yyyy}-{result.Id:D10}", result.Number);
        Assert.Equal(SupplierPurchaseOrderStatuses.Draft, result.Status);

        var lineId = await fixture.PurchaseOrderLineIdAsync(result.Id);
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(fixture.Receipt(result.Id, lineId, 1m, 1, "draft-receipt", "GR-DRAFT"))));

        var deliveredOn = DateTime.UtcNow;
        var issueCommand = fixture.Issue(result.Id, "issue-po", deliveredOn: deliveredOn);
        var issued = await fixture.Execute(service => service.IssuePurchaseOrderAsync(issueCommand));
        Assert.Equal(SupplierPurchaseOrderStatuses.Issued, issued.Status);
        Assert.True((await fixture.Execute(service => service.IssuePurchaseOrderAsync(issueCommand))).Replayed);
        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(issueCommand with
            {
                DeliveryEvidenceSha256 = new string('b', 64)
            })));
        await using var issuedContext = fixture.Context();
        Assert.Single(await issuedContext.IncomingInventory.Where(x => x.SourceType == "SupplierPurchaseOrderLine").ToListAsync());
        var issueEvent = await issuedContext.ProcurementEvents.SingleAsync(x => x.EventType == "SUPPLIER_PO_ISSUED");
        using var payload = JsonDocument.Parse(issueEvent.PayloadJson);
        Assert.Equal(issueCommand.DeliveryEvidenceReference,
            payload.RootElement.GetProperty("deliveryEvidenceReference").GetString());
        Assert.Equal(issueCommand.DeliveryEvidenceSha256,
            payload.RootElement.GetProperty("deliveryEvidenceSha256").GetString());
        Assert.Equal(deliveredOn, payload.RootElement.GetProperty("deliveredOn").GetDateTime());
    }

    [Fact]
    public async Task Purchase_order_issue_requires_controlled_complete_delivery_evidence()
    {
        using var fixture = new ProcurementScenario();
        var award = await fixture.CreateAwardAsync("evidence-contract", 8m);
        var draft = await fixture.Execute(service => service.CreatePurchaseOrderAsync(
            fixture.PurchaseOrder([award.Id], "evidence-contract-po")));

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(fixture.Issue(draft.Id, "bad-reference") with
            {
                DeliveryEvidenceReference = "free-form-reference"
            })));
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(fixture.Issue(draft.Id, "bad-hash") with
            {
                DeliveryEvidenceSha256 = "abc"
            })));
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(fixture.Issue(draft.Id, "bad-timestamp") with
            {
                DeliveredOn = DateTime.SpecifyKind(DateTime.Now, DateTimeKind.Local)
            })));
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.IssuePurchaseOrderAsync(fixture.Issue(draft.Id, "future-timestamp") with
            {
                DeliveredOn = DateTime.UtcNow.AddMinutes(10)
            })));
    }

    [Fact]
    public async Task Purchase_order_issue_revalidates_quote_and_expected_delivery()
    {
        using (var expiredQuoteFixture = new ProcurementScenario())
        {
            var award = await expiredQuoteFixture.CreateAwardAsync("expired-at-issue", 8m);
            var draft = await expiredQuoteFixture.Execute(service => service.CreatePurchaseOrderAsync(
                expiredQuoteFixture.PurchaseOrder([award.Id], "expired-at-issue-po")));
            await using (var setup = expiredQuoteFixture.Context())
            {
                var quote = await setup.SupplierQuotedItems.SingleAsync();
                quote.ValidUntil = DateTime.UtcNow.AddMinutes(-1);
                await setup.SaveChangesAsync();
            }

            var exception = await Assert.ThrowsAsync<ProcurementValidationException>(() => expiredQuoteFixture.Execute(service =>
                service.IssuePurchaseOrderAsync(expiredQuoteFixture.Issue(draft.Id, "expired-at-issue"))));
            Assert.Contains("unexpired", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        using var staleDeliveryFixture = new ProcurementScenario();
        var staleAward = await staleDeliveryFixture.CreateAwardAsync("stale-delivery", 8m);
        var staleDraft = await staleDeliveryFixture.Execute(service => service.CreatePurchaseOrderAsync(
            staleDeliveryFixture.PurchaseOrder([staleAward.Id], "stale-delivery-po")));
        await using (var setup = staleDeliveryFixture.Context())
        {
            var purchaseOrder = await setup.SupplierPurchaseOrders.SingleAsync(x => x.Id == staleDraft.Id);
            purchaseOrder.ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow);
            await setup.SaveChangesAsync();
        }

        var staleException = await Assert.ThrowsAsync<ProcurementValidationException>(() => staleDeliveryFixture.Execute(service =>
            service.IssuePurchaseOrderAsync(staleDeliveryFixture.Issue(staleDraft.Id, "stale-delivery"))));
        Assert.Contains("expected delivery", staleException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Purchase_order_register_searches_authoritative_commercial_identifiers()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("po-register", quantity: 8m);

        var byNumber = await fixture.Execute(service => service.SearchPurchaseOrdersAsync(
            fixture.BusinessUnitId, purchaseOrder.Number, 50));
        var bySupplier = await fixture.Execute(service => service.SearchPurchaseOrdersAsync(
            fixture.BusinessUnitId, "QA Supplier", 50));

        Assert.Equal(purchaseOrder.Id, Assert.Single(byNumber).Id);
        Assert.Equal(purchaseOrder.Id, Assert.Single(bySupplier).Id);
        Assert.Equal(SupplierPurchaseOrderStatuses.Issued, Assert.Single(byNumber).Status);
        Assert.Equal(8m, Assert.Single(byNumber).OpenQuantity);
    }

    [Fact]
    public async Task Receipt_number_replays_identical_business_content_and_rejects_changed_content()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("receipt-identity", quantity: 8m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        var command = fixture.Receipt(purchaseOrder.Id, lineId, 3m, 2, "receipt-identity-first", "GR-IDENTITY");
        var first = await fixture.Execute(service => service.PostGoodsReceiptAsync(command));

        var replay = await fixture.Execute(service => service.PostGoodsReceiptAsync(command with
        {
            IdempotencyKey = "receipt-identity-retry",
            ExpectedPurchaseOrderVersion = 3
        }));
        Assert.True(replay.Replayed);
        Assert.Equal(first.Id, replay.Id);

        await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(command with
            {
                IdempotencyKey = "receipt-identity-conflict",
                ExpectedPurchaseOrderVersion = 3,
                Lines = [new PostGoodsReceiptLine(lineId, 2m)]
            })));
        await fixture.AssertReceiptStateAsync(1, 1, 3m, ProcurementTestData.InitialOnHand + 3m);
    }

    [Fact]
    public async Task Receipt_number_is_a_tenant_wide_canonical_identity()
    {
        using var fixture = new ProcurementScenario();
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("tenant-receipt-identity", quantity: 4m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);
        var firstCommand = fixture.Receipt(purchaseOrder.Id, lineId, 2m, 2,
            "tenant-receipt-first", "GR-TENANT-CANONICAL");
        var first = await fixture.Execute(service => service.PostGoodsReceiptAsync(firstCommand));

        long otherPurchaseOrderId;
        await using (var setup = fixture.Context())
        {
            var other = new SupplierPurchaseOrder
            {
                BusinessUnitId = fixture.BusinessUnitId,
                RfqId = fixture.RfqId,
                SupplierId = ProcurementTestData.Supplier,
                CurrencyId = ProcurementTestData.Currency,
                PurchaseOrderNumber = "PO-QA-OTHER",
                Status = SupplierPurchaseOrderStatuses.Issued,
                TotalValue = 1m,
                ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
                IdempotencyKey = "tenant-receipt-other-po",
                RequestHash = new string('b', 64),
                CreatedOn = DateTime.UtcNow,
                CreatedBy = "qa"
            };
            setup.SupplierPurchaseOrders.Add(other);
            await setup.SaveChangesAsync();
            otherPurchaseOrderId = other.Id;
        }

        var conflict = await Assert.ThrowsAsync<ProcurementConflictException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(firstCommand with
            {
                PurchaseOrderId = otherPurchaseOrderId,
                IdempotencyKey = "tenant-receipt-other-attempt",
                ExpectedPurchaseOrderVersion = 1
            })));
        Assert.Contains("idempotency", conflict.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True((await fixture.Execute(service => service.PostGoodsReceiptAsync(firstCommand with
        {
            IdempotencyKey = "tenant-receipt-replay",
            ExpectedPurchaseOrderVersion = 2
        }))).Replayed);
        Assert.Equal(first.Id, (await fixture.Execute(service => service.PostGoodsReceiptAsync(firstCommand))).Id);
    }

    [Fact]
    public async Task Workbench_allocates_same_sku_inventory_once_in_rfq_line_order()
    {
        using var fixture = new ProcurementScenario();
        var secondLineId = await fixture.AddRfqLineAsync(3);

        var workbench = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        var lines = workbench.Lines.OrderBy(x => x.Id).ToArray();

        Assert.Equal(2, lines.Length);
        Assert.Equal(fixture.RfqItemId, lines[0].Id);
        Assert.Equal(2m, lines[0].AvailableQuantity);
        Assert.Equal(8m, lines[0].ShortfallQuantity);
        Assert.Equal(secondLineId, lines[1].Id);
        Assert.Equal(0m, lines[1].AvailableQuantity);
        Assert.Equal(3m, lines[1].ShortfallQuantity);
        Assert.Equal(2m, lines.Sum(x => x.AvailableQuantity));

        var solicitation = await fixture.Execute(service => service.CreateSolicitationAsync(
            fixture.Solicitation("second-same-sku-line") with { RfqItemIds = [secondLineId] }));
        Assert.True(solicitation.Id > 0);
    }

    [Fact]
    public async Task Purchase_order_creates_missing_inventory_bucket_and_receipt_date_uses_issuance_calendar_day()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            setup.Remove(await setup.Set<Models.Inventory>().SingleAsync());
            await setup.SaveChangesAsync();
        }
        var purchaseOrder = await fixture.CreatePurchaseOrderAsync("new-inventory", quantity: 10m);
        var lineId = await fixture.PurchaseOrderLineIdAsync(purchaseOrder.Id);

        await using (var verify = fixture.Context())
        {
            var line = await verify.SupplierPurchaseOrderLines.SingleAsync(x => x.Id == lineId);
            Assert.NotNull(line.InventoryId);
            Assert.Equal(line.InventoryId, await verify.Set<Models.Inventory>()
                .Where(x => x.ProductId == ProcurementTestData.Product && x.WarehouseId == ProcurementTestData.Warehouse)
                .Select(x => (long?)x.Id).SingleAsync());
        }

        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(fixture.Receipt(purchaseOrder.Id, lineId, 1m, 2,
                "receipt-before-po", "GR-BEFORE") with { ReceivedOn = DateTime.UtcNow.AddDays(-1) })));
        var sameDay = await fixture.Execute(service => service.PostGoodsReceiptAsync(
            fixture.Receipt(purchaseOrder.Id, lineId, 1m, 2, "receipt-same-day", "GR-SAME-DAY") with
            {
                ReceivedOn = DateTime.SpecifyKind(DateTime.UtcNow.Date, DateTimeKind.Utc)
            }));
        Assert.Equal(SupplierPurchaseOrderStatuses.PartiallyReceived, sameDay.PurchaseOrderStatus);
        await Assert.ThrowsAsync<ProcurementValidationException>(() => fixture.Execute(service =>
            service.PostGoodsReceiptAsync(fixture.Receipt(purchaseOrder.Id, lineId, 1m, 3,
                "receipt-future", "GR-FUTURE") with { ReceivedOn = DateTime.UtcNow.AddHours(1) })));
    }

    [Fact]
    public async Task Workbench_ignores_unrelated_incoming_supply_and_keeps_converted_award_lineage()
    {
        using var fixture = new ProcurementScenario();
        await using (var setup = fixture.Context())
        {
            var inventory = await setup.Set<Models.Inventory>().SingleAsync();
            inventory.QtyOnHand = 0;
            setup.IncomingInventory.Add(new IncomingInventory
            {
                BusinessUnitId = fixture.BusinessUnitId,
                ProductId = ProcurementTestData.Product,
                InventoryId = inventory.Id,
                WarehouseId = ProcurementTestData.Warehouse,
                OrderedQuantity = 50m,
                ReceivedQuantity = 0,
                AllocatedQuantity = 0,
                ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(2)),
                Status = IncomingInventoryStatus.Ordered,
                SourceType = "UnrelatedOrder",
                SourceId = "not-this-rfq"
            });
            await setup.SaveChangesAsync();
        }

        var beforePo = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        Assert.Equal("SHORTAGE", Assert.Single(beforePo.Lines).Resolution);

        var po = await fixture.CreatePurchaseOrderAsync("workbench-lineage", quantity: 10m);
        var afterPo = await fixture.Execute(service =>
            service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        var award = Assert.Single(afterPo.Awards);
        Assert.Equal("CONVERTED_TO_PO", award.Status);
        Assert.Equal(po.Id, award.PurchaseOrderId);
        Assert.Equal([fixture.RfqItemId], Assert.Single(afterPo.Solicitations).RfqItemIds);
    }

    [Fact]
    public async Task Tenant_context_cannot_operate_another_tenants_procurement_graph()
    {
        using var fixture = new ProcurementScenario();
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation("tenant-isolation")));

        await using (var otherTenant = fixture.Context(fixture.OtherBusinessUnitId))
        {
            var service = new ProcurementApplicationService(otherTenant);
            await Assert.ThrowsAsync<ProcurementValidationException>(() =>
                service.GetWorkbenchAsync(fixture.BusinessUnitId, fixture.RfqId));
        }

        await using var verify = fixture.Context(fixture.OtherBusinessUnitId);
        Assert.Null(await verify.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>()
            .SingleOrDefaultAsync(x => x.Id == solicitation.Id));
    }
}

internal sealed class ProcurementScenario : IDisposable
{
    private readonly TestDb _database = new();

    public ProcurementScenario()
    {
        using var seed = _database.ContextFor(null);
        // SQLite stores decimal values as TEXT, which makes its numeric CHECK
        // comparisons diverge from PostgreSQL. The PostgreSQL lane below certifies
        // the real constraints; portable tests exercise application validation.
        seed.Database.ExecuteSqlRaw("PRAGMA ignore_check_constraints = ON");
        ProcurementTestData.SeedGraph(seed, BusinessUnitId, 0);
        ProcurementTestData.SeedGraph(seed, OtherBusinessUnitId, 10_000);
        seed.SaveChanges();
    }

    public long BusinessUnitId => ProcurementTestData.Tenant;
    public long OtherBusinessUnitId => ProcurementTestData.OtherTenant;
    public long RfqId => ProcurementTestData.Rfq;
    public long RfqItemId => ProcurementTestData.RfqItem;
    public long OtherRfqItemId => ProcurementTestData.RfqItem + 10_000;
    public long OtherProductId => ProcurementTestData.Product + 10_000;
    public long OtherTenantSupplierId => ProcurementTestData.Supplier + 10_000;

    public ErpRfqAutomationContext Context(long? tenant = null) =>
        _database.ContextFor(tenant ?? BusinessUnitId);

    public async Task<T> Execute<T>(Func<ProcurementApplicationService, Task<T>> operation)
    {
        await using var context = Context();
        return await operation(new ProcurementApplicationService(context));
    }

    public CreateSolicitationCommand Solicitation(string key) => new(
        BusinessUnitId, RfqId, ProcurementTestData.Supplier, [RfqItemId], DateTime.UtcNow.AddDays(2),
        key, "qa", $"corr-{key}");

    public CaptureSupplierQuoteCommand Quote(long solicitationId, string key) => new(
        BusinessUnitId, solicitationId, $"SQ-{key}", 1, DateTime.UtcNow.AddDays(30), key,
        "qa", $"corr-{key}", [QuoteLine()]);

    public CaptureSupplierQuoteLine QuoteLine(decimal quantity = 10m) => new(
        RfqItemId, ProcurementTestData.Product, quantity, 12m, ProcurementTestData.Currency,
        5, quantity, 10m, 2m, 1m, 0m, 0m, 1m, 95m);

    public ApproveAwardCommand Award(long quoteId, string key, decimal quantity = 8m) => new(
        BusinessUnitId, quoteId, quantity, 1, key, "qa", $"corr-{key}", 42, "Best eligible landed cost");

    public CreatePurchaseOrderCommand PurchaseOrder(IReadOnlyCollection<long> awardIds, string key) => new(
        BusinessUnitId, RfqId, ProcurementTestData.Supplier, ProcurementTestData.Currency,
        ProcurementTestData.Warehouse, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
        awardIds, key, "qa", $"corr-{key}");

    public PostGoodsReceiptCommand Receipt(long poId, long lineId, decimal quantity, long version, string key, string number) => new(
        BusinessUnitId, poId, ProcurementTestData.Warehouse, number, DateTime.UtcNow, version,
        [new PostGoodsReceiptLine(lineId, quantity)], key, "qa", $"corr-{key}");

    public IssuePurchaseOrderCommand Issue(long poId, string key, long version = 1, DateTime? deliveredOn = null) => new(
        BusinessUnitId, poId, version, $"provider-receipt:{key}", key, "qa", $"corr-{key}",
        new string('a', 64), deliveredOn ?? DateTime.UtcNow);

    public async Task<long> CreateEligibleQuoteAsync(string key)
    {
        var solicitation = await Execute(service => service.CreateSolicitationAsync(Solicitation($"{key}-sol")));
        await MarkSolicitationSentAsync(solicitation.Id);
        var quote = await Execute(service => service.CaptureSupplierQuoteAsync(Quote(solicitation.Id, $"{key}-quote")));
        return Assert.Single(quote.LineIds);
    }

    public async Task<AwardResult> CreateAwardAsync(string key, decimal quantity = 8m)
    {
        var quoteId = await CreateEligibleQuoteAsync(key);
        return await Execute(service => service.ApproveAwardAsync(Award(quoteId, $"{key}-award", quantity)));
    }

    public async Task<PurchaseOrderResult> CreatePurchaseOrderAsync(string key, decimal quantity)
    {
        var award = await CreateAwardAsync(key, quantity);
        var draft = await Execute(service => service.CreatePurchaseOrderAsync(
            PurchaseOrder([award.Id], $"{key}-po")));
        return await Execute(service => service.IssuePurchaseOrderAsync(
            Issue(draft.Id, $"{key}-issue")));
    }

    public async Task<long> PurchaseOrderLineIdAsync(long purchaseOrderId)
    {
        await using var context = Context();
        return await context.SupplierPurchaseOrderLines.Where(x => x.SupplierPurchaseOrderId == purchaseOrderId)
            .Select(x => x.Id).SingleAsync();
    }

    public async Task<long> AddRfqLineAsync(int quantity)
    {
        await using var context = Context();
        var id = ProcurementTestData.RfqItem + 1;
        var line = AgentSeed.RfqItem(context, id, RfqId, "QA Product additional", quantity);
        line.ProductId = ProcurementTestData.Product;
        line.CurrencyId = ProcurementTestData.Currency;
        line.WarehouseId = ProcurementTestData.Warehouse;
        line.UnitOfMeasure = "EA";
        await context.SaveChangesAsync();
        return id;
    }

    public async Task MarkSolicitationSentAsync(long solicitationId)
    {
        await using var context = Context();
        var solicitation = await context.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>()
            .SingleAsync(x => x.Id == solicitationId);
        solicitation.Status = ERP_RFQ_Automation.Agent.Models.SolicitationStatus.Sent;
        solicitation.SentOn = DateTime.UtcNow;
        solicitation.UpdatedOn = DateTime.UtcNow;
        solicitation.Version++;
        await context.SaveChangesAsync();
    }

    public async Task AssertReceiptStateAsync(int expectedReceipts, int expectedMovements,
        decimal expectedReceived, decimal expectedOnHand)
    {
        await using var context = Context();
        Assert.Equal(expectedReceipts, await context.GoodsReceipts.CountAsync());
        var movements = await context.InventoryMovements.OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(expectedMovements, movements.Count);
        Assert.Equal(expectedReceived, movements.Sum(x => x.Quantity));
        Assert.Equal(expectedOnHand, await context.Set<Models.Inventory>()
            .Where(x => x.Id == ProcurementTestData.Inventory).Select(x => x.QtyOnHand).SingleAsync());
        Assert.Equal(expectedReceived, await context.SupplierPurchaseOrderLines
            .Select(x => x.ReceivedQuantity).SingleAsync());
        Assert.Equal(expectedReceived, await context.IncomingInventory
            .Select(x => x.ReceivedQuantity).SingleAsync());
    }

    public void Dispose() => _database.Dispose();
}

internal static class ProcurementTestData
{
    public const long Tenant = 96_001;
    public const long OtherTenant = 96_002;
    public const long Currency = 96_010;
    public const long Warehouse = 96_020;
    public const long Product = 96_030;
    public const long Inventory = 96_040;
    public const long Supplier = 96_050;
    public const long Rfq = 96_060;
    public const long RfqItem = 96_070;
    public const decimal InitialOnHand = 2m;
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    public static void SeedGraph(ErpRfqAutomationContext context, long tenant, long offset)
    {
        Seed.EnsureBusinessUnit(context, tenant);
        context.Currencies.Add(new Currency
        {
            Id = Currency + offset, BusinessUnitId = tenant, Code = $"Q{offset}", CurrencyName = "QA Currency",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.Warehouses.Add(new Warehouse
        {
            Id = Warehouse + offset, BusinessUnitId = tenant, WarehouseCode = $"QA-{offset}",
            WarehouseName = "QA Warehouse", IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.Products.Add(new Product
        {
            Id = Product + offset, Buid = tenant, PartNo = $"QA-PART-{offset}", ProductName = "QA Product",
            WarehouseId = Warehouse + offset, QtyOnHand = InitialOnHand, ReorderPoint = 0,
            IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        context.Set<Models.Inventory>().Add(new Models.Inventory
        {
            Id = Inventory + offset, Buid = tenant, ProductId = Product + offset, WarehouseId = Warehouse + offset,
            PartNo = $"QA-PART-{offset}", ProductName = "QA Product", QtyOnHand = InitialOnHand,
            ReorderPoint = 0, CreatedBy = "qa", CreatedOn = Now
        });
        var supplier = AgentSeed.Supplier(context, Supplier + offset, tenant, "QA Supplier", $"supplier-{offset}@example.test");
        supplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
        supplier.VerificationStatus = SupplierVerificationStatuses.Verified;
        supplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
        supplier.RiskStatus = SupplierRiskStatuses.Low;
        supplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
        supplier.ConcurrencyToken = Guid.NewGuid();
        var rfq = AgentSeed.Rfq(context, Rfq + offset, tenant, $"RFQ-QA-{offset}");
        context.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue =
            $"NXR-QA-{tenant}-{Rfq + offset}";
        var line = AgentSeed.RfqItem(context, RfqItem + offset, Rfq + offset, "QA Product", 10);
        line.ProductId = Product + offset;
        line.CurrencyId = Currency + offset;
        line.WarehouseId = Warehouse + offset;
        line.UnitOfMeasure = "EA";
    }
}
