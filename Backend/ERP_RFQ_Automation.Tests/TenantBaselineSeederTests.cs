using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services.Uom;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A provisioned tenant must be a workspace, not an empty shell wearing an "Active" badge.
///
/// <para><b>The state these tests end.</b> Provisioning created a Tenant, a BusinessUnit, the
/// lifecycle statuses, an AI policy, a SUPER_ADMIN role and one user. Nothing else. So the
/// customer's first morning was a Create Quote screen with an empty currency picker and an empty
/// unit picker, a PDF that printed "123 Business Rd, Tech City, 54321" as their address and the
/// RECIPIENT'S own email as the sender's, and a Roles screen on which the second role their
/// administrator created held nothing and had nothing to copy. None of that reported an error —
/// which is exactly why it survived.</para>
///
/// <para>The first test is the one that matters: it does not check that rows exist, it raises a
/// quote against them.</para>
/// </summary>
public sealed class TenantBaselineSeederTests
{
    private const long Bu = 9100;
    private const string Actor = "platform@nexora.app";

    private static readonly TenantBaselineProfile AcmeProfile = new(
        CountryCode: "SA",
        BaseCurrencyCode: "SAR",
        CompanyName: "Acme Industrial Trading LLC",
        CompanyAddress: "Unit 4, Second Industrial City, Dammam 34326",
        CompanyPhone: "+966 13 800 0199",
        CompanyEmail: "sales@acme-trading.example",
        LogoUrl: "https://cdn.acme-trading.example/logo.png",
        Locale: "en-GB");

    private static TenantBaselineSeeder Seeder(ErpRfqAutomationContext context) =>
        new(context, NullLogger<TenantBaselineSeeder>.Instance);

