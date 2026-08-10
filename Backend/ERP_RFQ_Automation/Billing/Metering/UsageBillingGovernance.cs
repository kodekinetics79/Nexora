using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Billing.Metering;

public enum TenantMeterSourceMode
{
    LegacyAuthoritative,
    CanonicalShadow,
    CanonicalAuthoritative,
    BillingBlocked
}

public enum UsageAuthoritativeSource { Legacy, Canonical }
public enum UsageCoverageCompleteness { Pending, Complete, Incomplete, Unknown }
public enum UsageReconciliationStatus { Pending, Matched, WithinApprovedTolerance, Mismatch, NotApplicable }

public sealed class TenantMeterSourcePolicy
{
    public long TenantId { get; set; }
    public string MeterKey { get; set; } = null!;
    public TenantMeterSourceMode Mode { get; set; } = TenantMeterSourceMode.LegacyAuthoritative;
    public DateTime? ProposedEffectiveAtUtc { get; set; }
    public DateTime? CutoverAtUtc { get; set; }
    public string? ProposedBy { get; set; }
    public DateTime? ProposedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? ApprovalReason { get; set; }
    public long Version { get; set; } = 1;
}

/// <summary>An immutable proof of which source owns one contiguous meter interval.</summary>
public sealed class UsageCoverageSegment
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public string MeterKey { get; set; } = null!;
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public UsageAuthoritativeSource AuthoritativeSource { get; set; }
    public UsageCoverageCompleteness Completeness { get; set; }
    public int EventCount { get; set; }
    public decimal QuantityTotal { get; set; }
    public decimal AllowanceAppliedTotal { get; set; }
    public decimal OverageQuantityTotal { get; set; }
    public decimal RatedAmountTotal { get; set; }
    public string Currency { get; set; } = "USD";
    public string RateLineageJson { get; set; } = "[]";
    public string RateLineageSha256 { get; set; } = null!;
    public string EvidenceSha256 { get; set; } = null!;
    public DateTime CompletenessWatermarkUtc { get; set; }
    public DateTime? CutoverAtUtc { get; set; }
    public UsageReconciliationStatus ReconciliationStatus { get; set; }
    public int? CounterpartEventCount { get; set; }
    public decimal? CounterpartQuantityTotal { get; set; }
    public string? CounterpartEvidenceSha256 { get; set; }
    public string ApprovedBy { get; set; } = null!;
    public DateTime ApprovedAtUtc { get; set; }
    public string ApprovalReason { get; set; } = null!;
}

public enum UsageEventRatingResult
{
    Rated,
    RatedZeroWithReason,
    ExcludedWithReason,
    Unrated,
    RatingFailed
}

