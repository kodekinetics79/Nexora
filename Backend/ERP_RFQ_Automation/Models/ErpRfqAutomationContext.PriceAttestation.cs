using ERP_RFQ_Automation.Intelligence.Pricing;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Models;

// R5 price-provenance attestation (Decision Register R5, replaces FR-QTM-04's LOA gate).
// Configured in a partial for the same reason as the SLA module: the large scaffolded
// context file stays untouched, and ErpRfqAutomationContext.Tenancy.cs's
// OnModelCreatingPartial makes ONE delegating call to ConfigurePriceAttestationModel.
//
// TENANT ISOLATION. Both entities carry a non-nullable BusinessUnitId and the standard
// fail-closed global filter (CurrentTenantId == null || BusinessUnitId == CurrentTenantId).
// The filter alone is not relied on: PriceAttestationService filters BusinessUnitId
// explicitly in every query, because this table decides whether a customer-facing price
// may leave the building and the send path also runs from the below-floor approval tool
// where the ambient tenant may be absent.
//
// The FK to Quotes is the composite (BusinessUnitId, QuoteId) -> (BusinessUnitId, Id)
// pair, matching the Procurement/SupplierQuotes precedent, so an attestation can never be
// attached to another tenant's quote by primary key alone.
public partial class ErpRfqAutomationContext
{
    public DbSet<QuotePriceAttestation> QuotePriceAttestations => Set<QuotePriceAttestation>();
    public DbSet<QuotePriceAttestationLine> QuotePriceAttestationLines => Set<QuotePriceAttestationLine>();

    // Defining declaration for the hook called from the Tenancy partial.
    partial void ConfigurePriceAttestationModel(ModelBuilder modelBuilder);

    partial void ConfigurePriceAttestationModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<QuotePriceAttestation>(e =>
        {
            e.ToTable("QuotePriceAttestations");
            e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

            e.Property(x => x.Source).HasMaxLength(32).IsRequired();
            e.Property(x => x.SourceReference).HasMaxLength(200).IsRequired();
            e.Property(x => x.LineFingerprint).HasMaxLength(64).IsRequired();
            e.Property(x => x.ConfirmedBy).HasMaxLength(255).IsRequired();
            e.Property(x => x.ConfirmedOn).HasDefaultValueSql("now()");

            // Only the two ratified provenances are storable. A future third source is a
            // product decision, not something a malformed request may introduce.
            e.HasCheckConstraint("CK_QuotePriceAttestations_Source",
                "\"Source\" IN ('SALES_MANAGER','SUPPLIER_QUOTE')");
            e.HasCheckConstraint("CK_QuotePriceAttestations_LineCount",
                "\"LineCount\" >= 0");

            // The send gate reads "latest attestation for this quote in this tenant".
            e.HasIndex(x => new { x.BusinessUnitId, x.QuoteId, x.ConfirmedOn })
                .HasDatabaseName("IX_QuotePriceAttestations_BU_Quote_ConfirmedOn");

            e.HasOne<Quote>()
                .WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.QuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });

        modelBuilder.Entity<QuotePriceAttestationLine>(e =>
        {
            e.ToTable("QuotePriceAttestationLines");
            e.HasKey(x => x.Id);

            e.Property(x => x.ItemDescription).HasMaxLength(1000);
            // Same precision as QuoteItems.Quantity / QuoteItems.UnitPrice — a snapshot that
            // rounds differently from the source would read as a price change on every send.
            e.Property(x => x.Quantity).HasColumnType("decimal(18, 6)");
            e.Property(x => x.UnitPrice).HasColumnType("decimal(18, 6)");

            // One row per quote line per attestation.
            e.HasIndex(x => new { x.AttestationId, x.QuoteItemId })
                .IsUnique()
                .HasDatabaseName("UX_QuotePriceAttestationLines_Attestation_QuoteItem");

            e.HasOne(x => x.Attestation)
                .WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.AttestationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);

            e.HasQueryFilter(x => CurrentTenantId == null || x.BusinessUnitId == CurrentTenantId);
        });
    }
}
