using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// EF configuration for the operator support desk, kept in the owning module so
/// <c>ErpRfqAutomationContext.Tenancy.cs</c> needs exactly one delegating call — the same splice
/// discipline <c>ApplyTenantOnboardingModel</c> / <c>ConfigureCommercialFinance</c> already use.
///
/// <para>Every table lands in the <c>platform</c> schema and none carries a global query filter.
/// See <see cref="SupportTicket"/> for why: these are control-plane records ABOUT tenants, written
/// and read by requests that hold no tenant scope, and a filter would enrol them in an RLS
/// expectation that has no business unit to key on.</para>
/// </summary>
public static class PlatformSupportModelBuilderExtensions
{
    public static ModelBuilder ApplyPlatformSupportModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SupportTicket>(entity =>
        {
            entity.ToTable("SupportTickets", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Subject).IsRequired().HasMaxLength(200);
            entity.Property(x => x.Body).HasMaxLength(8000);
            entity.Property(x => x.Resolution).HasMaxLength(4000);
            entity.Property(x => x.RequesterEmail).HasMaxLength(320);
            entity.Property(x => x.RedactedReason).HasMaxLength(200);

            // Enums stored as their NAMES, for Tenant.BillingMode's reason: a ticket closed two
            // years ago has to stay explainable, and reordering an enum must never silently
            // reclassify history that has already been quoted to a customer.
            entity.Property(x => x.Severity).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.Origin).HasConversion<string>().HasMaxLength(16).IsRequired();

            // The UPDATE's WHERE clause, not a read-then-write check. Two operators triaging the
            // same ticket from two tabs is ordinary; without this the second write wins silently
            // and the audit trail shows two transitions out of the same starting state.
            entity.Property(x => x.Version).IsConcurrencyToken();

            // RESTRICT, emphatically not CASCADE. A support ticket is the record of what Nexora
            // did for a customer, in the same class as the audit log, and a tenant DELETE that
            // quietly took the support history with it would destroy exactly the evidence a
            // disputed offboarding needs. Purge erases ticket CONTENT through
            // ISupportTicketRedactionService and leaves the rows; a raw delete fails loudly.
            entity.HasOne<Models.Tenant>()
                .WithMany()
                .HasForeignKey(x => x.TenantId)
                .OnDelete(DeleteBehavior.Restrict);

            // Assignee and opener are RESTRICT for a narrower reason: platform operators are
            // deactivated (PlatformUser.IsActive), never deleted, so the constraint costs nothing
            // and stops a hypothetical hard delete from blanking the "who was responsible" column
            // on every ticket that operator ever touched.
            entity.HasOne<Models.PlatformUser>()
                .WithMany()
                .HasForeignKey(x => x.AssignedToPlatformUserId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Models.PlatformUser>()
                .WithMany()
                .HasForeignKey(x => x.OpenedByPlatformUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // The tenant operations summary and the per-tenant queue: "this customer's tickets,
            // most recently touched first".
            entity.HasIndex(x => new { x.TenantId, x.Status, x.UpdatedAtUtc })
                .HasDatabaseName("IX_SupportTickets_Tenant_Status_Updated");

            // The desk's own queue: "what is assigned to me", and — with a NULL assignee — the
            // unassigned backlog, which is the query an ops lead actually runs each morning.
            entity.HasIndex(x => new { x.AssignedToPlatformUserId, x.Status, x.UpdatedAtUtc })
                .HasDatabaseName("IX_SupportTickets_Assignee_Status_Updated");

            // Cross-tenant triage by urgency: "every live Critical, oldest first".
            entity.HasIndex(x => new { x.Status, x.Severity, x.CreatedAtUtc })
                .HasDatabaseName("IX_SupportTickets_Status_Severity_Created");
        });

        modelBuilder.Entity<SupportTicketNote>(entity =>
        {
            entity.ToTable("SupportTicketNotes", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.AuthorLabel).IsRequired().HasMaxLength(320);
            entity.Property(x => x.Body).IsRequired().HasMaxLength(8000);
            entity.Property(x => x.AuthorKind).HasConversion<string>().HasMaxLength(16).IsRequired();
            entity.Property(x => x.IsInternal).HasDefaultValue(true);

            // CASCADE here and only here. A note has no meaning apart from its ticket, and the
            // ticket row itself is never deleted (its tenant FK is RESTRICT), so the cascade can
            // only ever fire from the erasure path that is deleting the notes on purpose.
            entity.HasOne(x => x.Ticket)
                .WithMany(x => x.Notes)
                .HasForeignKey(x => x.SupportTicketId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.SupportTicketId, x.CreatedAtUtc })
                .HasDatabaseName("IX_SupportTicketNotes_Ticket_Created");
        });

        modelBuilder.Entity<SupportTicketLink>(entity =>
        {
            entity.ToTable("SupportTicketLinks", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24).IsRequired();
            entity.Property(x => x.TargetKey).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Note).HasMaxLength(512);
            entity.Property(x => x.LinkedByLabel).IsRequired().HasMaxLength(320);

            entity.HasOne(x => x.Ticket)
                .WithMany(x => x.Links)
                .HasForeignKey(x => x.SupportTicketId)
                .OnDelete(DeleteBehavior.Cascade);

            // UNIQUE so that clicking "attach" twice is idempotent at the database rather than
            // producing a detail page that lists the same impersonation session three times.
            entity.HasIndex(x => new { x.SupportTicketId, x.Kind, x.TargetKey })
                .IsUnique()
                .HasDatabaseName("UX_SupportTicketLinks_Ticket_Kind_Target");

            // The reverse question, which is the one an auditor asks: "this impersonation session
            // — which ticket authorised it?"
            entity.HasIndex(x => new { x.Kind, x.TargetKey })
                .HasDatabaseName("IX_SupportTicketLinks_Kind_Target");
        });

        return modelBuilder;
    }
}
