using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

public sealed record TenantPurgeTableCount(string Table, string TenantColumn, long Rows);

/// <summary>One table a purge deliberately leaves standing, and the reason it does.</summary>
/// <param name="Table">Qualified <c>schema.table</c>, as the preview reports every other table.</param>
/// <param name="Reason">
/// Copied from <see cref="PlatformTenantDataMap"/> rather than restated, so the sentence an
/// operator reads at the confirmation dialog is the same sentence the next engineer reads when
/// they add a table and have to make the same call.
/// </param>
public sealed record TenantPurgePreservedTable(string Table, string Reason);

public sealed record TenantPurgePreview(
    long TenantId,
    long BusinessUnitId,
    IReadOnlyList<TenantPurgeTableCount> Tables,
    long TotalRows,
    IReadOnlyList<string> Preserved,
    /// <summary>
    /// The same set as <paramref name="Preserved"/> with the reason attached. Added because a bare
    /// list of table names answers "what survives" and not "why", and "why" is the half an operator
    /// has to be able to repeat to the customer who is asking what we still hold on them.
    /// </summary>
    IReadOnlyList<TenantPurgePreservedTable> PreservedDetail);

public sealed record TenantPurgeOutcome(
    long TenantId,
    long BusinessUnitId,
    long RowsDeleted,
    IReadOnlyList<TenantPurgeTableCount> Deleted)
{
    /// <summary>
    /// How many tables the sweep visited. Present because <see cref="Deleted"/> lists only the
    /// tables that yielded rows, and "absent from the report" is exactly how a table the purge
    /// failed to clear used to look. A reader can now tell "swept 196, 41 held rows" from
    /// "swept 41".
    /// </summary>
    public int TablesSwept { get; init; }

    /// <summary>
    /// How many of those tables were re-counted after the deletes and proved to hold none of this
    /// tenant's rows, inside the same transaction and before it committed. Equal to
    /// <see cref="TablesSwept"/> on every outcome that exists, because a shortfall throws rather
    /// than returning.
    /// </summary>
    public int TablesVerifiedEmpty { get; init; }

    /// <summary>
    /// How many tenant-scoped tables an INDEPENDENT check — derived from the catalogue and from
    /// the schema's own <c>nexora_tenant_isolation</c> declarations, never from the sweep's target
    /// list — confirmed were both visited and empty.
    ///
    /// <para>Reported separately from <see cref="TablesSwept"/> because the two numbers answering
    /// differently is the whole point. The old post-condition re-counted the tables the sweep had
    /// chosen, so it could only confirm that the sweep did what the sweep did: eleven snake_case
    /// tables were missing from the sweep, therefore missing from the check, therefore never
    /// reported. A reader can now tell "swept 220 and independently accounted for 229" from
    /// "swept 220 and checked the same 220".</para>
    /// </summary>
    public int TablesIndependentlyVerified { get; init; }
}

/// <summary>
/// Destroys one tenant's rows. Everything of theirs, everywhere, in one transaction.
///
/// <para><b>This is deliberately the same machinery as <c>TenantDataReset</c>, with the keep-list
/// inverted.</b> The reset keeps configuration and destroys transactions so a pilot can re-ingest;
/// a purge keeps almost nothing, because the customer has left. Sharing the shape is intentional:
/// the reset's two hard-won lessons apply unchanged here, and they are the two ways a
/// tenant-scoped delete silently fails.</para>
///
/// <para><b>Lesson one — triggers, not foreign keys, are the hard part.</b> Roughly thirty
/// append-only guards (<c>nexora_evidence_append_only</c>, <c>trg_source_documents_no_delete</c>
/// and friends) <c>RAISE EXCEPTION</c> on any DELETE, unconditionally and by design. They are
/// suspended for the life of one transaction via <c>session_replication_role = 'replica'</c>,
/// which requires the OWNER connection — hence <c>ConnectionStrings:MigrationConnection</c>
/// rather than the request-path role. The same setting suspends foreign-key triggers, which is
/// what makes deletion order irrelevant. It is set LOCAL, so it lapses with the transaction: if
/// it ever leaked, every immutability guarantee in the system would be silently off for the rest
/// of that connection's life.</para>
///
/// <para><b>Lesson two — which tables are cleared is derived from the live catalogue, not from a
/// list.</b> A hand-maintained delete list stops covering new tables the moment somebody adds
/// one, and the failure is invisible: the tenant is reported destroyed and their rows are still
/// there. The catalogue is also read rather than the EF model because the CLR property name and
/// the mapped column diverge across this schema (<c>BusinessUnitId</c> vs <c>"BusinessUnitID"</c>,
/// <c>Buid</c> vs <c>"BUID"</c>) and because several evidence and outbox tables are managed purely
/// by migrations and are not in the model at all.</para>
///
/// <para><b>Lesson three — the catalogue is the wrong authority for the PLATFORM plane.</b> The
/// two lessons above are about the tenant plane, where every table carries a business unit and
/// "derive it, do not list it" is right. The platform schema is the opposite case and cost two
/// defects to learn. Sweeping it for columns named <c>TenantId</c> destroyed
/// <c>ImpersonationSessions</c> — the record of operators signing into the customer's account,
/// which had to be KEPT — and missed <c>ProvisioningSteps</c> and <c>ProvisioningDrafts</c>, which
/// reach a tenant only through <c>ProvisioningExecutions</c> and had to be DESTROYED. A column
/// name cannot answer "whose record is this", so the platform half is driven by
/// <see cref="PlatformTenantDataMap"/>, where every table says which it is and why.</para>
///
/// <para><b>And cascades do not save the children.</b> <c>ProvisioningSteps</c> cascades from its
/// execution, but <c>ON DELETE CASCADE</c> is a foreign-key trigger and replica mode suspends
/// those too — the same suppression this purge relies on to make deletion order irrelevant. Rows
/// reached through a parent are therefore deleted explicitly, and deepest-first: a child is
/// selected through a subquery on its parent, so deleting the parent first would leave every child
/// behind.</para>
/// </summary>
public sealed class TenantPurgeExecutor(IConfiguration configuration, ILogger<TenantPurgeExecutor> logger)
{
    /// <summary>
    /// The role a purge reads and deletes under, created by
    /// <c>20260811154500_TenantPurgeExecutionRole</c>.
    ///
    /// <para><b>Lesson four — the owner connection is not exempt from row-level security, and
    /// where it is not, it deletes nothing and says so to nobody.</b> This executor used to run
    /// every statement on the bare owner connection. It has to OPEN that connection —
    /// <c>session_replication_role</c> is superuser-restricted, and it is what suspends the
    /// append-only guards — but issuing the DELETEs on it was the defect. 100 of the 195
    /// tenant-plane tables a purge sweeps are declared <c>FORCE ROW LEVEL SECURITY</c>, which
    /// makes the OWNER subject to its own policies, and every one of those policies is written
    /// <c>TO nexora_tenant_app</c>. PostgreSQL matches a policy's role list with
    /// <c>has_privs_of_role()</c>, and the runtime role is <c>NOINHERIT</c>
    /// (<c>Program.cs ValidateRuntimeDatabaseRoleAsync</c>), so membership is not enough: no
    /// policy applied, the default was deny, and <c>DELETE</c> returned 0 without raising. The
    /// executor recorded a table only when <c>rows &gt; 0</c>, so those tables were simply absent
    /// from the report. An offboarding could complete, report success, and leave the customer's
    /// data in place — while <c>public."BusinessUnits"</c>, which is NOT forced, was destroyed, so
    /// the tenant vanished from every screen at the same moment their rows became unreachable.
    /// </para>
    ///
    /// <para>Making the owner INHERIT would not have fixed it: with an inheriting owner the policy
    /// role list matches and the DELETE still returned 0, because this code never set
    /// <c>nexora.business_unit_id</c> and the policy predicate evaluated against NULL. Both were
    /// reproduced against postgres:16 before either was changed.</para>
    ///
    /// <para>So the purge now has an identity of its own. It is <c>NOBYPASSRLS</c> on purpose:
    /// with <see cref="PurgeScopeSetting"/> unset it can see nothing at all, and with it set it
    /// can see exactly one tenant. The database, not this file's WHERE clauses, is now what stops
    /// a purge reaching a second customer.</para>
    /// </summary>
    public const string PurgeRole = "nexora_purge_app";

