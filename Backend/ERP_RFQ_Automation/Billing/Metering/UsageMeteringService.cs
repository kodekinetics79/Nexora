using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Metering;

public sealed class UsageMeteringException(string message) : InvalidOperationException(message);

public sealed class UsageMeteringService(ErpRfqAutomationContext db)
{
    public async Task<UsageEvent> RecordAsync(RecordUsageEvent request, CancellationToken ct = default)
    {
        Validate(request);
        request = request with { OccurredAtUtc = NormalizeTimestamp(request.OccurredAtUtc) };
        if (db.Database.IsRelational() && db.Database.CurrentTransaction is null)
        {
            var strategy = db.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                var result = await RecordAsync(request, ct);
                await transaction.CommitAsync(ct);
                return result;
            });
        }

        if (db.Database.IsNpgsql())
        {
            var lockKey = StableLockKey(request.TenantId, request.IdempotencyKey);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
        }

        var key = request.IdempotencyKey.Trim();
        var replay = await db.Set<UsageEvent>().AsNoTracking()
            .FirstOrDefaultAsync(x => x.TenantId == request.TenantId && x.IdempotencyKey == key, ct);
        if (replay is not null)
        {
            if (!SameOccurrence(replay, request))
                throw new UsageMeteringException("The usage idempotency key was already used for different usage.");
            return replay;
        }
        if (await db.Set<UsageEvent>().AsNoTracking().AnyAsync(x => x.UsageEventId == request.UsageEventId, ct))
            throw new UsageMeteringException("The usage event identifier was already used by another occurrence.");

        UsageEvent? adjusted = null;
        if (request.AdjustsUsageEventId is Guid adjustedId)
        {
            adjusted = await db.Set<UsageEvent>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.UsageEventId == adjustedId, ct)
                ?? throw new UsageMeteringException("The adjusted usage event does not exist.");
            if (adjusted.TenantId != request.TenantId || adjusted.Kind == UsageEventKind.Adjustment)
                throw new UsageMeteringException("Adjustments must reference an original event in the same tenant.");
            if (adjusted.EventType != request.EventType || adjusted.Unit != request.Unit)
                throw new UsageMeteringException("An adjustment must use the original event type and unit.");
        }

        var certified = UsageMeterCatalog.IsBillingCertified(request.EventType);
        var overage = adjusted is null
            ? Math.Max(0, request.Quantity - request.AllowanceApplied)
            : request.Quantity;
        decimal? ratedAmount = certified && request.UnitPrice is decimal price
            ? decimal.Round(overage * price, 6, MidpointRounding.AwayFromZero)
            : null;
        var usage = new UsageEvent
        {
            UsageEventId = request.UsageEventId,
            TenantId = request.TenantId,
            Kind = adjusted is null ? UsageEventKind.Consumption : UsageEventKind.Adjustment,
            EventType = request.EventType,
            Quantity = request.Quantity,
            Unit = request.Unit,
            OccurredAtUtc = request.OccurredAtUtc,
            ReceivedAtUtc = DateTime.UtcNow,
            SourceRecordType = request.SourceRecordType.Trim(),
            SourceRecordId = request.SourceRecordId.Trim(),
            SourceSystem = request.SourceSystem.Trim(),
            ActorId = Normalize(request.ActorId),
            Provider = Normalize(request.Provider),
            Model = Normalize(request.Model),
            CorrelationId = request.CorrelationId.Trim(),
            IdempotencyKey = key,
            CostAmount = request.CostAmount,
            Currency = request.Currency.Trim().ToUpperInvariant(),
            EvidenceSha256 = request.EvidenceSha256.Trim().ToLowerInvariant(),
            RatingStatus = !certified ? UsageRatingStatus.BlockedUncertifiedMeter
                : ratedAmount is null ? UsageRatingStatus.Pending : UsageRatingStatus.Rated,
            AdjustsUsageEventId = request.AdjustsUsageEventId,
            RateCardId = request.RateCardId,
            RateCardLineId = request.RateCardLineId,
            RateCardVersion = request.RateCardVersion,
            AllowanceApplied = request.AllowanceApplied,
            OverageQuantity = overage,
            UnitPrice = request.UnitPrice,
            RatedAmount = ratedAmount
        };
        db.Add(usage);
        await db.SaveChangesAsync(ct);
        await RefreshMinuteAsync(usage.TenantId, usage.EventType, usage.Unit, usage.OccurredAtUtc, ct);
        return usage;
    }

    public async Task<IReadOnlyList<UsageMinuteAggregate>> ReadMinutesAsync(
        long tenantId, DateTime fromUtc, DateTime toUtc, CancellationToken ct = default)
    {
        if (tenantId <= 0 || toUtc <= fromUtc) throw new UsageMeteringException("A valid tenant and time range are required.");
        return await db.Set<UsageMinuteAggregate>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.MinuteUtc >= fromUtc && x.MinuteUtc < toUtc)
            .OrderBy(x => x.MinuteUtc).ThenBy(x => x.EventType).ToListAsync(ct);
    }

    private async Task RefreshMinuteAsync(long tenantId, string eventType, string unit, DateTime occurredAt, CancellationToken ct)
    {
        var minute = new DateTime(occurredAt.Ticks - occurredAt.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
        if (db.Database.IsNpgsql())
        {
            var bucketLock = StableLockKey(tenantId, $"{eventType}|{unit}|{minute:O}");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({bucketLock})", ct);
        }
        var totals = await db.Set<UsageEvent>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.EventType == eventType && x.Unit == unit
                        && x.OccurredAtUtc >= minute && x.OccurredAtUtc < minute.AddMinutes(1))
            .GroupBy(_ => 1).Select(group => new
            {
                Quantity = group.Sum(x => x.Quantity), Cost = group.Sum(x => x.CostAmount), Count = group.Count()
            }).SingleAsync(ct);
        var bucket = await db.Set<UsageMinuteAggregate>()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.EventType == eventType
                                       && x.Unit == unit && x.MinuteUtc == minute, ct);
        if (bucket is null)
        {
            bucket = new UsageMinuteAggregate
            {
                TenantId = tenantId, EventType = eventType, Unit = unit, MinuteUtc = minute
            };
            db.Add(bucket);
        }
        bucket.Quantity = totals.Quantity;
        bucket.CostAmount = totals.Cost;
        bucket.EventCount = totals.Count;
        bucket.RefreshedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static void Validate(RecordUsageEvent value)
    {
        if (value.UsageEventId == Guid.Empty || value.TenantId <= 0)
            throw new UsageMeteringException("Usage event and tenant identifiers are required.");
        if (!UsageMeterCatalog.Units.TryGetValue(value.EventType, out var expectedUnit))
            throw new UsageMeteringException("Unknown usage event type.");
        if (!string.Equals(expectedUnit, value.Unit, StringComparison.Ordinal))
            throw new UsageMeteringException($"Usage unit must be {expectedUnit} for {value.EventType}.");
        if (value.AdjustsUsageEventId is null && value.Quantity <= 0)
            throw new UsageMeteringException("Consumption quantity must be positive.");
        if (value.AdjustsUsageEventId is not null && value.Quantity == 0)
            throw new UsageMeteringException("Adjustment quantity cannot be zero.");
        if (value.AllowanceApplied < 0 || value.AllowanceApplied > Math.Max(0, value.Quantity))
            throw new UsageMeteringException("Applied allowance is outside the event quantity.");
        if (value.AdjustsUsageEventId is null && value.CostAmount < 0 || value.UnitPrice < 0)
            throw new UsageMeteringException("Consumption cost and unit price cannot be negative.");
        if (value.OccurredAtUtc.Kind != DateTimeKind.Utc || value.OccurredAtUtc > DateTime.UtcNow.AddMinutes(5))
            throw new UsageMeteringException("OccurredAt must be UTC and cannot be in the future.");
        if (value.OccurredAtUtc < DateTime.UtcNow.AddYears(-7))
            throw new UsageMeteringException("Usage is outside the supported late-arrival window.");
        Required(value.SourceRecordType, 64, "source record type");
        Required(value.SourceRecordId, 128, "source record identifier");
        Required(value.SourceSystem, 64, "source system");
        Required(value.CorrelationId, 128, "correlation identifier");
        Required(value.IdempotencyKey, 128, "idempotency key");
        if (value.Currency?.Trim().Length != 3) throw new UsageMeteringException("A three-letter currency is required.");
        if (value.EvidenceSha256?.Trim().Length != 64 || value.EvidenceSha256.Any(c => !Uri.IsHexDigit(c)))
            throw new UsageMeteringException("A SHA-256 evidence hash is required.");
        if (value.UnitPrice is not null && (value.RateCardId is null || value.RateCardLineId is null || value.RateCardVersion is null))
            throw new UsageMeteringException("Rated usage requires complete rate-card lineage.");
    }

    private static bool SameOccurrence(UsageEvent stored, RecordUsageEvent value) =>
        stored.UsageEventId == value.UsageEventId && stored.EventType == value.EventType
        && stored.Quantity == value.Quantity && stored.Unit == value.Unit
        && CanonicalUtc(stored.OccurredAtUtc) == CanonicalUtc(value.OccurredAtUtc)
        && stored.SourceRecordType == value.SourceRecordType.Trim()
        && stored.SourceRecordId == value.SourceRecordId.Trim() && stored.SourceSystem == value.SourceSystem.Trim()
        && stored.ActorId == Normalize(value.ActorId) && stored.Provider == Normalize(value.Provider)
        && stored.Model == Normalize(value.Model)
        && stored.CorrelationId == value.CorrelationId.Trim() && stored.CostAmount == value.CostAmount
        && stored.Currency == value.Currency.Trim().ToUpperInvariant()
        && stored.EvidenceSha256 == value.EvidenceSha256.Trim().ToLowerInvariant()
        && stored.AdjustsUsageEventId == value.AdjustsUsageEventId
        && stored.RateCardId == value.RateCardId && stored.RateCardLineId == value.RateCardLineId
        && stored.RateCardVersion == value.RateCardVersion && stored.AllowanceApplied == value.AllowanceApplied
        && stored.UnitPrice == value.UnitPrice;

    private static void Required(string? value, int maximum, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > maximum)
            throw new UsageMeteringException($"A {name} of at most {maximum} characters is required.");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long StableLockKey(long tenantId, string value)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var character in $"{tenantId}|{value}") { hash ^= character; hash *= 1099511628211UL; }
            return (long)hash;
        }
    }

    private static DateTime NormalizeTimestamp(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private static DateTime CanonicalUtc(DateTime value) => value.Kind == DateTimeKind.Unspecified
        ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
        : value.ToUniversalTime();
}
