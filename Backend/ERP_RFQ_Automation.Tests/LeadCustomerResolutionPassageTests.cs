using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the adapter hands the resolver as EVIDENCE, before any database is involved: which
/// text on a document is read as a statement about who is buying, which as a statement about
/// where the goods go, and which of it survives the passage budget on a 1,500-line bid list.
/// </summary>
public sealed class LeadCustomerResolutionPassageTests
{
    private static Lead LeadWith(Action<Lead> configure)
    {
        var lead = new Lead { Id = 1, BusinessUnitId = 1, CreatedBy = "test" };
        configure(lead);
        return lead;
    }

    // ── roles ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_company_name_and_the_buyer_sentence_are_header_evidence_not_ship_to()
    {
        // Lead 682: a Marafiq RFQ whose company-name field and captured sentence both name the
        // buying organisation. They are statements about WHO IS BUYING; the warehouse address
        // underneath them is a statement about where a truck goes.
        var passages = LeadCustomerResolutionService.Passages(LeadWith(lead =>
        {
            lead.CustomerCompanyNameExtracted = "MARAFIQ";
            lead.CustomerCompanyEvidence =
                "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).";
            lead.DeliveryLocation =
                "MARAFIQ Yanbu Warehouse, Power & Desalination Plant, Yanbu Al-Sinaiyah, KSA";
        }));

        Assert.Equal(
            [PassageRole.BuyerHeader, PassageRole.BuyerHeader, PassageRole.ShipTo],
            passages.Select(p => p.Role));
        // NamesTheBuyer keeps its old meaning for every tier that still reads it, so the 0.88
        // name-in-document confidence that links lead 682 is unchanged.
        Assert.All(passages, p => Assert.True(p.NamesTheBuyer));
    }

