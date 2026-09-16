using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement.Discovery;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Turning a search engine's three strings into a hit the rep can read: which company, what kind
/// (maker / appointed distributor / anyone else), where, and why it is on the list.
/// </summary>
public sealed class SupplierDiscoveryClassifierTests
{
    private static readonly string[] Makers = ["Schneider Electric", "ABB"];
    private static readonly string[] Parts = ["LV431831"];

    [Theory]
    [InlineData("https://www.gulfswitchgear.com/products/lv431831", "gulfswitchgear.com")]
    [InlineData("http://shop.acme.com.sa/a/b", "acme.com.sa")]
    [InlineData("https://www.se.com/sa/en/product/LV431831/", "se.com")]
    [InlineData("gulfswitchgear.com", "gulfswitchgear.com")]
    [InlineData("https://distributor.co.uk/x", "distributor.co.uk")]
    [InlineData("https://www.protective.co.zw/gloves", "protective.co.zw")]
    [InlineData("https://shop.acme.com.ng/", "acme.com.ng")]
    public void The_registrable_domain_drops_www_and_subdomains_and_keeps_country_second_levels(string url, string expected)
        => Assert.Equal(expected, SupplierDiscoveryClassifier.RegistrableDomain(url));

    [Theory]
    [InlineData("")]
    [InlineData("not a url at all with spaces")]
    [InlineData("https://localhost/x")]
    public void A_url_with_no_usable_host_has_no_domain(string url)
        => Assert.Null(SupplierDiscoveryClassifier.RegistrableDomain(url));

    [Theory]
    [InlineData("https://www.alibaba.com/product-detail/LV431831")]
    [InlineData("https://sa.aliexpress.com/item/1.html")]
    [InlineData("https://www.amazon.sa/dp/B0")]
    [InlineData("https://www.ebay.com/itm/1")]
    [InlineData("https://www.made-in-china.com/x")]
    [InlineData("https://dir.indiamart.com/x")]
    [InlineData("https://www.tradekey.com/x")]
    [InlineData("https://www.globalsources.com/x")]
    [InlineData("https://www.linkedin.com/company/x")]
    [InlineData("https://www.facebook.com/x")]
    [InlineData("https://www.youtube.com/watch?v=1")]
    [InlineData("https://en.wikipedia.org/wiki/Circuit_breaker")]
    [InlineData("https://www.manualslib.com/x.pdf")]
    public void Marketplaces_social_networks_and_pdf_hosts_are_dropped(string url)
    {
        var hit = SupplierDiscoveryClassifier.Classify(new WebSearchResult("Anything", url, "LV431831"), Makers, Parts, 0);
        Assert.Null(hit);
    }

    [Theory]
    [InlineData("https://www.gracesguide.co.uk/James_North_and_Sons", "James North and Sons - Graces Guide")]
    [InlineData("https://collection.sciencemuseumgroup.org.uk/people/cp62468", "James North and Sons Limited | Science Museum Group Collection")]
    [InlineData("https://find-and-update.company-information.service.gov.uk/company/022", "JAMES NORTH FOOTWEAR LIMITED overview - GOV.UK")]
    [InlineData("https://www.freepatentsonline.com/3079274.html", "Production of articles of protective clothing - NORTH & SONS LTD JAMES")]
    [InlineData("https://www.nsnlookup.com/cage/ea052", "James North & Sons Nz Ltd (CAGE Code EA052)")]
    [InlineData("https://www.listofcompaniesin.com/James_North_Zimbabwe_Company_2919748", "james North Zimbabwe, Workplace Safety Supplies")]
    [InlineData("https://some-register.example/x", "ACME Gloves — List of Companies in Riyadh")]
    public void Registers_directories_archives_and_patent_pages_are_dropped_even_when_they_name_the_maker(string url, string title)
    {
        var hit = SupplierDiscoveryClassifier.Classify(new WebSearchResult(title, url, "James North gloves"),
            ["JAMES NORTH AND SONS COMPANY"], ["NS 301"], 0, ["GLOVES", "WORKING", "HEAT", "RESISTANT"]);
        Assert.Null(hit);
    }

