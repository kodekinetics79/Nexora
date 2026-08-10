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
                db.ChangeTracker.Clear();
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
        UsageEventRating? adjustedRating = null;
        if (request.AdjustsUsageEventId is Guid adjustedId)
        {
            if (db.Database.IsNpgsql())
            {
                var adjustmentLock = StableLockKey(request.TenantId, $"adjustment|{adjustedId:N}");
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({adjustmentLock})", ct);
            }
            adjusted = await db.Set<UsageEvent>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.UsageEventId == adjustedId, ct)
                ?? throw new UsageMeteringException("The adjusted usage event does not exist.");
            if (adjusted.TenantId != request.TenantId || adjusted.Kind == UsageEventKind.Adjustment)
                throw new UsageMeteringException("Adjustments must reference an original event in the same tenant.");
            if (adjusted.EventType != request.EventType || adjusted.Unit != request.Unit)
                throw new UsageMeteringException("An adjustment must use the original event type and unit.");
            if (!string.Equals(adjusted.Currency, request.Currency.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new UsageMeteringException("An adjustment must use the original currency.");
            var prior = await db.Set<UsageEvent>().AsNoTracking()
                .Where(x => x.TenantId == request.TenantId && x.AdjustsUsageEventId == adjustedId)
                .GroupBy(_ => 1).Select(group => new
                {
                    Quantity = group.Sum(x => x.Quantity),
                    Cost = group.Sum(x => x.CostAmount),
                    Rated = group.Sum(x => x.RatedAmount ?? 0m)
                }).SingleOrDefaultAsync(ct);
            if (adjusted.Quantity + (prior?.Quantity ?? 0m) + request.Quantity < 0
                || adjusted.CostAmount + (prior?.Cost ?? 0m) + request.CostAmount < 0)
                throw new UsageMeteringException("Cumulative adjustments cannot reverse more quantity or cost than the original usage.");
            adjustedRating = (await db.Set<UsageEventRating>().AsNoTracking()
                    .Where(x => x.TenantId == request.TenantId && x.UsageEventId == adjustedId)
                    .OrderByDescending(x => x.AttemptNumber).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct));
        }

        var certified = UsageMeterCatalog.IsBillingCertified(request.EventType);
        if (adjusted is not null && certified && adjustedRating?.Status is not
                (UsageEventRatingResult.Rated or UsageEventRatingResult.RatedZeroWithReason))
            throw new UsageMeteringException("A billable adjustment requires a successful effective rating on the original event.");
        long? effectiveRateCardId = adjustedRating?.RateCardId ?? request.RateCardId;
        long? effectiveRateCardLineId = adjustedRating?.RateCardLineId ?? request.RateCardLineId;
        long? effectiveRateCardVersion = adjustedRating?.RateCardVersion ?? request.RateCardVersion;
        decimal? effectivePrice = adjustedRating?.UnitPrice ?? request.UnitPrice;
        var effectiveCurrency = adjustedRating?.Currency ?? request.Currency.Trim().ToUpperInvariant();
        var serverAllowance = 0m;
        if (adjusted is null && !certified)
        {
            effectiveRateCardId = null;
            effectiveRateCardLineId = null;
            effectiveRateCardVersion = null;
            effectivePrice = null;
        }
        if (adjusted is null && certified && request.UnitPrice is not null)
        {
            var tenantRateCardId = await db.Set<ERP_RFQ_Automation.Platform.Models.Tenant>().AsNoTracking()
                .Where(x => x.Id == request.TenantId).Select(x => x.RateCardId).SingleOrDefaultAsync(ct);
            if (tenantRateCardId is null || tenantRateCardId != request.RateCardId)
                throw new UsageMeteringException("Rated usage must use the tenant's pinned rate card.");
            var rating = await (from card in db.Set<ERP_RFQ_Automation.Billing.RateCard>().AsNoTracking()
                                join line in db.Set<ERP_RFQ_Automation.Billing.RateCardLine>().AsNoTracking()
                                    on card.Id equals line.RateCardId
                                where card.Id == request.RateCardId && line.Id == request.RateCardLineId
                                select new { Card = card, Line = line }).SingleOrDefaultAsync(ct)
                ?? throw new UsageMeteringException("The supplied rate-card line does not exist.");
            if (!rating.Card.IsActive || rating.Card.Version != request.RateCardVersion
                || rating.Card.EffectiveFromUtc > request.OccurredAtUtc
                || rating.Card.EffectiveToUtc is DateTime until && until <= request.OccurredAtUtc
                || !string.Equals(rating.Card.Currency, request.Currency.Trim(), StringComparison.OrdinalIgnoreCase)
                || rating.Line.MeterKey != RateMeterKey(request.EventType)
                || rating.Line.UnitPrice != request.UnitPrice || request.AllowanceApplied > rating.Line.IncludedQuantity)
                throw new UsageMeteringException("Rated usage does not match the effective tenant rate-card line.");
            effectiveRateCardId = rating.Card.Id;
            effectiveRateCardLineId = rating.Line.Id;
            effectiveRateCardVersion = rating.Card.Version;
            effectivePrice = rating.Line.UnitPrice;
            serverAllowance = await ServerAuthoritativeAllowance.AllocateAsync(db, request.TenantId,
                rating.Line.MeterKey, rating.Line.Id, rating.Line.IncludedQuantity, request.Quantity,
                request.OccurredAtUtc, request.UsageEventId, ct);
        }
        var overage = adjusted is null
            ? Math.Max(0, request.Quantity - serverAllowance)
            : request.Quantity;
        decimal? ratedAmount = certified && effectivePrice is decimal price
            ? decimal.Round(overage / ERP_RFQ_Automation.Billing.BillingStatementService.PricedUnitDivisor(
                RateMeterKey(request.EventType)) * price, 6, MidpointRounding.AwayFromZero)
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
            Currency = effectiveCurrency,
            EvidenceSha256 = request.EvidenceSha256.Trim().ToLowerInvariant(),
            RatingStatus = !certified ? UsageRatingStatus.BlockedUncertifiedMeter
                : ratedAmount is null ? UsageRatingStatus.Pending : UsageRatingStatus.Rated,
            AdjustsUsageEventId = request.AdjustsUsageEventId,
            RateCardId = effectiveRateCardId,
            RateCardLineId = effectiveRateCardLineId,
            RateCardVersion = effectiveRateCardVersion,
            AllowanceApplied = adjusted is null ? serverAllowance : 0,
            OverageQuantity = overage,
            UnitPrice = effectivePrice,
            RatedAmount = ratedAmount
        };
        db.Add(usage);
        await db.SaveChangesAsync(ct);
        var ratingResult = usage.RatingStatus switch
        {
            UsageRatingStatus.Rated when usage.RatedAmount == 0m => UsageEventRatingResult.RatedZeroWithReason,
            UsageRatingStatus.Rated => UsageEventRatingResult.Rated,
            UsageRatingStatus.BlockedUncertifiedMeter => UsageEventRatingResult.ExcludedWithReason,
            UsageRatingStatus.RatingFailed => UsageEventRatingResult.RatingFailed,
            _ => UsageEventRatingResult.Unrated
        };
        db.Add(new UsageEventRating
        {
            TenantId = usage.TenantId,
            UsageEventId = usage.UsageEventId,
            AttemptNumber = 1,
            IdempotencyKey = $"ingest:{usage.IdempotencyKey}",
            Status = ratingResult,
            ReasonCode = ratingResult switch
            {
                UsageEventRatingResult.RatedZeroWithReason when usage.AllowanceApplied > 0 => "INCLUDED_ALLOWANCE",
                UsageEventRatingResult.RatedZeroWithReason => "ZERO_PRICE_COMMERCIAL_TERM",
                UsageEventRatingResult.ExcludedWithReason => "METER_NOT_BILLING_CERTIFIED",
                UsageEventRatingResult.Unrated => "RATE_REQUIRED",
                UsageEventRatingResult.RatingFailed => "RATING_FAILED",
                _ => null
            },
            RateCardId = usage.RateCardId,
            RateCardLineId = usage.RateCardLineId,
            RateCardVersion = usage.RateCardVersion,
            Currency = usage.Currency,
            AllowanceApplied = usage.AllowanceApplied,
            OverageQuantity = usage.OverageQuantity,
            UnitPrice = usage.UnitPrice,
            RatedAmount = usage.RatedAmount,
            OccurredAtUtc = usage.OccurredAtUtc,
            RatedAtUtc = DateTime.UtcNow,
            RatedBy = usage.ActorId ?? "system:usage-ingestion",
            EvidenceSha256 = usage.EvidenceSha256
        });
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
        if (string.Equals(value.EventType, "base.subscription", StringComparison.Ordinal))
            throw new UsageMeteringException(
                "Base subscription is derived from the effective plan and cannot be submitted as a usage event.");
        if (!string.Equals(expectedUnit, value.Unit, StringComparison.Ordinal))
            throw new UsageMeteringException($"Usage unit must be {expectedUnit} for {value.EventType}.");
        if (value.AdjustsUsageEventId is null && value.Quantity <= 0)
            throw new UsageMeteringException("Consumption quantity must be positive.");
        if (value.AdjustsUsageEventId is not null && value.Quantity == 0)
            throw new UsageMeteringException("Adjustment quantity cannot be zero.");
        if (value.AllowanceApplied != 0)
            throw new UsageMeteringException("Allowance allocation is server-authoritative and cannot be supplied by a caller.");
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
        && (stored.AdjustsUsageEventId is not null || stored.RatingStatus == UsageRatingStatus.BlockedUncertifiedMeter ||
            stored.RateCardId == value.RateCardId && stored.RateCardLineId == value.RateCardLineId
            && stored.RateCardVersion == value.RateCardVersion
            && stored.UnitPrice == value.UnitPrice);

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

    private static string RateMeterKey(string eventType) => eventType switch
    {
        "ai.tokens" => ERP_RFQ_Automation.Billing.BillingMeterKeys.AiTokensExternal,
        "storage.gb-hours" => ERP_RFQ_Automation.Billing.BillingMeterKeys.StorageGb,
        _ => eventType
    };
}

