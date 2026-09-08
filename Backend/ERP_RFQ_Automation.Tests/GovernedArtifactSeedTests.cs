using System.Text.Json;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Tests.Support;
using Xunit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The seed shipped in <c>scripts/local/governed-artifact-seed.json</c>, checked against the
/// real contract.
///
/// <para>Taxonomy &amp; Document Skills, Integration Hub and Model &amp; Rule Lifecycle open
/// on an empty registry whose only way in is a hand-authored Definition JSON blob. The seed
/// exists so a tenant does not start from that blank dialog — which makes it operator-facing
/// input that has to be RIGHT. A definition that no longer satisfies
/// <see cref="PlatformGovernanceService.ValidateDefinition"/> should fail here, at build
/// time, and not as a 400 in front of whoever is configuring a tenant.</para>
/// </summary>
public sealed class GovernedArtifactSeedTests
{
    private sealed record SeedEntry(string ArtifactType, string ArtifactKey, string Name,
        string Description, JsonElement Definition);

    private static IReadOnlyList<SeedEntry> Seeds()
    {
        // Walk up from the test binary to the repository root; the seed is deliberately kept
        // beside the script that applies it rather than copied into test resources, so there
        // is exactly one definition of what a tenant gets.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "scripts", "local",
                   "governed-artifact-seed.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "scripts", "local", "governed-artifact-seed.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("artifacts").EnumerateArray()
            .Select(x => new SeedEntry(
                x.GetProperty("artifactType").GetString()!,
                x.GetProperty("artifactKey").GetString()!,
                x.GetProperty("name").GetString()!,
                x.GetProperty("description").GetString()!,
                x.GetProperty("definition").Clone()))
            .ToList();
    }

    [Fact]
    public void Every_seeded_definition_satisfies_the_validator_it_will_be_posted_to()
    {
        var seeds = Seeds();
        Assert.NotEmpty(seeds);
        foreach (var seed in seeds)
        {
            var type = Assert.IsType<GovernedArtifactType>(
                Enum.Parse<GovernedArtifactType>(seed.ArtifactType));
            // Throws PlatformGovernanceValidationException naming the missing property, which
            // is the whole point: the failure says which key to add.
            var normalized = PlatformGovernanceService.ValidateDefinition(
                type, seed.Definition.GetRawText());
            Assert.False(string.IsNullOrWhiteSpace(normalized));
        }
    }

    [Fact]
    public void Seeded_definitions_carry_content_rather_than_the_empty_scaffold()
    {
        // The defect the seed exists to answer. The Create dialog pre-fills a definition that
        // passes validation while saying nothing — `fieldGroups: []`, `lineSchema: []`,
        // `actions: []` — so a registry can be "configured" and still describe nothing. A seed
        // that shipped the same empty arrays would be indistinguishable from not seeding.
        foreach (var seed in Seeds())
            foreach (var property in seed.Definition.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.Array)
                    Assert.True(property.Value.GetArrayLength() > 0
                        // A connector with no webhooks is a statement, not an omission.
                        || property.Name is "webhooks",
                        $"{seed.ArtifactKey}.{property.Name} is an empty array");
    }

    [Fact]
    public void Seeded_quality_thresholds_match_the_defaults_they_replace()
    {
        // QualityMetricSet is the one seeded type running code reads
        // (QualityAnalyticsService.ThresholdsAsync). Publishing it swaps an implicit fallback
        // for a versioned, auditable decision — so it must not quietly MOVE a threshold while
        // doing so. Same numbers, now with an approval trail.
        var quality = Assert.Single(Seeds(), x => x.ArtifactType == "QualityMetricSet");
        Assert.Equal(30, quality.Definition.GetProperty("minimumSampleSize").GetInt32());
        Assert.Equal(20m, quality.Definition.GetProperty("reviewRateWarningPercent").GetDecimal());
        Assert.Equal(10m, quality.Definition.GetProperty("externalDependencyCeilingPercent").GetDecimal());
        Assert.Equal(15m, quality.Definition.GetProperty("turnaroundP95WarningMinutes").GetDecimal());
    }

    [Fact]
    public async Task Seed_creates_and_promotes_to_production_through_the_real_service()
    {
        const long tenant = 73_401;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        await context.SaveChangesAsync();
        var service = new PlatformGovernanceService(context);

        foreach (var seed in Seeds())
        {
            var type = Enum.Parse<GovernedArtifactType>(seed.ArtifactType);
            var created = await service.CreateAsync(tenant, 91, $"seed-create-{seed.ArtifactKey}",
                new(type, seed.ArtifactKey, seed.Name, seed.Description,
                    seed.Definition.GetRawText(), "Seeded first governed version"), default);
            Assert.Equal(GovernedLifecycleStatus.Draft, created.Artifact.Status);

            var tested = await service.TransitionAsync(tenant, created.Artifact.Id, 91,
                $"seed-test-{seed.ArtifactKey}",
                new(created.Artifact.Version, "TEST", "Seeded promotion: TEST"), default);
            Assert.Equal(GovernedLifecycleStatus.Test, tested.Artifact.Status);

            var published = await service.TransitionAsync(tenant, created.Artifact.Id, 91,
                $"seed-publish-{seed.ArtifactKey}",
                new(tested.Artifact.Version, "PUBLISH", "Seeded promotion: PUBLISH"), default);
            Assert.Equal(GovernedLifecycleStatus.Production, published.Artifact.Status);
            Assert.Equal(1, published.Artifact.ProductionVersionNumber);
        }

        // Re-running the seed must not duplicate or overwrite: the second create is refused.
        var first = Seeds()[0];
        await Assert.ThrowsAsync<PlatformGovernanceConflictException>(() =>
            service.CreateAsync(tenant, 91, "seed-create-again",
                new(Enum.Parse<GovernedArtifactType>(first.ArtifactType), first.ArtifactKey,
                    first.Name, first.Description, first.Definition.GetRawText(), "Again"), default));
    }
}
