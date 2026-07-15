using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class ProductSubCategory
{
    public int Id { get; set; }

    public string SubCategoryName { get; set; } = null!;

    public string? Description { get; set; }

    public long? BusinessUnitId { get; set; }

    public bool? IsActive { get; set; }

    public string? CreatedBy { get; set; }

    public DateTime? CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit? BusinessUnit { get; set; }

    public virtual ICollection<Product> Products { get; set; } = new List<Product>();
}
