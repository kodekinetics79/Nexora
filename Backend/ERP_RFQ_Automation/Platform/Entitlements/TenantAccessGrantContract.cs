using Npgsql;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// The grant this deployment must hold for tenant-status enforcement and plan limits to work at
/// all, asserted against the LIVE database before the process serves its first request.
///
/// <para><b>Why this exists.</b> <see cref="TenantAccessService"/> resolves a BusinessUnit's tenant
/// status and plan on the tenant plane, executing as <c>nexora_tenant_app</c> (or
/// <c>nexora_identity_app</c> on the login path). 20260805105320 deliberately narrowed both roles
/// from table-level SELECT on <c>platform."Tenants"</c> and <c>platform."Plans"</c> to a named list
/// of columns. That is a good control and it is kept — but it means the enforcement query and the
/// grant are two separate artifacts that have to agree, and when they disagree PostgreSQL answers
/// <c>42501</c> on the FIRST column the role cannot read, before evaluating anything else.</para>
///
/// <para>They did disagree. <c>CoreQuery</c> projects <c>Plan.Features</c>, and the grant for that
/// column arrived three days later in 20260808163605. Any deployment cut between those two
/// migrations answered 42501 on every single tenant request. The old <c>catch</c> in
/// <see cref="TenantAccessService"/> turned that into "no suspension, no past-due gating, no
/// archival, no plan limits" for every tenant on the platform, re-decided every ten seconds, and
/// the only trace was a log line. SQLite — the portable test lane — has neither roles nor column
/// privileges, so no test could see it either.</para>
///
/// <para><b>What this class changes.</b> Failing closed (Sec-D1) converts that silent hole into a
/// visible outage, which is better but is still an outage discovered by a customer. This assertion
/// moves the discovery to boot: a deployment whose grant does not cover every column the
/// enforcement query reads REFUSES TO START, naming the exact role/table/column triples to grant.
/// The deployment that would have run unenforced now does not run.</para>
///
/// <para><b>Skipped, deliberately, when the execution roles are absent.</b> A local or single-role
/// database has no <c>nexora_tenant_app</c> at all — the hardening migration itself returns early
/// in that case — so there is no column privilege to check and nothing to fail on. The assertion
/// applies exactly where the control does.</para>
/// </summary>
public static class TenantAccessGrantContract
{
    /// <summary>The two roles the tenant-access read can execute as. Both must be able to read
    /// every column: <c>nexora_identity_app</c> serves <c>/api/Auth/Login</c>, where a missing
    /// grant would refuse every sign-in rather than every request.</summary>
    public static readonly IReadOnlyList<string> ExecutionRoles =
        ["nexora_tenant_app", "nexora_identity_app"];

    /// <summary>
    /// Every column <see cref="TenantAccessService"/>'s <c>CoreQuery</c> projects, spelled as the
    /// database spells it.
    ///
    /// <para>Hand-maintained, because an EF projection cannot be introspected back into a column
    /// list — but NOT unguarded: <c>TenantAccessGrantContractTests</c> reflects over
    /// <see cref="PlanSnapshot"/> and fails if a property is added here without a matching column,
    /// which is the exact shape of the drift that produced the <c>Features</c> gap.</para>
    /// </summary>
    public static readonly IReadOnlyList<RequiredColumn> RequiredColumns =
    [
        // platform."Tenants" — the tenant identity, its owning BU, its lifecycle status and its
        // plan FK. Status is what suspension/past-due/archival enforcement reads.
        new("platform.\"Tenants\"", "Id"),
        new("platform.\"Tenants\"", "PrimaryBusinessUnitId"),
        new("platform.\"Tenants\"", "Status"),
        new("platform.\"Tenants\"", "PlanId"),
        // Granted by 20260818013530, in the same migration that creates it — the whole point of
        // this contract is that a projected column and its grant must never ship apart again.
        // This is the column that decides which modules a customer can reach.
        new("platform.\"Tenants\"", "Entitlements"),

        // platform."Plans" — every field of PlanSnapshot. Name is deliberately NOT here and
        // deliberately not projected: it is not granted to the tenant plane, which is why a plan
        // is identified by Code in quota messages.
        new("platform.\"Plans\"", "Id"),
        new("platform.\"Plans\"", "Code"),
        new("platform.\"Plans\"", "Weight"),
        new("platform.\"Plans\"", "MaxConcurrentExtractionJobs"),
        new("platform.\"Plans\"", "MaxDocsPerMonth"),
        new("platform.\"Plans\"", "MaxSeats"),
        // Granted only by 20260808163605. This is the column whose absence produced the defect.
        new("platform.\"Plans\"", "Features")
    ];

