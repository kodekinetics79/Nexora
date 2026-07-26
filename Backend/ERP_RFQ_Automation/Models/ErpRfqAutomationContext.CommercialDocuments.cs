using ERP_RFQ_Automation.CommercialDocuments;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public virtual DbSet<CommercialDocumentClassification> CommercialDocumentClassifications { get; set; }
        = null!;

    // The Integration Owner invokes this hook from the shared OnModelCreatingPartial
    // so this lane does not compete for edits to the central tenant model.
    partial void ConfigureCommercialDocumentsModel(ModelBuilder modelBuilder);

    partial void ConfigureCommercialDocumentsModel(ModelBuilder modelBuilder)
    {
        if (!Database.IsNpgsql()) return;
        modelBuilder.AddCommercialDocuments();
        modelBuilder.Entity<CommercialDocumentClassification>()
            .HasQueryFilter(row => CurrentTenantId == null || row.BusinessUnitId == CurrentTenantId);
    }
}
