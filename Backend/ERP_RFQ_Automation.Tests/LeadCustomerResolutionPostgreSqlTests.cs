using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Client resolution against the production dialect: the queries, the tenant filters and the
/// CustomerID⇔status CHECK constraint all have to hold on real PostgreSQL, not just SQLite.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class LeadCustomerResolutionPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_exact_sender_domain_links_the_lead_and_records_why()
    {
        var suffix = Random.Shared.Next(1, 50_000);
        var tenant = 9_310_000L + suffix;
        var customerId = 9_320_000L + suffix;
        var leadId = 9_330_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "3C2-AMER AL-DOSSARY");
            lead.Clientemail = "extraction@pipeline.local";
            lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = tenant,
                CustomerId = customerId,
                IdentifierType = CustomerIdentifierType.Domain,
                NormalizedValue = "se.com.sa",
                DisplayValue = "se.com.sa",
                IsVerified = true,
                Confidence = 0.95m,
                Source = "CustomerProfile",
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Equal(customerId, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);

        await using var verify = database.ContextFor(tenant);
        var stored = await verify.Leads.SingleAsync(l => l.Id == leadId);
        Assert.Equal(customerId, stored.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.AutoMatchedContactUnresolved, stored.CustomerMatchStatus);
        Assert.Equal(CustomerMatchReasonCodes.SenderDomain, stored.CustomerMatchReasonCode);
        Assert.Equal(0.95m, stored.CustomerMatchConfidence);
        Assert.Contains("se.com.sa", stored.CustomerMatchExplanation);
        Assert.NotNull(stored.CustomerMatchedOn);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Ambiguity_persists_ranked_candidates_and_links_nothing()
    {
        var suffix = Random.Shared.Next(50_001, 99_999);
        var tenant = 9_310_000L + suffix;
        var first = 9_320_000L + suffix;
        var second = 9_321_000L + suffix;
        var leadId = 9_330_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, first, tenant, "Saudi Electricity Company");
            Seed.Customer(seed, second, tenant, "SEC Distribution Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Khaled M. Al-dehdi");
            lead.CustomerBuyerEmailExtracted = "92442@se.com.sa";
            await seed.SaveChangesAsync();
            foreach (var customerId in new[] { first, second })
                seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                {
                    BusinessUnitId = tenant,
                    CustomerId = customerId,
                    IdentifierType = CustomerIdentifierType.Domain,
                    NormalizedValue = "se.com.sa",
                    DisplayValue = "se.com.sa",
                    IsVerified = true,
                    Confidence = 0.95m,
                    Source = "CustomerProfile",
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        await using var verify = database.ContextFor(tenant);
        var stored = await verify.Leads.SingleAsync(l => l.Id == leadId);
        Assert.Null(stored.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, stored.CustomerMatchStatus);

        var candidates = await verify.Set<LeadCustomerMatchCandidate>()
            .Where(c => c.LeadId == leadId).OrderBy(c => c.Rank).ToListAsync();
        Assert.Equal(2, candidates.Count);
        Assert.Equal([1, 2], candidates.Select(c => c.Rank));
        Assert.All(candidates, c => Assert.Equal(CustomerMatchReasonCodes.SenderDomain, c.ReasonCode));
        Assert.All(candidates, c => Assert.NotEqual(string.Empty, c.Explanation));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Re_running_resolution_replaces_candidates_instead_of_duplicating_ranks()
    {
        // The (tenant, lead, rank) unique index makes a lazy delete-then-insert a live
        // failure; resolution must be re-runnable any number of times.
        //
        // #5: THE LEAD MUST STAY UNLINKED ON BOTH PASSES, or this test guards nothing. It was
        // seeded with a company name that now links on the first pass, and a linked lead returns
        // before the candidate rewrite is reached, so the second pass never touched the candidate
        // rows and a single row was trivially still there. Two customers on one registered domain
        // are AMBIGUOUS on every pass, so the rank slots really are rewritten the second time.
        var suffix = Random.Shared.Next(100_000, 149_999);
        var tenant = 9_310_000L + suffix;
        var first = 9_320_000L + suffix;
        var second = 9_321_000L + suffix;
        var leadId = 9_330_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, first, tenant, "Saudi Electricity Company");
            Seed.Customer(seed, second, tenant, "SEC Distribution Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.CustomerBuyerEmailExtracted = "92442@se.com.sa";
            await seed.SaveChangesAsync();
            foreach (var customerId in new[] { first, second })
                seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
                {
                    BusinessUnitId = tenant,
                    CustomerId = customerId,
                    IdentifierType = CustomerIdentifierType.Domain,
                    NormalizedValue = "se.com.sa",
                    DisplayValue = "se.com.sa",
                    IsVerified = true,
                    Confidence = 0.95m,
                    Source = "CustomerProfile",
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                });
            await seed.SaveChangesAsync();
        }

        async Task<List<(long Id, int Rank, long? CustomerId)>> CandidatesAsync()
        {
            await using var verify = database.ContextFor(tenant);
            return (await verify.Set<LeadCustomerMatchCandidate>()
                    .Where(c => c.LeadId == leadId)
                    .OrderBy(c => c.Rank)
                    .ToListAsync())
                .Select(c => (c.Id, c.Rank, (long?)c.CustomerId))
                .ToList();
        }

        // Each pass on its own context, as a re-run is in production: a later request or a backfill.
        ClientResolutionOutcome firstPass;
        await using (var context = database.ContextFor(tenant))
            firstPass = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);
        var afterFirst = await CandidatesAsync();

        ClientResolutionOutcome secondPass;
        await using (var context = database.ContextFor(tenant))
            secondPass = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);
        var afterSecond = await CandidatesAsync();

        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, firstPass.Status);
        Assert.Equal(LeadCustomerMatchStatuses.Ambiguous, secondPass.Status);
        Assert.Null(secondPass.CustomerId);

        Assert.Equal(2, afterFirst.Count);
        Assert.Equal([1, 2], afterFirst.Select(c => c.Rank));
        // Unchanged evidence, unchanged rows: the same customers in the same rank slots, and the
        // same row ids, because the slots are rewritten in place (the interface's idempotency
        // promise) rather than deleted and re-inserted under the unique index.
        Assert.Equal(afterFirst, afterSecond);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_colleague_forwarding_from_a_staff_domain_that_is_no_intake_mailbox_is_not_evidence_about_the_buyer()
    {
        // #17, the service half. "Us" was the tenant's intake mailboxes and nothing else. The
        // salesman forwarding an SEC bid writes from his own address on the company's staff
        // domain, which is not a mailbox, so that domain was a stranger: a Domain row that one
        // reviewer's confirmation of a forwarded bid had taught against it linked every later
        // forward from any colleague to SEC at 0.95, whatever the attachment said. The tenant's
        // own active users now count as "us", through the same loader the learner and routing use.
        var suffix = Random.Shared.Next(300_000, 349_999);
        var tenant = 9_310_000L + suffix;
        var customerId = 9_320_000L + suffix;
        var leadId = 9_330_000L + suffix;
        var staffAddress = $"ahmed.{suffix}@alquraishi.com.sa";

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Saudi Electricity Company");
            seed.Users.Add(new User
            {
                Id = 9_340_000L + suffix, FirstName = "Ahmed", LastName = "Karim", Email = staffAddress,
                PasswordHash = "x", ImageUrl = "n/a", Buid = tenant, IsActive = true,
                CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });
            Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            // The forward: the colleague's own mailbox, not the intake mailbox Seed creates.
            seed.EmailIngests.Local.Single(i => i.Id == 20_000 + leadId).FromEmail = $"Ahmed Karim <{staffAddress}>";
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = tenant,
                CustomerId = customerId,
                IdentifierType = CustomerIdentifierType.Domain,
                NormalizedValue = "alquraishi.com.sa",
                DisplayValue = "alquraishi.com.sa",
                IsVerified = true,
                Confidence = 0.95m,
                Source = CustomerIdentifierSources.LeadReviewLearned,
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenant, leadId);

        Assert.Null(outcome.CustomerId);
        Assert.NotEqual(CustomerMatchReasonCodes.SenderDomain, outcome.ReasonCode);
        Assert.DoesNotContain(outcome.Candidates, c => c.ReasonCode == CustomerMatchReasonCodes.SenderDomain);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_human_decision_is_never_overwritten_by_a_later_resolution_pass()
    {
        var suffix = Random.Shared.Next(150_000, 199_999);
        var tenant = 9_310_000L + suffix;
        var chosen = 9_320_000L + suffix;
        var other = 9_321_000L + suffix;
        var leadId = 9_330_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, chosen, tenant, "The Client The Reviewer Picked");
            Seed.Customer(seed, other, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            lead.ResolveCommercialIdentity(chosen, null,
                LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = tenant,
                CustomerId = other,
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

        Assert.Equal(chosen, outcome.CustomerId);
        Assert.Equal(CustomerMatchReasonCodes.HumanResolved, outcome.ReasonCode);
        await using var verify = database.ContextFor(tenant);
        Assert.Equal(chosen, (await verify.Leads.SingleAsync(l => l.Id == leadId)).CustomerId);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_backfill_resolves_existing_unresolved_leads_without_re_upload()
    {
        // The production problem in one test: 26 leads, 0 with a customer, no re-ingestion
        // possible. The backfill is the entry point that fixes them in place.
        var suffix = Random.Shared.Next(200_000, 249_999);
        var tenant = 9_310_000L + suffix;
        var customerId = 9_320_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Saudi Electricity Company");
            for (var i = 0; i < 3; i++)
            {
                var lead = Seed.Lead(seed, 9_330_000L + suffix + i, tenant, buyersName: $"3C2-BUYER-{i}");
                lead.Clientemail = "extraction@pipeline.local";
                lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            }
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().Add(new CustomerIdentifier
            {
                BusinessUnitId = tenant,
                CustomerId = customerId,
                IdentifierType = CustomerIdentifierType.Domain,
                NormalizedValue = "se.com.sa",
                DisplayValue = "se.com.sa",
                IsVerified = true,
                Confidence = 0.95m,
                Source = "CustomerProfile",
                EffectiveFrom = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var result = await new LeadCustomerResolutionService(context).BackfillAsync(tenant);

        Assert.Equal(3, result.Examined);
        Assert.Equal(3, result.AutoMatched);
        Assert.Equal(0, result.Failed);

        await using var verify = database.ContextFor(tenant);
        var leads = await verify.Leads.Where(l => l.BusinessUnitId == tenant).ToListAsync();
        Assert.All(leads, lead => Assert.Equal(customerId, lead.CustomerId));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task One_tenants_learned_identity_never_resolves_another_tenants_lead()
    {
        var suffix = Random.Shared.Next(250_000, 299_999);
        var tenantA = 9_310_000L + suffix;
        var tenantB = 9_311_000L + suffix;
        var customerA = 9_320_000L + suffix;
        var customerB = 9_321_000L + suffix;
        var leadB = 9_330_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenantA);
            Seed.EnsureBusinessUnit(seed, tenantB);
            Seed.Customer(seed, customerA, tenantA, "Saudi Electricity Company");
            Seed.Customer(seed, customerB, tenantB, "A Completely Different Buyer");
            var lead = Seed.Lead(seed, leadB, tenantB, buyersName: "Buyer");
            lead.CustomerBuyerEmailExtracted = "57322@se.com.sa";
            lead.CustomerCompanyNameExtracted = "Saudi Electricity Company";
            await seed.SaveChangesAsync();
            seed.Set<CustomerIdentifier>().AddRange(
                new CustomerIdentifier
                {
                    BusinessUnitId = tenantA, CustomerId = customerA,
                    IdentifierType = CustomerIdentifierType.Email,
                    NormalizedValue = "57322@se.com.sa", DisplayValue = "57322@se.com.sa",
                    IsVerified = true, Confidence = 1m, Source = "CustomerProfile",
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                },
                new CustomerIdentifier
                {
                    BusinessUnitId = tenantA, CustomerId = customerA,
                    IdentifierType = CustomerIdentifierType.Alias,
                    NormalizedValue = CustomerNameNormalizer.LooseKey("Saudi Electricity Company"),
                    DisplayValue = "Saudi Electricity Company",
                    IsVerified = true, Confidence = 0.9m,
                    Source = CustomerIdentifierSources.LeadReviewLearned,
                    EffectiveFrom = DateTime.UtcNow.AddDays(-1)
                });
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenantB);
        var outcome = await new LeadCustomerResolutionService(context).ResolveAsync(tenantB, leadB);

        Assert.Null(outcome.CustomerId);
        Assert.Equal(LeadCustomerMatchStatuses.Unresolved, outcome.Status);
        Assert.Equal(CustomerMatchReasonCodes.NoMatch, outcome.ReasonCode);
    }
}