    /// <summary>One column of one table that the tenant-access projection reads.</summary>
    public readonly record struct RequiredColumn(string QualifiedTable, string Column)
    {
        public override string ToString() => $"{QualifiedTable}.\"{Column}\"";
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when any execution role cannot SELECT any
    /// column in <see cref="RequiredColumns"/>. Returns quietly when the execution roles do not
    /// exist (a database that never ran the grant hardening) or when the platform tables are not
    /// present yet.
    /// </summary>
    /// <param name="connectionString">A PostgreSQL connection. Any role may run this, including one
    /// with no privileges at all on the <c>platform</c> schema.
    ///
    /// <para>That last clause used to read "the privilege functions are asked ABOUT a role by name,
    /// they do not require being it" — true, and beside the point. Asking about another role does
    /// not need that role's privileges, but NAMING THE TABLE needs the caller's own. Both
    /// <c>to_regclass('platform."Tenants"')</c> and the text-table overload of
    /// <c>has_column_privilege</c> resolve the identifier in the CALLER's context, and that
    /// resolution requires USAGE on the schema before any privilege question is evaluated.</para>
    ///
    /// <para>Production has exactly the shape that breaks: the login role <c>nexora_runtime</c> is
    /// NOINHERIT and holds no ambient rights — it is a member of the execution roles and reaches the
    /// tenant plane only by SET ROLE, which the interceptor issues per command. USAGE on
    /// <c>platform</c> is granted to the four <c>*_app</c> roles and deliberately NOT to the login
    /// role. So this check, which opens a raw connection and issues no SET ROLE, died on its first
    /// statement with <c>42501: permission denied for schema platform</c>, taking the process down
    /// with it (Program.cs, exit 139) on every boot. The grants it exists to verify were correct the
    /// whole time; the verification could not see them.</para>
    ///
    /// <para>Resolved by looking the tables up in <c>pg_catalog</c> — readable by every role without
    /// schema USAGE — and passing the resulting OID to the <c>regclass</c> overload, which performs
    /// no name resolution. The check now asserts the same thing while requiring nothing of whoever
    /// runs it, which is what the original comment claimed and what a boot contract should be.</para></param>
    public static async Task AssertReadableAsync(
        string connectionString, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        var roles = await PresentRolesAsync(connection, ct);
        if (roles.Count == 0)
        {
            logger.LogInformation(
                "Tenant-access grant contract: neither {Roles} exists on this database, so there are "
                + "no column privileges to verify. The tenant plane is not role-separated here.",
                string.Join(" nor ", ExecutionRoles));
            return;
        }

        // One catalogue lookup per distinct table, reused for every role/column pair below.
        var tableOids = await ResolveTableOidsAsync(connection, ct);
        if (tableOids.Count < RequiredColumns.Select(c => c.QualifiedTable).Distinct().Count())
        {
            logger.LogInformation(
                "Tenant-access grant contract: the platform tables are not present yet, so the "
                + "column privileges cannot be verified on this boot.");
            return;
        }

        var missing = new List<string>();
        foreach (var role in roles)
            foreach (var column in RequiredColumns)
                if (!await CanSelectAsync(connection, role, tableOids[column.QualifiedTable], column, ct))
                    missing.Add($"GRANT SELECT (\"{column.Column}\") ON TABLE {column.QualifiedTable} TO {role};");

        if (missing.Count == 0)
        {
            logger.LogInformation(
                "Tenant-access grant contract satisfied: {RoleCount} execution role(s) can read all "
                + "{ColumnCount} column(s) the tenant status and plan-limit query projects.",
                roles.Count, RequiredColumns.Count);
            return;
        }

        // Deliberately fatal. The alternative — start and refuse every tenant with a 503 — is a
        // whole-platform outage discovered by customers, and the alternative before Sec-D1 was
        // worse still: start and enforce nothing. Neither is a better failure than not starting,
        // and the remedy is printed in full below.
        throw new InvalidOperationException(
            "Tenant-status enforcement and plan limits cannot run: the tenant-plane execution "
            + "role(s) cannot SELECT every column the tenant-access query reads, so PostgreSQL "
            + "would answer 42501 on every tenant request and every business unit would be refused. "
            + "This is what a deployment cut between 20260805105320 and 20260808163605 looks like. "
            + "Apply the outstanding migration, or run:\n  "
            + string.Join("\n  ", missing));
    }

    private static async Task<IReadOnlyList<string>> PresentRolesAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            "SELECT rolname FROM pg_roles WHERE rolname = ANY(@roles);", connection);
        command.Parameters.AddWithValue("roles", ExecutionRoles.ToArray());

