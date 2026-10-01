using System;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// The client's own status on top of the fixed quote lifecycle (owner request 2026-09-28): a
/// customer step while the quote is SENT ("Technical evaluation"), or an ending once it closed
/// ("Partly won", counting as Won). Both are <see cref="SetupMaster"/> rows of the tenant —
/// SetupType <c>QuoteStep</c> or <c>QuoteEnding</c> — whose <c>ParentSetupId</c> names the fixed
/// status they belong to. The fixed <see cref="StatusId"/> still drives every rule, reminder and
/// dashboard; this is the client's word for where the quote is.
///
/// <para>Null when none was picked. A new revision starts blank (owner decision OD3).</para>
/// </summary>
public partial class Quote
{
    /// <summary>A <c>QuoteStep</c> row while SENT, a <c>QuoteEnding</c> row once an outcome is recorded.</summary>
    public long? SubStatusId { get; set; }

    /// <summary>When the step or ending was set.</summary>
    public DateTime? SubStatusOn { get; set; }
}
