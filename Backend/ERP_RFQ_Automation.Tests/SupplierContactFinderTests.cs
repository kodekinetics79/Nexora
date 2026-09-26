using ERP_RFQ_Automation.Procurement.Discovery;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Which address a found company is asked at. The search engine answers "acme.com contact email
/// sales" with the contact page, the company's own posts and a few directory listings; the finder
/// keeps the sales desk on the company's own domain and nothing else.
/// </summary>
public sealed class SupplierContactFinderTests
{
    [Fact]
    public void The_sales_desk_on_the_suppliers_own_domain_wins_over_named_people_and_other_domains()
    {
        var results = new[]
        {
            new WebSearchResult("IMS Supply | LinkedIn", "https://linkedin.com/company/ims", "Contact: contact@imssupply.com, +1 888 437 6645"),
            new WebSearchResult("IMS Supply expands", "https://www.imssupply.com/blog/x", "Reach abehaine@imssupply.com or sales@imssupply.com"),
            new WebSearchResult("Aeroleads", "https://aeroleads.com/company/ims", "Email formats: jdoe@imssupply.com, first.last@imssupply.com, flast@imssupply.com"),
            new WebSearchResult("Findtheneedle", "https://www.findtheneedle.co.uk/companies/ims", "contactus@findtheneedle.co.uk"),
        };
        Assert.Equal("sales@imssupply.com", SupplierContactFinder.PickEmail("imssupply.com", results));
    }

    [Fact]
    public void A_placeholder_a_no_reply_box_and_another_companys_address_are_never_chosen()
    {
        var results = new[]
        {
            new WebSearchResult("Directory", "https://leadiq.com/c/acme", "FLast@acme.com, John.Doe@acme.com"),
            new WebSearchResult("Acme", "https://acme.com/privacy", "noreply@acme.com · privacy@acme.com · careers@acme.com"),
            new WebSearchResult("Reseller", "https://other-shop.com/acme", "sales@other-shop.com"),
        };
        Assert.Null(SupplierContactFinder.PickEmail("acme.com", results));
    }

    [Fact]
    public void A_sibling_country_domain_counts_and_a_named_person_is_kept_when_nothing_better_exists()
    {
        var uk = new[]
        {
            new WebSearchResult("Cell Pack Solutions", "https://cellpacksolutions.co.uk/company/", "info@cellpacksolutions.co.uk"),
        };
        Assert.Equal("info@cellpacksolutions.co.uk", SupplierContactFinder.PickEmail("cellpacksolutions.com", uk));

        var person = new[]
        {
            new WebSearchResult("Daralfursan team", "https://daralfursan.com/team", "Ask for Ahmed: a.saleh@daralfursan.com"),
        };
        Assert.Equal("a.saleh@daralfursan.com", SupplierContactFinder.PickEmail("daralfursan.com", person));
        Assert.Null(SupplierContactFinder.PickEmail("daralfursan.com", []));
    }

    [Theory]
    [InlineData("sales", 0)]
    [InlineData("Sales-Gulf", 0)]
    [InlineData("rfq", 1)]
    [InlineData("quotes", 2)]
    [InlineData("enquiries", 3)]
    [InlineData("info", 4)]
    [InlineData("contact", 5)]
    [InlineData("export", 6)]
    [InlineData("orders", 7)]
    [InlineData("support", 8)]
    [InlineData("jill.ledger", 9)]
    [InlineData("purchasing", 10)]
    public void Enquiry_desks_rank_ahead_of_people_and_buyers(string local, int rank)
        => Assert.Equal(rank, SupplierContactFinder.Rank(local));

    [Fact]
    public void The_lookup_asks_the_search_engine_one_plain_question_per_domain()
        => Assert.Equal("imssupply.com contact email sales", SupplierContactFinder.Query("imssupply.com"));
}
