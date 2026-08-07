using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Onboarding;

/// <summary>
/// EF configuration for the tenant-onboarding tables, kept in the owning module rather than in
/// <c>ErpRfqAutomationContext.Tenancy.cs</c> so the context needs exactly one delegating call —
/// the same splice discipline <c>ConfigureCommercialFinance</c> / <c>ConfigureGovernedCustomFields</c>
/// already use.
/// </summary>
public static class TenantOnboardingModelBuilderExtensions
{
    public static ModelBuilder ApplyTenantOnboardingModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantAdminInvitation>(entity =>
        {
            // Platform schema, alongside ImpersonationSessions: a control-plane record about a
            // tenant, written before the tenant has a usable identity and read on an anonymous
            // request. See the entity docs for why it carries NO global query filter — adding
            // one would enrol it in the RLS-policy expectation of
            // PostgreSqlProductionDialectTests, which no pre-authentication table can satisfy.
            entity.ToTable("TenantAdminInvitations", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Email).IsRequired().HasMaxLength(256);
            // Matches Tenant.Name's width; see TenantAdminInvitation.TenantName for why the
            // activation page reads a copy instead of joining to platform."Tenants".
            entity.Property(x => x.TenantName).IsRequired().HasMaxLength(256);
            entity.Property(x => x.IssuedBy).IsRequired().HasMaxLength(256);
            entity.Property(x => x.RevokedBy).HasMaxLength(256);
            entity.Property(x => x.RevocationReason).HasMaxLength(1000);
            // IPv6 with a zone index, worst case.
            entity.Property(x => x.RedeemedFromIp).HasMaxLength(64);

            // Lowercase hex SHA-256: fixed 64 characters, so the column is fixed-width and the
            // unique index below is the whole redemption lookup.
            entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);

            // UNIQUE is load-bearing twice over. It makes redemption a single indexed exact
            // match (no scan, nothing to time), and it makes a token collision a database
            // error rather than an invitation that quietly activates the wrong account.
            entity.HasIndex(x => x.TokenHash)
                .IsUnique()
                .HasDatabaseName("UX_TenantAdminInvitations_TokenHash");

            // "Show me this tenant's invitations, newest first" — the operator's resend/revoke screen.
            entity.HasIndex(x => new { x.TenantId, x.IssuedAtUtc })
                .HasDatabaseName("IX_TenantAdminInvitations_TenantId_IssuedAtUtc");

            // "Is there a live invitation for this user?" — read by reissue (to supersede the
            // previous token) and by redemption (to spend any sibling link in the same
            // transaction, so a second outstanding email cannot re-set the password afterwards).
            entity.HasIndex(x => new { x.UserId, x.RedeemedAtUtc, x.RevokedAtUtc })
                .HasDatabaseName("IX_TenantAdminInvitations_UserId_Live");

            // Supports pruning expired, never-redeemed rows.
            entity.HasIndex(x => x.ExpiresAtUtc)
                .HasDatabaseName("IX_TenantAdminInvitations_ExpiresAtUtc");
        });

        return modelBuilder;
    }
}
