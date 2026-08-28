namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Legal identity of the entity issuing tenant-owned commercial documents. It belongs on the
/// RLS-protected business unit so tenant workers never cross into the platform control plane.
/// </summary>
public partial class BusinessUnit
{
    public string? LegalName { get; set; }

    public string? CommercialRegistrationNumber { get; set; }
}
