using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The resolver is pure, so every rule that decides whether a lead gets a client can be
/// stated here without a database. The governing principle under test throughout: a WRONG
/// client on a lead is worse than an unresolved one.
/// </summary>
public sealed class CustomerIdentityResolverTests
{
    private const long Sec = 5001;          // Saudi Electricity Company — the buyer
    private const long OtherCustomer = 5002;
    private const long ThirdCustomer = 5003;
    private static readonly CustomerResolutionPolicy Policy = new();

    // ── S0 guards ────────────────────────────────────────────────────────────

    [Fact]
    public void The_tenants_own_name_is_never_a_customer()
    {
        // The ONLY company name printed on an SEC bid is the trading house that RECEIVED it.
        // A resolver that accepted it would link every SEC lead to a customer record of us.
        var corpus = Corpus(
            customers: [new(Sec, "ALI ZAID AL-QURAISHI & PARTNERS")],
            identifiers: [Alias(1, Sec, "ALI ZAID AL QURAISHI")]);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            CustomerCompanyName = "ALI ZAID AL-QURAISHI&PARTNERS EL",
            TenantSelfNameKeys = ["ALI ZAID AL-QURAISHI & PARTNERS"]
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NoEvidence, outcome.ReasonCode);
        Assert.Null(outcome.CustomerId);
    }

    [Fact]
    public void The_documents_own_vendor_block_is_never_a_customer()
    {
        // Direction of trade, enforced without any tenant configuration at all: the name in
        // the Vendname block of THIS document cannot be the buyer of THIS document.
        var corpus = Corpus(
            customers: [new(Sec, "A.Z. ALQURAISHI & PARTNERS")],
            identifiers: [Alias(1, Sec, CustomerNameNormalizer.LooseKey("A.Z. ALQURAISHI & PARTNERS"))]);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            CustomerCompanyName = "A.Z. ALQURAISHI & PARTNERS ESOSA",
            SupplierNameOnDocument = "A.Z. ALQURAISHI & PARTNERS"
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NoEvidence, outcome.ReasonCode);
    }

    [Theory]
    [InlineData("extraction@pipeline.local")]
    [InlineData("sec@system.com")]     // NOT Saudi Electricity — a FolderService label
    [InlineData("aramco@system.com")]
    [InlineData("manual@upload.com")]
    [InlineData("system@excel.upload")]
    public void Nexoras_own_ingestion_placeholders_are_never_customer_evidence(string sender)
    {
        var corpus = Corpus(identifiers:
        [
            new(1, Sec, CustomerIdentifierType.Email, sender.ToLowerInvariant(), true, 1m, "CustomerProfile"),
            new(2, Sec, CustomerIdentifierType.Domain,
                sender.Split('@')[1], true, 0.95m, "CustomerProfile")
        ]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = sender }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NoEvidence, outcome.ReasonCode);
    }

    [Fact]
    public void A_free_mail_domain_never_links_but_the_exact_address_still_can()
    {
        // gmail.com says something about a PERSON and nothing about an organisation.
        var domainOnly = Corpus(identifiers:
            [new(1, Sec, CustomerIdentifierType.Domain, "gmail.com", true, 0.95m, "CustomerProfile")]);
        var byDomain = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "buyer@gmail.com" },
            domainOnly, Policy);
        Assert.Null(byDomain.CustomerId);

        var addressToo = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers:
            [
                new(1, Sec, CustomerIdentifierType.Domain, "gmail.com", true, 0.95m, "CustomerProfile"),
                new(2, Sec, CustomerIdentifierType.Email, "buyer@gmail.com", true, 1m, "CustomerContact")
            ]);
        var byAddress = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "buyer@gmail.com" },
            addressToo, Policy);
        Assert.Equal(Sec, byAddress.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, byAddress.ReasonCode);
    }

    [Fact]
    public void Our_own_vendor_code_at_the_customer_never_links_by_itself()
    {
        // SEC's "Vendor Code 2004414" is OUR account number in THEIR portal. Matching it as
        // an ERP account would link the lead to whoever happens to own that account number.
        var corpus = Corpus(identifiers:
            [new(1, Sec, CustomerIdentifierType.ErpAccount, "2004414", true, 1m, "CustomerProfile")]);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            AccountReferences = ["2004414"],
            SupplierAccountRefOnDocument = "2004414"
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NoEvidence, outcome.ReasonCode);
    }

    // ── S1..S3 auto-link tiers ───────────────────────────────────────────────

    [Fact]
    public void An_exact_sender_address_links_and_says_why()
    {
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, CustomerIdentifierType.Email, "57322@se.com.sa", true, 1m, "CustomerContact")]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "57322@se.com.sa" },
            corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, outcome.Status);
        Assert.Equal(1.00m, outcome.Confidence);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
        Assert.Contains("57322@se.com.sa", outcome.Explanation);
    }

    [Fact]
    public void The_buyer_address_printed_on_the_document_carries_the_same_weight_as_the_sender()
    {
        // Folder-ingested SEC bids have NO real sender; "E-mail: 57322@se.com.sa" inside the
        // document is the only trace of the buying organisation's real domain.
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, CustomerIdentifierType.Domain, "se.com.sa", true, 0.95m, "CustomerProfile")]);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            SenderEmail = "extraction@pipeline.local",     // synthetic; discarded
            DocumentBuyerEmail = "AKGhuwainim@se.com.sa"
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);
        Assert.Equal(0.95m, outcome.Confidence);
    }

    [Fact]
    public void A_verified_learned_alias_links_where_a_bare_name_only_suggests()
    {
        var nameKey = CustomerNameNormalizer.LooseKey("Saudi Electricity Company");

        var unverified = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, CustomerIdentifierType.Alias, nameKey, false, 0.9m, "LeadReviewLearned")]);
        var suggestion = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, CustomerCompanyName = "SAUDI ELECTRICITY CO." },
            unverified, Policy);
        Assert.Null(suggestion.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, suggestion.Status);
        Assert.Equal(CustomerMatchReasonCodes.NameExactUnverified, suggestion.ReasonCode);

        var verified = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [Alias(1, Sec, nameKey)]);
        var linked = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, CustomerCompanyName = "SAUDI ELECTRICITY CO." },
            verified, Policy);
        Assert.Equal(Sec, linked.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedAlias, linked.ReasonCode);
        Assert.Equal(0.90m, linked.Confidence);
    }

    [Fact]
    public void A_learned_portal_and_vendor_code_pair_links()
    {
        var key = $"{CustomerNameNormalizer.LooseKey("MATERIALS E-BIDDING SYSTEM")}|2004414";
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers:
            [
                new(1, Sec, CustomerIdentifierType.PortalAccount, key, true, 0.92m,
                    CustomerIdentifierSources.LeadReviewLearned)
            ]);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            CustomerPortalName = "MATERIALS E-BIDDING SYSTEM",
            SupplierAccountRefOnDocument = "2004414"
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedPortalAccount, outcome.ReasonCode);
        Assert.Equal(0.92m, outcome.Confidence);
    }

    [Fact]
    public void An_untrusted_source_never_reaches_the_auto_link_tier()
    {
        // Only LeadReviewLearned / CustomerProfile / CustomerImport are trusted; a row
        // written by some other process may propose, never decide.
        var nameKey = CustomerNameNormalizer.LooseKey("Saudi Electricity Company");
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, CustomerIdentifierType.Alias, nameKey, true, 0.9m, "SomeOtherProcess")]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, CustomerCompanyName = "Saudi Electricity Company" },
            corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
    }

    // ── ambiguity ────────────────────────────────────────────────────────────

    [Fact]
    public void Two_customers_sharing_one_domain_produce_candidates_not_a_coin_toss()
    {
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "SEC Distribution")],
            identifiers:
            [
                new(1, Sec, CustomerIdentifierType.Domain, "se.com.sa", true, 0.95m, "CustomerProfile"),
                new(2, OtherCustomer, CustomerIdentifierType.Domain, "se.com.sa", true, 0.95m, "CustomerProfile")
            ]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "92442@se.com.sa" },
            corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.Ambiguous, outcome.ReasonCode);
        Assert.Equal(2, outcome.Candidates.Count);
        Assert.Equal([1, 2], outcome.Candidates.Select(c => c.Rank));
        Assert.All(outcome.Candidates, candidate => Assert.NotEqual(string.Empty, candidate.Explanation));
    }

    // ── suggestion tiers ─────────────────────────────────────────────────────

    [Fact]
    public void A_first_time_name_collision_suggests_and_never_links()
    {
        // Two distinct Gulf legal entities routinely share a trade name. A human confirms
        // once; only then does it become an auto-link.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, CustomerCompanyName = "SAUDI ELECTRICITY CO" },
            corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(0.75m, outcome.Confidence);
        Assert.Single(outcome.Candidates);
        Assert.Equal(Sec, outcome.Candidates[0].CustomerId);
    }

    [Fact]
    public void A_near_miss_name_suggests_at_a_capped_confidence()
    {
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, CustomerCompanyName = "Saudi Electricty Co" },
            corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NameFuzzy, outcome.ReasonCode);
        Assert.True(outcome.Confidence <= 0.85m, "a fuzzy name must never out-rank a verified alias");
    }

    [Fact]
    public void A_prior_human_resolved_lead_from_the_same_sender_suggests()
    {
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            priorSenders: [new("57322@se.com.sa", Sec)]);

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "57322@se.com.sa" },
            corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.PriorSender, outcome.ReasonCode);
        Assert.Equal(0.65m, outcome.Confidence);
    }

    [Fact]
    public void An_rfq_numbering_shape_suggests_but_can_never_link()
    {
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers:
            [
                // Learned unverified BY DESIGN — the auto-link tier requires IsVerified.
                new(1, Sec, CustomerIdentifierType.RfqNumberPattern, "^C\\d{9}$", false, 0.50m,
                    CustomerIdentifierSources.LeadReviewLearned)
            ]);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            RfqNumber = "C001046556",
            BuyerPersonName = "3C2-AMER AL-DOSSARY"
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.RfqPattern, outcome.ReasonCode);
    }

    // ── contact + honest failure ─────────────────────────────────────────────

    [Fact]
    public void A_contact_is_only_attached_when_exactly_one_address_matches_inside_that_customer()
    {
        var identifiers = new List<CustomerIdentifierSnapshot>
            { new(1, Sec, CustomerIdentifierType.Email, "57322@se.com.sa", true, 1m, "CustomerContact") };
        var evidence = new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "57322@se.com.sa" };

        var one = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: [new(Sec, "Saudi Electricity Company")], identifiers: identifiers,
            contacts: [new(900, Sec, "57322@se.com.sa", "Amer", "Al-Dossary")]), Policy);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatched, one.Status);
        Assert.Equal(900, one.ContactId);

        // Two contact rows carry the same address: which person sent it is genuinely
        // unknown, so the CUSTOMER still links and the CONTACT honestly does not.
        var two = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: [new(Sec, "Saudi Electricity Company")], identifiers: identifiers,
            contacts:
            [
                new(900, Sec, "57322@se.com.sa", "Amer", "Al-Dossary"),
                new(901, Sec, "57322@se.com.sa", "Duplicate", "Row")
            ]), Policy);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, two.Status);
        Assert.Null(two.ContactId);
        Assert.Equal(Sec, two.CustomerId);
    }

    [Fact]
    public void Evidence_that_matches_nothing_is_unresolved_with_NO_MATCH_not_NO_EVIDENCE()
    {
        // The distinction matters operationally: NO_EVIDENCE means the document told us
        // nothing, NO_MATCH means it did and this tenant has no such customer yet.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1,
            LeadId = 10,
            SenderEmail = "57322@se.com.sa",
            CustomerCompanyName = "Saudi Electricity Company"
        }, Corpus(), Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NoMatch, outcome.ReasonCode);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public void No_evidence_at_all_is_reported_as_such()
    {
        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10 }, Corpus(), Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NoEvidence, outcome.ReasonCode);
    }

    [Fact]
    public void Candidates_are_capped_and_ranked_strongest_first()
    {
        var customers = Enumerable.Range(0, 8)
            .Select(i => new CustomerNameSnapshot(6000 + i, "Saudi Electricity Company"))
            .ToList();

        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, CustomerCompanyName = "Saudi Electricity Company" },
            Corpus(customers: customers), Policy);

        Assert.Equal(Policy.MaximumCandidates, outcome.Candidates.Count);
        Assert.Equal(Enumerable.Range(1, Policy.MaximumCandidates), outcome.Candidates.Select(c => c.Rank));
        Assert.True(outcome.Candidates.Zip(outcome.Candidates.Skip(1))
            .All(pair => pair.First.Confidence >= pair.Second.Confidence));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // ── THE NAME THE BUYER WROTE ──────────────────────────────────────────────
    // A sourcing portal's print names no buyer in a "buyer" field; it names the buyer in the
    // delivery address and repeats it in the item text. The name is the one thing that survives
    // the buyer changing portal or ERP, so it must be enough on its own.

    [Fact]
    public void A_known_customers_name_inside_the_delivery_address_links_the_lead()
    {
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Test Customer")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, RfqNumber = "C001835789",
            Passages = [new DocumentPassage("storage location field", "Saudi Electricity Company-Jizan Area", true)]
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Contains("storage location", outcome.Explanation);
    }

    [Fact]
    public void The_customers_initials_in_the_delivery_address_link_the_lead_without_teaching()
    {
        // Production, 2026-09-12: "SEC Materials West Plant-West Operating Area" was on the page and
        // the lead was offered to Saudi Aramco at 55% because a numbering pattern had been learned
        // onto it. Nobody had taught "SEC". The initials of a customer's own name are not a lesson
        // to be taught; they follow the name.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")], identifiers:
        [
            new(1, OtherCustomer, CustomerIdentifierType.RfqNumberPattern, @"^C\d{9}$", false, 0.50m, CustomerIdentifierSources.LeadReviewLearned),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, RfqNumber = "C001832162",
            Passages = [new DocumentPassage("delivery location", "SEC Materials West Plant-West Operating Area", true)]
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(Policy.NameAcronymInAddressConfidence, outcome.Confidence);
        Assert.Contains("initials", outcome.Explanation);
    }

    [Fact]
    public void Two_letter_initials_never_match_and_initials_in_item_text_only_suggest()
    {
        // "Saudi Aramco" has no usable initials: "SA" is anybody's letters. And "SEC" inside
        // item text is a hint about a third party as often as about the buyer.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")]);
        var none = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery location", "SA Plant 4, Receiving Bay B", true)]
        }, corpus, Policy);
        Assert.Null(none.CustomerId);
        Assert.NotEqual(CustomerMatchReasonCodes.NameInDocument, none.ReasonCode);

        var hint = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            Passages = [new DocumentPassage("item text", "AFFIX SEC SPECIFIED BARCODE", false)]
        }, corpus, Policy);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, hint.Status);
        Assert.Equal(Sec, Assert.Single(hint.Candidates).CustomerId);
        Assert.Equal(Policy.NameAcronymInItemTextConfidence, hint.Confidence);
    }

    [Fact]
    public void Two_customers_initials_in_the_same_address_stay_ambiguous()
    {
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saline Water Conversion Corporation")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery location", "SEC substation inside the SWCC Jubail plant", true)]
        }, corpus, Policy);
        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
        Assert.Null(outcome.CustomerId);
    }

    [Fact]
    public void A_one_word_trade_name_is_found_in_the_sentence_that_names_the_buyer()
    {
        // Production, 2026-09-12: a Marafiq RFQ was read perfectly — the company name, the sentence
        // proving it, our vendor code and the buyer — and resolved to nothing. The passage scan
        // required two words and eight characters, written for "Saudi Electricity Company", which
        // makes the commonest buyer names in the country invisible: Marafiq, SABIC, NEOM, Sadara,
        // SATORP, Ma'aden. A Saudi buyer's trade name IS one word.
        var corpus = Corpus(customers: [new(Sec, "Marafiq"), new(OtherCustomer, "Saudi Aramco")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, RfqNumber = "HFE-26-202",
            // LeadCustomerResolutionService.Passages adds this sentence as a BuyerHeader. The fixture
            // predated roles and let the role default to ship-to, which only worked while a one-word
            // name could link from an address.
            Passages = [new DocumentPassage("the sentence that names the buyer",
                "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).", true)
                { Role = PassageRole.BuyerHeader }]
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void A_short_or_ordinary_one_word_name_is_still_refused()
    {
        // Four characters is the floor, and a name that is an ordinary address word is refused
        // however long: "Gate" would otherwise link on "Gate 4, Ras Tanura".
        var corpus = Corpus(customers: [new(Sec, "ACE"), new(OtherCustomer, "Gate")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "ACE deliveries to Gate 4, Ras Tanura", true)]
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
    }

    [Fact]
    public void Initials_that_are_an_ordinary_address_word_never_link()
    {
        // "Arabian Refinery Engineering Associates" derives AREA, and every SEC print says
        // "West Operating Area". Before this the wrong customer was linked at 0.85.
        var corpus = Corpus(customers: [new(OtherCustomer, "Arabian Refinery Engineering Associates")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery location", "SEC Materials West Plant-West Operating Area", true)]
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.NotEqual(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
    }

    [Fact]
    public void Initials_two_customers_share_identify_neither()
    {
        // Saudi Cable, Saudi Ceramics and Saudi Chemical all derive SCC. Before this, every
        // document carrying those three letters was a permanent stalemate.
        var corpus = Corpus(customers:
        [
            new(Sec, "Saudi Cable Company"),
            new(OtherCustomer, "Saudi Ceramics Company"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "SCC Jubail plant, gate 2", true)]
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.NotEqual(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
    }

    [Fact]
    public void A_four_character_company_code_no_longer_links_a_lead()
    {
        // On an SAP print a line's company reference is 1000 / 2000 / SA01, shared by every
        // affiliate of a group. Matching one at authoritative confidence claimed eleven companies.
        var corpus = Corpus(customers: [new(Sec, "Yanbu National Petrochemical Company")], identifiers:
        [
            new(1, Sec, CustomerIdentifierType.ErpAccount, "1000", true, 1.00m, CustomerIdentifierSources.MasterData),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, AccountReferences = ["1000"],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
    }

    [Fact]
    public void A_numbering_shape_two_customers_share_suggests_neither()
    {
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")], identifiers:
        [
            new(1, Sec, CustomerIdentifierType.RfqNumberPattern, @"^C\d{9}$", false, 0.50m, CustomerIdentifierSources.LeadReviewLearned),
            new(2, OtherCustomer, CustomerIdentifierType.RfqNumberPattern, @"^C\d{9}$", false, 0.50m, CustomerIdentifierSources.LeadReviewLearned),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, RfqNumber = "C001832162",
        }, corpus, Policy);

        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.ReasonCode == CustomerMatchReasonCodes.RfqPattern);
    }

    [Fact]
    public void An_ambiguous_result_claims_no_confidence_and_names_the_clients()
    {
        // Two customers on one corporate domain used to read "AMBIGUOUS at 95%" — the confidence
        // of a link the engine had just refused to make — and the sentence named neither.
        var corpus = Corpus(customers: [new(Sec, "Zamil Industrial"), new(OtherCustomer, "Zamil Steel")], identifiers:
        [
            new(1, Sec, CustomerIdentifierType.Domain, "zamil.com", true, 0.95m, CustomerIdentifierSources.MasterData),
            new(2, OtherCustomer, CustomerIdentifierType.Domain, "zamil.com", true, 0.95m, CustomerIdentifierSources.MasterData),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, SenderEmail = "buyer@zamil.com",
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
        Assert.Equal(0m, outcome.Confidence);
        Assert.Contains("Zamil Industrial", outcome.Explanation);
        Assert.Contains("Zamil Steel", outcome.Explanation);
    }

    [Fact]
    public void A_rule_an_administrator_entered_on_the_setup_screen_links_the_lead()
    {
        // Setup → Routing rules writes Source = MasterData. A portal vendor code entered there
        // is a deliberate fact about the customer, not a guess.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")], identifiers:
        [
            new(1, Sec, CustomerIdentifierType.PortalAccount, "MATERIALS E BIDDING SYSTEM|2004414", true, 0.95m, CustomerIdentifierSources.MasterData),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerPortalName = "MATERIALS E-BIDDING SYSTEM", SupplierAccountRefOnDocument = "2004414",
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedPortalAccount, outcome.ReasonCode);
    }

    [Fact]
    public void A_taught_alias_in_the_item_text_is_a_suggestion_not_a_link()
    {
        // "SEC" in "AFFIX SEC SPECIFIED BARCODE" is a strong hint and a weak proof: item text
        // may mention a third party. A reviewer taught the alias, so it is offered, not applied.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")], identifiers: [Alias(1, Sec, "SEC")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10, RfqNumber = "C001835789",
            Passages = [new DocumentPassage("item text", "* FOR THIS ITEM YOU ARE REQUIRED TO AFFIX SEC SPECIFIED BARCODE", false)]
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(Sec, Assert.Single(outcome.Candidates).CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
    }

    [Fact]
    public void Two_customers_named_in_the_address_leave_the_choice_to_a_person()
    {
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Jizan Power Holdings")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company c/o Jizan Power Holdings, Jizan", true)]
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
        Assert.Null(outcome.CustomerId);
    }

    [Fact]
    public void A_placeholder_name_and_a_word_inside_another_word_never_match()
    {
        // A one-word trade name now matches on its own, because Marafiq, SABIC and NEOM are how
        // Saudi buyers write themselves. "Test" is not one of those: a placeholder customer record
        // is scaffolding, and it is refused by name. "SEC" inside "SECOND" is still not SEC.
        var corpus = Corpus(customers: [new(OtherCustomer, "Test"), new(Sec, "Saudi Electricity Company")],
            identifiers: [Alias(1, Sec, "SEC")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Test site, SECOND FLOOR, Riyadh", true)]
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public void The_tenants_own_name_in_a_passage_is_never_a_customer()
    {
        var corpus = Corpus(customers: [new(OtherCustomer, "ALI ZAID AL-QURAISHI & PARTNERS")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Deliver to ALI ZAID AL-QURAISHI & PARTNERS warehouse, Dammam", true)],
            TenantSelfNameKeys = ["ALI ZAID AL-QURAISHI & PARTNERS"]
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
    }

    // ── A SHARED SUPPLIER NETWORK MUST NOT LINK ───────────────────────────────

    [Fact]
    public void A_vendor_code_on_a_shared_sourcing_network_is_offered_and_never_linked()
    {
        // Our Ariba Network ID is ONE number issued by the NETWORK that identifies US to every
        // buyer on it. Learned against the first Ariba buyer, the "portal|our-vendor-code" pair
        // would auto-link every later Ariba RFQ — from any buyer at all — to that one customer
        // at 0.92, which links without asking anybody; teach a second buyer and every Ariba
        // document becomes permanently AMBIGUOUS instead. The pair is still a true fact about
        // the document, so it is offered at the weakest confidence in the engine.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")], identifiers:
        [
            new(1, Sec, CustomerIdentifierType.PortalAccount, "SAP ARIBA|AN01234567", true, 0.92m,
                CustomerIdentifierSources.LeadReviewLearned),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerPortalName = "SAP Ariba", SupplierAccountRefOnDocument = "AN01234567",
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(Policy.RfqPatternSuggestionConfidence, outcome.Confidence);
        Assert.Equal(Sec, Assert.Single(outcome.Candidates).CustomerId);
        Assert.Contains("AN01234567", outcome.Explanation);
        Assert.Contains("OUR supplier number", outcome.Explanation);
        Assert.Contains("shared by several of your customers", outcome.Explanation);
    }

    [Fact]
    public void The_buyers_own_portal_still_links_because_that_buyer_issued_the_code()
    {
        // The test for membership is "who issued the number", not "is it a portal". SEC's own
        // MATERIALS E-BIDDING SYSTEM is buyer-operated: one buyer runs it and issues the codes
        // in it, so vendor code 2004414 means nothing anywhere else and therefore names SEC.
        // This is the case the tier was built for and it must keep linking at 0.92.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")], identifiers:
        [
            new(1, Sec, CustomerIdentifierType.PortalAccount, "MATERIALS E BIDDING SYSTEM|2004414", true, 0.92m,
                CustomerIdentifierSources.LeadReviewLearned),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerPortalName = "MATERIALS E-BIDDING SYSTEM", SupplierAccountRefOnDocument = "2004414",
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedPortalAccount, outcome.ReasonCode);
        Assert.Equal(Policy.LearnedPortalAccountConfidence, outcome.Confidence);
    }

    // ── THE LONGEST NAME IN A PASSAGE IS THE ONE THE DOCUMENT MEANS ───────────

    [Fact]
    public void The_longest_name_in_one_passage_wins_because_SATORP_is_not_Aramco()
    {
        // "Saudi Aramco Total Refining and Petrochemical Company, Jubail" contains "SAUDI
        // ARAMCO" as whole words, so with both companies on the books the address matched two
        // customers and the lead went AMBIGUOUS. SATORP is a separate joint venture with its own
        // vendor registration, payment terms and portal — an invoice sent to Aramco against a
        // SATORP order is not paid. Same shape for SAMREF, YASREF, Luberef and Sadara.
        var corpus = Corpus(customers:
        [
            new(OtherCustomer, "Saudi Aramco"),
            new(ThirdCustomer, "Saudi Aramco Total Refining and Petrochemical Company"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Total Refining and Petrochemical Company, Jubail", true)],
        }, corpus, Policy);

        Assert.Equal(ThirdCustomer, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
    }

    [Fact]
    public void Royal_Commission_Jubail_beats_a_bare_Royal_Commission()
    {
        // The industrial-city authority and the parent body are different buyers with different
        // budgets, and the longer of the two names is the one the page actually wrote.
        var corpus = Corpus(customers:
        [
            new(OtherCustomer, "Royal Commission"),
            new(ThirdCustomer, "Royal Commission Jubail"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Royal Commission Jubail, Industrial Area 2", true)],
        }, corpus, Policy);

        Assert.Equal(ThirdCustomer, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void Two_names_in_two_different_passages_are_two_statements_and_both_are_heard()
    {
        // The longest-name rule is about ONE sentence naming one company twice over. A name in
        // the address and a different name in the item text are two separate claims, and
        // silently dropping one because the other is longer would hide evidence from the rep.
        var corpus = Corpus(customers:
        [
            new(Sec, "Saudi Electricity Company"),
            new(ThirdCustomer, "Saudi Electricity Company Jizan"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages =
            [
                new DocumentPassage("delivery address", "Saudi Electricity Company Jizan, gate 2", true),
                new DocumentPassage("item text", "Barcode to Saudi Electricity Company standard", false),
            ],
        }, corpus, Policy);

        // The address decides (one customer survives suppression there); the item-text claim
        // about the parent company is still on the record as a candidate.
        Assert.Equal(ThirdCustomer, outcome.CustomerId);
    }

    // ── A DELIVERY ADDRESS NAMES THE CONSIGNEE ────────────────────────────────

    [Fact]
    public void Lead_680_an_SEC_print_carrying_only_a_delivery_address_still_links()
    {
        // PRODUCTION LEAD 680. An SEC portal print with no sender domain, no company-name field,
        // no portal name and no supplier block: the delivery address is the ONLY place the buyer
        // is named anywhere on the page. Nothing on that page competes with it, so it links.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            SenderEmail = "extraction@pipeline.local",     // Nexora's own ingestion label; discarded
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(0.88m, outcome.Confidence);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
    }

    [Fact]
    public void Lead_682_a_Marafiq_RFQ_links_on_the_name_the_document_states()
    {
        // PRODUCTION LEAD 682. The document names its own buyer three times over and carries no
        // sender at all. The company-name field and the buyer sentence are headers — that is how
        // LeadCustomerResolutionService.Passages builds them — and a header names the buyer. The
        // fixture used to leave all three at the ship-to default, which only linked while a one-word
        // name could link from an address; the warehouse address is now an offer beside the headers.
        var corpus = Corpus(customers: [new(Sec, "Marafiq"), new(OtherCustomer, "Saudi Aramco")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 682,
            CustomerCompanyName = "MARAFIQ",
            SupplierAccountRefOnDocument = "1495",
            RfqNumber = "HFE-26-202",
            Passages =
            [
                new DocumentPassage("company named on the document", "MARAFIQ", true) { Role = PassageRole.BuyerHeader },
                new DocumentPassage("the sentence that names the buyer",
                    "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).", true)
                    { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address",
                    "MARAFIQ Yanbu Warehouse, Power & Desalination Plant, Yanbu Al-Sinaiyah, KSA", true),
            ],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void An_EPC_contractors_delivery_address_only_suggests_the_site_owner()
    {
        // Hyundai Engineering & Construction buys on behalf of Saudi Aramco: its own name at the
        // top of the page, "Deliver to: Saudi Aramco Ras Tanura Refinery" in the address, its own
        // domain on the mail. Reading the consignee as the buyer linked the lead to Aramco, who
        // is buying nothing on this job and whose payment terms the rep would then have quoted.
        //
        // Hyundai is a customer here, with no domain registered, and the company-name field names
        // it. That is a claim for ANOTHER customer of this tenant, so the address steps aside and
        // both are offered, the named buyer first. This test used to have Hyundai NOT on the books
        // and still expect the demotion: it pinned "a name that matches nobody is a rival buyer",
        // which is what demoted SEC prints whose company-name field was Arabic, "S.E.C." or a
        // variant of our own vendor block (defect 2). A name that matches nobody no longer demotes.
        var corpus = Corpus(customers:
        [
            new(OtherCustomer, "Saudi Aramco"),
            new(ThirdCustomer, "Hyundai Engineering & Construction"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "procurement@hdec.com",
            CustomerCompanyName = "Hyundai Engineering & Construction",
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(ThirdCustomer, outcome.Candidates[0].CustomerId);
        var aramco = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
        Assert.Equal(Policy.ShipToDemotedConfidence, aramco.Confidence);
        Assert.Contains("delivery address", aramco.Explanation);
        Assert.Contains("Hyundai Engineering & Construction", aramco.Explanation);
        Assert.Contains("which is not Saudi Aramco", aramco.Explanation);
    }

    [Fact]
    public void The_same_EPC_enquiry_links_to_the_contractor_once_the_contractor_is_a_customer()
    {
        // The identical document, with Hyundai on the books and its domain registered: the lead
        // belongs to the company that is actually buying, and Aramco is nowhere near it.
        var corpus = Corpus(
            customers: [new(OtherCustomer, "Saudi Aramco"), new(Sec, "Hyundai Engineering & Construction")],
            identifiers:
            [
                new(1, Sec, CustomerIdentifierType.Domain, "hdec.com", true, 0.95m, CustomerIdentifierSources.MasterData),
            ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "procurement@hdec.com",
            CustomerCompanyName = "Hyundai Engineering & Construction",
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);
    }

    [Fact]
    public void A_header_names_the_buyer_even_when_the_address_names_the_site_owner()
    {
        // Same EPC enquiry, same contractor as a customer, but nobody has registered hdec.com.
        // The header is a statement about WHO IS BUYING and links on its own; the address is a
        // statement about where goods go and is contradicted by the header, so it steps aside.
        var corpus = Corpus(customers:
        [
            new(OtherCustomer, "Saudi Aramco"),
            new(Sec, "Hyundai Engineering & Construction"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "procurement@hdec.com",
            CustomerCompanyName = "Hyundai Engineering & Construction",
            Passages =
            [
                new DocumentPassage("company named on the document", "Hyundai Engineering & Construction", true)
                    { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true),
            ],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void A_portal_relay_sender_does_not_compete_with_the_delivery_address()
    {
        // Every RFQ that arrives through Ariba carries an Ariba sending host, whichever company
        // issued it. The postman is not a rival buyer, so an ordinary SEC enquiry relayed through
        // the network must still link on its delivery address exactly like lead 680.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "noreply@s4.ansmtp.ariba.com",
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void A_name_in_item_text_is_still_only_a_suggestion_whatever_else_the_page_says()
    {
        // Roles are three states, not two: demoting the consignee must not promote item text.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("item text", "Barcode to Saudi Electricity Company standard", false)],
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(Policy.NameInItemTextConfidence, outcome.Confidence);
    }

    // ── A BUYER'S OWN MAIL DOMAIN DOES NOT CONTRADICT ITS OWN NAME ────────────

    [Theory]
    // An SEC buyer writing from SEC's own domain, which nobody has registered on the customer yet.
    [InlineData("ali.nasser@se.com.sa", null, "Saudi Electricity Company", "Saudi Electricity Company-DAMMAM")]
    // The shape of an SEC portal print: our ingestion label as the sender and the buyer's own
    // address printed on the page (SEC bids carry 57322@se.com.sa).
    [InlineData("extraction@pipeline.local", "57322@se.com.sa", "Saudi Electricity Company", "Saudi Electricity Company-JIZAN AREA")]
    [InlineData("buyer@aramco.com", null, "Saudi Aramco", "Saudi Aramco Ras Tanura Refinery")]
    [InlineData("procurement@swcc.gov.sa", null, "Saline Water Conversion Corporation", "Saline Water Conversion Corporation Jubail Plant")]
    // Domains whose letters spell nothing of the legal name. The letter-guessing that spared the
    // rows above demoted every one of these to a 0.70 suggestion.
    [InlineData("buyer@sabic.com", null, "Saudi Basic Industries Corporation", "Saudi Basic Industries Corporation - Jubail")]
    [InlineData("buyer@satorp.com", null, "Saudi Aramco Total Refining and Petrochemical Company", "Saudi Aramco Total Refining and Petrochemical Company, Jubail")]
    [InlineData("khalid@apco-ksa.com", null, "Arabian Pipes Company", "Arabian Pipes Company - Dammam 2nd Industrial City")]
    public void A_buyers_own_unregistered_mail_domain_does_not_contradict_its_name_in_the_delivery_address(
        string sender, string? documentBuyerEmail, string customerName, string deliveryAddress)
    {
        // THE DEFECT: the consignee rule demoted a ship-to name whenever the page carried a sender
        // domain not registered to that customer. S2 returns the moment a domain matches anybody,
        // so by the passage tier EVERY corporate domain was unregistered — the rule demoted the
        // first e-mail from every new customer and every SEC print carrying the buyer's own
        // address, and told the rep "this document is from se.com.sa, which is not Saudi
        // Electricity Company". A domain nothing ties to ANOTHER customer is not a rival. (The
        // Marafiq row that stood here is gone: a one-word name no longer links from an address
        // alone, whatever the mail says — see Lead_680_still_links_when_a_customer_is_named_after_the_city_in_its_address.)
        var corpus = Corpus(customers: [new(Sec, customerName)]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = sender,
            DocumentBuyerEmail = documentBuyerEmail,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain("which is not", outcome.Explanation);
    }

    [Fact]
    public void A_contractors_domain_demotes_the_site_owner_once_a_record_ties_it_to_the_contractor()
    {
        // The Hyundai enquiry with no company-name field extracted, and Hyundai on the books with an
        // address at hdec.com registered to it. The sender domain is then the only thing on the page
        // that disagrees with the address, and it is heard because a record says whose it is.
        //
        // This test used to expect the demotion with NOTHING tying hdec.com to anybody: "hdec.com
        // spells nothing of Saudi Aramco". Spelling is not evidence. The same rule demoted
        // sabic.com, satorp.com, apco-ksa.com and a rep's own group domain (defect 3), and the
        // letter-guessing added to spare some of them read Schneider Electric's se.com as Saudi
        // Electricity's (defect 8). And the contractor itself is now offered, ranked above the site
        // owner (defect 14): before, the only name a rep was shown was the wrong one.
        var corpus = Corpus(
            customers: [new(OtherCustomer, "Saudi Aramco"), new(ThirdCustomer, "Hyundai Engineering & Construction")],
            identifiers:
            [
                new(1, ThirdCustomer, CustomerIdentifierType.Email, "k.lee@hdec.com", true, 1m, "CustomerProfile"),
            ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "procurement@hdec.com",
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        var hyundai = outcome.Candidates[0];
        Assert.Equal(ThirdCustomer, hyundai.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, hyundai.ReasonCode);
        Assert.Contains("k.lee@hdec.com is registered to Hyundai Engineering & Construction", hyundai.Explanation);
        var aramco = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
        Assert.Equal(Policy.ShipToDemotedConfidence, aramco.Confidence);
        Assert.Contains("this document is from hdec.com, which is not Saudi Aramco", aramco.Explanation);
    }

    [Fact]
    public void A_mail_domain_speaks_against_an_address_only_when_a_record_ties_it_to_someone_else()
    {
        // Arabian Pipes Company mails from apco-ksa.com, and nothing in the name spells that. This
        // test used to expect the unrecorded domain to demote the address (defect 3 pinned). A
        // domain nobody recorded is unknown, not a rival; a domain a record ties to this customer is
        // this customer's; a domain a record ties to ANOTHER customer is the one that is heard.
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "khalid@apco-ksa.com",
            Passages = [new DocumentPassage("delivery address", "Arabian Pipes Company - Dammam 2nd Industrial City", true)],
        };
        IReadOnlyList<CustomerNameSnapshot> customers = [new(Sec, "Arabian Pipes Company"), new(ThirdCustomer, "Al-Babtain Contracting")];

        var unrecorded = CustomerIdentityResolver.Resolve(evidence, Corpus(customers: customers), Policy);
        Assert.Equal(Sec, unrecorded.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, unrecorded.Confidence);
        Assert.DoesNotContain("which is not", unrecorded.Explanation);

        var recordedForSomeoneElse = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: customers,
            contacts: [new(79, ThirdCustomer, "tenders@apco-ksa.com", "Tenders", "Desk")]), Policy);
        Assert.Null(recordedForSomeoneElse.CustomerId);
        Assert.Equal(ThirdCustomer, recordedForSomeoneElse.Candidates[0].CustomerId);
        var pipes = Assert.Single(recordedForSomeoneElse.Candidates, candidate => candidate.CustomerId == Sec);
        Assert.Equal(Policy.ShipToDemotedConfidence, pipes.Confidence);
        Assert.Contains("Tenders Desk, a contact on Al-Babtain Contracting, writes from it", pipes.Explanation);

        var byContact = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: customers,
            contacts: [new(77, Sec, "Procurement@APCO-KSA.com", "Procurement", "Desk")]), Policy);
        Assert.Equal(Sec, byContact.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, byContact.Confidence);

        var byPriorLead = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: customers,
            priorSenders: [new("khalid@apco-ksa.com", Sec)]), Policy);
        Assert.Equal(Sec, byPriorLead.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, byPriorLead.Confidence);
    }

    [Fact]
    public void A_relay_domain_taught_before_the_learner_refused_it_can_no_longer_link_at_the_domain_tier()
    {
        // Until 2026-09-12 the learner refused only free mail as a Domain, so a confirmation on a
        // bidnet.com-relayed RFQ proposed bidnet.com as that customer's Domain. The row outlives
        // the fix, and S2 would link every later bidnet RFQ — from any buyer — to that customer at
        // 0.95. The relay names the postman: this SEC enquiry links on its delivery address and the
        // stale row does not even reach the candidate list.
        var corpus = Corpus(
            customers: [new(OtherCustomer, "Saudi Aramco"), new(Sec, "Saudi Electricity Company")],
            identifiers:
            [
                new(1, OtherCustomer, CustomerIdentifierType.Domain, "bidnet.com", true, 0.95m,
                    CustomerIdentifierSources.LeadReviewLearned),
            ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "alerts@bidnet.com",
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
    }

    [Fact]
    public void A_rep_forwarding_from_a_tenant_domain_that_is_not_an_ingestion_mailbox_does_not_demote_the_address()
    {
        // Our own group domain, which is not one of the configured ingestion mailboxes, counted as a
        // rival buyer: "this document is from alquraishi-group.com, which is not Saudi Electricity
        // Company", on a lead a colleague simply forwarded.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "zack@alquraishi-group.com",
            TenantSelfDomains = ["rfq@alquraishi.com.sa"],
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain("which is not", outcome.Explanation);
    }

    [Fact]
    public void Schneider_Electrics_se_com_is_not_Saudi_Electricity_and_its_own_records_say_whose_it_is()
    {
        // THE DEFECT: the domain letter-guess read "se" as the initials of Saudi Electricity, so an
        // enquiry from Schneider Electric's real domain for an SEC substation was treated as SEC
        // speaking and auto-linked to SEC at 0.88 — the contractor case the consignee rule exists
        // for. se.com is Schneider's because Schneider's own contact writes from it.
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(ThirdCustomer, "Schneider Electric")],
            contacts: [new(78, ThirdCustomer, "m.haddad@se.com", "Maher", "Haddad")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "rfq.ksa@se.com",
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company - Riyadh PP9 Substation", true)],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(ThirdCustomer, outcome.Candidates[0].CustomerId);
        var sec = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == Sec);
        Assert.Equal(Policy.ShipToDemotedConfidence, sec.Confidence);
        Assert.Contains("this document is from se.com, which is not Saudi Electricity Company", sec.Explanation);
    }

    // ── ONE WORD IN AN ADDRESS IS A PLACE AS OFTEN AS A COMPANY ───────────────

    [Theory]
    [InlineData("Saudi Electricity Company-DAMMAM", "Al Dammam Trading Co.")]
    [InlineData("Saudi Electricity Company-JIZAN AREA", "Jizan Establishment")]
    public void Lead_680_still_links_when_a_customer_is_named_after_the_city_in_its_address(
        string deliveryAddress, string cityNamedCustomer)
    {
        // THE DEFECT: the one-word scan turns "Al Dammam Trading Co." into DAMMAM and "Jizan
        // Establishment" into JIZAN. Both were found in lead 680's own delivery address beside
        // Saudi Electricity Company, both counted as the consignee, and the lead went AMBIGUOUS in
        // any tenant that trades with a house named after a city.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, cityNamedCustomer)]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            SenderEmail = "extraction@pipeline.local",
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(0.88m, outcome.Confidence);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
    }

    [Fact]
    public void A_one_word_name_alone_in_a_delivery_address_is_offered_and_never_applied()
    {
        var corpus = Corpus(customers: [new(OtherCustomer, "Al Dammam Trading Co.")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Dammam 2nd Industrial City, Warehouse 7", true)],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(Policy.ShipToDemotedConfidence, outcome.Confidence);
        Assert.Equal(OtherCustomer, Assert.Single(outcome.Candidates).CustomerId);
        Assert.Contains("offered rather than applied", outcome.Explanation);
    }

    [Fact]
    public void A_full_name_and_a_one_word_name_in_one_header_are_one_statement_about_the_full_name()
    {
        // "Saudi Electricity Company Dammam" in the company-name field is SEC saying where it is,
        // not SEC and Al Dammam Trading both buying. Read as two names it went AMBIGUOUS.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Al Dammam Trading Co.")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = "Saudi Electricity Company Dammam",
            Passages =
            [
                new DocumentPassage("company named on the document", "Saudi Electricity Company Dammam", true)
                    { Role = PassageRole.BuyerHeader },
            ],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void A_one_word_name_in_a_header_cannot_push_a_full_name_out_of_the_address()
    {
        // A header built from a person's name ("Requisitioner: Ahmed Al-Ghamdi") reads GHAMDI for
        // "Al-Ghamdi Trading Est". It may not decide against "Saudi Electricity Company" in the
        // address: that would turn a lead a person must choose into a lead linked to the wrong
        // company.
        //
        // This test used to expect AMBIGUOUS, and that enshrined the defect it was written against:
        // the person's one word still sat in the linking set beside SEC, so lead 680's own shape
        // asked a person, where before headers were read it linked SEC at 0.88. A one word name that
        // is not the header's whole statement is now only offered, and SEC links on its address.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Al-Ghamdi Trading Est")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages =
            [
                new DocumentPassage("requisitioner field", "Ahmed Al-Ghamdi", true) { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true),
            ],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain(outcome.Candidates, candidate =>
            candidate.CustomerId == OtherCustomer && candidate.Confidence >= Policy.MinimumAutoLinkConfidence);
    }

    // ── ONLY A CLAIM FOR ANOTHER CUSTOMER COMPETES WITH THE ADDRESS ───────────

    [Theory]
    [InlineData("الشركة السعودية للكهرباء", null)]                                  // SEC's own name in Arabic
    [InlineData("S.E.C.", null)]                                                     // SEC's initials, dotted
    [InlineData("A.Z. ALQURAISHI & PARTNERS ESOSA", "ALI ZAID AL-QURAISHI & PARTNERS")] // a variant of OUR vendor block
    public void A_company_name_field_that_names_no_other_customer_does_not_demote_the_delivery_address(
        string companyName, string? tenantName)
    {
        // THE DEFECT: any company-name field that was not exactly this customer's key counted as a
        // rival buyer, so the right buyer written another way demoted lead 680's shape to a 0.70
        // suggestion and told the rep the document named a different organisation. On an SEC print
        // the only company printed is often OUR vendor block, which the self guard does not always
        // catch. A field that names nobody else in this tenant is not a claim for anybody else.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = companyName,
            TenantSelfNameKeys = tenantName is null ? [] : [tenantName],
            Passages =
            [
                new DocumentPassage("company named on the document", companyName, true) { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true),
            ],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain("which is not", outcome.Explanation);
    }

    // ── A CUSTOMER'S NAME CAN BE THE FIRST HALF OF ANOTHER COMPANY'S ──────────

    [Theory]
    [InlineData(null, false)]      // only the parent is on the books
    [InlineData("SATORP", false)]  // SATORP recorded under its trade name, which the address never writes
    [InlineData(null, true)]       // the same long name as the buyer sentence
    public void The_parents_name_at_the_start_of_a_longer_company_name_is_offered_not_applied(
        string? satorpRecordedAs, bool asHeader)
    {
        // THE DEFECT: "Saudi Aramco Total Refining and Petrochemical Company, Jubail" is SATORP's
        // address. The longest-name rule only helps when SATORP is on the books under its full legal
        // name; otherwise "Saudi Aramco" was the only name found and the lead linked to Aramco at
        // 0.88. An invoice to Aramco against a SATORP order is not paid.
        IReadOnlyList<CustomerNameSnapshot> customers = satorpRecordedAs is null
            ? [new(OtherCustomer, "Saudi Aramco")]
            : [new(OtherCustomer, "Saudi Aramco"), new(ThirdCustomer, satorpRecordedAs)];
        var passage = asHeader
            ? new DocumentPassage("the sentence that names the buyer",
                "Saudi Aramco Total Refining and Petrochemical Company invites bidders for RFQ 6600012345", true)
                { Role = PassageRole.BuyerHeader }
            : new DocumentPassage("delivery address", "Saudi Aramco Total Refining and Petrochemical Company, Jubail", true);

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [passage],
        }, Corpus(customers: customers), Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        var aramco = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
        Assert.Equal(Policy.ShipToDemotedConfidence, aramco.Confidence);
        Assert.Contains("\"Saudi Aramco Total Refining and Petrochemical Company\"", aramco.Explanation);
    }

    [Fact]
    public void SATORP_recorded_with_its_trade_name_in_brackets_is_found_in_its_own_address()
    {
        // "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)" keys to "... PETROCHEMICAL
        // SATORP", which no address writes, so only the parent's name was found inside SATORP's
        // address and the lead linked to Aramco.
        var corpus = Corpus(customers:
        [
            new(OtherCustomer, "Saudi Aramco"),
            new(ThirdCustomer, "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)"),
        ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Total Refining and Petrochemical Company, Jubail", true)],
        }, corpus, Policy);

        Assert.Equal(ThirdCustomer, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
    }

    [Theory]
    [InlineData("Saudi Aramco", false, "Saudi Aramco Ras Tanura Refinery")]
    [InlineData("Saudi Aramco", true, "SAUDI ARAMCO INVITES BIDDERS FOR THE SUPPLY OF VALVES TO THE COMPANY")]
    [InlineData("Saudi Aramco", true, "Saudi Aramco invites bidders to quote; the Company reserves the right to reject any bid")]
    [InlineData("Saudi Electricity Company", false, "Saudi Electricity Company Ltd., Dammam")]
    [InlineData("Saudi Electricity Company", false, "Saudi Electricity Company-JIZAN AREA")]
    [InlineData("Saudi Electricity Company", false, "Deliver to: Saudi Electricity Company - Riyadh PP9 Substation")]
    // The bracket and dash rules below read just as narrowly: a short site code in brackets, a site
    // after a dash, and the customer's own initials in brackets are all still the customer.
    [InlineData("Saudi Aramco", false, "Saudi Aramco Ras Tanura Refinery (RTR)")]
    [InlineData("Saudi Aramco", false, "Saudi Aramco - Ras Tanura Refinery")]
    [InlineData("Saudi Electricity Company", false, "Saudi Electricity Company (SEC), Dammam")]
    [InlineData("Saudi Electricity Company", false, "Saudi Electricity Company - Eastern Operating Area")]
    public void A_name_followed_by_a_place_a_sentence_or_its_own_legal_words_still_links(
        string customerName, bool asHeader, string text)
    {
        // The longer-company-name rule reads narrowly, because every one of these is the customer
        // itself: a site, a buyer sentence that later says "the Company", its own legal suffix.
        var passage = asHeader
            ? new DocumentPassage("the sentence that names the buyer", text, true) { Role = PassageRole.BuyerHeader }
            : new DocumentPassage("delivery address", text, true);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [passage],
        }, Corpus(customers: [new(Sec, customerName)]), Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    // ── A NAME MUST NAME ONE COMPANY BEFORE A PASSAGE CAN LINK ON IT ──────────

    [Theory]
    [InlineData(CustomerIdentifierSources.LeadReviewLearned, CustomerIdentifierType.Alias, "SAUDI ARABIA", true,
        "Saudi Kayan Petrochemical Company, Jubail, Kingdom of Saudi Arabia invites bidders")]
    [InlineData(CustomerIdentifierSources.LeadReviewLearned, CustomerIdentifierType.Alias, "SAUDI ARABIA", false,
        "Ras Al-Khair, Saudi Arabia")]
    [InlineData("CustomerProfile", CustomerIdentifierType.CustomerName, "ARABIAN GULF", false,
        "Arabian Gulf Road, Dammam")]
    public void A_taught_name_with_no_word_that_belongs_to_one_company_never_links_a_passage(
        string source, CustomerIdentifierType type, string key, bool asHeader, string text)
    {
        // THE DEFECT: the extractor took the country line of an address block as the company name, a
        // reviewer rightly linked the lead to Saudi Aramco, and "SAUDI ARABIA" became a trusted
        // Aramco alias. The scan accepted any taught alias of three characters, so every buyer
        // sentence and delivery address mentioning Saudi Arabia then linked to Aramco at 0.88 — a
        // Saudi Kayan RFQ included.
        var corpus = Corpus(
            customers: [new(OtherCustomer, "Saudi Aramco")],
            identifiers: [new(1, OtherCustomer, type, key, true, 0.90m, source)]);
        var passage = asHeader
            ? new DocumentPassage("the sentence that names the buyer", text, true) { Role = PassageRole.BuyerHeader }
            : new DocumentPassage("delivery address", text, true);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [passage],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
    }

    [Fact]
    public void A_customer_whose_name_is_only_generic_words_is_not_found_on_every_page_that_uses_them()
    {
        // "Arabian International Company" keys to the one word ARABIAN, which every "Arabian Pipes
        // Company" buyer sentence contains: an Arabian Pipes RFQ linked to it at 0.88.
        var corpus = Corpus(customers: [new(OtherCustomer, "Arabian International Company")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages =
            [
                new DocumentPassage("the sentence that names the buyer", "Arabian Pipes Company invites bidders for line pipe", true)
                    { Role = PassageRole.BuyerHeader },
            ],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Empty(outcome.Candidates);
    }

    [Fact]
    public void A_taught_generic_name_does_not_link_even_when_the_company_name_field_repeats_it_exactly()
    {
        // The exact-match tier reads the same alias. The extractor that took "SAUDI ARABIA" off one
        // address block takes it off the next buyer's print too, and the alias taught on the first
        // lead linked the second to Saudi Aramco at 0.90 with no word on the page belonging to Aramco.
        var corpus = Corpus(
            customers: [new(OtherCustomer, "Saudi Aramco"), new(Sec, "Saudi Electricity Company")],
            identifiers: [Alias(1, OtherCustomer, "SAUDI ARABIA")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = "Saudi Arabia",
            Passages =
            [
                new DocumentPassage("company named on the document", "Saudi Arabia", true) { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true),
            ],
        }, corpus, Policy);

        // Nor does that alias speak against the customer the address does name.
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    // ── SHARED INITIALS ARE COUNTED OVER THE WHOLE TENANT ─────────────────────

    [Fact]
    public void Initials_a_customer_outside_the_loaded_list_shares_identify_neither()
    {
        // Above the name-scan cap the loader narrows customers by leading letters. "SCC Store,
        // Sudair Industrial City" loaded Sudair Ceramics and not Saudi Cable, SCC looked unique, and
        // a large tenant auto-linked Sudair Ceramics at 0.85 where a small tenant left it unresolved.
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "SCC Store, Sudair Industrial City", true)],
        };
        var loaded = Corpus(customers: [new(OtherCustomer, "Sudair Ceramics Company")]);
        var tenantWide = CustomerIdentityResolver.SharedDerivedAcronyms(
            [new(Sec, "Saudi Cable Company"), new(OtherCustomer, "Sudair Ceramics Company"), new(ThirdCustomer, "Marafiq")]);
        Assert.Equal(new[] { "SCC" }, tenantWide.ToArray());
        // One customer loaded twice shares its initials with nobody.
        Assert.Empty(CustomerIdentityResolver.SharedDerivedAcronyms(
            [new(Sec, "Saudi Cable Company"), new(Sec, "Saudi Cable Company")]));

        var outcome = CustomerIdentityResolver.Resolve(evidence, loaded, Policy, tenantWide);

        Assert.Null(outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.ReasonCode == CustomerMatchReasonCodes.NameInDocument);
        // The tenant-wide set is what makes the difference: counted over the loaded list alone, the
        // same page still links. A caller that has not been given the set behaves exactly as before.
        Assert.Equal(OtherCustomer, CustomerIdentityResolver.Resolve(evidence, loaded, Policy).CustomerId);
    }

    // ── WHAT THE EXACT TIERS MAY TRUST ────────────────────────────────────────

    [Theory]
    [InlineData(CustomerIdentifierType.Email, "ahmed@alquraishi-trading.com")]
    [InlineData(CustomerIdentifierType.Domain, "alquraishi-trading.com")]
    public void A_mailbox_the_learner_refused_to_vouch_for_never_links_at_the_exact_tiers(
        CustomerIdentifierType type, string value)
    {
        // THE DEFECT: the learner demotes a mailbox on a domain nobody tied to the chosen customer
        // to LeadReviewUnverified, but S1 and S2 never read Source, so the demoted row linked every
        // later mail from that sender at 1.00 or 0.95 anyway. The Source is what decides: the same
        // row entered by an administrator still links.
        var evidence = new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "ahmed@alquraishi-trading.com" };

        var demoted = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, type, value, false, 0.50m, CustomerIdentifierSources.LeadReviewUnverified)]), Policy);
        Assert.Null(demoted.CustomerId);
        Assert.DoesNotContain(demoted.Candidates, candidate =>
            candidate.ReasonCode is CustomerMatchReasonCodes.SenderEmailExact or CustomerMatchReasonCodes.SenderDomain);

        var entered = CustomerIdentityResolver.Resolve(evidence, Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, type, value, true, 1m, CustomerIdentifierSources.MasterData)]), Policy);
        Assert.Equal(Sec, entered.CustomerId);
    }

    [Fact]
    public void A_host_under_the_tenants_own_mail_domain_is_never_a_customers_address_or_domain()
    {
        // The tenant's own domains were compared exactly, so sales.alquraishi.com.sa was not "us"
        // when rfq@alquraishi.com.sa was the mailbox, and a row learned from a colleague's forward
        // linked every later forward from our own staff to one customer.
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers:
            [
                new(1, Sec, CustomerIdentifierType.Email, "ahmed@sales.alquraishi.com.sa", true, 1m, "CustomerProfile"),
                new(2, Sec, CustomerIdentifierType.Domain, "sales.alquraishi.com.sa", true, 0.95m, CustomerIdentifierSources.MasterData),
            ]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "ahmed@sales.alquraishi.com.sa",
            TenantSelfDomains = ["rfq@alquraishi.com.sa"],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NoEvidence, outcome.ReasonCode);
    }

    // ── SEAMS: WHAT ONE PART WRITES, THE NEXT MUST READ THE SAME WAY ─────────
    //
    // Each test below was green in every lane's own suite and red when the lanes met: one part
    // (the learner, routing, the setup screen) holds a rule the resolver read differently.

    [Fact]
    public void A_learned_one_word_of_a_customers_name_cannot_make_lead_680_ambiguous()
    {
        // The learner trusts a print whose every distinctive word is the customer's own, so "YANBU"
        // confirmed once for Yanbu Cement Company is a verified alias. The passage scan gave a taught
        // alias full rights, so it stood beside SEC in the delivery address and lead 680's own shape
        // went AMBIGUOUS: defect #1, back through learning.
        const long yanbuCement = OtherCustomer;
        Assert.True(CustomerAliasLearner.ResemblesCustomerName("YANBU", "Yanbu Cement Company"));
        Assert.False(CustomerAliasLearner.NamesAnotherCustomerAtLeastAsClosely(
            "YANBU", "Yanbu Cement Company", ["Saudi Electricity Company"]));

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-YANBU", true)],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(yanbuCement, "Yanbu Cement Company")],
            identifiers: [Alias(1, yanbuCement, "YANBU")]), Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(0.88m, outcome.Confidence);
    }

    [Fact]
    public void A_learned_sector_word_cannot_push_a_one_word_customer_out_of_an_address_and_link_itself()
    {
        // "ELECTRICITY" passes the learner's gate for Saudi Electricity Company. With full rights it
        // also outranked the one-word name Marafiq in Marafiq's own plant address, so Marafiq was
        // suppressed and the page linked to SEC at 0.88. Both are one word in an address now, and a
        // person picks.
        const long marafiq = OtherCustomer;
        Assert.True(CustomerAliasLearner.ResemblesCustomerName("ELECTRICITY", "Saudi Electricity Company"));

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            Passages = [new DocumentPassage("delivery address", "Marafiq Power & Electricity Plant, Jubail", true)],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(marafiq, "Marafiq")],
            identifiers: [Alias(1, Sec, "ELECTRICITY")]), Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Contains(outcome.Candidates, c => c.CustomerId == marafiq);
        Assert.All(outcome.Candidates, c => Assert.True(c.Confidence < Policy.MinimumAutoLinkConfidence,
            $"{c.CustomerName} was offered at {c.Confidence}, which is link strength."));
    }

    [Fact]
    public void A_learned_one_word_of_the_name_alone_in_someone_elses_site_address_is_offered_not_applied()
    {
        const long yanbuCement = OtherCustomer;
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 12,
            Passages = [new DocumentPassage("delivery address", "YANBU Industrial City, Gate 3", true)],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(yanbuCement, "Yanbu Cement Company")],
            identifiers: [Alias(1, yanbuCement, "YANBU")]), Policy);

        Assert.Null(outcome.CustomerId);
        var offered = Assert.Single(outcome.Candidates);
        Assert.Equal(yanbuCement, offered.CustomerId);
        Assert.Equal(Policy.ShipToDemotedConfidence, offered.Confidence);
    }

    [Fact]
    public void Learned_initials_still_link_from_an_address_and_a_learned_word_of_the_name_still_links_from_a_header()
    {
        // What the rule above must not take away: "SEC" is a deliberate abbreviation, and a buyer
        // sentence is a company naming itself.
        const long yanbuCement = OtherCustomer;
        var initials = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 13,
            Passages = [new DocumentPassage("delivery address", "SEC Materials West Plant", true)],
        }, Corpus(customers: [new(Sec, "Saudi Electricity Company")], identifiers: [Alias(1, Sec, "SEC")]), Policy);
        Assert.Equal(Sec, initials.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, initials.Status);

        var header = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 14,
            Passages = [new DocumentPassage("the sentence that names the buyer", "YANBU invites bidders for the kiln spares below", true)
                { Role = PassageRole.BuyerHeader }],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(yanbuCement, "Yanbu Cement Company")],
            identifiers: [Alias(1, yanbuCement, "YANBU")]), Policy);
        Assert.Equal(yanbuCement, header.CustomerId);
        Assert.Equal(0.88m, header.Confidence);
    }

    [Theory]
    [InlineData(CustomerIdentifierType.Email, "57322@se.com.sa", CustomerIdentifierSources.MasterData)]
    [InlineData(CustomerIdentifierType.Domain, "se.com.sa", CustomerIdentifierSources.MasterData)]
    [InlineData(CustomerIdentifierType.Email, "57322@se.com.sa", "CustomerContact")]
    public void A_detail_a_person_marked_unverified_no_longer_links_just_as_routing_already_refuses_it(
        CustomerIdentifierType type, string value, string source)
    {
        // Unticking "verified" on the setup screen is the one correction an administrator can make to
        // a wrong fact today. Routing's engine honours it; the exact and domain tiers here did not, so
        // SEC's mail still linked to Saudi Aramco at 1.00 or 0.95 while routing refused the same row.
        const long aramco = OtherCustomer;
        var evidence = new LeadClientEvidence { BusinessUnitId = 1, LeadId = 15, SenderEmail = "57322@se.com.sa" };
        CustomerIdentifierSnapshot Row(bool verified) => new(1, aramco, type, value, verified, 1m, source);
        var customers = new CustomerNameSnapshot[] { new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco") };

        var unverified = CustomerIdentityResolver.Resolve(evidence, Corpus(customers, [Row(false)]), Policy);
        Assert.Null(unverified.CustomerId);
        Assert.DoesNotContain(unverified.Candidates, c => c.Confidence >= Policy.MinimumAutoLinkConfidence);

        var verified = CustomerIdentityResolver.Resolve(evidence, Corpus(customers, [Row(true)]), Policy);
        Assert.Equal(aramco, verified.CustomerId);
    }

    [Fact]
    public void A_record_a_person_marked_unverified_does_not_say_whose_domain_it_is()
    {
        // The domain rule reads the same flag: an Email row an administrator unverified is not a fact
        // about hdec.com, so it neither demotes Aramco's address nor offers Hyundai.
        const long aramco = OtherCustomer, hyundai = ThirdCustomer;
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 16,
            SenderEmail = "procurement@hdec.com",
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, Corpus(
            customers: [new(aramco, "Saudi Aramco"), new(hyundai, "Hyundai Engineering & Construction")],
            identifiers: [new(1, hyundai, CustomerIdentifierType.Email, "k.lee@hdec.com", false, 1m, CustomerIdentifierSources.MasterData)]),
            Policy);

        Assert.Equal(aramco, outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, c => c.CustomerId == hyundai);
    }

    [Theory]
    // A colleague with no Nexora login, on a staff domain that spells the tenant's name.
    [InlineData("ahmed@alquraishi.com.sa", true)]
    // The mailbox's own domain, and a host under it.
    [InlineData("sales.desk@alquraishi.com", true)]
    [InlineData("ahmed@ksa.alquraishi.com", true)]
    // Buyers, including one whose initials are two letters.
    [InlineData("57322@se.com.sa", false)]
    [InlineData("buyer@sabic.com", false)]
    [InlineData("procurement@hdec.com", false)]
    [InlineData(null, false)]
    public void Ours_is_one_answer_for_the_learner_the_resolver_and_routing(string? address, bool ours)
        => Assert.Equal(ours, TenantSelfIdentity.IsOurs(
            address, ["rfq@alquraishi.com"], ["ALI ZAID AL-QURAISHI & PARTNERS", null]));

    [Fact]
    public void A_domain_that_spells_our_own_name_is_ours_even_when_no_user_or_mailbox_writes_from_it()
    {
        // The learner refuses to learn from a domain whose name spells the tenant's; the resolver knew
        // only the mailbox and user domains. So the rows a reviewer's confirmation of one forwarded
        // bid minted before the learner refused them (the colleague's address at 1.00, our domain at
        // 0.95) still linked every later forward from a colleague with no login to SEC, whatever the
        // attachment said. Here the attachment says Aramco, and Aramco is what it links.
        const long aramco = OtherCustomer;
        Assert.True(CustomerAliasLearner.DomainLabelSpellsName("alquraishi.com.sa", "ALI ZAID AL-QURAISHI & PARTNERS"));
        var customers = new CustomerNameSnapshot[] { new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco") };
        var legacy = new CustomerIdentifierSnapshot[]
        {
            new(1, Sec, CustomerIdentifierType.Email, "ahmed@alquraishi.com.sa", true, 1m, CustomerIdentifierSources.LeadReviewLearned),
            new(2, Sec, CustomerIdentifierType.Domain, "alquraishi.com.sa", true, 0.95m, CustomerIdentifierSources.LeadReviewLearned),
            new(3, Sec, CustomerIdentifierType.Domain, "se.com.sa", true, 0.95m, "CustomerProfile"),
        };

        var forward = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 17,
            SenderEmail = "ahmed@alquraishi.com.sa",
            TenantSelfDomains = ["rfq@alquraishi.com"],
            TenantSelfNameKeys = ["ALI ZAID AL-QURAISHI & PARTNERS"],
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, Corpus(customers, legacy), Policy);
        Assert.Equal(aramco, forward.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, forward.ReasonCode);

        // The customer's own domain, beside the same tenant name, still links at the domain tier.
        var buyer = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 18,
            SenderEmail = "57322@se.com.sa",
            TenantSelfDomains = ["rfq@alquraishi.com"],
            TenantSelfNameKeys = ["ALI ZAID AL-QURAISHI & PARTNERS"],
        }, Corpus(customers, legacy), Policy);
        Assert.Equal(Sec, buyer.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, buyer.ReasonCode);
    }

    // ── ONE WORD IN A HEADER IS A PLACE OR A PERSON UNLESS IT IS THE STATEMENT ─

    private const string MarafiqLegalName = "Power and Water Utility Company for Jubail and Yanbu (Marafiq)";

    [Theory]
    [InlineData("the sentence that names the buyer", "Dammam Area Materials Procurement invites bidders to quote", "Al Dammam Trading Co.", "Saudi Electricity Company-DAMMAM")]
    [InlineData("the sentence that names the buyer", "Tender issued by the Dammam office", "Al Dammam Trading Co.", "Saudi Electricity Company-DAMMAM")]
    [InlineData("company named on the document", "Eastern Operating Area - Dammam", "Al Dammam Trading Co.", "Saudi Electricity Company-DAMMAM")]
    [InlineData("the sentence that names the buyer", "Jubail Operations Procurement invites bidders to quote", "Jubail Trading Est", "Saudi Electricity Company-JUBAIL")]
    [InlineData("the name on the sender's mailbox", "Jubail Procurement", "Jubail Trading Est", "Saudi Electricity Company-JUBAIL")]
    [InlineData("company named on the document", "Rashid Al-Otaibi", "Al-Rashid Trading", "Saudi Electricity Company-DAMMAM")]
    [InlineData("purchaser field", "Ahmed Al-Ghamdi", "Al-Ghamdi Trading Est", "Saudi Electricity Company-DAMMAM")]
    public void A_place_or_a_persons_name_in_a_header_cannot_make_lead_680_ambiguous(
        string where, string headerText, string oneWordCustomer, string deliveryAddress)
    {
        // THE DEFECT: every hit in a header went straight into the linking set, one-word names
        // included. The buyer sentence, the company-name field, a relay's display name and a
        // purchaser column all carry cities and people, and in this market a trading house is often
        // named after either: "Al Dammam Trading Co." keys to DAMMAM, "Al-Ghamdi Trading Est" to
        // GHAMDI. Each stood beside Saudi Electricity Company in the address and the lead went
        // AMBIGUOUS, where before headers were read the same page linked SEC at 0.88.
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, oneWordCustomer)]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            CustomerCompanyName = where == "company named on the document" ? headerText : null,
            Passages =
            [
                new DocumentPassage(where, headerText, true) { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address", deliveryAddress, true),
            ],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain(outcome.Candidates, candidate =>
            candidate.CustomerId == OtherCustomer && candidate.Confidence >= Policy.MinimumAutoLinkConfidence);
    }

    [Fact]
    public void A_city_in_the_sentence_of_a_buyer_who_is_not_a_customer_links_nobody()
    {
        // Saudi Kayan is not a customer. Its sentence mentions Jubail, the whole key of "Al Jubail
        // Trading Est", and that one word linked Saudi Kayan's RFQ to the trading house at 0.88.
        var corpus = Corpus(customers: [new(OtherCustomer, "Saudi Aramco"), new(ThirdCustomer, "Al Jubail Trading Est")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages =
            [
                new DocumentPassage("the sentence that names the buyer",
                    "Saudi Kayan Petrochemical Company, Jubail invites bidders", true) { Role = PassageRole.BuyerHeader },
            ],
        }, corpus, Policy);

        Assert.Null(outcome.CustomerId);
        Assert.All(outcome.Candidates, candidate => Assert.True(candidate.Confidence < Policy.MinimumAutoLinkConfidence,
            $"{candidate.CustomerName} was offered at {candidate.Confidence}, which is link strength."));
    }

    [Theory]
    [InlineData("JUBAIL", MarafiqLegalName, "company named on the document", "Royal Commission for Jubail and Yanbu")]
    [InlineData("JUBAIL", MarafiqLegalName, "the sentence that names the buyer", "Royal Commission for Jubail and Yanbu invites bidders for the supply of gate valves")]
    [InlineData("JUBAIL", MarafiqLegalName, "company named on the document", "Jubail Industrial College")]
    [InlineData("RIYADH", "Riyadh Cables Group Company", "the sentence that names the buyer", "Saudi Kayan Petrochemical Company, Riyadh invites bidders")]
    public void A_learned_word_of_a_customers_name_links_from_a_header_only_where_the_header_is_that_word(
        string taught, string ownerName, string where, string headerText)
    {
        // THE DEFECT: a company-name field holding the city line of an address block ("JUBAIL"),
        // confirmed once for Marafiq, is a trusted alias because JUBAIL is one of Marafiq's own words.
        // Headers gave it full rights, so the next enquiry from the Royal Commission for Jubail and
        // Yanbu, who is not a customer, linked to Marafiq at 0.88.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = where == "company named on the document" ? headerText : null,
            Passages = [new DocumentPassage(where, headerText, true) { Role = PassageRole.BuyerHeader }],
        }, Corpus(
            customers: [new(OtherCustomer, ownerName), new(Sec, "Saudi Electricity Company")],
            identifiers: [Alias(1, OtherCustomer, taught)]), Policy);

        Assert.Null(outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.Confidence >= Policy.MinimumAutoLinkConfidence);
    }

    [Fact]
    public void A_learned_city_word_does_not_turn_another_customers_header_ambiguous()
    {
        // "SABIC Jubail" in the company-name field, with Marafiq's learned JUBAIL: the city word stood
        // beside SABIC and the header went AMBIGUOUS. Neither one word is the whole header, so neither
        // links, and Marafiq is never offered at link strength.
        const long marafiq = OtherCustomer, sabic = ThirdCustomer;
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = "SABIC Jubail",
            Passages = [new DocumentPassage("company named on the document", "SABIC Jubail", true) { Role = PassageRole.BuyerHeader }],
        }, Corpus(
            customers: [new(marafiq, MarafiqLegalName), new(sabic, "SABIC")],
            identifiers: [Alias(1, marafiq, "JUBAIL")]), Policy);

        Assert.NotEqual(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
        Assert.NotEqual(marafiq, outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate =>
            candidate.CustomerId == marafiq && candidate.Confidence >= Policy.MinimumAutoLinkConfidence);
    }

    // ── THE PARENT'S NAME BEFORE A BRACKETED TRADE NAME OR A DASHED COMPANY NAME ─

    [Theory]
    [InlineData("Saudi Aramco Total Refining & Petrochemical (SATORP), Jubail", "SATORP")]
    [InlineData("Saudi Aramco Total Refining & Petrochemical (SATORP), Jubail", null)]
    [InlineData("SAUDI ARAMCO - TOTAL REFINING AND PETROCHEMICAL COMPANY, JUBAIL", null)]
    [InlineData("SAUDI ARAMCO - TOTAL REFINING AND PETROCHEMICAL COMPANY, JUBAIL", "SATORP")]
    [InlineData("Saudi Aramco Jubail Refinery (SASREF), Jubail Industrial City", null)]
    public void The_parent_before_a_bracketed_trade_name_or_a_dashed_company_name_is_offered_not_applied(
        string deliveryAddress, string? satorpRecordedAs)
    {
        // THE DEFECT: the longer-company-name rule stopped at a bracket and at " - ", and only a
        // legal-form word made a run of words a company name. SATORP's name with its trade name in
        // brackets, SATORP's legal name set off from the parent's by a dash, and SASREF's refinery name
        // all left "Saudi Aramco" standing on its own, and each linked to Aramco at 0.88. An invoice to
        // Aramco against a SATORP or SASREF order is not paid.
        IReadOnlyList<CustomerNameSnapshot> customers = satorpRecordedAs is null
            ? [new(OtherCustomer, "Saudi Aramco")]
            : [new(OtherCustomer, "Saudi Aramco"), new(ThirdCustomer, satorpRecordedAs)];
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers: customers), Policy);

        Assert.Null(outcome.CustomerId);
        var aramco = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
        Assert.Equal(Policy.ShipToDemotedConfidence, aramco.Confidence);
        // SATORP written in the brackets is offered too, not swallowed by the parent's longer hit.
        if (satorpRecordedAs is not null && deliveryAddress.Contains("(SATORP)", StringComparison.Ordinal))
            Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == ThirdCustomer);
    }

    // ── A TAUGHT NAME IS CHECKED AGAIN EVERY TIME IT IS READ ──────────────────

    [Theory]
    // #15's own incident row: SEC's name taught to Saudi Aramco by a mis-click before the gate existed.
    [InlineData("SAUDI ELECTRICITY", "Saudi Aramco")]
    // Taught to another company while SEC was not yet a customer, when the gate had nobody to compare it with.
    [InlineData("SAUDI ELECTRICITY", "National Electricity Transmission Company")]
    public void A_name_taught_to_one_customer_gives_way_to_the_customer_whose_own_name_it_is(
        string taught, string wrongOwner)
    {
        // THE DEFECT: the learner's gate runs only at the moment of teaching, and the resolver trusted
        // every taught alias for ever. A row poisoned before the gate existed, or taught before the right
        // customer was created, still stood beside Saudi Electricity Company in lead 680's own address,
        // and the lead went AMBIGUOUS until somebody cleared the row by hand.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, wrongOwner)],
            identifiers: [Alias(1, OtherCustomer, taught)]), Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void Initials_taught_while_unique_give_way_once_another_customer_shares_them()
    {
        // "SCC" taught for Saudi Cable while it was the only SCC. Saudi Ceramics was added later, and
        // its print "SCC Riyadh Plant 2" still linked to Saudi Cable at 0.88, because a taught alias was
        // never checked again.
        const long saudiCable = OtherCustomer, saudiCeramics = ThirdCustomer;
        CustomerNameSnapshot[] both = [new(saudiCable, "Saudi Cable Company"), new(saudiCeramics, "Saudi Ceramics Company")];
        CustomerIdentifierSnapshot[] taught = [Alias(1, saudiCable, "SCC")];
        var shipTo = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "SCC Riyadh Plant 2", true)],
        };

        var address = CustomerIdentityResolver.Resolve(shipTo, Corpus(both, taught), Policy);
        Assert.Null(address.CustomerId);
        Assert.DoesNotContain(address.Candidates, candidate => candidate.Confidence >= Policy.MinimumAutoLinkConfidence);

        // The exact learned-alias tier reads the company-name field, and gives way the same way.
        var field = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            CustomerCompanyName = "SCC",
            Passages = [new DocumentPassage("company named on the document", "SCC", true) { Role = PassageRole.BuyerHeader }],
        }, Corpus(both, taught), Policy);
        Assert.Null(field.CustomerId);

        // Above the name-scan cap the other owner may not be loaded; the tenant-wide shared initials count.
        var capped = CustomerIdentityResolver.Resolve(
            shipTo, Corpus([new(saudiCable, "Saudi Cable Company")], taught), Policy, new HashSet<string> { "SCC" });
        Assert.Null(capped.CustomerId);

        // Initials only one customer carries still link.
        var unique = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 12,
            Passages = [new DocumentPassage("delivery address", "SEC Materials West Plant", true)],
        }, Corpus([new(Sec, "Saudi Electricity Company")], [Alias(1, Sec, "SEC")]), Policy);
        Assert.Equal(Sec, unique.CustomerId);
    }

    // ── A RELAY'S OWN SENDING ADDRESS ─────────────────────────────────────────

    [Theory]
    [InlineData("ordersender-prod@ansmtp.ariba.com", CustomerIdentifierSources.LeadReviewLearned)]
    [InlineData("alerts@bidnet.com", CustomerIdentifierSources.LeadReviewLearned)]
    [InlineData("noreply@etimad.sa", "MigrationBackfill")]
    public void A_relay_mailbox_nobody_registered_does_not_link_every_buyer_on_the_network(string relay, string source)
    {
        // THE DEFECT: the old learner minted a portal's own sending address as the Email of whichever
        // buyer was confirmed first, and the exact tier matches an address whatever its domain. A SABIC
        // RFQ relayed through Ariba linked to Saudi Aramco at 1.00, however plainly the page named SABIC.
        // That mailbox is the postman for every buyer on the network.
        const long aramco = OtherCustomer, sabic = ThirdCustomer;
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = relay,
            CustomerCompanyName = "Saudi Basic Industries Corporation",
            Passages =
            [
                new DocumentPassage("company named on the document", "Saudi Basic Industries Corporation", true)
                    { Role = PassageRole.BuyerHeader },
                new DocumentPassage("delivery address", "Saudi Basic Industries Corporation - Jubail", true),
            ],
        }, Corpus(
            customers: [new(aramco, "Saudi Aramco"), new(sabic, "Saudi Basic Industries Corporation")],
            identifiers: [new(1, aramco, CustomerIdentifierType.Email, relay, true, 1m, source)]), Policy);

        Assert.Equal(sabic, outcome.CustomerId);
        Assert.NotEqual(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
    }

    [Theory]
    [InlineData(CustomerIdentifierSources.MasterData)]
    [InlineData("CustomerProfile")]
    [InlineData("CustomerContact")]
    [InlineData("CustomerImport")]
    public void A_relay_mailbox_a_person_registered_on_a_customer_still_links(string source)
    {
        // One relay mailbox a person put on one customer is a statement about one mailbox.
        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = 1, LeadId = 10, SenderEmail = "sec-tenders@bidnet.com" },
            Corpus(
                customers: [new(Sec, "Saudi Electricity Company")],
                identifiers: [new(1, Sec, CustomerIdentifierType.Email, "sec-tenders@bidnet.com", true, 1m, source)]),
            Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
    }

    // ── THE ORGANISATION A MAILBOX IS SIGNED WITH ─────────────────────────────

    [Fact]
    public void A_contractors_mailbox_signed_with_a_customers_name_is_offered_before_the_site_owner()
    {
        // #14. Hyundai E&C mails from hdec.com about an Aramco site. Hyundai is a customer, but nobody
        // registered hdec.com and no contact writes from it, so nothing on the page was a claim for
        // Hyundai: Aramco linked at 0.88 and Hyundai was never offered. The mailbox is signed "Hyundai
        // E&C Procurement", which is the organisation writing, and that is a claim.
        const long aramco = OtherCustomer, hyundai = ThirdCustomer;
        CustomerNameSnapshot[] customers = [new(aramco, "Saudi Aramco"), new(hyundai, "Hyundai Engineering & Construction")];
        DocumentPassage[] rasTanura = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)];

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "procurement@hdec.com",
            SenderOrganisationName = "Hyundai E&C",
            Passages = rasTanura,
        }, Corpus(customers), Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(hyundai, outcome.Candidates[0].CustomerId);
        var offered = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == aramco);
        Assert.Equal(Policy.ShipToDemotedConfidence, offered.Confidence);
        Assert.Contains("Hyundai E&C", offered.Explanation);

        // The site owner's own mailbox, signed with its own name, still links on the same address.
        var own = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            SenderEmail = "buyer@aramco.com",
            SenderOrganisationName = "Saudi Aramco",
            Passages = rasTanura,
        }, Corpus(customers), Policy);
        Assert.Equal(aramco, own.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, own.Confidence);
    }

    // ── AN AUTO-LINK MUST CLEAR THE FLOOR ─────────────────────────────────────

    [Fact]
    public void An_auto_link_below_the_floor_is_offered_instead_of_applied()
    {
        // "Nexora decides at 0.85 and above" used to be an arithmetic coincidence between the
        // constants each tier happened to pick. Lower one of them and the engine would have
        // started auto-linking at 0.80 with nothing anywhere to stop it. The shipped policy is
        // unchanged, so this rejects nothing today — which is exactly why it is written down.
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        };
        var corpus = Corpus(customers: [new(Sec, "Saudi Electricity Company")]);

        var lowered = CustomerIdentityResolver.Resolve(
            evidence, corpus, new CustomerResolutionPolicy { NameInAddressConfidence = 0.80m });
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, lowered.Status);
        Assert.Null(lowered.CustomerId);
        Assert.Equal(0.80m, lowered.Confidence);
        Assert.Equal(Sec, Assert.Single(lowered.Candidates).CustomerId);

        var shipped = CustomerIdentityResolver.Resolve(evidence, corpus, Policy);
        Assert.Equal(Sec, shipped.CustomerId);
        Assert.True(shipped.Confidence >= Policy.MinimumAutoLinkConfidence);
    }

    // ── CORRECT LINKS THE HARDENING ROUND TOOK AWAY ───────────────────────────

    [Fact]
    public void A_misread_vendor_block_does_not_throw_away_the_buyers_registered_sender()
    {
        // THE DEFECT: the guard added the vendor block the document prints to the names that make a
        // domain "ours". The extractor misread a Marafiq RFQ's vendor field as "MARAFIQ", so
        // marafiq.com.sa became our own domain, the buyer's registered address and domain were thrown
        // away before the exact tiers, and a lead that linked at 1.00 went UNRESOLVED. The same with
        // "Saudi Aramco" in that field and aramco.com (0.95 before).
        const long marafiq = OtherCustomer, aramco = ThirdCustomer;
        var corpus = Corpus(
            customers: [new(marafiq, "Marafiq"), new(aramco, "Saudi Aramco")],
            identifiers:
            [
                new(1, marafiq, CustomerIdentifierType.Email, "buyer@marafiq.com.sa", true, 1m, "CustomerContact"),
                new(2, marafiq, CustomerIdentifierType.Domain, "marafiq.com.sa", true, 0.95m, "CustomerContact"),
                new(3, aramco, CustomerIdentifierType.Domain, "aramco.com", true, 0.95m, "CustomerContact"),
            ]);

        var marafiqRfq = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "buyer@marafiq.com.sa",
            SupplierNameOnDocument = "MARAFIQ",
        }, corpus, Policy);
        Assert.Equal(marafiq, marafiqRfq.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, marafiqRfq.ReasonCode);
        Assert.Equal(1.00m, marafiqRfq.Confidence);

        var aramcoRfq = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            SenderEmail = "buyer@aramco.com",
            SupplierNameOnDocument = "Saudi Aramco",
        }, corpus, Policy);
        Assert.Equal(aramco, aramcoRfq.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, aramcoRfq.ReasonCode);
        Assert.Equal(0.95m, aramcoRfq.Confidence);
    }

    [Theory]
    [InlineData("Saudi Aramco Oil Company - Ras Tanura")]
    [InlineData("Saudi Aramco Oil Co., Dhahran")]
    public void The_customers_own_name_with_a_sector_word_and_a_legal_form_is_still_the_customer(string deliveryAddress)
    {
        // THE DEFECT: the longer-company-name rule counted any word after the name as another company's
        // name word, so Aramco's own legal name written out ("Saudi Aramco Oil Company") read as a
        // different company and a correct 0.88 link fell to a 0.70 offer.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers: [new(OtherCustomer, "Saudi Aramco"), new(Sec, "Saudi Electricity Company")]), Policy);

        Assert.Equal(OtherCustomer, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain("runs on", outcome.Explanation);
    }

    [Fact]
    public void A_customer_whose_name_is_only_generic_words_links_where_the_page_writes_its_whole_name()
    {
        // THE DEFECT: the distinctiveness filter made a customer whose every name word is generic invisible.
        // "Arabian Gulf International Company - Dammam Yard" is its own yard; it linked at 0.88, and after the
        // filter the only name heard was DAMMAM, so the trading house named after the city was offered instead.
        const long agic = OtherCustomer, alDammam = ThirdCustomer;
        var corpus = Corpus(customers:
        [
            new(agic, "Arabian Gulf International Company"),
            new(alDammam, "Al Dammam Trading Co."),
            new(Sec, "Saudi Electricity Company"),
        ]);
        ClientResolutionOutcome At(string address) => CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", address, true)],
        }, corpus, Policy);

        var ownYard = At("Arabian Gulf International Company - Dammam Yard");
        Assert.Equal(agic, ownYard.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, ownYard.Status);
        Assert.Equal(Policy.NameInAddressConfidence, ownYard.Confidence);

        // A longer company that begins with the same generic words is another company: offered, not linked.
        var anotherCompany = At("Arabian Gulf International Trading Co., Dammam");
        Assert.Null(anotherCompany.CustomerId);
        var offered = Assert.Single(anotherCompany.Candidates, candidate => candidate.CustomerId == agic);
        Assert.True(offered.Confidence < Policy.MinimumAutoLinkConfidence,
            $"{offered.CustomerName} was offered at {offered.Confidence}, which is link strength.");

        // The generic words alone are a road, not the customer, and the customer is not even offered.
        var road = At("Arabian Gulf Road, Dammam");
        Assert.Null(road.CustomerId);
        Assert.DoesNotContain(road.Candidates, candidate => candidate.CustomerId == agic);
    }

    [Fact]
    public void Two_letter_initials_a_reviewer_taught_link_the_company_name_field_when_they_are_the_owners_alone()
    {
        // THE DEFECT: the exact learned-alias tier took the distinctiveness test, which wants a word of three
        // letters, so "GE" taught for General Electric Saudi Arabia no longer linked a company-name field that
        // says exactly "GE" (0.90 before), and the lead was left unresolved.
        const long ge = OtherCustomer, gulfEngineering = ThirdCustomer;
        CustomerIdentifierSnapshot[] taught = [Alias(1, ge, "GE")];
        var fieldSaysGe = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = "GE",
            Passages = [new DocumentPassage("company named on the document", "GE", true) { Role = PassageRole.BuyerHeader }],
        };

        var linked = CustomerIdentityResolver.Resolve(fieldSaysGe,
            Corpus([new(ge, "General Electric Saudi Arabia"), new(Sec, "Saudi Electricity Company")], taught), Policy);
        Assert.Equal(ge, linked.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedAlias, linked.ReasonCode);
        Assert.Equal(Policy.LearnedAliasConfidence, linked.Confidence);

        // Two letters another customer's name also begins with identify neither.
        var shared = CustomerIdentityResolver.Resolve(fieldSaysGe,
            Corpus([new(ge, "General Electric Saudi Arabia"), new(gulfEngineering, "Gulf Engineering Est")], taught), Policy);
        Assert.Null(shared.CustomerId);
        Assert.DoesNotContain(shared.Candidates, candidate => candidate.Confidence >= Policy.MinimumAutoLinkConfidence);

        // Still never read inside a passage: "GE" in an address is anybody's two letters.
        var inAddress = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            Passages = [new DocumentPassage("delivery address", "GE Store, Dammam", true)],
        }, Corpus([new(ge, "General Electric Saudi Arabia"), new(Sec, "Saudi Electricity Company")], taught), Policy);
        Assert.Null(inAddress.CustomerId);
    }

    [Theory]
    [InlineData("R7")]
    [InlineData("C05")]
    [InlineData("C44")]
    public void One_earlier_decision_from_another_mailbox_on_the_domain_does_not_take_a_named_print_away(string shape)
    {
        // THE DEFECT: one person-linked lead from ANY address on the sender's domain tied the whole domain
        // to that customer. Every later correctly named print from another mailbox fell to 0.70 with the
        // earlier pick ranked FIRST, so one mis-click steered the next rep into repeating it.
        const long rival = OtherCustomer;
        var (evidence, customers, earlier, consignee) = shape switch
        {
            // SEC print from 57322@se.com.sa; a person once linked a lead from 59000@se.com.sa to Aramco.
            "R7" => (new LeadClientEvidence
                {
                    BusinessUnitId = 1, LeadId = 10,
                    SenderEmail = "57322@se.com.sa",
                    Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
                },
                new CustomerNameSnapshot[] { new(Sec, "Saudi Electricity Company"), new(rival, "Saudi Aramco") },
                new PriorSenderResolution[] { new("59000@se.com.sa", rival) },
                Sec),
            // A folder-ingested SEC print carrying 57322@se.com.sa; a person linked a National Grid SA lead
            // that printed a.otaibi@se.com.sa.
            "C05" => (new LeadClientEvidence
                {
                    BusinessUnitId = 1, LeadId = 11,
                    SenderEmail = "extraction@pipeline.local",
                    DocumentBuyerEmail = "57322@se.com.sa",
                    SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL",
                    Passages =
                    [
                        new DocumentPassage("delivery address", "Saudi Electricity Company-JIZAN AREA", true),
                        new DocumentPassage("storage location", "SEC Materials West Plant-West Operating Area", true),
                    ],
                },
                new CustomerNameSnapshot[] { new(Sec, "Saudi Electricity Company"), new(rival, "National Grid SA") },
                new PriorSenderResolution[] { new("a.otaibi@se.com.sa", rival) },
                Sec),
            // SABIC mail with SABIC's address; a person linked one SADAF lead from y.otaibi@sabic.com.
            _ => (new LeadClientEvidence
                {
                    BusinessUnitId = 1, LeadId = 12,
                    SenderEmail = "procurement@sabic.com",
                    Passages = [new DocumentPassage("delivery address", "Saudi Basic Industries Corporation - Jubail", true)],
                },
                new CustomerNameSnapshot[] { new(ThirdCustomer, "Saudi Basic Industries Corporation"), new(rival, "Saudi Petrochemical Company (SADAF)") },
                new PriorSenderResolution[] { new("y.otaibi@sabic.com", rival) },
                ThirdCustomer),
        };

        var outcome = CustomerIdentityResolver.Resolve(evidence, Corpus(customers, priorSenders: earlier), Policy);

        Assert.Equal(consignee, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void Earlier_decisions_speak_for_a_domain_from_this_mailbox_or_from_two_that_agree_and_the_named_consignee_is_offered_first()
    {
        // What the rule above keeps: this very mailbox's decision, or two different mailboxes that both chose
        // the same other customer, still speak against the address. But the page's own name is offered first,
        // because the earlier pick is what a person chose before, not what this page says.
        const long aramco = OtherCustomer, ngsa = ThirdCustomer;
        CustomerNameSnapshot[] customers = [new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco"), new(ngsa, "National Grid SA")];
        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "57322@se.com.sa",
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        };
        ClientResolutionOutcome After(params PriorSenderResolution[] earlier)
            => CustomerIdentityResolver.Resolve(evidence, Corpus(customers, priorSenders: earlier), Policy);

        var twoMailboxes = After(new("59000@se.com.sa", aramco), new("59001@se.com.sa", aramco));
        Assert.Null(twoMailboxes.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, twoMailboxes.Status);
        Assert.Equal(Sec, twoMailboxes.Candidates[0].CustomerId);
        Assert.Equal(Policy.ShipToDemotedConfidence, twoMailboxes.Candidates[0].Confidence);
        Assert.Contains("59000@se.com.sa and 59001@se.com.sa", twoMailboxes.Candidates[0].Explanation);
        var earlierPick = Assert.Single(twoMailboxes.Candidates, candidate => candidate.CustomerId == aramco);
        Assert.True(earlierPick.Confidence < twoMailboxes.Candidates[0].Confidence);

        var thisMailbox = After(new PriorSenderResolution("57322@se.com.sa", aramco));
        Assert.Null(thisMailbox.CustomerId);
        Assert.Equal(Sec, thisMailbox.Candidates[0].CustomerId);
        Assert.Contains(thisMailbox.Candidates, candidate => candidate.CustomerId == aramco);

        // Two mailboxes that chose two different customers say nothing about whose domain it is.
        var disagreeing = After(new("59000@se.com.sa", aramco), new("59001@se.com.sa", ngsa));
        Assert.Equal(Sec, disagreeing.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, disagreeing.Confidence);
    }

    [Theory]
    [InlineData("Dammam", "procurement@se.com.sa", "Saudi Electricity Company-DAMMAM", "Al Dammam Trading Co.")]
    [InlineData("Jubail", "materials@se.com.sa", "Saudi Electricity Company-JUBAIL", "Jubail Trading Est")]
    public void Lead_680_from_SECs_own_department_mailbox_signed_with_its_city_still_links(
        string signedAs, string sender, string deliveryAddress, string cityNamedCustomer)
    {
        // PRODUCTION CONTROL, lead 680's shape from SEC's own mailbox "Dammam Procurement
        // <procurement@se.com.sa>". The signature loses its function word and reads "Dammam", which is
        // "Al Dammam Trading Co.", and that tie demoted SEC's own address to 0.70 with the trading house
        // ranked first. The signature says only what the address already says: where SEC is.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            SenderEmail = sender,
            SenderOrganisationName = signedAs,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers:
        [
            new(Sec, "Saudi Electricity Company"), new(OtherCustomer, cityNamedCustomer), new(ThirdCustomer, "Saudi Aramco"),
        ]), Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Theory]
    [InlineData("rt.project@hdec.com")]
    [InlineData("contracts.rt@hdec.com")]
    public void A_contractors_project_mailbox_signed_with_the_site_owners_name_does_not_outvote_the_contractors_contact(string sender)
    {
        // THE DEFECT: a signature tie counted as the consignee's own tie. Hyundai's project mailbox signed
        // "Saudi Aramco Procurement" at hdec.com therefore cancelled Hyundai's real contact there, and Aramco
        // auto-linked at 0.88 on its site address. The same lead from a bare procurement@hdec.com is offered
        // with Hyundai first. A signature may add a rival; it never shields the consignee.
        const long aramco = OtherCustomer, hyundai = ThirdCustomer;
        CustomerNameSnapshot[] customers = [new(aramco, "Saudi Aramco"), new(hyundai, "Hyundai Engineering & Construction")];
        DocumentPassage[] rasTanura = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)];

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = sender,
            SenderOrganisationName = "Saudi Aramco",
            Passages = rasTanura,
        }, Corpus(customers, contacts: [new(1, hyundai, "k.lee@hdec.com", "K", "Lee")]), Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(hyundai, outcome.Candidates[0].CustomerId);
        var site = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == aramco);
        Assert.True(site.Confidence < Policy.MinimumAutoLinkConfidence, $"Aramco was offered at {site.Confidence}, which is link strength.");

        // The site owner's own mailbox signed with its own short name, with nothing else tying the domain, still links.
        var own = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            SenderEmail = "buyer@aramco.com",
            SenderOrganisationName = "Aramco",
            Passages = rasTanura,
        }, Corpus(customers), Policy);
        Assert.Equal(aramco, own.CustomerId);
        Assert.Equal(Policy.NameInAddressConfidence, own.Confidence);
    }

    [Theory]
    [InlineData(true, "Saudi Electricity Company Main Store (DAMMAM)")]
    [InlineData(true, "Saudi Electricity Company Substation (RIYADH)")]
    [InlineData(true, "Saudi Electricity Company Central Warehouse (QASSIM)")]
    [InlineData(false, "Saudi Aramco Abqaiq Plants (ABQP)")]
    [InlineData(false, "Saudi Aramco Northern Area Oil Operations (NAOO)")]
    [InlineData(false, "Saudi Aramco Terminal Operations (JUBAIL)")]
    [InlineData(false, "Saudi Aramco Ras Tanura Refinery (RTRD)")]
    public void An_address_ending_in_a_bracketed_city_or_site_code_is_still_the_customers_own_site(bool sec, string deliveryAddress)
    {
        // PRODUCTION CONTROL. The bracketed-trade-name rule took any capitalised bracketed word of four letters
        // or more, after further name words, as another company's trade name. SEC and Aramco sites written
        // with their city or site code in brackets fell from 0.88 to a 0.70 offer. A trade name abbreviates
        // the run it closes and starts with its first letter (SATORP, SASREF); a city or site code does not.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")]), Policy);

        Assert.Equal(sec ? Sec : OtherCustomer, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    // ── REPAIR ROUND 2026-09-13 ─────────────────────────────────────────────────

    [Theory]
    [InlineData(true, "Saudi Electricity Company Substation (SUDAIR)")]
    [InlineData(true, "Saudi Electricity Company Sudair Substation (SUDAIR)")]
    [InlineData(true, "Saudi Electricity Company Shuqaiq Plant (SHUQAIQ)")]
    [InlineData(true, "Saudi Electricity Company Shoaiba Power Plant (SHOAIBA)")]
    [InlineData(true, "Saudi Electricity Company Store (SAKAKA)")]
    [InlineData(false, "Saudi Aramco Shaybah Producing Department (SHYB)")]
    [InlineData(false, "Saudi Aramco Safaniya Onshore Producing (SFNY)")]
    [InlineData(false, "Saudi Aramco Southern Area Oil Operations (SAOO)")]
    [InlineData(false, "Saudi Aramco Shedgum Gas Plant (SHEDGUM)")]
    [InlineData(false, "Saudi Aramco Safaniya (SAFANIYA)")]
    [InlineData(false, "Saudi Aramco Shaybah (SHAYBAH)")]
    [InlineData(false, "Saudi Aramco Material Supply (SAMS)")]
    [InlineData(false, "Saudi Aramco Shaybah NGL (SNGL)")]
    [InlineData(false, "Saudi Aramco Southern Area Producing (SAPD), Udhailiyah")]
    [InlineData(false, "Saudi Aramco Shaybah Operations (SHYB)")]
    public void A_site_whose_bracketed_code_starts_like_the_customers_name_is_still_the_customers_own_site(bool sec, string deliveryAddress)
    {
        // THE DEFECT (CTRL-bracketed-codes, the S half). A bracketed word was read as a joint venture's trade name
        // whenever it began with the run's first letter, so every SEC and Saudi Aramco site whose name or code starts
        // with S (Sudair, Shuqaiq, Shoaiba, Sakaka, Shaybah, Safaniya, Shedgum, Southern Area) fell from an 0.88 link
        // to a 0.70 offer. Base linked all of them. A bracket that repeats a word of the run, or a run carrying a site
        // or unit word, is the customer's own site.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")]), Policy);

        Assert.Equal(sec ? Sec : OtherCustomer, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Theory]
    [InlineData("Saudi Aramco Total Refining & Petrochemical (SATORP), Jubail")]
    [InlineData("Saudi Aramco Jubail Refinery (SASREF), Jubail Industrial City")]
    [InlineData("Saudi Aramco Mobil Refinery (SAMREF), Yanbu")]
    public void A_joint_ventures_bracketed_trade_name_is_still_offered_after_the_site_rule(string deliveryAddress)
    {
        // The control for the rule above: a run with no site or unit word, closed by a trade name that repeats none of
        // its words, is still another company's name, and the parent is offered, not applied.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers: [new(OtherCustomer, "Saudi Aramco")]), Policy);

        Assert.Null(outcome.CustomerId);
        var aramco = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
        Assert.Equal(Policy.ShipToDemotedConfidence, aramco.Confidence);
    }

    private static LeadClientEvidence EBiddingPrint(params DocumentPassage[] passages) => new()
    {
        BusinessUnitId = 1, LeadId = 10,
        CustomerPortalName = "MATERIALS E-BIDDING SYSTEM",
        SupplierAccountRefOnDocument = "2004414",
        SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL",
        Passages = passages
    };

    private static CustomerIdentifierSnapshot EBiddingPair(long owner, string source) =>
        new(1, owner, CustomerIdentifierType.PortalAccount, "MATERIALS E BIDDING SYSTEM|2004414", true, 0.92m, source);

    [Theory]
    [InlineData("delivery address", "Saudi Electricity Company-DAMMAM")]
    [InlineData("storage location", "SEC Materials West Plant-West Operating Area")]
    public void A_portal_pair_taught_to_one_customer_gives_way_to_a_page_that_names_another(string where, string text)
    {
        // THE DEFECT (T24 and W04.02, the root of the Aramco 55% incident). A pair a rep taught onto Saudi Aramco by
        // repeating a wrong pick, or taught before the learner's portal guard existed, decided at 0.92 before any
        // passage was read, over "Saudi Electricity Company-DAMMAM" in the address and over SEC's initials in the
        // storage location. Base did the same. The page is heard now: the named customer is offered first and the
        // earlier pick below it, and nothing is applied.
        const long aramco = OtherCustomer;
        var outcome = CustomerIdentityResolver.Resolve(
            EBiddingPrint(new DocumentPassage(where, text, true)) with { RfqNumber = "C001832162" },
            Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco")],
                identifiers:
                [
                    EBiddingPair(aramco, CustomerIdentifierSources.LeadReviewLearned),
                    new(2, aramco, CustomerIdentifierType.RfqNumberPattern, @"^C\d{9}$", false, 0.50m, CustomerIdentifierSources.LeadReviewLearned),
                ]), Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(Sec, outcome.Candidates[0].CustomerId);
        Assert.Equal(Policy.ShipToDemotedConfidence, outcome.Candidates[0].Confidence);
        var earlierPick = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == aramco);
        Assert.Equal(CustomerMatchReasonCodes.LearnedPortalAccount, earlierPick.ReasonCode);
        Assert.True(earlierPick.Confidence < outcome.Candidates[0].Confidence);
    }

    [Fact]
    public void A_portal_pair_still_decides_where_the_page_agrees_names_nobody_or_a_person_entered_it()
    {
        const long aramco = OtherCustomer;
        CustomerNameSnapshot[] customers = [new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco")];
        var secAddress = new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true);

        // PRODUCTION CONTROL: SEC's buyer-operated portal pair on SEC's own print.
        var own = CustomerIdentityResolver.Resolve(EBiddingPrint(secAddress),
            Corpus(customers, [EBiddingPair(Sec, CustomerIdentifierSources.LeadReviewLearned)]), Policy);
        Assert.Equal(Sec, own.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedPortalAccount, own.ReasonCode);
        Assert.Equal(Policy.LearnedPortalAccountConfidence, own.Confidence);

        foreach (var (outcome, why) in new[]
        {
            (CustomerIdentityResolver.Resolve(EBiddingPrint(new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)),
                Corpus(customers, [EBiddingPair(aramco, CustomerIdentifierSources.LeadReviewLearned)]), Policy), "the page names the pair's own customer"),
            (CustomerIdentityResolver.Resolve(EBiddingPrint(),
                Corpus(customers, [EBiddingPair(aramco, CustomerIdentifierSources.LeadReviewLearned)]), Policy), "the page names nobody"),
            (CustomerIdentityResolver.Resolve(EBiddingPrint(secAddress),
                Corpus(customers, [EBiddingPair(aramco, CustomerIdentifierSources.MasterData)]), Policy), "a person entered the pair"),
        })
        {
            Assert.True(outcome.CustomerId == aramco, why);
            Assert.Equal(Policy.LearnedPortalAccountConfidence, outcome.Confidence);
        }
    }

    [Theory]
    [InlineData("etimad.gov.sa", "no-reply@etimad.gov.sa")]
    [InlineData("coupa.com", "do_not_reply@coupa.com")]
    [InlineData("sap.com", "no-reply@sap.com")]
    public void A_domain_row_nobody_entered_does_not_link_what_a_system_mailbox_on_that_host_carries(string domain, string systemMailbox)
    {
        // MUST STAY FIXED (W03.04, W03.05, W03.08). S1 refuses a learned row on a system mailbox, but a learned Domain
        // row for the host a portal sends from still linked every SEC print it carried to Saudi Aramco at 0.95, in both
        // builds, because the relay list does not name etimad.gov.sa, coupa.com or sap.com and never will name every host.
        const long aramco = OtherCustomer;
        CustomerNameSnapshot[] customers = [new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco")];
        ClientResolutionOutcome Mail(string sender, string source, string? printedBuyer = null) => CustomerIdentityResolver.Resolve(
            new LeadClientEvidence
            {
                BusinessUnitId = 1, LeadId = 10, SenderEmail = sender, DocumentBuyerEmail = printedBuyer,
                Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
            },
            Corpus(customers, [new(1, aramco, CustomerIdentifierType.Domain, domain, true, 0.95m, source)]), Policy);

        var learned = Mail(systemMailbox, CustomerIdentifierSources.LeadReviewLearned);
        Assert.Equal(Sec, learned.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, learned.ReasonCode);
        Assert.Equal(Policy.NameInAddressConfidence, learned.Confidence);

        // A rule a person entered still decides, and a person's own mailbox on that host, on the envelope or printed on
        // the page beside the system envelope, is still matched by the learned row.
        foreach (var outcome in new[]
        {
            Mail(systemMailbox, CustomerIdentifierSources.MasterData),
            Mail($"buyer@{domain}", CustomerIdentifierSources.LeadReviewLearned),
            Mail(systemMailbox, CustomerIdentifierSources.LeadReviewLearned, printedBuyer: $"buyer@{domain}"),
        })
        {
            Assert.Equal(aramco, outcome.CustomerId);
            Assert.Equal(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);
        }
    }

    [Theory]
    [InlineData("Jubail", "Saudi Electricity Company - Riyadh PP9 Substation", "Jubail Trading Est")]
    [InlineData("Dammam", "Saudi Electricity Company-JUBAIL", "Al Dammam Trading Co.")]
    [InlineData("Dammam", "Saudi Electricity Company Eastern Operating Area, Riyadh", "Al Dammam Trading Co.")]
    public void A_mailbox_signed_only_with_a_city_is_a_branch_whatever_city_the_address_names(
        string signedAs, string deliveryAddress, string cityNamedCustomer)
    {
        // THE DEFECT (T27 residual, a regression against base). SEC's own "Jubail Procurement <procurement@se.com.sa>"
        // signs "Jubail", which reads as "Jubail Trading Est". The signature was spared only where the address repeated
        // its city, so an SEC print delivering to Riyadh was demoted to 0.70 with the trading house ranked first. Base
        // linked SEC at 0.88. A signature made only of place or trade words is a branch or a department.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 680,
            SenderEmail = "procurement@se.com.sa",
            SenderOrganisationName = signedAs,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(customers:
        [
            new(Sec, "Saudi Electricity Company"), new(OtherCustomer, cityNamedCustomer), new(ThirdCustomer, "Saudi Aramco"),
        ]), Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);

        // The control: a contractor's own signature still speaks for it (H14).
        var contractor = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 11,
            SenderEmail = "procurement@hdec.com",
            SenderOrganisationName = "Hyundai E&C",
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, Corpus(customers: [new(OtherCustomer, "Saudi Aramco"), new(ThirdCustomer, "Hyundai Engineering & Construction")]), Policy);
        Assert.Null(contractor.CustomerId);
        Assert.Equal(ThirdCustomer, contractor.Candidates[0].CustomerId);
    }

    [Theory]
    [InlineData("Rashid", PassageRole.ItemText, "nothing")]
    [InlineData("RASHID", PassageRole.BuyerHeader, "nothing")]
    [InlineData("Al-Rashid", PassageRole.ItemText, "nothing")]
    [InlineData("Dammam Procurement", PassageRole.ItemText, "nothing")]
    [InlineData("Ahmed Rashid, Procurement", PassageRole.BuyerHeader, "nothing")]
    // Controls: a person's fuller name is still a mention, and the trading house's whole name on a relay still links.
    [InlineData("Rashid Al-Otaibi", PassageRole.ItemText, "offered")]
    [InlineData("Al-Rashid Trading", PassageRole.BuyerHeader, "linked")]
    public void One_word_of_a_customers_name_alone_on_a_mailbox_is_a_person_or_a_department_not_that_customer(
        string displayName, PassageRole role, string expected)
    {
        // THE DEFECT (the repair round's P2). The mailbox display name became a passage, and one word of a customer's
        // name on it made that customer a candidate: "Rashid <rashid1987@gmail.com>" offered Al-Rashid Trading, SEC's
        // own "Dammam Procurement" offered Al Dammam Trading above SEC's own earlier decision, and a relay's "RASHID"
        // linked Al-Rashid Trading at 0.88. Base read nothing in any of them.
        const long rashid = OtherCustomer, dammam = ThirdCustomer;
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage(DocumentPassage.SenderDisplayNameWhere, displayName, role != PassageRole.ItemText) { Role = role }],
        }, Corpus(customers:
        [
            new(Sec, "Saudi Electricity Company"), new(rashid, "Al-Rashid Trading"), new(dammam, "Al Dammam Trading Co."),
        ]), Policy);

        switch (expected)
        {
            case "nothing":
                Assert.Null(outcome.CustomerId);
                Assert.Empty(outcome.Candidates);
                break;
            case "offered":
                Assert.Null(outcome.CustomerId);
                Assert.Equal(rashid, Assert.Single(outcome.Candidates).CustomerId);
                break;
            default:
                Assert.Equal(rashid, outcome.CustomerId);
                Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
                break;
        }
    }

    [Theory]
    [InlineData("Saudi Aramco", "ARAMCO", "ARAMCO Ras Tanura Refinery")]
    [InlineData("Marafiq Power & Water Utility Company", "MARAFIQ", "MARAFIQ Yanbu Warehouse, Yanbu Al-Sinaiyah")]
    [InlineData("Marafiq", "MARAFIQ", "MARAFIQ Yanbu Warehouse, Yanbu Al-Sinaiyah")]
    public void A_one_word_name_a_reviewer_taught_links_where_it_opens_the_delivery_address(
        string customerName, string taught, string deliveryAddress)
    {
        // THE DEFECT (A14.C22, LG13). "One word in an address names a place as often as a company" also caught a name a
        // reviewer confirmed for exactly this customer, written as the first word of its own address, and base's 0.88
        // link fell to a 0.70 offer.
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, customerName)],
            identifiers: [Alias(1, OtherCustomer, taught)]), Policy);

        Assert.Equal(OtherCustomer, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Theory]
    [InlineData("Al Dammam Trading Co.", "DAMMAM", "DAMMAM Main Store")]                // a place word, taught or not
    [InlineData("Saudi Aramco", "ARAMCO", "ARAMCO Services Company, Houston")]           // runs on into another company
    [InlineData("Saudi Aramco", "ARAMCO", "Ras Tanura ARAMCO Refinery")]                 // does not open the address
    [InlineData("Marafiq", null, "MARAFIQ Yanbu Warehouse, Yanbu Al-Sinaiyah")]         // nobody taught it
    public void A_one_word_name_in_an_address_is_still_only_offered_otherwise(string customerName, string? taught, string deliveryAddress)
    {
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            Passages = [new DocumentPassage("delivery address", deliveryAddress, true)],
        }, Corpus(
            customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, customerName)],
            identifiers: taught is null ? [] : [Alias(1, OtherCustomer, taught)]), Policy);

        Assert.Null(outcome.CustomerId);
        var offered = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == OtherCustomer);
        Assert.Equal(Policy.ShipToDemotedConfidence, offered.Confidence);
    }

    [Fact]
    public void Initials_written_with_dots_in_the_company_name_field_are_the_customers_initials()
    {
        // THE DEFECT (LF03). "S.E.C." folds to the single letters "S E C", which no initials key contains, and the learner
        // now refuses to teach "S E C" as naming nobody. SEC's own company-name field printed that way went from a 0.90
        // link at base to NO_MATCH.
        LeadClientEvidence Printed() => new()
        {
            BusinessUnitId = 1, LeadId = 10,
            CustomerCompanyName = "S.E.C.",
            Passages = [new DocumentPassage("company named on the document", "S.E.C.", true) { Role = PassageRole.BuyerHeader }],
        };

        var outcome = CustomerIdentityResolver.Resolve(Printed(),
            Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Aramco")]), Policy);
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(Policy.NameAcronymInAddressConfidence, outcome.Confidence);

        // Initials two customers share still name neither.
        var shared = CustomerIdentityResolver.Resolve(Printed(),
            Corpus(customers: [new(Sec, "Saudi Electricity Company"), new(OtherCustomer, "Saudi Engineering Company")]), Policy);
        Assert.Null(shared.CustomerId);
    }

    [Theory]
    [InlineData("learned address")]
    [InlineData("earlier decision from this mailbox")]
    [InlineData("earlier decisions from two system mailboxes")]
    public void A_system_mailbox_nobody_entered_neither_links_nor_demotes_the_consignee_it_carries(string record)
    {
        // THE SEAM (T17 against the domain-tie read). A system mailbox names nobody: S1 refuses a learned
        // no-reply@etimad.gov.sa row, and routing refuses it too. But the tie read beside the consignee check
        // still took that same row, and any person's decision on a lead the mailbox carried, as a fact about
        // who writes from etimad.gov.sa. The next SEC print through the portal fell to 0.70 with Saudi Aramco
        // ranked first, because the portal once carried an Aramco job.
        const long aramco = OtherCustomer;
        const string relay = "no-reply@etimad.gov.sa";
        CustomerNameSnapshot[] customers = [new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco")];
        var corpus = record switch
        {
            "learned address" => Corpus(customers,
                identifiers: [new(1, aramco, CustomerIdentifierType.Email, relay, true, 1m, CustomerIdentifierSources.LeadReviewLearned)]),
            "earlier decision from this mailbox" => Corpus(customers, priorSenders: [new PriorSenderResolution(relay, aramco)]),
            _ => Corpus(customers, priorSenders:
                [new PriorSenderResolution(relay, aramco), new PriorSenderResolution("noreply@etimad.gov.sa", aramco)]),
        };

        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = relay,
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, corpus, Policy);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(Policy.NameInAddressConfidence, outcome.Confidence);
    }

    [Fact]
    public void A_persons_mailbox_on_a_portals_host_still_speaks_for_the_domain()
    {
        // The control for the rule above: only the system mailbox is set aside. A learned address a person
        // wrote from on the same host is a fact about the domain like any other, and still demotes.
        const long aramco = OtherCustomer;
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "no-reply@etimad.gov.sa",
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)],
        }, Corpus([new(Sec, "Saudi Electricity Company"), new(aramco, "Saudi Aramco")],
            identifiers: [new(1, aramco, CustomerIdentifierType.Email, "tenders.desk@etimad.gov.sa", true, 1m, CustomerIdentifierSources.LeadReviewLearned)]),
            Policy);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == aramco);
    }

    [Fact]
    public void A_vendor_block_that_is_our_own_name_makes_the_domain_it_spells_ours_exactly_as_routing_reads_it()
    {
        // THE SEAM (T09a against T09c). Routing asks TenantSelfIdentity.DomainSelfNames: the configured name,
        // and the vendor block where it is a spelling of it. This Guard asked the configured name alone. A
        // print whose vendor block reads "ALI ZAID AL-QURAISHI & PARTNERS ESOSA" from sales@esosa.com linked
        // SEC here at 0.95 on SEC's Domain row, while routing refused the same row as our own mail and left the
        // lead unassigned. A vendor block that is NOT our name still cannot make a domain ours.
        const string tenant = "ALI ZAID AL-QURAISHI & PARTNERS";
        var corpus = Corpus(
            customers: [new(Sec, "Saudi Electricity Company")],
            identifiers: [new(1, Sec, CustomerIdentifierType.Domain, "esosa.com", true, 0.95m, "CustomerContact")]);
        LeadClientEvidence Printed(string vendorBlock) => new()
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "sales@esosa.com",
            SupplierNameOnDocument = vendorBlock,
            TenantSelfNameKeys = [tenant]
        };

        const string ourSpelling = "ALI ZAID AL-QURAISHI & PARTNERS ESOSA";
        Assert.True(TenantSelfIdentity.IsOurs("esosa.com", Array.Empty<string>(),
            TenantSelfIdentity.DomainSelfNames([tenant], ourSpelling)));
        var ours = CustomerIdentityResolver.Resolve(Printed(ourSpelling), corpus, Policy);
        Assert.Null(ours.CustomerId);
        Assert.NotEqual(CustomerMatchReasonCodes.SenderDomain, ours.ReasonCode);

        var misread = CustomerIdentityResolver.Resolve(Printed("ESOSA"), corpus, Policy);
        Assert.Equal(Sec, misread.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, misread.ReasonCode);
    }

    private static CustomerIdentifierSnapshot Alias(long id, long customerId, string normalizedValue) =>
        new(id, customerId, CustomerIdentifierType.Alias, normalizedValue, true, 0.90m,
            CustomerIdentifierSources.LeadReviewLearned);

    private static ClientResolutionCorpus Corpus(
        IReadOnlyList<CustomerNameSnapshot>? customers = null,
        IReadOnlyList<CustomerIdentifierSnapshot>? identifiers = null,
        IReadOnlyList<CustomerContactSnapshot>? contacts = null,
        IReadOnlyList<PriorSenderResolution>? priorSenders = null) => new()
        {
            Customers = customers ?? [],
            Identifiers = identifiers ?? [],
            Contacts = contacts ?? [],
            PriorSenderResolutions = priorSenders ?? []
        };
}
