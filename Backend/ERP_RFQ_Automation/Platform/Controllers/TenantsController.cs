using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Platform.Activation;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Cross-tenant tenant lifecycle: list / get / provision / suspend / resume.
/// Guarded by the default-deny <see cref="PlatformPolicies.PlatformScope"/> at the
/// class level; mutations additionally require a tenant-admin role. Provisioning
/// creates the Tenant plus its primary BusinessUnit transactionally. (ADR-0005 §4)
/// </summary>
[ApiController]
[Route("api/platform/tenants")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class TenantsController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IPlatformAuditService _audit;
    private readonly ILogger<TenantsController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ITenantScopeAccessor _tenantScope;
    private readonly ITenantBaselineSeeder _baseline;
    private readonly Onboarding.ITenantAdminInvitationService _invitations;
    private readonly Entitlements.ITenantAccessService? _tenantAccess;
    private readonly ITenantActivationPolicyService? _activationPolicy;

    /// <summary>
    /// <paramref name="baseline"/> and <paramref name="invitations"/> are REQUIRED dependencies,
    /// unlike the optional <paramref name="tenantAccess"/> cache. A tenant provisioned without its
    /// baseline is the empty-workspace defect this controller exists to prevent, and an invite
    /// path that silently degrades to a generated password would hand the operator a credential
    /// they were never supposed to hold. Neither has a configuration in which skipping it is
    /// correct, so a missing registration must fail loudly at activation.
    /// </summary>
    public TenantsController(
        ErpRfqAutomationContext context, IPlatformAuditService audit, ILogger<TenantsController> logger,
        IServiceScopeFactory scopeFactory, ITenantScopeAccessor tenantScope,
        ITenantBaselineSeeder baseline,
        Onboarding.ITenantAdminInvitationService invitations,
        Entitlements.ITenantAccessService? tenantAccess = null,
        ITenantActivationPolicyService? activationPolicy = null)
    {
        _context = context;
        _audit = audit;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _baseline = baseline;
        _invitations = invitations;
        _tenantAccess = tenantAccess;
        _activationPolicy = activationPolicy;
    }

    // GET /api/platform/tenants
    [HttpGet]
    public async Task<ActionResult<IEnumerable<TenantSummaryDto>>> List(
        [FromQuery] string? status, CancellationToken ct)
    {
        var query = _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && Enum.TryParse<TenantStatus>(status, true, out var s))
            query = query.Where(t => t.Status == s);

        var tenants = await query.OrderByDescending(t => t.CreatedOn).ToListAsync(ct);
        return Ok(tenants.Select(ToDto));
    }

    // GET /api/platform/tenants/{id}
    [HttpGet("{id:long}")]
    public async Task<ActionResult<TenantSummaryDto>> Get(long id, CancellationToken ct)
    {
        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        return tenant is null ? NotFound() : Ok(ToDto(tenant));
    }

    // PUT /api/platform/tenants/{id}/profile
    [HttpPut("{id:long}/profile")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<TenantSummaryDto>> UpdateProfile(
        long id, [FromBody] UpdateTenantProfileRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        if (ValidateEditableProfile(request) is string validationError)
            return BadRequest(new { error = validationError });

        var actor = User.FindFirst("email")?.Value ?? "platform";
        var now = DateTime.UtcNow;
        var strategy = _context.Database.CreateExecutionStrategy();

        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == id, ct)
                    ?? throw new TenantNotFoundException();

                if (tenant.Status == TenantStatus.Archived)
                    throw new InvalidTenantStatusTransitionException(
                        tenant.Status, "non-archived", "edited",
                        "An archived tenant is a retention-controlled record. Restore it before editing its company profile.");

                var before = ProfileAuditSnapshot(tenant);
                tenant.Name = request.Name.Trim();
                tenant.LegalName = Normalize(request.LegalName);
                tenant.RegistrationNumber = Normalize(request.RegistrationNumber);
                tenant.TaxNumber = Normalize(request.TaxNumber);
                tenant.CountryCode = Normalize(request.CountryCode)?.ToUpperInvariant();
                tenant.Industry = Normalize(request.Industry);
                tenant.Website = Normalize(request.Website);
                tenant.AddressLine1 = Normalize(request.AddressLine1);
                tenant.AddressLine2 = Normalize(request.AddressLine2);
                tenant.City = Normalize(request.City);
                tenant.StateProvince = Normalize(request.StateProvince);
                tenant.PostalCode = Normalize(request.PostalCode);
                tenant.Phone = Normalize(request.Phone);
                tenant.ContactEmail = Normalize(request.ContactEmail);
                tenant.LogoUrl = Normalize(request.LogoUrl);
                tenant.TimeZoneId = Normalize(request.TimeZoneId);
                tenant.Locale = Normalize(request.Locale);
                tenant.ModifiedOn = now;
                tenant.ModifiedBy = actor;

                if (tenant.PrimaryBusinessUnitId is long businessUnitId)
                {
                    var businessUnit = await _context.BusinessUnits.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(b => b.Id == businessUnitId, ct);
                    if (businessUnit is not null)
                    {
                        businessUnit.BusinessUnitName = tenant.Name;
                        businessUnit.ModifiedOn = now;
                        businessUnit.ModifiedBy = actor;
                    }

                    var quoteConfiguration = await _context.QuoteConfigurations.IgnoreQueryFilters()
                        .FirstOrDefaultAsync(q => q.BusinessUnitId == businessUnitId, ct);
                    if (quoteConfiguration is not null)
                    {
                        quoteConfiguration.Logo = tenant.LogoUrl;
                        quoteConfiguration.CompanyAddress = PostalAddressOf(tenant);
                        quoteConfiguration.CompanyPhone = tenant.Phone;
                        quoteConfiguration.CompanyEmail = tenant.ContactEmail;
                        quoteConfiguration.ModifiedOn = now;
                        quoteConfiguration.ModifiedBy = actor;
                    }
                }

                await _context.SaveChangesAsync(ct);
                await _audit.WriteAsync(User, "tenant.profile.update", nameof(Tenant), id.ToString(),
                    new { reason = request.Reason.Trim(), before, after = ProfileAuditSnapshot(tenant) },
                    actAsTenantId: id, httpContext: HttpContext, ct: ct);
                await tx.CommitAsync(ct);
            });
        }
        catch (TenantNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidTenantStatusTransitionException exception)
        {
            return Conflict(new { error = exception.Message });
        }

        var updated = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == id, ct);
        return Ok(ToDto(updated));
    }

    // PUT /api/platform/tenants/{id}/data-region
    /// <summary>
    /// Corrects the contractual data region — the only governed way this value can change after
    /// provisioning.
    ///
    /// <para><b>Why it is not on the profile form.</b> <c>DataRegion</c> is not a description of the
    /// tenant, it is an assertion about where their data physically is, and two controls already
    /// depend on it: <c>TenantDataAssetRegistryService.RegisterAsync</c> refuses to register an asset
    /// whose region differs, and the <c>data.residency-isolation</c> activation control passes only
    /// when the verified PostgreSQL asset's region equals this value. So a region typed wrongly at
    /// provisioning leaves a tenant that can never be activated and against which no data asset can
    /// ever be registered — reachable only by direct SQL until now — while a region edited freely
    /// afterwards would let an operator satisfy a residency control by rewriting the claim instead of
    /// moving the data.</para>
    ///
    /// <para>The resolution is that the region may be corrected only into agreement with the assets
    /// that are actually registered. Any registered asset sitting in a different region refuses the
    /// change and is named, because that asset is the evidence of where the data really is and this
    /// column is only a claim about it.</para>
    ///
    /// <para>Owner, not TenantAdmin: residency is a contractual commitment made during the sale and
    /// a control an auditor reads. Support may operate a tenant and may not restate where it lives.</para>
    /// </summary>
    [HttpPut("{id:long}/data-region")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<TenantSummaryDto>> UpdateDataRegion(
        long id, [FromBody] UpdateTenantDataRegionRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);

        var reason = Normalize(request.Reason);
        if (reason is null || reason.Length < MinimumDataRegionReasonLength)
            return BadRequest(new
            {
                error = $"A reason of at least {MinimumDataRegionReasonLength} characters is required. " +
                        "Restating where a customer's data resides is a contractual assertion, and the " +
                        "record has to say on what basis it was restated."
            });

        var region = Normalize(request.DataRegion);

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null) return NotFound();

        if (tenant.Status == TenantStatus.Archived)
            return Conflict(new
            {
                error = "An archived tenant is a retention-controlled record. Restore it before " +
                        "changing its recorded data region."
            });

        if (string.Equals(tenant.DataRegion, region, StringComparison.Ordinal))
            return Conflict(new { error = "That is already this tenant's recorded data region." });

        // Clearing it is refused on a tenant that is live or has been live. The residency control
        // reads presence, so an empty region would silently withdraw a control that has already
        // passed rather than surfacing as a failure anybody looks at.
        if (region is null && tenant.Status != TenantStatus.Provisioning)
            return BadRequest(new
            {
                error = "dataRegion cannot be cleared on a tenant that has left provisioning: the " +
                        "data.residency-isolation activation control reads its presence, so clearing " +
                        "it withdraws a control this tenant has already been certified against."
            });

        // The registered assets are the evidence; this column is the claim about them. A claim that
        // disagrees with the evidence is refused rather than reconciled, and the disagreeing assets
        // are named so the operator knows whether to correct the claim or move the data.
        var conflicting = await _context.Set<DataAssets.TenantDataAsset>().AsNoTracking()
            .Where(a => a.TenantId == id)
            .Select(a => new { a.LogicalKey, a.Region, a.Status })
            .ToListAsync(ct);
        var mismatched = conflicting
            .Where(a => region is null
                        || !string.Equals(a.Region, region, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (mismatched.Count > 0)
            return Conflict(new
            {
                error = "The tenant already has data assets registered in a different region, and this " +
                        "column only claims what those assets prove. Move or re-register the assets " +
                        "first: " +
                        string.Join("; ", mismatched.Select(a => $"'{a.LogicalKey}' is in '{a.Region}' ({a.Status})")) +
                        "."
            });

        var previous = tenant.DataRegion;
        var now = DateTime.UtcNow;
        var actor = User.FindFirst("email")?.Value ?? "platform";

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var target = await _context.Set<Tenant>().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == id, ct)
                    ?? throw new TenantNotFoundException();

                target.DataRegion = region;
                target.ModifiedOn = now;
                target.ModifiedBy = actor;
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, "tenant.data-region.update", nameof(Tenant), id.ToString(),
                    new { from = previous, to = region, reason },
                    actAsTenantId: id, httpContext: HttpContext, ct: ct);

                await tx.CommitAsync(ct);
            });
        }
        catch (TenantNotFoundException)
        {
            return NotFound();
        }

        var updated = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == id, ct);
        return Ok(ToDto(updated));
    }

    /// <summary>
    /// Same floor as a billing exemption and a destruction reason, for the same reason: a
    /// required field satisfied by "x" leaves a paper trail worth nothing.
    /// </summary>
    private const int MinimumDataRegionReasonLength = 15;

    // POST /api/platform/tenants  (provision)
    [HttpPost]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<ProvisionTenantResponse>> Provision(
        [FromBody] ProvisionTenantRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // The slug is far more permanent than "part of a URL". It becomes the BusinessUnitCode,
        // which becomes the tenant's LeadReferenceConfiguration prefix, which is stamped into every
        // commercial case's IMMUTABLE MasterReference and printed on their quotes — so a tenant
        // provisioned as "admin" issues quotes referenced ADMIN-2026-000001 forever, with no rename
        // path. This also refuses a slug that would fit Tenants.Slug but overflow
        // BusinessUnits.BusinessUnitCode (varchar(50)), which previously passed the tenant insert
        // and then failed the business-unit insert inside the same transaction, surfacing to the
        // operator as a bare "Provisioning failed." naming no field.
        var verdict = Provisioning.ReservedTenantSlugs.Evaluate(request.Slug, request.Name);
        if (!verdict.IsAccepted)
            return BadRequest(new { error = verdict.Message });
        var slug = verdict.Slug!;

        var slugTaken = await _context.Set<Tenant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == slug, ct);
        if (slugTaken)
            return Conflict(new { error = $"A tenant with slug '{slug}' already exists." });

        // Sec9, applied at the moment the price is FIRST set rather than only when it is changed.
        // ChangePlan below is Billing-gated with that reasoning written out; this endpoint took the
        // same commercial fields under a support-level policy, so the separation of duties held for
        // repricing an existing customer and was absent for creating a new one.
        var levers = Auth.PlatformCommercialAuthority.CommercialLeversIn(
            request.PlanId, request.BillingMode, request.BillingModeReason, request.RateCardId,
            request.BillingStartsOn, request.TrialEndsOn);
        if (levers.Count > 0 && !Auth.PlatformCommercialAuthority.HoldsCommercialAuthority(User))
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = Auth.PlatformCommercialAuthority.DescribeRefusal(levers) });

        if (ValidateCompanyProfile(request) is string profileError)
            return BadRequest(new { error = profileError });

        var billingMode = TenantBillingMode.Billable;
        if (Normalize(request.BillingMode) is string requestedMode
            && !Enum.TryParse(requestedMode, ignoreCase: true, out billingMode))
            return BadRequest(new
            {
                error = $"billingMode '{requestedMode}' is not recognised; use one of " +
                        $"{string.Join(", ", Enum.GetNames<TenantBillingMode>())}."
            });

        if (ValidateCommercialTerms(request, billingMode, DateTime.UtcNow) is string commercialError)
            return BadRequest(new { error = commercialError });

        Plan? plan = null;
        if (request.PlanId is long planId)
        {
            plan = await _context.Set<Plan>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == planId, ct);
            if (plan is null)
                return BadRequest(new { error = $"Plan {planId} does not exist." });
            if (!plan.IsActive)
                return BadRequest(new { error = $"Plan '{plan.Code}' is not active and cannot be assigned." });
        }

        // A pin is an immediate commercial assignment, not a future schedule. Validate the
        // same authority boundary as later assignment and statement calculation so a newly
        // provisioned billable tenant cannot be born with a billing run that is guaranteed to fail.
        string? rateCardCode = null;
        if (request.RateCardId is long rateCardId)
        {
            var card = await _context.Set<Billing.RateCard>().AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == rateCardId, ct);
            if (card is null)
                return BadRequest(new { error = $"Rate card {rateCardId} does not exist." });
            if (!card.IsActive)
                return BadRequest(new { error = $"Rate card '{card.Code}' is not active and cannot be pinned." });
            var now = DateTime.UtcNow;
            if (card.EffectiveFromUtc > now || card.EffectiveToUtc is { } end && end <= now)
                return BadRequest(new { error = $"Rate card '{card.Code}' is not effective now and cannot be pinned." });
            if (!string.Equals(card.Currency, "USD", StringComparison.OrdinalIgnoreCase))
                return BadRequest(new { error = $"Rate card '{card.Code}' is denominated in '{card.Currency}'; v1 billing is USD-only." });
            rateCardCode = card.Code;
        }

        // Users.Email is GLOBALLY unique — one address, one account, one tenant. Checked here so
        // the operator gets a clear 409 naming the problem instead of a transaction rollback
        // surfacing as "Provisioning failed." (Re-raced inserts still fail safely inside the
        // transaction; this check is for the error message, not the guarantee.)
        var adminEmail = request.AdminEmail.Trim();
        if (await _context.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == adminEmail, ct))
            return Conflict(new
            {
                error = $"A user with email '{adminEmail}' already exists. One email address maps to " +
                        "one account on one tenant; use a different address for this tenant's administrator."
            });

        var activation = Normalize(request.AdminActivation)?.ToLowerInvariant()
                         ?? AdminActivationMethods.Invite;
        var invited = activation == AdminActivationMethods.Invite;

        // On the invite path NOBODY holds a credential for this account — not the customer, who
        // has not chosen one yet, and deliberately not the operator either. The column is
        // non-nullable, so it is filled with the hash of a discarded random value: unusable by
        // construction, and it cannot be brute-forced into something that logs in because no
        // plaintext for it exists anywhere. The account stays inactive until the invitation is
        // redeemed, which also keeps it off the seats meter until the customer is really using it.
        string? generatedPassword = null;
        string passwordToHash;
        if (invited)
        {
            passwordToHash = GenerateInitialPassword() + GenerateInitialPassword();
        }
        else if (!string.IsNullOrEmpty(request.AdminPassword))
        {
            passwordToHash = request.AdminPassword;
        }
        else
        {
            generatedPassword = GenerateInitialPassword();
            passwordToHash = generatedPassword;
        }

        // Hashing happens OUTSIDE the retriable transaction — BCrypt mints a fresh salt per call,
        // and the stored hash must not change between retry attempts.
        var adminPasswordHash = BCrypt.Net.BCrypt.HashPassword(passwordToHash);

        var actor = User.FindFirst("email")?.Value ?? "platform";
        Tenant created;
        User foundingAdmin = null!;
        SetupMaster foundingRole = null!;
        TenantBaselineSummary workspace = null!;

        // Overwritten by EVERY execution-strategy attempt, deliberately: the token that activates
        // the account is the one belonging to the attempt that COMMITTED. A token from an attempt
        // whose transaction rolled back hashes to a row that no longer exists and activates nothing.
        Onboarding.IssuedTenantAdminInvitation? issuedInvitation = null;

        // The letterhead line the customer's first quote prints. Composed from the parts the
        // operator typed rather than asking for a second, free-text copy of the same address —
        // two addresses that can disagree is how a compliant document becomes a non-compliant one.
        var postalAddress = string.Join(", ", new[]
        {
            Normalize(request.AddressLine1), Normalize(request.AddressLine2), Normalize(request.City),
            Normalize(request.StateProvince), Normalize(request.PostalCode),
            Normalize(request.CountryCode)?.ToUpperInvariant()
        }.Where(part => part is not null));

        // The context enables EnableRetryOnFailure, so an explicit transaction must
        // run inside the execution strategy or EF throws. Provision Tenant + primary
        // BusinessUnit atomically. (ADR-0005 §1/§4)
        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            created = await strategy.ExecuteAsync(async () =>
            {
                // A failed execution-strategy attempt can leave generated keys and
                // post-SaveChanges states tracked even though its transaction rolled
                // back. Every attempt must construct a fresh provisioning graph.
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var tenant = new Tenant
                {
                    Name = request.Name.Trim(),
                    Slug = slug,
                    Status = TenantStatus.Provisioning,
                    PlanId = request.PlanId,
                    CreatedBy = actor,
                    CreatedOn = DateTime.UtcNow,

                    LegalName = Normalize(request.LegalName),
                    RegistrationNumber = Normalize(request.RegistrationNumber),
                    TaxNumber = Normalize(request.TaxNumber),
                    CountryCode = Normalize(request.CountryCode)?.ToUpperInvariant(),
                    Industry = Normalize(request.Industry),
                    Website = Normalize(request.Website),
                    AddressLine1 = Normalize(request.AddressLine1),
                    AddressLine2 = Normalize(request.AddressLine2),
                    City = Normalize(request.City),
                    StateProvince = Normalize(request.StateProvince),
                    PostalCode = Normalize(request.PostalCode),
                    Phone = Normalize(request.Phone),
                    ContactEmail = Normalize(request.ContactEmail),
                    LogoUrl = Normalize(request.LogoUrl),

                    BaseCurrencyCode = Normalize(request.BaseCurrencyCode)?.ToUpperInvariant(),
                    TimeZoneId = Normalize(request.TimeZoneId),
                    Locale = Normalize(request.Locale),
                    DataRegion = Normalize(request.DataRegion),

                    BillingMode = billingMode,
                    BillingModeReason = Normalize(request.BillingModeReason),
                    RateCardId = request.RateCardId,
                    // Charges accrue from creation unless the sale agreed otherwise: the default
                    // that costs the business nothing is the one where billing has already started.
                    BillingStartsOn = request.BillingStartsOn ?? DateTime.UtcNow.Date,
                    TrialEndsOn = request.TrialEndsOn,
                    ContractStartOn = request.ContractStartOn,
                    ContractEndOn = request.ContractEndOn,
                    PaymentTermsDays = request.PaymentTermsDays,
                    PurchaseOrderReference = Normalize(request.PurchaseOrderReference),
                    BillingContactName = Normalize(request.BillingContactName),
                    // Statements have to reach somebody. Absent a dedicated AP contact, the
                    // founding administrator is a far better default than nobody at all.
                    BillingContactEmail = Normalize(request.BillingContactEmail) ?? adminEmail,
                    BillingAddress = Normalize(request.BillingAddress),
                    AccountOwnerEmail = Normalize(request.AccountOwnerEmail) ?? actor
                };
                _context.Set<Tenant>().Add(tenant);
                await _context.SaveChangesAsync(ct);

                var bu = new BusinessUnit
                {
                    BusinessUnitCode = slug.ToUpperInvariant(),
                    BusinessUnitName = tenant.Name,
                    Description = $"Primary business unit for tenant '{tenant.Name}'",
                    IsActive = true,
                    CreatedBy = actor,
                    CreatedOn = DateTime.UtcNow
                };
                _context.Set<BusinessUnit>().Add(bu);
                _context.SetupMasters.AddRange(LifecycleStatusCatalog.CreateFor(bu, actor));
                await _context.SaveChangesAsync(ct);

                // PostgreSQL creates this row ITSELF: the business_units_create_ai_policy trigger
                // fires AFTER INSERT ON "BusinessUnits" and calls nexora_create_default_ai_policy().
                // Adding a second one unconditionally violated PK_AiProcessingPolicies, so every
                // provision through the portal failed with a bare "Provisioning failed." — and the
                // SQLite tests never caught it, because SQLite has no such trigger.
                //
                // Checked rather than assumed, so this works on both providers: the trigger owns
                // the row where it exists, and the explicit add covers providers where it does not.
                var policyExists = await _context.AiProcessingPolicies.IgnoreQueryFilters()
                    .AnyAsync(p => p.BusinessUnitId == bu.Id, ct);
                if (!policyExists)
                    _context.AiProcessingPolicies.Add(
                        AiProcessingPolicy.CreateSecureDefault(bu.Id, actor, DateTime.UtcNow));

                // ---- founding Super Administrator ---------------------------------------
                // The role and its holder are created IN THE SAME TRANSACTION as the tenant,
                // because a tenant without them is a shell nobody can log into — the exact
                // state every portal-provisioned tenant used to land in. RoleRank.Owner is
                // what PermissionHandler's rank rule reads, so the founding admin has full
                // tenant authority immediately, before any RolePermissions row exists.
                foundingRole = new SetupMaster
                {
                    SetupType = "Role",
                    SetupCode = "SUPER_ADMIN",
                    SetupValue = "Super Administrator",
                    Description = "Founding administrator role created at tenant provisioning.",
                    BusinessUnitId = bu.Id,
                    RoleRank = Authorization.RoleRanks.Owner,
                    IsActive = true,
                    CreatedBy = actor,
                    CreatedOn = DateTime.UtcNow
                };
                _context.SetupMasters.Add(foundingRole);
                await _context.SaveChangesAsync(ct);

                foundingAdmin = new User
                {
                    FirstName = request.AdminFirstName.Trim(),
                    LastName = request.AdminLastName.Trim(),
                    Email = adminEmail,
                    PasswordHash = adminPasswordHash,
                    ImageUrl = string.Empty,
                    RoleId = foundingRole.SetupId,
                    Buid = bu.Id,
                    // An invited administrator is dormant until they redeem their link. That is not
                    // ceremony: the account holds no usable credential yet, and an account nobody
                    // can sign into must not occupy a billable seat while the invitation is in
                    // flight — the seats meter reads exactly this flag.
                    IsActive = !invited,
                    Timezone = Normalize(request.TimeZoneId),
                    CreatedBy = actor,
                    CreatedOn = DateTime.UtcNow
                };
                _context.Users.Add(foundingAdmin);
                await _context.SaveChangesAsync(ct);

                // ---- the workspace itself -----------------------------------------------
                // Runs INSIDE this transaction, so a tenant is never left half-furnished: either
                // the customer gets a workspace with a letterhead, a base currency, units of
                // measure and usable starter roles, or the tenant does not exist at all. The
                // seeder re-reads its own state on every execution-strategy attempt, which is what
                // makes it safe under the ChangeTracker.Clear() above.
                workspace = await _baseline.SeedAsync(
                    bu.Id,
                    new TenantBaselineProfile(
                        CountryCode: Normalize(request.CountryCode)?.ToUpperInvariant(),
                        BaseCurrencyCode: Normalize(request.BaseCurrencyCode)?.ToUpperInvariant(),
                        CompanyName: Normalize(request.LegalName) ?? request.Name.Trim(),
                        CompanyAddress: string.IsNullOrEmpty(postalAddress) ? null : postalAddress,
                        CompanyPhone: Normalize(request.Phone),
                        CompanyEmail: Normalize(request.ContactEmail),
                        LogoUrl: Normalize(request.LogoUrl),
                        Locale: Normalize(request.Locale)),
                    actor,
                    ct);

                // The invitation is minted inside the transaction so a provision that fails takes
                // the activation link down with it. The EMAIL is sent after the commit — a sent
                // email cannot be rolled back, and a live activation link for a tenant that never
                // existed is worse than no email at all.
                issuedInvitation = invited
                    ? await _invitations.IssueAsync(_context, new Onboarding.TenantAdminInvitationRequest
                    {
                        TenantId = tenant.Id,
                        UserId = foundingAdmin.Id,
                        Email = adminEmail,
                        RecipientName = foundingAdmin.FirstName,
                        TenantName = tenant.Name,
                        IssuedBy = actor
                    }, ct)
                    : null;

                tenant.PrimaryBusinessUnitId = bu.Id;
                // Provisioning completion is not activation. The authoritative activation
                // policy owns the only Provisioning -> Active transition.
                tenant.Status = TenantStatus.Provisioning;
                tenant.ModifiedBy = actor;
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                // The admin's EMAIL is audited; the password never is, generated or not, and neither
                // is the activation token. The COMMERCIAL terms are audited in full: who decided
                // that this customer pays (or does not), against which plan and price list, is the
                // record that has to survive the salesperson who agreed it.
                await _audit.WriteAsync(User, "tenant.provision", nameof(Tenant), tenant.Id.ToString(),
                    new
                    {
                        tenant.Name, tenant.Slug, tenant.LegalName, tenant.CountryCode,
                        tenant.PlanId, tenant.PrimaryBusinessUnitId,
                        BillingMode = billingMode.ToString(),
                        tenant.BillingModeReason, tenant.RateCardId,
                        tenant.BillingStartsOn, tenant.TrialEndsOn,
                        tenant.ContractStartOn, tenant.ContractEndOn,
                        tenant.BillingContactEmail, tenant.AccountOwnerEmail,
                        AdminEmail = adminEmail, AdminUserId = foundingAdmin.Id,
                        AdminActivation = activation,
                        PasswordGenerated = generatedPassword is not null,
                        // The invitation's EXISTENCE and expiry are auditable; its token is not,
                        // and neither is the URL that carries it.
                        InvitationIssued = issuedInvitation is not null,
                        InvitationExpiresAtUtc = issuedInvitation?.ExpiresAtUtc,
                        Baseline = new
                        {
                            workspace.BaseCurrencyCode, workspace.UnitsOfMeasureCreated,
                            workspace.RolesCreated, workspace.RolePermissionsCreated,
                            workspace.QuoteConfigurationCreated, workspace.LeadReferencePrefix,
                            Roles = workspace.RoleNames
                        }
                    },
                    actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

                await tx.CommitAsync(ct);
                return tenant;
            });
        }
        catch (Exception exception)
        {
            // The exception was previously DISCARDED, so a failed provision left the operator with
            // "Provisioning failed." and the server with no record of why — which is how a
            // duplicate-key violation on the AI policy went unnoticed through every portal attempt.
            _logger.LogError(exception, "Failed to provision tenant {Slug} for admin {AdminEmail}.",
                slug, adminEmail);
            return StatusCode(500, new { error = "Provisioning failed." });
        }

        // Outside the transaction on purpose (see IssueAsync). SendInvitationEmailAsync never
        // throws: a mail outage must not turn a committed provision into a 500 the operator reads
        // as failure, and the link is in this response either way.
        var emailSent = issuedInvitation is not null
                        && await _invitations.SendInvitationEmailAsync(issuedInvitation, ct);

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == created.Id, ct);

        // ProvisionTenantResponse, not TenantSummaryDto: the generated password and the activation
        // link appear in THIS response and nowhere else, ever — not on list, not on get, not in the
        // audit log. Only a BCrypt hash and a token HASH are stored, so there is nothing to
        // retrieve later by design; a lost link is re-issued, never recovered.
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ProvisionTenantResponse
        {
            Tenant = ToDto(withPlan),
            FoundingAdmin = new FoundingAdminDto
            {
                UserId = foundingAdmin.Id,
                Email = foundingAdmin.Email,
                RoleName = foundingRole.SetupValue,
                GeneratedPassword = generatedPassword,
                Invitation = issuedInvitation is null ? null : new AdminInvitationDto
                {
                    ExpiresAtUtc = issuedInvitation.ExpiresAtUtc,
                    // Withheld when the mail went out: see AdminInvitationDto.ActivationUrl. The
                    // operator gets the link only in the case where nobody else has it.
                    ActivationUrl = emailSent ? null : issuedInvitation.ActivationUrl,
                    EmailSent = emailSent
                }
            },
            Baseline = new TenantBaselineDto
            {
                QuoteConfiguration = workspace.QuoteConfigurationCreated,
                BaseCurrency = workspace.BaseCurrencyCode,
                UnitsOfMeasure = workspace.UnitsOfMeasureCreated,
                Roles = workspace.RolesCreated,
                PermissionGrants = workspace.RolePermissionsCreated,
                LeadReferencePrefix = workspace.LeadReferencePrefix
            },
            Billing = new TenantBillingPostureDto
            {
                Mode = billingMode.ToString(),
                PlanCode = plan?.Code,
                RateCardCode = rateCardCode,
                BillingStartsOn = created.BillingStartsOn,
                TrialEndsOn = created.TrialEndsOn,
                Warnings = DescribeRevenueRisk(created, plan, billingMode, rateCardCode is not null)
            }
        });
    }

    /// <summary>
    /// Mirrors the floor the provisioning wizard enforces. Kept in sync deliberately: a rule that
    /// lives only in the client is a rule any other caller can ignore.
    /// </summary>
    private const int MinimumBillingModeReasonLength = 15;

    /// <summary>
    /// The commercial half of provisioning validation, kept together because these rules only
    /// make sense as a set: what a tenant is charged, from when, and against which price list.
    ///
    /// <para><b>Every rule here closes a measured revenue leak.</b> A Billable tenant with no
    /// plan produces a statement with no base-subscription line, because
    /// <c>BillingStatementService.BuildLines</c> emits that line only when a plan exists — so the
    /// customer is metered and never charged. A Trial with no end date is indistinguishable from
    /// permanent free service and nothing ever prompts conversion. A non-Billable mode with no
    /// written reason is free service that nobody signed for. These were all reachable through a
    /// form that called the plan "optional".</para>
    /// </summary>
    private static string? ValidateCommercialTerms(
        ProvisionTenantRequest request, TenantBillingMode mode, DateTime now)
    {
        if (mode == TenantBillingMode.Billable && request.PlanId is null)
            return "A billable tenant must be assigned a plan: without one its statements carry no " +
                   "base subscription line and the customer is never charged. Assign a plan, or set " +
                   "billingMode to Trial, Internal or Partner with a reason.";

        if (mode != TenantBillingMode.Billable)
        {
            // A length floor, not just presence. An exemption justified as "x" satisfies a
            // required-field check while leaving the paper trail worth nothing — and the whole
            // point of demanding a reason is that somebody has to be able to read it later and
            // understand why this customer was not charged.
            var reason = request.BillingModeReason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length < MinimumBillingModeReasonLength)
                return $"billingMode '{mode}' means this tenant is not charged; billingModeReason " +
                       $"is required and must be at least {MinimumBillingModeReasonLength} characters " +
                       "so the exemption is attributable to a real decision.";
        }

        if (mode == TenantBillingMode.Trial)
        {
            if (request.TrialEndsOn is not DateTime trialEnd)
                return "A trial tenant must carry trialEndsOn. An open-ended trial is free service " +
                       "with no conversion date.";
            if (trialEnd <= now)
                return $"trialEndsOn {trialEnd:yyyy-MM-dd} is not in the future; the trial would be " +
                       "expired the moment it is created.";
        }

        if (request.ContractStartOn is DateTime start && request.ContractEndOn is DateTime end
            && end <= start)
            return "contractEndOn must fall after contractStartOn.";

        return null;
    }

    /// <summary>
    /// Non-commercial field validation. Codes are checked for SHAPE and, where the runtime can
    /// answer authoritatively (time zones), for existence — a mistyped IANA id would otherwise
    /// surface much later as an SLA clock running in the wrong offset.
    /// </summary>
    private static string? ValidateCompanyProfile(ProvisionTenantRequest request)
    {
        if (Normalize(request.CountryCode) is string country
            && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            return $"countryCode '{country}' is not an ISO-3166-1 alpha-2 code (two letters, e.g. 'SA').";

        // Required, not optional. Every consequence of a missing base currency is INVISIBLE:
        // FxConversionService cannot resolve a base, so the dashboard total reports itself
        // unavailable; unit costs silently null out; the general ledger refuses to open a book.
        // Provisioning is the last moment at which anyone can still be told.
        if (Normalize(request.BaseCurrencyCode) is not string currency)
            return "baseCurrencyCode is required: without a base currency the tenant's totals, " +
                   "unit costs and general ledger all fail silently rather than visibly.";
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetter))
            return $"baseCurrencyCode '{currency}' is not an ISO-4217 code (three letters, e.g. 'SAR').";

        if (Normalize(request.TimeZoneId) is string timeZone)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            }
            catch (Exception exception) when (
                exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return $"timeZoneId '{timeZone}' is not a time zone this server recognises; use an " +
                       "IANA identifier such as 'Asia/Riyadh'.";
            }
        }

        var activation = Normalize(request.AdminActivation)?.ToLowerInvariant()
                         ?? AdminActivationMethods.Invite;
        if (activation is not (AdminActivationMethods.Invite or AdminActivationMethods.Password))
            return $"adminActivation '{activation}' is not recognised; use " +
                   $"'{AdminActivationMethods.Invite}' or '{AdminActivationMethods.Password}'.";

        // Refused rather than ignored. "No password exists until the administrator sets one" is
        // the entire security property of the invite path; silently discarding a supplied one
        // would leave that property resting on a client's good manners, and a caller who sent a
        // password believes it was set.
        if (activation == AdminActivationMethods.Invite && !string.IsNullOrEmpty(request.AdminPassword))
            return "adminPassword cannot be supplied with adminActivation 'invite': the invited " +
                   "administrator chooses their own credential and none exists until they do. " +
                   $"Use adminActivation '{AdminActivationMethods.Password}' to set one directly.";

        return null;
    }

    private static string? ValidateEditableProfile(UpdateTenantProfileRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return "name is required.";
        if (Normalize(request.CountryCode) is string country
            && (country.Length != 2 || !country.All(char.IsAsciiLetter)))
            return $"countryCode '{country}' is not an ISO-3166-1 alpha-2 code (two letters, e.g. 'SA').";

        if (Normalize(request.TimeZoneId) is string timeZone)
        {
            try
            {
                TimeZoneInfo.FindSystemTimeZoneById(timeZone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return $"timeZoneId '{timeZone}' is not a time zone this server recognises; use an IANA identifier such as 'Asia/Riyadh'.";
            }
        }

        return null;
    }

    private static object ProfileAuditSnapshot(Tenant tenant) => new
    {
        tenant.Name,
        tenant.LegalName,
        tenant.RegistrationNumber,
        tenant.TaxNumber,
        tenant.CountryCode,
        tenant.Industry,
        tenant.Website,
        tenant.AddressLine1,
        tenant.AddressLine2,
        tenant.City,
        tenant.StateProvince,
        tenant.PostalCode,
        tenant.Phone,
        tenant.ContactEmail,
        tenant.LogoUrl,
        tenant.TimeZoneId,
        tenant.Locale
    };

    private static string? PostalAddressOf(Tenant tenant)
    {
        var address = string.Join(", ", new[]
        {
            tenant.AddressLine1, tenant.AddressLine2, tenant.City, tenant.StateProvince,
            tenant.PostalCode, tenant.CountryCode
        }.Where(part => !string.IsNullOrWhiteSpace(part)));
        return address.Length == 0 ? null : address;
    }

    /// <summary>
    /// Names, at creation time, every way this tenant might fail to produce the revenue it should.
    /// An empty list is the contract: this customer will be charged exactly as intended.
    ///
    /// <para>Warnings rather than refusals, because each of these is a legitimate deliberate
    /// choice for SOME customer — an unpriced plan during a pricing rollout, an unpinned rate card
    /// for a standard-price account. What is not legitimate is discovering it at quarter end.</para>
    /// </summary>
    private static List<string> DescribeRevenueRisk(
        Tenant tenant, Plan? plan, TenantBillingMode mode, bool rateCardPinned)
    {
        var warnings = new List<string>();

        if (mode == TenantBillingMode.Billable)
        {
            if (plan is null)
                warnings.Add("No plan assigned — statements will carry no base subscription charge.");
            else if (plan.MonthlyPriceUsd is null)
                warnings.Add($"Plan '{plan.Code}' has no monthly price configured, so the base " +
                             "subscription charges 0.00 until one is set.");
            else if (plan.MonthlyPriceUsd <= 0m)
                warnings.Add($"Plan '{plan.Code}' is priced at {plan.MonthlyPriceUsd:0.00} USD/month.");

            if (!rateCardPinned)
                warnings.Add("No rate card pinned — metered usage will be priced against whichever " +
                             "card is active for the period, and a period with no active card " +
                             "produces no statement at all.");
        }
        else
        {
            warnings.Add($"Billing mode is {mode}: this tenant consumes the platform without being " +
                         $"charged. Reason on record: {tenant.BillingModeReason}");
        }

        if (mode == TenantBillingMode.Trial && tenant.TrialEndsOn is DateTime ends)
            warnings.Add($"Trial converts on {ends:yyyy-MM-dd}; after that date the tenant must be " +
                         "moved to Billable or it is free service.");

        return warnings;
    }

    /// <summary>
    /// A generated initial credential: 20 characters from a cryptographic RNG with guaranteed
    /// class coverage, over an alphabet with lookalikes (0/O, 1/l/I) removed because this value
    /// gets read aloud or retyped during customer handover.
    /// </summary>
    private static string GenerateInitialPassword()
    {
        const string upper = "ABCDEFGHJKMNPQRSTUVWXYZ";
        const string lower = "abcdefghjkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%^*-_+=";
        const string all = upper + lower + digits + symbols;

        var chars = new char[20];
        chars[0] = upper[System.Security.Cryptography.RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[System.Security.Cryptography.RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[System.Security.Cryptography.RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = symbols[System.Security.Cryptography.RandomNumberGenerator.GetInt32(symbols.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[System.Security.Cryptography.RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher–Yates with the same RNG, so the guaranteed classes are not always positions 0–3.
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }

    [HttpGet("{id:long}/ai-policy")]
    public async Task<ActionResult<TenantAiPolicyDto>> GetAiPolicy(long id, CancellationToken ct)
    {
        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant?.PrimaryBusinessUnitId is not long businessUnitId)
            return NotFound();
        using var tenantScope = _tenantScope.Push(businessUnitId);
        using var scope = _scopeFactory.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var policy = await tenantDb.AiProcessingPolicies.AsNoTracking()
            .SingleOrDefaultAsync(p => p.BusinessUnitId == businessUnitId, ct);
        return policy is null ? NotFound() : Ok(ToAiPolicyDto(policy));
    }

    [HttpPut("{id:long}/ai-policy")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<TenantAiPolicyDto>> UpdateAiPolicy(
        long id, [FromBody] UpdateTenantAiPolicyRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "A reason is required." });
        if (request.MonthlySoftTokenLimit is < 0 || request.MonthlyHardTokenLimit is < 0
            || request.MonthlySoftTokenLimit is { } soft && request.MonthlyHardTokenLimit is { } hard && soft > hard)
            return BadRequest(new { error = "Token limits must be non-negative and soft cannot exceed hard." });
        if (request.MaxTokensPerDocument is <= 0 || request.ExternalDependencyCeilingPercent is < 0 or > 10
            || request.RetentionDays is < 1 or > 3650
            || string.IsNullOrWhiteSpace(request.AllowedDataClassifications)
            || string.IsNullOrWhiteSpace(request.EgressPolicy)
            || string.IsNullOrWhiteSpace(request.DataResidency))
            return BadRequest(new { error = "AI privacy, residency, retention and budget controls are invalid." });
        if (request.ExternalProcessingAllowed && (!request.RedactionRequired || !request.PrivacyReviewRequired
            || string.IsNullOrWhiteSpace(request.AllowedProvider) || string.IsNullOrWhiteSpace(request.AllowedModel)))
            return BadRequest(new { error = "External processing requires redaction, privacy review, provider and model." });
        if ((request.ExternalInputCostPerMillionTokens.HasValue || request.ExternalOutputCostPerMillionTokens.HasValue)
            && (request.ExternalInputCostPerMillionTokens is null or < 0
                || request.ExternalOutputCostPerMillionTokens is null or < 0
                || string.IsNullOrWhiteSpace(request.ExternalCostCurrency)
                || string.IsNullOrWhiteSpace(request.ExternalPricingVersion)))
            return BadRequest(new { error = "External rates require input/output rates, currency and pricing version." });
        if ((request.LocalComputeCostPerHour.HasValue || request.OcrCostPerPage.HasValue)
            && (request.LocalComputeCostPerHour is null or < 0 || request.OcrCostPerPage is null or < 0
                || string.IsNullOrWhiteSpace(request.LocalCostCurrency)))
            return BadRequest(new { error = "Local rates require compute/OCR rates and currency." });

        var allowedPurposeSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { AiPurposes.RfqExtraction, AiPurposes.BoqDraft, AiPurposes.Agent };
        var purposes = (request.AllowedPurposes ?? [])
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (purposes.Any(p => !allowedPurposeSet.Contains(p)))
            return BadRequest(new { error = "One or more AI purposes are invalid." });
        if (request.ExternalProcessingAllowed && purposes.Length == 0)
            return BadRequest(new { error = "At least one purpose is required when external processing is allowed." });

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant?.PrimaryBusinessUnitId is not long businessUnitId)
            return NotFound();
        // This Owner mutation remains on the platform execution role and predicates every
        // tenant-owned read by the business unit derived from the authenticated route
        // mapping. An ambient tenant scope would downgrade the connection to
        // nexora_tenant_app, which cannot forge PlatformAudit rows and therefore cannot
        // atomically perform this control-plane operation.
        using var scope = _scopeFactory.CreateScope();
        var tenantDb = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var policy = await tenantDb.AiProcessingPolicies
            .SingleOrDefaultAsync(p => p.BusinessUnitId == businessUnitId, ct);
        if (policy is null)
            return Conflict(new { error = "The tenant AI policy has not been provisioned." });
        if (policy.Version != request.Version)
            return Conflict(new { error = "The AI policy changed; reload and try again.", currentVersion = policy.Version });

        var before = ToAiPolicyDto(policy);
        policy.IsEnabled = request.IsEnabled;
        policy.ExternalProcessingAllowed = request.ExternalProcessingAllowed;
        policy.AllowedPurposes = string.Join(',', purposes);
        policy.AllowedProvider = Normalize(request.AllowedProvider);
        policy.AllowedModel = Normalize(request.AllowedModel);
        policy.MonthlySoftTokenLimit = request.MonthlySoftTokenLimit;
        policy.MonthlyHardTokenLimit = request.MonthlyHardTokenLimit;
        policy.MaxTokensPerDocument = request.MaxTokensPerDocument;
        policy.ExternalInputCostPerMillionTokens = request.ExternalInputCostPerMillionTokens;
        policy.ExternalOutputCostPerMillionTokens = request.ExternalOutputCostPerMillionTokens;
        policy.ExternalCostCurrency = Normalize(request.ExternalCostCurrency)?.ToUpperInvariant();
        policy.ExternalPricingVersion = Normalize(request.ExternalPricingVersion);
        policy.ExternalDependencyCeilingPercent = request.ExternalDependencyCeilingPercent;
        policy.RedactionRequired = request.RedactionRequired;
        policy.AllowedDataClassifications = request.AllowedDataClassifications.Trim();
        policy.EgressPolicy = request.EgressPolicy.Trim();
        policy.DataResidency = request.DataResidency.Trim();
        policy.RetentionDays = request.RetentionDays;
        policy.InputOutputAuditAllowed = request.InputOutputAuditAllowed;
        policy.PrivacyReviewRequired = request.PrivacyReviewRequired;
        policy.LocalComputeCostPerHour = request.LocalComputeCostPerHour;
        policy.OcrCostPerPage = request.OcrCostPerPage;
        policy.LocalCostCurrency = Normalize(request.LocalCostCurrency)?.ToUpperInvariant();
        policy.Version++;
        policy.UpdatedOn = DateTime.UtcNow;
        policy.UpdatedBy = User.FindFirst("email")?.Value ?? "platform";
        var after = ToAiPolicyDto(policy);

        // Audit through PlatformAuditService so the positive-actor invariant is
        // enforced (a row can no longer be silently written with actor id 0). The
        // service is instantiated over the SAME tenant-scoped context, so its
        // SaveChanges persists the policy mutation and the audit row atomically.
        var scopedAudit = new PlatformAuditService(
            tenantDb, scope.ServiceProvider.GetRequiredService<ILogger<PlatformAuditService>>());
        await scopedAudit.WriteAsync(User, "tenant.ai-policy.update", nameof(AiProcessingPolicy),
            businessUnitId.ToString(), new { before, after, reason = request.Reason.Trim() },
            actAsTenantId: id, httpContext: HttpContext, ct: ct);
        return Ok(after);
    }

    [HttpGet("{id:long}/ai-providers")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<AiExternalProviderTrustView>> GetAiProviders(long id, CancellationToken ct)
    {
        var businessUnitId = await PrimaryBusinessUnitIdAsync(id, ct);
        if (businessUnitId is null) return NotFound();
        using var tenantScope = _tenantScope.Push(businessUnitId.Value);
        using var scope = _scopeFactory.CreateScope();
        return Ok(await scope.ServiceProvider.GetRequiredService<AiExternalProviderTrustService>()
            .GetAsync(businessUnitId.Value, ct));
    }

    [HttpPost("{id:long}/ai-providers")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<AiExternalProviderMutationResult>> AuthorizeAiProvider(
        long id, [FromBody] AuthorizeAiExternalProviderCommand request, CancellationToken ct)
    {
        var businessUnitId = await PrimaryBusinessUnitIdAsync(id, ct);
        if (businessUnitId is null) return NotFound();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedAudit = scope.ServiceProvider.GetRequiredService<IPlatformAuditService>();
            var trust = scope.ServiceProvider.GetRequiredService<AiExternalProviderTrustService>();
            var result = await trust
                .AuthorizeAsync(businessUnitId.Value, CurrentPlatformActorId(), IdempotencyKey(), request, ct,
                    (authorization, innerCt) => scopedAudit.WriteAsync(User,
                        "tenant.ai-provider.authorize", nameof(AiExternalProviderAuthorization),
                        authorization.Id.ToString(), new { tenantId = id, authorization.Provider,
                            authorization.Endpoint, authorization.Model, request.Justification },
                        actAsTenantId: id, httpContext: HttpContext, ct: innerCt));
            return Ok(result);
        }
        catch (PlatformGovernanceValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (PlatformGovernanceConflictException ex) { return Conflict(new { error = ex.Message }); }
    }

    [HttpPost("{id:long}/ai-providers/{authorizationId:long}/revoke")]
    [Authorize(Policy = PlatformPolicies.Owner)]
    public async Task<ActionResult<AiExternalProviderMutationResult>> RevokeAiProvider(
        long id, long authorizationId, [FromBody] RevokeAiExternalProviderCommand request, CancellationToken ct)
    {
        if (authorizationId != request.AuthorizationId)
            return BadRequest(new { error = "The route and request authorization ids must match." });
        var businessUnitId = await PrimaryBusinessUnitIdAsync(id, ct);
        if (businessUnitId is null) return NotFound();
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedAudit = scope.ServiceProvider.GetRequiredService<IPlatformAuditService>();
            var trust = scope.ServiceProvider.GetRequiredService<AiExternalProviderTrustService>();
            var result = await trust
                .RevokeAsync(businessUnitId.Value, CurrentPlatformActorId(), IdempotencyKey(), request, ct,
                    (authorization, innerCt) => scopedAudit.WriteAsync(User,
                        "tenant.ai-provider.revoke", nameof(AiExternalProviderAuthorization),
                        authorization.Id.ToString(), new { tenantId = id, request.Reason },
                        actAsTenantId: id, httpContext: HttpContext, ct: innerCt));
            return Ok(result);
        }
        catch (PlatformGovernanceValidationException ex) { return BadRequest(new { error = ex.Message }); }
        catch (PlatformGovernanceNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    // POST /api/platform/tenants/{id}/suspend
    [HttpPost("{id:long}/suspend")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Suspend(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, [TenantStatus.Active, TenantStatus.PastDue], TenantStatus.Suspended,
            request?.Reason, "tenant.suspend", "suspended", ct);

    // POST /api/platform/tenants/{id}/resume
    [HttpPost("{id:long}/resume")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Resume(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, [TenantStatus.Suspended], TenantStatus.Active, request?.Reason, "tenant.resume", "resumed", ct);

    // POST /api/platform/tenants/{id}/archive  (Suspended -> Archived)
    [HttpPost("{id:long}/archive")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Archive(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, [TenantStatus.Suspended], TenantStatus.Archived, request?.Reason, "tenant.archive", "archived", ct);

    // POST /api/platform/tenants/{id}/restore  (Archived -> Suspended)
    [HttpPost("{id:long}/restore")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Restore(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, [TenantStatus.Archived], TenantStatus.Suspended, request?.Reason, "tenant.restore", "restored", ct);

    // POST /api/platform/tenants/{id}/mark-past-due (Active -> PastDue)
    [HttpPost("{id:long}/mark-past-due")]
    [Authorize(Policy = PlatformPolicies.Billing)]
    public Task<ActionResult<TenantSummaryDto>> MarkPastDue(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, [TenantStatus.Active], TenantStatus.PastDue,
            request?.Reason, "tenant.past-due", "marked past due", ct);

    // POST /api/platform/tenants/{id}/resolve-past-due (PastDue -> Active)
    [HttpPost("{id:long}/resolve-past-due")]
    [Authorize(Policy = PlatformPolicies.Billing)]
    public Task<ActionResult<TenantSummaryDto>> ResolvePastDue(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, [TenantStatus.PastDue], TenantStatus.Active,
            request?.Reason, "tenant.past-due.resolve", "returned to current standing", ct);

    // PUT /api/platform/tenants/{id}/plan
    // Sec9: plan assignment is a BILLING operation (Owner | BillingAdmin), not a
    // support-tenant-admin one — SupportAdmin must not be able to change what a
    // customer is charged/entitled to.
    [HttpPut("{id:long}/plan")]
    [Authorize(Policy = PlatformPolicies.Billing)]
    public async Task<ActionResult<TenantSummaryDto>> ChangePlan(
        long id, [FromBody] ChangeTenantPlanRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var plan = await _context.Set<Plan>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == request.PlanId, ct);
        if (plan is null)
            return BadRequest(new { error = $"Plan {request.PlanId} does not exist." });
        if (!plan.IsActive)
            return BadRequest(new { error = $"Plan '{plan.Code}' is not active and cannot be assigned." });

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == id, ct);
                if (tenant is null)
                    throw new TenantNotFoundException();

                var previousPlanId = tenant.PlanId;
                tenant.PlanId = plan.Id;
                tenant.ModifiedBy = User.FindFirst("email")?.Value ?? "platform";
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, "tenant.plan.change", nameof(Tenant), tenant.Id.ToString(),
                    new { fromPlanId = previousPlanId, toPlanId = plan.Id, planCode = plan.Code, reason = request.Reason },
                    actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

                await tx.CommitAsync(ct);
            });
        }
        catch (TenantNotFoundException)
        {
            return NotFound();
        }
        catch (Exception)
        {
            _logger.LogError("Failed to change tenant {TenantId} plan to {PlanId}", id, request.PlanId);
            return StatusCode(500, new { error = "Tenant plan change failed." });
        }

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == id, ct);
        // P2-A7 (same rationale as status changes): drop the cached tenant+plan snapshot
        // so the new plan's limits apply immediately on this node.
        if (withPlan.PrimaryBusinessUnitId is long planChangedBu)
            _tenantAccess?.Evict(planChangedBu);
        return Ok(ToDto(withPlan));
    }

    private async Task<ActionResult<TenantSummaryDto>> ChangeStatus(
        long id, TenantStatus[] requiredCurrent, TenantStatus target, string? reason,
        string action, string operationVerb, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return BadRequest(new { error = "A reason is required." });

        var strategy = _context.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                // Reload inside every retry attempt. Reusing an entity that passed
                // through SaveChanges in a rolled-back attempt can otherwise commit
                // only the audit row while the tenant remains incorrectly Unchanged.
                _context.ChangeTracker.Clear();
                await using var tx = await _context.Database.BeginTransactionAsync(ct);

                var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
                    .FirstOrDefaultAsync(t => t.Id == id, ct);
                if (tenant is null)
                    throw new TenantNotFoundException();

                // Validated lifecycle graph: Active <-> Suspended <-> Archived.
                if (!requiredCurrent.Contains(tenant.Status))
                    throw new InvalidTenantStatusTransitionException(
                        tenant.Status, string.Join(" or ", requiredCurrent), operationVerb);

                if (target == TenantStatus.Active && _activationPolicy is not null)
                {
                    var activation = await _activationPolicy.EvaluateAsync(tenant.Id, ct)
                                     ?? throw new TenantNotFoundException();
                    if (!activation.Ready)
                        throw new TenantActivationBlockedException(activation);
                }

                var cancelsScheduledDeletion = action is "tenant.restore" or "tenant.resume";
                var deletionCancelled = false;
                if (cancelsScheduledDeletion)
                {
                    // Restore/resume and purge coordinate through the same offboarding row.
                    // This conditional UPDATE takes that row's database lock. If restore wins,
                    // purge's claim no longer sees PendingDeletion. If purge wins, this update
                    // resumes after the claim commits, sees PurgeStartedOn, and refuses to bring
                    // a tenant back while its data may already be disappearing.
                    var now = DateTime.UtcNow;
                    var stalePurgeCutoff = now - TenantOffboardingService.PurgeLease;
                    deletionCancelled = await _context.Set<TenantOffboarding>()
                        .Where(r => r.TenantId == tenant.Id
                                    && r.Stage == TenantOffboardingStage.PendingDeletion
                                    && r.PurgeExecutedOn == null
                                    && (r.PurgeStartedOn == null || r.PurgeStartedOn <= stalePurgeCutoff))
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(r => r.Stage, TenantOffboardingStage.NotScheduled)
                            .SetProperty(r => r.PurgeStartedOn, (DateTime?)null)
                            .SetProperty(r => r.PurgeAttemptId, (Guid?)null)
                            .SetProperty(r => r.PurgeReason, (string?)null)
                            .SetProperty(r => r.RetentionDays, (int?)null)
                            .SetProperty(r => r.DeletionScheduledOn, (DateTime?)null)
                            .SetProperty(r => r.PurgeEligibleOn, (DateTime?)null)
                            .SetProperty(r => r.DeletionReason, (string?)null)
                            .SetProperty(r => r.DeletionScheduledBy, (string?)null)
                            .SetProperty(r => r.ModifiedOn, now), ct) == 1;

                    var purgeInProgress = await _context.Set<TenantOffboarding>().AsNoTracking()
                        .AnyAsync(r => r.TenantId == tenant.Id
                                       && r.Stage == TenantOffboardingStage.PendingDeletion
                                       && r.PurgeStartedOn != null, ct);
                    if (purgeInProgress)
                        throw new InvalidTenantStatusTransitionException(
                            tenant.Status, requiredCurrent.ToString(), operationVerb,
                            "A purge is already in progress; restore or resume is blocked until it finishes.");
                }

                var previous = tenant.Status;
                tenant.Status = target;
                tenant.StatusReason = reason;
                tenant.ModifiedBy = User.FindFirst("email")?.Value ?? "platform";
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, action, nameof(Tenant), tenant.Id.ToString(),
                    new { from = previous.ToString(), to = target.ToString(), reason, deletionCancelled },
                    actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

                await tx.CommitAsync(ct);
            });
        }
        catch (TenantNotFoundException)
        {
            return NotFound();
        }
        catch (TenantActivationBlockedException blocked)
        {
            return Conflict(new
            {
                error = "Tenant resume is blocked by the authoritative activation policy.",
                decision = blocked.Decision
            });
        }
        catch (InvalidTenantStatusTransitionException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (Exception)
        {
            _logger.LogError("Failed to change tenant {TenantId} status with action {Action}", id, action);
            return StatusCode(500, new { error = "Tenant status change failed." });
        }

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == id, ct);
        // P2-A7: evict the ~60s tenant-access cache entry for this tenant's primary
        // BusinessUnit so suspension (and every other lifecycle transition) is
        // enforced IMMEDIATELY on this node. Other instances converge within the
        // cache TTL — a documented cross-instance bound.
        if (withPlan.PrimaryBusinessUnitId is long statusChangedBu)
            _tenantAccess?.Evict(statusChangedBu);
        return Ok(ToDto(withPlan));
    }

    private sealed class TenantNotFoundException : Exception;

    private sealed class InvalidTenantStatusTransitionException : Exception
    {
        public InvalidTenantStatusTransitionException(
            TenantStatus current, string required, string operation, string? detail = null)
            : base(detail ?? $"Only a {required} tenant can be {operation} (current: {current}).")
        {
        }
    }

    private static TenantSummaryDto ToDto(Tenant t) => new()
    {
        Id = t.Id,
        Name = t.Name,
        Slug = t.Slug,
        Status = t.Status.ToString(),
        PlanId = t.PlanId,
        PlanCode = t.Plan?.Code,
        PrimaryBusinessUnitId = t.PrimaryBusinessUnitId,
        CreatedOn = t.CreatedOn,
        StatusReason = t.StatusReason,

        LegalName = t.LegalName,
        RegistrationNumber = t.RegistrationNumber,
        TaxNumber = t.TaxNumber,
        CountryCode = t.CountryCode,
        Industry = t.Industry,
        Website = t.Website,
        AddressLine1 = t.AddressLine1,
        AddressLine2 = t.AddressLine2,
        City = t.City,
        StateProvince = t.StateProvince,
        PostalCode = t.PostalCode,
        Phone = t.Phone,
        ContactEmail = t.ContactEmail,
        LogoUrl = t.LogoUrl,

        BaseCurrencyCode = t.BaseCurrencyCode,
        TimeZoneId = t.TimeZoneId,
        Locale = t.Locale,
        DataRegion = t.DataRegion,

        BillingMode = t.BillingMode.ToString(),
        BillingModeReason = t.BillingModeReason,
        RateCardId = t.RateCardId,
        BillingStartsOn = t.BillingStartsOn,
        TrialEndsOn = t.TrialEndsOn,
        ContractStartOn = t.ContractStartOn,
        ContractEndOn = t.ContractEndOn,
        PaymentTermsDays = t.PaymentTermsDays,
        PurchaseOrderReference = t.PurchaseOrderReference,
        BillingContactName = t.BillingContactName,
        BillingContactEmail = t.BillingContactEmail,
        BillingAddress = t.BillingAddress,
        AccountOwnerEmail = t.AccountOwnerEmail
    };

    private static TenantAiPolicyDto ToAiPolicyDto(AiProcessingPolicy p) => new()
    {
        BusinessUnitId = p.BusinessUnitId,
        IsEnabled = p.IsEnabled,
        ExternalProcessingAllowed = p.ExternalProcessingAllowed,
        AllowedPurposes = p.AllowedPurposes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        AllowedProvider = p.AllowedProvider,
        AllowedModel = p.AllowedModel,
        MonthlySoftTokenLimit = p.MonthlySoftTokenLimit,
        MonthlyHardTokenLimit = p.MonthlyHardTokenLimit,
        MaxTokensPerDocument = p.MaxTokensPerDocument,
        ExternalInputCostPerMillionTokens = p.ExternalInputCostPerMillionTokens,
        ExternalOutputCostPerMillionTokens = p.ExternalOutputCostPerMillionTokens,
        ExternalCostCurrency = p.ExternalCostCurrency,
        ExternalPricingVersion = p.ExternalPricingVersion,
        ExternalDependencyCeilingPercent = p.ExternalDependencyCeilingPercent,
        RedactionRequired = p.RedactionRequired,
        AllowedDataClassifications = p.AllowedDataClassifications,
        EgressPolicy = p.EgressPolicy,
        DataResidency = p.DataResidency,
        RetentionDays = p.RetentionDays,
        InputOutputAuditAllowed = p.InputOutputAuditAllowed,
        PrivacyReviewRequired = p.PrivacyReviewRequired,
        LocalComputeCostPerHour = p.LocalComputeCostPerHour,
        OcrCostPerPage = p.OcrCostPerPage,
        LocalCostCurrency = p.LocalCostCurrency,
        Version = p.Version,
        UpdatedOn = p.UpdatedOn,
        UpdatedBy = p.UpdatedBy
    };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async Task<long?> PrimaryBusinessUnitIdAsync(long tenantId, CancellationToken ct) =>
        await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(tenant => tenant.Id == tenantId)
            .Select(tenant => tenant.PrimaryBusinessUnitId)
            .SingleOrDefaultAsync(ct);

    private long CurrentPlatformActorId() =>
        long.TryParse(User.FindFirst("sub")?.Value
                      ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
            out var actorId) && actorId > 0
            ? actorId
            : throw new UnauthorizedAccessException("A positive platform actor id is required.");

    private string IdempotencyKey() =>
        Request.Headers.TryGetValue("Idempotency-Key", out var value)
            ? value.ToString()
            : string.Empty;

    private static string Slugify(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
