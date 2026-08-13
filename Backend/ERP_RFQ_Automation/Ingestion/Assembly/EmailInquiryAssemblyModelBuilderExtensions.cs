using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

public static class EmailInquiryAssemblyModelBuilderExtensions
{
    public static void ConfigureEmailInquiryAssembly(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EmailInquiryAssembly>(e =>
        {
            e.ToTable("EmailInquiryAssemblies");
            e.HasKey(x => x.Id);
            // Composite alternate key so children can carry the tenant in their FK and the RLS
            // predicate on the child table is enforceable without a join.
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

            // ONE assembly per ingest. This is the whole point of the aggregate, so it is a
            // database constraint rather than an application convention: a replay that raced
            // itself must fail on insert, not create a second view of the same message.
            e.HasIndex(x => x.EmailIngestId).IsUnique();

            // The replay lookup. A poller that re-reads a message resolves the existing
            // assembly by (tenant, mailbox, message key) before deciding to do anything.
            e.HasIndex(x => new { x.BusinessUnitId, x.EmailConfigurationId, x.MessageKey }).IsUnique();

            // The recovery sweep reads exactly this.
            e.HasIndex(x => new { x.BusinessUnitId, x.Status, x.UpdatedAtUtc });

            e.Property(x => x.MessageKey).HasMaxLength(255).IsRequired();
            e.Property(x => x.RawEvidenceUri).HasMaxLength(1024);
            e.Property(x => x.RawEvidenceSha256).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.RawEvidenceVersionId).HasMaxLength(256);
            e.Property(x => x.SenderAddress).HasMaxLength(512);
            e.Property(x => x.RecipientsJson).HasColumnType("jsonb");
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.StatusReason).HasMaxLength(1000);
            e.Property(x => x.SkippedPartsJson).HasColumnType("jsonb");
            e.Property(x => x.Version).IsConcurrencyToken();

            e.HasOne(x => x.EmailIngest)
                .WithOne()
                .HasForeignKey<EmailInquiryAssembly>(x => x.EmailIngestId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EmailInquiryComponent>(e =>
        {
            e.ToTable("EmailInquiryComponents");
            e.HasKey(x => x.Id);

            // Idempotent replay: the second pass over a message updates these rows rather than
            // appending a parallel set. Without this a retried poll would double every
            // component and the barrier would wait forever for siblings that already finished.
            //
            // Keyed on ComponentKey, not Ordinal — the key is derived from the message itself
            // and therefore survives a restart, whereas an ordinal is only stable if the walk
            // that produced it ran to completion. Ordinal keeps its own index for display order.
            e.HasIndex(x => new { x.BusinessUnitId, x.AssemblyId, x.ComponentKey }).IsUnique();
            e.HasIndex(x => new { x.BusinessUnitId, x.AssemblyId, x.Ordinal }).IsUnique();

            // The worker resolves a component from the job it is processing.
            e.HasIndex(x => new { x.BusinessUnitId, x.ExtractionJobId });
            e.HasIndex(x => new { x.BusinessUnitId, x.Status });

            e.Property(x => x.ComponentKey).HasMaxLength(512).IsRequired();
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.FileName).HasMaxLength(512);
            e.Property(x => x.MimeType).HasMaxLength(255);
            e.Property(x => x.ContentHash).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.EvidenceUri).HasMaxLength(1024);
            e.Property(x => x.ReasonCode).HasMaxLength(64);
            e.Property(x => x.ReasonDetail).HasMaxLength(1000);
            e.Property(x => x.Version).IsConcurrencyToken();

            e.HasOne(x => x.Assembly)
                .WithMany(x => x.Components)
                .HasForeignKey(x => new { x.BusinessUnitId, x.AssemblyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
