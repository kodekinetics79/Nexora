using System;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// One labelled observation cell of the extraction corpus: for a single approved
/// document, a single field, how many machine-produced values the reviewer looked at
/// and how many of them the reviewer had to change.
///
/// WHY THIS TABLE EXISTS. Nexora has never had a measured extraction accuracy. The
/// confidence numbers the product currently persists are not measurements: on the
/// structured path they are a literal 1.0/0.2/0 written by the normalizer, and on the
/// model path they are the model's own self-report against a rubric in its prompt. No
/// labelled corpus existed, so no accuracy claim was defensible and none may be made.
///
/// The labels DO exist though — every human review already writes a before/after image
/// to <see cref="LeadReviewAudit"/>, and an APPROVED review is a human assertion that
/// the after image is right. That is ground truth. This table is that ground truth
/// reduced to countable cells so accuracy can be computed instead of asserted.
///
/// INVARIANTS THAT MAKE THE NUMBER HONEST.
///   * Written ONLY on approval — a "save" is work in progress, not a label.
///   * Written INSIDE the review transaction, never through the metric recorder (which
///     swallows exceptions by design). A corpus row that can silently fail to appear
///     produces a biased sample, and a biased sample is worse than no sample.
///   * One row per (document, field). The denominator of any published rate is
///     therefore DOCUMENTS, never line items — 2,966 lines from 27 documents is a
///     sample of 27, and two documents carrying 98% of the lines must not be allowed
///     to speak for the corpus.
///   * <see cref="ObservedCount"/> counts only positions where evidence exists: the
///     machine produced a value, or the reviewer supplied one, or both. A field the
///     document never carried (null before, null after) is NOT counted as a correct
///     prediction — silently scoring those as wins is exactly how a fake 99% is built.
/// </summary>
public sealed class ExtractionCorpusEntry
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public long LeadId { get; set; }

    /// <summary>The approving review. Its AfterJson is the label this row summarises.</summary>
    public long LeadReviewAuditId { get; set; }

    /// <summary>
    /// The source document this lead was extracted from, when the evidence chain has one.
    /// Null only for leads that reached approval without a bound occurrence.
    /// </summary>
    public long? SourceDocumentOccurrenceId { get; set; }

    /// <summary>
    /// Which pipeline produced the values being scored, from
    /// <see cref="ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath"/>, or "unknown" when
    /// the lead has no ingestion occurrence. Accuracy is never pooled across paths: a
    /// deterministic spreadsheet parse and a language-model read are different systems and
    /// averaging them describes neither.
    /// </summary>
    public string ExtractionPath { get; set; } = null!;

    /// <summary>"header", "line" or "document" — see <c>ExtractionCorpusScopes</c>.</summary>
    public string Scope { get; set; } = null!;

    /// <summary>Camel-cased field name, matching the review snapshot contract.</summary>
    public string FieldName { get; set; } = null!;

    /// <summary>Positions in this document where a value existed to be judged.</summary>
    public int ObservedCount { get; set; }

    /// <summary>Of those, how many the reviewer changed.</summary>
    public int CorrectedCount { get; set; }

    /// <summary>
    /// Document-level outcome: the machine got this field right everywhere in this
    /// document. This is the Bernoulli trial the published interval is computed over.
    /// </summary>
    public bool FieldCorrect { get; set; }

    public DateTime CapturedOn { get; set; }

    public string ApprovedBy { get; set; } = null!;

    public LeadReviewAudit ReviewAudit { get; set; } = null!;
}

/// <summary>Stable scope values (persisted contract).</summary>
public static class ExtractionCorpusScopes
{
    /// <summary>A lead-level field: one position per document.</summary>
    public const string Header = "header";

    /// <summary>A line-item field: one position per surviving machine-produced line.</summary>
    public const string Line = "line";

    /// <summary>
    /// A whole-document property. Currently only line inventory: did the machine find
    /// the right set of lines, independent of whether their contents were right.
    /// </summary>
    public const string Document = "document";
}
