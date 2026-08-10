using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// FR-INV-04. What kind of level was breached. One alert per (stock row, kind), so a row that is
/// both short AND has an unusable overstock of a different bucket cannot be reduced to one message
/// that only half-describes it.
///
/// <para>The three shortage kinds are a LADDER, not three independent alerts: a row is raised at
/// the worst kind it qualifies for and at no other, so a single shortage never produces three
/// emails. Deterioration from one rung to the next is genuinely new information and earns exactly
/// one further alert; staying on the same rung is silent.</para>
/// </summary>
public static class ReorderAlertKinds
{
    /// <summary>Projected availability has reached zero. Nothing can be promised from this row.</summary>
    public const string OutOfStock = "OUT_OF_STOCK";

    /// <summary>Projected availability is below the configured minimum level.</summary>
    public const string BelowMinimum = "BELOW_MINIMUM";

    /// <summary>Projected availability has reached the reorder point but is still above minimum.</summary>
    public const string ReorderPoint = "REORDER_POINT";

    /// <summary>On-hand stock exceeds the configured maximum level.</summary>
    public const string Overstock = "OVERSTOCK";

    /// <summary>The shortage ladder, worst first. Position in this array IS the severity order.</summary>
    public static readonly string[] ShortageLadder = [OutOfStock, BelowMinimum, ReorderPoint];

    /// <summary>
    /// Every kind this module can raise. Declared ONCE, here, as the set membership is tested
    /// against in memory.
    /// </summary>
    private static readonly HashSet<string> KnownKinds =
        new(StringComparer.Ordinal) { OutOfStock, BelowMinimum, ReorderPoint, Overstock };

    /// <summary>
    /// The same set, projected to an array for use inside a LINQ expression EF must translate.
    ///
    /// <para><b>This projection is not decoration.</b> <c>IReadOnlySet&lt;string&gt;.Contains</c>
    /// binds to the interface's own instance method, which EF Core cannot translate — it compiles
    /// cleanly and throws at query-compile time, which is how a set membership test dropped into a
    /// shipment predicate broke every shipment creation in this codebase a fortnight ago. An array
    /// binds to <c>Enumerable.Contains</c> and translates to <c>= ANY(...)</c>. Same set, stated
    /// once, projected at the query boundary.</para>
    /// </summary>
    public static readonly string[] AllForQuery = [.. KnownKinds.OrderBy(x => x, StringComparer.Ordinal)];

    /// <summary>Whether a caller-supplied filter names a kind this module can produce.</summary>
    public static bool IsKnown(string? kind) => kind is not null && KnownKinds.Contains(kind);

    /// <summary>
    /// The alert level the notification is sent at, reusing the SLA vocabulary
    /// (<c>warn</c>/<c>critical</c>/<c>overdue</c>) so one mailbox rule covers both engines.
    /// </summary>
    public static string SeverityOf(string kind) => kind switch
    {
        OutOfStock => "critical",
        BelowMinimum => "critical",
        _ => "warn",
    };
}

/// <summary>
/// FR-INV-04. The lifecycle of one reorder alert.
///
/// <para><see cref="Open"/> — the condition is true and nobody has said they are dealing with it.
/// <see cref="Acknowledged"/> — a named person has taken it, with a reason. It stays on the ledger
/// and stays deduped, so the sweep does not raise it again while they are acting on it.
/// <see cref="Resolved"/> — the condition is no longer true. This is the transition that lets the
/// SAME row alert again later: without it an alert fires once in the lifetime of a stock row and is
/// silent forever after, which is a worse failure than not having the alert at all.</para>
/// </summary>
public static class ReorderAlertStatuses
{
    public const string Open = "OPEN";
    public const string Acknowledged = "ACKNOWLEDGED";
    public const string Resolved = "RESOLVED";

    /// <summary>The statuses that hold the dedup key. Mirrors the partial unique index, which is
    /// the enforcement — this is the readable copy, not the authority.</summary>
    private static readonly HashSet<string> LiveSet = new(StringComparer.Ordinal) { Open, Acknowledged };

    /// <summary>
    /// The live set projected for use inside a translated LINQ predicate. See
    /// <see cref="ReorderAlertKinds.AllForQuery"/> for why this projection exists rather than the
    /// set being consulted directly.
    /// </summary>
    public static readonly string[] LiveForQuery = [.. LiveSet.OrderBy(x => x, StringComparer.Ordinal)];

    public static bool IsLive(string? status) => status is not null && LiveSet.Contains(status);

    private static readonly HashSet<string> KnownSet =
        new(StringComparer.Ordinal) { Open, Acknowledged, Resolved };

    public static bool IsKnown(string? status) => status is not null && KnownSet.Contains(status);
}

