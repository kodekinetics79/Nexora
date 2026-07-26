using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.SupplierQuotes;

public static class SupplierQuoteModelConfiguration
{
    /// <summary>The Integration Owner calls this from the shared model-building path.</summary>
    public static ModelBuilder AddSupplierQuotes(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SupplierQuote>(entity =>
        {
            entity.ToTable("supplier_quotes");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.SupplierQuoteReference).HasMaxLength(160).IsRequired();
            entity.Property(x => x.InboxStatus).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasCheckConstraint("CK_supplier_quotes_CurrentRevision", "\"CurrentRevisionNumber\" > 0");
            entity.HasCheckConstraint("CK_supplier_quotes_InboxStatus",
                "\"InboxStatus\" IN ('REVIEW_REQUIRED','READY_FOR_COMPARISON')");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierId, x.SupplierQuoteReference }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.InboxStatus, x.UpdatedOn });
            entity.HasIndex(x => new { x.BusinessUnitId, x.NexoraSerial });
            entity.HasOne<Supplier>().WithMany().HasForeignKey(x => new { x.SupplierId, x.BusinessUnitId })
                .HasPrincipalKey(x => new { x.Id, x.Buid }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierSolicitation>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierSolicitationId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourcingCase>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourcingCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Rfq>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.RfqId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierQuoteRevision>(entity =>
        {
            entity.ToTable("supplier_quote_revisions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.CaptureChannel).HasMaxLength(24).IsRequired();
            entity.Property(x => x.SourceIdentity).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SourceSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Incoterms).HasMaxLength(40);
            entity.Property(x => x.PaymentTerms).HasMaxLength(500);
            entity.Property(x => x.Notes).HasMaxLength(4000);
            entity.Property(x => x.FreightAmount).HasPrecision(18, 4);
            entity.Property(x => x.TaxAmount).HasPrecision(18, 4);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CapturedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.HasCheckConstraint("CK_supplier_quote_revisions_Values",
                "\"RevisionNumber\" > 0 AND \"FreightAmount\" >= 0 AND \"TaxAmount\" >= 0");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierQuoteId, x.RevisionNumber }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasOne(x => x.SupplierQuote).WithMany(x => x.Revisions)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Currency>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CurrencyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourceDocument>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierQuoteLine>(entity =>
        {
            entity.ToTable("supplier_quote_lines");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.PartNumber).HasMaxLength(255);
            entity.Property(x => x.Manufacturer).HasMaxLength(255);
            entity.Property(x => x.SupplierPartNumber).HasMaxLength(255);
            entity.Property(x => x.Description).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.UnitOfMeasure).HasMaxLength(80).IsRequired();
            entity.Property(x => x.AvailabilityType).HasMaxLength(80);
            entity.Property(x => x.OriginCountry).HasMaxLength(120);
            entity.Property(x => x.Warranty).HasMaxLength(500);
            entity.Property(x => x.Exceptions).HasMaxLength(2000);
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.AvailableQuantity).HasPrecision(18, 4);
            entity.Property(x => x.UnitPrice).HasPrecision(18, 6);
            entity.Property(x => x.MinimumOrderQuantity).HasPrecision(18, 4);
            entity.HasCheckConstraint("CK_supplier_quote_lines_Values",
                "\"LineNumber\" > 0 AND \"Quantity\" > 0 AND \"UnitPrice\" >= 0 AND (\"AvailableQuantity\" IS NULL OR \"AvailableQuantity\" >= 0) AND (\"MinimumOrderQuantity\" IS NULL OR \"MinimumOrderQuantity\" > 0) AND (\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0)");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId, x.LineNumber }).IsUnique();
            entity.HasOne<SupplierQuoteRevision>().WithMany(x => x.Lines)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialDemandLine>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.CommercialDemandLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierQuoteFieldEvidence>(entity =>
        {
            entity.ToTable("supplier_quote_field_evidence");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.FieldName).HasMaxLength(120).IsRequired();
            entity.Property(x => x.OriginalValue).HasMaxLength(4000);
            entity.Property(x => x.NormalizedValue).HasMaxLength(4000);
            entity.Property(x => x.Confidence).HasPrecision(5, 4);
            entity.Property(x => x.Method).HasMaxLength(80).IsRequired();
            entity.Property(x => x.ModelOrRuleVersion).HasMaxLength(160);
            entity.Property(x => x.SourceRegion).HasMaxLength(500);
            entity.HasCheckConstraint("CK_supplier_quote_field_evidence_Confidence",
                "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId, x.ReviewRequired });
            entity.HasOne<SupplierQuoteRevision>().WithMany(x => x.Evidence)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuoteLine>().WithMany(x => x.Evidence)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SupplierQuoteReviewDecision>(entity =>
        {
            entity.ToTable("supplier_quote_review_decisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasMaxLength(24).IsRequired();
            entity.Property(x => x.CorrectedValue).HasMaxLength(4000);
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.ReviewedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.HasCheckConstraint("CK_supplier_quote_review_decisions_Status",
                "\"Status\" IN ('ACCEPTED','CORRECTED','REJECTED')");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SupplierQuoteFieldEvidenceId, x.ReviewedOn });
            entity.HasOne<SupplierQuoteRevision>().WithMany(x => x.ReviewDecisions)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuoteFieldEvidence>().WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteFieldEvidenceId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
