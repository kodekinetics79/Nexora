using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// The live offboarding record for one tenant: its stage, its retention clock, and the
/// timestamps of the irreversible things that have already happened to it.
///
/// <para>One row per tenant, created lazily the first time anything offboarding-shaped is done —
/// an export, a scheduled deletion, an erasure. A tenant that has never been offboarded has no
/// row, which is why <see cref="TenantOffboardingStage.NotScheduled"/> and "no record at all"
/// have to mean the same thing everywhere they are read.</para>
///
/// <para>This row is MUTABLE and is the only mutable thing in the module. The immutable account
/// of how it got into its current state is <see cref="TenantLifecycleEvent"/>; nothing here is
/// authoritative history, and nothing here may be trusted to reconstruct a decision.</para>
/// </summary>
public sealed class TenantOffboarding
{
    public long Id { get; set; }

    /// <summary>The platform tenant. Unique — a tenant has exactly one offboarding record.</summary>
    public long TenantId { get; set; }

    public TenantOffboardingStage Stage { get; set; } = TenantOffboardingStage.NotScheduled;

    // ==== the retention clock ================================================================
    // Written together at scheduling and never recomputed. PurgeEligibleOn is STORED rather than
    // derived at read time from DeletionScheduledOn + RetentionDays: the window a customer was
    // promised is a commitment made on a date, and re-deriving it means a later change to the
    // configured default silently moves the destruction date of a tenant already inside its
    // window — in either direction.

    /// <summary>Length of the window agreed when deletion was scheduled, in days.</summary>
    public int? RetentionDays { get; set; }

    public DateTime? DeletionScheduledOn { get; set; }

    /// <summary>
    /// The earliest instant a purge may execute. A purge attempted before this is refused by the
    /// service, not merely hidden by the console.
    /// </summary>
    public DateTime? PurgeEligibleOn { get; set; }

    public string? DeletionReason { get; set; }

    public string? DeletionScheduledBy { get; set; }

    // ==== purge ==============================================================================

    /// <summary>
    /// Set and COMMITTED before the destructive transaction opens. A row carrying this with no
    /// <see cref="PurgedOn"/> is a purge that started and did not report back — recoverable by
    /// re-running it, and the only state in which "the data might be half gone" is knowable at all.
    /// </summary>
    public DateTime? PurgeStartedOn { get; set; }

    public DateTime? PurgedOn { get; set; }

    public string? PurgedBy { get; set; }

    public string? PurgeReason { get; set; }

    /// <summary>Rows destroyed, as counted by the purge itself. The figure quoted back to the
    /// customer, so it is stored rather than recomputed against a schema that no longer holds
    /// their data.</summary>
    public long? PurgedRowCount { get; set; }

    // ==== erasure — deliberately NOT a stage =================================================
    // See TenantOffboardingDisclosure.ErasureIsNotDeletion. These three columns are the whole of
    // the erasure axis: it either happened, or it did not, independently of how far the deletion
    // path has got.

    public DateTime? PersonalDataErasedOn { get; set; }

    public string? PersonalDataErasedBy { get; set; }

    public string? PersonalDataErasureReason { get; set; }

    /// <summary>Natural-person identities replaced by the last erasure. Reported, not recomputed
    /// — after an erasure there is nothing left to count.</summary>
    public long? ErasedIdentityCount { get; set; }

    // ==== export =============================================================================
    // A convenience denormalisation of the newest TenantExportReceipt, so the offboarding screen
    // can answer "have they been given their data yet" without a second query. The receipts are
    // the record; this is a cache of one field of the latest one.

    public DateTime? LastExportedOn { get; set; }

    public string? LastExportedBy { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public DateTime? ModifiedOn { get; set; }
}

/// <summary>
/// One immutable line in a tenant's lifecycle history. Append-only: every transition, every
/// refusal that mattered, every irreversible act, in order, with who and why.
///
/// <para><b>Why the tenant's identity is copied onto every row.</b> This table has to be readable
/// after the tenant it describes has been destroyed — that is the single hardest requirement in
/// the module, and a join to <c>platform."Tenants"</c> does not satisfy it. Slug and name are
/// therefore denormalised at write time, and there is deliberately NO foreign key to
/// <c>platform."Tenants"</c>: a foreign key is precisely the mechanism that would make an
/// operator's <c>DELETE FROM platform."Tenants"</c> either fail or take the history with it, and
/// "the evidence disappeared when the subject did" is the failure this table exists to prevent.
/// The tombstone tenant row is a convenience for the console; this table does not depend on it.</para>
/// </summary>
public sealed class TenantLifecycleEvent
{
    public long Id { get; set; }

