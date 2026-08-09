using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Metering;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Independent Wave 6 closure evidence for the production-dialect billing boundary.
/// These tests deliberately use the complete migrated PostgreSQL schema: SQLite cannot
/// certify advisory-lock allowance allocation, exclusion constraints, forced RLS or
/// append-only triggers.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Wave6BillingClosurePostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Distinct_checker_approves_a_complete_mid_period_cutover_and_readiness_uses_it()
    {
        var period = ClosedPeriod();
        var cutover = period.StartUtc.AddDays(14);
        var suffix = Guid.NewGuid().ToString("N");
        long businessUnitId;
        CommercialSeed seed;

        await using (var context = database.ContextFor(null))
        {
            var businessUnit = new BusinessUnit
            {
                BusinessUnitCode = $"CUT{suffix}"[..12], BusinessUnitName = "Closure cutover",
                IsActive = true, CreatedBy = "wave6-closure-sdet", CreatedOn = DateTime.UtcNow
            };
            var tenant = Tenant($"cutover-{suffix}");
            var card = Card($"cutover-{suffix}", period.StartUtc.AddMonths(-1), null, 2m, 0m);
            context.AddRange(businessUnit, tenant, card);
            await context.SaveChangesAsync();
            tenant.PrimaryBusinessUnitId = businessUnit.Id;
            tenant.RateCardId = card.Id;
            await context.SaveChangesAsync();
            businessUnitId = businessUnit.Id;
            seed = new CommercialSeed(tenant.Id, card.Id, card.Lines.Single().Id, card.Version);

            context.AddRange(
                Job(businessUnitId, period.StartUtc.AddDays(3), $"legacy-{suffix}"),
                Job(businessUnitId, period.StartUtc.AddDays(20), $"canonical-{suffix}"));
            await context.SaveChangesAsync();
        }

        await RecordAsync(RatedRequest(seed, 1m, period.StartUtc.AddDays(3), "cutover-legacy"));
        await RecordAsync(RatedRequest(seed, 1m, period.StartUtc.AddDays(20), "cutover-canonical"));

        await using (var context = database.ContextFor(null))
        {
            var readiness = new UsageBillingReadinessService(context);
            var proposal = await readiness.ProposeDocumentCoverageAsync(
                new ProposeDocumentCoverageCommand(seed.TenantId, period, cutover,
                    "Reconciled canonical document capture for closure certification."),
                "coverage-maker@example.test");
            Assert.Equal(TenantMeterSourceMode.CanonicalShadow, proposal.Mode);

            var segments = await readiness.ApproveDocumentCoverageAsync(seed.TenantId, period,
                "coverage-checker@example.test",
                "Independent checker approved exact reconciled cutover coverage.");
            Assert.Equal(2, segments.Count);
            var canonical = Assert.Single(segments, x =>
                x.AuthoritativeSource == UsageAuthoritativeSource.Canonical);
            Assert.Equal(cutover, canonical.StartUtc.ToUniversalTime());
            Assert.Equal(period.EndUtc, canonical.EndUtc.ToUniversalTime());
            Assert.Equal(UsageCoverageCompleteness.Complete, canonical.Completeness);
            Assert.Equal(UsageReconciliationStatus.Matched, canonical.ReconciliationStatus);
            Assert.Equal(1, canonical.EventCount);
            Assert.Equal(1m, canonical.QuantityTotal);
        }

        await using (var verify = database.ContextFor(null))
        {
            var policy = await verify.Set<TenantMeterSourcePolicy>().AsNoTracking().SingleAsync(x =>
                x.TenantId == seed.TenantId && x.MeterKey == BillingMeterKeys.Documents);
            Assert.Equal(TenantMeterSourceMode.CanonicalAuthoritative, policy.Mode);
            Assert.Equal("coverage-maker@example.test", policy.ProposedBy);
            Assert.Equal("coverage-checker@example.test", policy.ApprovedBy);
            Assert.Equal(cutover, policy.CutoverAtUtc!.Value.ToUniversalTime());

            var persisted = await verify.Set<UsageCoverageSegment>().AsNoTracking()
                .Where(x => x.TenantId == seed.TenantId && x.MeterKey == BillingMeterKeys.Documents)
                .OrderBy(x => x.StartUtc).ToListAsync();
            Assert.Equal(2, persisted.Count);
            Assert.Equal(period.StartUtc, persisted[0].StartUtc.ToUniversalTime());
            Assert.Equal(persisted[0].EndUtc.ToUniversalTime(), persisted[1].StartUtc.ToUniversalTime());
            Assert.Equal(period.EndUtc, persisted[1].EndUtc.ToUniversalTime());

            var resolved = await new UsageBillingReadinessService(verify).ResolveAsync(
                seed.TenantId, period,
                [new MeterReading(BillingMeterKeys.Documents, 2m, "document", "legacy source")],
                [BillingMeterKeys.Documents]);
            Assert.True(resolved.Readiness.Ready,
                string.Join("; ", resolved.Readiness.Failures.Select(x => $"{x.Code}:{x.Detail}"))
                + " | period=" + $"{period.StartUtc:O}({period.StartUtc.Kind})..{period.EndUtc:O}({period.EndUtc.Kind})"
                + " | segments=" + string.Join(",", persisted.Select(x =>
                    $"{x.Id}:{x.StartUtc:O}({x.StartUtc.Kind})..{x.EndUtc:O}({x.EndUtc.Kind})")));
            Assert.Equal(2m,
                Assert.Single(resolved.Meters, x => x.MeterKey == BillingMeterKeys.Documents).Quantity);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Canonical_shadow_never_replaces_the_legacy_authoritative_quantity()
    {
        var period = ClosedPeriod();
        var seed = await SeedCommercialTenantAsync(period, includedDocuments: 0);
        var request = RatedRequest(seed, 3m, period.StartUtc.AddDays(4), "shadow");

        await using (var context = database.ContextFor(null))
        {
            await new UsageMeteringService(context).RecordAsync(request);
            var policy = await context.Set<TenantMeterSourcePolicy>().SingleAsync(x =>
                x.TenantId == seed.TenantId && x.MeterKey == BillingMeterKeys.Documents);
            policy.Mode = TenantMeterSourceMode.CanonicalShadow;
            await context.SaveChangesAsync();
        }

        await using var verify = database.ContextFor(null);
        var resolved = await new UsageBillingReadinessService(verify).ResolveAsync(
            seed.TenantId, period,
            [new MeterReading(BillingMeterKeys.Documents, 11m, "document", "legacy fixture")],
            [BillingMeterKeys.Documents]);

        Assert.Equal(11m, Assert.Single(resolved.Meters, x => x.MeterKey == BillingMeterKeys.Documents).Quantity);
        Assert.True(resolved.Readiness.Ready);
        Assert.Contains("\"source\":\"Legacy\"", resolved.Readiness.ManifestJson);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Unrated_failed_unknown_and_uncertified_usage_are_named_readiness_failures()
    {
        var period = ClosedPeriod();
        var seed = await SeedCommercialTenantAsync(period, includedDocuments: 0);
        var unrated = await RecordPendingAsync(seed, period.StartUtc.AddDays(2), "unrated");
        var failed = await RecordPendingAsync(seed, period.StartUtc.AddDays(3), "failed");

        await using (var context = database.ContextFor(null))
        {
            context.Add(new UsageEventRating
            {
                TenantId = seed.TenantId,
                UsageEventId = failed,
                AttemptNumber = 2,
                IdempotencyKey = $"failed-{Guid.NewGuid():N}",
                Status = UsageEventRatingResult.RatingFailed,
                ReasonCode = "RATE_PROVIDER_FAILURE",
                Currency = "USD",
                OverageQuantity = 1m,
                OccurredAtUtc = period.StartUtc.AddDays(3),
                RatedAtUtc = DateTime.UtcNow,
                RatedBy = "rating-worker",
                EvidenceSha256 = Hash('f')
            });
            await context.SaveChangesAsync();
            context.Add(new UsageEvent
            {
                UsageEventId = Guid.NewGuid(), TenantId = seed.TenantId,
                Kind = UsageEventKind.Consumption, EventType = "unknown.closed.catalog", Quantity = 1m,
                Unit = "unit", OccurredAtUtc = period.StartUtc.AddDays(5), ReceivedAtUtc = DateTime.UtcNow,
                SourceRecordType = "closure-test", SourceRecordId = "unknown", SourceSystem = "sdet",
                CorrelationId = $"unknown-{Guid.NewGuid():N}", IdempotencyKey = $"unknown-{Guid.NewGuid():N}",
                Currency = "USD", EvidenceSha256 = Hash('u'), RatingStatus = UsageRatingStatus.Pending,
                OverageQuantity = 1m
            });
            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.CheckViolation,
                Assert.IsType<PostgresException>(rejected.InnerException).SqlState);
            context.ChangeTracker.Clear();
        }

        await using var verify = database.ContextFor(null);
        var resolved = await new UsageBillingReadinessService(verify).ResolveAsync(
            seed.TenantId, period,
            [new MeterReading(BillingMeterKeys.Documents, 2m, "document", "legacy fixture"),
             new MeterReading(BillingMeterKeys.PagesProcessed, 1m, "page", "uncertified fixture")],
            [BillingMeterKeys.Documents, BillingMeterKeys.PagesProcessed]);

        Assert.False(resolved.Readiness.Ready);
        Assert.Contains(resolved.Readiness.Failures, x =>
            x.Code == BillingReadinessCodes.UnratedEvent && x.Detail.Contains(unrated.ToString("N")));
        Assert.Contains(resolved.Readiness.Failures, x =>
            x.Code == BillingReadinessCodes.RatingFailed && x.Detail.Contains(failed.ToString("N")));
        Assert.Contains(resolved.Readiness.Failures, x =>
            x.Code == BillingReadinessCodes.UncertifiedMeter && x.MeterKey == BillingMeterKeys.PagesProcessed);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Readiness_failure_blocks_statement_finalization_and_out_of_band_final_invoice()
    {
        var period = ClosedPeriod();
        var seed = await SeedCommercialTenantAsync(period, includedDocuments: 0);
        await RecordPendingAsync(seed, period.StartUtc.AddDays(2), "finalize-block");
        long draftId;
        long maliciousFinalId;

        await using (var context = database.ContextFor(null))
        {
            var blockedManifest = UsageBillingReadinessService.CanonicalizeJson("{\"ready\":false}");
            var draft = Statement(seed, period, BillingStatementStatus.Draft,
                BillingReadinessStatus.Blocked, blockedManifest, HashUtf8(blockedManifest));
            var malicious = Statement(seed, period with
            {
                StartUtc = period.StartUtc.AddMonths(-1),
                EndUtc = period.EndUtc.AddMonths(-1)
            }, BillingStatementStatus.Draft, BillingReadinessStatus.Blocked,
                blockedManifest, HashUtf8(blockedManifest));
            context.AddRange(draft, malicious);
            await context.SaveChangesAsync();
            malicious.Status = BillingStatementStatus.Final;
            malicious.FinalizedAtUtc = DateTime.UtcNow.AddMonths(-1);
            malicious.FinalizedBy = "out-of-band";
            await context.SaveChangesAsync();
            draftId = draft.Id;
            maliciousFinalId = malicious.Id;
        }

        await using (var context = database.ContextFor(null))
        {
            var service = new BillingStatementService(context, NullLogger<BillingStatementService>.Instance);
            var refusal = await Assert.ThrowsAsync<BillingConflictException>(() =>
                service.FinalizeAsync(draftId, "distinct-owner@example.test"));
            Assert.Contains(BillingReadinessCodes.UnratedEvent, refusal.Message);
        }

        await using (var context = database.ContextFor(null))
        {
            var invoice = new SubscriptionInvoiceService(context);
            var refusal = await Assert.ThrowsAsync<BillingConflictException>(() => invoice.CreateDraftAsync(
                new CreateSubscriptionInvoice(maliciousFinalId, 0, "export exempt", "Nexora LLC", "NX-TAX"),
                "invoice-maker@example.test"));
            Assert.Contains("readiness manifest", refusal.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Rating_correction_uses_the_card_effective_at_event_time_not_the_current_card()
    {
        var period = ClosedPeriod();
        var suffix = Guid.NewGuid().ToString("N");
        long tenantId;
        long historicCardId;
        long historicLineId;
        await using (var context = database.ContextFor(null))
        {
            var tenant = Tenant($"lineage-{suffix}");
            var historic = Card($"historic-{suffix}", period.StartUtc.AddMonths(-1), period.EndUtc, 2m, 0m);
            var current = Card($"current-{suffix}", period.EndUtc, null, 9m, 0m);
            context.AddRange(tenant, historic, current);
            await context.SaveChangesAsync();
            tenant.RateCardId = historic.Id;
            await context.SaveChangesAsync();
            tenantId = tenant.Id;
            historicCardId = historic.Id;
            historicLineId = historic.Lines.Single().Id;
        }

        var eventId = Guid.NewGuid();
        await using (var context = database.ContextFor(null))
        {
            await new UsageMeteringService(context).RecordAsync(new RecordUsageEvent(
                eventId, tenantId, "documents", 4m, "document", period.StartUtc.AddDays(6),
                "closure-test", "historic-event", "sdet", null, null, null,
                $"lineage-{suffix}", $"lineage-{suffix}", 0m, "USD", Hash('l')));
        }

        await using (var context = database.ContextFor(null))
        {
            var corrected = await new UsageBillingReadinessService(context).CorrectRatingAsync(
                new CorrectUsageRatingCommand(eventId, $"correct-{suffix}",
                    "Resolve the event against its event-time commercial terms."),
                "billing-checker@example.test");
            Assert.Equal(historicCardId, corrected.RateCardId);
            Assert.Equal(historicLineId, corrected.RateCardLineId);
            Assert.Equal(2m, corrected.UnitPrice);
            Assert.Equal(8m, corrected.RatedAmount);
            Assert.Equal(2, corrected.AttemptNumber);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Coverage_gap_is_named_and_complete_segments_cannot_overlap()
    {
        var period = ClosedPeriod();
        var seed = await SeedCommercialTenantAsync(period, includedDocuments: 0);
        var first = Segment(seed.TenantId, period.StartUtc.AddDays(1), period.EndUtc, 'a');

        await using (var context = database.ContextFor(null))
        {
            var policy = await context.Set<TenantMeterSourcePolicy>().SingleAsync(x =>
                x.TenantId == seed.TenantId && x.MeterKey == BillingMeterKeys.Documents);
            policy.Mode = TenantMeterSourceMode.CanonicalAuthoritative;
            policy.CutoverAtUtc = period.StartUtc.AddDays(1);
            context.Add(first);
            await context.SaveChangesAsync();
        }

        await using (var context = database.ContextFor(null))
        {
            var resolved = await new UsageBillingReadinessService(context).ResolveAsync(
                seed.TenantId, period,
                [new MeterReading(BillingMeterKeys.Documents, 0m, "document", "legacy fixture")],
                [BillingMeterKeys.Documents]);
            Assert.Contains(resolved.Readiness.Failures, x => x.Code == BillingReadinessCodes.CoverageGap);
        }

        await using (var context = database.ContextFor(null))
        {
            context.Add(Segment(seed.TenantId, period.StartUtc.AddDays(2), period.EndUtc.AddDays(1), 'b'));
            var violation = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ExclusionViolation,
                Assert.IsType<PostgresException>(violation.InnerException).SqlState);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_rating_allocates_one_period_allowance_once_and_replays_idempotently()
    {
        var period = ClosedPeriod();
        var seed = await SeedCommercialTenantAsync(period, includedDocuments: 10m);
        var first = RatedRequest(seed, 7m, period.StartUtc.AddDays(1), "allowance-a");
        var second = RatedRequest(seed, 7m, period.StartUtc.AddDays(2), "allowance-b");

        var results = await Task.WhenAll(RecordAsync(first), RecordAsync(second));
        Assert.Equal(10m, results.Sum(x => x.AllowanceApplied));
        Assert.Equal(4m, results.Sum(x => x.OverageQuantity));
        Assert.Equal(8m, results.Sum(x => x.RatedAmount));

        var replays = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RecordAsync(first)));
        Assert.All(replays, x => Assert.Equal(first.UsageEventId, x.UsageEventId));

        await using var verify = database.ContextFor(null);
        Assert.Equal(2, await verify.Set<UsageEvent>().CountAsync(x => x.TenantId == seed.TenantId));
        Assert.Equal(2, await verify.Set<UsageEventRating>().CountAsync(x => x.TenantId == seed.TenantId));
        Assert.Equal(10m, await verify.Set<UsageEventRating>().Where(x => x.TenantId == seed.TenantId)
            .SumAsync(x => x.AllowanceApplied));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Migrated_rating_and_coverage_ledgers_are_append_only_and_tenant_roles_cannot_read_them()
    {
        var period = ClosedPeriod();
        var seed = await SeedCommercialTenantAsync(period, includedDocuments: 0);
        var usage = await RecordAsync(RatedRequest(seed, 1m, period.StartUtc.AddDays(1), "immutable"));
        long ratingId;
        long segmentId;
        await using (var context = database.ContextFor(null))
        {
            var segment = Segment(seed.TenantId, period.StartUtc, period.EndUtc, 'c');
            context.Add(segment);
            await context.SaveChangesAsync();
            segmentId = segment.Id;
            ratingId = await context.Set<UsageEventRating>().Where(x => x.UsageEventId == usage.UsageEventId)
                .Select(x => x.Id).SingleAsync();
        }

        await using (var connection = await database.OpenConnectionAsync())
        {
            foreach (var sql in new[]
                     {
                         $"UPDATE platform.\"UsageEventRatings\" SET \"RatedBy\"='tamper' WHERE \"Id\"={ratingId}",
                         $"DELETE FROM platform.\"UsageCoverageSegments\" WHERE \"Id\"={segmentId}"
                     })
            {
                await using var command = connection.CreateCommand();
                command.CommandText = sql;
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
                Assert.Equal("55000", refusal.SqlState);
            }
        }

        var tenantConnection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            Options = "-c role=nexora_tenant_app"
        }.ConnectionString;
        await using (var connection = new NpgsqlConnection(tenantConnection))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM platform.\"UsageEventRatings\"";
            var refusal = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refusal.SqlState);
        }
    }

    private async Task<UsageEvent> RecordAsync(RecordUsageEvent request)
    {
        await using var context = database.ContextFor(null);
        return await new UsageMeteringService(context).RecordAsync(request);
    }

    private async Task<Guid> RecordPendingAsync(CommercialSeed seed, DateTime occurredAt, string label)
    {
        var id = Guid.NewGuid();
        await using var context = database.ContextFor(null);
        await new UsageMeteringService(context).RecordAsync(new RecordUsageEvent(
            id, seed.TenantId, "documents", 1m, "document", occurredAt,
            "closure-test", label, "sdet", null, null, null,
            $"{label}-{id:N}", $"{label}-{id:N}", 0m, "USD", Hash('p')));
        return id;
    }

    private async Task<CommercialSeed> SeedCommercialTenantAsync(BillingPeriod period, decimal includedDocuments)
    {
        var suffix = Guid.NewGuid().ToString("N");
        await using var context = database.ContextFor(null);
        var tenant = Tenant($"closure-{suffix}");
        var card = Card($"closure-{suffix}", period.StartUtc.AddMonths(-1), null, 2m, includedDocuments);
        context.AddRange(tenant, card);
        await context.SaveChangesAsync();
        tenant.RateCardId = card.Id;
        await context.SaveChangesAsync();
        return new CommercialSeed(tenant.Id, card.Id, card.Lines.Single().Id, card.Version);
    }

    private static Tenant Tenant(string slug) => new()
    {
        Name = slug, LegalName = $"{slug} LLC", Slug = slug,
        Status = TenantStatus.Active, BillingMode = TenantBillingMode.Billable,
        BillingContactEmail = $"ap@{slug}.example.test", PaymentTermsDays = 30,
        CreatedBy = "wave6-closure-sdet", CreatedOn = DateTime.UtcNow
    };

    private static RateCard Card(string code, DateTime effectiveFrom, DateTime? effectiveTo,
        decimal unitPrice, decimal included) => new()
    {
        Code = code, Currency = "USD", IsActive = true, Version = 1,
        EffectiveFromUtc = effectiveFrom, EffectiveToUtc = effectiveTo,
        Lines = [new RateCardLine
        {
            MeterKey = BillingMeterKeys.Documents, Unit = "document",
            IncludedQuantity = included, UnitPrice = unitPrice
        }]
    };

    private static ExtractionJob Job(long businessUnitId, DateTime createdOn, string source) => new()
    {
        BusinessUnitId = businessUnitId, BatchId = Guid.NewGuid(),
        SourceType = ExtractionSourceType.ManualUpload, Status = ExtractionStatus.Succeeded,
        ContentHash = HashUtf8(source), StoragePath = $"/closure/{source}.pdf",
        CreatedOn = createdOn, UpdatedOn = createdOn, NextAttemptAt = createdOn
    };

    private static RecordUsageEvent RatedRequest(CommercialSeed seed, decimal quantity,
        DateTime occurredAt, string label)
    {
        var id = Guid.NewGuid();
        return new RecordUsageEvent(id, seed.TenantId, "documents", quantity, "document", occurredAt,
            "closure-test", label, "sdet", "sdet@example.test", null, null,
            $"{label}-{id:N}", $"{label}-{id:N}", 0m, "USD", Hash('r'),
            null, seed.RateCardId, seed.RateCardLineId, seed.RateCardVersion, 0m, 2m);
    }

    private static BillingStatement Statement(CommercialSeed seed, BillingPeriod period,
        BillingStatementStatus status, BillingReadinessStatus readiness,
        string manifest, string manifestHash) => new()
    {
        TenantId = seed.TenantId, RateCardId = seed.RateCardId,
        PeriodStartUtc = period.StartUtc, PeriodEndUtc = period.EndUtc,
        Status = status, Currency = "USD", TotalAmount = 2m,
        ReadinessStatus = readiness, ReadinessManifestJson = manifest,
        ReadinessManifestSha256 = manifestHash, ComputedAtUtc = DateTime.UtcNow,
        ComputedBy = "statement-maker@example.test",
        Lines = [new BillingStatementLine
        {
            MeterKey = BillingMeterKeys.Documents, Description = "Documents",
            MeteredQuantity = 1m, BillableQuantity = 1m, UnitPrice = 2m, Amount = 2m
        }]
    };

    private static UsageCoverageSegment Segment(long tenantId, DateTime start, DateTime end, char hash) => new()
    {
        TenantId = tenantId, MeterKey = BillingMeterKeys.Documents,
        StartUtc = start, EndUtc = end, AuthoritativeSource = UsageAuthoritativeSource.Canonical,
        Completeness = UsageCoverageCompleteness.Complete, Currency = "USD",
        RateLineageJson = "[]", RateLineageSha256 = Hash(hash), EvidenceSha256 = Hash(hash),
        CompletenessWatermarkUtc = end, CutoverAtUtc = start,
        ReconciliationStatus = UsageReconciliationStatus.Matched,
        ApprovedBy = "coverage-checker@example.test", ApprovedAtUtc = DateTime.UtcNow,
        ApprovalReason = "Independent closure coverage certification."
    };

    private static BillingPeriod ClosedPeriod()
    {
        var first = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(-2);
        return new BillingPeriod(first, first.AddMonths(1));
    }

    private static string Hash(char value) => new(value is >= '0' and <= '9' or >= 'a' and <= 'f' ? value : 'd', 64);

    private static string HashUtf8(string value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CommercialSeed(long TenantId, long RateCardId, long RateCardLineId, long RateCardVersion);
}
