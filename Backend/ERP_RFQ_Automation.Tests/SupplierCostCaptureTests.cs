using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Three cost-capture defects a finance panel found independently, and the arithmetic that proves
/// each one. They share a mechanism: the customer price is derived as <c>landed / (1 - margin)</c>,
/// so anything missing from landed cost underprices by <c>omission / (1 - margin)</c> — a 5%
/// omission at a 20% target margin is a 6.25% underprice, on every unit, silently.
///
/// Defect 1 — a partial award loaded the WHOLE line's freight onto the awarded quantity
/// -------------------------------------------------------------------------------------
/// <c>SupplierQuotedItem.FreightCost</c> is the line's share of the round's freight, computed for
/// the FULL quoted quantity. <c>CalculateAwardLandedUnitCost</c> handed all of it to the first
/// award of a line (<c>includeFixedCharges: awardedFromQuote == 0</c>) and none of it to any later
/// award, then divided by the awarded quantity alone.
///
///   quote 10 units at 100, freight 100  →  true landed cost 110.0000
///
///                        before      after
///     award of 2         150.0000    110.0000     36% overstated
///     award of 8         100.0000    110.0000      9% understated
///
/// Which award was entered first decided what each one cost. Both figures reach
/// <c>SourcingAward.LandedUnitCost</c> and <c>TotalValue</c>, then
/// <c>SupplierPurchaseOrderLine.LandedUnitCost</c> and the purchase order's committed-spend total.
///
/// Defect 2 — a reviewer's freight correction was accepted and discarded
/// ---------------------------------------------------------------------
/// <c>ValidateCorrection</c> routed FreightAmount to a catch-all that accepted any string up to
/// 4,000 characters, and <c>ProjectAsync</c> honoured header corrections for ValidUntil and
/// CurrencyId only — it read <c>revision.FreightAmount</c> raw. The reviewer who spotted missing
/// freight, corrected it, and was told the correction was accepted had changed nothing.
///
/// Defect 3 — duty was unrecordable on the canonical path
/// -------------------------------------------------------
/// The projection hardcoded <c>DutyCost = 0, OtherCost = 0, DiscountAmount = 0</c>, the capture
/// contract carried only freight and tax, and the workbench collapsed freight+duty+other into the
/// revision's single FreightAmount while dropping the discount entirely — so a re-projection
/// produced a different landed cost from the one the award was built on.
///
/// Decision R8 is not violated by capturing duty. R8 says the ENTERED price is authoritative
/// because duty is already inside it: true of a customer price a rep types, false of a supplier
/// quoting FOB or EXW, who by definition has not paid KSA duty. R8 forbids DERIVING duty from an
/// HS code. Nothing here derives anything.
/// </summary>
public sealed class SupplierCostCaptureTests
{
    // ───────────────────────────────────────── defect 1: the formula

    [Fact]
    public void A_partial_award_bears_only_the_share_of_a_charge_its_quantity_earns()
    {
        // The panel's worked example, at the formula: 1,000 of freight on a 100-unit line.
        Assert.Equal(200.0000m, LandedCostFormula.ProRateToAward(1_000m, 20m, 100m));
        Assert.Equal(800.0000m, LandedCostFormula.ProRateToAward(1_000m, 80m, 100m));
        Assert.Equal(1_000.0000m, LandedCostFormula.ProRateToAward(1_000m, 100m, 100m));

        // The two parts of a split reconcile to the whole.
        Assert.Equal(LandedCostFormula.ProRateToAward(1_000m, 100m, 100m),
            LandedCostFormula.ProRateToAward(1_000m, 20m, 100m) +
            LandedCostFormula.ProRateToAward(1_000m, 80m, 100m));

        // Clamped at 1 and floored at 0: an award cannot invent charges, and a quote that lost its
        // quantity cannot divide by zero mid-transaction.
        Assert.Equal(1m, LandedCostFormula.AwardShare(150m, 100m));
        Assert.Equal(0m, LandedCostFormula.AwardShare(20m, 0m));
        Assert.Equal(0m, LandedCostFormula.ProRateToAward(1_000m, 20m, 0m));
    }

