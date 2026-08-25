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

        await migrator.MigrateAsync(TargetMigration);
        Assert.True(await TableExistsAsync(container.GetConnectionString(), "RfqPromotions"));
        await migrator.MigrateAsync(PriorMigration);
        Assert.False(await TableExistsAsync(container.GetConnectionString(), "RfqPromotions"));
        await migrator.MigrateAsync(TargetMigration);
        Assert.True(await TableExistsAsync(container.GetConnectionString(), "RfqPromotions"));
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
