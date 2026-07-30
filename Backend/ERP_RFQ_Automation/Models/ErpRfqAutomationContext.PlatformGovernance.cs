using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<GovernedArtifact> GovernedArtifacts => Set<GovernedArtifact>();
    public DbSet<GovernedArtifactVersion> GovernedArtifactVersions => Set<GovernedArtifactVersion>();
    public DbSet<GovernedArtifactEvent> GovernedArtifactEvents => Set<GovernedArtifactEvent>();
    public DbSet<HumanActionItem> HumanActionItems => Set<HumanActionItem>();
    public DbSet<HumanActionEvent> HumanActionEvents => Set<HumanActionEvent>();
    public DbSet<TenantGovernanceAuditEvent> TenantGovernanceAuditEvents => Set<TenantGovernanceAuditEvent>();

    partial void ConfigurePlatformGovernanceModel(ModelBuilder modelBuilder);

    partial void ConfigurePlatformGovernanceModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GovernedArtifact>(entity =>
        {
            entity.ToTable("governed_artifacts");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ArtifactType).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.ArtifactKey).HasMaxLength(120);
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.BusinessUnitId, x.ArtifactType, x.ArtifactKey }).IsUnique();
            entity.HasCheckConstraint("CK_governed_artifacts_versions",
                "\"CurrentVersionNumber\" > 0 AND \"Version\" > 0 AND (\"ProductionVersionNumber\" IS NULL OR \"ProductionVersionNumber\" > 0)");
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<GovernedArtifactVersion>(entity =>
        {
            entity.ToTable("governed_artifact_versions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.DefinitionJson).HasColumnType("jsonb");
            entity.Property(x => x.ChangeSummary).HasMaxLength(1000);
            entity.HasIndex(x => new { x.BusinessUnitId, x.GovernedArtifactId, x.VersionNumber }).IsUnique();
            entity.HasOne(x => x.Artifact).WithMany(x => x.Versions)
                .HasForeignKey(x => new { x.BusinessUnitId, x.GovernedArtifactId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<GovernedArtifactEvent>(entity =>
        {
            entity.ToTable("governed_artifact_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(32);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.Artifact).WithMany(x => x.Events)
                .HasForeignKey(x => new { x.BusinessUnitId, x.GovernedArtifactId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<HumanActionItem>(entity =>
        {
            entity.ToTable("human_action_items");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.ActionType).HasMaxLength(64);
            entity.Property(x => x.SourceType).HasMaxLength(64);
            entity.Property(x => x.SourceReference).HasMaxLength(200);
            entity.Property(x => x.Title).HasMaxLength(240);
            entity.Property(x => x.Summary).HasMaxLength(2000);
            entity.Property(x => x.Recommendation).HasMaxLength(2000);
            entity.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.CommercialImpact).HasMaxLength(1000);
            entity.Property(x => x.ResumeActionCode).HasMaxLength(80);
            entity.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status, x.Priority, x.DueOn });
            entity.HasCheckConstraint("CK_human_action_items_confidence",
                "\"Confidence\" >= 0 AND \"Confidence\" <= 1 AND \"Version\" > 0");
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<HumanActionEvent>(entity =>
        {
            entity.ToTable("human_action_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Action).HasMaxLength(32);
            entity.Property(x => x.Comment).HasMaxLength(2000);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.Item).WithMany(x => x.Events)
                .HasForeignKey(x => new { x.BusinessUnitId, x.HumanActionItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<TenantGovernanceAuditEvent>(entity =>
        {
            entity.ToTable("tenant_governance_audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Area).HasMaxLength(64);
            entity.Property(x => x.AggregateType).HasMaxLength(64);
            entity.Property(x => x.AggregateReference).HasMaxLength(200);
            entity.Property(x => x.Action).HasMaxLength(48);
            entity.Property(x => x.Reason).HasMaxLength(1000);
            entity.Property(x => x.EvidenceJson).HasColumnType("jsonb");
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160);
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.Area, x.OccurredOn });
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
