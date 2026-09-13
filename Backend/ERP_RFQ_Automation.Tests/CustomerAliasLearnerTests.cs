using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The learning loop is where a wrong client becomes a PERMANENT wrong client, so every
/// poisoning safeguard (P1..P7) is asserted here, not assumed.
/// </summary>
public sealed class CustomerAliasLearnerTests
{
    private const long Tenant = 8100;
    private const long OtherTenant = 8200;
    private const long Sec = 8301;
    private const long Aramco = 8302;
    private const long Swcc = 8303;

    [Fact]
    public async Task A_human_correction_teaches_sender_domain_name_and_portal_account()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);

        var result = await new CustomerAliasLearner(context)
            .LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        Assert.Equal(0, result.Expired);
        var learned = await LearnedAsync(context);
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.Email
                                      && i.NormalizedValue == "57322@se.com.sa" && i.IsVerified);
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.Domain
                                      && i.NormalizedValue == "se.com.sa" && i.Confidence == 0.95m);
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.Alias
                                      && i.NormalizedValue == CustomerNameNormalizer.LooseKey("Saudi Electricity Company"));
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.PortalAccount
                                      && i.NormalizedValue.EndsWith("|2004414", StringComparison.Ordinal));
        Assert.All(learned, i =>
        {
            Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, i.Source);
            Assert.Equal(lead.Id, i.LearnedFromLeadId);
            Assert.Equal(99, i.LearnedFromReviewAuditId);
            Assert.Equal(1, i.ObservationCount);
            Assert.NotNull(i.LastObservedOn);
        });
    }

    [Fact]
    public async Task An_rfq_numbering_shape_is_learned_unverified_so_it_can_never_auto_link()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.Rfqno = "C001046556";

        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var pattern = (await LearnedAsync(context))
            .Single(i => i.IdentifierType == CustomerIdentifierType.RfqNumberPattern);
        Assert.Equal("^C\\d{9}$", pattern.NormalizedValue);
        Assert.False(pattern.IsVerified);
        Assert.Equal(0.50m, pattern.Confidence);
    }

    [Fact]
    public async Task P6_a_machine_match_never_teaches_itself()
    {
        // THE poisoning path: one machine mistake bootstrapping into an authoritative alias
        // that then "confirms" every later document. The gate is the lead's own status.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.AutoResolveCommercialIdentity(Sec, null, CustomerMatchReasonCodes.SenderDomain, 0.95m, "machine", DateTime.UtcNow);

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        Assert.Equal(0, result.Learned);
        Assert.Empty(await LearnedAsync(context));
    }

    [Theory]
    [InlineData(LeadCustomerMatchStatuses.Unresolved)]
    [InlineData(LeadCustomerMatchStatuses.Suggested)]
    [InlineData(LeadCustomerMatchStatuses.Ambiguous)]
    [InlineData(LeadCustomerMatchStatuses.AutoMatched)]
    [InlineData(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved)]
    [InlineData(LeadCustomerMatchStatuses.Confirmed)]
    [InlineData(LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved)]
    [InlineData(LeadCustomerMatchStatuses.VerifiedEmail)]
    [InlineData("CUSTOMER_CONFIRMED")]
    [InlineData("VERIFIED")]
    [InlineData("")]
    public void The_human_statuses_filtered_in_SQL_are_exactly_the_ones_a_human_decided(string status)
        // The learner filters earlier decisions by status in SQL, before a read is capped, so it holds the
        // statuses as a list. The day IsHumanDecided gains or loses one, this fails instead of the list drifting.
        => Assert.Equal(LeadCustomerMatchStatuses.IsHumanDecided(status),
            CustomerAliasLearner.HumanDecidedStatuses.Contains(status, StringComparer.Ordinal));

    [Fact]
    public async Task P1_the_tenants_own_identity_is_never_learned()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        // Our own tenant mailbox lives on quraishi.example.
        foreach (var config in await context.EmailConfigurations.Where(c => c.BusinessUnitId == Tenant).ToListAsync())
            config.EmailAddress = "rfq@quraishi.example";
        await context.SaveChangesAsync();

        var lead = await LoadLeadAsync(context, 8401);
        // The reviewer approved the lead, but the extracted "customer" is our own vendor
        // block and the sender is our own mailbox domain — a forwarded internal message.
        lead.CustomerCompanyNameExtracted = "ALI ZAID AL-QURAISHI & PARTNERS";
        lead.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI & PARTNERS";
        lead.CustomerBuyerEmailExtracted = "rfq@quraishi.example";
        lead.Clientemail = "rfq@quraishi.example";
        lead.EmailIngests!.FromEmail = "rfq@quraishi.example";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var learned = await LearnedAsync(context);
        Assert.DoesNotContain(learned, i => i.IdentifierType == CustomerIdentifierType.Alias);
        Assert.DoesNotContain(learned, i => i.NormalizedValue.Contains("quraishi.example", StringComparison.Ordinal));
        Assert.Contains(CustomerAliasLearner.SkipSelfIdentity, result.SkipReasons);
    }

    [Theory]
    [InlineData("extraction@pipeline.local")]
    [InlineData("sec@system.com")]
    [InlineData("manual@upload.com")]
    [InlineData("system@excel.upload")]
    public async Task P2_nexoras_ingestion_placeholders_are_never_learned(string sender)
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = sender;
        lead.Clientemail = sender;
        lead.CustomerBuyerEmailExtracted = null;

        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var learned = await LearnedAsync(context);
        Assert.DoesNotContain(learned, i => i.IdentifierType is CustomerIdentifierType.Email or CustomerIdentifierType.Domain);
    }

    [Theory]
    // A consumer mailbox: one freight agent forwards bids for four different end customers.
    [InlineData("Buyer Person <buyer.person@gmail.com>", "buyer.person@gmail.com")]
    [InlineData("agent@live.com", "agent@live.com")]
    // A procurement portal's relay, including a sending host the portal minted itself.
    [InlineData("noreply@ariba.com", "noreply@ariba.com")]
    [InlineData("system@s4.ansmtp.ariba.com", "system@s4.ansmtp.ariba.com")]
    [InlineData("alerts@bidnet.com", "alerts@bidnet.com")]
    public async Task P2_a_personal_or_relay_sender_never_becomes_an_identifier(string from, string address)
    {
        // THE CHANGE THIS TEST GUARDS. Until now ANY confirmed sender became a VERIFIED Email
        // identifier at 1.00 — the strongest, most exclusive thing this engine can write. On the
        // owner's live tenant that is how "Saudi Aramco" came to own personal addresses at
        // live.com and bidnet.com: one confirmation each, on one document each, and from then on
        // every forward from that mailbox was pinned to Aramco whatever the attachment said.
        //
        // The mailbox is still evidence — an address a human typed on the customer profile still
        // matches exactly in the resolver. What the learner refuses to do is MINT one of these
        // from a single document. A consumer mailbox people link to the same customer AGAIN is learned
        // as an address (P2_a_free_mail_buyer_people_confirmed_twice_...); a relay never is.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = from;
        lead.Clientemail = address;
        lead.CustomerBuyerEmailExtracted = null;

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var learned = await LearnedAsync(context);
        Assert.DoesNotContain(learned, i => i.IdentifierType == CustomerIdentifierType.Email);
        // The domain rule is unchanged for free mail and newly closed for relays: ariba.com was
        // not a free-mail domain, so it used to be learned as a Domain at 0.95 and would have
        // auto-linked the next portal-delivered RFQ from a completely different buyer.
        Assert.DoesNotContain(learned, i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.Contains(CustomerAliasLearner.SkipPersonalOrRelayAddress, result.SkipReasons);
        // Everything else the document said is still learned: only the mailbox is refused.
        Assert.Contains(learned, i => i.IdentifierType == CustomerIdentifierType.Alias);
    }

    [Fact]
    public async Task P2_a_free_mail_buyer_people_confirmed_twice_for_one_customer_is_learned_as_an_address_never_a_domain()
    {
        // THE BASE INTENT, RESTORED. P2_a_free_mail_domain_is_never_learned_as_a_domain_but_the_address_still_is
        // asserted that a gmail buyer's address IS learned. Refusing every consumer address closed the live.com
        // incident and stranded every sole trader on gmail: reps confirmed Al-Rashid Trading's buyer again and
        // again, and the next message was only ever offered at 0.65. One confirmation still writes nothing
        // (that is what closed the incident); the second for the same customer writes the ADDRESS, never gmail.com.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, Rashid, Tenant, "Al-Rashid Trading");
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        FromGmailBuyer(first, Rashid);
        var firstResult = await learner.LearnFromReviewAsync(Tenant, first, Rashid, null, 99);
        await context.SaveChangesAsync();
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.IdentifierType is CustomerIdentifierType.Email or CustomerIdentifierType.Domain);
        Assert.Contains(CustomerAliasLearner.SkipPersonalOrRelayAddress, firstResult.SkipReasons);

        var second = await LoadLeadAsync(context, 8402);
        FromGmailBuyer(second, Rashid);
        var secondResult = await learner.LearnFromReviewAsync(Tenant, second, Rashid, null, 100);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        var email = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        Assert.Equal(GmailBuyer, email.NormalizedValue);
        Assert.Equal(Rashid, email.CustomerId);
        Assert.True(email.IsVerified);
        Assert.Equal(1.00m, email.Confidence);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, email.Source);
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.DoesNotContain(CustomerAliasLearner.SkipPersonalOrRelayAddress, secondResult.SkipReasons);

        // The next message from that buyer, with nothing else on it, is linked by the address.
        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8605, Tenant, buyersName: null);
            next.Rfqno = null;
            next.Clientemail = GmailBuyer;
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8605).FromEmail = $"Buyer Person <{GmailBuyer}>";
            await seed.SaveChangesAsync();
        }
        var outcome = await ResolveWithoutSavingAsync(db, 8605);
        Assert.Equal(Rashid, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, outcome.Status);
    }

    [Fact]
    public async Task P2_a_free_mail_address_people_linked_to_two_customers_is_an_agents_and_loses_what_it_taught()
    {
        // The freight agent: one gmail account forwards bids for several end customers. Once a person links the
        // address to a second customer it names neither, so nothing is learned and the address the first
        // customer was taught is expired, and further confirmations for the first customer do not win it back.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, Rashid, Tenant, "Al-Rashid Trading");
            foreach (var id in new long[] { 8403, 8404 })
                Seed.Lead(seed, id, Tenant, buyersName: null);
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context);
        foreach (var (leadId, audit) in new[] { (8401L, 99L), (8402L, 100L) })
        {
            var lead = await LoadLeadAsync(context, leadId);
            FromGmailBuyer(lead, Rashid);
            await learner.LearnFromReviewAsync(Tenant, lead, Rashid, null, audit);
            await context.SaveChangesAsync();
        }
        Assert.Single(await ActiveAsync(context),
            i => i.IdentifierType == CustomerIdentifierType.Email && i.CustomerId == Rashid);

        var forSec = await LoadLeadAsync(context, 8403);
        FromGmailBuyer(forSec, Sec);
        var result = await learner.LearnFromReviewAsync(Tenant, forSec, Sec, null, 101);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipFreeMailAddressConfirmedForAnotherCustomer, result.SkipReasons);
        Assert.Equal(1, result.Expired);
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.IdentifierType is CustomerIdentifierType.Email or CustomerIdentifierType.Domain);

        var again = await LoadLeadAsync(context, 8404);
        FromGmailBuyer(again, Rashid);
        var againResult = await learner.LearnFromReviewAsync(Tenant, again, Rashid, null, 102);
        await context.SaveChangesAsync();
        Assert.Contains(CustomerAliasLearner.SkipFreeMailAddressConfirmedForAnotherCustomer, againResult.SkipReasons);
        Assert.DoesNotContain(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Email);
    }

    [Fact]
    public async Task P2_a_relay_address_confirmed_twice_for_one_customer_is_still_never_learned()
    {
        // The free-mail rule must not reach a relay: noreply@ariba.com carries every Ariba buyer's RFQ.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var learner = new CustomerAliasLearner(context);
        foreach (var (leadId, audit) in new[] { (8401L, 99L), (8402L, 100L) })
        {
            var lead = await LoadLeadAsync(context, leadId);
            lead.EmailIngests!.FromEmail = "noreply@ariba.com";
            lead.Clientemail = "noreply@ariba.com";
            lead.CustomerBuyerEmailExtracted = null;
            await learner.LearnFromReviewAsync(Tenant, lead, Sec, null, audit);
            await context.SaveChangesAsync();
        }

        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.IdentifierType is CustomerIdentifierType.Email or CustomerIdentifierType.Domain);
    }

    [Fact]
    public async Task P8_a_name_that_looks_nothing_like_the_customer_is_recorded_but_never_trusted()
    {
        // THE PRODUCTION INCIDENT. On the owner's live tenant "Saudi Aramco" carries the aliases
        // "FULTON COUNTY GOVERNMENT", "KORICS" and "ARCENE SUPPLY SERVICES LLP". The only gates
        // on learning the printed company name were "not empty" and "not our own name" — nothing
        // ever compared the words on the page with the words on the customer record.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.CustomerCompanyNameExtracted = "FULTON COUNTY GOVERNMENT";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipAliasUnlikeCustomer, result.SkipReasons);

        // The reviewer's statement is not thrown away — it is filed where a person can look at
        // it — but it is not authoritative on either axis the resolver checks.
        var alias = await context.Set<CustomerIdentifier>()
            .SingleAsync(i => i.IdentifierType == CustomerIdentifierType.Alias && i.EffectiveTo == null);
        Assert.Equal(CustomerNameNormalizer.LooseKey("FULTON COUNTY GOVERNMENT"), alias.NormalizedValue);
        Assert.False(alias.IsVerified);
        Assert.Equal(0.50m, alias.Confidence);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, alias.Source);
        Assert.DoesNotContain(CustomerIdentifierSources.TrustedForAutoLink, source =>
            string.Equals(source, CustomerAliasLearner.UnverifiedAliasSource, StringComparison.Ordinal));
    }

    [Fact]
    public async Task P8_a_demoted_alias_does_not_resolve_the_next_document()
    {
        // The consequence the incident was actually made of: after ONE confirmation, every later
        // print naming that company was offered to the wrong client. End to end, it no longer is.
        using var db = new TestDb();
        await using (var teaching = await SeedAsync(db))
        {
            var lead = await LoadLeadAsync(teaching, 8401);
            lead.CustomerCompanyNameExtracted = "FULTON COUNTY GOVERNMENT";
            // Strip every other signal so the alias is the only thing on trial.
            lead.EmailIngests!.FromEmail = "extraction@pipeline.local";
            lead.Clientemail = null;
            lead.CustomerBuyerEmailExtracted = null;
            lead.CustomerPortalNameExtracted = null;
            await new CustomerAliasLearner(teaching).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
            await teaching.SaveChangesAsync();
        }

        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8603, Tenant, buyersName: "Someone Else");
            next.Clientemail = "extraction@pipeline.local";
            next.CustomerCompanyNameExtracted = "FULTON COUNTY GOVERNMENT";
            await seed.SaveChangesAsync();
            (await seed.EmailIngests.SingleAsync(x => x.Id == 20_000 + 8603)).FromEmail = "extraction@pipeline.local";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, 8603);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
    }

    [Fact]
    public async Task P8_the_customers_own_initials_are_still_learned_as_a_verified_alias()
    {
        // The gate must not break the learning loop it protects. A buyer writing its own
        // initials is the commonest alias there is, and it scores only 0.67 on similarity —
        // which is exactly why AcronymKey is one of the three ways in.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, Swcc, Tenant, "Saline Water Conversion Corporation");
            await seed.SaveChangesAsync();
        }
        var lead = await LoadLeadAsync(context, 8401);
        lead.CustomerCompanyNameExtracted = "SWCC";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Swcc, null, 99);
        await context.SaveChangesAsync();

        Assert.DoesNotContain(CustomerAliasLearner.SkipAliasUnlikeCustomer, result.SkipReasons);
        var alias = (await LearnedAsync(context))
            .Single(i => i.IdentifierType == CustomerIdentifierType.Alias);
        Assert.Equal("SWCC", alias.NormalizedValue);
        Assert.True(alias.IsVerified);
        Assert.Equal(0.90m, alias.Confidence);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, alias.Source);
    }

    [Theory]
    // Spelled roughly the same — the transliteration and abbreviation variance this platform
    // exists to absorb. A reviewer resolves it once and the loop must keep it.
    [InlineData("SAUDI ELECTRICITY CO.", "Saudi Electricity Company", true)]
    [InlineData("Saudi Electricty Company", "Saudi Electricity Company", true)]
    [InlineData("MARAFIQ", "Marafiq", true)]
    // The initials the buyer writes for itself.
    [InlineData("SWCC", "Saline Water Conversion Corporation", true)]
    [InlineData("SEC", "Saudi Electricity Company", true)]
    // The customer's name as one part of a longer print, set off by a dash or bracket.
    [InlineData("SAUDI ARABIAN OIL COMPANY - SAUDI ARAMCO", "Saudi Aramco", true)]
    [InlineData("Saudi Electricity Company - Eastern Operating Area", "Saudi Electricity Company", true)]
    // Shorter: every distinctive word printed is one of the customer's own.
    [InlineData("ARAMCO", "Saudi Aramco", true)]
    [InlineData("ALRAJHI", "Al Rajhi Bank", true)]
    [InlineData("Marafiq Jubail", "Power and Water Utility Company for Jubail and Yanbu (Marafiq)", true)]
    // Longer by distinctive words the customer's name does not carry. This row used to expect
    // true, and it enshrined defect #15: the whole-word subsequence rule that accepted it is the
    // same rule that accepted SATORP's full name and a contractor's header (the two rows below)
    // as trusted Saudi Aramco aliases. A branch printed this way is recorded for review instead.
    [InlineData("MARAFIQ Yanbu Power & Desalination", "Marafiq", false)]
    [InlineData("SAUDI ARAMCO TOTAL REFINING AND PETROCHEMICAL", "Saudi Aramco", false)]
    [InlineData("HYUNDAI ENGINEERING FOR SAUDI ARAMCO", "Saudi Aramco", false)]
    // The three aliases the live tenant actually accumulated against "Saudi Aramco".
    [InlineData("FULTON COUNTY GOVERNMENT", "Saudi Aramco", false)]
    [InlineData("KORICS", "Saudi Aramco", false)]
    [InlineData("ARCENE SUPPLY SERVICES LLP", "Saudi Aramco", false)]
    // Nothing but country and legal words names nobody, even when it equals the customer's name (#16).
    [InlineData("SAUDI COMPANY", "Saudi Company", false)]
    [InlineData("Kingdom of Saudi Arabia", "Saudi Aramco", false)]
    // Fails closed: with no customer name there is nothing to compare against, and "accept
    // whatever is printed" is the defect, not the fallback.
    [InlineData("Saudi Aramco", null, false)]
    [InlineData("Saudi Aramco", "   ", false)]
    [InlineData("", "Saudi Aramco", false)]
    public void The_sanity_gate_asks_only_whether_the_printed_name_could_be_this_customer(
        string? extracted, string? customerName, bool resembles)
        => Assert.Equal(resembles, CustomerAliasLearner.ResemblesCustomerName(extracted, customerName));

    [Fact]
    public async Task P9_a_shared_supplier_network_pair_is_skipped_while_the_buyers_own_portal_is_learned()
    {
        // Our Ariba Network ID is ONE number that identifies US to every buyer on the network,
        // so "ARIBA|<our id>" names nobody. SEC issued vendor code 2004414 itself and it means
        // nothing anywhere else, so "MATERIALS E-BIDDING SYSTEM|2004414" names SEC — that pair
        // is the case the tier was built for and must stay learnable.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var learner = new CustomerAliasLearner(context);

        var sec = await LoadLeadAsync(context, 8401);
        await learner.LearnFromReviewAsync(Tenant, sec, Sec, null, 99);
        await context.SaveChangesAsync();

        var viaAriba = await LoadLeadAsync(context, 8402);
        viaAriba.CustomerPortalNameExtracted = "SAP Ariba";
        var result = await learner.LearnFromReviewAsync(Tenant, viaAriba, Aramco, null, 100);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipSharedSupplierNetwork, result.SkipReasons);
        var pair = await context.Set<CustomerIdentifier>()
            .SingleAsync(i => i.IdentifierType == CustomerIdentifierType.PortalAccount
                              && i.EffectiveTo == null);
        Assert.Equal(Sec, pair.CustomerId);
        Assert.EndsWith("|2004414", pair.NormalizedValue, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Re_confirmation_increments_the_observation_count()
    {
        // CORROBORATION IS RECORDED, NOT ENFORCED — deliberately, and this test pins both
        // halves so the day the owner takes that decision the data it needs is already real.
        //
        // The counter climbs with every human confirmation. Nothing reads it: the resolver's
        // learned-alias tier asks IsVerified, Source and Confidence and stops, so ONE sighting
        // still auto-links exactly as fifty would. Requiring a second sighting before auto-link
        // makes the platform slower to learn, which is a product call the owner has not made.
        // Where the enforcement would go is written on CustomerAliasLearner's <remarks>.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        var learner = new CustomerAliasLearner(context);

        for (var confirmation = 1; confirmation <= 3; confirmation++)
        {
            await learner.LearnFromReviewAsync(Tenant, lead, Sec, null, 98 + confirmation);
            await context.SaveChangesAsync();

            var row = await context.Set<CustomerIdentifier>()
                .SingleAsync(i => i.IdentifierType == CustomerIdentifierType.Alias && i.EffectiveTo == null);
            Assert.Equal(confirmation, row.ObservationCount);
            Assert.NotNull(row.LastObservedOn);
            // Unchanged on purpose: the FIRST sighting is already authoritative.
            Assert.True(row.IsVerified);
            Assert.Contains(CustomerIdentifierSources.TrustedForAutoLink, source =>
                string.Equals(source, row.Source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task P3_an_address_owned_by_another_customer_is_skipped_never_stolen()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
        {
            BusinessUnitId = Tenant,
            CustomerId = Aramco,
            IdentifierType = CustomerIdentifierType.Email,
            NormalizedValue = "57322@se.com.sa",
            DisplayValue = "57322@se.com.sa",
            IsVerified = true,
            Confidence = 1m,
            Source = "CustomerProfile",
            EffectiveFrom = DateTime.UtcNow.AddDays(-5)
        });
        await context.SaveChangesAsync();
        var lead = await LoadLeadAsync(context, 8401);

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipAliasConflict, result.SkipReasons);
        var owner = await context.Set<CustomerIdentifier>()
            .SingleAsync(i => i.IdentifierType == CustomerIdentifierType.Email
                              && i.NormalizedValue == "57322@se.com.sa" && i.EffectiveTo == null);
        Assert.Equal(Aramco, owner.CustomerId);
        // The non-exclusive knowledge is still learned: only the contested value is skipped.
        Assert.Contains(await LearnedAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias);
    }

    [Fact]
    public async Task P4_two_customers_may_legitimately_share_an_alias()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        var learner = new CustomerAliasLearner(context);

        await learner.LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var second = await LoadLeadAsync(context, 8402);
        await learner.LearnFromReviewAsync(Tenant, second, Aramco, null, 100);
        await context.SaveChangesAsync();

        var aliases = await context.Set<CustomerIdentifier>()
            .Where(i => i.IdentifierType == CustomerIdentifierType.Alias && i.EffectiveTo == null)
            .ToListAsync();
        Assert.Equal(2, aliases.Count);
        Assert.Equal([Sec, Aramco], aliases.Select(a => a.CustomerId).OrderBy(id => id));
    }

    [Fact]
    public async Task P5_changing_the_customer_expires_what_this_lead_previously_taught()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        var learner = new CustomerAliasLearner(context);

        await learner.LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();
        Assert.All(await LearnedAsync(context), i => Assert.Equal(Sec, i.CustomerId));

        // The reviewer corrects the correction: it was Aramco all along.
        var result = await learner.LearnFromReviewAsync(Tenant, lead, Aramco, Sec, 101);
        await context.SaveChangesAsync();

        Assert.True(result.Expired > 0);
        Assert.Empty(await context.Set<CustomerIdentifier>()
            .Where(i => i.CustomerId == Sec && i.EffectiveTo == null
                        && i.Source == CustomerIdentifierSources.LeadReviewLearned)
            .ToListAsync());
        Assert.All(await LearnedAsync(context), i => Assert.Equal(Aramco, i.CustomerId));
    }

    [Fact]
    public async Task Re_confirming_the_same_client_reinforces_instead_of_duplicating()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        var learner = new CustomerAliasLearner(context);

        await learner.LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();
        var second = await learner.LearnFromReviewAsync(Tenant, lead, Sec, null, 102);
        await context.SaveChangesAsync();

        Assert.Equal(0, second.Learned);
        Assert.True(second.Reinforced > 0);
        var alias = await context.Set<CustomerIdentifier>()
            .SingleAsync(i => i.IdentifierType == CustomerIdentifierType.Alias && i.EffectiveTo == null);
        Assert.Equal(2, alias.ObservationCount);
    }

    [Fact]
    public async Task What_one_tenant_learns_never_resolves_another_tenants_lead()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, OtherTenant);
            Seed.Customer(seed, 8399, OtherTenant, "Some Other Client");
            var foreignLead = Seed.Lead(seed, 8501, OtherTenant, buyersName: "Foreign Buyer");
            foreignLead.CustomerCompanyNameExtracted = "Saudi Electricity Company";
            foreignLead.Clientemail = "57322@se.com.sa";
            await seed.SaveChangesAsync();
        }

        await using var foreign = db.ContextFor(OtherTenant);
        var outcome = await new LeadCustomerResolutionService(foreign).ResolveAsync(OtherTenant, 8501);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
    }

    [Fact]
    public async Task What_a_human_taught_resolves_the_NEXT_document_by_itself()
    {
        // The whole point of the loop: correct it once, and Nexora gets it right from then on.
        using var db = new TestDb();
        await using (var teaching = await SeedAsync(db))
        {
            var lead = await LoadLeadAsync(teaching, 8401);
            await new CustomerAliasLearner(teaching).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
            await teaching.SaveChangesAsync();
        }

        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8601, Tenant, buyersName: "4T2-Khaled M. Al-dehdi");
            // A folder-ingested bid: the placeholder sender carries no information at all,
            // and the extracted company name is spelled differently from the customer record.
            next.Clientemail = "extraction@pipeline.local";
            next.CustomerCompanyNameExtracted = "SAUDI ELECTRICITY CO.";
            await seed.SaveChangesAsync();
            // Neutralise the seeded envelope sender (FromEmail is NOT NULL) with the very
            // placeholder production carries, so the LEARNED ALIAS is the only signal left.
            (await seed.EmailIngests.SingleAsync(x => x.Id == 20_000 + 8601)).FromEmail = "extraction@pipeline.local";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, 8601);

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedAlias, outcome.ReasonCode);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, outcome.Status);
    }

    // ── P15: resemblance is a shared distinctive word, never four shared letters ──

    [Theory]
    [InlineData("SAUDI ELECTRICITY", "Saudi Aramco")]
    [InlineData("SAUDI BASIC INDUSTRIES", "Saudi Aramco")]
    [InlineData("SAUDI KAYAN PETROCHEMICAL", "Saudi Aramco")]
    [InlineData("SADARA CHEMICAL", "Saudi Aramco")]
    [InlineData("SAUDI ARABIAN MINING", "Saudi Aramco")]
    [InlineData("Saudi Electricity", "Saudi Cable Company")]
    [InlineData("NATIONAL WATER", "National Grid SA")]
    [InlineData("Al Rajhi Bank", "Al Rashid Trading")]
    [InlineData("SAUDI WATER PARTNERSHIP", "Saudi Water Authority")]
    [InlineData("SAUDI ARABIA", "Saudi Aramco")]
    [InlineData("SAUDI COMPANY", "Saudi Aramco")]
    public void P15_names_that_share_only_opening_letters_are_not_the_same_customer(string printed, string customer)
    {
        // Every one of these cleared the 0.75 Jaro-Winkler gate this class used to trust: the
        // metric pays a bonus for the first four letters, and they all open SAUD, NATI or AL R.
        // One reviewer mis-click on any of them wrote a VERIFIED alias for the wrong company, and
        // "SAUDI ELECTRICITY" against Aramco turned every lead-680-shaped SEC print AMBIGUOUS.
        Assert.True(CustomerNameNormalizer.JaroWinkler(
            CustomerNameNormalizer.TightKey(printed), CustomerNameNormalizer.TightKey(customer)) >= 0.75d);
        Assert.False(CustomerAliasLearner.ResemblesCustomerName(printed, customer));
    }

    [Theory]
    // #20: initials two customers share name neither of them.
    [InlineData("SCC", "Saudi Cable Company", new[] { "Saudi Ceramics Company" }, true)]
    [InlineData("SEC", "Saudi Engineering Consultants", new[] { "Saudi Electricity Company" }, true)]
    [InlineData("SWCC", "Saline Water Conversion Corporation", new[] { "Saudi Electricity Company", "Saudi Aramco" }, false)]
    // #15: the other customer's exact name, or a closer shortening of it.
    [InlineData("SAUDI ARAMCO", "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)", new[] { "Saudi Aramco" }, true)]
    [InlineData("ARAMCO", "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)", new[] { "Saudi Aramco" }, true)]
    [InlineData("ARAMCO", "Saudi Aramco", new[] { "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)" }, false)]
    [InlineData("SAUDI ARAMCO TOTAL REFINING AND PETROCHEMICAL", "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)", new[] { "Saudi Aramco" }, false)]
    [InlineData("MARAFIQ", "Marafiq Yanbu", new[] { "Marafiq" }, true)]
    // Two records with one name: two equally good readings are not one customer's alias.
    [InlineData("Saudi Aramco", "Saudi Aramco", new[] { "Saudi Aramco" }, true)]
    // A city-named customer does not outrank the two-word name the print leads with.
    [InlineData("Saudi Electricity Company-DAMMAM", "Saudi Electricity Company", new[] { "Al Dammam Trading Co." }, false)]
    public void P15_P20_a_name_another_customer_owns_at_least_as_closely_is_not_this_customers_alias(
        string printed, string chosen, string[] others, bool namesAnother)
        => Assert.Equal(namesAnother, CustomerAliasLearner.NamesAnotherCustomerAtLeastAsClosely(printed, chosen, others));

    [Fact]
    public async Task P15_a_mis_click_between_two_saudi_companies_never_teaches_one_the_others_name()
    {
        // THE CONCRETE INPUT OF THE FINDING. The reviewer means Saudi Electricity and clicks Saudi
        // Aramco. The old gate scored the pair 0.79 and wrote "SAUDI ELECTRICITY" as a trusted
        // Aramco alias; the next SEC print, whose only evidence is its delivery address, then
        // matched both SEC's own name and Aramco's taught one and went AMBIGUOUS.
        using var db = new TestDb();
        await using (var teaching = await SeedAsync(db))
        {
            var lead = await LoadLeadAsync(teaching, 8401);
            lead.CustomerCompanyNameExtracted = "SAUDI ELECTRICITY COMPANY";
            StripAddresses(lead);
            lead.CustomerPortalNameExtracted = null;

            var result = await new CustomerAliasLearner(teaching).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
            await teaching.SaveChangesAsync();

            Assert.Contains(CustomerAliasLearner.SkipAliasUnlikeCustomer, result.SkipReasons);
            Assert.DoesNotContain(await LearnedAsync(teaching), i => i.IdentifierType == CustomerIdentifierType.Alias);
        }

        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8604, Tenant, buyersName: "Someone Else");
            next.Clientemail = "extraction@pipeline.local";
            next.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            await seed.SaveChangesAsync();
            (await seed.EmailIngests.SingleAsync(x => x.Id == 20_000 + 8604)).FromEmail = "extraction@pipeline.local";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, 8604);

        // Lead 680's shape keeps auto-linking SEC.
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.NotEqual(LeadCustomerMatchStatuses.Ambiguous, outcome.Status);
    }

    [Theory]
    [InlineData("SCC", SaudiCable)]
    [InlineData("SEC", SaudiEngineering)]
    [InlineData("SAUDI ARAMCO", Satorp)]
    public async Task P15_P20_a_name_another_customer_owns_is_recorded_but_never_trusted(string printed, long chosen)
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, Satorp, Tenant, "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)");
            Seed.Customer(seed, SaudiCable, Tenant, "Saudi Cable Company");
            Seed.Customer(seed, SaudiCeramics, Tenant, "Saudi Ceramics Company");
            Seed.Customer(seed, SaudiEngineering, Tenant, "Saudi Engineering Consultants");
            await seed.SaveChangesAsync();
        }
        var lead = await LoadLeadAsync(context, 8401);
        lead.CustomerCompanyNameExtracted = printed;

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, chosen, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipAliasNamesAnotherCustomer, result.SkipReasons);
        var alias = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias);
        Assert.Equal(chosen, alias.CustomerId);
        Assert.False(alias.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, alias.Source);
    }

    // ── P16: a name of nothing but country and legal words ───────────────────

    [Theory]
    [InlineData("SAUDI ARABIA")]
    [InlineData("SAUDI COMPANY")]
    [InlineData("Kingdom of Saudi Arabia")]
    [InlineData("National Company")]
    public async Task P16_a_company_name_of_only_country_and_legal_words_is_never_learned(string printed)
    {
        // The extractor took the country line of the address block and the reviewer correctly
        // linked the document to Aramco. "SAUDI ARABIA" became a trusted Aramco alias, and every
        // document naming Saudi Arabia in its buyer sentence or delivery address linked to Aramco
        // at 0.88. Not even a suggestion is kept: one click would promote it back.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.CustomerCompanyNameExtracted = printed;

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipAliasNotDistinctive, result.SkipReasons);
        Assert.DoesNotContain(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias);
    }

    // ── P17: a mailbox is the customer's only when something ties it to them ─

    [Fact]
    public async Task P17_a_colleague_forwarding_from_a_staff_domain_teaches_nothing_about_that_domain()
    {
        // CASE A. The intake mailbox is rfq@alquraishi.com; the salesman forwarding an SEC bid is
        // ahmed@alquraishi.com.sa. Only mailboxes counted as "us", so the reviewer's link taught
        // his address as SEC's Email at 1.00 and alquraishi.com.sa as SEC's Domain at 0.95, and
        // every later forward from any colleague linked to SEC whatever the attachment said.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        foreach (var config in await context.EmailConfigurations.Where(c => c.BusinessUnitId == Tenant).ToListAsync())
            config.EmailAddress = "rfq@alquraishi.com";
        context.Users.Add(new User
        {
            Id = 9101, FirstName = "Ahmed", LastName = "Salesman", Email = "ahmed@alquraishi.com.sa",
            PasswordHash = "not-a-hash", ImageUrl = "n/a", Buid = Tenant, IsActive = true,
            CreatedBy = "seed", CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = "Ahmed <ahmed@alquraishi.com.sa>";
        lead.Clientemail = "ahmed@alquraishi.com.sa";
        lead.CustomerBuyerEmailExtracted = null;
        // So only the user record can say the domain is ours (the vendor block would say it too).
        lead.SupplierNameOnDocument = null;

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipSelfIdentity, result.SkipReasons);
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.NormalizedValue.Contains("alquraishi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task P17_a_colleague_without_a_login_is_still_us_when_the_domain_spells_our_own_name()
    {
        // No user record and no mailbox on this domain: the forwarder has no Nexora seat. The tenant is
        // "ALI ZAID AL-QURAISHI & PARTNERS", the document's vendor block prints the same name
        // ("ALI ZAID AL-QURAISHI&PARTNERS EL"), and a domain whose own name spells ours is ours.
        // The business unit is named here because the vendor block ALONE no longer makes a domain ours
        // (T09a/T09c): the seed's unit is "Business Unit 8100", and this test used to pass on the vendor
        // block by itself, the very reading that let a misread "MARAFIQ" make marafiq.com.sa ours.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        (await context.BusinessUnits.IgnoreQueryFilters().SingleAsync(b => b.Id == Tenant)).BusinessUnitName =
            "ALI ZAID AL-QURAISHI & PARTNERS";
        await context.SaveChangesAsync();
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = "Salman <salman@alquraishi.com.sa>";
        lead.Clientemail = "salman@alquraishi.com.sa";
        lead.CustomerBuyerEmailExtracted = null;

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipSelfIdentity, result.SkipReasons);
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.NormalizedValue.Contains("alquraishi", StringComparison.Ordinal));
    }

    [Fact]
    public async Task P17_an_EPC_contractors_mailbox_is_never_minted_as_the_site_owners_identity()
    {
        // CASE B. Hyundai E&C buys for a Ras Tanura job and prints its own buyer's address; the
        // reviewer rightly says the DOCUMENT is Saudi Aramco's. That wrote hdec.com as Aramco's
        // Domain at 0.95, and S2 decides before the contractor and consignee rules ever run.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.CustomerBuyerEmailExtracted = "buyer@hdec.com";
        lead.CustomerCompanyNameExtracted = "Saudi Aramco";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipDomainNotTiedToCustomer, result.SkipReasons);
        var rows = await ActiveAsync(context);
        // Not even a suggestion: Email is exclusive whatever its source, so an unverified row would
        // make saving Hyundai's real contact at this address fail.
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        var domain = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.Equal("hdec.com", domain.NormalizedValue);
        Assert.False(domain.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, domain.Source);
        Assert.DoesNotContain(CustomerIdentifierSources.TrustedForAutoLink, source =>
            string.Equals(source, domain.Source, StringComparison.Ordinal));
        // The name on the document is still learned: only the intermediary's mailbox is refused.
        Assert.Contains(await LearnedAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias);
    }

    [Fact]
    public async Task P17_a_domain_another_customer_already_writes_from_is_not_filed_against_this_one()
    {
        // Hyundai E&C is a customer too, with a contact at hdec.com. P3 used to skip only the Email
        // half; the Domain half was still written for Aramco, and all hdec.com mail was AMBIGUOUS
        // from then on.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, Hyundai, Tenant, "Hyundai Engineering & Construction");
            Seed.Contact(seed, 9301, Tenant, Hyundai, "procurement@hdec.com");
            await seed.SaveChangesAsync();
        }
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.CustomerBuyerEmailExtracted = "buyer@hdec.com";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipDomainClaimedByAnotherCustomer, result.SkipReasons);
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.IdentifierType is CustomerIdentifierType.Email or CustomerIdentifierType.Domain);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P17_a_contact_on_the_customer_ties_its_domain_so_the_first_confirmation_is_trusted(bool withContact)
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await ForgetSiblingAddressesAsync(context);
        if (withContact)
        {
            await using var seed = db.ContextFor(null);
            Seed.Contact(seed, 9302, Tenant, Sec, "procurement@se.com.sa");
            await seed.SaveChangesAsync();
        }
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        var domain = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Domain);
        if (withContact)
        {
            var email = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
            Assert.Equal("57322@se.com.sa", email.NormalizedValue);
            Assert.True(email.IsVerified);
            Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, email.Source);
            Assert.True(domain.IsVerified);
            Assert.Equal(0.95m, domain.Confidence);
            Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, domain.Source);
        }
        else
        {
            // se.com.sa does not spell "Saudi Electricity": initials never tie a domain, because
            // se.com is Schneider Electric's. With no contact and no earlier human decision the
            // first confirmation is a suggestion.
            Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
            Assert.False(domain.IsVerified);
            Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, domain.Source);
            Assert.Contains(CustomerAliasLearner.SkipDomainNotTiedToCustomer, result.SkipReasons);
        }
    }

    [Fact]
    public async Task P17_a_second_human_confirmation_from_the_same_domain_promotes_the_unverified_domain()
    {
        // The first confirmation from an untied domain is a suggestion; the lead it confirmed is
        // itself the earlier human decision that ties the domain for the next one. The promotion
        // must move the SHELF as well as the flag, or the resolver, which checks Source, would
        // ignore the row forever.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await ForgetSiblingAddressesAsync(context);
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        StripAddresses(first);
        first.CustomerBuyerEmailExtracted = "57322@se.com.sa";
        await learner.LearnFromReviewAsync(Tenant, first, Sec, null, 99);
        await context.SaveChangesAsync();
        var suggested = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.False(suggested.IsVerified);

        var second = await LoadLeadAsync(context, 8402);
        StripAddresses(second);
        second.CustomerBuyerEmailExtracted = "92442@se.com.sa";
        await learner.LearnFromReviewAsync(Tenant, second, Sec, null, 100);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        var domain = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.True(domain.IsVerified);
        Assert.Equal(0.95m, domain.Confidence);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, domain.Source);
        Assert.Equal(2, domain.ObservationCount);
        Assert.Contains(rows, i => i.IdentifierType == CustomerIdentifierType.Email
                                   && i.NormalizedValue == "92442@se.com.sa" && i.IsVerified);
    }

    [Theory]
    [InlineData("marafiq.com.sa", "Marafiq", true)]
    [InlineData("procurement@marafiq.com.sa", "Power and Water Utility Company for Jubail and Yanbu (Marafiq)", true)]
    [InlineData("fultoncountyga.gov", "Fulton County Government", true)]
    [InlineData("alrajhibank.com.sa", "Al Rajhi Bank", true)]
    [InlineData("aramco.com", "Saudi Aramco", true)]
    [InlineData("saudiaramco.com", "Saudi Aramco", true)]
    [InlineData("mail.sabic.com", "SABIC", true)]
    // Initials never tie a domain: se.com is Schneider Electric's, swcc could be anybody's.
    [InlineData("se.com.sa", "Saudi Electricity Company", false)]
    [InlineData("se.com", "Saudi Electricity Company", false)]
    [InlineData("swcc.gov.sa", "Saline Water Conversion Corporation", false)]
    // An intermediary, or a host an intermediary named after its client.
    [InlineData("hdec.com", "Saudi Aramco", false)]
    [InlineData("aramco.hdec.com", "Saudi Aramco", false)]
    [InlineData("sabic.com", "Saudi Basic Industries Corporation", false)]
    // One short or generic word is not a name.
    [InlineData("oil.com", "Saudi Arabian Oil Company", false)]
    [InlineData("saudi.com", "Saudi Company", false)]
    [InlineData("watertech.com", "National Water Company", false)]
    [InlineData(null, "Saudi Aramco", false)]
    [InlineData("aramco.com", null, false)]
    public void P17_a_domain_is_tied_to_a_customer_by_its_own_name_never_by_initials(
        string? domain, string? name, bool spells)
    {
        // The learner ties customers with the strict reading and our own names use the permissive one; on
        // every row here the two agree.
        Assert.Equal(spells, CustomerAliasLearner.DomainLabelSpellsCustomerName(domain, name));
        Assert.Equal(spells, CustomerAliasLearner.DomainLabelSpellsName(domain, name));
    }

    [Theory]
    // One sector or place word of the name. Each of these tied its domain to the customer on the FIRST
    // confirmation: one mail from desk@pipes.com, a pipe marketplace, wrote the address at 1.00 and the
    // domain at 0.95, and the next trader there was linked to Arabian Pipes whatever the page said.
    [InlineData("bank.com", "Al Rajhi Bank")]
    [InlineData("pipes.com", "Arabian Pipes Company")]
    [InlineData("water.com", "National Water Company")]
    [InlineData("jubail.com", "Power and Water Utility Company for Jubail and Yanbu (Marafiq)")]
    [InlineData("utility.com", "Power and Water Utility Company for Jubail and Yanbu (Marafiq)")]
    [InlineData("electricity.com", "Saudi Electricity Company")]
    // A name that keys to one place word is a city, however whole.
    [InlineData("dammam.com", "Al Dammam Trading Co.")]
    public void P17_one_sector_or_place_word_of_a_customers_name_does_not_spell_that_customer(string domain, string name)
        => Assert.False(CustomerAliasLearner.DomainLabelSpellsCustomerName(domain, name));

    [Theory]
    // The only distinctive word, the bracketed trade name, the whole key, the whole name run together.
    [InlineData("aramco.com", "Saudi Aramco")]
    [InlineData("marafiq.com.sa", "Power and Water Utility Company for Jubail and Yanbu (Marafiq)")]
    [InlineData("marafiq.com.sa", "Marafiq")]
    [InlineData("satorp.com", "Saudi Aramco Total Refining & Petrochemical Co. (SATORP)")]
    [InlineData("mail.sabic.com", "SABIC")]
    [InlineData("alrajhibank.com.sa", "Al Rajhi Bank")]
    public void P17_a_customers_own_word_trade_name_or_whole_name_still_spells_it(string domain, string name)
        => Assert.True(CustomerAliasLearner.DomainLabelSpellsCustomerName(domain, name));

    [Fact]
    public void P17_our_own_name_keeps_the_permissive_reading()
    {
        // TenantSelfIdentity.IsOurs asks the permissive reading on purpose: a colleague with no login on a
        // domain that spells one of our distinctive words is ours, and the error costs a suggestion.
        Assert.True(CustomerAliasLearner.DomainLabelSpellsName("alquraishi.com.sa", "ALI ZAID AL-QURAISHI & PARTNERS"));
        Assert.False(CustomerAliasLearner.DomainLabelSpellsCustomerName("alquraishi.com.sa", "ALI ZAID AL-QURAISHI & PARTNERS"));
    }

    [Fact]
    public async Task P17_a_marketplace_on_a_sector_word_domain_is_not_tied_to_a_customer_by_one_confirmation()
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, ArabianPipes, Tenant, "Arabian Pipes Company");
            await seed.SaveChangesAsync();
        }
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.EmailIngests!.FromEmail = "desk@pipes.com";
        lead.Clientemail = "desk@pipes.com";
        lead.CustomerCompanyNameExtracted = null;
        lead.CustomerPortalNameExtracted = null;
        lead.DeliveryLocation = "Arabian Pipes Company - Dammam 2nd Industrial City";
        lead.ResolveCommercialIdentity(ArabianPipes, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, ArabianPipes, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        var domain = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.Equal("pipes.com", domain.NormalizedValue);
        Assert.False(domain.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, domain.Source);
        Assert.Contains(CustomerAliasLearner.SkipDomainNotTiedToCustomer, result.SkipReasons);

        // Another trader on the marketplace sends an SEC delivery: the domain no longer decides it for Arabian Pipes.
        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8606, Tenant, buyersName: null);
            next.Rfqno = null;
            next.Clientemail = "other.trader@pipes.com";
            next.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8606).FromEmail = "other.trader@pipes.com";
            await seed.SaveChangesAsync();
        }
        var outcome = await ResolveWithoutSavingAsync(db, 8606);
        Assert.NotEqual(ArabianPipes, outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate =>
            candidate.ReasonCode == CustomerMatchReasonCodes.SenderDomain && candidate.Confidence >= 0.85m);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == Sec);
    }

    // ── P23: a mailbox provider nobody listed ────────────────────────────────

    [Theory]
    [InlineData("agent@fastmail.com", false)]          // on the list: nothing at all
    [InlineData("agent@quietpost-mail.net", true)]     // not on any list: a suggestion, never a fact
    public async Task P23_a_consumer_mailbox_never_becomes_a_domain_fact_on_one_confirmation(string from, bool filedForReview)
    {
        // A freight agent forwards an SEC bid from a consumer provider. One confirmation made the
        // whole provider SEC's Domain at 0.95, and any later sender there, for any buyer, linked to
        // SEC at S2. A list of providers can never be complete, so the rule is the same one that
        // stops a contractor: a domain nothing ties to the customer is not a fact.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = from;
        lead.Clientemail = from;
        lead.CustomerBuyerEmailExtracted = null;

        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        Assert.DoesNotContain(await LearnedAsync(context), i => i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.Equal(filedForReview, rows.Any(i => i.IdentifierType == CustomerIdentifierType.Domain
                                                  && i.Source == CustomerAliasLearner.UnverifiedAliasSource));
    }

    [Fact]
    public async Task One_address_printed_as_both_sender_and_buyer_is_learned_once()
    {
        // An SEC portal mail carries 57322@se.com.sa as the sender AND as the buyer address on the
        // page. Both were proposed, two identical rows were staged, the unique index refused the
        // flush, and the review's learning was rolled back as "learningFailed".
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = "57322@se.com.sa";

        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Domain);
    }

    [Theory]
    // A colleague with no Nexora login, on a group domain that does not spell the tenant's name.
    [InlineData("ahmed@azq-group.com", "ahmed@azq-group.com")]
    // Two people at an EPC contractor, each confirmed for the site owner's job.
    [InlineData("procurement@hdec.com", "k.lee@hdec.com")]
    // Two agents at a consumer provider nobody listed.
    [InlineData("agent1@quietpost-mail.net", "agent2@quietpost-mail.net")]
    public async Task P17_an_envelope_sender_confirmed_twice_does_not_vouch_for_its_own_domain(string first, string second)
    {
        // THE DEFECT: a domain was tied to a customer by ANY earlier human decision from a mailbox on
        // it, read off the envelope. An intermediary that forwards the same buyer's bids twice, a
        // colleague with no login or a freight agent, therefore vouched for itself: the second
        // confirmation wrote its whole domain as that customer's at 0.95 and its address at 1.00, and
        // the next person on that domain forwarding another buyer's bid linked to the same customer at
        // S2, whatever the document said. The envelope says who carried the mail; only a buyer address
        // printed on an earlier document speaks for the buyer.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await ForgetSiblingAddressesAsync(context);
        var learner = new CustomerAliasLearner(context);
        var domain = first[(first.IndexOf('@') + 1)..];

        var leadA = await LoadLeadAsync(context, 8401);
        StripAddresses(leadA);
        leadA.EmailIngests!.FromEmail = first;
        leadA.Clientemail = first;
        await learner.LearnFromReviewAsync(Tenant, leadA, Sec, null, 99);
        await context.SaveChangesAsync();

        var leadB = await LoadLeadAsync(context, 8402);
        StripAddresses(leadB);
        leadB.EmailIngests!.FromEmail = second;
        leadB.Clientemail = second;
        var result = await learner.LearnFromReviewAsync(Tenant, leadB, Sec, null, 100);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        if (first == second)
        {
            // THE SAME MAILBOX CONFIRMED TWICE FOR ONE CUSTOMER IS THAT ADDRESS, NEVER ITS DOMAIN (LG01). The consumer
            // mailbox rule, applied to an organisation's mailbox: exactly this address is learned, verified, and
            // nothing else on the domain is. The rest of this test is unchanged: the domain stays unverified and the
            // next person on it is not linked by it.
            var address = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
            Assert.Equal(first, address.NormalizedValue);
            Assert.True(address.IsVerified);
        }
        else
        {
            Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        }
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Domain && i.IsVerified);
        Assert.Contains(CustomerAliasLearner.SkipDomainNotTiedToCustomer, result.SkipReasons);

        // The next person on that domain forwards an Aramco bid. The domain no longer decides it for SEC
        // at 0.95. The two SEC decisions from the domain still speak against Aramco's delivery address,
        // which only asks a person (both are offered): that is the rule that stops an EPC contractor's
        // next job linking to the site owner, and a demotion costs a click where a wrong link costs a quote.
        var third = $"someone.else@{domain}";
        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8403, Tenant, buyersName: null);
            next.Rfqno = null;
            next.Clientemail = third;
            next.DeliveryLocation = "Saudi Aramco Ras Tanura Refinery";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8403).FromEmail = third;
            await seed.SaveChangesAsync();
        }
        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, 8403);
        Assert.NotEqual(Sec, outcome.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate =>
            candidate.ReasonCode == CustomerMatchReasonCodes.SenderDomain && candidate.Confidence >= 0.85m);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == Aramco);
    }

    // ── P11: a buyer portal's pair is the customer's only when something ties it to them ──

    [Fact]
    public async Task P11_a_mis_click_on_a_buyer_portal_print_files_the_pair_for_review_not_as_a_fact()
    {
        // THE DEFECT: the portal + vendor-code pair was learned verified at 0.92 from one confirmation,
        // with no look at the document. The learned-portal tier decides before the passage tier, so one
        // mis-click on an SEC e-bidding print linked every later SEC print to Saudi Aramco at 0.92, and
        // lead 680's delivery address never got a say.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.CustomerCompanyNameExtracted = null;
        lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipPortalAccountNotTiedToCustomer, result.SkipReasons);
        var pair = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.Equal(Aramco, pair.CustomerId);
        Assert.False(pair.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, pair.Source);
    }

    [Fact]
    public async Task P11_the_buyers_own_portal_pair_is_trusted_when_the_document_names_that_buyer()
    {
        // SEC's own e-bidding print names SEC in its delivery address, so the production pair
        // "MATERIALS E-BIDDING SYSTEM / 2004414" is still learned as a fact and keeps linking at 0.92.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.CustomerCompanyNameExtracted = null;
        lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        Assert.DoesNotContain(CustomerAliasLearner.SkipPortalAccountNotTiedToCustomer, result.SkipReasons);
        var pair = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.Equal(Sec, pair.CustomerId);
        Assert.True(pair.IsVerified);
        Assert.Equal(0.92m, pair.Confidence);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, pair.Source);
    }

    [Fact]
    public async Task P11_a_pair_another_customer_already_holds_is_not_filed_against_a_second_one()
    {
        // With SEC already holding the pair, a confirmation for Aramco wrote it a second time, and every
        // later print from SEC's portal went AMBIGUOUS at 0.92, a state no further teaching could undo.
        // The document here even names Aramco, so only the other customer's hold can stop it.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
        {
            BusinessUnitId = Tenant, CustomerId = Sec, IdentifierType = CustomerIdentifierType.PortalAccount,
            NormalizedValue = "MATERIALS E BIDDING SYSTEM|2004414", DisplayValue = "MATERIALS E-BIDDING SYSTEM / 2004414",
            IsVerified = true, Confidence = 0.92m, Source = CustomerIdentifierSources.LeadReviewLearned,
            EffectiveFrom = DateTime.UtcNow.AddDays(-5), ObservationCount = 3, LastObservedOn = DateTime.UtcNow.AddDays(-1)
        });
        await context.SaveChangesAsync();
        var lead = await LoadLeadAsync(context, 8401);
        StripAddresses(lead);
        lead.CustomerCompanyNameExtracted = "Saudi Aramco";

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Aramco, null, 99);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipPortalAccountClaimedByAnotherCustomer, result.SkipReasons);
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.IdentifierType == CustomerIdentifierType.PortalAccount && i.CustomerId == Aramco);
    }

    [Fact]
    public async Task P11_a_second_human_decision_on_the_same_portal_pair_promotes_it()
    {
        // A print that names nobody ties nothing, so the first confirmation is a suggestion. The lead it
        // confirmed is itself the earlier human decision that ties the pair on the next one, exactly as
        // P10 ties a mailbox domain, and the promotion moves the shelf as well as the flag.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var sibling = await LoadLeadAsync(context, 8402);
        sibling.SupplierAccountRefOnDocument = null;
        await context.SaveChangesAsync();
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        StripAddresses(first);
        first.CustomerCompanyNameExtracted = null;
        await learner.LearnFromReviewAsync(Tenant, first, Sec, null, 99);
        await context.SaveChangesAsync();
        Assert.False(Assert.Single(await ActiveAsync(context),
            i => i.IdentifierType == CustomerIdentifierType.PortalAccount).IsVerified);

        var second = await LoadLeadAsync(context, 8402);
        StripAddresses(second);
        second.CustomerCompanyNameExtracted = null;
        second.SupplierAccountRefOnDocument = "2004414";
        await learner.LearnFromReviewAsync(Tenant, second, Sec, null, 100);
        await context.SaveChangesAsync();

        var pair = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.True(pair.IsVerified);
        Assert.Equal(0.92m, pair.Confidence);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, pair.Source);
        Assert.Equal(2, pair.ObservationCount);
    }

    [Fact]
    public async Task P11_a_wrong_pick_repeated_on_a_pair_only_print_does_not_promote_the_pair()
    {
        // THE FINDING, END TO END. An SEC e-bidding print whose delivery address names Saudi Electricity is
        // linked to Aramco (filed unverified). The next SEC print carries only the pair, resolves to nothing,
        // and the rep picks Aramco again. That earlier decision vouched for the pair whatever its page said,
        // the pair became Aramco's at 0.92, and lead 680's shape plus the pair linked to Aramco before its
        // address was read.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        PairOnlyPrint(first, Aramco);
        first.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
        await learner.LearnFromReviewAsync(Tenant, first, Aramco, null, 99);
        await context.SaveChangesAsync();

        var second = await LoadLeadAsync(context, 8402);
        PairOnlyPrint(second, Aramco);
        var result = await learner.LearnFromReviewAsync(Tenant, second, Aramco, null, 100);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipPortalAccountNotTiedToCustomer, result.SkipReasons);
        var pair = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.Equal(Aramco, pair.CustomerId);
        Assert.False(pair.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, pair.Source);

        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8607, Tenant, buyersName: null);
            next.Rfqno = null;
            next.Clientemail = "extraction@pipeline.local";
            next.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
            next.SupplierAccountRefOnDocument = "2004414";
            next.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
            next.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8607).FromEmail = "extraction@pipeline.local";
            await seed.SaveChangesAsync();
        }
        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, 8607);
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, outcome.Status);
    }

    [Fact]
    public async Task P11_an_earlier_decision_never_vouches_for_a_pair_on_a_print_that_names_another_customer()
    {
        // The other half: the earlier pick was on a print naming nobody, and THIS print names Saudi Electricity.
        // A page naming SEC cannot teach Aramco's pair, whatever was decided before.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        PairOnlyPrint(first, Aramco);
        await learner.LearnFromReviewAsync(Tenant, first, Aramco, null, 99);
        await context.SaveChangesAsync();

        var second = await LoadLeadAsync(context, 8402);
        PairOnlyPrint(second, Aramco);
        second.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
        var result = await learner.LearnFromReviewAsync(Tenant, second, Aramco, null, 100);
        await context.SaveChangesAsync();

        Assert.Contains(CustomerAliasLearner.SkipPortalAccountNotTiedToCustomer, result.SkipReasons);
        var pair = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.False(pair.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, pair.Source);
        Assert.Equal(2, pair.ObservationCount);
    }

    // ── REPAIR ROUND 2026-09-13 ─────────────────────────────────────────────────

    [Fact]
    public async Task P11_an_earlier_pick_on_a_page_naming_another_customer_vetoes_the_pair_however_often_the_pick_is_repeated()
    {
        // THE DEFECT (T24 residual). The rep picks Aramco on an SEC e-bidding print whose address names Saudi
        // Electricity Company, then twice more on pair-only prints. The pair-only prints vouched for each other and the
        // page that named SEC was simply not counted, so the third pick promoted the pair to 0.92, and lead 680's shape
        // plus the pair linked to Aramco from then on.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            var third = Seed.Lead(seed, 8403, Tenant, buyersName: null);
            third.Rfqno = null;
            third.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
            third.SupplierAccountRefOnDocument = "2004414";
            third.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
            var next = Seed.Lead(seed, 8609, Tenant, buyersName: null);
            next.Rfqno = null;
            next.Clientemail = "extraction@pipeline.local";
            next.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
            next.SupplierAccountRefOnDocument = "2004414";
            next.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
            next.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8609).FromEmail = "extraction@pipeline.local";
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        PairOnlyPrint(first, Aramco);
        first.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
        await learner.LearnFromReviewAsync(Tenant, first, Aramco, null, 99);
        await context.SaveChangesAsync();

        foreach (var (leadId, audit) in new[] { (8402L, 100L), (8403L, 101L) })
        {
            var pairOnly = await LoadLeadAsync(context, leadId);
            PairOnlyPrint(pairOnly, Aramco);
            var result = await learner.LearnFromReviewAsync(Tenant, pairOnly, Aramco, null, audit);
            await context.SaveChangesAsync();
            Assert.Contains(CustomerAliasLearner.SkipPortalAccountNotTiedToCustomer, result.SkipReasons);
        }

        var pair = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.PortalAccount);
        Assert.Equal(Aramco, pair.CustomerId);
        Assert.False(pair.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, pair.Source);
        Assert.Equal(3, pair.ObservationCount);

        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, 8609);
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
    }

    [Fact]
    public async Task P5_the_next_confirmation_promotes_a_mailbox_a_mis_click_relink_demoted_back_to_its_customer()
    {
        // THE DEFECT (T25 residual). One mis-click relink demotes SEC's fifty-times-confirmed address and domain, as it
        // should, and "the next confirmation promotes them back". For the portal pair it did. For the mailbox the next
        // SEC confirmation read the relink's own Aramco decision as a veto and skipped the demoted rows, so the address
        // and domain stayed unverified and SEC's next plain mail fell from 1.00 to a 0.65 tie with Aramco.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        context.Set<CustomerIdentifier>().AddRange(
            ConfirmedFiftyTimes(CustomerIdentifierType.Email, "57322@se.com.sa", 1.00m),
            ConfirmedFiftyTimes(CustomerIdentifierType.Domain, "se.com.sa", 0.95m),
            ConfirmedFiftyTimes(CustomerIdentifierType.Alias, CustomerNameNormalizer.LooseKey("Saudi Electricity Company"), 0.90m),
            ConfirmedFiftyTimes(CustomerIdentifierType.PortalAccount, "MATERIALS E BIDDING SYSTEM|2004414", 0.92m));
        await context.SaveChangesAsync();
        await using (var seed = db.ContextFor(null))
        {
            foreach (var (id, sender) in new[] { (8610L, "57322@se.com.sa"), (8611L, "60000@se.com.sa") })
            {
                var next = Seed.Lead(seed, id, Tenant, buyersName: null);
                next.Rfqno = null;
                next.Clientemail = sender;
                seed.EmailIngests.Local.Single(i => i.Id == 20_000 + id).FromEmail = sender;
            }
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context);

        var relinked = await LoadLeadAsync(context, 8401);
        SecPortalPrint(relinked);
        relinked.ResolveCommercialIdentity(Aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        await learner.LearnFromReviewAsync(Tenant, relinked, Aramco, Sec, 101);
        await context.SaveChangesAsync();

        var confirmed = await LoadLeadAsync(context, 8402);
        SecPortalPrint(confirmed);
        var result = await learner.LearnFromReviewAsync(Tenant, confirmed, Sec, null, 102);
        await context.SaveChangesAsync();

        Assert.DoesNotContain(CustomerAliasLearner.SkipDomainNotTiedToCustomer, result.SkipReasons);
        var active = await ActiveAsync(context);
        foreach (var type in new[] { CustomerIdentifierType.Email, CustomerIdentifierType.Domain })
        {
            var row = Assert.Single(active, i => i.CustomerId == Sec && i.IdentifierType == type);
            Assert.True(row.IsVerified);
            Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, row.Source);
            Assert.Equal(51, row.ObservationCount);
        }

        var fromTheMailbox = await ResolveWithoutSavingAsync(db, 8610);
        Assert.Equal(Sec, fromTheMailbox.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, fromTheMailbox.ReasonCode);
        var fromTheDomain = await ResolveWithoutSavingAsync(db, 8611);
        Assert.Equal(Sec, fromTheDomain.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, fromTheDomain.ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    // The control: a pick on a page that names nobody is the EPC shape, and it still vetoes the domain.
    [InlineData(false)]
    public async Task P10_a_wrong_pick_against_its_own_page_stops_vetoing_the_domain_once_the_right_customer_is_confirmed_twice(
        bool pickNamedTheRightCustomer)
    {
        // THE DEFECT (T26 residual). One SEC print from 57322@se.com.sa, its company-name field "Saudi Electricity
        // Company", was linked to Saudi Aramco and converted, so it can never be relinked. That single decision vetoed
        // se.com.sa for SEC for ever: two correct SEC confirmations later the domain was still unverified, no address
        // was written, and SEC's next plain mail was UNRESOLVED.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            SecPortalPrint(Seed.Lead(seed, 8403, Tenant, buyersName: null));
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8403).FromEmail = "57322@se.com.sa";
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context);

        var wrongPick = await LoadLeadAsync(context, 8401);
        SecPortalPrint(wrongPick);
        if (!pickNamedTheRightCustomer)
        {
            wrongPick.CustomerCompanyNameExtracted = null;
            wrongPick.DeliveryLocation = null;
        }
        wrongPick.ResolveCommercialIdentity(Aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        await learner.LearnFromReviewAsync(Tenant, wrongPick, Aramco, null, 99);
        await context.SaveChangesAsync();

        var firstConfirmation = await LoadLeadAsync(context, 8402);
        SecPortalPrint(firstConfirmation);
        await learner.LearnFromReviewAsync(Tenant, firstConfirmation, Sec, null, 100);
        await context.SaveChangesAsync();
        // One confirmation against one wrong pick is not yet enough.
        Assert.DoesNotContain(await ActiveAsync(context),
            i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Domain && i.IsVerified);

        var secondConfirmation = await LoadLeadAsync(context, 8403);
        SecPortalPrint(secondConfirmation);
        secondConfirmation.ResolveCommercialIdentity(Sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        await learner.LearnFromReviewAsync(Tenant, secondConfirmation, Sec, null, 101);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.Equal(pickNamedTheRightCustomer, rows.Any(i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Domain
                                                               && i.NormalizedValue == "se.com.sa" && i.IsVerified));
        Assert.Equal(pickNamedTheRightCustomer, rows.Any(i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Email
                                                               && i.NormalizedValue == "57322@se.com.sa" && i.IsVerified));
    }

    /// <summary>Seeds Hyundai and four plain messages: three from K Lee's mailbox at hdec.com, one from a colleague there.</summary>
    private static async Task SeedHyundaiMailboxAsync(TestDb db)
    {
        await using var seed = db.ContextFor(null);
        Seed.Customer(seed, Hyundai, Tenant, "Hyundai Engineering & Construction");
        foreach (var id in new long[] { 8403, 8404, 8405, 8406 })
        {
            var colleague = id == 8406;
            var lead = Seed.Lead(seed, id, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = colleague ? "j.park@hdec.com" : "k.lee@hdec.com";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + id).FromEmail = colleague ? "J Park <j.park@hdec.com>" : "K Lee <k.lee@hdec.com>";
        }
        await seed.SaveChangesAsync();
    }

    private static async Task ConfirmAsync(ErpRfqAutomationContext context, CustomerAliasLearner learner, long leadId, long customerId, long audit)
    {
        var lead = await LoadLeadAsync(context, leadId);
        lead.ResolveCommercialIdentity(customerId, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        await learner.LearnFromReviewAsync(Tenant, lead, customerId, null, audit);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task P10_a_buyers_own_mailbox_confirmed_twice_for_one_customer_is_learned_as_an_address_never_a_domain()
    {
        // THE DEFECT (LG01). Only a buyer address printed on a page could tie a domain, so "K Lee <k.lee@hdec.com>"
        // confirmed for Hyundai twice with nothing printed wrote nothing, and the third message was a 0.65 suggestion.
        // Base wrote the address at 1.00 on the first confirmation, and a gmail buyer confirmed twice is learned. The
        // consumer-mailbox rule now holds for an organisation's mailbox too: the address, never the domain.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await SeedHyundaiMailboxAsync(db);
        var learner = new CustomerAliasLearner(context);

        await ConfirmAsync(context, learner, 8403, Hyundai, 99);
        Assert.DoesNotContain(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Email);

        await ConfirmAsync(context, learner, 8404, Hyundai, 100);
        var rows = await ActiveAsync(context);
        var address = Assert.Single(rows, i => i.IdentifierType == CustomerIdentifierType.Email);
        Assert.Equal("k.lee@hdec.com", address.NormalizedValue);
        Assert.Equal(Hyundai, address.CustomerId);
        Assert.True(address.IsVerified);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, address.Source);
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Domain && i.IsVerified);

        var next = await ResolveWithoutSavingAsync(db, 8405);
        Assert.Equal(Hyundai, next.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, next.ReasonCode);
        var colleague = await ResolveWithoutSavingAsync(db, 8406);
        Assert.NotEqual(Hyundai, colleague.CustomerId);
    }

    [Fact]
    public async Task P10_a_mailbox_people_linked_to_two_customers_is_learned_as_neither_ones_address()
    {
        // The control: an address people gave to two customers is an intermediary's, however often either was picked.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await SeedHyundaiMailboxAsync(db);
        var learner = new CustomerAliasLearner(context);

        await ConfirmAsync(context, learner, 8403, Hyundai, 99);
        await ConfirmAsync(context, learner, 8404, Aramco, 100);
        await ConfirmAsync(context, learner, 8405, Hyundai, 101);

        Assert.DoesNotContain(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Email);
    }

    private const string ArabicSec = "الشركة السعودية للكهرباء";

    /// <summary>A folder print whose only buyer statement is SEC's name in Arabic, linked by a person to <paramref name="customerId"/>.</summary>
    private static void ArabicNamePrint(Lead lead, long customerId, string? deliveryAddress = null)
    {
        StripAddresses(lead);
        lead.Rfqno = null;
        lead.CustomerCompanyNameExtracted = ArabicSec;
        lead.CustomerPortalNameExtracted = null;
        lead.SupplierAccountRefOnDocument = null;
        lead.DeliveryLocation = deliveryAddress;
        lead.ResolveCommercialIdentity(customerId, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
    }

    private static async Task SeedArabicPrintAsync(TestDb db, long leadId)
    {
        await using var seed = db.ContextFor(null);
        var next = Seed.Lead(seed, leadId, Tenant, buyersName: null);
        next.Rfqno = null;
        next.Clientemail = "extraction@pipeline.local";
        next.CustomerCompanyNameExtracted = ArabicSec;
        seed.EmailIngests.Local.Single(i => i.Id == 20_000 + leadId).FromEmail = "extraction@pipeline.local";
        await seed.SaveChangesAsync();
    }

    [Fact]
    public async Task P8_a_company_name_in_another_script_is_trusted_once_people_corroborate_it()
    {
        // THE DEFECT (LF08). SEC's own name in Arabic cannot be compared letter by letter with "Saudi Electricity
        // Company", so it read as unlike SEC and was filed unverified however often people confirmed it; the next Arabic
        // print resolved to nothing, where base linked it at 0.90.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await SeedArabicPrintAsync(db, 8612);
        var learner = new CustomerAliasLearner(context);

        var first = await LoadLeadAsync(context, 8401);
        ArabicNamePrint(first, Sec);
        var firstResult = await learner.LearnFromReviewAsync(Tenant, first, Sec, null, 99);
        await context.SaveChangesAsync();
        Assert.Contains(CustomerAliasLearner.SkipAliasInAnotherScriptNotYetCorroborated, firstResult.SkipReasons);
        Assert.False(Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias).IsVerified);

        var second = await LoadLeadAsync(context, 8402);
        ArabicNamePrint(second, Sec);
        await learner.LearnFromReviewAsync(Tenant, second, Sec, null, 100);
        await context.SaveChangesAsync();

        var alias = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias);
        Assert.Equal(Sec, alias.CustomerId);
        Assert.True(alias.IsVerified);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, alias.Source);
        Assert.Equal(0.90m, alias.Confidence);

        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, 8612);
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.LearnedAlias, outcome.ReasonCode);
    }

    [Theory]
    [InlineData(true)]
    // The control: the same Arabic name on a page naming SEC, picked twice for Aramco, is never Aramco's alias.
    [InlineData(false)]
    public async Task P8_a_company_name_in_another_script_is_trusted_at_once_only_where_the_page_names_that_customer(bool pickedTheNamedCustomer)
    {
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await SeedArabicPrintAsync(db, 8612);
        var learner = new CustomerAliasLearner(context);
        var chosen = pickedTheNamedCustomer ? Sec : Aramco;

        foreach (var (leadId, audit) in pickedTheNamedCustomer ? new[] { (8401L, 99L) } : new[] { (8401L, 99L), (8402L, 100L) })
        {
            var lead = await LoadLeadAsync(context, leadId);
            ArabicNamePrint(lead, chosen, "Saudi Electricity Company-DAMMAM");
            await learner.LearnFromReviewAsync(Tenant, lead, chosen, null, audit);
            await context.SaveChangesAsync();
        }

        var alias = Assert.Single(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Alias && i.CustomerId == chosen);
        Assert.Equal(pickedTheNamedCustomer, alias.IsVerified);

        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, 8612);
        if (pickedTheNamedCustomer)
        {
            Assert.Equal(Sec, outcome.CustomerId);
            Assert.Equal(CustomerMatchReasonCodes.LearnedAlias, outcome.ReasonCode);
        }
        else
        {
            Assert.NotEqual(Aramco, outcome.CustomerId);
        }
    }

    // ── P22: a correction reaches what an earlier lead taught ────────────────

    [Fact]
    public async Task P22_relinking_a_lead_expires_the_wrong_customers_facts_even_when_another_lead_taught_them()
    {
        // THE STORY OF THE FINDING, END TO END. Lead A, an SEC print from 57322@se.com.sa, was
        // linked to Saudi Aramco and converted to an RFQ, so it can never be relinked; it taught
        // Aramco the address, the domain and SEC's name. Lead B from the same sender auto-linked
        // to Aramco at 1.00 and the rep relinked it to SEC. P5 expired nothing (no row carried B's
        // id), the SEC address was refused as ALIAS_CONFLICT, and lead C from the same sender
        // still went to Aramco. Deactivating Aramco or SQL were the only remedies.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var leadA = await LoadLeadAsync(context, 8401);
        leadA.ResolveCommercialIdentity(Aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        context.Set<CustomerIdentifier>().AddRange(
            TaughtByLeadA(CustomerIdentifierType.Email, "57322@se.com.sa", 1.00m),
            TaughtByLeadA(CustomerIdentifierType.Domain, "se.com.sa", 0.95m),
            TaughtByLeadA(CustomerIdentifierType.Alias, CustomerNameNormalizer.LooseKey("Saudi Electricity Company"), 0.90m));
        await context.SaveChangesAsync();
        await using (var seed = db.ContextFor(null))
        {
            Seed.Contact(seed, 9303, Tenant, Sec, "procurement@se.com.sa");
            await seed.SaveChangesAsync();
        }

        var leadB = await LoadLeadAsync(context, 8402);
        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, leadB, Sec, Aramco, 101);
        await context.SaveChangesAsync();

        Assert.Equal(3, result.Expired);
        Assert.DoesNotContain(CustomerAliasLearner.SkipAliasConflict, result.SkipReasons);
        Assert.Empty(await context.Set<CustomerIdentifier>()
            .Where(i => i.CustomerId == Aramco && i.EffectiveTo == null).ToListAsync());
        var learned = await LearnedAsync(context);
        Assert.Contains(learned, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Email
                                      && i.NormalizedValue == "57322@se.com.sa" && i.IsVerified);
        Assert.Contains(learned, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Domain
                                      && i.NormalizedValue == "se.com.sa" && i.IsVerified);

        // Lead C, from the same sender, decided by the resolver over exactly the rows now in the
        // store. Not pushed through LeadCustomerResolutionService only because that service
        // persists the winner as a candidate row, and on the SQLite harness a 1.0000 confidence is
        // stored as TEXT, where '1.0000' <= 1 is false, so CK_lead_customer_match_candidates_Confidence
        // refuses every exact-address link. PostgreSQL stores it as numeric and is unaffected.
        var stored = await context.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == Tenant && i.EffectiveTo == null)
            .Select(i => new CustomerIdentifierSnapshot(
                i.Id, i.CustomerId, i.IdentifierType, i.NormalizedValue, i.IsVerified, i.Confidence, i.Source))
            .ToListAsync();
        var outcome = CustomerIdentityResolver.Resolve(
            new LeadClientEvidence { BusinessUnitId = Tenant, LeadId = 8605, SenderEmail = "57322@se.com.sa" },
            new ClientResolutionCorpus
            {
                Customers = [new CustomerNameSnapshot(Sec, "Saudi Electricity Company"), new CustomerNameSnapshot(Aramco, "Saudi Aramco")],
                Identifiers = stored
            },
            new CustomerResolutionPolicy());

        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
    }

    [Fact]
    public async Task P22_a_relink_never_expires_what_a_person_typed_on_the_other_customers_profile()
    {
        // The reviewer contradicted the MACHINE, not the profile. An address somebody typed on
        // Aramco's record stays Aramco's, and P3 still refuses to take it.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        foreach (var (type, value) in new[] { (CustomerIdentifierType.Email, "57322@se.com.sa"), (CustomerIdentifierType.Domain, "se.com.sa") })
            context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = Tenant, CustomerId = Aramco, IdentifierType = type,
                NormalizedValue = value, DisplayValue = value, IsVerified = true, Confidence = 1m,
                Source = "CustomerProfile", EffectiveFrom = DateTime.UtcNow.AddDays(-5)
            });
        await context.SaveChangesAsync();

        var leadB = await LoadLeadAsync(context, 8402);
        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, leadB, Sec, Aramco, 101);
        await context.SaveChangesAsync();

        Assert.Equal(0, result.Expired);
        Assert.Contains(CustomerAliasLearner.SkipAliasConflict, result.SkipReasons);
        Assert.Equal(2, await context.Set<CustomerIdentifier>()
            .CountAsync(i => i.CustomerId == Aramco && i.Source == "CustomerProfile" && i.EffectiveTo == null));
    }

    [Fact]
    public async Task P5_one_relink_demotes_facts_other_decisions_confirmed_many_times_instead_of_expiring_them()
    {
        // SEC's address, domain, name and portal pair were confirmed fifty times. One lead from that mailbox is
        // relinked to Aramco by a mis-click, and every one of the four used to be expired. They are demoted
        // instead: out of every auto-link tier, still on the recognition table, promoted back by the next
        // confirmation. Nothing verified is written for Aramco, and the next SEC print still links from its address.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        context.Set<CustomerIdentifier>().AddRange(
            ConfirmedFiftyTimes(CustomerIdentifierType.Email, "57322@se.com.sa", 1.00m),
            ConfirmedFiftyTimes(CustomerIdentifierType.Domain, "se.com.sa", 0.95m),
            ConfirmedFiftyTimes(CustomerIdentifierType.Alias, CustomerNameNormalizer.LooseKey("Saudi Electricity Company"), 0.90m),
            ConfirmedFiftyTimes(CustomerIdentifierType.PortalAccount, "MATERIALS E BIDDING SYSTEM|2004414", 0.92m));
        await context.SaveChangesAsync();

        var relinked = await LoadLeadAsync(context, 8401);
        SecPortalPrint(relinked);
        relinked.ResolveCommercialIdentity(Aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, relinked, Aramco, Sec, 101);
        await context.SaveChangesAsync();

        Assert.Equal(0, result.Expired);
        Assert.Contains(CustomerAliasLearner.SkipContradictedFactDemoted, result.SkipReasons);
        var sec = await context.Set<CustomerIdentifier>()
            .Where(i => i.CustomerId == Sec && i.EffectiveTo == null).ToListAsync();
        Assert.Equal(4, sec.Count);
        Assert.All(sec, i =>
        {
            Assert.False(i.IsVerified);
            Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, i.Source);
            Assert.Equal(50, i.ObservationCount);
        });
        Assert.DoesNotContain(await ActiveAsync(context), i => i.CustomerId == Aramco && i.IsVerified);

        await using (var seed = db.ContextFor(null))
        {
            var next = Seed.Lead(seed, 8608, Tenant, buyersName: null);
            next.Rfqno = null;
            next.Clientemail = "extraction@pipeline.local";
            next.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
            next.SupplierAccountRefOnDocument = "2004414";
            next.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
            next.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8608).FromEmail = "extraction@pipeline.local";
            await seed.SaveChangesAsync();
        }
        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, 8608);
        Assert.Equal(Sec, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, outcome.Status);
    }

    [Fact]
    public async Task P10_decisions_a_later_relink_took_back_no_longer_veto_the_right_customers_domain()
    {
        // Leads A1 and A2, SEC's own prints from 57322@se.com.sa, were linked to Aramco by mistake and
        // converted, so they can never be relinked. Lead B was relinked from Aramco to SEC, and lead C was
        // confirmed SEC. A1's and A2's decisions still vetoed se.com.sa for SEC for ever: C left the domain
        // unverified and wrote no address. B's relink expired Aramco's own learned row for the domain and
        // Aramco holds nothing on it now, so those decisions no longer count.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            foreach (var id in new long[] { 8403, 8404 })
            {
                SecPortalPrint(Seed.Lead(seed, id, Tenant, buyersName: null));
                seed.EmailIngests.Local.Single(i => i.Id == 20_000 + id).FromEmail = "57322@se.com.sa";
            }
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context);

        foreach (var (leadId, audit) in new[] { (8401L, 99L), (8402L, 100L) })
        {
            var misLinked = await LoadLeadAsync(context, leadId);
            SecPortalPrint(misLinked);
            misLinked.ResolveCommercialIdentity(Aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            await learner.LearnFromReviewAsync(Tenant, misLinked, Aramco, null, audit);
            await context.SaveChangesAsync();
        }

        var relinked = await LoadLeadAsync(context, 8403);
        relinked.AutoResolveCommercialIdentity(Aramco, null, CustomerMatchReasonCodes.SenderDomain, 0.95m, "machine", DateTime.UtcNow);
        relinked.ResolveCommercialIdentity(Sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        await learner.LearnFromReviewAsync(Tenant, relinked, Sec, Aramco, 101);
        await context.SaveChangesAsync();

        var confirmed = await LoadLeadAsync(context, 8404);
        confirmed.ResolveCommercialIdentity(Sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
        await learner.LearnFromReviewAsync(Tenant, confirmed, Sec, null, 102);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        var domain = Assert.Single(rows, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Domain);
        Assert.Equal("se.com.sa", domain.NormalizedValue);
        Assert.True(domain.IsVerified);
        Assert.Equal(0.95m, domain.Confidence);
        Assert.Equal(CustomerIdentifierSources.LeadReviewLearned, domain.Source);
        Assert.Contains(rows, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Email
                                   && i.NormalizedValue == "57322@se.com.sa" && i.IsVerified);
        Assert.DoesNotContain(rows, i => i.CustomerId == Aramco
                                         && i.IdentifierType is CustomerIdentifierType.Domain or CustomerIdentifierType.Email
                                             or CustomerIdentifierType.PortalAccount);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    // ── seams between the lanes (GREEN, 2026-09-13) ─────────────────────────

    [Fact]
    public async Task A_misread_vendor_block_does_not_stop_a_persons_confirmation_teaching_the_buyers_own_mailbox()
    {
        // T09a/T09c, the learner's half. The resolver, the corpus loader and routing stopped letting the
        // document's vendor block make an address ours; the learner still passed it raw. A Marafiq print from
        // buyer@marafiq.com.sa whose vendor field was misread as "MARAFIQ" made marafiq.com.sa ours to the
        // learner alone, and a person's confirmation taught nothing (selfIdentity).
        const long marafiq = 8311;
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, marafiq, Tenant, "Marafiq");
            await seed.SaveChangesAsync();
        }
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = "Tenders <buyer@marafiq.com.sa>";
        lead.Clientemail = "buyer@marafiq.com.sa";
        lead.CustomerBuyerEmailExtracted = "buyer@marafiq.com.sa";
        lead.CustomerCompanyNameExtracted = "Marafiq";
        lead.CustomerPortalNameExtracted = null;
        lead.SupplierAccountRefOnDocument = null;
        lead.SupplierNameOnDocument = "MARAFIQ";
        lead.ResolveCommercialIdentity(marafiq, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, marafiq, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.Contains(rows, i => i.CustomerId == marafiq && i.IdentifierType == CustomerIdentifierType.Email
                                   && i.NormalizedValue == "buyer@marafiq.com.sa" && i.IsVerified);
        Assert.Contains(rows, i => i.CustomerId == marafiq && i.IdentifierType == CustomerIdentifierType.Domain
                                   && i.NormalizedValue == "marafiq.com.sa" && i.IsVerified);
    }

    [Theory]
    // A person linked mail from the domain to ANOTHER customer long ago: that veto must not be crowded out.
    [InlineData(true)]
    // A person's own older decision for this customer is what ties the domain: it must not be crowded out either.
    [InlineData(false)]
    public async Task P10_a_persons_decision_on_a_domain_is_read_past_a_flood_of_newer_machine_links(bool anotherCustomerDecided)
    {
        // T08, in the learner's own domain-tie read. It capped earlier leads at 200 and only then kept the human
        // decisions. With 205 newer machine links from newco.com.sa, a person's older decision fell out of the
        // window: with a rival's decision gone the domain was written to SEC at 0.95 (and the next Aramco job from
        // the domain linked to SEC without asking anyone); with SEC's own decision gone, a tie was lost.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            var oldest = Seed.Lead(seed, 9000, Tenant, buyersName: null);
            oldest.Rfqno = null;
            oldest.Clientemail = "x@newco.com.sa";
            oldest.CustomerBuyerEmailExtracted = "x@newco.com.sa";
            oldest.CustomerCompanyNameExtracted = null;
            oldest.ResolveCommercialIdentity(anotherCustomerDecided ? Aramco : Sec, null,
                LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            for (var i = 1; i <= 205; i++)
            {
                var machine = Seed.Lead(seed, 9000 + i, Tenant, buyersName: null);
                machine.Rfqno = null;
                machine.Clientemail = $"m{i}@newco.com.sa";
            }
            if (anotherCustomerDecided)
            {
                // The newest decision is SEC's, printed on the domain: alone it would tie newco.com.sa to SEC.
                var newest = Seed.Lead(seed, 9300, Tenant, buyersName: null);
                newest.Rfqno = null;
                newest.Clientemail = "z@newco.com.sa";
                newest.CustomerBuyerEmailExtracted = "z@newco.com.sa";
                newest.CustomerCompanyNameExtracted = null;
                newest.ResolveCommercialIdentity(Sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            }
            await seed.SaveChangesAsync();
            foreach (var machine in seed.Leads.Local.Where(l => l.Id is > 9000 and <= 9205).ToList())
                machine.AutoResolveCommercialIdentity(Sec, null, CustomerMatchReasonCodes.NameInDocument, 0.88m, "machine", DateTime.UtcNow);
            await seed.SaveChangesAsync();
        }

        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = "y@newco.com.sa";
        lead.Clientemail = "y@newco.com.sa";
        lead.CustomerBuyerEmailExtracted = "y@newco.com.sa";
        lead.CustomerPortalNameExtracted = null;
        lead.SupplierAccountRefOnDocument = null;
        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var domain = Assert.Single(await ActiveAsync(context),
            i => i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == "newco.com.sa");
        Assert.Equal(Sec, domain.CustomerId);
        Assert.Equal(!anotherCustomerDecided, domain.IsVerified);
    }

    [Fact]
    public async Task A_system_mailbox_on_the_customers_own_domain_teaches_the_domain_but_is_never_minted_as_an_address()
    {
        // T17, the learner's half. The resolver and routing refuse a learned row on a system mailbox, so minting
        // noreply@se.com.sa as SEC's Email at 1.00 wrote a row nothing may match that still claimed the domain in
        // every "who else writes from here" read. The domain is SEC's all the same (a person linked 57322@se.com.sa).
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        var lead = await LoadLeadAsync(context, 8401);
        lead.EmailIngests!.FromEmail = "SEC e-Bidding <noreply@se.com.sa>";
        lead.Clientemail = "noreply@se.com.sa";
        lead.CustomerBuyerEmailExtracted = null;

        var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.DoesNotContain(rows, i => i.IdentifierType == CustomerIdentifierType.Email && i.NormalizedValue == "noreply@se.com.sa");
        Assert.Contains(rows, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Domain
                                   && i.NormalizedValue == "se.com.sa" && i.IsVerified);
        Assert.Contains(CustomerAliasLearner.SkipPersonalOrRelayAddress, result.SkipReasons);
    }

    [Fact]
    public async Task A_learned_system_mailbox_row_on_another_customer_is_no_claim_on_the_domain()
    {
        // The same row read the other way: a legacy learned noreply@se.com.sa on Aramco, a row the resolver and
        // routing will never match, made se.com.sa "claimed by another customer", so SEC's own confirmation
        // from 57322@se.com.sa wrote neither the address nor the domain.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        context.Set<CustomerIdentifier>().Add(new CustomerIdentifier
        {
            BusinessUnitId = Tenant, CustomerId = Aramco, IdentifierType = CustomerIdentifierType.Email,
            NormalizedValue = "noreply@se.com.sa", DisplayValue = "noreply@se.com.sa", IsVerified = true, Confidence = 1m,
            Source = CustomerIdentifierSources.LeadReviewLearned, EffectiveFrom = DateTime.UtcNow.AddDays(-30)
        });
        await context.SaveChangesAsync();
        var lead = await LoadLeadAsync(context, 8401);

        await new CustomerAliasLearner(context).LearnFromReviewAsync(Tenant, lead, Sec, null, 99);
        await context.SaveChangesAsync();

        var rows = await ActiveAsync(context);
        Assert.Contains(rows, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Domain
                                   && i.NormalizedValue == "se.com.sa" && i.IsVerified);
        Assert.Contains(rows, i => i.CustomerId == Sec && i.IdentifierType == CustomerIdentifierType.Email
                                   && i.NormalizedValue == "57322@se.com.sa" && i.IsVerified);
    }

    [Fact]
    public async Task The_number_of_confirmations_a_free_mail_address_needs_is_the_policys()
    {
        // The policy carried FreeMailAddressConfirmationsRequired and the learner read its own constant, so the
        // setting the host registers changed nothing. Raised to three, two confirmations write nothing.
        using var db = new TestDb();
        await using var context = await SeedAsync(db);
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, Rashid, Tenant, "Al-Rashid Trading");
            await seed.SaveChangesAsync();
        }
        var learner = new CustomerAliasLearner(context, policy: new CustomerResolutionPolicy { FreeMailAddressConfirmationsRequired = 3 });

        foreach (var (leadId, audit) in new[] { (8401L, 99L), (8402L, 100L) })
        {
            var lead = await LoadLeadAsync(context, leadId);
            FromGmailBuyer(lead, Rashid);
            await learner.LearnFromReviewAsync(Tenant, lead, Rashid, null, audit);
            await context.SaveChangesAsync();
        }

        Assert.DoesNotContain(await ActiveAsync(context), i => i.IdentifierType == CustomerIdentifierType.Email);
    }

    [Fact]
    public void The_learner_and_the_resolution_service_filter_earlier_decisions_on_one_list()
        // Two copies of the human statuses drift apart the day one changes. The service's list is the one.
        => Assert.Same(LeadCustomerResolutionService.HumanDecidedStatuses, CustomerAliasLearner.HumanDecidedStatuses);

    private const long Satorp = 8304;
    private const long SaudiCable = 8305;
    private const long SaudiCeramics = 8306;
    private const long SaudiEngineering = 8307;
    private const long Hyundai = 8308;
    private const long Rashid = 8309;
    private const long ArabianPipes = 8310;
    private const string GmailBuyer = "buyer.person@gmail.com";

    /// <summary>A message from a sole trader's gmail mailbox and nothing else, linked by a person to <paramref name="customerId"/>.</summary>
    private static void FromGmailBuyer(Lead lead, long customerId)
    {
        lead.EmailIngests!.FromEmail = $"Buyer Person <{GmailBuyer}>";
        lead.Clientemail = GmailBuyer;
        lead.CustomerBuyerEmailExtracted = null;
        lead.CustomerCompanyNameExtracted = null;
        lead.CustomerPortalNameExtracted = null;
        lead.SupplierAccountRefOnDocument = null;
        lead.ResolveCommercialIdentity(customerId, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
    }

    /// <summary>SEC's e-bidding print carrying only the portal pair and our vendor block, linked by a person to <paramref name="customerId"/>.</summary>
    private static void PairOnlyPrint(Lead lead, long customerId)
    {
        StripAddresses(lead);
        lead.CustomerCompanyNameExtracted = null;
        lead.DeliveryLocation = null;
        lead.ResolveCommercialIdentity(customerId, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
    }

    /// <summary>SEC's e-bidding print as lead 680 carries it, from SEC's own mailbox, which is also the printed buyer address.</summary>
    private static void SecPortalPrint(Lead lead)
    {
        lead.Rfqno = null;
        lead.Clientemail = "57322@se.com.sa";
        lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
        lead.CustomerCompanyNameExtracted = "Saudi Electricity Company";
        lead.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
        lead.SupplierAccountRefOnDocument = "2004414";
        lead.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
        lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
        if (lead.EmailIngests is not null) lead.EmailIngests.FromEmail = "57322@se.com.sa";
    }

    private static CustomerIdentifier ConfirmedFiftyTimes(CustomerIdentifierType type, string value, decimal confidence) => new()
    {
        BusinessUnitId = Tenant,
        CustomerId = Sec,
        IdentifierType = type,
        NormalizedValue = value,
        DisplayValue = value,
        IsVerified = true,
        Confidence = confidence,
        Source = CustomerIdentifierSources.LeadReviewLearned,
        EffectiveFrom = DateTime.UtcNow.AddDays(-90),
        ObservationCount = 50,
        LastObservedOn = DateTime.UtcNow.AddDays(-1)
    };

    /// <summary>
    /// The resolver over the real corpus loader, without persisting the candidates: on SQLite a 1.0000
    /// confidence is stored as TEXT and CK_lead_customer_match_candidates_Confidence refuses it (see P22).
    /// </summary>
    private static async Task<ClientResolutionOutcome> ResolveWithoutSavingAsync(TestDb db, long leadId)
    {
        await using var context = db.ContextFor(Tenant);
        var lead = await context.Leads.Include(l => l.LeadItems).Include(l => l.EmailIngests).SingleAsync(l => l.Id == leadId);
        return await new LeadCustomerResolutionService(context).ResolveCoreAsync(Tenant, lead, CancellationToken.None);
    }

    /// <summary>Removes every mailbox from the lead, so only what a test puts back is on trial.</summary>
    private static void StripAddresses(Lead lead)
    {
        lead.EmailIngests!.FromEmail = "extraction@pipeline.local";
        lead.Clientemail = null;
        lead.CustomerBuyerEmailExtracted = null;
    }

    /// <summary>
    /// The seed links BOTH leads to SEC from 57322@se.com.sa, so each is an earlier human decision
    /// that ties se.com.sa to SEC for the other. Tests that need an untied domain forget lead 8402's.
    /// </summary>
    private static async Task ForgetSiblingAddressesAsync(ErpRfqAutomationContext context)
    {
        var sibling = await LoadLeadAsync(context, 8402);
        sibling.Clientemail = null;
        sibling.CustomerBuyerEmailExtracted = null;
        await context.SaveChangesAsync();
    }

    private static CustomerIdentifier TaughtByLeadA(CustomerIdentifierType type, string value, decimal confidence) => new()
    {
        BusinessUnitId = Tenant,
        CustomerId = Aramco,
        IdentifierType = type,
        NormalizedValue = value,
        DisplayValue = value,
        IsVerified = true,
        Confidence = confidence,
        Source = CustomerIdentifierSources.LeadReviewLearned,
        EffectiveFrom = DateTime.UtcNow.AddDays(-3),
        LearnedFromLeadId = 8401,
        ObservationCount = 1,
        LastObservedOn = DateTime.UtcNow.AddDays(-3)
    };

    private static async Task<List<CustomerIdentifier>> ActiveAsync(ErpRfqAutomationContext context) =>
        await context.Set<CustomerIdentifier>().Where(i => i.EffectiveTo == null).ToListAsync();

    private static async Task<ErpRfqAutomationContext> SeedAsync(TestDb db)
    {
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, Sec, Tenant, "Saudi Electricity Company");
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            foreach (var leadId in new long[] { 8401, 8402 })
            {
                var lead = Seed.Lead(seed, leadId, Tenant, buyersName: "3C2-AMER AL-DOSSARY");
                lead.Rfqno = $"C00104655{leadId % 10}";
                lead.Clientemail = "57322@se.com.sa";
                lead.CustomerCompanyNameExtracted = "Saudi Electricity Company";
                lead.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
                lead.SupplierAccountRefOnDocument = "2004414";
                lead.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
                lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
                // A human decided; only then does the loop learn (P6).
                lead.ResolveCommercialIdentity(Sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            }
            await seed.SaveChangesAsync();
        }
        return db.ContextFor(Tenant);
    }

    private static async Task<Lead> LoadLeadAsync(ErpRfqAutomationContext context, long leadId) =>
        await context.Leads.Include(l => l.EmailIngests).SingleAsync(l => l.Id == leadId);

    private static async Task<List<CustomerIdentifier>> LearnedAsync(ErpRfqAutomationContext context) =>
        await context.Set<CustomerIdentifier>()
            .Where(i => i.Source == CustomerIdentifierSources.LeadReviewLearned && i.EffectiveTo == null)
            .ToListAsync();
}

