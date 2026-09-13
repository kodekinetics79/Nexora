using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;

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

    [Theory]
    [InlineData("SAP Ariba Sourcing")]
    [InlineData("Ariba Sourcing")]
    [InlineData("Ariba Discovery")]
    [InlineData("SAP Business Network (Ariba)")]
    [InlineData("SAP Business Network Supplier Portal")]
    [InlineData("Coupa Supplier Portal")]
    [InlineData("COUPA CSP")]
    [InlineData("Etimad Portal")]
    [InlineData("etimad.sa")]
    [InlineData("منصة اعتماد")]                  // Etimad as the Saudi government writes it
    [InlineData("منصة إعتماد")]                  // the same, with the hamza a typist adds
    [InlineData("Jaggaer.com e-Sourcing")]
    [InlineData("Tejari Marketplace")]
    [InlineData("TenderBoard Oman")]
    [InlineData("ProcurePort e-Sourcing")]
    [InlineData("Oracle Supplier Network Portal")]
    // The mark glued to a neighbour, or split in two, which a whole-word match alone missed: each
    // was learned as a verified 0.92 pair for the first buyer confirmed on it.
    [InlineData("SAPAriba")]
    [InlineData("AribaNetwork")]
    [InlineData("SAP BusinessNetwork")]
    [InlineData("CoupaHost")]
    [InlineData("supplier.coupahost.com")]
    [InlineData("Tender Board")]
    [InlineData("e-Timad")]
    [InlineData("E'timad")]
    [InlineData("الاعتماد")]                     // Etimad with the Arabic article glued on
    public void A_network_is_shared_however_the_extractor_spells_its_name(string portal)
    {
        // The list was compared by exact key, so every other spelling slipped through: the
        // learner wrote "SAP ARIBA SOURCING|AN01012345678" as a verified 0.92 fact for SABIC,
        // and a Ma'aden RFQ on the same Ariba template then linked to SABIC.
        Assert.True(SharedSupplierNetworks.IsShared(portal));
    }

    [Theory]
    [InlineData("SARIBA Industrial Portal")]     // letters inside a word are not the mark
    [InlineData("Coupang Vendor Portal")]
    [InlineData("SAP SRM Supplier Portal")]      // SAP software a buyer runs itself: the buyer issues the codes
    [InlineData("SEC MATERIALS E-BIDDING SYSTEM")]
    [InlineData("Marafiq e-Procurement")]
    public void A_whole_word_match_still_leaves_buyer_operated_portals_learnable(string portal)
    {
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
    // sap.com was a row here and enshrined a defect: it is SAP's company domain, not a relay.
    // See A_software_vendors_own_domain_is_a_buyer_not_a_relay.
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

    [Theory]
    [InlineData("sap.com")]
    [InlineData("buyer@sap.com")]
    [InlineData("SAP.COM.")]
    public void A_software_vendors_own_domain_is_a_buyer_not_a_relay(string domain)
    {
        // SAP Arabia buys like any other customer and mails from sap.com. With sap.com on the
        // relay list the resolver dropped the domain before the domain tier, and a customer an
        // administrator had registered with Domain sap.com went from a 0.95 link to NO_MATCH.
        Assert.False(SyntheticIdentityGuard.IsPortalRelayDomain(domain));
        Assert.True(IdentityDomainGuard.IsOrganisationDomain(domain));
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

    // ── free-mail providers ──────────────────────────────────────────────────

    [Theory]
    [InlineData("fastmail.com")]
    [InlineData("fastmail.fm")]
    [InlineData("agent@fastmail.com")]           // callers hold both shapes
    [InlineData("rocketmail.com")]
    [InlineData("windowslive.com")]
    [InlineData("hey.com")]
    [InlineData("tutanota.com")]
    [InlineData("tuta.io")]
    [InlineData("proton.me")]
    [InlineData("pm.me")]
    [InlineData("gmx.de")]
    [InlineData("zohomail.com")]
    [InlineData("mail.com")]
    [InlineData("yandex.ru")]
    [InlineData("icloud.com")]
    [InlineData("me.com")]
    [InlineData("mac.com")]
    [InlineData("aol.com")]
    [InlineData("live.com")]
    [InlineData("msn.com")]
    [InlineData("outlook.sa")]
    [InlineData("hotmail.co.uk")]
    [InlineData("yahoo.com.sa")]
    [InlineData("ymail.com")]
    [InlineData("GMAIL.COM.")]
    // Providers the list still missed, each an organisation domain to the learner until now:
    // DuckDuckGo's relay, Saudi Telecom's consumer ISP, and the national webmail of several countries.
    [InlineData("duck.com")]
    [InlineData("awalnet.net.sa")]
    [InlineData("mail2world.com")]
    [InlineData("sina.cn")]
    [InlineData("inbox.lv")]
    [InlineData("seznam.cz")]
    [InlineData("wp.pl")]
    [InlineData("bigpond.com")]
    [InlineData("optonline.net")]
    public void A_consumer_mailbox_provider_is_free_mail(string domain)
    {
        // The list was closed at twenty-one first labels. A freight agent forwarding an SEC bid
        // from agent@fastmail.com taught fastmail.com as SEC's Domain at 0.95, and every later
        // fastmail sender — for any buyer — linked to SEC.
        Assert.True(SyntheticIdentityGuard.IsFreeMailDomain(domain));
        Assert.False(IdentityDomainGuard.IsOrganisationDomain(domain));
    }

    [Theory]
    [InlineData("se.com.sa")]
    [InlineData("aramco.com")]
    [InlineData("sabic.com")]
    [InlineData("heymarket.com")]                // "hey" is a whole domain, not a first label
    [InlineData("hey.com.sa")]
    [InlineData("mac.com.sa")]
    [InlineData("webasto.com")]
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_word_at_the_front_of_a_company_domain_is_not_free_mail(string? domain)
    {
        // A provider whose name is an ordinary word must not stop a company whose host starts
        // with that word from ever being matched by its registered Domain row.
        Assert.False(SyntheticIdentityGuard.IsFreeMailDomain(domain));
    }

    // ── distinctive words in a company name ──────────────────────────────────

    [Theory]
    [InlineData("SAUDI ARABIA")]
    [InlineData("Saudi Company")]                // LooseKey is SAUDI
    [InlineData("Kingdom of Saudi Arabia")]
    [InlineData("National Gulf International")]
    [InlineData("Middle East General Trading Co.")]
    [InlineData("Trading Company")]              // nothing but legal-form words
    [InlineData("Al")]
    [InlineData("AL-KSA")]
    [InlineData("المملكة العربية السعودية")]
    [InlineData("الشركة السعودية")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_name_made_only_of_country_and_business_words_names_nobody(string? name)
    {
        // Taught as an alias, "SAUDI ARABIA" linked every document that wrote the country in
        // its address or buyer sentence to one customer at 0.88.
        Assert.False(CustomerNameDistinctiveness.HasDistinctiveToken(name));
        Assert.Empty(CustomerNameDistinctiveness.DistinctiveTokens(name));
    }

    [Theory]
    [InlineData("Saudi Aramco", "ARAMCO")]
    [InlineData("Saudi Electricity Company", "ELECTRICITY")]   // lead 680 must keep a word to find
    [InlineData("MARAFIQ", "MARAFIQ")]
    [InlineData("SEC", "SEC")]
    [InlineData("SABIC", "SABIC")]
    [InlineData("Arabian Pipes Company", "PIPES")]
    [InlineData("Al Dammam Trading Co.", "DAMMAM")]
    [InlineData("الشركة السعودية للكهرباء", "للكهرباء")]
    public void A_name_keeps_the_word_that_says_which_company(string name, string expected)
    {
        Assert.True(CustomerNameDistinctiveness.HasDistinctiveToken(name));
        Assert.Contains(expected, CustomerNameDistinctiveness.DistinctiveTokens(name));
    }

    [Theory]
    [InlineData("SAUDI ELECTRICITY", "Saudi Aramco")]
    [InlineData("SAUDI BASIC INDUSTRIES", "Saudi Aramco")]
    [InlineData("SAUDI KAYAN PETROCHEMICAL", "Saudi Aramco")]
    [InlineData("SADARA CHEMICAL", "Saudi Aramco")]
    [InlineData("SAUDI ARABIAN MINING", "Saudi Aramco")]
    [InlineData("Saudi Electricity", "Saudi Cable Company")]
    [InlineData("NATIONAL WATER", "National Grid SA")]
    [InlineData("SAUDI ARABIA", "Saudi Aramco")]
    [InlineData("SAUDI COMPANY", "Saudi Aramco")]
    public void Names_that_share_only_country_words_do_not_resemble_each_other(string printed, string customer)
    {
        // Every one of these pairs cleared the learner's 0.75 Jaro-Winkler gate, because the
        // metric gives the first four letters a bonus and they all open SAUD, NATI or ARAB. A
        // reviewer's mis-click on any of them became a verified alias for the wrong company.
        Assert.True(CustomerNameNormalizer.JaroWinkler(
            CustomerNameNormalizer.TightKey(printed), CustomerNameNormalizer.TightKey(customer)) >= 0.75d);
        Assert.False(CustomerNameDistinctiveness.SharesDistinctiveToken(printed, customer));
        Assert.False(CustomerNameDistinctiveness.SharesDistinctiveToken(customer, printed));
    }

    [Theory]
    [InlineData("SAUDI ELECTRICITY CO", "Saudi Electricity Company")]
    [InlineData("ARAMCO", "Saudi Aramco")]
    [InlineData("Marafiq Jubail", "Power and Water Utility Company for Jubail and Yanbu (Marafiq)")]
    [InlineData("ALRAJHI BANK", "Al Rajhi Bank")]
    [InlineData("ALRAJHI", "Al Rajhi Bank")]     // the article printed glued to the family name
    public void Names_that_share_a_distinctive_word_do_resemble_each_other(string printed, string customer)
    {
        Assert.True(CustomerNameDistinctiveness.SharesDistinctiveToken(printed, customer));
    }

    // ── organisation domains ─────────────────────────────────────────────────

    private static readonly string[] OurMailboxes = ["rfq@alquraishi.com.sa", "alquraishi-group.com"];

    [Theory]
    [InlineData("se.com.sa")]
    [InlineData("57322@se.com.sa")]
    [InlineData("Ali Nasser <ali@se.com.sa>")]
    [InlineData("hdec.com")]                     // an organisation, though not the site owner
    [InlineData("apco-ksa.com")]
    [InlineData("notalquraishi.com.sa")]         // the self boundary is a label separator
    public void A_buyers_corporate_domain_is_an_organisation_domain(string domain)
    {
        Assert.True(IdentityDomainGuard.IsOrganisationDomain(domain, OurMailboxes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("localhost")]
    [InlineData("extraction@pipeline.local")]    // Nexora's own plumbing
    [InlineData("sec@system.com")]
    [InlineData("gmail.com")]
    [InlineData("agent@fastmail.com")]
    [InlineData("noreply@ansmtp.ariba.com")]     // the postman
    [InlineData("etimad.sa")]
    [InlineData("alerts@bidnet.com")]
    [InlineData("ahmed@alquraishi.com.sa")]      // us, by a mailbox address
    [InlineData("sales.alquraishi.com.sa")]      // a host under our domain is us
    [InlineData("zack@alquraishi-group.com")]    // us, by a bare domain entry
    [InlineData("10.0.0.1")]
    public void Plumbing_consumer_relay_and_our_own_domains_are_not_organisation_domains(string? domain)
    {
        Assert.False(IdentityDomainGuard.IsOrganisationDomain(domain, OurMailboxes));
    }

    [Theory]
    [InlineData("ALI@SE.COM.SA", "se.com.sa")]
    [InlineData("Ali Nasser <ali@se.com.sa>", "se.com.sa")]
    [InlineData("se.com.sa.", "se.com.sa")]
    [InlineData("https://www.aramco.com/", "aramco.com")]
    [InlineData("   ", null)]
    [InlineData("nobody@", null)]
    public void Every_shape_of_address_reads_to_the_same_bare_domain(string input, string? expected)
    {
        // Stored Domain rows were written through RoutingValueNormalizer; a lookup that differs
        // by a trailing dot or a "www." would never meet them.
        Assert.Equal(expected, IdentityDomainGuard.DomainOf(input));
    }

    // ── identifier sources ───────────────────────────────────────────────────

    [Fact]
    public void The_unverified_learned_source_is_one_value_and_is_never_trusted()
    {
        // The learner writes it, and the resolver's exact tiers and routing must skip it. One
        // misspelt copy in any reader would let a demoted row link at 1.00 or 0.95 again.
        Assert.Equal("LeadReviewUnverified", CustomerIdentifierSources.LeadReviewUnverified);
        Assert.Equal(CustomerIdentifierSources.LeadReviewUnverified, CustomerAliasLearner.UnverifiedAliasSource);
        Assert.DoesNotContain(CustomerIdentifierSources.LeadReviewUnverified, CustomerIdentifierSources.TrustedForAutoLink);
    }

    // ── the tenant's own domains ─────────────────────────────────────────────

    private const long SelfTenant = 8910;
    private const long NeighbourTenant = 8920;

    [Fact]
    public async Task A_colleagues_corporate_domain_is_ours_and_a_colleagues_gmail_is_not()
    {
        // The mailbox is rfq@alquraishi.com and the salesman forwards from ahmed@alquraishi.com.sa.
        // Read from the mailboxes alone, alquraishi.com.sa was not "us", and one confirmed forward
        // taught it as SEC's Domain at 0.95.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, SelfTenant);
            Seed.EnsureBusinessUnit(seed, NeighbourTenant);
            seed.EmailConfigurations.Add(Mailbox(SelfTenant, "rfq@alquraishi.com"));
            seed.EmailConfigurations.Add(Mailbox(NeighbourTenant, "intake@neighbour.com.sa"));
            seed.Users.AddRange(
                StaffUser("ahmed@alquraishi.com.sa", SelfTenant, active: true),
                StaffUser("Sara@AlQuraishi.com.sa", SelfTenant, active: true),
                StaffUser("colleague.personal@gmail.com", SelfTenant, active: true),
                StaffUser("seed@system.com", SelfTenant, active: true),
                StaffUser("left.the.company@oldname.com", SelfTenant, active: false),
                StaffUser("buyer@neighbour-staff.com", NeighbourTenant, active: true),
                StaffUser("operator@nexora-platform.com", null, active: true));
            await seed.SaveChangesAsync();
        }

        // The worker path: no tenant, so the EF filter is a no-op and the predicate is the scope.
        await using var context = db.ContextFor(null);
        var selfDomains = await TenantSelfIdentity.LoadSelfDomainsAsync(context, SelfTenant);

        Assert.Equal(
            new[] { "alquraishi.com", "alquraishi.com.sa" },
            selfDomains.OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.False(IdentityDomainGuard.IsOrganisationDomain("ahmed@alquraishi.com.sa", selfDomains));
        Assert.True(IdentityDomainGuard.IsOrganisationDomain("buyer@neighbour-staff.com", selfDomains));
        Assert.True(IdentityDomainGuard.IsOrganisationDomain("57322@se.com.sa", selfDomains));
    }

    [Fact]
    public void The_self_domain_rule_can_be_stated_without_a_database()
    {
        var domains = TenantSelfIdentity.SelfDomainsFrom(
            ["rfq@alquraishi.com", null, "  "],
            ["ahmed@alquraishi.com.sa", "someone@hotmail.com", "noreply@ariba.com", "extraction@pipeline.local", null]);

        Assert.Equal(
            new[] { "alquraishi.com", "alquraishi.com.sa" },
            domains.OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.True(domains.Contains("ALQURAISHI.COM.SA"));   // compared case-insensitively
    }

    private static EmailConfiguration Mailbox(long businessUnitId, string address) => new()
    {
        BusinessUnitId = businessUnitId,
        ConfigurationName = $"intake-{businessUnitId}",
        EmailAddress = address,
        Protocol = "IMAP",
        Host = "127.0.0.1",
        Port = 1,
        Username = address,
        Password = "secret",
        UseSsl = false,
        PollingInterval = 5,
        IsActive = true,
        CreatedOn = DateTime.UtcNow
    };

    private static User StaffUser(string email, long? businessUnitId, bool active) => new()
    {
        FirstName = "Staff",
        LastName = "Member",
        Email = email,
        PasswordHash = "x",
        ImageUrl = string.Empty,
        Buid = businessUnitId,
        IsActive = active,
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow
    };

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
