using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Billing.Metering;

public static class UsageMeteringModelConfiguration
{
    public static void Apply(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UsageEvent>(entity =>
        {
            entity.ToTable("UsageEvents", "platform");
            entity.HasKey(x => x.UsageEventId);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(20, 6);
            entity.Property(x => x.Unit).HasMaxLength(32).IsRequired();
            entity.Property(x => x.OccurredAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ReceivedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.SourceRecordType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SourceRecordId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.SourceSystem).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(256);
            entity.Property(x => x.Provider).HasMaxLength(128);
            entity.Property(x => x.Model).HasMaxLength(128);
            entity.Property(x => x.CorrelationId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CostAmount).HasPrecision(18, 6);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.EvidenceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.RatingStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.AllowanceApplied).HasPrecision(20, 6);
            entity.Property(x => x.OverageQuantity).HasPrecision(20, 6);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 8);
            entity.Property(x => x.RatedAmount).HasPrecision(18, 6);
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_UsageEvents_Tenant_IdempotencyKey");
            entity.HasAlternateKey(x => new { x.TenantId, x.UsageEventId });
            entity.HasIndex(x => new { x.TenantId, x.EventType, x.OccurredAtUtc });
            entity.HasIndex(x => x.AdjustsUsageEventId);
            entity.HasOne<UsageEvent>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.AdjustsUsageEventId })
                .HasPrincipalKey(x => new { x.TenantId, x.UsageEventId })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Platform.Models.Tenant>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Billing.RateCard>().WithMany()
                .HasForeignKey(x => x.RateCardId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Billing.RateCardLine>().WithMany()
                .HasForeignKey(x => x.RateCardLineId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UsageMinuteAggregate>(entity =>
        {
            entity.ToTable("UsageMinuteAggregates", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(20, 6);
            entity.Property(x => x.CostAmount).HasPrecision(18, 6);
            entity.Property(x => x.MinuteUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.RefreshedAtUtc).HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.TenantId, x.EventType, x.Unit, x.MinuteUtc }).IsUnique()
                .HasDatabaseName("UX_UsageMinuteAggregates_Bucket");
            entity.HasOne<ERP_RFQ_Automation.Platform.Models.Tenant>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantMeterSourcePolicy>(entity =>
        {
            entity.ToTable("TenantMeterSourcePolicies", "platform");
            entity.HasKey(x => new { x.TenantId, x.MeterKey });
            entity.Property(x => x.MeterKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Mode).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ProposedEffectiveAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CutoverAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ProposedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ApprovedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ProposedBy).HasMaxLength(256);
            entity.Property(x => x.ApprovedBy).HasMaxLength(256);
            entity.Property(x => x.ApprovalReason).HasMaxLength(1000);
            entity.Property(x => x.Version).HasDefaultValue(1L).IsConcurrencyToken();
            entity.HasOne<ERP_RFQ_Automation.Platform.Models.Tenant>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UsageCoverageSegment>(entity =>
        {
            entity.ToTable("UsageCoverageSegments", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MeterKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.StartUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.EndUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.AuthoritativeSource).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Completeness).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.QuantityTotal).HasPrecision(20, 6);
            entity.Property(x => x.AllowanceAppliedTotal).HasPrecision(20, 6);
            entity.Property(x => x.OverageQuantityTotal).HasPrecision(20, 6);
            entity.Property(x => x.RatedAmountTotal).HasPrecision(18, 6);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.RateLineageJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.RateLineageSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.EvidenceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(x => x.CompletenessWatermarkUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CutoverAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ReconciliationStatus).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.CounterpartQuantityTotal).HasPrecision(20, 6);
            entity.Property(x => x.CounterpartEvidenceSha256).HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.ApprovedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ApprovedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ApprovalReason).HasMaxLength(1000).IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.MeterKey, x.StartUtc, x.EndUtc }).IsUnique()
                .HasDatabaseName("UX_UsageCoverageSegments_Tenant_Meter_Range");
            entity.HasOne<ERP_RFQ_Automation.Platform.Models.Tenant>().WithMany()
                .HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UsageEventRating>(entity =>
        {
            entity.ToTable("UsageEventRatings", "platform");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(64);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.OccurredAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.RatedAtUtc).HasColumnType("timestamp with time zone");
            entity.Property(x => x.AllowanceApplied).HasPrecision(20, 6);
            entity.Property(x => x.OverageQuantity).HasPrecision(20, 6);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 8);
            entity.Property(x => x.RatedAmount).HasPrecision(18, 6);
            entity.Property(x => x.RatedBy).HasMaxLength(256).IsRequired();
            entity.Property(x => x.EvidenceSha256).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.HasIndex(x => new { x.TenantId, x.UsageEventId, x.AttemptNumber }).IsUnique()
                .HasDatabaseName("UX_UsageEventRatings_Event_Attempt");
            entity.HasIndex(x => new { x.TenantId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_UsageEventRatings_Tenant_Idempotency");
            entity.HasOne<UsageEvent>().WithMany()
                .HasForeignKey(x => new { x.TenantId, x.UsageEventId })
                .HasPrincipalKey(x => new { x.TenantId, x.UsageEventId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Billing.RateCard>().WithMany()
                .HasForeignKey(x => x.RateCardId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Billing.RateCardLine>().WithMany()
                .HasForeignKey(x => x.RateCardLineId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ERP_RFQ_Automation.Platform.Models.Plan>().WithMany()
                .HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
