using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class AiGovernanceLedgerTests
{
    [Fact]
    public async Task TenantFilter_HidesOtherTenantAiAccountingRows()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 501);
            Seed.EnsureBusinessUnit(seed, 502);
            seed.AiRequests.Add(Request(501, "same-operation"));
            seed.AiRequests.Add(Request(502, "same-operation"));
            await seed.SaveChangesAsync();
        }

        using var tenant = db.ContextFor(501);
        var visible = await tenant.AiRequests.SingleAsync();
        Assert.Equal(501, visible.BusinessUnitId);
    }

    [Fact]
    public async Task IdempotencyKey_IsUniqueWithinBusinessUnit()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, 503);
        context.AiRequests.Add(Request(503, "extract:1"));
        context.AiRequests.Add(Request(503, "extract:1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task Attempt_CannotClaimRequestFromAnotherTenant()
    {
        using var db = new TestDb();
        Guid requestId;
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, 504);
            Seed.EnsureBusinessUnit(seed, 505);
            var request = Request(504, "extract:2");
            requestId = request.Id;
            seed.AiRequests.Add(request);
            await seed.SaveChangesAsync();
        }

        using var context = db.ContextFor(null);
        context.AiCallAttempts.Add(new AiCallAttempt
        {
            RequestId = requestId,
            BusinessUnitId = 505,
            AttemptNumber = 1,
            Provider = "Ollama",
            Model = "test",
            Status = AiCallStatuses.Succeeded,
            TokenSource = AiTokenSources.Estimated,
            StartedOn = DateTime.UtcNow,
            CompletedOn = DateTime.UtcNow
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    private static AiRequest Request(long businessUnitId, string key) => new()
    {
        Id = Guid.NewGuid(),
        BusinessUnitId = businessUnitId,
        Operation = "RfqExtraction",
        IdempotencyKey = key,
        PromptHash = new string('a', 64),
        PromptVersion = "rfq-v1",
        Provider = "Ollama",
        Model = "test",
        Status = AiCallStatuses.Reserved,
        TokenSource = AiTokenSources.Estimated,
        CreatedOn = DateTime.UtcNow
    };
}
