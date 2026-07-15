using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class SetCountry
{
    public int CountryId { get; set; }

    public string CountryCode { get; set; } = null!;

    public string CountryName { get; set; } = null!;

    public string? Description { get; set; }

    public long Buid { get; set; }

    public bool IsActive { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedDate { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedDate { get; set; }

    public virtual BusinessUnit Bu { get; set; } = null!;

    public virtual ICollection<SetCity> SetCities { get; set; } = new List<SetCity>();

    public virtual ICollection<SetState> SetStates { get; set; } = new List<SetState>();

    public virtual ICollection<Supplier> Suppliers { get; set; } = new List<Supplier>();
}
