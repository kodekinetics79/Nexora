using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialDocuments;

public static class CommercialDocumentModelBuilderExtensions
{
    public static ModelBuilder AddCommercialDocuments(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CommercialDocumentClassification>(entity =>
        {
            entity.ToTable("commercial_document_classifications", table =>
            {
                table.HasCheckConstraint("ck_commercial_document_classifications_business_unit",
                    "business_unit_id > 0");
                table.HasCheckConstraint("ck_commercial_document_classifications_confidence",
                    "confidence >= 0 AND confidence <= 1");
                table.HasCheckConstraint("ck_commercial_document_classifications_version", "version > 0");
                table.HasCheckConstraint("ck_commercial_document_classifications_unknown_review",
                    "document_type <> 'Unknown' OR review_status IN ('ReviewRequired', 'Rejected')");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_commercial_document_classifications_tenant_id");
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.SourceDocumentId).HasColumnName("source_document_id");
            entity.Property(x => x.SourceDocumentContentHash).HasColumnName("source_document_content_hash")
                .HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.SourceObjectVersion).HasColumnName("source_object_version").HasMaxLength(255);
            entity.Property(x => x.DocumentType).HasColumnName("document_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ReviewStatus).HasColumnName("review_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(5, 4);
            entity.Property(x => x.ClassificationMethod).HasColumnName("classification_method").HasMaxLength(128);
            entity.Property(x => x.EvidenceJson).HasColumnName("evidence").HasColumnType("jsonb");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(256);
            entity.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.CustomerRfqId).HasColumnName("customer_rfq_id");
            entity.Property(x => x.SupplierRfqId).HasColumnName("supplier_rfq_id");
            entity.Property(x => x.SourcingCaseId).HasColumnName("sourcing_case_id");
            entity.Property(x => x.SupplierQuoteId).HasColumnName("supplier_quote_id");
            entity.Property(x => x.PurchaseOrderId).HasColumnName("purchase_order_id");
            entity.Property(x => x.SupplierInvoiceId).HasColumnName("supplier_invoice_id");
            entity.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
            entity.Property(x => x.ReviewedBy).HasColumnName("reviewed_by").HasMaxLength(255);
            entity.Property(x => x.ReviewReason).HasColumnName("review_reason").HasMaxLength(1_000);
            entity.Property(x => x.ReviewedOn).HasColumnName("reviewed_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceDocumentId }).IsUnique()
                .HasDatabaseName("ux_commercial_document_classifications_tenant_document");
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("ux_commercial_document_classifications_tenant_idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ReviewStatus, x.CreatedOn })
                .HasDatabaseName("ix_commercial_document_classifications_review_queue");
            entity.HasOne(x => x.SourceDocument).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
        return modelBuilder;
    }
}

