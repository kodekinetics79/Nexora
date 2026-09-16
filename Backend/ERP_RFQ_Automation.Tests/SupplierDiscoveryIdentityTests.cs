using ERP_RFQ_Automation.Procurement.Discovery;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the internet is asked about. A customer line that names several acceptable makers, each
/// with its own part number, is searched under every one of them — either is an answer the
/// customer accepts, so a search that only knew the first would miss half the suppliers.
/// </summary>
public sealed class SupplierDiscoveryIdentityTests
{
    [Fact]
    public void A_multi_maker_line_yields_every_maker_and_every_part_number()
    {
        var identity = SupplierDiscoveryIdentity.From(
            partNumber: "LV431831",
            maker: "Schneider Electric",
            description: "CIRCUIT BREAKER MCCB 3P 250A",
            approvedMakersText: "ABB (S203-C16); SIEMENS 5SY6316-7, Eaton");

        Assert.Equal("Schneider Electric", identity.Maker);
        Assert.Equal("LV431831", identity.PartNumber);
        Assert.Equal(["Schneider Electric", "ABB", "SIEMENS", "Eaton"], identity.Makers);
        Assert.Equal(["LV431831", "S203-C16", "5SY6316-7"], identity.PartNumbers);
        Assert.Equal(["ABB (S203-C16)", "SIEMENS 5SY6316-7", "Eaton"], identity.AcceptableMakers);
        Assert.Equal("LV431831 (Schneider Electric)", identity.Subject());
        Assert.Equal("LV431831; S203-C16; 5SY6316-7; Schneider Electric; ABB; SIEMENS; Eaton", identity.Tags());
    }

    [Fact]
    public void A_short_maker_name_with_a_digit_stays_a_maker_not_a_part_number()
    {
        var (maker, parts) = SupplierDiscoveryIdentity.SplitSegment("3M 1080-G12");
        Assert.Equal("3M", maker);
        Assert.Equal(["1080-G12"], parts);
    }

    [Fact]
    public void Each_maker_is_searched_with_its_own_part_number_two_ways()
    {
        var identity = SupplierDiscoveryIdentity.From("LV431831", "Schneider Electric", "CIRCUIT BREAKER", null);
        // The product words travel with the number, so a number that also means something else
        // ("301" is a stainless steel grade) still finds the product.
        Assert.Equal(
            ["Schneider Electric LV431831 CIRCUIT BREAKER supplier", "Schneider Electric CIRCUIT BREAKER distributor Saudi Arabia"],
            identity.Queries());

        var multi = SupplierDiscoveryIdentity.From("LV431831", "Schneider Electric", "CIRCUIT BREAKER MCCB",
            "ABB (S203-C16); SIEMENS 5SY6316-7, Eaton");
        Assert.Equal(
        [
            "Schneider Electric LV431831 CIRCUIT BREAKER MCCB supplier", "Schneider Electric CIRCUIT BREAKER MCCB distributor Saudi Arabia",
            "ABB S203-C16 CIRCUIT BREAKER MCCB supplier", "ABB CIRCUIT BREAKER MCCB distributor Saudi Arabia",
            "SIEMENS 5SY6316-7 CIRCUIT BREAKER MCCB supplier", "SIEMENS CIRCUIT BREAKER MCCB distributor Saudi Arabia",
            "Eaton CIRCUIT BREAKER MCCB distributor Saudi Arabia", "Eaton CIRCUIT BREAKER MCCB supplier"
        ], multi.Queries());
        // ABB is never asked about Schneider's number: that pairing would spend a call on nothing.
        Assert.DoesNotContain(multi.Queries(), q => q.StartsWith("ABB LV431831", StringComparison.Ordinal));
    }

