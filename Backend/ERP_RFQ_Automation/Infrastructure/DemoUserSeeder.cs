using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Infrastructure;

public static class DemoUserSeeder
{
    public static async Task EnsureAsync(IServiceProvider services, IConfiguration configuration, IHostEnvironment environment)
    {
        // Fail-closed: seeding is off unless a deployment explicitly opts in.
        var enabled = configuration.GetValue("DemoUser:Enabled", false);
        if (!enabled) return;

        using var scope = services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DemoUserSeeder");

        // Fail-closed: this seeder provisions a SUPER_ADMIN tenant login AND a platform owner.
        // It runs from startup, outside any HttpContext, which means CurrentTenantId is null
        // (EF global query filters are no-ops) and TenantRlsCommandInterceptor.ResolveDatabaseRole
        // returns the BYPASSRLS nexora_pipeline_app role — both tenant-isolation layers are off
        // for the duration. That is acceptable for a pilot/demo tenant and is not acceptable in
        // Production, so Production refuses rather than warns.
        if (environment.IsProduction())
        {
            logger.LogError(
                "DemoUserSeeder refused to run: DemoUser:Enabled is true in the Production environment. "
                + "This seeder provisions a Super Admin tenant login and a platform owner and is a demo/pilot "
                + "facility only. Set DemoUser:Enabled=false, or run this deployment under a non-Production "
                + "ASPNETCORE_ENVIRONMENT.");
            return;
        }

        var email = configuration["DemoUser:Email"] ?? "robert@example.com";
        var businessUnitName = configuration["DemoUser:BusinessUnitName"] ?? "Customer POC";
        var businessUnitCode = configuration["DemoUser:BusinessUnitCode"] ?? "CUSTOMER-POC";
        var roleName = configuration["DemoUser:RoleName"] ?? "Super Admin";
        var platformEmail = configuration["PlatformOwner:Email"] ?? "owner@nexora.app";

        // Passwords must be supplied explicitly. No hardcoded fallback credential is ever seeded,
        // so a production deployment cannot inherit a repo-published password.
        var password = configuration["DemoUser:Password"];
        var platformPassword = configuration["PlatformOwner:Password"];
        if (string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(platformPassword))
        {
            logger.LogWarning(
                "DemoUser:Enabled is true but DemoUser:Password and/or PlatformOwner:Password are not set. Skipping seeding — no default credential will be created.");
            return;
        }

        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var now = DateTime.UtcNow;

        var businessUnit = await db.BusinessUnits
            .FirstOrDefaultAsync(b => b.BusinessUnitName == businessUnitName || b.BusinessUnitCode == businessUnitCode);

        if (businessUnit == null)
        {
            businessUnit = new BusinessUnit
            {
                BusinessUnitCode = businessUnitCode,
                BusinessUnitName = businessUnitName,
                Description = "Demo tenant for Nexora pilot access.",
                IsActive = true,
                CreatedBy = "system:demo-seed",
                CreatedOn = now
            };
            db.BusinessUnits.Add(businessUnit);
            await db.SaveChangesAsync();
        }
        else if (businessUnit.IsActive != true)
        {
            businessUnit.IsActive = true;
            businessUnit.ModifiedBy = "system:demo-seed";
            businessUnit.ModifiedOn = now;
            await db.SaveChangesAsync();
        }

        if (!await db.AiProcessingPolicies.AnyAsync(p => p.BusinessUnitId == businessUnit.Id))
        {
            db.AiProcessingPolicies.Add(
                AiProcessingPolicy.CreateSecureDefault(businessUnit.Id, "system:demo-seed", now));
            await db.SaveChangesAsync();
        }

        var role = await db.SetupMasters
            .Where(ERP_RFQ_Automation.Authorization.SetupTypes.IsRoleRow)
            .FirstOrDefaultAsync(s =>
                s.BusinessUnitId == businessUnit.Id &&
                s.SetupValue == roleName);

        if (role == null)
        {
            role = new SetupMaster
            {
                SetupType = "Role",
                SetupCode = "SUPER_ADMIN",
                SetupValue = roleName,
                Description = "Full access demo role.",
                BusinessUnitId = businessUnit.Id,
                IsActive = true,
                CreatedBy = "system:demo-seed",
                CreatedOn = now
            };
            db.SetupMasters.Add(role);
            await db.SaveChangesAsync();
        }
        else if (role.IsActive != true || role.SetupCode != "SUPER_ADMIN")
        {
            role.IsActive = true;
            role.SetupCode = "SUPER_ADMIN";
            role.ModifiedBy = "system:demo-seed";
            role.ModifiedOn = now;
            await db.SaveChangesAsync();
        }

        var passwordHash = BCrypt.Net.BCrypt.HashPassword(password);

        // Users.Email carries a GLOBAL unique index (UQ__Users__A9D10534A3A2A11E), so the address
        // configured here may already belong to a real account in a DIFFERENT business unit. The
        // lookup used to be by email alone, which — combined with the null tenant context above —
        // matched that foreign account and then rewrote its Buid to the demo tenant and its RoleId
        // to SUPER_ADMIN. Resolve the demo user by (email, demo business unit) and treat a match on
        // any other tenant as a configuration error to refuse, never as a row to adopt.
        var accountForEmail = await db.Users.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Email == email);
        var user = accountForEmail?.Buid == businessUnit.Id ? accountForEmail : null;

