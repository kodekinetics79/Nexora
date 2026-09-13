using ERP_RFQ_Automation.CommercialRouting;
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
    [InlineData("Purchasing organization", PassageRole.BuyerHeader)]
    // A person label that names the organisation is about the organisation.
    [InlineData("Buyer organisation", PassageRole.BuyerHeader)]
    [InlineData("Buyer company", PassageRole.BuyerHeader)]
    [InlineData("Storage location", PassageRole.ShipTo)]
    [InlineData("Deliver to", PassageRole.ShipTo)]
    [InlineData("Consignee", PassageRole.ShipTo)]
    [InlineData("Substation", PassageRole.ShipTo)]
    // A label that speaks both vocabularies is a ship-to: reading a consignee as the buyer is
    // the expensive mistake, and a header outranks the address.
    [InlineData("Ship-to customer", PassageRole.ShipTo)]
    // #10. On an EPC contractor's requisition these name the project owner, not the buyer. This
    // row read "End user" as a header, which enshrined the defect: a header skips the consignee
    // check, so "End User: Saudi Aramco" on a hdec.com requisition linked Aramco at 0.88.
    [InlineData("End user", PassageRole.ShipTo)]
    [InlineData("END_USER", PassageRole.ShipTo)]
    [InlineData("Client", PassageRole.ShipTo)]
    [InlineData("Customer", PassageRole.ShipTo)]
    [InlineData("Project owner", PassageRole.ShipTo)]
    // #4. Columns of people. A person's surname is very often a one-word trade name here.
    [InlineData("Requisitioner", PassageRole.ItemText)]
    [InlineData("Buyer", PassageRole.ItemText)]
    [InlineData("Buyer name", PassageRole.ItemText)]
    [InlineData("Purchasing group", PassageRole.ItemText)]
    [InlineData("Contact person", PassageRole.ItemText)]
    [InlineData("Requested by", PassageRole.ItemText)]
    // A person inside an organisation or ship-to label is still a person.
    [InlineData("Customer contact", PassageRole.ItemText)]
    [InlineData("End user contact", PassageRole.ItemText)]
    // Parties that are by definition not the buyer, and values that are codes, not names.
    [InlineData("Manufacturer company", PassageRole.ItemText)]
    [InlineData("Vendor company", PassageRole.ItemText)]
    [InlineData("Customer material number", PassageRole.ItemText)]
    [InlineData("Sold-to party no.", PassageRole.ItemText)]
    [InlineData("Company code", PassageRole.ItemText)]
    [InlineData("Material long text", PassageRole.ItemText)]
    [InlineData("Net price", PassageRole.ItemText)]
    // A company, account, organisation or owner column names whoever the spreadsheet was about: the
    // maker on a BOQ, the site owner on an EPC requisition, the account a rep keeps. Read as a header
    // it skipped the consignee check and pushed SEC's own address aside for Saudi Cable Company.
    [InlineData("Company", PassageRole.ItemText)]
    [InlineData("Company Name", PassageRole.ItemText)]
    [InlineData("Owner company", PassageRole.ItemText)]
    [InlineData("Account name", PassageRole.ItemText)]
    [InlineData("Organisation", PassageRole.ItemText)]
    [InlineData("Make / Company", PassageRole.ItemText)]
    [InlineData("Brand company", PassageRole.ItemText)]
    [InlineData("OEM", PassageRole.ItemText)]
    [InlineData("Mfr company", PassageRole.ItemText)]
    [InlineData("", PassageRole.ItemText)]
    public void An_extracted_label_is_read_for_what_it_says_the_value_is(string label, PassageRole expected)
        => Assert.Equal(expected, LeadCustomerResolutionService.RoleForLabel(label));

    [Fact]
    public void A_requisitioners_surname_that_is_a_trade_name_no_longer_stalls_an_sec_print_it_does_not_name()
    {
        // #4, whole. Lead 680's shape plus one spreadsheet column: "Requisitioner" holding
        // "Ahmed Al-Ghamdi". Read as a header, GHAMDI (the one-word key of "Al-Ghamdi Trading Est")
        // joined Saudi Electricity Company in the linking set and the lead went AMBIGUOUS, which a
        // person then had to decide. A person's column is a mention: SEC links on its address and
        // Al-Ghamdi Trading is offered, never linked.
        const long sec = 1, alGhamdi = 2;
        var lead = LeadWith(lead =>
        {
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            lead.LeadItems.Add(new LeadItem
            {
                ItemText = "BALL VALVE 2IN CLASS 300",
                ExtraFields = ExtraFieldsJson.Serialize(new Dictionary<string, string>
                {
                    ["Requisitioner"] = "Ahmed Al-Ghamdi"
                })
            });
        });
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 1,
            Passages = LeadCustomerResolutionService.Passages(lead)
        };
        var corpus = new ClientResolutionCorpus
        {
            Customers = [new CustomerNameSnapshot(sec, "Saudi Electricity Company"),
                         new CustomerNameSnapshot(alGhamdi, "Al-Ghamdi Trading Est")]
        };

        Assert.Equal(PassageRole.ItemText, Assert.Single(evidence.Passages, p => p.Text == "Ahmed Al-Ghamdi").Role);
        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, new CustomerResolutionPolicy());

        Assert.Equal(sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(0.88m, outcome.Confidence);
    }

    [Theory]
    [InlineData("End User")]
    [InlineData("Client")]
    [InlineData("Customer")]
    [InlineData("Project Owner")]
    public void An_epc_contractors_owner_column_cannot_bypass_the_consignee_check(string label)
    {
        // #10, whole. Hyundai E&C mails from hdec.com about an Aramco site and prints the owner on
        // every line. Hyundai is on the books with an address at hdec.com, so the page's sender
        // speaks against Aramco: without the column the lead is offered, never linked. With the
        // column read as a header it linked Aramco at 0.88, because a header never asks whether the
        // page names somebody else. As a ship-to it is asked, like the delivery address beside it.
        const long aramco = 1, hyundai = 2;
        var policy = new CustomerResolutionPolicy();
        var lead = LeadWith(lead =>
        {
            lead.DeliveryLocation = "Saudi Aramco Ras Tanura Refinery";
            lead.LeadItems.Add(new LeadItem
            {
                ItemText = "GATE VALVE 4IN CLASS 600",
                ExtraFields = ExtraFieldsJson.Serialize(new Dictionary<string, string> { [label] = "Saudi Aramco" })
            });
        });
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 1,
            SenderEmail = "procurement@hdec.com",
            Passages = LeadCustomerResolutionService.Passages(lead)
        };
        var corpus = new ClientResolutionCorpus
        {
            Customers = [new CustomerNameSnapshot(aramco, "Saudi Aramco"),
                         new CustomerNameSnapshot(hyundai, "Hyundai Engineering & Construction")],
            Identifiers = [new CustomerIdentifierSnapshot(1, hyundai, CustomerIdentifierType.Email,
                "k.lee@hdec.com", true, 1m, "CustomerProfile")]
        };

        Assert.Equal(PassageRole.ShipTo, Assert.Single(evidence.Passages, p => p.Text == "Saudi Aramco").Role);
        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        var offered = Assert.Single(outcome.Candidates, c => c.CustomerId == aramco);
        Assert.True(offered.Confidence < policy.NameInAddressConfidence,
            $"Aramco was offered at {offered.Confidence}, which is link strength.");
    }

    [Theory]
    [InlineData("Company")]
    [InlineData("Company Name")]
    [InlineData("Make / Company")]
    [InlineData("Brand Company")]
    [InlineData("Account Name")]
    public void A_maker_or_account_column_does_not_take_an_sec_print_away_from_sec(string label)
    {
        // THE DEFECT, whole: lead 680's shape plus one line column naming the cable maker. Any label
        // containing "company" or "account name" was a header, a header links and speaks against the
        // address, so the lead linked to Saudi Cable Company at 0.88 where it had linked SEC. Exact
        // "Company" headings are mapped to BuyerName upstream, but compound headings and every LLM or
        // PDF extraction reach ExtraFields as written.
        const long sec = 1, saudiCable = 6;
        var lead = LeadWith(lead =>
        {
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            lead.LeadItems.Add(new LeadItem
            {
                ItemText = "CABLE XLPE 1C 630MM2",
                ExtraFields = ExtraFieldsJson.Serialize(new Dictionary<string, string> { [label] = "Saudi Cable Company" })
            });
        });
        var evidence = new LeadClientEvidence { BusinessUnitId = 1, LeadId = 1, Passages = LeadCustomerResolutionService.Passages(lead) };
        var corpus = new ClientResolutionCorpus
        {
            Customers = [new CustomerNameSnapshot(sec, "Saudi Electricity Company"),
                         new CustomerNameSnapshot(saudiCable, "Saudi Cable Company")]
        };

        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, new CustomerResolutionPolicy());

        Assert.Equal(sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(0.88m, outcome.Confidence);
    }

    [Theory]
    [InlineData("Company")]
    [InlineData("Owner Company")]
    [InlineData("Account name")]
    public void An_epc_requisitions_company_column_cannot_bypass_the_consignee_check(string label)
    {
        // In Saudi Aramco contract documents COMPANY is the defined term for Aramco and CONTRACTOR for
        // the EPC, so a Hyundai requisition prints "Company: Saudi Aramco" on the line. As a header the
        // cell never asked whether the page names somebody else, and linked Aramco at 0.88 although
        // Hyundai is on the books with a contact at the sending domain.
        const long aramco = 1, hyundai = 2;
        var policy = new CustomerResolutionPolicy();
        var lead = LeadWith(lead =>
        {
            lead.DeliveryLocation = "Saudi Aramco Ras Tanura Refinery";
            lead.LeadItems.Add(new LeadItem
            {
                ItemText = "GATE VALVE 4IN CLASS 600",
                ExtraFields = ExtraFieldsJson.Serialize(new Dictionary<string, string> { [label] = "Saudi Aramco" })
            });
        });
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 1,
            SenderEmail = "procurement@hdec.com",
            Passages = LeadCustomerResolutionService.Passages(lead)
        };
        var corpus = new ClientResolutionCorpus
        {
            Customers = [new CustomerNameSnapshot(aramco, "Saudi Aramco"),
                         new CustomerNameSnapshot(hyundai, "Hyundai Engineering & Construction")],
            Contacts = [new CustomerContactSnapshot(1, hyundai, "k.lee@hdec.com", "K", "Lee")]
        };

        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(hyundai, outcome.Candidates[0].CustomerId);
        Assert.All(outcome.Candidates, c => Assert.True(c.Confidence < policy.MinimumAutoLinkConfidence,
            $"{c.CustomerName} was offered at {c.Confidence}, which is link strength."));
    }

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
    public void The_name_a_portal_relay_writes_becomes_header_evidence_and_the_subject_item_text()
    {
        // "Saudi Aramco <ordersender-prod@ansmtp.ariba.com>" was parsed and the only unambiguous
        // statement of the buyer in the whole message was thrown away with the display name.
        // What is true now: on a relay the name is a header, because the relay's own domain names
        // only the postman and it writes the buyer organisation there. The role is taken from
        // SenderDisplayName, exactly as the service takes it.
        //
        // This test used "SEC Procurement <noreply@portal.se.com.sa>" and asserted a header on the
        // strength of the no-reply mailbox alone. That enshrined #9: a no-reply mailbox very often
        // carries a person's name (SharePoint, OneDrive), so it is no longer enough.
        var (displayName, role) = LeadCustomerResolutionService.SenderDisplayName(
            "Saudi Aramco <ordersender-prod@ansmtp.ariba.com>", []);
        var passages = LeadCustomerResolutionService.Passages(
            LeadWith(lead => lead.DeliveryLocation = "Gate 4, Ras Tanura"),
            senderDisplayName: displayName,
            emailSubject: "RFQ 4500123456 - spares",
            senderDisplayNameRole: role);

        var mailbox = Assert.Single(passages, p => p.Text == "Saudi Aramco");
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

    [Theory]
    // A relay's own domain names only the postman, so it writes the buyer into the display name.
    [InlineData("Saudi Aramco <ordersender-prod@ansmtp.ariba.com>", "Saudi Aramco", PassageRole.BuyerHeader)]
    [InlineData("SEC Procurement <ordersender-prod@ansmtp.ariba.com>", "SEC Procurement", PassageRole.BuyerHeader)]
    // #9. These two rows read BuyerHeader on the strength of a no-reply mailbox alone, which
    // enshrined the defect: a no-reply mailbox is not a relay, and the name on it is as often a
    // person's or a software brand's. The organisation's own domain is still evidence on its tier.
    [InlineData("SEC Procurement <noreply@portal.se.com.sa>", "SEC Procurement", PassageRole.ItemText)]
    [InlineData("Marafiq Tenders <do-not-reply@marafiq.com.sa>", "Marafiq Tenders", PassageRole.ItemText)]
    // #9. A person's name on a no-reply or relay mailbox is still a person's name.
    [InlineData("Rashid Al-Otaibi <no-reply@sharepointonline.com>", "Rashid Al-Otaibi", PassageRole.ItemText)]
    [InlineData("Rashid Al-Otaibi via Coupa <do_not_reply@coupahost.com>", "Rashid Al-Otaibi via Coupa", PassageRole.ItemText)]
    [InlineData("Rashid Al-Otaibi <ordersender-prod@ansmtp.ariba.com>", "Rashid Al-Otaibi", PassageRole.ItemText)]
    [InlineData("Microsoft Teams <noreply@email.teams.microsoft.com>", "Microsoft Teams", PassageRole.ItemText)]
    // A person's own mailbox: the name is kept as a mention, never as a statement about the buyer.
    [InlineData("\"Rashid Al-Otaibi\" <r.otaibi@se.com.sa>", "Rashid Al-Otaibi", PassageRole.ItemText)]
    // A rep forwarding a bid from the tenant's own domain: the name on it is ours.
    [InlineData("Ali Zaid Sales <sales@alquraishi.example>", null, PassageRole.ItemText)]
    // ...and from a host under it, which the old exact-domain comparison let through as a mention.
    [InlineData("Ahmed Karim <ahmed@ksa.alquraishi.example>", null, PassageRole.ItemText)]
    // Nexora's own ingestion label is plumbing, whatever it is called.
    [InlineData("Manual Upload <manual@upload.com>", null, PassageRole.ItemText)]
    [InlineData("ali@se.com.sa", null, PassageRole.ItemText)]
    [InlineData(null, null, PassageRole.ItemText)]
    public void The_mailbox_name_is_a_header_only_where_it_cannot_be_a_persons_name(
        string? from, string? expectedName, PassageRole expectedRole)
    {
        var (name, role) = LeadCustomerResolutionService.SenderDisplayName(from, ["rfq@alquraishi.example"]);

        Assert.Equal(expectedName, name);
        Assert.Equal(expectedRole, role);
    }

    [Theory]
    // The organisation writing, with its buying-function words taken off.
    [InlineData("Hyundai E&C Procurement <procurement@hdec.com>", "Hyundai E&C")]
    [InlineData("\"SEC Procurement\" <procurement@se.com.sa>", "SEC")]
    // A person's name signs a person, not an organisation.
    [InlineData("Rashid Al-Otaibi <r.otaibi@hdec.com>", null)]
    // Nothing left once the function is taken off.
    [InlineData("Procurement <procurement@sabic.com>", null)]
    // A relay's display name is already a header; a consumer mailbox names a person; ours names us.
    [InlineData("Saudi Aramco <ordersender-prod@ansmtp.ariba.com>", null)]
    [InlineData("Hyundai E&C <hyundai.ksa@gmail.com>", null)]
    [InlineData("SEC Accounts Desk <ahmed@alquraishi.com.sa>", null)]
    [InlineData("procurement@hdec.com", null)]
    [InlineData(null, null)]
    public void The_organisation_a_mailbox_is_signed_with_is_read_only_off_an_organisations_own_domain(
        string? from, string? expected)
        => Assert.Equal(expected, LeadCustomerResolutionService.SenderOrganisationName(
            from, ["rfq@alquraishi.com"], ["ALI ZAID AL-QURAISHI & PARTNERS"]));

    [Fact]
    public void A_colleague_with_no_login_on_a_domain_that_spells_our_name_puts_no_name_on_the_page()
    {
        // THE SEAM: the resolver's Guard treats a domain whose name spells the tenant's as ours, and
        // this method says it mirrors that Guard. It asked only the mailbox and user domains, so a
        // colleague with no Nexora login forwarding as "SEC Accounts Desk" still left SEC's initials on
        // the page as a mention, from an envelope the Guard had already thrown away as ours.
        string?[] ourNames = ["ALI ZAID AL-QURAISHI & PARTNERS"];

        var colleague = LeadCustomerResolutionService.SenderDisplayName(
            "SEC Accounts Desk <ahmed@alquraishi.com.sa>", ["rfq@alquraishi.com"], ourNames);
        Assert.Null(colleague.Name);
        Assert.Equal(PassageRole.ItemText, colleague.Role);

        // The same words on the buyer's own mailbox are still kept as a mention.
        var buyer = LeadCustomerResolutionService.SenderDisplayName(
            "SEC Accounts Desk <accounts@se.com.sa>", ["rfq@alquraishi.com"], ourNames);
        Assert.Equal("SEC Accounts Desk", buyer.Name);
        Assert.Equal(PassageRole.ItemText, buyer.Role);
    }

    [Theory]
    [InlineData("Rashid Al-Otaibi", true)]
    [InlineData("RASHID AL-OTAIBI", true)]
    [InlineData("Ahmed bin Saleh Al-Ghamdi", true)]
    [InlineData("Rashid Al-Otaibi via Coupa", true)]
    [InlineData("راشد العتيبي", true)]
    [InlineData("Saudi Aramco", false)]
    [InlineData("SEC Procurement", false)]
    [InlineData("NEOM Tenders", false)]
    [InlineData("SABIC", false)]
    [InlineData("Hyundai E&C", false)]
    [InlineData("Ma'aden Procurement", false)]
    [InlineData("Sadara Chemical Company", false)]
    [InlineData("Saudi Electricity Company Materials E-Bidding System", false)]
    [InlineData("Aramco Asia 2", false)]
    [InlineData("شركة أرامكو", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_display_name_is_read_as_a_person_by_its_shape(string? displayName, bool expected)
        => Assert.Equal(expected, LeadCustomerResolutionService.LooksLikeAPersonsName(displayName));

    [Theory]
    [InlineData("\"Rashid Al-Otaibi\" <r.otaibi@se.com.sa>")]
    // #9: the same person, on the two kinds of mailbox that used to make a name a header.
    [InlineData("Rashid Al-Otaibi <no-reply@sharepointonline.com>")]
    [InlineData("Rashid Al-Otaibi via Coupa <do_not_reply@coupahost.com>")]
    public void A_person_whose_name_shares_a_word_with_a_customer_is_offered_that_customer_and_never_linked_to_it(
        string from)
    {
        // THE HAZARD, whole: one-word trade names of four letters or more are scannable, and in
        // this market they are family names — Al-Rashid Trading keys to RASHID. Read as a header,
        // "Rashid Al-Otaibi <r.otaibi@se.com.sa>" linked an SEC enquiry to Al-Rashid Trading at
        // 0.88, and so did the same name on a SharePoint share notice or a Coupa message. As a
        // mention it is a suggestion a rep can dismiss in a glance.
        var (displayName, role) = LeadCustomerResolutionService.SenderDisplayName(from, []);
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 1,
            SenderEmail = LeadCustomerResolutionService.ParseAddress(from),
            Passages = LeadCustomerResolutionService.Passages(LeadWith(_ => { }), displayName, null, role),
        };
        var corpus = new ClientResolutionCorpus { Customers = [new CustomerNameSnapshot(7, "Al-Rashid Trading")] };

        var outcome = CustomerIdentityResolver.Resolve(evidence, corpus, new CustomerResolutionPolicy());

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(7L, Assert.Single(outcome.Candidates).CustomerId);
    }

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

    [Fact]
    public void Initials_are_never_found_by_a_name_bucket_which_is_why_they_are_looked_up_by_name()
    {
        // #11. This file's own comment once said SE out of "SEC Materials West Plant" was what
        // found "Saudi Electricity Company". That name is filed under SA, which the page never
        // writes; above the cap the print that links on SEC's initials resolved to nothing.
        var evidence = new LeadClientEvidence
        {
            Passages = [new DocumentPassage("storage location", "SEC Materials West Plant-West Operating Area", true)]
        };

        Assert.DoesNotContain("SA", LeadCustomerResolutionService.NameScanPrefixes(evidence));
        Assert.Contains("SEC", LeadCustomerResolutionService.PassageWords(evidence));
    }

    [Fact]
    public void The_page_is_searched_for_initials_in_every_passage_the_way_the_resolver_reads_it()
    {
        // Item text too: the resolver scans initials there (as a suggestion), and a customer list
        // built from fewer words than the resolver reads is one on which a shared acronym looks
        // unique again. Headers and addresses first, so they pin their customers before item text.
        var evidence = new LeadClientEvidence
        {
            Passages =
            [
                new DocumentPassage("item text", "AFFIX SWCC SPECIFIED BARCODE", false),
                new DocumentPassage("storage location", "SEC Materials West Plant-West Operating Area", true)
            ]
        };

        var words = LeadCustomerResolutionService.PassageWords(evidence).ToList();

        Assert.Contains("SEC", words);
        Assert.Contains("SWCC", words);
        Assert.True(words.IndexOf("SEC") < words.IndexOf("SWCC"));
        // Initials are three to six letters; nothing longer is worth a lookup.
        Assert.DoesNotContain("MATERIALS", words);
        Assert.DoesNotContain("SPECIFIED", words);
    }

    [Fact]
    public void Initials_two_customers_share_are_a_collision_and_every_owner_is_kept()
    {
        // #12. "Saudi Cable Company" and "Sudair Ceramics Company" both derive SCC. Every owner is
        // kept, because a customer dropped from the index is a collision the resolver cannot see.
        var index = LeadCustomerResolutionService.AcronymIndex.Build(
        [
            new CustomerNameSnapshot(2, "Sudair Ceramics Company"),
            new CustomerNameSnapshot(1, "Saudi Cable Company"),
            new CustomerNameSnapshot(3, "Saudi Electricity Company"),
            // Two words: no initials at all.
            new CustomerNameSnapshot(4, "Saudi Aramco")
        ]);

        Assert.Equal(new[] { "SCC" }, index.Collisions.ToArray());
        Assert.Equal(new long[] { 1, 2 }, index.CustomersByAcronym["SCC"]);
        Assert.Equal(new long[] { 3 }, index.CustomersByAcronym["SEC"]);
        Assert.Equal(2, index.CustomersByAcronym.Count);
    }
}