internal static class ServerAuthoritativeAllowance
{
    public static async Task<decimal> AllocateAsync(ErpRfqAutomationContext db, long tenantId,
        string meterKey, long rateCardLineId, decimal includedQuantity, decimal quantity,
        DateTime occurredAtUtc, Guid excludedUsageEventId, CancellationToken ct)
    {
        if (quantity <= 0 || includedQuantity <= 0) return 0m;
        var period = ERP_RFQ_Automation.Billing.BillingPeriod.Containing(occurredAtUtc);
        if (db.Database.IsNpgsql())
        {
            var lockKey = StableLockKey(tenantId, $"allowance|{meterKey}|{period.Key}");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({lockKey})", ct);
        }
        var rows = await db.Set<UsageEventRating>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.RateCardLineId == rateCardLineId
                        && x.OccurredAtUtc >= period.StartUtc && x.OccurredAtUtc < period.EndUtc
                        && x.UsageEventId != excludedUsageEventId).ToListAsync(ct);
        var allocated = rows.GroupBy(x => x.UsageEventId)
            .Select(x => x.OrderByDescending(r => r.AttemptNumber).ThenByDescending(r => r.Id).First())
            .Where(x => x.Status is UsageEventRatingResult.Rated or UsageEventRatingResult.RatedZeroWithReason)
            .Sum(x => x.AllowanceApplied);
        return Math.Min(quantity, Math.Max(0m, includedQuantity - allocated));
    }

    private static long StableLockKey(long tenantId, string value)
    {
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var character in $"{tenantId}|{value}") { hash ^= character; hash *= 1099511628211UL; }
            return (long)hash;
        }
    }
}
