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
        var suffix = Random.Shared.Next(100_000, 149_999);
        var tenant = 9_310_000L + suffix;
        var customerId = 9_320_000L + suffix;
        var leadId = 9_330_000L + suffix;

        await using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, tenant);
            Seed.Customer(seed, customerId, tenant, "Saudi Electricity Company");
            var lead = Seed.Lead(seed, leadId, tenant, buyersName: "Buyer");
            lead.CustomerCompanyNameExtracted = "Saudi Electricity Co.";
            await seed.SaveChangesAsync();
        }

        await using var context = database.ContextFor(tenant);
        var service = new LeadCustomerResolutionService(context);
        var first = await service.ResolveAsync(tenant, leadId);
        var second = await service.ResolveAsync(tenant, leadId);

        Assert.Equal(LeadCustomerMatchStatuses.Suggested, first.Status);
        Assert.Equal(first.Status, second.Status);
        await using var verify = database.ContextFor(tenant);
        Assert.Single(await verify.Set<LeadCustomerMatchCandidate>().Where(c => c.LeadId == leadId).ToListAsync());
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
