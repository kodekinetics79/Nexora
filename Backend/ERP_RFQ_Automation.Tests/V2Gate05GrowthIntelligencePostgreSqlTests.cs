using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class V2Gate05GrowthIntelligencePostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long TenantA = 98_501;
    private const long TenantB = 98_502;
    private const long ManagerA = 98_511;
    private const long RepA = 98_512;
    private const long ManagerB = 98_521;
    private const long RepB = 98_522;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Coaching_ledger_is_tenant_qualified_rls_forced_append_only_and_least_privilege()
    {
        await SeedPrincipalsAsync();
        await InsertAcknowledgementAsync(TenantA, ManagerA, RepA, "tenant-a");
        await InsertAcknowledgementAsync(TenantB, ManagerB, RepB, "tenant-b");

        await using var connection = await database.OpenConnectionAsync();
        await using var schema = connection.CreateCommand();
        // Squash note: dropped the leading id check for
        // '20260729135045_V2Gate05SalesCoachingGrowthIntelligence'.
        // 20260811033109_SquashedSchemaBaseline erased that id. Forced RLS, the tenant policy
        // predicate, the grant shape, the two append-only triggers and the two tenant-qualified
        // foreign keys are all still asserted below, against pg_catalog.
        schema.CommandText = """
            SELECT
                (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class
                 WHERE oid = 'public.sales_coaching_acknowledgements'::regclass),
                EXISTS (SELECT 1 FROM pg_policies
                    WHERE schemaname = 'public' AND tablename = 'sales_coaching_acknowledgements'
                      AND policyname = 'nexora_tenant_isolation'
                      AND position('nexora.business_unit_id' in qual) > 0
                      AND position('nexora.business_unit_id' in with_check) > 0),
                has_table_privilege('nexora_tenant_app', 'public.sales_coaching_acknowledgements', 'SELECT,INSERT')
                    AND NOT has_table_privilege('nexora_tenant_app',
                        'public.sales_coaching_acknowledgements', 'UPDATE,DELETE,TRUNCATE'),
                (SELECT count(*) FROM pg_trigger WHERE NOT tgisinternal
                    AND tgrelid = 'public.sales_coaching_acknowledgements'::regclass
                    AND tgname = ANY(ARRAY['sales_coaching_acknowledgements_append_only',
                        'sales_coaching_acknowledgements_reject_truncate'])) = 2,
                (SELECT count(*) FROM pg_constraint
                    WHERE conrelid = 'public.sales_coaching_acknowledgements'::regclass
                      AND contype = 'f'
                      AND position('("BusinessUnitId", "ManagerUserId")' in pg_get_constraintdef(oid)) > 0) = 1,
                (SELECT count(*) FROM pg_constraint
                    WHERE conrelid = 'public.sales_coaching_acknowledgements'::regclass
                      AND contype = 'f'
                      AND position('("BusinessUnitId", "SalesRepUserId")' in pg_get_constraintdef(oid)) > 0) = 1;
            """;
        await using (var reader = await schema.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            for (var index = 0; index < 6; index++)
                Assert.True(reader.GetBoolean(index), $"Growth schema assertion {index + 1} failed.");
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var scoped = connection.CreateCommand();
            scoped.Transaction = transaction;
            scoped.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{TenantA}';
                SELECT
                    (SELECT count(*) FROM sales_coaching_acknowledgements
                        WHERE "BusinessUnitId" = {TenantA}),
                    (SELECT count(*) FROM sales_coaching_acknowledgements
                        WHERE "BusinessUnitId" = {TenantB});
                """;
            await using (var reader = await scoped.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                Assert.Equal(1L, reader.GetInt64(0));
                Assert.Equal(0L, reader.GetInt64(1));
            }
            await transaction.RollbackAsync();
        }

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var forged = connection.CreateCommand();
            forged.Transaction = transaction;
            forged.CommandText = $"""
                SET LOCAL ROLE nexora_tenant_app;
                SET LOCAL nexora.business_unit_id = '{TenantA}';
                {InsertSql(TenantB, ManagerB, RepB, "forged-runtime")}
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => forged.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            await transaction.RollbackAsync();
        }

        await using (var mismatchedPrincipal = connection.CreateCommand())
        {
            mismatchedPrincipal.CommandText = InsertSql(TenantA, ManagerB, RepA, "forged-principal");
            var error = await Assert.ThrowsAsync<PostgresException>(() => mismatchedPrincipal.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
        }

        await using (var rewrite = connection.CreateCommand())
        {
            rewrite.CommandText = $"""
                UPDATE sales_coaching_acknowledgements SET "Reason" = 'rewritten'
                WHERE "BusinessUnitId" = {TenantA};
                """;
            var error = await Assert.ThrowsAsync<PostgresException>(() => rewrite.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }

        await using (var truncate = connection.CreateCommand())
        {
            truncate.CommandText = "TRUNCATE sales_coaching_acknowledgements;";
            var error = await Assert.ThrowsAsync<PostgresException>(() => truncate.ExecuteNonQueryAsync());
            Assert.Equal("55000", error.SqlState);
        }
    }

    private async Task SeedPrincipalsAsync()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO "BusinessUnits" ("ID", "BusinessUnitCode", "BusinessUnitName", "IsActive", "CreatedBy", "CreatedOn")
            VALUES
                ({TenantA}, 'GROWTH-A', 'Growth Tenant A', TRUE, 'test', CURRENT_TIMESTAMP),
                ({TenantB}, 'GROWTH-B', 'Growth Tenant B', TRUE, 'test', CURRENT_TIMESTAMP)
            ON CONFLICT ("ID") DO NOTHING;

            INSERT INTO "Users" ("ID", "FirstName", "LastName", "Email", "Password_Hash", "ImageURL",
                "BUID", "IsActive", "CreatedBy", "CreatedOn")
            VALUES
                ({ManagerA}, 'Manager', 'A', 'growth-manager-a@example.test', 'not-used', 'n/a', {TenantA}, TRUE, 'test', CURRENT_TIMESTAMP),
                ({RepA}, 'Rep', 'A', 'growth-rep-a@example.test', 'not-used', 'n/a', {TenantA}, TRUE, 'test', CURRENT_TIMESTAMP),
                ({ManagerB}, 'Manager', 'B', 'growth-manager-b@example.test', 'not-used', 'n/a', {TenantB}, TRUE, 'test', CURRENT_TIMESTAMP),
                ({RepB}, 'Rep', 'B', 'growth-rep-b@example.test', 'not-used', 'n/a', {TenantB}, TRUE, 'test', CURRENT_TIMESTAMP)
            ON CONFLICT ("ID") DO NOTHING;
            """;
        await command.ExecuteNonQueryAsync();
    }

    private async Task InsertAcknowledgementAsync(long tenantId, long managerId, long repId, string key)
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = InsertSql(tenantId, managerId, repId, key);
        await command.ExecuteNonQueryAsync();
    }

    private static string InsertSql(long tenantId, long managerId, long repId, string key) => $"""
        INSERT INTO sales_coaching_acknowledgements
            ("BusinessUnitId", "FindingKey", "FindingCode", "SalesRepUserId", "ManagerUserId",
             "DecisionCode", "Reason", "SourceAggregateType", "SourceAggregateId",
             "SourceAggregateVersion", "EvidenceSnapshotJson", "PolicyVersion",
             "FindingGeneratedAtUtc", "IdempotencyKey", "RequestHash", "CorrelationId", "CreatedAtUtc")
        VALUES
            ({tenantId}, repeat('a', 64), 'OVERDUE_FOLLOW_UP', {repId}, {managerId},
             'ACKNOWLEDGED', 'Reviewed with evidence', 'FollowUpTask', 1,
             repeat('b', 64), '[]'::jsonb, 'growth-intelligence-v2.5',
             CURRENT_TIMESTAMP, '{key}', repeat('c', 64), '{key}', CURRENT_TIMESTAMP);
        """;
}
