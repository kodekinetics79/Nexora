using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.Models;

public partial class EmailConfiguration
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public string ConfigurationName { get; set; } = null!;

    public string EmailAddress { get; set; } = null!;

    public string Protocol { get; set; } = null!;

    public string Host { get; set; } = null!;

    public int Port { get; set; }

    public string Username { get; set; } = null!;

    public string Password { get; set; } = null!;

    public bool UseSsl { get; set; }

    public int PollingInterval { get; set; }

    public bool IsActive { get; set; }

    public DateTime CreatedOn { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<EmailIngest> EmailIngests { get; set; } = new List<EmailIngest>();
}