        var present = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            present.Add(reader.GetString(0));
        return present;
    }

    /// <summary>
    /// Maps each required table to its OID, via <c>pg_catalog</c> rather than <c>to_regclass</c>.
    ///
    /// <para>The catalogue is readable by every role and needs no USAGE on the schema being
    /// described, so this succeeds on the NOINHERIT login role that <c>to_regclass</c> refused. A
    /// table absent from the result is simply not there yet — the same "not migrated" case the old
    /// existence probe reported, now per table.</para>
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, uint>> ResolveTableOidsAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT n.nspname, c.relname, c.oid
            FROM pg_catalog.pg_class c
            JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'platform' AND c.relname = ANY(@names);
            """, connection);
        command.Parameters.AddWithValue("names", RequiredColumns
            .Select(c => UnqualifiedName(c.QualifiedTable)).Distinct().ToArray());

        var oids = new Dictionary<string, uint>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            oids[$"{reader.GetString(0)}.\"{reader.GetString(1)}\""] = reader.GetFieldValue<uint>(2);
        return oids;
    }

    /// <summary>Turns <c>platform."Tenants"</c> into <c>Tenants</c> for a catalogue lookup.</summary>
    private static string UnqualifiedName(string qualifiedTable)
        => qualifiedTable[(qualifiedTable.IndexOf('.') + 1)..].Trim('"');

    private static async Task<bool> CanSelectAsync(
        NpgsqlConnection connection, string role, uint tableOid, RequiredColumn column,
        CancellationToken ct)
    {
        // The regclass (OID) overload, NOT the text one. The text overload resolves the table name
        // in the caller's context and therefore needs USAGE on the schema — which is exactly what
        // the login role does not have, and what took the boot down with 42501.
        //
        // has_column_privilege still raises 42703 for a column that does not exist at all, which is
        // a different defect (the projection names a column the schema does not have) but has the
        // same consequence, so it is reported the same way rather than crashing the boot check.
        await using var command = new NpgsqlCommand(
            "SELECT has_column_privilege(@role, @table::oid::regclass, @column, 'SELECT');",
            connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", (long)tableOid);
        command.Parameters.AddWithValue("column", column.Column);

        try
        {
            return await command.ExecuteScalarAsync(ct) is true;
        }
        catch (PostgresException)
        {
            return false;
        }
    }
}
