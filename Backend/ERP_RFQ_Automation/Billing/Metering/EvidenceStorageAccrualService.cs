using System.Globalization;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Metering;

public sealed record StorageAccrualResult(
    long TenantId, DateTime StartUtc, DateTime EndUtc, int SourceCount,
    decimal GigabyteHours, IReadOnlyList<Guid> UsageEventIds);

/// <summary>
/// Produces immutable, source-level logical retained-storage occurrences for one CLOSED UTC
/// hour. Byte size and lifetime come from the immutable evidence row; a present object must
/// also be measured through the configured durable provider before any event is committed.
/// Purged rows require a measured deletion tombstone. The service therefore fails closed on
/// local/ephemeral storage, missing objects and old zero-byte loss reconciliations.
/// </summary>
public sealed class EvidenceStorageAccrualService(
    ErpRfqAutomationContext db,
    IEvidenceObjectStorage storage,
    UsageMeteringService usage)
{
    public async Task<StorageAccrualResult> AccrueClosedHourAsync(
        long tenantId, DateTime hourStartUtc, CancellationToken ct = default)
    {
        if (tenantId <= 0 || hourStartUtc.Kind != DateTimeKind.Utc
            || hourStartUtc.Minute != 0 || hourStartUtc.Second != 0 || hourStartUtc.Millisecond != 0)
            throw new UsageMeteringException("Storage accrual requires a tenant and an exact UTC hour boundary.");
        var hourEndUtc = hourStartUtc.AddHours(1);
        if (hourEndUtc > DateTime.UtcNow)
            throw new UsageMeteringException("Storage accrual is allowed only after the UTC hour has closed.");
        if (!storage.IsDurable)
            throw new UsageMeteringException(
                "Storage coverage is blocked: the configured evidence provider is not durable.");

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.Id == tenantId)
            .Select(x => new { x.PrimaryBusinessUnitId, x.RateCardId })
            .SingleOrDefaultAsync(ct)
            ?? throw new UsageMeteringException("The platform tenant does not exist.");
        if (tenant.PrimaryBusinessUnitId is not long businessUnitId)
            throw new UsageMeteringException("Storage coverage is blocked: the tenant has no primary business unit.");
        var currency = tenant.RateCardId is long cardId
            ? await db.Set<ERP_RFQ_Automation.Billing.RateCard>().AsNoTracking()
                .Where(x => x.Id == cardId).Select(x => x.Currency).SingleOrDefaultAsync(ct)
            : null;

        var from = new DateTimeOffset(hourStartUtc);
        var to = new DateTimeOffset(hourEndUtc);
        var sources = await db.Set<SourceDocument>().IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CreatedOn < to
                        && (x.BytesPurgedOn == null || x.BytesPurgedOn > from))
            .OrderBy(x => x.Id).ToListAsync(ct);

        // Complete provider proof before opening the write transaction. One unverified
        // object blocks the entire hour; a partial usage ledger is never left behind.
        foreach (var source in sources)
        {
            if (source.BytesPurgedOn is null)
            {
                var measured = await storage.TryMeasureObjectAsync(
                    source.ObjectBucket, source.ObjectKey, source.ObjectVersion, ct);
                if (measured != source.ByteSize)
                    throw new UsageMeteringException(
                        $"Storage coverage is blocked: source document {source.Id} is absent or its provider size does not match the immutable ledger.");
            }
            else if (source.PurgedByteCount < source.ByteSize)
            {
                throw new UsageMeteringException(
                    $"Storage coverage is blocked: source document {source.Id} has no measured deletion proof for its logical bytes.");
            }
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var tx = db.Database.CurrentTransaction is null
                ? await db.Database.BeginTransactionAsync(ct)
                : null;
            var eventIds = new List<Guid>(sources.Count);
            decimal total = 0;
            foreach (var source in sources)
            {
                var retainedFrom = source.CreatedOn > from ? source.CreatedOn : from;
                var retainedTo = source.BytesPurgedOn is { } purged && purged < to ? purged : to;
                if (retainedTo <= retainedFrom) continue;
                var hours = (decimal)(retainedTo - retainedFrom).TotalSeconds / 3600m;
                var quantity = decimal.Round(source.ByteSize / 1_073_741_824m * hours,
                    12, MidpointRounding.AwayFromZero);
                if (quantity <= 0) continue;
                var key = $"source-document:{source.Id}:storage:{hourStartUtc:yyyyMMddHH}";
                var id = UsageEventIdentity.FromIdempotencyKey(tenantId, key);
                var recorded = await usage.RecordAsync(new RecordUsageEvent(
                    id, tenantId, "storage.gb-hours", quantity, "gb-hour", hourEndUtc.AddTicks(-1),
                    "SourceDocument", source.Id.ToString(CultureInfo.InvariantCulture),
                    "EvidenceStorageAccrual", "system:storage-accrual", null, null,
                    $"storage:{tenantId}:{hourStartUtc:yyyyMMddHH}", key, 0m,
                    currency ?? "USD", source.ContentHash), ct);
                eventIds.Add(recorded.UsageEventId);
                total += recorded.Quantity;
            }
            if (tx is not null) await tx.CommitAsync(ct);
            return new StorageAccrualResult(tenantId, hourStartUtc, hourEndUtc,
                sources.Count, total, eventIds);
        });
    }
}
