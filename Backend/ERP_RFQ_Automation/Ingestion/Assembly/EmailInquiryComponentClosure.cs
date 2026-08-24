using System;
using ERP_RFQ_Automation.Extraction;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// THE one rule for closing an email-assembly component whose extraction job has stopped
/// trying, and the one place that decides whether a failure HOLDS a message or FINALIZES it.
///
/// <para><b>Why it is shared.</b> Two callers now close a barrier. The live path
/// (<c>ExtractionWorker.CloseAssemblyComponentAsync</c>) closes a component at the instant its
/// job gives up; the stranded-component sweep
/// (<see cref="EmailInquiryAssemblyRecoveryService"/>) closes the components whose jobs died
/// before that path existed. Two copies of this rule would drift, and the direction of the drift
/// decides whether a customer's RFQ is held forever or quoted against a document nobody read —
/// so the rule lives here once and both callers ask it.</para>
///
/// <para><b>The distinction itself.</b> An INFRASTRUCTURE fault (evidence storage unreachable,
/// the object missing, the bucket misconfigured, the scanner down, the model refused by policy)
/// leaves the component <see cref="EmailInquiryComponentStatus.FailedRecoverable"/>: the message
/// is HELD, because the content is presumed readable once the fault is fixed and a lead built
/// without it would be priced against a document that still exists. A CONTENT fault (unsupported
/// format, an unreadable file, a refusal retrying cannot change) is
/// <see cref="EmailInquiryComponentStatus.Skipped"/>: terminal and commercially significant,
/// which finalizes the message into review and says plainly that one part could not be read.
/// Either way the message becomes visible to a human. What it must never do is disappear.</para>
/// </summary>
public static class EmailInquiryComponentClosure
{
    /// <summary>
    /// Whether the failure is about this deployment's plumbing rather than about the document.
    ///
    /// <para>The list is CLOSED and deliberately short. Anything not named here is treated as a
    /// content fault, which is the safe direction: a content fault sends the message to a human,
    /// while an infrastructure hold parks it until an operator notices. Nothing sweeps a held
    /// component back into flight AUTOMATICALLY, so "hold" is still the more expensive mistake of
    /// the two — but it is no longer a permanent one: the audited manual reopen on the Inbound
    /// Mail screen puts a held message back through the pipeline (see
    /// <see cref="EmailInquiryAssemblyStateMachine.CanGovernedTriageReopenTransition"/>), and a
    /// hold that is bound to an exhausted extraction job is recovered by the dead-letter replay
    /// instead.</para>
    /// </summary>
    public static bool IsInfrastructureFailure(string? errorCode) =>
        errorCode is "evidence_missing" or "evidence_bucket_mismatch" or "storage_unavailable"
            or "security_scanner_unavailable" or "ai_not_authorized";

    /// <summary>The terminal status a stopped job's component must take.</summary>
    public static EmailInquiryComponentStatus StatusFor(string? errorCode) =>
        IsInfrastructureFailure(errorCode)
            ? EmailInquiryComponentStatus.FailedRecoverable
            : EmailInquiryComponentStatus.Skipped;

    /// <summary>
    /// Whether a component is still open to being closed by a stopped job.
    ///
    /// <para>A security refusal, a completed extraction, an ignored decoration or an already
    /// recorded hold outranks anything decided here — those verdicts were reached with more
    /// information than a dead queue row carries.</para>
    /// </summary>
    public static bool IsClosable(EmailInquiryComponentStatus status) =>
        status is EmailInquiryComponentStatus.Pending
            or EmailInquiryComponentStatus.Inspecting
            or EmailInquiryComponentStatus.Extracting;

    /// <summary>
    /// Recovers the error code from a dead queue row's free-text <c>LastError</c>.
    ///
    /// <para><b>Why this is needed at all.</b> The live path is handed the error code it just
    /// decided. The sweep is not: it arrives minutes or days later at a job row that stores only
    /// prose, because the queue has no error-code column and the intake trigger flattens every
    /// dead letter to <c>extraction_dead_letter</c>. The failures whose disposition actually
    /// differs all stamp a CLOSED marker into that prose for exactly this reason — the same
    /// markers <c>ExtractionDeadLetterService.ClassifyFailure</c> matches — so the recovery is a
    /// lookup against constants rather than a guess at wording.</para>
    ///
    /// <para>Anything unrecognised returns a content code, so an unfamiliar failure sends its
    /// message to a human instead of parking it in a hold nothing sweeps.</para>
    /// </summary>
    public static string ErrorCodeFromJobError(string? lastError)
    {
        if (string.IsNullOrWhiteSpace(lastError)) return "extraction_failed";

        // Before the evidence rules: a policy refusal's text can mention the document it refused.
        if (lastError.Contains(ChunkedExtractionService.AiNotAuthorizedCode, StringComparison.Ordinal))
            return "ai_not_authorized";
        // Before the object-missing rule, whose prose a bucket mismatch also contains.
        if (lastError.Contains(EvidenceIntegrityException.BucketMismatchMarker, StringComparison.Ordinal))
            return "evidence_bucket_mismatch";
        if (lastError.Contains(EvidenceIntegrityException.ObjectMissingMarker, StringComparison.Ordinal))
            return "evidence_missing";
        if (lastError.Contains(
                Infrastructure.Storage.EvidenceStorageUnavailableException.ErrorCode,
                StringComparison.OrdinalIgnoreCase))
            return "storage_unavailable";
        if (lastError.Contains("security_scanner_unavailable", StringComparison.OrdinalIgnoreCase))
            return "security_scanner_unavailable";

        // A digest mismatch is the document being WRONG rather than the plumbing being down, so
        // it deliberately does NOT map to an infrastructure code — the message finalizes into
        // review rather than waiting for a fault that will never be fixed.
        return "extraction_failed";
    }
}
