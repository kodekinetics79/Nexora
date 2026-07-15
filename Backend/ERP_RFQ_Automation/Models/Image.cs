using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Image
{
    public long Id { get; set; }

    public string ResourceType { get; set; } = null!;

    public long ResourceId { get; set; }

    public string FileName { get; set; } = null!;

    public string FilePath { get; set; } = null!;

    public string? MimeType { get; set; }

    public string? Description { get; set; }

    public DateTime? UploadDate { get; set; }

    public long? UploadedBy { get; set; }

    public bool? IsPrimary { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }
}
