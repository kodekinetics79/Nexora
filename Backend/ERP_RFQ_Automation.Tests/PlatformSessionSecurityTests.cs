using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

public sealed class PlatformSessionSecurityTests
{
    private const long ActingOwnerId = 7;

    [Fact]
    public async Task Login_persists_a_revocable_session_matching_the_token_jti_and_generation()
    {
        using var db = new TestDb();
        var userId = await SeedUser(db, "owner@example.test", PlatformRole.Owner, "valid-password-123");
        await using var context = db.ContextFor(null);

        var response = await AuthService(context).LoginAsync(new PlatformLoginRequest
        {
            Email = "owner@example.test",
            Password = "valid-password-123"
        });

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response.Token);
        var jti = jwt.Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        var generation = long.Parse(jwt.Claims.Single(
            c => c.Type == PlatformAuthConstants.SessionGenerationClaim).Value);

        await using var verification = db.ContextFor(null);
        var session = await verification.Set<PlatformSession>().SingleAsync();
        Assert.Equal(jti, session.Jti);
        Assert.Equal(userId, session.PlatformUserId);
        Assert.Equal(generation, session.SessionGeneration);
        Assert.Null(session.RevokedAtUtc);
        Assert.True(session.ExpiresAtUtc > session.IssuedAtUtc);
    }

    [Fact]
    public async Task Request_time_validator_requires_a_live_session_and_current_active_account_role()
    {
        using var db = new TestDb();
        await SeedUser(db, "support@example.test", PlatformRole.SupportAdmin, "valid-password-123");
        ClaimsPrincipal principal;
        await using (var login = db.ContextFor(null))
        {
            var response = await AuthService(login).LoginAsync(new PlatformLoginRequest
            {
                Email = "support@example.test",
                Password = "valid-password-123"
            });
            principal = Principal(response.Token);
        }

        await using (var valid = db.ContextFor(null))
            Assert.True(await new PlatformSessionValidator(valid).IsCurrentAsync(principal));

        await using (var mutate = db.ContextFor(null))
            await mutate.Set<PlatformUser>().ExecuteUpdateAsync(
                setters => setters.SetProperty(user => user.PlatformRole, PlatformRole.ReadOnlyOps));

        await using var changed = db.ContextFor(null);
        Assert.False(await new PlatformSessionValidator(changed).IsCurrentAsync(principal));
    }

    [Fact]
    public async Task Logout_revokes_the_current_session_and_request_validation_fails_immediately()
    {
        using var db = new TestDb();
        await SeedUser(db, "logout@example.test", PlatformRole.Owner, "valid-password-123");
        string token;
        string jti;
        await using (var login = db.ContextFor(null))
        {
            var service = AuthService(login);
            token = (await service.LoginAsync(new PlatformLoginRequest
            {
                Email = "logout@example.test",
                Password = "valid-password-123"
            })).Token;
            jti = new JwtSecurityTokenHandler().ReadJwtToken(token).Claims
                .Single(claim => claim.Type == JwtRegisteredClaimNames.Jti).Value;
            Assert.True(await service.RevokeSessionAsync(jti, "logout@example.test"));
            Assert.False(await service.RevokeSessionAsync(jti, "logout@example.test"));
        }

        await using var verification = db.ContextFor(null);
        var session = await verification.Set<PlatformSession>().SingleAsync(s => s.Jti == jti);
        Assert.NotNull(session.RevokedAtUtc);
        Assert.Equal("platform-logout", session.RevocationReason);
        Assert.False(await new PlatformSessionValidator(verification).IsCurrentAsync(Principal(token)));
    }

    [Fact]
    public async Task Deactivation_and_password_reset_revoke_all_sessions_and_rotate_the_generation()
    {
        using var db = new TestDb();
        await SeedUser(db, "acting-owner@example.test", PlatformRole.Owner, "owner-password-123", ActingOwnerId);
        var targetId = await SeedUser(db, "target@example.test", PlatformRole.SupportAdmin, "old-password-123");

        await Login(db, "target@example.test", "old-password-123");
        await Login(db, "target@example.test", "old-password-123");

        await using (var resetContext = db.ContextFor(null))
        {
            var result = await Controller(resetContext).ResetPassword(targetId,
                new ResetPlatformUserPasswordRequest { NewPassword = "new-password-123" }, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        await using (var afterReset = db.ContextFor(null))
        {
            var user = await afterReset.Set<PlatformUser>().SingleAsync(u => u.Id == targetId);
            Assert.Equal(2, user.SessionGeneration);
            Assert.All(await afterReset.Set<PlatformSession>().Where(s => s.PlatformUserId == targetId).ToListAsync(),
                session => Assert.Equal("platform-password-reset", session.RevocationReason));
        }

        await Login(db, "target@example.test", "new-password-123");
        await using (var deactivateContext = db.ContextFor(null))
        {
            var result = await Controller(deactivateContext).Deactivate(targetId, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        await using var verification = db.ContextFor(null);
        var deactivated = await verification.Set<PlatformUser>().SingleAsync(u => u.Id == targetId);
        Assert.False(deactivated.IsActive);
        Assert.Equal(3, deactivated.SessionGeneration);
        Assert.All(await verification.Set<PlatformSession>().Where(s => s.PlatformUserId == targetId).ToListAsync(),
            session => Assert.NotNull(session.RevokedAtUtc));
    }

    [Fact]
    public async Task Generation_fence_rejects_a_session_that_lands_after_a_password_reset()
    {
        using var db = new TestDb();
        await SeedUser(db, "acting-owner@example.test", PlatformRole.Owner, "owner-password-123", ActingOwnerId);
        var targetId = await SeedUser(db, "racing@example.test", PlatformRole.SupportAdmin, "old-password-123");
        var staleToken = await Login(db, "racing@example.test", "old-password-123");

        await using (var resetContext = db.ContextFor(null))
            await Controller(resetContext).ResetPassword(targetId,
                new ResetPlatformUserPasswordRequest { NewPassword = "new-password-123" }, CancellationToken.None);

        // Model the adverse ordering: a login read generation 1 before reset, but
        // its generation-1 ledger write becomes visible after revoke-all completed.
        var staleJti = new JwtSecurityTokenHandler().ReadJwtToken(staleToken)
            .Claims.Single(c => c.Type == JwtRegisteredClaimNames.Jti).Value;
        await using (var lateWrite = db.ContextFor(null))
            await lateWrite.Set<PlatformSession>().Where(s => s.Jti == staleJti).ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(s => s.RevokedAtUtc, (DateTime?)null)
                    .SetProperty(s => s.RevokedBy, (string?)null)
                    .SetProperty(s => s.RevocationReason, (string?)null));

        await using var validation = db.ContextFor(null);
        Assert.False(await new PlatformSessionValidator(validation).IsCurrentAsync(Principal(staleToken)));
    }

    [Fact]
    public async Task Different_owner_can_reset_lost_mfa_and_atomically_revoke_target_sessions()
    {
        using var db = new TestDb();
        await SeedUser(db, "acting-owner@example.test", PlatformRole.Owner, "owner-password-123", ActingOwnerId);
        var targetId = await SeedUser(db, "locked-owner@example.test", PlatformRole.Owner, "target-password-123");
        var token = await Login(db, "locked-owner@example.test", "target-password-123");
        await using (var seed = db.ContextFor(null))
        {
            seed.Set<PlatformMfaCredential>().Add(new PlatformMfaCredential
            {
                PlatformUserId = targetId, TotpSecret = PlatformTotp.GenerateSecret(),
                EnabledAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow
            });
            seed.Set<PlatformMfaRecoveryCode>().Add(new PlatformMfaRecoveryCode
            {
                PlatformUserId = targetId, CodeHash = new string('A', 64), CreatedAtUtc = DateTime.UtcNow
            });
            seed.Set<PlatformMfaChallenge>().Add(new PlatformMfaChallenge
            {
                Id = Guid.NewGuid(), PlatformUserId = targetId, CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5)
            });
            await seed.SaveChangesAsync();
        }

        await using (var reset = db.ContextFor(null))
        {
            var result = await Controller(reset).ResetMfa(targetId, CancellationToken.None);
            Assert.IsType<NoContentResult>(result);
        }

        await using var verification = db.ContextFor(null);
        Assert.False(await verification.Set<PlatformMfaCredential>().AnyAsync(value => value.PlatformUserId == targetId));
        Assert.False(await verification.Set<PlatformMfaRecoveryCode>().AnyAsync(value => value.PlatformUserId == targetId));
        Assert.False(await verification.Set<PlatformMfaChallenge>().AnyAsync(value => value.PlatformUserId == targetId));
        var session = await verification.Set<PlatformSession>().SingleAsync(value => value.PlatformUserId == targetId);
        Assert.Equal("platform-mfa-recovery-reset", session.RevocationReason);
        Assert.False(await new PlatformSessionValidator(verification).IsCurrentAsync(Principal(token)));
        Assert.Contains(await verification.Set<PlatformAuditLog>().ToListAsync(),
            audit => audit.Action == "platform-user.mfa.reset" && audit.TargetId == targetId.ToString());
    }

    [Fact]
    public async Task Owner_cannot_self_reset_mfa()
    {
        using var db = new TestDb();
        await SeedUser(db, "acting-owner@example.test", PlatformRole.Owner, "owner-password-123", ActingOwnerId);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).ResetMfa(ActingOwnerId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
    }

    [Fact]
    public void Platform_bearer_scheme_wires_request_time_session_validation_without_changing_schemes()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication().AddPlatformJwtBearer(Configuration());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(PlatformAuthConstants.Scheme);

        Assert.NotNull(options.Events.OnTokenValidated);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IPlatformSessionValidator)
                                                && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Equal(PlatformAuthConstants.Audience, options.TokenValidationParameters.ValidAudience);
    }

    private static async Task<string> Login(TestDb db, string email, string password)
    {
        await using var context = db.ContextFor(null);
        return (await AuthService(context).LoginAsync(new PlatformLoginRequest
        {
            Email = email,
            Password = password
        })).Token;
    }

    private static PlatformAuthService AuthService(ErpRfqAutomationContext context) =>
        new(context, Configuration(), NullLogger<PlatformAuthService>.Instance);

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();

    private static ClaimsPrincipal Principal(string token) => new(new ClaimsIdentity(
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, PlatformAuthConstants.Scheme));

    private static PlatformUsersController Controller(ErpRfqAutomationContext context) => new(
        context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", ActingOwnerId.ToString()),
                    new Claim("email", "acting-owner@example.test")
                ], PlatformAuthConstants.Scheme))
            }
        }
    };

    private static async Task<long> SeedUser(
        TestDb db, string email, PlatformRole role, string password, long id = 0)
    {
        await using var context = db.ContextFor(null);
        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            PlatformRole = role,
            IsActive = true,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        };
        if (id > 0)
            user.Id = id;
        context.Set<PlatformUser>().Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }
}
