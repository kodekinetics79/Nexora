using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialRouting;

public static class CommercialRoutingModelBuilderExtensions
{
    public static ModelBuilder ApplyCommercialRoutingModel(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Lead>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
        modelBuilder.Entity<Lead>(entity =>
        {
            entity.Property(x => x.AssignmentMethod).HasMaxLength(16).HasDefaultValue(LeadAssignmentMethods.Automatic);
            entity.Property(x => x.ManualAssignmentOverride).HasDefaultValue(false);
            entity.Property(x => x.AssignmentVersion).HasDefaultValue(1L).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.AssignTo, x.AssignmentMethod })
                .HasDatabaseName("IX_Leads_BusinessUnitID_AssignTo_AssignmentMethod");
            entity.HasCheckConstraint("CK_Leads_AssignmentMethod", "\"AssignmentMethod\" IN ('AUTOMATIC','MANUAL')");
        });

        modelBuilder.Entity<CustomerIdentifier>(entity =>
        {
            entity.ToTable("customer_identifiers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.IdentifierType).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.NormalizedValue).HasMaxLength(320).IsRequired();
            entity.Property(x => x.DisplayValue).HasMaxLength(320).IsRequired();
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ObservationCount).HasDefaultValue(1);
            entity.HasIndex(x => new { x.BusinessUnitId, x.LearnedFromLeadId })
                .HasFilter("\"LearnedFromLeadId\" IS NOT NULL")
                .HasDatabaseName("IX_customer_identifiers_learned_from_lead");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdentifierType, x.NormalizedValue, x.CustomerId })
                .IsUnique()
                .HasFilter("\"EffectiveTo\" IS NULL");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdentifierType, x.NormalizedValue })
                .IsUnique()
                .HasFilter("\"EffectiveTo\" IS NULL AND \"IdentifierType\" IN ('ErpAccount', 'TaxRegistration', 'Email', 'Phone')")
                .HasDatabaseName("UX_customer_identifiers_authoritative");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId });
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasOne<Customer>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerId })
                .HasPrincipalKey(x => new { x.Buid, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerOwnership>(entity =>
        {
            entity.ToTable("customer_ownerships");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Scope).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ScopeKey).HasMaxLength(160);
            entity.Property(x => x.Source).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(500);
            entity.Property(x => x.MutationIdempotencyKey).HasMaxLength(160);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.IsActive });
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerId, x.Scope, x.ScopeKey })
                .IsUnique()
                .HasFilter("\"IsActive\" = TRUE AND \"EffectiveTo\" IS NULL")
                .HasDatabaseName("UX_customer_ownerships_single_active");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Scope, x.ScopeKey });
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasIndex(x => new { x.BusinessUnitId, x.MutationIdempotencyKey }).IsUnique()
                .HasFilter("\"MutationIdempotencyKey\" IS NOT NULL");
            entity.HasOne<Customer>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerId })
                .HasPrincipalKey(x => new { x.Buid, x.Id }).OnDelete(DeleteBehavior.Restrict);
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
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasOne<Lead>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Customer>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CustomerId })
                .HasPrincipalKey(x => new { x.Buid, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerIdentifier>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.MatchedIdentifierId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerOwnership>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OwnershipId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadAssignment>(entity =>
        {
            entity.ToTable("lead_assignments");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.AssignmentScope).HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.AssignmentMethod).HasMaxLength(16).HasDefaultValue(LeadAssignmentMethods.Automatic);
            entity.Property(x => x.IsManualOverride).HasDefaultValue(false);
            entity.Property(x => x.ReasonCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.Comment).HasMaxLength(1000);
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.EffectiveTo });
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.HasIndex(x => new { x.BusinessUnitId, x.LeadId })
                .IsUnique()
                .HasFilter("\"EffectiveTo\" IS NULL");
            entity.HasOne<Lead>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CustomerOwnership>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OwnershipId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.RoutingDecision).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.RoutingDecisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
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
            entity.HasOne<Lead>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.RoutingDecision).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.RoutingDecisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
