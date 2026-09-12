using ERP_RFQ_Automation.CustomerResolution;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The contracts the resolver and the learner both stand on: what counts as "us", what counts
/// as a buyer, and what the same evidence is worth on a second run. These are pure functions,
/// so each rule can be stated here on its own — which matters, because every one of them is a
/// REJECTION rule, and a rejection rule that is wrong deletes a customer silently and writes
/// no log for anyone to find.
/// </summary>
public sealed class CustomerIdentityContractsTests
{
    // ── shared supplier networks ─────────────────────────────────────────────

    [Theory]
    [InlineData("Ariba")]
    [InlineData("ARIBA")]
    [InlineData("SAP Ariba")]
    [InlineData("sap ariba network")]
    [InlineData("SAP Business Network")]
    [InlineData("Ariba Network")]
    [InlineData("Etimad")]
    [InlineData("etimad")]
    [InlineData("Jaggaer")]
    [InlineData("Coupa")]
    [InlineData("TenderBoard")]
    [InlineData("Tejari")]
    [InlineData("Oracle Supplier Network")]
    [InlineData("ProcurePort")]
    public void A_network_issued_supplier_number_identifies_no_buyer(string portal)
    {
        // Our Ariba Network ID is ONE number that identifies US to every buyer on the network.
        // The "portal|our-vendor-code" pair therefore names whichever buyer happened to be
        // taught first, and once two are taught every Ariba document is permanently ambiguous.
        Assert.True(SharedSupplierNetworks.IsShared(portal));
    }

    [Theory]
    [InlineData("MATERIALS E-BIDDING SYSTEM")]   // SEC's own system: one buyer issues the codes
    [InlineData("Materials E-Bidding System")]
    [InlineData("Saudi Electricity Company")]
    [InlineData("MARAFIQ")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_buyer_operated_portal_stays_learnable(string? portal)
    {
        // The test is "who issued the number", not "is it a portal". SEC runs its own bidding
        // system and issues vendor code 2004414 inside it, so that pair names SEC and is
        // exactly what the learned-portal-account tier was built for.
        Assert.False(SharedSupplierNetworks.IsShared(portal));
    }

    [Fact]
    public void Every_listed_network_is_recognised_by_its_own_name()
    {
        // The raw list and the comparison keys are derived from each other; if a future entry
        // normalises away to nothing, this catches it here instead of in production silence.
        Assert.NotEmpty(SharedSupplierNetworks.Names);
        foreach (var name in SharedSupplierNetworks.Names)
            Assert.True(SharedSupplierNetworks.IsShared(name), name);
    }

    // ── portal relay domains ─────────────────────────────────────────────────

    [Theory]
    [InlineData("ariba.com")]
    [InlineData("ANSMTP.ARIBA.COM")]
    [InlineData("eusmtp.ariba.com")]
    [InlineData("s4.ansmtp.ariba.com")]          // portals mint new sending hosts unannounced
    [InlineData("sap.com")]
    [InlineData("sapariba.com")]
    [InlineData("etimad.sa")]
    [InlineData("tenders.gov.sa")]
    [InlineData("jaggaer.com")]
    [InlineData("coupahost.com")]
    [InlineData("tejari.com")]
    [InlineData("bidnet.com")]
    [InlineData("bidnetdirect.com")]
    [InlineData("demandstar.com")]
    [InlineData("bonfirehub.com")]
    [InlineData("noreply@ariba.com")]            // callers hold both shapes
    public void A_portal_relay_domain_is_never_a_buyers_domain(string domain)
    {
        // noreply@ariba.com is the postman. Learned as a Domain it would auto-link the next
        // portal-delivered RFQ — from a different buyer entirely — at 0.95.
        Assert.True(SyntheticIdentityGuard.IsPortalRelayDomain(domain));
    }

    [Theory]
    [InlineData("se.com.sa")]                    // Saudi Electricity Company, a real buyer
    [InlineData("marafiq.com.sa")]
    [InlineData("aramco.com")]
    [InlineData("notariba.com")]                 // the boundary is a real label separator
    [InlineData("gmail.com")]                    // free-mail is a different guard
    [InlineData("")]
    [InlineData(null)]
    public void A_real_corporate_domain_is_not_a_relay(string? domain)
    {
        Assert.False(SyntheticIdentityGuard.IsPortalRelayDomain(domain));
    }

    [Fact]
    public void The_free_mail_and_synthetic_guards_are_unchanged()
    {
        // The relay list is additive. A consumer mailbox is still a statement about a PERSON,
        // and Nexora's own ingestion labels are still plumbing.
        Assert.True(SyntheticIdentityGuard.IsFreeMailDomain("gmail.com"));
        Assert.False(SyntheticIdentityGuard.IsFreeMailDomain("ariba.com"));
        Assert.True(SyntheticIdentityGuard.IsSyntheticDomain("system.com"));
        Assert.False(SyntheticIdentityGuard.IsSyntheticDomain("ariba.com"));
    }

    // ── self-identity distinctiveness floor ──────────────────────────────────

    private static readonly string[] OurName = ["ALI ZAID AL QURAISHI"];

    [Fact]
    public void A_short_customer_key_hiding_inside_our_name_is_not_us()
    {
        // Our tight key is ALIZAIDALQURAISHI, and "AID" is spelled by the letters of ZAID.
        // A customer called "Arabian Industrial Development" whose initials are AID was
        // discarded as ourselves by every tier that asks this question, permanently and with
        // no log: the floor was applied only to the tenant side while containment ran in both
        // directions.
        Assert.False(SelfIdentityGuard.IsSelfName("AID", OurName));
        Assert.False(SelfIdentityGuard.IsSelfName("SHI", OurName));
        Assert.False(SelfIdentityGuard.IsSelfName("RAI", OurName));
    }

    [Fact]
    public void Our_own_name_however_it_is_printed_is_still_us()
    {
        // The rejection rule stays generous where it is distinctive enough to be: real bids
        // attach noise to our name that an exact key comparison sails past.
        Assert.True(SelfIdentityGuard.IsSelfName("ALI ZAID AL QURAISHI", OurName));
        Assert.True(SelfIdentityGuard.IsSelfName("ALI ZAID AL QURAISHI AND EL", OurName));
        Assert.True(SelfIdentityGuard.IsSelfName("ALIZAID ALQURAISHI PARTNERS EST", OurName));
    }

    [Fact]
    public void A_short_key_that_IS_our_trading_name_is_still_us()
    {
        // Exact key equality needs no distinctiveness: if the tenant trades as "AZP", the
        // three letters AZP on a document are us whatever the floor says.
        Assert.True(SelfIdentityGuard.IsSelfName("AZP", ["AZP"]));
    }

    [Fact]
    public void A_near_miss_spelling_of_our_name_is_still_us()
    {
        // The similarity check is untouched by the floor: ZIAD/ZAID is a transliteration
        // typo on the same trading house, not a different company.
        Assert.True(SelfIdentityGuard.IsSelfName("ALI ZIAD AL QURAISHI", OurName));
    }

    // ── deterministic RFQ-pattern matching ───────────────────────────────────

    [Fact]
    public void The_same_rfq_number_gives_the_same_answer_every_time()
    {
        // The old implementation caught RegexMatchTimeoutException and reported it as "does
        // not match", so the same number could match on an idle machine and miss on a loaded
        // one. A module whose promise is "the same evidence gives the same answer forever"
        // cannot have a load-dependent answer.
        var pattern = RfqNumberPattern.Derive("C001046556");
        Assert.Equal(@"^C\d{9}$", pattern);

        for (var i = 0; i < 500; i++)
        {
            Assert.True(RfqNumberPattern.Matches(pattern, "C001046556"));
            Assert.True(RfqNumberPattern.Matches(pattern, "  C001046557  "));
            Assert.False(RfqNumberPattern.Matches(pattern, "C00104655"));
            Assert.False(RfqNumberPattern.Matches(pattern, "D001046556"));
        }
    }

    [Fact]
    public void A_separator_generalises_but_the_shape_does_not()
    {
        var pattern = RfqNumberPattern.Derive("RFQ-2026/001");
        Assert.True(RfqNumberPattern.Matches(pattern, "RFQ/2026-001"));
        Assert.False(RfqNumberPattern.Matches(pattern, "RFQ-2026/0011"));
    }

    [Theory]
    [InlineData(@"^C\d{")]                       // malformed: hand-edited in the database
    [InlineData(@"^(?=X)C\d{9}$")]               // a construct the engine refuses, and which
                                                 // could not match this number in any case
    [InlineData("")]
    [InlineData(null)]
    public void An_unusable_stored_pattern_means_no_match_every_time(string? pattern)
    {
        // Not evidence about a customer, and — unlike the timeout this replaced — not
        // evidence that changes its mind between runs.
        for (var i = 0; i < 50; i++)
            Assert.False(RfqNumberPattern.Matches(pattern, "C001046556"));
    }

    // ── passage roles ────────────────────────────────────────────────────────

    [Fact]
    public void A_passage_role_defaults_from_the_flag_every_caller_already_passes()
    {
        // Additive by construction: the existing three-argument call sites keep the exact
        // behaviour they had. "Names the buyer" has always meant an address or a site.
        var address = new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true);
        var itemText = new DocumentPassage("item text", "AFFIX SEC SPECIFIED BARCODE", false);

        Assert.Equal(PassageRole.ShipTo, address.Role);
        Assert.Equal(PassageRole.ItemText, itemText.Role);
        Assert.True(address.NamesTheBuyer);
        Assert.False(itemText.NamesTheBuyer);
    }