    [Fact]
    public void A_page_about_a_different_meaning_of_the_same_characters_is_dropped()
    {
        // "NS 301" gloves: grade-301 stainless steel dealers came up and mention none of the maker,
        // the number as one token, or a product word.
        var steel = SupplierDiscoveryClassifier.Classify(new WebSearchResult(
            "Stainless Steel 301 Pipes Manufacturer and Supplier in Riyadh, Saudi Arabia",
            "https://mirzapipes.com/ss-301", "SS 301 pipes and tubes stockist in Riyadh"),
            ["JAMES NORTH AND SONS COMPANY"], ["NS 301"], 0, ["GLOVES", "WORKING", "HEAT", "RESISTANT"]);
        Assert.Null(steel);

        var gloves = SupplierDiscoveryClassifier.Classify(new WebSearchResult(
            "7564 – Volcano Heat Resistant Kevlar Gloves 32cm – North Safety",
            "https://northsafetysupplies.com/product/7564", "Heat resistant glove for foundry work"),
            ["JAMES NORTH AND SONS COMPANY"], ["NS 301"], 1, ["GLOVES", "WORKING", "HEAT", "RESISTANT"]);
        Assert.NotNull(gloves);
        Assert.Equal("North Safety", gloves!.Name);

        var byNumber = SupplierDiscoveryClassifier.Classify(new WebSearchResult(
            "NS301 in stock", "https://ppe-trader.example/ns301", "Ships worldwide"),
            ["JAMES NORTH AND SONS COMPANY"], ["NS 301"], 2, ["GLOVES", "WORKING", "HEAT", "RESISTANT"]);
        Assert.NotNull(byNumber);

        // Nothing to judge against: every page is relevant.
        Assert.True(SupplierDiscoveryClassifier.IsRelevant("x.com", "anything", "", [], [], []));
    }

    [Theory]
    [InlineData("North Safety Products : Quotes, Address, Contact", "azomining.com", "North Safety Products")]
    [InlineData("Welcome to WebPortunities.net", "webportunities.net", "Webportunities")]
    [InlineData("DATA SHEET", "treadstoneproducts.com", "Treadstoneproducts")]
    [InlineData("Heat gloves | Saf-T-Glove", "saf-t-glove.com", "Saf-T-Glove")]
    [InlineData("", "saf-t-glove.com", "Saf T Glove")]
    [InlineData("301 Heat Resistant Gloves, Z-Flex® Aluminized Back, Zetex Texturized Fiberglass Palm", "govets.com", "Govets")]
    [InlineData("TERK500 XTREM HEAT Level E Cut Resistant Thermal Gloves | 360mm JODAH", "jodah.com.sa", "Jodah")]
    [InlineData("POLAT 301 – CUT AND HEAT RESISTANT GLOVE (LEVEL 4)", "polatprotect.com", "Polatprotect")]
    [InlineData("North Gloves Industry - Work Wear, Leather Accessories and Gloves Manufacturer", "northgloves.com", "North Gloves Industry")]
    [InlineData("Hot mill gloves | welding gloves manufacturer", "nassguard.sa", "Nassguard")]
    [InlineData("Heat gloves - Industrial & Heavy Duty Gloves Supplier", "astrosaudi.com", "Astrosaudi")]
    [InlineData("Kevlar gloves | Ready Stock", "areejintl.com", "Areejintl")]
    [InlineData("Heat gloves | Gulf Switchgear Trading Co.", "gulfswitchgear.com", "Gulf Switchgear Trading Co.")]
    public void A_title_that_is_the_pages_function_or_a_product_line_is_not_a_company_name(string title, string domain, string expected)
        => Assert.Equal(expected, SupplierDiscoveryClassifier.CompanyName(title, domain));

    [Fact]
    public void An_article_about_buying_the_product_is_dropped_and_a_self_described_maker_ranks_as_manufacturer()
    {
        var words = new[] { "GLOVES", "WORKING", "HEAT", "RESISTANT" };
        var article = SupplierDiscoveryClassifier.Classify(new WebSearchResult(
            "How to Find a Reliable Safety Gloves Supplier Saudi Arabia", "https://nosgroups.com/blog/x", "Tips for buyers"),
            [], [], 0, words);
        Assert.Null(article);

        var factory = SupplierDiscoveryClassifier.Classify(new WebSearchResult(
            "North Gloves Industry - Work Wear, Leather Accessories and Gloves Manufacturer",
            "https://northgloves.com/", "We manufacture leather work gloves in Sialkot"),
            ["JAMES NORTH AND SONS COMPANY"], ["NS 301"], 1, words);
        Assert.NotNull(factory);
        Assert.Equal(SupplierRoles.Manufacturer, factory!.Role);
        Assert.Equal("Describes itself as a manufacturer", factory.Why);
    }

    [Fact]
    public void A_real_supplier_page_is_kept()
    {
        var hit = SupplierDiscoveryClassifier.Classify(new WebSearchResult(
            "Schneider LV431831 | Gulf Switchgear Trading Co.",
            "https://www.gulfswitchgear.com/products/lv431831",
            "Authorised Schneider Electric distributor in Dammam. LV431831 in stock. Contact sales@gulfswitchgear.com"),
            Makers, Parts, 3);

        Assert.NotNull(hit);
        Assert.Equal("gulfswitchgear.com", hit!.Domain);
        Assert.Equal("https://www.gulfswitchgear.com", hit.Website);
        Assert.Equal("Gulf Switchgear Trading Co.", hit.Name);
        Assert.Equal(SupplierRoles.Distributor, hit.Role);
        Assert.Equal("SA", hit.Country);
        Assert.Equal("Lists Schneider Electric LV431831 on its page", hit.Why);
        Assert.Equal("sales@gulfswitchgear.com", hit.ContactEmail);
        Assert.Equal(3, hit.ProviderOrder);
        Assert.Equal(SupplierDiscoveryClassifier.HitId("gulfswitchgear.com"), hit.Id);
        Assert.Equal(40, hit.Id.Length);
    }

