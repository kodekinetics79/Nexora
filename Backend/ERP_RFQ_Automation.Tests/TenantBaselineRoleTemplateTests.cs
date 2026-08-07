using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Services.Uom;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Properties of the starter catalogue itself, asserted without a database.
///
/// <para>These are the invariants that make the seeded workspace SAFE rather than merely
/// populated. A grant naming a module that does not exist writes no row and therefore denies
/// silently; a role at <see cref="RoleRanks.Admin"/> holds the tenant no matter what its
/// permission matrix displays; a unit code the canonicaliser does not emit is a row nothing will
/// ever resolve to. Each of those is invisible in a running system and obvious here.</para>
/// </summary>
public sealed class TenantBaselineRoleTemplateTests
{
    private static IEnumerable<(TenantBaselineCatalog.StarterRole Role, TenantBaselineCatalog.ModuleGrant Grant)>
        AllGrants() => TenantBaselineCatalog.StarterRoles.SelectMany(
            role => role.Grants.Select(grant => (role, grant)));

    private static TenantBaselineCatalog.StarterRole Role(string code) =>
        TenantBaselineCatalog.StarterRoles.Single(role => role.Code == code);

    [Fact]
    public void Every_module_a_starter_role_grants_is_a_module_the_product_enforces()
    {
        // RolePermissions.ModuleId is a foreign key to Module, and ModuleCatalog is the only list
        // guaranteed to exist there. A typo'd module name would not fail the seed — it would drop
        // the grant, leaving a role that is denied a screen for a reason nothing reports.
        var unknown = AllGrants()
            .Select(pair => pair.Grant.Module)
            .Distinct(StringComparer.Ordinal)
            .Where(module => !ModuleCatalog.Names.Contains(module))
            .OrderBy(module => module, StringComparer.Ordinal)
            .ToList();

        Assert.True(unknown.Count == 0,
            "Starter roles grant these modules, which are not in ModuleCatalog and therefore cannot " +
            "be written as RolePermissions rows:\n  " + string.Join("\n  ", unknown));
    }

    [Fact]
    public void No_starter_role_sits_at_a_rank_that_would_bypass_its_own_permission_matrix()
    {
        // PermissionHandler succeeds on rank >= Admin before consulting a single RolePermissions
        // row. A starter role at that tier would hold everything while displaying a curated set of
        // ticked boxes — a permissions screen that states the opposite of what is enforced.
        Assert.All(TenantBaselineCatalog.StarterRoles, role =>
        {
            Assert.True(RoleRanks.IsDefined(role.Rank), $"{role.Code} has an unstorable rank.");
            Assert.True(role.Rank < RoleRanks.Admin,
                $"{role.Code} sits at {RoleRanks.Describe(role.Rank)}, which satisfies every module " +
                "check by rank and makes its grants decorative.");
        });
    }

    [Fact]
    public void Every_grant_states_read_access_and_actually_grants_something()
    {
        // Since RC-3/RC-4 the existence of a row is not the read grant. A row with CanView false
        // and nothing else set is a row that grants nothing at all while occupying the matrix.
        Assert.All(AllGrants(), pair =>
        {
            Assert.True(pair.Grant.CanView,
                $"{pair.Role.Code} holds a row on '{pair.Grant.Module}' without read access; " +
                "write without view is not a state any screen can be used in.");
            Assert.True(
                pair.Grant.CanView || pair.Grant.CanCreate || pair.Grant.CanEdit || pair.Grant.CanDelete,
                $"{pair.Role.Code} holds an all-false row on '{pair.Grant.Module}'.");
        });
    }

    [Fact]
    public void A_grant_names_each_module_once_per_role()
    {
        // RolePermissionRepository resolves a permission with AnyAsync over (role, module), so a
        // second row for the same pair would union its flags — the narrower of the two would look
        // enforced on the matrix and grant nothing in practice.
        Assert.All(TenantBaselineCatalog.StarterRoles, role =>
        {
            var duplicates = role.Grants.GroupBy(grant => grant.Module, StringComparer.Ordinal)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToList();
            Assert.True(duplicates.Count == 0, $"{role.Code} names {string.Join(", ", duplicates)} twice.");
        });
    }

