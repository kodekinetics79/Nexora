using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Globalization;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ERP_RFQ_Automation.MasterData;

/// <summary>
/// FR-MDM-05 / E44 — captures a field-level before/after audit for every create, update and delete
/// of <see cref="Customer"/>, <see cref="Supplier"/> and <see cref="Product"/>.
///
/// <para><b>Why it lives at the persistence boundary.</b> A controller-level log is bypassed by
/// every other write path, and this codebase has several: the spreadsheet uploader
/// (<c>ProductUploaderService</c>) writes products without touching <c>ProductController</c>, and
/// the repositories are reachable from services, workers and seeders. The change tracker holds the
/// original and the current value of every property natively, so capturing here is both complete
/// and free of a second read.</para>
///
/// <para><b>Why it is invoked from <c>ErpRfqAutomationContext.SaveChanges</c> and not registered
/// with <c>AddInterceptors</c>.</b> That is the established pattern in this codebase — the
/// custom-field and lifecycle governance guards are both called from the same two overrides — and
/// it is strictly stronger: an options-registered interceptor is absent from every context built
/// from a <c>DbContextOptionsBuilder</c> that forgot it, including the whole SQLite test lane. A
/// call inside the override cannot be configured away. The class still derives from
/// <see cref="SaveChangesInterceptor"/> so it can additionally be registered if the DbContext
/// wiring ever changes, matching <c>CustomFieldGovernanceInterceptor</c>.</para>
///
/// <para><b>Known reach limit, stated rather than hidden.</b> <c>ExecuteUpdateAsync</c> /
/// <c>ExecuteDeleteAsync</c> and raw SQL do not pass through <c>SaveChanges</c> and therefore
/// escape this capture — exactly as they escape the two existing governance guards. No master-data
/// write path uses them today; the database trigger protects the audit rows themselves, not the
/// master-data rows.</para>
///
/// <para><b>Cost.</b> One extra pass over the change tracker's entries with three type checks per
/// entry, and nothing else when no master-data row is in the batch. <c>DetectChanges</c> is
/// already forced by the two existing guards immediately above, so no additional detection is
/// triggered. When master data IS in the batch the cost is O(changed properties): 24 property
/// comparisons for a product, plus one header row and one row per field that actually moved.</para>
/// </summary>
public sealed class MasterDataAuditInterceptor : SaveChangesInterceptor
{
    /// <summary>Longest value stored per side. Beyond this the value is truncated with an ellipsis
    /// marker: an audit row is evidence of a change, not a blob store.</summary>
    private const int MaxValueLength = 4000;

    // ---------------------------------------------------------------- append-only

    /// <summary>
    /// Refuses any attempt to rewrite or erase the trail. The DATABASE trigger
    /// <c>trg_master_data_audit_append_only</c> is the control — this is the portable half, so the
    /// SQLite suite fails on the same mistake instead of passing and shipping it.
    /// </summary>
    public static void ValidateAppendOnly(ChangeTracker? changeTracker)
    {
        if (changeTracker is null) return;

        foreach (var entry in changeTracker.Entries())
        {
            if (entry.State is not (EntityState.Modified or EntityState.Deleted)) continue;
            if (entry.Entity is MasterDataChangeEvent or MasterDataFieldChange)
                throw new InvalidOperationException(
                    "Master-data change audit records are append-only and cannot be modified or deleted.");
        }
    }

    // ---------------------------------------------------------------- capture

