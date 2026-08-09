using ERP_RFQ_Automation.Billing.Metering;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EvidenceStorageAccrualPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Closed_hour_uses_immutable_bytes_and_partial_lifetime_and_replays_exactly_once()
    {
        var bu = Random.Shared.NextInt64(30_000_000, 40_000_000);
        var tenantId = Random.Shared.NextInt64(40_000_000, 50_000_000);
        var hour = new DateTime(DateTime.UtcNow.AddHours(-2).Ticks / TimeSpan.TicksPerHour
                                * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        await using var db = database.ContextFor(null);
        Seed.BusinessUnit(db, bu);
        db.Add(new Tenant
        {
            Id = tenantId, Name = $"Storage {tenantId}", LegalName = "Storage Meter Test LLC",
            Slug = $"storage-{tenantId}", BillingContactEmail = "billing@storage.test",
            Status = TenantStatus.Active, PrimaryBusinessUnitId = bu
        });
        var corpus = DocumentCorpus.Create(bu, Guid.NewGuid(), CorpusSourceType.ManualUpload,
            new DateTimeOffset(hour.AddMinutes(30)));
        db.Add(corpus);
        await db.SaveChangesAsync();
        var hash = new string('a', 64);
        db.Add(SourceDocument.Create(bu, corpus.Id, hash, "half-hour.pdf", "application/pdf",
            "durable", "object/half-hour", "v1", 1_073_741_824,
            new DateTimeOffset(hour.AddMinutes(30))));
        await db.SaveChangesAsync();

        var storage = new MeasuringStorage(1_073_741_824);
        var service = new EvidenceStorageAccrualService(db, storage, new UsageMeteringService(db));
        var first = await service.AccrueClosedHourAsync(tenantId, hour);
        db.ChangeTracker.Clear();
        var replay = await service.AccrueClosedHourAsync(tenantId, hour);

        Assert.Equal(.5m, first.GigabyteHours);
        Assert.Equal(first.UsageEventIds, replay.UsageEventIds);
        Assert.Equal(1, await db.Set<UsageEvent>().IgnoreQueryFilters().CountAsync(x =>
            x.TenantId == tenantId && x.EventType == "storage.gb-hours"));
        var usage = await db.Set<UsageEvent>().IgnoreQueryFilters().SingleAsync(x => x.TenantId == tenantId);
        Assert.Equal(UsageRatingStatus.BlockedUncertifiedMeter, usage.RatingStatus);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Ephemeral_provider_blocks_before_any_usage_is_written()
    {
        var hour = new DateTime(DateTime.UtcNow.AddHours(-2).Ticks / TimeSpan.TicksPerHour
                                * TimeSpan.TicksPerHour, DateTimeKind.Utc);
        await using var db = database.ContextFor(null);
        var service = new EvidenceStorageAccrualService(db, new EphemeralStorage(),
            new UsageMeteringService(db));
        await Assert.ThrowsAsync<UsageMeteringException>(() =>
            service.AccrueClosedHourAsync(Random.Shared.NextInt64(60_000_000, 70_000_000), hour));
    }

    private sealed class MeasuringStorage(long bytes) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long?> TryMeasureObjectAsync(string bucket, string key, string version,
            CancellationToken ct = default) => Task.FromResult<long?>(bytes);
    }

    private sealed class EphemeralStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => false;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
