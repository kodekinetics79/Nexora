using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialCases.Lifecycle;

public static class LifecycleModelBuilderExtensions
{
    public static void ConfigureCommercialLifecycle(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommercialLifecycleEvent>(entity =>
        {
            entity.ToTable("commercial_lifecycle_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CommercialCaseReference).HasMaxLength(100).IsRequired();
            entity.Property(x => x.AggregateType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.EventType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PreviousStatusCode).HasMaxLength(50);
            entity.Property(x => x.NewStatusCode).HasMaxLength(50).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.ActorSource).HasMaxLength(50).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(100);
            entity.Property(x => x.ReasonNotes).HasMaxLength(1000);
            entity.Property(x => x.PolicyVersion).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Source).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.RequestReference).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId, x.OccurredOn });
            entity.HasIndex(x => new { x.BusinessUnitId, x.AggregateType, x.AggregateId, x.AggregateVersion }).IsUnique();
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasOne(x => x.BusinessUnit).WithMany().HasForeignKey(x => x.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CommercialCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId, x.CommercialCaseReference })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.MasterReference }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Models.SetupMaster>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.NewStatusId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.SetupId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Models.SetupMaster>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.PreviousStatusId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.SetupId }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LifecycleOutboxMessage>(entity =>
        {
            entity.ToTable("lifecycle_outbox_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.SchemaVersion).HasDefaultValue(1);
            entity.Property(x => x.LockedBy).HasMaxLength(200);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => x.LifecycleEventId).IsUnique();
            entity.HasIndex(x => new { x.AvailableOn, x.LockedUntil, x.OccurredOn, x.Id })
                .HasFilter("\"ProcessedOn\" IS NULL AND \"DeadLetteredOn\" IS NULL");
            entity.HasOne(x => x.BusinessUnit).WithMany().HasForeignKey(x => x.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.LifecycleEvent).WithOne(x => x.OutboxMessage)
                .HasForeignKey<LifecycleOutboxMessage>(x => new { x.BusinessUnitId, x.LifecycleEventId })
                .HasPrincipalKey<CommercialLifecycleEvent>(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ERP_RFQ_Automation.Models.Lead>()
            .Property(x => x.LifecycleVersion).IsConcurrencyToken().HasDefaultValue(1);
        modelBuilder.Entity<ERP_RFQ_Automation.Models.Rfq>()
            .Property(x => x.LifecycleVersion).IsConcurrencyToken().HasDefaultValue(1);
        modelBuilder.Entity<ERP_RFQ_Automation.Models.Quote>()
            .Property(x => x.LifecycleVersion).IsConcurrencyToken().HasDefaultValue(1);
        modelBuilder.Entity<ERP_RFQ_Automation.Models.CommercialCase>()
            .HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<ERP_RFQ_Automation.Models.CommercialCase>()
            .HasAlternateKey(x => new { x.BusinessUnitId, x.Id, x.MasterReference });
        modelBuilder.Entity<ERP_RFQ_Automation.Models.SetupMaster>()
            .HasAlternateKey(x => new { x.BusinessUnitId, x.SetupId });
    }
}