    /// <summary>
    /// The GUC every <c>nexora_tenant_purge</c> policy is written against. Deliberately NOT
    /// <c>nexora.business_unit_id</c>: that one is set by <c>TenantRlsCommandInterceptor</c> on
    /// every request, and sharing it would mean a request-path session and a destructive sweep
    /// were distinguished by role alone. Two names make the two intents impossible to confuse in
    /// a policy definition, in a log, or in <c>pg_stat_activity</c>.
    /// </summary>
    public const string PurgeScopeSetting = "nexora.purge_business_unit_id";

    /// <summary>The policy name this executor requires on every RLS-enabled target.</summary>
    private const string PurgePolicyName = "nexora_tenant_purge";

    /// <summary>
    /// Tables a purge must never touch, as <c>schema.table</c>.
    ///
    /// <para>Derived from <see cref="PlatformTenantDataMap"/> rather than restated, so the reason a
    /// table survives lives in exactly one place — next to the reason its neighbour does not. Every
    /// entry is the operator's record OF the customer, or a statutory obligation the operator
    /// carries after the customer has gone.</para>
    ///
    /// <para>Note what is deliberately absent: the tenant's OWN audit trails
    /// (<c>public."IamAuditEvents"</c>, the AI ledger, the evidence tables) are destroyed with
    /// everything else. Tenant-plane audit is the tenant's data; platform-plane audit is not.</para>
    /// </summary>
    public static readonly IReadOnlySet<string> PreservedTables =
        PlatformTenantDataMap.Preserved
            .Select(t => $"{PlatformTenantDataMap.Schema}.{t.Table}")
            // Not tenant-scoped at all, so it is not in the map; named here because a sweep that
            // deleted from it would break the database rather than a customer.
            .Append("public.__EFMigrationsHistory")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The preserved set with the reason each entry survives, in the same order as
    /// <see cref="PreservedTables"/>.
    ///
    /// <para><c>__EFMigrationsHistory</c> is present in <see cref="PreservedTables"/> but is not a
    /// tenant table and carries no entry in the map, so it is described here rather than left to
    /// appear as a nameless survivor on the operator's screen.</para>
    /// </summary>
    public static readonly IReadOnlyList<TenantPurgePreservedTable> PreservedWithReasons =
        PlatformTenantDataMap.Preserved
            .Select(t => new TenantPurgePreservedTable(
                $"{PlatformTenantDataMap.Schema}.{t.Table}", t.Reason))
            .Append(new TenantPurgePreservedTable(
                "public.__EFMigrationsHistory",
                "The database's own schema ledger. Not the customer's data and not a record of "
                + "them; deleting from it would break the database rather than a tenant."))
            .OrderBy(t => t.Table, StringComparer.Ordinal)
            .ToList();

    /// <summary>The business unit's own row, whose tenant column is its primary key rather than a
    /// <c>BusinessUnitId</c>, so the catalogue sweep below cannot find it.</summary>
    private const string BusinessUnitTable = "public.\"BusinessUnits\"";

