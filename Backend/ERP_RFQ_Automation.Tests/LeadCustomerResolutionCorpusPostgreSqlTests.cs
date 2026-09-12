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
}
