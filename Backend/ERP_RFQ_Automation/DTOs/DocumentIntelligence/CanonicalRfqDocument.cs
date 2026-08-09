using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.DocumentIntelligence;

public enum CanonicalValueKind
{
    Extracted,
    Normalized,
    Derived,
    Assumption,
    Missing
}

public enum ValidationSeverity
{
    Info,
    Warning,
    Error
}

public enum ValidationStatus
{
    Unvalidated,
    Valid,
    NeedsReview,
    Invalid
}

public sealed class SourceEvidence
{
    public string SourceDocumentId { get; set; } = "upload";
    public string SourceDocumentName { get; set; } = "RFQ upload";
    public string Location { get; set; } = "";
    public string? RawValue { get; set; }
}

public sealed class CanonicalValue<T>
{
    public T? Value { get; set; }
    public string? OriginalValue { get; set; }
    public CanonicalValueKind Kind { get; set; } = CanonicalValueKind.Extracted;
    public decimal Confidence { get; set; }
    public ValidationStatus ValidationStatus { get; set; } = ValidationStatus.Unvalidated;
    public List<SourceEvidence> Evidence { get; set; } = new();
    public List<string> Transformations { get; set; } = new();
}

public sealed class CanonicalRfqLineItem
{
    public CanonicalValue<string> LineItemNo { get; set; } = new();
    public CanonicalValue<string> ProductName { get; set; } = new();
    public CanonicalValue<int> Quantity { get; set; } = new();

    /// <summary>The buyer's own unit word, verbatim. Canonicalised later; never defaulted here.</summary>
    public CanonicalValue<string> UnitOfMeasure { get; set; } = new();

    public CanonicalValue<decimal> UnitPrice { get; set; } = new();
    public CanonicalValue<string> Currency { get; set; } = new();
    public CanonicalValue<string> ManufacturerName { get; set; } = new();
    public CanonicalValue<string> ManufacturerPartNumber { get; set; } = new();
    public CanonicalValue<int> LeadTimeDays { get; set; } = new();

    /// <summary>The buyer's note against the line. Never validated — a note cannot be "wrong".</summary>
    public CanonicalValue<string> ItemText { get; set; } = new();
    public ValidationStatus ValidationStatus { get; set; } = ValidationStatus.Unvalidated;
}

public sealed class CanonicalRfqDocument
{
    public string SchemaVersion { get; set; } = "rfq-canonical/v1";
    public long BusinessUnitId { get; set; }
    public CanonicalValue<string> RfqNo { get; set; } = new();
    public CanonicalValue<string> BuyerName { get; set; } = new();
    public CanonicalValue<DateTime> ReceivedDate { get; set; } = new();
    public CanonicalValue<DateTime> BidClosingDate { get; set; } = new();

    /// <summary>FR-RFQ-04. Saudi region or city for delivery, in the buyer's own wording.</summary>
    public CanonicalValue<string> DeliveryLocation { get; set; } = new();

    /// <summary>FR-RFQ-04. The delivery date the buyer asked for. Optional — never invalidates.</summary>
    public CanonicalValue<DateTime> RequiredDeliveryDate { get; set; } = new();

    /// <summary>FR-RFQ-03. Standing agreement or frame contract reference.</summary>
    public CanonicalValue<string> AgreementReference { get; set; } = new();
    public List<CanonicalRfqLineItem> LineItems { get; set; } = new();
    public List<CanonicalValidationIssue> Issues { get; set; } = new();
    public ValidationStatus ValidationStatus { get; set; } = ValidationStatus.Unvalidated;
}

public sealed class CanonicalValidationIssue
{
    public ValidationSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
    public SourceEvidence? Evidence { get; set; }
}
