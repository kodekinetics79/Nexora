using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Services;

/// <summary>
/// The company facts a new workspace is opened with, taken from the tenant record the operator
/// filled in. Everything except <see cref="BaseCurrencyCode"/> is optional: a missing address
/// leaves a field blank on the quote template, which an administrator can see and fix, whereas a
/// missing base currency has no visible symptom at all (see <see cref="ITenantBaselineSeeder"/>).
/// </summary>
/// <param name="CountryCode">ISO-3166-1 alpha-2 country of incorporation, e.g. "SA".</param>
/// <param name="BaseCurrencyCode">ISO-4217 code of the tenant's functional currency, e.g. "SAR".</param>
/// <param name="CompanyName">Legal entity name, used for the quote footer when one is not supplied.</param>
/// <param name="CompanyAddress">Registered address as it should print on commercial documents.</param>
/// <param name="CompanyPhone">Switchboard number as it should print on commercial documents.</param>
/// <param name="CompanyEmail">General company address (sales@ / info@), NOT the founding admin's.</param>
/// <param name="LogoUrl">Absolute URL or data URI of the customer's logo.</param>
/// <param name="Locale">BCP-47 locale, e.g. "en-GB". Recorded for completeness; nothing seeded
/// here varies by locale, because reference data a customer will edit must not depend on which
/// node provisioned them.</param>
public sealed record TenantBaselineProfile(
    string? CountryCode = null,
    string? BaseCurrencyCode = null,
    string? CompanyName = null,
    string? CompanyAddress = null,
    string? CompanyPhone = null,
    string? CompanyEmail = null,
    string? LogoUrl = null,
    string? Locale = null);

/// <summary>One seeded role, as the provisioning API reports it back to the operator.</summary>
/// <param name="Grants">RolePermissions rows written for this role on THIS run. Zero on a
/// re-run against an already-seeded business unit — the seeder never rewrites a grant an
/// administrator may since have edited.</param>
public sealed record TenantBaselineRoleSummary(string Code, string Name, short Rank, int Grants);

/// <summary>
/// What one seeding pass actually created. Returned to the operator as evidence that the
/// workspace behind a green "Active" badge is real: every count here is a row that now exists,
/// and a zero is a statement that the row was already there, not that it was skipped.
/// </summary>
public sealed record TenantBaselineSummary(
    long BusinessUnitId,
    string BaseCurrencyCode,
    int CurrenciesCreated,
    int UnitsOfMeasureCreated,
    int CountriesCreated,
    int DiscountTypesCreated,
    int LifecycleStatusesCreated,
    int ReferenceListRowsCreated,
    int ModulesCreated,
    int RolesCreated,
    int RolePermissionsCreated,
    bool QuoteConfigurationCreated,
    bool LeadReferenceConfigurationCreated,
    string LeadReferencePrefix,
    IReadOnlyList<TenantBaselineRoleSummary> Roles)
{
    /// <summary>The starter role names, for the provisioning response and the audit entry.</summary>
    public IReadOnlyList<string> RoleNames => Roles.Select(role => role.Name).ToList();
}

