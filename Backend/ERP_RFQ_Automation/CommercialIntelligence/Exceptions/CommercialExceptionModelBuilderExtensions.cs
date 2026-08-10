using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialIntelligence.Exceptions;

public static class CommercialExceptionModelBuilderExtensions
{
    public static ModelBuilder ApplyCommercialExceptionModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommercialExceptionCase>(entity =>
        {
            entity.ToTable("commercial_exception_cases", table =>
            {
                // Gate 7 widened both of these for FR-DLM-07. Every branch now names ALL THREE
                // source columns — the two pre-existing branches gained "DeliveryProofLineId" IS
                // NULL — because a constraint that only mentions the columns it knew about at the
                // time silently permits the new one to be set alongside the old ones, and "which
                // source is this case really from" would stop having one answer.
                table.HasCheckConstraint("CK_commercial_exception_cases_Source",
                    "(\"ExceptionType\" = 'UnassignedLead' AND \"UnassignedWorkItemId\" IS NOT NULL AND \"FollowUpTaskId\" IS NULL AND \"DeliveryProofLineId\" IS NULL) "
                    + "OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"FollowUpTaskId\" IS NOT NULL AND \"UnassignedWorkItemId\" IS NULL AND \"DeliveryProofLineId\" IS NULL) "
                    + "OR (\"ExceptionType\" = 'DeliveryShortfall' AND \"DeliveryProofLineId\" IS NOT NULL AND \"FollowUpTaskId\" IS NULL AND \"UnassignedWorkItemId\" IS NULL)");
                table.HasCheckConstraint("CK_commercial_exception_cases_SourceIdentity",
                    "(\"ExceptionType\" = 'UnassignedLead' AND \"SourceType\" = 'UnassignedWorkItem' AND \"SourceId\" = \"UnassignedWorkItemId\") "
                    + "OR (\"ExceptionType\" = 'OverdueFollowUp' AND \"SourceType\" = 'FollowUpTask' AND \"SourceId\" = \"FollowUpTaskId\") "
                    + "OR (\"ExceptionType\" = 'DeliveryShortfall' AND \"SourceType\" = 'DeliveryProofLine' AND \"SourceId\" = \"DeliveryProofLineId\")");
                table.HasCheckConstraint("CK_commercial_exception_cases_SourceVersion", "\"SourceVersion\" >= 1");
                table.HasCheckConstraint("CK_commercial_exception_cases_Status",
                    "\"Status\" IN ('Open','Acknowledged','Resolved','Dismissed')");
                table.HasCheckConstraint("CK_commercial_exception_cases_Severity",
                    "\"Severity\" IN ('Low','Medium','High','Critical')");
                table.HasCheckConstraint("CK_commercial_exception_cases_Version", "\"Version\" >= 1");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.ExceptionType).HasConversion<string>().HasMaxLength(40);
            entity.Property(x => x.ExceptionKey).HasMaxLength(200).IsRequired();
            entity.Property(x => x.SourceType).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ReasonCode).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.Property(x => x.Summary).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.RecommendedActionCode).HasMaxLength(100).IsRequired();
            entity.Property(x => x.EvidenceJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.RuleVersion).HasMaxLength(40).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExceptionKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status, x.Severity, x.SlaDueAtUtc });
            entity.HasOne(x => x.CommercialCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialCaseId, x.NexoraSerial })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id, x.MasterReference })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<FollowUpTask>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.FollowUpTaskId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<UnassignedWorkItem>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.UnassignedWorkItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            // Gate 7 / FR-DLM-07. The foreign key is what makes a WRONG shortfall id impossible
            // rather than merely visible, and it is tenant-qualified for the same reason the other
            // two are.
            entity.HasOne<Delivery.DeliveryProofLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.DeliveryProofLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommercialExceptionEvent>(entity =>
        {
            entity.ToTable("commercial_exception_events", table =>
            {
                table.HasCheckConstraint("CK_commercial_exception_events_Status",
                    "(\"FromStatus\" IS NULL OR \"FromStatus\" IN ('Open','Acknowledged','Resolved','Dismissed')) AND \"ToStatus\" IN ('Open','Acknowledged','Resolved','Dismissed')");
                table.HasCheckConstraint("CK_commercial_exception_events_Version",
                    "\"FromVersion\" >= 0 AND \"ToVersion\" = \"FromVersion\" + 1");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.FromStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ToStatus).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ActionCode).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(160).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialExceptionCaseId, x.OccurredAtUtc });
            entity.HasOne(x => x.CommercialExceptionCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialExceptionCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommercialExceptionOutboxMessage>(entity =>
        {
            entity.ToTable("commercial_exception_outbox", table =>
                table.HasCheckConstraint("CK_commercial_exception_outbox_Attempts", "\"AttemptCount\" >= 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => x.CommercialExceptionEventId).IsUnique();
            entity.HasIndex(x => new { x.ProcessedAtUtc, x.AvailableAtUtc });
            entity.HasOne(x => x.CommercialExceptionEvent).WithOne(x => x.OutboxMessage)
                .HasForeignKey<CommercialExceptionOutboxMessage>(x => new { x.BusinessUnitId, x.CommercialExceptionEventId })
                .HasPrincipalKey<CommercialExceptionEvent>(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommercialExceptionOperation>(entity =>
        {
            entity.ToTable("commercial_exception_operations", table =>
            {
                table.HasCheckConstraint("CK_commercial_exception_operations_Type",
                    "\"OperationType\" IN ('Refresh','Transition')");
                table.HasCheckConstraint("CK_commercial_exception_operations_Case",
                    "(\"OperationType\" = 'Refresh' AND \"CommercialExceptionCaseId\" IS NULL) OR (\"OperationType\" = 'Transition' AND \"CommercialExceptionCaseId\" IS NOT NULL)");
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
            entity.HasIndex(x => new { x.BusinessUnitId, x.CommercialExceptionCaseId, x.OccurredAtUtc });
            entity.HasOne(x => x.CommercialExceptionCase).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialExceptionCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
