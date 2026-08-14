using System;
using System.Collections.Generic;
using System.Linq;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// The legal transitions of an <see cref="EmailInquiryAssembly"/>, and the ONE rule that
/// decides whether a message may be turned into commercial Leads.
///
/// <para>Pure and static so the rule is directly unit-testable without a database, a mailbox
/// or a queue. Every previous attempt to express "wait for the whole message" lived inside
/// the extraction worker's control flow, where it could only be tested by running the whole
/// pipeline — which is why it was never actually tested and never actually existed.</para>
/// </summary>
public static class EmailInquiryAssemblyStateMachine
{
    /// <summary>
    /// Transitions permitted from each state.
    ///
    /// <para><see cref="EmailInquiryAssemblyStatus.FailedRecoverable"/> is reachable from every
    /// non-terminal state and can return to <see cref="EmailInquiryAssemblyStatus.Inspecting"/>
    /// or <see cref="EmailInquiryAssemblyStatus.Extracting"/> — that IS the recovery path, and
    /// it deliberately re-enters the same assembly rather than starting a new one.</para>
    ///
    /// <para><see cref="EmailInquiryAssemblyStatus.RejectedSecurity"/> is absorbing. A message
    /// that was refused on security grounds must not be walked back into processing by a retry
    /// or a replay; a human reverses it by an explicit act, which creates its own record.</para>
    /// </summary>
    private static readonly IReadOnlyDictionary<EmailInquiryAssemblyStatus, EmailInquiryAssemblyStatus[]> Allowed =
        new Dictionary<EmailInquiryAssemblyStatus, EmailInquiryAssemblyStatus[]>
        {
            [EmailInquiryAssemblyStatus.Captured] = new[]
            {
                EmailInquiryAssemblyStatus.Inspecting,
                // Straight to Extracting is legal, and it is the ordinary path rather than a
                // shortcut. Security inspection is performed SYNCHRONOUSLY while a component is
                // being scheduled (DocumentIngestionService writes to quarantine, scans, and
                // promotes to cleared before the job exists), so by the time any component is
                // extracting, inspection has already happened — there is simply no window in
                // which the message sits in a distinct Inspecting state. Omitting this stranded
                // every real message at Captured: scheduling could not advance it, so the
                // barrier's later verdict was always an illegal transition and was discarded.
                EmailInquiryAssemblyStatus.Extracting,
                // A message that carried nothing to capture is NOT ready — it has no inquiry.
                // ReadyForAssembly is still deliberately absent from this row: reaching it
                // requires a component to have completed, which means the message must have
                // passed through Extracting first. That invariant is what stops an empty
                // message being assembled, and it is preserved.
                EmailInquiryAssemblyStatus.NoInquiry,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.Inspecting] = new[]
            {
                EmailInquiryAssemblyStatus.Extracting,
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                // A part went unread, or every part was refused: a human decides.
                EmailInquiryAssemblyStatus.NeedsReview,
                EmailInquiryAssemblyStatus.NoInquiry,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.Extracting] = new[]
            {
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.NeedsReview,
                EmailInquiryAssemblyStatus.NoInquiry,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.ReadyForAssembly] = new[]
            {
                EmailInquiryAssemblyStatus.Assembled,
                EmailInquiryAssemblyStatus.NeedsReview,
                // The merge can find that what was captured carries no commercial content
                // after all — a delivery receipt whose body extracted cleanly and said nothing.
                EmailInquiryAssemblyStatus.NoInquiry,
                // Assembly itself can hit an infrastructure fault.
                EmailInquiryAssemblyStatus.FailedRecoverable
            },
            // A reviewer can pull an assembled message back for attention. It can NOT re-enter
            // extraction: an amendment is a different email, with its own Message-Id, its own
            // EmailIngest and its own assembly, reconciled onto the same commercial Lead by
            // ILeadIdentityApplicationService. Re-opening this assembly instead would mutate
            // the durable record of what message #1 contained, and the revision history would
            // then describe a message that never arrived in that form.
            [EmailInquiryAssemblyStatus.Assembled] = new[]
            {
                EmailInquiryAssemblyStatus.NeedsReview
            },
            [EmailInquiryAssemblyStatus.NeedsReview] = new[]
            {
                EmailInquiryAssemblyStatus.Assembled,
                // The reviewer's verdict can be "this was never an inquiry".
                EmailInquiryAssemblyStatus.NoInquiry
            },
            // Recovery re-enters the PIPELINE, never the finish line. ReadyForAssembly is
            // deliberately absent: a held message has components that never reached a terminal
            // state, and letting recovery declare it ready would walk straight past the barrier
            // this whole aggregate exists to impose. Recovery re-inspects and re-extracts, and
            // the barrier decides — again — whether the message is ready.
            [EmailInquiryAssemblyStatus.FailedRecoverable] = new[]
            {
                EmailInquiryAssemblyStatus.Inspecting,
                EmailInquiryAssemblyStatus.Extracting,
                EmailInquiryAssemblyStatus.NeedsReview,
                EmailInquiryAssemblyStatus.NoInquiry,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            // Both terminal and absorbing. Re-opening either would need explicit, audited,
            // idempotent reprocessing semantics — a recorded actor, a reason, and an
            // idempotency key — and none of that exists yet. Until it does, the honest
            // behaviour is to refuse rather than to allow a silent, unattributed reopen.
            [EmailInquiryAssemblyStatus.NoInquiry] = Array.Empty<EmailInquiryAssemblyStatus>(),
            [EmailInquiryAssemblyStatus.RejectedSecurity] = Array.Empty<EmailInquiryAssemblyStatus>()
        };

    public static bool CanTransition(EmailInquiryAssemblyStatus from, EmailInquiryAssemblyStatus to)
        => from == to || (Allowed.TryGetValue(from, out var next) && next.Contains(to));

    /// <summary>
    /// Throws rather than silently correcting. A transition the model does not permit is a
    /// defect in the caller, and swallowing it is how a message ends up Assembled with an
    /// attachment still in flight.
    /// </summary>
    public static void EnsureTransition(EmailInquiryAssemblyStatus from, EmailInquiryAssemblyStatus to)
    {
        if (!CanTransition(from, to))
            throw new InvalidOperationException(
                $"An email inquiry assembly cannot move from {from} to {to}.");
    }

    /// <summary>
    /// THE commercial gate.
    ///
    /// <para>Given what every expected component of a message currently is, decide what the
    /// message as a whole is. This is the rule that makes "one email becomes one coherent
    /// Lead" true, and it is expressed once, here, rather than inferred at each call site.</para>
    /// </summary>
    /// <param name="expectedComponentCount">
    /// Decided once at capture from the parsed MIME tree. Passed explicitly rather than taken
    /// from <paramref name="componentStatuses"/>.Count so that a component row which failed to
    /// be written at all is a HOLD, not an invisible reduction in what we wait for.
    /// </param>
    public static EmailInquiryAssemblyEvaluation Evaluate(
        int expectedComponentCount,
        IReadOnlyCollection<EmailInquiryComponentStatus> componentStatuses)
    {
        ArgumentNullException.ThrowIfNull(componentStatuses);

        // Security refusal outranks everything, including a concurrent infrastructure fault.
        // A message carrying malware is not "retry when storage returns".
        if (componentStatuses.Contains(EmailInquiryComponentStatus.RefusedSecurity))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.RejectedSecurity,
                CompletedCount(componentStatuses), CapturedCount(componentStatuses),
                "A component of this message was refused by the malware scanner.");

        // A missing component row counts as outstanding. The barrier must not be satisfiable
        // by failing to record something.
        var missing = expectedComponentCount - componentStatuses.Count;
        if (missing > 0)
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.FailedRecoverable,
                CompletedCount(componentStatuses), CapturedCount(componentStatuses),
                $"{missing} expected part(s) of this message have no record yet; it is held for replay.");