    [Fact]
    public void A_delivery_address_alone_is_a_ship_to_that_still_names_the_buyer()
    {
        // Lead 680: an SEC portal print carrying ONE usable fact. No sender, no company-name
        // field, no portal name. If this stops being buyer-naming evidence the lead stops
        // resolving at all.
        var passages = LeadCustomerResolutionService.Passages(LeadWith(lead =>
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM"));

        var passage = Assert.Single(passages);
        Assert.Equal(PassageRole.ShipTo, passage.Role);
        Assert.True(passage.NamesTheBuyer);
        Assert.Equal("Saudi Electricity Company-DAMMAM", passage.Text);
    }

    [Theory]
    [InlineData("Sold-to party", PassageRole.BuyerHeader)]
    [InlineData("SOLD_TO PARTY", PassageRole.BuyerHeader)]
    [InlineData("Soldto", PassageRole.BuyerHeader)]
    [InlineData("Bill To", PassageRole.BuyerHeader)]
    [InlineData("Ordering party", PassageRole.BuyerHeader)]
    [InlineData("Purchaser", PassageRole.BuyerHeader)]
    [InlineData("End user", PassageRole.BuyerHeader)]
    [InlineData("Purchasing organization", PassageRole.BuyerHeader)]
    [InlineData("Storage location", PassageRole.ShipTo)]
    [InlineData("Deliver to", PassageRole.ShipTo)]
    [InlineData("Consignee", PassageRole.ShipTo)]
    [InlineData("Substation", PassageRole.ShipTo)]
    // A label that speaks both vocabularies is a ship-to: reading a consignee as the buyer is
    // the expensive mistake, and a header outranks the address.
    [InlineData("Ship-to customer", PassageRole.ShipTo)]
    [InlineData("Material long text", PassageRole.ItemText)]
    [InlineData("Net price", PassageRole.ItemText)]
    [InlineData("", PassageRole.ItemText)]
    public void An_extracted_label_is_read_for_what_it_says_the_value_is(string label, PassageRole expected)
        => Assert.Equal(expected, LeadCustomerResolutionService.RoleForLabel(label));

    [Fact]
    public void A_sold_to_label_now_outranks_a_storage_location_instead_of_falling_to_item_text()
    {
        // THE DEFECT: the label test asked only whether the label mentioned a location, so an
        // SAP print's "Sold-to party" — the strongest statement on the page — was filed as item
        // text and worth 0.70, while "Storage location" linked the lead at 0.88.
        var passages = LeadCustomerResolutionService.Passages(LeadWith(lead =>
            lead.LeadItems.Add(new LeadItem
            {
                StorageLocation = "Jizan Area Store",
                ExtraFields = ExtraFieldsJson.Serialize(new Dictionary<string, string>
                {
                    ["Sold-to party"] = "Saudi Electricity Company"
                })
            })));

        var soldTo = Assert.Single(passages, p => p.Text == "Saudi Electricity Company");
        Assert.Equal(PassageRole.BuyerHeader, soldTo.Role);
        Assert.True(soldTo.NamesTheBuyer);
        Assert.Equal("sold-to party field", soldTo.Where);
        Assert.Equal(PassageRole.ShipTo, Assert.Single(passages, p => p.Text == "Jizan Area Store").Role);
        // The header is offered to the resolver before the ship-to whatever order the document
        // printed them in.
        Assert.Equal(PassageRole.BuyerHeader, passages[0].Role);
    }

    // ── the budget ────────────────────────────────────────────────────────────

    [Fact]
    public void The_buyer_named_on_line_900_survives_a_1500_line_bid_list()
    {
        // THE DEFECT: the budget was 120 passages filled item by item from the top of the
        // document. On an Aramco bid list it was exhausted around item 30, so a company name
        // printed further down was never evidence at all and whether the buyer was found
        // depended on the order the extractor happened to emit lines in.
        var lead = LeadWith(lead =>
        {
            for (var i = 0; i < 1_500; i++)
                lead.LeadItems.Add(new LeadItem
                {
                    ItemText = $"LINE {i} BALL VALVE 2IN CLASS 300",
                    MaterialPotext = $"LINE {i} LONG TEXT",
                    ExtraFields = i == 900
                        ? ExtraFieldsJson.Serialize(new Dictionary<string, string>
                        {
                            ["Ordering party"] = "Saudi Electricity Company"
                        })
                        : null,
                    StorageLocation = i == 1_400 ? "Jizan Area Store" : null
                });
        });

        var passages = LeadCustomerResolutionService.Passages(lead);

        Assert.Contains(passages, p => p.Text == "Saudi Electricity Company" && p.Role == PassageRole.BuyerHeader);
        Assert.Contains(passages, p => p.Text == "Jizan Area Store" && p.Role == PassageRole.ShipTo);
        // Still bounded, and spent in role order: every header, then every ship-to, then
        // whatever item text fits.
        Assert.Equal(120, passages.Count);
        Assert.Equal(PassageRole.BuyerHeader, passages[0].Role);
        Assert.Equal(PassageRole.ShipTo, passages[1].Role);
        Assert.All(passages.Skip(2), p => Assert.Equal(PassageRole.ItemText, p.Role));
    }

    // ── the e-mail envelope ───────────────────────────────────────────────────

    [Fact]
    public void The_name_on_the_mailbox_becomes_header_evidence_and_the_subject_item_text()
    {
        // "SEC Procurement <noreply@portal.se.com.sa>" was parsed and the only unambiguous
        // statement of the buyer in the whole message was thrown away with the display name.
        var passages = LeadCustomerResolutionService.Passages(
            LeadWith(lead => lead.DeliveryLocation = "Gate 4, Ras Tanura"),
            senderDisplayName: "SEC Procurement",
            emailSubject: "RFQ 4500123456 - spares");

        var mailbox = Assert.Single(passages, p => p.Text == "SEC Procurement");
        Assert.Equal(PassageRole.BuyerHeader, mailbox.Role);
        Assert.True(mailbox.NamesTheBuyer);

        // A subject is written by a person and as often names a third party as the sender, so
        // it is worth no more than any other incidental mention.
        var subject = Assert.Single(passages, p => p.Text == "RFQ 4500123456 - spares");
        Assert.Equal(PassageRole.ItemText, subject.Role);
        Assert.False(subject.NamesTheBuyer);
    }

    [Theory]
    [InlineData("SEC Procurement <noreply@portal.se.com.sa>", "SEC Procurement")]
    [InlineData("\"Ali Nasser\" <ali@se.com.sa>", "Ali Nasser")]
    [InlineData("ali@se.com.sa", null)]
    [InlineData("<ali@se.com.sa>", null)]
    // A client that repeats the address as the display name states nothing the parsed address
    // does not.
    [InlineData("ali@se.com.sa <ali@se.com.sa>", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_display_name_is_kept_only_when_it_is_a_name(string? raw, string? expected)
        => Assert.Equal(expected, LeadCustomerResolutionService.ParseDisplayName(raw));

    // ── the customer-name prefilter ───────────────────────────────────────────

    [Fact]
    public void Above_the_name_scan_cap_the_prefixes_come_from_the_passages_not_the_empty_name_field()
    {
        // THE DEFECT: past MaximumNameScanRows the customer query narrowed on the first two
        // characters of the company-name field. On a portal print — the exact document the
        // passage tier exists for — that field is null, so the filter collapsed to
        // already-matched-only and the tier was handed an empty customer list.
        var evidence = new LeadClientEvidence
        {
            CustomerCompanyName = null,
            Passages =
            [
                new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true),
                new DocumentPassage("item text", "AFFIX ZENITH SPECIFIED BARCODE", false)
            ]
        };

        var prefixes = LeadCustomerResolutionService.NameScanPrefixes(evidence);

        Assert.Contains("SA", prefixes);   // finds "Saudi Electricity Company"
        Assert.Contains("EL", prefixes);
        Assert.Contains("DA", prefixes);
        // Item text is not a statement about the buyer and must not widen the scan.
        Assert.DoesNotContain("ZE", prefixes);
        Assert.True(prefixes.Count <= 40);
    }

    [Fact]
    public void The_company_name_fields_own_prefix_is_still_used_so_the_filter_only_widens()
    {
        var evidence = new LeadClientEvidence
        {
            CustomerCompanyName = "3M Gulf",
            Passages = [new DocumentPassage("company named on the document", "3M Gulf", true)]
        };

        Assert.Contains("3M", LeadCustomerResolutionService.NameScanPrefixes(evidence));
    }
}
