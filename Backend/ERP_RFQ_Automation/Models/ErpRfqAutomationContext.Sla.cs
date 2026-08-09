using System;
using System.Linq.Expressions;
using ERP_RFQ_Automation.Services;
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
            // FR-QTM-07: the knob formerly called QuoteAutoExpireDays is now the GRACE
            // allowance added to the validity date (default 0, was 14). The property was
            // renamed for honesty; the COLUMN keeps its legacy name so the rename costs
            // no migration. QuoteNoResponseExpiryDays (the 90-day submission rule) is a
            // genuinely new column.
            e.Property(x => x.QuoteExpiryGraceDays).HasColumnName("QuoteAutoExpireDays");
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
            e.Property(x => x.DedupKey).HasMaxLength(120).IsRequired();
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            e.HasIndex(x => new { x.BusinessUnitId, x.EntityType, x.EntityId, x.Level })
                .HasDatabaseName("IX_SlaEvents_BU_Entity_Level");
            // SCALE-OUT: the send-once guarantee is enforced by the DATABASE, not by a
            // lookup-before-insert race. Two SlaSweepWorker instances both INSERT their
            // claim; exactly one wins and sends, the loser gets 23505 and skips.
            // The digest's DedupKey carries the UTC day so it still repeats daily.
            e.HasIndex(x => new { x.BusinessUnitId, x.DedupKey })
                .IsUnique()
                .HasDatabaseName("UX_SlaEvents_BU_DedupKey");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        // ==== Watched-folder ingestion retry ledger (Services/FolderService.cs) ====
        // Spliced here because OnModelCreatingPartial (Tenancy partial) allows exactly
        // one call site per hook and this is the SLA/worker-owned splice point.
        // Replaces the per-file ".nexora-retry.json" sidecars, which lived on Render's
        // EPHEMERAL per-instance disk and therefore reset the attempt counter on every
        // restart and were invisible to every other instance.
        modelBuilder.ApplyFolderIngestionRetryModel(
            e => CurrentTenantId == null || e.BusinessUnitId == CurrentTenantId);

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
