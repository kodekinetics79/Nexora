namespace ERP_RFQ_Automation.Services;

/// <summary>One line of a quote that was issued before Nexora existed.</summary>
public sealed class QuoteBackfillLine
{
    public string Description { get; set; } = null!;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// The tax that was charged on this line on the quote the customer holds. Optional, and
    /// CHECKED rather than stored: R17 derives every line's output tax from the tenant's CURRENT
    /// rate, so an amount that disagrees with that derivation cannot be honoured and the import is
    /// REFUSED naming both figures. Omitting it accepts Nexora's derived tax.
    ///
    /// <para>It used to be added into the header total and then dropped, so a quote issued under a
    /// different VAT rate was silently re-taxed at today's.</para>
    /// </summary>
    public decimal? TaxAmount { get; set; }

    /// <summary>
    /// The discount taken on this line, as an AMOUNT in the quote's currency — not a percentage.
    /// Mapped onto the tenant's FIXED discount type so the create path can honour it; a tenant with
    /// no FIXED discount type configured is refused rather than having the discount dropped.
    ///
    /// <para>It used to be written onto <c>QuoteItem.Discount</c>, which <c>CreateQuoteAsync</c>
    /// RECOMPUTES and never reads, so every historical line discount vanished and the line was
    /// re-grossed to its undiscounted value.</para>
    /// </summary>
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
    /// The total this quote was actually sent at — the number on the piece of paper the customer
    /// holds. It is NOT stored, and this comment used to claim the opposite.
    ///
    /// <para>What actually happens: <c>QuoteService.CreateQuoteAsync</c> ignores a supplied header
    /// total outright and <c>CalculateQuoteTotals</c> derives one from the lines and the tenant's
    /// CURRENT output tax rate. So this figure is COMPARED against what was persisted and any
    /// difference is reported in <see cref="QuoteBackfillResult.TotalMismatchWarning"/>, naming both
    /// numbers. <see cref="QuoteBackfillResult.TotalAmount"/> is always the persisted one.</para>
    ///
    /// <para>The old promise — "stored verbatim ... the disagreement is reported rather than
    /// silently corrected" — was never implemented, and the result reported the tenant's figure
    /// while the database held Nexora's. A go-live import of 40 open quotes answered 201 with the
    /// right number every time and put every one of them in at a different one; a quote the
    /// customer holds at 105,000 showed as 115,000 on the pipeline, the list, the view screen and
    /// any re-issued PDF. The mismatch warning could not catch it: it compared the tenant's figure
    /// with a local sum of the request's own lines, never with what was stored.</para>
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
    /// <summary>
    /// What was PERSISTED, read back off the created quote — never the figure the request supplied.
    /// Returning the request's own number here is what let a whole import land at the wrong total
    /// with a clean 201 on every row.
    /// </summary>
    public decimal TotalAmount { get; set; }

    /// <summary>True when this reference was already present and nothing new was written.</summary>
    public bool AlreadyPresent { get; set; }

    /// <summary>
    /// Set when the total the tenant stated differs from the one Nexora derived and stored. Names
    /// BOTH figures, because the stored one is what every screen and any re-issued PDF will show.
    /// </summary>
    public string? TotalMismatchWarning { get; set; }
}
