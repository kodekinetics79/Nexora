using System.Net;
using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Review-board fix coverage for the platform/enforcement/security slice:
/// Sec3 (audit rows commit atomically with their mutation), Sec5 (IP-keyed gate in
/// front of the append-only login-failure audit), Sec9 (plan change requires the
/// Billing policy), P2-A7 (suspension cache eviction → same-process immediacy) and
/// P2-A10 (one problem+json base URI + typed seat denial).
/// </summary>
public sealed class PlatformAdmin360SecurityFixTests
{
    // ---- Sec9 -------------------------------------------------------------

    [Fact]
    public void ChangePlan_requires_the_billing_policy_not_tenant_admin()
    {
        var method = typeof(TenantsController).GetMethod(nameof(TenantsController.ChangePlan))!;
        var authorize = method.GetCustomAttributes<AuthorizeAttribute>().Single();
        Assert.Equal(PlatformPolicies.Billing, authorize.Policy);
    }

    // ---- P2-A10 -----------------------------------------------------------

    [Fact]
    public void Every_problem_type_lives_under_the_single_invalid_base_uri()
    {
        Assert.StartsWith(NexoraProblems.Base, TenantAccessDeniedException.Type);
        Assert.StartsWith(NexoraProblems.Base, DocumentQuotaExceededException.Type);
        Assert.StartsWith(NexoraProblems.Base, SeatLimitExceededException.Type);
        Assert.StartsWith(NexoraProblems.Base, NexoraProblems.ReadOnlyImpersonation);
        Assert.StartsWith(NexoraProblems.Base, NexoraProblems.ImpersonationSessionRevoked);
        Assert.StartsWith(NexoraProblems.Base, NexoraProblems.ImpersonationExportDenied);
        Assert.Equal("https://nexora.invalid/problems/", NexoraProblems.Base);
    }

    [Fact]
    public void Seat_denial_renders_as_the_canonical_typed_problem_with_usage_numbers()
    {
        var denial = new SeatLimitExceededException(42, EntitlementDecision.Deny(3, 3, "Seat limit reached: 3."));

        var result = EntitlementProblemFilter.ToResult(denial);

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Contains("application/problem+json", result.ContentTypes);
        var payload = result.Value!;
        Assert.Equal(NexoraProblems.SeatLimitExceeded, Prop(payload, "type"));
        Assert.Equal(3, Prop(payload, "limit"));
        Assert.Equal(3, Prop(payload, "activeUsers"));
    }

