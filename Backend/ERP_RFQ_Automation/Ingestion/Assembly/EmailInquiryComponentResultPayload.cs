namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// What the worker hands the coordinator to make durable for one component.
///
/// <para>Deliberately a plain value rather than the extractor's own outcome type: the store is
/// versioned and the extractor's shape is not, so the conversion happens once, at the boundary,
/// where the contract version is decided — instead of the persistence layer taking a dependency
/// on whatever the extraction pipeline currently returns.</para>
/// </summary>
public sealed record EmailInquiryComponentResultPayload(
    string PayloadJson,
    string ProcessingPath,
    string? AiProviderClass,
    string? ModelIdentifier,
    decimal? HeaderConfidence,
    int ExpectedItemCount,
    int ExtractedItemCount,
    string? ReviewReason,
    string? DiagnosticsJson);
