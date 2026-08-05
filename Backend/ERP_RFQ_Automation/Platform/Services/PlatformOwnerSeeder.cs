using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Services;

/// <summary>
/// One-shot bootstrap of the FIRST platform Owner so a fresh deployment can sign
/// into the platform plane at all. Mirrors the DemoUserSeeder security posture:
///
/// - Fail-closed: does nothing unless BOTH <c>Platform:BootstrapOwnerEmail</c> and
///   <c>Platform:BootstrapOwnerPassword</c> are explicitly configured. There is no
///   hardcoded fallback credential of any kind.
/// - Never overwrites: refuses to run when ANY platform user row exists (active or
///   not) — it can only ever create the very first account, never rotate or
///   resurrect one.
/// - Logs without secrets: only the email is ever logged.
///
/// Unlike DemoUserSeeder this deliberately DOES run in Production — bootstrapping
/// the first Owner from per-deployment secrets is its entire purpose (a fresh
/// production deploy is otherwise locked out of the control plane). The
/// fail-closed default (no config, no account) is what protects Production.
/// </summary>
public static class PlatformOwnerSeeder
{
    public const string EmailConfigKey = "Platform:BootstrapOwnerEmail";
    public const string PasswordConfigKey = "Platform:BootstrapOwnerPassword";

    /// <summary>
    /// Sec7: the bootstrap password must meet the same floor the audited
    /// /api/platform/users endpoints enforce ([MinLength(12)]); a weaker configured
    /// secret is refused (logged + skipped), never silently accepted.
    /// </summary>
    public const int MinimumPasswordLength = 12;

    public static async Task EnsureAsync(
        IServiceProvider services, IConfiguration configuration, CancellationToken ct = default)
    {
        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("PlatformOwnerSeeder");

        var email = configuration[EmailConfigKey];
        var password = configuration[PasswordConfigKey];

        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(password))
            return; // not configured — fail closed, silently.

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "Platform owner bootstrap is partially configured ({EmailKey} / {PasswordKey}); "
                + "both must be set. Skipping — no credential will be created.",
                EmailConfigKey, PasswordConfigKey);
            return;
        }

        if (password.Length < MinimumPasswordLength)
        {
            logger.LogWarning(
                "Platform owner bootstrap password ({PasswordKey}) is shorter than {MinimumLength} "
                + "characters. Skipping — no credential will be created until a stronger secret is "
                + "configured and the deployment restarted (rotate via /api/platform/users afterwards).",
                PasswordConfigKey, MinimumPasswordLength);
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        // Never overwrite / never coexist: the bootstrap path is exclusively for an
        // EMPTY platform-user table. Any existing row (even a deactivated one) means
        // the plane has been provisioned and further changes belong to the audited
        // /api/platform/users endpoints.
        if (await db.Set<PlatformUser>().AnyAsync(ct))
        {
            logger.LogInformation(
                "Platform owner bootstrap skipped: platform users already exist and the seeder never overwrites.");
            return;
        }

        try
        {
            var owner = new PlatformUser
            {
                Email = email.Trim(),
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
                PlatformRole = PlatformRole.Owner,
                IsActive = true,
                DisplayName = "Platform Owner",
                CreatedBy = "system:platform-bootstrap",
                CreatedOn = DateTime.UtcNow
            };
            db.Set<PlatformUser>().Add(owner);
            await db.SaveChangesAsync(ct);

            // Sec7: the bootstrap itself is an audited privileged event. The actor is
            // the created owner id (the only platform identity that exists yet), which
            // satisfies the positive-actor invariant for success records.
            var auditActor = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                [
                    new System.Security.Claims.Claim("sub", owner.Id.ToString()),
                    new System.Security.Claims.Claim("email", owner.Email)
                ], "platform-bootstrap"));
            var audit = new PlatformAuditService(db,
                scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
                    .CreateLogger<PlatformAuditService>());
            await audit.WriteAsync(auditActor, "platform.owner.bootstrap", nameof(PlatformUser),
                owner.Id.ToString(), new { owner.Email, role = owner.PlatformRole.ToString() }, ct: ct);

            logger.LogInformation("Seeded bootstrap platform owner {Email}.", email.Trim());
        }
        catch (DbUpdateException ex)
        {
            // A concurrent instance won the unique-email race; the invariant (exactly
            // one bootstrap Owner) still holds, so this is not an error.
            logger.LogWarning(ex,
                "Platform owner bootstrap hit a concurrent-create conflict; assuming another instance seeded the owner.");
        }
    }
}

/// <summary>
/// Startup splice for Program.cs. The single line required is:
/// <code>await app.SeedPlatformOwnerAsync();</code>
/// (place it next to the existing DemoUserSeeder call, before app.Run()).
/// </summary>
public static class PlatformOwnerSeederExtensions
{
    public static Task SeedPlatformOwnerAsync(this IHost host, CancellationToken ct = default) =>
        PlatformOwnerSeeder.EnsureAsync(
            host.Services, host.Services.GetRequiredService<IConfiguration>(), ct);
}
