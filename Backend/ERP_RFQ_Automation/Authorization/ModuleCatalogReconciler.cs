using Npgsql;

namespace ERP_RFQ_Automation.Authorization;

/// <summary>Counts from one reconciliation pass. Safe to log — names only, no tenant data.</summary>
public sealed record ModuleCatalogResult(int Inserted, int AlreadyPresent, IReadOnlyList<string> Unrecognised)
{
    public int CatalogueSize => Inserted + AlreadyPresent;
}

/// <summary>
/// Makes the <c>Module</c> table match <see cref="ModuleCatalog"/> on every boot.
///
/// <para><b>Why this is startup work rather than a migration.</b> The catalogue is CODE — it is
/// asserted against the assembly's <c>[RequireModulePermission]</c> attributes by
/// <c>ModuleCatalogTests</c>, so it changes whenever a gated endpoint is added. Reconciling it
/// from a migration would mean a new migration for every such change, and would silently rot the
/// moment someone forgot. Doing it at boot means the invariant is "the database agrees with the
/// code", enforced continuously, and adding a gated endpoint requires nothing beyond adding it to
/// the catalogue — which the tests already demand.</para>
///
/// <para><b>Insert-only, deliberately.</b> Rows that exist are left completely alone: not renamed,
/// not re-described, not reactivated. <c>Module</c> is platform-global reference data whose Ids are
/// foreign-keyed by every tenant's <c>RolePermissions</c>, so an UPDATE here rewrites data shared
/// by every customer on the platform on the strength of a code comment — including any description
/// a customer edited for their own operators. Nothing is ever deleted, because removing a module
/// cascades away every grant referencing it.</para>
///
/// <para><b>Note on <c>IsActive</c>:</b> it is NOT consulted by
/// <c>RolePermissionRepository.CheckPermissionAsync</c>, which matches on module NAME alone. So
/// deactivating a module does not currently revoke anything, and this class not reactivating rows
/// is about leaving administrator intent intact rather than about closing an access path. If
/// <c>IsActive</c> is ever made to gate access, that is the moment to re-check this decision.</para>
///
/// <para>Idempotent, so every boot after the first is a no-op. Failures are logged and swallowed:
/// a transient database problem must not stop the API from starting, and the next boot retries.</para>
/// </summary>
public static class ModuleCatalogReconciler
{
    public static async Task<ModuleCatalogResult> RunAsync(
        string connectionString,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(logger);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            var existing = await ReadExistingAsync(connection, cancellationToken);
            var missing = ModuleCatalog.All.Where(m => !existing.Contains(m.Name)).ToList();

            if (missing.Count > 0)
            {
                await InsertAsync(connection, missing, cancellationToken);
                logger.LogInformation(
                    "Module catalogue: inserted {Count} missing permission module(s): {Modules}.",
                    missing.Count, string.Join(", ", missing.Select(m => m.Name)));
            }

            // Rows in the table that the code does not enforce. Not an error and never deleted —
            // they may be a newer deployment's modules during a rolling release, or a module a
            // customer added by hand. Reported because a permission checkbox that gates nothing
            // is misleading to whoever ticks it.
            var unrecognised = existing.Where(name => !ModuleCatalog.Names.Contains(name))
                .OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (unrecognised.Count > 0)
                logger.LogWarning(
                    "Module catalogue: {Count} module row(s) are not enforced by any endpoint, so " +
                    "granting them has no effect: {Modules}.",
                    unrecognised.Count, string.Join(", ", unrecognised));

            return new ModuleCatalogResult(missing.Count, existing.Count - unrecognised.Count, unrecognised);
        }
        catch (Exception exception)
        {
            // Swallowed on purpose. A super admin bypasses module checks entirely, so an
            // unreconciled catalogue degrades the product rather than bricking it, and refusing
            // to boot over reference data would be a worse failure than starting without it.
            logger.LogError(exception,
                "Module catalogue reconciliation failed; permission modules may be missing. " +
                "Roles will be unable to hold permissions for any module absent from the Module table.");
            return new ModuleCatalogResult(0, 0, []);
        }
    }

    private static async Task<HashSet<string>> ReadExistingAsync(
        NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new NpgsqlCommand(
            """SELECT "ModuleName" FROM public."Module";""", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection,
        IReadOnlyList<ModuleCatalog.ModuleDefinition> missing,
        CancellationToken cancellationToken)
    {
        // ON CONFLICT rather than relying on the read above: two instances can start at once, and
        // Module.ModuleName carries a unique index. Losing that race must be a no-op, not a
        // startup crash on one of the instances.
        await using var command = new NpgsqlCommand
        {
            Connection = connection,
            CommandText =
                """
                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                SELECT unnest(@names), unnest(@descriptions), true, @actor, now()
                ON CONFLICT ("ModuleName") DO NOTHING;
                """
        };
        command.Parameters.AddWithValue("names", missing.Select(m => m.Name).ToArray());
        command.Parameters.AddWithValue("descriptions", missing.Select(m => m.Description).ToArray());
        command.Parameters.AddWithValue("actor", ModuleCatalog.SeedActor);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// Runs <see cref="ModuleCatalogReconciler"/> once, shortly after the host starts.
///
/// <para>A hosted service rather than an inline startup await, for two reasons: it runs after any
/// startup migration has applied the schema it depends on, and a slow or unreachable database
/// delays only the reconciliation instead of the whole boot. Reference data is not worth refusing
/// to serve traffic over — a super admin bypasses module checks entirely, so an unreconciled
/// catalogue degrades the product rather than bricking it.</para>
/// </summary>
public sealed class ModuleCatalogStartupService(
    IConfiguration configuration,
    ILogger<ModuleCatalogStartupService> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("MigrationConnection")
                               ?? configuration.GetConnectionString("DefaultConnection");

        return string.IsNullOrWhiteSpace(connectionString)
            ? Task.CompletedTask
            : ModuleCatalogReconciler.RunAsync(connectionString, logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
