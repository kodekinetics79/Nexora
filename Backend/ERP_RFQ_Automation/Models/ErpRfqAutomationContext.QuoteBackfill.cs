using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Quote back-fill: the two columns that let a quote predating Nexora be carried in and then
// monitored beside the quotes Nexora produced itself.
//
// Configured in a partial for the same reason as QuoteValidity / QuoteRemoval: the large
// scaffolded context file stays untouched, and ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial makes ONE delegating call to ConfigureQuoteBackfillModel.
//
// NO new table. A back-filled quote is a Quote, reached through the same Lead -> RFQ -> Quote
// commercial-identity chain the downstream trigger already enforces, so it inherits tenant
// isolation, serial lineage and every downstream consumer unchanged. The alternative — a
// parallel "legacy quotes" table — would have needed all of that rebuilt and kept in step.
public partial class ErpRfqAutomationContext
{
    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigureQuoteBackfillModel(ModelBuilder modelBuilder);

    partial void ConfigureQuoteBackfillModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Quote>(entity =>
        {
            entity.Property(x => x.ExternalQuoteReference).HasMaxLength(100);

            entity.Property(x => x.Origin)
                .HasMaxLength(20)
                .IsRequired()
                .HasDefaultValue(QuoteOrigin.Pipeline);

            // Origin is a closed set. Checked in the database rather than only in the service
            // because the bulk importer writes many rows in one transaction, and a typo in an
            // uploaded file must fail that row loudly instead of creating a third origin nobody
            // filters on.
            entity.HasCheckConstraint("CK_Quotes_Origin",
                "\"Origin\" IN ('PIPELINE', 'BACKFILL')");

            // A back-filled quote is identified by the number its customer already knows. Re-running
            // an import must not create it twice, so that number is unique per tenant where present.
            // Filtered, because a pipeline quote has no external reference and many carry NULL.
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExternalQuoteReference })
                .HasDatabaseName("UX_Quotes_BU_ExternalQuoteReference")
                .IsUnique()
                .HasFilter("\"ExternalQuoteReference\" IS NOT NULL");

            // The one screen that shows both kinds together sorts by date within an origin.
            entity.HasIndex(x => new { x.BusinessUnitId, x.Origin, x.QuoteDate })
                .HasDatabaseName("IX_Quotes_BU_Origin_QuoteDate");
        });
    }
}
