namespace ERP_RFQ_Automation.Models;

// Which catalogue product a requested line actually IS.
//
// LeadConversionIntelligence auto-assigns a product only above ConfidenceFloor (0.90), and
// refuses to guess below it — correctly, because a line silently bound to the wrong product
// produces a quote that looks right and prices the wrong thing. On a real bid document, where
// lines are prose rather than part numbers, most lines land below that floor.
//
// The consequence reached all the way to the end: an unresolved line makes every supplier offer
// against it ineligible ("product unresolved"), which blocks the award, which leaves the customer
// quote at zero. The machine's refusal to guess was right; what was missing was the human
// answer it was waiting for.
//
// Recorded exactly like a participation decision, and for the same reason: it is a commercial act
// with a person behind it, and "who said this line is that product, and why" must survive.
public partial class Rfqitem
{
    public string? ProductResolvedBy { get; private set; }
    public DateTime? ProductResolvedOn { get; private set; }
    public string? ProductResolutionReason { get; private set; }

    /// <summary>True when a person, not the matcher, bound this line to its product.</summary>
    public bool IsProductHumanResolved => ProductResolvedBy is not null;

    /// <summary>
    /// Binds this line to a catalogue product on a person's authority.
    ///
    /// The caller must have already checked the product exists in this tenant; the rule enforced
    /// HERE is that the act is attributable and re-attributable — a correction after a mistake is
    /// ordinary and must be allowed, but never anonymously.
    /// </summary>
    public void ResolveProduct(long productId, string? reason, string actor, DateTime nowUtc)
    {
        if (productId <= 0)
            throw new ArgumentOutOfRangeException(nameof(productId), "A product is required.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new ArgumentException("An actor is required.", nameof(actor));

        ProductId = productId;
        ProductResolvedBy = actor.Trim();
        ProductResolvedOn = nowUtc;
        ProductResolutionReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ModifiedBy = actor.Trim();
        ModifiedDate = nowUtc;
    }
}
