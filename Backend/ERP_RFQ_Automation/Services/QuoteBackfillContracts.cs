namespace ERP_RFQ_Automation.Services;

/// <summary>One line of a quote that was issued before Nexora existed.</summary>
public sealed class QuoteBackfillLine
{
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal? TaxAmount { get; set; }
    public decimal? Discount { get; set; }
    public long? ProductId { get; set; }
    public string? UnitOfMeasure { get; set; }
    public string? CustomerLineRef { get; set; }
}

/// <summary>A quote the tenant already sent, being carried into Nexora to be monitored.</summary>
public sealed class QuoteBackfillRequest
{
    public long CustomerId { get; set; }
    public long? ContactId { get; set; }

    /// <summary>The number the customer knows this quote by. Required: it is the idempotency key.</summary>
    public string ExternalQuoteReference { get; set; } = null!;

    /// <summary>The date the quote was ISSUED, not the date it is being typed in.</summary>
    public DateTime QuoteDate { get; set; }

    public DateTime? ValidUntil { get; set; }
    public long CurrencyId { get; set; }

    /// <summary>Lifecycle code, e.g. SENT. A back-filled quote is rarely a draft.</summary>
    public string? StatusCode { get; set; }

    public string? HeaderRemarks { get; set; }

    /// <summary>
    /// The total actually quoted. Supplied because it is a HISTORICAL FACT, not a calculation:
    /// the customer holds a piece of paper with this number on it. When present it is stored
    /// verbatim even if it disagrees with the lines, and the disagreement is reported rather
    /// than silently corrected — re-deriving it would change what the tenant promised.
    /// </summary>
    public decimal? TotalAmount { get; set; }

    public List<QuoteBackfillLine> Lines { get; set; } = [];
}

/// <summary>The outcome of carrying one quote in.</summary>
public sealed class QuoteBackfillResult
{
    public long QuoteId { get; set; }
    public string QuoteNo { get; set; } = null!;
    public string ExternalQuoteReference { get; set; } = null!;
    public string NexoraSerial { get; set; } = null!;
    public long RfqId { get; set; }
    public long LeadId { get; set; }
    public decimal TotalAmount { get; set; }

    /// <summary>True when this reference was already present and nothing new was written.</summary>
    public bool AlreadyPresent { get; set; }

    /// <summary>Set when a supplied total disagrees with the lines. Recorded, never corrected.</summary>
    public string? TotalMismatchWarning { get; set; }
}
