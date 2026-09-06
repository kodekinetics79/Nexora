using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Quote ownership column (Models/Quote.Ownership.cs), kept in a partial for the same reason the
// SLA and backfill partials are: the scaffolded context file stays untouched. Invoked from
// ErpRfqAutomationContext.Tenancy.cs's OnModelCreatingPartial via a single delegating call.
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial's OnModelCreatingPartial.
    partial void ConfigureQuoteOwnershipModel(ModelBuilder modelBuilder);

    partial void ConfigureQuoteOwnershipModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Quote>(entity =>
        {
            entity.Property(e => e.OwnerUserId).HasColumnName("OwnerUserID");

            // The index the scoped funnel reads: every predicate on this column arrives with the
            // tenant already fixed, so the business unit leads.
            entity.HasIndex(e => new { e.BusinessUnitId, e.OwnerUserId }, "IX_Quotes_BusinessUnitID_OwnerUserID");

            // No navigation, and no explicit delete behaviour — the same shape as FK_Leads_Users,
            // which is the other place a user is named as the owner of commercial work. An
            // optional reference restricts the delete, so a person who owns quotes cannot be
            // erased out from under them; that is already true of a person who owns leads.
            entity.HasOne<User>().WithMany()
                .HasForeignKey(e => e.OwnerUserId)
                .HasConstraintName("FK_Quotes_OwnerUser");
        });
    }
}
