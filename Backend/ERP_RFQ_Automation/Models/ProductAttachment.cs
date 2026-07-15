using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class ProductAttachment
{
    public long AttachmentId { get; set; }

    public long InventoryId { get; set; }

    public string FileName { get; set; } = null!;

    public string Locations { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime? UploadDate { get; set; }

    public long? UploadedBy { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual Product Inventory { get; set; } = null!;

    public virtual User? UploadedByNavigation { get; set; }
}
