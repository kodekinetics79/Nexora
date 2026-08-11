using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SQUASH NOTE — this file used to be
/// Populated_upgrade_backfills_legacy_maker_then_latest_readiness_blocks_unsafe_finalization.
///
/// It stood a database up at 20260807191755_PlatformOutboundEmailSettings, inserted a billing
/// statement with no ComputedBy column at all (the column did not exist yet), upgraded to
/// 20260808134402_PlatformSessionLegalHoldAndPurgeFencing and asserted the backfill wrote
/// 'system:legacy', then migrated to head and asserted that finalization was refused on readiness.
///
/// 20260811033109_SquashedSchemaBaseline erased both ids. The BACKFILL is retired — a statement row
/// with no ComputedBy cannot exist again, because the column is NOT NULL with a store default — but
/// the two things the backfill existed to make true are asserted here, and neither needs a walk:
///
///   * The DEFAULT itself. It is what the backfill wrote, and it is what an un-attributed statement
///     still gets today. It matters specifically because maker/checker compares ComputedBy against
///     the finalizing actor: 'system:legacy' is a value no human operator can be, so a statement
///     nobody is recorded as having computed can still be finalized by an Owner rather than
///     deadlocking. Asserted on the live column default AND by writing a real row.
///   * The readiness gate refusing an unsafe finalization. That was always application behaviour
///     reachable from head, and never needed a migration walk to observe.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PlatformBillingMakerCheckerMigrationPostgreSqlTests(
    PostgreSqlTestDatabase database)
{
    private const long TenantId = 998_001;
    private const long RateCardId = 998_002;
    private const long StatementId = 998_003;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Unattributed_statement_defaults_to_system_legacy_and_readiness_blocks_finalization()
    {
        await using var context = database.ContextFor(null);
        await using var transaction = await context.Database.BeginTransactionAsync();

        // Fail-closed defaults, asserted on the catalogue rather than inferred from a row: the
        // maker column and the readiness triple all have to carry a default, or an INSERT that
        // omits them fails outright and the platform cannot write a statement at all.
        var defaults = await context.Database.SqlQueryRaw<ColumnDefault>("""
            SELECT column_name AS "Name", column_default AS "Default", is_nullable AS "IsNullable"
            FROM information_schema.columns
            WHERE table_schema = 'platform' AND table_name = 'BillingStatements'
              AND column_name IN ('ComputedBy', 'ReadinessStatus')
            ORDER BY column_name
            """).ToListAsync();
        var computedBy = Assert.Single(defaults, x => x.Name == "ComputedBy");
        Assert.Equal("NO", computedBy.IsNullable);
        Assert.Contains("system:legacy", computedBy.Default);
        var readinessStatus = Assert.Single(defaults, x => x.Name == "ReadinessStatus");
        Assert.Equal("NO", readinessStatus.IsNullable);
        Assert.Contains("Blocked", readinessStatus.Default);

        // The period is deliberately historical so the settle-lag refusal cannot fire first and
        // hide the readiness refusal this test is about.
        await context.Database.ExecuteSqlRawAsync($"""
            INSERT INTO platform."Tenants"
                ("Id", "Name", "Slug", "Status", "CreatedOn", "BillingMode")
            VALUES ({TenantId}, 'Billing upgrade tenant', 'billing-upgrade-{TenantId}', 'Active', now(), 'Billable');
            INSERT INTO platform."RateCards"
                ("Id", "Code", "Currency", "EffectiveFromUtc", "IsActive", "CreatedOn", "Version")
            VALUES ({RateCardId}, 'billing-upgrade-card', 'USD', '2019-01-01', true, now(), 1);
            INSERT INTO platform."BillingStatements"
                ("Id", "TenantId", "PeriodStartUtc", "PeriodEndUtc", "RateCardId", "Currency",
                 "Status", "TotalAmount", "ComputedAtUtc", "Version")
            VALUES ({StatementId}, {TenantId}, '2020-01-01', '2020-02-01', {RateCardId}, 'USD',
                    'Draft', 125.00, '2020-02-03', 1);
            """);

        // No ComputedBy was named, exactly as the pre-column rows had none.
        Assert.Equal("system:legacy", await context.Database.SqlQueryRaw<string>($"""
            SELECT "ComputedBy" AS "Value" FROM platform."BillingStatements" WHERE "Id" = {StatementId}
            """).SingleAsync());

        context.ChangeTracker.Clear();
        var service = new BillingStatementService(context, NullLogger<BillingStatementService>.Instance);
        var refusal = await Assert.ThrowsAsync<BillingConflictException>(() =>
            service.FinalizeAsync(StatementId, "owner-reviewer@nexora.test"));
        Assert.Contains("readiness", refusal.Message, StringComparison.OrdinalIgnoreCase);

        await transaction.RollbackAsync();
    }

    private sealed record ColumnDefault(string Name, string Default, string IsNullable);
}
