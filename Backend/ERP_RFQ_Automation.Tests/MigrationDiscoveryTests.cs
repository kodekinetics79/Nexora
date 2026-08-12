using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;

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

    [Fact]
    public void Every_compiled_migration_is_discoverable_by_EF()
    {
        var undiscoverable = MigrationTypes()
            .Where(type => type.GetCustomAttribute<MigrationAttribute>() is null)
            .Select(type => type.Name)
            .Order()
            .ToList();

        Assert.True(undiscoverable.Count == 0,
            "These migration types carry no [Migration] attribute, so EF will never apply them: "
            + string.Join(", ", undiscoverable));
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