    /// <summary>
    /// The primary-key column of <c>public."BusinessUnits"</c>, read from the catalogue.
    ///
    /// <para>Not hardcoded, and the first version of this WAS: the entity property is
    /// <c>Id</c> and the mapped column is <c>"ID"</c>, so <c>WHERE "Id" = @scope</c> answers
    /// 42703 against the real schema while every portable test stays green. That is the same
    /// class of defect as <c>BusinessUnitID</c> vs <c>BusinessUnitId</c>, and the same fix
    /// applies: the database already knows the truth.</para>
    /// </summary>
    private static async Task<string> BusinessUnitKeyColumnAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT a.attname
            FROM pg_index i
            JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY (i.indkey)
            WHERE i.indrelid = 'public."BusinessUnits"'::regclass
              AND i.indisprimary
            LIMIT 1;
            """, connection, transaction);

        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "public.\"BusinessUnits\" has no primary key, so the tenant's own workspace row "
                + "cannot be identified.");
    }

    /// <summary>
    /// What a purge would destroy, counted through the SAME identity that will destroy it.
    ///
    /// <para>This used to count on the bare owner connection, and on the 100 forced tables the
    /// owner sees nothing — so every one of them counted zero and was dropped by the
    /// <c>rows &gt; 0</c> filter below. The number on the confirmation screen an operator reads
    /// before authorising destruction was therefore a floor, not a total, and the tables missing
    /// from it were exactly the tables the execution was also going to miss. Preview and execute
    /// now enter the same scope, so if one can see a row the other can delete it, and if neither
    /// can, both refuse.</para>
    ///
    /// <para>The transaction exists only to hold <c>SET LOCAL ROLE</c> and the scope GUC — a
    /// preview writes nothing and always rolls back.</para>
    /// </summary>
    public async Task<TenantPurgePreview> PreviewAsync(
        long tenantId, long businessUnitId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenOwnerConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Discovered as the OWNER, before the role switch. information_schema is filtered by the
        // caller's privileges, so a sweep run as nexora_purge_app would only find the tables that
        // role can already reach — the tables it CANNOT reach, which is the entire point of the
        // assertion below, would silently not be targets at all.
        var targets = await TargetsAsync(connection, transaction, cancellationToken);
        var mustBeEmpty = await TenantScopedTablesAsync(connection, transaction, cancellationToken);
        var keyColumn = await BusinessUnitKeyColumnAsync(connection, transaction, cancellationToken);
        await AssertPurgeReachAsync(connection, transaction, targets, mustBeEmpty, cancellationToken);
        await EnterPurgeScopeAsync(connection, transaction, businessUnitId, cancellationToken);

        var counts = new List<TenantPurgeTableCount>();
        foreach (var target in targets)
        {
            var rows = await CountAsync(connection, transaction, target, businessUnitId, tenantId, cancellationToken);
            if (rows > 0) counts.Add(new TenantPurgeTableCount(target.Qualified, target.Column, rows));
        }

        var businessUnitRows = await ScalarAsync(
            connection, transaction,
            $"""SELECT count(*)::bigint FROM {BusinessUnitTable} WHERE "{keyColumn}" = @scope;""",
            businessUnitId, cancellationToken);
        if (businessUnitRows > 0)
            counts.Add(new TenantPurgeTableCount(BusinessUnitTable, keyColumn, businessUnitRows));

        await transaction.RollbackAsync(cancellationToken);

        return new TenantPurgePreview(
            tenantId, businessUnitId,
            counts.OrderByDescending(c => c.Rows).ToList(),
            counts.Sum(c => c.Rows),
            PreservedTables.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            PreservedWithReasons);
    }

    /// <summary>
    /// Every storage path this tenant's rows still point at, read BEFORE anything is destroyed.
    ///
    /// <para><b>The ordering is the point.</b> Deleting a tenant's rows destroys the only index
    /// back to their bytes. Ask afterwards and the answer is nothing — which is precisely how 273
    /// objects and the raw <c>.eml</c> of every message survived a purge that reported success:
    /// not because anybody decided to keep them, but because after the transaction nothing could
    /// name them any more.</para>
    ///
    /// <para>Three columns hold paths, and they are read from three different shapes.
    /// <c>source_documents</c> carries a proper (bucket, key, version) triple and is scoped
    /// directly. <c>public."EmailIngests"</c> and <c>public."Attachments"</c> carry a bare path and
    /// are scoped only through a parent — the same tables defect P0-2 is about, which is not a
    /// coincidence: a table the sweep could not see is a table whose bytes nothing was tracking
    /// either.</para>
    ///
    /// <para>Read through the purge role and the purge scope, like everything else, so this cannot
    /// return another tenant's paths even if a predicate here were wrong. Writes nothing and always
    /// rolls back.</para>
    /// </summary>
    public async Task<IReadOnlyList<string>> CaptureStoragePathsAsync(
        long businessUnitId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenOwnerConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var targets = await TargetsAsync(connection, transaction, cancellationToken);
        var mustBeEmpty = await TenantScopedTablesAsync(connection, transaction, cancellationToken);
        await AssertPurgeReachAsync(connection, transaction, targets, mustBeEmpty, cancellationToken);
        await EnterPurgeScopeAsync(connection, transaction, businessUnitId, cancellationToken);

        var paths = new List<string>();
        foreach (var target in targets)
        {
            if (!StoragePathColumns.TryGetValue(target.Qualified, out var column)) continue;

            await using var command = new NpgsqlCommand(
                $"""
                 SELECT DISTINCT t."{column}"
                 FROM {target.Qualified} t
                 WHERE ({target.Predicate}) AND t."{column}" IS NOT NULL AND t."{column}" <> '';
                 """, connection, transaction);
            command.Parameters.AddWithValue("scope", businessUnitId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                paths.Add(reader.GetString(0));
        }

        await transaction.RollbackAsync(cancellationToken);
        return paths;
    }

    /// <summary>
    /// The columns that hold a path into byte storage, as <c>qualified table -&gt; column</c>.
    ///
    /// <para>Deliberately a short, named list rather than a catalogue sweep for "columns that look
    /// like a path". A wrong entry here means deleting a file the tenant does not own, and
    /// <c>public."Images".FilePath</c> — which matches any such heuristic — has no writer anywhere
    /// in the application and no tenant linkage at all. <c>TenantStoragePathCoverageTests</c> is
    /// what keeps this list honest against the schema.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> StoragePathColumns =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["\"public\".\"source_documents\""] = "object_key",
            ["\"public\".\"EmailIngests\""] = "RawEmailPath",
            ["\"public\".\"Attachments\""] = "FilePath"
        };

    /// <summary>
    /// Destroys the tenant. One transaction: either every row of theirs is gone, or none is.
    /// A half-purged tenant is worse than an un-purged one — it cannot be used, cannot be
    /// restored, and cannot be reasoned about.
    /// </summary>
    public async Task<TenantPurgeOutcome> ExecuteAsync(
        long tenantId, long businessUnitId, Guid purgeAttemptId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenOwnerConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // This row lock is the liveness proof for the destructive executor. A stale claimant,
        // restore, legal-hold placement, or another purge must wait for this transaction. The
        // token check fences an executor that was paused before it entered the transaction and
        // whose lease was legitimately taken over in the meantime.
        await using (var fence = new NpgsqlCommand(
            """
            SELECT 1
            FROM platform."TenantOffboardings"
            WHERE "TenantId" = @tenant
              AND "Stage" = 'PendingDeletion'
              AND "PurgeAttemptId" = @attempt
              AND "PurgeStartedOn" IS NOT NULL
              AND "PurgeExecutedOn" IS NULL
              AND NOT EXISTS (
                  SELECT 1 FROM platform."TenantLegalHolds" h
                  WHERE h."TenantId" = @tenant AND h."ReleasedOn" IS NULL)
            FOR UPDATE;
            """, connection, transaction))
        {
            fence.Parameters.AddWithValue("tenant", tenantId);
            fence.Parameters.AddWithValue("attempt", purgeAttemptId);
            if (await fence.ExecuteScalarAsync(cancellationToken) is null)
                throw new InvalidOperationException(
                    $"Purge attempt {purgeAttemptId} for tenant {tenantId} was fenced before execution.");
        }

        // Suspends the append-only guards AND the foreign-key triggers for this session only,
        // scoped to the transaction so a failure anywhere rolls back with the guards intact.
        //
        // ORDER MATTERS, and it is the reason this sequence works at all. The setting is
        // superuser-restricted, so it must be issued while the session is still on the OWNER's
        // login role; nexora_purge_app cannot set it and a SET LOCAL ROLE first would answer
        // 42501. Verified against postgres:16: a session that enters replica mode and THEN
        // switches role keeps replica mode for the life of the transaction, and RESET ROLE
        // returns to the owner with it still in force.
        await using (var replica = new NpgsqlCommand(
            "SET LOCAL session_replication_role = 'replica';", connection, transaction))
            await replica.ExecuteNonQueryAsync(cancellationToken);

        // Both discovered as the OWNER. See PreviewAsync for why this cannot move below the role
        // switch: information_schema hides tables the caller holds no privilege on, so the
        // catalogue sweep would quietly shrink to whatever the purge role could already reach.
        var targets = await TargetsAsync(connection, transaction, cancellationToken);
        var mustBeEmpty = await TenantScopedTablesAsync(connection, transaction, cancellationToken);
        var keyColumn = await BusinessUnitKeyColumnAsync(connection, transaction, cancellationToken);

        await AssertPurgeReachAsync(connection, transaction, targets, mustBeEmpty, cancellationToken);
        await EnterPurgeScopeAsync(connection, transaction, businessUnitId, cancellationToken);

        // Taken before a single DELETE, so "orphans this purge created" is answerable afterwards.
        var orphanBaseline = await OrphanBaselineAsync(
            connection, transaction, mustBeEmpty, cancellationToken);

        var deleted = new List<TenantPurgeTableCount>();
        var survivors = new List<string>();
        var swept = new HashSet<string>(StringComparer.Ordinal);
        var verified = 0;
        foreach (var target in targets)
        {
            await using var command = new NpgsqlCommand(
                $"""DELETE FROM {target.Qualified} t WHERE {target.Predicate};""",
                connection, transaction);
            command.Parameters.AddWithValue("scope", target.Scope == TenantScope.Tenant ? tenantId : businessUnitId);

            int rows;
            try
            {
                rows = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception)
            {
                // Naming the table matters: a bare SQLSTATE mid-sweep gives no way to tell which
                // of ~200 tables drifted, and the transaction has already rolled back by then.
                throw new InvalidOperationException(
                    $"Purge failed deleting from {target.Qualified} using column \"{target.Column}\": "
                    + $"{exception.SqlState} {exception.MessageText}", exception);
            }

            if (rows > 0) deleted.Add(new TenantPurgeTableCount(target.Qualified, target.Column, rows));
            swept.Add(target.Qualified);

            // THE POST-CONDITION, and it is taken HERE rather than after the sweep. A table
            // reached through a parent is selected by a subquery on that parent, and the parent is
            // deleted LATER in this same loop (PlatformTenantDataMap orders deepest-first, so a
            // child is deleted while the row its subquery selects through still exists). Counting
            // such a child after the sweep would be asking how many rows hang off a parent that no
            // longer exists — which is zero whatever happened to the child, and would be the
            // original defect rebuilt inside its own verification.
            var remaining = await CountAsync(
                connection, transaction, target, businessUnitId, tenantId, cancellationToken);
            verified++;
            if (remaining > 0)
                survivors.Add(
                    $"{target.Qualified} still holds {remaining} row(s) matching \"{target.Column}\"");
        }

        // Last, and by primary key: the business unit is the anchor every sweep above resolved
        // against, and it is the one tenant row whose tenant column is its own id.
        await using (var unit = new NpgsqlCommand(
            $"""DELETE FROM {BusinessUnitTable} WHERE "{keyColumn}" = @scope;""", connection, transaction))
        {
            unit.Parameters.AddWithValue("scope", businessUnitId);
            var rows = await unit.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0) deleted.Add(new TenantPurgeTableCount(BusinessUnitTable, keyColumn, rows));
        }

        var unitRemaining = await ScalarAsync(
            connection, transaction,
            $"""SELECT count(*)::bigint FROM {BusinessUnitTable} WHERE "{keyColumn}" = @scope;""",
            businessUnitId, cancellationToken);
        verified++;
        if (unitRemaining > 0)
            survivors.Add($"{BusinessUnitTable} still holds the tenant's own workspace row");

        if (survivors.Count > 0)
            throw new InvalidOperationException(
                $"Purge ABORTED for business unit {businessUnitId}: the destructive transaction ran "
                + $"to completion but {survivors.Count} table(s) still hold this tenant's rows, so "
                + $"nothing has been committed. {string.Join("; ", survivors)}. A purge that cannot "
                + "prove the rows are gone must not report that they are.");

        // AND THEN THE INDEPENDENT ONE. Everything above re-counts the tables the sweep chose,
        // which can only ever confirm that the sweep did what the sweep did — the check that let
        // eleven snake_case tables and fourteen indirect ones pass unnoticed. This asks a set of
        // tables derived from the schema, and its first question is not "how many rows are left"
        // but "was this table visited at all".
        swept.Add(BusinessUnitTable);
        await AssertNoTenantRowsRemainAsync(
            connection, transaction, mustBeEmpty, swept, orphanBaseline,
            businessUnitId, tenantId, cancellationToken);

        // Back to the owner for the bookkeeping below: platform."TenantOffboardings" carries no
        // row-level security and no grant to the purge role, deliberately. The record that a
        // purge happened is the operator's, and the identity that performs destruction should not
        // be able to write the evidence of it.
        await using (var reset = new NpgsqlCommand("RESET ROLE;", connection, transaction))
            await reset.ExecuteNonQueryAsync(cancellationToken);

        var total = deleted.Sum(d => d.Rows);
        var executedOn = DateTime.UtcNow;
        var detail = JsonSerializer.Serialize(deleted.OrderByDescending(d => d.Rows));
        await using (var outcome = new NpgsqlCommand(
            """
            UPDATE platform."TenantOffboardings"
            SET "PurgeExecutedOn" = @executed,
                "PurgeExecutedRowCount" = @rows,
                "PurgeExecutionDetail" = @detail,
                "ModifiedOn" = @executed
            WHERE "TenantId" = @tenant
              AND "Stage" = 'PendingDeletion'
              AND "PurgeAttemptId" = @attempt
              AND "PurgeExecutedOn" IS NULL;
            """, connection, transaction))
        {
            outcome.Parameters.AddWithValue("executed", executedOn);
            outcome.Parameters.AddWithValue("rows", total);
            outcome.Parameters.Add("detail", NpgsqlDbType.Jsonb).Value = detail;
            outcome.Parameters.AddWithValue("tenant", tenantId);
            outcome.Parameters.AddWithValue("attempt", purgeAttemptId);
            if (await outcome.ExecuteNonQueryAsync(cancellationToken) != 1)
                throw new InvalidOperationException(
                    $"Purge attempt {purgeAttemptId} for tenant {tenantId} lost its fence before commit.");
        }

        await transaction.CommitAsync(cancellationToken);

        logger.LogWarning(
            "TENANT PURGE complete for tenant {TenantId} (business unit {BusinessUnitId}): "
            + "{Rows} row(s) destroyed across {Tables} table(s); {Swept} table(s) swept, and "
            + "{Independent} tenant-scoped table(s) derived from the schema independently of that "
            + "sweep proved to hold none of this tenant's rows before commit.",
            tenantId, businessUnitId, total, deleted.Count, verified, mustBeEmpty.Count);

        return new TenantPurgeOutcome(
            tenantId, businessUnitId, total, deleted.OrderByDescending(d => d.Rows).ToList())
        {
            TablesSwept = verified,
            TablesVerifiedEmpty = verified,
            TablesIndependentlyVerified = mustBeEmpty.Count
        };
    }

    /// <summary>
    /// Puts the session into the purge scope: the tenant the policies will admit, then the role
    /// they are written for.
    ///
    /// <para>The GUC is set FIRST and as the owner. It is transaction-local, like everything else
    /// here, so it lapses on commit or rollback — a leaked purge scope on a pooled connection
    /// would be an authorisation for whatever ran next on it.</para>
    ///
    /// <para>A failure to enter the role is reported as a deployment fault rather than a purge
    /// fault, because that is what it is: the role and its policies arrive together in
    /// <c>20260811154500_TenantPurgeExecutionRole</c>, and a database that has not applied it
    /// cannot delete a tenant correctly no matter what this code does.</para>
    /// </summary>
    private static async Task EnterPurgeScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, long businessUnitId,
        CancellationToken cancellationToken)
    {
        await using (var scope = new NpgsqlCommand(
            $"SELECT set_config('{PurgeScopeSetting}', @scope, true);", connection, transaction))
        {
            scope.Parameters.AddWithValue(
                "scope", businessUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await scope.ExecuteScalarAsync(cancellationToken);
        }

        try
        {
            await using var role = new NpgsqlCommand($"SET LOCAL ROLE {PurgeRole};", connection, transaction);
            await role.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"The purge could not assume {PurgeRole}: {exception.SqlState} {exception.MessageText}. "
                + "That role and the nexora_tenant_purge policies it is named in are created by "
                + "20260811154500_TenantPurgeExecutionRole, and the migration grants it to the role "
                + "that runs migrations — which must be the same login as "
                + "ConnectionStrings:MigrationConnection. Until that holds, a purge would delete "
                + "nothing from any table declared FORCE ROW LEVEL SECURITY and report success.",
                exception);
        }
    }

    /// <summary>
    /// Refuses to start unless every table the sweep is about to touch can actually be reached by
    /// <see cref="PurgeRole"/>.
    ///
    /// <para><b>Why this is separate from the post-condition check below, and why neither is
    /// sufficient alone.</b> A count taken through an identity that row-level security is
    /// filtering returns zero for the same reason the DELETE affected zero rows. Verifying "no
    /// rows remain" through such an identity is not a weak check, it is a check that CANNOT FAIL
    /// — which is precisely the shape of the original defect, reproduced one level up. So the
    /// reachability of every target is established from the catalogue FIRST, where row-level
    /// security cannot lie about itself, and only then is the count taken.</para>
    ///
    /// <para>Three ways a target can be out of reach, all of them fatal and all of them named:
    /// no privilege (the purge would answer 42501 mid-sweep); row-level security enabled with no
    /// <c>nexora_tenant_purge</c> policy for this role (silent zero — the original defect, and
    /// how a table added by a later migration would re-introduce it); and a table carrying BOTH a
    /// <c>BusinessUnitId</c> and a <c>BUID</c>, where the sweep would emit two targets while the
    /// policy admits only one column, so one of the two would silently match nothing. The last
    /// does not occur in the schema today — it is asserted because the first two did not occur
    /// either, until they did.</para>
    /// </summary>
    private static async Task AssertPurgeReachAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<PurgeTarget> targets, IReadOnlyList<PurgeTarget> mustBeEmpty,
        CancellationToken cancellationToken)
    {
        await using (var roleExists = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @role);", connection, transaction))
        {
            roleExists.Parameters.AddWithValue("role", PurgeRole);
            if (await roleExists.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException(
                    $"Purge refused: the database has no {PurgeRole} role. It is created by "
                    + "20260811154500_TenantPurgeExecutionRole together with the "
                    + $"{PurgePolicyName} policies that admit it. Without both, a DELETE issued on "
                    + "the owner connection silently affects zero rows on every table declared "
                    + "FORCE ROW LEVEL SECURITY, and the tenant is reported destroyed with their "
                    + "data still present.");
        }

        // Both lists, because the post-condition looks at tables the sweep does not. A count taken
        // through an identity that row-level security is filtering returns zero for the same
        // reason a DELETE would have affected zero rows, so a verification target the purge role
        // cannot reach is a verification that CANNOT FAIL — the original defect one level up.
        var qualified = targets.Select(t => t.Qualified)
            .Concat(mustBeEmpty.Select(t => t.Qualified))
            .Append(BusinessUnitTable)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var unreachable = new List<string>();

        await using var command = new NpgsqlCommand(
            $"""
            WITH requested (qualified) AS (SELECT unnest(@targets::text[])),
            resolved AS (
                SELECT r.qualified, c.oid, c.relrowsecurity
                FROM requested r
                JOIN pg_class c ON c.oid = to_regclass(r.qualified))
            SELECT qualified
                   || CASE WHEN NOT has_table_privilege(@role, oid, 'SELECT')
                                  OR NOT has_table_privilege(@role, oid, 'DELETE')
                           THEN ' (no SELECT/DELETE privilege)' ELSE '' END
                   || CASE WHEN relrowsecurity AND NOT EXISTS (
                                  SELECT 1 FROM pg_policy p
                                  WHERE p.polrelid = resolved.oid
                                    AND p.polname = @policy
                                    AND p.polcmd = '*'
                                    AND @role::regrole = ANY (p.polroles))
                           THEN ' (row-level security is on and no ' || @policy || ' policy admits '
                                || @role || ', so a DELETE here would affect zero rows and raise nothing)'
                           ELSE '' END
                   || CASE WHEN (SELECT count(*) FROM pg_attribute a
                                 WHERE a.attrelid = resolved.oid AND a.attnum > 0
                                   AND NOT a.attisdropped
                                   AND lower(replace(a.attname, '_', '')) IN ('businessunitid', 'buid')) > 1
                           THEN ' (carries both BusinessUnitId and BUID; the sweep and the policy '
                                || 'would disagree about which one scopes it)'
                           ELSE '' END AS problem
            FROM resolved
            WHERE NOT has_table_privilege(@role, oid, 'SELECT')
               OR NOT has_table_privilege(@role, oid, 'DELETE')
               OR (relrowsecurity AND NOT EXISTS (
                       SELECT 1 FROM pg_policy p
                       WHERE p.polrelid = resolved.oid
                         AND p.polname = @policy
                         AND p.polcmd = '*'
                         AND @role::regrole = ANY (p.polroles)))
               OR (SELECT count(*) FROM pg_attribute a
                   WHERE a.attrelid = resolved.oid AND a.attnum > 0 AND NOT a.attisdropped
                     AND lower(replace(a.attname, '_', '')) IN ('businessunitid', 'buid')) > 1
            ORDER BY qualified;
            """, connection, transaction);
        command.Parameters.AddWithValue("targets", qualified);
        command.Parameters.AddWithValue("role", PurgeRole);
        command.Parameters.AddWithValue("policy", PurgePolicyName);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
                unreachable.Add(reader.GetString(0));

        if (unreachable.Count == 0) return;

        throw new InvalidOperationException(
            $"Purge refused: {unreachable.Count} of {qualified.Length} target table(s) cannot be "
            + $"reached by {PurgeRole}, so a sweep would report success having destroyed nothing "
            + $"in them. {string.Join("; ", unreachable)}. A table added by a later migration is "
            + "the expected cause: it needs the same GRANT and the same "
            + $"{PurgePolicyName} policy 20260811154500_TenantPurgeExecutionRole creates from the "
            + "catalogue.");
    }

    /// <summary>Which identifier a table is scoped by.</summary>
    private enum TenantScope
    {
        /// <summary>Tenant-plane table, keyed by the tenant's primary BusinessUnit.</summary>
        BusinessUnit,

        /// <summary>Platform-schema table, keyed by the platform tenant id.</summary>
        Tenant
    }

    /// <param name="Predicate">A WHERE clause over alias <c>t</c>, parameterised on <c>@scope</c>.
    /// For a table with its own tenant column this is a comparison; for one reached through a
    /// parent it is a subquery chain.</param>
    /// <param name="OrphanProbe">
    /// For a table reached through a parent, a <c>count(*)</c> over rows whose parent no longer
    /// exists — null for a table with a tenant column of its own.
    ///
    /// <para>This exists because the obvious post-condition for such a table CANNOT FAIL. Asking
    /// "how many rows still hang off a parent belonging to this tenant" once that parent has been
    /// deleted answers zero whatever happened to the child, which is the original defect rebuilt
    /// inside its own verification. An orphan count is the same question asked in a way the
    /// destruction cannot make vacuous — and it is the exact signature the production defect left
    /// behind: 103 <c>EmailIngests</c> rows pointing at an <c>Email_Configurations</c> row that no
    /// longer existed, with replica mode suspending the foreign key that would have objected.</para>
    /// </param>
    private readonly record struct PurgeTarget(
        string Qualified, string Column, string Predicate, TenantScope Scope,
        string? OrphanProbe = null);

    /// <summary>
    /// Every table a purge must clear, from TWO authorities, because the two planes are different
    /// problems.
    ///
    /// <para>The TENANT plane is discovered from the live catalogue: every table carrying a
    /// business unit column, whatever its schema. Deriving it is right there — a hand-maintained
    /// list stops covering new tables the moment somebody adds one, and the failure is invisible
    /// because the tenant is reported destroyed while their rows are still present.</para>
    ///
    /// <para>The PLATFORM plane comes from <see cref="PlatformTenantDataMap"/>, because there the
    /// question is not "does this table carry a tenant" but "whose record is it", and no column
    /// name answers that. Deepest-first, so a child is deleted while the parent its subquery
    /// selects through still exists.</para>
    /// </summary>
    private static async Task<IReadOnlyList<PurgeTarget>> TargetsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await EnsureEveryTenantLinkedPlatformTableIsClassifiedAsync(connection, transaction, cancellationToken);
        await EnsureEveryTenantPlaneTableIsClassifiedAsync(connection, transaction, cancellationToken);

        var found = new List<PurgeTarget>();

        // Reached through a parent, and FIRST — see TenantPlaneDataMap for why this ordering is
        // load-bearing rather than tidy. Replica mode suspends ON DELETE CASCADE along with the
        // append-only guards, so a child selected by a subquery on its parent has to be deleted
        // while that parent still exists. Every one of these is deleted before the sweep below
        // reaches the tables they hang off.
        var parentColumns = await TenantPlaneParentColumnsAsync(connection, transaction, cancellationToken);
        foreach (var declared in TenantPlaneDataMap.Destroyed)
        {
            if (!await TableExistsAsync(
                    connection, transaction, TenantPlaneDataMap.Schema, declared.Table, cancellationToken))
                continue;

            var qualified = $"\"{TenantPlaneDataMap.Schema}\".\"{declared.Table}\"";
            found.Add(new PurgeTarget(
                qualified,
                string.Join(" + ", declared.ReachedThrough!.Select(p => p.ForeignKeyColumn).Distinct()),
                TenantPlanePredicate(declared, "t", parentColumns),
                TenantScope.BusinessUnit,
                OrphanProbe: TenantPlaneOrphanProbe(declared, "t")));
        }

        await using (var command = new NpgsqlCommand(
            $"""
            SELECT c.table_schema, c.table_name, c.column_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND {TenantColumnMatch("c.column_name")}
            ORDER BY c.table_schema, c.table_name, c.column_name;
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var column = reader.GetString(2);

