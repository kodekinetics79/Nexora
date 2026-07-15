using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class RolePermission
{
    public long Id { get; set; }

    public long? RoleId { get; set; }

    public long ModuleId { get; set; }

    public long BusinessUnitId { get; set; }

    public bool? CanCreate { get; set; }

    public bool? CanEdit { get; set; }

    public bool? CanDelete { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual Module Module { get; set; } = null!;

    public virtual SetupMaster? Role { get; set; }
}
