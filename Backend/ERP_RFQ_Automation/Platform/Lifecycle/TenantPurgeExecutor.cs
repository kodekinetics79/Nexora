using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

public sealed record TenantPurgeTableCount(string Table, string TenantColumn, long Rows);

public sealed record TenantPurgePreview(
    long TenantId,
    long BusinessUnitId,
    IReadOnlyList<TenantPurgeTableCount> Tables,
    long TotalRows,
    IReadOnlyList<string> Preserved);

public sealed record TenantPurgeOutcome(
    long TenantId,
    long BusinessUnitId,
    long RowsDeleted,
    IReadOnlyList<TenantPurgeTableCount> Deleted);

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

    public async Task<TenantPurgePreview> PreviewAsync(
        long tenantId, long businessUnitId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenOwnerConnectionAsync(cancellationToken);

        var counts = new List<TenantPurgeTableCount>();
        foreach (var target in await TargetsAsync(connection, null, cancellationToken))
        {
            var rows = await CountAsync(connection, null, target, businessUnitId, tenantId, cancellationToken);
            if (rows > 0) counts.Add(new TenantPurgeTableCount(target.Qualified, target.Column, rows));
        }

        var keyColumn = await BusinessUnitKeyColumnAsync(connection, null, cancellationToken);
        var businessUnitRows = await ScalarAsync(
            connection, null,
            $"""SELECT count(*)::bigint FROM {BusinessUnitTable} WHERE "{keyColumn}" = @scope;""",
            businessUnitId, cancellationToken);
        if (businessUnitRows > 0)
            counts.Add(new TenantPurgeTableCount(BusinessUnitTable, keyColumn, businessUnitRows));

        return new TenantPurgePreview(
            tenantId, businessUnitId,
            counts.OrderByDescending(c => c.Rows).ToList(),
            counts.Sum(c => c.Rows),
            PreservedTables.OrderBy(x => x, StringComparer.Ordinal).ToList());
    }

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
        await using (var replica = new NpgsqlCommand(
            "SET LOCAL session_replication_role = 'replica';", connection, transaction))
            await replica.ExecuteNonQueryAsync(cancellationToken);

        var deleted = new List<TenantPurgeTableCount>();
        foreach (var target in await TargetsAsync(connection, transaction, cancellationToken))
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
        }

        // Last, and by primary key: the business unit is the anchor every sweep above resolved
        // against, and it is the one tenant row whose tenant column is its own id.
        var keyColumn = await BusinessUnitKeyColumnAsync(connection, transaction, cancellationToken);
        await using (var unit = new NpgsqlCommand(
            $"""DELETE FROM {BusinessUnitTable} WHERE "{keyColumn}" = @scope;""", connection, transaction))
        {
            unit.Parameters.AddWithValue("scope", businessUnitId);
            var rows = await unit.ExecuteNonQueryAsync(cancellationToken);
            if (rows > 0) deleted.Add(new TenantPurgeTableCount(BusinessUnitTable, keyColumn, rows));
        }

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
            + "{Rows} row(s) destroyed across {Tables} table(s).",
            tenantId, businessUnitId, total, deleted.Count);

        return new TenantPurgeOutcome(
            tenantId, businessUnitId, total, deleted.OrderByDescending(d => d.Rows).ToList());
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
    private readonly record struct PurgeTarget(
        string Qualified, string Column, string Predicate, TenantScope Scope);

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

        var found = new List<PurgeTarget>();

        await using (var command = new NpgsqlCommand(
            """
            SELECT c.table_schema, c.table_name, c.column_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND lower(c.column_name) IN ('businessunitid', 'buid')
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
            if (!await TableExistsAsync(connection, transaction, declared.Table, cancellationToken))
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

    private static async Task<bool> TableExistsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, string table,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualified) IS NOT NULL;", connection, transaction);
        command.Parameters.AddWithValue("qualified", $"{PlatformTenantDataMap.Schema}.\"{table}\"");
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, NpgsqlTransaction? transaction, PurgeTarget target,
        long businessUnitId, long tenantId, CancellationToken cancellationToken)
        => await ScalarAsync(
            connection, transaction,
            $"""SELECT count(*)::bigint FROM {target.Qualified} t WHERE {target.Predicate};""",
            target.Scope == TenantScope.Tenant ? tenantId : businessUnitId,
            cancellationToken);

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
    /// </summary>
    private string OwnerConnectionString() =>
        configuration.GetConnectionString("MigrationConnection")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? throw TenantOffboardingRefusedException.NotSupported(
            "No database connection is configured, so a purge cannot run here.");
}
