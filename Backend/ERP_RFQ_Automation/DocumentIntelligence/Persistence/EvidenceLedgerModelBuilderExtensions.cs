using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.DocumentIntelligence.Persistence;

public static class EvidenceLedgerModelBuilderExtensions
{
    public static ModelBuilder AddEvidenceLedger(this ModelBuilder modelBuilder)
    {
        ConfigureCorpus(modelBuilder);
        ConfigureSourceDocument(modelBuilder);
        ConfigureSourceDocumentOccurrence(modelBuilder);
        ConfigureExtractionRun(modelBuilder);
        ConfigurePage(modelBuilder);
        ConfigureRegion(modelBuilder);
        ConfigureInquiry(modelBuilder);
        ConfigureLineItem(modelBuilder);
        ConfigureValidationFinding(modelBuilder);
        ConfigureEvidence(modelBuilder);
        return modelBuilder;
    }

    private static void ConfigureSourceDocumentOccurrence(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SourceDocumentOccurrence>(entity =>
        {
            entity.ToTable("source_document_occurrences", table =>
            {
                table.HasCheckConstraint("ck_source_document_occurrences_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_source_document_occurrences_outcome_state",
                    "outcome_state IN ('NONE','EXACT_DUPLICATE_PENDING_SECURITY','EXACT_DUPLICATE_CONFIRMED','BUSINESS_DUPLICATE_CONFIRMED','DUPLICATE_RESCAN_REQUIRED','REVISION','POSSIBLE_MATCH','SECURITY_SCAN_BLOCKED','MALWARE_DETECTED','UNSUPPORTED_FORMAT','SOURCE_OBJECT_UNAVAILABLE','EVIDENCE_INTEGRITY_FAILURE')");
                table.HasCheckConstraint("ck_source_document_occurrences_resource_counts",
                    "bytes_uploaded >= 0 AND hashing_duration_ms >= 0 AND storage_physical_bytes >= 0 AND storage_logical_bytes >= 0");
                table.HasCheckConstraint("ck_source_document_occurrences_resource_costs",
                    "local_compute_cost >= 0 AND external_processing_cost >= 0 AND total_actual_cost >= 0 AND estimated_processing_avoided >= 0 AND length(cost_status) > 0");
                table.HasCheckConstraint("ck_source_document_occurrences_original",
                    "original_occurrence_id IS NULL OR original_occurrence_id <> id");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_source_document_occurrences_tenant_id");
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.SourceDocumentId).HasColumnName("source_document_id");
            entity.Property(x => x.CorpusId).HasColumnName("corpus_id");
            entity.Property(x => x.ExtractionJobId).HasColumnName("extraction_job_id");
            entity.Property(x => x.OriginalOccurrenceId).HasColumnName("original_occurrence_id");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(256);
            entity.Property(x => x.SourceMetadataJson).HasColumnName("source_metadata").HasColumnType("jsonb");
            entity.Property(x => x.LogicalGroupKey).HasColumnName("logical_group_key").HasMaxLength(256);
            entity.Property(x => x.IntakeStatus).HasColumnName("intake_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.OutcomeState).HasColumnName("outcome_state").HasConversion<string>().HasMaxLength(48);
            entity.Property(x => x.LastErrorCategory).HasColumnName("last_error_category").HasMaxLength(64);
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code").HasMaxLength(128);
            entity.Property(x => x.LastErrorDetailsJson).HasColumnName("last_error_details").HasColumnType("jsonb");
            entity.Property(x => x.ReceivedOn).HasColumnName("received_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.BytesUploaded).HasColumnName("bytes_uploaded");
            entity.Property(x => x.HashingDurationMs).HasColumnName("hashing_duration_ms");
            entity.Property(x => x.StoragePhysicalBytes).HasColumnName("storage_physical_bytes");
            entity.Property(x => x.StorageLogicalBytes).HasColumnName("storage_logical_bytes");
            entity.Property(x => x.MalwareScanReused).HasColumnName("malware_scan_reused");
            entity.Property(x => x.MalwareScanRerun).HasColumnName("malware_scan_rerun");
            entity.Property(x => x.ParserReused).HasColumnName("parser_reused");
            entity.Property(x => x.OcrReused).HasColumnName("ocr_reused");
            entity.Property(x => x.LocalModelReused).HasColumnName("local_model_reused");
            entity.Property(x => x.ExternalModelReused).HasColumnName("external_model_reused");
            entity.Property(x => x.ProcessingReused).HasColumnName("processing_reused");
            entity.Property(x => x.LocalComputeCost).HasColumnName("local_compute_cost").HasPrecision(18, 6);
            entity.Property(x => x.ExternalProcessingCost).HasColumnName("external_processing_cost").HasPrecision(18, 6);
            entity.Property(x => x.TotalActualCost).HasColumnName("total_actual_cost").HasPrecision(18, 6);
            entity.Property(x => x.EstimatedProcessingAvoided).HasColumnName("estimated_processing_avoided").HasPrecision(18, 6);
            entity.Property(x => x.CostStatus).HasColumnName("cost_status").HasMaxLength(48);
            entity.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique()
                .HasDatabaseName("ux_source_document_occurrences_tenant_idempotency");
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceDocumentId, x.ReceivedOn })
                .HasDatabaseName("ix_source_document_occurrences_tenant_document");
            entity.HasIndex(x => new { x.BusinessUnitId, x.LogicalGroupKey, x.ReceivedOn })
                .HasDatabaseName("ix_source_document_occurrences_tenant_group");
            entity.HasIndex(x => x.ExtractionJobId)
                .HasDatabaseName("ix_source_document_occurrences_extraction_job");
            entity.HasIndex(x => new { x.BusinessUnitId, x.OutcomeState, x.ReceivedOn })
                .HasDatabaseName("ix_source_document_occurrences_tenant_outcome");
            entity.HasIndex(x => new { x.BusinessUnitId, x.OriginalOccurrenceId })
                .HasDatabaseName("ix_source_document_occurrences_tenant_original");
            entity.HasOne(x => x.SourceDocument).WithMany(x => x.Occurrences)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Corpus).WithMany(x => x.Occurrences)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CorpusId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.OriginalOccurrence).WithMany(x => x.DuplicateOccurrences)
                .HasForeignKey(x => new { x.BusinessUnitId, x.OriginalOccurrenceId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureExtractionRun(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ExtractionRun>(entity =>
        {
            entity.ToTable("extraction_runs", table =>
            {
                table.HasCheckConstraint("ck_extraction_runs_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_extraction_runs_job", "extraction_job_id > 0");
                table.HasCheckConstraint("ck_extraction_runs_attempt", "attempt_number > 0");
                table.HasCheckConstraint("ck_extraction_runs_counts",
                    "page_count >= 0 AND region_count >= 0 AND inquiry_count >= 0 AND line_item_count >= 0 AND evidence_count >= 0 AND finding_count >= 0");
                table.HasCheckConstraint("ck_extraction_runs_completion",
                    "(status IN ('Completed', 'Failed') AND completed_on IS NOT NULL) OR (status NOT IN ('Completed', 'Failed') AND completed_on IS NULL)");
                table.HasCheckConstraint("ck_extraction_runs_failure",
                    "(status = 'Failed' AND failure_reason IS NOT NULL) OR (status <> 'Failed' AND failure_reason IS NULL)");
                table.HasCheckConstraint("ck_extraction_runs_cost",
                    "(processing_cost_amount IS NULL AND processing_cost_currency IS NULL) OR (processing_cost_amount >= 0 AND processing_cost_currency ~ '^[A-Z]{3}$')");
                table.HasCheckConstraint("ck_extraction_runs_ocr_evidence",
                    "ocr_page_count >= 0 AND (ocr_status <> 'NotRequired' OR (ocr_page_count = 0 AND ocr_truncated = FALSE))");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_extraction_runs_tenant_id");
            entity.HasAlternateKey(x => x.RunId).HasName("ak_extraction_runs_run_id");
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.RunId })
                .HasName("ak_extraction_runs_tenant_run_id");
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.SourceDocumentId).HasColumnName("source_document_id");
            entity.Property(x => x.RunId).HasColumnName("run_id");
            entity.Property(x => x.ExtractionJobId).HasColumnName("extraction_job_id");
            entity.Property(x => x.AttemptNumber).HasColumnName("attempt_number");
            entity.Property(x => x.ParserVersion).HasColumnName("parser_version").HasMaxLength(128);
            entity.Property(x => x.SchemaVersion).HasColumnName("schema_version").HasMaxLength(128);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.StartedOn).HasColumnName("started_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.CompletedOn).HasColumnName("completed_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.PageCount).HasColumnName("page_count");
            entity.Property(x => x.RegionCount).HasColumnName("region_count");
            entity.Property(x => x.InquiryCount).HasColumnName("inquiry_count");
            entity.Property(x => x.LineItemCount).HasColumnName("line_item_count");
            entity.Property(x => x.EvidenceCount).HasColumnName("evidence_count");
            entity.Property(x => x.FindingCount).HasColumnName("finding_count");
            entity.Property(x => x.FailureReason).HasColumnName("failure_reason").HasMaxLength(4_000);
            entity.Property(x => x.ProcessingPath).HasColumnName("processing_path").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.OcrStatus).HasColumnName("ocr_status").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.OcrPageCount).HasColumnName("ocr_page_count");
            entity.Property(x => x.OcrTruncated).HasColumnName("ocr_truncated");
            entity.Property(x => x.ProcessingCostAmount).HasColumnName("processing_cost_amount").HasPrecision(18, 6);
            entity.Property(x => x.ProcessingCostCurrency).HasColumnName("processing_cost_currency").HasMaxLength(3).IsFixedLength();
            entity.Property(x => x.ProcessingCostStatus).HasColumnName("processing_cost_status").HasMaxLength(32);
            entity.Property(x => x.OcrCostStatus).HasColumnName("ocr_cost_status").HasMaxLength(32);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExtractionJobId, x.AttemptNumber }).IsUnique()
                .HasDatabaseName("ux_extraction_runs_tenant_job_attempt");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Status, x.CreatedOn })
                .HasDatabaseName("ix_extraction_runs_tenant_status_created");
            entity.HasIndex(x => x.ExtractionJobId).HasDatabaseName("ix_extraction_runs_extraction_job");
            entity.HasOne(x => x.SourceDocument).WithMany(x => x.ExtractionRuns)
                .HasForeignKey(x => new { x.BusinessUnitId, x.SourceDocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureCorpus(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<DocumentCorpus>(entity =>
        {
            entity.ToTable("document_corpora", table =>
                table.HasCheckConstraint("ck_document_corpora_business_unit", "business_unit_id > 0"));
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_document_corpora_tenant_id");
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
                table.HasCheckConstraint("ck_source_documents_malware_verdict",
                    "(malware_verdict_status IS NULL AND malware_scanned_on IS NULL) OR (malware_verdict_status IN ('Clean','Infected','Unavailable','Error') AND malware_scanned_on IS NOT NULL AND malware_scanner_engine IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_source_documents_tenant_id");
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
            entity.Property(x => x.MalwareVerdictStatus).HasColumnName("malware_verdict_status").HasMaxLength(32);
            entity.Property(x => x.MalwareScannerEngine).HasColumnName("malware_scanner_engine").HasMaxLength(128);
            entity.Property(x => x.MalwareSignatureVersion).HasColumnName("malware_signature_version").HasMaxLength(256);
            entity.Property(x => x.MalwareScannedOn).HasColumnName("malware_scanned_on").HasColumnType("timestamp with time zone");
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
            entity.HasOne(x => x.Corpus).WithMany(x => x.Documents)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CorpusId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
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
                table.HasCheckConstraint("ck_document_pages_sheet_name",
                    "(page_kind = 'PhysicalPage' AND sheet_name IS NULL) OR (page_kind IN ('Worksheet', 'CsvSheet') AND sheet_name IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_document_pages_tenant_id");
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.DocumentId).HasColumnName("document_id");
            entity.Property(x => x.PageNumber).HasColumnName("page_number");
            entity.Property(x => x.Width).HasColumnName("width").HasPrecision(12, 4);
            entity.Property(x => x.Height).HasColumnName("height").HasPrecision(12, 4);
            entity.Property(x => x.Rotation).HasColumnName("rotation");
            entity.Property(x => x.TextHash).HasColumnName("text_hash").HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.PageKind).HasColumnName("page_kind").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.SheetName).HasColumnName("sheet_name").HasMaxLength(256);
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
            entity.HasOne(x => x.Document).WithMany(x => x.Pages)
                .HasForeignKey(x => new { x.BusinessUnitId, x.DocumentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
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
                table.HasCheckConstraint("ck_document_regions_coordinates",
                    "(row_number IS NULL OR row_number > 0) AND (column_number IS NULL OR column_number > 0)");
                table.HasCheckConstraint("ck_document_regions_source_address",
                    "(row_number IS NULL AND column_number IS NULL) OR source_address IS NOT NULL");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_document_regions_tenant_id");
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
            entity.Property(x => x.SourceAddress).HasColumnName("source_address").HasMaxLength(256);
            entity.Property(x => x.RowNumber).HasColumnName("row_number");
            entity.Property(x => x.ColumnNumber).HasColumnName("column_number");
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.PageId })
                .HasDatabaseName("ix_document_regions_tenant_page");
            entity.HasIndex(x => new { x.BusinessUnitId, x.RegionType })
                .HasDatabaseName("ix_document_regions_tenant_type");
            entity.HasIndex(x => new { x.PageId, x.SourceAddress })
                .HasDatabaseName("ix_document_regions_page_address");
            entity.HasOne(x => x.Page).WithMany(x => x.Regions)
                .HasForeignKey(x => new { x.BusinessUnitId, x.PageId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
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
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_canonical_inquiries_tenant_id");
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.CorpusId).HasColumnName("corpus_id");
            entity.Property(x => x.InquiryNumber).HasColumnName("inquiry_number");
            entity.Property(x => x.LeadId).HasColumnName("lead_id");
            entity.Property(x => x.CustomerRfqNumber).HasColumnName("customer_rfq_number").HasMaxLength(256);
            entity.Property(x => x.BuyerName).HasColumnName("buyer_name").HasMaxLength(512);
            entity.Property(x => x.ReceivedDate).HasColumnName("received_date").HasColumnType("timestamp with time zone");
            entity.Property(x => x.BidClosingDate).HasColumnName("bid_closing_date").HasColumnType("timestamp with time zone");
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
            entity.HasOne(x => x.Corpus).WithMany(x => x.Inquiries)
                .HasForeignKey(x => new { x.BusinessUnitId, x.CorpusId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
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
                table.HasCheckConstraint("ck_canonical_line_items_unit_price", "unit_price IS NULL OR unit_price >= 0");
                table.HasCheckConstraint("ck_canonical_line_items_lead_time", "lead_time_days IS NULL OR lead_time_days >= 0");
                table.HasCheckConstraint("ck_canonical_line_items_currency", "currency_code IS NULL OR currency_code ~ '^[A-Z]{3}$'");
            });
            entity.HasKey(x => x.Id);
            entity.HasAlternateKey(x => new { x.BusinessUnitId, x.Id })
                .HasName("ak_canonical_line_items_tenant_id");
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
            entity.Property(x => x.UnitPrice).HasColumnName("unit_price").HasPrecision(20, 6);
            entity.Property(x => x.LeadTimeDays).HasColumnName("lead_time_days");
            entity.Property(x => x.LeadItemId).HasColumnName("lead_item_id");
            entity.Property(x => x.ValidationStatus).HasColumnName("validation_status")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.RawPayload).HasColumnName("raw_payload").HasColumnType("jsonb");
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.Property(x => x.UpdatedOn).HasColumnName("updated_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.InquiryId, x.LineNumber }).IsUnique()
                .HasDatabaseName("ux_canonical_line_items_inquiry_line");
            entity.HasIndex(x => new { x.BusinessUnitId, x.InquiryId })
                .HasDatabaseName("ix_canonical_line_items_tenant_inquiry");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ManufacturerPartNumber })
                .HasDatabaseName("ix_canonical_line_items_tenant_mpn");
            entity.HasIndex(x => x.LeadItemId).HasDatabaseName("ix_canonical_line_items_lead_item");
            entity.HasOne(x => x.Inquiry).WithMany(x => x.LineItems)
                .HasForeignKey(x => new { x.BusinessUnitId, x.InquiryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });
    }

    private static void ConfigureValidationFinding(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ValidationFinding>(entity =>
        {
            entity.ToTable("validation_findings", table =>
            {
                table.HasCheckConstraint("ck_validation_findings_business_unit", "business_unit_id > 0");
                table.HasCheckConstraint("ck_validation_findings_target",
                    "NOT (inquiry_id IS NOT NULL AND line_item_id IS NOT NULL)");
            });
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id").ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessUnitId).HasColumnName("business_unit_id");
            entity.Property(x => x.ExtractionRunId).HasColumnName("extraction_run_id");
            entity.Property(x => x.InquiryId).HasColumnName("inquiry_id");
            entity.Property(x => x.LineItemId).HasColumnName("line_item_id");
            entity.Property(x => x.RegionId).HasColumnName("region_id");
            entity.Property(x => x.Code).HasColumnName("code").HasMaxLength(128);
            entity.Property(x => x.Severity).HasColumnName("severity").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.Message).HasColumnName("message").HasMaxLength(4_000);
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.ExtractionRunId, x.Severity })
                .HasDatabaseName("ix_validation_findings_tenant_run_severity");
            entity.HasIndex(x => new { x.BusinessUnitId, x.Code })
                .HasDatabaseName("ix_validation_findings_tenant_code");
            entity.HasOne(x => x.ExtractionRun).WithMany(x => x.Findings)
                .HasForeignKey(x => new { x.BusinessUnitId, x.ExtractionRunId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Inquiry).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.InquiryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.LineItem).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.LineItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Region).WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.RegionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
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
                table.HasCheckConstraint("ck_field_evidence_key", "evidence_key ~ '^[0-9a-f]{64}$'");
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
            entity.Property(x => x.EvidenceKey).HasColumnName("evidence_key").HasMaxLength(64).IsFixedLength();
            entity.Property(x => x.ValueKind).HasColumnName("value_kind").HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.ValidationStatus).HasColumnName("validation_status")
                .HasConversion<string>().HasMaxLength(32);
            entity.Property(x => x.TransformationsJson).HasColumnName("transformations").HasColumnType("jsonb");
            entity.Property(x => x.CreatedOn).HasColumnName("created_on").HasColumnType("timestamp with time zone");
            entity.HasIndex(x => new { x.BusinessUnitId, x.InquiryId, x.FieldName })
                .HasDatabaseName("ix_field_evidence_inquiry_field");
            entity.HasIndex(x => new { x.BusinessUnitId, x.LineItemId, x.FieldName })
                .HasDatabaseName("ix_field_evidence_line_field");
            entity.HasIndex(x => new { x.BusinessUnitId, x.RunId })
                .HasDatabaseName("ix_field_evidence_tenant_run");
            entity.HasIndex(x => new { x.BusinessUnitId, x.EvidenceKey }).IsUnique()
                .HasDatabaseName("ux_field_evidence_tenant_key");
            entity.HasIndex(x => x.RegionId).HasDatabaseName("ix_field_evidence_region");
            entity.HasOne(x => x.Region).WithMany(x => x.Evidence)
                .HasForeignKey(x => new { x.BusinessUnitId, x.RegionId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Inquiry).WithMany(x => x.Evidence)
                .HasForeignKey(x => new { x.BusinessUnitId, x.InquiryId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.LineItem).WithMany(x => x.Evidence)
                .HasForeignKey(x => new { x.BusinessUnitId, x.LineItemId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ExtractionRun).WithMany(x => x.Evidence)
                .HasForeignKey(x => new { x.BusinessUnitId, x.RunId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.RunId }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
