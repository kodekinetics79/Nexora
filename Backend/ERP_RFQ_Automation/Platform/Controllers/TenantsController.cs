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
using System.Security.Claims;
using System.Text.Json;
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

    public TenantsController(
        ErpRfqAutomationContext context, IPlatformAuditService audit, ILogger<TenantsController> logger,
        IServiceScopeFactory scopeFactory, ITenantScopeAccessor tenantScope)
    {
        _context = context;
        _audit = audit;
        _logger = logger;
        _scopeFactory = scopeFactory;
        _tenantScope = tenantScope;
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

        var actor = User.FindFirst("email")?.Value ?? "platform";
        Tenant created;

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

                _context.AiProcessingPolicies.Add(
                    AiProcessingPolicy.CreateSecureDefault(bu.Id, actor, DateTime.UtcNow));

                tenant.PrimaryBusinessUnitId = bu.Id;
                tenant.Status = TenantStatus.Active;
                tenant.ModifiedBy = actor;
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                await _audit.WriteAsync(User, "tenant.provision", nameof(Tenant), tenant.Id.ToString(),
                    new { tenant.Name, tenant.Slug, tenant.PlanId, tenant.PrimaryBusinessUnitId },
                    actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

                await tx.CommitAsync(ct);
                return tenant;
            });
        }
        catch (Exception)
        {
            _logger.LogError("Failed to provision tenant {Slug}", slug);
            return StatusCode(500, new { error = "Provisioning failed." });
        }

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == created.Id, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ToDto(withPlan));
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
        var subject = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        long.TryParse(subject, out var actorId);
        tenantDb.Set<PlatformAuditLog>().Add(new PlatformAuditLog
        {
            ActorPlatformUserId = actorId,
            ActAsTenantId = id,
            Action = "tenant.ai-policy.update",
            TargetType = nameof(AiProcessingPolicy),
            TargetId = businessUnitId.ToString(),
            Metadata = JsonSerializer.Serialize(new { before, after, reason = request.Reason.Trim() }),
            Ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
            CreatedOn = DateTime.UtcNow
        });
        await tenantDb.SaveChangesAsync(ct);
        return Ok(after);
    }

    // POST /api/platform/tenants/{id}/suspend
    [HttpPost("{id:long}/suspend")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Suspend(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, TenantStatus.Suspended, request?.Reason, "tenant.suspend", ct);

    // POST /api/platform/tenants/{id}/resume
    [HttpPost("{id:long}/resume")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<TenantSummaryDto>> Resume(
        long id, [FromBody] TenantStatusChangeRequest request, CancellationToken ct) =>
        ChangeStatus(id, TenantStatus.Active, request?.Reason, "tenant.resume", ct);

    private async Task<ActionResult<TenantSummaryDto>> ChangeStatus(
        long id, TenantStatus target, string? reason, string action, CancellationToken ct)
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

                if (target == TenantStatus.Suspended && tenant.Status != TenantStatus.Active)
                    throw new InvalidTenantStatusTransitionException(tenant.Status, "Active", "suspended");
                if (target == TenantStatus.Active && tenant.Status != TenantStatus.Suspended)
                    throw new InvalidTenantStatusTransitionException(tenant.Status, "Suspended", "resumed");

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
