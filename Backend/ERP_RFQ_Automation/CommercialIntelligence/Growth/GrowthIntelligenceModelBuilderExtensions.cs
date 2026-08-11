using System.Linq.Expressions;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Growth;

public static class GrowthIntelligenceModelBuilderExtensions
{
    public static ModelBuilder ApplyGrowthIntelligenceModel(this ModelBuilder modelBuilder,
        Expression<Func<SalesCoachingAcknowledgement, bool>> tenantFilter)
    {
        modelBuilder.Entity<SalesCoachingAcknowledgement>(entity =>
        {
            entity.ToTable("sales_coaching_acknowledgements", table =>
            {
                table.HasCheckConstraint("CK_sales_coaching_ack_decision",
                    "\"DecisionCode\" IN ('ACKNOWLEDGED','RESOLVED','DISMISSED')");
                table.HasCheckConstraint("CK_sales_coaching_ack_hashes",
                    "length(\"FindingKey\") = 64 AND length(\"RequestHash\") = 64");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.FindingKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FindingCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.DecisionCode).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.SourceAggregateType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SourceAggregateVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.EvidenceSnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.PolicyVersion).HasMaxLength(80).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            // THE OTHER HALF OF THE IDEMPOTENT-REPLAY DEFECT: the kind, not the precision.
            //
            // These columns are `timestamp without time zone`, which carries no offset, so Npgsql
            // materialises them as DateTimeKind.Unspecified. The create response serialises the
            // in-memory entity — Kind=Utc, so "...Z" — while the replay response serialises the row
            // just read back, with no "Z" at all. A consumer then resolves the replayed value
            // against its own zone: the same acknowledgement lands hours away from itself, which is
            // a far larger error than the microsecond that NormalizePostgreSqlTimestamp removes.
            //
            // Restoring the kind on materialisation makes a replayed acknowledgement byte-identical
            // to the create response, and does the same for the CreatedAtUtc that the coaching
            // workspace GET returns for an existing acknowledgement. The conversion is
            // DateTime -> DateTime, so the store type is unchanged, no stored value moves, and no
            // migration follows (PostgreSqlProductionDialectTests asserts exactly that).
            entity.Property(x => x.CreatedAtUtc)
                .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            entity.Property(x => x.FindingGeneratedAtUtc)
                .HasConversion(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.FindingKey, x.CreatedAtUtc });
            entity.HasIndex(x => new { x.BusinessUnitId, x.SalesRepUserId, x.CreatedAtUtc });
            entity.HasOne<BusinessUnit>().WithMany().HasForeignKey(x => x.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(tenantFilter);
        });

        return modelBuilder;
    }
}
