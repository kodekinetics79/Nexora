using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class InventoryCategory
{
    public long Id { get; set; }

    public string CategoryName { get; set; } = null!;

    public string? Description { get; set; }

    public long? ParentCategoryId { get; set; }

    public long BusinessUnitId { get; set; }

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<Inventory> Inventories { get; set; } = new List<Inventory>();

    public virtual ICollection<InventoryCategory> InverseParentCategory { get; set; } = new List<InventoryCategory>();

    public virtual InventoryCategory? ParentCategory { get; set; }
}
