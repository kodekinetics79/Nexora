using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Configuration;

public interface ITenantConfigurationService
{
    Task<TenantConfigurationView?> ReadAsync(long tenantId, ClaimsPrincipal actor, CancellationToken ct = default);
}

/// <summary>
/// Composes the one read the redesigned console needs. It writes nothing and decides nothing:
/// every fact here is already served by an existing endpoint, and this class exists because
/// scattering those facts across eleven reads is what made a twelve-tab screen feel inevitable.
/// </summary>
public sealed class TenantConfigurationService(
    ErpRfqAutomationContext db,
    ITenantActivationPolicyService? activation = null) : ITenantConfigurationService
{
    public async Task<TenantConfigurationView?> ReadAsync(
        long tenantId, ClaimsPrincipal actor, CancellationToken ct = default)
    {
        var role = actor.FindFirst(PlatformAuthConstants.PlatformRoleClaim)?.Value;
        var actorIsOwner = role == nameof(PlatformRole.Owner);

        var tenant = await db.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Plan)
            .SingleOrDefaultAsync(x => x.Id == tenantId, ct);
        if (tenant is null) return null;

        // Offboarding and legal hold live on the other lifecycle axis. Read here rather than in a
        // second console request so a header and a deletion screen can never disagree about the
        // retention clock — the defect that shipped when they were two fetches.
        var offboarding = await db.Set<TenantOffboarding>().IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        // OWNER ONLY. TenantLegalHoldsController is class-level [Authorize(Owner)] and the
        // offboarding status DTO carries no hold field at all — so whether a customer is under
        // legal hold is Owner-privileged information everywhere else in this control plane.
        // Surfacing it from an endpoint gated only on PlatformScope would have handed it to
        // ReadOnlyOps, the DEFAULT role for a new platform user, along with a sentence explaining
        // that deletion is frozen. An active hold is a litigation or regulatory signal about a
        // named customer; it is not lifecycle trivia.
        var holdActive = actorIsOwner
            && await db.Set<TenantLegalHold>().IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(x => x.TenantId == tenantId && x.ReleasedOn == null, ct);

        // OPTIONAL, and a null one degrades to "no blockers known" rather than "no blockers".
        // An unwired evaluator must never be able to report a customer as clear.
        TenantActivationDecision? decision = null;
        if (activation is not null)
            decision = await activation.EvaluateAsync(tenantId, ct);

        var canAdministerTenants = actorIsOwner || role == nameof(PlatformRole.SupportAdmin);
        var canAdministerBilling = actorIsOwner || role == nameof(PlatformRole.BillingAdmin);

        var state = new TenantConfigurationState(
            tenant.Status.ToString(),
            tenant.StatusReason,
            tenant.BillingMode.ToString(),
            tenant.DeploymentProfile.ToString(),
            tenant.Plan?.Code?.ToLowerInvariant(),
            offboarding?.Stage.ToString() ?? nameof(TenantOffboardingStage.NotScheduled),
            holdActive,
            tenant.TrialEndsOn,
            tenant.ContractEndOn);

        var slices = BuildSlices(tenant, canAdministerTenants, canAdministerBilling, actorIsOwner);
        var blockers = BuildBlockers(decision);

        return new TenantConfigurationView(
            tenant.Id, tenant.Version, state, slices, decision, blockers,
            NextActionFor(tenant, decision, blockers, holdActive));
    }

    // ------------------------------------------------------------------ slices

    private static IReadOnlyList<TenantConfigurationSlice> BuildSlices(
        Tenant t, bool canAdministerTenants, bool canAdministerBilling, bool isOwner) =>
    [
        new("identity", "Company", canAdministerTenants, "Owner or SupportAdmin",
            $"PUT /api/platform/tenants/{t.Id}/profile",
            [
                new("legalName", "Legal name", t.LegalName),
                new("registrationNumber", "Registration number", t.RegistrationNumber),
                new("taxNumber", "Tax number", t.TaxNumber),
                new("countryCode", "Country", t.CountryCode),
                new("contactEmail", "Company email", t.ContactEmail),
                new("phone", "Phone", t.Phone),
                new("addressLine1", "Address", t.AddressLine1),
                new("city", "City", t.City),
                new("postalCode", "Postal code", t.PostalCode),
                new("industry", "Industry", t.Industry),
                new("website", "Website", t.Website)
            ]),

        // Every field here is DERIVED. They are the ones an operator was asked to type and could
        // not correctly answer — currency and locale follow from the country, the region follows
        // from where the deployment's database actually is — and each is the source of a whole
        // class of typo that blocked activation from a different screen than the one that caused it.
        // dataRegion is written by the OWNER-GATED data-region endpoint, not by the profile PUT.
        // Naming the profile endpoint here was the drift this field was added to prevent.
        new("operating", "Operating defaults", false, "Derived — not keyboard input",
            $"PUT /api/platform/tenants/{t.Id}/data-region",
            [
                new("baseCurrencyCode", "Currency", t.BaseCurrencyCode, true,
                    "Fixed at provisioning from the customer's country; changing it would restate every price already quoted."),
                new("timeZoneId", "Time zone", t.TimeZoneId, true, "From the customer's country."),
                new("locale", "Language", t.Locale, true, "From the customer's country."),
                new("dataRegion", "Data region", t.DataRegion, true,
                    "From the registered data boundary of this deployment's database, not typed per customer.")
            ]),

        new("commercial", "Contract and billing", canAdministerBilling, "Owner or BillingAdmin",
            $"PUT /api/platform/billing/tenants/{t.Id}/account-contact",
            [
                new("planCode", "Plan", t.Plan?.Code),
                new("billingMode", "Billing mode", t.BillingMode.ToString()),
                new("billingModeReason", "Why not charged", t.BillingModeReason),
                new("contractStartOn", "Contract starts", Date(t.ContractStartOn)),
                new("contractEndOn", "Renews", Date(t.ContractEndOn)),
                new("trialEndsOn", "Trial ends", Date(t.TrialEndsOn)),
                new("billingStartsOn", "Billing starts", Date(t.BillingStartsOn)),
                new("paymentTermsDays", "Payment terms", Days(t.PaymentTermsDays)),
                new("purchaseOrderReference", "PO reference", t.PurchaseOrderReference),
                new("billingContactName", "Invoices to", t.BillingContactName),
                new("billingContactEmail", "Invoice email", t.BillingContactEmail),
                new("accountOwnerEmail", "Our account owner", t.AccountOwnerEmail)
            ]),

        new("modules", "What they can use", canAdministerBilling, "Owner or BillingAdmin",
            $"PUT /api/platform/tenants/{t.Id}/modules",
            [
                new("seats", "Seats", Count(t.Plan?.MaxSeats), true, PlanSource(t)),
                new("documentsPerMonth", "Documents a month", Count(t.Plan?.MaxDocsPerMonth), true, PlanSource(t)),
                new("concurrentExtractions", "Concurrent extractions",
                    Count(t.Plan?.MaxConcurrentExtractionJobs), true, PlanSource(t))
            ]),

        new("deployment", "Deployment", isOwner, "Owner",
            $"PUT /api/platform/tenants/{t.Id}/deployment-profile",
            [
                new("deploymentProfile", "Workspace purpose", t.DeploymentProfile.ToString()),
                new("deploymentProfileReason", "Why relaxed", t.DeploymentProfileReason),
                new("deploymentProfileApprovedBy", "Approved by", t.DeploymentProfileApprovedBy)
            ])
    ];

    private static string PlanSource(Tenant t) =>
        t.Plan is null
            ? "No plan yet — the newly provisioned allowance applies for the first fourteen days."
            : $"From the {t.Plan.Name} plan.";

    private static string? Date(DateTime? value) => value?.ToString("yyyy-MM-dd");
    private static string? Days(int? value) => value is null ? null : $"{value} days";
    private static string? Count(int? value) => value is null ? "Unlimited" : value.Value.ToString();

    // ------------------------------------------------------------------ blockers

    /// <summary>
    /// Turns the activation policy's control codes into something a salesperson can act on.
    ///
    /// <para>The catalogue already carries a human title, a remedy sentence and the authority that
    /// may apply it. What it does not carry is WHO TO GO AND ASK, and that is the question a rep
    /// actually has: "requires OwnerMfa" tells them nothing, "Finance" tells them everything. The
    /// mapping is one-way and display-only — nothing here can change a verdict.</para>
    /// </summary>
    private static IReadOnlyList<TenantConfigurationBlocker> BuildBlockers(TenantActivationDecision? decision)
    {
        if (decision is null) return [];

        return decision.Controls
            .Where(c => !c.Satisfied && c.Disposition != ActivationControlDispositions.Satisfied)
            .Select(c => new TenantConfigurationBlocker(
                c.Code,
                c.Title,
                c.Remediation?.Hint ?? c.Detail,
                OwnerFor(c),
                SliceFor(c.Remediation?.Surface)))
            .ToList();
    }

    private static string OwnerFor(ActivationControlDecision control) =>
        control.Remediation?.RequiredAuthority switch
        {
            ActivationRemediationAuthorities.Billing => "Finance",
            ActivationRemediationAuthorities.TenantAdmin => "Sales or Support",
            ActivationRemediationAuthorities.OwnerMfa => "Owner",
            ActivationRemediationAuthorities.Owner => "Owner",
            // No remedy in the catalogue means nobody in this console can clear it — the control
            // is waiting on the customer or on an engineer. Saying so is better than an empty cell.
            _ => control.Remediation is null ? "Waiting on the customer or engineering" : "Owner"
        };

    private static string? SliceFor(string? surface) => surface switch
    {
        ActivationRemediationSurfaces.TenantProfileAccess => "identity",
        ActivationRemediationSurfaces.TenantCommercial => "commercial",
        ActivationRemediationSurfaces.TenantModules => "modules",
        ActivationRemediationSurfaces.TenantDataStorage => "operating",
        _ => null
    };

    // ------------------------------------------------------------------ next action

    /// <summary>
    /// The one sentence a list row and a page header can both show, so nobody has to open five
    /// tabs to discover that a customer has been sitting unusable since Tuesday.
    /// </summary>
    private static TenantNextAction? NextActionFor(
        Tenant tenant, TenantActivationDecision? decision,
        IReadOnlyList<TenantConfigurationBlocker> blockers, bool holdActive)
    {
        // holdActive is already false for a non-Owner (see ReadAsync), so this sentence — which
        // states that a named customer is under legal hold — can only reach an Owner.
        if (holdActive)
            return new("Under legal hold", "Deletion and erasure are refused until the hold is released.", null);

        if (tenant.Status == TenantStatus.Provisioning)
        {
            if (decision is null)
                return new("Readiness unknown", "The activation policy could not be read, so this customer is not clear to go live.", null);
            if (blockers.Count == 0)
                return new("Ready to go live", "Every activation control passes. An Owner can switch this customer on.", null);

            var first = blockers[0];
            var others = blockers.Count - 1;
            var detail = others > 0
                ? $"{first.Title} — {first.Owner} has to act. {others} other item{(others == 1 ? "" : "s")} also outstanding."
                : $"{first.Title} — {first.Owner} has to act.";
            return new($"Blocked on {blockers.Count} item{(blockers.Count == 1 ? "" : "s")}", detail, first.ResolveSlice);
        }

        if (tenant.Status == TenantStatus.PastDue)
            return new("Payment overdue", "Chase the invoice before access is suspended.", "commercial");

        if (tenant.Status == TenantStatus.Suspended)
            return new("Suspended", tenant.StatusReason ?? "Access is switched off for this customer.", null);

        if (tenant.BillingMode == TenantBillingMode.Trial && tenant.TrialEndsOn is DateTime ends)
        {
            var daysLeft = (ends.Date - DateTime.UtcNow.Date).TotalDays;
            if (daysLeft < 0) return new("Trial expired", $"The trial ended on {ends:yyyy-MM-dd} and the customer is still not charged.", "commercial");
            if (daysLeft <= 14) return new("Trial ending", $"The trial ends on {ends:yyyy-MM-dd}. Convert it or extend it.", "commercial");
        }

        if (tenant.Status == TenantStatus.Active && blockers.Count > 0)
            return new($"{blockers.Count} setting{(blockers.Count == 1 ? "" : "s")} adrift",
                "This customer is live but would not pass its own activation bar today.", blockers[0].ResolveSlice);

        return null;
    }
}
