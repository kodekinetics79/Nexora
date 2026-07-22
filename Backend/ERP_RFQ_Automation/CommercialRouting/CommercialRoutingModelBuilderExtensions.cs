using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialRouting;

public static class CommercialRoutingModelBuilderExtensions
{
    public static ModelBuilder ApplyCommercialRoutingModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<CustomerIdentifier>(entity =>
        {
            entity.ToTable("customer_identifiers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdentifierType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.NormalizedValue).HasMaxLength(320).IsRequired();
            entity.Property(x => x.DisplayValue).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdentifierType, x.NormalizedValue })
                .IsUnique()
                .HasFilter("\"EffectiveTo\" IS NULL");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId });
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerOwnership>(entity =>
        {
            entity.ToTable("customer_ownerships");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ScopeKey).HasMaxLength(160);
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.IsActive });
            entity.HasIndex(x => new { x.BusinessUnitId, x.Scope, x.ScopeKey });
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.PrimaryUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.BackupUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadRoutingDecision>(entity =>
        {
            entity.ToTable("lead_routing_decisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MatchStatus).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.MatchConfidence).HasPrecision(5, 4);
            entity.Property(x => x.DecisionCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Explanation).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.PolicyVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.CreatedOn });
            entity.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerIdentifier>().WithMany().HasForeignKey(x => x.MatchedIdentifierId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerOwnership>().WithMany().HasForeignKey(x => x.OwnershipId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadAssignment>(entity =>
        {
            entity.ToTable("lead_assignments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AssignmentScope).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ReasonCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Comment).HasMaxLength(1000);
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.EffectiveTo });
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId })
                .IsUnique()
                .HasFilter("\"EffectiveTo\" IS NULL");
            entity.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerOwnership>().WithMany().HasForeignKey(x => x.OwnershipId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.RoutingDecision).WithMany().HasForeignKey(x => x.RoutingDecisionId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ToUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UnassignedWorkItem>(entity =>
        {
            entity.ToTable("unassigned_work_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QueueType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ReasonCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.MatchConfidence).HasPrecision(5, 4);
            entity.Property(x => x.RequiredAction).HasMaxLength(300).IsRequired();
            entity.Property(x => x.ResolutionCode).HasMaxLength(80);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status, x.SlaDueOn });
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.Status });
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId })
                .IsUnique()
                .HasFilter("\"Status\" IN ('Open', 'Claimed')");
            entity.HasOne<Lead>().WithMany().HasForeignKey(x => x.LeadId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.RoutingDecision).WithMany().HasForeignKey(x => x.RoutingDecisionId).OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
