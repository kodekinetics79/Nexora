using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification of the per-tenant concurrency entitlement inside
/// the atomic claim: the effective cap for a tenant is its plan's
/// MaxConcurrentExtractionJobs (platform.Tenants → platform.Plans join in ClaimSql),
/// while a business unit without a Tenant/Plan row keeps the configured fallback cap.
///
/// Uses its OWN container with EnsureCreated (schema from the current model) instead
/// of the shared migrated fixture, so it stays runnable while the Platform Admin 360
/// increment's single merged migration is still pending (Integration Owner contract).
/// </summary>
public sealed class EntitlementClaimPostgreSqlTests : IAsyncLifetime
{
    private const long PlannedBu = 981001;
    private const long LegacyBu = 981002;
    private const int FallbackCap = 4;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_entitlement_tests")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    private DbContextOptions<ErpRfqAutomationContext> _options = null!;

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _container.StartAsync();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_container.GetConnectionString())
            .EnableDetailedErrors()
            .Options;
        await using var context = Context();
        await context.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private ErpRfqAutomationContext Context() => new(_options, new StubTenant(null));

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Claim_UsesPlanConcurrencyCap_AndConfigFallbackWithoutPlan()
    {
        await using var ctx = Context();
        var queue = new ExtractionQueue(ctx, new NoopLogger<ExtractionQueue>(), new StubTenant(null));

        var plan = new Plan
        {
            Code = "cap-test",
            Name = "Cap Test",
            MaxConcurrentExtractionJobs = 1,
            Weight = 1
        };
        ctx.Set<Plan>().Add(plan);
        ctx.Set<Tenant>().Add(new Tenant
        {
            Name = "Cap Test Tenant",
            Slug = "cap-test",
            Status = TenantStatus.Active,
            Plan = plan,
            PrimaryBusinessUnitId = PlannedBu
        });
        await ctx.SaveChangesAsync();

        foreach (var (bu, marker) in new[]
        {
            (PlannedBu, "cap-a1"), (PlannedBu, "cap-a2"),
            (LegacyBu, "cap-b1"), (LegacyBu, "cap-b2")
        })
            await EnqueueClaimableAsync(ctx, queue, bu, marker);

        var lease = TimeSpan.FromMinutes(5);

        // Plan cap = 1: exactly one in-flight job for the planned tenant, even though
        // the config fallback would allow FallbackCap.
        var first = await queue.ClaimAsync("w-1", lease, FallbackCap);
        Assert.NotNull(first);
        Assert.Equal(PlannedBu, first!.BusinessUnitId);

        // The planned tenant is now at its plan cap — the claim must skip its second
        // pending job and take the legacy tenant's work instead.
        var second = await queue.ClaimAsync("w-2", lease, FallbackCap);
        Assert.NotNull(second);
        Assert.Equal(LegacyBu, second!.BusinessUnitId);

        // Legacy BU (no Tenant/Plan row) keeps the fallback cap: both of its jobs may
        // be in flight at once.
        var third = await queue.ClaimAsync("w-3", lease, FallbackCap);
        Assert.NotNull(third);
        Assert.Equal(LegacyBu, third!.BusinessUnitId);

        // Only the planned tenant's second job remains, and its tenant is capped.
        Assert.Null(await queue.ClaimAsync("w-4", lease, FallbackCap));
    }

    private static async Task EnqueueClaimableAsync(
        ErpRfqAutomationContext context, ExtractionQueue queue, long businessUnitId, string marker)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant();
        var corpus = DocumentCorpus.Create(businessUnitId, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        await context.SaveChangesAsync();

        var source = SourceDocument.Create(businessUnitId, corpus.Id, hash, $"{marker}.pdf",
            "application/pdf", "entitlement-claim", marker, "v1", 1);
        context.Set<SourceDocument>().Add(source);
        await context.SaveChangesAsync();

        var occurrence = SourceDocumentOccurrence.Create(
            businessUnitId, source.Id, corpus.Id, $"entitlement-claim:{marker}", "{}");
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        await context.SaveChangesAsync();

        var enqueued = await queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = businessUnitId,
            SourceDocumentOccurrenceId = occurrence.Id,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = $"blob://{hash}",
            FileName = $"{marker}.pdf",
            FileType = "pdf",
            ContentHash = hash
        });
        occurrence.BindExtractionJob(enqueued.JobId);
        await context.SaveChangesAsync();
    }
}
