using ERP_RFQ_Automation.CustomerResolution;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    /// <summary>
    /// Ranked machine proposals for a lead's client organisation. Read by the leads list,
    /// the lead detail panel and the resolve dialog; written only by
    /// <c>LeadCustomerResolutionService</c>.
    /// </summary>
    public DbSet<LeadCustomerMatchCandidate> LeadCustomerMatchCandidates => Set<LeadCustomerMatchCandidate>();
}