    [Fact]
    public void A_legal_company_name_is_searched_by_its_trading_name_and_the_description_by_its_product_words()
    {
        // Live on 2026-09-16: "JAMES NORTH AND SONS COMPANY NS 301 supplier" found a museum archive, a
        // company register and grade-301 stainless steel dealers. The trading name plus the product
        // words find glove suppliers.
        Assert.Equal("JAMES NORTH", SupplierDiscoveryIdentity.SearchName("JAMES NORTH AND SONS COMPANY"));
        Assert.Equal("ABB", SupplierDiscoveryIdentity.SearchName("ABB Ltd"));
        Assert.Equal("Schneider Electric", SupplierDiscoveryIdentity.SearchName("Schneider Electric"));
        Assert.Equal("Company", SupplierDiscoveryIdentity.SearchName("Company"));

        Assert.Equal(["GLOVES", "WORKING", "HEAT", "RESISTANT"],
            SupplierDiscoveryIdentity.ProductWordsOf("GLOVES:WORKING,HEAT RESISTANT,LARGE,LG 1"));

        var gloves = SupplierDiscoveryIdentity.From("NS 301", "JAMES NORTH AND SONS COMPANY",
            "GLOVES:WORKING,HEAT RESISTANT,LARGE,LG 1", null);
        Assert.Equal(
            ["JAMES NORTH NS 301 GLOVES WORKING HEAT RESISTANT supplier", "JAMES NORTH GLOVES WORKING HEAT RESISTANT distributor Saudi Arabia"],
            gloves.Queries());
        // What the rep is shown was searched keeps the customer's full wording.
        Assert.Equal("JAMES NORTH AND SONS COMPANY", gloves.Maker);
    }

    [Fact]
    public void Queries_stay_bounded_and_distinct_however_long_the_approved_list_is()
    {
        var many = SupplierDiscoveryIdentity.From("P1", "M1", "desc",
            "M2 P2; M3 P3; M4 P4; M5 P5; M6 P6");
        Assert.Equal(SupplierDiscoveryIdentity.MaxQueries, many.Queries().Count);
        Assert.Equal(many.Queries().Count, many.Queries().Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("M1 P1 desc supplier", many.Queries()[0]);
    }

    [Fact]
    public void Without_a_maker_the_description_carries_the_query()
    {
        var identity = SupplierDiscoveryIdentity.From("LV431831", null,
            "CIRCUIT BREAKER MCCB 3P 250A THERMAL MAGNETIC FIXED", null);
        // "3P" and "250A" are ratings, not product words; the first four product words go.
        Assert.Equal(["LV431831 CIRCUIT BREAKER MCCB THERMAL supplier Saudi Arabia"], identity.Queries());
        Assert.Equal("LV431831", identity.Subject());
    }

    [Fact]
    public void With_neither_maker_nor_part_the_description_alone_is_searched_and_nothing_at_all_is_empty()
    {
        var described = SupplierDiscoveryIdentity.From(null, null, "Stainless steel gate valve 6 inch class 150", null);
        Assert.Equal(
            ["Stainless steel gate valve supplier Saudi Arabia", "Stainless steel gate valve supplier"],
            described.Queries());
        Assert.False(described.IsEmpty);

        var blank = SupplierDiscoveryIdentity.From(null, "  ", null, null);
        Assert.True(blank.IsEmpty);
        Assert.Empty(blank.Queries());
        Assert.Null(blank.Tags());
    }

    [Fact]
    public void The_cache_key_ignores_case_spacing_and_maker_order_and_changes_with_the_part()
    {
        var a = SupplierDiscoveryIdentity.From("LV431831", "Schneider Electric", "Circuit  breaker", "ABB; Eaton");
        var b = SupplierDiscoveryIdentity.From("lv431831", "schneider electric", "circuit breaker", "Eaton; ABB");
        var c = SupplierDiscoveryIdentity.From("LV431832", "Schneider Electric", "Circuit breaker", "ABB; Eaton");

        Assert.Equal(a.Key(), b.Key());
        Assert.NotEqual(a.Key(), c.Key());
        Assert.Equal(64, a.Key().Length);
    }

    [Fact]
    public void A_free_text_query_is_searched_as_typed()
    {
        var identity = SupplierDiscoveryIdentity.FromQuery("  Schneider LV431831 ");
        Assert.Null(identity.Maker);
        Assert.Null(identity.PartNumber);
        Assert.Equal("Schneider LV431831", identity.Description);
        Assert.Equal(["Schneider LV431831 supplier Saudi Arabia", "Schneider LV431831 supplier"], identity.Queries());
    }
}
