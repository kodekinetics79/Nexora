using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Security.PasswordReset;

/// <summary>
/// EF configuration for the password-reset table, kept in the owning module rather than in
/// <c>ErpRfqAutomationContext.Tenancy.cs</c> so the context needs exactly one delegating call —
/// the same splice discipline <c>ApplyTenantOnboardingModel</c> already uses.
/// </summary>
public static class PasswordResetModelBuilderExtensions
{
    public static ModelBuilder ApplyPasswordResetModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PasswordResetToken>(entity =>
        {
            // Platform schema, alongside TenantAdminInvitations, and carrying NO global query
            // filter. Both halves of that are load-bearing: the row is written and read on
            // anonymous requests that have no tenant scope, and a filter would enrol the table
            // in the RLS-policy expectation of PostgreSqlProductionDialectTests, which no
            // pre-authentication table can satisfy. See the entity docs.
            entity.ToTable("PasswordResetTokens", "platform");
            entity.HasKey(x => x.Id);

            // Lowercase hex SHA-256: fixed 64 characters, so the column is fixed-width and the
            // unique index below is the whole lookup.
            entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);

            // IPv6 with a zone index, worst case — same width as the invitation's.
            entity.Property(x => x.RequestedFromIp).HasMaxLength(64);
            entity.Property(x => x.RedeemedFromIp).HasMaxLength(64);

            entity.Property(x => x.RevokedBy).HasMaxLength(256);
            entity.Property(x => x.RevocationReason).HasMaxLength(1000);

            // UNIQUE is load-bearing twice over, exactly as it is for invitations. It makes the
            // lookup a single indexed exact match (no scan, nothing to time), and it makes a
            // token collision a database error rather than a link that quietly resets the
            // wrong person's password.
            entity.HasIndex(x => x.TokenHash)
                .IsUnique()
                .HasDatabaseName("UX_PasswordResetTokens_TokenHash");

            // "Is there a live reset for this user?" — read by the request path (to supersede
            // whatever was outstanding before minting a new one) and by completion (to spend
            // every sibling in the same transaction, so a second email cannot re-set the
            // password afterwards).
            entity.HasIndex(x => new { x.UserId, x.RedeemedAtUtc, x.RevokedAtUtc })
                .HasDatabaseName("IX_PasswordResetTokens_UserId_Live");

            // Supports pruning expired, never-redeemed rows. Matters more here than for
            // invitations: an anonymous caller can cause rows, so the table grows on request
            // volume rather than on operator actions.
            entity.HasIndex(x => x.ExpiresAtUtc)
                .HasDatabaseName("IX_PasswordResetTokens_ExpiresAtUtc");

            // The purge's index. TenantPurgeExecutor deletes by TenantId, and without this the
            // deletion of a large tenant's rows is a sequential scan of every reset ever issued.
            entity.HasIndex(x => x.TenantId)
                .HasDatabaseName("IX_PasswordResetTokens_TenantId");
        });

        return modelBuilder;
    }
}
