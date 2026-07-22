using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
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
        if (Database.IsNpgsql())
            modelBuilder.HasSequence<long>("CommercialCaseReferenceSequence");

        // Commercial documents (non-nullable BusinessUnitId).
        modelBuilder.Entity<Lead>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Rfq>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Quote>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Order>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<Shipment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CommercialCase>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadReferenceConfiguration>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadStatusHistory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<SetupMaster>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<RolePermission>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<User>().HasQueryFilter(e => CurrentTenantId == null || e.Buid == null || e.Buid == CurrentTenantId);
        modelBuilder.Entity<CommercialLifecycleEvent>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LifecycleOutboxMessage>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

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

        // WP-BOQ foundation: inquiry classification (partial property in
        // Lead.Inquiry.cs). Column ("InquiryType" varchar(16) NULL) is added by a
        // lead-generated migration, same pattern as the duplicate-flag columns above.
        modelBuilder.Entity<Lead>().Property(e => e.InquiryType).HasMaxLength(16);

        // Routing uses portable relational constructs and is also enabled for the
        // SQLite integration suite. The evidence/custom-field modules retain their
        // PostgreSQL-only model because their constraints use jsonb and PostgreSQL
        // expressions.
        modelBuilder.ApplyCommercialRoutingModel();
        modelBuilder.Entity<CustomerIdentifier>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomerOwnership>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadRoutingDecision>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<LeadAssignment>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<UnassignedWorkItem>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        modelBuilder.ConfigureGovernedCustomFields();
        modelBuilder.ConfigureCommercialLifecycle();
        modelBuilder.Entity<CustomFieldDefinition>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldVersion>().HasQueryFilter(e => CurrentTenantId == null || e.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldOption>().HasQueryFilter(e => CurrentTenantId == null || e.Version.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldRule>().HasQueryFilter(e => CurrentTenantId == null || e.Version.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldDependency>().HasQueryFilter(e => CurrentTenantId == null || e.Version.Definition.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldRecord>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldValue>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        modelBuilder.Entity<CustomFieldValueHistory>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        // PostgreSQL-backed enterprise foundations.
        if (Database.IsNpgsql())
        {
            modelBuilder.AddEvidenceLedger();

            modelBuilder.Entity<DocumentCorpus>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<SourceDocument>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<DocumentPage>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<DocumentRegion>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<CanonicalInquiry>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<CanonicalLineItem>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
            modelBuilder.Entity<FieldEvidence>().HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

        }

        // Permanent tenant-scoped commercial-case identity. PostgreSQL assigns the
        // value through the migration trigger; ValueGeneratedOnAdd makes EF read the
        // generated value back with the INSERT result.
        modelBuilder.Entity<Lead>().Property(e => e.CommercialCaseReference)
            .HasMaxLength(100)
            .IsRequired()
            .ValueGeneratedOnAdd();
        var commercialCaseId = modelBuilder.Entity<Lead>().Property(e => e.CommercialCaseId);
        if (Database.IsNpgsql())
            commercialCaseId.ValueGeneratedOnAdd();
        modelBuilder.Entity<Lead>().HasIndex(e => new { e.BusinessUnitId, e.CommercialCaseReference })
            .IsUnique()
            .HasDatabaseName("UX_Leads_BU_CommercialCaseReference");
        modelBuilder.Entity<Lead>().HasIndex(e => e.CommercialCaseId).IsUnique()
            .HasDatabaseName("UX_Leads_CommercialCaseID");

        modelBuilder.Entity<CommercialCase>(entity =>
        {
            entity.ToTable("CommercialCases");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            var allocationNumber = entity.Property(e => e.AllocationNumber).ValueGeneratedOnAdd();
            if (Database.IsNpgsql())
                allocationNumber.HasDefaultValueSql("nextval('\"CommercialCaseReferenceSequence\"')");
            entity.Property(e => e.MasterReference).HasMaxLength(100).IsRequired().ValueGeneratedOnAdd();
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.CreatedBy).HasMaxLength(255).IsRequired();
            entity.HasIndex(e => e.AllocationNumber).IsUnique()
                .HasDatabaseName("UX_CommercialCases_AllocationNumber");
            entity.HasIndex(e => new { e.BusinessUnitId, e.MasterReference }).IsUnique()
                .HasDatabaseName("UX_CommercialCases_BU_MasterReference");
            entity.HasOne(e => e.BusinessUnit).WithMany(e => e.CommercialCases)
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.Lead).WithOne(e => e.CommercialCase)
                .HasForeignKey<Lead>(e => e.CommercialCaseId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadReferenceConfiguration>(entity =>
        {
            entity.ToTable("LeadReferenceConfigurations");
            entity.HasKey(e => e.BusinessUnitId);
            entity.Property(e => e.BusinessUnitId).ValueGeneratedNever().HasColumnName("BusinessUnitID");
            entity.Property(e => e.Prefix).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Format).HasMaxLength(100).IsRequired();
            entity.Property(e => e.SequencePadding).HasDefaultValue(6);
            entity.Property(e => e.FinancialYearStartMonth).HasDefaultValue(1);
            entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
            entity.HasOne(e => e.BusinessUnit).WithOne(e => e.LeadReferenceConfiguration)
                .HasForeignKey<LeadReferenceConfiguration>(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LeadStatusHistory>(entity =>
        {
            entity.ToTable("LeadStatusHistories");
            entity.HasKey(e => e.Id);
            entity.Property(e => e.BusinessUnitId).HasColumnName("BusinessUnitID");
            entity.Property(e => e.LeadId).HasColumnName("LeadID");
            entity.Property(e => e.CommercialCaseId).HasColumnName("CommercialCaseID");
            entity.Property(e => e.PreviousStatusId).HasColumnName("PreviousStatusID");
            entity.Property(e => e.NewStatusId).HasColumnName("NewStatusID");
            entity.Property(e => e.EventType).HasMaxLength(30).IsRequired();
            entity.Property(e => e.ChangedBy).HasMaxLength(255);
            entity.Property(e => e.ActorSource).HasMaxLength(50).IsRequired();
            entity.Property(e => e.ChangedOn).HasDefaultValueSql("now()");
            entity.Property(e => e.Reason).HasMaxLength(1000);
            entity.Property(e => e.CommercialCaseReference).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => new { e.BusinessUnitId, e.LeadId, e.ChangedOn })
                .HasDatabaseName("IX_LeadStatusHistory_BU_Lead_ChangedOn");
            entity.HasOne(e => e.Lead).WithMany(e => e.StatusHistory)
                .HasForeignKey(e => e.LeadId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.CommercialCase).WithMany(e => e.LeadStatusHistory)
                .HasForeignKey(e => e.CommercialCaseId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(e => e.BusinessUnit).WithMany(e => e.LeadStatusHistories)
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);
        });

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

        // ==== Passive AI metrics + quote revisions (WP-B4, Metrics/) ====
        // Same partial-splice pattern; implementation in ErpRfqAutomationContext.Metrics.cs.
        ConfigureMetricsModel(modelBuilder);

        // ==== Service RFQ → BOQ engine (Boq/) ====
        // Same partial-splice pattern; implementation in ErpRfqAutomationContext.Boq.cs.
        ConfigureBoqModel(modelBuilder);
    }
}