/// <summary>
/// Turns a bare <c>BusinessUnit</c> into a workspace a customer can use on their first morning.
///
/// <para><b>The state this exists to end.</b> Provisioning created a Tenant, one BusinessUnit,
/// the lifecycle statuses, an AI policy, a SUPER_ADMIN role and one user — and stopped. Everything
/// a commercial screen reads was therefore empty, and empty is not how any of it fails:
/// </para>
/// <list type="bullet">
/// <item>No <c>QuoteConfiguration</c>, so <c>QuoteService.GenerateQuotePdfAsync</c> falls back to
/// its hardcoded placeholders and the customer's first quote goes out reading
/// "123 Business Rd, Tech City, 54321" and "+1 800 555 0199" — and, worse, its company email
/// falls back to <c>quote.Rfq.Lead.Clientemail</c>, printing the RECIPIENT'S OWN address as the
/// sender's.</item>
/// <item>No base <c>Currency</c>, so <c>FxConversionService.ResolveBaseCurrencyIdAsync</c> returns
/// null: the dashboard's quoted value reports "no single active base currency", product match
/// suggestions silently drop their unit cost (<c>ProductRepository</c>), and
/// <c>GeneralLedgerService</c> refuses to open an accounting book at all.</item>
/// <item>No <c>SetUom</c>, so every unit an extracted customer document states resolves to a
/// canonical string with a null foreign key, and the unit pickers open blank.</item>
/// <item>No <c>RolePermission</c> template, so the second role the customer's Super Administrator
/// creates starts with zero grants and nothing to copy — and a role below
/// <see cref="RoleRanks.Admin"/> with no rows is denied everything, with no error naming why.</item>
/// <item>No lifecycle <c>Setup_Master</c> rows, so the tenant has no <c>QuoteStatus</c>,
/// <c>OrderStatus</c> or <c>PaymentStatus</c> to point at. See
/// <see cref="TenantBaselineSeeder.EnsureLifecycleStatusesAsync"/> for what that costs — it is the
/// difference between a workspace that can raise its first quote and one that cannot.</item>
/// </list>
///
/// <para><b>Transaction contract.</b> This runs INSIDE the caller's transaction and on the
/// caller's <c>DbContext</c>. It opens no transaction and holds no state between calls: every
/// existence check re-reads the database, so an execution-strategy retry that cleared the change
/// tracker and rolled its attempt back simply seeds again from nothing. It calls
/// <c>SaveChanges</c> exactly once, at the end, which enlists in the ambient transaction and is
/// therefore rolled back with everything else if a later step of provisioning fails.</para>
///
/// <para><b>Idempotent by check-then-insert, never by overwrite.</b> Re-running against a
/// partially seeded business unit fills the gaps and touches nothing that already exists. That
/// asymmetry is deliberate: a re-run may happen long after go-live (a repair, a retry, a support
/// action), and by then every one of these rows may have been edited by the customer. Restoring a
/// "default" over an administrator's terms and conditions or over a role they narrowed would be a
/// data loss with no audit trail.</para>
/// </summary>
public interface ITenantBaselineSeeder
{
    /// <summary>
    /// Seeds the baseline for <paramref name="businessUnitId"/> and reports what it created.
    /// </summary>
    /// <param name="actor">Stamped into every <c>CreatedBy</c>, so a support engineer can tell
    /// provisioning-seeded reference data from anything the customer added.</param>
    /// <exception cref="ArgumentException">
    /// <see cref="TenantBaselineProfile.BaseCurrencyCode"/> is absent or is not a three-letter
    /// alphabetic ISO-4217 code. This is the one profile field the seeder refuses to proceed
    /// without: every consequence of a missing base currency is invisible (a total that reports
    /// itself unavailable, a unit cost that silently disappears, a ledger that cannot be opened),
    /// so provisioning is the last moment at which anyone can still be told. Validate it at the
    /// API boundary so the operator gets a 400 naming the field rather than a rolled-back
    /// transaction.
    /// </exception>
    /// <exception cref="InvalidOperationException">The business unit does not exist.</exception>
    Task<TenantBaselineSummary> SeedAsync(
        long businessUnitId, TenantBaselineProfile profile, string actor, CancellationToken ct = default);

    /// <summary>
    /// Adds every <see cref="TenantBaselineCatalog.ReferenceLists"/> list the business unit has
    /// NO rows of, and saves. Idempotent: a second call creates nothing. A list the tenant already
    /// holds — in any shape — is never touched, so this is safe to run against a live tenant at any
    /// point in its life; it is what the startup reconciler runs for every business unit.
    /// </summary>
    /// <returns>The number of Setup_Master rows written.</returns>
    Task<int> ReconcileReferenceListsAsync(long businessUnitId, string actor, CancellationToken ct = default);
}

