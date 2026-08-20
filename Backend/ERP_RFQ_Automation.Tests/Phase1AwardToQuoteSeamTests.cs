using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.SupplierQuotes;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE SEAM NOBODY OWNS: supplier award -> priced customer quote.
///
/// <para><b>Why this file exists.</b> Phase1SpineSeamTests proves inquiry -> lead -> RFQ ->
/// sourcing case, and then picks the journey up again at the supplier purchase order. Between
/// those two lies the stretch that decides whether a quote carries money, and its own doc comment
/// says so: "the customer arm — quotation, customer PO, customer award, sales order — is exercised
/// only as far as the sales order the despatch needs."</para>
///
/// <para><b>What the gap cost.</b> On the live tenant this segment has never once completed. Two
/// customer quotes exist, QT-0826-0001 and QT-0826-0002, and BOTH carry a total of zero, because
/// the price preview refuses: "Create or revise the Customer Quote through the governed Supplier
/// award pricing bridge." Every gate on either side is green and the product still cannot produce
/// its own output. A seam with no test is a seam that fails in production instead of in CI.</para>
///
/// <para><b>What this deliberately does not cover.</b> Dispatch. Reaching a delivered solicitation
/// is its own seam, owned by the outbox and the dispatch worker, and it is stubbed here by setting
/// the status the worker would have set. That is stated rather than hidden: if a future change
/// breaks DISPATCH, this file keeps passing and should not be read as saying otherwise.</para>
/// </summary>
public sealed class Phase1AwardToQuoteSeamTests
{
    [Fact]
    public async Task A_supplier_award_is_what_puts_a_price_on_the_customer_quote()
    {
        using var spine = new UpstreamSpine();

        var lead = await spine.EstablishLeadAsync();
        var (rfqId, _) = await spine.ConvertAsync(lead.Id);
        var sourcingCase = await spine.OpenSourcingCaseAsync(rfqId);

        long rfqItemId;
        await using (var read = spine.Context())
            rfqItemId = await read.Rfqitems.Where(x => x.Rfqid == rfqId && x.LineItemNo == "00010")
                .Select(x => x.Id).SingleAsync();

        // ---- a supplier is solicited -------------------------------------------------------
        const long supplierId = 97_150;
        await using (var seed = spine.Context())
        {
            var supplier = AgentSeed.Supplier(seed, supplierId, UpstreamSpine.Tenant,
                "Seam Supplier", "seam-supplier@example.test");
// SupplierRfqBlockingReasons is the real contract and all six clauses are satisfied
            // here rather than the guard weakened. An unverified, uncleared or unready supplier is
            // deliberately not solicitable, and a test that bypassed that would be certifying a
            // path production does not have.
            supplier.IsActive = true;
            supplier.ContactEmail = "seam-supplier@example.test";
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
            supplier.VerificationStatus = SupplierVerificationStatuses.Verified;
            supplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
            supplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
            supplier.RiskStatus = SupplierRiskStatuses.Low;
            // Discovery matches supplier Tags or Name against the sourcing case's requested part
            // or manufacturer, so the supplier is tagged with the part it actually supplies.
            supplier.Tags = UpstreamSpine.FirstLinePart;
            await seed.SaveChangesAsync();
        }

        // The candidate search is not decoration: PrepareSupplierRfqAsync refuses a supplier that
        // is not a PERSISTED candidate of this case, so the discovery step is part of the seam.
        long caseVersion = sourcingCase.Version;
        await using (var search = spine.Context())
        {
            var found = await new ProcurementApplicationService(search).SearchSourcingCandidatesAsync(
                new SearchSourcingCandidatesCommand(UpstreamSpine.Tenant, sourcingCase.Id, 10,
                    caseVersion, "seam-search", "qa", "corr-seam-search"));
            caseVersion = found.Version;
        }

        long solicitationId;
        await using (var prepare = spine.Context())
        {
            var prepared = await new ProcurementApplicationService(prepare).PrepareSupplierRfqAsync(
                new PrepareSupplierRfqCommand(UpstreamSpine.Tenant, sourcingCase.Id, supplierId,
                    DateTime.UtcNow.AddDays(7), caseVersion,
                    "seam-prepare", "qa", "corr-seam-prepare"));
            solicitationId = prepared.SupplierSolicitationId;
        }

        // NOT the seam under test. The dispatch worker owns delivery; this stands in for it so the
        // capture guard — which correctly refuses a response with no delivery evidence — is
        // satisfied honestly rather than weakened.
        await using (var deliver = spine.Context())
        {
            var solicitation = await deliver.Set<SupplierSolicitation>().SingleAsync(x => x.Id == solicitationId);
            solicitation.Status = SolicitationStatus.Sent;
            solicitation.SentOn = DateTime.UtcNow;
            await deliver.SaveChangesAsync();
        }

        // ---- the supplier answers ----------------------------------------------------------
        const decimal SupplierUnitPrice = 214.50m;
        long supplierQuotedItemId;
        await using (var capture = spine.Context())
        {
            var captured = await new ProcurementApplicationService(capture).CaptureSupplierQuoteAsync(
                new Procurement.CaptureSupplierQuoteCommand(UpstreamSpine.Tenant, solicitationId,
                    "SEAM-Q-0001", 1, DateTime.UtcNow.AddDays(30),
                    "seam-capture", "qa", "corr-seam-capture",
                    new[]
                    {
                        new Procurement.CaptureSupplierQuoteLine(
                            RfqItemId: rfqItemId,
                            // THE FIELD THAT DECIDES ELIGIBILITY. A captured offer with a null
                            // product is refused by the award for "product unresolved", and an
                            // offer already recorded cannot be retro-fixed — resolving the RFQ
                            // line afterwards does not reach it. That is exactly how the live
                            // journey stalled.
                            ProductId: UpstreamSpine.ProductId,
                            Quantity: UpstreamSpine.FirstLineQuantity,
                            UnitPrice: SupplierUnitPrice,
                            CurrencyId: UpstreamSpine.CurrencyId,
                            LeadTimeDays: 45,
                            AvailableQuantity: UpstreamSpine.FirstLineQuantity,
                            FreightCost: 0m, DutyCost: 0m, OtherCost: 0m,
                            TaxAmount: 0m, DiscountAmount: 0m,
                            MinimumOrderQuantity: null, ReliabilitySnapshot: null,
                            WarrantyMonths: 12),
                    }));
            supplierQuotedItemId = captured.LineIds.Single();
        }

        // SEAM: the comparison the buyer reads must find this offer ELIGIBLE. If it does not, the
        // award below cannot happen and the customer quote can never be priced.
        await using (var compare = spine.Context())
        {
            var comparison = await new ProcurementApplicationService(compare)
                .CompareQuotesAsync(UpstreamSpine.Tenant, rfqItemId);
            var offer = comparison.Lines.Single(x => x.SupplierQuotedItemId == supplierQuotedItemId);
            Assert.True(offer.Eligible,
                $"the captured offer is not awardable: {string.Join("; ", offer.Blockers)}");
        }

        // ---- the award ---------------------------------------------------------------------
        // A freshly captured canonical revision is version 1. If that ever stops being true the
        // award throws a concurrency conflict naming the expected value, which is a better failure
        // than a silently-skipped assertion.
        const long quoteVersion = 1;
        long awardId;
        await using (var award = spine.Context())
        {
            var approved = await new ProcurementApplicationService(award).ApproveAwardAsync(
                new ApproveAwardCommand(UpstreamSpine.Tenant, supplierQuotedItemId,
                    UpstreamSpine.FirstLineQuantity, quoteVersion,
                    "seam-award", "qa", "corr-seam-award", AwardedByUserId: null,
                    Rationale: "Lowest landed cost with stock availability."));
            awardId = approved.Id;
        }

        // ---- the customer quote ------------------------------------------------------------
        await using (var decide = spine.Context())
        {
            var line = await decide.Rfqitems.SingleAsync(x => x.Id == rfqItemId);
            line.DecideParticipation("Quote", "Awarded to a supplier at a captured price.", "qa", DateTime.UtcNow);
            await decide.SaveChangesAsync();
        }

        long quoteItemId;
        await using (var draft = spine.Context())
        {
            var quote = await new QuoteService(draft, null!, null!).PrepareDraftFromRfqAsync(rfqId, UpstreamSpine.Tenant, "qa");

            // SEAM: the quote inherits the RFQ's commercial identity. Read the ROW, not the DTO —
            // a response contract that omits the serial and a quote that never received one look
            // identical from outside, and only one of them is a defect.
            await using (var verify = spine.Context())
            {
                var stored = await verify.Quotes.AsNoTracking().SingleAsync(x => x.Id == quote.Id);
                Assert.Equal(lead.CommercialCaseReference, stored.NexoraSerial);
                Assert.Equal(lead.CustomerId, stored.CustomerId);
            }

            // The draft is deliberately born UNPRICED — "Commercial Review Required: pricing,
            // inventory, lead time, tax, freight and validity remain pending". Zero here is
            // correct, and asserting otherwise would be asserting against the design.
            var quoted = Assert.Single(quote.QuoteItems);
            Assert.Equal(0m, quoted.UnitPrice);
            quoteItemId = quoted.Id;
        }

        // ---- THE SEAM: the award prices the quote ------------------------------------------
        //
        // This is the step nobody called. On the live tenant two customer quotes sat at zero and
        // the price preview kept answering "Create or revise the Customer Quote through the
        // governed Supplier award pricing bridge" — which is this call. The award and the draft
        // are both necessary and neither is sufficient; the bridge is what joins them, and until
        // now nothing in the suite exercised it.
        await using (var price = spine.Context())
        {
            var priced = await new SupplierQuoteCommercialService(price).ApplyPricingAsync(
                new ApplyCustomerQuotePricingCommand(UpstreamSpine.Tenant, quoteItemId, awardId,
                    TargetMarginPercent: 22m,
                    Rationale: "Standard commercial margin on the awarded landed cost.",
                    "seam-pricing", "qa", "corr-seam-pricing"));
            Assert.True(priced.SupplierLandedUnitCost > 0m,
                "the pricing bridge derived no landed cost from the award");
        }

        await using (var verify = spine.Context())
        {
            var line = await verify.QuoteItems.AsNoTracking().SingleAsync(x => x.Id == quoteItemId);
            // The point of the whole segment: the customer quote now states a price, and that
            // price is derived from what a supplier actually offered rather than typed in.
            Assert.True(line.UnitPrice > 0m,
                "the customer quote line is still zero after the award pricing bridge ran");
        }
    }
}
