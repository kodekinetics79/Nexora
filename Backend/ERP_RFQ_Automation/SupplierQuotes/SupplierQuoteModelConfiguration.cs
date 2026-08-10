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
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.SupplierQuoteId, x.Id });
            entity.Property(x => x.CaptureChannel).HasMaxLength(24).IsRequired();
            entity.Property(x => x.SourceIdentity).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SourceSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Incoterms).HasMaxLength(40);
            entity.Property(x => x.PaymentTerms).HasMaxLength(500);
            entity.Property(x => x.Notes).HasMaxLength(4000);
            entity.Property(x => x.FreightAmount).HasPrecision(18, 4);
            entity.Property(x => x.TaxAmount).HasPrecision(18, 4);
            // Configured exactly like FreightAmount and TaxAmount above — numeric(18,4), NOT NULL,
            // no store default. HasDefaultValue would give these three ValueGenerated.OnAdd and
            // make them behave differently from their siblings for no gain; the ADD COLUMN in the
            // migration carries defaultValue: 0m to backfill existing rows.
            entity.Property(x => x.DutyAmount).HasPrecision(18, 4);
            entity.Property(x => x.OtherAmount).HasPrecision(18, 4);
            entity.Property(x => x.DiscountAmount).HasPrecision(18, 4);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CapturedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            // Duty, other and discount are non-negative for the same reason freight and tax are: a
            // negative charge would reduce landed cost and therefore the customer price, and no
            // supplier bills a negative duty. A refund is a new revision, not a negative charge.
            entity.HasCheckConstraint("CK_supplier_quote_revisions_Values",
                "\"RevisionNumber\" > 0 AND \"FreightAmount\" >= 0 AND \"TaxAmount\" >= 0 " +
                "AND \"DutyAmount\" >= 0 AND \"OtherAmount\" >= 0 AND \"DiscountAmount\" >= 0");
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

        modelBuilder.Entity<SupplierNegotiationDecision>(entity =>
        {
            entity.ToTable("supplier_negotiation_decisions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.RecommendationCode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Disposition).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.EvidenceSnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.PolicyVersion).HasMaxLength(80).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Actor).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.HasCheckConstraint("CK_supplier_negotiation_decisions_Disposition",
                "\"Disposition\" IN ('PREPARED','DEFERRED','DISMISSED')");
            entity.HasCheckConstraint("CK_supplier_negotiation_decisions_ExpectedVersion",
                "\"ExpectedQuoteVersion\" > 0");
            entity.HasCheckConstraint("CK_supplier_negotiation_decisions_RecommendationCode",
                "\"RecommendationCode\" IN ('BEST_AND_FINAL_PRICE','QUANTITY_BREAK','FASTER_DELIVERY','FREIGHT_INCLUSIVE_OFFER','IMPROVED_PAYMENT_TERMS','PARTIAL_IMMEDIATE_AVAILABILITY','APPROVED_ALTERNATE')");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new
                { x.BusinessUnitId, x.SupplierQuoteId, x.SupplierQuoteRevisionId, x.DecidedOn });
            entity.HasOne(x => x.SupplierQuote).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.SupplierQuoteRevision).WithMany(x => x.NegotiationDecisions)
                .HasForeignKey(x => new
                    { x.BusinessUnitId, x.SupplierQuoteId, x.SupplierQuoteRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.SupplierQuoteId, Id = x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CustomerQuoteSourcingDecision>(entity =>
        {
            entity.ToTable("customer_quote_sourcing_decisions");
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            entity.Property(x => x.NexoraSerial).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Quantity).HasPrecision(18, 4);
            entity.Property(x => x.SupplierLandedUnitCost).HasPrecision(18, 6);
            entity.Property(x => x.TargetMarginPercent).HasPrecision(7, 4);
            entity.Property(x => x.CustomerUnitPrice).HasPrecision(18, 6);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Rationale).HasMaxLength(1000).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(255).IsRequired();
            entity.Property(x => x.CorrelationId).HasMaxLength(160).IsRequired();
            entity.HasCheckConstraint("CK_customer_quote_sourcing_decisions_Values",
                "\"Quantity\" > 0 AND \"SupplierLandedUnitCost\" > 0 AND \"TargetMarginPercent\" >= 0 AND \"TargetMarginPercent\" < 95 AND \"CustomerUnitPrice\" > 0");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.BusinessUnitId, x.QuoteItemId, x.CreatedOn });
            entity.HasOne<Quote>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.QuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<QuoteItem>().WithMany().HasForeignKey(x => new { x.QuoteItemId, x.QuoteId })
                .HasPrincipalKey(x => new { x.Id, x.QuoteId }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuote>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuoteRevision>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteRevisionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuoteLine>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuoteLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<CommercialDemandLine>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CommercialDemandLineId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourcingCase>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SourcingCaseId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SourcingAward>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SourcingAwardId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<SupplierQuotedItem>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.SupplierQuotedItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
