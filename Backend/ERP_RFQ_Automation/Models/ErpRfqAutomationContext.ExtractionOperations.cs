using ERP_RFQ_Automation.Extraction;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

public partial class ErpRfqAutomationContext
{
    public DbSet<ExtractionDeadLetterEvent> ExtractionDeadLetterEvents => Set<ExtractionDeadLetterEvent>();

    private void ConfigureExtractionOperations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtractionDeadLetterEvent>(entity =>
        {
            entity.ToTable("extraction_dead_letter_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasConversion<string>().HasMaxLength(40).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ActorId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128).IsRequired();
            entity.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExtractionJobId, x.IdempotencyKey })
                .HasDatabaseName("UX_extraction_dead_letter_events_tenant_job_idempotency")
                .IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExtractionJobId, x.AttemptNumber, x.CreatedOn });
            entity.HasOne(x => x.ExtractionJob).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ExtractionJobId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
