using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The permission catalogue must match what the code actually enforces — in both directions.
///
/// <para><b>The failure this prevents.</b> <c>[RequireModulePermission("X", …)]</c> resolves by
/// matching a <c>Module</c> row on exact name, and <c>RolePermissionRepository</c> denies when
/// nothing matches. A gated endpoint whose module row was never inserted is therefore not merely
/// ungoverned — it is permanently forbidden to every non-super-admin, and the denial names no
/// cause. That is precisely how most of this product came to be usable only by a super admin:
/// modules were inserted ad hoc by whichever feature migration remembered to.</para>
///
/// <para>These tests are what make <see cref="ModuleCatalog"/> self-maintaining. A new gated
/// endpoint with an unseeded or misspelled module name fails here rather than shipping a screen
/// nobody can open.</para>
/// </summary>
public sealed class ModuleCatalogTests
{
    /// <summary>Every module name the assembly actually enforces, read from the attributes
    /// themselves rather than from a hand-maintained list — a list would have the same drift
    /// problem this file exists to eliminate.</summary>
    private static IReadOnlySet<string> EnforcedNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var type in typeof(UserController).Assembly.GetTypes())
        {
            foreach (var attribute in type.GetCustomAttributes<RequireModulePermissionAttribute>(inherit: true))
                names.Add(attribute.ModuleName);

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                                                   | BindingFlags.Instance | BindingFlags.Static
                                                   | BindingFlags.DeclaredOnly))
                foreach (var attribute in method.GetCustomAttributes<RequireModulePermissionAttribute>(inherit: true))
                    names.Add(attribute.ModuleName);
        }

        return names;
    }

    [Fact]
    public void Every_enforced_module_exists_in_the_catalogue()
    {
        // Missing here means: seeded nowhere, so permanently denied to every non-super-admin.
        var missing = EnforcedNames().Except(ModuleCatalog.Names).OrderBy(x => x).ToList();

        Assert.True(missing.Count == 0,
            "These module names are enforced by [RequireModulePermission] but are absent from " +
            "ModuleCatalog, so no tenant role can ever be granted them:\n  " +
            string.Join("\n  ", missing));
    }

    [Fact]
    public void Every_catalogue_entry_is_actually_enforced_somewhere()
    {
        // The opposite drift: a checkbox on the permissions matrix that grants and revokes
        // nothing. It is worse than an absent checkbox, because an administrator who unticks it
        // reasonably believes they have removed an access path.
        var unused = ModuleCatalog.Names.Except(EnforcedNames()).OrderBy(x => x).ToList();

        Assert.True(unused.Count == 0,
            "These modules are in ModuleCatalog but nothing enforces them, so they would render " +
            "as permission checkboxes that revoke nothing:\n  " + string.Join("\n  ", unused));
    }

    [Fact]
    public void Module_names_are_unique_and_free_of_case_or_whitespace_collisions()
    {
        // Module.ModuleName carries an exact-match unique index, and permission resolution
        // compares exactly. Two entries differing only by case or a trailing space would both
        // insert successfully and then behave as separate, half-working permissions.
        var collisions = ModuleCatalog.All
            .GroupBy(m => m.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(collisions);
        Assert.All(ModuleCatalog.All, m => Assert.Equal(m.Name.Trim(), m.Name));
    }

    [Fact]
    public void Every_module_carries_a_description_an_administrator_can_act_on()
    {
        // The description is the only thing on the permissions matrix explaining what a checkbox
        // does. "Leads" as both label and description tells an administrator nothing.
        Assert.All(ModuleCatalog.All, m =>
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Description));
            Assert.True(m.Description.Length is > 10 and <= 255,
                $"'{m.Name}' description must be meaningful and fit Module.Description (255).");
            Assert.NotEqual(m.Name, m.Description, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void The_modules_gating_the_mailbox_and_user_screens_are_present()
    {
        // Named explicitly because these are the two screens the product is unusable without,
        // and both were reachable only by a super admin before the catalogue existed.
        Assert.Contains("Email & SMTP", ModuleCatalog.Names);
        Assert.Contains("Users", ModuleCatalog.Names);
        Assert.Contains("Roles & Permissions", ModuleCatalog.Names);
    }
}
