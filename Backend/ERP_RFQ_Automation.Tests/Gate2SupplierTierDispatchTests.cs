using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 2, FR-QTM-01: tier-targeted supplier dispatch, on the search endpoint a sales engineer
/// actually picks suppliers from.
///
/// <para>The capability had no backend at all — the search projected nine columns and not the tier,
/// and the endpoint took no tier parameter — so "dispatch this Supplier RFQ to our Tier 1 partners"
/// could not be expressed however the tier was captured.</para>
///
/// <para><b>The line these tests hold.</b> Ruling R-A: a tier ANNOTATES, ORDERS and PRE-SELECTS, and
/// NEVER gates. The governance/verification/compliance/risk/readiness predicate is the only gate on
/// who may receive a Supplier RFQ, and a tier filter is a view over that list — narrowing it must
/// leave every supplier exactly as dispatchable as it was. An unclassified supplier — which is every
/// supplier that existed before this gate — must still come back.</para>
/// </summary>
public sealed class Gate2SupplierTierDispatchTests
{
    private const long Tenant = 72_500;

    // Names are deliberately in the OPPOSITE order to the tiers, so a result that merely kept the
    // pre-existing alphabetical ordering cannot pass the ordering assertions by luck.
    private const long Tier1SupplierId = 72_511;   // "Zulu Partner Works"
    private const long Tier2SupplierId = 72_512;   // "Yankee Extended Supply"
    private const long Tier3SupplierId = 72_513;   // "Alpha Exception Trading"
    private const long UnclassifiedSupplierId = 72_514; // "Bravo Unclassified Metals"

    // ============================================================================
    // The tier reaches the search result, and orders it
    // ============================================================================

    /// <summary>
    /// The tier comes back on every row, the list is ordered Tier 1 → Tier 2 → Tier 3 →
    /// unclassified, and the unclassified supplier is still in it. Ordering is what makes the tier
    /// useful without gating anything: the closest relationships surface first and nobody disappears.
    /// </summary>
    [Fact]
    public async Task The_search_returns_the_tier_orders_tier_one_first_and_still_returns_the_unclassified()
    {
        using var database = new TestDb();
        await SeedDispatchableSuppliersAsync(database);

        await using var context = database.ContextFor(Tenant);
        var results = await Search(Controller(context));

        Assert.Equal(
            [Tier1SupplierId, Tier2SupplierId, Tier3SupplierId, UnclassifiedSupplierId],
            results.Select(x => x.Id).ToArray());

        // The value itself, not merely the ordering it produced — the caller has to be able to show
        // the operator which tier they are looking at.
        Assert.Equal(SupplierTiers.Tier1Partner, results[0].Tier);
        Assert.Equal(SupplierTiers.Tier2Extended, results[1].Tier);
        Assert.Equal(SupplierTiers.Tier3OutOfNetwork, results[2].Tier);

        // "Not yet classified" is a resting state, not an exclusion. Every supplier that existed
        // before this gate is in it.
        Assert.Null(results[3].Tier);
    }

    // ============================================================================
    // The filter narrows the view and nothing else — R-A
    // ============================================================================

    /// <summary>
    /// Supplied, the tier filter narrows the list. Omitted, nothing changes — including for the
    /// suppliers nobody has classified.
    /// </summary>
    [Fact]
    public async Task The_tier_filter_narrows_the_list_only_when_it_is_supplied()
    {
        using var database = new TestDb();
        await SeedDispatchableSuppliersAsync(database);

        await using var context = database.ContextFor(Tenant);
        var controller = Controller(context);

        Assert.Equal(4, (await Search(controller)).Count);
        Assert.Equal(4, (await Search(controller, tier: "  ")).Count);

        var partners = await Search(controller, SupplierTiers.Tier1Partner);
        Assert.Equal(Tier1SupplierId, Assert.Single(partners).Id);

        var unclassifiedStillListed = await Search(controller, SupplierTiers.Tier3OutOfNetwork);
        Assert.Equal(Tier3SupplierId, Assert.Single(unclassifiedStillListed).Id);
    }

    /// <summary>
    /// THE R-A TEST. Filtering the list by tier changes what is DISPLAYED and never who may be
    /// dispatched to: every supplier the filter left out — including the one nobody has classified —
    /// passes the Supplier RFQ gate exactly as it did before, and the gate's answer is identical for
    /// every tier value. A tier that could silence outreach would be a compliance control wearing a
    /// commercial label.
    /// </summary>
    [Fact]
    public async Task Filtering_by_tier_never_changes_which_suppliers_are_dispatch_eligible()
    {
        using var database = new TestDb();
        await SeedDispatchableSuppliersAsync(database);

        await using var context = database.ContextFor(Tenant);
        var controller = Controller(context);

        // A tier-targeted search that returns exactly one supplier…
        var targeted = await Search(controller, SupplierTiers.Tier1Partner);
        Assert.Equal(Tier1SupplierId, Assert.Single(targeted).Id);

        // …leaves every OTHER supplier just as dispatchable, whatever tier it carries and whether it
        // carries one at all.
        foreach (var supplierId in new[]
                 { Tier1SupplierId, Tier2SupplierId, Tier3SupplierId, UnclassifiedSupplierId })
        {
            var outreach = await controller.ComposeQuoteEmail(new BatchQuoteRequestDTO
            {
                SupplierId = supplierId,
                Items = [new QuoteItemDTO { PartNumber = "P-1", Quantity = 1 }]
            });

            Assert.IsType<OkObjectResult>(outreach.Result);
        }
    }

