using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class SetUom
{
    public int UomId { get; set; }

    public long BusinessUnitId { get; set; }

    public string UomCode { get; set; } = null!;

    public string UomName { get; set; } = null!;

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<OrderItem> OrderItems { get; set; } = new List<OrderItem>();

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();

    public virtual ICollection<Rfqitem> Rfqitems { get; set; } = new List<Rfqitem>();

    public virtual ICollection<SupplierQuotedItem> SupplierQuotedItems { get; set; } = new List<SupplierQuotedItem>();
}
