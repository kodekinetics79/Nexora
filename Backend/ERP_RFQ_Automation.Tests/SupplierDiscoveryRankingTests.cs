using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Procurement.Discovery;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The order a buyer trusts: maker, then appointed distributors, then everyone else; within a
/// role a supplier the company already knows first, then a Saudi company, then the search engine's
/// own order. Paging reports the whole ranked count so "show 10 more" knows when to stop.
/// </summary>
public sealed class SupplierDiscoveryRankingTests
{
    private static SupplierDiscoveryStoredHit Hit(string domain, string role, string? country, int order, string? name = null)
        => new(SupplierDiscoveryClassifier.HitId(domain), name ?? domain, $"https://{domain}", domain, role, country,
            "why", null, order);

    [Fact]
    public void Manufacturer_then_distributor_then_reseller_and_known_suppliers_first_within_a_role()
    {
        var hits = new[]
        {
            Hit("reseller-foreign.com", SupplierRoles.Reseller, null, 0),
            Hit("distributor-foreign.com", SupplierRoles.Distributor, null, 1),
            Hit("distributor-known.com", SupplierRoles.Distributor, null, 2),
            Hit("reseller-sa.com.sa", SupplierRoles.Reseller, "SA", 3),
            Hit("maker.com", SupplierRoles.Manufacturer, null, 4),
            Hit("distributor-sa.com", SupplierRoles.Distributor, "SA", 5),
            Hit("reseller-known-by-name.com", SupplierRoles.Reseller, null, 6, "Al Rajhi Electric"),
        };
        var known = new[]
        {
            new KnownSupplier(12, "Distributor Known LLC", "https://www.distributor-known.com/about"),
            new KnownSupplier(31, "AL RAJHI ELECTRIC", null)
        };

        var ranked = SupplierDiscoveryRanker.Rank(hits, known);

        Assert.Equal(
        [
            "maker.com",
            "distributor-known.com",      // known beats Saudi beats foreign, within Distributor
            "distributor-sa.com",
            "distributor-foreign.com",
            "reseller-known-by-name.com", // known by name, no website on record
            "reseller-sa.com.sa",
            "reseller-foreign.com"
        ], ranked.Select(x => x.Domain).ToArray());
        Assert.Equal(12, ranked.Single(x => x.Domain == "distributor-known.com").ExistingSupplierId);
        Assert.Equal(31, ranked.Single(x => x.Domain == "reseller-known-by-name.com").ExistingSupplierId);
        Assert.All(ranked.Where(x => x.Domain is not ("distributor-known.com" or "reseller-known-by-name.com")),
            x => Assert.Null(x.ExistingSupplierId));
    }

    [Fact]
    public void Within_one_role_and_country_the_search_engines_order_is_kept()
    {
        var hits = new[]
        {
            Hit("third.com", SupplierRoles.Reseller, null, 7),
            Hit("first.com", SupplierRoles.Reseller, null, 1),
            Hit("second.com", SupplierRoles.Reseller, null, 4),
        };
        Assert.Equal(["first.com", "second.com", "third.com"],
            SupplierDiscoveryRanker.Rank(hits, []).Select(x => x.Domain).ToArray());
    }

    [Fact]
    public void Paging_returns_the_slice_and_the_caller_keeps_the_total()
    {
        var hits = Enumerable.Range(0, 23).Select(i => Hit($"site{i:00}.com", SupplierRoles.Reseller, null, i)).ToArray();
        var ranked = SupplierDiscoveryRanker.Rank(hits, []);

        var first = SupplierDiscoveryRanker.Page(ranked, 0, 10);
        var second = SupplierDiscoveryRanker.Page(ranked, 10, 10);
        var third = SupplierDiscoveryRanker.Page(ranked, 20, 10);
        var beyond = SupplierDiscoveryRanker.Page(ranked, 30, 10);

        Assert.Equal(23, ranked.Count);
        Assert.Equal(10, first.Count);
        Assert.Equal("site00.com", first[0].Domain);
        Assert.Equal("site10.com", second[0].Domain);
        Assert.Equal(3, third.Count);
        Assert.Empty(beyond);
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, 9)]
    [InlineData(0, 51)]
    public void A_page_outside_the_contract_is_refused_in_plain_words(int offset, int limit)
    {
        var error = Assert.Throws<ProcurementValidationException>(() => SupplierDiscoveryRanker.ValidatePage(offset, limit));
        Assert.DoesNotContain("Exception", error.Message);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(40, 50)]
    public void Pages_inside_the_contract_are_accepted(int offset, int limit)
        => SupplierDiscoveryRanker.ValidatePage(offset, limit);
}
