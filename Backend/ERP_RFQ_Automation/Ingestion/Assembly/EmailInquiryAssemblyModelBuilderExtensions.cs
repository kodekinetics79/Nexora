using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Schema for the email-assembly aggregate.
///
/// <para><b>Where tenant isolation actually lives.</b> The RLS policies, role grants and the
/// tenant-purge policy are in migration <c>20260813134002_EmailInquiryAssembly</c> as hand-written
/// SQL; the EF query filters are NOT here — they are declared with the other tenant entities in
/// <c>ErpRfqAutomationContext.Tenancy.cs</c>. That migration's comment says the filter is "added
/// alongside" the policies, which reads as though it is in this file. It is not, and a reviewer
/// looking here concluded no filter existed at all. The migration is left byte-identical on this
/// branch because it carries governed SQL no model diff can reproduce, so the correction is
/// recorded here instead and the migration comment is fixed by the next change that legitimately
/// edits that file.</para>
///
/// <para>RLS is the boundary; the query filter is a convenience. The pipeline role is
/// <c>BYPASSRLS</c>, so on the worker path the explicit <c>BusinessUnitId</c> predicate in each
/// query is the only thing between tenants — write it every time.</para>
/// </summary>
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
            // No HasDefaultValue: it marks the property ValueGenerated.OnAdd, so an assembly
            // constructed with ManifestContractVersion = 0 - a forgotten assignment - would be
            // silently stored as 1 instead of as an obviously wrong 0, defeating the mismatch
            // detector a second way. The migration's column default still backfills historic rows.
            e.Property(x => x.ManifestContractVersion).IsRequired();
            e.Property(x => x.RawEvidenceUri).HasMaxLength(1024);
            e.Property(x => x.RawEvidenceSha256).HasMaxLength(64).IsFixedLength();
            e.Property(x => x.RawEvidenceVersionId).HasMaxLength(256);
            e.Property(x => x.SenderAddress).HasMaxLength(512);
            e.Property(x => x.RecipientsJson).HasColumnType("jsonb");
            e.Property(x => x.Subject).HasMaxLength(1000);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.StatusReason).HasMaxLength(1000);
            e.Property(x => x.SkippedPartsJson).HasColumnType("jsonb");
            e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();

            // The barrier's recovery read: every message that finished its components but has
            // not yet produced its Lead. A sweeper cannot find a stranded message without it.
            e.HasIndex(x => new { x.BusinessUnitId, x.AssembledLeadId });

            e.HasOne(x => x.EmailIngest)
                .WithOne()
                .HasForeignKey<EmailInquiryAssembly>(x => x.EmailIngestId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<EmailInquiryComponent>(e =>
        {
            e.ToTable("EmailInquiryComponents");
            e.HasKey(x => x.Id);

            // Same reason as the assembly's: children — the result store, and the extraction
            // job that owns one — carry the tenant INSIDE their foreign key, so a row can
            // never point at another tenant's component and the RLS predicate on the child
            // needs no join to be enforceable.
            e.HasAlternateKey(x => new { x.BusinessUnitId, x.Id });

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
            e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();

            e.HasOne(x => x.Assembly)
                .WithMany(x => x.Components)
                .HasForeignKey(x => new { x.BusinessUnitId, x.AssemblyId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EmailInquiryComponentResult>(e =>
        {
            e.ToTable("EmailInquiryComponentResults");
            e.HasKey(x => x.Id);

            // ONE result per component. A second extraction of the same part must UPDATE this
            // row; appending would let the barrier read two contradictory answers for one
            // attachment and pick whichever the query ordering happened to return.
            e.HasIndex(x => new { x.BusinessUnitId, x.ComponentId }).IsUnique();

            // The barrier's read: every result for one message, in one indexed scan.
            e.HasIndex(x => new { x.BusinessUnitId, x.AssemblyId });

            e.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            e.Property(x => x.PayloadContractVersion).IsRequired();
            e.Property(x => x.ProcessingPath).HasMaxLength(48).IsRequired();
            e.Property(x => x.AiProviderClass).HasMaxLength(24);
            e.Property(x => x.ModelIdentifier).HasMaxLength(256);
            e.Property(x => x.HeaderConfidence).HasPrecision(5, 4);
            e.Property(x => x.ReviewReason).HasMaxLength(1000);
            e.Property(x => x.DiagnosticsJson).HasColumnType("jsonb");
            e.Property(x => x.ConcurrencyVersion).IsConcurrencyToken();

            e.HasOne(x => x.Component)
                .WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.ComponentId })
                .HasPrincipalKey(x => new { x.BusinessUnitId, x.Id })
                // CASCADE, matching the component's own cascade from the assembly: a purged
                // message must not leave its extraction payloads behind. Those payloads are
                // derived from customer content and are exactly what a deletion request means.
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
