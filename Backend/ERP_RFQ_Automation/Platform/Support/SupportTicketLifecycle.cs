namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// The permitted moves between <see cref="SupportTicketStatus"/> values, in one place.
///
/// <para>Kept out of the controller for the reason <c>TenantsController</c>'s
/// Active &lt;-&gt; Suspended &lt;-&gt; Archived graph is a single validated helper rather than four
/// independent endpoints: a lifecycle spread across handlers is a lifecycle nobody can read, and
/// the first "just this once" edge added directly in an endpoint is the one that lets a ticket be
/// resolved twice, or closed and then resolved, producing timestamps that no report can explain.</para>
///
/// <para>The graph is deliberately permissive about FINISHING and strict about going backwards.
/// Any live state may resolve or close, because an operator who has fixed the problem should not
/// have to walk a ticket through ceremony to say so. Nothing may return to
/// <see cref="SupportTicketStatus.New"/>: "untriaged" is a claim about a ticket nobody has looked at
/// yet, and once someone has looked, it is false forever.</para>
/// </summary>
public static class SupportTicketLifecycle
{
    private static readonly IReadOnlyDictionary<SupportTicketStatus, SupportTicketStatus[]> Allowed =
        new Dictionary<SupportTicketStatus, SupportTicketStatus[]>
        {
            // A brand-new ticket can be picked up, or answered on the spot ("restarted the poller,
            // done") without a pointless trip through Open, or dismissed as a duplicate.
            [SupportTicketStatus.New] =
            [
                SupportTicketStatus.Open, SupportTicketStatus.Pending,
                SupportTicketStatus.Resolved, SupportTicketStatus.Closed
            ],

            [SupportTicketStatus.Open] =
            [
                SupportTicketStatus.Pending, SupportTicketStatus.Resolved, SupportTicketStatus.Closed
            ],

            [SupportTicketStatus.Pending] =
            [
                SupportTicketStatus.Open, SupportTicketStatus.Resolved, SupportTicketStatus.Closed
            ],

            // Reopening a resolved ticket is the customer saying "that did not fix it". It is the
            // single most informative event on a support desk and it must land on the SAME ticket,
            // where the earlier attempt is, rather than on a fresh one that has forgotten it.
            [SupportTicketStatus.Resolved] =
            [
                SupportTicketStatus.Open, SupportTicketStatus.Pending, SupportTicketStatus.Closed
            ],

            // Closed is not terminal, for the same reason.
            [SupportTicketStatus.Closed] = [SupportTicketStatus.Open]
        };

    /// <summary>Statuses a ticket in <paramref name="current"/> may move to.</summary>
    public static IReadOnlyList<SupportTicketStatus> NextFrom(SupportTicketStatus current)
        => Allowed.TryGetValue(current, out var next) ? next : [];

    public static bool CanTransition(SupportTicketStatus current, SupportTicketStatus target)
        => Allowed.TryGetValue(current, out var next) && next.Contains(target);

    /// <summary>
    /// True when <paramref name="target"/> puts the ticket back into active work from a finished
    /// state. Callers use this to clear <see cref="SupportTicket.ResolvedAtUtc"/> /
    /// <see cref="SupportTicket.ClosedAtUtc"/>, because a reopened ticket that keeps its old
    /// resolution timestamp silently understates every time-to-resolution figure derived from the
    /// table — the number would be measuring the first attempt and reporting it as the outcome.
    /// </summary>
    public static bool IsReopen(SupportTicketStatus current, SupportTicketStatus target)
        => current is SupportTicketStatus.Resolved or SupportTicketStatus.Closed
           && target is SupportTicketStatus.Open or SupportTicketStatus.Pending;

    /// <summary>Statuses that still represent outstanding work. Drives the "open tickets" counts.</summary>
    public static readonly SupportTicketStatus[] Live =
    [
        SupportTicketStatus.New, SupportTicketStatus.Open, SupportTicketStatus.Pending
    ];

    public static bool IsLive(SupportTicketStatus status) => Live.Contains(status);
}
