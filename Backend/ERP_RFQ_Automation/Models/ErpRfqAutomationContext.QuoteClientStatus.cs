using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// The client's own quote statuses (Models/Quote.ClientStatus.cs) and the list order of setup rows.
// Kept in a partial like the other quote additions; ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial makes ONE delegating call to ConfigureQuoteClientStatusModel.
public partial class ErpRfqAutomationContext
{
    partial void ConfigureQuoteClientStatusModel(ModelBuilder modelBuilder);

    partial void ConfigureQuoteClientStatusModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SetupMaster>(entity =>
        {
            // NOT NULL with a store default of 0, like RoleRank: every existing row and every row a
            // legacy path writes lands at 0 and falls back to its id for order.
            entity.Property(e => e.SortOrder)
                .HasColumnType("smallint")
                .HasDefaultValue((short)0);
        });

        modelBuilder.Entity<Quote>(entity =>
        {
            // No navigation: the scaffolded SetupMaster relationship web stays untouched and the
            // readers batch-resolve the name, as they already do for OutcomeReasonId. SET NULL so
            // removing a setup row can never take a quote with it; the setup delete refuses a row
            // that is in use anyway (SetupMasterRepository.DeleteAsync).
            entity.HasOne<SetupMaster>().WithMany()
                .HasForeignKey(e => e.SubStatusId)
                .OnDelete(DeleteBehavior.SetNull)
                .HasConstraintName("FK_Quotes_SubStatus");
        });
    }
}
