using ERP_RFQ_Automation.Tests.Support;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class LeadParticipationPromotionPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Focused_migration_installs_forced_tenant_security_and_commercial_integrity_guards()
    {
        await using var connection = await database.OpenConnectionAsync();
        var securedTables = await ReadSetAsync(connection, """
            SELECT c.relname
            FROM pg_class c
            WHERE c.relname = ANY (ARRAY[
                'LeadFitAssessments', 'LeadParticipationDecisions',
                'LeadLineParticipationDecisions', 'RfqPromotions'])
              AND c.relrowsecurity
              AND c.relforcerowsecurity;
            """);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "LeadFitAssessments", "LeadParticipationDecisions",
            "LeadLineParticipationDecisions", "RfqPromotions"
        }, securedTables);

        var constraints = await ReadSetAsync(connection, """
            SELECT conname
            FROM pg_constraint
            WHERE conname = ANY (ARRAY[
                'CK_LeadLineParticipationDecisions_BidCommercialIdentity',
                'FK_LeadLineParticipationDecisions_DecisionConsistency',
                'FK_LeadLineParticipationDecisions_RevisionLineConsistency',
                'FK_RfqPromotions_DecisionConsistency',
                'FK_RFQ_PromotionConsistency',
                'FK_RFQItems_ParentSourceConsistency',
                'CK_RFQ_LeadPromotionLineage']);
            """);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "CK_LeadLineParticipationDecisions_BidCommercialIdentity",
            "FK_LeadLineParticipationDecisions_DecisionConsistency",
            "FK_LeadLineParticipationDecisions_RevisionLineConsistency",
            "FK_RfqPromotions_DecisionConsistency",
            "FK_RFQ_PromotionConsistency",
            "FK_RFQItems_ParentSourceConsistency",
            "CK_RFQ_LeadPromotionLineage"
        }, constraints);

        await using var definition = new NpgsqlCommand("""
            SELECT pg_get_constraintdef(oid)
            FROM pg_constraint
            WHERE conname = 'CK_LeadLineParticipationDecisions_BidCommercialIdentity';
            """, connection);
        var bidGuard = (string)(await definition.ExecuteScalarAsync())!;
        Assert.Contains("Quantity", bidGuard, StringComparison.Ordinal);
        Assert.Contains("UomId", bidGuard, StringComparison.Ordinal);
        Assert.Contains("CurrencyId", bidGuard, StringComparison.Ordinal);

        var triggers = await ReadSetAsync(connection, """
            SELECT tgname
            FROM pg_trigger
            WHERE NOT tgisinternal
              AND tgname = ANY (ARRAY[
                'TR_LeadFitAssessments_AppendOnly',
                'TR_LeadParticipationDecisions_AppendOnly',
                'TR_LeadLineParticipationDecisions_AppendOnly',
                'TR_RfqPromotions_AppendOnly']);
            """);
        Assert.Equal(4, triggers.Count);
    }

    private static async Task<HashSet<string>> ReadSetAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync()) values.Add(reader.GetString(0));
        return values;
    }
}

public sealed class LeadParticipationPromotionMigrationRollbackPostgreSqlTests
{
    private const string PreIdentityMigration = "20260724230121_Release01OrderLineage";
    private const string PriorMigration = "20260824140000_EmailIngestPurgeTombstone";
    private const string TargetMigration = "20260825043000_LeadParticipationAndRfqPromotion";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Focused_migration_rolls_back_and_replays_on_a_disposable_production_dialect_database()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_participation_rollback")
            .WithUsername("nexora")
            .WithPassword("nexora-tests")
            .Build();
        await container.StartAsync();
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(container.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        var migrator = context.GetService<IMigrator>();

        // Reproduce the production upgrade shape: a Lead and line existed before Release 01A,
        // which then created an immutable LeadItemRevision for it. An empty database cannot
        // exercise the backfill that failed on Render because UPDATE touches no rows there.
        await migrator.MigrateAsync(PreIdentityMigration);
        await SeedHistoricalLeadWithLineAsync(container.GetConnectionString());
        await migrator.MigrateAsync(TargetMigration);
        Assert.True(await TableExistsAsync(container.GetConnectionString(), "RfqPromotions"));
        await AssertHistoricalLineageWasBackfilledAndResealedAsync(container.GetConnectionString());
        await migrator.MigrateAsync(PriorMigration);
        Assert.False(await TableExistsAsync(container.GetConnectionString(), "RfqPromotions"));
        await migrator.MigrateAsync(TargetMigration);
        Assert.True(await TableExistsAsync(container.GetConnectionString(), "RfqPromotions"));
        await AssertHistoricalLineageWasBackfilledAndResealedAsync(container.GetConnectionString());
    }

    private static async Task SeedHistoricalLeadWithLineAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO "BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES (99201, 'RENDER-UPGRADE', 'Render upgrade regression', 'tests', now());

            INSERT INTO "Leads"
                ("ID", "BusinessUnitID", "RFQNo", "BuyersName", "RecDate", "LeadSource", "CreatedBy")
            VALUES (99201, 99201, 'RENDER-UPGRADE-RFQ', 'Migration Buyer', now(), 'Email', 'tests');

            INSERT INTO "LeadItems"
                ("LeadID", "LineItemNo", "ManufacturerPartNumber", "ProductShortDescription",
                 "Quantity", "UnitOfMeasure")
            VALUES (99201, '10', 'PART-99201', 'Migration regression line', 2, 'EA');
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertHistoricalLineageWasBackfilledAndResealedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var lineage = new NpgsqlCommand("""
            SELECT line."LeadId", trigger.tgenabled
            FROM "LeadItemRevisions" line
            JOIN pg_trigger trigger
              ON trigger.tgrelid = 'public."LeadItemRevisions"'::regclass
             AND trigger.tgname = 'trg_lead_item_revisions_append_only'
            WHERE line."BusinessUnitId" = 99201;
            """, connection))
        await using (var reader = await lineage.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal(99201L, reader.GetInt64(0));
            Assert.Equal('O', reader.GetChar(1));
            Assert.False(await reader.ReadAsync());
        }

        await using var forbiddenUpdate = new NpgsqlCommand("""
            UPDATE "LeadItemRevisions"
            SET "SnapshotJson" = "SnapshotJson"
            WHERE "BusinessUnitId" = 99201;
            """, connection);
        var error = await Assert.ThrowsAsync<PostgresException>(() => forbiddenUpdate.ExecuteNonQueryAsync());
        Assert.Contains("append-only", error.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> TableExistsAsync(string connectionString, string table)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass('public.' || quote_ident(@table)) IS NOT NULL", connection);
        command.Parameters.AddWithValue("table", table);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
