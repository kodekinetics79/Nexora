namespace ERP_RFQ_Automation.ProductIntelligence.ManufacturerKnowledge;

/// <summary>
/// One thing a tenant has taught the platform about a maker's part numbers: "a number that
/// starts like THIS came from THAT manufacturer", learned from a reviewed lead line that stated
/// both fields.
///
/// <para><b>Why a table of the tenant's own history and not a catalogue.</b> <c>Products</c>
/// carries no manufacturer column at all (<c>EfProductResolutionCatalog</c> projects it as null),
/// so there is nothing platform-wide to look a part number up in — and there should not be:
/// which maker "X7-M5" belongs to is something this tenant's buyers established with their own
/// documents, and a neighbouring tenant's answer to the same prefix may be a different maker's
/// range. Every row is scoped to a business unit and the store is under row-level security for
/// the same reason <c>customer_identifiers</c> is.</para>
///
/// <para><b>Why rows count observations instead of being unique per pattern.</b> A prefix seen
/// once on one line is an anecdote; <see cref="ManufacturerInference"/> requires a summed
/// <see cref="ObservationCount"/> of at least two before it will assert a maker from a pattern,
/// and refuses outright when two rows with the same pattern name different makers. Storing one
/// row per (pattern, maker) rather than one per pattern is what makes that disagreement visible
/// instead of last-writer-wins.</para>
///
/// <para><b>Provenance.</b> <see cref="LearnedFromLeadId"/> and
/// <see cref="LearnedFromReviewAuditId"/> record which human review first taught the row, mirroring
/// <c>CustomerIdentifier</c>; without them a wrong pattern cannot be traced to the review that
/// introduced it.</para>
/// </summary>
public sealed class ManufacturerPartPattern
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>
    /// The normalised, separator-free part-number prefix — exactly one of the candidates
    /// <see cref="ManufacturerInference.PatternCandidates"/> produces, so a lookup is an equality
    /// test and never a LIKE scan. Upper-case, compatibility-normalised, no punctuation.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>The maker's name as the reviewed document spelled it; what a rep sees on the line.</summary>
    public string Manufacturer { get; set; } = string.Empty;

    /// <summary>
    /// <see cref="ProductIdentityNormalizer.NormalizeManufacturer"/> of <see cref="Manufacturer"/>.
    /// Agreement between rows is decided on this, so "Siemens" and "SIEMENS " are one maker and
    /// never flag each other as a conflict.
    /// </summary>
    public string NormalizedManufacturer { get; set; } = string.Empty;

    /// <summary>How many reviewed lines have stated this pairing. Never decremented.</summary>
    public int ObservationCount { get; set; } = 1;

    public long? LearnedFromLeadId { get; set; }
    public long? LearnedFromReviewAuditId { get; set; }
    public DateTimeOffset CreatedOn { get; set; }
    public DateTimeOffset LastObservedOn { get; set; }
}
