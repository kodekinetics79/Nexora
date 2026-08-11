using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
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

        // THE lifecycle states. This seeder created a business unit, an AI policy, a role and a
        // user — and no Setup_Master lifecycle rows at all, which is a business unit that cannot
        // raise a quote. QuoteService resolves QuoteStatus/DRAFT and falls back to the documented
        // legacy id 42, and Quotes."StatusID" is a foreign key to Setup_Master."SetupID": with no
        // row carrying that id the first quote fails on the foreign key, and where some other
        // tenant's row happens to hold it the quote is stamped with a status from somebody else's
        // workspace. OrderService then refuses the conversion outright ("No OrderStatus setup found
        // in the system"). This is a demo/pilot tenant — the one somebody walks the journey on —
        // so it is exactly the tenant on which that must not happen.
        var lifecycleStatuses = await LifecycleStatusCatalog.EnsureAsync(
            db, businessUnit, "system:demo-seed", now);
        if (lifecycleStatuses > 0)
        {
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Seeded {Count} lifecycle status row(s) for demo business unit {BusinessUnitId}.",
                lifecycleStatuses, businessUnit.Id);
        }

        await EnsureGovernedPlatformTenantAsync(db, businessUnit, logger, now);

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
                // Authority is the RoleRank column now, not the "SUPER_ADMIN" string. Without this
                // the demo tenant's owner role would seed at Member and the demo account would have
                // no access at all.
                RoleRank = ERP_RFQ_Automation.Authorization.RoleRanks.Owner,
                BusinessUnitId = businessUnit.Id,
                IsActive = true,
                CreatedBy = "system:demo-seed",
                CreatedOn = now
            };
            db.SetupMasters.Add(role);
            await db.SaveChangesAsync();
        }
        else if (role.IsActive != true
                 || role.SetupCode != "SUPER_ADMIN"
                 || role.RoleRank < ERP_RFQ_Automation.Authorization.RoleRanks.Owner)
        {
            role.IsActive = true;
            role.SetupCode = "SUPER_ADMIN";
            // Repair path for a demo tenant seeded before RoleRank existed.
            role.RoleRank = ERP_RFQ_Automation.Authorization.RoleRanks.Owner;
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

    /// <summary>The demo plan's code. Stable so a re-run converges on the same row.</summary>
    private const string DemoPlanCode = "demo-local";

    /// <summary>
    /// Makes the demo business unit a GOVERNED PLATFORM TENANT: a <c>platform.Plans</c> row that
    /// enables every runtime-available entitlement, and a <c>platform.Tenants</c> row that is Active
    /// and points at the business unit.
    ///
    /// <para><b>The gap this closes.</b> This seeder created a BusinessUnit, an AI policy, lifecycle
    /// statuses, a role and a Super Admin user — and no control-plane record at all.
    /// <c>TenantAccessService.CoreQuery</c> resolves a business unit's entitlements through
    /// <c>Tenant.PrimaryBusinessUnitId</c>, so with no Tenant row every
    /// <c>[RequiresEntitlement]</c> action answered 403 <i>"Entitlement 'module.rfq' is unavailable
    /// because no governed platform tenant owns this business unit"</i>. Reproduced against
    /// PostgreSQL on 2026-08-12: sign in as the demo user, <c>GET /api/Rfq</c> → 403 with exactly
    /// that detail. Thirty-five client-portal routes are behind those attributes, which is the whole
    /// RFQ → quote → order journey — so the demo tenant could not walk the journey it exists to
    /// demonstrate until somebody hand-inserted a plan and a tenant row.</para>
    ///
    /// <para><b>This does not weaken the check.</b> Nothing about
    /// <c>EntitlementService.CheckFeatureAsync</c> changes: it still demands a tenant, still demands
    /// a plan, and still reads the plan's own feature map. This seeds the data the check asks for,
    /// which is the same data <c>TenantProvisioningRunner</c> writes on the real provisioning path.
    /// A tenant seeded here is indistinguishable to the enforcement code from a provisioned one.</para>
    ///
    /// <para>Only entitlements with a real server execution boundary
    /// (<c>TypedEntitlementCatalog.RuntimeAvailableKeys</c>) are enabled. Turning on a packaging
    /// flag for an unimplemented capability would advertise a surface that does not exist — and
    /// runtime authorization denies it anyway — so the demo plan says exactly what the product can
    /// currently do. Same class of gap, and same fix, as the LifecycleStatusCatalog.EnsureAsync call
    /// above: a demo tenant that cannot raise a quote is not a demo.</para>
    ///
    /// <para>Idempotent by natural key and NEVER by overwrite: an existing plan's features and an
    /// existing tenant's status are left exactly as they are, because by the second run an operator
    /// may have suspended the tenant or narrowed the plan deliberately, and re-imposing a default
    /// over that would be an unlogged reversal of their decision. The one exception is a Tenant row
    /// with no plan at all, which is not a decision — it is the gap.</para>
    /// </summary>
    private static async Task EnsureGovernedPlatformTenantAsync(
        ErpRfqAutomationContext db, BusinessUnit businessUnit, ILogger logger, DateTime now)
    {
        var plan = await db.Set<Plan>().FirstOrDefaultAsync(p => p.Code == DemoPlanCode);
        if (plan is null)
        {
            plan = new Plan
            {
                Code = DemoPlanCode,
                Name = "Local Demo",
                Weight = 5,
                MaxConcurrentExtractionJobs = 4,
                MaxDocsPerMonth = 5000,
                MaxSeats = 25,
                Features = DemoPlanFeaturesJson(),
                MonthlyPriceUsd = null,
                IsActive = true,
                CreatedOn = now
            };
            db.Set<Plan>().Add(plan);
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded demo plan '{PlanCode}'.", DemoPlanCode);
        }

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.PrimaryBusinessUnitId == businessUnit.Id);
        if (tenant is null)
        {
            // The slug is derived from the business unit code the same way the provisioning wizard
            // derives the code from the slug, so the demo tenant reads like a provisioned one.
            var slug = businessUnit.BusinessUnitCode.ToLowerInvariant();
            db.Set<Tenant>().Add(new Tenant
            {
                Name = businessUnit.BusinessUnitName,
                Slug = slug,
                Status = TenantStatus.Active,
                PlanId = plan.Id,
                PrimaryBusinessUnitId = businessUnit.Id,
                BillingMode = TenantBillingMode.Internal,
                BillingModeReason =
                    "Local demo/pilot tenant seeded by DemoUserSeeder; never invoiced. Recorded so the "
                    + "unconfigured-tenant allowance can tell a deliberate exemption from an oversight.",
                CreatedBy = "system:demo-seed",
                CreatedOn = now
            });
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Ensured governed platform tenant '{Slug}' (Active, plan '{PlanCode}') for demo business unit {BusinessUnitId}.",
                slug, DemoPlanCode, businessUnit.Id);
            return;
        }

        // A tenant with no plan cannot be entitled to anything; attach the demo plan and say so.
        // Everything else about an existing tenant is left untouched.
        if (tenant.PlanId is null)
        {
            tenant.PlanId = plan.Id;
            tenant.ModifiedBy = "system:demo-seed";
            tenant.ModifiedOn = now;
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Attached demo plan '{PlanCode}' to existing platform tenant {TenantId}, which had none.",
                DemoPlanCode, tenant.Id);
        }
    }

    /// <summary>
    /// The demo plan's feature map: every catalogue key present and explicitly true/false, so an
    /// absent key can never be mistaken for an intentional grant. True exactly for the entitlements
    /// the server can actually execute.
    /// </summary>
    private static string DemoPlanFeaturesJson()
    {
        var features = ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToDictionary(
                key => key,
                ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.IsRuntimeAvailable,
                StringComparer.Ordinal);
        return System.Text.Json.JsonSerializer.Serialize(features);
    }
}
