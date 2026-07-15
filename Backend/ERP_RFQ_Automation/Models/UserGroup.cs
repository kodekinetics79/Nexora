using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class UserGroup
{
    public long Id { get; set; }

    public string UserGroupsName { get; set; } = null!;

    public long BusinessUnitId { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
