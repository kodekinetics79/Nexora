namespace ERP_RFQ_Automation.QuoteDelivery;

/// <summary>Durable, tenant-owned request to deliver an immutable quote-send intent.</summary>
public sealed class QuoteDeliveryRequest
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }
    public long QuoteId { get; set; }
    public string IdempotencyKey { get; set; } = null!;
    public string RecipientEmail { get; set; } = null!;
    public string Subject { get; set; } = null!;
    public string Body { get; set; } = null!;
    public string? FromEmail { get; set; }
    public string AttachmentFileName { get; set; } = null!;
    public DateTime RequestedOn { get; set; }
    public DateTime AvailableOn { get; set; }
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptOn { get; set; }
    public string? LeaseOwner { get; set; }
    public Guid? LeaseToken { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public DateTime? CompletedOn { get; set; }
    public DateTime? DeadLetteredOn { get; set; }
    public string? LastErrorCode { get; set; }
    public long Version { get; set; }

    /// <summary>
    /// R5 content binding. The price fingerprint (currency + every line's unit price) that was
    /// attested at the moment this send was authorised — the exact priced content the rep
    /// committed to putting in front of the customer.
    ///
    /// <para>Enqueueing is not sending: the quote stays DRAFT and therefore editable until the
    /// worker drains this row, so "attested" at queue time proves nothing about what the PDF
    /// will contain at dispatch time. The dispatcher recomputes the fingerprint immediately
    /// before rendering and refuses to send when it no longer matches this value, which is the
    /// same record-at-capture / verify-at-serve shape <c>FileController</c> uses for attachment
    /// bytes.</para>
    ///
    /// <para>Nullable only for rows written before this column existed. Absent is UNKNOWN, not
    /// verified — the dispatcher still requires a currently-valid attestation for those, it
    /// simply has no earlier value to compare against.</para>
    /// </summary>
    public string? AttestedPriceFingerprint { get; set; }
}
