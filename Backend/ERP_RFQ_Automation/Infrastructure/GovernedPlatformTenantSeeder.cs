using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// Makes a locally-seeded business unit a GOVERNED PLATFORM TENANT: a <c>platform.Plans</c> row
/// that enables every runtime-available entitlement, and a <c>platform.Tenants</c> row that is
/// Active and points at the business unit.
///
/// <para><b>The gap this closes.</b> A seeder that creates a BusinessUnit, an AI policy, lifecycle
/// statuses, a role and a user — and no control-plane record at all — produces a tenant that cannot
/// use the product. <c>TenantAccessService.CoreQuery</c> resolves a business unit's entitlements
/// through <c>Tenant.PrimaryBusinessUnitId</c>, so with no Tenant row every
/// <c>[RequiresEntitlement]</c> action answers 403 <i>"Entitlement 'module.rfq' is unavailable
/// because no governed platform tenant owns this business unit"</i>. Reproduced against PostgreSQL
/// on 2026-08-12 against BOTH seeders: sign in as the seeded user, <c>GET /api/Rfq</c> → 403 with
/// exactly that detail. Thirty-five client-portal routes are behind those attributes, which is the
/// whole RFQ → quote → order journey — so the seeded tenant could not walk the journey it exists to
/// demonstrate (demo) or to certify (golden E2E) until somebody hand-inserted a plan and a tenant
/// row.</para>
///
/// <para><b>This does not weaken the check.</b> Nothing about
/// <c>EntitlementService.CheckFeatureAsync</c> changes: it still demands a tenant, still demands a
/// plan, and still reads the plan's own feature map. This seeds the data the check asks for, which
/// is the same data <c>TenantProvisioningRunner</c> writes on the real provisioning path. A tenant
/// seeded here is indistinguishable to the enforcement code from a provisioned one.</para>
///
/// <para><b>One implementation, two callers.</b> <see cref="DemoUserSeeder"/> and
/// <see cref="GoldenCommercialJourneySeeder"/> both call this. It is deliberately NOT copied into
/// each of them: two divergent copies of a control-plane seeder is the class of defect this
/// codebase keeps paying for, and the failure mode is silent — the copy that drifts hands its
/// tenant a plan the enforcement code reads differently, and nothing says so.</para>
///
/// <para>Only entitlements with a real server execution boundary
/// (<c>TypedEntitlementCatalog.RuntimeAvailableKeys</c>) are enabled. Turning on a packaging flag
/// for an unimplemented capability would advertise a surface that does not exist — and runtime
/// authorization denies it anyway — so a locally seeded plan says exactly what the product can
/// currently do.</para>
///
/// <para>Idempotent by natural key and NEVER by overwrite: an existing plan's features and an
/// existing tenant's status are left exactly as they are, because by the second run an operator may
/// have suspended the tenant or narrowed the plan deliberately, and re-imposing a default over that
/// would be an unlogged reversal of their decision. The one exception is a Tenant row with no plan
/// at all, which is not a decision — it is the gap.</para>
///
/// <para><b>Labelled for what it is.</b> A tenant CREATED here is written on the
/// <c>LOCAL_TEST</c> deployment profile with a recorded reason. It changes nothing about runtime
/// enforcement — the profile is read only by the activation surface — but it stops a seeded
/// workspace from sitting on a shared platform console looking exactly like a real customer that
/// passed every activation control. It is set at creation because it cannot be set later: the
/// audited endpoint refuses to move a tenant off <c>PRODUCTION</c> once it has left Provisioning,
/// and these rows are Active from the start. An EXISTING tenant is never re-labelled, for the same
/// reason its status is never re-imposed.</para>
///
/// <para><b>Caller responsibility.</b> This helper carries no environment guard of its own; both
/// callers refuse under Production before reaching it, and that refusal is theirs to keep because
/// it also governs the logins and commercial records they write.</para>
/// </summary>
internal static class GovernedPlatformTenantSeeder
{
    /// <summary>
    /// Ensures <paramref name="businessUnit"/> is owned by an Active platform tenant carrying the
    /// plan identified by <paramref name="planCode"/>, creating either only when absent.
    /// </summary>
    /// <param name="planCode">Stable plan code. Re-runs converge on the same row.</param>
    /// <param name="planName">Display name, used only when the plan is created.</param>
    /// <param name="actor">Audit stamp written to <c>CreatedBy</c>/<c>ModifiedBy</c>.</param>
    /// <param name="billingModeReason">
    /// Why this tenant is never invoiced. Recorded rather than left blank so the
    /// unconfigured-tenant allowance can tell a deliberate exemption from an oversight.
    /// </param>
    internal static async Task EnsureAsync(
        ErpRfqAutomationContext db,
        BusinessUnit businessUnit,
        string planCode,
        string planName,
        string actor,
        string billingModeReason,
        ILogger logger,
        DateTime now)
    {
        var plan = await db.Set<Plan>().FirstOrDefaultAsync(p => p.Code == planCode);
        if (plan is null)
        {
            plan = new Plan
            {
                Code = planCode,
                Name = planName,
                Weight = 5,
                MaxConcurrentExtractionJobs = 4,
                MaxDocsPerMonth = 5000,
                MaxSeats = 25,
                Features = RuntimeAvailableFeaturesJson(),
                MonthlyPriceUsd = null,
                IsActive = true,
                CreatedOn = now
            };
            db.Set<Plan>().Add(plan);
            await db.SaveChangesAsync();
            logger.LogInformation("Seeded local plan '{PlanCode}'.", planCode);
        }

        // ORDER BY Id, exactly as the fixture repair contract requires. Nothing enforces one
        // Tenant per PrimaryBusinessUnitId in the legacy schema, so the development seeder repairs
        // the stable canonical (oldest) row. Runtime authorization is deliberately stricter:
        // TenantAccessService refuses an ambiguous mapping instead of using this repair rule.
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .OrderBy(t => t.Id)
            .FirstOrDefaultAsync(t => t.PrimaryBusinessUnitId == businessUnit.Id);
        if (tenant is null)
        {
            // The slug is derived from the business unit code the same way the provisioning wizard
            // derives the code from the slug, so a seeded tenant reads like a provisioned one.
            var slug = businessUnit.BusinessUnitCode.ToLowerInvariant();
            db.Set<Tenant>().Add(new Tenant
            {
                Name = businessUnit.BusinessUnitName,
                Slug = slug,
                Status = TenantStatus.Active,
                PlanId = plan.Id,
                // Copied from the seeded plan for the same reason provisioning copies it: the
                // tenant's own column is what [RequiresEntitlement] reads now, so a seeded
                // workspace left on the {} default would come up with every module denied and the
                // seed would grant nothing it claims to grant.
                Entitlements = TypedEntitlementCatalog.Complete(plan.Features),
                PrimaryBusinessUnitId = businessUnit.Id,
                BillingMode = TenantBillingMode.Internal,
                BillingModeReason = billingModeReason,
                // Classified at CREATION, because it can never be classified afterwards: the audited
                // endpoint refuses to move any tenant off PRODUCTION once it has left Provisioning
                // (TenantsController.SetDeploymentProfile), and this row is Active from its first
                // instant — correctly so, since Provisioning would deny the access the seed exists to
                // grant. Left on the default PRODUCTION profile, a seeded workspace sits on a shared
                // console indistinguishable from a real customer that passed every activation
                // control, and permanently so. LOCAL_TEST is the true statement, and it is the
                // conservative one: it relaxes nothing at runtime, it only lets the activation
                // surface record catalogued external prerequisites as deferred instead of asserting
                // that a laptop satisfied them. Every one of them stays a production blocker and
                // this tenant stays uncertifiable — which is exactly what should be true of it.
                DeploymentProfile = TenantDeploymentProfile.LocalTest,
                DeploymentProfileReason =
                    $"Seeded workspace, not a customer. Created by {actor} for business unit "
                    + $"'{businessUnit.BusinessUnitCode}' on a local or CI deployment: no customer data, "
                    + "no invoices, and none of the third-party estate a real customer brings.",
                CreatedBy = actor,
                CreatedOn = now
            });
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Ensured governed platform tenant '{Slug}' (Active, plan '{PlanCode}', deployment profile "
                + "{DeploymentProfile}) for business unit {BusinessUnitId}.",
                slug, planCode,
                TenantDeploymentProfiles.ToWire(TenantDeploymentProfile.LocalTest), businessUnit.Id);
            return;
        }

        // A tenant with no plan has no capacity; attach the plan and say so. Everything else about
        // an existing tenant — its status above all — is left untouched.
        var attachedPlan = false;
        if (tenant.PlanId is null)
        {
            tenant.PlanId = plan.Id;
            attachedPlan = true;
        }

        // Since 20260818013530 the plan no longer decides what a tenant may open, so attaching one
        // is no longer enough to make a seeded workspace usable: the tenant's own grant is what
        // [RequiresEntitlement] reads, and a pre-existing row created before that migration — or by
        // a fixture that predates it — carries the closed default.
        //
        // Guarded on the grant still being EMPTY. A seeder that overwrote a decided grant would
        // quietly re-open, on every boot, a module an operator had deliberately revoked; and this
        // runs against whatever database it is pointed at. Empty is unambiguously "nobody has
        // decided yet", which is the only state it is safe to fill.
        if (!TypedEntitlementCatalog.TryParse(tenant.Entitlements, out var granted, out _)
            || !granted.Values.Any(enabled => enabled))
        {
            tenant.Entitlements = TypedEntitlementCatalog.Complete(plan.Features);
            attachedPlan = true;
            logger.LogInformation(
                "Seeded module grants for platform tenant {TenantId} from plan '{PlanCode}': its own "
                + "grant was empty, so every module was denied.", tenant.Id, planCode);
        }

        if (attachedPlan)
        {
            tenant.ModifiedBy = actor;
            tenant.ModifiedOn = now;
            await db.SaveChangesAsync();
            logger.LogInformation(
                "Ensured plan '{PlanCode}' and module grants on existing platform tenant {TenantId}.",
                planCode, tenant.Id);
        }
    }

    /// <summary>
    /// The seeded plan's feature map: every catalogue key present and explicitly true/false, so an
    /// absent key can never be mistaken for an intentional grant. True exactly for the entitlements
    /// the server can actually execute.
    /// </summary>
    internal static string RuntimeAvailableFeaturesJson()
    {
        var features = TypedEntitlementCatalog.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToDictionary(
                key => key,
                TypedEntitlementCatalog.IsRuntimeAvailable,
                StringComparer.Ordinal);
        return System.Text.Json.JsonSerializer.Serialize(features);
    }
}
