using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// Decision Register R7 — reasoned, auditable quote-validity extensions.
//
// Configured in a partial for the same reason as the SLA / PriceAttestation modules: the
// large scaffolded context file stays untouched, and ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial makes ONE delegating call to ConfigureQuoteValidityModel.
//
// TENANT ISOLATION. QuoteValidityExtension carries a non-nullable BusinessUnitId and the
// standard fail-closed global filter (CurrentTenantId == null || BusinessUnitId ==
// CurrentTenantId). The filter is not relied on alone: QuoteService filters BusinessUnitId
// explicitly in every query against this table.
//
// The FK to Quotes is the composite (BusinessUnitId, QuoteId) -> (BusinessUnitId, Id) pair,
// matching the PriceAttestation precedent, so an extension can never be attached to another
// tenant's quote by primary key alone. This configuration must therefore run AFTER the
// Quote alternate key is declared in the Tenancy partial.
public partial class ErpRfqAutomationContext
{
    public DbSet<QuoteValidityExtension> QuoteValidityExtensions => Set<QuoteValidityExtension>();

    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigureQuoteValidityModel(ModelBuilder modelBuilder);

    partial void ConfigureQuoteValidityModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuoteValidityExtension>(e =>
        {
            e.ToTable("QuoteValidityExtensions");
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

            e.Property(x => x.Reason).HasMaxLength(500).IsRequired();
            e.Property(x => x.ExtendedBy).HasMaxLength(255).IsRequired();
            e.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            e.Property(x => x.ExtendedOn).HasDefaultValueSql("now()");

            // A reason nobody can read later is not a reason (R7). An empty string would be
            // exactly that, so it is refused by the database and not only by the service.
            e.HasCheckConstraint("CK_QuoteValidityExtensions_Reason", "trim(\"Reason\") <> ''");

            // Replay safety: a retried extend command records one row, not two.
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey })
                .IsUnique()
                .HasDatabaseName("UX_QuoteValidityExtensions_BU_IdempotencyKey");

            // The history read is "every extension on this quote in this tenant, newest first".
            e.HasIndex(x => new { x.BusinessUnitId, x.QuoteId, x.ExtendedOn })
                .HasDatabaseName("IX_QuoteValidityExtensions_BU_Quote_ExtendedOn");

            e.HasOne<Quote>()
                .WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.QuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        // ==== Quote validity marker column (R7, Models/Quote.Validity.cs) ====
        // Plain nullable timestamp. It is denormalised from QuoteValidityExtensions so the
        // SLA sweep can decide "is this quote being deliberately held open?" without joining.
        modelBuilder.Entity<Quote>(e =>
        {
            e.HasIndex(x => new { x.BusinessUnitId, x.ValidityExtendedOn })
                .HasFilter("\"ValidityExtendedOn\" IS NOT NULL")
                .HasDatabaseName("IX_Quotes_BU_ValidityExtendedOn");
        });
    }
}
