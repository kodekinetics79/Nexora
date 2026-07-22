using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.DocumentIntelligence.Persistence;

public static class EvidenceLedgerModelBuilderExtensions
{
    public static ModelBuilder AddEvidenceLedger(this ModelBuilder modelBuilder)
    {
        ConfigureCorpus(modelBuilder);
        ConfigureSourceDocument(modelBuilder);
        ConfigurePage(modelBuilder);
        ConfigureRegion(modelBuilder);
        ConfigureInquiry(modelBuilder);
        ConfigureLineItem(modelBuilder);
        ConfigureEvidence(modelBuilder);
        return modelBuilder;
    }

    private static void ConfigureCorpus(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentCorpus>(entity =>
        {
            entity.ToTable("document_corpora", table =>
                table.HasCheckConstraint("ck_document_corpora_business_unit", "business_unit_id > 0"));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.BatchId).HasColumnName("batch_id");
            entity.Property(x => x.SourceType).HasColumnName("source_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.BatchId }).IsUnique()
                .HasDatabaseName("ux_document_corpora_tenant_batch");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CreatedOn })
                .HasDatabaseName("ix_document_corpora_tenant_created");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status })
                .HasDatabaseName("ix_document_corpora_tenant_status");
        });
    }

    private static void ConfigureSourceDocument(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceDocument>(entity =>
        {
            entity.ToTable("source_documents", table =>
            {
                table.HasCheckConstraint("ck_source_documents_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_source_documents_byte_size", "byte_size >= 0");
                table.HasCheckConstraint("ck_source_documents_page_count", "page_count >= 0");
                table.HasCheckConstraint("ck_source_documents_content_hash", "content_hash ~ '^[0-9a-f]{64}$'");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.CorpusId).HasColumnName("corpus_id");
            entity.Property(x => x.ExtractionJobId).HasColumnName("extraction_job_id");
            entity.Property(x => x.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.OriginalFileName).HasColumnName("original_file_name").HasMaxLength(512);
            entity.Property(x => x.DetectedMimeType).HasColumnName("detected_mime_type").HasMaxLength(255);
            entity.Property(x => x.ObjectBucket).HasColumnName("object_bucket").HasMaxLength(255);
            entity.Property(x => x.ObjectKey).HasColumnName("object_key").HasMaxLength(1024);
            entity.Property(x => x.ObjectVersion).HasColumnName("object_version").HasMaxLength(255);
            entity.Property(x => x.ByteSize).HasColumnName("byte_size");
            entity.Property(x => x.PageCount).HasColumnName("page_count");
            entity.Property(x => x.SecurityStatus).HasColumnName("security_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ProcessingStatus).HasColumnName("processing_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ContentHash }).IsUnique()
                .HasDatabaseName("ux_source_documents_tenant_hash");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ObjectBucket, x.ObjectKey, x.ObjectVersion }).IsUnique()
                .HasDatabaseName("ux_source_documents_object_version");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CorpusId })
                .HasDatabaseName("ix_source_documents_tenant_corpus");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SecurityStatus })
                .HasDatabaseName("ix_source_documents_tenant_security");
            entity.HasIndex(x => x.ExtractionJobId).HasDatabaseName("ix_source_documents_extraction_job");
            entity.HasOne(x => x.Corpus).WithMany(x => x.Documents).HasForeignKey(x => x.CorpusId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigurePage(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentPage>(entity =>
        {
            entity.ToTable("document_pages", table =>
            {
                table.HasCheckConstraint("ck_document_pages_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_document_pages_number", "page_number > 0");
                table.HasCheckConstraint("ck_document_pages_dimensions", "width > 0 AND height > 0");
                table.HasCheckConstraint("ck_document_pages_rotation", "rotation IN (0, 90, 180, 270)");
                table.HasCheckConstraint("ck_document_pages_text_hash", "text_hash IS NULL OR text_hash ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("ck_document_pages_ocr_confidence", "ocr_confidence IS NULL OR (ocr_confidence >= 0 AND ocr_confidence <= 1)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.PageNumber).HasColumnName("page_number");
            entity.Property(x => x.Width).HasColumnName("width").HasPrecision(12, 4);
            entity.Property(x => x.Height).HasColumnName("height").HasPrecision(12, 4);
            entity.Property(x => x.Rotation).HasColumnName("rotation");
            entity.Property(x => x.TextHash).HasColumnName("text_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.OcrStatus).HasColumnName("ocr_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.OcrConfidence).HasColumnName("ocr_confidence").HasPrecision(6, 5);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.DocumentId, x.PageNumber }).IsUnique()
                .HasDatabaseName("ux_document_pages_document_number");
            entity.HasIndex(x => new { x.BusinessUnitId, x.DocumentId })
                .HasDatabaseName("ix_document_pages_tenant_document");
            entity.HasIndex(x => new { x.BusinessUnitId, x.OcrStatus })
                .HasDatabaseName("ix_document_pages_tenant_ocr_status");
            entity.HasOne(x => x.Document).WithMany(x => x.Pages).HasForeignKey(x => x.DocumentId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureRegion(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentRegion>(entity =>
        {
            entity.ToTable("document_regions", table =>
            {
                table.HasCheckConstraint("ck_document_regions_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_document_regions_bounds", "x >= 0 AND y >= 0 AND width > 0 AND height > 0");
                table.HasCheckConstraint("ck_document_regions_confidence", "confidence >= 0 AND confidence <= 1");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.PageId).HasColumnName("page_id");
            entity.Property(x => x.RegionType).HasColumnName("region_type").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.X).HasColumnName("x").HasPrecision(12, 4);
            entity.Property(x => x.Y).HasColumnName("y").HasPrecision(12, 4);
            entity.Property(x => x.Width).HasColumnName("width").HasPrecision(12, 4);
            entity.Property(x => x.Height).HasColumnName("height").HasPrecision(12, 4);
            entity.Property(x => x.Text).HasColumnName("text").HasMaxLength(100_000);
            entity.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(6, 5);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.PageId })
                .HasDatabaseName("ix_document_regions_tenant_page");
            entity.HasIndex(x => new { x.BusinessUnitId, x.RegionType })
                .HasDatabaseName("ix_document_regions_tenant_type");
            entity.HasOne(x => x.Page).WithMany(x => x.Regions).HasForeignKey(x => x.PageId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureInquiry(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CanonicalInquiry>(entity =>
        {
            entity.ToTable("canonical_inquiries", table =>
            {
                table.HasCheckConstraint("ck_canonical_inquiries_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_canonical_inquiries_number", "inquiry_number > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.CorpusId).HasColumnName("corpus_id");
            entity.Property(x => x.InquiryNumber).HasColumnName("inquiry_number");
            entity.Property(x => x.LeadId).HasColumnName("lead_id");
            entity.Property(x => x.CustomerRfqNumber).HasColumnName("customer_rfq_number").HasMaxLength(256);
            entity.Property(x => x.BuyerName).HasColumnName("buyer_name").HasMaxLength(512);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.CorpusId, x.InquiryNumber }).IsUnique()
                .HasDatabaseName("ux_canonical_inquiries_corpus_number");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status })
                .HasDatabaseName("ix_canonical_inquiries_tenant_status");
            entity.HasIndex(x => new { x.BusinessUnitId, x.CustomerRfqNumber })
                .HasDatabaseName("ix_canonical_inquiries_tenant_customer_rfq");
            entity.HasIndex(x => x.LeadId).HasDatabaseName("ix_canonical_inquiries_lead");
            entity.HasOne(x => x.Corpus).WithMany(x => x.Inquiries).HasForeignKey(x => x.CorpusId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureLineItem(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CanonicalLineItem>(entity =>
        {
            entity.ToTable("canonical_line_items", table =>
            {
                table.HasCheckConstraint("ck_canonical_line_items_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_canonical_line_items_number", "line_number > 0");
                table.HasCheckConstraint("ck_canonical_line_items_quantity", "quantity IS NULL OR quantity > 0");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.InquiryId).HasColumnName("inquiry_id");
            entity.Property(x => x.LineNumber).HasColumnName("line_number");
            entity.Property(x => x.Description).HasColumnName("description").HasMaxLength(4_000);
            entity.Property(x => x.Quantity).HasColumnName("quantity").HasPrecision(20, 6);
            entity.Property(x => x.UnitOfMeasure).HasColumnName("unit_of_measure").HasMaxLength(64);
            entity.Property(x => x.Manufacturer).HasColumnName("manufacturer").HasMaxLength(512);
            entity.Property(x => x.ManufacturerPartNumber).HasColumnName("manufacturer_part_number").HasMaxLength(512);
            entity.Property(x => x.CurrencyCode).HasColumnName("currency_code").HasMaxLength(3).IsFixedLength();
            entity.Property(x => x.RawPayload).HasColumnName("raw_payload").HasColumnType("jsonb");
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.InquiryId, x.LineNumber }).IsUnique()
                .HasDatabaseName("ux_canonical_line_items_inquiry_line");
            entity.HasIndex(x => new { x.BusinessUnitId, x.InquiryId })
                .HasDatabaseName("ix_canonical_line_items_tenant_inquiry");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ManufacturerPartNumber })
                .HasDatabaseName("ix_canonical_line_items_tenant_mpn");
            entity.HasOne(x => x.Inquiry).WithMany(x => x.LineItems).HasForeignKey(x => x.InquiryId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureEvidence(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FieldEvidence>(entity =>
        {
            entity.ToTable("field_evidence", table =>
            {
                table.HasCheckConstraint("ck_field_evidence_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_field_evidence_target", "(inquiry_id IS NOT NULL AND line_item_id IS NULL) OR (inquiry_id IS NULL AND line_item_id IS NOT NULL)");
                table.HasCheckConstraint("ck_field_evidence_confidence", "confidence >= 0 AND confidence <= 1");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.RegionId).HasColumnName("region_id");
            entity.Property(x => x.InquiryId).HasColumnName("inquiry_id");
            entity.Property(x => x.LineItemId).HasColumnName("line_item_id");
            entity.Property(x => x.FieldName).HasColumnName("field_name").HasMaxLength(256);
            entity.Property(x => x.RawValue).HasColumnName("raw_value").HasMaxLength(100_000);
            entity.Property(x => x.NormalizedValue).HasColumnName("normalized_value").HasMaxLength(100_000);
            entity.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(6, 5);
            entity.Property(x => x.Extractor).HasColumnName("extractor").HasMaxLength(256);
            entity.Property(x => x.RunId).HasColumnName("run_id");
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.InquiryId, x.FieldName })
                .HasDatabaseName("ix_field_evidence_inquiry_field");
            entity.HasIndex(x => new { x.BusinessUnitId, x.LineItemId, x.FieldName })
                .HasDatabaseName("ix_field_evidence_line_field");
            entity.HasIndex(x => new { x.BusinessUnitId, x.RunId })
                .HasDatabaseName("ix_field_evidence_tenant_run");
            entity.HasIndex(x => x.RegionId).HasDatabaseName("ix_field_evidence_region");
            entity.HasOne(x => x.Region).WithMany(x => x.Evidence).HasForeignKey(x => x.RegionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Inquiry).WithMany(x => x.Evidence).HasForeignKey(x => x.InquiryId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.LineItem).WithMany(x => x.Evidence).HasForeignKey(x => x.LineItemId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
