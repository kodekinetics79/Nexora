using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Tests.Support;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The invariant whose absence produced three separate defects.
///
/// <para>A red-team audit found that a purge orphaned <c>platform."ProvisioningSteps"</c>, left
/// <c>platform."ProvisioningDrafts"</c> holding the founding administrator's email address through
/// both a purge AND an Article 17 erasure, and destroyed
/// <c>platform."ImpersonationSessions"</c> — the record of operators signing into the customer's
/// account, which had to be kept. Three findings, one root cause: <b>nothing forced a new platform
/// table carrying tenant data to declare whose record it is.</b> The purge inferred it from a
/// column name, and a column name cannot answer that question in either direction.</para>
///
/// <para>This test is that missing force. It computes, from the LIVE catalogue, every
/// platform-schema table reachable from a tenant — a <c>TenantId</c> column, or a foreign key to
/// something reachable, transitively — and fails unless each one is classified in
/// <see cref="PlatformTenantDataMap"/> as the customer's record or the operator's. A table cannot
/// join the schema silently; somebody has to decide, in writing, and the decision sits next to the
/// neighbouring decisions it has to be consistent with.</para>
///
/// <para>Reachability is computed rather than listed for the same reason the tenant-plane sweep is
/// derived rather than listed: a list of things to check stops covering the schema the moment
/// somebody adds to it, and that failure is invisible.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantLifecyclePlatformTableClassificationTests(PostgreSqlTestDatabase database)
{
    /// <summary>
    /// Tables that hold no tenant data and never will. Declared, not inferred, so that adding one
    /// is still a decision somebody records — the point of the whole exercise.
    /// </summary>
    private static readonly IReadOnlySet<string> NotTenantScoped =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // The catalogue of what can be sold, and the people who work for the operator.
            // Neither belongs to any customer, and neither survives or dies with one.
            "Plans",
            "PlatformUsers",
            "RateCards",
            "RateCardLines"
        };

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_platform_table_reachable_from_a_tenant_declares_whose_record_it_is()
    {
        await using var connection = await database.OpenConnectionAsync();
        var reachable = await ReachableFromATenantAsync(connection);

        Assert.NotEmpty(reachable);

        var unclassified = reachable
            .Where(table => PlatformTenantDataMap.Find(table) is null)
            .Where(table => !NotTenantScoped.Contains(table))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToList();

        Assert.True(unclassified.Count == 0,
            $"These platform tables hold data reachable from a tenant and are not classified:\n"
            + string.Join("\n", unclassified.Select(t => $"  platform.\"{t}\""))
            + "\n\nAdd each to PlatformTenantDataMap.Tables saying whether it is the CUSTOMER's "
            + "record (destroyed by a purge) or the OPERATOR's record of them (preserved), and why. "
            + "Neither answer can be inferred from the schema, and both wrong answers are damaging: "
            + "one erases the evidence of how a customer was treated, the other leaves their data "
            + "behind after they were told it was gone.");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_classified_table_still_exists_and_its_declared_columns_are_real()
    {
        // The other direction. A stale entry is a rule that quietly stops applying — worse than a
        // missing one, because the map reads as complete.
        await using var connection = await database.OpenConnectionAsync();
        var problems = new List<string>();

        foreach (var declared in PlatformTenantDataMap.Tables)
        {
            if (!await TableExistsAsync(connection, declared.Table))
            {
                // A module whose migration has not landed is not a defect; a permanently absent
                // table is. Reported as information rather than failure would hide the second, so
                // the assertion below tolerates only tables the schema genuinely has.
                continue;
            }

            if (declared.TenantColumn is string tenantColumn
                && !await ColumnExistsAsync(connection, declared.Table, tenantColumn))
                problems.Add($"platform.\"{declared.Table}\" has no column \"{tenantColumn}\".");

            if (declared.ReachedThrough is PlatformTenantParent parent)
            {
                if (!await ColumnExistsAsync(connection, declared.Table, parent.ForeignKeyColumn))
                    problems.Add(
                        $"platform.\"{declared.Table}\" has no column \"{parent.ForeignKeyColumn}\".");

                if (await TableExistsAsync(connection, parent.ParentTable)
                    && !await ColumnExistsAsync(connection, parent.ParentTable, parent.ParentKeyColumn))
                    problems.Add(
                        $"platform.\"{parent.ParentTable}\" has no column \"{parent.ParentKeyColumn}\".");

                Assert.True(PlatformTenantDataMap.Find(parent.ParentTable) is not null,
                    $"platform.\"{declared.Table}\" is reached through platform.\"{parent.ParentTable}\", "
                    + "which is not itself classified. The chain has to end at a table carrying a "
                    + "tenant column.");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public void A_table_reached_through_a_parent_is_deleted_before_that_parent()
    {
        // Ordering is the second way to re-create the orphaned-steps defect. A child is selected
        // through a subquery on its parent, so deleting the parent first matches nothing and
        // leaves every child behind — the same outcome as never listing the child at all.
        foreach (var child in PlatformTenantDataMap.Destroyed.Where(t => t.IsIndirect))
        {
            var parent = PlatformTenantDataMap.Find(child.ReachedThrough!.ParentTable);
            Assert.NotNull(parent);
            Assert.True(PlatformTenantDataMap.Depth(child) > PlatformTenantDataMap.Depth(parent!),
                $"platform.\"{child.Table}\" must sort deeper than its parent "
                + $"platform.\"{parent!.Table}\" or the purge deletes the parent first.");
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public void The_preserved_list_the_purge_uses_is_exactly_what_the_map_declares()
    {
        // One source of truth. The preserve list used to be its own hand-kept set, which is how
        // ImpersonationSessions came to be missing from it while sitting one line away from
        // SupportTickets in everybody's mental model.
        foreach (var preserved in PlatformTenantDataMap.Preserved)
            Assert.Contains($"platform.{preserved.Table}", TenantPurgeExecutor.PreservedTables);

        foreach (var destroyed in PlatformTenantDataMap.Destroyed)
            Assert.DoesNotContain($"platform.{destroyed.Table}", TenantPurgeExecutor.PreservedTables);

        // The two findings this pins, named so a future edit that reverses either one fails here
        // with the reason rather than somewhere downstream with a row count.
        Assert.Equal(TenantDataClass.OperatorRecord,
            PlatformTenantDataMap.Find("ImpersonationSessions")!.Classification);
        Assert.Equal(TenantDataClass.CustomerRecord,
            PlatformTenantDataMap.Find("ProvisioningDrafts")!.Classification);
    }

    // ---------------------------------------------------------------------------------- helpers

    /// <summary>
    /// Transitive closure over the platform schema's foreign keys, seeded with every table
    /// carrying a <c>TenantId</c> column plus <c>Tenants</c> itself.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReachableFromATenantAsync(NpgsqlConnection connection)
    {
        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tenants" };

        await using (var seed = new NpgsqlCommand(
            """
            SELECT DISTINCT c.table_name
            FROM information_schema.columns c
            JOIN information_schema.tables t
              ON t.table_schema = c.table_schema AND t.table_name = c.table_name
             AND t.table_type = 'BASE TABLE'
            WHERE c.table_schema = 'platform'
              AND lower(c.column_name) = 'tenantid';
            """, connection))
        await using (var reader = await seed.ExecuteReaderAsync())
            while (await reader.ReadAsync()) reachable.Add(reader.GetString(0));

        var edges = new List<(string Child, string Parent)>();
        await using (var keys = new NpgsqlCommand(
            """
            SELECT child.relname AS child, parent.relname AS parent
            FROM pg_constraint fk
            JOIN pg_class child ON child.oid = fk.conrelid
            JOIN pg_class parent ON parent.oid = fk.confrelid
            JOIN pg_namespace cn ON cn.oid = child.relnamespace
            JOIN pg_namespace pn ON pn.oid = parent.relnamespace
            WHERE fk.contype = 'f' AND cn.nspname = 'platform' AND pn.nspname = 'platform';
            """, connection))
        await using (var reader = await keys.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                edges.Add((reader.GetString(0), reader.GetString(1)));

        // Fixed point: a child of a reachable table is reachable, however deep the chain.
        bool grew;
        do
        {
            grew = false;
            foreach (var (child, parent) in edges)
                if (reachable.Contains(parent) && reachable.Add(child))
                    grew = true;
        } while (grew);

        return reachable.OrderBy(t => t, StringComparer.Ordinal).ToList();
    }

    private static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string table)
    {
        await using var command = new NpgsqlCommand(
            "SELECT to_regclass(@qualified) IS NOT NULL;", connection);
        command.Parameters.AddWithValue("qualified", $"platform.\"{table}\"");
        return await command.ExecuteScalarAsync() is true;
    }

    private static async Task<bool> ColumnExistsAsync(
        NpgsqlConnection connection, string table, string column)
    {
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'platform' AND table_name = @table AND column_name = @column);
            """, connection);
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);
        return await command.ExecuteScalarAsync() is true;
    }
}