/// <summary>
/// FR-INV-04. One raised reorder/overstock alert.
///
/// <para><b>Why this is a row and not just an email.</b> An email is fire-and-forget: nobody can
/// answer "which alerts are outstanding", "who took this one", or "did we act on it", and a mail
/// server outage silently loses the entire signal. The row is the alert; the email is one delivery
/// of it. The row also carries the arithmetic that produced it — on-hand, available, incoming, the
/// threshold and the shortfall — so the person reading it six hours later can see WHY it fired
/// without re-deriving anything, and so an alert cannot silently change meaning when the stock
/// moves underneath it.</para>
///
/// <para><b>What stops it firing on everything.</b> Four things, and each is load-bearing:
/// a threshold must be CONFIGURED (null/non-positive is "not set", never "alert now"); the measure
/// is available-to-promise plus committed incoming supply, not on-hand, so a row already covered by
/// a purchase order is not chased; only the worst rung of the ladder is raised; and the partial
/// unique index means a live alert suppresses every re-raise until it is resolved.</para>
/// </summary>
public sealed class InventoryReorderAlert
{
    public long Id { get; set; }
    public long BusinessUnitId { get; set; }

    public long InventoryId { get; set; }
    public long ProductId { get; set; }
    public long WarehouseId { get; set; }

    /// <summary>See <see cref="ReorderAlertKinds"/>.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>See <see cref="ReorderAlertStatuses"/>.</summary>
    public string Status { get; set; } = ReorderAlertStatuses.Open;

    /// <summary><c>warn</c> or <c>critical</c> — see <see cref="ReorderAlertKinds.SeverityOf"/>.</summary>
    public string Severity { get; set; } = "warn";

    /// <summary>Physical units in the building when the alert was raised.</summary>
    public decimal OnHandQuantity { get; set; }

    /// <summary>Available-to-promise when the alert was raised, through
    /// <see cref="InventoryQuantityMath.AvailableToPromise"/> and no other route.</summary>
    public decimal AvailableQuantity { get; set; }

    /// <summary>Open committed supply already on its way when the alert was raised.</summary>
    public decimal IncomingQuantity { get; set; }

    /// <summary>Available plus incoming: what the buyer will actually have if nothing else happens.
    /// This, not on-hand, is what the shortage kinds are measured against.</summary>
    public decimal ProjectedQuantity { get; set; }

    /// <summary>The configured level that was breached — the minimum, the reorder point or the
    /// maximum, depending on <see cref="Kind"/>. Never a value the system invented.</summary>
    public decimal ThresholdQuantity { get; set; }

    /// <summary>How far the wrong side of the threshold the row is. Positive by construction: for
    /// a shortage it is what must be bought to reach the threshold, for an overstock it is the
    /// excess above it.</summary>
    public decimal ShortfallQuantity { get; set; }

    public DateTime RaisedOn { get; set; }

    /// <summary>The email claim, when one was taken. Null means the alert exists on the ledger but
    /// no copy has been posted yet — a visible state, not a blank.</summary>
    public int NotifiedCount { get; set; }

    public DateTime? AcknowledgedOn { get; set; }
    public string? AcknowledgedBy { get; set; }

    /// <summary>Why the acknowledger is not acting further, or what they are doing about it. An
    /// acknowledgement with no reason is a mute button, which is how an alert channel dies.</summary>
    public string? AcknowledgementReason { get; set; }

    public DateTime? ResolvedOn { get; set; }

    /// <summary>What made the condition stop being true: stock arrived, the level was changed, the
    /// row was emptied. Null while the alert is live.</summary>
    public string? ResolutionReason { get; set; }

    public DateTime CreatedOn { get; set; }

    /// <summary>Optimistic concurrency, so two people acknowledging the same alert cannot both
    /// believe they own it.</summary>
    public uint Version { get; set; }
}

/// <summary>
/// FR-INV-04 relational mapping.
///
/// <para><b>One new tenant-scoped table, <c>inventory_reorder_alerts</c>.</b> It needs BOTH a
/// row-level-security policy AND an explicit <c>GRANT</c> in the migration that creates it — the
/// schema revoked default privileges, so a policy without a grant raises <c>42501</c> before any row
/// predicate is evaluated. Three tables shipped exactly that defect two gates ago. The full delta is
/// stated in the gate report.</para>
///
/// <para>Plus two columns on the existing <c>Inventory</c> table (<c>MinimumLevel</c>,
/// <c>MaximumLevel</c>), which needs no new policy or grant because it already carries both.</para>
///
/// <para>Deliberately portable: no <c>jsonb</c>, no PostgreSQL-only expressions. The <c>CHECK</c>
/// constraints below are NOT enforced on the portable lane, which runs SQLite with
/// <c>PRAGMA ignore_check_constraints = ON</c>, which is why every invariant they express is also
/// enforced in <see cref="ReorderAlertService"/> and <see cref="StockLedgerService"/>. The
/// constraint text itself is certified in the PostgreSQL lane.</para>
/// </summary>
public static class InventoryReorderAlertModelBuilderExtensions
{
    public static ModelBuilder ApplyReorderAlertModel(
        this ModelBuilder modelBuilder,
        Expression<Func<InventoryReorderAlert, bool>> tenantFilter)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Models.Inventory>(entity =>
        {
            entity.Property(x => x.MinimumLevel).HasPrecision(18, 4);
            entity.Property(x => x.MaximumLevel).HasPrecision(18, 4);

            // Negative levels are meaningless, and a maximum below the minimum is a policy that
            // can never be satisfied — every row configured that way would be simultaneously short
            // and overstocked, and would alert twice, forever. Enforced in
            // StockLedgerService.SetStockLevelsAsync as well, because this is unenforced on SQLite.
            entity.ToTable("Inventory", table =>
            {
                table.HasCheckConstraint("CK_Inventory_StockLevels",
                    "(\"MinimumLevel\" IS NULL OR \"MinimumLevel\" >= 0) "
                    + "AND (\"MaximumLevel\" IS NULL OR \"MaximumLevel\" >= 0) "
                    + "AND (\"MinimumLevel\" IS NULL OR \"MaximumLevel\" IS NULL "
                    + "OR \"MaximumLevel\" >= \"MinimumLevel\")");
            });
        });

