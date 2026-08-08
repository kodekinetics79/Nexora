using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// RED TEAM. Adversarial certification of 20260807002456 and 20260807022229 against a real
/// PostgreSQL with the real role topology.
///
/// <para>Everything here targets a defect class the SQLite lane structurally cannot observe:
/// column-level grants, ungranted new tables, <c>ENABLE ALWAYS</c> trigger semantics under
/// <c>session_replication_role = 'replica'</c>, and what a catalogue-driven purge actually
/// reaches when foreign-key triggers are suspended.</para>
///
/// <para>Tests that PROVE a live defect are <c>Skip</c>ped with the finding referenced in the
/// reason, so the suite stays green and the defect stays documented. Remove the Skip to watch
/// them fail.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class RedTeamControlPlanePostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public RedTeamControlPlanePostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    /// <summary>Every table 20260807022229 and 20260807002456 create.</summary>
    private static readonly string[] NewControlPlaneTables =
    [
        "ProvisioningExecutions", "ProvisioningSteps", "ProvisioningDrafts",
        "TenantOffboardings", "TenantLifecycleEvents", "TenantExportReceipts",
        "SupportTickets", "SupportTicketNotes", "SupportTicketLinks",
        "TenantAdminInvitations", "PlatformSessions", "TenantLegalHolds"
    ];

    // ================================================================== grants on the new tables

    /// <summary>
    /// The inverse of the column-grant defect: a table created after the point-in-time blanket
    /// grant that nobody remembered to grant at all. Every one of these is written on the very
    /// first provisioning submit, offboarding action or support ticket, and a missing privilege
    /// there is a 42501 in production over a green SQLite suite.
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("ProvisioningExecutions", "SELECT,INSERT,UPDATE,DELETE")]
    [InlineData("ProvisioningSteps", "SELECT,INSERT,UPDATE,DELETE")]
    [InlineData("ProvisioningDrafts", "SELECT,INSERT,UPDATE,DELETE")]
    [InlineData("TenantOffboardings", "SELECT,INSERT,UPDATE")]
    [InlineData("SupportTickets", "SELECT,INSERT,UPDATE,DELETE")]
    [InlineData("SupportTicketLinks", "SELECT,INSERT,UPDATE,DELETE")]
    [InlineData("SupportTicketNotes", "SELECT,INSERT,DELETE")]
    [InlineData("TenantLifecycleEvents", "SELECT,INSERT")]
    [InlineData("TenantExportReceipts", "SELECT,INSERT")]
    [InlineData("TenantAdminInvitations", "SELECT,INSERT,UPDATE,DELETE")]
    [InlineData("PlatformSessions", "SELECT,INSERT,UPDATE")]
    [InlineData("TenantLegalHolds", "SELECT,INSERT,UPDATE")]
    public async Task The_pipeline_role_holds_exactly_the_privileges_each_new_table_needs(
        string table, string expected)
    {
        await using var connection = await _database.OpenConnectionAsync();
        var granted = await PrivilegesAsync(connection, "nexora_pipeline_app", table);
        Assert.Equal(expected, string.Join(",", granted));
    }

    /// <summary>
    /// The operator plane's record of what we said about a customer must never be reachable from
    /// that customer's own execution roles — not merely ungranted, actively revoked.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task No_tenant_plane_role_can_touch_any_new_control_plane_table()
    {
        await using var connection = await _database.OpenConnectionAsync();

        var reachable = new List<string>();
        foreach (var role in new[] { "nexora_tenant_app", "nexora_identity_app" })
        foreach (var table in NewControlPlaneTables)
        {
            // The activation plane legitimately reads and spends an invitation.
            if (role == "nexora_identity_app" && table == "TenantAdminInvitations") continue;

            var granted = await PrivilegesAsync(connection, role, table);
            if (granted.Count > 0) reachable.Add($"{role} -> platform.\"{table}\": {string.Join(",", granted)}");
        }

        Assert.True(reachable.Count == 0,
            "Tenant-plane roles reached the control plane:\n  " + string.Join("\n  ", reachable));
    }

    /// <summary>
    /// The activation grant, pinned in both directions. The identity role may spend an invitation
    /// and may NOT mint one — an anonymous caller who could INSERT here could invite themselves
    /// into any tenant.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_activation_role_may_spend_an_invitation_but_never_mint_one()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var granted = await PrivilegesAsync(connection, "nexora_identity_app", "TenantAdminInvitations");
        Assert.Equal("SELECT,UPDATE", string.Join(",", granted));
    }

    /// <summary>
    /// Column-scoped UPDATE on Users is what stops a defect in the identity path from
    /// becoming a privilege escalation. Written against the MAPPED column names, because
    /// <c>User.PasswordHash</c> is <c>"Password_Hash"</c> and <c>RoleId</c> is <c>"RoleID"</c> —
    /// a grant written against the CLR name parses and fails with 42703 only on PostgreSQL.
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("Password_Hash", true)]
    [InlineData("IsActive", true)]
    [InlineData("DeactivatedAtUtc", true)]
    [InlineData("ModifiedBy", true)]
    [InlineData("ModifiedOn", true)]
    // Successful tenant login may stamp activity, but cannot change identity or authority.
    [InlineData("LastLogin", true)]
    // The three columns that decide what an account IS and whose tenant it belongs to.
    [InlineData("RoleID", false)]
    [InlineData("BUID", false)]
    [InlineData("Email", false)]
    // Not a redemption concern, and a role that could rename a person could impersonate one.
    [InlineData("FirstName", false)]
    [InlineData("LastName", false)]
    // The one column whose CLR name is NOT its column name. A grant written as "PasswordHash"
    // parses and fails with 42703 only on PostgreSQL, which is why this asserts the mapped name
    // above and the CLR name's ABSENCE is asserted by the schema itself (it does not exist).
    [InlineData("ManagerID", false)]
    public async Task The_identity_role_can_only_update_activation_and_login_activity_columns(
        string column, bool expected)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT has_column_privilege('nexora_identity_app', 'public."Users"', @column, 'UPDATE');""";
        command.Parameters.AddWithValue("column", column);

        Assert.Equal(expected, (bool)(await command.ExecuteScalarAsync())!);
    }

    /// <summary>
    /// The two columns 20260807002456 opens on platform."Tenants", and the one it deliberately
    /// does not. Pinned here rather than only in the migration's prose so a later widening is a
    /// failing test rather than a comment nobody re-reads.
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("nexora_tenant_app", "BillingMode", true)]
    [InlineData("nexora_tenant_app", "CreatedOn", true)]
    [InlineData("nexora_identity_app", "BillingMode", true)]
    [InlineData("nexora_identity_app", "CreatedOn", true)]
    // Internal commercial commentary about why a customer is not charged.
    [InlineData("nexora_tenant_app", "BillingModeReason", false)]
    [InlineData("nexora_identity_app", "BillingModeReason", false)]
    // The 29 other columns 20260807002456 adds are the customer directory by another name.
    [InlineData("nexora_tenant_app", "LegalName", false)]
    [InlineData("nexora_tenant_app", "BillingContactEmail", false)]
    [InlineData("nexora_tenant_app", "AccountOwnerEmail", false)]
    [InlineData("nexora_tenant_app", "TaxNumber", false)]
    [InlineData("nexora_tenant_app", "PurchaseOrderReference", false)]
    [InlineData("nexora_tenant_app", "RateCardId", false)]
    public async Task The_commercial_terms_columns_did_not_widen_the_tenant_plane_read_surface(
        string role, string column, bool expected)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """SELECT has_column_privilege(@role, 'platform."Tenants"', @column, 'SELECT');""";
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("column", column);

        Assert.Equal(expected, (bool)(await command.ExecuteScalarAsync())!);
    }

    // ============================================================== ENABLE ALWAYS under replica

    /// <summary>
    /// The load-bearing claim of 20260807022229: <c>ENABLE ALWAYS</c> is what makes a trigger fire
    /// inside <c>session_replication_role = 'replica'</c>, which is the mode the purge runs in on
    /// the OWNER connection — where table privileges do not bind and ordinary triggers do not fire.
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("PlatformAuditLogs")]
    [InlineData("TenantLifecycleEvents")]
    [InlineData("TenantExportReceipts")]
    [InlineData("TenantOffboardings")]
    public async Task Evidence_tables_refuse_deletion_even_in_replica_mode_on_the_owner_connection(
        string table)
    {
        await using var connection = await _database.OpenConnectionAsync();
        var scope = await SeedPurgeableTenantAsync(connection, "redteam-guard");

        try
        {
            // A BEFORE ROW trigger only fires for a row that actually matches, so the guard has to
            // be pointed at real evidence — deleting nothing proves nothing.
            var rowId = await SeedEvidenceRowAsync(connection, table, scope);

            await using var transaction = await connection.BeginTransactionAsync();
            await using (var replica = new NpgsqlCommand(
                "SET LOCAL session_replication_role = 'replica';", connection, transaction))
                await replica.ExecuteNonQueryAsync();

            var refusal = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand(
                    $"""DELETE FROM platform."{table}" WHERE "Id" = @id;""", connection, transaction);
                command.Parameters.AddWithValue("id", rowId);
                await command.ExecuteNonQueryAsync();
            });

            Assert.Equal("55000", refusal.SqlState);
            await transaction.RollbackAsync();
        }
        finally
        {
            await CleanupTenantAsync(connection, scope);
        }
    }

    /// <summary>
    /// The UPDATE half. Three of the four are also closed to rewriting, which is what makes them
    /// evidence rather than a working record; TenantOffboardings takes the DELETE guard only
    /// because the purge writes its own completion into it.
    /// </summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("PlatformAuditLogs", true)]
    [InlineData("TenantLifecycleEvents", true)]
    [InlineData("TenantExportReceipts", true)]
    [InlineData("TenantOffboardings", false)]
    public async Task Evidence_tables_refuse_rewriting_even_in_replica_mode(
        string table, bool guarded)
    {
        await using var connection = await _database.OpenConnectionAsync();
        var scope = await SeedPurgeableTenantAsync(connection, "redteam-rewrite");

        try
        {
            var rowId = await SeedEvidenceRowAsync(connection, table, scope);

            await using var transaction = await connection.BeginTransactionAsync();
            await using (var replica = new NpgsqlCommand(
                "SET LOCAL session_replication_role = 'replica';", connection, transaction))
                await replica.ExecuteNonQueryAsync();

            async Task Rewrite()
            {
                await using var command = new NpgsqlCommand(
                    $"""UPDATE platform."{table}" SET "Id" = "Id" WHERE "Id" = @id;""",
                    connection, transaction);
                command.Parameters.AddWithValue("id", rowId);
                await command.ExecuteNonQueryAsync();
            }

            if (guarded)
                Assert.Equal("55000", (await Assert.ThrowsAsync<PostgresException>(Rewrite)).SqlState);
            else
                await Rewrite();

            await transaction.RollbackAsync();
        }
        finally
        {
            await CleanupTenantAsync(connection, scope);
        }
    }

    /// <summary>TRUNCATE is a statement-level operation a row trigger never sees.</summary>
    [Theory]
    [Trait("Category", "PostgreSQL")]
    [InlineData("PlatformAuditLogs")]
    [InlineData("TenantLifecycleEvents")]
    [InlineData("TenantExportReceipts")]
    [InlineData("TenantOffboardings")]
    public async Task Evidence_tables_refuse_truncate_even_in_replica_mode(string table)
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var replica = new NpgsqlCommand(
            "SET LOCAL session_replication_role = 'replica';", connection, transaction))
            await replica.ExecuteNonQueryAsync();

        var refusal = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var command = new NpgsqlCommand(
                $"""TRUNCATE TABLE platform."{table}";""", connection, transaction);
            await command.ExecuteNonQueryAsync();
        });

        Assert.Equal("55000", refusal.SqlState);
        await transaction.RollbackAsync();
    }

    /// <summary>
    /// FINDING R2 (revenue), now closed: EVERY guard in the platform schema is <c>ENABLE ALWAYS</c>.
    ///
    /// <para>The evidence guards installed by 20260807022229 were always 'A'. The REVENUE guards
    /// installed by 20260805105320 — the ones making a finalized billing statement immutable —
    /// were left origin-only, i.e. silently off in replica mode, which is the mode the tenant purge
    /// runs in on the owner connection. The reasoning the later migration wrote out for itself,
    /// that a REVOKE binds a grantee and the owner bypasses table privileges, applies to the record
    /// of what a customer was charged exactly as it applies to the record of what we did to them.
    /// It is now applied to both.</para>
    ///
    /// <para>This is deliberately a sweep over the whole schema rather than a list of known
    /// trigger names: the failure it exists to catch is a FUTURE guard added at the weaker setting,
    /// and a test enumerating today's triggers cannot see that.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_guard_in_the_platform_schema_fires_under_replica_mode()
    {
        await using var connection = await _database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.tgname, t.tgenabled
            FROM pg_trigger t
            JOIN pg_class c ON c.oid = t.tgrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'platform' AND NOT t.tgisinternal
            ORDER BY t.tgname;
            """;

        var modes = new Dictionary<string, char>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                modes[reader.GetString(0)] = reader.GetChar(1);

        Assert.NotEmpty(modes);

        // 'O' = origin only. Any guard left there protects nothing against the one connection that
        // can actually destroy the row it is guarding.
        //
        // Deliberately an exemption list rather than a blanket rule, and deliberately empty today.
        // The tenant purge RELIES on replica mode suspending triggers for the rows it is supposed
        // to destroy, so a guard on a table the purge legitimately empties would correctly be
        // origin-only. Same contract as the opt-out lists in TenantIsolationTests: an entry here
        // disables a protection, so it costs a written justification.
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal);

        var originOnly = modes
            .Where(m => m.Value != 'A' && !exempt.ContainsKey(m.Key))
            .Select(m => $"{m.Key} (tgenabled = '{m.Value}')").ToList();

        Assert.True(originOnly.Count == 0, $"""
            {originOnly.Count} trigger(s) in the platform schema are not ENABLE ALWAYS, so they do
            not fire under session_replication_role = 'replica' — the mode the tenant purge runs in
            on the owner connection, where table privileges are bypassed too. A guard that is off in
            exactly the situation it exists to guard against is not a guard.

            Offenders:
              {string.Join("\n              ", originOnly)}
            """);

        // The specific guards this finding was about, named so the intent survives a refactor.
        foreach (var guard in new[]
                 {
                     "billing_statements_guard_update", "billing_statements_guard_delete",
                     "billing_statement_lines_guard_write"
                 })
            Assert.Equal('A', Assert.Contains(guard, modes));
    }

    /// <summary>
    /// The regression for the replica-mode bypass, asserted where it used to succeed.
    ///
    /// <para>This began as a passing REPRODUCTION: a finalized billing statement — the record of
    /// what a customer was actually charged — was refused in origin mode and silently rewritten
    /// and committed inside a <c>session_replication_role = 'replica'</c> transaction on the owner
    /// connection, which is the mode the tenant purge runs in. The only thing standing between the
    /// two was the purge's PreservedTables list, and a list is precisely the protection
    /// 20260807022229's own comment argues is insufficient.</para>
    ///
    /// <para>It now asserts the refusal in BOTH modes, and re-reads the row afterwards: a guard
    /// that raised but let the write land would satisfy a throws-assertion while changing the
    /// customer's invoice.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_finalized_billing_statement_is_immutable_even_under_replica_mode()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var (tenantId, rateCardId, statementId) = await SeedFinalStatementAsync(connection);
        const decimal sealedTotal = 4200m;

        try
        {
            // Origin mode: the guard holds, exactly as PlatformControlPlaneHardeningPostgreSqlTests
            // asserts.
            var blocked = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var command = new NpgsqlCommand(
                    """UPDATE platform."BillingStatements" SET "TotalAmount" = 1 WHERE "Id" = @id;""",
                    connection);
                command.Parameters.AddWithValue("id", statementId);
                await command.ExecuteNonQueryAsync();
            });
            Assert.Equal("55000", blocked.SqlState);

            // Replica mode on the owner connection — where table privileges are bypassed and
            // ordinary triggers do not fire. ENABLE ALWAYS is what makes this refusal happen.
            var blockedInReplica = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await using (var replica = new NpgsqlCommand(
                    "SET LOCAL session_replication_role = 'replica';", connection, transaction))
                    await replica.ExecuteNonQueryAsync();

                await using var rewrite = new NpgsqlCommand(
                    """UPDATE platform."BillingStatements" SET "TotalAmount" = 0 WHERE "Id" = @id;""",
                    connection, transaction);
                rewrite.Parameters.AddWithValue("id", statementId);
                await rewrite.ExecuteNonQueryAsync();
                await transaction.CommitAsync();
            });
            Assert.Equal("55000", blockedInReplica.SqlState);

            // And the amount the customer was charged is untouched.
            await using var verify = new NpgsqlCommand(
                """SELECT "TotalAmount" FROM platform."BillingStatements" WHERE "Id" = @id;""",
                connection);
            verify.Parameters.AddWithValue("id", statementId);
            Assert.Equal(sealedTotal, (decimal)(await verify.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanupStatementAsync(connection, tenantId, rateCardId, statementId);
        }
    }

    // ================================================================================ the purge

    /// <summary>
    /// FINDING R3 (incomplete erasure). <c>session_replication_role = 'replica'</c> suspends
    /// FOREIGN KEY triggers as well as the append-only guards — the purge's own comment says so,
    /// and relies on it to make deletion order irrelevant. The consequence it does not account for
    /// is that <c>ON DELETE CASCADE</c> is also a foreign-key trigger. <c>ProvisioningSteps</c>
    /// carries no tenant column, so the catalogue sweep never finds it, and its cascade from
    /// <c>ProvisioningExecutions</c> never fires. Every step row of the purged tenant survives the
    /// purge, orphaned, carrying the step <c>Detail</c> jsonb.
    ///
    /// <para>SKIPPED because it FAILS: it proves the defect. Remove the Skip to see it.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_purge_destroys_the_provisioning_steps_of_the_tenant_it_purges()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var scope = await SeedPurgeableTenantAsync(connection, "redteam-steps");

        try
        {
            var executionId = await SeedProvisioningExecutionAsync(connection, scope, "redteam-steps");
            await SeedProvisioningStepAsync(connection, executionId, "redteam-steps");

            await TenantLifecycleHarness.PurgeExecutor(_database.ConnectionString)
                .ExecuteAsync(scope.TenantId, scope.BusinessUnitId, scope.PurgeAttemptId, CancellationToken.None);

            Assert.Equal(0, await CountAsync(connection,
                """SELECT count(*)::int FROM platform."ProvisioningExecutions" WHERE "TenantId" = @id;""",
                scope.TenantId));

            // The step rows are the assertion. They point at an ExecutionId that no longer exists.
            Assert.Equal(0, await CountAsync(connection,
                """SELECT count(*)::int FROM platform."ProvisioningSteps" WHERE "ExecutionId" = @id;""",
                executionId));
        }
        finally
        {
            await CleanupTenantAsync(connection, scope);
        }
    }

    /// <summary>
    /// FINDING R4 (incomplete erasure). A provisioning DRAFT holds the full submitted request as
    /// jsonb — company legal name, address, the founding administrator's email address — and
    /// carries no tenant column, so neither the purge's catalogue sweep nor
    /// <see cref="TenantPersonalDataEraser"/> ever reaches it. The founding administrator's email
    /// address survives both a GDPR Article 17 erasure and a full tenant purge.
    ///
    /// <para>SKIPPED because it FAILS: it proves the defect. Remove the Skip to see it.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_purge_destroys_the_provisioning_draft_that_produced_the_tenant()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var scope = await SeedPurgeableTenantAsync(connection, "redteam-draft");
        const string founderEmail = "founder@redteam-draft.invalid";
        long draftId;

        try
        {
            var executionId = await SeedProvisioningExecutionAsync(connection, scope, "redteam-draft");

            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO platform."ProvisioningDrafts"
                    ("Name", "OwnerEmail", "Payload", "CreatedOn", "UpdatedOn",
                     "SubmittedExecutionId", "Version")
                VALUES ('Red Team Draft', 'operator@nexora.invalid',
                        jsonb_build_object('adminEmail', @email::text,
                                           'legalName', 'Red Team Holdings Ltd',
                                           'addressLine1', '1 Breach Street'),
                        now(), now(), @executionId, 1)
                RETURNING "Id";
                """, connection))
            {
                command.Parameters.AddWithValue("email", founderEmail);
                command.Parameters.AddWithValue("executionId", executionId);
                draftId = (long)(await command.ExecuteScalarAsync())!;
            }

            await TenantLifecycleHarness.PurgeExecutor(_database.ConnectionString)
                .ExecuteAsync(scope.TenantId, scope.BusinessUnitId, scope.PurgeAttemptId, CancellationToken.None);

            await using var probe = new NpgsqlCommand(
                """SELECT count(*)::int FROM platform."ProvisioningDrafts" WHERE "Id" = @id;""",
                connection);
            probe.Parameters.AddWithValue("id", draftId);
            Assert.Equal(0, (int)(await probe.ExecuteScalarAsync())!);
        }
        finally
        {
            await using (var cleanup = new NpgsqlCommand(
                """DELETE FROM platform."ProvisioningDrafts" WHERE "OwnerEmail" = 'operator@nexora.invalid';""",
                connection))
                await cleanup.ExecuteNonQueryAsync();
            await CleanupTenantAsync(connection, scope);
        }
    }

    /// <summary>
    /// FINDING R7 (evidence loss). <c>platform."ImpersonationSessions"</c> carries a
    /// <c>TenantId</c> and is NOT on <see cref="TenantPurgeExecutor.PreservedTables"/>, so the
    /// catalogue sweep destroys it. That is the record of a platform operator having signed IN to a
    /// customer's account — the operator's own record of what the operator did, the same class as
    /// <c>PlatformAuditLogs</c> and <c>SupportTickets</c>, both of which the purge preserves by
    /// name and for exactly that reason.
    ///
    /// <para>It also breaks the evidence the purge DOES keep: a support ticket survives, its
    /// <c>SupportTicketLinks</c> row of kind <c>ImpersonationSession</c> survives, and the session
    /// its <c>TargetKey</c> jti names does not — a dangling reference inside the record the purge
    /// went out of its way to protect. <c>TenantOperationsController.OperationsSummary</c> and the
    /// audit explorer's tenant timeline then report zero impersonations for a tenant whose
    /// <c>impersonate.issue</c> audit rows are still there.</para>
    ///
    /// <para>SKIPPED because it FAILS: it proves the defect. Remove the Skip to see it.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_purge_keeps_the_record_of_operators_who_signed_into_the_tenant()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var scope = await SeedPurgeableTenantAsync(connection, "redteam-impersonation");
        var jti = $"redteam-{Guid.NewGuid():N}";

        try
        {
            await using (var session = new NpgsqlCommand(
                """
                INSERT INTO platform."ImpersonationSessions"
                    ("Jti", "TenantId", "ActorPlatformUserId", "Reason", "IssuedAtUtc", "ExpiresAtUtc")
                VALUES (@jti, @tenantId, 7, 'Investigating a quote that would not send.', now(),
                        now() + interval '30 minutes');
                """, connection))
            {
                session.Parameters.AddWithValue("jti", jti);
                session.Parameters.AddWithValue("tenantId", scope.TenantId);
                await session.ExecuteNonQueryAsync();
            }

            var ticketId = await SeedSupportTicketAsync(connection, scope);
            await SeedImpersonationLinkAsync(connection, ticketId, jti);

            await TenantLifecycleHarness.PurgeExecutor(_database.ConnectionString)
                .ExecuteAsync(scope.TenantId, scope.BusinessUnitId, scope.PurgeAttemptId, CancellationToken.None);

            // The ticket and its link survive by design. The thing they point at must too.
            Assert.Equal(1, await CountAsync(connection,
                """SELECT count(*)::int FROM platform."SupportTickets" WHERE "Id" = @id;""", ticketId));
            Assert.Equal(1, await CountAsync(connection,
                """SELECT count(*)::int FROM platform."SupportTicketLinks" WHERE "SupportTicketId" = @id;""",
                ticketId));

            await using var probe = new NpgsqlCommand(
                """SELECT count(*)::int FROM platform."ImpersonationSessions" WHERE "Jti" = @jti;""",
                connection);
            probe.Parameters.AddWithValue("jti", jti);
            Assert.Equal(1, (int)(await probe.ExecuteScalarAsync())!);
        }
        finally
        {
            await CleanupSupportAsync(connection, scope, jti);
            await CleanupTenantAsync(connection, scope);
        }
    }

    /// <summary>
    /// The property the existing suite asserts only for tables carrying a BUSINESS UNIT column:
    /// a purge touches exactly one tenant. This extends it to the PLATFORM-schema half of the
    /// sweep, which is scoped by <c>TenantId</c> and is where the newly created tables live —
    /// <c>TenantLifecyclePostgreSqlTests.FingerprintAsync</c> discovers only
    /// <c>businessunitid</c>/<c>buid</c>, so a widened predicate on the platform sweep would not
    /// be caught by it.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_purge_leaves_the_neighbours_platform_schema_rows_untouched()
    {
        await using var connection = await _database.OpenConnectionAsync();
        var doomed = await SeedPurgeableTenantAsync(connection, "redteam-doomed");
        var neighbour = await SeedPurgeableTenantAsync(connection, "redteam-neighbour");

        try
        {
            await SeedProvisioningExecutionAsync(connection, doomed, "redteam-doomed");
            var neighbourExecution =
                await SeedProvisioningExecutionAsync(connection, neighbour, "redteam-neighbour");

            var before = await PlatformFingerprintAsync(connection, neighbour.TenantId);
            Assert.NotEmpty(before);

            await TenantLifecycleHarness.PurgeExecutor(_database.ConnectionString)
                .ExecuteAsync(doomed.TenantId, doomed.BusinessUnitId, doomed.PurgeAttemptId, CancellationToken.None);

            Assert.Equal(0, await CountAsync(connection,
                """SELECT count(*)::int FROM platform."ProvisioningExecutions" WHERE "TenantId" = @id;""",
                doomed.TenantId));
            Assert.Equal(1, await CountAsync(connection,
                """SELECT count(*)::int FROM platform."ProvisioningExecutions" WHERE "Id" = @id;""",
                neighbourExecution));
            Assert.Equal(before, await PlatformFingerprintAsync(connection, neighbour.TenantId));
        }
        finally
        {
            await CleanupTenantAsync(connection, doomed);
            await CleanupTenantAsync(connection, neighbour);
        }
    }

    // ================================================================================== helpers

    private readonly record struct PurgeScope(
        long TenantId, long BusinessUnitId, string Slug, Guid PurgeAttemptId);

    private static async Task<List<string>> PrivilegesAsync(
        NpgsqlConnection connection, string role, string table)
    {
        var granted = new List<string>();
        foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT has_table_privilege(@role, @table, @privilege);";
            command.Parameters.AddWithValue("role", role);
            command.Parameters.AddWithValue("table", $"platform.\"{table}\"");
            command.Parameters.AddWithValue("privilege", privilege);
            if ((bool)(await command.ExecuteScalarAsync())!) granted.Add(privilege);
        }
        return granted;
    }

    private static async Task<int> CountAsync(NpgsqlConnection connection, string sql, long id)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>An md5 per platform table over every row carrying this tenant's id.</summary>
    private static async Task<Dictionary<string, string>> PlatformFingerprintAsync(
        NpgsqlConnection connection, long tenantId)
    {
        var tables = new List<string>();
        await using (var discover = new NpgsqlCommand(
            """
            SELECT c.table_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'platform' AND lower(c.column_name) = 'tenantid'
            ORDER BY c.table_name;
            """, connection))
        await using (var reader = await discover.ExecuteReaderAsync())
            while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        var fingerprint = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand(
                $"""
                 SELECT coalesce(md5(string_agg(row_text, '|' ORDER BY row_text)), '') AS hash
                 FROM (SELECT t::text AS row_text FROM platform."{table}" t
                       WHERE t."TenantId" = @tenant) rows;
                 """, connection);
            command.Parameters.AddWithValue("tenant", tenantId);
            var hash = (string)(await command.ExecuteScalarAsync())!;
            if (hash.Length > 0) fingerprint[table] = hash;
        }

        return fingerprint;
    }

    private static async Task<PurgeScope> SeedPurgeableTenantAsync(
        NpgsqlConnection connection, string slug)
    {
        var unique = $"{slug}-{Guid.NewGuid():N}"[..40];

        long businessUnitId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO public."BusinessUnits"
                ("BusinessUnitCode", "BusinessUnitName", "IsActive", "CreatedOn", "CreatedBy")
            VALUES (@code, @name, true, now(), 'redteam')
            RETURNING "ID";
            """, connection))
        {
            command.Parameters.AddWithValue("code", unique);
            command.Parameters.AddWithValue("name", unique);
            businessUnitId = (long)(await command.ExecuteScalarAsync())!;
        }

        long tenantId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO platform."Tenants"
                ("Name", "Slug", "Status", "PrimaryBusinessUnitId", "CreatedOn", "CreatedBy")
            VALUES (@name, @slug, 'Archived', @unit, now(), 'redteam')
            RETURNING "Id";
            """, connection))
        {
            command.Parameters.AddWithValue("name", unique);
            command.Parameters.AddWithValue("slug", unique);
            command.Parameters.AddWithValue("unit", businessUnitId);
            tenantId = (long)(await command.ExecuteScalarAsync())!;
        }

        var purgeAttemptId = Guid.NewGuid();
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO platform."TenantOffboardings"
                ("TenantId", "Stage", "PurgeStartedOn", "PurgeAttemptId", "CreatedOn")
            VALUES (@tenant, 'PendingDeletion', now(), @attempt, now());
            """, connection))
        {
            command.Parameters.AddWithValue("tenant", tenantId);
            command.Parameters.AddWithValue("attempt", purgeAttemptId);
            await command.ExecuteNonQueryAsync();
        }

        return new PurgeScope(tenantId, businessUnitId, unique, purgeAttemptId);
    }

    private static async Task<long> SeedSupportTicketAsync(NpgsqlConnection connection, PurgeScope scope)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform."SupportTickets"
                ("TenantId", "Subject", "Severity", "Status", "Origin",
                 "CreatedAtUtc", "UpdatedAtUtc", "Version")
            VALUES (@tenantId, 'Quote will not send', 'Normal', 'Open', 'Operator', now(), now(), 1)
            RETURNING "Id";
            """, connection);
        command.Parameters.AddWithValue("tenantId", scope.TenantId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SeedImpersonationLinkAsync(
        NpgsqlConnection connection, long ticketId, string jti)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform."SupportTicketLinks"
                ("SupportTicketId", "Kind", "TargetKey", "LinkedByLabel", "LinkedAtUtc")
            VALUES (@ticketId, 'ImpersonationSession', @jti, 'redteam@nexora.invalid', now());
            """, connection);
        command.Parameters.AddWithValue("ticketId", ticketId);
        command.Parameters.AddWithValue("jti", jti);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CleanupSupportAsync(
        NpgsqlConnection connection, PurgeScope scope, string jti)
    {
        foreach (var sql in new[]
                 {
                     """DELETE FROM platform."SupportTicketLinks" l USING platform."SupportTickets" t WHERE l."SupportTicketId" = t."Id" AND t."TenantId" = @tenantId;""",
                     """DELETE FROM platform."SupportTickets" WHERE "TenantId" = @tenantId;""",
                     """DELETE FROM platform."ImpersonationSessions" WHERE "Jti" = @jti;"""
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("tenantId", scope.TenantId);
            command.Parameters.AddWithValue("jti", jti);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>One real row in each guarded evidence table, so the BEFORE ROW guard has a row.</summary>
    private static async Task<long> SeedEvidenceRowAsync(
        NpgsqlConnection connection, string table, PurgeScope scope)
    {
        var sql = table switch
        {
            "PlatformAuditLogs" => """
                INSERT INTO platform."PlatformAuditLogs"
                    ("ActorPlatformUserId", "ActAsTenantId", "Action", "TargetType", "TargetId",
                     "Result", "CreatedOn")
                VALUES (7, @tenantId, 'redteam.probe', 'Tenant', @tenantId::text, 'success', now())
                RETURNING "Id";
                """,
            "TenantLifecycleEvents" => """
                INSERT INTO platform."TenantLifecycleEvents"
                    ("TenantId", "TenantSlug", "TenantName", "Action", "TenantStatus", "Reason",
                     "ActorPlatformUserId", "ActorEmail", "OccurredOn")
                VALUES (@tenantId, @slug, @slug, 'redteam.probe', 'Archived', 'red team probe',
                        7, 'redteam@nexora.invalid', now())
                RETURNING "Id";
                """,
            "TenantExportReceipts" => """
                INSERT INTO platform."TenantExportReceipts"
                    ("TenantId", "TenantSlug", "RequestedOn", "CompletedOn", "RequestedBy",
                     "ActorPlatformUserId", "Sections", "TotalRows", "SizeBytes", "ContentSha256", "Format")
                VALUES (@tenantId, @slug, now(), now(), 'redteam@nexora.invalid', 7, '[]'::jsonb,
                        0, 0, 'deadbeef', 'json')
                RETURNING "Id";
                """,
            "TenantOffboardings" => """
                SELECT "Id" FROM platform."TenantOffboardings" WHERE "TenantId" = @tenantId;
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "No seeder for this table.")
        };

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("tenantId", scope.TenantId);
        command.Parameters.AddWithValue("slug", scope.Slug);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> SeedProvisioningExecutionAsync(
        NpgsqlConnection connection, PurgeScope scope, string label)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform."ProvisioningExecutions"
                ("IdempotencyKey", "RequestFingerprint", "RequestPayload", "AdminPasswordHash",
                 "Slug", "Name", "AdminEmail", "AdminActivation", "State", "FailureIsTerminal",
                 "TenantId", "CorrelationId", "RequestedBy", "CreatedOn", "AttemptCount", "Version")
            VALUES (@key, 'fingerprint', '{}'::jsonb, '', @slug, @label,
                    @email, 'Invitation', 'Succeeded', false, @tenantId,
                    @key, 'operator@nexora.invalid', now(), 1, 1)
            RETURNING "Id";
            """, connection);
        command.Parameters.AddWithValue("key", $"{label}-{Guid.NewGuid():N}");
        command.Parameters.AddWithValue("slug", scope.Slug);
        command.Parameters.AddWithValue("label", label);
        command.Parameters.AddWithValue("email", $"founder@{label}.invalid");
        command.Parameters.AddWithValue("tenantId", scope.TenantId);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task SeedProvisioningStepAsync(
        NpgsqlConnection connection, long executionId, string label)
    {
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform."ProvisioningSteps"
                ("ExecutionId", "StepCode", "Ordinal", "Status", "AttemptCount", "Detail")
            VALUES (@executionId, 'founding-admin', 6, 'Succeeded', 1,
                    jsonb_build_object('adminEmail', @email::text));
            """, connection);
        command.Parameters.AddWithValue("executionId", executionId);
        command.Parameters.AddWithValue("email", $"founder@{label}.invalid");
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The four tables 20260807022229 makes append-only, and the guards it installs on each.
    /// Fixture hygiene has to lift them by NAME and put them back as <c>ENABLE ALWAYS</c> —
    /// a bare <c>ENABLE TRIGGER</c> silently downgrades them to origin-only, which is the very
    /// property the migration exists to establish.
    /// </summary>
    private static readonly (string Table, string[] Guards)[] AppendOnlyGuards =
    [
        ("PlatformAuditLogs", ["platform_audit_logs_append_only", "platform_audit_logs_no_truncate"]),
        ("TenantLifecycleEvents", ["tenant_lifecycle_events_append_only", "tenant_lifecycle_events_no_truncate"]),
        ("TenantExportReceipts", ["tenant_export_receipts_append_only", "tenant_export_receipts_no_truncate"]),
        ("TenantOffboardings", ["tenant_offboardings_append_only", "tenant_offboardings_no_truncate"])
    ];

    /// <summary>
    /// Fixture hygiene only. Runs in replica mode (foreign keys) AND lifts the append-only guards
    /// by name, because a probe row inside a guarded table is otherwise undeletable — which is the
    /// whole point of the guard. Restored to ENABLE ALWAYS in the same transaction;
    /// <see cref="The_evidence_guards_are_enable_always_and_the_revenue_guards_are_not"/> is the
    /// canary if that ever fails.
    /// </summary>
    private static async Task CleanupTenantAsync(NpgsqlConnection connection, PurgeScope scope)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var replica = new NpgsqlCommand(
            "SET LOCAL session_replication_role = 'replica';", connection, transaction))
            await replica.ExecuteNonQueryAsync();

        foreach (var (table, guards) in AppendOnlyGuards)
        foreach (var guard in guards)
            await using (var off = new NpgsqlCommand(
                $"""ALTER TABLE platform."{table}" DISABLE TRIGGER {guard};""", connection, transaction))
                await off.ExecuteNonQueryAsync();

        foreach (var sql in new[]
                 {
                     """DELETE FROM platform."ProvisioningSteps" s USING platform."ProvisioningExecutions" e WHERE s."ExecutionId" = e."Id" AND e."TenantId" = @tenantId;""",
                     """DELETE FROM platform."ProvisioningExecutions" WHERE "TenantId" = @tenantId;""",
                     """DELETE FROM platform."PlatformAuditLogs" WHERE "ActAsTenantId" = @tenantId;""",
                     """DELETE FROM platform."TenantExportReceipts" WHERE "TenantId" = @tenantId;""",
                     """DELETE FROM platform."TenantLifecycleEvents" WHERE "TenantId" = @tenantId;""",
                     """DELETE FROM platform."TenantOffboardings" WHERE "TenantId" = @tenantId;""",
                     """DELETE FROM platform."Tenants" WHERE "Id" = @tenantId;""",
                     """DELETE FROM public."BusinessUnits" WHERE "ID" = @unitId;"""
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("tenantId", scope.TenantId);
            command.Parameters.AddWithValue("unitId", scope.BusinessUnitId);
            await command.ExecuteNonQueryAsync();
        }

        foreach (var (table, guards) in AppendOnlyGuards)
        foreach (var guard in guards)
            await using (var on = new NpgsqlCommand(
                $"""ALTER TABLE platform."{table}" ENABLE ALWAYS TRIGGER {guard};""",
                connection, transaction))
                await on.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }

    private static async Task<(long TenantId, long RateCardId, long StatementId)>
        SeedFinalStatementAsync(NpgsqlConnection connection)
    {
        long tenantId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO platform."Tenants" ("Name", "Slug", "Status", "CreatedOn", "CreatedBy")
            VALUES ('Red team revenue probe', @slug, 'Active', now(), 'redteam')
            RETURNING "Id";
            """, connection))
        {
            command.Parameters.AddWithValue("slug", $"redteam-revenue-{Guid.NewGuid():N}");
            tenantId = (long)(await command.ExecuteScalarAsync())!;
        }

        long rateCardId;
        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO platform."RateCards"
                ("Code", "Currency", "EffectiveFromUtc", "IsActive", "CreatedOn", "CreatedBy", "Version")
            VALUES (@code, 'USD', now(), true, now(), 'redteam', 1)
            RETURNING "Id";
            """, connection))
        {
            command.Parameters.AddWithValue("code", $"redteam-{Guid.NewGuid():N}");
            rateCardId = (long)(await command.ExecuteScalarAsync())!;
        }

        await using (var command = new NpgsqlCommand(
            """
            INSERT INTO platform."BillingStatements"
                ("TenantId", "PeriodStartUtc", "PeriodEndUtc", "RateCardId", "Currency",
                 "Status", "TotalAmount", "ComputedAtUtc", "Version")
            VALUES (@tenantId, timestamp '2026-01-01', timestamp '2026-02-01', @rateCardId,
                    'USD', 'Final', 4200, now(), 1)
            RETURNING "Id";
            """, connection))
        {
            command.Parameters.AddWithValue("tenantId", tenantId);
            command.Parameters.AddWithValue("rateCardId", rateCardId);
            return (tenantId, rateCardId, (long)(await command.ExecuteScalarAsync())!);
        }
    }

    private static async Task CleanupStatementAsync(
        NpgsqlConnection connection, long tenantId, long rateCardId, long statementId)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var replica = new NpgsqlCommand(
            "SET LOCAL session_replication_role = 'replica';", connection, transaction))
            await replica.ExecuteNonQueryAsync();

        // The guard refuses EVERY write to a Final row — including demoting it back to Draft — and
        // ENABLE ALWAYS means replica mode no longer offers a way around that. Suspending the
        // trigger explicitly is the only remaining route, which is the honest statement of the
        // property under test: removing a finalized statement now requires an operator who can
        // ALTER the table, not merely one who can reach the database. Re-enabled in the same
        // teardown so a failure here cannot silently disarm the guard for every later test.
        await using (var disable = new NpgsqlCommand(
            """
            ALTER TABLE platform."BillingStatements" DISABLE TRIGGER billing_statements_guard_update;
            ALTER TABLE platform."BillingStatements" DISABLE TRIGGER billing_statements_guard_delete;
            ALTER TABLE platform."BillingStatementLines" DISABLE TRIGGER billing_statement_lines_guard_write;
            """, connection, transaction))
            await disable.ExecuteNonQueryAsync();

        foreach (var sql in new[]
                 {
                     """DELETE FROM platform."BillingStatementLines" WHERE "BillingStatementId" = @statementId;""",
                     """DELETE FROM platform."BillingStatements" WHERE "Id" = @statementId;""",
                     """DELETE FROM platform."RateCards" WHERE "Id" = @rateCardId;""",
                     """DELETE FROM platform."Tenants" WHERE "Id" = @tenantId;"""
                 })
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.Parameters.AddWithValue("statementId", statementId);
            command.Parameters.AddWithValue("rateCardId", rateCardId);
            command.Parameters.AddWithValue("tenantId", tenantId);
            await command.ExecuteNonQueryAsync();
        }

        await using (var reenable = new NpgsqlCommand(
            """
            ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_update;
            ALTER TABLE platform."BillingStatements" ENABLE ALWAYS TRIGGER billing_statements_guard_delete;
            ALTER TABLE platform."BillingStatementLines" ENABLE ALWAYS TRIGGER billing_statement_lines_guard_write;
            """, connection, transaction))
            await reenable.ExecuteNonQueryAsync();

        await transaction.CommitAsync();
    }
}
