using System.Reflection;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// What the resolution service builds out of a saved lead row and hands the resolver. The resolver's
/// own tests state each rule on hand-built evidence; these prove the rule is reachable from the lead
/// the extraction worker actually saves, through the entry point it calls.
/// </summary>
public sealed class LeadCustomerResolutionServiceWiringTests
{
    private const long Tenant = 8700;
    private const long Aramco = 8711;
    private const long Hyundai = 8712;

    [Fact]
    public async Task A_contractors_mailbox_signed_with_a_customers_name_offers_that_customer_end_to_end()
    {
        // #14. Hyundai is a customer, nobody registered hdec.com and no contact writes from it. The
        // display name EmailService stores ("Hyundai E&C Procurement <procurement@hdec.com>") is the only
        // statement of who is writing, and it was read as a mention that named nobody: Aramco linked on
        // its site address at 0.88 and Hyundai was never offered.
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            Seed.Customer(seed, Hyundai, Tenant, "Hyundai Engineering & Construction");
            var lead = Seed.Lead(seed, 8721, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "procurement@hdec.com";
            lead.DeliveryLocation = "Saudi Aramco Ras Tanura Refinery";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + 8721).FromEmail =
                "Hyundai E&C Procurement <procurement@hdec.com>";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, 8721);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(Hyundai, outcome.Candidates[0].CustomerId);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == Aramco && candidate.Confidence < 0.85m);
    }

    [Theory]
    [InlineData("Rashid", false)]
    // The control: the trading house's own name on the relay is still the statement, and links.
    [InlineData("Al-Rashid Trading", true)]
    public async Task A_relay_carrying_one_word_that_is_a_family_name_offers_the_trading_house_and_links_nothing(
        string displayName, bool links)
    {
        // T04. Ariba relays the buyer's message and writes a name in front of its own address. A buyer
        // whose name reads "Rashid" put that one word in the header, and "Rashid" is the whole one-word key
        // of Al-Rashid Trading, so the lead linked to the trading house at 0.88 and asked nobody. The same
        // relay carrying the trading house's own name still links it.
        const long rashid = 8731, sec = 8732, leadId = 8741;
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, rashid, Tenant, "Al-Rashid Trading");
            Seed.Customer(seed, sec, Tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "noreply@ansmtp.ariba.com";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + leadId).FromEmail =
                $"{displayName} <noreply@ansmtp.ariba.com>";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, leadId);

        if (links)
        {
            Assert.Equal(rashid, outcome.CustomerId);
            Assert.Equal(0.88m, outcome.Confidence);
            return;
        }
        // A single word on the relay is a person's name, not the trading house, so it is not even offered (the repair
        // round's P2): "Rashid <noreply@ansmtp.ariba.com>" resolved to nothing at base, and offering Al-Rashid Trading
        // there put a customer nobody named on top of the rep's list. It still never links.
        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == rashid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(205)]
    public async Task A_rivals_older_human_decisions_on_the_domain_are_not_crowded_out_by_newer_machine_links(
        int newerMachineLinks)
    {
        // T08. The prior-lead read took the 200 most recent linked leads on the domain and only THEN kept human
        // decisions, so 205 newer machine links from bulk@sabic.com pushed every person's decision past the cap.
        //
        // OWNER DECISION 2026-09-13, policy A ("A whole email domain is never learned from confirmations"): this test used
        // to prove the read through a decision that SHIELDED SABIC's address from SADAF's contact. No decision shields a
        // consignee any more (see the next test), so T08 is proven where decisions still speak: two people each linked a
        // lead from a different sabic.com mailbox to SADAF, and those decisions must still be read, however many newer
        // machine links there are, to demote SABIC's address to a suggestion.
        const long sabic = 8751, sadaf = 8752, newLead = 8999;
        const long firstMachineLead = 9_100;
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, sabic, Tenant, "Saudi Basic Industries Corporation");
            Seed.Customer(seed, sadaf, Tenant, "Saudi Petrochemical Company (SADAF)");

            foreach (var (id, mailbox) in new[] { (8760L, "y.otaibi@sabic.com"), (8761L, "k.harbi@sabic.com") })
            {
                var human = Seed.Lead(seed, id, Tenant, buyersName: null);
                human.Clientemail = mailbox;
                human.ResolveCommercialIdentity(sadaf, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            }

            for (var i = 0; i < newerMachineLinks; i++)
            {
                var machine = Seed.Lead(seed, firstMachineLead + i, Tenant, buyersName: null);
                machine.Clientemail = "bulk@sabic.com";
                machine.AutoResolveCommercialIdentity(sabic, null, CustomerMatchReasonCodes.NameInDocument,
                    0.88m, "machine", DateTime.UtcNow);
            }

            var lead = Seed.Lead(seed, newLead, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "procurement@sabic.com";
            lead.DeliveryLocation = "Saudi Basic Industries Corporation - Jubail";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + newLead).FromEmail =
                "Procurement <procurement@sabic.com>";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, newLead);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(sabic, outcome.Candidates[0].CustomerId);
        Assert.True(outcome.Candidates[0].Confidence < 0.85m);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == sadaf);
    }

    [Fact]
    public async Task PolicyA_one_persons_decision_on_another_mailbox_does_not_shield_the_consignee_from_a_rivals_contact()
    {
        // OWNER DECISION 2026-09-13, policy A: "A whole email domain is never learned from confirmations." SADAF's contact
        // k.harbi@sabic.com is on SADAF's record. ONE person's link of a lead from a.buyer@sabic.com to SABIC used to be read
        // as SABIC's hold on the whole domain: it shielded SABIC's address, and every later sabic.com print naming SABIC,
        // from any mailbox, auto-linked SABIC at 0.88 over the contact (this was T08's own assertion). One decision is a fact
        // about one mailbox. Through the entry point the extraction worker calls, so the domain read is proven too.
        const long sabic = 8753, sadaf = 8754, humanLead = 8762, newLead = 8998;
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, sabic, Tenant, "Saudi Basic Industries Corporation");
            Seed.Customer(seed, sadaf, Tenant, "Saudi Petrochemical Company (SADAF)");
            Seed.Contact(seed, 8772, Tenant, sadaf, email: "k.harbi@sabic.com");

            var human = Seed.Lead(seed, humanLead, Tenant, buyersName: null);
            human.Clientemail = "a.buyer@sabic.com";
            human.ResolveCommercialIdentity(sabic, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

            var lead = Seed.Lead(seed, newLead, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "procurement@sabic.com";
            lead.DeliveryLocation = "Saudi Basic Industries Corporation - Jubail";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + newLead).FromEmail =
                "Procurement <procurement@sabic.com>";
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, newLead);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Contains(outcome.Candidates, candidate => candidate.CustomerId == sadaf);
        var site = Assert.Single(outcome.Candidates, candidate => candidate.CustomerId == sabic);
        Assert.True(site.Confidence < 0.85m, $"SABIC was offered at {site.Confidence}, which is link strength.");
    }

    [Fact]
    public async Task PolicyA_a_traders_address_confirmed_twice_links_the_next_message_although_its_pages_name_the_site_owner()
    {
        // OWNER DECISION 2026-09-13, policy A, in the words he approved: "A buyer's exact email address is learned once reps
        // confirm it for the same customer twice, and never for anyone else." A trader buying for an SEC job prints SEC's
        // delivery address. Reps linked two of trader.z@gmail.com's enquiries to Al-Rashid Trading, and the learner refused
        // both because the page named SEC, a veto the owner never approved: the third enquiry auto-linked SEC at 0.88
        // (the regression round's ADV5.20). The approved control: confirmed twice for one customer, never for another, the
        // next message links at 1.00.
        //
        // ResolveCoreAsync, not ResolveAsync: SQLite stores the decimal as text, and its CHECK constraint refuses a 1.00
        // candidate row that PostgreSQL accepts. Nothing here is about persistence.
        const long rashid = 8755, sec = 8756, first = 8763, second = 8764, next = 8765;
        const string trader = "trader.z@gmail.com";
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, rashid, Tenant, "Al-Rashid Trading");
            Seed.Customer(seed, sec, Tenant, "Saudi Electricity Company");
            foreach (var id in new[] { first, second, next })
            {
                var lead = Seed.Lead(seed, id, Tenant, buyersName: null);
                lead.Rfqno = null;
                lead.Clientemail = trader;
                lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
                seed.EmailIngests.Local.Single(i => i.Id == 20_000 + id).FromEmail = $"Trader <{trader}>";
                if (id != next)
                    lead.ResolveCommercialIdentity(rashid, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            }
            await seed.SaveChangesAsync();
        }

        await using (var reviewing = db.ContextFor(Tenant))
        {
            var confirmed = await reviewing.Leads.Include(l => l.EmailIngests).Include(l => l.LeadItems).SingleAsync(l => l.Id == second);
            await new CustomerAliasLearner(reviewing).LearnFromReviewAsync(Tenant, confirmed, rashid, null, 99);
            await reviewing.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var saved = await context.Leads.IgnoreQueryFilters()
            .Include(l => l.LeadItems).Include(l => l.EmailIngests)
            .SingleAsync(l => l.Id == next);
        var outcome = await new LeadCustomerResolutionService(context).ResolveCoreAsync(Tenant, saved, CancellationToken.None);

        Assert.Equal(rashid, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
        Assert.Equal(1.00m, outcome.Confidence);
    }

    [Fact]
    public async Task A_persons_correction_from_the_exact_sender_is_not_crowded_out_by_older_machine_links()
    {
        // T08, the exact-address read. It took the OLDEST 200 linked leads from the sender's address and
        // only then kept human decisions, so a person's correction made after 205 machine links from the
        // same mailbox was never read: the next message from that buyer came back with nothing to offer.
        const long sabic = 8801, sadaf = 8802, correctedLead = 9_500, newLead = 9_600;
        const long firstMachineLead = 9_200;
        const string buyer = "a.buyer@sabic.com";
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, sabic, Tenant, "Saudi Basic Industries Corporation");
            Seed.Customer(seed, sadaf, Tenant, "Saudi Petrochemical Company (SADAF)");
            for (var i = 0; i < 205; i++)
            {
                var machine = Seed.Lead(seed, firstMachineLead + i, Tenant, buyersName: null);
                machine.Clientemail = buyer;
                machine.AutoResolveCommercialIdentity(sadaf, null, CustomerMatchReasonCodes.NameInDocument,
                    0.88m, "machine", DateTime.UtcNow);
            }
            var corrected = Seed.Lead(seed, correctedLead, Tenant, buyersName: null);
            corrected.Clientemail = buyer;
            corrected.ResolveCommercialIdentity(sabic, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

            var lead = Seed.Lead(seed, newLead, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = buyer;
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + newLead).FromEmail = buyer;
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, newLead);

        var offered = Assert.Single(outcome.Candidates, c => c.ReasonCode == CustomerMatchReasonCodes.PriorSender);
        Assert.Equal(sabic, offered.CustomerId);
        Assert.DoesNotContain(outcome.Candidates, c => c.CustomerId == sadaf);
    }

    [Fact]
    public void The_human_decided_statuses_filtered_in_sql_are_exactly_the_ones_the_model_calls_human()
    {
        // T08's filter moved from IsHumanDecided in memory to a list in SQL. The two must never drift: a
        // status missing from the list would silently stop being evidence, one too many would let a
        // machine guess propagate.
        Assert.All(LeadCustomerResolutionService.HumanDecidedStatuses,
            status => Assert.True(LeadCustomerMatchStatuses.IsHumanDecided(status), status));
        var declared = typeof(LeadCustomerMatchStatuses)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!);
        foreach (var status in declared)
            Assert.Equal(LeadCustomerMatchStatuses.IsHumanDecided(status),
                LeadCustomerResolutionService.HumanDecidedStatuses.Contains(status));
    }

    [Fact]
    public async Task A_vendor_block_misread_as_the_buyers_name_does_not_make_the_buyers_registered_address_ours()
    {
        // T09c (C09). Marafiq has buyer@marafiq.com.sa and marafiq.com.sa on record. The extractor misread
        // the document's vendor block as "MARAFIQ". The service put that reading into the tenant's own
        // names, which decide whether an ADDRESS is ours, so marafiq.com.sa "spelled our name", the
        // registered sender was thrown away as our own mail and the lead resolved to NO_EVIDENCE. The
        // same message with the field empty linked at 1.00. The vendor block may only suppress a NAME.
        //
        // ResolveCoreAsync, not ResolveAsync: SQLite stores the decimal as text, and its CHECK constraint
        // refuses a 1.00 candidate row that PostgreSQL accepts. Nothing here is about persistence.
        const long marafiq = 8781, leadId = 8791;
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, marafiq, Tenant, "Marafiq");
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            var lead = Seed.Lead(seed, leadId, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "buyer@marafiq.com.sa";
            lead.SupplierNameOnDocument = "MARAFIQ";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + leadId).FromEmail = "Tenders <buyer@marafiq.com.sa>";
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().AddRange(
                Identifier(marafiq, CustomerIdentifierType.Email, "buyer@marafiq.com.sa", 1m),
                Identifier(marafiq, CustomerIdentifierType.Domain, "marafiq.com.sa", 0.95m));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var saved = await context.Leads.IgnoreQueryFilters()
            .Include(l => l.LeadItems).Include(l => l.EmailIngests)
            .SingleAsync(l => l.Id == leadId);
        var outcome = await new LeadCustomerResolutionService(context).ResolveCoreAsync(Tenant, saved, CancellationToken.None);

        Assert.Equal(marafiq, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
        Assert.Equal(1.00m, outcome.Confidence);
    }

    [Fact]
    public async Task The_corpus_loader_reads_the_buyers_domain_rows_whatever_the_vendor_block_says()
    {
        // T09c, the loader half, on its own: the resolver is not involved. The domain filter asked
        // IsOurs with the vendor block among our names, so a document whose vendor block reads
        // "Saudi Aramco" never loaded Aramco's own aramco.com Domain row, whatever the resolver did next.
        const long leadId = 8792;
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(Identifier(Aramco, CustomerIdentifierType.Domain, "aramco.com", 0.95m));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var corpus = await new LeadCustomerResolutionService(context).LoadCorpusAsync(Tenant, new LeadClientEvidence
        {
            BusinessUnitId = Tenant,
            LeadId = leadId,
            SenderEmail = "buyer@aramco.com",
            SupplierNameOnDocument = "Saudi Aramco",
            TenantSelfNameKeys = [$"Business Unit {Tenant}"]
        }, CancellationToken.None);

        Assert.Contains(corpus.Identifiers, identifier => identifier.CustomerId == Aramco
                                                          && identifier.IdentifierType == CustomerIdentifierType.Domain
                                                          && identifier.NormalizedValue == "aramco.com");
    }

    [Fact]
    public async Task A_sender_on_a_domain_our_own_vendor_name_spells_is_ours_to_the_service_as_it_is_to_routing()
    {
        // THE SEAM, end to end. The service asked the tenant's configured name alone whether a domain is ours,
        // for the corpus loader, the envelope's display name and the Guard, while routing asked the configured
        // name and the vendor block where it spells it. "ALI ZAID AL-QURAISHI & PARTNERS ESOSA" from
        // sales@esosa.com linked SEC at 0.95 here, and routing sent the same lead to the unassigned queue.
        const long sec = 8783, leadId = 8793;
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant).BusinessUnitName = "ALI ZAID AL-QURAISHI & PARTNERS";
            Seed.Customer(seed, sec, Tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = "sales@esosa.com";
            lead.SupplierNameOnDocument = "ALI ZAID AL-QURAISHI & PARTNERS ESOSA";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + leadId).FromEmail = "Sales Desk <sales@esosa.com>";
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(Identifier(sec, CustomerIdentifierType.Domain, "esosa.com", 0.95m));
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(Tenant);
        var saved = await context.Leads.IgnoreQueryFilters()
            .Include(l => l.LeadItems).Include(l => l.EmailIngests)
            .SingleAsync(l => l.Id == leadId);
        var outcome = await new LeadCustomerResolutionService(context).ResolveCoreAsync(Tenant, saved, CancellationToken.None);

        Assert.Null(outcome.CustomerId);
        Assert.NotEqual(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);
    }

    [Theory]
    [InlineData("a person linked the portal's earlier job")]
    [InlineData("the learner once minted the portal's mailbox")]
    public async Task A_portal_mailbox_that_carried_another_customers_job_does_not_take_the_next_named_print_away(string record)
    {
        // T17 against T19, through the entry point the extraction worker calls. Etimad delivers every
        // government buyer's tender from no-reply@etimad.gov.sa, a host nobody listed as a relay. A person
        // linked the Aramco job it carried (or the old learner minted the mailbox as Aramco's), and lead 680's
        // shape through the same portal fell to 0.70 with Aramco ranked first.
        const long sec = 8784, earlierId = 8794, leadId = 8795;
        const string portal = "no-reply@etimad.gov.sa";
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, sec, Tenant, "Saudi Electricity Company");
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            foreach (var (id, site) in new[] { (earlierId, "Saudi Aramco Ras Tanura Refinery"), (leadId, "Saudi Electricity Company-DAMMAM") })
            {
                var lead = Seed.Lead(seed, id, Tenant, buyersName: null);
                lead.Rfqno = null;
                lead.Clientemail = portal;
                lead.DeliveryLocation = site;
                seed.EmailIngests.Local.Single(i => i.Id == 20_000 + id).FromEmail = portal;
                if (id == earlierId && record.StartsWith("a person", StringComparison.Ordinal))
                    lead.ResolveCommercialIdentity(Aramco, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            }
            await seed.SaveChangesAsync();
            if (record.StartsWith("the learner", StringComparison.Ordinal))
            {
                seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                {
                    BusinessUnitId = Tenant, CustomerId = Aramco, IdentifierType = CustomerIdentifierType.Email,
                    NormalizedValue = portal, DisplayValue = portal, IsVerified = true, Confidence = 1m,
                    Source = CustomerIdentifierSources.LeadReviewLearned, EffectiveFrom = DateTime.UtcNow.AddDays(-30)
                });
                await seed.SaveChangesAsync();
            }
        }

        await using var context = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, leadId);

        Assert.Equal(sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(0.88m, outcome.Confidence);
    }

    [Fact]
    public async Task A_department_mailbox_signed_with_a_city_puts_no_city_named_trading_house_before_the_buyers_own_decision()
    {
        // THE DEFECT (the repair round's P2: A04.18 and LG04). The display name is a passage, and SEC's own
        // "Dammam Procurement <procurement@se.com.sa>" read DAMMAM for "Al Dammam Trading Co.": an unconfirmed message
        // offered the trading house at 0.70 where base offered nothing, and after a person had linked that very mailbox
        // to SEC the next plain message still ranked the trading house (0.70) above SEC's own decision (0.65).
        const long sec = 8785, dammam = 8786, unconfirmedId = 8796, earlierId = 8797, leadId = 8798;
        const string from = "Dammam Procurement <procurement@se.com.sa>";
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, sec, Tenant, "Saudi Electricity Company");
            Seed.Customer(seed, dammam, Tenant, "Al Dammam Trading Co.");
            foreach (var id in new[] { unconfirmedId, earlierId, leadId })
            {
                var lead = Seed.Lead(seed, id, Tenant, buyersName: null);
                lead.Rfqno = null;
                lead.Clientemail = "procurement@se.com.sa";
                seed.EmailIngests.Local.Single(i => i.Id == 20_000 + id).FromEmail = from;
            }
            await seed.SaveChangesAsync();
        }

        await using (var context = db.ContextFor(Tenant))
        {
            var unconfirmed = await new LeadCustomerResolutionService(context).ResolveAsync(Tenant, unconfirmedId);
            Assert.Null(unconfirmed.CustomerId);
            Assert.DoesNotContain(unconfirmed.Candidates, candidate => candidate.CustomerId == dammam);
        }

        await using (var seed = db.ContextFor(null))
        {
            (await seed.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == earlierId))
                .ResolveCommercialIdentity(sec, null, LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            await seed.SaveChangesAsync();
        }

        await using var resolving = db.ContextFor(Tenant);
        var outcome = await new LeadCustomerResolutionService(resolving).ResolveAsync(Tenant, leadId);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(sec, outcome.Candidates[0].CustomerId);
        Assert.DoesNotContain(outcome.Candidates, candidate => candidate.CustomerId == dammam);
    }

    [Fact]
    public async Task A_learned_domain_row_is_never_loaded_or_linked_whatever_mailbox_carries_the_print()
    {
        // MUST STAY FIXED (W03.04), through the loader and the entry point. A learned etimad.gov.sa Domain row on Saudi
        // Aramco linked every SEC tender Etimad's no-reply mailbox carried to Aramco at 0.95.
        //
        // OWNER DECISION 2026-09-13, policy A ("2 A"): a whole email domain is never learned from confirmations; it only
        // comes from a customer contact or an admin entry. This test used to assert that a person's own mailbox on the
        // host still loaded the learned row. Under policy A neither mailbox loads it, and a row a person entered is
        // loaded for both.
        const long sec = 8787, portalOperator = 8788, leadId = 8799;
        const string portal = "no-reply@etimad.gov.sa";
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            Seed.Customer(seed, sec, Tenant, "Saudi Electricity Company");
            Seed.Customer(seed, Aramco, Tenant, "Saudi Aramco");
            var lead = Seed.Lead(seed, leadId, Tenant, buyersName: null);
            lead.Rfqno = null;
            lead.Clientemail = portal;
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + leadId).FromEmail = $"Etimad <{portal}>";
            await seed.SaveChangesAsync();
            var learned = Identifier(Aramco, CustomerIdentifierType.Domain, "etimad.gov.sa", 0.95m);
            learned.Source = CustomerIdentifierSources.LeadReviewLearned;
            seed.Set<CustomerIdentifier>().Add(learned);
            await seed.SaveChangesAsync();
        }

        LeadClientEvidence Evidence(string sender) => new()
        {
            BusinessUnitId = Tenant, LeadId = leadId, SenderEmail = sender,
            Passages = [new DocumentPassage("delivery address", "Saudi Electricity Company-DAMMAM", true)]
        };
        var mailboxes = new[] { portal, "buyer@etimad.gov.sa" };

        await using (var context = db.ContextFor(Tenant))
        {
            var service = new LeadCustomerResolutionService(context);
            foreach (var mailbox in mailboxes)
            {
                var loaded = await service.LoadCorpusAsync(Tenant, Evidence(mailbox), CancellationToken.None);
                Assert.DoesNotContain(loaded.Identifiers, identifier => identifier.IdentifierType == CustomerIdentifierType.Domain);
            }

            var outcome = await service.ResolveAsync(Tenant, leadId);
            Assert.Equal(sec, outcome.CustomerId);
            Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        }

        // The control: a Domain row a person entered (a customer contact) is loaded for both mailboxes.
        await using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, portalOperator, Tenant, "National Tender Portal Company");
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(Identifier(portalOperator, CustomerIdentifierType.Domain, "etimad.gov.sa", 0.95m));
            await seed.SaveChangesAsync();
        }

        await using (var context = db.ContextFor(Tenant))
        {
            var service = new LeadCustomerResolutionService(context);
            foreach (var mailbox in mailboxes)
            {
                var loaded = await service.LoadCorpusAsync(Tenant, Evidence(mailbox), CancellationToken.None);
                Assert.Contains(loaded.Identifiers, identifier => identifier.IdentifierType == CustomerIdentifierType.Domain
                                                                  && identifier.CustomerId == portalOperator
                                                                  && identifier.Source == "CustomerContact");
                Assert.DoesNotContain(loaded.Identifiers, identifier => identifier.IdentifierType == CustomerIdentifierType.Domain
                                                                        && identifier.CustomerId == Aramco);
            }
        }
    }

    private static CustomerIdentifier Identifier(long customerId, CustomerIdentifierType type, string value, decimal confidence)
        => new()
        {
            BusinessUnitId = Tenant, CustomerId = customerId, IdentifierType = type,
            NormalizedValue = value, DisplayValue = value, IsVerified = true, Confidence = confidence,
            Source = "CustomerContact", EffectiveFrom = DateTime.UtcNow.AddDays(-30)
        };
}
