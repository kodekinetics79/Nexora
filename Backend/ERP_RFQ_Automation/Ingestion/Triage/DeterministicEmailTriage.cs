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
        if (IsUnattendedSender(s.FromAddress))
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