    public long TenantId { get; set; }

    /// <summary>The tenant's slug as it was at the time. Copied, not joined — see the type docs.</summary>
    public string TenantSlug { get; set; } = null!;

    /// <summary>The tenant's name as it was at the time.</summary>
    public string TenantName { get; set; } = null!;

    /// <summary>One of <see cref="TenantLifecycleActions"/>.</summary>
    public string Action { get; set; } = null!;

    /// <summary>Offboarding stage before the event; null for events that are not stage transitions
    /// (an export, an erasure).</summary>
    public string? FromStage { get; set; }

    public string? ToStage { get; set; }

    /// <summary>The platform <c>TenantStatus</c> at the moment of the event, so a reader can see
    /// both axes without reconstructing the other table's history.</summary>
    public string TenantStatus { get; set; } = null!;

    /// <summary>
    /// Why. Mandatory on every event this module writes — there is no lifecycle transition here
    /// that a reason is optional for, because every one of them is either irreversible or the
    /// decision to make something irreversible.
    /// </summary>
    public string Reason { get; set; } = null!;

    public long ActorPlatformUserId { get; set; }

    public string ActorEmail { get; set; } = null!;

    /// <summary>Free-form JSON: retention days, row counts, section counts, failure text.</summary>
    public string? Detail { get; set; }

