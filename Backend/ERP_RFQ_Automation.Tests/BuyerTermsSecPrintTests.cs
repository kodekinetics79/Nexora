using System.Text;
using ERP_RFQ_Automation.Extraction.Templates;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// CP-06 / HT-04: every SEC lead showed no "Buyer requires" strip, because terms were read from
/// .docx only and an SEC print is an Ariba HTML page saved as .doc. The rows below are the SEC
/// print's own markup and wording (SE RFP C001817585.doc), trimmed to the rows that state terms.
/// </summary>
public sealed class BuyerTermsSecPrintTests
{
    private const string SecPrint = """
        <!-- class: ariba.sourcing.rfxui.PrintRFXEncode -->
        <html><head><meta content="text/html; charset=UTF-8" http-equiv="Content-Type"/></head><body>
        <table width="100%" border="0"><tr class="tableHead"><th colspan="2"><span class="sectionHead">Overview</span></th></tr>
        <tr><td align="left" class="fieldLabel">Owner</td><td width="75%" class="fieldValue">Labib A. Nogali</td></tr>
        <tr><td align="left" class="fieldLabel">Currency</td><td width="75%" class="fieldValue">Saudi Riyal</td></tr></table>
        <table width="100%" border="0"><tr class="tableHead"><th colspan="2"><span class="sectionHead">Timing Rules</span></th></tr>
        <tr><td align="left" class="fieldLabel">Publish time</td><td width="75%" class="fieldValue">9/3/2026 11:10 AM</td></tr>
        <tr><td align="left" class="fieldLabel">Due date</td><td width="75%" class="fieldValue">9/8/2026 5:00 PM</td></tr></table>
        <table width="100%" border="0"><tr class="tableHead"><th colspan="2"><span class="sectionHead">Currency Rules</span></th></tr>
        <tr><td align="left" class="fieldLabel">Allow participants to select bidding currency</td><td width="75%" class="fieldValue">Yes</td></tr></table>
        <table width="100%" border="0"><tr class="tableHead"><th colspan="3"><span class="sectionHead">Content</span></th></tr>
        <tr><th align="left" class="fieldLabel">Name</th><th align="left" class="fieldLabel">Alternative</th><th align="left" class="fieldLabel">Value</th></tr>
        <tr><td class="titleFieldValue">2 Local vendors MUST bid in SAR only</td><td class="fieldValue"></td><td class="fieldValue"></td></tr>
        <tr><td class="titleFieldValue">4.6 Quotation Validity (minimum 90 days)</td><td class="fieldValue"></td><td class="fieldValue"><a href="#413669765"></a></td></tr>
        <tr><td class="titleFieldValue">5 Header Text</td><td class="fieldValue"></td><td class="fieldValue"><a href="#413669766">
        Important points to be considered when participating in the tender:1.Ensure all documents, images, and catalogs for each item areattached clearly and precisely, without including any materials pricesin the attachments.2.Offers must be submitted for each item separately and not on thebasis of the full RFQ unless otherwise stated in the tender.3.SEC reserves the right to award the tender completely or partiallywithout needing vendor&#8217;s approval.4.Prices for item / items must be included all the applicable costs,obligations which are borne by the vendor without VAT.5.Any change in the price after bid close will be rejected.6.Quotation must be valid for 90 days from bid due date.7.For local vendor, the delivery of material must be deliver to SaudiEnergy warehouse or user location.8.For foreign vendor, the delivery of material must be as perincoterms which is selected in the bid.
        </a></td></tr>
        </table></body></html>
        """;

    private static Dictionary<string, BuyerTerms.Term> Read()
        => BuyerTerms.ReadDocument(Encoding.UTF8.GetBytes(SecPrint), "SE  RFP C001817585.doc").ToDictionary(t => t.Key);

    [Fact]
    public void An_SEC_print_saved_as_doc_states_its_terms()
    {
        var terms = Read();

        Assert.Equal("8 Sep 2026, 5:00 PM", terms["closes"].Value);
        // "MUST bid in SAR only" decides it for a local seller, whatever the event lets bidders choose.
        Assert.Equal("SAR only (local vendors)", terms["quote_currency"].Value);
        Assert.Equal("90 days after closing", terms["validity"].Value);
        Assert.Equal("Prices without VAT", terms["vat"].Value);
        Assert.Equal("SaudiEnergy warehouse or user location", terms["deliver_to"].Value);
    }

    [Fact]
    public void Each_quote_is_the_buyers_own_clause_not_the_whole_header_text()
    {
        var terms = Read();

        Assert.Equal("6.Quotation must be valid for 90 days from bid due date.", terms["validity"].Quote);
        Assert.StartsWith("4.Prices for item / items must be included", terms["vat"].Quote);
        Assert.Contains("Local vendors MUST bid in SAR only", terms["quote_currency"].Quote);
    }

    [Fact]
    public void The_minimum_validity_row_is_read_when_the_clause_is_absent()
    {
        var print = SecPrint.Replace("6.Quotation must be valid for 90 days from bid due date.", "");
        var terms = BuyerTerms.ReadDocument(Encoding.UTF8.GetBytes(print), "SE RFP.doc").ToDictionary(t => t.Key);

        Assert.Equal("At least 90 days", terms["validity"].Value);
    }

    [Theory]
    [InlineData("SE  RFP C001817585.doc", null, true)]
    [InlineData("RFP 6000000028.docx", null, true)]
    [InlineData("print.html", null, true)]
    [InlineData("stored-object", "text/html", true)]
    [InlineData("BID5500813867.PDF", "application/pdf", false)]
    [InlineData("ARAMCO Enquiry 6031.xlsx", null, false)]
    public void Word_files_and_HTML_prints_are_read(string name, string? mediaType, bool expected)
        => Assert.Equal(expected, BuyerTerms.CanRead(name, mediaType));

    [Fact]
    public void A_Word_97_binary_states_no_terms_rather_than_failing()
    {
        // OLE compound-file signature: a real .doc that is not an HTML print.
        var ole = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0 };
        Assert.Empty(BuyerTerms.ReadDocument(ole, "old.doc"));
    }
}
