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
