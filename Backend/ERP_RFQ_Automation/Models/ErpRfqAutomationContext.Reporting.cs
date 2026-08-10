using System;
using System.Linq.Expressions;
using ERP_RFQ_Automation.Reporting;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Gate 8 scheduled-reporting configuration (Reporting/). Applied through an extension method from
/// <c>OnModelCreatingPartial</c>, matching <c>ApplyTenantLifecycleModel</c> and friends — the
/// partial-method hooks each allow exactly one call site and are already spoken for.
/// </summary>
public static class ReportingModelBuilderExtensions
{
    public static ModelBuilder ApplyReportingModel(
        this ModelBuilder modelBuilder,
        Expression<Func<ReportSubscription, bool>> tenantFilter)
    {
        modelBuilder.Entity<ReportSubscription>(e =>
        {
            e.ToTable("ReportSubscriptions");
            e.HasKey(x => x.Id);

            e.Property(x => x.ReportKey).HasMaxLength(60).IsRequired();
            e.Property(x => x.Cadence).HasMaxLength(20).IsRequired();
            e.Property(x => x.Format).HasMaxLength(10).IsRequired();
            e.Property(x => x.Recipients).HasMaxLength(2000).IsRequired();
            e.Property(x => x.LastRunOutcome).HasMaxLength(30);
            e.Property(x => x.LastRunDetail).HasMaxLength(1000);
            e.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            e.Property(x => x.ModifiedBy).HasMaxLength(255);
            e.Property(x => x.CreatedOn).HasDefaultValueSql("now()");

            // NextRunOn deliberately carries NO default. See ReportSubscription's remarks: a
            // NOT NULL date defaulting to year 1 would read as "overdue since forever" and the
            // first sweep would mail the whole estate at once.

            // The worker's only selection predicate, in the order it filters.
            e.HasIndex(x => new { x.BusinessUnitId, x.IsActive, x.NextRunOn })
                .HasDatabaseName("IX_ReportSubscriptions_BU_Active_NextRun");

            // One subscription per (report, cadence, format) per tenant. Without this a stray
            // double-submit gives a tenant two identical emails every morning, and the send-once
            // claim cannot help because they are genuinely different subscription ids.
            e.HasIndex(x => new { x.BusinessUnitId, x.ReportKey, x.Cadence, x.Format })
                .IsUnique()
                .HasDatabaseName("UX_ReportSubscriptions_BU_Report_Cadence_Format");

            e.HasQueryFilter(tenantFilter);
        });

        return modelBuilder;
    }
}
