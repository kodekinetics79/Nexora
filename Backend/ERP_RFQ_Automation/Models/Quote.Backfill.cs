namespace ERP_RFQ_Automation.Models;

public partial class Quote
{
    /// <summary>
    /// The number the CUSTOMER already knows this quote by.
    ///
    /// A tenant does not start on the day it starts using Nexora. It arrives holding quotes
    /// it has already sent, and the customer chasing one of them will quote the number on the
    /// paper in front of them, never <see cref="QuoteNo"/>. Discarding that number to impose
    /// ours would make a back-filled quote unfindable by the only reference the outside world
    /// has for it, so both are kept: ours for lineage, theirs for recognition.
    ///
    /// Null on a quote this system produced, which has no prior identity to preserve.
    /// </summary>
    public string? ExternalQuoteReference { get; set; }

    /// <summary>
    /// How this quote came to exist: <c>PIPELINE</c> when Nexora produced it from an enquiry,
    /// <c>BACKFILL</c> when a person entered a quote that predates Nexora.
    ///
    /// This is stored rather than derived from the originating lead's source. Both kinds are
    /// meant to be monitored on ONE screen, and a list that must join through RFQ and Lead to
    /// discover what it is looking at is a list that will eventually be written without the
    /// join. The column makes the distinction impossible to lose.
    /// </summary>
    public string Origin { get; set; } = QuoteOrigin.Pipeline;
}

/// <summary>The permitted values of <see cref="Quote.Origin"/>.</summary>
public static class QuoteOrigin
{
    public const string Pipeline = "PIPELINE";
    public const string Backfill = "BACKFILL";

    public static bool IsKnown(string? value) => value is Pipeline or Backfill;
}
