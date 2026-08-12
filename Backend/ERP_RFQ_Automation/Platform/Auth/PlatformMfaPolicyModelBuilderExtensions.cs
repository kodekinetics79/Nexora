using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Auth;

/// <summary>
/// EF configuration for the platform MFA enforcement policy and the browser-trust ledger, kept in
/// the owning module so the context needs exactly one delegating call — the same splice discipline
/// <c>ApplyPlatformEmailModel</c> and <c>ApplyPlatformSupportModel</c> already use.
///
/// <para>Platform schema, and no global query filter: this is control-plane security configuration
/// with no business unit to key on, and a filter would enrol it in an RLS expectation it cannot
/// satisfy (see the note on <c>ConfigureIamAuditTrail</c> for what that auto-enrolment costs).</para>
/// </summary>
public static class PlatformMfaPolicyModelBuilderExtensions
{
    public static ModelBuilder ApplyPlatformMfaPolicyModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformMfaPolicy>(entity =>
        {
            // The check constraint is the single-row rule, in the database rather than in whichever
            // service remembers it. Two rows would not be a duplicate: they would be two answers to
            // "is MFA enforced on this platform", and the one that wins would depend on ordering.
            entity.ToTable("PlatformMfaPolicies", "platform", table =>
            {
                table.HasCheckConstraint(
                    "CK_PlatformMfaPolicies_Singleton",
                    $"\"Id\" = {PlatformMfaPolicy.SingletonId}");

                // The browser-trust window is bounded in the DATABASE and not only in the service
                // that writes it. The service refuses an out-of-range value with a sentence an
                // Owner can act on; this refuses the UPDATE that never went through the service —
                // a hand-edited row, a restore, a future admin script — because "remember this
                // browser forever" is exactly the value somebody would set by hand.
                table.HasCheckConstraint(
                    "CK_PlatformMfaPolicies_BrowserTrustHours",
                    $"\"BrowserTrustHours\" BETWEEN {PlatformMfaPolicyOptions.MinBrowserTrustHours} " +
                    $"AND {PlatformMfaPolicyOptions.MaxBrowserTrustHours}");
            });

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();

            // Stored as the enum NAME with a store default of REQUIRED. Both halves matter: the
            // name so a reordering of PlatformMfaMode cannot reclassify a deployment from enforced
            // to disabled, and the default so a row inserted by a path that forgets the column
            // fails CLOSED rather than open.
            entity.Property(x => x.Mode).IsRequired().HasMaxLength(32)
                .HasDefaultValue(nameof(PlatformMfaMode.REQUIRED));

            entity.Property(x => x.EnvironmentClass).IsRequired().HasMaxLength(32)
                .HasDefaultValue(nameof(PlatformMfaEnvironmentClass.Production));

            entity.Property(x => x.ChangedBy).HasMaxLength(320);
            entity.Property(x => x.ChangeReason).HasMaxLength(1000);

            // Store defaults on both, for the same reason Mode carries one: a row inserted by a path
            // that does not know these columns exist gets the shipped behaviour rather than
            // false/0 — and 0 hours would be a trust that expires before it is written.
            entity.Property(x => x.BrowserTrustEnabled).HasDefaultValue(true);
            entity.Property(x => x.BrowserTrustHours)
                .HasDefaultValue(PlatformMfaPolicyOptions.DefaultBrowserTrustHours);

            // The UPDATE's WHERE clause, not a read-then-compare. Two Owners relaxing enforcement
            // from two tabs is exactly the situation where a silent last-write-wins would leave the
            // LONGER bypass in force with the shorter one's audit row sitting beside it.
            entity.Property(x => x.Version).IsConcurrencyToken();
        });

        modelBuilder.Entity<PlatformBrowserTrust>(entity =>
        {
            entity.ToTable("PlatformBrowserTrusts", "platform");
            entity.HasKey(x => x.Id);

            // Unique so a hash collision — or a replayed insert — cannot produce two live trusts
            // that revoking one of them would appear to have handled.
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64).IsFixedLength();

            entity.Property(x => x.Label).HasMaxLength(200);
            entity.Property(x => x.RevokedBy).HasMaxLength(320);
            entity.Property(x => x.RevocationReason).HasMaxLength(256);

            // The two read paths: "is this presented token live" (covered by the unique index above)
            // and "list/revoke everything this operator trusts".
            entity.HasIndex(x => new { x.PlatformUserId, x.RevokedAtUtc, x.ExpiresAtUtc });

            entity.HasOne(x => x.PlatformUser).WithMany()
                .HasForeignKey(x => x.PlatformUserId).OnDelete(DeleteBehavior.Restrict);
        });

        return modelBuilder;
    }
}
