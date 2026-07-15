using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Taxis
{
    public long Id { get; set; }

    public string TaxCode { get; set; } = null!;

    public string TaxName { get; set; } = null!;

    public decimal TaxRate { get; set; }

    public string TaxType { get; set; } = null!;

    public string? Country { get; set; }

    public string? State { get; set; }

    public bool? IsInclusive { get; set; }

    public bool? IsCompound { get; set; }

    public DateOnly EffectiveDate { get; set; }

    public DateOnly? ExpiryDate { get; set; }

    public long BusinessUnitId { get; set; }

    public bool? IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<Inventory> Inventories { get; set; } = new List<Inventory>();
}