    /// <summary>
    /// Builds the audit rows for everything master-data in this batch.
    ///
    /// <para>Modified and Deleted rows are added to the change tracker immediately, so they are
    /// INSERTed inside the very same implicit transaction as the change they describe: the audit
    /// commits with the change or rolls back with it, and there is no window in which one exists
    /// without the other.</para>
    ///
    /// <para>Created rows cannot be: the primary key is database-generated and is still 0 here.
    /// Those are returned in the capture and materialised by
    /// <see cref="MasterDataAuditCapture.Materialise"/> after the insert has run. See that method
    /// for what that costs.</para>
    /// </summary>
    public static MasterDataAuditCapture Capture(ErpRfqAutomationContext context)
    {
        var tracker = context.ChangeTracker;
        List<MasterDataChangeEvent>? immediate = null;
        List<DeferredCreate>? deferred = null;
        MasterDataAuditActor? resolvedActor = null;

        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            var descriptor = MasterDataEntityDescriptor.For(entry.Entity);
            if (descriptor is null) continue;

            var businessUnitId = descriptor.TenantId(entry.Entity) ?? context.ScopedTenantId;
            if (businessUnitId is not { } tenantId || tenantId <= 0)
                // A master-data row with no tenant, saved with no tenant scope, is shared
                // reference data outside the FR-MDM-05 boundary. There is nowhere tenant-safe to
                // file its audit: a null BusinessUnitId sits outside the RLS policy and cannot be
                // attributed to anyone, which is the same fail-closed reasoning IamAuditWriter
                // applies. Recorded here as "not audited" rather than written unattributably.
                continue;

            var fields = CollectFieldChanges(entry, descriptor);
            if (entry.State == EntityState.Modified && fields.Count == 0)
                // Nothing an auditor would call a change — only excluded columns (the row's own
                // ModifiedBy/ModifiedOn stamp, its concurrency token) moved. Writing a header with
                // no fields would fill the trail with rows that say nothing happened.
                continue;

            resolvedActor ??= MasterDataAuditScope.Current;
            var actor = ResolveActor(resolvedActor, entry, descriptor);

            var changeEvent = new MasterDataChangeEvent
            {
                BusinessUnitId = tenantId,
                EntityType = descriptor.EntityType,
                EntityId = entry.State == EntityState.Added ? 0 : descriptor.Key(entry.Entity),
                EntityLabel = Truncate(descriptor.Label(entry.Entity), 256),
                ChangeType = entry.State switch
                {
                    EntityState.Added => MasterDataChangeTypes.Created,
                    EntityState.Deleted => MasterDataChangeTypes.Deleted,
                    _ => MasterDataChangeTypes.Updated
                },
                ActorUserId = actor.UserId,
                ActorRoleId = actor.RoleId,
                Actor = Truncate(actor.Name, 200)!,
                ChangeSource = actor.Source,
                CorrelationId = Truncate(actor.CorrelationId, 64),
                FieldCount = fields.Count,
                OccurredOn = DateTime.UtcNow
            };

            foreach (var field in fields)
            {
                field.BusinessUnitId = tenantId;
                changeEvent.Fields.Add(field);
            }

            if (entry.State == EntityState.Added)
                (deferred ??= []).Add(new DeferredCreate(entry.Entity, descriptor, changeEvent));
            else
                (immediate ??= []).Add(changeEvent);
        }

        // Enlisted AFTER the loop: adding to the tracker while enumerating its entries would
        // invalidate the enumerator.
        if (immediate is not null)
            foreach (var changeEvent in immediate)
                context.Add(changeEvent);

