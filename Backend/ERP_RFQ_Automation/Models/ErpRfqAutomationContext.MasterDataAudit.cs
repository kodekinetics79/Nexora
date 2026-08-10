using ERP_RFQ_Automation.MasterData;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// FR-MDM-05 / register item E44 — the master-data before/after audit trail.
//
// Configured in a partial for the same reason as the SLA, PriceAttestation and QuoteValidity
// modules: the large scaffolded context file stays untouched and the Tenancy partial's
// OnModelCreatingPartial makes ONE delegating call to ConfigureMasterDataAuditModel.
//
// TENANT ISOLATION. Both tables carry a non-nullable BusinessUnitId, the standard fail-closed
// global filter, and a real foreign key to BusinessUnits. The query filter is not just a read
// guard: it auto-enrols both tables in
// PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase, which enumerates
// every filtered entity and FAILS unless a matching nexora_tenant_isolation RLS policy exists with
// both polqual and polwithcheck referencing nexora.business_unit_id. That test is the reason the
// migration MUST ship the policy and the GRANT together — the schema is deny-by-default, so a
// policy without a grant raises 42501 before any row predicate is evaluated.
public partial class ErpRfqAutomationContext
{
    /// <summary>Append-only master-data change headers (FR-MDM-05). PostgreSQL additionally blocks
    /// UPDATE/DELETE with <c>trg_master_data_audit_append_only</c>, so even direct SQL under the
    /// tenant role cannot rewrite the trail.</summary>
    public DbSet<MasterDataChangeEvent> MasterDataChangeEvents => Set<MasterDataChangeEvent>();

    /// <summary>One row per field that moved. This is the table FR-MDM-05's "before/after values"
    /// clause actually names.</summary>
    public DbSet<MasterDataFieldChange> MasterDataFieldChanges => Set<MasterDataFieldChange>();

    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigureMasterDataAuditModel(ModelBuilder modelBuilder);

    partial void ConfigureMasterDataAuditModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MasterDataChangeEvent>(entity =>
        {
            entity.ToTable("MasterDataChangeEvents");
            entity.HasKey(e => e.Id);
            // Composite alternate key so the field rows can be tied to their header BY TENANT.
            // A single-column FK would let a field change be attached to another tenant's header
            // by primary key alone — the same reasoning as QuotePriceAttestations.
            entity.HasAlternateKey(e => new { e.BusinessUnitId, e.Id });

            entity.Property(e => e.EntityType).HasMaxLength(32).IsRequired();
            entity.Property(e => e.EntityLabel).HasMaxLength(256);
            entity.Property(e => e.ChangeType).HasMaxLength(16).IsRequired();
            entity.Property(e => e.Actor).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ChangeSource).HasMaxLength(16).IsRequired();
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.Property(e => e.Reason).HasMaxLength(512);
            entity.Property(e => e.OccurredOn).HasDefaultValueSql("now()");

            // The record-history read: "every change to this product, newest first."
            entity.HasIndex(e => new { e.BusinessUnitId, e.EntityType, e.EntityId, e.OccurredOn })
                .HasDatabaseName("IX_MasterDataChangeEvents_BU_Entity_OccurredOn");
            // The review read: "everything that changed in this tenant this week."
            entity.HasIndex(e => new { e.BusinessUnitId, e.OccurredOn })
                .HasDatabaseName("IX_MasterDataChangeEvents_BU_OccurredOn");

            entity.HasCheckConstraint(
                "CK_MasterDataChangeEvents_EntityType",
                "\"EntityType\" IN ('Customer', 'Supplier', 'Product')");
            entity.HasCheckConstraint(
                "CK_MasterDataChangeEvents_ChangeType",
                "\"ChangeType\" IN ('CREATED', 'UPDATED', 'DELETED')");

            // ActorUserId and EntityId deliberately carry NO foreign key: the record of who
            // deleted a product must survive that product's deletion, and the record of who
            // changed it must survive the actor's. BusinessUnitId does, because a tenant-less
            // audit row is unattributable and falls outside the RLS policy — the identical
            // trade-off IamAuditEvent documents.
            entity.HasOne<BusinessUnit>()
                .WithMany()
                .HasForeignKey(e => e.BusinessUnitId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<MasterDataFieldChange>(entity =>
        {
            entity.ToTable("MasterDataFieldChanges");
            entity.HasKey(e => e.Id);

            entity.Property(e => e.FieldName).HasMaxLength(128).IsRequired();
            entity.Property(e => e.BeforeValue).HasMaxLength(4000);
            entity.Property(e => e.AfterValue).HasMaxLength(4000);
            entity.Property(e => e.Sensitivity).HasMaxLength(16);

            // "Who has touched landed cost, ever" is the query this control exists to answer, and
            // it must not be a sequential scan of the whole trail.
            entity.HasIndex(e => new { e.BusinessUnitId, e.FieldName })
                .HasDatabaseName("IX_MasterDataFieldChanges_BU_FieldName");
            entity.HasIndex(e => new { e.BusinessUnitId, e.ChangeEventId })
                .HasDatabaseName("IX_MasterDataFieldChanges_BU_ChangeEvent");

            // CASCADE is correct here and only here: a field row is part of its header, not an
            // independent record, and the header itself can never be deleted (append-only
            // trigger). The cascade therefore describes an unreachable state and exists so the
            // relationship is fully specified rather than left to EF's default.
            entity.HasOne(e => e.ChangeEvent)
                .WithMany(e => e.Fields)
                .HasForeignKey(e => new { e.BusinessUnitId, e.ChangeEventId })
                .HasPrincipalKey(e => new { e.BusinessUnitId, e.Id })
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasQueryFilter(e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);
        });
    }
}