    [Fact]
    public void Duty_other_and_discount_are_in_the_landed_cost_definition()
    {
        // 10 at 100, freight 100, duty 50, other 10, discount 20, no tax.
        // (1,000 + 100 + 50 + 10 - 20) / 10.
        Assert.Equal(114.0000m, LandedCostFormula.UnitCost(100m, 10m, 100m, 0m,
            supplierInputTaxRecoverablePercent: 100m, allocatedDuty: 50m, allocatedOther: 10m,
            allocatedDiscount: 20m));

        // Duty omitted is the 6.25% underprice: 110.0000 landed instead of 115.0000 on a 5% duty,
        // which at a 20% target margin quotes 137.50 against a correct 143.75.
        var withoutDuty = LandedCostFormula.UnitCost(100m, 10m, 100m, 0m, 100m);
        var withDuty = LandedCostFormula.UnitCost(100m, 10m, 100m, 0m, 100m, allocatedDuty: 50m);
        Assert.Equal(110.0000m, withoutDuty);
        Assert.Equal(115.0000m, withDuty);
        Assert.Equal(137.500000m, decimal.Round(withoutDuty / 0.8m, 6, MidpointRounding.AwayFromZero));
        Assert.Equal(143.750000m, decimal.Round(withDuty / 0.8m, 6, MidpointRounding.AwayFromZero));
    }

    // ───────────────────────────────────────── defect 1: the production chain

    [Fact]
    public async Task Splitting_an_award_across_two_parts_costs_what_the_whole_line_costs()
    {
        using var fixture = new ProcurementScenario();
        await SetInventoryOnHandAsync(fixture, 0m);
        // 10 units at 100 with 100 of freight. The whole line lands at (1,000 + 100) / 10 = 110.
        var quotedItemId = await CaptureAsync(fixture, "split", freight: 100m);

        var first = await fixture.Execute(service => service.ApproveAwardAsync(new ApproveAwardCommand(
            fixture.BusinessUnitId, quotedItemId, 2m, 1, "split-award-1", "qa", "corr-split-1", 42,
            "Partial award of the immediately available stock")));
        var second = await fixture.Execute(service => service.ApproveAwardAsync(new ApproveAwardCommand(
            fixture.BusinessUnitId, quotedItemId, 8m, 1, "split-award-2", "qa", "corr-split-2", 42,
            "Residual award of the same Supplier line")));

        // Each part costs what its own units cost. Before the fix the first award carried the
        // entire 100 of freight over 2 units and landed at 150.0000 — 36% overstated — and the
        // second carried none at all and landed at 100.0000, 9% understated.
        Assert.Equal(110.0000m, first.LandedUnitCost);
        Assert.Equal(110.0000m, second.LandedUnitCost);
        Assert.NotEqual(150.0000m, first.LandedUnitCost);
        Assert.NotEqual(100.0000m, second.LandedUnitCost);

        await using var context = fixture.Context();
        var awards = await context.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>()
            .OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(2, awards.Count);
        Assert.Equal(220m, awards[0].TotalValue);
        Assert.Equal(880m, awards[1].TotalValue);

        // The invariant, stated rather than asserted as a literal: two awards that between them
        // take the whole quoted quantity commit exactly what the whole quoted line costs. The old
        // arithmetic committed 300 + 800 = 1,100 as well, but split as 150/unit and 100/unit — so
        // the total was right by luck while every per-unit cost, and every customer price derived
        // from one, was wrong.
        var quotedLine = await context.SupplierQuotedItems.SingleAsync(x => x.Id == quotedItemId);
        var wholeLineCost = quotedLine.UnitPrice!.Value * quotedLine.Quantity + quotedLine.FreightCost;
        Assert.Equal(wholeLineCost, awards.Sum(x => x.TotalValue));
        Assert.Equal(quotedLine.LandedUnitCost, first.LandedUnitCost);
        Assert.Equal(quotedLine.LandedUnitCost, second.LandedUnitCost);
    }

    [Fact]
    public async Task An_award_for_the_whole_line_is_unchanged_by_pro_rating()
    {
        // Guard against over-correcting: the full-quantity award is the common case and its number
        // must not have moved at all.
        using var fixture = new ProcurementScenario();
        await SetInventoryOnHandAsync(fixture, 0m);
        var quotedItemId = await CaptureAsync(fixture, "whole", freight: 100m);

        var award = await fixture.Execute(service => service.ApproveAwardAsync(new ApproveAwardCommand(
            fixture.BusinessUnitId, quotedItemId, 10m, 1, "whole-award", "qa", "corr-whole", 42,
            "Best eligible landed cost")));

        Assert.Equal(110.0000m, award.LandedUnitCost);
    }