        if (accountForEmail is not null && user is null)
        {
            logger.LogError(
                "DemoUserSeeder refused to seed {Email}: that address already belongs to an existing "
                + "account in a different business unit. Seeding it would move that account into the "
                + "demo tenant {BusinessUnit} and grant it Super Admin. Choose a different "
                + "DemoUser:Email.", email, businessUnit.BusinessUnitName);
        }
        else
        {
            if (user == null)
            {
                user = new User
                {
                    FirstName = "Robert",
                    LastName = "Pilot",
                    Email = email,
                    PasswordHash = passwordHash,
                    ImageUrl = string.Empty,
                    RoleId = role.SetupId,
                    Buid = businessUnit.Id,
                    Timezone = "UTC",
                    Region = "Demo",
                    IsActive = true,
                    CreatedBy = "system:demo-seed",
                    CreatedOn = now
                };
                db.Users.Add(user);
            }
            else
            {
                // Never overwrite an existing user's password on restart — that would silently reset a
                // credential an operator may have already rotated. Only backfill non-credential metadata.
                user.FirstName = string.IsNullOrWhiteSpace(user.FirstName) ? "Robert" : user.FirstName;
                user.LastName = string.IsNullOrWhiteSpace(user.LastName) ? "Pilot" : user.LastName;
                user.RoleId = role.SetupId;
                user.Buid = businessUnit.Id;
                user.IsActive = true;
                user.DeactivatedAtUtc = null; // reactivation clears the deactivation stamp
                user.ModifiedBy = "system:demo-seed";
                user.ModifiedOn = now;
            }

            await db.SaveChangesAsync();
            logger.LogInformation("Ensured demo login user {Email} for business unit {BusinessUnit}.", email, businessUnit.BusinessUnitName);
        }

        var platformOwner = await db.Set<PlatformUser>().FirstOrDefaultAsync(u => u.Email == platformEmail);
        var platformPasswordHash = BCrypt.Net.BCrypt.HashPassword(platformPassword);

        if (platformOwner == null)
        {
            platformOwner = new PlatformUser
            {
                Email = platformEmail,
                PasswordHash = platformPasswordHash,
                PlatformRole = PlatformRole.Owner,
                IsActive = true,
                DisplayName = "Platform Owner",
                CreatedBy = "system:demo-seed",
                CreatedOn = now
            };
            db.Set<PlatformUser>().Add(platformOwner);
        }
        else
        {
            // Do not reset an existing platform owner's password on restart.
            platformOwner.PlatformRole = PlatformRole.Owner;
            platformOwner.IsActive = true;
            platformOwner.DisplayName = string.IsNullOrWhiteSpace(platformOwner.DisplayName)
                ? "Platform Owner"
                : platformOwner.DisplayName;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Ensured platform owner login user {Email}.", platformEmail);
    }
}
