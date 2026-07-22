using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Infrastructure;

public static class DemoUserSeeder
{
    public static async Task EnsureAsync(IServiceProvider services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue("DemoUser:Enabled", true);
        if (!enabled) return;

        var email = configuration["DemoUser:Email"] ?? "robert@example.com";
        var password = configuration["DemoUser:Password"] ?? "Nexora#Pilot-a9bc9e";
        var businessUnitName = configuration["DemoUser:BusinessUnitName"] ?? "Customer POC";
        var businessUnitCode = configuration["DemoUser:BusinessUnitCode"] ?? "CUSTOMER-POC";
        var roleName = configuration["DemoUser:RoleName"] ?? "Super Admin";

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DemoUserSeeder");
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
            user.FirstName = string.IsNullOrWhiteSpace(user.FirstName) ? "Robert" : user.FirstName;
            user.LastName = string.IsNullOrWhiteSpace(user.LastName) ? "Pilot" : user.LastName;
            user.PasswordHash = passwordHash;
            user.RoleId = role.SetupId;
            user.Buid = businessUnit.Id;
            user.IsActive = true;
            user.ModifiedBy = "system:demo-seed";
            user.ModifiedOn = now;
        }

        await db.SaveChangesAsync();
        logger.LogInformation("Ensured demo login user {Email} for business unit {BusinessUnit}.", email, businessUnit.BusinessUnitName);
    }
}
