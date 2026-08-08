using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class PlatformMfaSecurityTests
{
    [Fact]
    public async Task Enrollment_revokes_existing_sessions_and_never_stores_the_TOTP_secret_in_cleartext()
    {
        using var db = new TestDb();
        var userId = await SeedUser(db, "mfa-enroll@example.test");
        string oldToken;
        string secret;
        IReadOnlyList<string> recoveryCodes;
        await using (var context = db.ContextFor(null))
        {
            var service = Service(context);
            oldToken = (await service.LoginAsync(Login("mfa-enroll@example.test"))).Token!;
            var enrollment = await service.BeginMfaEnrollmentAsync(userId);
            secret = enrollment.Secret;
            Assert.Contains("otpauth://totp/", enrollment.OtpAuthUri);
            var confirmed = await service.ConfirmMfaEnrollmentAsync(userId,
                new PlatformMfaEnrollmentConfirmRequest
                {
                    TotpCode = PlatformTotp.CodeAt(secret, CurrentStep())
                });
            recoveryCodes = confirmed.RecoveryCodes;
        }

        Assert.Equal(PlatformAuthService.RecoveryCodeCount, recoveryCodes.Count);
        Assert.Equal(recoveryCodes.Count, recoveryCodes.Distinct(StringComparer.Ordinal).Count());
        await using var verification = db.ContextFor(null);
        var user = await verification.Set<PlatformUser>().SingleAsync(value => value.Id == userId);
        Assert.Equal(2, user.SessionGeneration);
        Assert.All(await verification.Set<PlatformSession>().ToListAsync(),
            session => Assert.Equal("platform-mfa-enabled", session.RevocationReason));
        Assert.False(await new PlatformSessionValidator(verification).IsCurrentAsync(Principal(oldToken)));

        var connection = verification.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT TotpSecretProtected FROM PlatformMfaCredentials WHERE PlatformUserId = $userId";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$userId";
        parameter.Value = userId;
        command.Parameters.Add(parameter);
        var stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.NotEqual(secret, stored);
        Assert.StartsWith("v1:", stored);
    }

    [Fact]
    public async Task Enrolled_login_requires_challenge_and_issues_a_server_bound_MFA_session()
    {
        using var db = new TestDb();
        var userId = await SeedUser(db, "mfa-login@example.test");
        string secret;
        await using (var enrollmentContext = db.ContextFor(null))
        {
            var service = Service(enrollmentContext);
            secret = (await service.BeginMfaEnrollmentAsync(userId)).Secret;
            await service.ConfirmMfaEnrollmentAsync(userId, new PlatformMfaEnrollmentConfirmRequest
            {
                TotpCode = PlatformTotp.CodeAt(secret, CurrentStep())
            });
        }

        PlatformLoginResponse firstFactor;
        await using (var loginContext = db.ContextFor(null))
            firstFactor = await Service(loginContext).LoginAsync(Login("mfa-login@example.test"));

        Assert.True(firstFactor.MfaRequired);
        Assert.Null(firstFactor.Token);
        Assert.NotNull(firstFactor.MfaChallengeId);

        PlatformLoginResponse completed;
        await using (var challengeContext = db.ContextFor(null))
            completed = await Service(challengeContext).CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
            {
                ChallengeId = firstFactor.MfaChallengeId!.Value,
                // Enrollment consumed the current step. The verifier permits one adjacent
                // step for clock skew but still records it to prevent replay.
                TotpCode = PlatformTotp.CodeAt(secret, CurrentStep() + 1)
            });

        Assert.NotNull(completed.Token);
        var principal = Principal(completed.Token!);
        Assert.Equal(PlatformAuthConstants.MfaAuthenticationMethod,
            principal.FindFirst(PlatformAuthConstants.AuthenticationMethodClaim)?.Value);
        await using var validation = db.ContextFor(null);
        Assert.True(await new PlatformSessionValidator(validation).IsCurrentAsync(principal));
        Assert.NotNull((await validation.Set<PlatformSession>().SingleAsync(
            session => session.PlatformUserId == userId && session.RevokedAtUtc == null)).MfaAuthenticatedAtUtc);
        Assert.True(await AuthorizesOwnerAsync(principal));
    }

    [Fact]
    public async Task Recovery_code_is_single_use_and_a_claim_cannot_upgrade_a_non_MFA_session()
    {
        using var db = new TestDb();
        var userId = await SeedUser(db, "mfa-recovery@example.test");
        string recoveryCode;
        await using (var enrollmentContext = db.ContextFor(null))
        {
            var service = Service(enrollmentContext);
            var secret = (await service.BeginMfaEnrollmentAsync(userId)).Secret;
            recoveryCode = (await service.ConfirmMfaEnrollmentAsync(userId,
                new PlatformMfaEnrollmentConfirmRequest
                {
                    TotpCode = PlatformTotp.CodeAt(secret, CurrentStep())
                })).RecoveryCodes[0];
        }

        var first = await Challenge(db, "mfa-recovery@example.test");
        await using (var completion = db.ContextFor(null))
        {
            var response = await Service(completion).CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
            {
                ChallengeId = first,
                RecoveryCode = recoveryCode
            });
            Assert.True(response.RecoveryCodeUsed);
        }

        var replay = await Challenge(db, "mfa-recovery@example.test");
        await using (var rejected = db.ContextFor(null))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                Service(rejected).CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
                {
                    ChallengeId = replay,
                    RecoveryCode = recoveryCode
                }));

        var ordinaryId = await SeedUser(db, "no-mfa@example.test");
        string ordinaryToken;
        await using (var login = db.ContextFor(null))
            ordinaryToken = (await Service(login).LoginAsync(Login("no-mfa@example.test"))).Token!;
        var forged = Principal(ordinaryToken);
        ((ClaimsIdentity)forged.Identity!).AddClaims(
        [
            new Claim(PlatformAuthConstants.AuthenticationMethodClaim, PlatformAuthConstants.MfaAuthenticationMethod),
            new Claim(PlatformAuthConstants.MfaAuthenticatedAtClaim,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString())
        ]);
        await using var validation = db.ContextFor(null);
        Assert.False(await new PlatformSessionValidator(validation).IsCurrentAsync(forged));
        Assert.False(await AuthorizesOwnerAsync(Principal(ordinaryToken)));
        Assert.True(ordinaryId > 0);
    }

    private static async Task<Guid> Challenge(TestDb db, string email)
    {
        await using var context = db.ContextFor(null);
        var response = await Service(context).LoginAsync(Login(email));
        Assert.True(response.MfaRequired);
        return response.MfaChallengeId!.Value;
    }

    private static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds;

    private static PlatformLoginRequest Login(string email) => new()
    {
        Email = email,
        Password = "valid-password-123"
    };

    private static PlatformAuthService Service(ErpRfqAutomationContext context) =>
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

    private static async Task<bool> AuthorizesOwnerAsync(ClaimsPrincipal principal)
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddAuthorization(options => options.AddPlatformPolicies()).BuildServiceProvider();
        return (await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, PlatformPolicies.Owner)).Succeeded;
    }

    private static async Task<long> SeedUser(TestDb db, string email)
    {
        await using var context = db.ContextFor(null);
        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("valid-password-123"),
            PlatformRole = PlatformRole.Owner,
            IsActive = true,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = "test"
        };
        context.Set<PlatformUser>().Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }
}
