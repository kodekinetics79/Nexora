using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// How far through OFFBOARDING a tenant is — the second half of the lifecycle, which begins
/// where <see cref="TenantStatus"/> ends.
///
/// <para><b>Why this is a separate axis and not four more <see cref="TenantStatus"/> values.</b>
/// <c>TenantStatus</c> answers one question and is read as the answer to that question all over
/// the product: <i>may this tenant be served right now?</i> <c>TenantAccessSnapshot.IsAccessDenied</c>
/// is <c>Status is Suspended or Archived</c>; <c>ExtractionQueue</c>'s <c>blocked_tenants</c> CTE is
/// <c>"Status" IN ('Suspended','Archived')</c>; <c>BillingRunWorker</c> is <c>Status != Archived</c>.
/// Adding <c>PendingDeletion</c> to that enum would have made every one of those tests
/// <c>false</c> for a tenant scheduled for destruction — a customer on their way out would have
/// regained API access, had their queued extraction jobs claimed and been invoiced again, and
/// nothing in the type system would have said a word. The columns are also GRANTed separately
/// (<c>GRANT SELECT ("Id","PrimaryBusinessUnitId","Status","PlanId")</c>), so the tenant plane
/// reads <c>Status</c> and must keep reading exactly what it reads today.</para>
///
/// <para>So a tenant stays <see cref="TenantStatus.Archived"/> for the whole of offboarding, and
/// this enum records how far the destruction has got. Access denial is therefore true by
/// construction from the moment offboarding starts until the tenant no longer exists, and every
/// existing suspension check keeps working with no change.</para>
/// </summary>
public enum TenantOffboardingStage
{
    /// <summary>
    /// No deletion is scheduled. The default, and the state an offboarding record returns to when
    /// a scheduled deletion is cancelled — the tenant is simply archived again, exactly as before.
    /// </summary>
    NotScheduled = 0,

    /// <summary>
    /// Deletion has been decided and the retention clock is running.
    /// <see cref="TenantOffboarding.PurgeEligibleOn"/> is the earliest instant a purge may execute.
    /// Reversible: the whole point of the window is that somebody can still stop it.
    /// </summary>
    PendingDeletion = 1,

    /// <summary>
    /// The tenant's business records have been destroyed. Terminal — there is no transition out,
    /// because there is nothing left to transition. The <c>platform."Tenants"</c> row survives as a
    /// tombstone and the lifecycle history survives in full; see <see cref="TenantOffboardingDisclosure"/>.
    /// </summary>
    Purged = 2
}

/// <summary>
/// The transition graph, stated once so the service, the API and the console cannot disagree
/// about what is legal.
///
/// <para><b>The whole lifecycle, both axes.</b>
/// <code>
///   Provisioning -> Active &lt;-&gt; Suspended &lt;-&gt; Archived        (TenantStatus, TenantsController)
///                                                |
///                                                |  schedule-deletion (reason, retention days)
///                                                v
///                                         PendingDeletion  --cancel(reason)--> NotScheduled
///                                                |
///                                                |  retention window elapsed
///                                                |  + typed confirmation + reason
///                                                v
///                                             Purged   (terminal)
///
///   erase-personal-data:  an INDEPENDENT operation available at any stage before Purged,
///                         on a tenant that is Suspended or Archived. It is not a stage.
/// </code></para>
///
/// <para><b>Why erasure is not a stage.</b> A stage is a position on a single path; erasure is not
/// on that path. A customer can demand erasure of personal data while their invoices, orders and
/// delivery notes must be kept for another seven years under tax law — so "erased" and
/// "pending deletion" must be able to be true at the same time, and "purged" must be reachable
/// without erasure ever having been asked for. Modelling erasure as a stage would have made those
/// two obligations mutually exclusive, which is the one thing the law does not allow.</para>
/// </summary>
public static class TenantLifecycleGraph
{
    /// <summary>
    /// The tenant statuses from which offboarding may begin. Archived only: archiving is already
    /// the "this customer has left" decision, it already denies access, and it is already reached
    /// through Suspended — so scheduling deletion cannot be the first anybody hears of it.
    /// </summary>
    public const TenantStatus DeletionRequiresStatus = TenantStatus.Archived;

