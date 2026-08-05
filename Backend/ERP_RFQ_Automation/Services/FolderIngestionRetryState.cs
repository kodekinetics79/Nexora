using System;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services;

/// <summary>
/// Durable per-file staging-retry counter for the watched-folder ingestion sweep.
///
/// This replaces the <c>&lt;file&gt;.nexora-retry.json</c> sidecar files that
/// <see cref="FolderService"/> used to write next to the watched document. On Render
/// the upload root is an EPHEMERAL, per-instance disk
/// (<c>Storage__RootPath=/tmp/nexora/uploads</c> in render.yaml), so those sidecars
/// (a) vanished on every restart — resetting the attempt counter and letting a
/// poison document retry forever — and (b) were invisible to every other instance,
/// so the three-strikes quarantine rule was per-instance rather than per-file.
///
/// Keyed by (BusinessUnitId, SourceLabel, FileName): the stable identity of a watched
/// file, independent of the per-claim GUID prefix the sweep adds while processing.
/// </summary>
public sealed class FolderIngestionRetryState
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    /// <summary>Watched-folder label ("SEC Leads", "Aramco Leads", "Shared Leads").</summary>
    public string SourceLabel { get; set; } = string.Empty;

    /// <summary>File name as it appeared in the watched folder.</summary>
    public string FileName { get; set; } = string.Empty;

    public int Attempts { get; set; }

    /// <summary>Exception type name of the most recent failure (never the message —
    /// exception text can carry document content).</summary>
    public string? LastErrorType { get; set; }

    public DateTime FirstFailedOn { get; set; }
    public DateTime LastFailedOn { get; set; }
}

public static class FolderIngestionRetryModelBuilderExtensions
{
    public static ModelBuilder ApplyFolderIngestionRetryModel(
        this ModelBuilder modelBuilder,
        Expression<Func<FolderIngestionRetryState, bool>> tenantFilter)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(tenantFilter);

        modelBuilder.Entity<FolderIngestionRetryState>(entity =>
        {
            entity.ToTable("FolderIngestionRetryStates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SourceLabel).HasMaxLength(100).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(400).IsRequired();
            entity.Property(x => x.LastErrorType).HasMaxLength(200);
            entity.HasIndex(x => new { x.BusinessUnitId, x.SourceLabel, x.FileName })
                .IsUnique()
                .HasDatabaseName("UX_FolderIngestionRetryStates_BU_Source_File");
            entity.HasQueryFilter(tenantFilter);
        });

        return modelBuilder;
    }
}
