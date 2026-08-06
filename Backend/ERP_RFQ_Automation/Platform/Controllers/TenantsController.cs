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
    private readonly Entitlements.ITenantAccessService? _tenantAccess;

    public TenantsController(
        ErpRfqAutomationContext context, IPlatformAuditService audit, ILogger<TenantsController> logger,
        IServiceScopeFactory scopeFactory, ITenantScopeAccessor tenantScope,
        Entitlements.ITenantAccessService? tenantAccess = null)
    {
        _context = context;
        _audit = audit;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
        _tenantAccess = tenantAccess;
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

    // POST /api/platform/tenants  (provision)
    [HttpPost]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<TenantSummaryDto>> Provision(
        [FromBody] ProvisionTenantRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var slug = Slugify(string.IsNullOrWhiteSpace(request.Slug) ? request.Name : request.Slug);
        if (string.IsNullOrEmpty(slug))
            return BadRequest(new { error = "Could not derive a valid slug from the name." });

        var slugTaken = await _context.Set<Tenant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == slug, ct);
        if (slugTaken)
            return Conflict(new { error = $"A tenant with slug '{slug}' already exists." });

        if (request.PlanId is long planId &&
            !await _context.Set<Plan>().AnyAsync(p => p.Id == planId, ct))
            return BadRequest(new { error = $"Plan {planId} does not exist." });

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

        // Generated when the operator did not supply one: returned exactly once in the response,
        // stored only as a BCrypt hash. Hashing happens OUTSIDE the retriable transaction — BCrypt
        // mints a fresh salt per call, and the stored hash must not change between retry attempts.
        var generatedPassword = string.IsNullOrEmpty(request.AdminPassword)
            ? GenerateInitialPassword()
            : null;
        var adminPasswordHash = BCrypt.Net.BCrypt.HashPassword(generatedPassword ?? request.AdminPassword);

        var actor = User.FindFirst("email")?.Value ?? "platform";
        Tenant created;
        User foundingAdmin = null!;
        SetupMaster foundingRole = null!;

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
                    CreatedOn = DateTime.UtcNow
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
                    IsActive = true,
                    CreatedBy = actor,
                    CreatedOn = DateTime.UtcNow
                };
                _context.Users.Add(foundingAdmin);
                await _context.SaveChangesAsync(ct);

                tenant.PrimaryBusinessUnitId = bu.Id;
                tenant.Status = TenantStatus.Active;
                tenant.ModifiedBy = actor;
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                // The admin's EMAIL is audited; the password never is, generated or not.
                await _audit.WriteAsync(User, "tenant.provision", nameof(Tenant), tenant.Id.ToString(),
                    new
                    {
                        tenant.Name, tenant.Slug, tenant.PlanId, tenant.PrimaryBusinessUnitId,
                        AdminEmail = adminEmail, AdminUserId = foundingAdmin.Id,
                        PasswordGenerated = generatedPassword is not null
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

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == created.Id, ct);

        // ProvisionTenantResponse, not TenantSummaryDto: the generated password appears in THIS
        // response and nowhere else, ever — not on list, not on get, not in the audit log. Only a
        // BCrypt hash is stored, so there is nothing to retrieve later by design.
        return CreatedAtAction(nameof(Get), new { id = created.Id }, new ProvisionTenantResponse
        {
            Tenant = ToDto(withPlan),
            FoundingAdmin = new FoundingAdminDto
            {
                UserId = foundingAdmin.Id,
                Email = foundingAdmin.Email,
                RoleName = foundingRole.SetupValue,
                GeneratedPassword = generatedPassword
            }
        });
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
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<TenantAiPolicyDto>> UpdateAiPolicy(
        long id, [FromBody] UpdateTenantAiPolicyRequest request, CancellationToken ct)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new { error = "A reason is required." });
        if (request.MonthlySoftTokenLimit is < 0 || request.MonthlyHardTokenLimit is < 0
            || request.MonthlySoftTokenLimit is { } soft && request.MonthlyHardTokenLimit is { } hard && soft > hard)
            return BadRequest(new { error = "Token limits must be non-negative and soft cannot exceed hard." });

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
        using var tenantScope = _tenantScope.Push(businessUnitId);
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

    // POST /api/platform/tenants/{id}/suspend
    [HttpPost("{id:long}/suspend")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Suspend(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, TenantStatus.Active, TenantStatus.Suspended, request?.Reason, "tenant.suspend", "suspended", ct);

    // POST /api/platform/tenants/{id}/resume
    [HttpPost("{id:long}/resume")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Resume(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, TenantStatus.Suspended, TenantStatus.Active, request?.Reason, "tenant.resume", "resumed", ct);

    // POST /api/platform/tenants/{id}/archive  (Suspended -> Archived)
    [HttpPost("{id:long}/archive")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Archive(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, TenantStatus.Suspended, TenantStatus.Archived, request?.Reason, "tenant.archive", "archived", ct);

    // POST /api/platform/tenants/{id}/restore  (Archived -> Suspended)
    [HttpPost("{id:long}/restore")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Restore(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, TenantStatus.Archived, TenantStatus.Suspended, request?.Reason, "tenant.restore", "restored", ct);

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
        long id, TenantStatus requiredCurrent, TenantStatus target, string? reason,
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
                if (tenant.Status != requiredCurrent)
                    throw new InvalidTenantStatusTransitionException(
                        tenant.Status, requiredCurrent.ToString(), operationVerb);

                var previous = tenant.Status;
                tenant.Status = target;
                tenant.StatusReason = reason;
                tenant.ModifiedBy = User.FindFirst("email")?.Value ?? "platform";
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, action, nameof(Tenant), tenant.Id.ToString(),
                    new { from = previous.ToString(), to = target.ToString(), reason },
                    actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

                await tx.CommitAsync(ct);
            });
        }
        catch (TenantNotFoundException)
        {
            return NotFound();
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
            TenantStatus current, string required, string operation)
            : base($"Only a {required} tenant can be {operation} (current: {current}).")
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
        StatusReason = t.StatusReason
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
        Version = p.Version,
        UpdatedOn = p.UpdatedOn,
        UpdatedBy = p.UpdatedBy
    };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Slugify(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
