using ERP_RFQ_Automation.Security;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// SEC-H6 entity configuration for the durable failed-login counter, kept in a partial so the
// large scaffolded context file stays untouched — mirrors the Metrics/Sla/Agent partials.
// Invoked from ErpRfqAutomationContext.Tenancy.cs's OnModelCreatingPartial.
//
// NOTE: intentionally no HasQueryFilter. The row is written before authentication, so there
// is no businessUnitId to scope by; the composite unique key namespaces tenant and platform
// counters instead. Because it carries no query filter it is also outside the RLS-policy
// expectation asserted by PostgreSqlProductionDialectTests.
public partial class ErpRfqAutomationContext
{
    public virtual DbSet<LoginAttemptRecord> LoginAttempts { get; set; } = null!;

    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigureLoginThrottlingModel(ModelBuilder modelBuilder);

    partial void ConfigureLoginThrottlingModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LoginAttemptRecord>(e =>
        {
            e.ToTable("LoginAttempts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Plane).HasMaxLength(20).IsRequired();
            e.Property(x => x.AttemptKey).HasMaxLength(256).IsRequired();
            e.Property(x => x.FailedCount).IsRequired();

            // The uniqueness constraint is what makes the cross-instance counter correct:
            // concurrent first-failure inserts collide instead of double-counting.
            e.HasIndex(x => new { x.Plane, x.AttemptKey })
                .IsUnique()
                .HasDatabaseName("UX_LoginAttempts_Plane_AttemptKey");

            // Supports pruning released lockouts.
            e.HasIndex(x => x.LockedUntilUtc)
                .HasDatabaseName("IX_LoginAttempts_LockedUntilUtc");
        });
    }
}
