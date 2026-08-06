using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The boot-time reconciliation that makes the Module table agree with <see cref="ModuleCatalog"/>.
///
/// <para>PostgreSQL because the behaviour under test IS the SQL: the unique index that makes the
/// insert idempotent, the <c>ON CONFLICT</c> that makes two simultaneous instances safe, and the
/// deliberate absence of any UPDATE.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ModuleCatalogReconcilerPostgreSqlTests(PostgreSqlTestDatabase database)
{
    private Task<ModuleCatalogResult> RunAsync() =>
        ModuleCatalogReconciler.RunAsync(database.ConnectionString, NullLogger.Instance);

    private async Task<int> CountAsync(string moduleName)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT count(*)::int FROM public."Module" WHERE "ModuleName" = @name;""", connection);
        command.Parameters.AddWithValue("name", moduleName);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_module_the_product_enforces_ends_up_in_the_table()
    {
        await RunAsync();

        await using var context = database.ContextFor(null);
        var present = await context.Modules.AsNoTracking()
            .Select(m => m.ModuleName).ToListAsync();

        var missing = ModuleCatalog.Names.Except(present).OrderBy(x => x).ToList();
        Assert.True(missing.Count == 0,
            "After reconciliation these enforced modules are still absent, so no tenant role can " +
            "hold them:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Running_it_twice_inserts_nothing_the_second_time()
    {
        // Every boot runs this. A second pass that duplicated rows would break permission
        // resolution for the whole platform, because Module rows are what RolePermissions point at.
        await RunAsync();
        var second = await RunAsync();

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, await CountAsync("Email & SMTP"));
        Assert.Equal(1, await CountAsync("Roles & Permissions"));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_existing_row_is_never_rewritten_or_reactivated()
    {
        await RunAsync();

        // An administrator deliberately deactivates a module and gives it their own description.
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                UPDATE public."Module"
                SET "IsActive" = false, "Description" = 'Deliberately disabled by the customer'
                WHERE "ModuleName" = 'Shipments';
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        await RunAsync();

        // Reconciliation is insert-only. Reactivating a module an administrator switched off would
        // silently restore an access path, and rewriting the description would overwrite
        // platform-global reference data on the strength of a code comment.
        await using var context = database.ContextFor(null);
        var row = await context.Modules.AsNoTracking().SingleAsync(m => m.ModuleName == "Shipments");
        Assert.False(row.IsActive);
        Assert.Equal("Deliberately disabled by the customer", row.Description);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_module_row_the_code_does_not_enforce_is_reported_but_left_alone()
    {
        await using (var connection = new NpgsqlConnection(database.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                """
                INSERT INTO public."Module" ("ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
                VALUES ('Legacy Widget Console', 'Left over from an older release', true, 'tests', now())
                ON CONFLICT ("ModuleName") DO NOTHING;
                """, connection);
            await command.ExecuteNonQueryAsync();
        }

        var result = await RunAsync();

        // Reported, because granting it does nothing and that is misleading to whoever ticks it.
        Assert.Contains("Legacy Widget Console", result.Unrecognised);
        // But NOT deleted: removing a module cascades away every grant that references it.
        Assert.Equal(1, await CountAsync("Legacy Widget Console"));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_database_failure_is_swallowed_so_the_api_still_starts()
    {
        // A super admin bypasses module checks entirely, so an unreconciled catalogue degrades the
        // product rather than bricking it. Refusing to boot over reference data would be the worse
        // failure.
        var result = await ModuleCatalogReconciler.RunAsync(
            "Host=127.0.0.1;Port=1;Database=nope;Username=nope;Password=nope;Timeout=1",
            NullLogger.Instance);

        Assert.Equal(0, result.Inserted);
        Assert.Empty(result.Unrecognised);
    }
}
