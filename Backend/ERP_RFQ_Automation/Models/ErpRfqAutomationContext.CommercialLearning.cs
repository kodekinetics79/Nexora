using ERP_RFQ_Automation.CommercialLearning;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<LearningGovernanceEvent> LearningGovernanceEvents => Set<LearningGovernanceEvent>();

    partial void ConfigureCommercialLearningModel(ModelBuilder modelBuilder);

    partial void ConfigureCommercialLearningModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LearningGovernanceEvent>(entity =>
        {
            entity.ToTable("learning_governance_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SignalId).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.Action).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.OccurredOn).HasDefaultValueSql("now()");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SignalId, x.Version }).IsUnique()
                .HasDatabaseName("UX_learning_governance_events_BU_Signal_Version");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_learning_governance_events_BU_Idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SignalId, x.OccurredOn })
                .HasDatabaseName("IX_learning_governance_events_BU_Signal_OccurredOn");
            entity.HasCheckConstraint("CK_learning_governance_events_Action",
                "\"Action\" IN ('APPROVED','DISABLED','ROLLED_BACK')");
            entity.HasCheckConstraint("CK_learning_governance_events_Version",
                "\"Version\" > 0 AND (\"Action\" = 'ROLLED_BACK') = (\"RevertsVersion\" IS NOT NULL)");
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
