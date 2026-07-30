using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

public sealed class Wave1PlatformGovernanceTests
{
    [Fact]
    public async Task Artifact_versions_promote_and_rollback_with_append_only_history()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(61_001);
        Seed.BusinessUnit(context, 61_001);
        await context.SaveChangesAsync();
        var service = new PlatformGovernanceService(context);

        var created = await service.CreateAsync(61_001, 10, "taxonomy-create",
            new(GovernedArtifactType.CommercialTaxonomy, "customer-rfq", "Customer RFQ",
                "Canonical customer inquiry taxonomy",
                "{\"documentType\":\"CustomerRfq\",\"fields\":[\"rfqNumber\"]}",
                "Initial taxonomy"), default);
        var tested = await service.TransitionAsync(61_001, created.Artifact.Id, 10, "taxonomy-test",
            new(created.Artifact.Version, "TEST", "Validation corpus passed"), default);
        var published = await service.TransitionAsync(61_001, created.Artifact.Id, 10, "taxonomy-publish",
            new(tested.Artifact.Version, "PUBLISH", "Approved for production"), default);
        var next = await service.CreateVersionAsync(61_001, created.Artifact.Id, 10, "taxonomy-v2",
            new(published.Artifact.Version,
                "{\"documentType\":\"CustomerRfq\",\"fields\":[\"rfqNumber\",\"buyer\"]}",
                "Add buyer field"), default);
        var v2Test = await service.TransitionAsync(61_001, created.Artifact.Id, 10, "taxonomy-v2-test",
            new(next.Artifact.Version, "TEST", "Version two corpus passed"), default);
        var v2Published = await service.TransitionAsync(61_001, created.Artifact.Id, 10, "taxonomy-v2-publish",
            new(v2Test.Artifact.Version, "PUBLISH", "Publish version two"), default);
        var rolledBack = await service.TransitionAsync(61_001, created.Artifact.Id, 10, "taxonomy-rollback",
            new(v2Published.Artifact.Version, "ROLLBACK", "Regression detected", 1), default);

        Assert.Equal(1, rolledBack.Artifact.ProductionVersionNumber);
        Assert.Equal(GovernedLifecycleStatus.Production, rolledBack.Artifact.Status);
        var detail = await service.GetAsync(61_001, created.Artifact.Id, default);
        Assert.Equal(7, detail.Events.Count);
        Assert.Equal(2, detail.Versions.Count);
        Assert.Contains(detail.Events, x => x.Action == "ROLLBACK");
    }

    [Fact]
    public async Task Artifact_idempotency_and_query_filter_prevent_duplicate_or_cross_tenant_results()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 61_011);
            Seed.BusinessUnit(seed, 61_012);
            await seed.SaveChangesAsync();
        }
        await using (var tenantA = database.ContextFor(61_011))
        {
            var service = new PlatformGovernanceService(tenantA);
            var command = new CreateGovernedArtifactCommand(GovernedArtifactType.DocumentSkill,
                "customer-rfq-native", "Customer RFQ Native", "Native parser skill",
                "{\"taxonomyKey\":\"customer-rfq\",\"processingStrategy\":\"NativeParser\"}",
                "Initial skill");
            var first = await service.CreateAsync(61_011, 20, "skill-create", command, default);
            var replay = await service.CreateAsync(61_011, 20, "skill-create", command, default);
            Assert.Equal(first.Artifact.Id, replay.Artifact.Id);
            Assert.True(replay.IdempotentReplay);
        }
        await using var tenantB = database.ContextFor(61_012);
        Assert.Empty(await new PlatformGovernanceService(tenantB)
            .ListAsync(61_012, null, null, default));
    }

    [Fact]
    public async Task Connector_rejects_embedded_secrets_and_human_decisions_are_terminal()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(61_021);
        Seed.BusinessUnit(context, 61_021);
        await context.SaveChangesAsync();
        var artifacts = new PlatformGovernanceService(context);
        await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() => artifacts.CreateAsync(
            61_021, 30, "unsafe-connector",
            new(GovernedArtifactType.Connector, "unsafe", "Unsafe", "Unsafe connector",
                "{\"connectorType\":\"REST\",\"apiKey\":\"plaintext\"}", "Unsafe"), default));

        var actions = new HumanActionService(context);
        var created = await actions.CreateAsync(61_021, 30, "action-create",
            new("ProductMatch", "Lead", "NXR-TEST-1", "Confirm product match",
                "One line has an ambiguous manufacturer part number.", "Review the two candidates.",
                "{\"candidateCount\":2}", .72m, "Quote preparation is blocked.",
                "RESUME_PRODUCT_RESOLUTION", HumanActionPriority.High, 30,
                DateTime.UtcNow.AddHours(2)), default);
        var completed = await actions.TransitionAsync(61_021, created.Item.Id, 30, "action-complete",
            new(created.Item.Version, HumanActionStatus.Completed, "APPROVE", "Candidate one verified."),
            default);
        Assert.Equal(HumanActionStatus.Completed, completed.Item.Status);
        await Assert.ThrowsAsync<PlatformGovernanceConflictException>(() => actions.TransitionAsync(
            61_021, created.Item.Id, 30, "action-reopen",
            new(completed.Item.Version, HumanActionStatus.InReview, "REOPEN", "Try to reopen."), default));
    }
}
