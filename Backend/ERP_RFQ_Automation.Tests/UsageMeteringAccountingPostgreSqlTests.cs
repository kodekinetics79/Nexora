using ERP_RFQ_Automation.Billing.Accounting;
using ERP_RFQ_Automation.Billing.Metering;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

public sealed class UsageMeteringAccountingPostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("wave6_metering").WithUsername("nexora").WithPassword("nexora-tests").Build();
    private DbContextOptions<ErpRfqAutomationContext> _options = null!;

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await EnsureSchemaAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_duplicate_usage_is_one_event_and_one_minute_projection()
    {
        await EnsureSchemaAsync();
        var tenantId = Random.Shared.NextInt64(900_000, 990_000);
        var id = Guid.NewGuid();
        var key = $"pg-usage-{Guid.NewGuid():N}";
        var occurred = DateTime.UtcNow.AddMinutes(-2);
        var request = new RecordUsageEvent(id, tenantId, "documents", 7, "document", occurred,
            "extraction-job", "job-1", "extraction-worker", null, null, null, key, key,
            0.1m, "USD", new string('a', 64), null, null, null, null, 2, null);

        var ids = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RecordAsync(request)));
        Assert.All(ids, value => Assert.Equal(id, value));
        await using var verify = Context();
        Assert.Equal(1, await verify.Set<UsageEvent>().CountAsync(x => x.TenantId == tenantId));
        var bucket = await verify.Set<UsageMinuteAggregate>().AsNoTracking().SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal(7, bucket.Quantity);
        Assert.Equal(1, bucket.EventCount);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_workers_claim_disjoint_outbox_rows_then_ack_and_poison_are_visible()
    {
        await EnsureSchemaAsync();
        var prefix = $"pg-outbox-{Guid.NewGuid():N}";
        await using (var seed = Context())
        {
            for (var index = 0; index < 10; index++)
                seed.Add(Message($"{prefix}-{index}", maxAttempts: index == 9 ? 1 : 8));
            await seed.SaveChangesAsync();
        }

        var claims = await Task.WhenAll(ClaimAsync("worker-a"), ClaimAsync("worker-b"));
        var all = claims.SelectMany(x => x).ToList();
        Assert.Equal(10, all.Count);
        Assert.Equal(10, all.Select(x => x.Id).Distinct().Count());

        var acknowledged = all[0];
        await using (var ackDb = Context())
            await new AccountingOutboxService(ackDb).AcknowledgeAsync(
                acknowledged.Id, acknowledged.LeaseToken!.Value, "ERP-PG-1", new string('b', 64), "worker-a");
        var poison = all.Single(x => x.MaxAttempts == 1);
        await using (var failDb = Context())
            await new AccountingOutboxService(failDb).FailAsync(
                poison.Id, poison.LeaseToken!.Value, "ERP_TIMEOUT", TimeSpan.Zero);

        await using var verify = Context();
        Assert.Equal(AccountingOutboxStatus.Acknowledged,
            (await verify.Set<AccountingOutboxMessage>().AsNoTracking().SingleAsync(x => x.Id == acknowledged.Id)).Status);
        Assert.Equal(AccountingOutboxStatus.Poison,
            (await verify.Set<AccountingOutboxMessage>().AsNoTracking().SingleAsync(x => x.Id == poison.Id)).Status);
    }

    private async Task<Guid> RecordAsync(RecordUsageEvent request)
    {
        await using var db = Context();
        return (await new UsageMeteringService(db).RecordAsync(request)).UsageEventId;
    }

    private async Task<IReadOnlyList<AccountingOutboxMessage>> ClaimAsync(string worker)
    {
        await using var db = Context();
        return await new AccountingOutboxService(db).ClaimAsync(worker, 10);
    }

    private static AccountingOutboxMessage Message(string key, int maxAttempts) => new()
    {
        Id = Guid.NewGuid(), TenantId = 700, SubscriptionInvoiceId = Random.Shared.NextInt64(1_000_000, 2_000_000),
        MessageType = "subscription-invoice.finalized", IdempotencyKey = key, PayloadJson = "{}",
        PayloadSha256 = new string('c', 64), Status = AccountingOutboxStatus.Pending,
        ReconciliationStatus = AccountingReconciliationStatus.NotSent, MaxAttempts = maxAttempts,
        CreatedAtUtc = DateTime.UtcNow, AvailableAtUtc = DateTime.UtcNow.AddSeconds(-1)
    };

    private async Task EnsureSchemaAsync()
    {
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE SCHEMA IF NOT EXISTS platform;
            CREATE TABLE IF NOT EXISTS platform."UsageEvents" (
              "UsageEventId" uuid PRIMARY KEY, "TenantId" bigint NOT NULL, "Kind" varchar(16) NOT NULL,
              "EventType" varchar(64) NOT NULL, "Quantity" numeric(20,6) NOT NULL, "Unit" varchar(32) NOT NULL,
              "OccurredAtUtc" timestamptz NOT NULL, "ReceivedAtUtc" timestamptz NOT NULL,
              "SourceRecordType" varchar(64) NOT NULL, "SourceRecordId" varchar(128) NOT NULL,
              "SourceSystem" varchar(64) NOT NULL, "ActorId" varchar(256), "Provider" varchar(128), "Model" varchar(128),
              "CorrelationId" varchar(128) NOT NULL, "IdempotencyKey" varchar(128) NOT NULL,
              "CostAmount" numeric(18,6) NOT NULL, "Currency" varchar(3) NOT NULL, "EvidenceSha256" char(64) NOT NULL,
              "RatingStatus" varchar(32) NOT NULL, "AdjustsUsageEventId" uuid REFERENCES platform."UsageEvents"("UsageEventId"),
              "RateCardId" bigint, "RateCardLineId" bigint, "RateCardVersion" bigint,
              "AllowanceApplied" numeric(20,6) NOT NULL, "OverageQuantity" numeric(20,6) NOT NULL,
              "UnitPrice" numeric(18,8), "RatedAmount" numeric(18,6),
              CONSTRAINT "UX_UsageEvents_Tenant_IdempotencyKey" UNIQUE ("TenantId", "IdempotencyKey")
            );
            CREATE TABLE IF NOT EXISTS platform."UsageMinuteAggregates" (
              "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, "TenantId" bigint NOT NULL,
              "EventType" varchar(64) NOT NULL, "Unit" varchar(32) NOT NULL, "MinuteUtc" timestamptz NOT NULL,
              "Quantity" numeric(20,6) NOT NULL, "CostAmount" numeric(18,6) NOT NULL,
              "EventCount" integer NOT NULL, "RefreshedAtUtc" timestamptz NOT NULL,
              CONSTRAINT "UX_UsageMinuteAggregates_Bucket" UNIQUE ("TenantId", "EventType", "Unit", "MinuteUtc")
            );
            CREATE TABLE IF NOT EXISTS platform."AccountingOutbox" (
              "Id" uuid PRIMARY KEY, "TenantId" bigint NOT NULL, "SubscriptionInvoiceId" bigint NOT NULL,
              "MessageType" varchar(64) NOT NULL, "IdempotencyKey" varchar(160) NOT NULL UNIQUE,
              "PayloadJson" jsonb NOT NULL, "PayloadSha256" char(64) NOT NULL, "Status" varchar(24) NOT NULL,
              "ReconciliationStatus" varchar(32) NOT NULL, "AttemptCount" integer NOT NULL,
              "MaxAttempts" integer NOT NULL, "CreatedAtUtc" timestamptz NOT NULL, "AvailableAtUtc" timestamptz NOT NULL,
              "LastAttemptAtUtc" timestamptz, "LeaseExpiresAtUtc" timestamptz, "LeaseToken" uuid,
              "WorkerId" varchar(128), "LastFailureCode" varchar(64), "ExternalReference" varchar(256),
              "ExternalReceiptSha256" char(64), "AcknowledgedAtUtc" timestamptz, "AcknowledgedBy" varchar(256),
              "RedrivenAtUtc" timestamptz, "RedrivenBy" varchar(256), "RedriveReason" varchar(1000),
              CONSTRAINT "UX_AccountingOutbox_Invoice_MessageType" UNIQUE ("SubscriptionInvoiceId", "MessageType")
            );
            """);
    }

    private ErpRfqAutomationContext Context() => new(_options, new StubTenant(null));
}
