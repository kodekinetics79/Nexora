using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The AI processing policy is a governed row: Owner authority, a second factor, a written reason
/// and an optimistic-concurrency check, through
/// <c>PUT /api/platform/tenants/{id}/ai-policy</c>.
///
/// <para>It had a back door. Every evaluation — the path every document runs through, and
/// <c>GetAsync</c>, so merely opening the tenant's AI tab — first called an auto-provisioner that
/// set <c>IsEnabled</c>, <c>ExternalProcessingAllowed</c> and <c>EgressPolicy</c>, overwrote the
/// purpose list, and minted a never-expiring grant authored by <c>AuthorizedByUserId = 0</c>. No
/// audit row. No <c>Version</c> increment — so the Owner's next write still matched on the stale
/// version and recorded the machine's values in its own before-snapshot. An Owner who switched AI
/// off had it switched back on by the next document, and the audit trail showed only the switching
/// off.</para>
///
/// <para>It was on by default and NO test covered it: every existing test constructed the service
/// without configuration, which turned it off, so the suite asserted the safe behaviour while the
/// deployed service did the unsafe one. These tests are constructed WITH configuration, the way
/// the composed application resolves it, precisely so they would have failed.</para>
/// </summary>
public sealed class AiPolicyHasOneWriterTests
{
    private const long TenantId = 94_101;
    private const string Endpoint = "https://ollama.com";

    [Fact]
    public async Task Evaluating_a_document_never_writes_to_the_governed_policy_row()
    {
        using var harness = new Harness();
        // An Owner turned AI off through the front door. That is the kill switch.
        harness.Mutate(policy =>
        {
            policy.IsEnabled = false;
            policy.ExternalProcessingAllowed = false;
            policy.EgressPolicy = AiEgressPolicies.RedactedFieldsOnly;
            policy.AllowedPurposes = AiPurposes.RfqExtraction;
        });
        var before = await harness.Snapshot();

        var decision = await harness.Trust.EvaluateAsync(
            TenantId, harness.Provider, AiPurposes.RfqExtraction, unstructuredPayload: true, default);

        Assert.False(decision.Allowed);
        Assert.Equal(before, await harness.Snapshot());
    }

    [Fact]
    public async Task Reading_the_trust_centre_never_writes_to_the_governed_policy_row()
    {
        // GetAsync calls the same chain. Opening a tenant's AI tab is a read.
        using var harness = new Harness();
        harness.Mutate(policy => policy.IsEnabled = false);
        var before = await harness.Snapshot();

        await harness.Trust.GetAsync(TenantId, default);

        Assert.Equal(before, await harness.Snapshot());
    }

    [Fact]
    public async Task Evaluating_a_document_never_mints_an_egress_grant()
    {
        // The grant is the artefact a customer is shown to prove who authorised sending their
        // documents to an external provider. It is only ever authored by a named Owner.
        using var harness = new Harness();

        await harness.Trust.EvaluateAsync(
            TenantId, harness.Provider, AiPurposes.RfqExtraction, unstructuredPayload: true, default);

        await using var read = harness.Database.ContextFor(null);
        Assert.Empty(await read.AiExternalProviderAuthorizations.IgnoreQueryFilters().ToListAsync());
    }

    private sealed class Harness : IDisposable
    {
        private readonly ErpRfqAutomationContext _db;

        public TestDb Database { get; } = new();
        public AiExternalProviderTrustService Trust { get; }
        public AiProviderDescriptor Provider { get; }

        public Harness()
        {
            using (var seed = Database.ContextFor(null))
            {
                Seed.EnsureBusinessUnit(seed, TenantId);
                seed.AiProcessingPolicies.Add(
                    AiProcessingPolicy.CreateSecureDefault(TenantId, "tenant-provisioning", DateTime.UtcNow));
                seed.SaveChanges();
            }

            // Configuration PRESENT, as the composed application always supplies it. The old
            // auto-provisioner was on by default in exactly this shape, and off in every test
            // that omitted configuration — which was all of them.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ollama:BaseUrl"] = Endpoint + "/",
                ["Ollama:Model"] = "deepseek-v4-pro"
            }).Build();
            var resolver = new AiProviderEndpointResolver(
                configuration, new NoopLogger<AiProviderEndpointResolver>());
            Provider = resolver.Current;

            _db = Database.ContextFor(TenantId);
            Trust = new AiExternalProviderTrustService(
                _db, new StubTenant(TenantId), resolver,
                new NoopLogger<AiExternalProviderTrustService>(), configuration);
        }

        public void Mutate(Action<AiProcessingPolicy> change)
        {
            using var context = Database.ContextFor(null);
            var policy = context.AiProcessingPolicies.IgnoreQueryFilters()
                .Single(x => x.BusinessUnitId == TenantId);
            change(policy);
            context.SaveChanges();
        }

        /// <summary>Every field the removed writer used to touch, plus the concurrency token.</summary>
        public async Task<string> Snapshot()
        {
            await using var read = Database.ContextFor(null);
            var p = await read.AiProcessingPolicies.IgnoreQueryFilters()
                .SingleAsync(x => x.BusinessUnitId == TenantId);
            return string.Join('|', p.IsEnabled, p.ExternalProcessingAllowed, p.EgressPolicy,
                p.AllowedPurposes, p.Version, p.UpdatedBy);
        }

        public void Dispose()
        {
            _db.Dispose();
            Database.Dispose();
        }
    }
}
