using ERP_RFQ_Automation.Metrics;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// WP-B4 entity configuration: passive AI-metric events + the quote revision
// columns, kept in a partial so the large scaffolded context file stays
// untouched — mirrors the Sla/Agent partial pattern. Invoked from
// ErpRfqAutomationContext.Tenancy.cs's OnModelCreatingPartial via a single
// delegating call to ConfigureMetricsModel(modelBuilder).
//
// The lead generates the migration from this configuration; the full column
// list is in Sla/WAVEB-WIRING.md.
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial's
    // OnModelCreatingPartial. The implementing declaration below supplies the body.
    partial void ConfigureMetricsModel(ModelBuilder modelBuilder);

    partial void ConfigureMetricsModel(ModelBuilder modelBuilder)
    {
        // ==== MetricEvents (append-only passive metrics, WP-B4) ====
        modelBuilder.Entity<MetricEvent>(e =>
        {
            e.ToTable("MetricEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(60).IsRequired();
            e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");
            // Reporting always slices by (BU, Type, time window); append-only, so no
            // unique constraints anywhere.
            e.HasIndex(x => new { x.BusinessUnitId, x.Type, x.CreatedOn })
                .HasDatabaseName("IX_MetricEvents_BU_Type_CreatedOn");
            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        // ==== Quote revision columns (revisions-lite, Models/Quote.Revision.cs) ====
        // RevisionOfQuoteId is a loose self-reference to Quotes.Id: mapped as a plain
        // nullable bigint with NO navigation/FK constraint so the scaffolded Quote
        // entity and its relationship web stay untouched (same convention as
        // OutcomeReasonId in the Sla partial). Chain walking is done in QuoteService.
        modelBuilder.Entity<Quote>(e =>
        {
            e.Property(x => x.RevisionNo).HasDefaultValue(1);
            e.HasIndex(x => x.RevisionOfQuoteId).HasDatabaseName("IX_Quotes_RevisionOfQuoteId");
        });
    }
}
