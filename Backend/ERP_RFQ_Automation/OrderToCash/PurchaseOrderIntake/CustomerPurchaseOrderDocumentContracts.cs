namespace ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake;

/// <summary>
/// FR-COM-01. One line as the BUYER stated it in their purchase-order document.
///
/// <para>Every value on this record came out of the customer's own file. Nothing here is ever
/// defaulted from our quotation: the whole point of reading the PO is to obtain an INDEPENDENT
/// statement of what was ordered and at what price, so that the discrepancy check compares two
/// different documents rather than the system against itself. A value that could not be read is
/// null with a reason in <see cref="ReviewReasons"/> — never a substituted number.</para>
/// </summary>
/// <param name="LineNumber">Ordinal of the line within the document, 1-based.</param>
/// <param name="ExternalLineReference">Suggested PO line reference (the ordinal), editable by the user.</param>
/// <param name="Description">The buyer's description of the item, or null when the document states none.</param>
/// <param name="OrderedQuantity">Quantity as read, or null when it could not be read with confidence.</param>
/// <param name="QuantityText">The raw cell text behind <paramref name="OrderedQuantity"/>, for the reviewer.</param>
/// <param name="UnitOfMeasure">The buyer's own unit word, verbatim and uncanonicalised.</param>
/// <param name="UnitPrice">Awarded unit price as read, or null when the document does not state one.</param>
/// <param name="UnitPriceText">The raw cell text behind <paramref name="UnitPrice"/>.</param>
/// <param name="LineAmount">Line total as read, or null.</param>
/// <param name="CustomerItemCode">The buyer's own item/material code, when the column says so.</param>
/// <param name="ManufacturerName">Manufacturer / make / brand as stated.</param>
/// <param name="ManufacturerPartNumber">Manufacturer part number as stated.</param>
/// <param name="SourceAddress">Where in the document this line was read from ('Sheet1'!C7, row 12, …).</param>
/// <param name="ReviewReasons">Everything that could not be established. Empty means fully read.</param>
public sealed record CustomerPurchaseOrderDocumentLineView(
    int LineNumber,
    string ExternalLineReference,
    string? Description,
    decimal? OrderedQuantity,
    string? QuantityText,
    string? UnitOfMeasure,
    decimal? UnitPrice,
    string? UnitPriceText,
    decimal? LineAmount,
    string? CustomerItemCode,
    string? ManufacturerName,
    string? ManufacturerPartNumber,
    string SourceAddress,
    IReadOnlyList<string> ReviewReasons)
{
    public bool RequiresReview => ReviewReasons.Count > 0;
}

/// <summary>
/// What was read out of one uploaded customer purchase order, before any of it is committed.
/// This is a PREVIEW: it is shown to a human, corrected where the document was unclear, and only
/// then turned into a <see cref="CreateCustomerPurchaseOrderCommand"/>.
/// </summary>
public sealed record CustomerPurchaseOrderDocumentView(
    long SourceAttachmentId,
    string FileName,
    string ContentSha256,
    long ByteSize,
    string ProcessingPath,
    string OcrStatus,
    string? ExternalPoNumber,
    DateTime? PoDate,
    string? PoDateText,
    bool PoDateIsDayMonthAmbiguous,
    IReadOnlyList<CustomerPurchaseOrderDocumentLineView> Lines,
    IReadOnlyList<string> ReviewReasons)
{
    public bool RequiresReview => ReviewReasons.Count > 0 || Lines.Any(line => line.RequiresReview);
}

/// <summary>
/// The document-level reading, without the storage identity the service adds afterwards.
/// </summary>
public sealed record CustomerPurchaseOrderDocumentReading(
    string? ExternalPoNumber,
    DateTime? PoDate,
    string? PoDateText,
    bool PoDateIsDayMonthAmbiguous,
    IReadOnlyList<CustomerPurchaseOrderDocumentLineView> Lines,
    IReadOnlyList<string> ReviewReasons);

/// <summary>
/// The PO document was accepted by security inspection and physically read, but nothing usable
/// could be taken out of it.
///
/// <para>This exists so that failure is LOUD. The alternative — returning a successful extraction
/// with zero lines, or a purchase order whose quantities are all null — puts a commercial record
/// into the system that nobody knows is empty, and the discrepancy check against the quotation
/// then passes because there is nothing to disagree with.</para>
/// </summary>
public sealed class CustomerPurchaseOrderDocumentException(string code, string message)
    : InvalidOperationException(message)
{
    /// <summary>Stable machine code the UI may branch on.</summary>
    public string Code { get; } = code;
}

public static class CustomerPurchaseOrderDocumentErrorCodes
{
    /// <summary>The file was read but no line-item table could be identified in it.</summary>
    public const string NoLineItems = "po_document_no_line_items";

    /// <summary>The file could not be decoded into text or rows at all.</summary>
    public const string Unreadable = "po_document_unreadable";
}

/// <summary>
/// Commits a reviewed extraction. The purchase order is created by the existing customer-award
/// application service — this command only adds the evidence link, so the commercial record can
/// always be traced back to the document it was read from (FR-COM-01).
/// </summary>
public sealed record CreateCustomerPurchaseOrderFromDocumentCommand(
    long SourceAttachmentId,
    CreateCustomerPurchaseOrderCommand PurchaseOrder);
