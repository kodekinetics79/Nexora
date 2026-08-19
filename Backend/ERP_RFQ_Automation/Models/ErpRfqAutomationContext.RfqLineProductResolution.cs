using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Audit columns for human resolution of a line's product. Configured in a partial so the large
// scaffolded context file stays untouched; ErpRfqAutomationContext.Tenancy.cs makes ONE
// delegating call to ConfigureRfqLineProductResolutionModel.
public partial class ErpRfqAutomationContext
{
    partial void ConfigureRfqLineProductResolutionModel(ModelBuilder modelBuilder);

    partial void ConfigureRfqLineProductResolutionModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Rfqitem>(entity =>
        {
            entity.Property(x => x.ProductResolvedBy).HasMaxLength(255);
            entity.Property(x => x.ProductResolutionReason).HasMaxLength(500);

            // An attributed resolution carries both an actor and a time, or neither. Enforced in
            // the database because the audit is the point: a half-written attribution is worse
            // than none, since it reads as a real one.
            entity.HasCheckConstraint("CK_Rfqitems_ProductResolution",
                "(\"ProductResolvedBy\" IS NULL AND \"ProductResolvedOn\" IS NULL) OR " +
                "(\"ProductResolvedBy\" IS NOT NULL AND trim(\"ProductResolvedBy\") <> '' " +
                "AND \"ProductResolvedOn\" IS NOT NULL)");
        });
    }
}
