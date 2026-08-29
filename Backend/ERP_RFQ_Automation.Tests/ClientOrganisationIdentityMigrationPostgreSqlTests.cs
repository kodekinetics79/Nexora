using ERP_RFQ_Automation.CustomerResolution;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The client-identity schema has to be safe in the database, not merely in EF: tenant
/// isolation by RLS, least privilege on the table and its sequence, and the CustomerID⇔status
/// invariant enforced by a CHECK constraint so no code path — application, script or console —
/// can leave a lead claiming a client it does not have.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ClientOrganisationIdentityMigrationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Candidate_table_is_row_level_secured_and_least_privileged()
    {
        await using var connection = await database.OpenConnectionAsync();

        await using (var security = connection.CreateCommand())
        {
            security.CommandText = """
                SELECT c.relrowsecurity, c.relforcerowsecurity,
                       (SELECT count(*) FROM pg_policy p WHERE p.polrelid = c.oid)
                FROM pg_class c
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'lead_customer_match_candidates';
                """;
            await using var reader = await security.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));   // RLS enabled
            Assert.True(reader.GetBoolean(1));   // FORCE: the table owner is not exempt either
            Assert.True(reader.GetInt64(2) >= 1);
        }

        await using (var privileges = connection.CreateCommand())
        {
            privileges.CommandText = """
                SELECT has_table_privilege('nexora_tenant_app', 'public.lead_customer_match_candidates', 'SELECT'),
                       has_table_privilege('nexora_tenant_app', 'public.lead_customer_match_candidates', 'INSERT'),
                       has_table_privilege('nexora_tenant_app', 'public.lead_customer_match_candidates', 'UPDATE'),
                       has_table_privilege('nexora_tenant_app', 'public.lead_customer_match_candidates', 'DELETE'),
                       has_table_privilege('nexora_tenant_app', 'public.lead_customer_match_candidates', 'TRUNCATE'),
                       has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                           'public.lead_customer_match_candidates', 'Id'), 'USAGE'),
                       has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                           'public.lead_customer_match_candidates', 'Id'), 'SELECT'),
                       has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                           'public.lead_customer_match_candidates', 'Id'), 'UPDATE');
                """;
            await using var reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            // Candidates are rewritten in place on every resolution pass, so all four verbs
            // are needed — and nothing beyond them.
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.True(reader.GetBoolean(2));
            Assert.True(reader.GetBoolean(3));
            Assert.False(reader.GetBoolean(4));
            // USAGE only on the sequence: a tenant session may draw an id, never read the
            // allocation or reset it.
            Assert.True(reader.GetBoolean(5));
            Assert.False(reader.GetBoolean(6));
            Assert.False(reader.GetBoolean(7));
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Pipeline_customer_resolution_has_only_the_candidate_permissions_it_needs()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var privileges = connection.CreateCommand();
        privileges.CommandText = """
            SELECT has_table_privilege('nexora_pipeline_app',
                       'public.lead_customer_match_candidates', 'SELECT'),
                   has_table_privilege('nexora_pipeline_app',
                       'public.lead_customer_match_candidates', 'INSERT'),
                   has_table_privilege('nexora_pipeline_app',
                       'public.lead_customer_match_candidates', 'UPDATE'),
                   has_table_privilege('nexora_pipeline_app',
                       'public.lead_customer_match_candidates', 'DELETE'),
                   has_table_privilege('nexora_pipeline_app',
                       'public.lead_customer_match_candidates', 'TRUNCATE'),
                   has_sequence_privilege('nexora_pipeline_app', pg_get_serial_sequence(
                       'public.lead_customer_match_candidates', 'Id'), 'USAGE'),
                   has_sequence_privilege('nexora_pipeline_app', pg_get_serial_sequence(
                       'public.lead_customer_match_candidates', 'Id'), 'SELECT'),
                   has_sequence_privilege('nexora_pipeline_app', pg_get_serial_sequence(
                       'public.lead_customer_match_candidates', 'Id'), 'UPDATE');
            """;

        await using var reader = await privileges.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.True(reader.GetBoolean(1));
        Assert.True(reader.GetBoolean(2));
        Assert.True(reader.GetBoolean(3));
        Assert.False(reader.GetBoolean(4));
        Assert.True(reader.GetBoolean(5));
        Assert.False(reader.GetBoolean(6));
        Assert.False(reader.GetBoolean(7));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_tenant_session_cannot_read_or_write_another_tenants_candidates()
    {
        var suffix = Random.Shared.Next(1, 40_000);
        var tenantA = 9_410_000L + suffix;
        var tenantB = 9_411_000L + suffix;
        var customerA = 9_420_000L + suffix;
        var leadA = 9_430_000L + suffix;
        var leadB = 9_431_000L + suffix;

        await using (var owner = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenantA);
            Seed.EnsureBusinessUnit(owner, tenantB);
            Seed.Customer(owner, customerA, tenantA, "Saudi Electricity Company");
            Seed.Lead(owner, leadA, tenantA, buyersName: "Buyer A");
            Seed.Lead(owner, leadB, tenantB, buyersName: "Buyer B");
            await owner.SaveChangesAsync();
            owner.Set<LeadCustomerMatchCandidate>().Add(new LeadCustomerMatchCandidate
            {
                BusinessUnitId = tenantA,
                LeadId = leadA,
                CustomerId = customerA,
                Rank = 1,
                Confidence = 0.75m,
                ReasonCode = "NAME_EXACT_UNVERIFIED",
                Explanation = "Tenant A proposal.",
                CreatedOn = DateTime.UtcNow
            });
            await owner.SaveChangesAsync();
        }

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE nexora_tenant_app");
        await ExecuteAsync(connection, transaction, $"SET LOCAL nexora.business_unit_id = '{tenantB}'");

        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText =
                $"SELECT count(*) FROM public.lead_customer_match_candidates WHERE \"LeadId\" = {leadA}";
            Assert.Equal(0L, Convert.ToInt64(await read.ExecuteScalarAsync()));
        }

        var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, transaction, $"""
            INSERT INTO public.lead_customer_match_candidates
                ("BusinessUnitId", "LeadId", "CustomerId", "Rank", "Confidence", "ReasonCode", "Explanation", "CreatedOn")
            VALUES ({tenantA}, {leadA}, {customerA}, 2, 0.5, 'NAME_FUZZY', 'Cross-tenant write.', now())
            """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_candidate_can_never_point_at_another_tenants_lead_or_customer()
    {
        var suffix = Random.Shared.Next(40_001, 79_999);
        var tenantA = 9_410_000L + suffix;
        var tenantB = 9_411_000L + suffix;
        var customerB = 9_421_000L + suffix;
        var leadA = 9_430_000L + suffix;

        await using (var owner = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenantA);
            Seed.EnsureBusinessUnit(owner, tenantB);
            Seed.Customer(owner, customerB, tenantB, "Another Tenant's Client");
            Seed.Lead(owner, leadA, tenantA, buyersName: "Buyer A");
            await owner.SaveChangesAsync();
        }

        await using var context = database.ContextFor(null);
        var violation = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync($"""
                INSERT INTO public.lead_customer_match_candidates
                    ("BusinessUnitId", "LeadId", "CustomerId", "Rank", "Confidence", "ReasonCode", "Explanation", "CreatedOn")
                VALUES ({tenantA}, {leadA}, {customerB}, 1, 0.9, 'LEARNED_ALIAS', 'Cross-tenant customer.', now())
                """));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, violation.SqlState);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_suggested_lead_can_never_carry_a_customer_and_a_matched_one_must()
    {
        var suffix = Random.Shared.Next(80_000, 119_999);
        var tenant = 9_410_000L + suffix;
        var customerId = 9_420_000L + suffix;
        var leadId = 9_430_000L + suffix;

        await using (var owner = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenant);
            Seed.Customer(owner, customerId, tenant, "Saudi Electricity Company");
            Seed.Lead(owner, leadId, tenant, buyersName: "Buyer");
            await owner.SaveChangesAsync();
        }

        await using var context = database.ContextFor(null);

        // A suggestion that quietly wrote a customer would be indistinguishable from a
        // confirmed link — the exact failure "a wrong client is worse than an unresolved one"
        // is about.
        var suggestedWithCustomer = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync($"""
                UPDATE "Leads" SET "CustomerMatchStatus" = 'SUGGESTED', "CustomerID" = {customerId}
                WHERE "ID" = {leadId}
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, suggestedWithCustomer.SqlState);

        var ambiguousWithCustomer = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync($"""
                UPDATE "Leads" SET "CustomerMatchStatus" = 'AMBIGUOUS', "CustomerID" = {customerId}
                WHERE "ID" = {leadId}
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ambiguousWithCustomer.SqlState);

        var matchedWithoutCustomer = await Assert.ThrowsAsync<PostgresException>(() =>
            context.Database.ExecuteSqlRawAsync($"""
                UPDATE "Leads" SET "CustomerMatchStatus" = 'AUTO_MATCHED', "CustomerID" = NULL
                WHERE "ID" = {leadId}
                """));
        Assert.Equal(PostgresErrorCodes.CheckViolation, matchedWithoutCustomer.SqlState);

        // The legal shapes still pass.
        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync($"""
            UPDATE "Leads" SET "CustomerMatchStatus" = 'SUGGESTED', "CustomerID" = NULL WHERE "ID" = {leadId}
            """));
        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync($"""
            UPDATE "Leads" SET "CustomerMatchStatus" = 'AUTO_MATCHED', "CustomerID" = {customerId} WHERE "ID" = {leadId}
            """));
        // The legacy VERIFIED_EMAIL backfill status stays legal — it always carried a customer.
        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync($"""
            UPDATE "Leads" SET "CustomerMatchStatus" = 'VERIFIED_EMAIL', "CustomerID" = {customerId} WHERE "ID" = {leadId}
            """));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Learning_provenance_columns_exist_with_a_usable_default()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'customer_identifiers'
              AND column_name IN ('LearnedFromLeadId', 'LearnedFromReviewAuditId', 'ObservationCount', 'LastObservedOn')
            ORDER BY column_name;
            """;
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new Dictionary<string, (string Nullable, string? Default)>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
            columns[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));

        Assert.Equal(4, columns.Count);
        Assert.Equal("YES", columns["LearnedFromLeadId"].Nullable);
        Assert.Equal("YES", columns["LearnedFromReviewAuditId"].Nullable);
        Assert.Equal("YES", columns["LastObservedOn"].Nullable);
        // Existing rows predate the learning loop; they count as one observation, not zero.
        Assert.Equal("NO", columns["ObservationCount"].Nullable);
        Assert.Contains("1", columns["ObservationCount"].Default ?? string.Empty);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
