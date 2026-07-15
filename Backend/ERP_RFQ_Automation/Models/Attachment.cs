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
}
