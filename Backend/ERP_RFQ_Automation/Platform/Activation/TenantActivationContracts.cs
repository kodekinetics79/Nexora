namespace ERP_RFQ_Automation.Platform.Activation;

public static class TenantActivationPolicy
{
    public const string Version = "tenant-activation/2026-08-08.v1";
}

public sealed record ActivationControlDecision(
    string Code, bool Satisfied, string Detail, IReadOnlyList<string> EvidenceReferences);

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
    DateTime EvaluatedAtUtc);

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
