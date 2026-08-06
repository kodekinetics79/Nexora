using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.LeadIdentity;

public static class LeadIdentityModelBuilderExtensions
{
    public static void ConfigureLeadIdentity(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LeadIngestionBatch>(e =>
        {
            e.ToTable("LeadIngestionBatches"); e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.SourceChannel).HasMaxLength(32); e.Property(x => x.CreatedBy).HasMaxLength(256);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.BusinessUnitId, x.CreatedAtUtc });
        });
        modelBuilder.Entity<LeadIngestionOccurrence>(e =>
        {
            e.ToTable("LeadIngestionOccurrences"); e.HasKey(x => x.Id);
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.SourceChannel).HasMaxLength(32); e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.Property(x => x.ExternalSourceId).HasMaxLength(512); e.Property(x => x.EmailThreadId).HasMaxLength(512);
            e.Property(x => x.LogicalGroupKey).HasMaxLength(256);
            e.Property(x => x.SourceSystem).HasMaxLength(128); e.Property(x => x.Sender).HasMaxLength(512);
            e.Property(x => x.Subject).HasMaxLength(1000); e.Property(x => x.OriginalFileName).HasMaxLength(512);
            e.Property(x => x.MimeType).HasMaxLength(255); e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.CustomerScopeKey).HasMaxLength(256); e.Property(x => x.LogicalInquiryFingerprint).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.RecordKind).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Classification).HasConversion<string>().HasMaxLength(48);
            e.Property(x => x.ProcessingPath).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Confidence).HasPrecision(6, 5); e.Property(x => x.ExternalCost).HasPrecision(18, 6);
            e.Property(x => x.DecisionReasonsJson).HasColumnType("jsonb"); e.Property(x => x.PolicyVersion).HasMaxLength(64);
            e.Property(x => x.ActorType).HasMaxLength(32); e.Property(x => x.ActorId).HasMaxLength(256);
            e.Property(x => x.CorrelationId).HasMaxLength(128); e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique();
            e.HasIndex(x => new { x.BusinessUnitId, x.ContentHash, x.CustomerScopeKey });
            e.HasIndex(x => new { x.BusinessUnitId, x.LogicalInquiryFingerprint });
            e.HasIndex(x => new { x.BusinessUnitId, x.Classification, x.CreatedAtUtc });
            e.HasIndex(x => new { x.BusinessUnitId, x.SourceDocumentOccurrenceId });
            // The three analytics readers all filter on RecordKind before windowing.
            e.HasIndex(x => new { x.BusinessUnitId, x.RecordKind });
            e.HasOne(x => x.Batch).WithMany(x => x.Occurrences).HasForeignKey(x => new { x.BusinessUnitId, x.BatchId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Lead).WithMany(x => x.IngestionOccurrences).HasForeignKey(x => new { x.BusinessUnitId, x.LeadId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadOccurrenceDocument>(e =>
        {
            e.ToTable("LeadOccurrenceDocuments"); e.HasKey(x => x.Id);
            e.Property(x => x.Role).HasMaxLength(40).IsRequired();
            e.HasIndex(x => new { x.BusinessUnitId, x.OccurrenceId, x.SourceDocumentId }).IsUnique();
            e.HasOne(x => x.Occurrence).WithMany(x => x.Documents)
                .HasForeignKey(x => new { x.BusinessUnitId, x.OccurrenceId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadRevision>(e =>
        {
            e.ToTable("LeadRevisions"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.LogicalInquiryFingerprint).HasMaxLength(64).IsFixedLength(); e.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            e.Property(x => x.CustomerRfqReference).HasMaxLength(256); e.Property(x => x.NormalizedCustomerRfqReference).HasMaxLength(256);
            e.Property(x => x.CreatedBy).HasMaxLength(256); e.Property(x => x.ProcessingPath).HasConversion<string>().HasMaxLength(32);
            e.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.RevisionNumber }).IsUnique();
            e.HasIndex(x => new { x.BusinessUnitId, x.LogicalInquiryFingerprint });
            e.HasOne(x => x.Lead).WithMany(x => x.Revisions).HasForeignKey(x => new { x.BusinessUnitId, x.LeadId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.EstablishedByOccurrence).WithOne(x => x.Revision).HasForeignKey<LeadRevision>(x => new { x.BusinessUnitId, x.EstablishedByOccurrenceId }).HasPrincipalKey<LeadIngestionOccurrence>(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadItemRevision>(e =>
        {
            e.ToTable("LeadItemRevisions"); e.HasKey(x => x.Id); e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });
            e.Property(x => x.LineFingerprint).HasMaxLength(64).IsFixedLength(); e.Property(x => x.SnapshotJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.BusinessUnitId, x.LeadRevisionId, x.LineNumber }).IsUnique();
            e.HasOne(x => x.Revision).WithMany(x => x.Items).HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadRevisionDifference>(e =>
        {
            e.ToTable("LeadRevisionDifferences"); e.HasKey(x => x.Id); e.Property(x => x.ChangeType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Scope).HasMaxLength(32); e.Property(x => x.Path).HasMaxLength(512); e.Property(x => x.PreviousValueJson).HasColumnType("jsonb"); e.Property(x => x.CurrentValueJson).HasColumnType("jsonb");
            e.HasOne(x => x.Revision).WithMany(x => x.Differences).HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadMatchCandidate>(e =>
        {
            e.ToTable("LeadMatchCandidates"); e.HasKey(x => x.Id); e.Property(x => x.Confidence).HasPrecision(6, 5);
            e.Property(x => x.MatchEvidenceJson).HasColumnType("jsonb"); e.Property(x => x.DifferencesJson).HasColumnType("jsonb"); e.Property(x => x.DownstreamImpactJson).HasColumnType("jsonb");
            e.Property(x => x.ProposedLeadSnapshotJson).HasColumnType("jsonb");
            e.Property(x => x.ReviewState).HasConversion<string>().HasMaxLength(32); e.Property(x => x.ReviewedBy).HasMaxLength(256); e.Property(x => x.ReviewReason).HasMaxLength(2000); e.Property(x => x.Version).IsConcurrencyToken();
            e.HasIndex(x => new { x.BusinessUnitId, x.OccurrenceId, x.CandidateLeadId }).IsUnique();
            e.HasOne(x => x.Occurrence).WithMany(x => x.MatchCandidates).HasForeignKey(x => new { x.BusinessUnitId, x.OccurrenceId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.CandidateLead).WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CandidateLeadId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<LeadIdentityAuditEvent>(e =>
        {
            e.ToTable("LeadIdentityAuditEvents"); e.HasKey(x => x.Id); e.Property(x => x.EventType).HasMaxLength(64); e.Property(x => x.PayloadJson).HasColumnType("jsonb");
            e.Property(x => x.ActorType).HasMaxLength(32); e.Property(x => x.ActorId).HasMaxLength(256); e.Property(x => x.CorrelationId).HasMaxLength(128); e.Property(x => x.IdempotencyKey).HasMaxLength(256);
            e.HasIndex(x => new { x.BusinessUnitId, x.IdempotencyKey }).IsUnique(); e.HasIndex(x => new { x.BusinessUnitId, x.LeadId, x.OccurredAtUtc });
        });
        modelBuilder.Entity<LeadRevisionImpact>(e =>
        {
            e.ToTable("LeadRevisionImpacts"); e.HasKey(x => x.Id);
            e.Property(x => x.AggregateType).HasMaxLength(32); e.Property(x => x.ImpactType).HasMaxLength(64);
            e.Property(x => x.Status).HasMaxLength(32); e.Property(x => x.DetailsJson).HasColumnType("jsonb");
            e.HasIndex(x => new { x.BusinessUnitId, x.LeadRevisionId, x.AggregateType, x.AggregateId, x.ImpactType }).IsUnique();
            e.HasOne<LeadRevision>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadRevisionId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Models.Lead>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.LeadId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<Models.Lead>(e =>
        {
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id }).HasName("AK_Leads_BusinessUnitID_ID");
            e.Property(x => x.CurrentInquiryFingerprint).HasMaxLength(64).IsFixedLength(); e.Property(x => x.CurrentOccurrenceClassification).HasMaxLength(48);
            e.Property(x => x.IdentityVersion).IsConcurrencyToken(); e.HasIndex(x => new { x.BusinessUnitId, x.CurrentInquiryFingerprint });
            e.HasOne<LeadRevision>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CurrentRevisionId }).HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Models.Customer>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.CustomerId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, x.Id }).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Models.Contact>().WithMany().HasForeignKey(x => new { x.BusinessUnitId, x.ContactId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