        modelBuilder.Entity<InventoryReorderAlert>(entity =>
        {
            entity.ToTable("inventory_reorder_alerts", table =>
            {
                table.HasCheckConstraint("CK_inventory_reorder_alerts_Kind",
                    "\"Kind\" IN ('OUT_OF_STOCK','BELOW_MINIMUM','REORDER_POINT','OVERSTOCK')");
                table.HasCheckConstraint("CK_inventory_reorder_alerts_Status",
                    "\"Status\" IN ('OPEN','ACKNOWLEDGED','RESOLVED')");
                // An acknowledgement with no name, no time or no reason is not an acknowledgement:
                // it is a mute with nobody's signature on it.
                table.HasCheckConstraint("CK_inventory_reorder_alerts_Acknowledgement",
                    "(\"AcknowledgedOn\" IS NULL AND \"AcknowledgedBy\" IS NULL "
                    + "AND \"AcknowledgementReason\" IS NULL) "
                    + "OR (\"AcknowledgedOn\" IS NOT NULL AND \"AcknowledgedBy\" IS NOT NULL "
                    + "AND \"AcknowledgementReason\" IS NOT NULL)");
                // ThresholdQuantity > 0 because a threshold of zero is "not configured" and must
                // never reach this table. ShortfallQuantity >= 0 rather than > 0: a row sitting
                // exactly ON its reorder point is the moment the reorder point exists to name, and
                // its gap is honestly zero — see ReorderAlertService.Classify.
                table.HasCheckConstraint("CK_inventory_reorder_alerts_Quantities",
                    "\"OnHandQuantity\" >= 0 AND \"IncomingQuantity\" >= 0 "
                    + "AND \"AvailableQuantity\" >= 0 AND \"ThresholdQuantity\" > 0 "
                    + "AND \"ShortfallQuantity\" >= 0");
            });
            entity.HasKey(x => x.Id);

            entity.Property(x => x.Kind).HasMaxLength(24).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Severity).HasMaxLength(16).IsRequired();
            entity.Property(x => x.OnHandQuantity).HasPrecision(18, 4);
            entity.Property(x => x.AvailableQuantity).HasPrecision(18, 4);
            entity.Property(x => x.IncomingQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ProjectedQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ThresholdQuantity).HasPrecision(18, 4);
            entity.Property(x => x.ShortfallQuantity).HasPrecision(18, 4);
            entity.Property(x => x.AcknowledgedBy).HasMaxLength(160);
            entity.Property(x => x.AcknowledgementReason).HasMaxLength(500);
            entity.Property(x => x.ResolutionReason).HasMaxLength(120);
            entity.Property(x => x.Version).IsConcurrencyToken();

            // THE dedup. At most one LIVE alert per (tenant, stock row, kind): two sweep instances
            // racing produce a 23505 for the loser rather than two copies of the same alert, and a
            // sweep every half hour cannot fill the table with one row per pass. Filtered on the
            // live statuses rather than on `<> RESOLVED` so a resolved alert frees the key and the
            // same row can legitimately alert again when it drops back below its level.
            entity.HasIndex(x => new { x.BusinessUnitId, x.InventoryId, x.Kind })
                .IsUnique()
                .HasFilter("\"Status\" IN ('OPEN','ACKNOWLEDGED')")
                .HasDatabaseName("UX_inventory_reorder_alerts_live");

            entity.HasIndex(x => new { x.BusinessUnitId, x.Status, x.RaisedOn })
                .HasDatabaseName("IX_inventory_reorder_alerts_status");

            entity.HasOne<Models.Inventory>()
                .WithMany()
                .HasForeignKey(x => new { x.BusinessUnitId, x.InventoryId })
                .HasPrincipalKey(x => new { BusinessUnitId = x.Buid, InventoryId = x.Id })
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasQueryFilter(tenantFilter);
        });

        return modelBuilder;
    }
}