public sealed class TenantBaselineSeeder(
    ErpRfqAutomationContext context, ILogger<TenantBaselineSeeder> logger) : ITenantBaselineSeeder
{
    /// <summary>
    /// Stamped on <c>Module</c> rows this seeder has to insert itself, so they are
    /// distinguishable from the ones <c>ModuleCatalogReconciler</c> writes at boot
    /// (<see cref="ModuleCatalog.SeedActor"/>). A row bearing this actor is evidence that
    /// reconciliation had not run when the tenant was created.
    /// </summary>
    public const string ModuleSeedActor = "provisioning:tenant-baseline:v1";

    /// <summary>
    /// Default terms printed on a quote until the customer replaces them. Deliberately the same
    /// seven clauses <c>QuoteService.GenerateQuotePdfAsync</c> already falls back to, so seeding
    /// changes WHERE the terms come from (a row the customer owns and can edit) without changing
    /// what the first quote says.
    /// </summary>
    private const string DefaultTermsAndConditions =
        "1. Prices are valid for 30 days from the date of the quote.\n" +
        "2. Payment terms: Net 30 days from invoice date.\n" +
        "3. Delivery dates are estimates and subject to confirmation.\n" +
        "4. All products remain the property of the seller until fully paid.\n" +
        "5. Any applicable taxes or duties are not included unless specified.\n" +
        "6. Warranty and liability are as per the manufacturer's standard terms.\n" +
        "7. This quote is confidential and intended solely for the recipient.";

    /// <summary>The quote accent colour used when the customer has not supplied a brand colour —
    /// the same value the PDF writer already defaults to, so nothing changes visually.</summary>
    private const string DefaultPrimaryColor = "#1e3a8a";

    public async Task<TenantBaselineSummary> SeedAsync(
        long businessUnitId, TenantBaselineProfile profile, string actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var seededBy = string.IsNullOrWhiteSpace(actor) ? "provisioning" : actor.Trim();
        var currency = TenantBaselineCatalog.ResolveCurrency(NormalizeCurrencyCode(profile.BaseCurrencyCode));
        var now = DateTime.UtcNow;

        // The business unit is read rather than passed as an entity: the caller's execution
        // strategy discards its object graph between attempts, so an entity handed in by one
        // attempt would be a detached ghost in the next. Its code is also what makes the lead
        // reference prefix meaningful.
        var businessUnit = await context.BusinessUnits
            .FirstOrDefaultAsync(unit => unit.Id == businessUnitId, ct)
            ?? throw new InvalidOperationException(
                $"Business unit {businessUnitId} does not exist; the baseline cannot be seeded for it.");

        var quoteConfiguration = await EnsureQuoteConfigurationAsync(businessUnitId, profile, seededBy, now, ct);
        var currencies = await EnsureBaseCurrencyAsync(businessUnitId, currency, seededBy, now, ct);
        var units = await EnsureUnitsOfMeasureAsync(businessUnitId, seededBy, now, ct);
        var countries = await EnsureCountryAsync(businessUnitId, profile.CountryCode, seededBy, now, ct);
        var discountTypes = await EnsureDiscountTypesAsync(businessUnitId, seededBy, now, ct);
        var lifecycleStatuses = await EnsureLifecycleStatusesAsync(businessUnit, seededBy, now, ct);
        var referenceRows = await EnsureReferenceListsAsync(businessUnitId, seededBy, now, ct);
        var (leadReferenceSeeded, leadReferencePrefix) =
            await EnsureLeadReferenceConfigurationAsync(businessUnit, now, ct);
        var roles = await EnsureStarterRolesAsync(businessUnitId, seededBy, now, ct);

        // One SaveChanges, inside the caller's transaction. Everything above is expressed through
        // navigation properties rather than foreign-key ids precisely so that a single flush can
        // order the inserts itself — a role and the RolePermissions rows pointing at it are added
        // together and EF resolves the generated key between them.
        await context.SaveChangesAsync(ct);

        var summary = new TenantBaselineSummary(
            businessUnitId,
            currency.Code,
            currencies,
            units,
            countries,
            discountTypes,
            lifecycleStatuses,
            referenceRows,
            roles.ModulesCreated,
            roles.RolesCreated,
            roles.GrantsCreated,
            quoteConfiguration,
            leadReferenceSeeded,
            leadReferencePrefix,
            roles.Roles);

        logger.LogInformation(
            "Seeded tenant baseline for business unit {BusinessUnitId}: base currency {Currency}, "
            + "{Units} unit(s) of measure, {Countries} country row(s), {LifecycleStatuses} lifecycle "
            + "status row(s), {ReferenceRows} reference-list row(s), {Roles} starter role(s) holding "
            + "{Grants} permission grant(s).",
            businessUnitId, currency.Code, units, countries, lifecycleStatuses, referenceRows,
            roles.RolesCreated, roles.GrantsCreated);

        return summary;
    }

    /// <summary>
    /// ISO-4217 is three ASCII letters. The check is here and not only at the API boundary because
    /// this value becomes <c>Currency.Code</c>, which every quote, order and FX rate joins on — a
    /// stray "SAR " or "sar" would create a second, non-matching currency the first time somebody
    /// typed it properly.
    /// </summary>
    private static string NormalizeCurrencyCode(string? code)
    {
        var trimmed = code?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length != 3 || !trimmed.All(char.IsAsciiLetter))
            throw new ArgumentException(
                "A three-letter ISO-4217 base currency code is required to open a workspace. Without a "
                + "base currency the business unit has no functional currency: cross-currency totals "
                + "report themselves unavailable, product unit costs are suppressed, and no accounting "
                + "book can be created — none of which surfaces as an error the customer can act on.",
                nameof(TenantBaselineProfile.BaseCurrencyCode));
        return trimmed;
    }

    /// <summary>
    /// The letterhead. <c>QuoteConfiguration</c> has a unique index on BusinessUnitId, so this is
    /// the one row per business unit that <c>QuoteService</c> reads before printing anything.
    /// </summary>
    private async Task<bool> EnsureQuoteConfigurationAsync(
        long businessUnitId, TenantBaselineProfile profile, string actor, DateTime now, CancellationToken ct)
    {
        // IgnoreQueryFilters throughout this class. Provisioning runs on the platform plane, where
        // the tenant filter is a no-op, but the seeder must also be correct if it is ever called
        // from a context scoped to a DIFFERENT business unit: there, a filtered existence check
        // would answer "absent" for a row that exists and the insert would violate the unique
        // index — turning a safe re-run into a failed transaction.
        if (await context.QuoteConfigurations.IgnoreQueryFilters()
                .AnyAsync(configuration => configuration.BusinessUnitId == businessUnitId, ct))
            return false;

        context.QuoteConfigurations.Add(new QuoteConfiguration
        {
            BusinessUnitId = businessUnitId,
            Logo = Trimmed(profile.LogoUrl),
            PrimaryColor = DefaultPrimaryColor,
            TermsAndConditions = DefaultTermsAndConditions,
            CompanyAddress = Trimmed(profile.CompanyAddress),
            CompanyPhone = Trimmed(profile.CompanyPhone),

            // Left NULL rather than defaulted when the operator did not supply one. The PDF writer
            // then falls back, and a blank sender line is a visible omission somebody will fix,
            // whereas inventing "sales@..." would print an address that does not receive mail.
            CompanyEmail = Trimmed(profile.CompanyEmail),
            FooterText = Trimmed(profile.CompanyName),
            ModifiedBy = actor,
            ModifiedOn = now
        });
        return true;
    }

    /// <summary>
    /// Exactly one currency row: the tenant's own.
    ///
    /// <para>Companion currencies are deliberately NOT seeded. An FX-capable tenant needs
    /// <c>FxRate</c> rows, and this seeder has no honest source for a rate — inventing one would
    /// put a fabricated number behind a customer's converted totals. Seeding the currencies
    /// WITHOUT rates is worse still: <c>FxConversionService.TotalAsync</c> fails closed on any
    /// amount it cannot convert, so a pickable EUR row with no EUR→base rate turns the dashboard's
    /// quoted value into "unavailable" the first time anybody uses it. A tenant that trades in a
    /// second currency adds it together with its rate, which is the only order in which either
    /// value is true.</para>
    ///
    /// <para><c>IsBaseCurrency</c> is set only when the business unit has no base currency at all.
    /// The "exactly one base" rule is application-level (<c>CurrencyRepository</c>), never a
    /// database constraint, and every reader takes two rows and refuses to guess when it finds
    /// them — so a second base-flagged row would not error, it would silently disable conversion
    /// tenant-wide.</para>
    /// </summary>
    private async Task<int> EnsureBaseCurrencyAsync(
        long businessUnitId, TenantBaselineCatalog.CurrencyDefinition currency,
        string actor, DateTime now, CancellationToken ct)
    {
        var existing = await context.Currencies.IgnoreQueryFilters()
            .Where(row => row.BusinessUnitId == businessUnitId)
            .Select(row => new { row.Code, row.IsBaseCurrency })
            .ToListAsync(ct);

        if (existing.Any(row => string.Equals(row.Code, currency.Code, StringComparison.OrdinalIgnoreCase)))
            return 0;

        context.Currencies.Add(new Currency
        {
            BusinessUnitId = businessUnitId,
            Code = currency.Code,
            CurrencyName = currency.Name,
            Symbol = currency.Symbol,

            // The base currency's rate against itself. Stored rather than left null so the column
            // never has to be read as "unknown" for the one currency whose rate is not a question.
            ExchangeRate = 1m,
            IsBaseCurrency = !existing.Any(row => row.IsBaseCurrency == true),
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = now
        });
        return 1;
    }

    /// <summary>
    /// The unit list, matched case-insensitively on code. A tenant that already spells a unit
    /// their own way keeps their row: <c>SetUomVocabulary</c> indexes by folded code AND name and
    /// maps each onto the canonical code, so their spelling still resolves and a second row would
    /// only split the same unit in two.
    /// </summary>
    private async Task<int> EnsureUnitsOfMeasureAsync(
        long businessUnitId, string actor, DateTime now, CancellationToken ct)
    {
        var existing = (await context.SetUoms.IgnoreQueryFilters()
                .Where(unit => unit.BusinessUnitId == businessUnitId)
                .Select(unit => unit.UomCode)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        foreach (var unit in TenantBaselineCatalog.UnitsOfMeasure.Where(unit => !existing.Contains(unit.Code)))
        {
            context.SetUoms.Add(new SetUom
            {
                BusinessUnitId = businessUnitId,
                UomCode = unit.Code,
                UomName = unit.Name,
                Description = unit.Description,
                IsActive = true,
                CreatedBy = actor,

                // Set explicitly, always. CreatedDate carries a store default of now(), and EF
                // omits a property still holding its CLR default from the INSERT — which would
                // hand the column to a PostgreSQL function that no other provider has.
                CreatedDate = now
            });
            created++;
        }
        return created;
    }

    /// <summary>
    /// The tenant's own country, and only that one.
    ///
    /// <para>A full ISO country list is a per-business-unit table here, so seeding one would write
    /// ~250 rows per tenant to make a dropdown longer. The one country a supplier or customer
    /// record is overwhelmingly likely to need is the one the tenant trades from; the rest are an
    /// administrator adding the countries they actually deal with. States and cities are not
    /// seeded for the same reason — there is no defensible subset, and an incomplete one reads as
    /// an authoritative list.</para>
    ///
    /// <para>The NAME is resolved through <c>RegionInfo</c> and falls back to the code. Unlike a
    /// currency name it is a label on a dropdown the tenant can rename, never something printed as
    /// an identity, so ICU's spelling is good enough and a hand-maintained table of every country
    /// would not earn its keep.</para>
    /// </summary>
    private async Task<int> EnsureCountryAsync(
        long businessUnitId, string? countryCode, string actor, DateTime now, CancellationToken ct)
    {
        var code = countryCode?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(code) || code.Length != 2 || !code.All(char.IsAsciiLetter))
        {
            logger.LogInformation(
                "No usable country code for business unit {BusinessUnitId}; the country list is left "
                + "empty for the tenant to populate.", businessUnitId);
            return 0;
        }

        if (await context.SetCountries.IgnoreQueryFilters()
                .AnyAsync(country => country.Buid == businessUnitId && country.CountryCode == code, ct))
            return 0;

        context.SetCountries.Add(new SetCountry
        {
            Buid = businessUnitId,
            CountryCode = code,
            CountryName = CountryName(code),
            Description = "Country of incorporation, recorded at provisioning",
            IsActive = true,
            CreatedBy = actor,
            CreatedDate = now
        });
        return 1;
    }

    private static string CountryName(string alpha2)
    {
        try
        {
            var name = new System.Globalization.RegionInfo(alpha2).EnglishName;
            return string.IsNullOrWhiteSpace(name) ? alpha2 : name;
        }
        catch (ArgumentException)
        {
            // A code ICU does not know (or a host running globalization-invariant). The code is
            // the identity and is already correct; only the label is unavailable.
            return alpha2;
        }
    }

    /// <summary>
    /// PERCENTAGE and FIXED, the two codes <c>QuoteService.CalculateQuoteTotals</c> can act on.
    /// Matched on SetupType + SetupCode so a tenant that already has them keeps theirs.
    /// </summary>
    private async Task<int> EnsureDiscountTypesAsync(
        long businessUnitId, string actor, DateTime now, CancellationToken ct)
    {
        var existing = (await context.SetupMasters.IgnoreQueryFilters()
                .Where(row => row.BusinessUnitId == businessUnitId && row.SetupType == "DiscountType")
                .Select(row => row.SetupCode)
                .ToListAsync(ct))
            .Where(code => code is not null)
            .Select(code => code!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var created = 0;
        foreach (var (code, label, description) in
                 TenantBaselineCatalog.DiscountTypes.Where(item => !existing.Contains(item.Code)))
        {
            context.SetupMasters.Add(new SetupMaster
            {
                SetupType = "DiscountType",
                SetupCode = code,
                SetupValue = label,
                Description = description,
                BusinessUnitId = businessUnitId,

                // Rank is meaningless on a non-role row and SetupMasterController refuses to store
                // anything else on one; stated here so the value is never inherited by accident.
                RoleRank = RoleRanks.Member,
                IsActive = true,
                CreatedBy = actor,
                CreatedOn = now
            });
            created++;
        }
        return created;
    }

    /// <summary>
    /// The lifecycle states — <c>LeadStatus</c>, <c>RFQStatus</c>, <c>QuoteStatus</c>,
    /// <c>OrderStatus</c>, <c>PaymentStatus</c> — taken verbatim from
    /// <see cref="LifecycleStatusCatalog"/> rather than restated here, because a second copy of the
    /// list is a second thing to keep in step with <c>LifecyclePolicy</c>.
    ///
    /// <para><b>Why this belongs in the baseline and not only in provisioning.</b> These rows
    /// were reference data written once per business unit, by whichever creation path ran:
    /// <c>ProvisioningStepExecutor</c>'s <c>lifecycle-statuses</c> step,
    /// <c>TenantsController.Provision</c>, or <c>BusinessUnitRepository.AddAsync</c>. Nothing ever
    /// checked afterwards. A business unit created before that step existed, one whose step failed
    /// while later steps succeeded — the reconciler has no probe for this step, so it can neither
    /// confirm nor repair it — or one created by any path that did not think to seed them, is left
    /// permanently without them, and this seeder is the documented repair tool that a support
    /// engineer re-runs. Filling the gap here makes the repair actually repair.</para>
    ///
    /// <para><b>What a missing <c>QuoteStatus</c> costs, concretely.</b>
    /// <c>QuoteService.CreateQuoteAsync</c> resolves DRAFT through
    /// <c>ResolveQuoteStatusIdAsync</c>, which falls back to the documented legacy id 42 when no
    /// row carries the code. <c>Quotes."StatusID"</c> is a foreign key to
    /// <c>Setup_Master."SetupID"</c>, so on a database where no row has that id the tenant's first
    /// quote fails on the foreign key, and on one where some OTHER tenant's row happens to hold it
    /// the quote is stamped with a status belonging to somebody else's workspace.
    /// <c>LifecycleStatusCatalog.ResolveIdAsync</c> — the path
    /// <c>RfqRepository</c> and <c>LeadConversionIntelligence</c> take — instead throws
    /// "DRAFT is not configured and active for this tenant." Downstream,
    /// <c>DashboardRepository</c> and <c>QuoteRepository.GetQuoteStatsAsync</c> count against ids
    /// that match nothing and report zeros without saying why.</para>
    ///
    /// <para>The check-then-insert itself lives on the catalogue
    /// (<see cref="LifecycleStatusCatalog.EnsureAsync"/>) rather than here, because this is the
    /// THIRD caller that needs it, and the caller that did NOT have it — <c>DemoUserSeeder</c> —
    /// is how a business unit came to exist with no <c>QuoteStatus</c> rows at all.</para>
    /// </summary>
    private Task<int> EnsureLifecycleStatusesAsync(
        BusinessUnit businessUnit, string actor, DateTime now, CancellationToken ct)
        => LifecycleStatusCatalog.EnsureAsync(context, businessUnit, actor, now, ct);

    public async Task<int> ReconcileReferenceListsAsync(
        long businessUnitId, string actor, CancellationToken ct = default)
    {
        var seededBy = string.IsNullOrWhiteSpace(actor) ? "provisioning" : actor.Trim();
        if (!await context.BusinessUnits.IgnoreQueryFilters().AnyAsync(unit => unit.Id == businessUnitId, ct))
            throw new InvalidOperationException(
                $"Business unit {businessUnitId} does not exist; its reference lists cannot be reconciled.");

        var created = await EnsureReferenceListsAsync(businessUnitId, seededBy, DateTime.UtcNow, ct);
        if (created > 0)
            await context.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// The Setup_Master reference lists in <see cref="TenantBaselineCatalog.ReferenceLists"/>.
    ///
    /// <para><b>Whole-list, type-level presence.</b> A list is written only when the tenant has NO
    /// row of that SetupType — active or not — matched with the type folded for case and spacing,
    /// the same comparison <c>ShipmentController</c> and <c>LifecycleStatusCatalog</c> read with.
    /// This is deliberately coarser than the per-code check the lifecycle catalogue uses. A
    /// lifecycle code is a hard runtime dependency the product resolves by name, so a missing one
    /// must be restored; a reference list is the customer's vocabulary, and a tenant that has
    /// deleted "CHECK" from its payment methods has not lost a row — it has made a decision. The
    /// re-run therefore fills empty lists and leaves shaped ones alone.</para>
    ///
    /// <para>What a missing list costs is stated per list on the catalogue. The one that bites
    /// first is <c>ShipmentStatus</c>: business units 7 and 8 on the live database had none, and
    /// their despatch screen could not be submitted at all.</para>
    /// </summary>
    private async Task<int> EnsureReferenceListsAsync(
        long businessUnitId, string actor, DateTime now, CancellationToken ct)
    {
        var presentTypes = (await context.SetupMasters.IgnoreQueryFilters().AsNoTracking()
                .Where(row => row.BusinessUnitId == businessUnitId)
                .Select(row => row.SetupType)
                .Distinct()
                .ToListAsync(ct))
            .Select(FoldSetupType)
            .ToHashSet(StringComparer.Ordinal);

        var created = 0;
        foreach (var list in TenantBaselineCatalog.ReferenceLists)
        {
            if (presentTypes.Contains(FoldSetupType(list.SetupType)))
                continue;

            foreach (var entry in list.Entries)
            {
                context.SetupMasters.Add(new SetupMaster
                {
                    SetupType = list.SetupType,
                    SetupCode = entry.Code,
                    SetupValue = entry.Label,
                    Description = entry.Description,
                    BusinessUnitId = businessUnitId,
                    RoleRank = RoleRanks.Member,
                    IsActive = true,
                    CreatedBy = actor,
                    CreatedOn = now
                });
                created++;
            }
        }
        return created;
    }

    private static string FoldSetupType(string? setupType)
        => (setupType ?? string.Empty).Replace(" ", string.Empty).ToLowerInvariant();

    /// <summary>
    /// The commercial-case reference format.
    ///
    /// <para>This row is NOT required for correctness: both the PostgreSQL
    /// <c>nexora_assign_lead_reference</c> trigger and the non-PostgreSQL fallback in
    /// <c>LeadPersistenceRules</c> create a default row on the first lead if none exists. What
    /// seeding buys is the prefix. The reference is permanent and immutable — the trigger rejects
    /// any attempt to change it and <c>LeadPersistenceRules.EnforceReferenceImmutability</c>
    /// throws on the same — so whatever the first lead is stamped with is that lead's identity for
    /// the life of the tenant. Leaving the lazy default to fire means the customer's earliest
    /// cases read "NXR-2026-000001" (Nexora's own initials), and correcting the prefix afterwards
    /// does not rewrite them: it leaves the tenant with two permanent reference families in one
    /// workspace. Seeding it before the first lead is the only moment at which that is avoidable.
    /// </para>
    /// </summary>
    private async Task<(bool Created, string Prefix)> EnsureLeadReferenceConfigurationAsync(
        BusinessUnit businessUnit, DateTime now, CancellationToken ct)
    {
        var existing = await context.LeadReferenceConfigurations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(configuration => configuration.BusinessUnitId == businessUnit.Id, ct);
        if (existing is not null)
            return (false, existing.Prefix);

        var prefix = ReferencePrefix(businessUnit.BusinessUnitCode);
        context.LeadReferenceConfigurations.Add(new LeadReferenceConfiguration
        {
            BusinessUnitId = businessUnit.Id,
            Prefix = prefix,
            Format = "{PREFIX}-{YEAR}-{SEQUENCE}",
            SequencePadding = 6,
            FinancialYearStartMonth = 1,

            // Explicit for the same reason as the SetUom dates: the column defaults to now().
            CreatedOn = now
        });
        return (true, prefix);
    }

    /// <summary>
    /// The business unit code reduced to what a reference may contain. Both the trigger and
    /// <c>LeadReferenceFormatter</c> strip anything outside <c>[A-Z0-9_-]</c> when they render
    /// <c>{PREFIX}</c>, so storing an unsanitised value would mean the stored prefix and the
    /// printed one disagree. Capped at the column's 20 characters, and falls back to "NXR" when a
    /// code sanitises away to nothing — an empty prefix produces a reference beginning with a
    /// hyphen.
    /// </summary>
    private static string ReferencePrefix(string? businessUnitCode)
    {
        var sanitized = new string((businessUnitCode ?? string.Empty).ToUpperInvariant()
            .Where(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            .ToArray());
        return sanitized.Length == 0 ? "NXR" : sanitized[..Math.Min(sanitized.Length, 20)];
    }

    private sealed record StarterRoleOutcome(
        int ModulesCreated, int RolesCreated, int GrantsCreated, IReadOnlyList<TenantBaselineRoleSummary> Roles);

    /// <summary>
    /// The starter roles and their permission grants.
    ///
    /// <para>Roles are matched on SetupCode within the business unit. A role that already exists
    /// keeps its rank, its name and every grant it holds — only grants for modules it has no row
    /// for at all are added, so a re-run can complete a half-seeded tenant without undoing an
    /// administrator's narrowing of a role that was seeded earlier.</para>
    /// </summary>
    private async Task<StarterRoleOutcome> EnsureStarterRolesAsync(
        long businessUnitId, string actor, DateTime now, CancellationToken ct)
    {
        var (modules, modulesCreated) = await ResolveModulesAsync(actor, now, ct);

        var codes = TenantBaselineCatalog.StarterRoles.Select(role => role.Code).ToArray();
        var existingRoles = await context.SetupMasters.IgnoreQueryFilters()
            .Where(SetupTypes.IsRoleRow)
            .Where(row => row.BusinessUnitId == businessUnitId && row.SetupCode != null
                          && codes.Contains(row.SetupCode!))
            .ToListAsync(ct);

        var existingRoleIds = existingRoles.Select(role => role.SetupId).ToArray();
        var existingGrants = new HashSet<(long RoleId, long ModuleId)>();
        if (existingRoleIds.Length > 0)
        {
            var grants = await context.RolePermissions.IgnoreQueryFilters()
                .Where(grant => grant.BusinessUnitId == businessUnitId
                                && grant.RoleId != null && existingRoleIds.Contains(grant.RoleId.Value))
                .Select(grant => new { RoleId = grant.RoleId!.Value, grant.ModuleId })
                .ToListAsync(ct);
            foreach (var grant in grants)
                existingGrants.Add((grant.RoleId, grant.ModuleId));
        }

        var rolesCreated = 0;
        var grantsCreated = 0;
        var summaries = new List<TenantBaselineRoleSummary>(TenantBaselineCatalog.StarterRoles.Count);

        foreach (var template in TenantBaselineCatalog.StarterRoles)
        {
            var role = existingRoles.FirstOrDefault(
                candidate => string.Equals(candidate.SetupCode, template.Code, StringComparison.OrdinalIgnoreCase));
            if (role is null)
            {
                role = new SetupMaster
                {
                    // "Role", matching the casing the founding SUPER_ADMIN is written with and the
                    // casing production data holds. SetupTypes.IsRole is tolerant of case and
                    // spacing, but a second spelling in the same tenant is a trap for every raw
                    // SQL query that is not.
                    SetupType = "Role",
                    SetupCode = template.Code,
                    SetupValue = template.Name,
                    Description = template.Description,
                    BusinessUnitId = businessUnitId,
                    RoleRank = template.Rank,
                    IsActive = true,
                    CreatedBy = actor,
                    CreatedOn = now
                };
                context.SetupMasters.Add(role);
                rolesCreated++;
            }

            var granted = 0;
            foreach (var grant in template.Grants)
            {
                if (!modules.TryGetValue(grant.Module, out var module))
                {
                    // Unreachable while ModuleCatalog and this catalogue agree, and asserted by
                    // TenantBaselineRoleTemplateTests. Logged rather than thrown because a grant
                    // that cannot be written must not cost the customer their whole tenant.
                    logger.LogWarning(
                        "Starter role {Role} references permission module '{Module}', which is not in "
                        + "ModuleCatalog; the grant is skipped and the role will be denied that module.",
                        template.Code, grant.Module);
                    continue;
                }

                // A tracked-but-unsaved role has SetupId 0, and no saved grant can reference it,
                // so the lookup is only meaningful for roles that already existed.
                if (role.SetupId != 0 && existingGrants.Contains((role.SetupId, module.Id)))
                    continue;

                context.RolePermissions.Add(new RolePermission
                {
                    // Both navigations rather than ids: Module.Id is generated for a module this
                    // pass had to insert, and Role is the composite (BusinessUnitId, SetupId)
                    // principal, so EF has to resolve both keys during the flush.
                    Role = role,
                    Module = module,
                    BusinessUnitId = businessUnitId,

                    // Every flag stated. CanView in particular: the column defaults to TRUE in the
                    // store (it had to, so the migration that added it reproduced the old
                    // row-exists semantics), which means an unset CanView silently grants read on
                    // a module a template meant to withhold.
                    CanView = grant.CanView,
                    CanCreate = grant.CanCreate,
                    CanEdit = grant.CanEdit,
                    CanDelete = grant.CanDelete,
                    CreatedBy = actor,
                    CreatedOn = now
                });
                granted++;
            }

            grantsCreated += granted;
            summaries.Add(new TenantBaselineRoleSummary(template.Code, template.Name, template.Rank, granted));
        }

        return new StarterRoleOutcome(modulesCreated, rolesCreated, grantsCreated, summaries);
    }

    /// <summary>
    /// The <c>Module</c> rows the starter roles need, inserting any that are absent.
    ///
    /// <para><c>Module</c> is platform-global reference data that <c>ModuleCatalogReconciler</c>
    /// owns and rewrites nothing of; this method follows the same insert-only contract. It exists
    /// because reconciliation is best-effort by design — it runs only against PostgreSQL and
    /// swallows its failures so a transient database problem cannot stop the API booting. A tenant
    /// provisioned inside that window would get starter roles missing exactly the grants whose
    /// modules were absent, permanently and silently, because nothing ever re-seeds a tenant's
    /// RolePermissions. Only the modules these templates reference are inserted: the rest of the
    /// catalogue is not this transaction's business.</para>
    ///
    /// <para>The read-then-insert has a race with a node reconciling at the same moment, and
    /// <c>Module.ModuleName</c> is uniquely indexed. Losing that race aborts the provisioning
    /// transaction and the operator retries; that is preferred to the alternative of quietly
    /// shipping a tenant whose roles are missing grants nobody will notice for months.</para>
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, Module> Modules, int Created)> ResolveModulesAsync(
        string actor, DateTime now, CancellationToken ct)
    {
        var required = TenantBaselineCatalog.StarterRoles
            .SelectMany(role => role.Grants)
            .Select(grant => grant.Module)
            .ToHashSet(StringComparer.Ordinal);

        // Tracked, not AsNoTracking: these instances are assigned to RolePermission.Module, and a
        // detached one would be re-inserted as a duplicate module.
        var modules = await context.Modules
            .Where(module => required.Contains(module.ModuleName))
            .ToDictionaryAsync(module => module.ModuleName, StringComparer.Ordinal, ct);

        var missing = ModuleCatalog.All
            .Where(definition => required.Contains(definition.Name) && !modules.ContainsKey(definition.Name))
            .ToList();
        if (missing.Count == 0)
            return (modules, 0);

        logger.LogWarning(
            "Permission module(s) {Modules} were absent when a tenant baseline was seeded, which means "
            + "ModuleCatalogReconciler has not completed a pass on this database. They are being inserted "
            + "so the starter roles can hold them.",
            string.Join(", ", missing.Select(definition => definition.Name)));

        foreach (var definition in missing)
        {
            var module = new Module
            {
                ModuleName = definition.Name,
                Description = definition.Description,
                IsActive = true,
                CreatedBy = ModuleSeedActor,
                CreatedOn = now
            };
            context.Modules.Add(module);
            modules[definition.Name] = module;
        }
        return (modules, missing.Count);
    }

    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
