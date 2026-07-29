using ERP_RFQ_Automation.CommercialIntelligence.Opportunity;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<OpportunityRecommendation> OpportunityRecommendations => Set<OpportunityRecommendation>();
    public DbSet<OpportunityOutcome> OpportunityOutcomes => Set<OpportunityOutcome>();
    public DbSet<OpportunityFeedback> OpportunityFeedback => Set<OpportunityFeedback>();
    public DbSet<OpportunityEvent> OpportunityEvents => Set<OpportunityEvent>();
    public DbSet<OpportunityOutbox> OpportunityOutbox => Set<OpportunityOutbox>();
    public DbSet<OpportunityOperation> OpportunityOperations => Set<OpportunityOperation>();
}
