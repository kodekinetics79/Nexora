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
        // from a single document.
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
    // Longer or shorter by whole words, in either direction.
    [InlineData("SAUDI ARABIAN OIL COMPANY - SAUDI ARAMCO", "Saudi Aramco", true)]
    [InlineData("MARAFIQ Yanbu Power & Desalination", "Marafiq", true)]
    // The three aliases the live tenant actually accumulated against "Saudi Aramco".
    [InlineData("FULTON COUNTY GOVERNMENT", "Saudi Aramco", false)]
    [InlineData("KORICS", "Saudi Aramco", false)]
    [InlineData("ARCENE SUPPLY SERVICES LLP", "Saudi Aramco", false)]
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

    // ── helpers ──────────────────────────────────────────────────────────────

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
