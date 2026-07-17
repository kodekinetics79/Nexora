using ERP_RFQ_Automation.Sla;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// SLA / deadline-engine entity configuration (WP-A2) plus the Quote outcome
// columns (WP-A4), kept in a partial so the large scaffolded context file stays
// untouched — mirrors the Agent partial pattern. Invoked from
// ErpRfqAutomationContext.Tenancy.cs's OnModelCreatingPartial via a single
// delegating call to ConfigureSlaModel(modelBuilder).
//
// Tenant-scoped entities carry `long BusinessUnitId` and use the SAME
// fail-closed global filter pattern as the Tenancy/Agent partials:
//   CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId
// The sweep worker runs without a tenant context, so the filters become no-ops
// there and every worker query is explicitly BU-scoped instead.
//
// Timestamps use `now()` server defaults, matching the extraction/agent entities.
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial's
    // OnModelCreatingPartial. The implementing declaration below supplies the body.
    partial void ConfigureSlaModel(ModelBuilder modelBuilder);

    partial void ConfigureSlaModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SlaPolicy>(e =>
        {
            e.ToTable("SlaPolicies");
            e.HasKey(x => x.Id);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.Property(x => x.UpdatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => x.BusinessUnitId).IsUnique().HasDatabaseName("UX_SlaPolicies_BU");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<SlaEvent>(e =>
        {
            e.ToTable("SlaEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.EntityType).HasMaxLength(40).IsRequired();
            e.Property(x => x.Level).HasMaxLength(20).IsRequired();
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            // Dedup lookups always hit (BU, EntityType, EntityId, Level); append-only,
            // so no unique constraint — the worker does lookup-before-insert and the
            // daily digest key intentionally allows one row per day.
            e.HasIndex(x => new { x.BusinessUnitId, x.EntityType, x.EntityId, x.Level })
                .HasDatabaseName("IX_SlaEvents_BU_Entity_Level");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        // ==== Quote outcome-capture columns (WP-A4, Models/Quote.Outcome.cs) ====
        // OutcomeReasonId is a loose FK to SetupMaster.SetupId (SetupType
        // "QuoteOutcomeReason"): mapped as a plain nullable bigint with NO
        // navigation/constraint so the scaffolded SetupMaster entity and its
        // relationship web stay untouched. Reason names are batch-resolved in the
        // repositories. SentOn/RespondedOn/OutcomeOn are plain nullable timestamps.
        modelBuilder.Entity<Quote>(e =>
        {
            e.Property(x => x.OutcomeNote).HasMaxLength(500);
        });
    }
}
