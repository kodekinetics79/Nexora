using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<SalesRepProfile> SalesRepProfiles => Set<SalesRepProfile>();
    public DbSet<SalesTeamMembership> SalesTeamMemberships => Set<SalesTeamMembership>();
    public DbSet<CommercialActivity> CommercialActivities => Set<CommercialActivity>();
    public DbSet<FollowUpTask> FollowUpTasks => Set<FollowUpTask>();
    public DbSet<FollowUpTransitionEvent> FollowUpTransitionEvents => Set<FollowUpTransitionEvent>();
    public DbSet<SalesContribution> SalesContributions => Set<SalesContribution>();
}
