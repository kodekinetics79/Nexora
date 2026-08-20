using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ERP_RFQ_Automation.Ingestion.Triage;

/// <summary>
/// Everything the triage rules are allowed to see. Pure data: the caller resolves party type
/// and pulls the raw headers, so <see cref="DeterministicEmailTriage"/> stays IO-free and
/// exhaustively testable.
/// </summary>
public sealed record EmailTriageSignals
{
    public string Subject { get; init; } = string.Empty;

    /// <summary>The sender's own words only — see <see cref="EmailBodyNormalizer"/>.</summary>
    public string FreshBody { get; init; } = string.Empty;

    public string? Signature { get; init; }
    public string? FromAddress { get; init; }
    public string? FromDomain { get; init; }

    /// <summary>
    /// True when the sender address is one this tenant SENDS from. Defaults false, so a caller
    /// that does not supply it leaves the classifier's behaviour exactly as it was.
    /// </summary>
    public bool SenderIsOwnMailbox { get; init; }

    /// <summary>"customer" | "supplier" | null. Resolved by the caller against tenant master
    /// data. Null means unknown — which must never, on its own, stop a message.</summary>
    public string? SenderPartyType { get; init; }

    public bool HasInReplyTo { get; init; }
    public bool HasReferences { get; init; }
    public bool HasAttachments { get; init; }

    /// <summary>From <see cref="EmailBodyParts.BodyEmptyAfterStrip"/>.</summary>
    public bool BodyEmptyAfterStrip { get; init; }

    // ---- raw headers ----
    public string? AutoSubmitted { get; init; }
    public string? XAutoreply { get; init; }

    /// <summary>Recorded because Exchange sets it on out-of-office mail, but deliberately NOT
    /// decisive on its own: legitimate senders set it on ordinary business mail too.</summary>
    public string? XAutoResponseSuppress { get; init; }

    public string? Precedence { get; init; }
    public string? ListId { get; init; }
    public string? ListUnsubscribe { get; init; }
    public string? ContentClass { get; init; }
}