    [Fact]
    public void A_caller_that_reads_a_header_can_say_so()
    {
        // An EPC contractor buying for Saudi Aramco ships to Aramco and is not Aramco: the
        // header is a statement about who is buying, the address about where goods go.
        var header = new DocumentPassage("the sentence that names the buyer",
            "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).", true)
        {
            Role = PassageRole.BuyerHeader
        };

        Assert.Equal(PassageRole.BuyerHeader, header.Role);
        Assert.True(header.NamesTheBuyer);
    }

    // ── policy ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_auto_link_line_sits_where_the_tiers_assume_it_does()
    {
        var policy = new CustomerResolutionPolicy();

        // Lead 680 is an SEC portal print whose ONLY evidence is the delivery address
        // "Saudi Electricity Company-DAMMAM". It must keep linking at 0.88.
        Assert.Equal(0.88m, policy.NameInAddressConfidence);
        Assert.True(policy.NameInAddressConfidence >= policy.MinimumAutoLinkConfidence);
        Assert.True(policy.NameAcronymInAddressConfidence >= policy.MinimumAutoLinkConfidence);

        // A consignee, and incidental item text, must stay below the line: a suggestion a rep
        // accepts, never a decision made for them.
        Assert.True(policy.ShipToDemotedConfidence < policy.MinimumAutoLinkConfidence);
        Assert.True(policy.NameInItemTextConfidence < policy.MinimumAutoLinkConfidence);
        Assert.True(policy.ExactNameSuggestionConfidence < policy.MinimumAutoLinkConfidence);
    }
}
