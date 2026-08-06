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

    /// <summary>
    /// ING-08: UTC timestamp of the last poll that actually reached this mailbox, searched it
    /// and drained the window. THE authority for the lookback window — a failed cycle never
    /// advances it, so an outage is recoverable instead of a permanent hole. Null means this
    /// mailbox has never been polled successfully.
    /// </summary>
    public DateTime? LastSuccessfulPollOn { get; set; }

    /// <summary>UTC timestamp of the last poll ATTEMPT, successful or not. Together with
    /// <see cref="LastSuccessfulPollOn"/> it distinguishes "nobody is polling" from
    /// "polling and failing".</summary>
    public DateTime? LastPollAttemptOn { get; set; }

    /// <summary>Operator-readable reason the last poll failed; NULL when the last poll
    /// succeeded. This is the durable half of the truth that <c>/ready</c> reports.</summary>
    public string? LastPollError { get; set; }

    /// <summary>How many consecutive poll cycles have failed for this mailbox. Reset to 0 by
    /// the first success.</summary>
    public int ConsecutivePollFailures { get; set; }

    public virtual BusinessUnit BusinessUnit { get; set; } = null!;

    public virtual ICollection<EmailIngest> EmailIngests { get; set; } = new List<EmailIngest>();
}
