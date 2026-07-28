using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// Preserves a managed PostgreSQL schema-owner role when replaying the legacy AI
/// governance migration. Runtime traffic still uses a separately validated,
/// least-privilege NOINHERIT role.
/// </summary>
public sealed class ManagedPostgresMigrationCommandInterceptor : DbCommandInterceptor
{
    private const string LegacyOwnerMutation =
        "EXECUTE format('ALTER ROLE %I NOINHERIT', current_user);";

    private const string ManagedOwnerCompatibilityStatement =
        "RAISE NOTICE 'Skipping legacy NOINHERIT mutation for the managed migration owner; runtime role validation remains mandatory.';";

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        command.CommandText = RewriteLegacyManagedOwnerMutation(command.CommandText);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        command.CommandText = RewriteLegacyManagedOwnerMutation(command.CommandText);
        return ValueTask.FromResult(result);
    }

    public static string RewriteLegacyManagedOwnerMutation(string commandText)
        => commandText.Replace(
            LegacyOwnerMutation,
            ManagedOwnerCompatibilityStatement,
            StringComparison.Ordinal);
}