    [Fact]
    public void The_sales_desk_holds_no_finance_write_and_no_administration()
    {
        // The coherence rule. A sales role that can raise a credit note or mint a user is not a
        // sales role, and a starter set that ships one teaches the customer that the permission
        // model is advisory.
        var financeAndAdministration = new[]
        {
            "Accounts Receivable", "Customer Payments", "Customer Refunds", "Customer Statements",
            "Receivable Adjustments", "Receivable Write-offs", "Collection Controls",
            "Dunning Cases", "Dunning Notices", "Dunning Policies",
            "Bank Accounts", "Bank Statement Import", "Bank Reconciliation",
            "Bank Reconciliation Approval", "Bank Adjustments", "Bank Adjustment Approval",
            "Bank Matching Rule Administration", "Bank Matching Rule Approval",
            "General Ledger", "General Ledger Posting", "Ledger Control",
            "Accounting Periods", "Period Close",
            "Users", "Roles & Permissions", "Business Units", "Email & SMTP"
        }.ToHashSet(StringComparer.Ordinal);

        foreach (var code in new[] { "SALES_MANAGER", "SALES_REP" })
        {
            var offending = Role(code).Grants
                .Where(grant => financeAndAdministration.Contains(grant.Module))
                .Where(grant => grant.CanCreate || grant.CanEdit || grant.CanDelete)
                .Select(grant => grant.Module)
                .ToList();
            Assert.True(offending.Count == 0,
                $"{code} holds write on {string.Join(", ", offending)}.");
        }

        // The sales representative may not so much as READ the finance or administration surface;
        // the manager is allowed the three read-only receivable views that tell them whether a
        // customer is safe to quote, and the user list that lead assignment picks from.
        var representativeReads = Role("SALES_REP").Grants
            .Where(grant => financeAndAdministration.Contains(grant.Module))
            .Select(grant => grant.Module)
            .ToList();
        Assert.Empty(representativeReads);

        var managerReads = Role("SALES_MANAGER").Grants
            .Where(grant => financeAndAdministration.Contains(grant.Module))
            .Select(grant => grant.Module)
            .OrderBy(module => module, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ["Accounts Receivable", "Collection Controls", "Customer Statements", "Users"],
            managerReads);
    }

    [Fact]
    public void Neither_procurement_nor_finance_can_touch_the_customer_facing_pipeline()
    {
        // The mirror of the rule above: quoting a customer is the sales desk's authority.
        foreach (var code in new[] { "PROCUREMENT_OFFICER", "FINANCE_OFFICER" })
        {
            var pipeline = Role(code).Grants
                .Where(grant => grant.Module is "Leads" or "Quotations" or "Customer Awards")
                .ToList();
            Assert.True(pipeline.Count == 0,
                $"{code} holds {string.Join(", ", pipeline.Select(grant => grant.Module))} on the " +
                "customer-facing pipeline.");
        }

        // Finance reads customers and orders to work a receivable, and writes to neither.
        Assert.All(
            Role("FINANCE_OFFICER").Grants.Where(grant => grant.Module is "Customers" or "Orders"),
            grant =>
            {
                Assert.True(grant.CanView);
                Assert.False(grant.CanCreate || grant.CanEdit || grant.CanDelete);
            });
    }

    [Fact]
    public void Finance_prepares_but_never_approves()
    {
        // Segregation of duties. Every module below is the approval half of a control whose
        // preparation half the finance officer holds; granting both at provisioning would remove
        // the second signature from a tenant's very first reconciliation.
        var approvals = new[]
        {
            "Bank Reconciliation Approval", "Bank Adjustment Approval",
            "Bank Matching Rule Administration", "Bank Matching Rule Approval",
            "General Ledger Posting", "Ledger Control", "Period Close"
        };

        var held = Role("FINANCE_OFFICER").Grants.Select(grant => grant.Module).ToHashSet(StringComparer.Ordinal);
        Assert.All(approvals, module => Assert.DoesNotContain(module, held));

        // …and the preparation half is genuinely held, so the role is a job rather than a gesture.
        Assert.Contains("Bank Reconciliation", held);
        Assert.Contains("Accounts Receivable", held);
    }

