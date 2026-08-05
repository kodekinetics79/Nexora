using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Impersonation hardening: the [5,60]-minute TTL clamp, the revocable
/// ImpersonationSession row written at issue time, the sessions listing and
/// revoke endpoints, and middleware enforcement that a revoked or unknown jti
/// is rejected even for safe (GET) requests.
/// </summary>
public sealed class ImpersonationHardeningTests
{
    [Theory]
    [InlineData("1", 5)]      // below the floor -> clamped up
    [InlineData("240", 60)]   // above the ceiling -> clamped down
    [InlineData(null, 15)]    // unset -> unchanged default inside the window
    public void Impersonation_expiry_is_clamped_to_five_to_sixty_minutes(
        string? configuredMinutes, double expectedMinutes)
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var service = new PlatformAuthService(context, Configuration(configuredMinutes),
            NullLogger<PlatformAuthService>.Instance);
        var before = DateTime.UtcNow;

        var (_, expiresAtUtc, jti) = service.IssueImpersonationToken(
            7, new Tenant { Id = 1, Name = "T", Slug = "t" }, 42, "support");

        var lifetime = expiresAtUtc - before;
        Assert.InRange(lifetime, TimeSpan.FromMinutes(expectedMinutes - 0.5), TimeSpan.FromMinutes(expectedMinutes + 0.5));
        Assert.False(string.IsNullOrWhiteSpace(jti));
    }

    [Fact]
    public async Task Issuing_a_token_writes_a_session_row_keyed_by_the_tokens_jti()
    {
        using var db = new TestDb();
        var tenantId = await SeedActiveTenant(db, "impersonation-tenant");
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var result = await controller.Impersonate(tenantId,
            new ImpersonationRequest { Reason = "Support ticket 991" }, CancellationToken.None);

        var response = Assert.IsType<ImpersonationResponse>(Assert.IsType<OkObjectResult>(result.Result).Value);
        var tokenJti = new JwtSecurityTokenHandler().ReadJwtToken(response.Token)
            .Claims.Single(c => c.Type == "jti").Value;

        // The response surfaces the revocable session id directly so the console
        // can call the revoke endpoint without decoding the JWT.
        Assert.Equal(tokenJti, response.Jti);

        await using var verification = db.ContextFor(null);
        var session = await verification.Set<ImpersonationSession>().SingleAsync();
        Assert.Equal(tokenJti, session.Jti);
        Assert.Equal(tenantId, session.TenantId);
        Assert.Equal(7, session.ActorPlatformUserId);
        Assert.Equal("Support ticket 991", session.Reason);
        Assert.Null(session.RevokedAtUtc);
        Assert.Equal(response.ExpiresAtUtc, session.ExpiresAtUtc, TimeSpan.FromSeconds(1));

        var audit = await verification.Set<PlatformAuditLog>().SingleAsync(a => a.Action == "impersonate.issue");
        Assert.Contains(tokenJti, audit.Metadata);
    }

    [Fact]
    public async Task Revoke_stamps_the_session_and_audits()
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var result = await controller.Revoke(jti, CancellationToken.None);

        var dto = Assert.IsType<ImpersonationSessionDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("revoked", dto.Status);
        await using var verification = db.ContextFor(null);
        var session = await verification.Set<ImpersonationSession>().SingleAsync(s => s.Jti == jti);
        Assert.NotNull(session.RevokedAtUtc);
        Assert.Equal("operator@example.test", session.RevokedBy);
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync(a => a.Action == "impersonate.revoke");
        Assert.Equal(jti, audit.TargetId);
        Assert.Equal(5, audit.ActAsTenantId);
    }

    [Fact]
    public async Task Revoke_is_idempotent_and_unknown_jtis_are_not_found()
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        Assert.IsType<OkObjectResult>((await controller.Revoke(jti, CancellationToken.None)).Result);
        Assert.IsType<OkObjectResult>((await controller.Revoke(jti, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>((await controller.Revoke("no-such-jti", CancellationToken.None)).Result);

        await using var verification = db.ContextFor(null);
        Assert.Equal(1, await verification.Set<PlatformAuditLog>()
            .CountAsync(a => a.Action == "impersonate.revoke"));
    }

    [Fact]
    public async Task Sessions_listing_reports_active_and_revoked_states()
    {
        using var db = new TestDb();
        var activeJti = Guid.NewGuid().ToString();
        var revokedJti = Guid.NewGuid().ToString();
        await SeedSession(db, activeJti, tenantId: 5);
        await SeedSession(db, revokedJti, tenantId: 5, revoked: true);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Sessions(CancellationToken.None);

        var sessions = Assert.IsAssignableFrom<IEnumerable<ImpersonationSessionDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value).ToList();
        Assert.Equal("active", sessions.Single(s => s.Jti == activeJti).Status);
        Assert.Equal("revoked", sessions.Single(s => s.Jti == revokedJti).Status);
    }

    [Fact]
    public async Task Middleware_allows_a_live_session_and_rejects_it_after_revocation()
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);

        Assert.Equal(StatusCodes.Status200OK, await InvokeMiddleware(db, jti));

        await using (var revoke = db.ContextFor(null))
        {
            var session = await revoke.Set<ImpersonationSession>().SingleAsync(s => s.Jti == jti);
            session.RevokedAtUtc = DateTime.UtcNow;
            await revoke.SaveChangesAsync();
        }

        // A brand-new revoked session must be rejected on first sight:
        var revokedJti = Guid.NewGuid().ToString();
        await SeedSession(db, revokedJti, tenantId: 5, revoked: true);
        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeMiddleware(db, revokedJti));
    }

    [Fact]
    public async Task Revoke_evicts_the_shared_cache_so_denial_is_immediate_same_process()
    {
        // P2-A12: the middleware's cached "valid" verdict lives in the shared
        // IMemoryCache. Revoking through the controller evicts the jti entry, so the
        // SAME cache instance rejects the very next request — no 30s staleness window
        // on the node that performed the revoke.
        using var db = new TestDb();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);

        // Prime the cache with a valid verdict.
        Assert.Equal(StatusCodes.Status200OK, await InvokeMiddleware(db, jti, cache: cache));

        await using (var context = db.ContextFor(null))
        {
            var result = await Controller(context, cache).Revoke(jti, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeMiddleware(db, jti, cache: cache));
    }

    [Fact]
    public async Task Cached_valid_verdict_persists_within_ttl_without_eviction()
    {
        // Cross-instance bound: WITHOUT the same-process eviction (a revoke on another
        // node), a cached "valid" verdict may legitimately persist up to the 30s TTL.
        using var db = new TestDb();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);

        Assert.Equal(StatusCodes.Status200OK, await InvokeMiddleware(db, jti, cache: cache));

        await using (var revoke = db.ContextFor(null))
        {
            var session = await revoke.Set<ImpersonationSession>().SingleAsync(s => s.Jti == jti);
            session.RevokedAtUtc = DateTime.UtcNow;
            await revoke.SaveChangesAsync();
        }

        // Same cache, no eviction → still allowed inside the TTL (documented bound).
        Assert.Equal(StatusCodes.Status200OK, await InvokeMiddleware(db, jti, cache: cache));
    }

    // ---- Sec8: file-download / export deny-list for impersonated sessions --------

    [Theory]
    [InlineData("/api/File/attachment/17")]
    [InlineData("/api/File/DownloadFile")]
    [InlineData("/api/processing-evidence/leads/5")]
    [InlineData("/api/CustomerUploader/export")]
    [InlineData("/api/Boq/12/export.csv")]
    [InlineData("/api/LeadUploader/download-template")]
    public async Task Impersonated_get_on_download_or_export_routes_is_denied(string path)
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);

        Assert.Equal(StatusCodes.Status403Forbidden, await InvokeMiddleware(db, jti, path: path));
    }

    [Fact]
    public async Task Impersonated_get_on_ordinary_read_routes_still_passes()
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);

        Assert.Equal(StatusCodes.Status200OK, await InvokeMiddleware(db, jti, path: "/api/Lead"));
    }

    [Fact]
    public async Task Non_impersonated_requests_reach_download_routes_unhindered()
    {
        using var db = new TestDb();
        var reachedNext = false;
        var middleware = new ReadOnlyImpersonationMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, new MemoryCache(new MemoryCacheOptions()));
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/api/File/attachment/17";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim("businessUnitId", "42") }, "test"));

        await middleware.InvokeAsync(context);

        Assert.True(reachedNext);
    }

    [Fact]
    public async Task Middleware_rejects_an_unknown_jti_and_a_token_without_a_jti()
    {
        using var db = new TestDb();

        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeMiddleware(db, Guid.NewGuid().ToString()));
        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeMiddleware(db, jti: null));
    }

    [Fact]
    public async Task Middleware_rejects_an_expired_session_row()
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5, expiresAtUtc: DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(StatusCodes.Status401Unauthorized, await InvokeMiddleware(db, jti));
    }

    [Fact]
    public async Task Middleware_still_blocks_mutations_before_any_session_lookup()
    {
        using var db = new TestDb();
        var jti = Guid.NewGuid().ToString();
        await SeedSession(db, jti, tenantId: 5);

        Assert.Equal(StatusCodes.Status403Forbidden, await InvokeMiddleware(db, jti, method: "POST"));
    }

    private static async Task<int> InvokeMiddleware(
        TestDb db, string? jti, string method = "GET", IMemoryCache? cache = null, string path = "/api/Lead")
    {
        var reachedNext = false;
        var middleware = new ReadOnlyImpersonationMiddleware(_ =>
        {
            reachedNext = true;
            return Task.CompletedTask;
        }, cache ?? new MemoryCache(new MemoryCacheOptions()));

        var claims = new List<Claim>
        {
            new(PlatformAuthConstants.ImpersonatedClaim, "true"),
            new("businessUnitId", "42")
        };
        if (jti is not null)
            claims.Add(new Claim("jti", jti));

        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddScoped(_ => db.ContextFor(null))
                .BuildServiceProvider()
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        await middleware.InvokeAsync(context);
        return reachedNext ? StatusCodes.Status200OK : context.Response.StatusCode;
    }

    private static IConfiguration Configuration(string? impersonationMinutes = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        };
        if (impersonationMinutes is not null)
            values["Jwt:ImpersonationExpiryMinutes"] = impersonationMinutes;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ImpersonationController Controller(
        ErpRfqAutomationContext context, IMemoryCache? revocationCache = null) => new(
        context,
        new PlatformAuthService(context, Configuration(), NullLogger<PlatformAuthService>.Instance),
        new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
        revocationCache)
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", "7"),
                    new Claim("email", "operator@example.test")
                ], "Platform"))
            }
        }
    };

    private static async Task<long> SeedActiveTenant(TestDb db, string slug)
    {
        await using var seed = db.ContextFor(null);
        var bu = new BusinessUnit
        {
            BusinessUnitCode = slug.ToUpperInvariant(),
            BusinessUnitName = slug,
            IsActive = true,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        };
        seed.Set<BusinessUnit>().Add(bu);
        await seed.SaveChangesAsync();
        var tenant = new Tenant
        {
            Name = "Impersonation Tenant",
            Slug = slug,
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = bu.Id,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        };
        seed.Set<Tenant>().Add(tenant);
        await seed.SaveChangesAsync();
        return tenant.Id;
    }

    private static async Task SeedSession(
        TestDb db, string jti, long tenantId, bool revoked = false, DateTime? expiresAtUtc = null)
    {
        await using var seed = db.ContextFor(null);
        seed.Set<ImpersonationSession>().Add(new ImpersonationSession
        {
            Jti = jti,
            TenantId = tenantId,
            ActorPlatformUserId = 7,
            Reason = "test session",
            IssuedAtUtc = DateTime.UtcNow.AddMinutes(-1),
            ExpiresAtUtc = expiresAtUtc ?? DateTime.UtcNow.AddMinutes(14),
            RevokedAtUtc = revoked ? DateTime.UtcNow : null,
            RevokedBy = revoked ? "operator@example.test" : null
        });
        await seed.SaveChangesAsync();
    }
}
