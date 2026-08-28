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
                'AK_LeadParticipationDecisions_CommittedConsistency',
                'FK_LeadLineParticipationDecisions_DecisionCommitConsistency',
                'FK_LeadLineParticipationDecisions_RevisionLineConsistency',
                'FK_RfqPromotions_DecisionConsistency',
                'FK_RFQ_PromotionConsistency',
                'FK_RFQItems_ParentSourceConsistency',
                'CK_RFQ_LeadPromotionLineage']);
            """);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "CK_LeadLineParticipationDecisions_BidCommercialIdentity",
            "AK_LeadParticipationDecisions_CommittedConsistency",
            "FK_LeadLineParticipationDecisions_DecisionCommitConsistency",
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
        Assert.Contains("DecisionIsCommitted", bidGuard, StringComparison.Ordinal);

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

        var outcomeTriggers = await ReadSetAsync(connection, """
            SELECT tgname
            FROM pg_trigger
            WHERE NOT tgisinternal
              AND tgname = ANY (ARRAY[
                'TR_LeadParticipationDecisions_OutcomeConsistency',
                'TR_LeadLineParticipationDecisions_OutcomeConsistency']);
            """);
        Assert.Equal(new HashSet<string>(StringComparer.Ordinal)
        {
            "TR_LeadParticipationDecisions_OutcomeConsistency",
            "TR_LeadLineParticipationDecisions_OutcomeConsistency"
        }, outcomeTriggers);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Deferred_database_guard_rejects_committed_header_that_disagrees_with_lines()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SeedParticipationAggregateAsync(connection, transaction, 99310, "FullBid");

        await using var validate = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE;", connection, transaction);
        var error = await Assert.ThrowsAsync<PostgresException>(() => validate.ExecuteNonQueryAsync());

        Assert.Equal("23514", error.SqlState);
        Assert.Contains("inconsistent", error.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Deferred_database_guard_accepts_consistent_committed_no_bid_snapshot()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SeedParticipationAggregateAsync(connection, transaction, 99320, "NoBid");

        await using var validate = new NpgsqlCommand("SET CONSTRAINTS ALL IMMEDIATE;", connection, transaction);
        await validate.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    private static async Task SeedParticipationAggregateAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long id, string outcome)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES (@id, @code, 'Participation outcome guard', 'tests', now());

            INSERT INTO "Leads"
                ("ID", "BusinessUnitID", "RFQNo", "BuyersName", "RecDate", "LeadSource", "CreatedBy")
            VALUES (@id, @id, @rfq, 'Outcome Guard Buyer', now(), 'Test', 'tests');

            INSERT INTO "LeadIngestionBatches"
                ("Id", "BusinessUnitId", "SourceChannel", "CreatedBy", "CreatedAtUtc", "UpdatedAtUtc", "Version")
            VALUES (@batch, @id, 'Test', 'tests', now(), now(), 1);

            INSERT INTO "LeadIngestionOccurrences"
                ("Id", "RecordKind", "BusinessUnitId", "BatchId", "LeadId", "SourceChannel",
                 "IdempotencyKey", "LogicalInquiryFingerprint", "Classification", "Confidence",
                 "DecisionReasonsJson", "PolicyVersion", "ProcessingPath", "ExternalAiUsed",
                 "IngestedAtUtc", "CreatedAtUtc", "ActorType", "ActorId", "CorrelationId", "Version")
            VALUES (@occurrence, 'Ingestion', @id, @batch, @id, 'Test', @occurrence_key,
                    repeat('a', 64), 'New', 1, '[]'::jsonb, 'test/v1', 'Deterministic', false,
                    now(), now(), 'TestFixture', 'tests', @correlation, 1);

            INSERT INTO "LeadRevisions"
                ("Id", "BusinessUnitId", "LeadId", "RevisionNumber", "EstablishedByOccurrenceId",
                 "LogicalInquiryFingerprint", "SnapshotJson", "CreatedAtUtc", "CreatedBy",
                 "ProcessingPath", "ExternalAiUsed")
            VALUES (@revision, @id, @id, 1, @occurrence, repeat('b', 64), '{}'::jsonb,
                    now(), 'tests', 'Deterministic', false);

            INSERT INTO "LeadItemRevisions"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "LineNumber",
                 "LineFingerprint", "SnapshotJson")
            VALUES (@revision_line, @id, @id, @revision, 1, repeat('c', 64), '{}'::jsonb);

            INSERT INTO "LeadFitAssessments"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "Sequence", "PolicyVersion",
                 "Recommendation", "IsActionable", "AssessmentJson", "IdempotencyKey", "RequestHash",
                 "AssessedBy", "AssessedAtUtc")
            VALUES (@fit, @id, @id, @revision, 1, 'test/v1', 'FIT', true, '{}'::jsonb,
                    @fit_key, repeat('d', 64), 'tests', now());

            INSERT INTO "LeadParticipationDecisions"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "FitAssessmentId", "Sequence",
                 "IsCommitted", "Outcome", "IdempotencyKey", "RequestHash", "DecidedBy", "DecidedAtUtc")
            VALUES (@decision, @id, @id, @revision, @fit, 1, true, @outcome,
                    @decision_key, repeat('e', 64), 'tests', now());

            INSERT INTO "LeadLineParticipationDecisions"
                ("Id", "BusinessUnitId", "LeadId", "LeadRevisionId", "ParticipationDecisionId",
                 "DecisionIsCommitted", "LeadItemRevisionId", "Choice", "ReasonCode",
                 "CatalogPolicyVersion", "WarningSnapshotJson")
            VALUES (@decision_line, @id, @id, @revision, @decision, true, @revision_line,
                    'NoBid', 'NO_CAPACITY', 'test/v1', '{}'::jsonb);
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("code", $"OUTCOME-{id}");
        command.Parameters.AddWithValue("rfq", $"OUTCOME-RFQ-{id}");
        command.Parameters.AddWithValue("batch", Guid.Parse($"00000000-0000-0000-0000-{id:D12}"));
        command.Parameters.AddWithValue("occurrence", id + 1);
        command.Parameters.AddWithValue("occurrence_key", $"outcome-occurrence-{id}");
        command.Parameters.AddWithValue("correlation", $"outcome-{id}");
        command.Parameters.AddWithValue("revision", id + 2);
        command.Parameters.AddWithValue("revision_line", id + 3);
        command.Parameters.AddWithValue("fit", id + 4);
        command.Parameters.AddWithValue("fit_key", $"outcome-fit-{id}");
        command.Parameters.AddWithValue("decision", id + 5);
        command.Parameters.AddWithValue("outcome", outcome);
        command.Parameters.AddWithValue("decision_key", $"outcome-decision-{id}");
        command.Parameters.AddWithValue("decision_line", id + 6);
        await command.ExecuteNonQueryAsync();
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

public sealed class ParticipationOutcomeConsistencyMigrationRollbackPostgreSqlTests
{
    private const string PriorMigration = "20260826134500_ParticipationDraftCommercialIdentity";
    private const string TargetMigration = "20260827170000_EnforceParticipationOutcomeConsistency";

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Outcome_consistency_migration_rolls_back_and_replays_cleanly()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await using var container = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("nexora_participation_outcome_rollback")
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

        await migrator.MigrateAsync(PriorMigration);
        Assert.False(await OutcomeTriggerExistsAsync(container.GetConnectionString()));
        await migrator.MigrateAsync(TargetMigration);
        Assert.True(await OutcomeTriggerExistsAsync(container.GetConnectionString()));
        await migrator.MigrateAsync(PriorMigration);
        Assert.False(await OutcomeTriggerExistsAsync(container.GetConnectionString()));
        await migrator.MigrateAsync(TargetMigration);
        Assert.True(await OutcomeTriggerExistsAsync(container.GetConnectionString()));
    }

    private static async Task<bool> OutcomeTriggerExistsAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) = 2
            FROM pg_trigger
            WHERE NOT tgisinternal
              AND tgname = ANY (ARRAY[
                'TR_LeadParticipationDecisions_OutcomeConsistency',
                'TR_LeadLineParticipationDecisions_OutcomeConsistency']);
            """, connection);
        return (bool)(await command.ExecuteScalarAsync())!;
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

        // Reproduce the production upgrade shape at the supported squashed baseline: an
        // immutable LeadItemRevision already exists when the focused migration starts. An
        // empty database cannot exercise the backfill that failed on Render because UPDATE
        // touches no rows there.
        await migrator.MigrateAsync(PriorMigration);
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
                ("ID", "LeadID", "LineItemNo", "ManufacturerPartNumber", "ProductShortDescription",
                 "Quantity", "UnitOfMeasure")
            VALUES (99202, 99201, '10', 'PART-99201', 'Migration regression line', 2, 'EA');

            INSERT INTO "LeadIngestionBatches"
                ("Id", "BusinessUnitId", "SourceChannel", "CreatedBy", "CreatedAtUtc", "UpdatedAtUtc", "Version")
            VALUES ('00000000-0000-0000-0000-000000099201'::uuid, 99201, 'MigrationTest',
                    'tests', now(), now(), 1);

            INSERT INTO "LeadIngestionOccurrences"
                ("Id", "RecordKind", "BusinessUnitId", "BatchId", "LeadId", "SourceChannel",
                 "IdempotencyKey", "LogicalInquiryFingerprint", "Classification", "Confidence",
                 "DecisionReasonsJson", "PolicyVersion", "ProcessingPath", "ExternalAiUsed",
                 "IngestedAtUtc", "CreatedAtUtc", "ActorType", "ActorId", "CorrelationId", "Version")
            VALUES (99203, 'Ingestion', 99201, '00000000-0000-0000-0000-000000099201'::uuid,
                    99201, 'MigrationTest', 'render-upgrade-occurrence', repeat('a', 64), 'New', 1,
                    '[]'::jsonb, 'render-upgrade/v1', 'Deterministic', false, now(), now(),
                    'TestFixture', 'tests', 'render-upgrade', 1);

            INSERT INTO "LeadRevisions"
                ("Id", "BusinessUnitId", "LeadId", "RevisionNumber", "EstablishedByOccurrenceId",
                 "LogicalInquiryFingerprint", "SnapshotJson", "CreatedAtUtc", "CreatedBy",
                 "ProcessingPath", "ExternalAiUsed")
            VALUES (99204, 99201, 99201, 1, 99203, repeat('b', 64), '{}'::jsonb,
                    now(), 'tests', 'Deterministic', false);

            INSERT INTO "LeadItemRevisions"
                ("Id", "BusinessUnitId", "LeadRevisionId", "LineNumber", "LineFingerprint", "SnapshotJson")
            VALUES (99205, 99201, 99204, 1, repeat('c', 64), jsonb_build_object(
                    'line', '10', 'part', 'part99201', 'description', 'migrationregressionline',
                    'quantity', 2, 'uom', 'EA'));
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
