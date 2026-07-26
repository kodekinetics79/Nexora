using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class Release02ProcurementHandoffPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Handoff_schema_has_migration_rls_least_privilege_and_lineage_trigger()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" = '20260726194942_Release02ProcurementHandoffs') = 1,
                (SELECT count(*) FROM "__EFMigrationsHistory"
                 WHERE "MigrationId" = '20260726205812_Release02ProcurementHandoffHardening') = 1,
                (SELECT count(*) FROM pg_policies
                 WHERE schemaname = 'public' AND tablename = 'procurement_handoffs'
                   AND policyname = 'nexora_tenant_isolation'
                   AND 'nexora_tenant_app' = ANY(roles)) = 1,
                has_table_privilege('nexora_tenant_app', 'public.procurement_handoffs', 'SELECT,INSERT,UPDATE')
                    AND NOT has_table_privilege('nexora_tenant_app', 'public.procurement_handoffs', 'DELETE'),
                has_sequence_privilege('nexora_tenant_app', 'public."procurement_handoffs_Id_seq"', 'USAGE')
                    AND NOT has_sequence_privilege('nexora_tenant_app', 'public."procurement_handoffs_Id_seq"', 'SELECT,UPDATE'),
                (SELECT count(*) FROM pg_trigger
                 WHERE NOT tgisinternal AND tgname = 'trg_procurement_handoffs_protect_lineage') = 1,
                (SELECT count(*) FROM pg_constraint WHERE conname = ANY(ARRAY[
                    'FK_procurement_handoffs_RFQ_BusinessUnitId_RfqId',
                    'FK_procurement_handoffs_RFQItems_RfqItemId_RfqId'])) = 2;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var index = 0; index < 7; index++) Assert.True(reader.GetBoolean(index));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Runtime_role_cannot_disclose_handoff_from_another_tenant()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertIsolatedHandoffAsync(connection, transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SET LOCAL ROLE nexora_tenant_app;
            SELECT set_config('nexora.business_unit_id', '99202', true);
            """;
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT count(*)::int FROM procurement_handoffs WHERE \"Id\" = 992010;";
        Assert.Equal(0, Convert.ToInt32(await command.ExecuteScalarAsync()));

        command.CommandText = "SELECT set_config('nexora.business_unit_id', '99201', true);";
        await command.ExecuteNonQueryAsync();
        command.CommandText = "SELECT count(*)::int FROM procurement_handoffs WHERE \"Id\" = 992010;";
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Handoff_lineage_is_immutable_in_postgresql()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertIsolatedHandoffAsync(connection, transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE procurement_handoffs SET "NexoraSerial" = 'NXR-CHANGED'
            WHERE "Id" = 992010;
            """;
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, exception.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Handoff_lifecycle_cannot_skip_forward_in_postgresql()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertIsolatedHandoffAsync(connection, transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE procurement_handoffs SET "Status" = 'RECEIVED'
            WHERE "Id" = 992010;
            """;
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, exception.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Handoff_idempotency_inputs_are_immutable_in_postgresql()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await InsertIsolatedHandoffAsync(connection, transaction);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE procurement_handoffs SET "ExternalSystemTarget" = 'ERP-CHANGED'
            WHERE "Id" = 992010;
            """;
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, exception.SqlState);
    }

    private static async Task InsertIsolatedHandoffAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SET LOCAL session_replication_role = replica;
            INSERT INTO procurement_handoffs
                ("Id", "BusinessUnitId", "CustomerOrderId", "CustomerOrderLineId",
                 "CommercialDemandLineId", "SourcingAwardId", "SupplierQuotedItemId", "SupplierId",
                 "RfqId", "RfqItemId", "CurrencyId", "NexoraSerial", "RequiredQuantity",
                 "SelectedUnitCost", "DestinationType", "ExternalSystemTarget", "Status",
                 "IdempotencyKey", "RequestHash", "IsAuthoritative", "Version", "CreatedOn", "CreatedBy")
            VALUES
                (992010, 99201, 992011, 992012, 992013, 992014, 992015, 992016,
                 992017, 992018, 992019, 'NXR-PG-HANDOFF', 2, 19.50, 'DROP_SHIP', 'MANUAL', 'CREATED',
                 'pg-handoff-isolation', repeat('a', 64), false, 1, now(), 'postgresql-tests');
            SET LOCAL session_replication_role = origin;
            """;
        await command.ExecuteNonQueryAsync();
    }
}
