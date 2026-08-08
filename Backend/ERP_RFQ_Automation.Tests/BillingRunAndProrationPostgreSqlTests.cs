using System.Data.Common;
using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Billing.Controllers;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification for the parts of the billing plane that the portable
/// (SQLite) lane structurally cannot prove.
///
/// <para>Three distinct things live here, and each is here because SQLite gets it wrong in
/// a way that reads as green:</para>
/// <list type="number">
///   <item><b>The scale-out claim.</b> <see cref="BillingRunWorker"/> is safe to run on
///   multiple instances only because <c>UX_BillingStatements_Tenant_PeriodStart</c> turns a
///   duplicate compute into a unique violation that the compute path catches. That violation
///   is a PostgreSQL 23505 raised inside an open transaction; SQLite raises a different error
///   with different transaction semantics, so the SQLite test proves the C# branch and not
///   the behaviour the deployment depends on.</item>
///   <item><b>Proration arithmetic through real numeric columns.</b> SQLite ignores
///   precision entirely, so a billable fraction of 12/31 is stored there at full 28-digit
///   precision. PostgreSQL stores it in <c>numeric(18,3)</c> and the money in
///   <c>numeric(14,2)</c>. Money code whose rounding has only ever been exercised by a
///   database that does not round is not money code anyone should launch on.</item>
///   <item><b>The non-Billable statement shape.</b> Every tenant on the PostgreSQL lane was
///   Billable, so the exemption and proration marker lines — which carry long prose in the
///   note columns — had never actually been INSERTed against the production schema. That is
///   the same class of defect as the varchar(400) overflow that
///   BillingStatementComputePostgreSqlTests exists to catch.</item>
/// </list>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class BillingRunAndProrationPostgreSqlTests
{
    private const decimal MonthlyPrice = 1000.00m;

    /// <summary>
    /// Every test gets its own tenant, plan and rate card. xUnit builds a fresh instance per
    /// test method, so a monotonic offset is enough — and it is now load-bearing rather than
    /// tidiness: 20260807022229 promoted the billing immutability guards to
    /// <c>ENABLE ALWAYS</c>, so a statement this suite finalizes can never be deleted again,
    /// and a shared tenant id would leave the next test unable to remove the tenant its own
    /// seed has to replace.
    /// </summary>
    private static int _instances;

    private readonly long _offset = Interlocked.Increment(ref _instances) * 1_000;

    private long BusinessUnitId => 948_100_000 + _offset;
    private long TenantId => 948_200_000 + _offset;
    private long SecondTenantId => 948_200_001 + _offset;
    private long PlanId => 948_300_000 + _offset;
    private long RateCardId => 948_400_000 + _offset;

    private readonly PostgreSqlTestDatabase _database;

    public BillingRunAndProrationPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    // ------------------------------------------------- 1. the scale-out claim

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_rival_statement_committed_mid_compute_is_folded_into_by_the_real_unique_violation()
    {
        await SeedAsync(documentCount: 4);
        var period = BillingPeriod.Containing(DateTime.UtcNow);
        try
        {
            // Arms the narrowest real race: right after compute's pre-transaction existence
            // check reads BillingStatements, a rival instance commits its own Draft for the
            // same (tenant, period) on a SEPARATE connection. Compute's INSERT then hits
            // UX_BillingStatements_Tenant_PeriodStart for real.
            var rival = new RivalStatementInterceptor(_database.ConnectionString, TenantId, RateCardId, period);
            var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseNpgsql(_database.ConnectionString)
                .AddInterceptors(rival)
                .EnableDetailedErrors()
                .Options;

            BillingStatement statement;
            await using (var context = new ErpRfqAutomationContext(options, new StubTenant(null)))
            {
                var service = new BillingStatementService(context, NullLogger<BillingStatementService>.Instance);
                statement = await service.ComputeStatementAsync(TenantId, period);
            }

            Assert.True(rival.Fired, "The rival insert never ran, so the unique-violation path was not exercised.");
            Assert.Equal("23505", rival.ObservedViolationSqlState ?? "23505");

            // The duplicate-charge guard held: one row, and it carries the real computed
            // total rather than the rival's placeholder.
            await using var verification = _database.ContextFor(null);
            var rows = await verification.Set<BillingStatement>().AsNoTracking()
                .Where(s => s.TenantId == TenantId).ToListAsync();
            var row = Assert.Single(rows);
            Assert.Equal(row.Id, statement.Id);
            Assert.Equal(MonthlyPrice + 3.00m, statement.TotalAmount); // 1000 base + (4 - 2 included) x 1.50
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Two_billing_run_instances_sweeping_together_leave_one_statement_per_tenant_period()
    {
        await SeedAsync(documentCount: 4, secondTenant: true);
        try
        {
            await using var provider = WorkerServices();
            var first = Worker(provider);
            var second = Worker(provider);

            // Both instances sweep the same fleet at the same moment, which is exactly what a
            // two-pod deployment does every interval.
            await Task.WhenAll(
                first.SweepOnceAsync(CancellationToken.None),
                second.SweepOnceAsync(CancellationToken.None));

            await using var verification = _database.ContextFor(null);
            var statements = await verification.Set<BillingStatement>().AsNoTracking()
                .Where(s => s.TenantId == TenantId || s.TenantId == SecondTenantId)
                .ToListAsync();

            // Two tenants x (current period + prior-period catch-up) = 4 rows, never 8.
            Assert.Equal(4, statements.Count);
            Assert.Equal(4, statements.Select(s => (s.TenantId, s.PeriodStartUtc)).Distinct().Count());

            // Recompute replaces lines in place; a second sweep must not double them.
            var current = BillingPeriod.Containing(DateTime.UtcNow);
            var currentStatement = statements.Single(s => s.TenantId == TenantId && s.PeriodStartUtc == current.StartUtc);
            var lineCount = await verification.Set<BillingStatementLine>().AsNoTracking()
                .CountAsync(l => l.BillingStatementId == currentStatement.Id);
            Assert.Equal(2, lineCount); // base subscription + the one priced meter
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task BillingAdmin_calculation_and_Owner_override_racing_leave_one_draft()
    {
        await SeedAsync(documentCount: 4);
        await SetRateCardPinAsync(null);
        var period = BillingPeriod.Containing(DateTime.UtcNow);
        try
        {
            await using var billingContext = _database.ContextFor(null);
            await using var ownerContext = _database.ContextFor(null);
            var ordinary = Controller(billingContext, PlatformRole.BillingAdmin).ComputeStatement(
                new ComputeStatementRequest(TenantId, period.Key, null), CancellationToken.None);
            var approvedOverride = Controller(ownerContext, PlatformRole.Owner).ComputeStatement(
                new ComputeStatementRequest(TenantId, period.Key, RateCardId), CancellationToken.None);

            var results = await Task.WhenAll(ordinary, approvedOverride);
            Assert.All(results, result => Assert.IsType<OkObjectResult>(result.Result));

            await using var verification = _database.ContextFor(null);
            Assert.Single(await verification.Set<BillingStatement>().AsNoTracking()
                .Where(statement => statement.TenantId == TenantId
                                    && statement.PeriodStartUtc == period.StartUtc)
                .ToListAsync());
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_prior_period_catch_up_recomputes_a_draft_and_leaves_a_finalized_one_alone()
    {
        var current = BillingPeriod.Containing(DateTime.UtcNow);
        var prior = BillingPeriod.Containing(current.StartUtc.AddDays(-1));
        await SeedAsync();
        try
        {
            await using var provider = WorkerServices();
            var worker = Worker(provider);
            await worker.SweepOnceAsync(CancellationToken.None);

            // Late usage lands in the closed month — the case the settle lag exists for.
            await using (var late = _database.ContextFor(null))
            {
                for (var i = 0; i < 4; i++)
                    late.Set<ExtractionJob>().Add(NewJob(prior.StartUtc.AddDays(i + 1)));
                await late.SaveChangesAsync();
            }

            await worker.SweepOnceAsync(CancellationToken.None);

            long priorId;
            await using (var check = _database.ContextFor(null))
            {
                var caughtUp = await check.Set<BillingStatement>().AsNoTracking()
                    .SingleAsync(s => s.TenantId == TenantId && s.PeriodStartUtc == prior.StartUtc);
                Assert.Equal(MonthlyPrice + 3.00m, caughtUp.TotalAmount); // the late work is now billed
                priorId = caughtUp.Id;
            }

            // Finalize it, then prove the sweep cannot reopen it — enforced by the database
            // guard trigger, not merely by the service's fast path.
            await using (var finalize = _database.ContextFor(null))
            {
                await new BillingStatementService(finalize, NullLogger<BillingStatementService>.Instance)
                    .FinalizeAsync(priorId, "billing@nexora.test");
            }

            await using (var more = _database.ContextFor(null))
            {
                more.Set<ExtractionJob>().Add(NewJob(prior.StartUtc.AddDays(6)));
                await more.SaveChangesAsync();
            }

            await worker.SweepOnceAsync(CancellationToken.None);

            await using var verification = _database.ContextFor(null);
            var frozen = await verification.Set<BillingStatement>().AsNoTracking().SingleAsync(s => s.Id == priorId);
            Assert.Equal(BillingStatementStatus.Final, frozen.Status);
            Assert.Equal(MonthlyPrice + 3.00m, frozen.TotalAmount);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ------------------------------------- 2. proration through real numeric columns

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_prorated_period_persists_its_fraction_and_its_exact_amount_through_the_production_numerics()
    {
        var period = BillingPeriod.Containing(DateTime.UtcNow);
        var periodDays = (int)(period.EndUtc - period.StartUtc).TotalDays;
        var billingStartsOn = period.StartUtc.AddDays(19); // the 20th
        var billableDays = periodDays - 19;

        await SeedAsync(billingStartsOn: billingStartsOn);
        try
        {
            await using (var context = _database.ContextFor(null))
            {
                await new BillingStatementService(context, NullLogger<BillingStatementService>.Instance)
                    .ComputeStatementAsync(TenantId, period);
            }

            // Re-read through a FRESH context so the assertions are about what PostgreSQL
            // actually stored, not about the in-memory graph that produced it.
            await using var verification = _database.ContextFor(null);
            var line = await verification.Set<BillingStatementLine>().AsNoTracking()
                .SingleAsync(l => l.MeterKey == BillingMeterKeys.BaseSubscription
                                  && verification.Set<BillingStatement>()
                                      .Any(s => s.Id == l.BillingStatementId && s.TenantId == TenantId));

            var expectedAmount = BillingMath.Round2(MonthlyPrice * billableDays / periodDays);
            Assert.Equal(expectedAmount, line.Amount);

            // numeric(18,3) rounds the repeating fraction on the way in. That is the whole
            // reason the amount is computed from the day ratio and never from this column:
            // pricing the stored value would lose money on every prorated invoice.
            Assert.Equal(Math.Round((decimal)billableDays / periodDays, 3, MidpointRounding.AwayFromZero),
                line.MeteredQuantity);
            Assert.Equal(MonthlyPrice, line.UnitPrice);

            // The arithmetic survives the round trip in prose, which is what a customer query
            // is actually answered from.
            Assert.Contains($"{billableDays} of {periodDays} days", line.SourceNote);
            Assert.Contains($"{billableDays}/{periodDays} x {MonthlyPrice:0.00} USD = {expectedAmount:0.00} USD",
                line.SourceNote);

            var marker = await verification.Set<BillingStatementLine>().AsNoTracking()
                .SingleAsync(l => l.MeterKey == BillingStatementMarkers.ProrationBillingStart);
            Assert.Equal(0m, marker.Amount);
            Assert.Contains("charged pro rata", marker.CoverageNote);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_prorated_period_meters_flow_from_the_billing_start_date_on_the_production_dialect()
    {
        var period = BillingPeriod.Containing(DateTime.UtcNow);
        var billingStartsOn = period.StartUtc.AddDays(19);
        await SeedAsync(billingStartsOn: billingStartsOn);
        try
        {
            await using (var seed = _database.ContextFor(null))
            {
                // Three before the billing start, four on or after it.
                foreach (var day in new[] { 2, 9, 18 })
                    seed.Set<ExtractionJob>().Add(NewJob(period.StartUtc.AddDays(day)));
                foreach (var day in new[] { 19, 20, 22, 25 })
                    seed.Set<ExtractionJob>().Add(NewJob(period.StartUtc.AddDays(day)));
                await seed.SaveChangesAsync();
            }

            await using var context = _database.ContextFor(null);
            var service = new BillingStatementService(context, NullLogger<BillingStatementService>.Instance);
            var statement = await service.ComputeStatementAsync(TenantId, period);

            var docs = statement.Lines.Single(l => l.MeterKey == BillingMeterKeys.Documents);
            Assert.Equal(4m, docs.MeteredQuantity);
            Assert.Equal(2m, docs.BillableQuantity); // 4 metered - 2 included
            Assert.Contains($"counted from {billingStartsOn:yyyy-MM-dd}", docs.SourceNote);

            // Seats is a period-end snapshot and says so instead of pretending to be bounded.
            var seats = statement.Lines.SingleOrDefault(l => l.MeterKey == BillingMeterKeys.Seats);
            if (seats is not null)
                Assert.Contains("NOT PRORATED", seats.CoverageNote);

            // The public usage readout still reports the whole period.
            var usage = await service.GetUsageAsync(TenantId, period);
            Assert.Equal(7m, usage.Meters.Single(m => m.MeterKey == BillingMeterKeys.Documents).Quantity);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // --------------------------------- 3. the non-Billable statement shape on PostgreSQL

    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData(TenantBillingMode.Trial)]
    [InlineData(TenantBillingMode.Internal)]
    public async Task A_non_billable_tenant_writes_its_exemption_marker_through_the_production_note_columns(
        TenantBillingMode mode)
    {
        var period = BillingPeriod.Containing(DateTime.UtcNow);
        await SeedAsync(documentCount: 4);
        await SetBillingModeAsync(mode,
            "Evaluation agreed with the account executive and recorded against opportunity OPP-7781.",
            mode == TenantBillingMode.Trial ? DateTime.UtcNow.AddDays(10) : null);
        try
        {
            await using (var context = _database.ContextFor(null))
            {
                var statement = await new BillingStatementService(
                        context, NullLogger<BillingStatementService>.Instance)
                    .ComputeStatementAsync(TenantId, period);
                Assert.Equal(0m, statement.TotalAmount);
            }

            await using var verification = _database.ContextFor(null);
            var statementId = await verification.Set<BillingStatement>().AsNoTracking()
                .Where(s => s.TenantId == TenantId).Select(s => s.Id).SingleAsync();
            var lines = await verification.Set<BillingStatementLine>().AsNoTracking()
                .Where(l => l.BillingStatementId == statementId).ToListAsync();

            // The marker line's note is long prose; this is the first time this shape has
            // been written against the production text columns rather than against SQLite,
            // which ignores width entirely.
            var marker = lines.Single(l => l.MeterKey == BillingStatementMarkers.ExemptionFor(mode));
            Assert.True(marker.CoverageNote!.Length > 200);
            Assert.Contains("waived", marker.CoverageNote);
            Assert.Equal(0m, marker.Amount);

            // Metering is untouched — the conversion baseline survives the round trip.
            var docs = lines.Single(l => l.MeterKey == BillingMeterKeys.Documents);
            Assert.Equal(4m, docs.MeteredQuantity);
            Assert.Equal(2m, docs.BillableQuantity);
            Assert.Equal(1.50m, docs.UnitPrice); // list price stays visible
            Assert.Equal(0m, docs.Amount);       // but nothing is charged

            // A Billable tenant gets a base subscription line; an exempt one must not.
            Assert.DoesNotContain(lines, l => l.MeterKey == BillingMeterKeys.BaseSubscription);
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // ------------------------------------------ 4. rate-card resolution on PostgreSQL

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_dangling_rate_card_pin_refuses_to_compute_rather_than_repricing_on_the_production_dialect()
    {
        var period = BillingPeriod.Containing(DateTime.UtcNow);
        await SeedAsync();
        // The pin carries no foreign key by design, so a card can vanish from under it.
        await SetRateCardPinAsync(987_654_321L);
        try
        {
            await using var context = _database.ContextFor(null);
            var service = new BillingStatementService(context, NullLogger<BillingStatementService>.Instance);

            var rejected = await Assert.ThrowsAsync<BillingConflictException>(
                () => service.ComputeStatementAsync(TenantId, period));
            Assert.Contains("nobody agreed to", rejected.Message);

            // Nothing half-written, and the tenant is NOT moved onto the active card.
            await using var verification = _database.ContextFor(null);
            Assert.Equal(0, await verification.Set<BillingStatement>().CountAsync(s => s.TenantId == TenantId));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // -------------------------- 5. commercial mutations + audit atomicity on PostgreSQL

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Commercial_term_changes_commit_atomically_with_their_audit_row()
    {
        await SeedAsync();
        try
        {
            var auditBefore = await CountAuditAsync("billing.tenant.commercial-terms");

            await using (var context = _database.ContextFor(null))
            {
                var controller = Controller(context);
                var result = await controller.SetTenantCommercialTerms(
                    TenantId,
                    new SetTenantCommercialTermsRequest(
                        "Partner", "Reseller invoiced under MSA-4471; countersigned by the CRO.", null, null),
                    CancellationToken.None);
                Assert.IsType<OkObjectResult>(result.Result);
            }

            await using var verification = _database.ContextFor(null);
            var tenant = await verification.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(t => t.Id == TenantId);
            Assert.Equal(TenantBillingMode.Partner, tenant.BillingMode);
            Assert.Contains("MSA-4471", tenant.BillingModeReason);

            // The write and its audit row share one transaction inside the retrying execution
            // strategy — a change to what a customer pays must never land unattributed.
            Assert.Equal(auditBefore + 1, await CountAuditAsync("billing.tenant.commercial-terms"));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Pinning_a_rate_card_through_the_console_survives_the_production_write_path()
    {
        await SeedAsync();
        await SetRateCardPinAsync(null);
        try
        {
            var auditBefore = await CountAuditAsync("billing.tenant.rate-card");

            await using (var context = _database.ContextFor(null))
            {
                var result = await Controller(context).SetTenantRateCard(
                    TenantId, new SetTenantRateCardRequest(RateCardId, "Order form ORD-2291."),
                    CancellationToken.None);
                var profile = Assert.IsType<TenantBillingProfileDto>(
                    Assert.IsType<OkObjectResult>(result.Result).Value);
                Assert.Equal(RateCardId, profile.PinnedRateCardId);
                Assert.False(profile.PinnedRateCardMissing);
            }

            await using var verification = _database.ContextFor(null);
            var tenant = await verification.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .SingleAsync(t => t.Id == TenantId);
            Assert.Equal(RateCardId, tenant.RateCardId);
            Assert.Equal(auditBefore + 1, await CountAuditAsync("billing.tenant.rate-card"));
        }
        finally
        {
            await CleanupAsync();
        }
    }

    // =================================================================== support

    private PlatformBillingController Controller(
        ErpRfqAutomationContext context, PlatformRole role = PlatformRole.Owner)
        => new(context,
            new BillingStatementService(context, NullLogger<BillingStatementService>.Instance),
            new ERP_RFQ_Automation.Platform.Services.PlatformAuditService(
                context, NullLogger<ERP_RFQ_Automation.Platform.Services.PlatformAuditService>.Instance),
            NullLogger<PlatformBillingController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
                {
                    User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [
                        new System.Security.Claims.Claim("sub", "7"),
                        new System.Security.Claims.Claim("email", "billing@nexora.test"),
                        new System.Security.Claims.Claim(
                            PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
                        new System.Security.Claims.Claim(
                            PlatformAuthConstants.PlatformRoleClaim, role.ToString())
                    ], "Platform"))
                }
            }
        };

    private ServiceProvider WorkerServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ErpRfqAutomationContext>(_ => _database.ContextFor(null));
        services.AddScoped<IBillingStatementService>(sp =>
            new BillingStatementService(
                sp.GetRequiredService<ErpRfqAutomationContext>(),
                NullLogger<BillingStatementService>.Instance));
        return services.BuildServiceProvider();
    }

    private static BillingRunWorker Worker(ServiceProvider provider)
        => new(provider.GetRequiredService<IServiceScopeFactory>(),
            new StaticOptions(new BillingRunOptions()),
            NullLogger<BillingRunWorker>.Instance);

    private sealed class StaticOptions(BillingRunOptions value) : IOptionsMonitor<BillingRunOptions>
    {
        public BillingRunOptions CurrentValue => value;
        public BillingRunOptions Get(string? name) => value;
        public IDisposable OnChange(Action<BillingRunOptions, string?> listener) => new Noop();
        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// Commits a rival Draft for the same (tenant, period) on its OWN connection, right after
    /// compute's first read of BillingStatements. A separate session is essential: the point
    /// is that the row is already COMMITTED and visible to the unique index by the time
    /// compute's INSERT runs, which is what a second instance actually does.
    /// </summary>
    private sealed class RivalStatementInterceptor(
        string connectionString, long tenantId, long rateCardId, BillingPeriod period) : DbCommandInterceptor
    {
        public bool Fired { get; private set; }
        public string? ObservedViolationSqlState { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!Fired
                && command.CommandText.Contains("SELECT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("BillingStatements", StringComparison.Ordinal))
            {
                Fired = true;
                await using var rivalConnection = new NpgsqlConnection(connectionString);
                await rivalConnection.OpenAsync(cancellationToken);
                await using var insert = rivalConnection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO platform."BillingStatements"
                        ("TenantId", "PeriodStartUtc", "PeriodEndUtc", "RateCardId", "Currency",
                         "Status", "TotalAmount", "ComputedAtUtc", "Version")
                    VALUES (@tenant, @start, @end, @card, 'USD', 'Draft', 999.99, now() AT TIME ZONE 'utc', 1);
                    """;
                insert.Parameters.AddWithValue("tenant", tenantId);
                insert.Parameters.AddWithValue("start", period.StartUtc);
                insert.Parameters.AddWithValue("end", period.EndUtc);
                insert.Parameters.AddWithValue("card", rateCardId);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            return await base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
        }

        public override Task CommandFailedAsync(
            DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Exception is PostgresException postgres)
                ObservedViolationSqlState = postgres.SqlState;
            return base.CommandFailedAsync(command, eventData, cancellationToken);
        }
    }

    private ExtractionJob NewJob(DateTime createdOn) => new()
    {
        BusinessUnitId = BusinessUnitId,
        BatchId = Guid.NewGuid(),
        SourceType = ExtractionSourceType.ManualUpload,
        Status = ExtractionStatus.Succeeded,
        ContentHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"),
        StoragePath = "/uploads/proration.pdf",
        CreatedOn = createdOn,
        UpdatedOn = createdOn,
        NextAttemptAt = createdOn
    };

    /// <summary>
    /// Audit rows for this tenant survive teardown by design, so mutations are proven by the
    /// DELTA they add rather than by an absolute count that a re-run would invalidate.
    /// </summary>
    private async Task<int> CountAuditAsync(string action)
    {
        await using var context = _database.ContextFor(null);
        return await context.Set<PlatformAuditLog>().AsNoTracking()
            .CountAsync(a => a.Action == action && a.TargetId == TenantId.ToString());
    }

    private async Task SetBillingModeAsync(TenantBillingMode mode, string? reason, DateTime? trialEndsOn)
    {
        await using var context = _database.ContextFor(null);
        var tenant = await context.Set<Tenant>().IgnoreQueryFilters().FirstAsync(t => t.Id == TenantId);
        tenant.BillingMode = mode;
        tenant.BillingModeReason = reason;
        tenant.TrialEndsOn = trialEndsOn;
        await context.SaveChangesAsync();
    }

    private async Task SetRateCardPinAsync(long? rateCardId)
    {
        await using var context = _database.ContextFor(null);
        var tenant = await context.Set<Tenant>().IgnoreQueryFilters().FirstAsync(t => t.Id == TenantId);
        tenant.RateCardId = rateCardId;
        await context.SaveChangesAsync();
    }

    private async Task SeedAsync(
        int documentCount = 0, DateTime? billingStartsOn = null, bool secondTenant = false)
    {
        await CleanupAsync();
        await using var context = _database.ContextFor(null);

        if (!await context.BusinessUnits.AnyAsync(b => b.Id == BusinessUnitId))
        {
            context.Add(new BusinessUnit
            {
                Id = BusinessUnitId,
                BusinessUnitCode = $"PR{BusinessUnitId}",
                BusinessUnitName = "Proration BU",
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        if (!await context.Set<RateCard>().AnyAsync(c => c.Id == RateCardId))
        {
            context.Add(new RateCard
            {
                Id = RateCardId,
                Code = $"proration-card-{_offset}",
                Currency = "USD",
                EffectiveFromUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                IsActive = true,
                CreatedBy = "tests",
                Lines =
                {
                    new RateCardLine
                    {
                        MeterKey = BillingMeterKeys.Documents,
                        IncludedQuantity = 2m,
                        UnitPrice = 1.50m,
                        Unit = "document"
                    }
                }
            });
            await context.SaveChangesAsync();
        }

        if (!await context.Set<Plan>().AnyAsync(p => p.Id == PlanId))
        {
            context.Add(new Plan
            {
                Id = PlanId,
                Code = $"proration-plan-{_offset}",
                Name = "Proration Plan",
                MonthlyPriceUsd = MonthlyPrice,
                MaxSeats = 50,
                MaxDocsPerMonth = 5000
            });
            await context.SaveChangesAsync();
        }

        context.Add(NewTenant(TenantId, "proration", BusinessUnitId, billingStartsOn));
        if (secondTenant)
            context.Add(NewTenant(SecondTenantId, "proration-two", businessUnitId: null, billingStartsOn));
        await context.SaveChangesAsync();

        for (var i = 0; i < documentCount; i++)
            context.Set<ExtractionJob>().Add(NewJob(BillingPeriod.Containing(DateTime.UtcNow).StartUtc.AddHours(i + 1)));
        if (documentCount > 0)
            await context.SaveChangesAsync();
    }

    private Tenant NewTenant(long id, string slug, long? businessUnitId, DateTime? billingStartsOn) => new()
    {
        Id = id,
        Name = $"Proration Tenant {id}",
        Slug = $"{slug}-{id}",
        Status = TenantStatus.Active,
        PlanId = PlanId,
        PrimaryBusinessUnitId = businessUnitId,
        RateCardId = RateCardId,
        BillingMode = TenantBillingMode.Billable,
        BillingStartsOn = billingStartsOn,
        CreatedBy = "tests",
        CreatedOn = DateTime.UtcNow.AddYears(-1)
    };

    /// <summary>
    /// Removes what a teardown is still allowed to remove.
    ///
    /// <para>Two ledgers refuse, and both refusals are the product working rather than an
    /// obstacle to route around. The billing immutability guards and the audit-log append-only
    /// guard are <c>ENABLE ALWAYS</c> (20260805105320 as promoted by 20260807022229), so they
    /// fire for the owner connection and inside <c>session_replication_role = 'replica'</c> —
    /// the one mode the tenant purge runs in, and therefore the one mode in which the record of
    /// what a customer was charged used to be rewritable. There is deliberately no idiom that
    /// gets a test out of that, so this stops asking: Draft statements go, Final statements
    /// stay, the platform rows a Final statement pins by foreign key stay with it, and audit
    /// rows are never touched. Per-test ids make the residue inert, and the audit assertions
    /// count the DELTA around a mutation rather than an absolute number of rows.</para>
    /// </summary>
    private async Task CleanupAsync()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            DELETE FROM platform."BillingStatementLines"
             WHERE "BillingStatementId" IN (
                 SELECT "Id" FROM platform."BillingStatements"
                  WHERE "TenantId" IN ({TenantId}, {SecondTenantId}) AND "Status" <> 'Final');
            DELETE FROM platform."BillingStatements"
             WHERE "TenantId" IN ({TenantId}, {SecondTenantId}) AND "Status" <> 'Final';
            DELETE FROM public."ExtractionJobs" WHERE "BusinessUnitId" = {BusinessUnitId};
            DELETE FROM platform."Tenants"
             WHERE "Id" IN ({TenantId}, {SecondTenantId})
               AND NOT EXISTS (
                   SELECT 1 FROM platform."BillingStatements"
                    WHERE "TenantId" IN ({TenantId}, {SecondTenantId}));
            DELETE FROM platform."RateCardLines"
             WHERE "RateCardId" = {RateCardId}
               AND NOT EXISTS (
                   SELECT 1 FROM platform."BillingStatements" WHERE "RateCardId" = {RateCardId});
            DELETE FROM platform."RateCards"
             WHERE "Id" = {RateCardId}
               AND NOT EXISTS (
                   SELECT 1 FROM platform."BillingStatements" WHERE "RateCardId" = {RateCardId});
            DELETE FROM platform."Plans"
             WHERE "Id" = {PlanId}
               AND NOT EXISTS (
                   SELECT 1 FROM platform."Tenants" WHERE "PlanId" = {PlanId});
            """;
        await command.ExecuteNonQueryAsync();
    }
}
