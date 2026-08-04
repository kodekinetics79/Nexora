using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The crown-jewel invariant (ADR-0005): EF Core global query filters transparently
/// scope every authenticated read to its business unit, while a null tenant context
/// (login / anonymous / background worker) applies no filter. These tests seed rows for
/// TWO business units and assert a scoped context can NEVER observe the other tenant's
/// data, that a null tenant sees everything, that IgnoreQueryFilters is the deliberate
/// opt-out, and that master data with a null Buid is shared to all tenants.
/// </summary>
public class TenantIsolationTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;

    // ---- Commercial documents (non-nullable BusinessUnitId) ----

    [Fact]
    public void ScopedContext_Leads_ReturnsOnlyOwnBusinessUnit()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 101, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var leads = ctx.Leads.ToList();

        Assert.Equal(2, leads.Count);
        Assert.All(leads, l => Assert.Equal(Bu1, l.BusinessUnitId));
        Assert.DoesNotContain(leads, l => l.Id == 200);
    }

    [Fact]
    public void ScopedContext_CannotReadOtherTenantRowByPrimaryKey()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        // BU1 asks for BU2's lead by its exact id -> the filter hides it entirely.
        using var ctx = db.ContextFor(Bu1);
        var found = ctx.Leads.FirstOrDefault(l => l.Id == 200);

        Assert.Null(found);
    }

    [Fact]
    public void NullTenant_BackgroundWorker_SeesAllBusinessUnits()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            Seed.Lead(seed, leadId: 201, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var worker = db.ContextFor(null); // no tenant -> filter is a no-op
        var all = worker.Leads.ToList();

        Assert.Equal(3, all.Count);
        Assert.Contains(all, l => l.BusinessUnitId == Bu1);
        Assert.Contains(all, l => l.BusinessUnitId == Bu2);
    }

    [Fact]
    public void IgnoreQueryFilters_IsTheDeliberateCrossTenantOptOut()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);

        Assert.Single(ctx.Leads);                                  // scoped
        Assert.Equal(2, ctx.Leads.IgnoreQueryFilters().Count());   // opt-out sees both
    }

    [Fact]
    public void ScopedCount_DoesNotLeakOtherTenantThroughAggregates()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 100, businessUnitId: Bu1);
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            Seed.Lead(seed, leadId: 201, businessUnitId: Bu2);
            Seed.Lead(seed, leadId: 202, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        // A COUNT is pushed to SQL; the filter must be applied server-side, not post-hoc.
        using var bu1 = db.ContextFor(Bu1);
        using var bu2 = db.ContextFor(Bu2);
        Assert.Equal(1, bu1.Leads.Count());
        Assert.Equal(3, bu2.Leads.Count()); // sibling context sees only its own rows
    }

    // ---- Customer identity is tenant-owned even when legacy Buid is null ----

    [Fact]
    public void ScopedContext_Customers_SeesOnlyOwnTenant()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, id: 1, buid: Bu1, name: "BU1 Customer");
            Seed.Customer(seed, id: 2, buid: Bu2, name: "BU2 Customer");
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        var visible = ctx.Customers.OrderBy(c => c.Id).ToList();

        Assert.Single(visible);
        Assert.Contains(visible, c => c.Id == 1);   // own tenant
        Assert.DoesNotContain(visible, c => c.Id == 2); // other tenant hidden
    }

    [Fact]
    public void NullTenant_MasterData_SeesEveryRowIncludingBothTenants()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Customer(seed, id: 1, buid: Bu1, name: "BU1 Customer");
            Seed.Customer(seed, id: 2, buid: Bu2, name: "BU2 Customer");
            seed.SaveChanges();
        }

        using var worker = db.ContextFor(null);
        Assert.Equal(2, worker.Customers.Count());
    }

    [Fact]
    public void Customer_without_tenant_is_rejected()
    {
        using var db = new TestDb();
        using var seed = db.ContextFor(null);
        Seed.Customer(seed, id: 3, buid: null, name: "Unowned");

        Assert.Throws<InvalidOperationException>(() => seed.SaveChanges());
    }

    // ---- FindAsync is filtered, and the one case where it is not ----

    /// <summary>
    /// Pins a load-bearing assumption behind every "resolved PK-only" audit finding: in EF Core 9
    /// <c>FindAsync</c> is NOT a filter bypass. When the key is not already tracked it issues a
    /// normal filtered query, so a repository calling <c>FindAsync(id)</c> on a filtered entity is
    /// already tenant-scoped. If this ever regresses, a large amount of code silently becomes a
    /// cross-tenant read and the remediation list changes completely — hence the test.
    /// </summary>
    [Fact]
    public async Task FindAsync_applies_the_global_query_filter()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        Assert.Null(await ctx.Leads.FindAsync(200L));
    }

    /// <summary>
    /// The sharp edge that <em>does</em> exist: Find/FindAsync short-circuits on the change
    /// tracker. An entity pulled into the context through <c>IgnoreQueryFilters()</c> is then
    /// returned by key without any query at all. This is not a reason to distrust FindAsync — it
    /// is a reason to treat <c>IgnoreQueryFilters()</c> as contaminating the whole context.
    /// </summary>
    [Fact]
    public async Task IgnoreQueryFilters_contaminates_later_FindAsync_calls_on_the_same_context()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.Lead(seed, leadId: 200, businessUnitId: Bu2);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(Bu1);
        Assert.Null(await ctx.Leads.FindAsync(200L));

        // Deliberate cross-tenant opt-out anywhere earlier in the request leaves the row tracked.
        ctx.Leads.IgnoreQueryFilters().Single(l => l.Id == 200);

        var fromChangeTracker = await ctx.Leads.FindAsync(200L);
        Assert.NotNull(fromChangeTracker);
        Assert.Equal(Bu2, fromChangeTracker!.BusinessUnitId);
    }

    // =====================================================================================
    // CONVENTION GUARDS
    //
    // Every individual "this repository forgot a .Where(BusinessUnitId == ...)" finding is a
    // symptom. The disease is that tenant isolation here is a PAIR — an EF global query
    // filter (protects every provider, and the BYPASSRLS pipeline/identity roles) plus a
    // PostgreSQL row-level-security policy (protects the tenant role even when application
    // code is wrong) — and nothing structurally forces a new table to join BOTH halves.
    //
    // The two tests below are that structural force. They walk the real production model,
    // not a sample, so a table added next month by any agent fails the build unless a
    // reviewer either wires both layers or consciously extends the opt-out list with a
    // written reason.
    // =====================================================================================

    /// <summary>CLR property names that mark a row as tenant-owned (matched case-insensitively,
    /// so this covers BusinessUnitId / BusinessUnitID / Buid / BUID).</summary>
    private static readonly HashSet<string> TenantPropertyNames =
        new(StringComparer.OrdinalIgnoreCase) { "BusinessUnitId", "Buid" };

    /// <summary>
    /// Entities that carry a tenant column but deliberately have NO global query filter.
    /// Adding a name here is a security decision: it means "a cross-tenant read of this
    /// table is intended". Every entry must carry the reason it is safe.
    /// </summary>
    private static readonly Dictionary<string, string> QueryFilterOptOuts = new(StringComparer.Ordinal)
    {
        // Reviewer: extend ONLY with a written justification. An entry here disables the
        // application-side half of tenant isolation for that table.
    };

    /// <summary>
    /// Tables that deliberately have no PostgreSQL RLS policy. Same rule as above: an entry
    /// here disables the database-side half of tenant isolation for that table.
    /// </summary>
    private static readonly Dictionary<string, string> RlsOptOuts = new(StringComparer.Ordinal)
    {
    };

    /// <summary>
    /// 20260723120000_CompleteTenantRlsCoverage ENABLEs RLS by sweeping information_schema for
    /// every table carrying a tenant column, so tables that already existed at that point are
    /// covered without appearing literally in any migration. Anything created AFTER it must
    /// declare its own policy.
    /// </summary>
    private const long DynamicRlsSweepMigrationId = 20260723120000;

    /// <summary>
    /// The PRODUCTION (Npgsql) model. Built without a connection — EF only needs the provider
    /// to decide which OnModelCreating branches run. Using Npgsql rather than the SQLite
    /// TestDb matters: several entities are configured inside `if (Database.IsNpgsql())`, so
    /// the SQLite model is not the model that ships.
    /// </summary>
    private static IModel ProductionModel()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql("Host=model-only.invalid;Database=nexora;Username=none;Password=none")
            .Options;
        using var context = new ErpRfqAutomationContext(options, new StubTenant(null));
        return context.Model;
    }

    private static IEnumerable<IEntityType> TenantOwnedEntityTypes(IModel model)
        => model.GetEntityTypes()
            // A query filter is declared on the root of an inheritance hierarchy and inherited
            // by its derived types, and owned types are filtered through their owner.
            .Where(e => e.BaseType is null && !e.IsOwned())
            .Where(e => e.GetProperties().Any(p => TenantPropertyNames.Contains(p.Name)))
            .OrderBy(e => e.Name, StringComparer.Ordinal);

    /// <summary>
    /// A table with a tenant column and no query filter is a cross-tenant read waiting to
    /// happen: every LINQ query against it returns other tenants' rows unless the author
    /// remembered a predicate. This test makes "remembering" unnecessary and forgetting loud.
    /// </summary>
    [Fact]
    public void Every_entity_with_a_tenant_column_has_a_global_query_filter()
    {
        var model = ProductionModel();

        var unfiltered = TenantOwnedEntityTypes(model)
            .Where(e => e.GetQueryFilter() is null)
            .Where(e => !QueryFilterOptOuts.ContainsKey(e.ClrType.Name))
            .Select(e => $"{e.ClrType.Name} (table: {e.GetSchema() ?? "public"}.{e.GetTableName()})")
            .ToList();

        Assert.True(unfiltered.Count == 0, $"""
            {unfiltered.Count} entity type(s) carry a tenant column but have NO EF global query filter,
            so every query against them is cross-tenant unless the caller hand-writes a predicate.

            Fix by adding the filter in Models/ErpRfqAutomationContext.Tenancy.cs:
                modelBuilder.Entity<T>().HasQueryFilter(
                    e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

            If a table is genuinely meant to be readable across tenants, add it to
            TenantIsolationTests.QueryFilterOptOuts WITH the reason.

            Offenders:
              {string.Join("\n              ", unfiltered)}
            """);
    }

    /// <summary>
    /// The reverse direction. A query filter alone is not isolation: the tenant database role
    /// executes raw SQL, `IgnoreQueryFilters()` exists, and an application bug bypasses EF
    /// entirely. Every tenant-owned table must also be covered by a row-level-security policy.
    /// </summary>
    [Fact]
    public void Every_tenant_owned_table_is_covered_by_row_level_security()
    {
        var model = ProductionModel();
        var migrations = MigrationSources();

        var uncovered = new List<string>();
        foreach (var entityType in TenantOwnedEntityTypes(model))
        {
            var table = entityType.GetTableName();
            if (table is null) continue;

            // The platform control plane is deliberately not tenant-scoped (ADR-0005): its
            // rows describe tenants, they do not belong to one.
            if (string.Equals(entityType.GetSchema(), "platform", StringComparison.Ordinal)) continue;
            if (RlsOptOuts.ContainsKey(table)) continue;

            if (HasExplicitRlsStatement(migrations, table)) continue;
            if (ExistedBeforeDynamicRlsSweep(migrations, table)) continue;

            uncovered.Add($"{entityType.ClrType.Name} (table: {table})");
        }

        Assert.True(uncovered.Count == 0, $"""
            {uncovered.Count} tenant-owned table(s) have an EF query filter but no row-level-security
            statement in any migration, so the database itself does not enforce the boundary.

            Fix by adding a migration containing, for each table:
                ALTER TABLE public."T" ENABLE ROW LEVEL SECURITY;
                CREATE POLICY nexora_tenant_isolation ON public."T" TO nexora_tenant_app
                    USING ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
                    WITH CHECK ("BusinessUnitID" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

            Offenders:
              {string.Join("\n              ", uncovered)}
            """);
    }

    private readonly record struct MigrationSource(long Id, string Name, string Text);

    private static IReadOnlyList<MigrationSource> MigrationSources()
    {
        var directory = Path.Combine(
            FindRepositoryRoot(), "Backend", "ERP_RFQ_Automation", "Migrations");
        Assert.True(Directory.Exists(directory), $"Migrations directory not found at {directory}.");

        return Directory.EnumerateFiles(directory, "*.cs")
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(path => new
            {
                Name = Path.GetFileNameWithoutExtension(path),
                Text = File.ReadAllText(path)
            })
            .Where(file => file.Name.Length > 14 && long.TryParse(file.Name[..14], out _))
            .Select(file => new MigrationSource(long.Parse(file.Name[..14]), file.Name, file.Text))
            .OrderBy(file => file.Id)
            .ToList();
    }

    /// <summary>Literal `ALTER TABLE ... ENABLE/FORCE ROW LEVEL SECURITY`, or the table named as a
    /// SQL string literal inside a migration that drives the same statement through EXECUTE format.</summary>
    private static bool HasExplicitRlsStatement(IReadOnlyList<MigrationSource> migrations, string table)
    {
        var quoted = Regex.Escape(table);
        var literal = new Regex(
            $"""ALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?:public\.)?"?{quoted}"?\s+(?:ENABLE|FORCE)\s+ROW\s+LEVEL\s+SECURITY""",
            RegexOptions.IgnoreCase);
        var dynamicName = new Regex($"'{quoted}'");

        return migrations.Any(migration =>
            migration.Text.Contains("ROW LEVEL SECURITY", StringComparison.OrdinalIgnoreCase)
            && (literal.IsMatch(migration.Text)
                || (migration.Text.Contains("%I", StringComparison.Ordinal)
                    && dynamicName.IsMatch(migration.Text))));
    }

    private static bool ExistedBeforeDynamicRlsSweep(IReadOnlyList<MigrationSource> migrations, string table)
    {
        var created = new Regex($"""name:\s*"{Regex.Escape(table)}"|CREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?:public\.)?"?{Regex.Escape(table)}"?""",
            RegexOptions.IgnoreCase);

        return migrations
            .Where(migration => migration.Id <= DynamicRlsSweepMigrationId)
            .Any(migration => created.IsMatch(migration.Text));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