    [Fact]
    public void The_sales_manager_dominates_the_sales_representative()
    {
        // RoleGate.CanManageRoleAsync refuses a target holding any grant the caller lacks. If the
        // manager did not dominate the representative, the manager could not edit their own team's
        // role — and nothing below Owner could, so the tenant owner would be the only person able
        // to adjust a sales rep's access.
        var manager = Role("SALES_MANAGER").Grants.ToDictionary(grant => grant.Module, StringComparer.Ordinal);

        Assert.All(Role("SALES_REP").Grants, grant =>
        {
            Assert.True(manager.TryGetValue(grant.Module, out var held),
                $"The sales representative holds '{grant.Module}' and the manager holds no row for it.");
            Assert.True(!grant.CanView || held!.CanView, $"view on {grant.Module}");
            Assert.True(!grant.CanCreate || held!.CanCreate, $"create on {grant.Module}");
            Assert.True(!grant.CanEdit || held!.CanEdit, $"edit on {grant.Module}");
            Assert.True(!grant.CanDelete || held!.CanDelete, $"delete on {grant.Module}");
        });

        Assert.True(Role("SALES_MANAGER").Rank > Role("SALES_REP").Rank);
    }

    [Fact]
    public void Role_codes_are_unique_and_never_collide_with_the_founding_super_admin()
    {
        // Provisioning creates SUPER_ADMIN itself, at Owner rank, before this seeder runs. A
        // second row with that code would give the tenant two "Super Administrator" roles, one of
        // which is not one.
        var codes = TenantBaselineCatalog.StarterRoles.Select(role => role.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("SUPER_ADMIN", codes, StringComparer.OrdinalIgnoreCase);

        var names = TenantBaselineCatalog.StarterRoles.Select(role => role.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain("Super Administrator", names, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Seeded_units_are_exactly_the_codes_the_canonicaliser_resolves_to()
    {
        // The correspondence that makes the tenant's UoM table useful to extraction: every seeded
        // code must round-trip through UomCanonicalizer as itself. A code that canonicalises to
        // something ELSE (say "PCS" -> "EA") would sit in the list forever while every extracted
        // line pointed at the row for its canonical twin, and a code the canonicaliser REFUSES
        // (PACK, PALLET, TON) would legitimise a quantity no document ever stated.
        Assert.All(TenantBaselineCatalog.UnitsOfMeasure, unit =>
        {
            var resolution = UomCanonicalizer.Canonicalize(unit.Code);
            Assert.Equal(UomResolution.Canonical, resolution.Resolution);
            Assert.Equal(unit.Code, resolution.CanonicalCode);
        });

        var codes = TenantBaselineCatalog.UnitsOfMeasure.Select(unit => unit.Code).ToList();
        Assert.Equal(codes.Count, codes.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The count unit the overwhelming majority of trading lines are stated in has to be there,
        // whatever else is trimmed from the list.
        Assert.Contains("EA", codes);
    }

    [Fact]
    public void Every_unit_carries_a_name_and_description_that_fit_their_columns()
    {
        Assert.All(TenantBaselineCatalog.UnitsOfMeasure, unit =>
        {
            Assert.InRange(unit.Code.Length, 1, 50);
            Assert.InRange(unit.Name.Length, 1, 100);
            Assert.InRange(unit.Description.Length, 11, 255);
            Assert.NotEqual(unit.Code, unit.Description, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void An_unlisted_currency_code_is_accepted_rather_than_blocking_a_provision()
    {
        var known = TenantBaselineCatalog.ResolveCurrency("SAR");
        Assert.Equal("Saudi Riyal", known.Name);

        // A customer trading in a currency the table has not listed yet must still be able to
        // open a workspace; the code stands in for the name until an administrator fills it in.
        var unlisted = TenantBaselineCatalog.ResolveCurrency("XOF");
        Assert.Equal("XOF", unlisted.Code);
        Assert.Equal("XOF", unlisted.Name);
        Assert.Equal("XOF", unlisted.Symbol);
    }
}
