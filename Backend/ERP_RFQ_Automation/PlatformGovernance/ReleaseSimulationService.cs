using System.Data;
using System.Text.Json;
using System.Text.Json.Nodes;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.PlatformGovernance;

public sealed record SimulationTestResult(string Name, bool Passed, string? Difference);
public sealed record ReleaseSimulationResult(long TestSuiteId, int VersionNumber, int Total,
    int Passed, int Failed, decimal PassRate, decimal PassThreshold, bool Succeeded,
    IReadOnlyList<SimulationTestResult> Tests, DateTime CompletedOn, bool IdempotentReplay);

public sealed class ReleaseSimulationService(ErpRfqAutomationContext db)
{
    public async Task<ReleaseSimulationResult> RunAsync(long tenantId, long actorUserId,
        long testSuiteId, string idempotencyKey, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var replay = await db.TenantGovernanceAuditEvents.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == tenantId && x.IdempotencyKey == idempotencyKey, ct);
            if (replay is not null)
            {
                var prior = JsonSerializer.Deserialize<ReleaseSimulationResult>(replay.EvidenceJson)
                    ?? throw new PlatformGovernanceValidationException("The prior simulation result is invalid.");
                return prior with { IdempotentReplay = true };
            }
            var suite = await db.GovernedArtifacts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.BusinessUnitId == tenantId && x.Id == testSuiteId
                    && x.ArtifactType == GovernedArtifactType.TestSuite, ct)
                ?? throw new PlatformGovernanceNotFoundException("The test suite was not found.");
            if (suite.Status == GovernedLifecycleStatus.Archived)
                throw new PlatformGovernanceConflictException("Archived test suites cannot run.");
            var version = await db.GovernedArtifactVersions.AsNoTracking().SingleAsync(x =>
                x.BusinessUnitId == tenantId && x.GovernedArtifactId == suite.Id
                    && x.VersionNumber == suite.CurrentVersionNumber, ct);
            using var definition = JsonDocument.Parse(version.DefinitionJson);
            var root = definition.RootElement;
            var tests = root.GetProperty("tests");
            if (tests.ValueKind != JsonValueKind.Array || tests.GetArrayLength() is < 1 or > 100)
                throw new PlatformGovernanceValidationException("A simulation requires between one and 100 tests.");
            var threshold = root.GetProperty("passThreshold").GetDecimal();
            if (threshold is < 0 or > 1)
                throw new PlatformGovernanceValidationException("Pass threshold must be between zero and one.");
            var results = new List<SimulationTestResult>();
            foreach (var test in tests.EnumerateArray())
            {
                if (!test.TryGetProperty("name", out var nameElement)
                    || string.IsNullOrWhiteSpace(nameElement.GetString())
                    || !test.TryGetProperty("actual", out var actual)
                    || !test.TryGetProperty("expected", out var expected))
                    throw new PlatformGovernanceValidationException(
                        "Each test requires a name, deterministic actual value and expected value.");
                var passed = JsonNode.DeepEquals(JsonNode.Parse(actual.GetRawText()),
                    JsonNode.Parse(expected.GetRawText()));
                results.Add(new(nameElement.GetString()!.Trim(), passed,
                    passed ? null : $"Expected {expected.GetRawText()}; received {actual.GetRawText()}"));
            }
            var passedCount = results.Count(x => x.Passed);
            var rate = decimal.Round(passedCount / (decimal)results.Count, 4);
            var now = DateTime.UtcNow;
            var result = new ReleaseSimulationResult(suite.Id, suite.CurrentVersionNumber,
                results.Count, passedCount, results.Count - passedCount, rate, threshold,
                rate >= threshold, results, now, false);
            db.TenantGovernanceAuditEvents.Add(new()
            {
                BusinessUnitId = tenantId,
                Area = "ReleaseCenter",
                AggregateType = "TestSuite",
                AggregateReference = $"{suite.Id}:v{suite.CurrentVersionNumber}",
                Action = result.Succeeded ? "SIMULATION_PASSED" : "SIMULATION_FAILED",
                Reason = $"{passedCount}/{results.Count} deterministic tests passed",
                EvidenceJson = JsonSerializer.Serialize(result),
                IdempotencyKey = idempotencyKey,
                ActorUserId = actorUserId,
                OccurredOn = now
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return result;
        });
    }
}
