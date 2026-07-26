using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class QuoteDeliveryPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantA = 96_201;
    private const long TenantB = 96_202;
    private const long QuoteA = 96_211;
    private const long QuoteB = 96_212;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Runtime_role_has_usage_only_sequence_and_RLS_blocks_cross_tenant_delivery()
    {
        await using (var context = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(context, TenantA);
            Seed.EnsureBusinessUnit(context, TenantB);
            context.Quotes.AddRange(Quote(QuoteA, TenantA), Quote(QuoteB, TenantB));
            await context.SaveChangesAsync();
        }

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE nexora_tenant_app");
        await ExecuteAsync(connection, transaction, $"SET LOCAL nexora.business_unit_id = '{TenantA}'");

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO public.quote_delivery_requests
                ("BusinessUnitId", "QuoteId", "IdempotencyKey", "RecipientEmail", "Subject", "Body",
                 "AttachmentFileName", "RequestedOn", "AvailableOn", "AttemptCount", "Version")
            VALUES (@tenant, @quote, 'postgres-delivery-a', 'buyer@nexora.invalid', 'Quote', 'Body',
                    'quote.pdf', now(), now(), 0, 1)
            RETURNING "Id";
            """;
        insert.Parameters.AddWithValue("tenant", TenantA);
        insert.Parameters.AddWithValue("quote", QuoteA);
        var deliveryId = Convert.ToInt64(await insert.ExecuteScalarAsync());

        await transaction.SaveAsync("before_cross_tenant");
        await using var crossTenant = connection.CreateCommand();
        crossTenant.Transaction = transaction;
        crossTenant.CommandText = insert.CommandText;
        crossTenant.Parameters.AddWithValue("tenant", TenantB);
        crossTenant.Parameters.AddWithValue("quote", QuoteB);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => crossTenant.ExecuteScalarAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await transaction.RollbackAsync("before_cross_tenant");

        await transaction.SaveAsync("before_payload_mutation");
        await using var mutatePayload = connection.CreateCommand();
        mutatePayload.Transaction = transaction;
        mutatePayload.CommandText = """
            UPDATE public.quote_delivery_requests
            SET "RecipientEmail" = 'other@nexora.invalid', "Version" = "Version" + 1
            WHERE "Id" = @id;
            """;
        mutatePayload.Parameters.AddWithValue("id", deliveryId);
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => mutatePayload.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, immutable.SqlState);
        await transaction.RollbackAsync("before_payload_mutation");

        await transaction.RollbackAsync();

        await using var privilege = connection.CreateCommand();
        privilege.CommandText = """
            SELECT has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                       'public.quote_delivery_requests', 'Id'), 'USAGE'),
                   has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                       'public.quote_delivery_requests', 'Id'), 'SELECT'),
                   has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                       'public.quote_delivery_requests', 'Id'), 'UPDATE');
            """;
        await using var reader = await privilege.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.False(reader.GetBoolean(1));
        Assert.False(reader.GetBoolean(2));
    }

    private static Quote Quote(long id, long tenant) => new()
    {
        Id = id,
        QuoteNo = $"QD-{id}",
        BusinessUnitId = tenant,
        QuoteDate = DateTime.UtcNow,
        ValidUntil = DateTime.UtcNow.AddDays(30),
        TotalAmount = 100,
        CreatedBy = "tests",
        CreatedDate = DateTime.UtcNow
    };

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
