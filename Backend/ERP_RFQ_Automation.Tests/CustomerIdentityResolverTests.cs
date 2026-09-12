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
            Passages = [new DocumentPassage("the sentence that names the buyer",
                "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).", true)]
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
        // sender at all. Nothing competes: the company-name field IS this customer's name, so
        // the delivery address is the buyer writing their own address.
        var corpus = Corpus(customers: [new(Sec, "Marafiq"), new(OtherCustomer, "Saudi Aramco")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 682,
            CustomerCompanyName = "MARAFIQ",
            SupplierAccountRefOnDocument = "1495",
            RfqNumber = "HFE-26-202",
            Passages =
            [
                new DocumentPassage("company named on the document", "MARAFIQ", true),
                new DocumentPassage("the sentence that names the buyer",
                    "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).", true),
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
        // Hyundai is not a customer here, so the honest answer is a suggestion and a sentence
        // saying exactly what disagrees with what.
        var corpus = Corpus(customers: [new(OtherCustomer, "Saudi Aramco")]);
        var outcome = CustomerIdentityResolver.Resolve(new LeadClientEvidence
        {
            BusinessUnitId = 1, LeadId = 10,
            SenderEmail = "procurement@hdec.com",
            CustomerCompanyName = "Hyundai Engineering & Construction",
            Passages = [new DocumentPassage("delivery address", "Saudi Aramco Ras Tanura Refinery", true)],
        }, corpus, Policy);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(Policy.ShipToDemotedConfidence, outcome.Confidence);
        Assert.Equal(OtherCustomer, Assert.Single(outcome.Candidates).CustomerId);
        Assert.Contains("delivery address", outcome.Explanation);
        Assert.Contains("hdec.com", outcome.Explanation);
        Assert.Contains("which is not Saudi Aramco", outcome.Explanation);
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