    /// <summary>
    /// The eligibility predicate is untouched by the tier work: a supplier whose governance has
    /// lapsed is out of the search whatever tier it holds, and the most senior tier does not buy it
    /// back in. The two axes stay independent in both directions.
    /// </summary>
    [Fact]
    public async Task A_tier_one_partner_that_fails_governance_is_still_excluded()
    {
        using var database = new TestDb();
        await SeedDispatchableSuppliersAsync(database);

        await using (var setup = database.ContextFor(Tenant))
        {
            var partner = setup.Suppliers.Single(x => x.Id == Tier1SupplierId);
            partner.ComplianceStatus = SupplierComplianceStatuses.Blocked;
            await setup.SaveChangesAsync();
        }

        await using var context = database.ContextFor(Tenant);
        var controller = Controller(context);

        Assert.DoesNotContain(await Search(controller), x => x.Id == Tier1SupplierId);
        Assert.Empty(await Search(controller, SupplierTiers.Tier1Partner));
    }

    // ============================================================================
    // The filter reads the one canonicaliser
    // ============================================================================

    /// <summary>
    /// A tier typed the way the supplier form accepts it filters the way the supplier form stores
    /// it. The endpoint validates through the same canonicaliser as the request DTOs and the bulk
    /// importer rather than restating the permitted set, so the three cannot disagree.
    /// </summary>
    [Theory]
    [InlineData("tier_1_partner")]
    [InlineData("Tier 1 Partner")]
    [InlineData("  TIER-1-PARTNER  ")]
    public async Task A_tier_filter_is_canonicalised_before_it_is_applied(string typed)
    {
        using var database = new TestDb();
        await SeedDispatchableSuppliersAsync(database);

        await using var context = database.ContextFor(Tenant);
        var matched = await Search(Controller(context), typed);

        Assert.Equal(Tier1SupplierId, Assert.Single(matched).Id);
    }

    /// <summary>
    /// A tier nobody defined is refused with the permitted values named. Returning an empty list
    /// instead would read as "you have no Tier 4 suppliers" — a false answer to a question that
    /// cannot be asked.
    /// </summary>
    [Fact]
    public async Task An_unknown_tier_filter_is_refused_rather_than_silently_matching_nothing()
    {
        using var database = new TestDb();
        await SeedDispatchableSuppliersAsync(database);

        await using var context = database.ContextFor(Tenant);
        var response = await Controller(context).Search(searchTerm: null, productCategory: null, tier: "PLATINUM");

        var problem = Assert.IsType<ProblemDetails>(
            Assert.IsType<BadRequestObjectResult>(response.Result).Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("PLATINUM", problem.Detail!, StringComparison.Ordinal);
        foreach (var permitted in SupplierTiers.All)
            Assert.Contains(permitted, problem.Detail!, StringComparison.Ordinal);
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static async Task<List<SupplierSearchResultDTO>> Search(
        SupplierController controller, string? tier = null)
    {
        var response = await controller.Search(searchTerm: null, productCategory: null, tier: tier);
        return Assert.IsType<List<SupplierSearchResultDTO>>(
            Assert.IsType<OkObjectResult>(response.Result).Value);
    }

    /// <summary>
    /// Four suppliers that are identical on every dimension the dispatch gate reads and differ only
    /// in tier — so anything the tests observe is the tier and nothing else.
    /// </summary>
    private static async Task SeedDispatchableSuppliersAsync(TestDb database)
    {
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, Tenant);
        context.Suppliers.AddRange(
            Dispatchable(Tier1SupplierId, "Zulu Partner Works", SupplierTiers.Tier1Partner),
            Dispatchable(Tier2SupplierId, "Yankee Extended Supply", SupplierTiers.Tier2Extended),
            Dispatchable(Tier3SupplierId, "Alpha Exception Trading", SupplierTiers.Tier3OutOfNetwork),
            Dispatchable(UnclassifiedSupplierId, "Bravo Unclassified Metals", tier: null));
        await context.SaveChangesAsync();
    }

    private static Supplier Dispatchable(long id, string name, string? tier) => new()
    {
        Id = id,
        Buid = Tenant,
        Name = name,
        ContactEmail = $"sales-{id}@example.test",
        ImageUrl = "n/a",
        IsActive = true,
        Tier = tier,
        GovernanceStatus = SupplierGovernanceStatuses.Approved,
        VerificationStatus = SupplierVerificationStatuses.Verified,
        ComplianceStatus = SupplierComplianceStatuses.Cleared,
        RiskStatus = SupplierRiskStatuses.Low,
        ReadinessStatus = SupplierReadinessStatuses.Ready,
        ConcurrencyToken = Guid.NewGuid(),
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };

    private static SupplierController Controller(ErpRfqAutomationContext context)
    {
        var identity = new ClaimsIdentity(
            [new Claim("businessUnitId", Tenant.ToString())], "Test");
        return new SupplierController(
            new SupplierRepository(context, new DeterministicSupplierNumberGenerator()),
            new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) }
            }
        };
    }
}
