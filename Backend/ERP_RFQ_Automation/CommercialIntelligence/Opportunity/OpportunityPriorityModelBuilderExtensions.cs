using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Opportunity;

public static class OpportunityPriorityModelBuilderExtensions
{
    public static ModelBuilder ApplyOpportunityPriorityModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Lead>().HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

        modelBuilder.Entity<OpportunityRecommendation>(entity =>
        {
            entity.ToTable("commercial_opportunity_recommendations", table =>
            {
                table.HasCheckConstraint("CK_opportunity_recommendations_Mode", "\"Mode\" = 'Shadow'");
                table.HasCheckConstraint("CK_opportunity_recommendations_Score", "\"PriorityScore\" BETWEEN 0 AND 100");
                table.HasCheckConstraint("CK_opportunity_recommendations_Confidence", "\"Confidence\" BETWEEN 0 AND 1");
                table.HasCheckConstraint("CK_opportunity_recommendations_Completeness", "\"Completeness\" BETWEEN 0 AND 1");
                table.HasCheckConstraint("CK_opportunity_recommendations_SampleSize", "\"SampleSize\" >= 0");
                table.HasCheckConstraint("CK_opportunity_recommendations_EvidenceHash", "length(\"EvidenceHash\") = 64");
                table.HasCheckConstraint("CK_opportunity_recommendations_Generated", "\"GeneratedAtUtc\" >= \"EvidenceCutoffAtUtc\"");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.RecommendationKey).HasMaxLength(180).IsRequired();
            entity.Property(x => x.PolicyVersion).HasMaxLength(60).IsRequired();
            entity.Property(x => x.FeatureSchemaVersion).HasMaxLength(60).IsRequired();
            entity.Property(x => x.EvidenceSnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.EvidenceHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PriorityBand).HasMaxLength(30).IsRequired();
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.Completeness).HasPrecision(5, 4);
            entity.Property(x => x.RecommendedActionCode).HasMaxLength(80).IsRequired();
            entity.Property(x => x.RecommendedActionLabel).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RationaleJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.CohortKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Mode).HasMaxLength(20).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.RecommendationKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId, x.PolicyVersion, x.EvidenceHash }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.GeneratedAtUtc, x.PriorityScore });
            entity.HasOne(x => x.CommercialCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId, x.NexoraSerial })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.MasterReference })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Lead).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LeadId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SupersedesRecommendation).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupersedesRecommendationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpportunityOutcome>(entity =>
        {
            entity.ToTable("commercial_opportunity_outcomes", table =>
            {
                table.HasCheckConstraint("CK_opportunity_outcomes_Code",
                    "\"OutcomeCode\" IN ('ORDER_CREATED','QUOTE_WON','QUOTE_LOST','QUOTE_EXPIRED')");
                table.HasCheckConstraint("CK_opportunity_outcomes_Source", "\"SourceType\" IN ('Order','Quote')");
                table.HasCheckConstraint("CK_opportunity_outcomes_SourceVersion", "\"SourceVersion\" >= 1");
                table.HasCheckConstraint("CK_opportunity_outcomes_EvidenceHash", "length(\"EvidenceHash\") = 64");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.OutcomeCode).HasMaxLength(40).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.EvidenceJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.EvidenceHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.OpportunityRecommendationId, x.SourceType, x.SourceId, x.SourceVersion, x.OutcomeCode }).IsUnique();
            entity.HasOne(x => x.Recommendation).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OpportunityRecommendationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpportunityFeedback>(entity =>
        {
            entity.ToTable("commercial_opportunity_feedback", table =>
            {
                table.HasCheckConstraint("CK_opportunity_feedback_Decision",
                    "\"Decision\" IN ('Accepted','Rejected','Replaced','Deferred','Reverted')");
                table.HasCheckConstraint("CK_opportunity_feedback_Replacement",
                    "(\"Decision\" = 'Replaced' AND \"ReplacementActionCode\" IS NOT NULL) OR (\"Decision\" <> 'Replaced' AND \"ReplacementActionCode\" IS NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.Decision).HasMaxLength(20).IsRequired();
            entity.Property(x => x.ReplacementActionCode).HasMaxLength(80);
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.OpportunityRecommendationId, x.OccurredAtUtc });
            entity.HasOne(x => x.Recommendation).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OpportunityRecommendationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SupersedesFeedback).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupersedesFeedbackId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpportunityEvent>(entity =>
        {
            entity.ToTable("commercial_opportunity_events", table =>
                table.HasCheckConstraint("CK_opportunity_events_RequestHash", "length(\"RequestHash\") = 64"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.OpportunityRecommendationId, x.OccurredAtUtc });
            entity.HasOne(x => x.Recommendation).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OpportunityRecommendationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpportunityOutbox>(entity =>
        {
            entity.ToTable("commercial_opportunity_outbox", table =>
                table.HasCheckConstraint("CK_opportunity_outbox_Attempts", "\"AttemptCount\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => new { x.BusinessUnitId, x.OpportunityEventId }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ProcessedAtUtc, x.AvailableAtUtc });
            entity.HasOne(x => x.Event).WithOne(x => x.OutboxMessage)
                .HasForeignKey<OpportunityOutbox>(x => new { x.BusinessUnitId, x.OpportunityEventId })
                .HasPrincipalKey<OpportunityEvent>(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OpportunityOperation>(entity =>
        {
            entity.ToTable("commercial_opportunity_operations", table =>
            {
                table.HasCheckConstraint("CK_opportunity_operations_Type", "\"OperationType\" IN ('Reconcile','Feedback')");
                table.HasCheckConstraint("CK_opportunity_operations_RequestHash", "length(\"RequestHash\") = 64");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.OperationType).HasMaxLength(20).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.ResultJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialCaseId, x.OccurredAtUtc });
            entity.HasOne(x => x.Recommendation).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.OpportunityRecommendationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
