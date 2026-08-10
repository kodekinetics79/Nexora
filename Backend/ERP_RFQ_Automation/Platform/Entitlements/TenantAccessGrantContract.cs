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
    /// <param name="connectionString">A PostgreSQL connection. Any role may run this — the
    /// privilege functions are asked ABOUT a role by name, they do not require being it.</param>
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

        if (!await PlatformTablesExistAsync(connection, ct))
        {
            logger.LogInformation(
                "Tenant-access grant contract: the platform tables are not present yet, so the "
                + "column privileges cannot be verified on this boot.");
            return;
        }

        var missing = new List<string>();
        foreach (var role in roles)
            foreach (var column in RequiredColumns)
                if (!await CanSelectAsync(connection, role, column, ct))
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

    private static async Task<bool> PlatformTablesExistAsync(
        NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('platform."Tenants"') IS NOT NULL
               AND to_regclass('platform."Plans"') IS NOT NULL;
            """, connection);
        return await command.ExecuteScalarAsync(ct) is true;
    }

    private static async Task<bool> CanSelectAsync(
        NpgsqlConnection connection, string role, RequiredColumn column, CancellationToken ct)
    {
        // has_column_privilege raises 42703 for a column that does not exist at all, which is a
        // different defect (the projection names a column the schema does not have) but has the
        // same consequence, so it is reported the same way rather than crashing the boot check.
        await using var command = new NpgsqlCommand(
            "SELECT has_column_privilege(@role, @table, @column, 'SELECT');", connection);
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("table", column.QualifiedTable);
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
