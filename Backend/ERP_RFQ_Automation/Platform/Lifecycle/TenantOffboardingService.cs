using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// The offboarding half of the tenant lifecycle: export, schedule deletion, cancel, purge, erase.
///
/// <para><b>Every operation here writes two records, not one.</b> A
/// <c>platform."PlatformAuditLogs"</c> row (who did what, on the same trail as every other
/// privileged action) and a <c>platform."TenantLifecycleEvents"</c> row (what the tenant's state
/// became, on a trail that survives the tenant). They are not redundant: the audit log answers
/// "what has this operator been doing" and is purge-proof by grant
/// (<c>REVOKE UPDATE, DELETE, TRUNCATE ... FROM nexora_pipeline_app</c>); the lifecycle events
/// answer "reconstruct this customer's offboarding" and carry the tenant's name and slug inline so
/// they remain readable when nothing else about the tenant exists.</para>
///
/// <para><b>Ordering discipline for the destructive operations.</b> Intent is committed first,
/// the destruction runs second, completion is committed third — the same rule
/// <c>EvidenceRetentionService</c> follows, for the same reason. The purge cannot share a
/// transaction with its own bookkeeping: it runs on the OWNER connection (it has to suspend the
/// append-only guards) while the bookkeeping runs on the request connection. Given that they
/// cannot be atomic, the ordering decides which non-atomic outcome is possible. Recording intent
/// first makes the forbidden state — a tenant recorded as purged whose data is still present —
/// impossible, and leaves only the tolerable one: intent recorded, destruction done or partly
/// done, completion not yet written. That state is visible (<c>PurgeStartedOn</c> set,
/// <c>PurgedOn</c> null) and self-heals, because re-running a purge that finds nothing to delete
/// succeeds.</para>
/// </summary>
public sealed class TenantOffboardingService(
    ErpRfqAutomationContext context,
    IPlatformAuditService audit,
    TenantPurgeExecutor purge,
    TenantPersonalDataEraser eraser,
    TenantDataExportService export,
    IOptions<TenantLifecycleOptions> options,
    ILogger<TenantOffboardingService> logger,
    ITenantAccessService? tenantAccess = null,
    Support.ISupportTicketRedactionService? supportRedaction = null)
{
    /// <summary>
    /// The floor a reason must clear on the operations that destroy something or commit to
    /// destroying it. Mirrors the floor tenant provisioning applies to a billing exemption, for
    /// the same reason: a reason of "x" satisfies a required-field check while leaving the paper
    /// trail worth nothing, and the entire point of demanding one is that somebody has to be able
    /// to read it years later and understand why a customer's records were destroyed.
    /// </summary>
    public const int MinimumDestructionReasonLength = 15;

    /// <summary>
    /// How long a claimed purge holds its lease before another caller may take it over.
    ///
    /// <para>This window exists for exactly one situation: the process running a purge died
    /// without unwinding. Every failure the service can see releases the lease itself, so the
    /// operator never waits on this after an error they were shown. Thirty minutes is longer than
    /// any single purge transaction and short enough that a crash is not an overnight block.</para>
    /// </summary>
    public static readonly TimeSpan PurgeLease = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Status changes that mean the tenant came back to life after its deletion was scheduled.
    /// Read from the platform audit trail because they are written there, by
    /// <c>TenantsController</c>, and nowhere else this module can see.
    /// </summary>
    private static readonly string[] ReanimatingActions = ["tenant.restore", "tenant.resume"];

    private TenantLifecycleOptions Options => options.Value;

    // ---------------------------------------------------------------------------------- read

    public async Task<TenantOffboardingStatusDto> GetStatusAsync(long tenantId, CancellationToken ct)
    {
        var tenant = await LoadTenantAsync(tenantId, tracked: false, ct);
        var record = await LoadRecordAsync(tenantId, tracked: false, ct);

        var history = await context.Set<TenantLifecycleEvent>().AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.OccurredOn).ThenBy(e => e.Id)
            .Select(e => new TenantLifecycleEventDto(
                e.Id, e.Action, e.FromStage, e.ToStage, e.TenantStatus, e.Reason,
                e.ActorEmail, e.Detail, e.OccurredOn))
            .ToListAsync(ct);

        var exports = await context.Set<TenantExportReceipt>().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .OrderByDescending(r => r.CompletedOn).ThenByDescending(r => r.Id)
            .Select(r => new TenantExportReceiptDto(
                r.Id, r.RequestedOn, r.CompletedOn, r.RequestedBy, r.TotalRows, r.SizeBytes,
                r.ContentSha256, r.Format, r.Sections))
            .ToListAsync(ct);

        return ToStatusDto(tenant, record, history, exports);
    }

    /// <summary>
    /// The operator's work queue: every tenant whose retention clock is running, soonest first.
    /// Projected rather than materialised — the platform roles hold column-level SELECT on
    /// <c>platform."Tenants"</c>, and a query that reads the whole entity fails with 42501 at
    /// runtime while every SQLite test stays green.
    /// </summary>
    public async Task<IReadOnlyList<PendingTenantDeletionDto>> ListPendingDeletionsAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        var pending = await (
            from record in context.Set<TenantOffboarding>().AsNoTracking()
            where record.Stage == TenantOffboardingStage.PendingDeletion
            join tenant in context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                on record.TenantId equals tenant.Id
            orderby record.PurgeEligibleOn
            select new
            {
                record.TenantId,
                tenant.Name,
                tenant.Slug,
                record.DeletionScheduledOn,
                record.PurgeEligibleOn,
                record.DeletionReason,
                record.DeletionScheduledBy
            }).ToListAsync(ct);

        return pending.Select(p => new PendingTenantDeletionDto(
            p.TenantId, p.Name, p.Slug, p.DeletionScheduledOn, p.PurgeEligibleOn,
            IsEligible(p.PurgeEligibleOn, now), DaysUntil(p.PurgeEligibleOn, now),
            p.DeletionReason, p.DeletionScheduledBy)).ToList();
    }

    // -------------------------------------------------------------------------------- export

    /// <summary>
    /// Produces the tenant's data file and the receipt that proves it was produced. The document
    /// itself is returned to the caller and deliberately not stored: it is the customer's entire
    /// commercial history, and keeping a copy of it on the platform after they have left is the
    /// opposite of what offboarding is for.
    /// </summary>
    public async Task<(TenantExportDocument Document, TenantExportReceiptDto Receipt)> ExportAsync(
        long tenantId, string? reason, string? confirmation, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        // The same floor and the same typed confirmation as the destructive verbs. An export is
        // not destructive, but it is the single largest disclosure this control plane can make,
        // and disclosure is no more reversible than deletion.
        var trimmedReason = RequireReason(reason, MinimumDestructionReasonLength);
        var tenant = await LoadTenantAsync(tenantId, tracked: true, ct);
        RequireConfirmation(confirmation, tenant);
        var businessUnitId = RequirePrimaryBusinessUnit(tenant);

        var requestedOn = DateTime.UtcNow;
        var document = await export.ExportAsync(tenant, businessUnitId, ActorEmail(actor), ct);
        var completedOn = DateTime.UtcNow;

        var sectionsJson = JsonSerializer.Serialize(document.Sections.Select(s => new
        {
            section = s.Section, table = s.Table, rows = s.Rows,
            scopedBy = s.ScopedBy, redactedColumns = s.RedactedColumns
        }));

        var receipt = new TenantExportReceipt
        {
            TenantId = tenant.Id,
            TenantSlug = tenant.Slug,
            RequestedOn = requestedOn,
            CompletedOn = completedOn,
            RequestedBy = ActorEmail(actor),
            ActorPlatformUserId = ActorId(actor),
            Sections = sectionsJson,
            TotalRows = document.TotalRows,
            SizeBytes = document.Content.LongLength,
            ContentSha256 = document.ContentSha256,
            Format = document.Format
        };

        await InTransactionAsync(async () =>
        {
            var record = await LoadOrCreateRecordAsync(tenant.Id, ct);
            record.LastExportedOn = completedOn;
            record.LastExportedBy = receipt.RequestedBy;
            record.ModifiedOn = completedOn;

            context.Set<TenantExportReceipt>().Add(receipt);
            await context.SaveChangesAsync(ct);

            await WriteLifecycleEventAsync(tenant, record.Stage, record.Stage, TenantLifecycleActions.Export,
                trimmedReason, actor, new
                {
                    document.TotalRows,
                    SizeBytes = document.Content.LongLength,
                    Sha256 = document.ContentSha256,
                    Sections = document.Sections.Count,
                    Refused = document.Refused.Select(r => r.Section).ToArray(),
                    document.RowLevelSecurityEnforced
                }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.Export, nameof(Tenant), tenant.Id.ToString(),
                new
                {
                    reason = trimmedReason,
                    rows = document.TotalRows,
                    bytes = document.Content.LongLength,
                    sha256 = document.ContentSha256,
                    format = document.Format,
                    sections = document.Sections.Select(s => new { s.Section, s.Rows }),
                    refused = document.Refused,
                    rowLevelSecurityEnforced = document.RowLevelSecurityEnforced
                },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        return (document, new TenantExportReceiptDto(
            receipt.Id, receipt.RequestedOn, receipt.CompletedOn, receipt.RequestedBy,
            receipt.TotalRows, receipt.SizeBytes, receipt.ContentSha256, receipt.Format, receipt.Sections));
    }

    // ------------------------------------------------------------------- schedule / cancel

    public async Task<TenantOffboardingStatusDto> ScheduleDeletionAsync(
        long tenantId, ScheduleTenantDeletionRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = RequireReason(request?.Reason, MinimumDestructionReasonLength);
        var retentionDays = ResolveRetentionDays(request?.RetentionDays);

        var tenant = await LoadTenantAsync(tenantId, tracked: true, ct);

        // Archived only. Archiving is already the "this customer has left" decision and already
        // denies access, and it is only reachable through Suspended — so a deletion can never be
        // the first anybody hears that a tenant is going.
        if (tenant.Status != TenantLifecycleGraph.DeletionRequiresStatus)
            throw TenantOffboardingRefusedException.Conflict(
                $"Only an {TenantLifecycleGraph.DeletionRequiresStatus} tenant can be scheduled for "
                + $"deletion (current: {tenant.Status}). Suspend and archive it first — that path is "
                + "reversible and this one leads somewhere that is not.");

        await InTransactionAsync(async () =>
        {
            var record = await LoadOrCreateRecordAsync(tenant.Id, ct);
            if (!TenantLifecycleGraph.CanScheduleDeletion(record.Stage))
                throw TenantOffboardingRefusedException.Conflict(
                    $"Deletion cannot be scheduled from stage {record.Stage}.");

            var scheduledOn = DateTime.UtcNow;
            var previous = record.Stage;
            record.Stage = TenantOffboardingStage.PendingDeletion;
            record.RetentionDays = retentionDays;
            record.DeletionScheduledOn = scheduledOn;
            record.PurgeEligibleOn = scheduledOn.AddDays(retentionDays);
            record.DeletionReason = reason;
            record.DeletionScheduledBy = ActorEmail(actor);
            record.ModifiedOn = scheduledOn;
            await context.SaveChangesAsync(ct);

            await WriteLifecycleEventAsync(tenant, previous, record.Stage,
                TenantLifecycleActions.ScheduleDeletion, reason, actor,
                new { retentionDays, purgeEligibleOn = record.PurgeEligibleOn }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.ScheduleDeletion, nameof(Tenant),
                tenant.Id.ToString(),
                new
                {
                    from = previous.ToString(), to = record.Stage.ToString(), reason,
                    retentionDays, purgeEligibleOn = record.PurgeEligibleOn
                },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        logger.LogWarning(
            "Tenant {TenantId} ('{Slug}') is scheduled for deletion; purge becomes possible after "
            + "{RetentionDays} day(s).", tenant.Id, tenant.Slug, retentionDays);

        return await GetStatusAsync(tenantId, ct);
    }

    public async Task<TenantOffboardingStatusDto> CancelDeletionAsync(
        long tenantId, CancelTenantDeletionRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = RequireReason(request?.Reason, minimumLength: 1);
        var tenant = await LoadTenantAsync(tenantId, tracked: true, ct);

        await InTransactionAsync(async () =>
        {
            var record = await LoadRecordAsync(tenant.Id, tracked: true, ct)
                ?? throw TenantOffboardingRefusedException.Conflict(
                    "This tenant has no scheduled deletion to cancel.");

            if (!TenantLifecycleGraph.CanCancelDeletion(record.Stage))
                throw TenantOffboardingRefusedException.Conflict(
                    record.Stage == TenantOffboardingStage.Purged
                        ? "This tenant has already been purged. There is nothing left to cancel."
                        : "This tenant has no scheduled deletion to cancel.");

            var previous = record.Stage;
            var scheduledOn = record.DeletionScheduledOn;
            var eligibleOn = record.PurgeEligibleOn;

            // The clock is cleared, not paused. Rescheduling starts a fresh full window, because a
            // window that resumes where it left off gives a tenant cancelled at day 29 a one-day
            // window on the second attempt — which is not the guarantee anybody was promised.
            record.Stage = TenantOffboardingStage.NotScheduled;
            record.RetentionDays = null;
            record.DeletionScheduledOn = null;
            record.PurgeEligibleOn = null;
            record.DeletionReason = null;
            record.DeletionScheduledBy = null;
            record.ModifiedOn = DateTime.UtcNow;
            await context.SaveChangesAsync(ct);

            await WriteLifecycleEventAsync(tenant, previous, record.Stage,
                TenantLifecycleActions.CancelDeletion, reason, actor,
                new { cancelledSchedule = scheduledOn, cancelledEligibility = eligibleOn }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.CancelDeletion, nameof(Tenant),
                tenant.Id.ToString(),
                new { from = previous.ToString(), to = record.Stage.ToString(), reason },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        return await GetStatusAsync(tenantId, ct);
    }

    // --------------------------------------------------------------------------------- purge

    public async Task<TenantPurgeResultDto> PurgeAsync(
        long tenantId, ConfirmTenantDestructionRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = RequireReason(request?.Reason, MinimumDestructionReasonLength);
        var tenant = await LoadTenantAsync(tenantId, tracked: true, ct);
        var businessUnitId = RequirePrimaryBusinessUnit(tenant);
        RequireConfirmation(request?.Confirmation, tenant);

        var record = await LoadRecordAsync(tenant.Id, tracked: true, ct)
            ?? throw TenantOffboardingRefusedException.Conflict(
                "This tenant has no scheduled deletion. Schedule one and let the retention window "
                + "elapse; there is deliberately no path that destroys a tenant without one.");

        if (!TenantLifecycleGraph.CanPurge(record.Stage))
            throw TenantOffboardingRefusedException.Conflict(
                record.Stage == TenantOffboardingStage.Purged
                    ? $"Tenant {tenant.Id} was already purged on {record.PurgedOn:yyyy-MM-dd HH:mm} UTC."
                    : "This tenant has no scheduled deletion. Schedule one and let the retention "
                      + "window elapse; there is deliberately no path that destroys a tenant without one.");

        // The tenant must STILL be the tenant the deletion was scheduled against.
        //
        // Scheduling requires Archived, and every other guard here re-reads the offboarding record
        // — but nothing re-read the tenant's own status, and nothing unschedules a deletion when a
        // tenant comes back. The reachable sequence: an Owner archives and schedules; the customer
        // returns; a SupportAdmin restores and resumes them (both are Platform.TenantAdmin, and
        // ChangeStatus knows nothing about offboarding); the retention clock keeps running under a
        // live, serving customer; on day 30 the tenant is purged with every guard satisfied.
        //
        // The asymmetry that allowed it is worth naming: CanScheduleDeletion carried a status
        // predicate and CanPurge did not, so the reversible door was guarded and the irreversible
        // one was not. Worse, the role that can bring a tenant back cannot disarm the deletion —
        // CancelDeletion is Owner-only — so the operator best placed to notice was the one operator
        // who could do nothing about it.
        if (tenant.Status != TenantLifecycleGraph.DeletionRequiresStatus)
            throw TenantOffboardingRefusedException.Conflict(
                $"Tenant {tenant.Id} is {tenant.Status}, not {TenantLifecycleGraph.DeletionRequiresStatus}. "
                + "A deletion was scheduled while it was archived, but it has since been brought back "
                + "and is being served. Cancel the scheduled deletion; a live tenant is never purged, "
                + "however long its retention window has been running.");

        // The retention clock. Enforced HERE, in the service, and not merely by a disabled button:
        // the window is the only control standing between a decision and an irreversible act, and
        // a control that a different caller can skip is not one.
        var now = DateTime.UtcNow;
        if (record.PurgeEligibleOn is not DateTime eligibleOn)
            throw TenantOffboardingRefusedException.Conflict(
                "This tenant carries no purge eligibility date, so the retention window cannot be "
                + "shown to have elapsed. Cancel and re-schedule the deletion.");
        if (now < eligibleOn)
            throw TenantOffboardingRefusedException.Conflict(
                $"The retention window has not elapsed. Tenant {tenant.Id} becomes eligible for purge "
                + $"on {eligibleOn:yyyy-MM-dd HH:mm} UTC ({Math.Max(1, (int)Math.Ceiling((eligibleOn - now).TotalDays))} "
                + "day(s) from now). Cancel the deletion if the decision has changed; the window "
                + "cannot be shortened.");

        // A retention window is a promise about a tenant that was ARCHIVED for its whole duration.
        // Restore and resume are reachable throughout it — they live on TenantsController and know
        // nothing about a scheduled deletion — so a tenant can be brought back to life on day two,
        // trade for a month, be archived again for an unrelated reason, and find itself instantly
        // purgeable on the strength of a window it spent alive. The status guard above catches the
        // tenant that is STILL restored; this catches the one that was.
        //
        // Refused rather than silently re-armed: the operator cancels and re-schedules, which
        // starts a fresh full window and leaves both decisions on the record.
        if (record.DeletionScheduledOn is DateTime scheduledOn)
        {
            var reanimated = await context.Set<PlatformAuditLog>().AsNoTracking()
                .Where(a => a.ActAsTenantId == tenant.Id
                            && a.CreatedOn > scheduledOn
                            && ReanimatingActions.Contains(a.Action))
                .OrderByDescending(a => a.CreatedOn)
                .Select(a => new { a.Action, a.CreatedOn })
                .FirstOrDefaultAsync(ct);

            if (reanimated is not null)
                throw TenantOffboardingRefusedException.Conflict(
                    $"Tenant {tenant.Id} was brought back into service on "
                    + $"{reanimated.CreatedOn:yyyy-MM-dd HH:mm} UTC ('{reanimated.Action}'), after its "
                    + "deletion was scheduled. The retention window it has just run through is not a "
                    + "window this tenant spent offboarded, so it does not justify destroying them. "
                    + "Cancel the scheduled deletion and schedule a new one; that starts a fresh "
                    + "window and records the decision.");
        }

        // ---- 1. intent, committed before anything is destroyed -------------------------------
        await InTransactionAsync(async () =>
        {
            // The CLAIM, and it is a compare-and-set rather than a read-then-write.
            //
            // Every guard above this line is a read: two Owners hitting purge at the same instant
            // both passed all of them, both swept, and the loser committed a completion reporting
            // zero rows destroyed — an offboarding record that says the tenant held nothing. The
            // stage cannot be the claim, because it only moves to Purged after the destruction
            // succeeds, and moving it earlier would re-create the one forbidden state: a tenant
            // recorded as purged whose data is still there.
            //
            // So PurgeStartedOn is a LEASE. Setting it is conditional on it being unset or stale,
            // in a single UPDATE the database serialises for us, and the row count says whether
            // this caller won. A handled failure clears it, so a retry is immediate; a process
            // that dies mid-purge leaves it set and the lease expires, which is the only reason
            // the staleness window exists.
            var claimed = await context.Set<TenantOffboarding>()
                .Where(r => r.TenantId == tenant.Id
                            && r.Stage == TenantOffboardingStage.PendingDeletion
                            && (r.PurgeStartedOn == null || r.PurgeStartedOn <= now - PurgeLease))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.PurgeStartedOn, now)
                    .SetProperty(r => r.PurgeReason, reason)
                    .SetProperty(r => r.ModifiedOn, now), ct);

            if (claimed != 1)
                throw TenantOffboardingRefusedException.Conflict(
                    $"A purge of tenant {tenant.Id} is already in progress. Only one may run: two "
                    + "concurrent purges would both sweep, and the second would record a completion "
                    + "claiming the tenant held nothing. Wait for it to finish, or retry after "
                    + $"{PurgeLease.TotalMinutes:0} minutes if the process running it has died.");

            // ExecuteUpdate bypasses the change tracker, so the loaded entity is now stale on the
            // two columns above. Reconciled by hand rather than reloaded: the completion step
            // writes different columns, and a reload here would discard nothing but cost a query.
            context.Entry(record).Property(r => r.PurgeStartedOn).CurrentValue = now;
            context.Entry(record).Property(r => r.PurgeStartedOn).IsModified = false;
            context.Entry(record).Property(r => r.PurgeReason).CurrentValue = reason;
            context.Entry(record).Property(r => r.PurgeReason).IsModified = false;

            await WriteLifecycleEventAsync(tenant, record.Stage, record.Stage,
                TenantLifecycleActions.PurgeStarted, reason, actor,
                new { businessUnitId, eligibleOn }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.PurgeStarted, nameof(Tenant),
                tenant.Id.ToString(),
                new { reason, businessUnitId, purgeEligibleOn = eligibleOn },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        // ---- 2. the destruction itself, on the owner connection ------------------------------
        TenantPurgeOutcome outcome;
        try
        {
            outcome = await purge.ExecuteAsync(tenant.Id, businessUnitId, ct);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Purge FAILED for tenant {TenantId} (business unit {BusinessUnitId}) after intent was "
                + "recorded. The tenant remains PendingDeletion and the purge can be re-run.",
                tenant.Id, businessUnitId);

            await InTransactionAsync(async () =>
            {
                // Release the lease. This attempt is over and we know it, so the operator retries
                // immediately rather than waiting out a window that exists only for the case where
                // nobody is left to tell us. The PurgeFailed event below is the durable record
                // that it was tried.
                await context.Set<TenantOffboarding>()
                    .Where(r => r.TenantId == tenant.Id && r.Stage == TenantOffboardingStage.PendingDeletion)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.PurgeStartedOn, (DateTime?)null), ct);
                context.Entry(record).Property(r => r.PurgeStartedOn).CurrentValue = null;
                context.Entry(record).Property(r => r.PurgeStartedOn).IsModified = false;

                await WriteLifecycleEventAsync(tenant, record.Stage, record.Stage,
                    TenantLifecycleActions.PurgeFailed, reason, actor,
                    new { businessUnitId, error = exception.Message }, ct);

                await audit.WriteAsync(actor, TenantLifecycleActions.PurgeFailed, nameof(Tenant),
                    tenant.Id.ToString(), new { reason, businessUnitId, error = exception.Message },
                    actAsTenantId: tenant.Id, httpContext: httpContext,
                    result: PlatformAuditResults.Failure, ct: ct);
            }, ct);
            throw;
        }

        // ---- 3. completion -------------------------------------------------------------------
        var completedOn = DateTime.UtcNow;
        Support.SupportTicketRedactionResult support = new(0, 0);
        await InTransactionAsync(async () =>
        {
            // Support tickets are the OPERATOR's record of what was promised and escalated, so
            // they survive the purge — but the customer's words inside them do not. The ticket
            // module owns that distinction and exposes this hook for it; the purge only decides
            // WHEN it runs. Optional so a deployment without the support module still purges, and
            // idempotent (it selects on RedactedAtUtc IS NULL) so a re-run after a failed
            // completion neither double-counts nor re-erases.
            if (supportRedaction is not null)
                support = await supportRedaction.RedactForPurgedTenantAsync(actor, tenant.Id, reason, ct);

            var previous = record.Stage;
            record.Stage = TenantOffboardingStage.Purged;
            record.PurgedOn = completedOn;
            record.PurgedBy = ActorEmail(actor);
            record.PurgedRowCount = outcome.RowsDeleted;
            record.ModifiedOn = completedOn;
            await context.SaveChangesAsync(ct);

            await WriteLifecycleEventAsync(tenant, previous, record.Stage, TenantLifecycleActions.Purged,
                reason, actor,
                new
                {
                    businessUnitId,
                    outcome.RowsDeleted,
                    support.TicketsRedacted,
                    support.NotesErased,
                    tables = outcome.Deleted.Select(d => new { d.Table, d.Rows })
                }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.Purged, nameof(Tenant), tenant.Id.ToString(),
                new
                {
                    reason, businessUnitId, outcome.RowsDeleted,
                    supportTicketsRedacted = support.TicketsRedacted,
                    supportNotesErased = support.NotesErased,
                    tables = outcome.Deleted.Select(d => new { d.Table, d.Rows })
                },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        // The tenant is Archived and stays Archived, so status enforcement never lapsed; the
        // eviction is here because the cached snapshot still names a plan and a business unit that
        // no longer have any rows behind them.
        tenantAccess?.Evict(businessUnitId);

        var lifecycleEvents = await context.Set<TenantLifecycleEvent>().AsNoTracking()
            .CountAsync(e => e.TenantId == tenant.Id, ct);
        var auditRecords = await context.Set<PlatformAuditLog>().AsNoTracking()
            .CountAsync(a => a.ActAsTenantId == tenant.Id, ct);

        return new TenantPurgeResultDto(
            tenant.Id, tenant.Slug, outcome.RowsDeleted, outcome.Deleted.Count, outcome.Deleted,
            lifecycleEvents, auditRecords, support.TicketsRedacted, support.NotesErased,
            $"Destroyed {outcome.RowsDeleted:N0} row(s) across {outcome.Deleted.Count} table(s) for "
            + $"tenant '{tenant.Slug}'. {lifecycleEvents:N0} lifecycle event(s) and {auditRecords:N0} "
            + "platform audit record(s) were retained.",
            [TenantOffboardingDisclosure.Irreversible, TenantOffboardingDisclosure.Survives,
             TenantOffboardingDisclosure.DeletionIsNotErasure]);
    }

    // ------------------------------------------------------------------------------- erasure

    /// <summary>
    /// Erases the personal data and leaves the commercial records. Independent of the deletion
    /// path in both directions — see <see cref="TenantOffboardingDisclosure.ErasureIsNotDeletion"/>.
    /// </summary>
    public async Task<TenantErasureResultDto> ErasePersonalDataAsync(
        long tenantId, ConfirmTenantDestructionRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = RequireReason(request?.Reason, MinimumDestructionReasonLength);
        var tenant = await LoadTenantAsync(tenantId, tracked: true, ct);
        RequireConfirmation(request?.Confirmation, tenant);

        if (!TenantLifecycleGraph.ErasureAllowedFrom(tenant.Status))
            throw TenantOffboardingRefusedException.Conflict(
                $"Personal data cannot be erased while the tenant is {tenant.Status}. Erasure "
                + "deactivates every user and replaces their sign-in address, so on a live tenant it "
                + "is an unannounced outage. Suspend the tenant first.");

        IReadOnlyList<TenantErasureTarget> targets = [];

        await InTransactionAsync(async () =>
        {
            var record = await LoadOrCreateRecordAsync(tenant.Id, ct);
            if (!TenantLifecycleGraph.CanErase(record.Stage))
                throw TenantOffboardingRefusedException.Conflict(
                    "This tenant has been purged. Its personal data went with its records; there is "
                    + "nothing left to erase.");

            targets = await eraser.EraseAsync(tenant, tenant.PrimaryBusinessUnitId, ct);
            var erasedOn = DateTime.UtcNow;

            record.PersonalDataErasedOn = erasedOn;
            record.PersonalDataErasedBy = ActorEmail(actor);
            record.PersonalDataErasureReason = reason;
            record.ErasedIdentityCount = targets.Sum(t => t.IdentitiesErased);
            record.ModifiedOn = erasedOn;
            await context.SaveChangesAsync(ct);

            // FromStage and ToStage are the SAME on purpose: erasure is not a move along the
            // deletion path, and recording it as one would make the history claim a transition
            // that never happened.
            await WriteLifecycleEventAsync(tenant, record.Stage, record.Stage,
                TenantLifecycleActions.ErasePersonalData, reason, actor,
                new { targets = targets.Select(t => new { t.Target, t.IdentitiesErased }) }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.ErasePersonalData, nameof(Tenant),
                tenant.Id.ToString(),
                new
                {
                    reason,
                    identitiesErased = targets.Sum(t => t.IdentitiesErased),
                    targets = targets.Select(t => new { t.Target, t.IdentitiesErased })
                },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        // Every user is now deactivated and holds no usable credential; the cached snapshot that
        // decides seat counts and access must not survive the operation that invalidated it.
        if (tenant.PrimaryBusinessUnitId is long erasedBusinessUnit)
            tenantAccess?.Evict(erasedBusinessUnit);

        var identities = targets.Sum(t => t.IdentitiesErased);
        return new TenantErasureResultDto(
            tenant.Id, identities, targets,
            $"Replaced {identities:N0} personal identity/identities for tenant '{tenant.Slug}'. "
            + "Commercial records were not touched.",
            [TenantOffboardingDisclosure.ErasureIsNotDeletion]);
    }

    // ------------------------------------------------------------------------------- helpers

    /// <summary>
    /// Runs one unit of bookkeeping inside the retriable execution strategy the context is
    /// configured with. The ChangeTracker is deliberately NOT cleared here, unlike in
    /// <c>TenantsController</c>: these blocks operate on entities the caller already loaded and
    /// still holds, and a retry re-runs the same delegate against the same tracked graph.
    /// </summary>
    private async Task InTransactionAsync(Func<Task> work, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(ct);
            await work();
            await transaction.CommitAsync(ct);
        });
    }

    private async Task WriteLifecycleEventAsync(
        Tenant tenant, TenantOffboardingStage from, TenantOffboardingStage to, string action,
        string reason, ClaimsPrincipal actor, object? detail, CancellationToken ct)
    {
        context.Set<TenantLifecycleEvent>().Add(new TenantLifecycleEvent
        {
            TenantId = tenant.Id,
            // Copied, never joined: this row has to stay readable after the tenant it describes
            // has been destroyed.
            TenantSlug = tenant.Slug,
            TenantName = tenant.Name,
            Action = action,
            FromStage = from.ToString(),
            ToStage = to.ToString(),
            TenantStatus = tenant.Status.ToString(),
            Reason = reason,
            ActorPlatformUserId = ActorId(actor),
            ActorEmail = ActorEmail(actor),
            Detail = detail is null ? null : JsonSerializer.Serialize(detail),
            OccurredOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync(ct);
    }

    private async Task<Tenant> LoadTenantAsync(long tenantId, bool tracked, CancellationToken ct)
    {
        var query = context.Set<Tenant>().IgnoreQueryFilters();
        if (!tracked) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(t => t.Id == tenantId, ct)
               ?? throw new TenantOffboardingNotFoundException(tenantId);
    }

    private async Task<TenantOffboarding?> LoadRecordAsync(long tenantId, bool tracked, CancellationToken ct)
    {
        var query = context.Set<TenantOffboarding>().AsQueryable();
        if (!tracked) query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(r => r.TenantId == tenantId, ct);
    }

    private async Task<TenantOffboarding> LoadOrCreateRecordAsync(long tenantId, CancellationToken ct)
    {
        var record = await LoadRecordAsync(tenantId, tracked: true, ct);
        if (record is not null) return record;

        record = new TenantOffboarding { TenantId = tenantId, CreatedOn = DateTime.UtcNow };
        context.Set<TenantOffboarding>().Add(record);
        await context.SaveChangesAsync(ct);
        return record;
    }

    private int ResolveRetentionDays(int? requested)
    {
        if (requested is not int days) return Options.EffectiveDefaultRetentionDays;

        if (days < Options.EffectiveMinimumRetentionDays || days > Options.EffectiveMaximumRetentionDays)
            // Refused rather than clamped. A caller who asked for three days and silently received
            // thirty has been told something untrue about when the data goes.
            throw TenantOffboardingRefusedException.BadRequest(
                $"retentionDays must be between {Options.EffectiveMinimumRetentionDays} and "
                + $"{Options.EffectiveMaximumRetentionDays}. The floor of "
                + $"{TenantLifecycleOptions.AbsoluteMinimumRetentionDays} day(s) is enforced by the "
                + "platform and cannot be configured away — it is the window in which a deletion "
                + "scheduled by mistake can still be noticed.");

        return days;
    }

    private static string RequireReason(string? reason, int minimumLength)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw TenantOffboardingRefusedException.BadRequest("A reason is required.");
        if (trimmed.Length < minimumLength)
            throw TenantOffboardingRefusedException.BadRequest(
                $"The reason must be at least {minimumLength} characters, so the decision to destroy "
                + "a customer's records is attributable to something a reader can understand later.");
        return trimmed;
    }

    /// <summary>
    /// The typed-confirmation contract. The tenant's exact name, for the reason
    /// <c>TenantDataReset</c> established: a generated token can be echoed back by a script
    /// without anybody reading it, whereas typing the customer's name requires knowing which
    /// customer is about to be destroyed. Ordinal and case-sensitive — "acme" is a different
    /// customer from "ACME" often enough to matter.
    /// </summary>
    private static void RequireConfirmation(string? confirmation, Tenant tenant)
    {
        if (!string.Equals(confirmation?.Trim(), tenant.Name, StringComparison.Ordinal))
            throw TenantOffboardingRefusedException.BadRequest(
                $"Confirmation did not match. Type the tenant's name exactly — '{tenant.Name}' — to "
                + "confirm this operation against this customer.");
    }

    private static long RequirePrimaryBusinessUnit(Tenant tenant) =>
        tenant.PrimaryBusinessUnitId
        ?? throw TenantOffboardingRefusedException.Conflict(
            $"Tenant {tenant.Id} has no primary business unit, so there is no way to identify which "
            + "rows are theirs. Refusing rather than guessing: the guess would either miss their "
            + "data or take somebody else's.");

    private TenantOffboardingStatusDto ToStatusDto(
        Tenant tenant, TenantOffboarding? record,
        IReadOnlyList<TenantLifecycleEventDto> history, IReadOnlyList<TenantExportReceiptDto> exports)
    {
        var now = DateTime.UtcNow;
        var stage = record?.Stage ?? TenantOffboardingStage.NotScheduled;
        var eligible = IsEligible(record?.PurgeEligibleOn, now);

        return new TenantOffboardingStatusDto(
            tenant.Id, tenant.Name, tenant.Slug, tenant.Status.ToString(), stage.ToString(),
            record?.RetentionDays, record?.DeletionScheduledOn, record?.PurgeEligibleOn,
            eligible, DaysUntil(record?.PurgeEligibleOn, now),
            record?.DeletionReason, record?.DeletionScheduledBy,
            record?.PurgedOn, record?.PurgedBy, record?.PurgedRowCount,
            record?.PersonalDataErasedOn, record?.PersonalDataErasedBy, record?.ErasedIdentityCount,
            record?.LastExportedOn, record?.LastExportedBy,

            CanScheduleDeletion: TenantLifecycleGraph.CanScheduleDeletion(stage)
                                 && tenant.Status == TenantLifecycleGraph.DeletionRequiresStatus,
            CanCancelDeletion: TenantLifecycleGraph.CanCancelDeletion(stage),
            // Mirrors the guard inside PurgeAsync, including the status predicate: a tenant that
            // has been brought back to life is no longer purgeable, so the console must stop
            // offering it rather than let an operator discover that at the confirmation dialog.
            CanPurge: TenantLifecycleGraph.CanPurge(stage) && eligible
                      && tenant.Status == TenantLifecycleGraph.DeletionRequiresStatus,
            CanErasePersonalData: TenantLifecycleGraph.CanErase(stage)
                                  && TenantLifecycleGraph.ErasureAllowedFrom(tenant.Status),

            ConfirmationRequired: tenant.Name,
            History: history,
            Exports: exports,
            Disclosures:
            [
                TenantOffboardingDisclosure.Irreversible,
                TenantOffboardingDisclosure.Survives,
                TenantOffboardingDisclosure.ErasureIsNotDeletion,
                TenantOffboardingDisclosure.DeletionIsNotErasure
            ]);
    }

    private static bool IsEligible(DateTime? eligibleOn, DateTime now) =>
        eligibleOn is DateTime on && now >= on;

    private static int? DaysUntil(DateTime? eligibleOn, DateTime now) =>
        eligibleOn is DateTime on ? Math.Max(0, (int)Math.Ceiling((on - now).TotalDays)) : null;

    private static string ActorEmail(ClaimsPrincipal actor) =>
        actor.FindFirst("email")?.Value ?? actor.FindFirst(ClaimTypes.Email)?.Value ?? "platform";

    private static long ActorId(ClaimsPrincipal actor) =>
        long.TryParse(
            actor.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? actor.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) && id > 0
            ? id
            : 0;
}
