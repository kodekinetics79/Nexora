using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE SEAM: stock that arrives AFTER a sourcing case is raised must stop the outreach.
///
/// <para><b>Why this matters commercially.</b> A sourcing case is a standing statement that we
/// are short. Between raising it and contacting a supplier, the shortfall can close on its own —
/// a receipt against another order, a return, a cancelled allocation. Soliciting a supplier for
/// material already on the shelf costs money and credibility, and the buyer has no way to know
/// from the case row itself, because <c>UnfulfilledQuantity</c> was computed when the case was
/// opened.</para>
///
/// <para><b>What the code actually does, having expected worse.</b> Nothing in
/// <c>Inventory/</c> or <c>InboundLogistics/</c> references a sourcing case at all — there is no
/// event, no listener, no projection refresh. That looked like a broken link. It is not:
/// <c>PrepareSupplierRfqAsync</c> recomputes the net requirement from live ATP at the moment of
/// outreach and REFUSES if it has closed. Lazy recomputation with a guard at the point of action,
/// rather than an event chain that can drop a message.</para>
///
/// <para><b>Why it is worth a test anyway.</b> That guarantee is invisible. It lives in one
/// recomputation inside a method whose name is about preparing outreach, and a refactor that
/// moved the shortfall read to the stored column — the obvious "optimisation", since the column is
/// right there — would delete it silently and every existing test would stay green.</para>
/// </summary>
public sealed class Phase1InventoryToSourcingSeamTests
{
    [Fact]
    public async Task Stock_arriving_after_the_case_is_raised_refuses_supplier_outreach()
    {
        using var spine = new UpstreamSpine();
        var lead = await spine.EstablishLeadAsync();
        var (rfqId, _) = await spine.ConvertAsync(lead.Id);
        var sourcingCase = await spine.OpenSourcingCaseAsync(rfqId);

        // The case was opened short: that is the premise, and asserting it keeps the refusal below
        // from passing for the wrong reason.
        Assert.True(sourcingCase.UnfulfilledQuantity > 0);
        Assert.Equal(UpstreamSpine.FirstLineQuantity, sourcingCase.RequestedQuantity);

        long supplierId = 97_170;
        long caseVersion;
        await using (var seed = spine.Context())
        {
            var supplier = Support.AgentSeed.Supplier(seed, supplierId, UpstreamSpine.Tenant,
                "Inventory Seam Supplier", "inventory-seam@example.test");
            supplier.IsActive = true;
            supplier.ContactEmail = "inventory-seam@example.test";
            supplier.GovernanceStatus = SupplierGovernanceStatuses.Approved;
            supplier.VerificationStatus = SupplierVerificationStatuses.Verified;
            supplier.ComplianceStatus = SupplierComplianceStatuses.Cleared;
            supplier.RiskStatus = SupplierRiskStatuses.Low;
            supplier.ReadinessStatus = SupplierReadinessStatuses.Ready;
            var product = await seed.Products.SingleAsync(x => x.Id == UpstreamSpine.ProductId);
            product.PreferredSupplierId = supplierId;
            await seed.SaveChangesAsync();
        }
        await using (var search = spine.Context())
        {
            var found = await new ProcurementApplicationService(search).SearchSourcingCandidatesAsync(
                new SearchSourcingCandidatesCommand(UpstreamSpine.Tenant, sourcingCase.Id, 10,
                    sourcingCase.Version, "inv-seam-search", "qa", "corr-inv-seam-search"));
            caseVersion = found.Version;
            Assert.Contains(found.Candidates, x => x.SupplierId == supplierId && x.EligibleForSupplierRfq);
        }

        // ---- the shortfall closes itself --------------------------------------------------
        // Stock lands for the exact product this line resolved to. No procurement API is told;
        // that is the whole point — inventory does not notify anybody.
        await using (var stock = spine.Context())
        {
            stock.Set<Models.Inventory>().Add(new Models.Inventory
            {
                Buid = UpstreamSpine.Tenant,
                ProductId = UpstreamSpine.ProductId,
                WarehouseId = UpstreamSpine.WarehouseId,
                PartNo = UpstreamSpine.FirstLinePart,
                QtyOnHand = UpstreamSpine.FirstLineQuantity,
                ReorderPoint = 0m,
                CreatedBy = "qa",
                CreatedOn = DateTime.UtcNow,
            });
            await stock.SaveChangesAsync();
        }

        // ---- SEAM: outreach must now be refused ---------------------------------------------
        await using (var prepare = spine.Context())
        {
            var refusal = await Assert.ThrowsAsync<ProcurementConflictException>(() =>
                new ProcurementApplicationService(prepare).PrepareSupplierRfqAsync(
                    new PrepareSupplierRfqCommand(UpstreamSpine.Tenant, sourcingCase.Id, supplierId,
                        DateTime.UtcNow.AddDays(7), caseVersion,
                        "inv-seam-prepare", "qa", "corr-inv-seam-prepare")));

            Assert.Contains("fully covered", refusal.Message, StringComparison.OrdinalIgnoreCase);
        }

        // And nothing was sent: a refusal that still queued the outreach would be worse than none.
        await using (var verify = spine.Context())
        {
            Assert.Empty(await verify.Set<Agent.Models.SupplierSolicitation>()
                .Where(x => x.SourcingCaseId == sourcingCase.Id).ToListAsync());
        }
    }
}
