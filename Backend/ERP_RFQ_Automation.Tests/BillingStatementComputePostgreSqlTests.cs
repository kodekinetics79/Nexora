using System.Globalization;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification of billing statement COMPUTE — the one path
/// the portable (SQLite) lane structurally cannot certify.
///
/// PlatformBillingTests proves the meter aggregation and the charge math, but it
/// runs on SQLite, which ignores <c>varchar(n)</c> lengths entirely: every string
/// fits, so a statement line whose notes overflow its column is silently green
/// there and raises 22001 (string data right truncation) on PostgreSQL. The
/// resulting DbUpdateException matches neither of the compute path's catches
/// (unique violation / concurrency), so the API 500s and NO statement is
/// produced. That is the exact "green on SQLite, broken in production" trap, and
/// it is what this file exists to close: the whole compute runs end to end
/// against a real PostgreSQL with every meter priced, so the note columns are
/// certified by the write that actually happens in production.
///
/// It then finalizes a CLOSED historical period and re-certifies that
/// immutability still holds afterwards.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class BillingStatementComputePostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public BillingStatementComputePostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    /// <summary>
    /// The note columns must be UNBOUNDED text. Asserted directly against the
    /// live schema so the failure names the cause instead of surfacing as a
    /// mystery 22001 inside the compute test.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Statement_line_note_columns_are_unbounded_text_in_the_production_schema()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = 'platform'
              AND table_name = 'BillingStatementLines'
              AND column_name IN ('SourceNote', 'CoverageNote')
            ORDER BY column_name;
            """;

        var observed = new Dictionary<string, (string DataType, int? MaxLength)>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                observed[reader.GetString(0)] =
                    (reader.GetString(1), await reader.IsDBNullAsync(2) ? null : reader.GetInt32(2));
        }

        foreach (var column in new[] { "SourceNote", "CoverageNote" })
        {
            Assert.True(observed.ContainsKey(column),
                $"platform.\"BillingStatementLines\".\"{column}\" is missing — the billing note migration has not been applied.");
            Assert.True(observed[column].DataType == "text" && observed[column].MaxLength is null,
                $"platform.\"BillingStatementLines\".\"{column}\" is {observed[column].DataType}" +
                $"({observed[column].MaxLength?.ToString(CultureInfo.InvariantCulture) ?? "unbounded"}); " +
                "provenance and coverage notes routinely exceed any fixed width, and a bounded column turns " +
                "statement compute into a 22001 that no billing catch handles.");
        }
    }

    /// <summary>
    /// End-to-end: real ledgers → meters → rate card → persisted statement, on
    /// PostgreSQL, with every meter the card prices producing a line. Then
    /// Draft → Final on a closed period, and immutability re-certified in both
    /// the service (recompute returns the Final row unchanged) and the database
    /// (the guard trigger rejects UPDATE/DELETE).
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Compute_prices_every_meter_end_to_end_then_finalize_locks_the_statement()
    {
        var period = ClosedPeriod();
        var seed = await SeedAsync(period);

        try
        {
            long statementId;

            // ---------------------------------------------------------- compute
            await using (var context = _database.ContextFor(null))
            {
                var service = new BillingStatementService(
                    context, NullLogger<BillingStatementService>.Instance);

                // The usage readout first: the meters the statement is built from
                // must read the real ledgers, not defaults.
                var usage = await service.GetUsageAsync(seed.TenantId, period);
                Assert.Equal(seed.BusinessUnitId, usage.BusinessUnitId);
                Assert.Equal(2m, Meter(usage, BillingMeterKeys.Documents).Quantity);          // DeadLetter + out-of-period excluded
                Assert.Equal(5m, Meter(usage, BillingMeterKeys.PagesProcessed).Quantity);     // 3 + MAX(2,2) across retry attempts
                Assert.Equal(2m, Meter(usage, BillingMeterKeys.PagesOcr).Quantity);
                Assert.Equal(175_000m, Meter(usage, BillingMeterKeys.AiTokensExternal).Quantity);
                Assert.Equal(3m, Meter(usage, BillingMeterKeys.Seats).Quantity);
                Assert.Equal(3m * BillingMeterKeys.BytesPerGigabyte,                          // 3 GiB retained at period end
                    Meter(usage, BillingMeterKeys.StorageGb).Quantity);

                var statement = await service.ComputeStatementAsync(seed.TenantId, period, seed.RateCardId);
                statementId = statement.Id;
                Assert.Equal(BillingStatementStatus.Draft, statement.Status);
            }

            // The assertion that would have caught F1: the statement is actually
            // IN the database, with every line, read back through a fresh context.
            await using (var verification = _database.ContextFor(null))
            {
                var persisted = await verification.Set<BillingStatement>().AsNoTracking()
                    .Include(s => s.Lines)
                    .SingleAsync(s => s.Id == statementId);

                Assert.Equal(6, persisted.Lines.Count);
                Assert.Equal(ExpectedTotal, persisted.TotalAmount);
                Assert.Equal("USD", persisted.Currency);
                Assert.Equal(period.StartUtc, persisted.PeriodStartUtc);
                Assert.Equal(period.EndUtc, persisted.PeriodEndUtc);

                Assert.Equal(100.00m, Line(persisted, BillingMeterKeys.BaseSubscription).Amount);
                Assert.Equal(3.00m, Line(persisted, BillingMeterKeys.Documents).Amount);        // 2 x 1.50
                Assert.Equal(0.50m, Line(persisted, BillingMeterKeys.PagesProcessed).Amount);   // 5 x 0.10
                Assert.Equal(0.10m, Line(persisted, BillingMeterKeys.PagesOcr).Amount);         // 2 x 0.05
                Assert.Equal(0.18m, Line(persisted, BillingMeterKeys.AiTokensExternal).Amount); // 175 x 0.001 = 0.175 -> 0.18
                Assert.Equal(0.75m, Line(persisted, BillingMeterKeys.StorageGb).Amount);        // 3 GiB x 0.25

                // Meters are additive: seats are metered (3 above) but the card
                // carries no seats line, so nothing is charged for them.
                Assert.DoesNotContain(persisted.Lines, l => l.MeterKey == BillingMeterKeys.Seats);

                // F1 proper: the notes survived the real INSERT at full length.
                // The page/storage lines are the ones whose old concatenated
                // SourceNote ran to ~900 / ~576 characters against varchar(400).
                foreach (var meterKey in new[]
                         {
                             BillingMeterKeys.PagesProcessed, BillingMeterKeys.PagesOcr, BillingMeterKeys.StorageGb
                         })
                {
                    var line = Line(persisted, meterKey);
                    Assert.NotNull(line.SourceNote);
                    Assert.NotNull(line.CoverageNote);
                    Assert.True(line.SourceNote!.Length + line.CoverageNote!.Length > 400,
                        $"{meterKey}: the pre-fix concatenated note was only {line.SourceNote.Length + line.CoverageNote.Length} " +
                        "characters, so this test no longer covers the varchar(400) overflow.");
                    Assert.Equal(
                        meterKey == BillingMeterKeys.StorageGb
                            ? BillingStatementService.StorageCoverageNote
                            : BillingStatementService.PageSignalCoverageNote,
                        line.CoverageNote);
                    // Provenance stays provenance: the caveat is a column, not a suffix.
                    Assert.DoesNotContain("NOT BILLING-READY", line.SourceNote);
                }

                // Complete-signal meters carry no caveat at all.
                Assert.Null(Line(persisted, BillingMeterKeys.Documents).CoverageNote);
            }

            // --------------------------------------------------------- finalize
            await using (var context = _database.ContextFor(null))
            {
                var service = new BillingStatementService(
                    context, NullLogger<BillingStatementService>.Instance);

                // The period closed over a month ago, so it is well past the 48h
                // settle lag and finalizing is legitimate.
                var finalized = await service.FinalizeAsync(statementId, "billing@nexora.test");
                Assert.Equal(BillingStatementStatus.Final, finalized.Status);
                Assert.NotNull(finalized.FinalizedAtUtc);

                // Immutability, service layer: new usage after finalization does
                // NOT change the Final statement, and no second row appears.
                var recomputed = await service.ComputeStatementAsync(seed.TenantId, period, seed.RateCardId);
                Assert.Equal(statementId, recomputed.Id);
                Assert.Equal(BillingStatementStatus.Final, recomputed.Status);
                Assert.Equal(ExpectedTotal, recomputed.TotalAmount);
            }

            // Immutability, database layer: the guard trigger rejects writes to a
            // Final statement even from a raw connection with full privileges.
            await using (var connection = await _database.OpenConnectionAsync())
            {
                var update = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText =
                        """UPDATE platform."BillingStatements" SET "TotalAmount" = 1 WHERE "Id" = @id;""";
                    command.Parameters.AddWithValue("id", statementId);
                    await command.ExecuteNonQueryAsync();
                });
                Assert.Equal("55000", update.SqlState);

                var delete = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await using var command = connection.CreateCommand();
                    command.CommandText = """DELETE FROM platform."BillingStatements" WHERE "Id" = @id;""";
                    command.Parameters.AddWithValue("id", statementId);
                    await command.ExecuteNonQueryAsync();
                });
                Assert.Equal("55000", delete.SqlState);
            }

            await using (var verification = _database.ContextFor(null))
            {
                var rows = await verification.Set<BillingStatement>().AsNoTracking()
                    .Include(s => s.Lines)
                    .Where(s => s.TenantId == seed.TenantId)
                    .ToListAsync();
                var row = Assert.Single(rows); // duplicate-charge protection held
                Assert.Equal(BillingStatementStatus.Final, row.Status);
                Assert.Equal(ExpectedTotal, row.TotalAmount);
                Assert.Equal(6, row.Lines.Count);
            }
        }
        finally
        {
            await CleanupAsync(seed);
        }
    }

    // =============================================================== expectations

    /// <summary>
    /// 100.00 base + 3.00 documents + 0.50 pages + 0.10 OCR + 0.18 tokens + 0.75 storage.
    /// </summary>
    private const decimal ExpectedTotal = 104.53m;

    // ==================================================================== support

    /// <summary>
    /// A calendar month that closed long ago: two months back, so it is closed
    /// AND far past <see cref="BillingStatementService.FinalizeSettleLag"/> under
    /// any clock the suite runs on. Derived from UtcNow rather than hard-coded so
    /// the period never drifts into the future (compute) or into the settle lag
    /// (finalize) as the calendar moves.
    /// </summary>
    private static BillingPeriod ClosedPeriod()
    {
        var firstOfThisMonth = new DateTime(
            DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var key = firstOfThisMonth.AddMonths(-2).ToString("yyyy-MM", CultureInfo.InvariantCulture);
        Assert.True(BillingPeriod.TryParse(key, out var period));
        return period;
    }

    private static MeterReading Meter(TenantUsageReadout usage, string key)
        => usage.Meters.Single(m => m.MeterKey == key);

    private static BillingStatementLine Line(BillingStatement statement, string key)
        => statement.Lines.Single(l => l.MeterKey == key);

    private sealed record SeededFixture(
        long TenantId, long PlanId, long BusinessUnitId, long RateCardId);

    /// <summary>
    /// Seeds a tenant + plan + business unit, the source ledgers the meters read
    /// (billable extraction jobs with run evidence carrying page/OCR counts,
    /// evidence documents with byte sizes, settled external AI requests, seat
    /// holders) and a rate card pricing every meter the statement charges.
    /// </summary>
    private async Task<SeededFixture> SeedAsync(BillingPeriod period)
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        await using var context = _database.ContextFor(null);

        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = $"BILL{suffix}"[..12],
            BusinessUnitName = "Billing PG BU",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        var plan = new Plan
        {
            Code = $"pg-bill-{suffix}",
            Name = "Billing PG Plan",
            MonthlyPriceUsd = 100.00m,
            CreatedOn = DateTime.UtcNow
        };
        context.Add(businessUnit);
        context.Add(plan);
        await context.SaveChangesAsync();

        var tenant = new Tenant
        {
            Name = "Billing PG Tenant",
            Slug = $"billing-pg-{suffix}",
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = businessUnit.Id,
            PlanId = plan.Id,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        var rateCard = new RateCard
        {
            Code = $"pg-bill-{suffix}",
            Currency = "USD",
            EffectiveFromUtc = period.StartUtc.AddYears(-1),
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow,
            Lines =
            {
                new RateCardLine { MeterKey = BillingMeterKeys.Documents, IncludedQuantity = 0m, UnitPrice = 1.50m, Unit = "document" },
                new RateCardLine { MeterKey = BillingMeterKeys.PagesProcessed, IncludedQuantity = 0m, UnitPrice = 0.10m, Unit = "page" },
                new RateCardLine { MeterKey = BillingMeterKeys.PagesOcr, IncludedQuantity = 0m, UnitPrice = 0.05m, Unit = "page" },
                new RateCardLine { MeterKey = BillingMeterKeys.AiTokensExternal, IncludedQuantity = 0m, UnitPrice = 0.001m, Unit = "1K tokens" },
                new RateCardLine { MeterKey = BillingMeterKeys.StorageGb, IncludedQuantity = 0m, UnitPrice = 0.25m, Unit = "GB" }
            }
        };
        context.Add(tenant);
        context.Add(rateCard);
        await context.SaveChangesAsync();

        var bu = businessUnit.Id;
        var inPeriod = period.StartUtc.AddDays(2);
        var halfGigabyte = (long)(BillingMeterKeys.BytesPerGigabyte / 2m);
        var oneGigabyte = (long)BillingMeterKeys.BytesPerGigabyte;

        // 2 billable documents: 3 native pages, and 2 pages that were fully OCR'd
        // across TWO attempts (retry) — the meter must take MAX per job, not SUM.
        await SeedJobWithRunsAsync(context, bu, inPeriod, ExtractionStatus.Succeeded,
            pages: 3, ocrPages: 0, byteSize: halfGigabyte);
        await SeedJobWithRunsAsync(context, bu, inPeriod.AddDays(1), ExtractionStatus.Succeeded,
            pages: 2, ocrPages: 2, byteSize: halfGigabyte, attempts: 2);

        // Never delivered: excluded from documents AND pages (P0-B1), but its
        // bytes are still retained in the append-only evidence ledger, so storage
        // — a period-end ledger snapshot, not a job-status meter — still counts them.
        await SeedJobWithRunsAsync(context, bu, inPeriod.AddDays(2), ExtractionStatus.DeadLetter,
            pages: 40, ocrPages: 40, byteSize: oneGigabyte);

        // Received a month BEFORE the period: not a period document, but retained
        // storage at period end.
        await SeedJobWithRunsAsync(context, bu, period.StartUtc.AddDays(-20), ExtractionStatus.Succeeded,
            pages: 99, ocrPages: 9, byteSize: oneGigabyte);

        // Received AFTER the period closed: outside the period-end snapshot entirely.
        await SeedJobWithRunsAsync(context, bu, period.EndUtc.AddDays(3), ExtractionStatus.Succeeded,
            pages: 77, ocrPages: 7, byteSize: oneGigabyte * 4);

        // 175,000 settled external tokens in the period; local, failed and
        // out-of-period traffic must not reach the meter.
        context.AddRange(
            NewAiRequest(bu, AiProviderClass.External, AiCallStatuses.Succeeded, 100_000, 0, inPeriod),
            NewAiRequest(bu, AiProviderClass.External, AiCallStatuses.Succeeded, 50_000, 25_000, inPeriod.AddDays(1)),
            NewAiRequest(bu, AiProviderClass.Local, AiCallStatuses.Succeeded, 999_999, 1, inPeriod),
            NewAiRequest(bu, AiProviderClass.External, AiCallStatuses.Failed, 7_000, 0, inPeriod),
            NewAiRequest(bu, AiProviderClass.External, AiCallStatuses.Succeeded, 5_000, 5_000, period.StartUtc.AddDays(-5)));

        // 3 seat holders active through period end.
        for (var i = 0; i < 3; i++)
            context.Add(NewUser(bu, inPeriod));
        await context.SaveChangesAsync();

        return new SeededFixture(tenant.Id, plan.Id, bu, rateCard.Id);
    }

    /// <summary>
    /// One ExtractionJob plus its evidence trail — corpus, SourceDocument
    /// (ByteSize) and one ExtractionRun per attempt, each recording the same page
    /// / OCR counts — exactly the shape ExtractionWorker persists for retries.
    /// </summary>
    private static async Task SeedJobWithRunsAsync(
        ErpRfqAutomationContext context, long buId, DateTime createdOn, ExtractionStatus status,
        int pages, int ocrPages, long byteSize, int attempts = 1)
    {
        var job = new ExtractionJob
        {
            BusinessUnitId = buId,
            BatchId = Guid.NewGuid(),
            SourceType = ExtractionSourceType.ManualUpload,
            Status = status,
            ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
            StoragePath = "/uploads/billing-pg.pdf",
            CreatedOn = createdOn,
            UpdatedOn = createdOn,
            NextAttemptAt = createdOn
        };
        var corpus = DocumentCorpus.Create(buId, Guid.NewGuid(), CorpusSourceType.ManualUpload, createdOn);
        context.AddRange(job, corpus);
        await context.SaveChangesAsync();

        var document = SourceDocument.Create(buId, corpus.Id, job.ContentHash, "billing-pg.pdf",
            "application/pdf", "evidence", $"objects/{Guid.NewGuid():N}", "1", byteSize, createdOn);
        context.Add(document);
        await context.SaveChangesAsync();

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var run = ExtractionRun.Create(buId, document.Id, Guid.NewGuid(), job.Id, attempt,
                "llm-unstructured/v1", "lead-extraction/v1", createdOn);
            run.Start(createdOn);
            if (ocrPages > 0)
                run.RecordProcessingEvidence(ExtractionProcessingPath.LocalOcr,
                    ExtractionOcrStatus.Completed, ocrPages, ocrTruncated: false);
            run.Complete(pages, 0, 0, 0, 0, 0, createdOn.AddMinutes(1));
            context.Add(run);
        }

        await context.SaveChangesAsync();
    }

    private static AiRequest NewAiRequest(
        long buId, AiProviderClass providerClass, string status,
        long inputTokens, long outputTokens, DateTime createdOn) => new()
    {
        Id = Guid.NewGuid(),
        BusinessUnitId = buId,
        Operation = "RfqExtraction",
        IdempotencyKey = Guid.NewGuid().ToString("N"),
        // CK_AiRequests_HashShape is PostgreSQL-only and demands UPPERCASE hex —
        // one of several constraints the SQLite lane never sees.
        PromptHash = new string('A', 64),
        PromptVersion = "v1",
        Provider = providerClass == AiProviderClass.Local ? "local-ollama" : "external-api",
        ProviderClass = providerClass,
        Model = "test-model",
        Status = status,
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        CostStatus = AiCostStatuses.RateUnavailable,
        TokenSource = AiTokenSources.ProviderExact,
        CreatedOn = createdOn,
        CompletedOn = createdOn.AddSeconds(30)
    };

    private static User NewUser(long buId, DateTime createdOn) => new()
    {
        FirstName = "Seat",
        LastName = "Holder",
        Email = $"seat-{Guid.NewGuid():N}@nexora.test",
        PasswordHash = "hash",
        ImageUrl = "",
        Buid = buId,
        IsActive = true,
        CreatedBy = "tests",
        CreatedOn = createdOn
    };

    /// <summary>
    /// Fixture hygiene: the collection shares one database, so every row this
    /// test created is removed. Both governed ledgers it touched refuse deletes
    /// by design — the Final billing statement (immutability trigger) and the
    /// append-only evidence ledger — so the teardown disables triggers for THIS
    /// session only, exactly as PlatformControlPlaneHardeningPostgreSqlTests
    /// does. The guards being in the way is the point; suspending them is
    /// explicit, session-local, and restored immediately.
    /// </summary>
    private async Task CleanupAsync(SeededFixture seed)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SET session_replication_role = replica;
            DELETE FROM platform."BillingStatementLines"
             WHERE "BillingStatementId" IN (SELECT "Id" FROM platform."BillingStatements" WHERE "TenantId" = @tenantId);
            DELETE FROM platform."BillingStatements" WHERE "TenantId" = @tenantId;
            DELETE FROM platform."Tenants" WHERE "Id" = @tenantId;
            DELETE FROM platform."Plans" WHERE "Id" = @planId;
            DELETE FROM platform."RateCardLines" WHERE "RateCardId" = @rateCardId;
            DELETE FROM platform."RateCards" WHERE "Id" = @rateCardId;
            DELETE FROM extraction_runs WHERE business_unit_id = @businessUnitId;
            DELETE FROM source_documents WHERE business_unit_id = @businessUnitId;
            DELETE FROM document_corpora WHERE business_unit_id = @businessUnitId;
            DELETE FROM public."ExtractionJobs" WHERE "BusinessUnitId" = @businessUnitId;
            DELETE FROM public."AiRequests" WHERE "BusinessUnitId" = @businessUnitId;
            DELETE FROM public."Users" WHERE "BUID" = @businessUnitId;
            DELETE FROM public."BusinessUnits" WHERE "ID" = @businessUnitId;
            SET session_replication_role = origin;
            """;
        command.Parameters.AddWithValue("tenantId", seed.TenantId);
        command.Parameters.AddWithValue("planId", seed.PlanId);
        command.Parameters.AddWithValue("rateCardId", seed.RateCardId);
        command.Parameters.AddWithValue("businessUnitId", seed.BusinessUnitId);
        await command.ExecuteNonQueryAsync();
    }
}
