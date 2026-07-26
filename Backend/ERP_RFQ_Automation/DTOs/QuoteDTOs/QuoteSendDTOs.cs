using System;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    /// <summary>
    /// Caller-supplied context for sending a quote email (WP-B3). The requester
    /// identity comes from the authenticated caller and is stamped onto any
    /// below-floor hold; <see cref="BypassFloorHold"/> is ONLY set by the
    /// approve_below_floor_quote tool when completing an already-approved send —
    /// it must never be reachable from client input.
    /// </summary>
    public sealed class QuoteSendOptions
    {
        /// <summary>Skip the below-floor check (approved-hold execution path only).</summary>
        public bool BypassFloorHold { get; init; }

        public long? RequestedByUserId { get; init; }
        public string? RequestedBy { get; init; }
    }

    /// <summary>
    /// Outcome of a quote-send attempt: either the email went out, or the send
    /// was parked as a below-floor approval (WP-B3) and nothing was emailed.
    /// </summary>
    public sealed class QuoteSendResult
    {
        /// <summary>true when the send was held for approval instead of being performed.</summary>
        public bool Held { get; init; }

        /// <summary>The pending AgentApproval id when <see cref="Held"/>.</summary>
        public Guid? ApprovalId { get; init; }

        /// <summary>Plain-language hold summary ("Quote #Q-1042: 3 line(s) below floor by up to 12%").</summary>
        public string? HoldSummary { get; init; }
        public bool QueuedForDelivery { get; init; }
        public bool Delivered { get; init; }
        public bool Replayed { get; init; }
        public bool FailedPermanently { get; init; }
        public string? FailureCode { get; init; }

        public static QuoteSendResult Queued(bool delivered, bool replayed) => new()
        {
            QueuedForDelivery = !delivered,
            Delivered = delivered,
            Replayed = replayed
        };

        public static QuoteSendResult Failed(string failureCode) => new()
        {
            FailedPermanently = true,
            FailureCode = failureCode
        };

        public static QuoteSendResult HeldForApproval(Guid approvalId, string? summary) =>
            new() { Held = true, ApprovalId = approvalId, HoldSummary = summary };
    }

    /// <summary>Revision-chain facts for one quote (revisions-lite, WP-B4).</summary>
    public sealed class QuoteRevisionInfoDTO
    {
        public long QuoteId { get; set; }
        public string QuoteNo { get; set; } = string.Empty;

        /// <summary>1 = original, 2 = first revision, …</summary>
        public int RevisionNo { get; set; }

        /// <summary>The quote this one replaces (null on the original).</summary>
        public long? RevisionOfQuoteId { get; set; }
        public string? RevisionOfQuoteNo { get; set; }

        /// <summary>The newer revision that replaces this quote, when one exists.</summary>
        public long? SupersededByQuoteId { get; set; }
        public string? SupersededByQuoteNo { get; set; }

        /// <summary>true once any revision in the chain has a recorded outcome — the chain is closed.</summary>
        public bool ChainLocked { get; set; }

        /// <summary>true when a new revision may be created from THIS quote (non-draft, latest, chain open).</summary>
        public bool CanRevise { get; set; }
    }
}