    /// <summary>
    /// Everything provisioning does BEFORE the baseline seeder is reached: the business unit and
    /// the lifecycle statuses, created exactly as <c>TenantsController.Provision</c> creates them.
    /// The founding SUPER_ADMIN is included because the seeder must never duplicate it.
    /// </summary>
    private static BusinessUnit ProvisionedBusinessUnit(
        ErpRfqAutomationContext context, long id = Bu, string code = "ACME-TRADING")
    {
        var businessUnit = new BusinessUnit
        {
            Id = id,
            BusinessUnitCode = code,
            BusinessUnitName = "Acme Industrial Trading",
            IsActive = true,
            CreatedBy = Actor,
            CreatedOn = DateTime.UtcNow
        };
        context.BusinessUnits.Add(businessUnit);
        context.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(businessUnit, Actor));
        context.SetupMasters.Add(new SetupMaster
        {
            SetupType = "Role",
            SetupCode = "SUPER_ADMIN",
            SetupValue = "Super Administrator",
            Description = "Founding administrator role created at tenant provisioning.",
            BusinessUnit = businessUnit,
            RoleRank = RoleRanks.Owner,
            IsActive = true,
            CreatedBy = Actor,
            CreatedOn = DateTime.UtcNow
        });
        context.SaveChanges();
        return businessUnit;
    }

    /// <summary>The Module rows ModuleCatalogReconciler writes at boot. Present in every test but
    /// the one that deliberately removes them.</summary>
    private static void ReconcileModuleCatalogue(ErpRfqAutomationContext context)
    {
        context.Modules.AddRange(ModuleCatalog.All.Select(definition => new Module
        {
            ModuleName = definition.Name,
            Description = definition.Description,
            IsActive = true,
            CreatedBy = ModuleCatalog.SeedActor,
            CreatedOn = DateTime.UtcNow
        }));
        context.SaveChanges();
    }

    [Fact]
    public async Task A_seeded_workspace_can_raise_a_quote_on_its_first_morning()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        var summary = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        // ---- the letterhead -------------------------------------------------------------
        // Without this row QuoteService.GenerateQuotePdfAsync prints its hardcoded placeholder
        // address and phone, and falls back to quote.Rfq.Lead.Clientemail for the company email —
        // putting the customer's own address on the quote as though it were the seller's.
        var quoteConfiguration = await context.QuoteConfigurations.IgnoreQueryFilters()
            .SingleAsync(configuration => configuration.BusinessUnitId == Bu);
        Assert.Equal("Unit 4, Second Industrial City, Dammam 34326", quoteConfiguration.CompanyAddress);
        Assert.Equal("+966 13 800 0199", quoteConfiguration.CompanyPhone);
        Assert.Equal("sales@acme-trading.example", quoteConfiguration.CompanyEmail);
        Assert.Equal("Acme Industrial Trading LLC", quoteConfiguration.FooterText);
        Assert.False(string.IsNullOrWhiteSpace(quoteConfiguration.TermsAndConditions));
        Assert.True(summary.QuoteConfigurationCreated);

        // ---- the base currency, resolved the way the product resolves it ------------------
        // Not "a Currency row exists" but "FxConversionService can name exactly one base
        // currency" — the question the dashboard total, product costing and the ledger all ask.
        var baseCurrencyId = await new FxConversionService(context).ResolveBaseCurrencyIdAsync(Bu);
        Assert.NotNull(baseCurrencyId);
        var baseCurrency = await context.Currencies.IgnoreQueryFilters()
            .SingleAsync(currency => currency.Id == baseCurrencyId);
        Assert.Equal("SAR", baseCurrency.Code);
        Assert.Equal("Saudi Riyal", baseCurrency.CurrencyName);
        Assert.True(baseCurrency.IsBaseCurrency);
        Assert.Equal("SAR", summary.BaseCurrencyCode);

        // Exactly one currency: a second, rate-less currency would make every cross-currency
        // total report itself unavailable the first time somebody quoted in it.
        Assert.Equal(1, await context.Currencies.IgnoreQueryFilters().CountAsync(c => c.BusinessUnitId == Bu));

        // ---- the unit of measure, resolved the way extraction resolves it -----------------
        // A customer document says "pcs"; the canonicaliser folds it to EA and then has to find
        // the tenant's own row to attach a foreign key to. With no rows the unit resolves to a
        // string pointing at nothing.
        var vocabulary = SetUomVocabulary.From(
            await context.SetUoms.IgnoreQueryFilters().Where(unit => unit.BusinessUnitId == Bu).ToListAsync());
        var unit = UomCanonicalizer.Canonicalize("10 pcs", vocabulary);
        Assert.Equal(UomResolution.Canonical, unit.Resolution);
        Assert.Equal("EA", unit.CanonicalCode);
        Assert.NotNull(unit.TenantUomId);
        Assert.Equal(TenantBaselineCatalog.UnitsOfMeasure.Count, summary.UnitsOfMeasureCreated);

        // ---- the country the tenant trades from -------------------------------------------
        // The code is the identity and is exact; the name is an editable label taken from ICU,
        // which falls back to the code on a host running globalization-invariant.
        var country = await context.SetCountries.IgnoreQueryFilters().SingleAsync(row => row.Buid == Bu);
        Assert.Equal("SA", country.CountryCode);
        Assert.Contains(country.CountryName, new[] { "Saudi Arabia", "SA" });

        // ---- and now actually raise the quote ---------------------------------------------
        var draftStatusId = await LifecycleStatusCatalog.ResolveIdAsync(context, Bu, "Quote", "DRAFT");
        var quote = new Quote
        {
            QuoteNo = "QT-000001",
            BusinessUnitId = Bu,
            CurrencyId = baseCurrencyId,
            StatusId = draftStatusId,
            QuoteDate = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            TotalAmount = 1250m,
            CreatedBy = "rep@acme-trading.example",
            CreatedDate = DateTime.UtcNow
        };
        quote.QuoteItems.Add(new QuoteItem
        {
            ItemDescription = "Gate valve, 6in, class 150",
            Quantity = 10m,
            UnitPrice = 125m,
            TotalAmount = 1250m,
            UnitOfMeasure = unit.CanonicalCode,
            CreatedBy = "rep@acme-trading.example",
            CreatedDate = DateTime.UtcNow
        });
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();

        await using var verify = db.ContextFor(Bu);
        var stored = await verify.Quotes.Include(q => q.Currency).Include(q => q.QuoteItems)
            .SingleAsync(q => q.QuoteNo == "QT-000001");
        Assert.Equal("SAR", stored.Currency!.Code);
        Assert.Equal("EA", Assert.Single(stored.QuoteItems).UnitOfMeasure);

        // And the tenant's quoted value is a number rather than "no single active base currency".
        var total = await new FxConversionService(verify).TotalAsync(
            Bu, [new FxAmount(stored.TotalAmount ?? 0m, stored.CurrencyId)], DateTime.UtcNow);
        Assert.True(total.Converted);
        Assert.Equal(1250m, total.Total);
        Assert.Equal("SAR", total.TargetCurrencyCode);
    }

    /// <summary>
    /// A business unit that reaches the seeder WITHOUT lifecycle statuses leaves it with them, and
    /// can then raise a quote.
    ///
    /// <para><b>The gap this closes.</b> The six <c>QuoteStatus</c> codes were reference data
    /// written once per business unit by whichever creation path ran — the
    /// <c>lifecycle-statuses</c> provisioning step, <c>TenantsController.Provision</c>, or
    /// <c>BusinessUnitRepository.AddAsync</c> — and by a one-time migration for the units that
    /// pre-dated them. Nothing checked afterwards: <c>ProvisioningStepReconciler</c> has no probe
    /// for that step, the squashed baseline's reference data seeds only <c>public."Module"</c>, and
    /// this seeder — the documented repair a support engineer re-runs — did not fill the gap. A
    /// business unit that lost that one step stayed unable to quote for the rest of its life.</para>
    ///
    /// <para>The test raises the quote the way the application does, through
    /// <c>LifecycleStatusCatalog.ResolveIdAsync</c>, because that is the call that threw
    /// "DRAFT is not configured and active for this tenant."</para>
    /// </summary>
    [Fact]
    public async Task A_business_unit_that_never_got_its_lifecycle_statuses_is_repaired_by_the_seeder()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);

        // Deliberately NOT ProvisionedBusinessUnit: this is the unit whose lifecycle-statuses step
        // never took effect, which is the whole point.
        context.BusinessUnits.Add(new BusinessUnit
        {
            Id = Bu,
            BusinessUnitCode = "ACME-TRADING",
            BusinessUnitName = "Acme Industrial Trading",
            IsActive = true,
            CreatedBy = Actor,
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        Assert.Empty(await context.SetupMasters.IgnoreQueryFilters()
            .Where(row => row.BusinessUnitId == Bu && row.SetupType == "QuoteStatus").ToListAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => LifecycleStatusCatalog.ResolveIdAsync(context, Bu, "Quote", "DRAFT"));

        var summary = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        // Every lifecycle row the catalogue defines, not only the quote ones: OrderService
        // resolves OrderStatus/DRAFT and PaymentStatus/UNPAID on the conversion that a won quote
        // becomes, and throws by name when either is absent.
        Assert.Equal(
            LifecycleStatusCatalog.CreateFor(new BusinessUnit { Id = Bu }, Actor).Count,
            summary.LifecycleStatusesCreated);

        await using var verify = db.ContextFor(Bu);
        Assert.Equal(
            ["ACCEPTED", "DRAFT", "EXPIRED", "ORDERED", "REJECTED", "SENT"],
            await verify.SetupMasters.Where(row => row.SetupType == "QuoteStatus")
                .Select(row => row.SetupCode).OrderBy(code => code).ToListAsync());

        // And the call that used to throw now names a row that belongs to THIS tenant.
        var draftStatusId = await LifecycleStatusCatalog.ResolveIdAsync(verify, Bu, "Quote", "DRAFT");
        var draft = await verify.SetupMasters.SingleAsync(row => row.SetupId == draftStatusId);
        Assert.Equal("QuoteStatus", draft.SetupType);
        Assert.Equal(Bu, draft.BusinessUnitId);
    }

    /// <summary>
    /// The other half of the same rule: a business unit that ALREADY has its lifecycle statuses is
    /// not given a second set. Duplicate rows would put every state twice in every picker and give
    /// <c>ResolveIdAsync</c> two rows to choose between.
    /// </summary>
    [Fact]
    public async Task Lifecycle_statuses_a_business_unit_already_has_are_never_duplicated()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);   // seeds the lifecycle statuses, as provisioning does

        var summary = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        Assert.Equal(0, summary.LifecycleStatusesCreated);

        await using var verify = db.ContextFor(Bu);
        Assert.Equal(6, await verify.SetupMasters.CountAsync(row => row.SetupType == "QuoteStatus"));
    }

    [Fact]
    public async Task The_sales_representative_holds_exactly_the_grants_intended_and_nothing_beyond()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        await using var verify = db.ContextFor(Bu);
        var role = await verify.SetupMasters.SingleAsync(row => row.SetupCode == "SALES_REP");

        // Member rank, deliberately. At RoleRanks.Admin or above PermissionHandler succeeds
        // before it reads a single row, so everything asserted below would be theatre.
        Assert.Equal(RoleRanks.Member, role.RoleRank);
        Assert.Equal("Role", role.SetupType);

        var held = await verify.RolePermissions
            .Where(grant => grant.RoleId == role.SetupId)
            .Select(grant => new
            {
                grant.Module.ModuleName,
                View = grant.CanView == true, Create = grant.CanCreate == true,
                Edit = grant.CanEdit == true, Delete = grant.CanDelete == true
            })
            .OrderBy(grant => grant.ModuleName)
            .ToListAsync();

        var expected = TenantBaselineCatalog.StarterRoles
            .Single(template => template.Code == "SALES_REP").Grants
            .OrderBy(grant => grant.Module, StringComparer.Ordinal)
            .Select(grant => (grant.Module, grant.CanView, grant.CanCreate, grant.CanEdit, grant.CanDelete))
            .ToList();

        Assert.Equal(
            expected,
            held.Select(grant => (grant.ModuleName, grant.View, grant.Create, grant.Edit, grant.Delete)).ToList());

        // The same answers through the code the authorization handler actually calls, because a
        // row is only a grant if RolePermissionRepository agrees it is one.
        var repository = new RolePermissionRepository(verify);
        Assert.True(await repository.CheckPermissionAsync(role.SetupId, "Quotations", "canview", Bu));
        Assert.True(await repository.CheckPermissionAsync(role.SetupId, "Quotations", "cancreate", Bu));
        Assert.True(await repository.CheckPermissionAsync(role.SetupId, "Quotations", "canedit", Bu));

        // Not delete: a quote is the evidence behind a commercial commitment.
        Assert.False(await repository.CheckPermissionAsync(role.SetupId, "Quotations", "candelete", Bu));

        // Read-only on the supply side, and completely absent from finance and administration —
        // a module with no row at all is denied outright for anything below Admin rank.
        Assert.True(await repository.CheckPermissionAsync(role.SetupId, "Suppliers", "canview", Bu));
        Assert.False(await repository.CheckPermissionAsync(role.SetupId, "Suppliers", "canedit", Bu));
        Assert.False(await repository.CheckPermissionAsync(role.SetupId, "Accounts Receivable", "canview", Bu));
        Assert.False(await repository.CheckPermissionAsync(role.SetupId, "Customer Payments", "cancreate", Bu));
        Assert.False(await repository.CheckPermissionAsync(role.SetupId, "Users", "canview", Bu));
        Assert.False(await repository.CheckPermissionAsync(role.SetupId, "Roles & Permissions", "canedit", Bu));
    }

    [Fact]
    public async Task The_sales_manager_can_administer_its_own_desk_and_no_other()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        await using var verify = db.ContextFor(Bu);
        var roles = await verify.SetupMasters.Where(SetupTypes.IsRoleRow)
            .ToDictionaryAsync(row => row.SetupCode!, row => row.SetupId);

        var gate = new RoleGate(verify, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));

        // RoleGate.CanManageRoleAsync refuses a target holding any grant the caller lacks, so a
        // manager who did not dominate their own representatives could not edit their access —
        // and every trivial change would escalate to the tenant owner.
        Assert.True(await gate.CanManageRoleAsync(roles["SALES_MANAGER"], roles["SALES_REP"], Bu));

        // Procurement and finance answer to the owner, not to sales.
        Assert.False(await gate.CanManageRoleAsync(roles["SALES_MANAGER"], roles["PROCUREMENT_OFFICER"], Bu));
        Assert.False(await gate.CanManageRoleAsync(roles["SALES_MANAGER"], roles["FINANCE_OFFICER"], Bu));

        // And nobody below Owner may administer the founding administrator.
        Assert.False(await gate.CanManageRoleAsync(roles["SALES_MANAGER"], roles["SUPER_ADMIN"], Bu));
        Assert.True(await gate.CanManageRoleAsync(roles["SUPER_ADMIN"], roles["FINANCE_OFFICER"], Bu));
    }

    [Fact]
    public async Task Re_running_against_a_partially_seeded_business_unit_completes_it_and_changes_nothing_else()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        // Provisioning retries: the execution strategy can call this more than once for one
        // tenant, and a support engineer can call it again months later.
        var first = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);
        Assert.Equal(1, first.CurrenciesCreated);
        Assert.Equal(4, first.RolesCreated);

        // Between the two runs the customer edits their terms and narrows a seeded role — the
        // exact state a re-run must not undo. Unticking every box writes an all-false row rather
        // than deleting it (the documented "Revoke All Access" behaviour), so the row still
        // exists and the seeder must leave it alone.
        var configuration = await context.QuoteConfigurations.IgnoreQueryFilters().SingleAsync();
        configuration.TermsAndConditions = "Payment strictly 60 days from bill of lading.";
        var representative = await context.SetupMasters.IgnoreQueryFilters()
            .SingleAsync(row => row.SetupCode == "SALES_REP");
        var revoked = await context.RolePermissions.IgnoreQueryFilters()
            .Include(grant => grant.Module)
            .FirstAsync(grant => grant.RoleId == representative.SetupId && grant.Module.ModuleName == "Customers");
        revoked.CanView = false;
        revoked.CanCreate = false;
        revoked.CanEdit = false;
        await context.SaveChangesAsync();

        var second = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        Assert.Equal(0, second.CurrenciesCreated);
        Assert.Equal(0, second.UnitsOfMeasureCreated);
        Assert.Equal(0, second.CountriesCreated);
        Assert.Equal(0, second.DiscountTypesCreated);
        Assert.Equal(0, second.RolesCreated);
        Assert.Equal(0, second.RolePermissionsCreated);
        Assert.False(second.QuoteConfigurationCreated);
        Assert.False(second.LeadReferenceConfigurationCreated);
        Assert.Equal("SAR", second.BaseCurrencyCode);

        await using var verify = db.ContextFor(Bu);
        Assert.Equal("Payment strictly 60 days from bill of lading.",
            (await verify.QuoteConfigurations.SingleAsync()).TermsAndConditions);
        Assert.False(await new RolePermissionRepository(verify)
            .CheckPermissionAsync(representative.SetupId, "Customers", "canview", Bu));

        // Nothing was duplicated either: one row per unit, one role per code.
        Assert.Equal(TenantBaselineCatalog.UnitsOfMeasure.Count, await verify.SetUoms.CountAsync());
        Assert.Equal(TenantBaselineCatalog.StarterRoles.Count + 1,
            await verify.SetupMasters.Where(SetupTypes.IsRoleRow).CountAsync());
    }

    [Fact]
    public async Task A_business_unit_that_already_has_a_base_currency_does_not_get_a_second_one()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        context.Currencies.Add(new Currency
        {
            BusinessUnitId = Bu, Code = "AED", CurrencyName = "UAE Dirham", Symbol = "AED",
            IsBaseCurrency = true, IsActive = true, CreatedBy = "customer", CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        // "Exactly one base currency" is an application rule that no database constraint enforces,
        // and every reader (FxConversionService, ProductRepository, CommercialLineResolution)
        // takes two rows and refuses to guess. A second base-flagged row would not error — it
        // would silently disable conversion for the whole tenant.
        await using var verify = db.ContextFor(Bu);
        Assert.Equal(1, await verify.Currencies.CountAsync(currency => currency.IsBaseCurrency == true));
        Assert.Equal("AED", (await verify.Currencies.SingleAsync(c => c.IsBaseCurrency == true)).Code);
        Assert.False((await verify.Currencies.SingleAsync(c => c.Code == "SAR")).IsBaseCurrency);
        Assert.NotNull(await new FxConversionService(verify).ResolveBaseCurrencyIdAsync(Bu));
    }

    [Fact]
    public async Task Provisioning_without_a_base_currency_is_refused_rather_than_half_completed()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        // Every consequence of a missing base currency is invisible at the point it bites: a total
        // that reports itself unavailable, a unit cost that quietly disappears, a ledger that
        // cannot be opened. Provisioning is the last moment anyone can still be told.
        var missing = await Assert.ThrowsAsync<ArgumentException>(
            () => Seeder(context).SeedAsync(Bu, AcmeProfile with { BaseCurrencyCode = null }, Actor));
        Assert.Equal(nameof(TenantBaselineProfile.BaseCurrencyCode), missing.ParamName);

        await Assert.ThrowsAsync<ArgumentException>(
            () => Seeder(context).SeedAsync(Bu, AcmeProfile with { BaseCurrencyCode = "Saudi Riyal" }, Actor));

        // It fails before anything is written, so a rejected profile leaves no partial workspace.
        await using var verify = db.ContextFor(Bu);
        Assert.Empty(await verify.Currencies.ToListAsync());
        Assert.Empty(await verify.SetUoms.ToListAsync());
        Assert.Empty(await verify.QuoteConfigurations.ToListAsync());

        // …and the code is normalised rather than stored as typed, because Currency.Code is what
        // every quote, order and FX rate joins on.
        await Seeder(context).SeedAsync(Bu, AcmeProfile with { BaseCurrencyCode = " sar " }, Actor);
        await using var normalised = db.ContextFor(Bu);
        Assert.Equal("SAR", (await normalised.Currencies.SingleAsync()).Code);
    }

    [Fact]
    public async Task The_lead_reference_prefix_is_the_tenants_own_before_the_first_lead_locks_it_in()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        var summary = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);
        Assert.True(summary.LeadReferenceConfigurationCreated);
        Assert.Equal("ACME-TRADING", summary.LeadReferencePrefix);

        // The reference is permanent — the PostgreSQL trigger and
        // LeadPersistenceRules.EnforceReferenceImmutability both refuse to change one — so the
        // prefix in force when the first lead arrives is that lead's identity forever. Left to the
        // lazy default, the customer's earliest cases would read "NXR-…", Nexora's own initials,
        // and correcting it afterwards would leave the tenant with two permanent families.
        var lead = Seed.Lead(context, leadId: 8001, businessUnitId: Bu);
        await context.SaveChangesAsync();

        Assert.StartsWith("ACME-TRADING-", lead.CommercialCaseReference);
        Assert.EndsWith("-000001", lead.CommercialCaseReference);
    }

    [Fact]
    public async Task Nothing_survives_a_rollback_of_the_transaction_that_called_it()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        // The seeder opens no transaction of its own and saves inside the caller's, so a
        // provisioning step that fails AFTER seeding takes the whole workspace with it. Anything
        // else would leave reference data behind for a tenant that does not exist.
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);
            Assert.NotEmpty(await context.Currencies.IgnoreQueryFilters().ToListAsync());
            await transaction.RollbackAsync();
        }

        context.ChangeTracker.Clear();
        await using var verify = db.ContextFor(Bu);
        Assert.Empty(await verify.Currencies.ToListAsync());
        Assert.Empty(await verify.SetUoms.ToListAsync());
        Assert.Empty(await verify.QuoteConfigurations.ToListAsync());
        Assert.Empty(await verify.SetupMasters.Where(SetupTypes.IsRoleRow)
            .Where(row => row.SetupCode != "SUPER_ADMIN").ToListAsync());
    }

    [Fact]
    public async Task A_missing_permission_module_is_created_rather_than_dropping_the_grant()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ProvisionedBusinessUnit(context);

        // ModuleCatalogReconciler runs only against PostgreSQL and swallows its own failures by
        // design, so a tenant can be provisioned against a database it has not reconciled. Without
        // the Module row the grant cannot be written at all, and a role below Admin rank with no
        // row is denied permanently, with no error naming the cause — the precise failure
        // ModuleCatalog exists to prevent.
        var summary = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        var required = TenantBaselineCatalog.StarterRoles
            .SelectMany(role => role.Grants).Select(grant => grant.Module)
            .Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(required, summary.ModulesCreated);
        Assert.Equal(
            TenantBaselineCatalog.StarterRoles.Sum(role => role.Grants.Count),
            summary.RolePermissionsCreated);

        await using var verify = db.ContextFor(Bu);
        var role = await verify.SetupMasters.SingleAsync(row => row.SetupCode == "FINANCE_OFFICER");
        Assert.True(await new RolePermissionRepository(verify)
            .CheckPermissionAsync(role.SetupId, "Bank Reconciliation", "canedit", Bu));

        // Only the modules the starter roles need; the rest of the catalogue stays
        // ModuleCatalogReconciler's business.
        Assert.Equal(required, await verify.Modules.CountAsync());
        Assert.All(await verify.Modules.ToListAsync(),
            module => Assert.Equal(TenantBaselineSeeder.ModuleSeedActor, module.CreatedBy));
    }

    [Fact]
    public async Task Seeding_one_business_unit_leaves_its_neighbour_untouched()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);
        ProvisionedBusinessUnit(context, id: 9200, code: "BETA-SUPPLY");

        await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        // Every row this seeder writes is tenant-owned, and a reference row leaking into the wrong
        // business unit is a currency or a role appearing in a customer's workspace unbidden.
        await using var neighbour = db.ContextFor(9200);
        Assert.Empty(await neighbour.Currencies.ToListAsync());
        Assert.Empty(await neighbour.SetUoms.ToListAsync());
        Assert.Empty(await neighbour.SetCountries.ToListAsync());
        Assert.Empty(await neighbour.QuoteConfigurations.ToListAsync());
        Assert.Empty(await neighbour.RolePermissions.ToListAsync());
        Assert.Equal("SUPER_ADMIN",
            (await neighbour.SetupMasters.Where(SetupTypes.IsRoleRow).SingleAsync()).SetupCode);
    }

    [Fact]
    public async Task The_summary_is_evidence_the_operator_can_read()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        ReconcileModuleCatalogue(context);
        ProvisionedBusinessUnit(context);

        var summary = await Seeder(context).SeedAsync(Bu, AcmeProfile, Actor);

        Assert.Equal(Bu, summary.BusinessUnitId);
        Assert.Equal(
            ["Sales Manager", "Sales Representative", "Procurement Officer", "Finance Officer"],
            summary.RoleNames);
        Assert.All(summary.Roles, role => Assert.True(role.Grants > 0));
        Assert.Equal(TenantBaselineCatalog.DiscountTypes.Count, summary.DiscountTypesCreated);

        // The counts are rows, not intentions.
        await using var verify = db.ContextFor(Bu);
        Assert.Equal(summary.UnitsOfMeasureCreated, await verify.SetUoms.CountAsync());
        Assert.Equal(summary.RolePermissionsCreated, await verify.RolePermissions.CountAsync());
        Assert.Equal(summary.DiscountTypesCreated,
            await verify.SetupMasters.CountAsync(row => row.SetupType == "DiscountType"));

        // Percentage and fixed, the only two codes QuoteService.CalculateQuoteTotals can act on.
        Assert.Equal(["FIXED", "PERCENTAGE"],
            await verify.SetupMasters.Where(row => row.SetupType == "DiscountType")
                .Select(row => row.SetupCode).OrderBy(code => code).ToListAsync());
    }
}
