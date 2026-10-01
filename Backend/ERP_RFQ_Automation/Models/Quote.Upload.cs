namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The file behind a quote the rep made outside Nexora (by hand, in Excel, on the customer's
/// portal) and uploaded. All null on a quote Nexora produced.
///
/// <para>On the quote row rather than in <c>Attachments</c>: that table is polymorphic and its
/// row-level security admits only lead attachments, so a tenant could neither write nor read a
/// quote's file there. One quote carries one file, and <c>Quotes</c> is already tenant-scoped.</para>
/// </summary>
public partial class Quote
{
    /// <summary>The name the file was uploaded under, and the name it downloads as.</summary>
    public string? UploadedFileName { get; set; }

    /// <summary>Where the evidence store keeps the bytes (content-addressed, immutable).</summary>
    public string? UploadedFileStorageUri { get; set; }

    /// <summary>SHA-256 of the bytes, lowercase hex. Every read is verified against it.</summary>
    public string? UploadedFileSha256 { get; set; }

    /// <summary>The type inspection detected from the bytes, not the one the browser claimed.</summary>
    public string? UploadedFileContentType { get; set; }

    public long? UploadedFileSize { get; set; }
}
