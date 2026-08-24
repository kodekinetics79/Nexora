using System;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// The authority to put a message BACK into the pipeline by scheduling a fresh extraction job for
/// a part that never got one — carrying who is doing it and why.
///
/// <para><b>Why a type and not a bool.</b> The transition it authorizes can take a message out of
/// a person's review tray. That is a decision someone owns, and an unattributed reopen is exactly
/// the kind of silent state change this aggregate refuses everywhere else: the dead-letter
/// recovery carries actor, reason and an idempotency key for the same reason. Making the grant a
/// value the caller must construct means a future call site cannot acquire the wider authority by
/// forgetting to pass something.</para>
///
/// <para><b>Where the record goes.</b> The actor and reason are written onto the assembly's
/// operator-visible status reason and into a structured warning log, which is the same standard
/// <c>HoldForReviewAsync</c> and <c>MarkAssembledAsync</c> hold. Nothing here reaches the platform
/// audit log, which is for staff actions against a tenant and needs a ClaimsPrincipal this path
/// does not have.</para>
/// </summary>
/// <param name="ActorId">
/// Who is reopening the message. A person's id, or a NAMED system actor — never blank. A
/// background service is still an actor, and recording "system" is what makes the alternative
/// (recording nothing) obviously wrong.
/// </param>
/// <param name="Reason">Why, in words an operator reading the message would understand.</param>
public sealed record EmailInquirySchedulingGrant(string ActorId, string Reason)
{
    /// <summary>The actor the recovery sweep signs its reopens with.</summary>
    public const string RecoverySweepActor = "automatic recovery";

    /// <summary>
    /// The grant the recovery sweep uses. Named rather than anonymous so the reopen is
    /// attributable to the thing that did it, and so a reader of the message can tell an
    /// automatic rescue apart from a colleague's decision.
    /// </summary>
    public static EmailInquirySchedulingGrant RecoverySweep { get; } = new(
        RecoverySweepActor,
        "a part of this message had never been handed to processing, so it was sent again");

    public string ActorId { get; } = string.IsNullOrWhiteSpace(ActorId)
        ? throw new ArgumentException(
            "A scheduling reopen must record who performed it; a blank actor is how an "
            + "unattributed state change gets in.", nameof(ActorId))
        : ActorId.Trim();

    public string Reason { get; } = string.IsNullOrWhiteSpace(Reason)
        ? throw new ArgumentException(
            "A scheduling reopen must record why it happened.", nameof(Reason))
        : Reason.Trim();

    /// <summary>The sentence written where the operator reads it. Kept short: the Inbound Mail
    /// screen REJECTS rather than truncates anything over 300 characters.</summary>
    public string Describe() => $"Reopened by {ActorId}: {Reason}.";
}