    // ───────────────────────────────────────── defect 2: the correction

    [Fact]
    public async Task A_freight_correction_must_be_an_amount_and_not_a_sentence()
    {
        using var fixture = new ProcurementScenario();
        var quotedItemId = await CaptureAsync(fixture, "validate", freight: 100m);
        var (quoteId, revisionId, evidenceId) = await FreightEvidenceAsync(fixture, quotedItemId);

        await using var context = fixture.Context();
        var service = new SupplierQuoteInboxService(context);

        // "approx 1,500 SAR" used to be accepted: FreightAmount fell through to the catch-all,
        // which allowed any 4,000-character string with no control characters.
        var prose = await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            service.ReviewAsync(Correction(fixture, quoteId, revisionId, evidenceId, "approx 1,500 SAR")));
        Assert.Contains("FreightAmount", prose.Message);

        // A negative charge would reduce landed cost and therefore the customer price.
        await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            service.ReviewAsync(Correction(fixture, quoteId, revisionId, evidenceId, "-250")));

        // A real amount is accepted, and zero is a legitimate correction of a freight that was
        // captured in error.
        await service.ReviewAsync(Correction(fixture, quoteId, revisionId, evidenceId, "250"));
        await service.ReviewAsync(Correction(fixture, quoteId, revisionId, evidenceId, "0"));
    }

    [Fact]
    public async Task An_accepted_freight_correction_reaches_the_landed_cost()
    {
        using var fixture = new ProcurementScenario();
        // 10 at 100, freight 100, duty 50, other 10, discount 20 → 114.0000 as captured.
        var quotedItemId = await CaptureAsync(fixture, "correct", freight: 100m, duty: 50m,
            other: 10m, discount: 20m);
        await using (var seed = fixture.Context())
            Assert.Equal(114.0000m, await seed.SupplierQuotedItems.Where(x => x.Id == quotedItemId)
                .Select(x => x.LandedUnitCost).SingleAsync());

        var (quoteId, revisionId, evidenceId) = await FreightEvidenceAsync(fixture, quotedItemId);
        await using var context = fixture.Context();
        await new SupplierQuoteInboxService(context).ReviewAsync(
            Correction(fixture, quoteId, revisionId, evidenceId, "300"));

        var version = await context.SupplierQuotes.Where(x => x.Id == quoteId)
            .Select(x => x.Version).SingleAsync();
        await new SupplierQuoteCommercialService(context).ProjectAsync(new ProjectSupplierQuoteCommand(
            fixture.BusinessUnitId, quoteId, version, "correct-projection", "qa", "corr-correct-projection"));

        // (1,000 + 300 corrected freight + 50 duty + 10 other - 20 discount) / 10.
        // Before the fix the correction was recorded and then ignored: the projection re-read
        // revision.FreightAmount and produced 114.0000 again, so the reviewer's accepted correction
        // moved no number anywhere in the platform.
        var corrected = await context.SupplierQuotedItems.AsNoTracking()
            .SingleAsync(x => x.Id == quotedItemId);
        Assert.Equal(134.0000m, corrected.LandedUnitCost);
        Assert.Equal(300m, corrected.FreightCost);
        Assert.NotEqual(114.0000m, corrected.LandedUnitCost);
    }

    [Fact]
    public async Task A_correction_cannot_re_base_an_offer_that_has_already_been_awarded()
    {
        // The other half of "never accepted-and-ignored": where the correction must not move the
        // money, it is refused with a reason, not swallowed.
        using var fixture = new ProcurementScenario();
        await SetInventoryOnHandAsync(fixture, 0m);
        var quotedItemId = await CaptureAsync(fixture, "awarded", freight: 100m);
        await fixture.Execute(service => service.ApproveAwardAsync(new ApproveAwardCommand(
            fixture.BusinessUnitId, quotedItemId, 10m, 1, "awarded-award", "qa", "corr-awarded", 42,
            "Best eligible landed cost")));

        var (quoteId, revisionId, evidenceId) = await FreightEvidenceAsync(fixture, quotedItemId);
        await using var context = fixture.Context();
        await new SupplierQuoteInboxService(context).ReviewAsync(
            Correction(fixture, quoteId, revisionId, evidenceId, "900"));

        // Correcting an offer a live award is built on puts the quote back into REVIEW_REQUIRED.
        // This assertion failed when first written, and the reason was a second defect: ReviewAsync
        // read revision.Evidence from a navigation the query never Included, so the set of
        // "evidence that affects the projection" was always empty, the awarded-offer guard never
        // ran, and the inbox reported nothing at all.
        var reviewed = await context.SupplierQuotes.AsNoTracking().SingleAsync(x => x.Id == quoteId);
        Assert.Equal(SupplierQuoteInboxStatuses.ReviewRequired, reviewed.InboxStatus);
        await Assert.ThrowsAsync<SupplierQuoteValidationException>(() =>
            new SupplierQuoteCommercialService(context).ProjectAsync(new ProjectSupplierQuoteCommand(
                fixture.BusinessUnitId, quoteId, reviewed.Version, "awarded-projection", "qa",
                "corr-awarded-projection")));

        // And the awarded cost is untouched — the refusal is real, not cosmetic.
        Assert.Equal(110.0000m, await context.SupplierQuotedItems.AsNoTracking()
            .Where(x => x.Id == quotedItemId).Select(x => x.LandedUnitCost).SingleAsync());
    }

    [Fact]
    public async Task An_uploaded_supplier_quote_can_carry_freight_and_duty()
    {
        // The document-intake path passed literal 0, 0 for freight and tax and had no field for
        // duty at all, so landed cost equalled unit price on the platform's headline ingestion
        // path. This drives the same UPLOAD-channel capture command the intake service now builds.
        using var fixture = new ProcurementScenario();
        var anchor = await AnchorAsync(fixture, "upload");

        await using var context = fixture.Context();
        var captured = await new SupplierQuoteInboxService(context).CaptureAsync(
            new SupplierQuotes.CaptureSupplierQuoteCommand(fixture.BusinessUnitId,
                ProcurementTestData.Supplier, anchor.SolicitationId, anchor.SourcingCaseId,
                anchor.NexoraSerial, "SQ-UPLOAD-1", 1, SupplierQuoteCaptureChannels.Upload,
                null, "source-document:1", new string('a', 64), ProcurementTestData.Currency,
                DateTime.UtcNow.AddDays(30), "FOB", 100m, 0m, 50m, 10m, 20m, null, null,
                [new SupplierQuotes.CaptureSupplierQuoteLine(1, fixture.RfqItemId, anchor.DemandLineId, "QA-PART-0",
                    null, null, "QA Product", 10m, 10m, "EA", 100m, null, 5, null, null, null,
                    false, null, [])],
                [], "upload-capture", "qa", "corr-upload"));

        var revision = await context.Set<SupplierQuoteRevision>().AsNoTracking()
            .SingleAsync(x => x.Id == captured.RevisionId);
        Assert.Equal(100m, revision.FreightAmount);
        Assert.Equal(50m, revision.DutyAmount);
        Assert.Equal(10m, revision.OtherAmount);
        Assert.Equal(20m, revision.DiscountAmount);
        Assert.Equal("FOB", revision.Incoterms);

        // And every one of them is correctable, because capture now emits header evidence for the
        // charge block. Without an evidence row a reviewer cannot lodge a correction at all.
        var chargeFields = await context.Set<SupplierQuoteFieldEvidence>().AsNoTracking()
            .Where(x => x.SupplierQuoteRevisionId == revision.Id && x.SupplierQuoteLineId == null)
            .Select(x => x.FieldName).ToListAsync();
        Assert.Contains("FreightAmount", chargeFields);
        Assert.Contains("DutyAmount", chargeFields);
        Assert.Contains("DiscountAmount", chargeFields);
    }

    // ───────────────────────────────────────── defect 3: duty, other and discount

    [Fact]
    public async Task Each_charge_reaches_the_canonical_revision_as_itself()
    {
        using var fixture = new ProcurementScenario();
        var quotedItemId = await CaptureAsync(fixture, "charges", freight: 100m, duty: 50m,
            other: 10m, discount: 20m);

        await using var context = fixture.Context();
        var quotedItem = await context.SupplierQuotedItems.AsNoTracking()
            .SingleAsync(x => x.Id == quotedItemId);
        var revision = await context.Set<SupplierQuoteRevision>().AsNoTracking()
            .SingleAsync(x => x.Id == quotedItem.SourceSupplierQuoteRevisionId);

        // Freight is freight. It used to be 160 — freight + duty + other collapsed into one field —
        // and the discount was dropped on the floor between the workbench and the revision.
        Assert.Equal(100m, revision.FreightAmount);
        Assert.NotEqual(160m, revision.FreightAmount);
        Assert.Equal(50m, revision.DutyAmount);
        Assert.Equal(10m, revision.OtherAmount);
        Assert.Equal(20m, revision.DiscountAmount);
    }

    [Fact]
    public async Task A_re_projection_reproduces_the_landed_cost_the_offer_was_captured_at()
    {
        using var fixture = new ProcurementScenario();
        var quotedItemId = await CaptureAsync(fixture, "reproject", freight: 100m, duty: 50m,
            other: 10m, discount: 20m);

        await using var context = fixture.Context();
        var quotedItem = await context.SupplierQuotedItems.AsNoTracking()
            .SingleAsync(x => x.Id == quotedItemId);
        Assert.Equal(114.0000m, quotedItem.LandedUnitCost);

        // Correct a NON-money field, so the only thing under test is whether the recomputation
        // reproduces the same money.
        var evidence = await context.Set<SupplierQuoteFieldEvidence>().AsNoTracking()
            .Where(x => x.SupplierQuoteRevisionId == quotedItem.SourceSupplierQuoteRevisionId &&
                x.FieldName == "LeadTimeDays").Select(x => x.Id).FirstAsync();
        var quoteId = quotedItem.SourceSupplierQuoteId!.Value;
        await new SupplierQuoteInboxService(context).ReviewAsync(new ReviewSupplierQuoteFieldCommand(
            fixture.BusinessUnitId, quoteId, quotedItem.SourceSupplierQuoteRevisionId!.Value,
            evidence, SupplierQuoteReviewStatuses.Corrected, "4",
            "Supplier confirmed a shorter lead time by telephone", "qa", "corr-reproject-review"));

        var version = await context.SupplierQuotes.Where(x => x.Id == quoteId)
            .Select(x => x.Version).SingleAsync();
        await new SupplierQuoteCommercialService(context).ProjectAsync(new ProjectSupplierQuoteCommand(
            fixture.BusinessUnitId, quoteId, version, "reproject", "qa", "corr-reproject"));

        // The number the award would have been built on, reproduced exactly. Before the fix the
        // re-projection read a collapsed FreightAmount of 160 and no discount, and produced
        // 116.0000 — a 2.00/unit drift with nothing in the record to explain it.
        var reprojected = await context.SupplierQuotedItems.AsNoTracking()
            .SingleAsync(x => x.Id == quotedItemId);
        Assert.Equal(114.0000m, reprojected.LandedUnitCost);
        Assert.NotEqual(116.0000m, reprojected.LandedUnitCost);
        Assert.Equal(50m, reprojected.DutyCost);
        Assert.Equal(10m, reprojected.OtherCost);
        Assert.Equal(20m, reprojected.DiscountAmount);
        Assert.Equal(4, reprojected.LeadTimeDays);
    }

    [Fact]
    public async Task A_non_delivered_incoterm_recording_no_duty_warns_without_blocking()
    {
        using var fixture = new ProcurementScenario();
        var quotedItemId = await CaptureAsync(fixture, "exw", freight: 100m, incoterms: "EXW");

        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        var offer = Assert.Single(comparison.Lines.Where(x => x.SupplierQuotedItemId == quotedItemId));

        // A warning, not a blocker. An EXW quote with no duty is perfectly awardable — it is just
        // probably underpriced, and only the buyer can say.
        Assert.True(offer.Eligible);
        Assert.Empty(offer.Blockers);
        var warning = Assert.Single(offer.CostWarnings!);
        Assert.Contains("EXW", warning);
        Assert.Contains("duty", warning);
    }

    [Fact]
    public async Task A_delivered_incoterm_and_a_recorded_duty_both_silence_the_warning()
    {
        using var fixture = new ProcurementScenario();
        // DDP: the Supplier clears import customs, so duty really is inside the quoted price.
        var delivered = await CaptureAsync(fixture, "ddp", freight: 100m, incoterms: "DDP");
        var deliveredOffer = await OfferAsync(fixture, delivered);
        Assert.Empty(deliveredOffer.CostWarnings!);

        // EXW with the duty actually captured: nothing is missing, so nothing is said.
        using var second = new ProcurementScenario();
        var dutied = await CaptureAsync(second, "exw-duty", freight: 100m, duty: 50m, incoterms: "EXW");
        var dutiedOffer = await OfferAsync(second, dutied);
        Assert.Empty(dutiedOffer.CostWarnings!);
    }

    // ───────────────────────────────────────── fixture plumbing

    /// <summary>
    /// One Supplier line of 10 units at 100, carrying whatever charges the case under test needs.
    /// The shared <c>ProcurementScenario.QuoteLine</c> helper quotes 12 a unit with small charges,
    /// which makes every landed-cost figure a repeating decimal and hides the arithmetic.
    /// </summary>
    private static async Task<long> CaptureAsync(ProcurementScenario fixture, string key,
        decimal freight = 0m, decimal duty = 0m, decimal other = 0m, decimal discount = 0m,
        string? incoterms = null)
    {
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation($"{key}-sol")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        var captured = await fixture.Execute(service => service.CaptureSupplierQuoteAsync(
            new Procurement.CaptureSupplierQuoteCommand(fixture.BusinessUnitId, solicitation.Id,
                $"SQ-{key}", 1, DateTime.UtcNow.AddDays(30), $"{key}-quote", "qa", $"corr-{key}",
                [new Procurement.CaptureSupplierQuoteLine(fixture.RfqItemId, ProcurementTestData.Product,
                    10m, 100m, ProcurementTestData.Currency, 5, 10m, freight, duty, other,
                    0m, discount, 1m, 95m)],
                incoterms)));
        return Assert.Single(captured.LineIds);
    }

    private sealed record CaptureAnchor(long SolicitationId, long SourcingCaseId, long DemandLineId,
        string NexoraSerial);

    private static async Task<CaptureAnchor> AnchorAsync(ProcurementScenario fixture, string key)
    {
        var solicitation = await fixture.Execute(service =>
            service.CreateSolicitationAsync(fixture.Solicitation($"{key}-sol")));
        await fixture.MarkSolicitationSentAsync(solicitation.Id);
        await using var context = fixture.Context();
        var row = await context.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>().AsNoTracking()
            .SingleAsync(x => x.Id == solicitation.Id);
        var demandLineId = await context.CommercialDemandLines.AsNoTracking()
            .Where(x => x.BusinessUnitId == fixture.BusinessUnitId && x.RfqItemId == fixture.RfqItemId)
            .Select(x => x.Id).SingleAsync();
        return new CaptureAnchor(row.Id, row.SourcingCaseId!.Value, demandLineId, row.NexoraSerial!);
    }

    private static async Task<(long QuoteId, long RevisionId, long EvidenceId)> FreightEvidenceAsync(
        ProcurementScenario fixture, long quotedItemId)
    {
        await using var context = fixture.Context();
        var quotedItem = await context.SupplierQuotedItems.AsNoTracking()
            .SingleAsync(x => x.Id == quotedItemId);
        var evidenceId = await context.Set<SupplierQuoteFieldEvidence>().AsNoTracking()
            .Where(x => x.SupplierQuoteRevisionId == quotedItem.SourceSupplierQuoteRevisionId &&
                x.SupplierQuoteLineId == null && x.FieldName == "FreightAmount")
            .Select(x => x.Id).SingleAsync();
        return (quotedItem.SourceSupplierQuoteId!.Value,
            quotedItem.SourceSupplierQuoteRevisionId!.Value, evidenceId);
    }

    private static ReviewSupplierQuoteFieldCommand Correction(ProcurementScenario fixture,
        long quoteId, long revisionId, long evidenceId, string value) => new(
        fixture.BusinessUnitId, quoteId, revisionId, evidenceId,
        SupplierQuoteReviewStatuses.Corrected, value,
        "Supplier freight confirmed against the signed quotation", "qa", "corr-freight-review");

    private static async Task<QuoteComparisonLine> OfferAsync(ProcurementScenario fixture, long quotedItemId)
    {
        var comparison = await fixture.Execute(service =>
            service.CompareQuotesAsync(fixture.BusinessUnitId, fixture.RfqItemId));
        return Assert.Single(comparison.Lines.Where(x => x.SupplierQuotedItemId == quotedItemId));
    }

    private static async Task SetInventoryOnHandAsync(ProcurementScenario fixture, decimal quantity)
    {
        await using var context = fixture.Context();
        var inventory = await context.Set<Models.Inventory>()
            .SingleAsync(x => x.Id == ProcurementTestData.Inventory);
        inventory.QtyOnHand = quantity;
        await context.SaveChangesAsync();
    }
}
