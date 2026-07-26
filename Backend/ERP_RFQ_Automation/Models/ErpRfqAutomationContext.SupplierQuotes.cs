using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<SupplierQuote> SupplierQuotes => Set<SupplierQuote>();
    public DbSet<SupplierQuoteRevision> SupplierQuoteRevisions => Set<SupplierQuoteRevision>();
    public DbSet<SupplierQuoteLine> SupplierQuoteLines => Set<SupplierQuoteLine>();
    public DbSet<SupplierQuoteFieldEvidence> SupplierQuoteFieldEvidence => Set<SupplierQuoteFieldEvidence>();
    public DbSet<SupplierQuoteReviewDecision> SupplierQuoteReviewDecisions => Set<SupplierQuoteReviewDecision>();
    public DbSet<CustomerQuoteSourcingDecision> CustomerQuoteSourcingDecisions => Set<CustomerQuoteSourcingDecision>();

    partial void ConfigureSupplierQuotesModel(ModelBuilder modelBuilder);

    partial void ConfigureSupplierQuotesModel(ModelBuilder modelBuilder)
    {
        modelBuilder.AddSupplierQuotes();
        modelBuilder.Entity<SupplierQuote>()
            .HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SupplierQuoteRevision>()
            .HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SupplierQuoteLine>()
            .HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SupplierQuoteFieldEvidence>()
            .HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SupplierQuoteReviewDecision>()
            .HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerQuoteSourcingDecision>()
            .HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
    }
}
