using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialRouting;

/// <summary>
/// Routing reads the same customer_identifiers rows as the resolver, and it can write a customer
/// onto the lead (WriteCustomerThroughToLead). So routing must refuse exactly what the resolver
/// refuses, or every resolver guard is undone one step later.
///
/// <para>That is what happened. The resolver stopped treating bidnet.com as a customer's domain,
/// but a bidnet.com → Saudi Aramco Domain row learned before that fix was still in the store.
/// Routing added the sender's domain with no check, the engine called the row a verified 0.95
/// match, and the lead was linked to Aramco and handed to Aramco's owner. The extraction worker
/// routes straight after resolution, so the lead the resolver had rightly left unlinked came out
/// linked anyway.</para>
///
/// <para>Every "does not route" case below uses a row that is already verified and trusted, the
/// shape legacy data has. Each one linked the lead on the old code. The "still routes" cases pin
/// what the guard must NOT take away: a real corporate domain, and an exact address a person
/// registered, even on gmail.</para>
/// </summary>
public sealed class RoutingSenderIdentityGuardTests
{
    private const long Tenant = 71;
    private const long OtherTenant = 72;
    private const long LeadId = 781;
    private const long Aramco = 7811;
    private const long Sec = 7812;
    private const long Marafiq = 7813;
    private const long AramcoOwner = 7821;
    private const long SecOwner = 7822;
    private const long MarafiqOwner = 7826;

