using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using Microsoft.EntityFrameworkCore;
using System.Reflection;

namespace ERP_RFQ_Automation.Tests;

public sealed class Wave1PlatformGovernanceTests
{
    [Theory]
    [InlineData(nameof(PlatformGovernanceController.SearchArchive), PermissionAction.View)]
    [InlineData(nameof(PlatformGovernanceController.GovernArchiveDocument), PermissionAction.Edit)]
    [InlineData(nameof(PlatformGovernanceController.GetQualityAnalytics), PermissionAction.View)]
    public void Archive_and_quality_http_routes_require_tenant_admin_permission(string actionName,
        PermissionAction expectedAction)
    {
        var method = typeof(PlatformGovernanceController).GetMethod(actionName)
            ?? throw new InvalidOperationException($"Missing action {actionName}.");
        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>());
        Assert.Equal("Users", permission.ModuleName);
        Assert.Equal(expectedAction, permission.Action);
    }

    [Fact]
    public async Task Archive_policy_is_versioned_tested_published_and_rollback_ready()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(60_991);
        Seed.BusinessUnit(context, 60_991);
        await context.SaveChangesAsync();
        var service = new PlatformGovernanceService(context);
        var created = await service.CreateAsync(60_991, 9, "archive-policy-create",
            new(GovernedArtifactType.ArchivePolicy, "commercial-evidence", "Commercial evidence",
                "Retention and legal hold controls",
                "{\"retentionDays\":2555,\"legalHoldEnabled\":true,\"exportApprovalRequired\":true,\"deletionApprovalRequired\":true,\"evidenceAccessAuditRequired\":true}",
                "Initial archive policy"), default);
        var tested = await service.TransitionAsync(60_991, created.Artifact.Id, 9, "archive-policy-test",
            new(created.Artifact.Version, "TEST", "Policy controls verified"), default);
        var published = await service.TransitionAsync(60_991, created.Artifact.Id, 9, "archive-policy-publish",
            new(tested.Artifact.Version, "PUBLISH", "Compliance approval recorded"), default);

        Assert.Equal(GovernedLifecycleStatus.Production, published.Artifact.Status);
        Assert.Equal(1, published.Artifact.ProductionVersionNumber);
    }

    [Fact]
    public async Task Quality_metric_definition_requires_explicit_reconciliation_thresholds()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(60_992);
        Seed.BusinessUnit(context, 60_992);
        await context.SaveChangesAsync();
        var service = new PlatformGovernanceService(context);
        var created = await service.CreateAsync(60_992, 9, "quality-metrics-create",
            new(GovernedArtifactType.QualityMetricSet, "document-quality", "Document quality",
                "Reconciled quality cohort definitions",
                "{\"windowDays\":30,\"minimumSampleSize\":30,\"reviewRateWarningPercent\":20,\"externalDependencyCeilingPercent\":10,\"turnaroundP95WarningMinutes\":15}",
                "Initial measured definitions"), default);

        Assert.Equal(GovernedLifecycleStatus.Draft, created.Artifact.Status);
        await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() => service.CreateAsync(
            60_992, 9, "quality-metrics-invalid", new(GovernedArtifactType.QualityMetricSet,
                "invalid-quality", "Invalid quality", "Missing denominators", "{\"windowDays\":30}",
                "Invalid"), default));
    }

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
        Assert.Contains(await context.TenantGovernanceAuditEvents.ToListAsync(),
            x => x.Action == "WORKFLOW_RESUME_REQUESTED" && x.AggregateReference == "NXR-TEST-1");
        await Assert.ThrowsAsync<PlatformGovernanceConflictException>(() => actions.TransitionAsync(
            61_021, created.Item.Id, 30, "action-reopen",
            new(completed.Item.Version, HumanActionStatus.InReview, "REOPEN", "Try to reopen."), default));
    }

    [Fact]
    public async Task Human_action_bulk_decision_is_atomic_idempotent_and_audited()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(61_031);
        Seed.BusinessUnit(context, 61_031);
        await context.SaveChangesAsync();
        var service = new HumanActionService(context);
        var due = DateTime.UtcNow.AddHours(2);
        var first = await service.CreateAsync(61_031, 40, "bulk-create-1",
            new("Extraction", "Lead", "NXR-BULK-1", "Review customer", "Ambiguous customer.",
                "Select verified customer.", "{\"field\":\"customer\"}", .7m, "RFQ blocked.",
                "RESUME_EXTRACTION", HumanActionPriority.High, 40, due), default);
        var second = await service.CreateAsync(61_031, 40, "bulk-create-2",
            new("Extraction", "Lead", "NXR-BULK-2", "Review contact", "Ambiguous contact.",
                "Select verified contact.", "{\"field\":\"contact\"}", .75m, "RFQ blocked.",
                "RESUME_EXTRACTION", HumanActionPriority.High, 40, due), default);
        var command = new BulkTransitionHumanActionCommand(
            [new(first.Item.Id, first.Item.Version), new(second.Item.Id, second.Item.Version)],
            HumanActionStatus.Completed, "APPROVE", "Verified against customer master.");

        var completed = await service.BulkTransitionAsync(61_031, 40, "bulk-approve", command, default);
        var replay = await service.BulkTransitionAsync(61_031, 40, "bulk-approve", command, default);

        Assert.All(completed.Items, x => Assert.Equal(HumanActionStatus.Completed, x.Status));
        Assert.True(replay.IdempotentReplay);
        Assert.Equal(2, await context.TenantGovernanceAuditEvents.CountAsync(
            x => x.Action == "WORKFLOW_RESUME_REQUESTED"));
        Assert.Equal(4, await context.HumanActionEvents.CountAsync());
    }

    [Theory]
    [InlineData(GovernedArtifactType.Model, "{\"modelKind\":\"Local\"}")]
    [InlineData(GovernedArtifactType.Rule, "{\"condition\":\"confidence < .8\"}")]
    [InlineData(GovernedArtifactType.Dataset, "{\"purpose\":\"Evaluation\"}")]
    [InlineData(GovernedArtifactType.Model, "{\"modelKind\":\"External\",\"purpose\":\"Extraction\",\"evaluationDatasetKey\":\"rfq-eval\",\"external\":true,\"apiKey\":\"plain\"}")]
    public async Task Lifecycle_artifact_contracts_reject_incomplete_or_secret_bearing_definitions(
        GovernedArtifactType type, string definition)
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(61_041);
        Seed.BusinessUnit(context, 61_041);
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
            new PlatformGovernanceService(context).CreateAsync(61_041, 50, Guid.NewGuid().ToString("N"),
                new(type, $"test-{type}", $"Test {type}", "Contract test", definition, "Initial"), default));
    }

    [Fact]
    public async Task Connector_contract_is_versioned_and_uses_secret_references_only()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(61_051);
        Seed.BusinessUnit(context, 61_051);
        await context.SaveChangesAsync();
        var definition = """
            {"connectorType":"REST","contractVersion":"1.0","authMode":"OAuth2",
             "credentialReference":"vault:nexora/rest","actions":["InventoryRead"],
             "eventTriggers":[],"webhooks":[],"polling":{"enabled":true,"minutes":15},
             "fieldMappings":[],"idempotency":{"required":true},
             "retryPolicy":{"maxAttempts":3},"deadLetterPolicy":{"retentionDays":30},
             "rateLimit":{"requestsPerMinute":60},"health":{"freshnessMinutes":30},
             "sandbox":true}
            """;
        var created = await new PlatformGovernanceService(context).CreateAsync(61_051, 60,
            "connector-create", new(GovernedArtifactType.Connector, "erp-inventory",
                "ERP Inventory", "Inventory read connector", definition, "Initial sandbox contract"), default);

        Assert.Equal(GovernedLifecycleStatus.Draft, created.Artifact.Status);
        Assert.Equal(1, created.Artifact.CurrentVersionNumber);
    }

    [Fact]
    public async Task Release_candidate_requires_every_referenced_test_suite_to_be_published()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(61_061);
        Seed.BusinessUnit(context, 61_061);
        await context.SaveChangesAsync();
        var artifacts = new PlatformGovernanceService(context);
        var suite = await artifacts.CreateAsync(61_061, 70, "suite-create",
            new(GovernedArtifactType.TestSuite, "wave1-contracts", "Wave 1 contracts",
                "Contract test suite",
                "{\"requirements\":[\"W1-SEC-01\"],\"tests\":[{\"name\":\"tenant contract\",\"actual\":true,\"expected\":true}],\"environment\":\"Sandbox\",\"passThreshold\":1}",
                "Initial suite"), default);
        var release = await artifacts.CreateAsync(61_061, 70, "release-create",
            new(GovernedArtifactType.ReleaseCandidate, "wave1-rc", "Wave 1 RC",
                "Wave 1 release candidate",
                "{\"releaseVersion\":\"1.0.0\",\"requirements\":[\"W1-SEC-01\"],\"testSuiteKeys\":[\"wave1-contracts\"],\"rollbackArtifactVersion\":\"0.9.0\"}",
                "Initial release candidate"), default);
        var releaseTest = await artifacts.TransitionAsync(61_061, release.Artifact.Id, 70, "release-test",
            new(release.Artifact.Version, "TEST", "Candidate assembled"), default);

        await Assert.ThrowsAsync<PlatformGovernanceConflictException>(() => artifacts.TransitionAsync(
            61_061, release.Artifact.Id, 70, "release-publish-early",
            new(releaseTest.Artifact.Version, "PUBLISH", "Attempt before the suite is published"), default));

        var suiteTest = await artifacts.TransitionAsync(61_061, suite.Artifact.Id, 70, "suite-test",
            new(suite.Artifact.Version, "TEST", "Suite definition reviewed"), default);
        await artifacts.TransitionAsync(61_061, suite.Artifact.Id, 70, "suite-publish",
            new(suiteTest.Artifact.Version, "PUBLISH", "Suite approved"), default);
        var published = await artifacts.TransitionAsync(61_061, release.Artifact.Id, 70,
            "release-publish", new(releaseTest.Artifact.Version, "PUBLISH", "Referenced suites published"), default);

        Assert.Equal(GovernedLifecycleStatus.Production, published.Artifact.Status);
    }
}