/// <summary>Append-only rating decision; the highest attempt is the effective decision.</summary>
public sealed class UsageEventRating
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public Guid UsageEventId { get; set; }
    public int AttemptNumber { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public UsageEventRatingResult Status { get; set; }
    public string? ReasonCode { get; set; }
    public long? ContractId { get; set; }
    public long? PlanId { get; set; }
    public long? RateCardId { get; set; }
    public long? RateCardLineId { get; set; }
    public long? RateCardVersion { get; set; }
    public string Currency { get; set; } = null!;
    public decimal AllowanceApplied { get; set; }
    public decimal OverageQuantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public decimal? RatedAmount { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime RatedAtUtc { get; set; }
    public string RatedBy { get; set; } = null!;
    public string EvidenceSha256 { get; set; } = null!;
}

public static class BillingReadinessCodes
{
    public const string BillingBlocked = "BILLING_BLOCKED";
    public const string CoverageGap = "COVERAGE_GAP";
    public const string CoverageOverlap = "COVERAGE_OVERLAP";
    public const string CoverageIncomplete = "COVERAGE_INCOMPLETE";
    public const string ReconciliationUnresolved = "RECONCILIATION_UNRESOLVED";
    public const string UnratedEvent = "UNRATED_EVENT";
    public const string RatingFailed = "RATING_FAILED";
    public const string RatingLineageMismatch = "RATING_LINEAGE_MISMATCH";
    public const string UnknownMeter = "UNKNOWN_METER";
    public const string UncertifiedMeter = "UNCERTIFIED_METER";
    public const string StaleReadiness = "STALE_READINESS";
}

public sealed record BillingReadinessFailure(string Code, string MeterKey, string Detail);

public sealed record BillingReadinessResult(
    bool Ready, IReadOnlyList<BillingReadinessFailure> Failures, string ManifestJson, string ManifestSha256);

public sealed record ResolvedBillingUsage(
    IReadOnlyList<MeterReading> Meters, BillingReadinessResult Readiness);

public sealed record CorrectUsageRatingCommand(Guid UsageEventId, string IdempotencyKey, string Reason);
public sealed record ProposeDocumentCoverageCommand(long TenantId, BillingPeriod Period,
    DateTime? MidPeriodCutoverUtc, string Reason);

public sealed class UsageBillingReadinessService(ERP_RFQ_Automation.Models.ErpRfqAutomationContext db)
{
    public async Task<UsageEventRating> CorrectRatingAsync(CorrectUsageRatingCommand command, string actor,
        Func<UsageEventRating, CancellationToken, Task>? audit = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey) || command.IdempotencyKey.Trim().Length > 128
            || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length < 10)
            throw new UsageMeteringException("A bounded idempotency key and correction reason of at least 10 characters are required.");
        return await InTransactionAsync(async () =>
        {
            var usageEvent = await db.Set<UsageEvent>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.UsageEventId == command.UsageEventId, ct)
                ?? throw new UsageMeteringException("The usage event does not exist.");
            await LockAsync(usageEvent.TenantId, $"rating|{usageEvent.UsageEventId:N}", ct);
            var key = command.IdempotencyKey.Trim();
            var replay = await db.Set<UsageEventRating>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.TenantId == usageEvent.TenantId && x.IdempotencyKey == key, ct);
            if (replay is not null) return replay;
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(x => x.Id == usageEvent.TenantId, ct);
            RateCard card;
            RateCardLine line;
            if (usageEvent.Kind == UsageEventKind.Adjustment)
            {
                var originalRating = await db.Set<UsageEventRating>().AsNoTracking()
                    .Where(x => x.TenantId == usageEvent.TenantId
                                && x.UsageEventId == usageEvent.AdjustsUsageEventId)
                    .OrderByDescending(x => x.AttemptNumber).ThenByDescending(x => x.Id).FirstOrDefaultAsync(ct);
                if (originalRating?.Status is not
                        (UsageEventRatingResult.Rated or UsageEventRatingResult.RatedZeroWithReason)
                    || originalRating.RateCardId is not long inheritedCardId
                    || originalRating.RateCardLineId is not long inheritedLineId)
                    throw new UsageMeteringException("An adjustment correction requires successful effective rating lineage on its original event.");
                card = await db.Set<RateCard>().AsNoTracking().SingleAsync(x => x.Id == inheritedCardId, ct);
                line = await db.Set<RateCardLine>().AsNoTracking().SingleAsync(x => x.Id == inheritedLineId, ct);
            }
            else
            {
                IQueryable<RateCard> cards = db.Set<RateCard>().AsNoTracking().Include(x => x.Lines)
                    .Where(x => x.IsActive && x.EffectiveFromUtc <= usageEvent.OccurredAtUtc
                                && (x.EffectiveToUtc == null || x.EffectiveToUtc > usageEvent.OccurredAtUtc));
                cards = tenant.RateCardId is long pinned ? cards.Where(x => x.Id == pinned) : cards;
                var matches = (await cards.ToListAsync(ct)).SelectMany(x => x.Lines.Select(rateLine => new { Card = x, Line = rateLine }))
                    .Where(x => x.Line.MeterKey == CanonicalMeterKey(usageEvent.EventType)).ToList();
                if (matches.Count != 1)
                    throw new UsageMeteringException("The event has no unambiguous tenant-pinned effective rate-card line at OccurredAt.");
                card = matches[0].Card;
                line = matches[0].Line;
            }
            var allowance = usageEvent.Kind == UsageEventKind.Adjustment ? 0m
                : await ServerAuthoritativeAllowance.AllocateAsync(db, usageEvent.TenantId, line.MeterKey,
                    line.Id, line.IncludedQuantity, usageEvent.Quantity, usageEvent.OccurredAtUtc,
                    usageEvent.UsageEventId, ct);
            var overage = usageEvent.Kind == UsageEventKind.Adjustment
                ? usageEvent.Quantity : Math.Max(0m, usageEvent.Quantity - allowance);
            var amount = decimal.Round(overage /
                BillingStatementService.PricedUnitDivisor(line.MeterKey) * line.UnitPrice,
                6, MidpointRounding.AwayFromZero);
            var attempt = (await db.Set<UsageEventRating>().Where(x => x.TenantId == usageEvent.TenantId
                && x.UsageEventId == usageEvent.UsageEventId).MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
            var rating = new UsageEventRating
            {
                TenantId = usageEvent.TenantId, UsageEventId = usageEvent.UsageEventId,
                AttemptNumber = attempt, IdempotencyKey = key,
                Status = amount == 0 ? UsageEventRatingResult.RatedZeroWithReason : UsageEventRatingResult.Rated,
                ReasonCode = amount == 0 && allowance > 0 ? "INCLUDED_ALLOWANCE"
                    : amount == 0 ? "ZERO_PRICE_COMMERCIAL_TERM" : null,
                PlanId = tenant.PlanId, RateCardId = card.Id, RateCardLineId = line.Id,
                RateCardVersion = card.Version, Currency = card.Currency,
                AllowanceApplied = allowance, OverageQuantity = overage,
                UnitPrice = line.UnitPrice, RatedAmount = amount,
                OccurredAtUtc = usageEvent.OccurredAtUtc, RatedAtUtc = DateTime.UtcNow,
                RatedBy = actor, EvidenceSha256 = usageEvent.EvidenceSha256
            };
            db.Add(rating);
            await db.SaveChangesAsync(ct);
            if (audit is not null) await audit(rating, ct);
            return rating;
        }, ct);
    }

    public async Task<TenantMeterSourcePolicy> ProposeDocumentCoverageAsync(
        ProposeDocumentCoverageCommand command, string actor,
        Func<TenantMeterSourcePolicy, CancellationToken, Task>? audit = null, CancellationToken ct = default)
    {
        ValidateCoverageCommand(command);
        return await InTransactionAsync(async () =>
        {
            await LockAsync(command.TenantId, $"coverage|documents|{command.Period.Key}", ct);
            var policy = await db.Set<TenantMeterSourcePolicy>().SingleOrDefaultAsync(x =>
                x.TenantId == command.TenantId && x.MeterKey == BillingMeterKeys.Documents, ct);
            if (policy is null)
            {
                policy = new TenantMeterSourcePolicy { TenantId = command.TenantId,
                    MeterKey = BillingMeterKeys.Documents };
                db.Add(policy);
            }
            var existingBoundary = policy.CutoverAtUtc;
            policy.Mode = TenantMeterSourceMode.CanonicalShadow;
            // Default cutover is the NEXT period boundary. Once that boundary has
            // been approved, reconciliation of the following closed period reuses
            // its exact start instead of silently moving the cutover again.
            policy.ProposedEffectiveAtUtc = command.MidPeriodCutoverUtc
                ?? (existingBoundary == command.Period.StartUtc ? existingBoundary : command.Period.EndUtc);
            policy.ProposedBy = actor; policy.ProposedAtUtc = DateTime.UtcNow;
            policy.ApprovedBy = null; policy.ApprovedAtUtc = null;
            policy.ApprovalReason = command.Reason.Trim(); policy.Version++;
            await db.SaveChangesAsync(ct);
            if (audit is not null) await audit(policy, ct);
            return policy;
        }, ct);
    }

    public async Task<IReadOnlyList<UsageCoverageSegment>> ApproveDocumentCoverageAsync(
        long tenantId, BillingPeriod period, string actor, string reason,
        Func<IReadOnlyList<UsageCoverageSegment>, CancellationToken, Task>? audit = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 10)
            throw new UsageMeteringException("An approval reason of at least 10 characters is required.");
        return await InTransactionAsync(async () =>
        {
            await LockAsync(tenantId, $"coverage|documents|{period.Key}", ct);
            var policy = await db.Set<TenantMeterSourcePolicy>().SingleOrDefaultAsync(x =>
                x.TenantId == tenantId && x.MeterKey == BillingMeterKeys.Documents, ct)
                ?? throw new UsageMeteringException("A maker proposal is required before approval.");
            if (string.Equals(policy.ProposedBy, actor, StringComparison.OrdinalIgnoreCase))
                throw new UsageMeteringException("The coverage proposal maker cannot approve the same cutover.");
            var cutover = policy.ProposedEffectiveAtUtc ?? period.StartUtc;
            if (cutover < period.StartUtc || cutover > period.EndUtc)
                throw new UsageMeteringException("The proposed cutover must fall on or inside the billing-period boundary.");
            var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == tenantId, ct);
            if (tenant.PrimaryBusinessUnitId is not long bu) throw new UsageMeteringException("The tenant has no primary business unit.");
            var nonBillable = BillableDocumentPolicy.NonBillableStatuses;
            var jobs = await db.Set<ExtractionJob>().IgnoreQueryFilters().AsNoTracking()
                .Where(x => x.BusinessUnitId == bu && !nonBillable.Contains(x.Status)
                            && x.CreatedOn >= period.StartUtc && x.CreatedOn < period.EndUtc)
                .Select(x => new { x.Id, x.CreatedOn }).ToListAsync(ct);
            var events = await db.Set<UsageEvent>().AsNoTracking().Where(x => x.TenantId == tenantId
                && x.EventType == "documents" && x.OccurredAtUtc >= period.StartUtc
                && x.OccurredAtUtc < period.EndUtc).ToListAsync(ct);
            var ids = events.Select(x => x.UsageEventId).ToList();
            var rows = await db.Set<UsageEventRating>().AsNoTracking().Where(x => x.TenantId == tenantId
                && ids.Contains(x.UsageEventId)).ToListAsync(ct);
            var ratings = rows.GroupBy(x => x.UsageEventId).Select(x => x.OrderByDescending(r => r.AttemptNumber).First()).ToList();
            if (events.Count == 0 || ratings.Count != events.Count || ratings.Any(x => x.Status is not
                    (UsageEventRatingResult.Rated or UsageEventRatingResult.RatedZeroWithReason)))
                throw new UsageMeteringException("Every canonical document event must have a successful effective rating.");
            var effectiveCards = await db.Set<RateCard>().AsNoTracking().Include(x => x.Lines)
                .Where(x => x.IsActive && x.EffectiveFromUtc < period.EndUtc
                            && (x.EffectiveToUtc == null || x.EffectiveToUtc > period.StartUtc)
                            && (tenant.RateCardId == null || x.Id == tenant.RateCardId))
                .ToListAsync(ct);
            foreach (var usageEvent in events)
            {
                var rating = ratings.Single(x => x.UsageEventId == usageEvent.UsageEventId);
                var matches = effectiveCards.Where(x => x.EffectiveFromUtc <= usageEvent.OccurredAtUtc
                        && (x.EffectiveToUtc == null || x.EffectiveToUtc > usageEvent.OccurredAtUtc))
                    .SelectMany(x => x.Lines.Where(line => line.MeterKey == BillingMeterKeys.Documents)
                        .Select(line => new { Card = x, Line = line })).ToList();
                var match = matches.Count == 1 ? matches[0] : null;
                var expected = match is null ? (decimal?)null : decimal.Round(rating.OverageQuantity
                    / BillingStatementService.PricedUnitDivisor(BillingMeterKeys.Documents) * match.Line.UnitPrice,
                    6, MidpointRounding.AwayFromZero);
                if (match is null || rating.RateCardId != match.Card.Id || rating.RateCardLineId != match.Line.Id
                    || rating.RateCardVersion != match.Card.Version || rating.Currency != match.Card.Currency
                    || rating.UnitPrice != match.Line.UnitPrice || rating.RatedAmount != expected
                    || rating.Status == UsageEventRatingResult.RatedZeroWithReason
                    && string.IsNullOrWhiteSpace(rating.ReasonCode))
                    throw new UsageMeteringException("Canonical document rating lineage is ambiguous or does not match the event-time effective commercial rate.");
            }
            if (cutover > period.StartUtc && cutover < period.EndUtc
                && ratings.Any(x => x.AllowanceApplied != 0))
                throw new UsageMeteringException("Mid-period cutover is ambiguous when a per-period allowance is nonzero.");
            if (jobs.Count != events.Sum(x => x.Quantity))
                throw new UsageMeteringException("Legacy and canonical document totals do not reconcile for the complete period.");
            if (await db.Set<UsageCoverageSegment>().AnyAsync(x => x.TenantId == tenantId
                && x.MeterKey == BillingMeterKeys.Documents && x.EndUtc > period.StartUtc && x.StartUtc < period.EndUtc, ct))
                throw new UsageMeteringException("Coverage already exists or overlaps this tenant period.");

            var segments = new List<UsageCoverageSegment>();
            if (cutover > period.StartUtc)
                segments.Add(await BuildSegmentAsync(tenant, period.StartUtc, cutover, UsageAuthoritativeSource.Legacy,
                    jobs.Where(x => x.CreatedOn < cutover).Select(x => x.Id.ToString()).ToList(),
                    applyPeriodAllowance: cutover == period.EndUtc, actor, reason, ct));
            if (cutover < period.EndUtc)
            {
                var canonicalEvents = events.Where(x => x.OccurredAtUtc >= cutover).ToList();
                var canonicalRatings = ratings.Where(x => canonicalEvents.Any(e => e.UsageEventId == x.UsageEventId)).ToList();
                segments.Add(BuildCanonicalSegment(tenantId, cutover, period.EndUtc, canonicalEvents, canonicalRatings, actor, reason));
            }
            db.AddRange(segments);
            policy.Mode = TenantMeterSourceMode.CanonicalAuthoritative; policy.CutoverAtUtc = cutover;
            policy.ApprovedBy = actor; policy.ApprovedAtUtc = DateTime.UtcNow;
            policy.ApprovalReason = reason.Trim(); policy.Version++;
            await db.SaveChangesAsync(ct);
            if (audit is not null) await audit(segments, ct);
            return segments;
        }, ct);
    }
    public async Task<ResolvedBillingUsage> ResolveAsync(
        long tenantId, BillingPeriod period, IReadOnlyList<MeterReading> legacyMeters,
        IReadOnlyCollection<string> invoiceableMeterKeys, CancellationToken ct = default)
    {
        var failures = new List<BillingReadinessFailure>();
        var resolved = legacyMeters.ToDictionary(x => x.MeterKey, StringComparer.Ordinal);
        var policies = await db.Set<TenantMeterSourcePolicy>().AsNoTracking()
            .Where(x => x.TenantId == tenantId).ToDictionaryAsync(x => x.MeterKey, StringComparer.Ordinal, ct);
        var manifestMeters = new List<object>();

        // Rating readiness is period-wide, not merely source-selection-wide. Shadow
        // capture is safe from double charging, but it is not reconcilable while an
        // event is unrated or failed and therefore cannot support a final invoice.
        var periodEvents = await db.Set<UsageEvent>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OccurredAtUtc >= period.StartUtc
                        && x.OccurredAtUtc < period.EndUtc).ToListAsync(ct);
        var periodEventIds = periodEvents.Select(x => x.UsageEventId).ToList();
        var allRatingRows = await db.Set<UsageEventRating>().AsNoTracking()
            .Where(x => x.TenantId == tenantId && periodEventIds.Contains(x.UsageEventId)).ToListAsync(ct);
        var effectiveRatings = allRatingRows.GroupBy(x => x.UsageEventId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(r => r.AttemptNumber).ThenByDescending(r => r.Id).First());
        var rateCardIds = effectiveRatings.Values.Where(x => x.RateCardId.HasValue)
            .Select(x => x.RateCardId!.Value).Distinct().ToList();
        var rateLineIds = effectiveRatings.Values.Where(x => x.RateCardLineId.HasValue)
            .Select(x => x.RateCardLineId!.Value).Distinct().ToList();
        var rateCards = await db.Set<RateCard>().AsNoTracking().Where(x => rateCardIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        var rateLines = await db.Set<RateCardLine>().AsNoTracking().Where(x => rateLineIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);
        foreach (var usageEvent in periodEvents)
        {
            var meterKey = CanonicalMeterKey(usageEvent.EventType);
            if (UsageMeterCatalog.ForEvent(usageEvent.EventType) is null)
            {
                failures.Add(new(BillingReadinessCodes.UnknownMeter, meterKey,
                    "The canonical event type is not in the closed meter catalog."));
                continue;
            }
            if (!effectiveRatings.TryGetValue(usageEvent.UsageEventId, out var rating)
                || rating.Status == UsageEventRatingResult.Unrated)
                failures.Add(new(BillingReadinessCodes.UnratedEvent, meterKey,
                    $"Usage event {usageEvent.UsageEventId:N} has no effective rating."));
            else if (rating.Status == UsageEventRatingResult.RatingFailed)
                failures.Add(new(BillingReadinessCodes.RatingFailed, meterKey,
                    $"Usage event {usageEvent.UsageEventId:N} rating failed ({rating.ReasonCode})."));
            else if (rating.Status is UsageEventRatingResult.Rated or UsageEventRatingResult.RatedZeroWithReason)
            {
                var validLineage = rating.RateCardId is long cardId && rating.RateCardLineId is long lineId
                    && rating.RateCardVersion is long version && rating.UnitPrice is decimal unitPrice
                    && rateCards.TryGetValue(cardId, out var card) && rateLines.TryGetValue(lineId, out var line)
                    && line.RateCardId == card.Id && card.Version == version
                    && card.EffectiveFromUtc <= usageEvent.OccurredAtUtc
                    && (card.EffectiveToUtc is null || card.EffectiveToUtc > usageEvent.OccurredAtUtc)
                    && string.Equals(card.Currency, rating.Currency, StringComparison.Ordinal)
                    && line.MeterKey == meterKey && line.UnitPrice == unitPrice;
                var expected = rating.UnitPrice is decimal price
                    ? decimal.Round(rating.OverageQuantity / BillingStatementService.PricedUnitDivisor(meterKey)
                                    * price, 6, MidpointRounding.AwayFromZero)
                    : (decimal?)null;
                if (!validLineage || rating.RatedAmount != expected
                    || rating.Status == UsageEventRatingResult.RatedZeroWithReason
                    && string.IsNullOrWhiteSpace(rating.ReasonCode))
                    failures.Add(new(BillingReadinessCodes.RatingLineageMismatch, meterKey,
                        $"Usage event {usageEvent.UsageEventId:N} rating does not match its event-time commercial lineage."));
            }
        }

        foreach (var meterKey in invoiceableMeterKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var certification = UsageMeterCatalog.BillingCertification(meterKey);
            if (certification != MeterCertificationStatus.BillingCertified)
                failures.Add(new(BillingReadinessCodes.UncertifiedMeter, meterKey,
                    $"Meter classification is {certification}."));

            var mode = policies.GetValueOrDefault(meterKey)?.Mode ?? TenantMeterSourceMode.LegacyAuthoritative;
            if (mode == TenantMeterSourceMode.BillingBlocked)
                failures.Add(new(BillingReadinessCodes.BillingBlocked, meterKey, "The meter source policy blocks billing."));

            if (mode != TenantMeterSourceMode.CanonicalAuthoritative)
            {
                manifestMeters.Add(new { meterKey, mode, source = "Legacy", quantity = resolved.GetValueOrDefault(meterKey)?.Quantity ?? 0m });
                continue;
            }

            var segments = await db.Set<UsageCoverageSegment>().AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.MeterKey == meterKey
                            && x.EndUtc > period.StartUtc && x.StartUtc < period.EndUtc)
                .OrderBy(x => x.StartUtc).ThenBy(x => x.EndUtc).ToListAsync(ct);
            // Legacy Npgsql timestamp behavior can materialize timestamptz values as
            // DateTimeKind.Local. Compare instants in UTC; raw DateTime operators compare
            // ticks and would otherwise manufacture a gap/overlap at the local offset.
            var periodStartUtc = NormalizeUtc(period.StartUtc);
            var periodEndUtc = NormalizeUtc(period.EndUtc);
            var cursor = periodStartUtc;
            foreach (var segment in segments)
            {
                var segmentStartUtc = NormalizeUtc(segment.StartUtc);
                var segmentEndUtc = NormalizeUtc(segment.EndUtc);
                if (segmentStartUtc > cursor)
                    failures.Add(new(BillingReadinessCodes.CoverageGap, meterKey,
                        $"No authoritative coverage starts at {cursor:O}."));
                if (segmentStartUtc < cursor)
                    failures.Add(new(BillingReadinessCodes.CoverageOverlap, meterKey,
                        $"Authoritative segment {segment.Id} overlaps another segment."));
                if (segment.Completeness != UsageCoverageCompleteness.Complete)
                    failures.Add(new(BillingReadinessCodes.CoverageIncomplete, meterKey,
                        $"Segment {segment.Id} is {segment.Completeness}."));
                if (segment.ReconciliationStatus is not (UsageReconciliationStatus.Matched
                    or UsageReconciliationStatus.WithinApprovedTolerance or UsageReconciliationStatus.NotApplicable))
                    failures.Add(new(BillingReadinessCodes.ReconciliationUnresolved, meterKey,
                        $"Segment {segment.Id} reconciliation is {segment.ReconciliationStatus}."));
                cursor = segmentEndUtc > cursor ? segmentEndUtc : cursor;
            }
            if (segments.Count == 0 || cursor < periodEndUtc)
                failures.Add(new(BillingReadinessCodes.CoverageGap, meterKey,
                    $"Coverage does not reach period end {periodEndUtc:O}."));

            var canonicalSegments = segments.Where(x => x.AuthoritativeSource == UsageAuthoritativeSource.Canonical).ToList();
            var events = canonicalSegments.Count == 0
                ? []
                : await db.Set<UsageEvent>().AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.OccurredAtUtc >= period.StartUtc
                                && x.OccurredAtUtc < period.EndUtc).ToListAsync(ct);
            events = events.Where(x => CanonicalMeterKey(x.EventType) == meterKey
                    && canonicalSegments.Any(s => IsWithin(x.OccurredAtUtc, s.StartUtc, s.EndUtc)))
                .ToList();
            foreach (var segment in canonicalSegments)
            {
                var segmentEvents = events.Where(x => IsWithin(x.OccurredAtUtc, segment.StartUtc, segment.EndUtc))
                    .ToList();
                var segmentRatings = segmentEvents.Where(x => effectiveRatings.ContainsKey(x.UsageEventId))
                    .Select(x => effectiveRatings[x.UsageEventId])
                    .Where(x => x.Status is UsageEventRatingResult.Rated or UsageEventRatingResult.RatedZeroWithReason)
                    .OrderBy(x => x.UsageEventId).ToList();
                var ratedIds = segmentRatings.Select(x => x.UsageEventId).ToHashSet();
                var ratedQuantity = segmentEvents.Where(x => ratedIds.Contains(x.UsageEventId)).Sum(x => x.Quantity);
                if (segment.EventCount != segmentRatings.Count || segment.QuantityTotal != ratedQuantity
                    || segment.AllowanceAppliedTotal != segmentRatings.Sum(x => x.AllowanceApplied)
                    || segment.OverageQuantityTotal != segmentRatings.Sum(x => x.OverageQuantity)
                    || segment.RatedAmountTotal != segmentRatings.Sum(x => x.RatedAmount ?? 0m)
                    || segmentRatings.Any(x => !string.Equals(x.Currency, segment.Currency, StringComparison.Ordinal))
                    || !string.Equals(segment.RateLineageSha256, ComputeRatingLineageSha256(segmentRatings),
                        StringComparison.Ordinal))
                    failures.Add(new(BillingReadinessCodes.RatingLineageMismatch, meterKey,
                        $"Segment {segment.Id} totals or rate lineage do not reconcile to effective event ratings."));
            }
            if (!failures.Any(x => x.MeterKey == meterKey))
            {
                var quantity = segments.Sum(x => x.QuantityTotal);
                var evidence = Hash(string.Join("\n", segments.Select(x => $"{x.Id}|{x.EvidenceSha256}|{x.QuantityTotal}")));
                resolved[meterKey] = new MeterReading(meterKey, quantity,
                    resolved.GetValueOrDefault(meterKey)?.Unit ?? events.FirstOrDefault()?.Unit ?? "unit",
                    $"Governed coverage segments {period.Key}; manifest sha256:{evidence} (tenant {tenantId})")
                {
                    AuthoritativeAllowanceApplied = segments.Sum(x => x.AllowanceAppliedTotal),
                    AuthoritativeOverageQuantity = segments.Sum(x => x.OverageQuantityTotal),
                    AuthoritativeRatedAmount = segments.Sum(x => x.RatedAmountTotal),
                    RateLineageManifestJson = JsonSerializer.Serialize(segments.Select(x => new
                    {
                        x.Id, x.AuthoritativeSource, x.Currency, x.RateLineageSha256, x.RateLineageJson
                    }))
                };
            }
            manifestMeters.Add(new
            {
                meterKey, mode, segments = segments.Select(x => new
                {
                    x.Id, x.StartUtc, x.EndUtc, x.AuthoritativeSource, x.Completeness,
                    x.EventCount, x.QuantityTotal, x.AllowanceAppliedTotal, x.OverageQuantityTotal,
                    x.RatedAmountTotal, x.Currency, x.RateLineageSha256, x.EvidenceSha256, x.CompletenessWatermarkUtc,
                    x.CutoverAtUtc, x.ReconciliationStatus, x.ApprovedBy, x.ApprovedAtUtc
                })
            });
        }

        var orderedFailures = failures.OrderBy(x => x.MeterKey, StringComparer.Ordinal)
            .ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Detail, StringComparer.Ordinal).ToList();
        var json = CanonicalizeJson(JsonSerializer.Serialize(
            new { tenantId, period = period.Key, meters = manifestMeters, failures = orderedFailures }));
        return new ResolvedBillingUsage(resolved.Values.OrderBy(x => x.MeterKey, StringComparer.Ordinal).ToList(),
            new BillingReadinessResult(orderedFailures.Count == 0, orderedFailures, json, Hash(json)));
    }

    private static bool IsWithin(DateTime instant, DateTime start, DateTime end)
    {
        var instantUtc = NormalizeUtc(instant);
        return instantUtc >= NormalizeUtc(start) && instantUtc < NormalizeUtc(end);
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    public static string CanonicalMeterKey(string eventType)
        => UsageMeterCatalog.ForEvent(eventType)?.BillingMeterKey ?? eventType;

    public static string ComputeRatingLineageSha256(IEnumerable<UsageEventRating> ratings)
        => Hash(string.Join("\n", ratings.OrderBy(x => x.UsageEventId).Select(x =>
            $"{x.UsageEventId:N}|{x.Status}|{x.RateCardId}|{x.RateCardLineId}|{x.RateCardVersion}|" +
            $"{x.Currency}|{D(x.AllowanceApplied)}|{D(x.OverageQuantity)}|{D(x.RatedAmount)}|{x.ReasonCode}")));

    private static string D(decimal? value)
        => value?.ToString("0.############################", CultureInfo.InvariantCulture) ?? "";

    private static void ValidateCoverageCommand(ProposeDocumentCoverageCommand command)
    {
        if (command.TenantId <= 0 || command.Period.EndUtc >= DateTime.UtcNow
            || command.MidPeriodCutoverUtc is DateTime cutover
            && (cutover <= command.Period.StartUtc || cutover >= command.Period.EndUtc)
            || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Trim().Length < 10)
            throw new UsageMeteringException(
                "Coverage requires a closed period, an optional timestamp strictly inside it, and a reason of at least 10 characters.");
    }

    private async Task<UsageCoverageSegment> BuildSegmentAsync(Tenant tenant, DateTime start, DateTime end,
        UsageAuthoritativeSource source, IReadOnlyList<string> sourceIds, bool applyPeriodAllowance,
        string actor, string reason, CancellationToken ct)
    {
        IQueryable<RateCard> cards = db.Set<RateCard>().AsNoTracking().Include(x => x.Lines)
            .Where(x => x.IsActive && x.EffectiveFromUtc <= start
                        && (x.EffectiveToUtc == null || x.EffectiveToUtc >= end));
        cards = tenant.RateCardId is long pinned ? cards.Where(x => x.Id == pinned) : cards;
        var matches = (await cards.ToListAsync(ct)).SelectMany(x => x.Lines.Select(line => new { Card = x, Line = line }))
            .Where(x => x.Line.MeterKey == BillingMeterKeys.Documents).ToList();
        if (matches.Count != 1)
            throw new UsageMeteringException("Legacy segment has no unambiguous effective documents rate for its complete interval.");
        var match = matches[0];
        var quantity = sourceIds.Count;
        if (!applyPeriodAllowance && match.Line.IncludedQuantity != 0)
            throw new UsageMeteringException("Mid-period cutover is ambiguous when the documents rate has a period allowance.");
        var allowance = applyPeriodAllowance ? Math.Min(quantity, match.Line.IncludedQuantity) : 0m;
        var overage = Math.Max(0m, quantity - allowance);
        var amount = decimal.Round(overage * match.Line.UnitPrice, 6, MidpointRounding.AwayFromZero);
        var lineage = JsonSerializer.Serialize(new[] { new { match.Card.Id, match.Card.Version,
            rateCardLineId = match.Line.Id, match.Line.UnitPrice, match.Card.Currency } });
        return NewSegment(tenant.Id, start, end, source, sourceIds.Count, quantity, allowance, overage, amount,
            match.Card.Currency, lineage, Hash(lineage), Hash(string.Join("\n", sourceIds.Order(StringComparer.Ordinal))),
            actor, reason);
    }

    private static UsageCoverageSegment BuildCanonicalSegment(long tenantId, DateTime start, DateTime end,
        IReadOnlyList<UsageEvent> events, IReadOnlyList<UsageEventRating> ratings, string actor, string reason)
    {
        var lineage = JsonSerializer.Serialize(ratings.OrderBy(x => x.UsageEventId).Select(x => new
        {
            x.UsageEventId, x.Status, x.RateCardId, x.RateCardLineId, x.RateCardVersion,
            x.Currency, x.AllowanceApplied, x.OverageQuantity, x.UnitPrice, x.RatedAmount, x.ReasonCode
        }));
        return NewSegment(tenantId, start, end, UsageAuthoritativeSource.Canonical, ratings.Count,
            events.Sum(x => x.Quantity), ratings.Sum(x => x.AllowanceApplied), ratings.Sum(x => x.OverageQuantity),
            ratings.Sum(x => x.RatedAmount ?? 0), ratings.Select(x => x.Currency).Distinct().Single(),
            lineage, ComputeRatingLineageSha256(ratings),
            Hash(string.Join("\n", events.OrderBy(x => x.UsageEventId).Select(x => $"{x.UsageEventId:N}|{x.EvidenceSha256}"))),
            actor, reason);
    }

    private static UsageCoverageSegment NewSegment(long tenantId, DateTime start, DateTime end,
        UsageAuthoritativeSource source, int eventCount, decimal quantity, decimal allowance, decimal overage,
        decimal amount, string currency, string lineage, string lineageHash, string evidenceHash,
        string actor, string reason) => new()
    {
        TenantId = tenantId, MeterKey = BillingMeterKeys.Documents, StartUtc = start, EndUtc = end,
        AuthoritativeSource = source, Completeness = UsageCoverageCompleteness.Complete,
        EventCount = eventCount, QuantityTotal = quantity, AllowanceAppliedTotal = allowance,
        OverageQuantityTotal = overage, RatedAmountTotal = amount, Currency = currency,
        RateLineageJson = lineage, RateLineageSha256 = lineageHash, EvidenceSha256 = evidenceHash,
        CompletenessWatermarkUtc = end, ReconciliationStatus = UsageReconciliationStatus.Matched,
        CutoverAtUtc = source == UsageAuthoritativeSource.Canonical ? start : null,
        ApprovedBy = actor, ApprovedAtUtc = DateTime.UtcNow, ApprovalReason = reason.Trim()
    };

    private async Task<T> InTransactionAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null) return await action();
        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var result = await action();
            await transaction.CommitAsync(ct);
            return result;
        });
    }

    private async Task LockAsync(long tenantId, string operation, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql() || db.Database.CurrentTransaction is null) return;
        unchecked
        {
            ulong hash = 14695981039346656037UL;
            foreach (var c in $"{tenantId}|{operation}") { hash ^= c; hash *= 1099511628211UL; }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({(long)hash})", ct);
        }
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string CanonicalizeJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream)) WriteCanonical(document.RootElement, writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                writer.WritePropertyName(property.Name);
                WriteCanonical(property.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray()) WriteCanonical(item, writer);
            writer.WriteEndArray();
        }
        else element.WriteTo(writer);
    }
}
