using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// A step refused to complete, with a verdict on whether trying again could ever help.
///
/// <para><see cref="IsTerminal"/> is the difference between an execution the runner should
/// re-attempt on its own and one that needs a human to change something. Getting it wrong in
/// either direction is expensive: a terminal condition retried is a hundred identical log lines
/// burying the real message, and a transient condition marked terminal is an operator told to
/// edit a request that was perfectly correct.</para>
/// </summary>
public sealed class ProvisioningStepException : Exception
{
    public ProvisioningStepException(string message, bool isTerminal, string? failureCode = null)
        : base(message)
    {
        IsTerminal = isTerminal;
        FailureCode = failureCode;
    }

    public bool IsTerminal { get; }

    public string? FailureCode { get; }
}

/// <summary>Everything one step needs, on the step's own unit of work.</summary>
/// <param name="Execution">Tracked by <paramref name="Db"/>, so ids it records commit with the work.</param>
/// <param name="Request">The submitted request, rehydrated. Carries no credential.</param>
/// <param name="Actor">Operator email, stamped into every <c>CreatedBy</c>.</param>
/// <param name="IsRetry">
/// True when this step has already been attempted. The invitation step reads it and compensates
/// instead of re-issuing; nothing else needs it, and nothing else should.
/// </param>
public sealed record ProvisioningStepContext(
    ErpRfqAutomationContext Db,
    ProvisioningExecution Execution,
    ProvisionTenantRequest Request,
    string Actor,
    bool IsRetry)
{
    /// <summary>
    /// The cleartext activation token, when the invitation step minted one. In memory only, and
    /// the runner clears it after the mail has gone out.
    ///
    /// <para>Mutable, and OVERWRITTEN by every execution-strategy attempt, deliberately: the
    /// token that activates the account is the one belonging to the attempt that COMMITTED. A
    /// token from an attempt whose transaction rolled back hashes to a row that no longer exists
    /// and activates nothing, so keeping the first one would email the customer a dead link.</para>
    /// </summary>
    public IssuedTenantAdminInvitation? Issued { get; set; }
}

/// <summary>
/// The nine steps, as ordinary methods on a scoped service.
///
/// <para><b>Every step is written to be run twice.</b> Not because retries are expected, but
/// because they are unavoidable: EF's <c>EnableRetryOnFailure</c> execution strategy re-invokes
/// the whole delegate on a transient error, an operator can press retry, and a runner whose
/// process died mid-step leaves work for the next one to pick up. So each step establishes what
/// already exists before it writes — either from an id the previous attempt recorded on the
/// execution row inside the same transaction that created it, or by reading the target rows.</para>
///
/// <para><b>No tenant scope is pushed, deliberately.</b> These steps write tenant tables
/// (<c>BusinessUnits</c>, <c>SetupMasters</c>, <c>Users</c>, and everything the baseline seeder
/// touches) from the platform plane, exactly as <c>TenantsController.Provision</c> does today.
/// Pushing a scope would route the commands to <c>nexora_tenant_app</c>, which is
/// RLS-constrained and has no privilege to create a business unit that does not exist yet — the
/// same reasoning that keeps <c>BillingRunWorker</c> unscoped. With no scope the command
/// interceptor supplies <c>nexora_pipeline_app</c>, which is what a control-plane write needs.</para>
/// </summary>
public interface IProvisioningStepExecutor
{
    /// <summary>Runs one step and returns its <c>Detail</c> JSON, or null when it has nothing to report.</summary>
    Task<string?> ExecuteAsync(string stepCode, ProvisioningStepContext context, CancellationToken ct);
}

public sealed class ProvisioningStepExecutor : IProvisioningStepExecutor
{
    private readonly ITenantBaselineSeeder _baseline;
    private readonly ITenantAdminInvitationService _invitations;
    private readonly IPlatformDataBoundaryProvisioner? _dataBoundaries;
    private readonly IPlatformDataBoundaryManifest? _manifest;

