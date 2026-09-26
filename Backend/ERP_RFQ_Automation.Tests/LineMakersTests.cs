using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Owner scenario 2026-09-17: a customer accepts several makers for one item (a 10KVA breaker
/// from ABB, GE or Eaton). The rep keeps the list on the line, suppliers of any accepted maker are
/// found, and every supplier email lists them all.
/// </summary>
public sealed class LineMakersTests
{
    [Theory]
    [InlineData("REPLACEMENT", "GE", "THQL32010", "AF96-30-00-13", null, "Offered: GE THQL32010, replaces AF96-30-00-13")]
    [InlineData("REPLACEMENT", null, "THQL32010", null, "Maker's successor", "Offered: THQL32010. Maker's successor")]
    [InlineData("EQUIVALENT", "SIEMENS", "3RT2046", "AF96", "Original discontinued", "Offered as an equivalent: SIEMENS 3RT2046, in place of AF96. Original discontinued")]
    public void The_customer_reads_one_sentence_about_what_is_offered(string kind, string? maker, string part, string? asked, string? note, string expected) =>
        Assert.Equal(expected, OfferedPartKinds.Sentence(kind, maker, part, asked, note));

    [Fact]
    public void A_line_offered_as_asked_says_nothing() =>
        Assert.Null(OfferedPartKinds.Sentence(null, null, null, "AF96", null));

    [Fact]
    public void The_rep_types_makers_like_email_addresses()
    {
        Assert.Equal(["ABB S203-C16", "GE THQL32010", "Eaton"],
            RfqLineMakersController.Normalise(" ABB   S203-C16; GE THQL32010 ;;\nEaton\neaton"));
        Assert.Empty(RfqLineMakersController.Normalise("  ;  "));
    }

    [Theory]
    [InlineData("ABB ELECTRICAL INDUSTRIES CO. LTD (SA): P/N AF96-30-00-13", "ABB")]
    [InlineData("SIEMENS AG AUTOMATION AND DRIVE (DE): P/N 3RT2046-1AN20", "SIEMENS")]
    [InlineData("SCHNEIDER ELECTRIC USA / SQUARE-D (US): P/N LC1D95M7", "SCHNEIDER")]
    [InlineData("BRIDGESTONE CORPORATION (JP) via BRIDGESTONE MIDDLE EAST & AFRICA", "BRIDGESTONE")]
    [InlineData("GENERAL ELECTRIC", "GENERAL ELECTRIC")]
    [InlineData("GE THQL32010", "GE")]
    [InlineData("Eaton", "Eaton")]
    public void The_brand_is_what_a_supplier_would_be_tagged_with(string segment, string brand) =>
        Assert.Equal(brand, ERP_RFQ_Automation.Procurement.Discovery.SupplierDiscoveryIdentity.BrandOf(segment));

    [Theory]
    [InlineData("GE; Eaton", "GE", true)]
    [InlineData("Gears and pulleys", "GE", false)]
    [InlineData("General Electric (GE)", "GE", true)]
    [InlineData("EATON-authorised", "Eaton", true)]
    public void A_short_maker_name_matches_whole_words_only(string text, string maker, bool expected) =>
        Assert.Equal(expected, ProcurementApplicationService.ContainsWord(text, maker));

    [Fact]
    public async Task A_supplier_carrying_any_accepted_maker_is_a_candidate()
    {
        using var fixture = new ProcurementScenario();
        long eatonId = 96_777, gearsId = 96_778;
        await using (var setup = fixture.Context())
        {
            var line = await setup.Rfqitems.SingleAsync(x => x.Id == fixture.RfqItemId);
            line.ManufacturerName = "ABB";
            line.ManufacturerPartNumber = "S203-C16";
            line.ExtraFields = """{"Approved manufacturers": "GE THQL32010; Eaton"}""";
            var eaton = AgentSeed.Supplier(setup, eatonId, fixture.BusinessUnitId, "Gulf Power Trading", "sales@gulfpower.example");
            eaton.Tags = "Eaton; breakers";
            var gears = AgentSeed.Supplier(setup, gearsId, fixture.BusinessUnitId, "Gears and Pulleys Co", "sales@gears.example");
            gears.Tags = "Gears";
            await setup.SaveChangesAsync();
        }

        var created = await fixture.Execute(service => service.CreateOrOpenSourcingCaseAsync(new CreateSourcingCaseCommand(
            fixture.BusinessUnitId, fixture.RfqId, fixture.RfqItemId, 10, false, "makers", "qa", "corr-makers")));

        var eatonCandidate = Assert.Single(created.Candidates, x => x.SupplierId == eatonId);
        Assert.Equal("Carries Eaton", eatonCandidate.RecommendationReason);
        Assert.DoesNotContain(created.Candidates, x => x.SupplierId == gearsId);
    }
}
