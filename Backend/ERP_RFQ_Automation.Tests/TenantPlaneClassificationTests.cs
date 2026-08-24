using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Keeps <see cref="TenantPlaneDataMap"/> in step with the live schema, so that adding a table
/// fails HERE rather than by leaving a departed customer's data in place.
///
/// <para><b>Why the runtime guard is not enough on its own.</b>
/// <c>TenantPurgeExecutor.EnsureEveryTenantPlaneTableIsClassifiedAsync</c> refuses to purge an
/// unclassified table, which is the right behaviour and the wrong moment: the first person to
/// discover it is an operator halfway through destroying a customer, and until somebody purges a
/// tenant nothing says anything at all. These run on every build.</para>
///
/// <para><b>What went wrong to make this necessary.</b> Fourteen <c>public</c> tables reach a
/// tenant only through a parent, and the purge swept none of them — <c>RFQItems</c>,
/// <c>QuoteItems</c>, <c>LeadItems</c> and <c>OrderItems</c> are every line of every enquiry,
/// quote and order the tenant ever had. Nothing was deciding to keep them; the sweep was derived
/// from "carries a business unit column", and the question "and what about the tables that do
/// not?" had never been asked.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantPlaneClassificationTests(PostgreSqlTestDatabase database)
{
    /// <summary>
    /// Every <c>public</c> table with no business unit column is classified.
    ///
    /// <para>The rule is deliberately about the COLUMN and not about the casing: a name is matched
    /// with underscores stripped, because this schema carries <c>"BusinessUnitId"</c>,
    /// <c>"BUID"</c> and <c>business_unit_id</c> and the purge used to recognise only the first
    /// two. Eleven evidence and extraction tables were invisible to it for that reason alone.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_public_table_without_a_business_unit_column_is_classified()
    {
        var unclassified = (await QueryAsync<string>("""
            SELECT t.table_name AS "Value"
            FROM information_schema.tables t
            WHERE t.table_schema = 'public'
              AND t.table_type = 'BASE TABLE'
              AND NOT EXISTS (
                  SELECT 1 FROM information_schema.columns c
                  WHERE c.table_schema = t.table_schema
                    AND c.table_name = t.table_name
                    AND lower(replace(c.column_name, '_', '')) IN ('businessunitid', 'buid'))
            ORDER BY t.table_name;
            """)).Where(t => TenantPlaneDataMap.Find(t) is null).ToList();

        Assert.True(unclassified.Count == 0,
            "These public tables carry no business unit column and nobody has said whether they "
            + "hold the customer's data. Declare each in TenantPlaneDataMap — the parent that "
            + "scopes it, or the reason it is not tenant data. Leaving one out is how every RFQ "
            + "line in the system survived a purge that reported success: "
            + string.Join(", ", unclassified));
    }

    /// <summary>
    /// Every table the DATABASE declares tenant-scoped is one the purge can select.
    ///
    /// <para><c>nexora_tenant_isolation</c> is the schema's own statement that a table holds one
    /// tenant's rows — the request path has always honoured it, and it already knew
    /// <c>EmailIngests</c> belonged to a tenant while the purge did not. Reading it here is what
    /// turns "add a tenant table and forget the sweep" into a failing build.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_table_the_schema_calls_tenant_scoped_can_be_selected_by_the_purge()
    {
        var declared = await QueryAsync<string>($"""
            SELECT c.relname AS "Value"
            FROM pg_policy p
            JOIN pg_class c ON c.oid = p.polrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE p.polname = 'nexora_tenant_isolation'
              AND n.nspname = 'public'
              AND NOT EXISTS (
                  SELECT 1 FROM pg_attribute a
                  WHERE a.attrelid = c.oid AND a.attnum > 0 AND NOT a.attisdropped
                    AND lower(replace(a.attname, '_', '')) IN ('businessunitid', 'buid'))
            ORDER BY c.relname;
            """);

        var unreachable = declared
            .Where(t => TenantPlaneDataMap.Find(t) is not { ReachedThrough.Count: > 0 })
            // The tenant's own workspace row is destroyed last and by primary key, not through a
            // parent chain; it is classified, just not as an indirect target.
            .Where(t => t != "BusinessUnits")
            .ToList();

        Assert.True(unreachable.Count == 0,
            "The database says these hold one tenant's rows and the purge has no predicate that "
            + "selects them, so a purge would report success with the customer's data still in "
            + "place: " + string.Join(", ", unreachable));
    }

    /// <summary>
    /// Every declared parent link is <c>NOT NULL</c>, and the chain really does end at a business
    /// unit.
    ///
    /// <para>A NULLABLE parent link is the quietest way to rebuild this defect. The predicate would
    /// match most of the tenant's rows, the sweep would report a healthy number, and every row
    /// whose parent column happened to be null would survive — partial success, reported as
    /// success. <c>public."QuoteItems"</c> is the live example of how easily it happens: it carries
    /// five foreign keys to tenant-scoped tables and only <c>QuoteID</c> is mandatory.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Every_declared_parent_link_is_mandatory_and_ends_at_a_business_unit()
    {
        var problems = new List<string>();

        foreach (var table in TenantPlaneDataMap.Tables.Where(t => t.ReachedThrough is { Count: > 0 }))
        foreach (var parent in table.ReachedThrough!)
        {
            var nullable = await QueryAsync<string>($"""
                SELECT c.is_nullable AS "Value"
                FROM information_schema.columns c
                WHERE c.table_schema = 'public'
                  AND c.table_name = '{table.Table}'
                  AND c.column_name = '{parent.ForeignKeyColumn}';
                """);
            if (nullable.Count == 0)
                problems.Add($"public.\"{table.Table}\".\"{parent.ForeignKeyColumn}\" does not exist");
            else if (nullable[0] != "NO")
                problems.Add(
                    $"public.\"{table.Table}\".\"{parent.ForeignKeyColumn}\" is nullable, so the "
                    + "purge predicate built from it cannot match every one of the tenant's rows");

            var parentScoped = await QueryAsync<string>($"""
                SELECT a.attname AS "Value"
                FROM pg_attribute a
                WHERE a.attrelid = to_regclass('public."{parent.ParentTable}"')
                  AND a.attnum > 0 AND NOT a.attisdropped
                  AND lower(replace(a.attname, '_', '')) IN ('businessunitid', 'buid');
                """);
            var parentIsIndirect = TenantPlaneDataMap.Find(parent.ParentTable)
                is { ReachedThrough.Count: > 0 };
            if (parentScoped.Count == 0 && !parentIsIndirect)
                problems.Add(
                    $"public.\"{table.Table}\" is scoped through public.\"{parent.ParentTable}\", "
                    + "which carries no business unit column and is not itself classified, so the "
                    + "chain does not end at a tenant");

            var parentKey = await QueryAsync<string>($"""
                SELECT a.attname AS "Value"
                FROM pg_attribute a
                WHERE a.attrelid = to_regclass('public."{parent.ParentTable}"')
                  AND a.attname = '{parent.ParentKeyColumn}'
                  AND a.attnum > 0 AND NOT a.attisdropped;
                """);
            if (parentKey.Count == 0)
                problems.Add(
                    $"public.\"{parent.ParentTable}\".\"{parent.ParentKeyColumn}\" does not exist, "
                    + $"so the predicate for public.\"{table.Table}\" answers 42703 mid-sweep");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// <c>public."Attachments"</c> is polymorphic, so the map has to name every parent KIND the
    /// application writes — and it has to be all of them.
    ///
    /// <para>The row-level-security policy admits only <c>'Lead'</c>, which is why a
    /// <c>MaterialLotCertificate</c> attachment is invisible to the tenant that owns it and
    /// survived every purge. The constant is referenced rather than typed out so that renaming it
    /// breaks this test rather than silently reintroducing a third immortal kind.</para>
    /// </summary>
    [Fact]
    public void The_attachment_parent_kinds_cover_every_kind_the_application_writes()
    {
        var declared = TenantPlaneDataMap.Find("Attachments")!.ReachedThrough!
            .Select(p => p.DiscriminatorValue)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(TenantPlaneDataMap.LeadAttachmentParentType, declared);
        Assert.Contains(
            ERP_RFQ_Automation.Traceability.MaterialLotCertificateService.CertificateParentType,
            declared);
        Assert.All(
            TenantPlaneDataMap.Find("Attachments")!.ReachedThrough!,
            parent => Assert.Equal("ParentType", parent.DiscriminatorColumn));
    }

    private async Task<List<T>> QueryAsync<T>(string sql)
    {
        await using var context = database.ContextFor(null);
        return await context.Database.SqlQueryRaw<T>(sql).ToListAsync();
    }
}