/// <summary>
/// The inbound-mail gate. Pure, deterministic, no IO, no AI.
///
/// FAILURE POSTURE (decided, not incidental): a message is stopped ONLY on positive,
/// machine-verifiable evidence that it is not business mail. The ABSENCE of RFQ keywords is
/// never a reason to stop — that was the old gate's failure mode, and it is why a bare prose
/// enquiry ("please quote 40 nos cable tray 300mm") could be dropped while every supplier
/// order confirmation sailed through on the word "quote". Uncertain means EXTRACT AND FLAG:
/// one governed prose call costs a fraction of a cent under an existing token ledger with
/// caps, and one missed cable-tray enquiry is five-figure margin.
/// </summary>
public static class DeterministicEmailTriage
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Opts = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>"pls quote", "please send", "kindly advise", "please offer".</summary>
    private static readonly Regex RequestVerbPattern = new(
        @"\b(?:pls|please|kindly)\s+(?:quote|send|advise|offer)", Opts, RegexTimeout);

    /// <summary>A quantity written against a unit of measure — the strongest conversational
    /// signal that a physical good is being requested.</summary>
    private static readonly Regex QuantityUomPattern = new(
        @"\b\d{1,6}\s*(?:nos?|pcs?|units?|sets?|mtrs?|m|kg|ltr)\b", Opts, RegexTimeout);

    private static readonly string[] SubjectInquiryTerms =
        { "rfq", "request for quot", "enquiry", "inquiry", "itb", "tender" };

    /// <summary>
    /// Vocabulary of the public- and enterprise-procurement channel, which reaches a distributor
    /// almost exclusively through NO-REPLY notification platforms — BidNet, DemandStar,
    /// Periscope, SAM.gov, Ariba, Jaggaer and their like.
    ///
    /// <para>These words are positive evidence that a message is business mail, and they exist
    /// to override the unattended-sender signal. Without them a solicitation or an addendum is
    /// discarded for the sole reason that nobody could reply to it — which is true of every
    /// notification this channel has ever sent.</para>
    /// </summary>
    private static readonly string[] ProcurementNotificationTerms =
        { "solicitation", "addendum", "addenda", "bid", "invitation to bid", "request for proposal",
          "rfp", "rfi", "sourcing event", "prequalification", "procurement", "award notice",
          "amendment", "due date", "closing date" };

    /// <summary>Local parts that identify an unattended mailbox. A reply to one of these can
    /// never reach a human, so a request never originates from one.</summary>
    private static readonly string[] UnattendedLocalParts =
        { "no-reply", "noreply", "no_reply", "donotreply", "do-not-reply", "mailer-daemon", "postmaster" };

    /// <summary>Terms lifted from <c>DeterministicCommercialDocumentClassifier</c> so the two
    /// classifiers can never drift apart on what a supplier quotation looks like.</summary>
    internal static readonly string[] SupplierQuoteTerms =
        { "supplier quotation", "quotation no", "quote validity", "unit price", "incoterms" };

    internal static readonly string[] SupplierInvoiceTerms =
        { "tax invoice", "invoice number", "amount due", "remit to" };

    internal static readonly string[] PurchaseOrderTerms =
        { "purchase order", "customer po", "order confirmation requested", "ship to" };

    /// <summary>
    /// Whether the message carries positive evidence of commercial content, used to override
    /// the unattended-sender signal.
    ///
    /// <para>Deliberately broad and cheap: the cost of a false positive is one message reaching
    /// extraction and being classified as no-inquiry, while the cost of a false negative is a
    /// discarded solicitation. Those are not comparable, so this errs toward letting mail
    /// through.</para>
    /// </summary>
    internal static bool HasCommercialEvidence(EmailTriageSignals s)
    {
        var haystack = $"{s.Subject}\n{s.FreshBody}".ToLowerInvariant();
        if (SubjectInquiryTerms.Any(haystack.Contains)) return true;
        if (ProcurementNotificationTerms.Any(haystack.Contains)) return true;
        if (RequestVerbPattern.IsMatch(haystack)) return true;
        if (QuantityUomPattern.IsMatch(haystack)) return true;
        // An unattended sender that troubled to attach a document is describing something, and
        // the document is the thing worth reading.
        return s.HasAttachments;
    }

    public static EmailTriageDecision Evaluate(EmailTriageSignals s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var threadContinuation = s.HasInReplyTo || s.HasReferences;

        // ---- 1. NOISE: hard stop, positive evidence only -------------------
        var noise = new List<string>();
        if (IsAutoSubmitted(s.AutoSubmitted) || !string.IsNullOrWhiteSpace(s.XAutoreply))
            noise.Add(EmailTriageReasonCodes.AutoSubmittedHeader);
        if (IsBulkPrecedence(s.Precedence)
            || !string.IsNullOrWhiteSpace(s.ListId)
            || !string.IsNullOrWhiteSpace(s.ListUnsubscribe))
            noise.Add(EmailTriageReasonCodes.BulkListHeader);
        if (IsCalendarMessage(s.ContentClass))
            noise.Add(EmailTriageReasonCodes.CalendarInvite);
        // UNATTENDED SENDER IS NOT EVIDENCE OF NON-BUSINESS MAIL.
        //
        // This rule used to be a hard stop, reasoning that "a reply to one of these can never
        // reach a human, so a request never originates from one". That premise is false in this
        // product's actual market. Government and enterprise procurement reaches a distributor
        // almost entirely through no-reply notification platforms — BidNet Direct, DemandStar,
        // Periscope, SAM.gov, Ariba, Jaggaer. You do not reply to a solicitation; you act on it.
        //
        // Observed in production on 2026-08-13: BidNet Direct addenda for the Fulton County Jail
        // and Clayton County justice-system solicitations were all discarded as "No-reply
        // sender". Real bid opportunities, thrown away for being unrepliable.
        //
        // It stays a signal — a no-reply address genuinely does correlate with automated mail —
        // but it can no longer stop a message ON ITS OWN. Positive commercial evidence in the
        // subject or body overrides it, which is exactly the principle this gate already claims
        // to follow: stop only on positive, machine-verifiable evidence that something is NOT
        // business mail. An unattended address was never that evidence.
        // Our own outbound mail is not an inquiry, and unlike the unattended-sender rule this one
        // is NOT overridden by commercial evidence. A supplier RFQ we sent is full of commercial
        // evidence — that is what makes it convincing, and what makes the phantom lead it creates
        // look real enough to work on. The address is machine-verifiable and ours; there is no
        // reading of it under which we are a customer enquiring of ourselves.
        if (s.SenderIsOwnMailbox)
            noise.Add(EmailTriageReasonCodes.OwnOutboundMail);
        if (IsUnattendedSender(s.FromAddress) && !HasCommercialEvidence(s))
            noise.Add(EmailTriageReasonCodes.NoreplySender);
        // "The sender added no new words AND attached nothing" — there is provably nothing
        // here to extract. With an attachment present this is a perfectly ordinary
        // "see attached" RFQ, so the attachment clause is load bearing.
        if (s.BodyEmptyAfterStrip && !s.HasAttachments)
            noise.Add(EmailTriageReasonCodes.EmptyAfterQuoteStrip);

        if (noise.Count > 0)
            return new EmailTriageDecision(EmailTriageOutcome.Noise, noise.ToArray(), null, threadContinuation);

        var haystack = $"{s.Subject}\n{s.FreshBody}".ToLowerInvariant();
        var evidence = new List<string>();
        if (PurchaseOrderTerms.Any(haystack.Contains))
            evidence.Add(EmailTriageReasonCodes.PoTerms);

        // ---- 2. COMMERCIAL NON-INQUIRY ------------------------------------
        // A VERIFIED supplier sender PLUS supplier-document vocabulary. Sender alone is never
        // sufficient: suppliers send us RFQs too, and in this segment the same counterparty
        // both buys and sells. Purchase-order vocabulary is recorded as evidence but never
        // routes on its own, for the same reason.
        if (string.Equals(s.SenderPartyType, "supplier", StringComparison.OrdinalIgnoreCase))
        {
            if (SupplierQuoteTerms.Any(haystack.Contains))
            {
                var reasons = new List<string> { EmailTriageReasonCodes.SupplierQuoteTerms };
                reasons.AddRange(evidence);
                return new EmailTriageDecision(EmailTriageOutcome.CommercialNonInquiry,
                    reasons.ToArray(), EmailTriageDocumentHints.SupplierQuote, threadContinuation);
            }
            if (SupplierInvoiceTerms.Any(haystack.Contains))
            {
                var reasons = new List<string> { EmailTriageReasonCodes.InvoiceTerms };
                reasons.AddRange(evidence);
                return new EmailTriageDecision(EmailTriageOutcome.CommercialNonInquiry,
                    reasons.ToArray(), EmailTriageDocumentHints.SupplierInvoice, threadContinuation);
            }
        }

        // ---- 3. INQUIRY ---------------------------------------------------
        var inquiry = new List<string>();
        if (string.Equals(s.SenderPartyType, "customer", StringComparison.OrdinalIgnoreCase))
            inquiry.Add(EmailTriageReasonCodes.KnownCustomerContact);
        var subject = s.Subject?.ToLowerInvariant() ?? string.Empty;
        if (SubjectInquiryTerms.Any(subject.Contains))
            inquiry.Add(EmailTriageReasonCodes.RfqReference);
        if (!string.IsNullOrEmpty(s.FreshBody) && RequestVerbPattern.IsMatch(s.FreshBody))
            inquiry.Add(EmailTriageReasonCodes.RequestVerb);
        if (!string.IsNullOrEmpty(s.FreshBody) && QuantityUomPattern.IsMatch(s.FreshBody))
            inquiry.Add(EmailTriageReasonCodes.QtyUomPattern);

        if (inquiry.Count > 0)
        {
            inquiry.AddRange(evidence);
            return new EmailTriageDecision(EmailTriageOutcome.Inquiry, inquiry.ToArray(), null, threadContinuation);
        }

        // ---- 4. UNCERTAIN: extract and flag -------------------------------
        var uncertain = evidence.Count > 0 ? evidence : new List<string> { EmailTriageReasonCodes.NoSignal };
        return new EmailTriageDecision(EmailTriageOutcome.Uncertain, uncertain.ToArray(), null, threadContinuation);
    }

    /// <summary>RFC 3834: any value other than <c>no</c> means the message was generated by
    /// an automated process.</summary>
    private static bool IsAutoSubmitted(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && !value.Trim().Equals("no", StringComparison.OrdinalIgnoreCase);

    private static bool IsBulkPrecedence(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim().ToLowerInvariant();
        return v is "bulk" or "list" or "junk";
    }

    private static bool IsCalendarMessage(string? contentClass)
        => !string.IsNullOrWhiteSpace(contentClass)
           && contentClass.Trim().Equals("urn:content-classes:calendarmessage", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnattendedSender(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return false;
        var at = address.IndexOf('@');
        var localPart = (at > 0 ? address[..at] : address).Trim().ToLowerInvariant();
        if (UnattendedLocalParts.Contains(localPart)) return true;
        return localPart.StartsWith("bounce", StringComparison.Ordinal);
    }
}
