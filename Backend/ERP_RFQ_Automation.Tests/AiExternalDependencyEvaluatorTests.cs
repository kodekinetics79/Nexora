using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class AiExternalDependencyEvaluatorTests
{
    [Fact]
    public async Task Query_excludes_denials_and_takes_latest_100_governed_calls()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new ErpRfqAutomationContext(
            new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        Seed.EnsureBusinessUnit(db, 7);
        var now = DateTime.UtcNow;
        for (var i = 0; i < 101; i++)
            db.AiRequests.Add(Request(i == 100 ? AiProviderClass.External : AiProviderClass.Local,
                now.AddMinutes(-i), AiCallStatuses.Succeeded));
        db.AiRequests.Add(Request(AiProviderClass.External, now.AddMinutes(1), AiCallStatuses.Denied));
        await db.SaveChangesAsync();

        var snapshot = await AiExternalDependencyEvaluator.EvaluateAsync(
            db.AiRequests, 7, 10m, CancellationToken.None);

        Assert.Equal(100, snapshot.Total);
        Assert.Equal(0, snapshot.External); // the 101st/oldest governed call is outside the window
    }

    [Fact]
    public void Uses_bounded_non_denied_window_configured_ceiling_and_authorization_receipts()
    {
        var calls = Enumerable.Range(0, 100)
            .Select(i => Call(i < 5 ? AiProviderClass.External : AiProviderClass.Local))
            .ToList();
        calls[0] = Call(AiProviderClass.External, authorizationId: 41);
        var snapshot = AiExternalDependencyEvaluator.Evaluate(calls, ceilingPercent: 3m);

        Assert.Equal(100, snapshot.Total);
        Assert.Equal(5, snapshot.External);
        Assert.Equal(1, snapshot.AuthorizedExternal);
        Assert.Equal(4m, snapshot.ExternalSharePercent);
        Assert.Equal(3m, snapshot.CeilingPercent);
        Assert.True(snapshot.CeilingBreached);
    }

    [Fact]
    public void Authorized_external_only_does_not_breach_zero_percent_ceiling()
    {
        var snapshot = AiExternalDependencyEvaluator.Evaluate(
            [Call(AiProviderClass.External, AiCallStatuses.Running, 9)], 0m);

        Assert.Equal(0m, snapshot.ExternalSharePercent);
        Assert.Equal(1, snapshot.Unresolved);
        Assert.False(snapshot.CeilingBreached);
    }

    private static AiExternalDependencyEvaluator.GovernedCall Call(
        AiProviderClass providerClass, string status = AiCallStatuses.Succeeded,
        long? authorizationId = null) => new(providerClass, authorizationId, status);

    private static AiRequest Request(AiProviderClass providerClass, DateTime createdOn, string status) => new()
    {
        Id = Guid.NewGuid(), BusinessUnitId = 7, Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"), PromptHash = new string('A', 64),
        PromptVersion = "v1", Provider = providerClass.ToString(), ProviderClass = providerClass,
        Model = "test", Status = status, CreatedOn = createdOn
    };
}