/// <summary>
/// P5's write-through on the production dialect. SQLite runs without a transaction, so it cannot
/// show that the expiry lands inside the caller's transaction and savepoint, takes the advisory
/// lock, and clears UX_customer_identifiers_authoritative before the right customer's row is
/// inserted. This mirrors LeadRepository.LearnClientIdentityAsync step for step.
///
/// What it deliberately does NOT do is move a lead from one customer to another, because
/// PostgreSQL refuses that: nexora_validate_lead_commercial_identity (Release01 migration, never
/// replaced) raises 55000 "Lead customer identity is immutable once resolved" on any UPDATE that
/// changes a non-null CustomerID. So on production a rep cannot relink a lead the machine
/// auto-linked to the wrong client, the review's own SaveChanges fails before the learner runs,
/// and this path is reached only once that rule is relaxed. Lead B is therefore seeded already
/// linked to SEC; the learner is told Aramco was the previous client, as the relink would tell it.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class CustomerAliasLearnerPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_correction_moves_the_senders_address_to_the_right_customer_inside_the_review_transaction()
    {
        var suffix = Random.Shared.Next(1, 50_000);
        var tenant = 9_410_000L + suffix;
        var sec = 9_420_000L + suffix;
        var aramco = 9_425_000L + suffix;
        var leadA = 9_430_000L + suffix;
        var leadB = 9_435_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, sec, tenant, "Saudi Electricity Company");
            Seed.Customer(seed, aramco, tenant, "Saudi Aramco");
            Seed.Contact(seed, 9_450_000L + suffix, tenant, sec, "procurement@se.com.sa");
            foreach (var id in new[] { leadA, leadB })
            {
                var lead = Seed.Lead(seed, id, tenant, buyersName: "3C2-AMER AL-DOSSARY");
                lead.Clientemail = "57322@se.com.sa";
                lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            }
            await seed.SaveChangesAsync();
        }

        await using (var seed = database.ContextFor(null))
        {
            // Lead A: linked to Aramco by a person, converted, never relinkable. It taught Aramco
            // the SEC buyer's address and domain. Lead B carries the correction to SEC (see summary).
            (await seed.Leads.SingleAsync(l => l.Id == leadA))
                .ResolveCommercialIdentity(aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            (await seed.Leads.SingleAsync(l => l.Id == leadB))
                .ResolveCommercialIdentity(sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            foreach (var (type, value, confidence) in new[]
                     {
                         (CustomerIdentifierType.Email, "57322@se.com.sa", 1.00m),
                         (CustomerIdentifierType.Domain, "se.com.sa", 0.95m)
                     })
                seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                {
                    BusinessUnitId = tenant, CustomerId = aramco, IdentifierType = type,
                    NormalizedValue = value, DisplayValue = value, IsVerified = true, Confidence = confidence,
                    Source = CustomerIdentifierSources.LeadReviewLearned, EffectiveFrom = DateTime.UtcNow.AddDays(-3),
                    LearnedFromLeadId = leadA, ObservationCount = 1, LastObservedOn = DateTime.UtcNow.AddDays(-3)
                });
            await seed.SaveChangesAsync();
        }

        CustomerAliasLearningResult result;
        await using (var context = database.ContextFor(tenant))
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            var leadToRelink = await context.Leads.Include(l => l.EmailIngests).SingleAsync(l => l.Id == leadB);

            await transaction.CreateSavepointAsync("client_identity_learning");
            result = await new CustomerAliasLearner(context).LearnFromReviewAsync(tenant, leadToRelink, sec, aramco, null);
            await context.SaveChangesAsync();
            await transaction.ReleaseSavepointAsync("client_identity_learning");
            await transaction.CommitAsync();
        }

        Assert.Equal(2, result.Expired);
        Assert.DoesNotContain(CustomerAliasLearner.SkipAliasConflict, result.SkipReasons);

        await using var verify = database.ContextFor(tenant);
        var active = await verify.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == tenant && i.EffectiveTo == null)
            .ToListAsync();
        Assert.DoesNotContain(active, i => i.CustomerId == aramco);
        Assert.Contains(active, i => i.CustomerId == sec && i.IdentifierType == CustomerIdentifierType.Email
                                     && i.NormalizedValue == "57322@se.com.sa" && i.IsVerified
                                     && i.Source == CustomerIdentifierSources.LeadReviewLearned);
        Assert.Contains(active, i => i.CustomerId == sec && i.IdentifierType == CustomerIdentifierType.Domain
                                     && i.NormalizedValue == "se.com.sa" && i.IsVerified);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_free_mail_the_earlier_document_and_the_demotion_reads_run_on_the_production_dialect()
    {
        // Three reads were proven on SQLite only, and each translates differently on Npgsql: the consumer-address
        // search over the sender columns and the ingest join, an earlier document's lines through a capped
        // correlated projection, and the relink's two-column demotion written through with ExecuteUpdate.
        // Every lead is seeded with its final customer: PostgreSQL refuses to move a resolved lead (see summary).
        var suffix = Random.Shared.Next(1, 50_000);
        var tenant = 9_510_000L + suffix;
        var sec = 9_520_000L + suffix;
        var aramco = 9_525_000L + suffix;
        var rashid = 9_527_000L + suffix;
        var gmailFirst = 9_600_000L + suffix;
        var gmailSecond = 9_650_000L + suffix;
        var pairFirst = 9_700_000L + suffix;
        var pairSecond = 9_750_000L + suffix;
        var relinked = 9_800_000L + suffix;
        const string gmail = "buyer.person@gmail.com";

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, sec, tenant, "Saudi Electricity Company");
            Seed.Customer(seed, aramco, tenant, "Saudi Aramco");
            Seed.Customer(seed, rashid, tenant, "Al-Rashid Trading");
            foreach (var id in new[] { gmailFirst, gmailSecond })
                Seed.Lead(seed, id, tenant, buyersName: null).Clientemail = gmail;
            foreach (var id in new[] { pairFirst, pairSecond })
            {
                var lead = Seed.Lead(seed, id, tenant, buyersName: null);
                lead.CustomerPortalNameExtracted = "MATERIALS E-BIDDING SYSTEM";
                lead.SupplierAccountRefOnDocument = "2004414";
                lead.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
                if (id == pairFirst)
                {
                    var line = Seed.LeadItem(9_900_000L + suffix, "10", 1, "BALL VALVE");
                    line.StorageLocation = "Main Store 3";
                    lead.LeadItems.Add(line);
                }
            }
            Seed.Lead(seed, relinked, tenant, buyersName: null).Clientemail = "57322@se.com.sa";
            await seed.SaveChangesAsync();
        }

        await using (var seed = database.ContextFor(null))
        {
            foreach (var id in new[] { gmailFirst, gmailSecond })
                (await seed.EmailIngests.SingleAsync(i => i.Id == 20_000 + id)).FromEmail = $"Buyer Person <{gmail}>";
            // The envelope is read before Clientemail, so SEC's mailbox must be the envelope for the relink to carry se.com.sa.
            (await seed.EmailIngests.SingleAsync(i => i.Id == 20_000 + relinked)).FromEmail = "57322@se.com.sa";
            foreach (var (id, customer) in new[] { (gmailFirst, rashid), (gmailSecond, rashid), (pairFirst, sec), (pairSecond, sec), (relinked, aramco) })
                (await seed.Leads.SingleAsync(l => l.Id == id))
                    .ResolveCommercialIdentity(customer, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = tenant, CustomerId = sec, IdentifierType = CustomerIdentifierType.Domain,
                NormalizedValue = "se.com.sa", DisplayValue = "se.com.sa", IsVerified = true, Confidence = 0.95m,
                Source = CustomerIdentifierSources.LeadReviewLearned, EffectiveFrom = DateTime.UtcNow.AddDays(-30),
                ObservationCount = 5, LastObservedOn = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        async Task<CustomerAliasLearningResult> LearnAsync(long leadId, long customerId, long? previousCustomerId)
        {
            await using var context = database.ContextFor(tenant);
            await using var transaction = await context.Database.BeginTransactionAsync();
            var lead = await context.Leads.Include(l => l.EmailIngests).Include(l => l.LeadItems).SingleAsync(l => l.Id == leadId);
            var result = await new CustomerAliasLearner(context).LearnFromReviewAsync(tenant, lead, customerId, previousCustomerId, null);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
            return result;
        }

        await LearnAsync(gmailSecond, rashid, null);
        await LearnAsync(pairSecond, sec, null);
        var relinkResult = await LearnAsync(relinked, aramco, sec);

        await using var verify = database.ContextFor(tenant);
        var active = await verify.Set<CustomerIdentifier>().AsNoTracking()
            .Where(i => i.BusinessUnitId == tenant && i.EffectiveTo == null)
            .ToListAsync();
        Assert.Contains(active, i => i.CustomerId == rashid && i.IdentifierType == CustomerIdentifierType.Email
                                     && i.NormalizedValue == gmail && i.IsVerified);
        Assert.DoesNotContain(active, i => i.IdentifierType == CustomerIdentifierType.Domain && i.NormalizedValue == "gmail.com");
        Assert.Contains(active, i => i.CustomerId == sec && i.IdentifierType == CustomerIdentifierType.PortalAccount
                                     && i.IsVerified && i.Confidence == 0.92m);
        var demoted = Assert.Single(active, i => i.CustomerId == sec && i.IdentifierType == CustomerIdentifierType.Domain
                                                 && i.NormalizedValue == "se.com.sa");
        Assert.False(demoted.IsVerified);
        Assert.Equal(CustomerAliasLearner.UnverifiedAliasSource, demoted.Source);
        Assert.Equal(5, demoted.ObservationCount);
        Assert.Contains(CustomerAliasLearner.SkipContradictedFactDemoted, relinkResult.SkipReasons);
    }
}
