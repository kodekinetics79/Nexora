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

        if (environment.IsProduction())
        {
            logger.LogWarning(
                "DemoUserSeeder is running in the Production environment (DemoUser:Enabled=true). Ensure the seeded credentials are intended and rotated after first login.");
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
            .FirstOrDefaultAsync(s =>
                s.BusinessUnitId == businessUnit.Id &&
                s.SetupType.ToLower() == "role" &&
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
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email);

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
            user.ModifiedBy = "system:demo-seed";
            user.ModifiedOn = now;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Ensured demo login user {Email} for business unit {BusinessUnit}.", email, businessUnit.BusinessUnitName);

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
