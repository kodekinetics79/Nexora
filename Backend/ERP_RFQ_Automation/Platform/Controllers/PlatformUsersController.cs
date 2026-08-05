using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Controllers;

/// <summary>
/// Platform operator management (control-plane accounts, NOT tenant users).
/// Owner-only end to end. Safety rails: an Owner can never deactivate their own
/// account, and the LAST ACTIVE Owner can be neither deactivated nor demoted —
/// the platform plane must always retain at least one active Owner. Passwords
/// use the same BCrypt scheme verified by <c>PlatformAuthService</c> at login.
/// Every mutation is written to the platform audit trail. (ADR-0005 §3)
/// </summary>
[ApiController]
[Route("api/platform/users")]
[Authorize(Policy = PlatformPolicies.Owner)]
public class PlatformUsersController : ControllerBase
{
    private readonly ErpRfqAutomationContext _context;
    private readonly IPlatformAuditService _audit;

    public PlatformUsersController(ErpRfqAutomationContext context, IPlatformAuditService audit)
    {
        _context = context;
        _audit = audit;
    }

    /// <summary>
    /// Sec3: every platform-user mutation and its audit record commit in ONE
    /// transaction — an audit-write failure rolls the mutation back, and a committed
    /// mutation can never be missing its trail. The body runs inside the context's
    /// execution strategy (EnableRetryOnFailure requires it) with a fresh change
    /// tracker per attempt.
    /// </summary>
    private async Task ExecuteAuditedAsync(Func<Task> mutateAndAudit, CancellationToken ct)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            _context.ChangeTracker.Clear();
            await using var tx = await _context.Database.BeginTransactionAsync(ct);
            await mutateAndAudit();
            await tx.CommitAsync(ct);
        });
    }

    // GET /api/platform/users
    [HttpGet]
    public async Task<ActionResult<IEnumerable<PlatformUserDto>>> List(CancellationToken ct)
    {
        var users = await _context.Set<PlatformUser>().AsNoTracking()
            .OrderBy(u => u.Email).ToListAsync(ct);
        return Ok(users.Select(ToDto));
    }

    // POST /api/platform/users
    [HttpPost]
    public async Task<ActionResult<PlatformUserDto>> Create(
        [FromBody] CreatePlatformUserRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!Enum.TryParse<PlatformRole>(request.Role, true, out var role))
            return BadRequest(new { error = $"Unknown platform role '{request.Role}'." });

        var email = request.Email.Trim();
        var emailTaken = await _context.Set<PlatformUser>()
            .AnyAsync(u => u.Email.ToLower() == email.ToLower(), ct);
        if (emailTaken)
            return Conflict(new { error = $"A platform user with email '{email}' already exists." });

        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            PlatformRole = role,
            IsActive = true,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim(),
            CreatedBy = User.FindFirst("email")?.Value ?? "platform",
            CreatedOn = DateTime.UtcNow
        };
        await ExecuteAuditedAsync(async () =>
        {
            _context.Set<PlatformUser>().Add(user);
            await _context.SaveChangesAsync(ct);

            await _audit.WriteAsync(User, "platform-user.create", nameof(PlatformUser), user.Id.ToString(),
                new { user.Email, role = user.PlatformRole.ToString() }, httpContext: HttpContext, ct: ct);
        }, ct);

        return CreatedAtAction(nameof(List), new { }, ToDto(user));
    }

    // PUT /api/platform/users/{id}/role
    [HttpPut("{id:long}/role")]
    public async Task<ActionResult<PlatformUserDto>> ChangeRole(
        long id, [FromBody] ChangePlatformUserRoleRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (!Enum.TryParse<PlatformRole>(request.Role, true, out var role))
            return BadRequest(new { error = $"Unknown platform role '{request.Role}'." });

        var user = await _context.Set<PlatformUser>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound();

        if (user.PlatformRole == PlatformRole.Owner && role != PlatformRole.Owner
            && user.IsActive && !await AnotherActiveOwnerExistsAsync(user.Id, ct))
            return Conflict(new { error = "The last active platform Owner cannot be demoted." });

        var previous = user.PlatformRole;
        await ExecuteAuditedAsync(async () =>
        {
            user.PlatformRole = role;
            _context.Set<PlatformUser>().Update(user);
            await _context.SaveChangesAsync(ct);

            await _audit.WriteAsync(User, "platform-user.role.change", nameof(PlatformUser), user.Id.ToString(),
                new { user.Email, from = previous.ToString(), to = role.ToString() }, httpContext: HttpContext, ct: ct);
        }, ct);

        return Ok(ToDto(user));
    }

    // POST /api/platform/users/{id}/deactivate
    [HttpPost("{id:long}/deactivate")]
    public async Task<ActionResult<PlatformUserDto>> Deactivate(long id, CancellationToken ct)
    {
        var user = await _context.Set<PlatformUser>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound();

        var selfId = CurrentActorId();
        if (selfId == user.Id)
            return Conflict(new { error = "You cannot deactivate your own platform account." });

        if (user.IsActive && user.PlatformRole == PlatformRole.Owner
            && !await AnotherActiveOwnerExistsAsync(user.Id, ct))
            return Conflict(new { error = "The last active platform Owner cannot be deactivated." });

        if (user.IsActive)
        {
            await ExecuteAuditedAsync(async () =>
            {
                user.IsActive = false;
                _context.Set<PlatformUser>().Update(user);
                await _context.SaveChangesAsync(ct);
                await _audit.WriteAsync(User, "platform-user.deactivate", nameof(PlatformUser), user.Id.ToString(),
                    new { user.Email }, httpContext: HttpContext, ct: ct);
            }, ct);
        }

        return Ok(ToDto(user));
    }

    // POST /api/platform/users/{id}/reactivate
    [HttpPost("{id:long}/reactivate")]
    public async Task<ActionResult<PlatformUserDto>> Reactivate(long id, CancellationToken ct)
    {
        var user = await _context.Set<PlatformUser>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound();

        if (!user.IsActive)
        {
            await ExecuteAuditedAsync(async () =>
            {
                user.IsActive = true;
                _context.Set<PlatformUser>().Update(user);
                await _context.SaveChangesAsync(ct);
                await _audit.WriteAsync(User, "platform-user.reactivate", nameof(PlatformUser), user.Id.ToString(),
                    new { user.Email }, httpContext: HttpContext, ct: ct);
            }, ct);
        }

        return Ok(ToDto(user));
    }

    // POST /api/platform/users/{id}/password  (administrative reset)
    [HttpPost("{id:long}/password")]
    public async Task<ActionResult<PlatformUserDto>> ResetPassword(
        long id, [FromBody] ResetPlatformUserPasswordRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var user = await _context.Set<PlatformUser>().FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null)
            return NotFound();

        await ExecuteAuditedAsync(async () =>
        {
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            _context.Set<PlatformUser>().Update(user);
            await _context.SaveChangesAsync(ct);

            // Metadata deliberately excludes the password and its hash.
            await _audit.WriteAsync(User, "platform-user.password.reset", nameof(PlatformUser), user.Id.ToString(),
                new { user.Email }, httpContext: HttpContext, ct: ct);
        }, ct);

        return Ok(ToDto(user));
    }

    private long CurrentActorId()
    {
        var sub = User.FindFirst("sub")?.Value
                  ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
        return long.TryParse(sub, out var id) ? id : 0;
    }

    private Task<bool> AnotherActiveOwnerExistsAsync(long excludingId, CancellationToken ct) =>
        _context.Set<PlatformUser>().AnyAsync(u =>
            u.Id != excludingId && u.IsActive && u.PlatformRole == PlatformRole.Owner, ct);

    private static PlatformUserDto ToDto(PlatformUser u) => new()
    {
        Id = u.Id,
        Email = u.Email,
        PlatformRole = u.PlatformRole.ToString(),
        IsActive = u.IsActive,
        DisplayName = u.DisplayName,
        LastLogin = u.LastLogin,
        CreatedOn = u.CreatedOn
    };
}