        return deferred is null ? MasterDataAuditCapture.Empty : new MasterDataAuditCapture(deferred);
    }

    /// <summary>
    /// Actor resolution, most trustworthy source first.
    ///
    /// <para>1. The ambient scope, pushed from the validated JWT by
    /// <c>MasterDataAuditActorMiddleware</c>. It carries the user id, the role held at the time and
    /// the request correlation id, and nothing in a request body can influence it.</para>
    ///
    /// <para>2. The row's own <c>ModifiedBy</c>/<c>CreatedBy</c> stamp. These are set server-side
    /// by the master-data controllers and by the uploader from the authenticated caller, so they
    /// are a genuine second source rather than a guess — but they carry no user id and no
    /// correlation id, and on a delete they name the last EDITOR, not the deleter. Used only when
    /// there is no ambient scope, and marked <c>SYSTEM</c> so the weaker provenance is visible in
    /// the trail rather than indistinguishable from the strong one.</para>
    ///
    /// <para>3. <c>system</c>. Never a person's name invented from nothing.</para>
    /// </summary>
    private static MasterDataAuditActor ResolveActor(
        MasterDataAuditActor? ambient, EntityEntry entry, MasterDataEntityDescriptor descriptor)
    {
        // A scope carrying a real user id came from a validated token. Nothing beats it.
        if (ambient is { UserId: not null } authenticated) return authenticated;

        var stamp = entry.State == EntityState.Added
            ? descriptor.CreatedBy(entry.Entity)
            : descriptor.ModifiedBy(entry.Entity) ?? descriptor.CreatedBy(entry.Entity);

        // A scope with no user id is a SOURCE marker pushed outside a request — the uploader run
        // by a background job is the case. Keep its source and correlation id, but take the name
        // from the row's own server-set stamp rather than reporting the change as "system" when
        // the write path actually knows who asked for it.
        var basis = ambient ?? MasterDataAuditActor.System;
        return string.IsNullOrWhiteSpace(stamp) ? basis : basis with { Name = stamp };
    }

    private static List<MasterDataFieldChange> CollectFieldChanges(
        EntityEntry entry, MasterDataEntityDescriptor descriptor)
    {
        var changes = new List<MasterDataFieldChange>();

        foreach (var property in entry.Properties)
        {
            var name = property.Metadata.Name;
            if (descriptor.IsExcluded(name)) continue;

            string? before = null;
            string? after = null;

            switch (entry.State)
            {
                case EntityState.Added:
                    after = Render(property.CurrentValue);
                    if (after is null) continue;  // a field that was never set did not change.
                    break;

                case EntityState.Deleted:
                    before = Render(property.OriginalValue);
                    if (before is null) continue;
                    break;

                default:
                    if (!property.IsModified) continue;
                    before = Render(property.OriginalValue);
                    after = Render(property.CurrentValue);
                    // EF marks a property modified when it is ASSIGNED, not when it differs. A
                    // controller that re-assigns every field from a request body would otherwise
                    // report twenty-four "changes" per edit and bury the one that mattered.
                    if (string.Equals(before, after, StringComparison.Ordinal)) continue;
                    break;
            }

            changes.Add(new MasterDataFieldChange
            {
                FieldName = name,
                BeforeValue = Truncate(before, MaxValueLength),
                AfterValue = Truncate(after, MaxValueLength),
                Sensitivity = MasterDataEntityDescriptor.ClassifyField(name)
            });
        }

        return changes;
    }

    /// <summary>
    /// Invariant-culture rendering. Culture matters: a decimal written under an Arabic locale and
    /// read back under an English one would compare unequal and manufacture a change that never
    /// happened.
    /// </summary>
    private static string? Render(object? value) => value switch
    {
        null => null,
        string text => text,
        bool flag => flag ? "true" : "false",
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString("R", CultureInfo.InvariantCulture),
        float number => number.ToString("R", CultureInfo.InvariantCulture),
        DateTime timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
        Guid id => id.ToString("D"),
        byte[] bytes => $"<{bytes.Length} bytes>",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..(max - 1)] + "…";

    internal sealed record DeferredCreate(
        object Entity, MasterDataEntityDescriptor Descriptor, MasterDataChangeEvent Event);
}

/// <summary>
/// The audit rows that could not be written before the change was persisted. Only creates land
/// here, because only a create has a database-generated primary key.
/// </summary>
public sealed class MasterDataAuditCapture
{
    internal static readonly MasterDataAuditCapture Empty = new(null);

    private readonly List<MasterDataAuditInterceptor.DeferredCreate>? _deferred;

    internal MasterDataAuditCapture(List<MasterDataAuditInterceptor.DeferredCreate>? deferred)
        => _deferred = deferred;

    public bool HasDeferredCreates => _deferred is { Count: > 0 };

    /// <summary>
    /// Fills in the now-known primary key and label and enlists the create audit rows.
    ///
    /// <para><b>The one atomicity gap in this design, stated plainly.</b> These rows are INSERTed
    /// by a second <c>SaveChanges</c>. When the caller owns a transaction — which the spreadsheet
    /// uploader does — that second save joins it and the create and its audit commit together.
    /// When it does not, a process death between the two leaves a created row with no audit row.
    /// The alternative was for this method to open its own transaction, and it cannot: the
    /// DbContext is registered with <c>EnableRetryOnFailure</c>, and EF refuses to save inside a
    /// user-initiated transaction under a retrying execution strategy — the failure documented at
    /// length on <c>IIamAuditWriter.ExecuteAtomicAsync</c>, which broke every IAM mutation.</para>
    ///
    /// <para>The gap is confined to CREATE, which is the least consequential of the three: a newly
    /// created row still carries its own server-set <c>CreatedBy</c> and <c>CreatedOn</c>. An
    /// UPDATE or a DELETE — the changes with no other record anywhere — are written inside the
    /// original transaction and have no window at all. If the second save throws, the exception
    /// propagates to the caller; it is never swallowed.</para>
    /// </summary>
    public void Materialise(ErpRfqAutomationContext context)
    {
        if (_deferred is null) return;

        foreach (var (entity, descriptor, changeEvent) in _deferred)
        {
            changeEvent.EntityId = descriptor.Key(entity);
            changeEvent.EntityLabel ??= descriptor.Label(entity);
            if (changeEvent.EntityId <= 0) continue;   // insert did not produce a key; nothing to point at.
            context.Add(changeEvent);
        }

        _deferred.Clear();
    }
}
