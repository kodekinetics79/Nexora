namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// The durable extraction result for ONE component of an email inquiry.
///
/// <para><b>Why this table has to exist.</b> Extraction output has only ever flowed straight
/// into Lead creation: the worker extracted, persisted a Lead, and the structured result was
/// never written anywhere. That is workable when one document means one Lead. It is not
/// workable for a message, where the body and each attachment finish at different times and a
/// Lead may only be built once ALL of them have. Without somewhere to put a finished part, the
/// worker's only honest options were to throw the result away or to hold the component — and
/// holding every component is what the pipeline did.</para>
///
/// <para><b>Versioned payload, on purpose.</b> <see cref="PayloadJson"/> is a serialized
/// extraction result and its shape will change. <see cref="PayloadContractVersion"/> is stamped
/// at write time so a later reader can tell "written under the old shape" from "corrupt", and
/// refuse rather than guess. A payload whose version a reader does not recognise sends the
/// message to review; it is never coerced.</para>
///
/// <para><b>Ownership is the component, singular.</b> The row is keyed to
/// <see cref="ComponentId"/> — not to a component key, not to a job id, not to an occurrence.
/// Those three disagreeing is how a result gets attributed to the wrong part of the wrong
/// message when one tenant receives the same message in two mailboxes.</para>
/// </summary>
public class EmailInquiryComponentResult
{
    /// <summary>
    /// Contract version written by the CURRENT code. Bump whenever the meaning or shape of
    /// <see cref="PayloadJson"/> changes in a way a previous reader would misread — never for a
    /// serializer setting or a field that is purely additive and optional.
    /// </summary>
    public const int CurrentPayloadContractVersion = 1;

    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    /// <summary>Denormalized from the component so the barrier can read a whole message's
    /// results in one indexed query without joining.</summary>
    public long AssemblyId { get; set; }

    /// <summary>THE owning component. One result per component, enforced by a unique index.</summary>
    public long ComponentId { get; set; }

    /// <summary>
    /// The extraction job that produced this result. Audit and diagnosis only — it is
    /// deliberately NOT the identity of the row, so a re-run under a new job updates the
    /// component's single result rather than appending a second one.
    /// </summary>
    public long ExtractionJobId { get; set; }

    /// <inheritdoc cref="CurrentPayloadContractVersion"/>
    public int PayloadContractVersion { get; set; } = CurrentPayloadContractVersion;

    /// <summary>The serialized extraction result. Never null on a stored row.</summary>
    public string PayloadJson { get; set; } = null!;

    /// <summary>
    /// How the result was produced — deterministic rules, native parser, or a model. Persisted
    /// rather than inferred because it decides whether a reviewer is looking at arithmetic or
    /// at a generated answer, and that question outlives the log line that could answer it.
    /// </summary>
    public string ProcessingPath { get; set; } = null!;

    /// <summary>Local / External where a model was involved; null on a deterministic path.</summary>
    public string? AiProviderClass { get; set; }

    /// <summary>The model identity, where one was used. Null on a deterministic path.</summary>
    public string? ModelIdentifier { get; set; }

    /// <summary>Header-level confidence the extractor reported, 0..1. Null where not applicable.</summary>
    public decimal? HeaderConfidence { get; set; }

    /// <summary>Rows the source appeared to contain, as the extractor counted them.</summary>
    public int ExpectedItemCount { get; set; }

    /// <summary>Rows actually extracted. A shortfall against
    /// <see cref="ExpectedItemCount"/> is what sends a message to review.</summary>
    public int ExtractedItemCount { get; set; }

    /// <summary>Why the extractor believes a human should look, if it does.</summary>
    public string? ReviewReason { get; set; }

    /// <summary>Per-stage extractor diagnostics, kept so a dead end can be explained later.</summary>
    public string? DiagnosticsJson { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }

    /// <inheritdoc cref="EmailInquiryAssembly.ConcurrencyVersion"/>
    public int ConcurrencyVersion { get; set; }

    public virtual EmailInquiryComponent Component { get; set; } = null!;
}
