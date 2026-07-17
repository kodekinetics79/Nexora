using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Tenant isolation via EF Core global query filters (ADR-0005). Implemented in a
// partial so the large scaffolded context file stays untouched. Fail-closed:
// every authenticated request is transparently scoped to its business unit; a
// null tenant (login / anonymous / background worker) applies NO filter so those
// paths keep working. For legitimate cross-tenant reads (platform plane, worker
// sweeps) use .IgnoreQueryFilters().
public partial class ErpRfqAutomationContext
{
    // Set by the tenant-scoping constructor; null on the design-time /
    // parameterless / options-only paths.
    private readonly ITenantContext? _tenant;

    // Null when there is no tenant context -> filters become no-ops.
    private long? CurrentTenantId => _tenant?.BusinessUnitId;

    partial void OnModelCreatingPartial(ModelBuilder modelBuilder)
    {
        // Commercial documents (non-nullable BusinessUnitId).
        modelBuilder.Entity<Lead>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Rfq>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Quote>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Shipment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // Master data (nullable Buid). Rows with a null Buid are treated as shared
        // reference data (visible to all tenants); tenant-owned rows are scoped.
        modelBuilder.Entity<Customer>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Supplier>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<Product>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);

        // LeadItem.ExtraFields (partial property in LeadItem.Extra.cs): verbatim
        // unrecognized customer-document columns, stored as jsonb.
        modelBuilder.Entity<LeadItem>().Property(e => e.ExtraFields).HasColumnType("jsonb");

        // WP-A3 duplicate flag (partial properties in Lead.Duplicate.cs). Columns are
        // added by a lead-generated migration; see Deduplication/DEDUP-WIRING.md.
        modelBuilder.Entity<Lead>().Property(e => e.DuplicateStatus).HasMaxLength(20);
        modelBuilder.Entity<Lead>().Property(e => e.DuplicateResolvedBy).HasMaxLength(256);
        modelBuilder.Entity<Lead>().HasIndex(e => new { e.BusinessUnitId, e.DuplicateStatus })
            .HasDatabaseName("IX_Lead_BU_DuplicateStatus");

        // ==== Async extraction pipeline (ADR-0003) ====
        modelBuilder.Entity<ERP_RFQ_Automation.Extraction.ExtractionJob>(entity =>
        {
            entity.ToTable("ExtractionJobs");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.SourceType).HasConversion<string>().HasMaxLength(30).IsRequired();
            entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
            entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
            entity.Property(e => e.StoragePath).HasMaxLength(1024).IsRequired();
            entity.Property(e => e.FileName).HasMaxLength(512);
            entity.Property(e => e.FileType).HasMaxLength(50);
            entity.Property(e => e.LeasedBy).HasMaxLength(200);
            entity.Property(e => e.LastError).HasMaxLength(4000);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.UpdatedOn).HasDefaultValueSql("now()");
            entity.HasIndex(e => new { e.BusinessUnitId, e.ContentHash }).IsUnique().HasDatabaseName("UX_ExtractionJobs_BU_ContentHash");
            entity.HasIndex(e => new { e.Status, e.NextAttemptAt, e.Priority, e.SchedulerTag }).HasDatabaseName("IX_ExtractionJobs_Claim");
            entity.HasIndex(e => new { e.BusinessUnitId, e.Status }).HasDatabaseName("IX_ExtractionJobs_BU_Status");
            entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_ExtractionJobs_BatchId");
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Extraction.TenantQueueState>(entity =>
        {
            entity.ToTable("TenantQueueStates");
            entity.HasKey(e => e.BusinessUnitId);
            entity.Property(e => e.BusinessUnitId).ValueGeneratedNever();
            entity.Property(e => e.Weight).HasDefaultValue(1.0);
        });

        // ==== Platform-Owner control plane (ADR-0005) — non-tenant, "platform" schema ====
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.PlatformUser>(e =>
        {
            e.ToTable("PlatformUsers", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.Email).IsRequired().HasMaxLength(256);
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.PlatformRole).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.DisplayName).HasMaxLength(200);
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.Plan>(e =>
        {
            e.ToTable("Plans", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.Property(x => x.Code).IsRequired().HasMaxLength(64);
            e.Property(x => x.Name).IsRequired().HasMaxLength(128);
            e.Property(x => x.Features).HasColumnType("jsonb").HasDefaultValue("{}");
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.Tenant>(e =>
        {
            e.ToTable("Tenants", "platform");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Name).IsRequired().HasMaxLength(256);
            e.Property(x => x.Slug).IsRequired().HasMaxLength(64);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.StatusReason).HasMaxLength(1000);
            e.HasOne(x => x.Plan).WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<ERP_RFQ_Automation.Platform.Models.PlatformAuditLog>(e =>
        {
            e.ToTable("PlatformAuditLogs", "platform");
            e.HasKey(x => x.Id);
            e.Property(x => x.Action).IsRequired().HasMaxLength(128);
            e.Property(x => x.TargetType).HasMaxLength(128);
            e.Property(x => x.TargetId).HasMaxLength(128);
            e.Property(x => x.Metadata).HasColumnType("jsonb");
            e.Property(x => x.Ip).HasMaxLength(64);
            e.HasIndex(x => new { x.ActorPlatformUserId, x.CreatedOn });
            e.HasIndex(x => new { x.ActAsTenantId, x.CreatedOn });
        });

        // ==== Sourcing-copilot ("Agent") engine (Agent/) ====
        // Entity configuration + tenant query filters live in the NEW partial file
        // ErpRfqAutomationContext.Agent.cs; this single delegating call is the only
        // splice point (partial methods allow exactly one call site + implementation).
        ConfigureAgentModel(modelBuilder);

        // ==== SLA / deadline engine + quote outcome capture (Sla/) ====
        // Same partial-splice pattern; implementation in ErpRfqAutomationContext.Sla.cs.
        ConfigureSlaModel(modelBuilder);
    }
}
