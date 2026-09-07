using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.DataAssets;

/// <summary>
/// What this deployment says its own database is — recorded once, in the console, by an Owner.
///
/// <para><b>Why a row and not configuration.</b> <c>Platform:DataBoundaries</c> already carries
/// these facts, and for an infrastructure-as-code deployment that is the right home. It is the
/// wrong home for the person who actually meets this: an operator onboarding a customer hits a
/// blocked activation control, and "set four environment variables on the API service" is not an
/// instruction they can act on — it needs a deploy, a dashboard they may not have, and a value
/// (the Neon endpoint id) they have no way to know. A control-plane fact that an operator is
/// expected to supply has to be editable where operators work, and audited like everything else
/// they do. The precedent is <c>PlatformEmailSettings</c>, which moved outbound mail identity out
/// of configuration for exactly this reason.</para>
///
/// <para><b>Single row, enforced by the database.</b> Two rows would be two answers to "where does
/// this deployment keep its customers' data", and the failure mode of two answers is that the one
/// an auditor reads and the one the probe uses are not the same one.</para>
/// </summary>
public class PlatformDataBoundarySettings
{
    /// <summary>The only permitted primary key. Enforced by a database check constraint.</summary>
    public const long SingletonId = 1;

    public long Id { get; set; } = SingletonId;

    /// <summary>Opaque identifier for the database — never a URL, connection string or credential.</summary>
    public string OpaqueProviderReference { get; set; } = string.Empty;

    /// <summary>Must equal every tenant's contractual data region, or their registration is refused.</summary>
    public string Region { get; set; } = string.Empty;

    public string BackupPolicyReference { get; set; } = string.Empty;

    public int BackupPolicyVersion { get; set; } = 1;

    /// <summary>
    /// How the values got here: <c>observed-and-confirmed</c> when an Owner accepted what the
    /// process read off its own connection, <c>entered</c> when they typed them. Kept because the
    /// two are different kinds of evidence and an auditor is entitled to know which one this is.
    /// </summary>
    public string Basis { get; set; } = ProvenanceBases.Entered;

    /// <summary>The host that was observed at the moment of confirmation, when there was one.</summary>
    public string? ObservedHost { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string RecordedBy { get; set; } = string.Empty;

    public DateTime RecordedOn { get; set; }

    /// <summary>Optimistic concurrency: two Owners cannot silently overwrite each other.</summary>
    public long Version { get; set; } = 1;
}

public static class ProvenanceBases
{
    public const string ObservedAndConfirmed = "observed-and-confirmed";
    public const string Entered = "entered";

    public static bool IsKnown(string? value) =>
        value is ObservedAndConfirmed or Entered;
}

public static class PlatformDataBoundarySettingsModelBuilderExtensions
{
    public static ModelBuilder ApplyPlatformDataBoundarySettingsModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlatformDataBoundarySettings>(entity =>
        {
            entity.ToTable("PlatformDataBoundarySettings", "platform", table =>
                table.HasCheckConstraint(
                    "CK_PlatformDataBoundarySettings_Singleton",
                    $"\"Id\" = {PlatformDataBoundarySettings.SingletonId}"));

            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedNever();

            entity.Property(x => x.OpaqueProviderReference).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Region).IsRequired().HasMaxLength(64);
            entity.Property(x => x.BackupPolicyReference).IsRequired().HasMaxLength(128);
            entity.Property(x => x.Basis).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ObservedHost).HasMaxLength(255);
            entity.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
            entity.Property(x => x.RecordedBy).IsRequired().HasMaxLength(320);
        });

        return modelBuilder;
    }
}
