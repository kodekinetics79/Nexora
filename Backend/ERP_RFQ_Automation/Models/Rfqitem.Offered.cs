namespace ERP_RFQ_Automation.Models;

public partial class Rfqitem
{
    /// <summary>
    /// The part actually being offered when it is not the one the customer asked for: the
    /// manufacturer's replacement for an obsolete part, or an equivalent when the part was
    /// discontinued with no replacement. Null means the line is offered as asked.
    /// </summary>
    public string? OfferedPartNumber { get; set; }

    public string? OfferedMakerName { get; set; }

    /// <summary>REPLACEMENT (the maker's own successor) or EQUIVALENT (another part that does the job).</summary>
    public string? OfferedKind { get; set; }

    /// <summary>What the customer reads: why it is offered instead of the part they asked for.</summary>
    public string? OfferedNote { get; set; }

    /// <summary>The specs the customer needs to judge an equivalent (ratings, size, standard).</summary>
    public string? OfferedSpecs { get; set; }
}

/// <summary>Why a line is offered as a different part.</summary>
public static class OfferedPartKinds
{
    public const string Replacement = "REPLACEMENT";
    public const string Equivalent = "EQUIVALENT";
    public const int MaxNote = 300;
    public const int MaxSpecs = 600;

    public static bool IsKnown(string? kind) => kind is Replacement or Equivalent;

    /// <summary>"Offered: GE THQL32010, replaces ABB AF96-30-00-13" — one sentence for a quote line or a supplier email.</summary>
    public static string? Sentence(string? kind, string? offeredMaker, string? offeredPart, string? askedPart, string? note)
    {
        if (string.IsNullOrWhiteSpace(offeredPart) && string.IsNullOrWhiteSpace(offeredMaker)) return null;
        var offered = string.Join(" ", new[] { offeredMaker, offeredPart }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var head = kind == Equivalent
            ? $"Offered as an equivalent: {offered}"
            : $"Offered: {offered}";
        if (!string.IsNullOrWhiteSpace(askedPart))
            head += kind == Equivalent ? $", in place of {askedPart}" : $", replaces {askedPart}";
        return string.IsNullOrWhiteSpace(note) ? head : $"{head}. {note.Trim()}";
    }
}
