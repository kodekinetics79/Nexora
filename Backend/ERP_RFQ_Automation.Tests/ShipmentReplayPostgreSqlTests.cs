using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification for the database half of shipment/POD replay governance.
/// Domain tests exercise replay and mismatch behavior; this class proves the migration actually
/// installs the columns and tenant-scoped uniqueness those behaviors depend on in PostgreSQL.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ShipmentReplayPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Replay_columns_and_tenant_scoped_shipment_key_invariant_are_installed()
    {
        await using var connection = await database.OpenConnectionAsync();

        await using var indexCommand = new NpgsqlCommand("""
            SELECT indexdef
              FROM pg_indexes
             WHERE schemaname = 'public'
               AND tablename = 'Shipments'
               AND indexname = 'UX_Shipments_BU_IdempotencyKey';
            """, connection);
        var indexDefinition = Assert.IsType<string>(await indexCommand.ExecuteScalarAsync());
        Assert.Contains("UNIQUE INDEX", indexDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"BusinessUnitID\", \"IdempotencyKey\"", indexDefinition, StringComparison.Ordinal);
        Assert.Contains("WHERE (\"IdempotencyKey\" IS NOT NULL)", indexDefinition, StringComparison.Ordinal);

        await using var columnCommand = new NpgsqlCommand("""
            SELECT table_name, column_name, is_nullable, character_maximum_length
              FROM information_schema.columns
             WHERE table_schema = 'public'
               AND ((table_name = 'Shipments' AND column_name IN ('IdempotencyKey','RequestHash'))
                 OR (table_name = 'delivery_proofs' AND column_name = 'RequestHash'))
             ORDER BY table_name, column_name;
            """, connection);
        await using var reader = await columnCommand.ExecuteReaderAsync();
        var columns = new List<(string Table, string Column, string Nullable, int Length)>();
        while (await reader.ReadAsync())
            columns.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));

        Assert.Equal(3, columns.Count);
        Assert.All(columns, column => Assert.Equal("YES", column.Nullable));
        Assert.Contains(("Shipments", "IdempotencyKey", "YES", 160), columns);
        Assert.Contains(("Shipments", "RequestHash", "YES", 64), columns);
        Assert.Contains(("delivery_proofs", "RequestHash", "YES", 64), columns);
    }
}
