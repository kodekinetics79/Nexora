using System.Reflection;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// EF finds migrations by <see cref="MigrationAttribute"/>, not by filename. In this repo that
/// attribute normally arrives on the generated <c>.Designer.cs</c>, so a hand-written migration —
/// which is how every data-repair migration here is authored — is one omission away from being
/// invisible: it compiles, it reviews, it merges, and it never runs. Nothing else in the suite
/// would notice, because a migration that is never discovered cannot fail.
/// </summary>
public sealed class MigrationDiscoveryTests
{
    private static readonly Assembly MigrationsAssembly =
        typeof(ERP_RFQ_Automation.Models.ErpRfqAutomationContext).Assembly;

    private static List<Type> MigrationTypes() => MigrationsAssembly.GetTypes()
        .Where(type => typeof(Migration).IsAssignableFrom(type) && !type.IsAbstract)
        .ToList();

    /// <summary>
    /// Asks EF's own <see cref="IMigrationsAssembly"/> which migrations it can see, rather than
    /// reflecting over attributes and hoping that is the same question.
    ///
    /// <para>The first version of this test DID reflect over attributes — it asserted every
    /// migration type carries <c>[Migration]</c> — and it passed while
    /// <c>20260812150000_CompleteTypedPlanEntitlements</c> was invisible to EF, shipped to
    /// production, and left the database untouched. <c>MigrationsAssembly.GetMigrations()</c>
    /// filters on <c>[DbContext(typeof(TContext))]</c> FIRST and only then reads the id, so a
    /// migration missing that second attribute is not rejected, it is never enumerated. Nothing
    /// logs, because there is nothing to log about a type that was filtered out.</para>
    ///
    /// <para>So the assertion is now "EF sees every migration I compiled", which is the property
    /// that actually matters and the only one that would have caught it.</para>
    /// </summary>
    [Fact]
    public void EF_can_see_every_compiled_migration()
    {
        using var context = new ErpRfqAutomationContext(
            new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseNpgsql("Host=localhost;Database=discovery-only;Username=none;Password=none")
                .Options,
            new StubTenant(null));

        var seenByEf = context.GetService<IMigrationsAssembly>().Migrations.Keys
            .ToHashSet(StringComparer.Ordinal);

        var invisible = MigrationTypes()
            .Select(type => new
            {
                type.Name,
                Id = type.GetCustomAttribute<MigrationAttribute>()?.Id,
                HasContext = type.GetCustomAttribute<DbContextAttribute>()?.ContextType
                    == typeof(ErpRfqAutomationContext),
            })
            .Where(migration => migration.Id is null || !seenByEf.Contains(migration.Id))
            .Select(migration => migration.Id is null
                ? $"{migration.Name} (no [Migration] attribute)"
                : $"{migration.Name} (id {migration.Id}, "
                  + $"[DbContext] {(migration.HasContext ? "present" : "MISSING — this is the usual cause")})")
            .Order()
            .ToList();

        Assert.True(invisible.Count == 0,
            "EF cannot see these compiled migrations, so it will never apply them and will report "
            + "nothing pending: " + string.Join("; ", invisible));

        // Guards the inverse: a test that discovers nothing would pass the check above vacuously.
        Assert.NotEmpty(seenByEf);
    }

    /// <summary>
    /// Two migrations sharing an id is legal — EF orders by the full id string — but the order then
    /// falls out of a comparison of the NAME after the timestamp, which is not a decision anybody
    /// made and does not survive a rename. Renaming one to fix it is worse: the id is a primary key
    /// in <c>__EFMigrationsHistory</c>, so a rename invents a migration that has never been applied
    /// whose Up() runs against a schema that already has it. That happened on 2026-08-12 and had to
    /// be reverted. Catching the collision at the point it is introduced avoids both.
    /// </summary>
    /// <summary>
    /// The one collision that must NOT be fixed. Both of these are recorded in production's
    /// <c>__EFMigrationsHistory</c> under this exact timestamp, so renumbering either would invent
    /// a migration that has never been applied whose Up() runs against a schema that already has
    /// it — a 42P07 at boot on a service that migrates at startup. It is grandfathered, by id, so
    /// the guard still fires on any NEW pair.
    /// </summary>
    private static readonly HashSet<string> AppliedCollisions =
    [
        "20260812130000_PlatformBrowserTrustPolicy",
        "20260812130000_TenantSelfServicePasswordReset",
    ];

    [Fact]
    public void No_two_migrations_share_a_timestamp()
    {
        var collisions = MigrationTypes()
            .Where(type => !AppliedCollisions.Contains(
                type.GetCustomAttribute<MigrationAttribute>()?.Id ?? string.Empty))
            .Select(type => type.GetCustomAttribute<MigrationAttribute>()?.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .GroupBy(id => id!.Split('_')[0])
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(" and ", group.Order())}")
            .ToList();

        Assert.True(collisions.Count == 0,
            "Migrations written in parallel picked the same timestamp. Renumber the NEW one before "
            + "it is applied anywhere — never after: " + string.Join("; ", collisions));
    }
}