    public DateTime OccurredOn { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Proof that a tenant was handed its data: who asked, when it completed, exactly which sections
/// and row counts went into the file, and the SHA-256 of the bytes that left the building.
///
/// <para>The hash is the point. "We sent you an export" is unfalsifiable on both sides a year
/// later; "we produced this document, of this size, with this fingerprint, containing these
/// counts" can be checked against the file the customer still has. It is also the only way to
/// tell a truncated or edited export from the one that was produced.</para>
///
/// <para>The export itself is NOT stored. It contains the customer's entire commercial history,
/// and keeping a second copy of it on the platform after they have left is the opposite of what
/// offboarding is for.</para>
/// </summary>
public sealed class TenantExportReceipt
{
    public long Id { get; set; }

    public long TenantId { get; set; }

    /// <summary>Denormalised for the same reason as on <see cref="TenantLifecycleEvent"/>: a
    /// receipt has to stay readable after the purge it preceded.</summary>
    public string TenantSlug { get; set; } = null!;

    public DateTime RequestedOn { get; set; }

    public DateTime CompletedOn { get; set; }

    public string RequestedBy { get; set; } = null!;

    public long ActorPlatformUserId { get; set; }

    /// <summary>JSON array of <c>{ section, table, rows, redactedColumns }</c> — what was in it.</summary>
    public string Sections { get; set; } = null!;

    public long TotalRows { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lowercase hex SHA-256 of the exported bytes. Fixed 64 characters.</summary>
    public string ContentSha256 { get; set; } = null!;

    public string Format { get; set; } = null!;
}

/// <summary>
/// EF configuration for the offboarding tables, kept in the owning module so the context needs
/// exactly one delegating call — the same splice discipline
/// <c>ApplyTenantOnboardingModel</c> / <c>ConfigureCommercialFinance</c> use.
///
/// <para>Platform schema, and deliberately NO global query filter on any of the three. A query
/// filter would enrol them in the RLS-policy expectation
/// <c>PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase</c> enforces,
/// which no platform-plane table can satisfy — there is no <c>nexora.business_unit_id</c> on a
/// control-plane request, and these rows are about a tenant rather than inside one.</para>
/// </summary>
public static class TenantLifecycleModelBuilderExtensions
{
    public static ModelBuilder ApplyTenantLifecycleModel(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantOffboarding>(entity =>
        {
            entity.ToTable("TenantOffboardings", "platform");
            entity.HasKey(x => x.Id);

            // UNIQUE, not merely indexed. Two offboarding records for one tenant would mean two
            // retention clocks, and a purge that reads the wrong one destroys a customer's data
            // before the window they were promised has elapsed.
            entity.HasIndex(x => x.TenantId)
                .IsUnique()
                .HasDatabaseName("UX_TenantOffboardings_TenantId");

            // Stored as its NAME. A stage read back in two years has to still mean what it meant;
            // reordering the enum must not silently reclassify a purged tenant as pending. Same
            // rationale as Tenant.BillingMode and Tenant.Status.
            entity.Property(x => x.Stage).HasConversion<string>().HasMaxLength(32);

            entity.Property(x => x.DeletionReason).HasMaxLength(1000);
            entity.Property(x => x.DeletionScheduledBy).HasMaxLength(256);
            entity.Property(x => x.PurgedBy).HasMaxLength(256);
            entity.Property(x => x.PurgeReason).HasMaxLength(1000);
            entity.Property(x => x.PersonalDataErasedBy).HasMaxLength(256);
            entity.Property(x => x.PersonalDataErasureReason).HasMaxLength(1000);
            entity.Property(x => x.LastExportedBy).HasMaxLength(256);

            // The operator's work queue: "which tenants become purgeable, and when". Ordered by
            // eligibility because that is the order the console shows and the order the decision
            // gets made in.
            entity.HasIndex(x => new { x.Stage, x.PurgeEligibleOn })
                .HasDatabaseName("IX_TenantOffboardings_Stage_PurgeEligibleOn");
        });

        modelBuilder.Entity<TenantLifecycleEvent>(entity =>
        {
            entity.ToTable("TenantLifecycleEvents", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.TenantSlug).IsRequired().HasMaxLength(64);
            entity.Property(x => x.TenantName).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Action).IsRequired().HasMaxLength(128);
            entity.Property(x => x.FromStage).HasMaxLength(32);
            entity.Property(x => x.ToStage).HasMaxLength(32);
            entity.Property(x => x.TenantStatus).IsRequired().HasMaxLength(32);
            entity.Property(x => x.Reason).IsRequired().HasMaxLength(1000);
            entity.Property(x => x.ActorEmail).IsRequired().HasMaxLength(320);

            // jsonb on PostgreSQL for the same reason PlatformAuditLogs.Metadata is jsonb: the
            // detail of a five-year-old offboarding gets queried, not just read.
            entity.Property(x => x.Detail).HasColumnType("jsonb");

            // "Replay this tenant's offboarding in order" — the reconstruction query, and the
            // reason the history is worth keeping. Id is the tiebreak: two events written inside
            // one transaction share a timestamp to the microsecond often enough to matter.
            entity.HasIndex(x => new { x.TenantId, x.OccurredOn, x.Id })
                .HasDatabaseName("IX_TenantLifecycleEvents_TenantId_OccurredOn");

            // "Show me every purge this quarter" — the compliance question, asked across tenants.
            entity.HasIndex(x => new { x.Action, x.OccurredOn })
                .HasDatabaseName("IX_TenantLifecycleEvents_Action_OccurredOn");
        });

        modelBuilder.Entity<TenantExportReceipt>(entity =>
        {
            entity.ToTable("TenantExportReceipts", "platform");
            entity.HasKey(x => x.Id);

            entity.Property(x => x.TenantSlug).IsRequired().HasMaxLength(64);
            entity.Property(x => x.RequestedBy).IsRequired().HasMaxLength(256);
            entity.Property(x => x.Sections).HasColumnType("jsonb");
            entity.Property(x => x.Format).IsRequired().HasMaxLength(32);

            // Lowercase hex SHA-256: fixed 64 characters, so the column is fixed width.
            entity.Property(x => x.ContentSha256).IsRequired().HasMaxLength(64);

            entity.HasIndex(x => new { x.TenantId, x.CompletedOn })
                .HasDatabaseName("IX_TenantExportReceipts_TenantId_CompletedOn");
        });

        return modelBuilder;
    }
}