    [Fact]
    public async Task A_legacy_relay_domain_row_does_not_write_a_customer_through_routing()
    {
        using var db = new TestDb();
        await SeedAsync(db, "alerts@bidnet.com", context => context.Set<CustomerIdentifier>().Add(
            Identifier(7851, Aramco, CustomerIdentifierType.Domain, "bidnet.com", 0.95m)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-relay-bidnet", "corr-relay-bidnet"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.NoEvidence, result.MatchStatus);
        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.CustomerId);
        Assert.Null(result.AssignmentId);
        Assert.NotNull(result.WorkItemId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, lead.CustomerMatchStatus);
        Assert.Null(lead.AssignTo);
        var decision = await context.Set<LeadRoutingDecision>().AsNoTracking().SingleAsync();
        Assert.Null(decision.CustomerId);
        Assert.Null(decision.MatchedIdentifierId);
    }

    [Fact]
    public async Task A_genuine_corporate_domain_row_still_routes_and_links()
    {
        // The tenant has its own mailbox and staff domains on file, and ANOTHER tenant has a user
        // at se.com.sa. Neither may stop SEC's real domain from naming SEC here: a self-domain
        // list that over-reaches, or one read without the tenant predicate, fails this test.
        using var db = new TestDb();
        await SeedAsync(db, "57322@se.com.sa", context =>
        {
            context.Set<CustomerIdentifier>().Add(
                Identifier(7852, Sec, CustomerIdentifierType.Domain, "se.com.sa", 0.95m));
            Seed.EmailConfig(context, 7861, Tenant).EmailAddress = "rfq@alquraishi.com";
            context.Users.Add(User(7823, Tenant, "ahmed@alquraishi.com.sa"));
            Seed.EnsureBusinessUnit(context, OtherTenant);
            context.Users.Add(User(7824, OtherTenant, "buyer.admin@se.com.sa"));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-corporate-domain", "corr-corporate-domain"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(RoutingOutcome.AssignedPrimary, result.Outcome);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, lead.CustomerMatchStatus);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, lead.CustomerMatchReasonCode);
        Assert.Equal(0.95m, lead.CustomerMatchConfidence);
    }

    [Theory]
    // A relay's sending host: every Ariba buyer's RFQ comes from under ariba.com.
    [InlineData("rfq@s4.ansmtp.ariba.com", "s4.ansmtp.ariba.com")]
    // Etimad delivers the whole Saudi government's tenders.
    [InlineData("noreply@etimad.sa", "etimad.sa")]
    // Consumer mail. fastmail.com was missing from the free-mail list until 2026-09-12.
    [InlineData("agent@fastmail.com", "fastmail.com")]
    [InlineData("buyer.sec@gmail.com", "gmail.com")]
    // Nexora's own ingestion placeholder: the folder and upload doors write this address.
    [InlineData("extraction@pipeline.local", "pipeline.local")]
    public async Task A_domain_that_names_no_single_organisation_never_names_a_customer_through_routing(
        string sender, string storedDomain)
    {
        using var db = new TestDb();
        await SeedAsync(db, sender, context => context.Set<CustomerIdentifier>().Add(
            Identifier(7853, Aramco, CustomerIdentifierType.Domain, storedDomain, 0.95m)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-shared-{storedDomain}", $"corr-shared-{storedDomain}"),
            CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Theory]
    // A salesman's own address, on a staff domain that is NOT an ingestion mailbox: only the
    // tenant's user list says it is ours.
    [InlineData("ahmed@alquraishi.com.sa", "alquraishi.com.sa")]
    // The ingestion mailbox's own domain.
    [InlineData("sales.desk@alquraishi.com", "alquraishi.com")]
    // A host under one of our domains is still us.
    [InlineData("ahmed@ksa.alquraishi.com.sa", "ksa.alquraishi.com.sa")]
    public async Task A_forward_from_our_own_domain_does_not_route_to_the_customer_it_was_taught_for(
        string sender, string storedDomain)
    {
        // The rows a reviewer's confirmation of one forwarded SEC bid used to mint: the colleague's
        // exact address at 1.00 and our domain at 0.95. With them in the store, every later forward
        // from anyone on our staff went to SEC's owner and was linked to SEC, whatever it said.
        using var db = new TestDb();
        await SeedAsync(db, sender, context =>
        {
            Seed.EmailConfig(context, 7862, Tenant).EmailAddress = "rfq@alquraishi.com";
            context.Users.Add(User(7825, Tenant, "ahmed@alquraishi.com.sa"));
            context.Set<CustomerIdentifier>().AddRange(
                Identifier(7854, Sec, CustomerIdentifierType.Email, sender, 1.00m),
                Identifier(7855, Sec, CustomerIdentifierType.Domain, storedDomain, 0.95m));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-self-{storedDomain}", $"corr-self-{storedDomain}"),
            CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Theory]
    [InlineData(CustomerIdentifierType.Email, "procurement@hdec.com", 1.00)]
    [InlineData(CustomerIdentifierType.Domain, "hdec.com", 0.95)]
    [InlineData(CustomerIdentifierType.Alias, "Hyundai Engineering and Construction", 0.90)]
    public async Task A_row_the_learner_refused_to_trust_never_routes_even_when_its_flag_reads_verified(
        CustomerIdentifierType type, string value, double confidence)
    {
        // hdec.com is an EPC contractor buying for Aramco's Ras Tanura site; a reviewer linked the
        // document to Aramco and the learner kept what it saw as LeadReviewUnverified, a row for a
        // person to look at. Its IsVerified flag reads true here, which is what the learner's
        // reinforcement path leaves behind: it promotes the flag and never touches Source.
        using var db = new TestDb();
        await SeedAsync(db, "procurement@hdec.com", context =>
            context.Set<CustomerIdentifier>().Add(Identifier(7856, Aramco, type, value, (decimal)confidence,
                source: CustomerIdentifierSources.LeadReviewUnverified)),
            buyersName: "Hyundai Engineering and Construction");
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-unverified-{type}", $"corr-unverified-{type}"),
            CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Theory]
    // One gmail mailbox is one person; an administrator registered it on SEC.
    [InlineData("buyer.sec@gmail.com", "gmail.com")]
    // One relay mailbox a person confirmed belongs to one buyer. The resolver keeps the exact
    // relay address as evidence too (S1); routing must not be stricter than the resolver here.
    [InlineData("sec-tenders@bidnet.com", "bidnet.com")]
    public async Task An_exact_address_on_a_shared_domain_still_routes_to_the_customer_it_was_registered_on(
        string sender, string sharedDomain)
    {
        // The shared domain is ALSO stored against Aramco, the legacy shape. The guard must drop
        // only that domain row, not the sender address with it.
        using var db = new TestDb();
        await SeedAsync(db, sender, context => context.Set<CustomerIdentifier>().AddRange(
            Identifier(7857, Sec, CustomerIdentifierType.Email, sender, 1.00m,
                source: CustomerIdentifierSources.MasterData),
            Identifier(7858, Aramco, CustomerIdentifierType.Domain, sharedDomain, 0.95m)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-exact-{sharedDomain}", $"corr-exact-{sharedDomain}"),
            CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
    }

    [Theory]
    [InlineData(CustomerIdentifierSources.LeadReviewLearned)]
    [InlineData("MigrationBackfill")]
    public async Task A_relay_mailbox_nobody_registered_does_not_route_every_buyer_on_the_network_to_one_customer(string source)
    {
        // The old learner minted a portal's own sending address as the first confirmed buyer's Email.
        // Routing let every relay address through as exact evidence, so a SABIC RFQ relayed through
        // Ariba went to Saudi Aramco's owner and Aramco was written onto the lead. A relay mailbox
        // routes only where a person registered it (sec-tenders@bidnet.com, above).
        const string relay = "ordersender-prod@ansmtp.ariba.com";
        using var db = new TestDb();
        await SeedAsync(db, relay, context => context.Set<CustomerIdentifier>().Add(
            Identifier(7869, Aramco, CustomerIdentifierType.Email, relay, 1.00m, source: source)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-relay-email-{source}", $"corr-relay-email-{source}"),
            CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Fact]
    public async Task A_forward_from_a_domain_that_spells_our_own_name_does_not_route_when_no_user_writes_from_it()
    {
        // THE SEAM: the learner calls a domain whose name spells the tenant's "ours" and never learns
        // from it, and the resolver now agrees. Routing knew only the mailbox and user domains, so a
        // colleague with no Nexora login forwarding from alquraishi.com.sa still went to SEC's owner on
        // the rows one confirmation minted before the learner refused them, and SEC was written
        // through onto the lead.
        using var db = new TestDb();
        await SeedAsync(db, "ahmed@alquraishi.com.sa", context =>
        {
            context.BusinessUnits.Find(Tenant)!.BusinessUnitName = "ALI ZAID AL-QURAISHI & PARTNERS";
            Seed.EmailConfig(context, 7863, Tenant).EmailAddress = "rfq@alquraishi.com";
            context.Set<CustomerIdentifier>().AddRange(
                Identifier(7859, Sec, CustomerIdentifierType.Email, "ahmed@alquraishi.com.sa", 1.00m),
                Identifier(7860, Sec, CustomerIdentifierType.Domain, "alquraishi.com.sa", 0.95m));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-own-name-domain", "corr-own-name-domain"), CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Fact]
    public async Task A_customers_own_domain_still_routes_beside_the_tenants_real_name()
    {
        using var db = new TestDb();
        await SeedAsync(db, "57322@se.com.sa", context =>
        {
            context.BusinessUnits.Find(Tenant)!.BusinessUnitName = "ALI ZAID AL-QURAISHI & PARTNERS";
            context.Set<CustomerIdentifier>().Add(
                Identifier(7864, Sec, CustomerIdentifierType.Domain, "se.com.sa", 0.95m));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-own-name-control", "corr-own-name-control"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
    }

    [Fact]
    public async Task A_four_character_company_code_on_the_lines_does_not_outrank_the_buyers_registered_address()
    {
        // THE SEAM: the resolver refuses an account number under five characters, because on an SAP
        // print a line's company reference is 1000, 2000 or SA01, shared by every affiliate of a
        // group (CustomerIdentityResolverTests.A_four_character_company_code_no_longer_links_a_lead).
        // Routing read every line's CompanyRef as an ERP account, and ERP account is its strongest
        // identifier. A customer whose DocId is 1000 (the migration backfill writes DocIds as ERP
        // accounts) outranked SEC's own registered address on SEC's own print: the lead went to that
        // customer's owner and, where resolution had not linked it, that customer was written on.
        using var db = new TestDb();
        await SeedAsync(db, "57322@se.com.sa", context =>
        {
            AddLine(context, 7871, companyRef: "1000");
            context.Set<CustomerIdentifier>().AddRange(
                Identifier(7865, Aramco, CustomerIdentifierType.ErpAccount, "1000", 1.00m, source: "MigrationBackfill"),
                Identifier(7866, Sec, CustomerIdentifierType.Email, "57322@se.com.sa", 1.00m, source: "CustomerProfile"));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-company-code", "corr-company-code"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
    }

    [Fact]
    public async Task Our_own_vendor_code_printed_on_a_line_never_names_a_customer_through_routing()
    {
        // Vendor code 2004414 is OUR number at SEC. The resolver drops it from the account evidence;
        // routing matched a legacy ERP-account row on it and wrote that customer onto the lead.
        using var db = new TestDb();
        await SeedAsync(db, "extraction@pipeline.local", context =>
        {
            context.Leads.Local.Single(l => l.Id == LeadId).SupplierAccountRefOnDocument = "2004414";
            AddLine(context, 7872, companyRef: "2004414");
            context.Set<CustomerIdentifier>().Add(
                Identifier(7867, Aramco, CustomerIdentifierType.ErpAccount, "2004414", 1.00m));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-our-vendor-code", "corr-our-vendor-code"), CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Fact]
    public async Task A_real_customer_account_number_on_a_line_still_routes_and_links()
    {
        using var db = new TestDb();
        await SeedAsync(db, "extraction@pipeline.local", context =>
        {
            AddLine(context, 7873, portalId: "0010045678");
            context.Set<CustomerIdentifier>().Add(
                Identifier(7868, Sec, CustomerIdentifierType.ErpAccount, "0010045678", 1.00m, source: "CustomerProfile"));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-real-account", "corr-real-account"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.ErpAccountExact, lead.CustomerMatchReasonCode);
    }

    [Fact]
    public async Task A_vendor_block_misread_as_the_buyer_does_not_discard_the_buyers_registered_address()
    {
        // C09: the extractor put MARAFIQ in the vendor block. Routing counted the vendor block among
        // our names, so marafiq.com.sa spelled "us", Marafiq's registered address and domain were
        // dropped, and the lead went to the unassigned queue instead of Marafiq's owner.
        using var db = new TestDb();
        await SeedAsync(db, "buyer@marafiq.com.sa", context =>
        {
            context.Leads.Local.Single(l => l.Id == LeadId).SupplierNameOnDocument = "MARAFIQ";
            Seed.Customer(context, Marafiq, Tenant, "Marafiq");
            context.Users.Add(User(MarafiqOwner, Tenant, "marafiq.owner@example.com"));
            context.SaveChanges();
            context.Set<CustomerOwnership>().Add(Ownership(7833, Marafiq, MarafiqOwner));
            context.Set<CustomerIdentifier>().AddRange(
                Identifier(7874, Marafiq, CustomerIdentifierType.Email, "buyer@marafiq.com.sa", 1.00m, source: "CustomerContact"),
                Identifier(7875, Marafiq, CustomerIdentifierType.Domain, "marafiq.com.sa", 0.95m, source: "CustomerContact"));
        });
        await using var context = await RoutingContextAsync(db, MarafiqOwner);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-vendor-block-marafiq", "corr-vendor-block-marafiq"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(MarafiqOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Marafiq, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
        Assert.Equal(1.00m, lead.CustomerMatchConfidence);
    }

    [Fact]
    public async Task A_vendor_block_that_names_the_buyer_does_not_discard_the_buyers_registered_domain()
    {
        // C39: vendor block "Saudi Aramco", sender on aramco.com, Aramco's domain on record.
        using var db = new TestDb();
        await SeedAsync(db, "buyer@aramco.com", context =>
        {
            context.Leads.Local.Single(l => l.Id == LeadId).SupplierNameOnDocument = "Saudi Aramco";
            context.Set<CustomerIdentifier>().Add(
                Identifier(7876, Aramco, CustomerIdentifierType.Domain, "aramco.com", 0.95m, source: "CustomerContact"));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-vendor-block-aramco", "corr-vendor-block-aramco"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(AramcoOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Aramco, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, lead.CustomerMatchReasonCode);
        Assert.Equal(0.95m, lead.CustomerMatchConfidence);
    }

    [Fact]
    public async Task A_vendor_block_that_spells_our_own_name_still_keeps_a_colleagues_forward_out_of_routing()
    {
        // The other half of C09: our own name, printed however the vendor block prints it, is still us.
        using var db = new TestDb();
        await SeedAsync(db, "ahmed@alquraishi.com.sa", context =>
        {
            context.BusinessUnits.Find(Tenant)!.BusinessUnitName = "ALI ZAID AL-QURAISHI & PARTNERS";
            context.Leads.Local.Single(l => l.Id == LeadId).SupplierNameOnDocument = "ALI ZAID AL-QURAISHI&PARTNERS EL";
            context.Set<CustomerIdentifier>().AddRange(
                Identifier(7877, Sec, CustomerIdentifierType.Email, "ahmed@alquraishi.com.sa", 1.00m),
                Identifier(7878, Sec, CustomerIdentifierType.Domain, "alquraishi.com.sa", 0.95m));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-vendor-block-ours", "corr-vendor-block-ours"), CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Fact]
    public async Task A_demo_login_on_a_customers_registered_domain_does_not_stop_that_customer_routing()
    {
        // C11: tenant 7's shape. A login zack@kodekinetics.com made kodekinetics.com ours, so Aramco's
        // registered contact zahid@kodekinetics.com routed NO_MATCH_EVIDENCE.
        using var db = new TestDb();
        await SeedAsync(db, "zahid@kodekinetics.com", context =>
        {
            context.Users.Add(User(7827, Tenant, "zack@kodekinetics.com"));
            Seed.Contact(context, 7881, Tenant, Aramco, "zahid@kodekinetics.com");
            context.Set<CustomerIdentifier>().AddRange(
                Identifier(7879, Aramco, CustomerIdentifierType.Email, "zahid@kodekinetics.com", 1.00m, source: "CustomerContact"),
                Identifier(7880, Aramco, CustomerIdentifierType.Domain, "kodekinetics.com", 0.95m, source: "CustomerContact"));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-demo-login-aramco", "corr-demo-login-aramco"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(AramcoOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Aramco, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
        Assert.Equal(1.00m, lead.CustomerMatchConfidence);
    }

    [Fact]
    public async Task A_demo_login_on_a_customers_own_domain_does_not_stop_its_registered_address_routing()
    {
        // C11b: a login demo@se.com.sa, SEC's registered 57322@se.com.sa, nothing else.
        using var db = new TestDb();
        await SeedAsync(db, "57322@se.com.sa", context =>
        {
            context.Users.Add(User(7828, Tenant, "demo@se.com.sa"));
            context.Set<CustomerIdentifier>().Add(
                Identifier(7882, Sec, CustomerIdentifierType.Email, "57322@se.com.sa", 1.00m, source: "CustomerContact"));
        });
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-demo-login-sec", "corr-demo-login-sec"), CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
    }

    [Theory]
    [InlineData("no-reply@etimad.gov.sa", CustomerIdentifierSources.LeadReviewLearned)]
    [InlineData("do_not_reply@coupa.com", CustomerIdentifierSources.LeadReviewLearned)]
    [InlineData("no-reply@etimad.gov.sa", "MigrationBackfill")]
    public async Task A_learned_system_mailbox_on_an_unlisted_portal_host_does_not_route(string mailbox, string source)
    {
        // Neither host is on the relay list, so the learned row decided at 1.00 and routed to Aramco's owner.
        using var db = new TestDb();
        await SeedAsync(db, mailbox, context => context.Set<CustomerIdentifier>().Add(
            Identifier(7883, Aramco, CustomerIdentifierType.Email, mailbox, 1.00m, source: source)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-system-mailbox-{source}", $"corr-system-mailbox-{source}"),
            CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
        Assert.Null(lead.AssignTo);
    }

    [Theory]
    [InlineData("no-reply@etimad.gov.sa")]
    [InlineData("do_not_reply@coupa.com")]
    public async Task A_system_mailbox_a_person_registered_still_routes(string mailbox)
    {
        using var db = new TestDb();
        await SeedAsync(db, mailbox, context => context.Set<CustomerIdentifier>().Add(
            Identifier(7884, Sec, CustomerIdentifierType.Email, mailbox, 1.00m, source: CustomerIdentifierSources.MasterData)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-registered-system-mailbox", "corr-registered-system-mailbox"),
            CancellationToken.None);

        Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
        Assert.Equal(SecOwner, result.SelectedUserId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Equal(Sec, lead.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, lead.CustomerMatchReasonCode);
    }

    private static void AddLine(ErpRfqAutomationContext context, long id, string? companyRef = null, string? portalId = null)
    {
        // Added with its lead id already set: the lead is saved, and fixing the item up through the
        // lead's collection makes EF try to change LeadId, which the model keys on.
        var item = Seed.LeadItem(id, "10", 1, "BALL VALVE");
        item.LeadId = LeadId;
        item.CompanyRef = companyRef;
        item.CustomerAccountPortalId = portalId;
        context.Set<LeadItem>().Add(item);
    }

    [Fact]
    public async Task A_domain_our_own_vendor_name_spells_is_ours_to_the_resolver_and_to_routing_alike()
    {
        // THE SEAM. Routing and the resolver read one lead one step apart, and they asked "is this domain ours"
        // with different names: routing with the configured name and the vendor block where it spells it, the
        // resolver with the configured name alone. On "ALI ZAID AL-QURAISHI & PARTNERS ESOSA" from
        // sales@esosa.com the resolver linked SEC at 0.95 and routing refused the same Domain row as our own
        // mail. Both must answer the same way on the same lead.
        using var db = new TestDb();
        await SeedAsync(db, "sales@esosa.com", context =>
        {
            context.BusinessUnits.Find(Tenant)!.BusinessUnitName = "ALI ZAID AL-QURAISHI & PARTNERS";
            context.Leads.Local.Single(l => l.Id == LeadId).SupplierNameOnDocument = "ALI ZAID AL-QURAISHI & PARTNERS ESOSA";
            context.EmailIngests.Local.Single(i => i.Id == 20_000 + LeadId).FromEmail = "sales@esosa.com";
            context.Set<CustomerIdentifier>().Add(
                Identifier(7870, Sec, CustomerIdentifierType.Domain, "esosa.com", 0.95m, "CustomerContact"));
        });

        await using (var resolving = db.ContextFor(Tenant))
        {
            var lead = await resolving.Leads.Include(l => l.LeadItems).Include(l => l.EmailIngests)
                .SingleAsync(l => l.Id == LeadId);
            var resolved = await new LeadCustomerResolutionService(resolving).ResolveCoreAsync(Tenant, lead, CancellationToken.None);
            Assert.Null(resolved.CustomerId);
        }

        await using var context = await RoutingContextAsync(db);
        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, "route-vendor-spelled-domain", "corr-vendor-spelled-domain"), CancellationToken.None);

        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.AssignmentId);
    }

    [Theory]
    [InlineData("no-reply@etimad.gov.sa", "etimad.gov.sa", CustomerIdentifierSources.LeadReviewLearned, false)]
    [InlineData("do_not_reply@coupa.com", "coupa.com", CustomerIdentifierSources.LeadReviewLearned, false)]
    [InlineData("no-reply@sap.com", "sap.com", CustomerIdentifierSources.LeadReviewLearned, false)]
    // Controls: a rule a person entered, and a person's own mailbox on the host, still route.
    [InlineData("no-reply@etimad.gov.sa", "etimad.gov.sa", CustomerIdentifierSources.MasterData, true)]
    [InlineData("buyer@etimad.gov.sa", "etimad.gov.sa", CustomerIdentifierSources.LeadReviewLearned, true)]
    public async Task A_domain_row_nobody_entered_does_not_route_what_a_system_mailbox_on_that_host_carries(
        string sender, string domain, string source, bool routes)
    {
        // MUST STAY FIXED (W03.04, W03.05, W03.08), routing's half. The resolver's domain tier refuses a learned Domain
        // row for a lead a system mailbox carried; routing read the same row as a verified 0.95 match, so the SEC
        // tender went to Aramco's owner and Aramco was written onto the lead one step later.
        using var db = new TestDb();
        await SeedAsync(db, sender, context => context.Set<CustomerIdentifier>().Add(
            Identifier(7880, Aramco, CustomerIdentifierType.Domain, domain, 0.95m, source)));
        await using var context = await RoutingContextAsync(db);

        var result = await Service(context).RouteLeadAsync(Tenant,
            new RouteLeadCommand(LeadId, $"route-system-mailbox-{domain}-{routes}", $"corr-system-mailbox-{domain}-{routes}"),
            CancellationToken.None);

        if (routes)
        {
            Assert.Equal(CustomerMatchStatus.Matched, result.MatchStatus);
            Assert.Equal(AramcoOwner, result.SelectedUserId);
            return;
        }
        Assert.Equal("NO_MATCH_EVIDENCE", result.DecisionCode);
        Assert.Null(result.CustomerId);
        Assert.Null(result.AssignmentId);
        var lead = await context.Leads.AsNoTracking().SingleAsync(l => l.Id == LeadId);
        Assert.Null(lead.CustomerId);
    }

    private static CommercialRoutingApplicationService Service(ErpRfqAutomationContext context) =>
        new(context, new DeterministicRoutingEngine(), new RoutingPolicy());

    /// <summary>A tenant-scoped context with both customer owners eligible for routing, so an
    /// outcome with no owner can only mean routing found no customer.</summary>
    private static async Task<ErpRfqAutomationContext> RoutingContextAsync(TestDb db, params long[] additionalOwners)
    {
        var context = db.ContextFor(Tenant);
        var now = DateTime.UtcNow;
        foreach (var userId in new[] { AramcoOwner, SecOwner }.Concat(additionalOwners))
        {
            context.SalesRepProfiles.Add(new SalesRepProfile
            {
                BusinessUnitId = Tenant, UserId = userId, IsRoutingEligible = true,
                CapacityPercent = 100, DistributionWeight = 1, EffectiveFromUtc = now.AddDays(-1),
                Version = 1, UpdatedAtUtc = now, UpdatedBy = "test",
                LastMutationIdempotencyKey = $"sender-guard-profile-{userId}"
            });
        }
        await context.SaveChangesAsync();
        return context;
    }

    private static async Task SeedAsync(
        TestDb db, string senderEmail, Action<ErpRfqAutomationContext> addEvidence, string? buyersName = null)
    {
        await using var context = db.ContextFor(null);
        var lead = Seed.Lead(context, LeadId, Tenant, buyersName: buyersName);
        lead.Clientemail = senderEmail;
        Seed.Customer(context, Aramco, Tenant, "Saudi Aramco");
        Seed.Customer(context, Sec, Tenant, "Saudi Electricity Company");
        // Owners on Nexora's placeholder domain, so the owners themselves add nothing to "us".
        context.Users.AddRange(
            User(AramcoOwner, Tenant, "aramco.owner@example.com"),
            User(SecOwner, Tenant, "sec.owner@example.com"));
        await context.SaveChangesAsync();

        context.Set<CustomerOwnership>().AddRange(Ownership(7831, Aramco, AramcoOwner), Ownership(7832, Sec, SecOwner));
        addEvidence(context);
        await context.SaveChangesAsync();
    }

    private static CustomerIdentifier Identifier(
        long id, long customerId, CustomerIdentifierType type, string value, decimal confidence,
        string source = CustomerIdentifierSources.LeadReviewLearned) => new()
    {
        Id = id,
        BusinessUnitId = Tenant,
        CustomerId = customerId,
        IdentifierType = type,
        NormalizedValue = RoutingValueNormalizer.Normalize(type, value),
        DisplayValue = value,
        IsVerified = true,
        Confidence = confidence,
        Source = source,
        EffectiveFrom = DateTime.UtcNow.AddDays(-30)
    };

    private static CustomerOwnership Ownership(long id, long customerId, long ownerId) => new()
    {
        Id = id,
        BusinessUnitId = Tenant,
        CustomerId = customerId,
        PrimaryUserId = ownerId,
        Scope = OwnershipScope.GeneralCustomer,
        Priority = 100,
        EffectiveFrom = DateTime.UtcNow.AddDays(-1),
        IsActive = true,
        Source = "test",
        Version = 1
    };

    private static User User(long id, long businessUnitId, string email) => new()
    {
        Id = id,
        FirstName = $"User{id}",
        LastName = "Routing",
        Email = email,
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        Buid = businessUnitId,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };
}
