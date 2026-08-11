namespace ERP_RFQ_Automation.Platform.Activation;

public static class TenantActivationPolicy
{
    /// <summary>
    /// Bumped from <c>2026-08-08.v1</c> when the deployment profile landed. The controls
    /// themselves and the PRODUCTION verdict are unchanged — what changed is that a decision now
    /// records WHICH profile it was taken under, so a decision read back later cannot be mistaken
    /// for a production one.
    /// </summary>
    public const string Version = "tenant-activation/2026-08-10.v2";
}

/// <param name="Satisfied">
/// Whether the control passes. Unchanged, and deliberately still the first thing on the record:
/// a deferred control is NOT satisfied, and any consumer that reads only this bit continues to
/// read the strict answer.
/// </param>
/// <param name="Disposition">
/// One of <see cref="ActivationControlDispositions"/>. Says what the failure MEANS for this
/// deployment profile; says nothing about whether the control passed.
/// </param>
/// <param name="BlocksProduction">
/// True for every unsatisfied control, in every profile. This is the field that keeps a deferral
/// honest: a control deferred on a laptop still reads as a production blocker on the same screen.
/// </param>
/// <param name="DeferralKey">The <see cref="DeploymentPrerequisiteCatalog"/> entry that explains a deferral.</param>
/// <param name="ProductionRequirement">What production actually needs. Present whenever the control is unsatisfied and catalogued.</param>
public sealed record ActivationControlDecision(
    string Code, bool Satisfied, string Detail, IReadOnlyList<string> EvidenceReferences)
{
    public string Disposition { get; init; } =
        Satisfied ? ActivationControlDispositions.Satisfied : ActivationControlDispositions.Blocking;

    public bool BlocksProduction { get; init; } = !Satisfied;

    public string? DeferralKey { get; init; }

    public string? ProductionRequirement { get; init; }
}

/// <param name="Ready">
/// Whether the tenant may be activated UNDER ITS OWN PROFILE. For a PRODUCTION tenant this is
/// exactly what it always was: every control satisfied. It is never sufficient evidence that a
/// tenant is production-ready — see <paramref name="ProductionReadiness"/>.
/// </param>
/// <param name="BlockingControls">
/// What stops activation now. Identical to "every unsatisfied control" on a PRODUCTION tenant.
/// </param>
/// <param name="ProductionBlockingControls">
/// Every unsatisfied control, regardless of profile. On a PRODUCTION tenant this equals
/// <paramref name="BlockingControls"/>; on a deferring profile it is the honest list.
/// </param>
public sealed record TenantActivationDecision(
    long TenantId,
    bool Ready,
    string CommercialState,
    string AccessState,
    string DataState,
    string LegalHoldState,
    IReadOnlyList<ActivationControlDecision> Controls,
    IReadOnlyList<string> BlockingControls,
    IReadOnlyList<string> Warnings,
    string PolicyVersion,
    DateTime EvaluatedAtUtc)
{
    /// <summary>One of <see cref="Models.TenantDeploymentProfiles"/>.</summary>
    public string DeploymentProfile { get; init; } = Models.TenantDeploymentProfiles.Production;

    /// <summary>One sentence naming what the profile does. Rendered verbatim by the console.</summary>
    public string DeploymentProfileDetail { get; init; } = string.Empty;

    public IReadOnlyList<string> ProductionBlockingControls { get; init; } = [];

    public IReadOnlyList<string> DeferredControls { get; init; } = [];

    public IReadOnlyList<string> ExternallyBlockedControls { get; init; } = [];

    /// <summary>
    /// The strict verdict, evaluated the same way for every profile. A deferring profile can be
    /// <see cref="Ready"/> and still be uncertifiable — that is the entire arrangement.
    /// </summary>
    public ProductionReadinessCertification ProductionReadiness { get; init; } =
        new(false, [], [], "Production readiness has not been evaluated.");
}

/// <param name="Certifiable">
/// True only when every activation control is SATISFIED and every catalogued deployment
/// prerequisite is met. Nothing about a deferral can make this true; a deferral is precisely the
/// statement that it is not.
/// </param>
public sealed record ProductionReadinessCertification(
    bool Certifiable,
    IReadOnlyList<string> BlockingControls,
    IReadOnlyList<DeploymentPrerequisiteStatus> Prerequisites,
    string Detail);

public sealed record DeploymentPrerequisiteStatus(
    string Key,
    string Title,
    string? ControlCode,
    bool Satisfied,
    string Disposition,
    string ProductionRequirement,
    string Detail);

public sealed class TenantActivationBlockedException(TenantActivationDecision decision)
    : Exception("Tenant activation is blocked by authoritative policy.")
{
    public TenantActivationDecision Decision { get; } = decision;
}

public sealed record RecordActivationControlEvidenceRequest(
    string Disposition, string EvidenceReference, string EvidenceSha256,
    DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, string Reason);

public sealed record ActivationControlEvidenceReceipt(
    long TenantId, string ControlCode, string Disposition, string EvidenceReference,
    DateTime EffectiveFromUtc, DateTime? EffectiveToUtc, string PolicyVersion);
