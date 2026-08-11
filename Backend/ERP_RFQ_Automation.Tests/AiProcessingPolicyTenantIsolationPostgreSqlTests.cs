using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Production-dialect certification that a tenant cannot write another tenant's AI governance row.
///
/// <para><b>The defect this pins.</b> public."AiProcessingPolicies" carried TWO permissive
/// policies. PostgreSQL ORs permissive policies, so satisfying EITHER admits the row.
/// <c>nexora_tenant_isolation</c> pins BusinessUnitId to the tenant GUC.
/// <c>nexora_ai_default_provisioning</c> (INSERT, TO PUBLIC) pinned nine constants and did NOT
/// pin BusinessUnitId, so reproducing those constants was a complete bypass of the isolation
/// predicate — and the columns it left unpinned are the governance ones: RetentionDays,
/// DataResidency, EgressPolicy, RedactionRequired, PrivacyReviewRequired,
/// InputOutputAuditAllowed. 20260811110019_DropAiDefaultProvisioningPolicy removes it.</para>
///
/// <para><b>Why this cannot be a SQLite test.</b> SQLite has neither roles nor row-level
/// security, so both policies, the bypass and the fix are all invisible on the portable lane.</para>
///
/// <para><b>Why the cross-tenant test grants INSERT first.</b> In the shipped configuration
/// nexora_tenant_app holds only SELECT and UPDATE on this table, and table privileges are checked
/// BEFORE row-level security — so the cross-tenant write is currently refused with 42501
/// "permission denied for table" no matter what the policies say. That mask is real defence but it
/// is not the control under test, and it is one GRANT away from being removed. The test therefore
/// grants INSERT inside a transaction it rolls back, so that the RLS layer is the only thing left
/// standing between the attacker and the victim's row. Without that grant this test passes for a
/// reason unrelated to the fix, which would make it worthless as a regression.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class AiProcessingPolicyTenantIsolationPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private const long AttackerBusinessUnitId = 8_090_101;
    private const long VictimBusinessUnitId = 8_090_102;
    private const long ProvisionedBusinessUnitId = 8_090_103;

    [Fact]
    public async Task AiProcessingPolicies_carries_exactly_one_permissive_policy_and_it_pins_the_tenant()
    {
        await using var connection = await database.OpenConnectionAsync();

        // Not "nexora_ai_default_provisioning is gone" but "there is exactly one permissive policy
        // a REQUEST can reach, and it is the isolating one". Any future permissive policy this
        // filter admits is a fresh OR-branch around the tenant predicate, whatever it is called.
        //
        // The role filter, and why it is a tightening rather than a loosening. The original
        // assertion counted every permissive policy on the table, which stated the invariant
        // through a proxy: the hazard is not a second policy, it is a second policy that ORs into
        // the decision for a role a request can execute under. nexora_ai_default_provisioning was
        // TO PUBLIC, so it did. 20260811154500_TenantPurgeExecutionRole adds nexora_tenant_purge
        // TO nexora_purge_app, which does not: that role is NOLOGIN, is granted only to the role
        // running migrations, and none of the three execution roles is a member — so no request,
        // whatever it sets, can execute under it. Written as a reachability test rather than as a
        // second expected name, this still fails on a policy added TO PUBLIC or TO any of the
        // request-path roles, which is the whole of what it was defending.
        Assert.Equal("nexora_tenant_isolation", await ScalarStringAsync(connection, """
            SELECT string_agg(policy.polname, ', ' ORDER BY policy.polname)
            FROM pg_policy policy
            JOIN pg_class target ON target.oid = policy.polrelid
            JOIN pg_namespace target_schema
              ON target_schema.oid = target.relnamespace AND target_schema.nspname = 'public'
            WHERE target.relname = 'AiProcessingPolicies'
              AND policy.polpermissive
              AND (policy.polroles = '{0}'::oid[]
                   OR EXISTS (
                       SELECT 1
                       FROM unnest(policy.polroles) AS admitted(role_oid)
                       CROSS JOIN pg_roles request_role
                       WHERE request_role.rolname IN (
                                 'nexora_tenant_app', 'nexora_identity_app', 'nexora_pipeline_app')
                         AND pg_has_role(request_role.oid, admitted.role_oid, 'USAGE')));
            """));

        // The half the filter above deliberately does not check: that the policy it excluded is
        // excluded for the stated reason, and not because the role happens not to exist yet.
        Assert.Equal(1L, await ScalarAsync(connection, """
            SELECT count(*) FROM pg_roles
            WHERE rolname = 'nexora_purge_app'
              AND NOT rolcanlogin AND NOT rolbypassrls AND NOT rolsuper
              AND NOT pg_has_role('nexora_tenant_app', oid, 'MEMBER');
            """));

        Assert.Equal(true, await ScalarAsync(connection, """
            SELECT position('nexora.business_unit_id' in pg_get_expr(policy.polwithcheck, policy.polrelid)) > 0
               AND position('BusinessUnitId' in pg_get_expr(policy.polwithcheck, policy.polrelid)) > 0
            FROM pg_policy policy
            JOIN pg_class target ON target.oid = policy.polrelid
            WHERE target.relname = 'AiProcessingPolicies'
              AND policy.polname = 'nexora_tenant_isolation';
            """));
    }

    [Fact]
    public async Task A_tenant_cannot_insert_an_ai_governance_row_for_another_tenant()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await SeedBusinessUnitAsync(connection, AttackerBusinessUnitId, "AI-ISO-ATTACKER");
        await SeedBusinessUnitAsync(connection, VictimBusinessUnitId, "AI-ISO-VICTIM");

        // The trigger already created the victim's row. Remove it so a refusal below can only be
        // row-level security and never the primary key.
        await ExecuteAsync(connection,
            $"""DELETE FROM public."AiProcessingPolicies" WHERE "BusinessUnitId" = {VictimBusinessUnitId};""");

        // Strip the privilege mask so RLS is the only remaining control. Rolled back with the rest.
        await ExecuteAsync(connection,
            """GRANT INSERT ON public."AiProcessingPolicies" TO nexora_tenant_app;""");

        await ExecuteAsync(connection,
            $"SELECT set_config('nexora.business_unit_id', '{AttackerBusinessUnitId}', true);");
        await ExecuteAsync(connection, "SET LOCAL ROLE nexora_tenant_app;");

        // Every one of the nine constants the dropped policy pinned, so the attempt fails ONLY
        // because BusinessUnitId no longer has an unpinned route in. The unpinned columns carry
        // the payload that made this worth exploiting: a decade of retention, an arbitrary
        // residency region, and raw egress.
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, $"""
            INSERT INTO public."AiProcessingPolicies"
                ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                 "AllowedProvider", "AllowedModel", "MonthlySoftTokenLimit", "MonthlyHardTokenLimit",
                 "Version", "UpdatedOn", "UpdatedBy",
                 "RetentionDays", "DataResidency", "EgressPolicy")
            VALUES ({VictimBusinessUnitId}, TRUE, FALSE, 'RfqExtraction,BoqDraft',
                    NULL, NULL, NULL, NULL,
                    1, now(), 'tenant-provisioning',
                    3650, 'ATTACKER-REGION', 'AllFieldsRaw');
            """));

        // 42501 with this message is the row-level security refusal specifically, not the table
        // grant — the grant was restored above, and a privilege refusal reads "permission denied".
        Assert.Equal("42501", refused.SqlState);
        Assert.Contains("row-level security", refused.MessageText, StringComparison.OrdinalIgnoreCase);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_tenant_can_still_write_its_OWN_ai_governance_row()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await SeedBusinessUnitAsync(connection, AttackerBusinessUnitId, "AI-ISO-SELF");
        await ExecuteAsync(connection,
            $"""DELETE FROM public."AiProcessingPolicies" WHERE "BusinessUnitId" = {AttackerBusinessUnitId};""");
        await ExecuteAsync(connection,
            """GRANT INSERT ON public."AiProcessingPolicies" TO nexora_tenant_app;""");
        await ExecuteAsync(connection,
            $"SELECT set_config('nexora.business_unit_id', '{AttackerBusinessUnitId}', true);");
        await ExecuteAsync(connection, "SET LOCAL ROLE nexora_tenant_app;");

        // The counterpart the previous test needs to be meaningful: dropping the second policy
        // must not have made nexora_tenant_isolation refuse a tenant's own in-scope write.
        await ExecuteAsync(connection, $"""
            INSERT INTO public."AiProcessingPolicies"
                ("BusinessUnitId", "IsEnabled", "ExternalProcessingAllowed", "AllowedPurposes",
                 "Version", "UpdatedOn", "UpdatedBy")
            VALUES ({AttackerBusinessUnitId}, TRUE, FALSE, 'RfqExtraction,BoqDraft',
                    1, now(), 'tenant-provisioning');
            """);

        Assert.Equal(1L, await ScalarAsync(connection,
            $"""SELECT count(*) FROM public."AiProcessingPolicies" WHERE "BusinessUnitId" = {AttackerBusinessUnitId};"""));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Provisioning_a_business_unit_still_creates_the_fail_closed_default_policy()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        // The legitimate path, and the reason the dropped policy was written in the first place.
        // nexora_create_default_ai_policy() is SECURITY DEFINER, so the effective role for its
        // INSERT is the function owner rather than whoever inserted the business unit — which is
        // why removing a policy addressed TO PUBLIC does not disturb it.
        await ExecuteAsync(connection, "SET LOCAL ROLE nexora_pipeline_app;");
        await SeedBusinessUnitAsync(connection, ProvisionedBusinessUnitId, "AI-ISO-PROV");
        await ExecuteAsync(connection, "RESET ROLE;");

        Assert.Equal("tenant-provisioning|30|TenantApprovedRegion|RedactedFieldsOnly|false",
            await ScalarStringAsync(connection, $"""
                SELECT "UpdatedBy" || '|' || "RetentionDays" || '|' || "DataResidency"
                       || '|' || "EgressPolicy" || '|' || "ExternalProcessingAllowed"
                FROM public."AiProcessingPolicies"
                WHERE "BusinessUnitId" = {ProvisionedBusinessUnitId};
                """));

        await transaction.RollbackAsync();
    }

    private static Task SeedBusinessUnitAsync(NpgsqlConnection connection, long id, string code)
        => ExecuteAsync(connection, $"""
            INSERT INTO public."BusinessUnits"
                ("ID", "BusinessUnitCode", "BusinessUnitName", "CreatedBy", "CreatedOn")
            VALUES ({id}, '{code}', '{code}', 'ai-isolation-tests', now())
            ON CONFLICT ("ID") DO NOTHING;
            """);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection connection, string sql)
        => (await ScalarAsync(connection, sql)) as string;
}