        // An infrastructure hold anywhere holds the WHOLE message. This is the rule that stops
        // a body-only Lead being created when the attachment carrying the priced lines could
        // not be stored or scanned.
        if (componentStatuses.Contains(EmailInquiryComponentStatus.FailedRecoverable))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.FailedRecoverable,
                CompletedCount(componentStatuses), CapturedCount(componentStatuses),
                "Part of this message could not be processed because a required service was "
                + "unavailable. It is held and will resume without re-reading the mailbox.");

        if (componentStatuses.Any(s => s is EmailInquiryComponentStatus.Inspecting))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.Inspecting, CompletedCount(componentStatuses),
                CapturedCount(componentStatuses), null);

        if (componentStatuses.Any(s => s is EmailInquiryComponentStatus.Pending
                or EmailInquiryComponentStatus.Extracting))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.Extracting, CompletedCount(componentStatuses),
                CapturedCount(componentStatuses), null);

        // Everything expected is terminal. What the message IS now depends on what was actually
        // captured AND on whether anything the sender attached went unread.
        var captured = CapturedCount(componentStatuses);
        var unread = UnreadCount(componentStatuses);

        // (1) A part the sender attached could not be processed. THE MESSAGE GOES TO A HUMAN,
        // even when the body extracted perfectly and even when other attachments succeeded.
        //
        // This outranks capture deliberately. "Please see the attached quotation" plus an
        // attachment nobody could read is not a body-only inquiry — it is an inquiry whose
        // commercial content is missing, and a clean Lead built from the covering note alone is
        // a price quoted against a document that was never opened. An earlier version of this
        // method returned ReadyForAssembly here on the grounds that the unread part was
        // probably a company logo. Sometimes it is. Sometimes it is the bill of quantities, and
        // the two are indistinguishable at this layer — which is exactly why the only parts
        // allowed to be invisible here are the ones a deterministic classifier has already
        // proven to be non-commercial (see EmailInquiryComponentStatus.Ignored).
        if (unread > 0)
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.NeedsReview,
                CompletedCount(componentStatuses), captured,
                captured > 0
                    ? $"{unread} attached part(s) of this message could not be read, so the "
                      + "inquiry may be incomplete. The original is retained for review."
                    : "No part of this message could be read. Its attachments were refused or "
                      + "are unsupported; the original is retained for review.");

        // (2) Everything the sender attached was either read or provably non-commercial. Only
        // now may the message become commercial fact.
        if (captured > 0)
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                CompletedCount(componentStatuses), captured, null);

        // (3) Nothing to capture and nothing unread: no fresh body, and no parts beyond inline
        // assets. A terminal triage outcome, NOT an assemblable inquiry.
        return new EmailInquiryAssemblyEvaluation(
            EmailInquiryAssemblyStatus.NoInquiry, CompletedCount(componentStatuses), 0,
            "This message carried no body text and no readable attachments.");
    }

    /// <summary>
    /// Components that reached ANY terminal state. This is the barrier's progress counter and
    /// what <c>EmailInquiryAssembly.CompletedComponentCount</c> stores — "how many are we still
    /// waiting for", not "how much did we get".
    /// </summary>
    private static int CompletedCount(IReadOnlyCollection<EmailInquiryComponentStatus> statuses)
        => statuses.Count(s => s is EmailInquiryComponentStatus.Completed
            or EmailInquiryComponentStatus.Skipped
            or EmailInquiryComponentStatus.RefusedSecurity
            or EmailInquiryComponentStatus.Ignored
            or EmailInquiryComponentStatus.StructuralOnly);

    /// <summary>
    /// Parts the sender attached that this system did NOT read and cannot prove were
    /// non-commercial. One is enough to send the message to a human.
    ///
    /// <para><see cref="EmailInquiryComponentStatus.Ignored"/> is excluded and is the only
    /// exclusion: those parts were classified deterministically as inline assets, not merely
    /// judged unimportant.</para>
    /// </summary>
    private static int UnreadCount(IReadOnlyCollection<EmailInquiryComponentStatus> statuses)
        => statuses.Count(s => s == EmailInquiryComponentStatus.Skipped);

    /// <summary>
    /// Components that durably captured commercial content. Only <c>Completed</c> counts:
    /// Skipped and RefusedSecurity are terminal but carry nothing. This is the number that
    /// decides whether a message is an inquiry at all, and keeping it separate from
    /// <see cref="CompletedCount"/> is the whole correction — the two were conflated, so
    /// "everything finished" read as "we have something".
    /// </summary>
    private static int CapturedCount(IReadOnlyCollection<EmailInquiryComponentStatus> statuses)
        => statuses.Count(s => s == EmailInquiryComponentStatus.Completed);
}

/// <param name="Status">What the message as a whole now is.</param>
/// <param name="CompletedComponentCount">
/// How many expected components reached ANY terminal state — the barrier's progress counter.
/// </param>
/// <param name="CapturedComponentCount">
/// How many components durably captured commercial content. Deliberately separate from
/// <paramref name="CompletedComponentCount"/>: conflating the two is what let "everything
/// finished" read as "we have something", and an empty message reach an empty Lead.
/// </param>
/// <param name="Reason">
/// Operator-readable explanation, or null when the state needs none. Never carries a bucket
/// name, endpoint, credential or provider exception type.
/// </param>
public readonly record struct EmailInquiryAssemblyEvaluation(
    EmailInquiryAssemblyStatus Status,
    int CompletedComponentCount,
    int CapturedComponentCount,
    string? Reason);