                if (PreservedTables.Contains($"{schema}.{table}")) continue;

                // Identifiers come from the catalogue, so they cannot carry an injected quote; the
                // guard is here so that stays true if this ever reads from somewhere else.
                if (schema.Contains('"') || table.Contains('"') || column.Contains('"')) continue;

                found.Add(new PurgeTarget(
                    $"\"{schema}\".\"{table}\"", column, $"t.\"{column}\" = @scope",
                    TenantScope.BusinessUnit));
            }
        }

        foreach (var declared in PlatformTenantDataMap.Destroyed
                     .OrderByDescending(PlatformTenantDataMap.Depth)
                     .ThenBy(t => t.Table, StringComparer.Ordinal))
        {
            // A module whose migration has not landed yet is absent rather than fatal: the purge
            // must not refuse to destroy a customer's data because an unrelated feature is
            // half-deployed.
            if (!await TableExistsAsync(
                    connection, transaction, PlatformTenantDataMap.Schema, declared.Table, cancellationToken))
                continue;

            found.Add(new PurgeTarget(
                $"\"{PlatformTenantDataMap.Schema}\".\"{declared.Table}\"",
                declared.TenantColumn ?? declared.ReachedThrough!.ForeignKeyColumn,
                TenantPredicate(declared, "t", 0),
                TenantScope.Tenant));
        }

        return found;
    }

    /// <summary>
    /// Builds the WHERE clause that selects one tenant's rows: a direct comparison, or a chain of
    /// subqueries up to whichever ancestor carries the tenant column.
    /// </summary>
    private static string TenantPredicate(PlatformTenantTable table, string alias, int depth)
    {
        if (table.TenantColumn is string column)
            return $"{alias}.\"{column}\" = @scope";

        var parent = table.ReachedThrough!;
        var parentTable = PlatformTenantDataMap.Find(parent.ParentTable)
            ?? throw new InvalidOperationException(
                $"platform.\"{table.Table}\" is declared as reached through "
                + $"platform.\"{parent.ParentTable}\", which is not itself classified. The chain "
                + "must end at a table carrying a tenant column.");

        // Distinct alias per level so a three-deep chain cannot shadow its own ancestor.
        var parentAlias = $"p{depth}";
        return $"{alias}.\"{parent.ForeignKeyColumn}\" IN ("
               + $"SELECT {parentAlias}.\"{parent.ParentKeyColumn}\" "
               + $"FROM \"{PlatformTenantDataMap.Schema}\".\"{parent.ParentTable}\" {parentAlias} "
               + $"WHERE {TenantPredicate(parentTable, parentAlias, depth + 1)})";
    }

    /// <summary>
    /// Refuses to run if the platform schema holds a table with a tenant column that nobody has
    /// classified.
    ///
    /// <para>Fail closed, and deliberately harsher than the tenant plane. There, an unknown table
    /// is swept because everything with a business unit is the customer's. Here the two possible
    /// answers are opposite and equally damaging: destroying the operator's evidence, or leaving
    /// the customer's data behind. A guess is not available, so the purge stops and names the
    /// table. Classifying it is one entry in <see cref="PlatformTenantDataMap"/>, and
    /// <c>TenantLifecyclePlatformTableClassificationTests</c> is meant to catch it long before
    /// anybody reaches production.</para>
    ///
    /// <para>This runtime check covers the DIRECT case only — a table carrying <c>TenantId</c>.
    /// Reachability through a foreign-key chain is a graph walk, which is the test's job rather
    /// than something worth running on every purge.</para>
    /// </summary>
    private static async Task EnsureEveryTenantLinkedPlatformTableIsClassifiedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var unclassified = new List<string>();

        await using var command = new NpgsqlCommand(
            """
            SELECT c.table_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'platform'
              AND lower(c.column_name) = 'tenantid'
            ORDER BY c.table_name;
            """, connection, transaction);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (PlatformTenantDataMap.Find(table) is null) unclassified.Add(table);
            }

        if (unclassified.Count == 0) return;

        throw new InvalidOperationException(
            $"Purge refused: {string.Join(", ", unclassified.Select(t => $"platform.\"{t}\""))} "
            + "carr" + (unclassified.Count == 1 ? "ies" : "y") + " a tenant column but "
            + (unclassified.Count == 1 ? "is" : "are") + " not classified in PlatformTenantDataMap. "
            + "Whether a platform table is the CUSTOMER's record (destroy) or the OPERATOR's record "
            + "of them (preserve) cannot be inferred from a column name, and both wrong answers "
            + "are damaging: one erases the evidence of how a customer was treated, the other "
            + "leaves their data behind after they were told it was gone.");
    }

    /// <summary>
    /// Refuses to run if the tenant plane holds a table that carries no business unit column and
    /// that nobody has classified.
    ///
    /// <para>The catalogue answers "does this table carry a business unit" and that is enough for
    /// the 205 tables that do. It is silent about the fifteen that do not, and fourteen of those
    /// turned out to be the customer's data reached through a parent — every RFQ line, every
    /// quote line, every lead line, every inbound message. They were not skipped by a decision;
    /// they were skipped because nothing ever asked. This is the question.</para>
    ///
    /// <para>Fail closed, for the same reason the platform plane fails closed: a table with no
    /// tenant column offers nothing to infer from, and both wrong answers are damaging. Deleting
    /// shared reference data breaks every other tenant; skipping the customer's rows is the defect
    /// this class exists to end.</para>
    /// </summary>
    private static async Task EnsureEveryTenantPlaneTableIsClassifiedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var unclassified = new List<string>();

        await using var command = new NpgsqlCommand(
            $"""
            SELECT t.table_name
            FROM information_schema.tables t
            WHERE t.table_schema = @schema
              AND t.table_type = 'BASE TABLE'
              AND NOT EXISTS (
                  SELECT 1 FROM information_schema.columns c
                  WHERE c.table_schema = t.table_schema
                    AND c.table_name = t.table_name
                    AND {TenantColumnMatch("c.column_name")})
            ORDER BY t.table_name;
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", TenantPlaneDataMap.Schema);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken))
            {
                var table = reader.GetString(0);
                if (TenantPlaneDataMap.Find(table) is null) unclassified.Add(table);
            }

        if (unclassified.Count == 0) return;

        throw new InvalidOperationException(
            $"Purge refused: {string.Join(", ", unclassified.Select(t => $"public.\"{t}\""))} "
            + "carr" + (unclassified.Count == 1 ? "ies" : "y") + " no business unit column and "
            + (unclassified.Count == 1 ? "is" : "are") + " not classified in TenantPlaneDataMap, "
            + "so the sweep cannot see " + (unclassified.Count == 1 ? "it" : "them") + " and has no "
            + "way to tell whether " + (unclassified.Count == 1 ? "it holds" : "they hold") + " the "
            + "customer's rows. Declare the parent that scopes it, or declare why it is not tenant "
            + "data. A purge must not report success over a table nobody has looked at.");
    }

    /// <summary>
    /// Every table that MUST be empty of this tenant when the purge commits, derived from the
    /// schema and NOT from the list the sweep happened to build.
    ///
    /// <para><b>This is the answer to defect P0-1's second half, and it matters more than the
    /// first.</b> The post-condition used to re-count the tables the sweep had just visited, which
    /// means it could only ever confirm that the sweep did what the sweep did. Eleven snake_case
    /// tables were absent from the sweep list, so they were absent from the check, so the check
    /// passed — the verification was structurally incapable of catching the omission it existed to
    /// catch. A verification that can only see what the actor already touched is not a
    /// verification.</para>
    ///
    /// <para>So this reads THREE independent authorities and unions them:</para>
    /// <list type="number">
    /// <item>the catalogue, for any column that names a business unit under any spelling;</item>
    /// <item><c>pg_policy</c>, for every table the schema itself declares tenant-scoped by giving
    /// it a <c>nexora_tenant_isolation</c> policy — the same declaration the request path has
    /// always honoured, and the one that already knew <c>EmailIngests</c> belonged to a
    /// tenant;</item>
    /// <item><see cref="TenantPlaneDataMap"/>, for the polymorphic links no catalogue can
    /// derive.</item>
    /// </list>
    ///
    /// <para>A table that any of the three calls tenant-scoped must be swept and must be empty.
    /// Adding a tenant table and forgetting the sweep now stops the purge by name instead of
    /// producing a success report with the customer's rows still in place.</para>
    /// </summary>
    private static async Task<IReadOnlyList<PurgeTarget>> TenantScopedTablesAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var found = new List<PurgeTarget>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await using (var command = new NpgsqlCommand(
            $"""
            SELECT n.nspname, c.relname, a.attname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
            WHERE c.relkind = 'r'
              AND n.nspname NOT IN ('pg_catalog', 'information_schema')
              AND {TenantColumnMatch("a.attname")}
            ORDER BY n.nspname, c.relname, a.attname;
            """, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                var column = reader.GetString(2);
                if (PreservedTables.Contains($"{schema}.{table}")) continue;
                if (schema.Contains('"') || table.Contains('"') || column.Contains('"')) continue;
                var qualified = $"\"{schema}\".\"{table}\"";
                if (!seen.Add(qualified)) continue;
                found.Add(new PurgeTarget(
                    qualified, column, $"t.\"{column}\" = @scope", TenantScope.BusinessUnit));
            }
        }

        var parentColumns = await TenantPlaneParentColumnsAsync(connection, transaction, cancellationToken);
        foreach (var declared in TenantPlaneDataMap.Destroyed)
        {
            var qualified = $"\"{TenantPlaneDataMap.Schema}\".\"{declared.Table}\"";
            if (PreservedTables.Contains($"{TenantPlaneDataMap.Schema}.{declared.Table}")) continue;
            if (!seen.Add(qualified)) continue;
            if (!await TableExistsAsync(
                    connection, transaction, TenantPlaneDataMap.Schema, declared.Table, cancellationToken))
                continue;

            found.Add(new PurgeTarget(
                qualified,
                string.Join(" + ", declared.ReachedThrough!.Select(p => p.ForeignKeyColumn).Distinct()),
                TenantPlanePredicate(declared, "t", parentColumns),
                TenantScope.BusinessUnit,
                OrphanProbe: TenantPlaneOrphanProbe(declared, "t")));
        }

        // The schema's own declaration, read last so it can only ADD. Anything it names that the
        // two authorities above did not produce is a tenant table nobody has taught the purge
        // about, and there is no predicate to count it with — so it is reported as unclassified
        // rather than silently dropped, which is precisely how the eleven snake_case tables and
        // the fourteen indirect ones went missing in the first place.
        var undeclared = new List<string>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname
            FROM pg_policy p
            JOIN pg_class c ON c.oid = p.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE p.polname = @policy
            ORDER BY n.nspname, c.relname;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue("policy", TenantIsolationPolicyName);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var schema = reader.GetString(0);
                var table = reader.GetString(1);
                if (PreservedTables.Contains($"{schema}.{table}")) continue;
                if (seen.Contains($"\"{schema}\".\"{table}\"")) continue;

                // Already classified, just not by a rule that produces a sweep predicate.
                // public."BusinessUnits" is the whole of this case: its tenant column IS its
                // primary key, so it is destroyed last and by key rather than through a target.
                if (schema == TenantPlaneDataMap.Schema && TenantPlaneDataMap.Find(table) is not null) continue;
                if (schema == PlatformTenantDataMap.Schema && PlatformTenantDataMap.Find(table) is not null) continue;

                undeclared.Add($"{schema}.{table}");
            }
        }

        if (undeclared.Count > 0)
            throw new InvalidOperationException(
                $"Purge refused: {string.Join(", ", undeclared)} carr"
                + (undeclared.Count == 1 ? "ies" : "y") + $" a {TenantIsolationPolicyName} policy, so "
                + "the database itself says " + (undeclared.Count == 1 ? "it holds" : "they hold")
                + " one tenant's rows — but the purge has no way to select them. Declare the parent "
                + "that scopes " + (undeclared.Count == 1 ? "it" : "them")
                + " in TenantPlaneDataMap. Until then a purge would report success with the "
                + "customer's rows still in place, which is the one outcome this path must never "
                + "produce.");

        foreach (var declared in PlatformTenantDataMap.Destroyed
                     .OrderByDescending(PlatformTenantDataMap.Depth)
                     .ThenBy(t => t.Table, StringComparer.Ordinal))
        {
            if (!await TableExistsAsync(
                    connection, transaction, PlatformTenantDataMap.Schema, declared.Table, cancellationToken))
                continue;

            var qualified = $"\"{PlatformTenantDataMap.Schema}\".\"{declared.Table}\"";
            if (!seen.Add(qualified)) continue;

            found.Add(new PurgeTarget(
                qualified,
                declared.TenantColumn ?? declared.ReachedThrough!.ForeignKeyColumn,
                TenantPredicate(declared, "t", 0),
                TenantScope.Tenant,
                OrphanProbe: declared.IsIndirect ? PlatformOrphanProbe(declared, "t") : null));
        }

        return found;
    }

    /// <summary>A <c>count(*)</c> of platform rows whose parent no longer exists. Same reasoning as
    /// <see cref="TenantPlaneOrphanProbe"/>: after the parent is gone the subquery predicate is
    /// vacuous and only an orphan count still means anything.</summary>
    private static string PlatformOrphanProbe(PlatformTenantTable table, string alias)
    {
        var parent = table.ReachedThrough!;
        return $"NOT EXISTS (SELECT 1 FROM \"{PlatformTenantDataMap.Schema}\".\"{parent.ParentTable}\" o "
               + $"WHERE o.\"{parent.ParentKeyColumn}\" = {alias}.\"{parent.ForeignKeyColumn}\")";
    }

    /// <summary>
    /// The post-condition, taken against a set of tables the sweep did not choose.
    ///
    /// <para>Two failures are reported and they are different failures. A table the schema calls
    /// tenant-scoped that the sweep never visited is a COVERAGE gap — the purge did not even try,
    /// and no count would have revealed it. A table that was visited and still holds rows is a
    /// RESIDUE failure. Both abort the transaction, because a purge that cannot prove the rows are
    /// gone must not report that they are.</para>
    /// </summary>
    private static async Task AssertNoTenantRowsRemainAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<PurgeTarget> mustBeEmpty, IReadOnlySet<string> swept,
        IReadOnlyDictionary<string, long> orphanBaseline,
        long businessUnitId, long tenantId, CancellationToken cancellationToken)
    {
        var failures = new List<string>();

        foreach (var target in mustBeEmpty)
        {
            if (!swept.Contains(target.Qualified))
            {
                failures.Add(
                    $"{target.Qualified} is tenant-scoped by \"{target.Column}\" and the sweep "
                    + "never visited it, so whether it still holds this tenant's rows was never "
                    + "established");
                continue;
            }

            if (target.OrphanProbe is string probe)
            {
                // An indirect table is checked for ORPHANS APPEARING, not for tenant rows. Two
                // reasons, and both are the same trap seen from different sides. Its parent has
                // just been deleted, so the tenant predicate would answer zero however many of the
                // child's rows survived. And a bare orphan count would fire on an orphan that was
                // already there — public."Attachments" carries no foreign key at all, so one is
                // possible — refusing a purge for somebody else's inconsistency. Comparing against
                // a baseline taken before the sweep asks only "did THIS purge strand rows", which
                // is the question, and it is the exact signature the production defect left: 103
                // EmailIngests rows pointing at an Email_Configurations row that no longer existed.
                //
                // Under row-level security this count can still be filtered to nothing, because the
                // purge policy on such a table is itself parent-derived. That is why the sweep's
                // in-loop check — taken while the parent is still alive, and therefore not vacuous
                // — is the load-bearing residue proof for these tables, and this is corroboration.
                var stranded = await ScalarNoScopeAsync(
                    connection, transaction,
                    $"""SELECT count(*)::bigint FROM {target.Qualified} t WHERE {probe};""",
                    cancellationToken);
                var before = orphanBaseline.TryGetValue(target.Qualified, out var baseline) ? baseline : 0;
                if (stranded > before)
                    failures.Add(
                        $"{target.Qualified} gained {stranded - before} row(s) orphaned against a "
                        + $"parent this purge deleted (reached by \"{target.Column}\")");
                continue;
            }

            var remaining = await ScalarAsync(
                connection, transaction,
                $"""SELECT count(*)::bigint FROM {target.Qualified} t WHERE {target.Predicate};""",
                target.Scope == TenantScope.Tenant ? tenantId : businessUnitId,
                cancellationToken);

            if (remaining > 0)
                failures.Add($"{target.Qualified} still holds {remaining} row(s) matching \"{target.Column}\"");
        }

        if (failures.Count == 0) return;

        throw new InvalidOperationException(
            $"Purge ABORTED for business unit {businessUnitId}: the destructive transaction ran to "
            + $"completion but an independent check of {mustBeEmpty.Count} tenant-scoped table(s) — "
            + "derived from the schema rather than from the sweep's own list — found "
            + $"{failures.Count} still accounted for. Nothing has been committed. "
            + $"{string.Join("; ", failures)}. A purge that cannot prove the rows are gone must not "
            + "report that they are.");
    }

    /// <summary>
    /// Orphan counts taken BEFORE anything is deleted, so the post-condition can tell rows this
    /// purge stranded from rows that were already stranded when it started.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, long>> OrphanBaselineAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction,
        IReadOnlyList<PurgeTarget> targets, CancellationToken cancellationToken)
    {
        var baseline = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var target in targets)
        {
            if (target.OrphanProbe is not string probe) continue;
            baseline[target.Qualified] = await ScalarNoScopeAsync(
                connection, transaction,
                $"""SELECT count(*)::bigint FROM {target.Qualified} t WHERE {probe};""",
                cancellationToken);
        }

        return baseline;
    }

    /// <summary>The row-level-security policy the request path is scoped by. Read here as an
    /// INDEPENDENT declaration of which tables hold one tenant's rows.</summary>
    private const string TenantIsolationPolicyName = "nexora_tenant_isolation";

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string schema, string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualified) IS NOT NULL;", connection, transaction);
        command.Parameters.AddWithValue("qualified", $"{schema}.\"{table}\"");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    /// <summary>
    /// The SQL fragment that recognises a business unit column, whatever the naming convention of
    /// the migration that created it.
    ///
    /// <para><b>This is the whole of defect P0-1.</b> The predicate used to be
    /// <c>lower(name) IN ('businessunitid', 'buid')</c>, and this schema contains both
    /// <c>"BusinessUnitId"</c> and <c>business_unit_id</c> because the evidence and extraction
    /// tables were written snake_case. Eleven tables — <c>source_documents</c>,
    /// <c>canonical_inquiries</c>, <c>field_evidence</c> and eight more — matched neither spelling
    /// and were never targets of the sweep, never counted in the preview, and never named in the
    /// report. Stripping underscores before comparing makes the rule about the IDENTIFIER rather
    /// than about one team's casing habit.</para>
    /// </summary>
    private static string TenantColumnMatch(string columnExpression) =>
        $"lower(replace({columnExpression}, '_', '')) IN ('businessunitid', 'buid')";

    /// <summary>
    /// The WHERE clause selecting one tenant's rows in a table that has no business unit column,
    /// as the OR of one subquery per declared parent kind.
    /// </summary>
    private static string TenantPlanePredicate(
        TenantPlaneTable table, string alias, IReadOnlyDictionary<string, string> parentColumns)
        => string.Join(" OR ", table.ReachedThrough!.Select((parent, index) =>
            $"({ParentMatch(parent, alias, index, parentColumns)})"));

    /// <summary>
    /// A <c>count(*)</c> of rows in an indirect table whose parent is gone, across every declared
    /// parent kind. See <see cref="PurgeTarget.OrphanProbe"/> for why the post-condition needs
    /// this and cannot use the delete predicate.
    /// </summary>
    private static string TenantPlaneOrphanProbe(TenantPlaneTable table, string alias)
    {
        var arms = table.ReachedThrough!.Select((parent, index) =>
        {
            var parentAlias = $"o{index}";
            var discriminator = parent.DiscriminatorColumn is null
                ? string.Empty
                : $"{alias}.\"{parent.DiscriminatorColumn}\" = '{Literal(parent.DiscriminatorValue!)}' AND ";
            return $"({discriminator}NOT EXISTS ("
                   + $"SELECT 1 FROM \"{TenantPlaneDataMap.Schema}\".\"{parent.ParentTable}\" {parentAlias} "
                   + $"WHERE {parentAlias}.\"{parent.ParentKeyColumn}\" = {alias}.\"{parent.ForeignKeyColumn}\"))";
        });

        return string.Join(" OR ", arms);
    }

    private static string ParentMatch(
        TenantPlaneParent parent, string alias, int index,
        IReadOnlyDictionary<string, string> parentColumns)
    {
        var parentAlias = $"q{index}";
        var discriminator = parent.DiscriminatorColumn is null
            ? string.Empty
            : $"{alias}.\"{parent.DiscriminatorColumn}\" = '{Literal(parent.DiscriminatorValue!)}' AND ";

        // A parent that is itself reached through a parent (custom_field_options ->
        // custom_field_versions -> custom_field_definitions) recurses; one that carries a business
        // unit column ends the chain with a comparison.
        string parentScope;
        if (TenantPlaneDataMap.Find(parent.ParentTable) is { ReachedThrough.Count: > 0 } declaredParent)
        {
            parentScope = TenantPlanePredicate(declaredParent, parentAlias, parentColumns);
        }
        else
        {
            // READ from the catalogue, never written out here. This schema spells the same
            // concept three ways — "BusinessUnitID" on Leads, "BUID" on Products, and
            // "BusinessUnitId" on material_lots — so a hardcoded name is a 42703 mid-sweep on a
            // transaction that has already deleted rows, and it is right for every portable test
            // while being wrong against the real database.
            if (!parentColumns.TryGetValue(parent.ParentTable, out var column))
                throw new InvalidOperationException(
                    $"public.\"{parent.ParentTable}\" is declared in TenantPlaneDataMap as the "
                    + $"parent that scopes another table, but it carries no business unit column, "
                    + "so the chain does not end at a tenant and the child's rows cannot be "
                    + "attributed. Classify the parent, or re-point the child at one that is "
                    + "scoped.");
            parentScope = $"{parentAlias}.\"{column}\" = @scope";
        }

        return $"{discriminator}{alias}.\"{parent.ForeignKeyColumn}\" IN ("
               + $"SELECT {parentAlias}.\"{parent.ParentKeyColumn}\" "
               + $"FROM \"{TenantPlaneDataMap.Schema}\".\"{parent.ParentTable}\" {parentAlias} "
               + $"WHERE {parentScope})";
    }

    /// <summary>
    /// The business unit column of every table <see cref="TenantPlaneDataMap"/> names as a parent,
    /// read from the live catalogue for the reason the class comment gives at length: the mapped
    /// column and the property name diverge across this schema, and three different spellings are
    /// in use.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, string>> TenantPlaneParentColumnsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var parents = TenantPlaneDataMap.Tables
            .SelectMany(t => t.ReachedThrough ?? [])
            .Select(p => p.ParentTable)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parents.Length == 0) return columns;

        await using var command = new NpgsqlCommand(
            $"""
            SELECT c.table_name, c.column_name
            FROM information_schema.columns c
            WHERE c.table_schema = @schema
              AND c.table_name = ANY (@parents)
              AND {TenantColumnMatch("c.column_name")}
            ORDER BY c.table_name, c.column_name;
            """, connection, transaction);
        command.Parameters.AddWithValue("schema", TenantPlaneDataMap.Schema);
        command.Parameters.AddWithValue("parents", parents);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            columns.TryAdd(reader.GetString(0), reader.GetString(1));

        return columns;
    }

    /// <summary>Doubles single quotes in a declared discriminator value. The values are compile-time
    /// constants today; the escape is here so that stays safe if one ever is not.</summary>
    private static string Literal(string value) => value.Replace("'", "''");

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, PurgeTarget target,
        long businessUnitId, long tenantId, CancellationToken cancellationToken)
        => await ScalarAsync(
            connection, transaction,
            $"""SELECT count(*)::bigint FROM {target.Qualified} t WHERE {target.Predicate};""",
            target.Scope == TenantScope.Tenant ? tenantId : businessUnitId,
            cancellationToken);

    /// <summary>A count whose predicate names no tenant — the orphan probes. Separate from
    /// <see cref="ScalarAsync"/> rather than passing an unused parameter, so a probe that
    /// accidentally DID reference <c>@scope</c> fails loudly instead of binding silently.</summary>
    private static async Task<long> ScalarNoScopeAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"Could not count rows stranded by the purge: {exception.SqlState} {exception.MessageText}",
                exception);
        }
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, long scope,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("scope", scope);
        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"Could not count tenant rows for the purge preview: {exception.SqlState} {exception.MessageText}",
                exception);
        }
    }

    private async Task<NpgsqlConnection> OpenOwnerConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new NpgsqlConnection(OwnerConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// The OWNER connection. <c>session_replication_role</c> is a superuser-restricted setting, so
    /// the request-path roles cannot suspend the append-only guards — which is the correct
    /// arrangement: a purge is not something the tenant plane should be able to perform even if
    /// every application check above it were bypassed.
    ///
    /// <para><b>Opening it is not the same as deleting on it, and conflating the two was the
    /// defect.</b> The connection is opened as the owner because only the owner can enter replica
    /// mode; the statements that touch a customer's rows then run under
    /// <see cref="PurgeRole"/>, which the policies name and which is scoped to one tenant. The
    /// owner's own reach over its tables is deliberately no longer what this class depends on —
    /// on the 100 tables declared FORCE that reach is nil, and it was nil silently.</para>
    /// </summary>
    private string OwnerConnectionString() =>
        configuration.GetConnectionString("MigrationConnection")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? throw TenantOffboardingRefusedException.NotSupported(
            "No database connection is configured, so a purge cannot run here.");
}
