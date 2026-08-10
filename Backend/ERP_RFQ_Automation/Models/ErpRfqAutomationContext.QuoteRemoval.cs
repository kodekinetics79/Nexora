using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Quote removal: the soft-removal columns on Quotes and the append-only QuoteRemovalRecords
// tombstone.
//
// Configured in a partial for the same reason as the PriceAttestation / QuoteValidity modules: the
// large scaffolded context file stays untouched, and ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial makes ONE delegating call to ConfigureQuoteRemovalModel.
//
// TENANT ISOLATION. QuoteRemovalRecord carries a non-nullable BusinessUnitId, an FK to
// BusinessUnits and the standard fail-closed global filter. Unlike the evidence tables it does NOT
// carry a composite FK to Quotes, on purpose: a discarded draft has no row left to reference, and
// the record of its discarding is the whole point of the table.
public partial class ErpRfqAutomationContext
{
    public DbSet<QuoteRemovalRecord> QuoteRemovalRecords => Set<QuoteRemovalRecord>();

    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigureQuoteRemovalModel(ModelBuilder modelBuilder);

    partial void ConfigureQuoteRemovalModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Quote>(entity =>
        {
            entity.Property(x => x.RemovedBy).HasMaxLength(255);
            entity.Property(x => x.RemovalReason).HasMaxLength(500);

            // A withdrawal is a reason-bearing state change or it is nothing. The three columns
            // move together, and a blank reason is refused by the database, not only by the
            // service — the same standard R7 already holds validity extensions to.
            entity.HasCheckConstraint("CK_Quotes_Removal",
                "(\"RemovedOn\" IS NULL AND \"RemovedBy\" IS NULL AND \"RemovalReason\" IS NULL) OR " +
                "(\"RemovedOn\" IS NOT NULL AND \"RemovedBy\" IS NOT NULL AND trim(\"RemovedBy\") <> '' " +
                "AND \"RemovalReason\" IS NOT NULL AND trim(\"RemovalReason\") <> '')");

            // The list and the stats both read "live quotes in this tenant".
            entity.HasIndex(x => new { x.BusinessUnitId, x.RemovedOn })
                .HasDatabaseName("IX_Quotes_BU_RemovedOn");
        });

        modelBuilder.Entity<QuoteRemovalRecord>(entity =>
        {
            entity.ToTable("QuoteRemovalRecords");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.QuoteNo).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Mode).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.RemovedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.StatusCode).HasMaxLength(100);
            entity.Property(x => x.TotalAmount).HasColumnType("decimal(18, 6)");

            entity.HasCheckConstraint("CK_QuoteRemovalRecords_Mode",
                "\"Mode\" IN ('DRAFT_DISCARDED','WITHDRAWN')");
            entity.HasCheckConstraint("CK_QuoteRemovalRecords_Reason", "trim(\"Reason\") <> ''");
            entity.HasCheckConstraint("CK_QuoteRemovalRecords_RemovedBy", "trim(\"RemovedBy\") <> ''");

            entity.HasIndex(x => new { x.BusinessUnitId, x.RemovedOn })
                .HasDatabaseName("IX_QuoteRemovalRecords_BU_RemovedOn");
            entity.HasIndex(x => new { x.BusinessUnitId, x.QuoteId })
                .HasDatabaseName("IX_QuoteRemovalRecords_BU_Quote");

            entity.HasOne(x => x.BusinessUnit).WithMany()
                .HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
