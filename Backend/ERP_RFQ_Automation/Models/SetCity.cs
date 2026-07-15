using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class SetCity
{
    public int CityId { get; set; }

    public string CityName { get; set; } = null!;

    public int StateId { get; set; }

    public int CountryId { get; set; }

    public long Buid { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public virtual BusinessUnit Bu { get; set; } = null!;

    public virtual SetCountry Country { get; set; } = null!;

    public virtual SetState State { get; set; } = null!;

    public virtual ICollection<Supplier> Suppliers { get; set; } = new List<Supplier>();
}
