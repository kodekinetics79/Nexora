using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Attachment
{
    public long Id { get; set; }

    public string ParentType { get; set; } = null!;

    public long ParentId { get; set; }

    public string FileName { get; set; } = null!;

    public string FilePath { get; set; } = null!;

    public string? MimeType { get; set; }

    public long? FileSize { get; set; }

    public string? ContentType { get; set; }

    public DateTime CreatedOn { get; set; }

    public DateTime? UploadedDate { get; set; }

    /// <summary>
    /// FR-RFQ-08. SHA-256 of the stored bytes, lowercase hex.
    ///
    /// <para>The requirement is that the original document is retained as an IMMUTABLE
    /// attachment for audit. Until now this row was a bare file path: nothing recorded what the
    /// bytes were, so nothing could detect that they had changed, and "immutable" was an
    /// intention rather than a property anyone could check. With the digest recorded at the
    /// moment of capture, a reader can prove the bytes it serves are the bytes that arrived.</para>
    ///
    /// <para>Nullable because rows created before this column existed have no recorded digest.
    /// Absent means "unknown", which is honest; it must never be treated as "verified".</para>
    /// </summary>
    public string? ContentSha256 { get; set; }
}
