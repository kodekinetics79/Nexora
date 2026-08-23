using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE SEAM: does onboarding a supplier actually produce a supplier you can USE?
///
/// <para><b>Why this exists.</b> A supplier is created UNVERIFIED / UNKNOWN / REVIEW_REQUIRED, and
/// <c>SupplierRfqBlockingReasons</c> refuses six different ways. Governance is set by a different
/// service, discovery is done by a third, and outreach by a fourth. Every one of those is tested
/// on its own; nothing asserted that walking them in order leaves you able to solicit the
/// supplier. Master data that cannot be used is the most expensive kind of data.</para>
///
/// <para><b>What it caught in the writing.</b> Nothing yet — it passes. That is worth stating
/// plainly rather than dressing up: this is a guard-pin, not a regression proof. Its value is that
/// the six-clause eligibility contract, the preferred-supplier discovery edge and the outreach
/// gate are now joined by something that fails if any one of them drifts apart from the others.
/// The one assertion that WOULD have caught a defect is the first: an ungoverned supplier must be
/// refused, so the rest of the test cannot pass for the wrong reason.</para>
/// </summary>
public sealed class Phase1SupplierOnboardingSeamTests
{
    private const long SupplierId = 97_160;

    [Fact]
    public async Task A_supplier_becomes_solicitable_only_after_governance_and_discovery_agree()
    {
        using var spine = new UpstreamSpine();
        var lead = await spine.EstablishLeadAsync();
        var (rfqId, _) = await spine.ConvertAsync(lead.Id);
        var sourcingCase = await spine.OpenSourcingCaseAsync(rfqId);

        // ---- a supplier as it is actually created: ungoverned -------------------------------
        Guid token;
        await using (var seed = spine.Context())
        {
            var supplier = AgentSeed.Supplier(seed, SupplierId, UpstreamSpine.Tenant,
                "Onboarding Supplier", "onboarding@example.test");
            supplier.IsActive = true;
            supplier.ContactEmail = "onboarding@example.test";
            supplier.ConcurrencyToken = Guid.NewGuid();
            // Deliberately NOT tagged with the part. Discovery must find this supplier through the
            // master-data link a human actually maintains, not through a free-text tag.
            await seed.SaveChangesAsync();
            token = supplier.ConcurrencyToken!.Value;
        }

        // Named as the product's preferred supplier BEFORE the refusal check, so that check asserts
        // something: the supplier IS discovered and is still refused. Without this it would pass
        // merely because nothing found it, which proves nothing about the eligibility contract.
        await using (var prefer = spine.Context())
        {
            var product = await prefer.Products.SingleAsync(x => x.Id == UpstreamSpine.ProductId);
            product.PreferredSupplierId = SupplierId;
            await prefer.SaveChangesAsync();
        }

        // VACUITY CHECK, and the only assertion here that could have caught a live defect: a
        // supplier in its birth state must be refused. If this ever passes, everything below is
        // passing for the wrong reason and this file is worthless.
        await using (var refuse = spine.Context())
        {
            var candidates = await new ProcurementApplicationService(refuse).SearchSourcingCandidatesAsync(
                new SearchSourcingCandidatesCommand(UpstreamSpine.Tenant, sourcingCase.Id, 10,
                    sourcingCase.Version, "onboard-search-0", "qa", "corr-onboard-0"));
            var found = candidates.Candidates.SingleOrDefault(x => x.SupplierId == SupplierId);
            Assert.True(found is not null,
                "the preferred-supplier edge did not discover the supplier at all");
            Assert.False(found!.EligibleForSupplierRfq,
                "an ungoverned supplier was offered as an ELIGIBLE sourcing candidate");
            Assert.NotEmpty(found.BlockingReasons);
        }

        // ---- governance, through the service that owns it -----------------------------------
        await using (var govern = spine.Context())
        {
            await new SupplierGovernanceService(govern)
                .GovernAsync(new GovernSupplierCommand(
                    UpstreamSpine.Tenant, SupplierId,
                    SupplierGovernanceStatuses.Approved,
                    SupplierVerificationStatuses.Verified,
                    SupplierComplianceStatuses.Cleared,
                    SupplierRiskStatuses.Low,
                    SupplierReadinessStatuses.Ready,
                    token,
                    "Onboarding completed: documents verified and compliance cleared.",
                    "qa", "onboard-govern", "corr-onboard-govern"));
        }

        long caseVersion;
        await using (var discover = spine.Context())
        {
            var refreshed = await discover.SourcingCases.AsNoTracking().SingleAsync(x => x.Id == sourcingCase.Id);
            caseVersion = refreshed.Version;
        }

        await using (var search = spine.Context())
        {
            var candidates = await new ProcurementApplicationService(search).SearchSourcingCandidatesAsync(
                new SearchSourcingCandidatesCommand(UpstreamSpine.Tenant, sourcingCase.Id, 10,
                    caseVersion, "onboard-search-1", "qa", "corr-onboard-1"));
            var found = candidates.Candidates.SingleOrDefault(x => x.SupplierId == SupplierId);
            Assert.True(found is not null,
                "a governed, preferred supplier was not discovered as a sourcing candidate");
            Assert.True(found!.EligibleForSupplierRfq,
                $"a fully governed supplier is still not solicitable: {string.Join("; ", found.BlockingReasons)}");
            caseVersion = candidates.Version;
        }

        // ---- and the outreach the whole chain exists to permit -------------------------------
        await using (var prepare = spine.Context())
        {
            var prepared = await new ProcurementApplicationService(prepare).PrepareSupplierRfqAsync(
                new PrepareSupplierRfqCommand(UpstreamSpine.Tenant, sourcingCase.Id, SupplierId,
                    DateTime.UtcNow.AddDays(7), caseVersion,
                    "onboard-prepare", "qa", "corr-onboard-prepare"));
            Assert.True(prepared.SupplierSolicitationId > 0,
                "a solicitation could not be prepared for a fully onboarded supplier");
        }
    }
}