    /// <summary>
    /// Erasure requires the tenant to be off the product. Erasing a live tenant's people would
    /// break the running business it is still paying for: every user is deactivated and their
    /// login address replaced, so the next sign-in fails for a customer nobody warned.
    /// </summary>
    public static bool ErasureAllowedFrom(TenantStatus status) =>
        status is TenantStatus.Suspended or TenantStatus.Archived;

    public static bool CanScheduleDeletion(TenantOffboardingStage stage) =>
        stage == TenantOffboardingStage.NotScheduled;

    public static bool CanCancelDeletion(TenantOffboardingStage stage) =>
        stage == TenantOffboardingStage.PendingDeletion;

    /// <summary>
    /// Purge is reachable only from <see cref="TenantOffboardingStage.PendingDeletion"/> — never
    /// directly from Archived. There is deliberately no "delete now" path: the retention window is
    /// the control, and a control that can be skipped by choosing a different endpoint is not one.
    /// </summary>
    public static bool CanPurge(TenantOffboardingStage stage) =>
        stage == TenantOffboardingStage.PendingDeletion;

    public static bool CanErase(TenantOffboardingStage stage) =>
        stage != TenantOffboardingStage.Purged;
}

/// <summary>
/// Audit verbs and lifecycle-event actions. Constants rather than literals because they are the
/// join key between <c>platform."PlatformAuditLogs"</c>, <c>platform."TenantLifecycleEvents"</c>
/// and whatever the console filters on; a typo in one of the three is invisible until somebody
/// tries to reconstruct an offboarding and finds half of it.
/// </summary>
public static class TenantLifecycleActions
{
    public const string Export = "tenant.offboarding.export";
    public const string ScheduleDeletion = "tenant.offboarding.schedule-deletion";
    public const string CancelDeletion = "tenant.offboarding.cancel-deletion";

    /// <summary>
    /// Written and COMMITTED before a single row is destroyed. See
    /// <see cref="TenantOffboardingService"/> for why intent is recorded first.
    /// </summary>
    public const string PurgeStarted = "tenant.offboarding.purge.started";

    public const string Purged = "tenant.offboarding.purge.completed";
    public const string PurgeFailed = "tenant.offboarding.purge.failed";
    public const string ErasePersonalData = "tenant.offboarding.erase-personal-data";
}

/// <summary>
/// The sentences the API returns with every irreversible operation, kept next to the state
/// machine so a surface cannot describe an offboarding in terms the implementation does not
/// honour. Modelled on <c>EvidenceRetentionDisclosure</c>, which exists for the same reason.
/// </summary>
public static class TenantOffboardingDisclosure
{
    public const string Irreversible =
        "A purge destroys this tenant's business records permanently. There is no undo, no "
        + "recycle bin and no backup this platform can restore from — the retention window before "
        + "the purge is the only opportunity to change the decision.";

    /// <summary>
    /// The distinction the whole module exists to make. Repeated on every erasure response
    /// because "we deleted everything" is the answer people assume they got.
    /// </summary>
    public const string ErasureIsNotDeletion =
        "Erasing personal data is not deleting the tenant. Erasure replaces the identities of the "
        + "natural persons this platform holds for the tenant — its users, its invitation "
        + "recipients and its named contact details — and leaves every commercial record intact, "
        + "because invoices, orders and delivery documents carry statutory retention that a "
        + "customer cannot waive. A tenant can be erased and still be billed; a tenant can be "
        + "purged having never been erased.";

    public const string DeletionIsNotErasure =
        "Deleting the tenant is not an erasure certificate. A purge destroys the tenant's own "
        + "rows; it deliberately does not touch the platform audit trail, the tenant lifecycle "
        + "history or the billing statements those records support. Run the erasure operation if "
        + "what was asked for is the removal of personal data.";

    /// <summary>What a purge deliberately leaves standing. The list is the answer to
    /// "so what is left of us afterwards", which every offboarding customer asks.</summary>
    public const string Survives =
        "What survives a purge: the platform audit trail, the tenant's lifecycle history "
        + "(who decided, when, and why, at every transition), the export receipts proving what "
        + "was handed back, the billing statements and their lines, and a tombstone "
        + "platform.\"Tenants\" row so none of the above points at nothing.";
}