    [Fact]
    public async Task Read_only_impersonation_denial_uses_the_invalid_base_uri()
    {
        var middleware = new ReadOnlyImpersonationMiddleware(
            _ => Task.CompletedTask, new MemoryCache(new MemoryCacheOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(PlatformAuthConstants.ImpersonatedClaim, "true") }, "test"));

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        Assert.Contains(NexoraProblems.ReadOnlyImpersonation, body);
        Assert.DoesNotContain("nexora.local", body);
    }

    // ---- P2-A7 ------------------------------------------------------------

    [Fact]
    public async Task Suspend_evicts_the_access_cache_so_denial_is_immediate_same_process()
    {
        using var db = new TestDb();
        var cache = new MemoryCache(new MemoryCacheOptions());
        long tenantId, buId;
        await using (var seed = db.ContextFor(null))
        {
            var bu = new BusinessUnit
            {
                BusinessUnitCode = "A7", BusinessUnitName = "A7 Tenant",
                IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            };
            seed.Set<BusinessUnit>().Add(bu);
            await seed.SaveChangesAsync();
            var tenant = new Tenant
            {
                Name = "A7 Tenant", Slug = "a7-tenant", Status = TenantStatus.Active,
                PrimaryBusinessUnitId = bu.Id, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            };
            seed.Set<Tenant>().Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
            buId = bu.Id;
        }

        // Prime the shared cache with the ACTIVE snapshot (60s TTL).
        await using (var ctx = db.ContextFor(null))
        {
            var primed = await AccessService(ctx, cache).GetAccessAsync(buId);
            Assert.False(primed.IsAccessDenied);
        }

        // Suspend through the controller, wired to an access service on the SAME cache.
        await using (var ctx = db.ContextFor(null))
        {
            var result = await Controller(ctx, AccessService(ctx, cache)).Suspend(
                tenantId, new TenantStatusChangeRequest { Reason = "non-payment" }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        // Same process, same cache, well inside the TTL: the denial must be immediate.
        await using (var verify = db.ContextFor(null))
        {
            var after = await AccessService(verify, cache).GetAccessAsync(buId);
            Assert.True(after.IsAccessDenied);
        }
    }

    // ---- Sec3 -------------------------------------------------------------

    private sealed class ThrowingAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default)
            => throw new InvalidOperationException("audit sink unavailable");
    }

    [Fact]
    public async Task PlatformUser_create_rolls_back_when_the_audit_write_fails()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = WithActor(new PlatformUsersController(context, new ThrowingAudit()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Create(
            new CreatePlatformUserRequest
            {
                Email = "new-op@example.test", Password = "a-long-enough-password",
                Role = nameof(PlatformRole.SupportAdmin)
            }, CancellationToken.None));

        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<PlatformUser>().ToListAsync());
    }

    [Fact]
    public async Task PlatformUser_deactivate_rolls_back_when_the_audit_write_fails()
    {
        using var db = new TestDb();
        long userId;
        await using (var seed = db.ContextFor(null))
        {
            var user = new PlatformUser
            {
                Email = "victim@example.test", PasswordHash = "x",
                PlatformRole = PlatformRole.SupportAdmin, IsActive = true,
                CreatedBy = "test", CreatedOn = DateTime.UtcNow
            };
            seed.Set<PlatformUser>().Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        await using (var context = db.ContextFor(null))
        {
            var controller = WithActor(new PlatformUsersController(context, new ThrowingAudit()));
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.Deactivate(userId, CancellationToken.None));
        }

        await using var verification = db.ContextFor(null);
        Assert.True((await verification.Set<PlatformUser>().SingleAsync(u => u.Id == userId)).IsActive);
    }

    [Fact]
    public async Task Plan_create_rolls_back_when_the_audit_write_fails()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = WithActor(new PlatformOperationsController(context, new ThrowingAudit()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.CreatePlan(
            new UpsertPlanRequest
            {
                Code = "atomic", Name = "Atomic", Weight = 1,
                MaxConcurrentExtractionJobs = 1, MaxDocsPerMonth = 1, MaxSeats = 1, IsActive = true
            }, CancellationToken.None));

        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<Plan>().ToListAsync());
    }

    [Fact]
    public async Task Impersonation_issue_rolls_back_the_session_when_the_audit_write_fails()
    {
        using var db = new TestDb();
        long tenantId;
        await using (var seed = db.ContextFor(null))
        {
            var bu = new BusinessUnit
            {
                BusinessUnitCode = "SEC3", BusinessUnitName = "Sec3 Tenant",
                IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            };
            seed.Set<BusinessUnit>().Add(bu);
            await seed.SaveChangesAsync();
            var tenant = new Tenant
            {
                Name = "Sec3 Tenant", Slug = "sec3-tenant", Status = TenantStatus.Active,
                PrimaryBusinessUnitId = bu.Id, CreatedBy = "test", CreatedOn = DateTime.UtcNow
            };
            seed.Set<Tenant>().Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
        }

        await using (var context = db.ContextFor(null))
        {
            var controller = WithActor(new ImpersonationController(
                context,
                new PlatformAuthService(context, JwtConfig(), NullLogger<PlatformAuthService>.Instance),
                new ThrowingAudit()));

            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Impersonate(
                tenantId, new ImpersonationRequest { Reason = "atomicity" }, CancellationToken.None));
        }

        // No orphan session: a token that was never returned has no live jti row.
        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<ImpersonationSession>().ToListAsync());
    }

    // ---- Sec5 -------------------------------------------------------------

    [Fact]
    public async Task Failed_platform_logins_beyond_the_ip_threshold_429_without_an_audit_row()
    {
        using var db = new TestDb();
        var options = new LoginThrottleOptions
        {
            FailureThreshold = 2,
            FailureWindow = TimeSpan.FromMinutes(15),
            BaseLockout = TimeSpan.FromMinutes(5),
            MaximumLockout = TimeSpan.FromMinutes(60)
        };
        var ip = IPAddress.Parse("203.0.113.9");

        async Task<int> AttemptAsync(string email)
        {
            await using var context = db.ContextFor(null);
            var controller = new PlatformAuthController(
                new PlatformAuthService(context, JwtConfig(), NullLogger<PlatformAuthService>.Instance),
                new LoginAttemptThrottle(context, options, NullLogger<LoginAttemptThrottle>.Instance),
                new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance))
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.HttpContext.Connection.RemoteIpAddress = ip;
            var result = await controller.Login(new PlatformLoginRequest
            {
                Email = email, Password = "wrong-password!"
            });
            return (result.Result as IStatusCodeActionResult)?.StatusCode ?? 200;
        }

        // Rotating emails defeat the per-email counter; the IP counter still trips.
        Assert.Equal(StatusCodes.Status401Unauthorized, await AttemptAsync("a@example.test"));
        Assert.Equal(StatusCodes.Status401Unauthorized, await AttemptAsync("b@example.test"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await AttemptAsync("c@example.test"));
        Assert.Equal(StatusCodes.Status429TooManyRequests, await AttemptAsync("d@example.test"));

        await using var verification = db.ContextFor(null);
        // Only the pre-lockout failures were audited — the flood stopped growing the
        // append-only table at the IP threshold.
        Assert.Equal(2, await verification.Set<PlatformAuditLog>()
            .CountAsync(a => a.Action == "platform.login.failed"));
    }

    // ---- helpers ----------------------------------------------------------

    private static object? Prop(object payload, string name)
        => payload.GetType().GetProperty(name)?.GetValue(payload);

    private static TenantAccessService AccessService(ErpRfqAutomationContext ctx, IMemoryCache cache)
        => new(ctx, cache, NullLogger<TenantAccessService>.Instance);

    private static TenantsController Controller(
        ErpRfqAutomationContext context, ITenantAccessService tenantAccess)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return WithActor(new TenantsController(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor(),
            tenantAccess));
    }

    private static T WithActor<T>(T controller) where T : ControllerBase
    {
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "7"),
                    new Claim("email", "operator@example.test")
                ], "Platform"))
            }
        };
        return controller;
    }

    private static IConfiguration JwtConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "0123456789abcdef0123456789abcdef-unit-test-key",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();
}