    /// <param name="dataBoundaries">
    /// OPTIONAL, and a null one degrades to today's manual data-boundary registration rather than
    /// to a silent pass. Optional so the existing two-argument construction in any partially wired
    /// host keeps working; the loss is an activation control that stays blocking until an operator
    /// registers the boundary by hand, never an automatic pass on something nobody looked at.
    /// </param>
    public ProvisioningStepExecutor(
        ITenantBaselineSeeder baseline, ITenantAdminInvitationService invitations,
        IPlatformDataBoundaryProvisioner? dataBoundaries = null,
        IPlatformDataBoundaryManifest? manifest = null)
    {
        _baseline = baseline;
        _invitations = invitations;
        _dataBoundaries = dataBoundaries;
        _manifest = manifest;
    }

    public Task<string?> ExecuteAsync(
        string stepCode, ProvisioningStepContext context, CancellationToken ct) => stepCode switch
    {
        ProvisioningStepCodes.Tenant => CreateTenantAsync(context, ct),
        ProvisioningStepCodes.BusinessUnit => CreateBusinessUnitAsync(context, ct),
        ProvisioningStepCodes.LifecycleStatuses => SeedLifecycleStatusesAsync(context, ct),
        ProvisioningStepCodes.AiPolicy => CreateAiPolicyAsync(context, ct),
        ProvisioningStepCodes.FoundingRole => CreateFoundingRoleAsync(context, ct),
        ProvisioningStepCodes.FoundingAdmin => CreateFoundingAdminAsync(context, ct),
        ProvisioningStepCodes.BaselineSeed => SeedBaselineAsync(context, ct),
        ProvisioningStepCodes.DataBoundaries => RegisterDataBoundariesAsync(context, ct),
        ProvisioningStepCodes.Invitation => IssueInvitationAsync(context, ct),
        _ => throw new ProvisioningStepException(
            $"'{stepCode}' is not a provisioning step this server knows how to run.", isTerminal: true)
    };

    // ---- 1. the tenant record -----------------------------------------------------------

