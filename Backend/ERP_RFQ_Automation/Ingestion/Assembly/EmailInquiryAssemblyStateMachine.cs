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
                // A message that carried nothing to capture is NOT ready — it has no inquiry.
                // ReadyForAssembly is deliberately absent from this row: it is unreachable
                // without at least one component completing, which cannot have happened yet.
                EmailInquiryAssemblyStatus.NoInquiry,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.Inspecting] = new[]
            {
                EmailInquiryAssemblyStatus.Extracting,
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                // Every part was refused or unsupported: a human may still recognise an inquiry.
                EmailInquiryAssemblyStatus.NeedsReview,
                EmailInquiryAssemblyStatus.FailedRecoverable,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            [EmailInquiryAssemblyStatus.Extracting] = new[]
            {
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.NeedsReview,
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
                EmailInquiryAssemblyStatus.Extracting,
                // The reviewer's verdict can be "this was never an inquiry".
                EmailInquiryAssemblyStatus.NoInquiry
            },
            [EmailInquiryAssemblyStatus.FailedRecoverable] = new[]
            {
                EmailInquiryAssemblyStatus.Inspecting,
                EmailInquiryAssemblyStatus.Extracting,
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                EmailInquiryAssemblyStatus.NeedsReview,
                EmailInquiryAssemblyStatus.NoInquiry,
                EmailInquiryAssemblyStatus.RejectedSecurity
            },
            // Terminal, but reversible by an explicit human act: "reprocess as inquiry" already
            // exists in the product, and a triage outcome a human overrules must be able to
            // re-enter the pipeline through the SAME assembly rather than forking a second one.
            [EmailInquiryAssemblyStatus.NoInquiry] = new[]
            {
                EmailInquiryAssemblyStatus.Inspecting,
                EmailInquiryAssemblyStatus.Extracting
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

        // Everything expected is terminal. What the message IS now depends on whether anything
        // was actually captured — three outcomes, deliberately distinct, because collapsing
        // them is how an empty Lead gets created.
        var captured = CapturedCount(componentStatuses);

        // (1) Something was captured. Only now may the message become commercial fact.
        if (captured > 0)
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.ReadyForAssembly,
                CompletedCount(componentStatuses), captured, null);

        // (2) There were no parts to capture in the first place: no fresh body, no attachments.
        // A terminal triage outcome, NOT an assemblable inquiry. This is the case that used to
        // return ReadyForAssembly and would have produced a Lead with nothing in it.
        if (expectedComponentCount == 0)
            return new EmailInquiryAssemblyEvaluation(
                EmailInquiryAssemblyStatus.NoInquiry, 0, 0,
                "This message carried no body text and no attachments to process.");

        // (3) Parts existed and every one of them was refused or unsupported. Distinct from (2)
        // because a human may well recognise an inquiry in a file the pipeline cannot read — a
        // CAD drawing, an unusual archive, a format we do not support yet. It goes to a person
        // with its reasons attached; it never becomes an empty Lead and it is never silently
        // discarded.
        return new EmailInquiryAssemblyEvaluation(
            EmailInquiryAssemblyStatus.NeedsReview, CompletedCount(componentStatuses), 0,
            "No part of this message could be read. Its attachments were refused or are "
            + "unsupported; the original is retained for review.");
    }

    /// <summary>
    /// Components that reached ANY terminal state. This is the barrier's progress counter and
    /// what <c>EmailInquiryAssembly.CompletedComponentCount</c> stores — "how many are we still
    /// waiting for", not "how much did we get".
    /// </summary>
    private static int CompletedCount(IReadOnlyCollection<EmailInquiryComponentStatus> statuses)
        => statuses.Count(s => s is EmailInquiryComponentStatus.Completed
            or EmailInquiryComponentStatus.Skipped
            or EmailInquiryComponentStatus.RefusedSecurity);

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
