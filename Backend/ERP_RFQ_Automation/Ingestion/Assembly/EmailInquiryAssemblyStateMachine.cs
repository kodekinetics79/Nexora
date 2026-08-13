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
                // A message with nothing enqueuable (no fresh body, no supported attachment)
                // has zero expected components and is ready the moment it is captured.
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.Inspecting] = new[]
            {
                EmailInquiryAssemblyStatus.Extracting,
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.Extracting] = new[]
            {
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.ReadyForAssembly] = new[]
            {
                EmailInquiryAssemblyStatus.Assembled,
                EmailInquiryAssemblyStatus.NeedsReview,
                // Assembly itself can hit an infrastructure fault.
                EmailInquiryAssemblyStatus.FailedRecoverable
            },
            [EmailInquiryAssemblyStatus.Assembled] = new[]
            {
                // A reviewer can pull an assembled message back for attention; an amendment
                // arriving later re-opens extraction through the SAME assembly.
                EmailInquiryAssemblyStatus.NeedsReview,
                EmailInquiryAssemblyStatus.Extracting
            },
            [EmailInquiryAssemblyStatus.NeedsReview] = new[]
            {
                EmailInquiryAssemblyStatus.Assembled,
                EmailInquiryAssemblyStatus.Extracting
            },
            [EmailInquiryAssemblyStatus.FailedRecoverable] = new[]
            {
                EmailInquiryAssemblyStatus.Inspecting,
                EmailInquiryAssemblyStatus.Extracting,
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
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
                CompletedCount(componentStatuses),
                "A component of this message was refused by the malware scanner.");

        // A missing component row counts as outstanding. The barrier must not be satisfiable
        // by failing to record something.
        var missing = expectedComponentCount - componentStatuses.Count;
        if (missing > 0)
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.FailedRecoverable,
                CompletedCount(componentStatuses),
                $"{missing} expected part(s) of this message have no record yet; it is held for replay.");

        // An infrastructure hold anywhere holds the WHOLE message. This is the rule that stops
        // a body-only Lead being created when the attachment carrying the priced lines could
        // not be stored or scanned.
        if (componentStatuses.Contains(EmailInquiryComponentStatus.FailedRecoverable))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.FailedRecoverable,
                CompletedCount(componentStatuses),
                "Part of this message could not be processed because a required service was "
                + "unavailable. It is held and will resume without re-reading the mailbox.");

        if (componentStatuses.Any(s => s is EmailInquiryComponentStatus.Inspecting))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.Inspecting, CompletedCount(componentStatuses), null);

        if (componentStatuses.Any(s => s is EmailInquiryComponentStatus.Pending
                or EmailInquiryComponentStatus.Extracting))
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.Extracting, CompletedCount(componentStatuses), null);

        // Everything expected is terminal. Only now may the message become commercial fact.
        //
        // Note this includes the all-Skipped case: a message whose every part was unsupported
        // is READY, not held. There is nothing to wait for and nothing to recover — the
        // reviewer needs to see it with its reasons, which is what NeedsReview downstream is
        // for, not a permanent hold that no operator will ever clear.
        return new EmailInquiryAssemblyEvaluation(
            EmailInquiryAssemblyStatus.ReadyForAssembly, CompletedCount(componentStatuses), null);
    }

    private static int CompletedCount(IReadOnlyCollection<EmailInquiryComponentStatus> statuses)
        => statuses.Count(s => s is EmailInquiryComponentStatus.Completed
            or EmailInquiryComponentStatus.Skipped
            or EmailInquiryComponentStatus.RefusedSecurity);
}

/// <param name="Status">What the message as a whole now is.</param>
/// <param name="CompletedComponentCount">How many expected components are terminal.</param>
/// <param name="Reason">
/// Operator-readable explanation, or null when the state needs none. Never carries a bucket
/// name, endpoint, credential or provider exception type.
/// </param>
public readonly record struct EmailInquiryAssemblyEvaluation(
    EmailInquiryAssemblyStatus Status,
    int CompletedComponentCount,
    string? Reason);
