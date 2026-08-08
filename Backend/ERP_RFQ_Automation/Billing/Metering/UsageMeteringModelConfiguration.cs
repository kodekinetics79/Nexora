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
        });
    }
}
