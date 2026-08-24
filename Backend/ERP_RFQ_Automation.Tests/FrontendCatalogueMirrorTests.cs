using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Platform.Services;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The frontend restates two catalogues this assembly owns, and these tests are what stop the
/// restatement becoming a second source of truth.
///
/// <para><b>Why the check lives here and not in the frontend suite.</b> The authority should verify
/// its own copies — a mirror checked only by the mirror can drift on the day the mirror is edited.
/// It is also the only side that CAN: Vite refuses to read files outside the frontend project, so
/// a vitest spec cannot open a .cs at all.</para>
///
/// <para><b>Why the frontend needs a copy in the first place.</b> Neither catalogue is served by an
/// endpoint. The permissions matrix has to know which modules grant something before it can stop
/// rendering checkboxes for the nine that grant nothing, and the roles screen has to know what a
/// "Sales Representative" is before it can offer that setup as one click instead of 23
/// ticks. Both answers are compile-time facts about this assembly, so they are mirrored and pinned
/// rather than fetched.</para>
/// </summary>
public sealed class FrontendCatalogueMirrorTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Backend")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string FrontendSource(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot(), "Frontend", "src", "pages", "Setup", relativePath);
        Assert.True(File.Exists(path), $"{relativePath} is missing at {path}.");
        return File.ReadAllText(path);
    }

    // ── the module catalogue ─────────────────────────────────────────────────

    [Fact]
    public void The_frontend_enforced_module_list_matches_ModuleCatalog_exactly()
    {
        // A module MISSING from the frontend list is a real grant that can no longer be given: its
        // checkbox stops rendering and nobody can tick it. A module ADDED to the frontend list that
        // this assembly does not enforce is the original defect — a checkbox that grants nothing.
        // Both directions are failures, so the comparison is ordered equality, not containment.
        var source = FrontendSource("permissionModules.ts");

        var mirrored = Regex.Matches(source, @"\{\s*name:\s*'((?:[^'\\]|\\.)*)',\s*description:")
            .Select(match => Unescape(match.Groups[1].Value))
            .ToList();

        Assert.Equal(ModuleCatalog.All.Select(module => module.Name).ToList(), mirrored);
    }

    [Fact]
    public void The_frontend_carries_the_same_description_for_every_module()
    {
        // The descriptions are what the matrix shows a human. A stale one describes authority the
        // module no longer carries.
        var source = FrontendSource("permissionModules.ts");

        var mirrored = Regex.Matches(source, @"description:\s*'((?:[^'\\]|\\.)*)'\s*\}")
            .Select(match => Unescape(match.Groups[1].Value))
            .ToList();

        Assert.Equal(ModuleCatalog.All.Select(module => module.Description).ToList(), mirrored);
    }

    // ── the starter roles ────────────────────────────────────────────────────

    [Fact]
    public void The_frontend_presets_name_exactly_the_starter_roles_this_assembly_seeds()
    {
        var source = FrontendSource("rolePresets.ts");

        var mirrored = Regex.Matches(source, @"code:\s*""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            TenantBaselineCatalog.StarterRoles
                .Select(role => role.Code)
                .OrderBy(code => code, StringComparer.Ordinal)
                .ToList(),
            mirrored);
    }

    [Fact]
    public void Every_frontend_preset_carries_the_rank_and_grants_this_assembly_seeds()
    {
        // The grants are the whole value of a preset: "apply the standard Sales Representative
        // setup" has to produce the role this catalogue would have provisioned. A drifted mirror
        // would write a role that merely LOOKS standard, which is the failure mode nobody notices
        // until a quote cannot be sent.
        var source = FrontendSource("rolePresets.ts");

        foreach (var role in TenantBaselineCatalog.StarterRoles)
        {
            var block = PresetBlock(source, role.Code);

            var rank = Regex.Match(block, @"rank:\s*ROLE_RANK_(MEMBER|MANAGER|ADMIN|OWNER)");
            Assert.True(rank.Success, $"{role.Code} does not state a rank in rolePresets.ts.");
            Assert.Equal(RankName(role.Rank), rank.Groups[1].Value);

            var mirrored = Regex.Matches(
                    block,
                    @"\{\s*module:\s*""([^""]+)"",\s*canView:\s*(true|false),\s*canCreate:\s*(true|false),\s*canEdit:\s*(true|false),\s*canDelete:\s*(true|false)\s*\}")
                .Select(match => new TenantBaselineCatalog.ModuleGrant(
                    match.Groups[1].Value,
                    bool.Parse(match.Groups[2].Value),
                    bool.Parse(match.Groups[3].Value),
                    bool.Parse(match.Groups[4].Value),
                    bool.Parse(match.Groups[5].Value)))
                .ToList();

            Assert.Equal(role.Grants.ToList(), mirrored);
        }
    }

    [Fact]
    public void The_frontend_ladder_offers_three_rungs_and_never_the_owner()
    {
        // Three is the product decision this branch exists to deliver. A fourth rung is a decision
        // the customer must make on their first day for no benefit, and Owner in particular is
        // provisioned when the organization is created — offering it is how a live tenant ends up
        // with all six of its users holding the whole tenant plane.
        var source = FrontendSource("rolePresets.ts");

        var ladder = Regex.Match(
            source,
            @"ROLE_LADDER:\s*readonly\s+RolePreset\[\]\s*=\s*\[(.*?)\n\];",
            RegexOptions.Singleline);
        Assert.True(ladder.Success, "rolePresets.ts no longer declares ROLE_LADDER.");

        var codes = Regex.Matches(ladder.Groups[1].Value, @"code:\s*""([^""]+)""")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.Equal(["SYSTEM_ADMIN", "SALES_MANAGER", "SALES_REP"], codes);
        Assert.DoesNotContain("ROLE_RANK_OWNER", source);
    }

    /// <summary>The text of one preset object literal, from its code to the next code (or the end).</summary>
    private static string PresetBlock(string source, string code)
    {
        var start = source.IndexOf($"code: \"{code}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{code} is missing from rolePresets.ts.");

        var next = source.IndexOf("code: \"", start + 10, StringComparison.Ordinal);
        return next < 0 ? source[start..] : source[start..next];
    }

    private static string RankName(short rank) => rank switch
    {
        RoleRanks.Owner => "OWNER",
        RoleRanks.Admin => "ADMIN",
        RoleRanks.Manager => "MANAGER",
        _ => "MEMBER"
    };

    /// <summary>Undoes the single-quote escaping a TypeScript string literal uses.</summary>
    private static string Unescape(string value) =>
        value.Replace("\\'", "'").Replace("\\\\", "\\");
}