    private async Task<string?> CreateTenantAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, request, actor, _) = context;

        // The id was written inside the transaction that created the row, so its presence is
        // proof the row committed. Reading the row back would be the weaker check: a slug
        // lookup cannot distinguish OUR tenant from one an operator created by hand between
        // attempts, and adopting a stranger's tenant is a far worse outcome than a failed retry.
        if (execution.TenantId is { } existingTenantId)
        {
            var exists = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(t => t.Id == existingTenantId, ct);
            if (exists)
                return Detail(new { tenantId = existingTenantId, reused = true });

            // The recorded id points at nothing. Somebody deleted the tenant out from under an
            // in-flight execution, and continuing would attach a business unit to a ghost.
            throw new ProvisioningStepException(
                $"Tenant {existingTenantId} was recorded by an earlier attempt but no longer exists. " +
                "The execution cannot be resumed; cancel it and submit again.",
                isTerminal: true);
        }

        var slug = execution.Slug;
        if (await db.Set<Tenant>().IgnoreQueryFilters().AnyAsync(t => t.Slug == slug, ct))
            throw new ProvisioningStepException(
                $"The workspace address '{slug}' now belongs to another tenant. Choose a different " +
                "address and submit again — retrying this execution cannot succeed.",
                isTerminal: true, failureCode: "slug-taken");

        // Re-verified here rather than trusted from submit. Minutes can pass between an operator
        // pressing submit and a runner picking the work up, and a plan deactivated in that window
        // would otherwise produce a tenant billed against a plan the business has withdrawn.
        // Features ride along with the activeness check because the plan is also the source of the
        // tenant's STARTING module grant — read here, at the same instant the plan is verified, so
        // the copy can never be taken from a plan that turned out to be withdrawn.
        string? planFeatures = null;
        if (request.PlanId is long planId)
        {
            var plan = await db.Set<Plan>().AsNoTracking()
                .Select(p => new { p.Id, p.Code, p.IsActive, p.Features })
                .FirstOrDefaultAsync(p => p.Id == planId, ct);
            if (plan is null)
                throw new ProvisioningStepException(
                    $"Plan {planId} no longer exists.", isTerminal: true, failureCode: "plan-missing");
            if (!plan.IsActive)
                throw new ProvisioningStepException(
                    $"Plan '{plan.Code}' has been deactivated since this request was submitted and " +
                    "cannot be assigned.", isTerminal: true, failureCode: "plan-inactive");
            planFeatures = plan.Features;
        }

        var billingMode = TenantBillingMode.Billable;
        if (Normalize(request.BillingMode) is string requestedMode
            && !Enum.TryParse(requestedMode, ignoreCase: true, out billingMode))
            throw new ProvisioningStepException(
                $"billingMode '{requestedMode}' is not recognised.", isTerminal: true);

        var adminEmail = execution.AdminEmail;
        var now = DateTime.UtcNow;

        // Absent or unparseable is PRODUCTION, deliberately, and without failing the step: TryParse
        // yields Production on both, the value came through a door that already refused anything
        // unrecognised, and the safe reading of a profile nobody can parse is the strictest one.
        TenantDeploymentProfiles.TryParse(request.DeploymentProfile, out var requestedProfile);

        var tenant = new Tenant
        {
            Name = execution.Name,
            Slug = slug,
            Status = TenantStatus.Provisioning,
            PlanId = request.PlanId,
            // The plan's features COPIED once as a starting template — not inherited. Since
            // 20260818013530 this column is what [RequiresEntitlement] reads, so a tenant left on
            // the closed default would provision cleanly and be able to open nothing; and a tenant
            // that INHERITED would have its operator's later revoke undone by the next plan edit.
            // Completed to the whole catalogue so the tenant is born DECIDED, which is what the
            // entitlements.typed-hard-limits activation control requires.
            Entitlements = Entitlements.TypedEntitlementCatalog.Complete(planFeatures),
            CreatedBy = actor,
            CreatedOn = now,

            LegalName = Normalize(request.LegalName),
            RegistrationNumber = Normalize(request.RegistrationNumber),
            TaxNumber = Normalize(request.TaxNumber),
            CountryCode = Normalize(request.CountryCode)?.ToUpperInvariant(),
            Industry = Normalize(request.Industry),
            Website = Normalize(request.Website),
            AddressLine1 = Normalize(request.AddressLine1),
            AddressLine2 = Normalize(request.AddressLine2),
            City = Normalize(request.City),
            StateProvince = Normalize(request.StateProvince),
            PostalCode = Normalize(request.PostalCode),
            Phone = Normalize(request.Phone),
            ContactEmail = Normalize(request.ContactEmail),
            LogoUrl = Normalize(request.LogoUrl),

            BaseCurrencyCode = Normalize(request.BaseCurrencyCode)?.ToUpperInvariant(),
            TimeZoneId = Normalize(request.TimeZoneId),
            Locale = Normalize(request.Locale),
            // Left blank by the operator means "wherever this deployment puts tenants", not "no
            // residency claim": every tenant lives in the database this deployment declares, and
            // the console has no better answer to type in than the one the deployment already
            // holds. A blank column, by contrast, is fatal — the residency control reads its
            // presence and the scope probe has nothing to agree with, so a tenant provisioned
            // without it can never be activated until somebody edits it back in through a
            // governed endpoint. A submitted region is never overridden, and a deployment that
            // declares nothing still records exactly what was typed, including nothing.
            DataRegion = Normalize(request.DataRegion)
                         ?? _manifest?.For(TenantDataAssetTypes.PostgreSqlTenantScope)?.Region,

            // Recorded here rather than left to a second Owner call on a screen the operator has
            // not opened yet. The AUTHORITY for it was checked at the door — both provisioning
            // endpoints run PlatformDeploymentProfileAuthority, which refuses a non-production
            // profile to anyone but an Owner and refuses it to an Owner without a reason — so by
            // the time the step runs, an unparseable or unauthorised profile cannot be here. A
            // stored payload naming one anyway is treated as PRODUCTION: the strict profile is the
            // only safe reading of a value whose provenance is unknown.
            DeploymentProfile = requestedProfile,
            DeploymentProfileReason = requestedProfile == TenantDeploymentProfile.Production
                ? null : Normalize(request.DeploymentProfileReason),
            DeploymentProfileApprovedBy = requestedProfile == TenantDeploymentProfile.Production
                ? null : actor,
            DeploymentProfileApprovedOn = requestedProfile == TenantDeploymentProfile.Production
                ? null : now,

            BillingMode = billingMode,
            BillingModeReason = Normalize(request.BillingModeReason),
            RateCardId = request.RateCardId,
            // Charges accrue from creation unless the sale agreed otherwise: the default that
            // costs the business nothing is the one where billing has already started.
            BillingStartsOn = request.BillingStartsOn ?? now.Date,
            TrialEndsOn = request.TrialEndsOn,
            ContractStartOn = request.ContractStartOn,
            ContractEndOn = request.ContractEndOn,
            PaymentTermsDays = request.PaymentTermsDays,
            PurchaseOrderReference = Normalize(request.PurchaseOrderReference),
            BillingContactName = Normalize(request.BillingContactName),
            // Statements have to reach somebody. Absent a dedicated AP contact, the founding
            // administrator is a far better default than nobody at all.
            BillingContactEmail = Normalize(request.BillingContactEmail) ?? adminEmail,
            BillingAddress = Normalize(request.BillingAddress),
            AccountOwnerEmail = Normalize(request.AccountOwnerEmail) ?? actor
        };

        db.Set<Tenant>().Add(tenant);
        await db.SaveChangesAsync(ct);

        // Recorded on the execution INSIDE this transaction. If the commit fails, the id is
        // forgotten alongside the row it names, and the next attempt starts cleanly.
        execution.TenantId = tenant.Id;
        return Detail(new { tenantId = tenant.Id, slug, billingMode = billingMode.ToString() });
    }

    // ---- 2. the primary business unit ----------------------------------------------------

    private async Task<string?> CreateBusinessUnitAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, request, actor, _) = context;

        if (execution.ProvisionedBusinessUnitId is { } existing)
        {
            var exists = await db.Set<BusinessUnit>().AsNoTracking().AnyAsync(b => b.Id == existing, ct);
            if (exists)
                return Detail(new { businessUnitId = existing, reused = true });

            throw new ProvisioningStepException(
                $"Business unit {existing} was recorded by an earlier attempt but no longer exists.",
                isTerminal: true);
        }

        if (execution.TenantId is not long tenantId)
            throw new ProvisioningStepException(
                "The tenant step has not completed, so there is nothing to attach a business unit to.",
                isTerminal: false);

        var businessUnit = new BusinessUnit
        {
            // Uppercased slug, matching the synchronous path exactly. This value is permanent in
            // a way the slug is not: TenantBaselineSeeder derives the lead reference prefix from
            // it, and LeadPersistenceRules stamps that into every commercial case's immutable
            // MasterReference — printed on quotes the customer keeps.
            BusinessUnitCode = execution.Slug.ToUpperInvariant(),
            BusinessUnitName = execution.Name,
            LegalName = Normalize(request.LegalName),
            CommercialRegistrationNumber = Normalize(request.RegistrationNumber),
            Description = $"Primary business unit for tenant '{execution.Name}'",
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow
        };
        db.Set<BusinessUnit>().Add(businessUnit);
        await db.SaveChangesAsync(ct);

        execution.ProvisionedBusinessUnitId = businessUnit.Id;

        // The tenant learns its business unit here rather than at the end, so a tenant that
        // stalls half-way is still navigable: the AI policy screen, the impersonation path and
        // the entitlement cache all resolve through PrimaryBusinessUnitId, and a null there
        // makes a partially provisioned tenant completely opaque to support.
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId, ct);
        tenant.PrimaryBusinessUnitId = businessUnit.Id;
        tenant.ModifiedBy = actor;
        tenant.ModifiedOn = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Detail(new { businessUnitId = businessUnit.Id, businessUnit.BusinessUnitCode });
    }

    // ---- 3. lifecycle statuses -----------------------------------------------------------

    private async Task<string?> SeedLifecycleStatusesAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, _, actor, _) = context;
        var businessUnitId = RequireBusinessUnit(execution);

        var businessUnit = await db.Set<BusinessUnit>().FirstAsync(b => b.Id == businessUnitId, ct);
        var catalog = LifecycleStatusCatalog.CreateFor(businessUnit, actor);

        // Check-then-insert against what is already there, per (type, code). A previous attempt
        // may have committed some of these and rolled back on the rest, and re-inserting a
        // status the tenant already has would give every dropdown a duplicate entry.
        var existing = await db.SetupMasters.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId)
            .Select(s => new { s.SetupType, s.SetupCode })
            .ToListAsync(ct);
        var present = existing
            .Select(s => $"{s.SetupType} {s.SetupCode}")
            .ToHashSet(StringComparer.Ordinal);

        var missing = catalog
            .Where(status => !present.Contains($"{status.SetupType} {status.SetupCode}"))
            .ToList();

        if (missing.Count > 0)
        {
            db.SetupMasters.AddRange(missing);
            await db.SaveChangesAsync(ct);
        }

        return Detail(new { created = missing.Count, alreadyPresent = catalog.Count - missing.Count });
    }

    // ---- 4. the AI processing policy ------------------------------------------------------

    private async Task<string?> CreateAiPolicyAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, _, actor, _) = context;
        var businessUnitId = RequireBusinessUnit(execution);

        // PostgreSQL creates this row ITSELF: the business_units_create_ai_policy trigger fires
        // AFTER INSERT ON "BusinessUnits" and calls nexora_create_default_ai_policy(). Adding a
        // second one unconditionally violated the primary key, so every provision through the
        // portal failed with a bare "Provisioning failed." — and the SQLite tests never caught
        // it, because SQLite has no such trigger. Checked rather than assumed, so this works on
        // both providers: the trigger owns the row where it exists, and the explicit add covers
        // providers where it does not.
        var policy = await db.AiProcessingPolicies.IgnoreQueryFilters()
            .SingleOrDefaultAsync(p => p.BusinessUnitId == businessUnitId, ct);
        var createdByTrigger = policy is not null;
        if (policy is null)
        {
            policy = AiProcessingPolicy.CreateSecureDefault(businessUnitId, actor, DateTime.UtcNow);
            db.AiProcessingPolicies.Add(policy);
        }

        // The same one-time copy the synchronous path makes. Kept in step with it deliberately:
        // a starting posture that depends on which provisioning path ran is not a starting posture.
        // Read through the tenant rather than the request: by this step the tenant row exists and
        // carries the plan it was actually created with, which is the one thing that cannot have
        // drifted between the request being submitted and this step running.
        var plan = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.PrimaryBusinessUnitId == businessUnitId && t.PlanId != null)
            .Join(db.Set<Plan>().IgnoreQueryFilters().AsNoTracking(),
                t => t.PlanId, x => x.Id, (_, x) => x)
            .FirstOrDefaultAsync(ct);
        AiPackageProvisioning.Apply(
            policy, plan?.AiPackage, plan?.AiMonthlyTokenAllowance,
            plan?.AiAllowanceUnlimited ?? false, actor, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);

        return Detail(new
        {
            businessUnitId, createdByTrigger,
            aiPackage = AiPackages.Resolve(plan?.AiPackage).Key
        });
    }

    // ---- 5. the founding role -------------------------------------------------------------

    private async Task<string?> CreateFoundingRoleAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, _, actor, _) = context;
        var businessUnitId = RequireBusinessUnit(execution);

        // Re-read rather than trusting the recorded id alone: the role is identified by a natural
        // key the tenant's own administrator could in principle have created, and adopting an
        // existing SUPER_ADMIN is correct here in a way that adopting an existing tenant is not.
        var existing = await db.SetupMasters.IgnoreQueryFilters()
            .Where(s => s.BusinessUnitId == businessUnitId
                        && s.SetupType == "Role" && s.SetupCode == "SUPER_ADMIN")
            .Select(s => new { s.SetupId, s.SetupValue })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            execution.FoundingRoleId = existing.SetupId;
            return Detail(new { roleId = existing.SetupId, existing.SetupValue, reused = true });
        }

        // RoleRank.Owner is what PermissionHandler's rank rule reads, so the founding admin has
        // full tenant authority immediately, before any RolePermissions row exists.
        var role = new SetupMaster
        {
            SetupType = "Role",
            SetupCode = "SUPER_ADMIN",
            SetupValue = "Super Administrator",
            Description = "Founding administrator role created at tenant provisioning.",
            BusinessUnitId = businessUnitId,
            RoleRank = RoleRanks.Owner,
            IsActive = true,
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow
        };
        db.SetupMasters.Add(role);
        await db.SaveChangesAsync(ct);

        execution.FoundingRoleId = role.SetupId;
        return Detail(new { roleId = role.SetupId, role.SetupValue, reused = false });
    }

    // ---- 6. the founding administrator ----------------------------------------------------

    private async Task<string?> CreateFoundingAdminAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, request, actor, _) = context;
        var businessUnitId = RequireBusinessUnit(execution);
        if (execution.FoundingRoleId is not long roleId)
            throw new ProvisioningStepException(
                "The founding role step has not completed.", isTerminal: false);

        var email = execution.AdminEmail;

        // Users.Email is GLOBALLY unique — one address, one account, one tenant. A row already
        // carrying this address is either ours from a previous attempt or somebody else's, and
        // the two need completely different answers.
        var existing = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Email == email)
            .Select(u => new { u.Id, u.Buid })
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            if (existing.Buid == businessUnitId)
            {
                execution.FoundingUserId = existing.Id;
                return Detail(new { userId = existing.Id, reused = true });
            }

            throw new ProvisioningStepException(
                $"A user with email '{email}' already exists on another tenant. One address maps to " +
                "one account on one tenant, so this execution cannot complete; cancel it and submit " +
                "again with a different administrator address.",
                isTerminal: true, failureCode: "email-taken");
        }

        var invited = execution.AdminActivation == AdminActivationMethods.Invite;
        var admin = new User
        {
            FirstName = request.AdminFirstName.Trim(),
            LastName = request.AdminLastName.Trim(),
            Email = email,
            // Hashed once, at submit. BCrypt mints a fresh salt per call, so hashing here would
            // give two attempts two different stored hashes — and a customer who had already
            // activated against the first would find their password silently invalidated by a
            // retry that was supposed to be a repair.
            PasswordHash = execution.AdminPasswordHash,
            ImageUrl = string.Empty,
            RoleId = roleId,
            Buid = businessUnitId,
            // An invited administrator is dormant until they redeem their link. That is not
            // ceremony: the account holds no usable credential yet, and an account nobody can
            // sign into must not occupy a billable seat while the invitation is in flight — the
            // seats meter reads exactly this flag.
            IsActive = !invited,
            Timezone = Normalize(request.TimeZoneId),
            CreatedBy = actor,
            CreatedOn = DateTime.UtcNow
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);

        execution.FoundingUserId = admin.Id;
        return Detail(new { userId = admin.Id, admin.IsActive, reused = false });
    }

    // ---- 7. the workspace baseline --------------------------------------------------------

    private async Task<string?> SeedBaselineAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, request, actor, _) = context;
        var businessUnitId = RequireBusinessUnit(execution);

        // The letterhead line the customer's first quote prints. Composed from the parts the
        // operator typed rather than asking for a second, free-text copy of the same address —
        // two addresses that can disagree is how a compliant document becomes a non-compliant one.
        var postalAddress = string.Join(", ", new[]
        {
            Normalize(request.AddressLine1), Normalize(request.AddressLine2), Normalize(request.City),
            Normalize(request.StateProvince), Normalize(request.PostalCode),
            Normalize(request.CountryCode)?.ToUpperInvariant()
        }.Where(part => part is not null));

        try
        {
            var summary = await _baseline.SeedAsync(
                businessUnitId,
                new TenantBaselineProfile(
                    CountryCode: Normalize(request.CountryCode)?.ToUpperInvariant(),
                    BaseCurrencyCode: Normalize(request.BaseCurrencyCode)?.ToUpperInvariant(),
                    CompanyName: Normalize(request.LegalName) ?? execution.Name,
                    CompanyAddress: string.IsNullOrEmpty(postalAddress) ? null : postalAddress,
                    CompanyPhone: Normalize(request.Phone),
                    CompanyEmail: Normalize(request.ContactEmail),
                    LogoUrl: Normalize(request.LogoUrl),
                    Locale: Normalize(request.Locale)),
                actor,
                ct);

            return Detail(new
            {
                summary.BaseCurrencyCode,
                summary.CurrenciesCreated,
                summary.UnitsOfMeasureCreated,
                summary.CountriesCreated,
                summary.DiscountTypesCreated,
                summary.ModulesCreated,
                summary.RolesCreated,
                summary.RolePermissionsCreated,
                summary.QuoteConfigurationCreated,
                summary.LeadReferenceConfigurationCreated,
                summary.LeadReferencePrefix,
                roles = summary.RoleNames
            });
        }
        catch (ArgumentException exception)
        {
            // The seeder refuses to proceed without a base currency, and it is right to: every
            // consequence of a missing one is invisible. Submit validation catches this, so
            // reaching it here means the stored payload is not what validation saw — a state a
            // retry cannot mend.
            throw new ProvisioningStepException(exception.Message, isTerminal: true, failureCode: "baseline-profile");
        }
        catch (InvalidOperationException exception)
        {
            throw new ProvisioningStepException(exception.Message, isTerminal: true, failureCode: "baseline-target");
        }
    }

    // ---- 8. the tenant's data boundaries ---------------------------------------------------

    /// <summary>
    /// Registers this deployment's declared data boundaries against the tenant, and verifies the
    /// one boundary the platform can actually observe: its own PostgreSQL tenant scope.
    ///
    /// <para><b>The defect this closes.</b> A Nexora-hosted tenant could not reach Active until an
    /// operator had typed the platform's own provider reference, region and backup-policy version
    /// into a form and hand-hashed an evidence document about a database Nexora runs itself.
    /// Deletion certification then asked for the same thing nine times, once per boundary type.
    /// None of it was a fact the operator was in a position to know better than the platform.</para>
    ///
    /// <para><b>Why it can fail, and must.</b> The verification is a real probe — the tenant's
    /// primary business unit, the business-unit row it names, the isolation layer actually in force
    /// for tenant-scoped tables, and the region the manifest declares against the region the
    /// contract records. A disagreement fails the step and leaves <c>data.residency-isolation</c>
    /// blocking, which is the correct outcome: a residency control that passed on an unobserved
    /// fact would be worse than the manual form it replaced, because nobody would ever look
    /// again.</para>
    /// </summary>
    private async Task<string?> RegisterDataBoundariesAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (_, execution, _, _, _) = context;

        if (_dataBoundaries is null)
            return Detail(new
            {
                configured = false,
                reason = "No platform data-boundary provisioner is wired up, so boundaries stay on "
                         + "the manual registration path."
            });

        if (execution.TenantId is not long tenantId)
            throw new ProvisioningStepException(
                "The tenant step has not completed, so there are no data boundaries to register.",
                isTerminal: false);

        // Attributed to system:provisioning so no reader can mistake a probe for a person, but
        // carrying the SUBMITTING operator's platform user id, because PlatformAuditService is
        // right to refuse an anonymous privileged write and the human who asked for this tenant is
        // the honest answer to "on whose authority". Without that id there can be no audit record,
        // and an automated registration nobody can see afterwards is exactly what this step must
        // not produce — so it fails instead.
        if (execution.RequestedByPlatformUserId is not long actorId || actorId <= 0)
            throw new ProvisioningStepException(
                "This execution carries no platform actor id, so an automated data-boundary "
                + "registration could not be audited. Register the boundaries from the tenant's "
                + "Data & storage tab instead.",
                isTerminal: true, failureCode: "data-boundary-unauditable");

        var actor = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtRegisteredClaimNames.Sub, actorId.ToString()),
            new Claim("email", PlatformAutomationActors.Provisioning)
        ], "ProvisioningStepExecutor"));

        PlatformDataBoundaryProvisionResult result;
        try
        {
            result = await _dataBoundaries.EnsureAsync(tenantId, actor, ct);
        }
        catch (TenantDataAssetValidationException exception)
        {
            // The manifest disagrees with something the registry will not bend on — most often the
            // tenant's contractual data region. Terminal: no number of retries reconciles a
            // deployment's declared region with a contract that says somewhere else.
            throw new ProvisioningStepException(
                $"The platform data-boundary manifest was refused: {exception.Message}",
                isTerminal: true, failureCode: "data-boundary-manifest");
        }
        catch (TenantDataAssetConflictException exception)
        {
            throw new ProvisioningStepException(
                $"This tenant already carries a different data boundary: {exception.Message}",
                isTerminal: true, failureCode: "data-boundary-conflict");
        }

        if (result.Failure is { Length: > 0 } failure)
            throw new ProvisioningStepException(failure,
                isTerminal: false, failureCode: "data-boundary-probe");

        return Detail(new
        {
            configured = result.Configured,
            primaryScope = result.PrimaryScopeState,
            evidenceReference = result.EvidenceReference,
            evidenceSha256 = result.EvidenceSha256,
            registered = result.RegisteredLogicalKeys,
            alreadyRegistered = result.AlreadyRegisteredLogicalKeys,
            // Named rather than counted: an undeclared boundary type is still a deletion-certification
            // blocker, and the operator needs to know WHICH one to describe.
            undeclaredAssetTypes = result.UndeclaredAssetTypes,
            manifestDefects = result.ManifestDefects,
            actor = PlatformAutomationActors.Provisioning
        });
    }

    // ---- 9. the activation invitation -----------------------------------------------------

    private async Task<string?> IssueInvitationAsync(ProvisioningStepContext context, CancellationToken ct)
    {
        var (db, execution, _, actor, _) = context;

        if (execution.AdminActivation != AdminActivationMethods.Invite)
            throw new ProvisioningStepException(
                "This request uses the password activation path, so there is no invitation to issue.",
                isTerminal: true);

        if (execution.TenantId is not long tenantId || execution.FoundingUserId is not long userId)
            throw new ProvisioningStepException(
                "The administrator has not been created, so there is nobody to invite.", isTerminal: false);

        // THE step that cannot simply be run again. Every call to IssueAsync mints a fresh token
        // and a fresh row, so a plain retry would leave TWO live activation links for one account,
        // in two different states of the mail system, either of which sets the founding
        // administrator's password. That is a second key to the front door, created by a button
        // labelled "retry".
        //
        // The compensation is a supersede-then-issue, in ONE transaction, so there is never an
        // instant at which two links work. It runs unconditionally rather than only on a retry:
        // on a first attempt it revokes nothing and costs one indexed UPDATE, and on any later
        // attempt it is the whole reason the step is safe — including the case nobody thinks of,
        // where an earlier attempt committed the invitation and a LATER step failed.
        //
        // Written out here rather than delegated to ITenantAdminInvitationService.ReissueAsync
        // because that method opens its OWN execution strategy and transaction and clears the
        // change tracker. Called from inside this step's transaction it would throw on the nested
        // BeginTransaction and detach the execution entity whose ids this step is recording.
        var supersededAt = DateTime.UtcNow;
        var supersededBy = actor.Length <= 256 ? actor : actor[..256];
        var superseded = await db.Set<TenantAdminInvitation>()
            .Where(i => i.UserId == userId && i.RedeemedAtUtc == null && i.RevokedAtUtc == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(i => i.RevokedAtUtc, (DateTime?)supersededAt)
                .SetProperty(i => i.RevokedBy, supersededBy)
                .SetProperty(i => i.RevocationReason,
                    "Superseded by a reissued invitation during provisioning retry."), ct);

        var user = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => new { u.FirstName })
            .FirstAsync(ct);

        // Issued INSIDE this step's transaction so a failed step takes the activation link down
        // with it. The EMAIL is sent after the commit — a sent email cannot be rolled back, and a
        // live activation link for a tenant that never existed is worse than no email at all.
        var issued = await _invitations.IssueAsync(db, new TenantAdminInvitationRequest
        {
            TenantId = tenantId,
            UserId = userId,
            Email = execution.AdminEmail,
            RecipientName = user.FirstName,
            TenantName = execution.Name,
            IssuedBy = actor
        }, ct);

        execution.InvitationId = issued.InvitationId;

        // Handed to the runner, which mails it after the commit. It is never persisted and never
        // reaches an API response on the durable path: the submit that could have carried a link
        // returned long before this step ran, and putting one in a status payload the console
        // polls would make a live activation link readable by anyone who can see the execution.
        context.Issued = issued;

        return Detail(new
        {
            invitationId = issued.InvitationId,
            expiresAtUtc = issued.ExpiresAtUtc,
            supersededInvitations = superseded,
            delivery = "email"
        });
    }

    private static long RequireBusinessUnit(ProvisioningExecution execution)
        => execution.ProvisionedBusinessUnitId
           ?? throw new ProvisioningStepException(
               "The business unit step has not completed, so this step has no tenant to write into.",
               isTerminal: false);

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Detail(object value) => JsonSerializer.Serialize(value);
}