    [Fact]
    public void The_makers_own_domain_is_the_manufacturer()
    {
        Assert.Equal(SupplierRoles.Manufacturer, SupplierDiscoveryClassifier.Role(
            "schneider-electric.com", "LV431831 - Compact NSX250", "Product datasheet", Makers));
        Assert.Equal(SupplierRoles.Manufacturer, SupplierDiscoveryClassifier.Role(
            "abb.com", "Miniature circuit breakers", "ABB offers", Makers));
    }

    [Fact]
    public void A_page_that_says_it_is_a_distributor_is_a_distributor_even_when_its_title_names_the_maker()
    {
        Assert.Equal(SupplierRoles.Distributor, SupplierDiscoveryClassifier.Role(
            "gulfswitchgear.com", "Schneider Electric LV431831 | Gulf Switchgear",
            "We are an authorized distributor of Schneider Electric products.", Makers));
        Assert.Equal(SupplierRoles.Distributor, SupplierDiscoveryClassifier.Role(
            "stockists.co.uk", "ABB stockist - Fast delivery", "Stock of ABB breakers", Makers));
        Assert.Equal(SupplierRoles.Distributor, SupplierDiscoveryClassifier.Role(
            "partners.example", "Channel partner for ABB", "…", Makers));
    }

    [Fact]
    public void A_page_that_introduces_itself_as_the_maker_is_the_manufacturer_and_anyone_else_is_a_reseller()
    {
        Assert.Equal(SupplierRoles.Manufacturer, SupplierDiscoveryClassifier.Role(
            "se.com", "Schneider Electric Global | Homepage", "Life is On", Makers));
        Assert.Equal(SupplierRoles.Reseller, SupplierDiscoveryClassifier.Role(
            "cheapbreakers.example", "LV431831 best price", "Buy LV431831 online", Makers));
    }

    [Theory]
    [InlineData("acme.com.sa", "Industrial parts", "SA")]
    [InlineData("acme.ae", "Industrial parts", "AE")]
    [InlineData("acme.qa", "Industrial parts", "QA")]
    [InlineData("acme.com", "Serving Riyadh and Jubail since 1990", "SA")]
    [InlineData("acme.com", "Head office in Saudi Arabia", "SA")]
    [InlineData("acme.com", "Head office in Hamburg", null)]
    public void Country_comes_from_the_tld_or_a_saudi_place_name(string domain, string text, string? expected)
        => Assert.Equal(expected, SupplierDiscoveryClassifier.Country(domain, text));

    [Fact]
    public void The_first_email_in_the_excerpt_is_offered_as_the_contact_and_file_names_are_not()
    {
        Assert.Equal("sales@acme.com", SupplierDiscoveryClassifier.FirstEmail("Write to Sales@Acme.com. or info@acme.com"));
        Assert.Null(SupplierDiscoveryClassifier.FirstEmail("See logo@2x.png for the image"));
        Assert.Null(SupplierDiscoveryClassifier.FirstEmail("No address here"));
    }

    [Fact]
    public void Why_is_one_sentence_in_the_reps_words()
    {
        Assert.Equal("Lists LV431831 on its page",
            SupplierDiscoveryClassifier.Why(SupplierRoles.Reseller, "LV431831 in stock", [], Parts));
        Assert.Equal("Describes itself as a ABB distributor",
            SupplierDiscoveryClassifier.Why(SupplierRoles.Distributor, "Authorised ABB distributor", Makers, Parts));
        Assert.Equal("Schneider Electric's own website",
            SupplierDiscoveryClassifier.Why(SupplierRoles.Manufacturer, "Schneider Electric Compact NSX", Makers, Parts));
        Assert.Equal("Came up in the internet search for this part",
            SupplierDiscoveryClassifier.Why(SupplierRoles.Reseller, "", Makers, Parts));
        var clipped = SupplierDiscoveryClassifier.Why(SupplierRoles.Reseller, new string('x', 300), [], []);
        Assert.True(clipped.Length <= 140);
        Assert.EndsWith("…", clipped);
    }

    [Theory]
    [InlineData("Schneider LV431831 | Gulf Switchgear Trading Co.", "Gulf Switchgear Trading Co.")]
    [InlineData("LV431831 - Compact NSX250 - Schneider Electric", "Schneider Electric")]
    [InlineData("Al Rajhi Electric", "Al Rajhi Electric")]
    [InlineData("Gulf Switchgear | Home", "Gulf Switchgear")]
    [InlineData("", "Acme")]
    public void The_company_name_is_the_titles_own_name_segment_or_the_domain(string title, string expected)
        => Assert.Equal(expected, SupplierDiscoveryClassifier.CompanyName(title, "acme.com"));
}
