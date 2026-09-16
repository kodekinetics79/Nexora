using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement.Discovery;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Internet supplier discovery goes through the SAME allow-list gate as the AI provider, for the
/// same host, under its own purpose (<see cref="AiPurposes.SupplierDiscovery"/>). These tests pin
/// what that means for a tenant that already exists: a grant this code wrote for itself before the
/// purpose existed is widened to cover it; a grant a person wrote is not, so discovery stays off
/// until they add it under AI governance.
/// </summary>
public sealed class SupplierDiscoveryConsentTests
{
    private const long Tenant = 88_101;

    [Fact]
    public async Task An_auto_provisioned_grant_written_before_discovery_existed_is_widened_to_cover_it()
    {
        using var database = new TestDb();
        SeedTenantWithGrant(database, authorizedBy: AiExternalProviderTrustService.AutoProvisionActor);

        await using var db = database.ContextFor(Tenant);
        var decision = await Trust(db, autoProvision: true).EvaluateAsync(
            Tenant, Destination(), AiPurposes.SupplierDiscovery, unstructuredPayload: false, CancellationToken.None);

        Assert.True(decision.Allowed, decision.Reason);
        await using var verify = database.ContextFor(Tenant);
        var grant = await verify.AiExternalProviderAuthorizations.SingleAsync();
        Assert.True(grant.CoversPurpose(AiPurposes.SupplierDiscovery));
        Assert.True(grant.CoversPurpose(AiPurposes.RfqExtraction));
        Assert.Equal(AiExternalProviderTrustService.AutoProvisionActor, grant.AuthorizedBy);
    }

    [Fact]
    public async Task A_grant_a_person_wrote_is_left_alone_so_discovery_stays_off_until_they_add_it()
    {
        using var database = new TestDb();
        SeedTenantWithGrant(database, authorizedBy: "user:7");

        await using var db = database.ContextFor(Tenant);
        var decision = await Trust(db, autoProvision: true).EvaluateAsync(
            Tenant, Destination(), AiPurposes.SupplierDiscovery, unstructuredPayload: false, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.PurposeNotAuthorized, decision.Reason);
        await using var verify = database.ContextFor(Tenant);
        var grant = await verify.AiExternalProviderAuthorizations.SingleAsync();
        Assert.Equal("RfqExtraction,BoqDraft,Agent", grant.AllowedPurposes);
    }

    [Fact]
    public async Task The_same_human_grant_still_covers_the_purposes_it_names()
    {
        using var database = new TestDb();
        SeedTenantWithGrant(database, authorizedBy: "user:7");

        await using var db = database.ContextFor(Tenant);
        var decision = await Trust(db, autoProvision: true).EvaluateAsync(
            Tenant, Destination(), AiPurposes.RfqExtraction, unstructuredPayload: false, CancellationToken.None);

        Assert.True(decision.Allowed, decision.Reason);
    }

    [Fact]
    public async Task With_auto_provisioning_off_and_no_grant_discovery_is_refused()
    {
        using var database = new TestDb();
        using (var seed = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Tenant);
            seed.AiProcessingPolicies.Add(AiProcessingPolicy.CreateSecureDefault(Tenant, "qa", DateTime.UtcNow));
            seed.SaveChanges();
        }

        await using var db = database.ContextFor(Tenant);
        var decision = await Trust(db, autoProvision: false).EvaluateAsync(
            Tenant, Destination(), AiPurposes.SupplierDiscovery, unstructuredPayload: false, CancellationToken.None);

        Assert.False(decision.Allowed);
        Assert.Equal(AiExternalProviderTrustReasons.NotAuthorized, decision.Reason);
        await using var verify = database.ContextFor(Tenant);
        Assert.False(await verify.AiExternalProviderAuthorizations.AnyAsync());
    }

    [Fact]
    public void Supplier_discovery_is_a_purpose_an_administrator_can_grant()
    {
        // The auto-provision list and the "known purposes" validation both read the constant, so a
        // grant naming it is accepted and a fresh tenant's grant carries it.
        Assert.Equal("SupplierDiscovery", AiPurposes.SupplierDiscovery);
        Assert.Contains(AiPurposes.SupplierDiscovery,
            AiExternalProviderTrustService.AutoProvisionPurposes);
    }

    private static AiProviderDescriptor Destination()
        => AiProviderEndpoint.Describe(AiProviderEndpointResolver.OllamaProvider, SupplierDiscoveryConfiguration.Endpoint, null);

    private static void SeedTenantWithGrant(TestDb database, string authorizedBy)
    {
        using var seed = database.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Tenant);
        var now = DateTime.UtcNow;
        seed.AiProcessingPolicies.Add(AiProcessingPolicy.CreateSecureDefault(Tenant, "qa", now));
        seed.AiExternalProviderAuthorizations.Add(new AiExternalProviderAuthorization
        {
            BusinessUnitId = Tenant,
            Provider = AiProviderEndpointResolver.OllamaProvider,
            Endpoint = Destination().Endpoint,
            Model = AiProviderEndpoint.AnyModel,
            AllowedPurposes = "RfqExtraction,BoqDraft,Agent",
            UnstructuredDocumentsAllowed = true,
            Justification = "seeded",
            AuthorizedByUserId = 0,
            AuthorizedBy = authorizedBy,
            AuthorizedOn = now.AddDays(-30),
            UpdatedOn = now.AddDays(-30)
        });
        seed.SaveChanges();
    }

    private static AiExternalProviderTrustService Trust(ErpRfqAutomationContext db, bool autoProvision)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ollama:BaseUrl"] = "https://ollama.com/",
            ["Ollama:Model"] = "deepseek-v4-pro",
            [AiExternalProviderTrustService.AutoProvisionConfigKey] = autoProvision ? "true" : "false"
        }).Build();
        var resolver = new AiProviderEndpointResolver(configuration, new NoopLogger<AiProviderEndpointResolver>());
        return new AiExternalProviderTrustService(db, new StubTenant(Tenant), resolver,
            new NoopLogger<AiExternalProviderTrustService>(), configuration);
    }
}
