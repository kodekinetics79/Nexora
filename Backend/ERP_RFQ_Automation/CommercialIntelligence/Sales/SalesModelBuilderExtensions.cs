using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Sales;

public static class SalesModelBuilderExtensions
{
    public static ModelBuilder ApplyCommercialSalesModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SalesRepProfile>(entity =>
        {
            entity.ToTable("sales_rep_profiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.DistributionWeight).HasPrecision(10, 4);
            entity.Property(x => x.TerritoryKeys).HasColumnType("text[]");
            entity.Property(x => x.ProductCategoryKeys).HasColumnType("text[]");
            entity.Property(x => x.UpdatedBy).HasMaxLength(160).IsRequired();
            entity.Property(x => x.LastMutationIdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.UserId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.LastMutationIdempotencyKey }).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesTeamMembership>(entity =>
        {
            entity.ToTable("sales_team_memberships");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.UserId, x.TeamId, x.EffectiveToUtc });
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommercialActivity>(entity =>
        {
            entity.ToTable("commercial_activities");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ActivityType).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.AggregateType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.OutcomeCode).HasMaxLength(80);
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SalesRepUserId, x.OccurredAtUtc });
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.SalesRepUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FollowUpTask>(entity =>
        {
            entity.ToTable("follow_up_tasks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.AggregateType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.PurposeCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.CreationIdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CreationIdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.AssignedToUserId, x.DueAtUtc });
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.AssignedToUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FollowUpTransitionEvent>(entity =>
        {
            entity.ToTable("follow_up_transition_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne<FollowUpTask>().WithMany().HasForeignKey(x => x.FollowUpTaskId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SalesContribution>(entity =>
        {
            entity.ToTable("sales_contributions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ContributionType).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.AggregateType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ContributionPercent).HasPrecision(5, 2);
            entity.Property(x => x.RevenueAmount).HasPrecision(19, 4);
            entity.Property(x => x.CurrencyCode).HasMaxLength(3);
            entity.Property(x => x.EvidenceReference).HasMaxLength(500);
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.SalesRepUserId, x.RecognizedAtUtc });
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.SalesRepUserId).OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
