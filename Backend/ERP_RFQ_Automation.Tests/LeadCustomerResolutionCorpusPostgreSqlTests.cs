using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The CORPUS the resolver is given, against the production dialect. Every defect covered here
/// is invisible in a unit test because it lives in the SQL: which rows a cap keeps, whether a
/// pre-filter can see the buyer at all, and whether a surname written the Saudi way can be
/// matched by an equality test (it cannot).
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class LeadCustomerResolutionCorpusPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Above_the_name_scan_cap_a_portal_print_with_no_company_name_field_still_links()
    {
        // THE DEFECT: past MaximumNameScanRows the customer query narrowed on the first two
        // characters of CustomerCompanyNameExtracted. Lead 680 is an SEC portal print whose
        // ONLY evidence is the delivery address "Saudi Electricity Company-DAMMAM" — there is
        // no company-name field — so the pre-filter fell back to already-matched-only, the
        // passage tier was handed an empty customer list, and the one tier that can read that
        // document matched nothing. The cap is lowered here instead of seeding 2,000 customers.
        var suffix = Random.Shared.Next(1, 49_999);
        var tenant = 9_410_000L + suffix;
        var decoyA = 9_420_000L + suffix;
        var decoyB = 9_421_000L + suffix;
        var target = 9_422_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, decoyA, tenant, "Zulu Freight Holdings");
            Seed.Customer(seed, decoyB, tenant, "Yankee Marine Supplies");
            Seed.Customer(seed, target, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            // Lead 680 came through the folder door, not a mailbox: there is no sender at all,
            // which is exactly why the delivery address has to carry the lead on its own.
            lead.EmailIngestsId = null;
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(
                context, new CustomerResolutionPolicy { MaximumNameScanRows = 2 })
            .ResolveAsync(tenant, leadId);

        Assert.Equal(target, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(0.88m, outcome.Confidence);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Above_the_name_scan_cap_a_print_that_names_the_buyer_only_by_its_initials_still_links()
    {
        // #11. The name-bucket filter compared the first two letters of each word on the page with
        // the first two letters of each customer's name. "SEC Materials West Plant-West Operating
        // Area" yields SE, MA, WE, PL, OP, AR, and "Saudi Electricity Company" is filed under SA,
        // so above the cap SEC was never loaded and a print that links at 0.85 on SEC's initials in
        // a smaller tenant resolved to nothing. The cap is lowered instead of seeding 2,000 rows.
        var suffix = Random.Shared.Next(300_000, 339_999);
        var tenant = 9_410_000L + suffix;
        var decoyA = 9_420_000L + suffix;
        var decoyB = 9_421_000L + suffix;
        var target = 9_422_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, decoyA, tenant, "Zulu Freight Holdings");
            Seed.Customer(seed, decoyB, tenant, "Yankee Marine Supplies");
            Seed.Customer(seed, target, tenant, "Saudi Electricity Company");
            var item = Seed.LeadItem(9_440_000L + suffix, "10", 1, "BALL VALVE");
            item.StorageLocation = "SEC Materials West Plant-West Operating Area";
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer", items: [item]);
            lead.EmailIngestsId = null;
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(
                context, new CustomerResolutionPolicy { MaximumNameScanRows = 2 })
            .ResolveAsync(tenant, leadId);

        Assert.Equal(target, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(0.85m, outcome.Confidence);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Above_the_name_scan_cap_a_name_written_with_its_article_is_still_in_the_scan()
    {
        // #11. "Al-Rashid Trading Est" is filed under AL, the page writes "Rashid Trading", and a
        // two-letter "Al" on the page is skipped as noise, so the bucket could never meet the name.
        // The same for "El Seif" and "The Arabian Pipes". The name's bucket is now also taken after
        // its article. The decoys prove the filter still narrows.
        var suffix = Random.Shared.Next(340_000, 379_999);
        var tenant = 9_410_000L + suffix;
        var rashid = 9_420_000L + suffix;
        var seif = 9_421_000L + suffix;
        var pipes = 9_422_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, 9_423_000L + suffix, tenant, "Zulu Freight Holdings");
            Seed.Customer(seed, 9_424_000L + suffix, tenant, "Yankee Marine Supplies");
            Seed.Customer(seed, 9_425_000L + suffix, tenant, "Quartz Holdings Group");
            Seed.Customer(seed, rashid, tenant, "Al-Rashid Trading Est");
            Seed.Customer(seed, seif, tenant, "El Seif Engineering");
            Seed.Customer(seed, pipes, tenant, "The Arabian Pipes Company");
            await seed.SaveChangesAsync();
        }

        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = tenant,
            Passages =
            [
                new DocumentPassage("delivery address",
                    "Rashid Trading warehouse, c/o Seif Engineering, Arabian Pipes store", true)
            ]
        };

        await using var context = database.ContextFor(tenant);
        var corpus = await new LeadCustomerResolutionService(
                context, new CustomerResolutionPolicy { MaximumNameScanRows = 4 })
            .LoadCorpusAsync(tenant, evidence, CancellationToken.None);

        Assert.Equal(
            new[] { rashid, seif, pipes }.Order(),
            corpus.Customers.Select(c => c.CustomerId).Order());
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Above_the_name_scan_cap_initials_two_customers_share_link_neither()
    {
        // #12. "Saudi Cable Company" (bucket SA) and "Sudair Ceramics Company" (bucket SU) both
        // derive SCC. The page "SCC Store, Sudair Industrial City" buckets only Sudair Ceramics, so
        // the capped list saw SCC as unique and the lead auto-linked to Sudair Ceramics at 0.85 —
        // where, below the cap, the same page linked nobody. Every owner of initials the page
        // prints is now read, and the tenant-wide collision set is computed from every customer.
        var suffix = Random.Shared.Next(380_000, 419_999);
        var tenant = 9_410_000L + suffix;
        var cable = 9_420_000L + suffix;
        var ceramics = 9_421_000L + suffix;
        var leadId = 9_430_000L + suffix;
        const string address = "SCC Store, Sudair Industrial City";

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, 9_423_000L + suffix, tenant, "Zulu Freight Holdings");
            Seed.Customer(seed, 9_424_000L + suffix, tenant, "Yankee Marine Supplies");
            Seed.Customer(seed, 9_425_000L + suffix, tenant, "Quartz Holdings Group");
            Seed.Customer(seed, cable, tenant, "Saudi Cable Company");
            Seed.Customer(seed, ceramics, tenant, "Sudair Ceramics Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.DeliveryLocation = address;
            lead.EmailIngestsId = null;
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var capped = new LeadCustomerResolutionService(context, new CustomerResolutionPolicy { MaximumNameScanRows = 3 });
        var outcome = await capped.ResolveAsync(tenant, leadId);

        Assert.Null(outcome.CustomerId);
        Assert.False(outcome.Status.StartsWith(LeadCustomerMatchStatuses.AutoMatched, StringComparison.Ordinal));

        var evidence = new LeadClientEvidence
        {
            BusinessUnitId = tenant,
            Passages = [new DocumentPassage("delivery address", address, true)]
        };
        var above = await capped.LoadCustomerNamesAsync(tenant, evidence, [], CancellationToken.None);
        Assert.Contains(cable, above.Customers.Select(c => c.CustomerId));
        Assert.Contains(ceramics, above.Customers.Select(c => c.CustomerId));
        Assert.Contains("SCC", above.TenantAcronymCollisions);

        // Below the cap the loaded list IS the tenant, and says the same thing.
        var below = await new LeadCustomerResolutionService(context)
            .LoadCustomerNamesAsync(tenant, evidence, [], CancellationToken.None);
        Assert.Contains("SCC", below.TenantAcronymCollisions);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_sec_print_carrying_the_buyers_own_mail_address_still_links_on_its_delivery_address()
    {
        // THE DEFECT: the consignee rule treated every sender domain not registered to the
        // customer as a rival buyer, and the domain tier returns whenever a domain IS registered
        // to anybody, so every corporate domain reaching the rule was "a rival". An SEC print that
        // prints its buyer's own address (57322@se.com.sa) beside "Saudi Electricity
        // Company-JIZAN AREA" was demoted to a 0.70 suggestion, explained as "from se.com.sa, which
        // is not Saudi Electricity Company". The lead-680 fixtures above only passed because they
        // carry no address at all.
        var suffix = Random.Shared.Next(250_000, 289_999);
        var tenant = 9_410_000L + suffix;
        var customerId = 9_420_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.DeliveryLocation = "Saudi Electricity Company-JIZAN AREA";
            lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            lead.EmailIngestsId = null;
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Equal(customerId, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(0.88m, outcome.Confidence);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_hyphenated_surname_finds_the_contact_an_equality_test_could_never_match()
    {
        // THE DEFECT: the pre-filter compared LastName.ToUpper() with the last token of the
        // normalised buyer line. SEC prints its buyers "3C2-AMER AL-DOSSARY", which yields
        // DOSSARY, and the stored contact is "Al-Dossary" — so the contact never loaded and the
        // contact-person tier, written for exactly this format, could not fire at all.
        var suffix = Random.Shared.Next(50_000, 99_999);
        var tenant = 9_410_000L + suffix;
        var customerId = 9_420_000L + suffix;
        var contactId = 9_425_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Zephyr Marine Services");
            seed.Contacts.Add(new Contact
            {
                Id = contactId,
                BusinessUnitId = tenant,
                CustomerId = customerId,
                FirstName = "Amer",
                LastName = "Al-Dossary",
                Email = "amer@zephyr.example",
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = DateTime.UtcNow
            });
            Seed.Lead(seed, leadId, tenant, buyersName: "3C2-AMER AL-DOSSARY");
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        // A person, not an organisation: it suggests and never links.
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.ContactPerson, outcome.ReasonCode);
        Assert.Null(outcome.CustomerId);
        Assert.Equal(customerId, Assert.Single(outcome.Candidates).CustomerId);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_tenants_taught_names_cannot_crowd_out_its_own_sender_email_identifier()
    {
        // THE DEFECT: one OR-predicate with a single Take(500) and no OrderBy. The learned-name
        // class is open-ended, so past roughly 1,500 taught rows the ONE row carrying the
        // buyer's exact sender address could be cut from the result — a 1.00 auto-link silently
        // degrading to a 0.65 suggestion a human then has to decide. Each class is capped on its
        // own budget now, so the authoritative row is loaded however much a tenant has taught.
        var suffix = Random.Shared.Next(100_000, 149_999);
        var tenant = 9_410_000L + suffix;
        var noisy = 9_420_000L + suffix;
        var target = 9_422_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, noisy, tenant, "Zulu Freight Holdings");
            Seed.Customer(seed, target, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            // A passage is what makes the taught-name class load at all; this one names nobody.
            lead.DeliveryLocation = "Second Industrial Area, Dammam";
            await seed.SaveChangesAsync();

            // Written FIRST, so they carry the lower ids that an unordered LIMIT returns first.
            for (var i = 0; i < 600; i++)
                seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                {
                    BusinessUnitId = tenant,
                    CustomerId = noisy,
                    IdentifierType = CustomerIdentifierType.Alias,
                    NormalizedValue = $"ZULU FREIGHT VARIANT {i:D4}",
                    DisplayValue = $"Zulu Freight variant {i:D4}",
                    IsVerified = true,
                    Confidence = 0.9m,
                    Source = CustomerIdentifierSources.LeadReviewLearned,
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                });
            await seed.SaveChangesAsync();

            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = tenant,
                CustomerId = target,
                IdentifierType = CustomerIdentifierType.Email,
                NormalizedValue = "57322@se.com.sa",
                DisplayValue = "57322@se.com.sa",
                IsVerified = true,
                Confidence = 1m,
                Source = "CustomerProfile",
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Equal(target, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderEmailExact, outcome.ReasonCode);
        Assert.Equal(1.00m, outcome.Confidence);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_company_named_on_a_marafiq_rfq_links_it_end_to_end()
    {
        // Production lead 682, whole: a one-word Saudi trade name in the company-name field and
        // in the captured sentence, our own vendor code on the page, a warehouse address, and no
        // sender. Both of the first two are header evidence, so the lead links on what the
        // document says about who is buying rather than on where the goods go.
        var suffix = Random.Shared.Next(200_000, 249_999);
        var tenant = 9_410_000L + suffix;
        var customerId = 9_420_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Marafiq");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.CustomerCompanyNameExtracted = "MARAFIQ";
            lead.CustomerCompanyEvidence =
                "MARAFIQ invites bidders in accordance with our Request for Quotation(RFQ).";
            lead.SupplierAccountRefOnDocument = "1495";
            lead.DeliveryLocation =
                "MARAFIQ Yanbu Warehouse, Power & Desalination Plant, Yanbu Al-Sinaiyah, KSA";
            lead.EmailIngestsId = null;
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Equal(customerId, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(0.88m, outcome.Confidence);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Resolution_returns_the_same_answer_on_every_pass_over_unchanged_evidence()
    {
        // The module's promise to a rep: the same evidence gives the same answer. With an
        // unordered LIMIT over a corpus larger than the cap that promise was a property of the
        // query plan, not of the data.
        var suffix = Random.Shared.Next(150_000, 199_999);
        var tenant = 9_410_000L + suffix;
        var customerId = 9_420_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            lead.EmailIngestsId = null;
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var service = new LeadCustomerResolutionService(context);
        var first = await service.ResolveAsync(tenant, leadId);
        var second = await service.ResolveAsync(tenant, leadId);

        Assert.Equal(customerId, first.CustomerId);
        Assert.Equal(first.Status, second.Status);
        Assert.Equal(first.CustomerId, second.CustomerId);
        Assert.Equal(first.Confidence, second.Confidence);
        Assert.Equal(first.Explanation, second.Explanation);
    }

    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("contact")]
    [InlineData("email identifier")]
    [InlineData("earlier human decision")]
    public async Task An_epc_contractors_other_mailbox_on_record_demotes_the_site_owner_end_to_end(string record)
    {
        // THE SEAM: the resolver lets a delivery address link only while nothing on the page names
        // somebody else, and a mail domain another customer's own records write from does. Its unit
        // tests hand it Hyundai's k.lee@hdec.com directly. This loader read only the EXACT sender
        // address, so procurement@hdec.com brought nothing about hdec.com with it and Aramco, who is
        // buying nothing on this job, linked at 0.88. Each row is one kind of record a person set:
        // a contact typed in (with no identifier sync behind it), an address registered on the
        // customer with no Domain row beside it, and an earlier lead from hdec.com a person linked.
        var suffix = Random.Shared.Next(420_000, 459_999);
        var tenant = 9_410_000L + suffix;
        var aramco = 9_420_000L + suffix;
        var hyundai = 9_421_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, aramco, tenant, "Saudi Aramco");
            Seed.Customer(seed, hyundai, tenant, "Hyundai Engineering & Construction");
            // Not "Buyer": the contact tier's surname read would load the contact on its own and the
            // test would pass without the domain read it exists to prove.
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Park Jihoon");
            lead.Clientemail = "procurement@hdec.com";
            lead.EmailIngestsId = null;
            lead.DeliveryLocation = "Saudi Aramco Ras Tanura Refinery";
            switch (record)
            {
                case "contact":
                    Seed.Contact(seed, 9_440_000L + suffix, tenant, hyundai, email: "k.lee@hdec.com");
                    break;
                case "email identifier":
                    seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                    {
                        BusinessUnitId = tenant, CustomerId = hyundai,
                        IdentifierType = CustomerIdentifierType.Email,
                        NormalizedValue = "k.lee@hdec.com", DisplayValue = "k.lee@hdec.com",
                        IsVerified = true, Confidence = 1m, Source = "CustomerImport",
                        EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                    });
                    break;
                case "earlier human decision":
                    // Two different hdec.com mailboxes a person linked to Hyundai. One decision from another
                    // mailbox is a fact about that mailbox, not the domain (T19; the resolver's R7, C05 and C44
                    // rows): read as the domain's, it took every correctly named print away from its consignee.
                    // This row held that single-decision shape until T19, which is why it now seeds two.
                    foreach (var (id, from) in new[]
                             {
                                 (9_450_000L + suffix, "Kim Lee <k.lee@hdec.com>"),
                                 (9_460_000L + suffix, "Min Park <m.park@hdec.com>")
                             })
                    {
                        var earlier = Seed.Lead(seed, id, tenant, buyersName: "Kim Lee");
                        earlier.Clientemail = from;
                        earlier.EmailIngestsId = null;
                        earlier.ResolveCommercialIdentity(hyundai, null,
                            LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
                    }
                    break;
            }
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Suggested, outcome.Status);
        if (record == "earlier human decision")
            // Where earlier decisions are all that speak, the page's named consignee is offered first and the
            // earlier pick below it (T19), so a rep is not steered into repeating it. Hyundai is still offered.
            Assert.Contains(outcome.Candidates, c => c.CustomerId == hyundai);
        else
            Assert.Equal(hyundai, outcome.Candidates[0].CustomerId);
        var site = Assert.Single(outcome.Candidates, c => c.CustomerId == aramco);
        Assert.True(site.Confidence < 0.85m, $"Aramco was offered at {site.Confidence}, which is link strength.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_colleague_of_the_buyer_on_record_does_not_demote_the_buyers_own_address()
    {
        // What the domain read must not take away: lead 680's shape with a sender. SEC's own contact
        // at se.com.sa ties the domain to SEC, so it speaks FOR the address, not against it.
        var suffix = Random.Shared.Next(460_000, 499_999);
        var tenant = 9_410_000L + suffix;
        var sec = 9_420_000L + suffix;
        var aramco = 9_421_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, sec, tenant, "Saudi Electricity Company");
            Seed.Customer(seed, aramco, tenant, "Saudi Aramco");
            Seed.Contact(seed, 9_440_000L + suffix, tenant, sec, email: "ali.nasser@se.com.sa");
            Seed.Contact(seed, 9_441_000L + suffix, tenant, aramco, email: "buyer@aramco.com");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Park Jihoon");
            lead.Clientemail = "57322@se.com.sa";
            lead.EmailIngestsId = null;
            lead.DeliveryLocation = "Saudi Electricity Company-DAMMAM";
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Equal(sec, outcome.CustomerId);
        Assert.StartsWith(LeadCustomerMatchStatuses.AutoMatched, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NameInDocument, outcome.ReasonCode);
        Assert.Equal(0.88m, outcome.Confidence);
    }
}
