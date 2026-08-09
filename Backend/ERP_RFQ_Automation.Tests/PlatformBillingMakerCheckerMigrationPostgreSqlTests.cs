using ERP_RFQ_Automation.Billing;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PlatformBillingMakerCheckerMigrationPostgreSqlTests(
    PostgreSqlTestDatabase database)
{
    private const string PreviousMigration = "20260807191755_PlatformOutboundEmailSettings";
    private const string CurrentMigration = "20260808134402_PlatformSessionLegalHoldAndPurgeFencing";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Populated_upgrade_backfills_legacy_maker_then_latest_readiness_blocks_unsafe_finalization()
    {
        var databaseName = $"nexora_platform_billing_upgrade_{Guid.NewGuid():N}";
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = databaseName };
        await ExecuteAdminAsync(database.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        try
        {
            await using var context = database.ContextForConnectionString(connection.ConnectionString, null);
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(PreviousMigration);
            await context.Database.ExecuteSqlRawAsync("""
                INSERT INTO platform."Tenants"
                    ("Id", "Name", "Slug", "Status", "CreatedOn", "BillingMode")
                VALUES (998001, 'Billing upgrade tenant', 'billing-upgrade-998001', 'Active', now(), 'Billable');
                INSERT INTO platform."RateCards"
                    ("Id", "Code", "Currency", "EffectiveFromUtc", "IsActive", "CreatedOn", "Version")
                VALUES (998002, 'billing-upgrade-card', 'USD', '2019-01-01', true, now(), 1);
                INSERT INTO platform."BillingStatements"
                    ("Id", "TenantId", "PeriodStartUtc", "PeriodEndUtc", "RateCardId", "Currency",
                     "Status", "TotalAmount", "ComputedAtUtc", "Version")
                VALUES (998003, 998001, '2020-01-01', '2020-02-01', 998002, 'USD',
                        'Draft', 125.00, '2020-02-03', 1);
                """);

            await migrator.MigrateAsync(CurrentMigration);

            var backfilled = await context.Database.SqlQueryRaw<string>("""
                SELECT "ComputedBy" AS "Value" FROM platform."BillingStatements" WHERE "Id" = 998003
                """).SingleAsync();
            Assert.Equal("system:legacy", backfilled);

            await migrator.MigrateAsync();
            context.ChangeTracker.Clear();
            var service = new BillingStatementService(context, NullLogger<BillingStatementService>.Instance);
            var refusal = await Assert.ThrowsAsync<BillingConflictException>(() =>
                service.FinalizeAsync(998003, "owner-reviewer@nexora.test"));
            Assert.Contains("readiness", refusal.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAdminAsync(database.ConnectionString, $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task ExecuteAdminAsync(string connectionString, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
