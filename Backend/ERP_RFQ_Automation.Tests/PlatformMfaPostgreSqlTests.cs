using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PlatformMfaPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_password_logins_share_exactly_one_live_MFA_challenge()
    {
        var now = DateTime.UtcNow;
        var secret = PlatformTotp.GenerateSecret();
        var email = $"mfa-login-race-{Guid.NewGuid():N}@example.test";
        var userId = await SeedEnabledUserAsync(email, secret, now);

        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 8).Select(async _ =>
            {
                await release.Task;
                await using var context = database.ContextFor(null);
                return await new PlatformAuthService(context, Configuration(), NullLogger<PlatformAuthService>.Instance)
                    .LoginAsync(new PlatformLoginRequest
                    {
                        Email = email,
                        Password = "valid-password-123"
                    });
            }).ToArray();

            release.SetResult();
            var responses = await Task.WhenAll(attempts);

            Assert.All(responses, response => Assert.True(response.MfaRequired));
            Assert.Single(responses.Select(response => response.MfaChallengeId).Distinct());

            await using var verification = database.ContextFor(null);
            Assert.Equal(1, await verification.Set<PlatformMfaChallenge>().AsNoTracking()
                .CountAsync(challenge => challenge.PlatformUserId == userId
                                         && challenge.ConsumedAtUtc == null
                                         && challenge.ExpiresAtUtc > DateTime.UtcNow));
        }
        finally
        {
            await CleanupAsync(userId);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Parallel_invalid_codes_consume_the_exact_five_attempt_challenge_ceiling()
    {
        var now = DateTime.UtcNow;
        var secret = PlatformTotp.GenerateSecret();
        var challengeId = Guid.NewGuid();
        var userId = await SeedEnabledUserAsync(
            $"mfa-invalid-race-{Guid.NewGuid():N}@example.test", secret, now);
        await using (var seed = database.ContextFor(null))
        {
            seed.Set<PlatformMfaChallenge>().Add(Challenge(challengeId, userId, now));
            await seed.SaveChangesAsync();
        }

        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var attempts = Enumerable.Range(0, 12).Select(async attempt =>
            {
                await release.Task;
                await using var context = database.ContextFor(null);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                    new PlatformAuthService(context, Configuration(), NullLogger<PlatformAuthService>.Instance)
                        .CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
                        {
                            ChallengeId = challengeId,
                            TotpCode = $"{100000 + attempt:000000}"
                        }));
            }).ToArray();

            release.SetResult();
            await Task.WhenAll(attempts);

            await using (var verification = database.ContextFor(null))
            {
                var challenge = await verification.Set<PlatformMfaChallenge>().AsNoTracking()
                    .SingleAsync(value => value.Id == challengeId);
                Assert.Equal(PlatformAuthService.MfaChallengeMaxAttempts, challenge.FailedAttempts);
                Assert.Null(challenge.ConsumedAtUtc);
                Assert.Empty(await verification.Set<PlatformSession>().AsNoTracking()
                    .Where(session => session.PlatformUserId == userId).ToListAsync());
            }

            var validCode = PlatformTotp.CodeAt(secret,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds);
            await using var locked = database.ContextFor(null);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                new PlatformAuthService(locked, Configuration(), NullLogger<PlatformAuthService>.Instance)
                    .CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
                    {
                        ChallengeId = challengeId,
                        TotpCode = validCode
                    }));
        }
        finally
        {
            await CleanupAsync(userId);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Enrollment_confirmation_uses_the_production_retry_execution_strategy()
    {
        long userId;
        await using (var seed = database.ContextFor(null))
        {
            var user = new PlatformUser
            {
                Email = $"mfa-enroll-{Guid.NewGuid():N}@example.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("valid-password-123"),
                PlatformRole = PlatformRole.Owner, IsActive = true,
                CreatedOn = DateTime.UtcNow, CreatedBy = "test"
            };
            seed.Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
        }

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString, npgsql => npgsql.EnableRetryOnFailure())
            .Options;
        await using (var context = new ErpRfqAutomationContext(options))
        {
            var service = new PlatformAuthService(context, Configuration(), NullLogger<PlatformAuthService>.Instance);
            var enrollment = await service.BeginMfaEnrollmentAsync(userId);
            var code = PlatformTotp.CodeAt(enrollment.Secret,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds);
            var result = await service.ConfirmMfaEnrollmentAsync(userId,
                new PlatformMfaEnrollmentConfirmRequest { TotpCode = code });
            Assert.Equal(PlatformAuthService.RecoveryCodeCount, result.RecoveryCodes.Count);
        }

        await using var cleanup = database.ContextFor(null);
        await cleanup.Set<PlatformSession>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformMfaChallenge>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformMfaRecoveryCode>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformMfaCredential>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformUser>().Where(value => value.Id == userId).ExecuteDeleteAsync();
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Concurrent_challenges_cannot_replay_one_TOTP_step()
    {
        var now = DateTime.UtcNow;
        var secret = PlatformTotp.GenerateSecret();
        var firstChallenge = Guid.NewGuid();
        var secondChallenge = Guid.NewGuid();
        long userId;
        await using (var seed = database.ContextFor(null))
        {
            var user = new PlatformUser
            {
                Email = $"mfa-race-{Guid.NewGuid():N}@example.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword("valid-password-123"),
                PlatformRole = PlatformRole.Owner,
                IsActive = true,
                CreatedOn = now,
                CreatedBy = "test"
            };
            seed.Set<PlatformUser>().Add(user);
            await seed.SaveChangesAsync();
            userId = user.Id;
            seed.Set<PlatformMfaCredential>().Add(new PlatformMfaCredential
            {
                PlatformUserId = userId,
                TotpSecret = secret,
                EnabledAtUtc = now,
                UpdatedAtUtc = now
            });
            seed.Set<PlatformMfaChallenge>().AddRange(
                Challenge(firstChallenge, userId, now), Challenge(secondChallenge, userId, now));
            await seed.SaveChangesAsync();
        }

        try
        {
            var code = PlatformTotp.CodeAt(secret,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds);
            var first = Complete(firstChallenge, code);
            var second = Complete(secondChallenge, code);
            var outcomes = await Task.WhenAll(first, second);

            Assert.Single(outcomes, outcome => outcome);
            Assert.Single(outcomes, outcome => !outcome);
            await using var verification = database.ContextFor(null);
            Assert.Single(await verification.Set<PlatformSession>().AsNoTracking()
                .Where(session => session.PlatformUserId == userId).ToListAsync());
            Assert.Equal(1, await verification.Set<PlatformMfaChallenge>().AsNoTracking()
                .CountAsync(challenge => challenge.PlatformUserId == userId
                                         && challenge.ConsumedAtUtc != null));
        }
        finally
        {
            await using var cleanup = database.ContextFor(null);
            await cleanup.Set<PlatformSession>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
            await cleanup.Set<PlatformMfaChallenge>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
            await cleanup.Set<PlatformMfaRecoveryCode>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
            await cleanup.Set<PlatformMfaCredential>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
            await cleanup.Set<PlatformUser>().Where(value => value.Id == userId).ExecuteDeleteAsync();
        }
    }

    private async Task<bool> Complete(Guid challengeId, string code)
    {
        await using var context = database.ContextFor(null);
        try
        {
            await new PlatformAuthService(context, Configuration(), NullLogger<PlatformAuthService>.Instance)
                .CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
                {
                    ChallengeId = challengeId,
                    TotpCode = code
                });
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
    }

    private async Task<long> SeedEnabledUserAsync(string email, string secret, DateTime now)
    {
        await using var seed = database.ContextFor(null);
        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("valid-password-123"),
            PlatformRole = PlatformRole.Owner,
            IsActive = true,
            CreatedOn = now,
            CreatedBy = "test"
        };
        seed.Set<PlatformUser>().Add(user);
        await seed.SaveChangesAsync();
        seed.Set<PlatformMfaCredential>().Add(new PlatformMfaCredential
        {
            PlatformUserId = user.Id,
            TotpSecret = secret,
            EnabledAtUtc = now,
            UpdatedAtUtc = now
        });
        await seed.SaveChangesAsync();
        return user.Id;
    }

    private async Task CleanupAsync(long userId)
    {
        await using var cleanup = database.ContextFor(null);
        await cleanup.Set<PlatformSession>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformMfaChallenge>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformMfaRecoveryCode>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformMfaCredential>().Where(value => value.PlatformUserId == userId).ExecuteDeleteAsync();
        await cleanup.Set<PlatformUser>().Where(value => value.Id == userId).ExecuteDeleteAsync();
    }

    private static PlatformMfaChallenge Challenge(Guid id, long userId, DateTime now) => new()
    {
        Id = id,
        PlatformUserId = userId,
        CreatedAtUtc = now,
        ExpiresAtUtc = now.AddMinutes(5)
    };

    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();
}
