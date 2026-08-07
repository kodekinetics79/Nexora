using ERP_RFQ_Automation.Security;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Notifications;

/// <summary>
/// EF configuration for the platform's outbound email identity, kept in the owning module so the
/// context needs exactly one delegating call — the same splice discipline
/// <c>ApplyTenantOnboardingModel</c> and <c>ApplyPlatformSupportModel</c> already use.
///
/// <para>Platform schema, and no global query filter: this is control-plane configuration with no
/// business unit to key on, and a filter would enrol it in an RLS expectation it cannot satisfy.</para>
/// </summary>
public static class PlatformEmailModelBuilderExtensions
{
    public static ModelBuilder ApplyPlatformEmailModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformEmailSettings>(entity =>
        {
            // The check constraint is the single-row rule. Enforcing it in application code would
            // mean the rule holds only for callers who remember it, and the failure mode of a second
            // row is not a duplicate — it is two answers to "which address does Nexora send from".
            entity.ToTable("PlatformEmailSettings", "platform", table =>
                table.HasCheckConstraint(
                    "CK_PlatformEmailSettings_Singleton",
                    $"\"Id\" = {PlatformEmailSettings.SingletonId}"));

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();

            entity.Property(x => x.Provider).IsRequired().HasMaxLength(32).HasDefaultValue("console");
            entity.Property(x => x.FromAddress).IsRequired().HasMaxLength(320);
            entity.Property(x => x.FromName).IsRequired().HasMaxLength(200);
            entity.Property(x => x.ReplyToAddress).HasMaxLength(320);
            entity.Property(x => x.AppBaseUrl).IsRequired().HasMaxLength(512);

            entity.Property(x => x.SmtpHost).HasMaxLength(255);
            entity.Property(x => x.SmtpUsername).HasMaxLength(320);

            // Deliberately NO store default on SmtpEnableSsl. A database default of true on a bool
            // whose CLR default is false is the shape where EF omits the column from the INSERT and
            // the row silently comes back with TLS enabled after an operator turned it off. The
            // property initialiser carries the default instead, where it cannot be second-guessed.

            // Both credentials go through the SAME converter that protects the per-tenant mailbox
            // password (Security/ProtectedSecretConverter, AES-256-GCM with a per-value nonce).
            // Applying it at the persistence boundary rather than in the service is what makes it
            // impossible to forget: every read and every write of these two properties passes
            // through it, including one written by code that has never heard of encryption.
            // 2048 is the same width the mailbox column was widened to — the envelope is roughly
            // 1.4x the plaintext plus ~36 characters of framing.
            entity.Property(x => x.SmtpPassword)
                .HasMaxLength(ProtectedSecretConverter.ProtectedColumnMaxLength)
                .HasConversion(new ProtectedSecretConverter());
            entity.Property(x => x.SendGridApiKey)
                .HasMaxLength(ProtectedSecretConverter.ProtectedColumnMaxLength)
                .HasConversion(new ProtectedSecretConverter());

            entity.Property(x => x.SendGridApiBaseUrl).HasMaxLength(512);

            entity.Property(x => x.OutboundGuardMode).IsRequired().HasMaxLength(32).HasDefaultValue("Live");
            entity.Property(x => x.OutboundGuardRedirectTo).HasMaxLength(320);
            entity.Property(x => x.OutboundGuardAllowedRecipients).HasMaxLength(4000);
            entity.Property(x => x.OutboundGuardAllowedDomains).HasMaxLength(4000);
            entity.Property(x => x.OutboundGuardSubjectTag).HasMaxLength(64);

            // The UPDATE's WHERE clause, not a read-then-compare. Two operators editing outbound
            // credentials from two tabs is ordinary; without this the second save wins silently and
            // the first operator's password is gone with no error and no evidence.
            entity.Property(x => x.Version).IsConcurrencyToken();

            entity.Property(x => x.UpdatedBy).HasMaxLength(320);
            entity.Property(x => x.UpdateReason).HasMaxLength(1000);
            entity.Property(x => x.LastVerifiedBy).HasMaxLength(320);
            entity.Property(x => x.LastVerifiedRecipient).HasMaxLength(320);
            entity.Property(x => x.LastFailureKind).HasMaxLength(48);
            entity.Property(x => x.LastFailureReason).HasMaxLength(1000);
        });

        return modelBuilder;
    }
}
