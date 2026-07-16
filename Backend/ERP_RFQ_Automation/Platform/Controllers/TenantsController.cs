using System.Text.RegularExpressions;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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

    public TenantsController(
        ErpRfqAutomationContext context, IPlatformAuditService audit, ILogger<TenantsController> logger)
    {
        _context = context;
        _audit = audit;
        _logger = logger;
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
                await _context.SaveChangesAsync(ct);

                tenant.PrimaryBusinessUnitId = bu.Id;
                tenant.Status = TenantStatus.Active;
                tenant.ModifiedBy = actor;
                tenant.ModifiedOn = DateTime.UtcNow;
                await _context.SaveChangesAsync(ct);

                await tx.CommitAsync(ct);
                return tenant;
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to provision tenant {Slug}", slug);
            return StatusCode(500, new { error = "Provisioning failed." });
        }

        await _audit.WriteAsync(User, "tenant.provision", nameof(Tenant), created.Id.ToString(),
            new { created.Name, created.Slug, created.PlanId, created.PrimaryBusinessUnitId },
            actAsTenantId: created.Id, httpContext: HttpContext, ct: ct);

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == created.Id, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, ToDto(withPlan));
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

        var tenant = await _context.Set<Tenant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id, ct);
        if (tenant is null)
            return NotFound();

        if (target == TenantStatus.Suspended && tenant.Status != TenantStatus.Active)
            return Conflict(new { error = $"Only an Active tenant can be suspended (current: {tenant.Status})." });
        if (target == TenantStatus.Active && tenant.Status != TenantStatus.Suspended)
            return Conflict(new { error = $"Only a Suspended tenant can be resumed (current: {tenant.Status})." });

        var previous = tenant.Status;
        tenant.Status = target;
        tenant.StatusReason = reason;
        tenant.ModifiedBy = User.FindFirst("email")?.Value ?? "platform";
        tenant.ModifiedOn = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        await _audit.WriteAsync(User, action, nameof(Tenant), tenant.Id.ToString(),
            new { from = previous.ToString(), to = target.ToString(), reason },
            actAsTenantId: tenant.Id, httpContext: HttpContext, ct: ct);

        var withPlan = await _context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(t => t.Plan).FirstAsync(t => t.Id == tenant.Id, ct);
        return Ok(ToDto(withPlan));
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

    private static string Slugify(string input)
    {
        var lowered = input.Trim().ToLowerInvariant();
        var slug = Regex.Replace(lowered, @"[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 60 ? slug[..60].Trim('-') : slug;
    }
}
