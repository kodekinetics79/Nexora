using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Platform.Testing;

public sealed record TenantResetTableCount(string Table, string TenantColumn, long Rows);

public sealed record TenantResetPreview(
    long BusinessUnitId,
    string BusinessUnitName,
    IReadOnlyList<TenantResetTableCount> Tables,
    long TotalRows,
    IReadOnlyList<string> Preserved,
    string ConfirmationRequired);

public sealed record TenantResetOutcome(
    long BusinessUnitId,
    long RowsDeleted,
    IReadOnlyList<TenantResetTableCount> Deleted,
    bool MailboxPollStateCleared,
    string Summary);

/// <summary>Thrown when a reset is attempted somewhere it must never run.</summary>
public sealed class TenantResetRefusedException(string message) : Exception(message);

/// <summary>
/// Erases a tenant's TRANSACTIONAL data — leads, RFQs, quotes, ingested mail and the evidence
/// behind them — while leaving its CONFIGURATION intact, so the same source emails can be
/// ingested again from a clean slate during testing.
///
/// <para><b>Why re-ingestion needs more than deleting leads.</b> Deleting the leads alone changes
/// nothing observable: the poller will report "no new mail" forever. Three separate mechanisms
/// remember that a message was already handled — <c>EmailIngests</c> (per-message dedupe),
/// <c>LeadIngestionOccurrences</c> (idempotency keys, which make <c>ReconcileAsync</c> converge on
/// the existing lead rather than create one), and <c>Email_Configurations.LastSuccessfulPollOn</c>
/// (the lookback window, which never rewinds on its own). A reset that misses any one of them
/// looks like it worked and then silently ingests nothing. All three are cleared together.</para>
///
/// <para><b>Triggers, not foreign keys, are the hard part.</b> Roughly thirty append-only guards
/// (<c>nexora_evidence_append_only</c> and friends) <c>RAISE EXCEPTION</c> on any DELETE,
/// unconditionally and by design — that immutability is what makes the evidence trail worth
/// having. They are suspended for the duration of one transaction via
/// <c>session_replication_role = replica</c>, which requires the OWNER connection; that is why
/// this runs on <c>ConnectionStrings:MigrationConnection</c> rather than the request-path role.
/// The same setting suspends foreign-key triggers, which is what makes deletion order irrelevant.</para>
///
/// <para><b>Which tables are cleared is derived from the model, not from a list.</b> Every entity
/// carrying a tenant column is cleared unless it is named in <see cref="PreservedEntities"/>. A
/// hand-maintained delete list silently stops covering new tables the moment someone adds one,
/// producing a "clean slate" that quietly is not — the worst possible failure for a reset whose
/// entire purpose is a known starting state.</para>
/// </summary>
public sealed class TenantDataReset(
    ErpRfqAutomationContext context,
    IConfiguration configuration,
    IHostEnvironment environment,
    ILogger<TenantDataReset> logger)
{
    /// <summary>Must be explicitly true. Absent or false refuses — this capability is opt-in.</summary>
    public const string EnabledConfigurationPath = "Testing:AllowTenantDataReset";

    /// <summary>
    /// Configuration and master data that must SURVIVE a reset. Without these the tenant would be
    /// unusable afterwards — no way to log in, no roles, no mailbox to re-ingest from — which is
    /// the opposite of a repeatable test fixture.
    /// </summary>
    public static readonly IReadOnlySet<string> PreservedEntities = new HashSet<string>(StringComparer.Ordinal)
    {
        // Identity and access. Losing these locks every user out of the tenant.
        nameof(User),
        nameof(RolePermission),
        // Setup_Master is roles AND lifecycle statuses AND currencies AND units of measure. Losing
        // it breaks lead lifecycle transitions, not merely a settings screen.
        nameof(SetupMaster),
        nameof(BusinessUnit),

        // The mailboxes being tested. Cleared of poll state below, never deleted — deleting them
        // would mean re-entering credentials before every test run.
        nameof(EmailConfiguration),

        // Master data a commercial journey resolves against. Wiping it would make every re-run
        // start by rebuilding the product catalogue and customer list.
        nameof(Product),
        nameof(Customer),
        nameof(Supplier),
        nameof(Warehouse),

        // Per-tenant policy.
        nameof(LeadReferenceConfiguration),
    };

    private const string TenantColumnA = "BusinessUnitId";
    private const string TenantColumnB = "Buid";

    public bool IsEnabled =>
        !environment.IsProduction() && configuration.GetValue<bool>(EnabledConfigurationPath);

    /// <summary>
    /// Fails closed, twice over. Production is refused whatever the configuration says, because a
    /// mistyped environment variable must not be sufficient to erase a customer's data; and
    /// outside Production it still requires an explicit opt-in.
    /// </summary>
    private void EnsurePermitted()
    {
        if (environment.IsProduction())
            throw new TenantResetRefusedException(
                "Tenant data reset is refused in Production. This capability exists for testing " +
                "and is not available against production data under any configuration.");

        if (!configuration.GetValue<bool>(EnabledConfigurationPath))
            throw new TenantResetRefusedException(
                $"Tenant data reset is disabled. Set {EnabledConfigurationPath}=true to enable it " +
                "in this environment.");
    }

    /// <summary>
    /// Table names the preserved entities map to. Resolved through EF because the keep-list is
    /// expressed in entity terms, which is how a reader reasons about it ("keep Users"), while the
    /// deletion works in table terms.
    /// </summary>
    private IReadOnlySet<string> PreservedTables()
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (!PreservedEntities.Contains(entity.ClrType.Name)) continue;
            var table = entity.GetTableName();
            if (!string.IsNullOrEmpty(table)) tables.Add(table);
        }
        return tables;
    }

    /// <summary>
    /// Every table carrying a tenant column, discovered from the live schema rather than the EF
    /// model.
    ///
    /// <para><b>Why information_schema and not EF metadata.</b> EF's CLR property name and its
    /// mapped column name diverge across this schema (BusinessUnitId vs "BusinessUnitID",
    /// Buid vs "BUID"), so building SQL from property names produces 42703 on exactly the tables
    /// that matter most. The database already knows the truth. Reading it from there also covers
    /// tables EF does not model at all — several evidence and outbox tables are managed purely by
    /// migrations — which a model-driven sweep would silently skip, leaving a "clean slate" that
    /// is not one.</para>
    /// </summary>
    private async Task<IReadOnlyList<(string Qualified, string Column)>> ResettableTablesAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var preserved = PreservedTables();
        var found = new List<(string, string)>();

        await using var command = new NpgsqlCommand(
            """
            SELECT c.table_schema, c.table_name, c.column_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND lower(c.column_name) IN ('businessunitid', 'buid')
            ORDER BY c.table_schema, c.table_name;
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            var column = reader.GetString(2);
            if (preserved.Contains(table)) continue;
            // Identifiers come from the catalogue, so they cannot contain an injected quote; the
            // guard is here so that stays true if this ever reads from somewhere else.
            if (schema.Contains('"') || table.Contains('"') || column.Contains('"')) continue;
            found.Add(($"\"{schema}\".\"{table}\"", column));
        }

        return found;
    }

    public async Task<TenantResetPreview> PreviewAsync(long businessUnitId, CancellationToken cancellationToken)
    {
        EnsurePermitted();

        var name = await context.BusinessUnits.AsNoTracking()
            .Where(b => b.Id == businessUnitId).Select(b => b.BusinessUnitName)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new KeyNotFoundException("Business unit not found.");

        await using var connection = new NpgsqlConnection(OwnerConnectionString());
        await connection.OpenAsync(cancellationToken);

        var counts = new List<TenantResetTableCount>();
        foreach (var (table, column) in await ResettableTablesAsync(connection, cancellationToken))
        {
            var rows = await CountAsync(connection, table, column, businessUnitId, cancellationToken);
            if (rows > 0) counts.Add(new TenantResetTableCount(table, column, rows));
        }

        return new TenantResetPreview(
            businessUnitId, name,
            counts.OrderByDescending(c => c.Rows).ToList(),
            counts.Sum(c => c.Rows),
            PreservedEntities.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            // The business unit's own name. A generated token could be echoed back by a script
            // without anyone reading it; typing the tenant's name requires knowing which tenant
            // is about to be erased.
            name);
    }

    public async Task<TenantResetOutcome> ExecuteAsync(
        long businessUnitId, string confirmation, CancellationToken cancellationToken)
    {
        EnsurePermitted();

        var preview = await PreviewAsync(businessUnitId, cancellationToken);
        if (!string.Equals(confirmation?.Trim(), preview.BusinessUnitName, StringComparison.Ordinal))
            throw new TenantResetRefusedException(
                $"Confirmation did not match. Type the business unit's name exactly — " +
                $"'{preview.BusinessUnitName}' — to confirm that its transactional data will be erased.");

        logger.LogWarning(
            "TENANT DATA RESET starting for business unit {Tenant} ('{Name}'): {Rows} row(s) across {Tables} table(s).",
            businessUnitId, preview.BusinessUnitName, preview.TotalRows, preview.Tables.Count);

        await using var connection = new NpgsqlConnection(OwnerConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // Suspends the append-only guards AND foreign-key triggers for this session only. Scoped
        // to the transaction, so a failure anywhere rolls back to the original state with the
        // guards intact — a half-reset tenant is harder to reason about than an un-reset one.
        await ExecuteAsync(connection, transaction, "SET LOCAL session_replication_role = 'replica';", cancellationToken);

        await AssertSweepIsNotSilentlyRefusedAsync(connection, transaction, cancellationToken);

        var deleted = new List<TenantResetTableCount>();
        foreach (var (table, column) in await ResettableTablesAsync(connection, cancellationToken))
        {
            await using var command = new NpgsqlCommand(
                $"""DELETE FROM {table} WHERE "{column}" = @tenant;""", connection, transaction);
            command.Parameters.AddWithValue("tenant", businessUnitId);
            int rows;
            try
            {
                rows = await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (PostgresException exception)
            {
                // Naming the table matters: a bare 42703 mid-sweep gives no way to tell which of
                // ~200 tables drifted, and the transaction has already rolled back by then.
                throw new InvalidOperationException(
                    $"Reset failed deleting from {table} using column \"{column}\": {exception.MessageText}",
                    exception);
            }
            if (rows > 0) deleted.Add(new TenantResetTableCount(table, column, rows));
        }

        // Without this the poller keeps its lookback window and reports "no new mail" forever, so
        // the reset would appear to work and then ingest nothing. Cleared in the same transaction
        // as the deletes: rewinding the window while the old ingest rows survive would re-read
        // every message and immediately discard it as a duplicate.
        // The tenant column is resolved, not assumed: this table spells it "BusinessUnitID" while
        // newer ones use "BusinessUnitId". Hardcoding either is a 42703 on half the schema.
        var mailboxTenantColumn = await ResolveTenantColumnAsync(
            connection, transaction, "Email_Configurations", cancellationToken);

        await using (var rewind = new NpgsqlCommand(
            $"""
             UPDATE public."Email_Configurations"
             SET "LastSuccessfulPollOn" = NULL, "LastPollAttemptOn" = NULL,
                 "LastPollError" = NULL, "ConsecutivePollFailures" = 0
             WHERE "{mailboxTenantColumn}" = @tenant;
             """, connection, transaction))
        {
            rewind.Parameters.AddWithValue("tenant", businessUnitId);
            await rewind.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var total = deleted.Sum(d => d.Rows);
        logger.LogWarning(
            "TENANT DATA RESET complete for business unit {Tenant}: {Rows} row(s) deleted, mailbox poll state cleared.",
            businessUnitId, total);

        return new TenantResetOutcome(
            businessUnitId, total, deleted.OrderByDescending(d => d.Rows).ToList(), true,
            $"Deleted {total:N0} row(s) across {deleted.Count} table(s). Mailbox poll history was " +
            "cleared, so the next poll re-reads the mailbox from the beginning and the same " +
            "messages will be ingested again as new leads.");
    }

    /// <summary>Reads a table's tenant column name from the live catalogue.</summary>
    private static async Task<string> ResolveTenantColumnAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string table, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT column_name FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table
              AND lower(column_name) IN ('businessunitid', 'buid')
            LIMIT 1;
            """, connection, transaction);
        command.Parameters.AddWithValue("table", table);

        return (string?)await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException($"No tenant column found on public.\"{table}\".");
    }

    /// <summary>
    /// Refuses to sweep at all when this connection's row-level-security posture means the DELETEs
    /// would be silently refused.
    ///
    /// <para><b>The defect this exists to make impossible.</b> The sweep runs on the OWNER
    /// connection — it has to, because <c>session_replication_role</c> is superuser-restricted and
    /// is what suspends the append-only guards. But the owner is NOT exempt from row-level
    /// security: ~100 of the tenant-plane tables are declared <c>FORCE ROW LEVEL SECURITY</c>,
    /// which subjects the owner to its own policies, and every one of those policies is written
    /// <c>TO nexora_tenant_app</c>. PostgreSQL matches a policy's role list with
    /// <c>has_privs_of_role()</c> and the runtime role is <c>NOINHERIT</c>, so no policy applies,
    /// the default is deny, and <c>DELETE</c> returns 0 WITHOUT RAISING. The caller records a table
    /// only when <c>rows &gt; 0</c>, so a silently-refused table is simply absent from the report:
    /// the operator is told "Deleted 8,412 row(s) across 41 table(s)" while the forced tables —
    /// including <c>EmailIngests</c> and <c>LeadIngestionOccurrences</c>, the two this class names
    /// as its whole point — still hold everything, and the next poll re-reads the mailbox and
    /// discards every message as a duplicate. Verbatim the outcome the class summary says it
    /// exists to prevent.</para>
    ///
    /// <para><b>Why this is a PRECONDITION and not a post-sweep row count.</b> A post-sweep count
    /// is the same defect rebuilt inside its own verification: the deny that swallowed the DELETE
    /// swallows the SELECT too, so the survivors count as zero and the check passes. The condition
    /// has to be read from the catalogue, which no policy hides, and read BEFORE any work.</para>
    ///
    /// <para><b>The repair, when someone takes it on:</b> the one <c>TenantPurgeExecutor</c> made
    /// after proving all of this against postgres:16 — delete under a dedicated NOBYPASSRLS role
    /// (<c>nexora_purge_app</c>) with its scope GUC set, so the database rather than a WHERE clause
    /// is what bounds the sweep. Until then, refusing is the only honest answer available.</para>
    /// </summary>
    private static async Task AssertSweepIsNotSilentlyRefusedAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT
                (SELECT count(*)::bigint
                 FROM pg_class c
                 JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'public' AND c.relkind = 'r' AND c.relforcerowsecurity
                   AND NOT EXISTS (
                       SELECT 1 FROM pg_policy p
                       WHERE p.polrelid = c.oid
                         AND (p.polroles = '{0}'::oid[]
                              OR EXISTS (SELECT 1 FROM unnest(p.polroles) role_oid
                                         WHERE pg_has_role(current_user, role_oid, 'USAGE')))))
                AS unreachable,
                (SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user) AS bypasses;
            """, connection, transaction);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return;

        var unreachable = reader.GetInt64(0);
        var bypasses = !reader.IsDBNull(1) && reader.GetBoolean(1);

        // BYPASSRLS makes every policy irrelevant, so the sweep is sound regardless.
        if (bypasses || unreachable == 0) return;

        throw new TenantResetRefusedException(
            $"Tenant data reset REFUSED before deleting anything: {unreachable} table(s) in public are "
            + "declared FORCE ROW LEVEL SECURITY with no policy this connection's role can satisfy, and "
            + "this connection does not hold BYPASSRLS. DELETE on those tables returns 0 without raising, "
            + "so the sweep would report a total it did not achieve and leave the tenant in an unknown "
            + "state — the exact failure this facility exists to prevent. Fix it the way "
            + "TenantPurgeExecutor did: run the deletes under a dedicated role with its tenant scope "
            + "GUC set, rather than on the bare owner connection.");
    }

    private string OwnerConnectionString() =>
        configuration.GetConnectionString("MigrationConnection")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? throw new InvalidOperationException("No database connection is configured.");

    private static async Task<long> CountAsync(
        NpgsqlConnection connection, string table, string column, long tenant, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""SELECT count(*)::bigint FROM {table} WHERE "{column}" = @tenant;""", connection);
        command.Parameters.AddWithValue("tenant", tenant);
        try
        {
            return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException exception)
        {
            // A bare 42703 names neither the table nor the column, which makes a schema drift
            // between the catalogue and the live table a guessing game.
            throw new InvalidOperationException(
                $"Could not count tenant rows in {table} using column \"{column}\": {exception.MessageText}",
                exception);
        }
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
