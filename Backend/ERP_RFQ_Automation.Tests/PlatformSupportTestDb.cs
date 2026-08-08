using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Platform.Support;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <see cref="TestDb"/> plus a seeded acting operator.
///
/// <para>The support tables reach the model through <c>modelBuilder.ApplyPlatformSupportModel()</c>
/// in <c>ErpRfqAutomationContext.Tenancy.cs</c>, so this is the production context over the
/// production entity configuration and the production relational schema — the same SQLite-in-memory
/// arrangement <see cref="TestDb"/> uses, and for the same reasons (real foreign keys, real unique
/// indexes, real query-filter translation).</para>
/// </summary>
public sealed class PlatformSupportTestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ErpRfqAutomationContext> _options;

    public PlatformSupportTestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

        using var context = ContextFor(null);
        context.Database.EnsureCreated();

        // Tickets carry foreign keys to platform.PlatformUsers for the operator who raised them and
        // the operator who owns them, so the actor these tests speak as has to be a real row — which
        // it always is in production, where a platform token's `sub` IS a PlatformUsers id.
        context.Set<PlatformUser>().Add(new PlatformUser
        {
            Id = PlatformSupportFixture.OwnerActorId,
            Email = PlatformSupportFixture.OwnerEmail,
            PasswordHash = "not-a-real-hash",
            PlatformRole = PlatformRole.Owner,
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });
        context.SaveChanges();
    }

    /// <summary>Null models the operator plane, which holds no tenant scope. See <see cref="TestDb"/>.</summary>
    public ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(_options, new StubTenant(businessUnitId));

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Shared seeding and controller construction for the support-desk suites. Kept in one place so the
/// tests below read as scenarios rather than as setup.
/// </summary>
public static class PlatformSupportFixture
{
    public const long OwnerActorId = 7;
    public const string OwnerEmail = "owner@example.test";

    public static async Task<long> SeedTenantAsync(
        PlatformSupportTestDb db, string slug, TenantStatus status = TenantStatus.Active,
        string? name = null)
    {
        await using var context = db.ContextFor(null);
        var tenant = new Tenant
        {
            Name = name ?? slug,
            Slug = slug,
            Status = status,
            StatusReason = status == TenantStatus.Suspended ? "Non-payment" : null,
            CreatedOn = DateTime.UtcNow
        };
        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();
        return tenant.Id;
    }

    public static async Task<long> SeedOperatorAsync(
        PlatformSupportTestDb db, string email, PlatformRole role = PlatformRole.SupportAdmin,
        bool isActive = true)
    {
        await using var context = db.ContextFor(null);
        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = "not-a-real-hash",
            PlatformRole = role,
            IsActive = isActive,
            CreatedOn = DateTime.UtcNow
        };
        context.Set<PlatformUser>().Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>
    /// A platform principal shaped exactly like the one <c>PlatformAuthService</c> mints: the actor
    /// id lives on <c>sub</c>, the label on <c>email</c>, and the role on <c>platformRole</c>. The
    /// controllers read all three.
    /// </summary>
    public static ClaimsPrincipal Actor(
        long id = OwnerActorId, string email = OwnerEmail, PlatformRole role = PlatformRole.SupportAdmin)
        => new(new ClaimsIdentity(
        [
            new Claim("sub", id.ToString()),
            new Claim("email", email),
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
            new Claim(PlatformAuthConstants.PlatformRoleClaim, role.ToString()),
            new Claim(PlatformAuthConstants.AuthenticationMethodClaim,
                PlatformAuthConstants.MfaAuthenticationMethod)
        ], PlatformAuthConstants.Scheme));

    /// <summary>
    /// The PRODUCTION policy registration, not a re-statement of it. The disclosure gate that closed
    /// finding R6 evaluates callers against these exact policies, so a test double here would test
    /// the double. <c>AddPlatformPolicies</c> pins an authentication scheme on each policy, which the
    /// middleware honours and <see cref="IAuthorizationService"/> does not — leaving precisely the
    /// requirements (authenticated user + scope claim + role claim) that decide the outcome.
    /// </summary>
    public static IAuthorizationService Authorization()
        => new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddPlatformPolicies())
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

    public static PlatformSupportTicketsController Tickets(
        ErpRfqAutomationContext context, ClaimsPrincipal? actor = null)
        => new(context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            Authorization(),
            NullLogger<PlatformSupportTicketsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = actor ?? Actor() }
            }
        };

    public static PlatformAuditExplorerController Explorer(
        ErpRfqAutomationContext context, ClaimsPrincipal? actor = null)
        => new(context, Authorization())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = actor ?? Actor() }
            }
        };

    public static TenantOperationsController Operations(
        ErpRfqAutomationContext context, ClaimsPrincipal? actor = null)
        => new(context, Authorization())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = actor ?? Actor() }
            }
        };

    public static SupportTicketRedactionService Redactor(ErpRfqAutomationContext context)
        => new(context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance));

    /// <summary>Raises a ticket through the real endpoint, so every test starts from a real create.</summary>
    public static async Task<long> RaiseTicketAsync(
        PlatformSupportTestDb db, long tenantId, string subject = "Cannot log in",
        string severity = nameof(SupportTicketSeverity.Normal), long? assignTo = null)
    {
        await using var context = db.ContextFor(null);
        var result = await Tickets(context).Create(new CreateSupportTicketRequest
        {
            TenantId = tenantId,
            Subject = subject,
            Body = "Customer reports the login page rejects a known-good password.",
            Severity = severity,
            RequesterEmail = "buyer@customer.test",
            AssignToPlatformUserId = assignTo
        }, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        return Assert.IsType<SupportTicketDetailDto>(created.Value).Id;
    }

    public static async Task<PlatformAuditLog> SeedAuditAsync(
        PlatformSupportTestDb db, string action, long? tenantId, long actorId,
        string? metadata = null, DateTime? at = null,
        string result = PlatformAuditResults.Success,
        string? targetType = null, string? targetId = null)
    {
        await using var context = db.ContextFor(null);
        var row = new PlatformAuditLog
        {
            Action = action,
            ActAsTenantId = tenantId,
            ActorPlatformUserId = actorId,
            Metadata = metadata,
            Result = result,
            TargetType = targetType,
            TargetId = targetId,
            CreatedOn = at ?? DateTime.UtcNow
        };
        context.Set<PlatformAuditLog>().Add(row);
        await context.SaveChangesAsync();
        return row;
    }

    public static async Task<string> SeedImpersonationAsync(
        PlatformSupportTestDb db, long tenantId, long actorId, string reason = "Reproducing the login failure",
        DateTime? issuedAt = null, TimeSpan? lifetime = null, DateTime? revokedAt = null)
    {
        await using var context = db.ContextFor(null);
        var issued = issuedAt ?? DateTime.UtcNow;
        var session = new ImpersonationSession
        {
            Jti = Guid.NewGuid().ToString("N"),
            TenantId = tenantId,
            ActorPlatformUserId = actorId,
            Reason = reason,
            IssuedAtUtc = issued,
            ExpiresAtUtc = issued + (lifetime ?? TimeSpan.FromMinutes(30)),
            RevokedAtUtc = revokedAt
        };
        context.Set<ImpersonationSession>().Add(session);
        await context.SaveChangesAsync();
        return session.Jti;
    }

    public static SupportTicketDetailDto Ok(ActionResult<SupportTicketDetailDto> result)
        => Assert.IsType<SupportTicketDetailDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
