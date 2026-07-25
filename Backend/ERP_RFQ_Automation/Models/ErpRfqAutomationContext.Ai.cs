using ERP_RFQ_Automation.AI;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public virtual DbSet<AiProcessingPolicy> AiProcessingPolicies { get; set; } = null!;
    public virtual DbSet<AiRequest> AiRequests { get; set; } = null!;
    public virtual DbSet<AiCallAttempt> AiCallAttempts { get; set; } = null!;
    public virtual DbSet<AiBudgetPeriod> AiBudgetPeriods { get; set; } = null!;

    private static void ConfigureAiGovernance(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AiProcessingPolicy>(entity =>
        {
            entity.ToTable("AiProcessingPolicies");
            entity.HasKey(e => e.BusinessUnitId);
            entity.Property(e => e.BusinessUnitId).ValueGeneratedNever();
            entity.Property(e => e.AllowedPurposes).HasMaxLength(500).IsRequired();
            entity.Property(e => e.AllowedProvider).HasMaxLength(100);
            entity.Property(e => e.AllowedModel).HasMaxLength(255);
            entity.Property(e => e.Version).HasDefaultValue(1L).IsConcurrencyToken();
            entity.Property(e => e.UpdatedBy).HasMaxLength(255).IsRequired();
            entity.HasOne<BusinessUnit>().WithOne()
                .HasForeignKey<AiProcessingPolicy>(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiRequest>(entity =>
        {
            entity.ToTable("AiRequests");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Operation).HasMaxLength(100).IsRequired();
            entity.Property(e => e.IdempotencyKey).HasMaxLength(255).IsRequired();
            entity.Property(e => e.PromptHash).HasMaxLength(64).IsFixedLength().IsRequired();
            entity.Property(e => e.PromptVersion).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Provider).HasMaxLength(100).IsRequired();
            entity.Property(e => e.ProviderClass).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(30).IsRequired();
            entity.Property(e => e.InputHash).HasMaxLength(64).IsFixedLength();
            entity.Property(e => e.OutputHash).HasMaxLength(64).IsFixedLength();
            entity.Property(e => e.TokenSource).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ErrorCode).HasMaxLength(100);
            entity.HasIndex(e => new { e.BusinessUnitId, e.IdempotencyKey }).IsUnique()
                .HasDatabaseName("UX_AiRequests_BU_IdempotencyKey");
            entity.HasIndex(e => new { e.BusinessUnitId, e.CreatedOn })
                .HasDatabaseName("IX_AiRequests_BU_CreatedOn");
            entity.HasIndex(e => new { e.BusinessUnitId, e.ProviderClass, e.CreatedOn });
            entity.HasIndex(e => e.CreatedOn)
                .HasDatabaseName("IX_AiRequests_Unresolved_CreatedOn")
                .HasFilter("\"CompletedOn\" IS NULL AND \"Status\" IN ('Reserved', 'Running')");
            entity.HasOne<BusinessUnit>().WithMany()
                .HasForeignKey(e => e.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiCallAttempt>(entity =>
        {
            entity.ToTable("AiCallAttempts");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Provider).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Model).HasMaxLength(255).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ProviderRequestId).HasMaxLength(255);
            entity.Property(e => e.TokenSource).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ResponseHash).HasMaxLength(64).IsFixedLength();
            entity.Property(e => e.ErrorCode).HasMaxLength(100);
            entity.HasIndex(e => new { e.RequestId, e.AttemptNumber }).IsUnique()
                .HasDatabaseName("UX_AiCallAttempts_Request_Attempt");
            entity.HasIndex(e => new { e.BusinessUnitId, e.StartedOn })
                .HasDatabaseName("IX_AiCallAttempts_BU_StartedOn");
            entity.HasOne(e => e.Request).WithMany(e => e.Attempts)
                .HasForeignKey(e => new { e.BusinessUnitId, e.RequestId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AiBudgetPeriod>(entity =>
        {
            entity.ToTable("AiBudgetPeriods");
            entity.HasKey(e => new { e.BusinessUnitId, e.PeriodStartUtc });
            entity.Property(e => e.Version).HasDefaultValue(1L).IsConcurrencyToken();
            entity.HasOne<BusinessUnit>().WithMany()
                .HasForeignKey(e => e.BusinessUnitId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
