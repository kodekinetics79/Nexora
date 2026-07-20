using ERP_RFQ_Automation.Boq;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Service RFQ → BOQ engine entity configuration (Boq/), kept in a partial so the
// large scaffolded context file stays untouched — mirrors the Agent/Sla partial
// pattern. Invoked from ErpRfqAutomationContext.Tenancy.cs's OnModelCreatingPartial
// via a single delegating call to ConfigureBoqModel(modelBuilder).
//
// Every entity is tenant-scoped: `long BusinessUnitId` + the SAME fail-closed
// global filter as the Tenancy/Agent/Sla partials:
//   CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId
//
// Timestamps use `now()` server defaults, matching the agent/sla entities; the
// application code always sets them explicitly too (keeps SQLite tests honest).
// Tables/columns are listed for the lead's migration in Boq/BOQ-WIRING.md.
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial's
    // OnModelCreatingPartial. The implementing declaration below supplies the body.
    partial void ConfigureBoqModel(ModelBuilder modelBuilder);

    partial void ConfigureBoqModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BoqDocument>(e =>
        {
            e.ToTable("BoqDocuments");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(300).IsRequired();
            e.Property(x => x.ServiceCategory).HasMaxLength(30).IsRequired();
            e.Property(x => x.Status).HasMaxLength(20).IsRequired();
            e.Property(x => x.OverallConfidence).HasColumnType("numeric(5,2)");
            e.Property(x => x.Notes).HasMaxLength(4000);
            e.Property(x => x.AssumptionsJson).HasColumnType("jsonb");
            e.Property(x => x.TotalAmount).HasColumnType("numeric(18,2)");
            e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.ApprovedBy).HasMaxLength(256);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            // LeadId is a loose reference (no FK constraint) so lead lifecycle/cleanup
            // owned by other work packages never cascades into BOQ history.
            e.HasIndex(x => new { x.BusinessUnitId, x.Status }).HasDatabaseName("IX_BoqDocuments_BU_Status");
            e.HasIndex(x => new { x.BusinessUnitId, x.LeadId }).HasDatabaseName("IX_BoqDocuments_BU_Lead");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<BoqSection>(e =>
        {
            e.ToTable("BoqSections");
            e.HasKey(x => x.Id);
            e.Property(x => x.Title).HasMaxLength(200).IsRequired();
            e.Property(x => x.TotalAmount).HasColumnType("numeric(18,2)");
            e.HasOne(x => x.Document).WithMany(d => d.Sections)
                .HasForeignKey(x => x.BoqDocumentId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BoqDocumentId, x.Seq }).HasDatabaseName("IX_BoqSections_Doc_Seq");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<BoqItem>(e =>
        {
            e.ToTable("BoqItems");
            e.HasKey(x => x.Id);
            e.Property(x => x.ItemCode).HasMaxLength(64);
            e.Property(x => x.Description).HasMaxLength(2000).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.Quantity).HasColumnType("numeric(18,3)");
            e.Property(x => x.ItemType).HasMaxLength(20).IsRequired();
            e.Property(x => x.UnitRate).HasColumnType("numeric(18,4)");
            e.Property(x => x.TotalAmount).HasColumnType("numeric(18,2)");
            e.Property(x => x.Source).HasMaxLength(20).IsRequired();
            e.Property(x => x.Confidence).HasColumnType("numeric(5,2)");
            e.Property(x => x.AssemblyCode).HasMaxLength(64);
            e.Property(x => x.EvidenceNote).HasMaxLength(1000);
            e.HasOne(x => x.Section).WithMany(s => s.Items)
                .HasForeignKey(x => x.BoqSectionId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BoqSectionId, x.Seq }).HasDatabaseName("IX_BoqItems_Section_Seq");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<BoqAssembly>(e =>
        {
            e.ToTable("BoqAssemblies");
            e.HasKey(x => x.Id);
            e.Property(x => x.Code).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Description).HasMaxLength(1000);
            e.Property(x => x.ServiceCategory).HasMaxLength(30).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            // One code per tenant — the idempotent starter seed relies on this.
            e.HasIndex(x => new { x.BusinessUnitId, x.Code }).IsUnique().HasDatabaseName("UX_BoqAssemblies_BU_Code");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<BoqAssemblyComponent>(e =>
        {
            e.ToTable("BoqAssemblyComponents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Description).HasMaxLength(500).IsRequired();
            e.Property(x => x.Unit).HasMaxLength(20).IsRequired();
            e.Property(x => x.QtyPer).HasColumnType("numeric(18,4)");
            e.Property(x => x.ItemType).HasMaxLength(20).IsRequired();
            e.Property(x => x.DefaultRate).HasColumnType("numeric(18,4)");
            e.HasOne(x => x.Assembly).WithMany(a => a.Components)
                .HasForeignKey(x => x.BoqAssemblyId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.BoqAssemblyId, x.Seq }).HasDatabaseName("IX_BoqAssemblyComponents_Assembly_Seq");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
