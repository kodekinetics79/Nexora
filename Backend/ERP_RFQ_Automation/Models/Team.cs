using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class Team
{
    public long Id { get; set; }

    public string TeamName { get; set; } = null!;

    public long? SubTeamId { get; set; }

    public long? ManagerId { get; set; }

    public long BusinessUnitId { get; set; }

    public string CreatedBy { get; set; } = null!;

    public DateTime CreatedOn { get; set; }

    public string? ModifiedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<Team> InverseSubTeam { get; set; } = new List<Team>();

    public virtual User? Manager { get; set; }

    public virtual Team? SubTeam { get; set; }

    public virtual ICollection<User> Users { get; set; } = new List<User>();
}
