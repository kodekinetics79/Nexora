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
    TenantStoragePurger storage,
    TenantPersonalDataEraser eraser,
    TenantDataExportService export,
    IOptions<TenantLifecycleOptions> options,
    ILogger<TenantOffboardingService> logger,
    ITenantOffboardingReadinessService readiness,
    ITenantAccessService? tenantAccess = null,
    Support.ISupportTicketRedactionService? supportRedaction = null,
    TimeProvider? timeProvider = null)
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
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ---------------------------------------------------------------------------------- read

    /// <param name="viewer">
    /// The operator asking. Optional, and null only for callers that have no principal to offer:
    /// the separation-of-duties verdict is a fact about a PERSON, so a status read with no person
    /// reports <c>PurgeRequiresDifferentApprover = false</c> rather than guessing. Nothing depends
    /// on that flag for safety — <see cref="PurgeAsync"/> re-checks independently — it exists so the
    /// console stops offering a button the server is going to refuse.
    /// </param>
    public async Task<TenantOffboardingStatusDto> GetStatusAsync(
        long tenantId, CancellationToken ct, ClaimsPrincipal? viewer = null)
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

        // Read from the append-only event, not from TenantOffboarding.DeletionScheduledBy — the same
        // source PurgeAsync consults, so the console's verdict and the server's cannot disagree.
        var scheduledOn = record?.DeletionScheduledOn;
        var scheduler = record?.Stage != TenantOffboardingStage.PendingDeletion
            ? null
            : await context.Set<TenantLifecycleEvent>().AsNoTracking()
                .Where(e => e.TenantId == tenantId
                            && e.Action == TenantLifecycleActions.ScheduleDeletion
                            && (scheduledOn == null || e.OccurredOn >= scheduledOn))
                .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id)
                .Select(e => new { e.ActorPlatformUserId, e.ActorEmail })
                .FirstOrDefaultAsync(ct);

        var viewerId = viewer is null ? 0 : ActorId(viewer);
        var sameHand = scheduler is not null && viewerId > 0
                       && scheduler.ActorPlatformUserId == viewerId;

        var commercialPosition = await readiness.GetCommercialRetirementPositionAsync(tenant, ct);
        var scheduleReadiness = await readiness.AssessAsync(
            tenant, TenantOffboardingReadinessPhase.Schedule, ct);

        return ToStatusDto(
            tenant, record, history, exports,
            sameHand,
            scheduler?.ActorPlatformUserId > 0 ? scheduler.ActorEmail : null,
            // Asked of the readiness service rather than re-derived here, so the screen and the
            // gate cannot drift apart.
            commercialPosition,
            scheduleReadiness);
    }

    /// <summary>
    /// The operator's work queue: every tenant whose retention clock is running, soonest first.
    /// Projected rather than materialised — the platform roles hold column-level SELECT on
    /// <c>platform."Tenants"</c>, and a query that reads the whole entity fails with 42501 at
    /// runtime while every SQLite test stays green.
    /// </summary>
    public async Task<IReadOnlyList<PendingTenantDeletionDto>> ListPendingDeletionsAsync(CancellationToken ct)
    {
        var now = UtcNow;

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

        var requestedOn = UtcNow;
        var document = await export.ExportAsync(tenant, businessUnitId, ActorEmail(actor), ct);
        var completedOn = UtcNow;

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

    /// <summary>
    /// Records the narrow, auditable exception for a legacy Production-labelled tenant that never
    /// became a billed customer. The attestation changes no tenant profile and destroys nothing;
    /// it merely makes the otherwise impossible commercial-closure questions vacuous. Every
    /// structural offboarding control remains in force.
    /// </summary>
    public async Task<TenantOffboardingStatusDto> AttestNonCustomerAsync(
        long tenantId, AttestNonCustomerRetirementRequest request, ClaimsPrincipal actor,
        HttpContext? httpContext, CancellationToken ct)
    {
        var reason = RequireReason(request?.Reason, MinimumDestructionReasonLength);
        var tenant = await LoadTenantAsync(tenantId, tracked: false, ct);
        RequireConfirmation(request?.Confirmation, tenant);

        if (tenant.Status != TenantLifecycleGraph.DeletionRequiresStatus)
            throw TenantOffboardingRefusedException.Conflict(
                "A non-customer retirement attestation is available only after the tenant is Archived.");

        var record = await LoadRecordAsync(tenant.Id, tracked: false, ct);
        var stage = record?.Stage ?? TenantOffboardingStage.NotScheduled;
        if (stage != TenantOffboardingStage.NotScheduled)
            throw TenantOffboardingRefusedException.Conflict(
                $"A non-customer retirement attestation cannot be recorded from stage {stage}.");

        var position = await readiness.GetCommercialRetirementPositionAsync(tenant, ct);
        if (position.HasCommercialFootprint)
            throw TenantOffboardingRefusedException.Conflict(
                "This tenant has platform billing records and cannot be attested as a non-customer. "
                + $"Found {position.BillingStatementCount} billing statement(s) and "
                + $"{position.SubscriptionInvoiceCount} subscription invoice(s); complete the "
                + "governed financial close instead.");

        // Idempotent replay: the immutable fact already exists, so do not create a second audit
        // decision merely because a browser retried after losing the response.
        if (position.HasNonCustomerAttestation)
            return await GetStatusAsync(tenantId, ct, actor);

        if (tenant.DeploymentProfile != TenantDeploymentProfile.Production)
            throw TenantOffboardingRefusedException.Conflict(
                "This tenant already has a governed non-production deployment profile and does not "
                + "need a separate non-customer retirement attestation.");

        await InTransactionAsync(async () =>
        {
            // Re-read the commercial facts inside the write transaction. Scheduling re-checks them
            // again, so a billing record that appears later always wins over this attestation.
            var current = await readiness.GetCommercialRetirementPositionAsync(tenant, ct);
            if (current.HasCommercialFootprint)
                throw TenantOffboardingRefusedException.Conflict(
                    "Billing records appeared while the attestation was being recorded. Nothing was "
                    + "waived; complete the governed financial close instead.");
            if (current.HasNonCustomerAttestation) return;

            await WriteLifecycleEventAsync(
                tenant, stage, stage, TenantLifecycleActions.AttestNonCustomer, reason, actor,
                new
                {
                    tenant.DeploymentProfile,
                    current.BillingStatementCount,
                    current.SubscriptionInvoiceCount,
                    scope = "commercial-closure-only",
                    controlsRetained = new[]
                    {
                        "archive", "legal-hold", "customer-export", "personal-data-erasure",
                        "retention-window", "independent-purge-approval"
                    }
                }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.AttestNonCustomer, nameof(Tenant),
                tenant.Id.ToString(),
                new
                {
                    reason,
                    tenant.DeploymentProfile,
                    current.BillingStatementCount,
                    current.SubscriptionInvoiceCount,
                    scope = "commercial-closure-only"
                },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        logger.LogWarning(
            "Tenant {TenantId} ('{Slug}') was attested as a non-customer for commercial closure; "
            + "all structural offboarding controls remain required.", tenant.Id, tenant.Slug);

        return await GetStatusAsync(tenantId, ct, actor);
    }

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

        await RequireReadinessAsync(tenant, TenantOffboardingReadinessPhase.Schedule, ct);

        await InTransactionAsync(async () =>
        {
            var record = await LoadOrCreateRecordAsync(tenant.Id, ct);
            if (!TenantLifecycleGraph.CanScheduleDeletion(record.Stage))
                throw TenantOffboardingRefusedException.Conflict(
                    $"Deletion cannot be scheduled from stage {record.Stage}.");

            var scheduledOn = UtcNow;
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

        return await GetStatusAsync(tenantId, ct, actor);
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
            record.ModifiedOn = UtcNow;
            await context.SaveChangesAsync(ct);

            await WriteLifecycleEventAsync(tenant, previous, record.Stage,
                TenantLifecycleActions.CancelDeletion, reason, actor,
                new { cancelledSchedule = scheduledOn, cancelledEligibility = eligibleOn }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.CancelDeletion, nameof(Tenant),
                tenant.Id.ToString(),
                new { from = previous.ToString(), to = record.Stage.ToString(), reason },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
        }, ct);

        return await GetStatusAsync(tenantId, ct, actor);
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

        // Scheduling was evidence-gated too, but financial reconciliation can be reopened during
        // the retention window. Re-evaluate at the irreversible boundary before recording intent.
        await RequireReadinessAsync(tenant, TenantOffboardingReadinessPhase.Purge, ct);

        // The retention clock. Enforced HERE, in the service, and not merely by a disabled button:
        // the window is the only control standing between a decision and an irreversible act, and
        // a control that a different caller can skip is not one.
        var now = UtcNow;
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

        var activeHold = await context.Set<TenantLegalHold>().AsNoTracking()
            .Where(h => h.TenantId == tenant.Id && h.ReleasedOn == null)
            .OrderBy(h => h.PlacedOn)
            .Select(h => new { h.Id, h.Scope, h.Authority })
            .FirstOrDefaultAsync(ct);
        if (activeHold is not null)
            throw TenantOffboardingRefusedException.Conflict(
                $"Purge is blocked by active legal hold {activeHold.Id} "
                + $"('{activeHold.Scope}', authority: {activeHold.Authority}). Release the hold "
                + "through its governed workflow before retrying.");

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

        // Separation of duties — LAST among the decision guards, and deliberately so.
        //
        // Every other control on this path is one person satisfying a rule: the Owner policy, the
        // typed name, the reason, the clock, the hold, the readiness evidence. None of them is a
        // second opinion, and the purge is the one act in this system that no later reviewer can
        // correct. The platform already requires an independent checker to finalize a billing
        // statement, to finalize an invoice, to approve a tax rule, to void or refund an invoice, to
        // approve an exchange rate — and, in this very module, to RELEASE a legal hold. Destroying
        // the customer was the only one left where the maker could also be the checker.
        //
        // It runs last because "fetch a second Owner" is only useful advice once nothing else is
        // outstanding. Refusing on this while the retention window still has twenty days to run
        // would send somebody to find a colleague for a purge that was going to be refused anyway,
        // and the operator would learn about the clock on the second attempt.
        //
        // It is SKIPPED when a previous attempt already committed the destructive transaction (see
        // the recovery branch below). At that point the rows are gone and the only thing left to do
        // is write down that they are gone. This rule governs the decision to destroy, not the
        // bookkeeping that follows one; applying it here would strand a tenant in the "executed but
        // not finalized" state — the one state this module calls tolerable precisely because it
        // self-heals — until somebody with the right identity happened to retry.
        if (record.PurgeExecutedOn is null)
            await RequireIndependentPurgeApproverAsync(tenant, record, actor, ct);

        var purgeAttemptId = Guid.NewGuid();
        TenantPurgeOutcome? outcome = null;

        // A previous process may have committed the destructive owner transaction and died before
        // it could write the platform audit completion. That outcome is part of the same commit as
        // the deletes, so it is authoritative and safe to finalize without sweeping again.
        if (record.PurgeExecutedOn is not null)
        {
            if (record.PurgeAttemptId is null || record.PurgeExecutedRowCount is null)
                throw TenantOffboardingRefusedException.Conflict(
                    "The purge execution record is incomplete and cannot be finalized safely. "
                    + "Escalate for lifecycle recovery; do not start another destructive attempt.");

            purgeAttemptId = record.PurgeAttemptId.Value;
            var deleted = string.IsNullOrWhiteSpace(record.PurgeExecutionDetail)
                ? []
                : JsonSerializer.Deserialize<List<TenantPurgeTableCount>>(record.PurgeExecutionDetail) ?? [];
            outcome = new TenantPurgeOutcome(
                tenant.Id, businessUnitId, record.PurgeExecutedRowCount.Value, deleted);
        }

        // ---- 1. intent, committed before anything is destroyed -------------------------------
        if (outcome is null)
        {
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
                            && context.Set<Tenant>().IgnoreQueryFilters().Any(t =>
                                t.Id == r.TenantId
                                && t.Status == TenantLifecycleGraph.DeletionRequiresStatus)
                            && !context.Set<TenantLegalHold>().Any(h =>
                                h.TenantId == r.TenantId && h.ReleasedOn == null)
                            && r.PurgeExecutedOn == null
                            && (r.PurgeStartedOn == null || r.PurgeStartedOn <= now - PurgeLease))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.PurgeStartedOn, now)
                    .SetProperty(r => r.PurgeAttemptId, purgeAttemptId)
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
            context.Entry(record).Property(r => r.PurgeAttemptId).CurrentValue = purgeAttemptId;
            context.Entry(record).Property(r => r.PurgeAttemptId).IsModified = false;
            context.Entry(record).Property(r => r.PurgeReason).CurrentValue = reason;
            context.Entry(record).Property(r => r.PurgeReason).IsModified = false;

            await WriteLifecycleEventAsync(tenant, record.Stage, record.Stage,
                TenantLifecycleActions.PurgeStarted, reason, actor,
                new { businessUnitId, eligibleOn, purgeAttemptId }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.PurgeStarted, nameof(Tenant),
                tenant.Id.ToString(),
                new { reason, businessUnitId, purgeEligibleOn = eligibleOn, purgeAttemptId },
                actAsTenantId: tenant.Id, httpContext: httpContext, ct: ct);
            }, ct);
        }

        // ---- 2. the destruction itself, on the owner connection ------------------------------
        {
            if (outcome is null)
            try
            {
                // THE BYTE INVENTORY, taken and COMMITTED before a single row is deleted.
                //
                // A purge used to issue SQL and nothing else, so a completed offboarding left 273
                // evidence objects and a 5 GB disk untouched — the raw .eml of every message the
                // tenant ever received included — while telling the customer their data was gone.
                // It cannot be fixed by deleting the bytes afterwards: destroying the rows
                // destroys the only index back to them, and after the transaction nothing can name
                // them any more.
                //
                // It also cannot join the destructive transaction. Object storage has no rollback,
                // so a delete inside the transaction would be permanent even when the transaction
                // was not — a tenant whose purge was correctly aborted would find their files gone
                // regardless. So the list is written down first, and the deletion is a recorded
                // follow-up step against it. That is what makes it resumable, and what makes a
                // half-emptied bucket a state somebody can find rather than one nobody can see.
                var captured = await storage.CaptureAsync(
                    businessUnitId, await purge.CaptureStoragePathsAsync(businessUnitId, ct), ct);
                var capturedJson = JsonSerializer.Serialize(captured);
                await InTransactionAsync(async () =>
                {
                    await context.Set<TenantOffboarding>()
                        .Where(r => r.TenantId == tenant.Id && r.PurgeAttemptId == purgeAttemptId)
                        .ExecuteUpdateAsync(setters => setters
                            .SetProperty(r => r.StoragePurgeInventory, capturedJson)
                            .SetProperty(r => r.StoragePurgeCompletedOn, (DateTime?)null)
                            .SetProperty(r => r.StoragePurgeOutstandingCount, (int?)null), ct);
                    context.Entry(record).Property(r => r.StoragePurgeInventory).CurrentValue = capturedJson;
                    context.Entry(record).Property(r => r.StoragePurgeInventory).IsModified = false;
                }, ct);

                outcome = await purge.ExecuteAsync(tenant.Id, businessUnitId, purgeAttemptId, ct);
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
                    .Where(r => r.TenantId == tenant.Id
                                && r.Stage == TenantOffboardingStage.PendingDeletion
                                && r.PurgeAttemptId == purgeAttemptId
                                && r.PurgeExecutedOn == null)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.PurgeStartedOn, (DateTime?)null)
                        .SetProperty(r => r.PurgeAttemptId, (Guid?)null), ct);
                context.Entry(record).Property(r => r.PurgeStartedOn).CurrentValue = null;
                context.Entry(record).Property(r => r.PurgeStartedOn).IsModified = false;
                context.Entry(record).Property(r => r.PurgeAttemptId).CurrentValue = null;
                context.Entry(record).Property(r => r.PurgeAttemptId).IsModified = false;

                await WriteLifecycleEventAsync(tenant, record.Stage, record.Stage,
                    TenantLifecycleActions.PurgeFailed, reason, actor,
                    new { businessUnitId, purgeAttemptId, error = exception.Message }, ct);

                await audit.WriteAsync(actor, TenantLifecycleActions.PurgeFailed, nameof(Tenant),
                    tenant.Id.ToString(), new { reason, businessUnitId, purgeAttemptId, error = exception.Message },
                    actAsTenantId: tenant.Id, httpContext: httpContext,
                    result: PlatformAuditResults.Failure, ct: ct);
            }, ct);
                throw;
            }
        }

        // The branch above either restored a committed execution or executed one now.
        var completedOutcome = outcome
            ?? throw new InvalidOperationException("The fenced purge produced no durable outcome.");

        // ---- 2b. the bytes ------------------------------------------------------------------
        // Re-run safe by construction: every object is addressed by (bucket, key, version) and an
        // absent one counts as success, so a retry after a partial sweep finishes the job rather
        // than failing on what it already did. This is where a recovered attempt resumes too — the
        // inventory outlived the rows precisely so that it could.
        var storageReport = await PurgeStoredBytesAsync(tenant.Id, businessUnitId, purgeAttemptId, record, ct);
        if (!storageReport.IsComplete)
            throw TenantOffboardingRefusedException.Conflict(
                $"Tenant {tenant.Id}'s rows are destroyed, but {storageReport.Outstanding} stored "
                + "object(s) could not be deleted, so this purge is INCOMPLETE and has not been "
                + "recorded as finished. "
                + string.Join("; ", storageReport.Refusals.Take(5)
                    .Select(r => $"{r.Bucket}/{r.Key}: {r.Refusal}"))
                + ". The inventory is held on the offboarding record; re-run the purge once the "
                + "store is reachable and it will resume from where it stopped. A purge that could "
                + "not delete the customer's files must not report that it did.");

        // ---- 3. completion -------------------------------------------------------------------
        var completedOn = UtcNow;
        Support.SupportTicketRedactionResult support = new(0, 0);
        await InTransactionAsync(async () =>
        {
            var previous = record.Stage;
            var finalized = await context.Set<TenantOffboarding>()
                .Where(r => r.TenantId == tenant.Id
                            && r.Stage == TenantOffboardingStage.PendingDeletion
                            && r.PurgeAttemptId == purgeAttemptId
                            && r.PurgeExecutedOn != null)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Stage, TenantOffboardingStage.Purged)
                    .SetProperty(r => r.PurgedOn, completedOn)
                    .SetProperty(r => r.PurgedBy, ActorEmail(actor))
                    .SetProperty(r => r.PurgedRowCount, completedOutcome.RowsDeleted)
                    .SetProperty(r => r.ModifiedOn, completedOn), ct);
            if (finalized != 1)
                throw TenantOffboardingRefusedException.Conflict(
                    $"Purge attempt {purgeAttemptId} is no longer the active completed attempt. "
                    + "It was fenced and cannot write completion evidence.");

            // Support tickets are the OPERATOR's record of what was promised and escalated, so
            // they survive the purge — but the customer's words inside them do not. This occurs
            // after the token-conditional finalize inside the same transaction: a fenced attempt
            // cannot redact support content, and a later failure rolls both changes back.
            if (supportRedaction is not null)
                support = await supportRedaction.RedactForPurgedTenantAsync(actor, tenant.Id, reason, ct);

            context.Entry(record).State = EntityState.Detached;

            await WriteLifecycleEventAsync(tenant, previous, TenantOffboardingStage.Purged, TenantLifecycleActions.Purged,
                reason, actor,
                new
                {
                    businessUnitId,
                    purgeAttemptId,
                    completedOutcome.RowsDeleted,
                    support.TicketsRedacted,
                    support.NotesErased,
                    tables = completedOutcome.Deleted.Select(d => new { d.Table, d.Rows })
                }, ct);

            await audit.WriteAsync(actor, TenantLifecycleActions.Purged, nameof(Tenant), tenant.Id.ToString(),
                new
                {
                    reason, businessUnitId, purgeAttemptId, completedOutcome.RowsDeleted,
                    supportTicketsRedacted = support.TicketsRedacted,
                    supportNotesErased = support.NotesErased,
                    tables = completedOutcome.Deleted.Select(d => new { d.Table, d.Rows })
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
            tenant.Id, tenant.Slug, completedOutcome.RowsDeleted, completedOutcome.Deleted.Count, completedOutcome.Deleted,
            lifecycleEvents, auditRecords, support.TicketsRedacted, support.NotesErased,
            $"Destroyed {completedOutcome.RowsDeleted:N0} row(s) across {completedOutcome.Deleted.Count} table(s) "
            + $"and {storageReport.Deleted:N0} stored object(s) ({storageReport.BytesFreed:N0} byte(s) "
            + $"reclaimed) for tenant '{tenant.Slug}'. {lifecycleEvents:N0} lifecycle event(s) and "
            + $"{auditRecords:N0} platform audit record(s) were retained.",
            [TenantOffboardingDisclosure.Irreversible, TenantOffboardingDisclosure.Survives,
             TenantOffboardingDisclosure.StatutoryRecordsMoveWithTheCustomer,
             TenantOffboardingDisclosure.DeletionIsNotErasure])
        {
            StoredObjectsDeleted = storageReport.Deleted,
            StoredObjectsAlreadyAbsent = storageReport.AlreadyAbsent,
            StorageBytesFreed = storageReport.BytesFreed
        };
    }

    // ------------------------------------------------------------------------------- erasure

    /// <summary>
    /// Erases personal data while leaving commercial and preserved legal/financial evidence.
    /// Erasure does not schedule deletion, but its persisted proof is required by the readiness
    /// gate before a scheduled deletion may advance to destructive purge.
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
            // This UPDATE takes the same row lock as legal-hold placement and the owner purge
            // transaction. The hold predicate is therefore stable until erasure commits.
            await context.Set<TenantOffboarding>().Where(r => r.TenantId == tenant.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.ModifiedOn, UtcNow), ct);
            await context.Entry(record).ReloadAsync(ct);
            if (await context.Set<TenantLegalHold>().AnyAsync(
                    h => h.TenantId == tenant.Id && h.ReleasedOn == null, ct))
                throw TenantOffboardingRefusedException.Conflict(
                    "Personal-data erasure is blocked by an active legal hold. Release the hold "
                    + "through its governed workflow before retrying.");
            if (!TenantLifecycleGraph.CanErase(record.Stage))
                throw TenantOffboardingRefusedException.Conflict(
                    "This tenant has been purged. Its personal data went with its records; there is "
                    + "nothing left to erase.");

            targets = await eraser.EraseAsync(tenant, tenant.PrimaryBusinessUnitId, ct);
            var erasedOn = UtcNow;

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
    /// Refuses a purge executed by a platform user who supplied either destructive prerequisite:
    /// scheduling the deletion or attesting that a Production-labelled tenant was not a customer.
    ///
    /// <para><b>Where the maker's identity comes from, and why not from the offboarding record.</b>
    /// <see cref="TenantOffboarding.DeletionScheduledBy"/> holds an email, and an email is a label
    /// that can be reassigned to a different person. The append-only
    /// <see cref="TenantLifecycleEvent"/> written by <c>ScheduleDeletionAsync</c> already carries
    /// <see cref="TenantLifecycleEvent.ActorPlatformUserId"/> — the account, not the label — and it
    /// cannot be edited afterwards. Reading the maker from the evidence rather than from the mutable
    /// working row is the same choice <c>TenantLegalHoldService</c> makes, and it needs no new
    /// column, so this control is live on the schema that exists today rather than after a
    /// migration.</para>
    ///
    /// <para><b>Unattributable is refused, not waived.</b> An offboarding record whose scheduling
    /// event is missing, or whose actor id is the reserved system id, cannot be shown to have had
    /// two people behind it. <c>CurrencyController</c> reaches the same conclusion about an
    /// exchange rate created by "System": segregation of duties that cannot be verified has not been
    /// observed. The remedy is stated in the refusal and is not destructive — cancel and re-schedule,
    /// which writes a fresh attributable event and restarts the window.</para>
    /// </summary>
    private async Task RequireIndependentPurgeApproverAsync(
        Tenant tenant, TenantOffboarding record, ClaimsPrincipal actor, CancellationToken ct)
    {
        var approverId = ActorId(actor);
        if (approverId <= 0)
            throw TenantOffboardingRefusedException.Conflict(
                "The purging operator carries no platform account identifier, so it cannot be shown "
                + "that a second person authorised this destruction. Sign in again and retry.");

        // The scheduling event for THIS schedule, not an older cancelled one: a tenant can be
        // scheduled, cancelled and re-scheduled, and only the live decision has an approver.
        var scheduledOn = record.DeletionScheduledOn;
        var scheduler = await context.Set<TenantLifecycleEvent>().AsNoTracking()
            .Where(e => e.TenantId == tenant.Id
                        && e.Action == TenantLifecycleActions.ScheduleDeletion
                        && (scheduledOn == null || e.OccurredOn >= scheduledOn))
            .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id)
            .Select(e => new { e.ActorPlatformUserId, e.ActorEmail, e.OccurredOn })
            .FirstOrDefaultAsync(ct);

        if (scheduler is null || scheduler.ActorPlatformUserId <= 0)
            throw TenantOffboardingRefusedException.Conflict(
                "This tenant's scheduled deletion carries no attributable platform account for the "
                + "operator who scheduled it, so segregation of duties cannot be verified and the "
                + "purge is refused. Cancel the scheduled deletion and schedule it again; that "
                + "records an attributable decision and starts a fresh retention window.");

        if (scheduler.ActorPlatformUserId == approverId)
            throw TenantOffboardingRefusedException.Conflict(
                $"The deletion of tenant {tenant.Id} was scheduled by this same operator "
                + $"({scheduler.ActorEmail}) on {scheduler.OccurredOn:yyyy-MM-dd HH:mm} UTC. Destroying "
                + "a customer's records requires a second platform Owner: the person who decided it "
                + "must not also be the person who carries it out. Have another Owner run the purge, "
                + "or cancel the deletion if the decision has changed.");

        var attester = await context.Set<TenantLifecycleEvent>().AsNoTracking()
            .Where(e => e.TenantId == tenant.Id
                        && e.Action == TenantLifecycleActions.AttestNonCustomer)
            .OrderByDescending(e => e.OccurredOn).ThenByDescending(e => e.Id)
            .Select(e => new { e.ActorPlatformUserId, e.ActorEmail, e.OccurredOn })
            .FirstOrDefaultAsync(ct);

        if (attester is null) return;

        if (attester.ActorPlatformUserId <= 0)
            throw TenantOffboardingRefusedException.Conflict(
                "This tenant's non-customer attestation carries no attributable platform account, "
                + "so an independent purge approval cannot be verified and the purge is refused.");

        if (attester.ActorPlatformUserId == approverId)
            throw TenantOffboardingRefusedException.Conflict(
                $"This same operator ({attester.ActorEmail}) attested on "
                + $"{attester.OccurredOn:yyyy-MM-dd HH:mm} UTC that tenant {tenant.Id} was not a "
                + "customer. That attestation waives commercial-closure evidence and is part of the "
                + "destruction decision; its maker cannot also approve the later purge. Have another "
                + "platform Owner run the purge.");
    }

    /// <summary>
    /// Deletes the tenant's stored bytes against the inventory committed before the destruction,
    /// and writes down per object what it managed.
    ///
    /// <para>Records the outcome whether or not it is complete. An incomplete sweep with nothing
    /// written down is indistinguishable from one that never ran, and the whole reason the
    /// inventory is durable is that after the destructive transaction nothing else can name these
    /// objects.</para>
    /// </summary>
    private async Task<TenantStoragePurgeReport> PurgeStoredBytesAsync(
        long tenantId, long businessUnitId, Guid purgeAttemptId, TenantOffboarding record,
        CancellationToken ct)
    {
        // Read back rather than carried in a field: a recovered attempt re-enters here with no
        // in-memory inventory at all, and this is the path that has to work for it.
        var stored = await context.Set<TenantOffboarding>().AsNoTracking()
            .Where(r => r.TenantId == tenantId)
            .Select(r => r.StoragePurgeInventory)
            .FirstOrDefaultAsync(ct);

        var inventory = string.IsNullOrWhiteSpace(stored)
            ? null
            : JsonSerializer.Deserialize<TenantStoragePurgeInventory>(stored);

        if (inventory is null)
        {
            // No inventory: a purge that committed under an earlier build, or an attempt that died
            // between destroying the rows and recording what it was about to delete. The rows are
            // gone, so the row-derived half cannot be reconstructed — but the two PREFIX trees can
            // be, because they are keyed by the business unit id and need no row at all.
            //
            // Refusing here was the first version and it was wrong. It stranded a tenant whose data
            // was already destroyed and left every one of the recoverable objects in place in order
            // to protect a handful that were not recoverable either way. Sweeping what is
            // enumerable and SAYING which half could not be is the honest form of the same
            // caution: recorded on the offboarding, so nobody later mistakes it for exhaustive.
            logger.LogWarning(
                "Tenant {TenantId} (business unit {BusinessUnitId}) has no recorded storage "
                + "inventory, so it is being rebuilt from the tenant prefixes alone. Files in the "
                + "legacy flat folders whose rows are already deleted cannot be attributed to this "
                + "tenant by any means and are NOT covered by this sweep.",
                tenantId, businessUnitId);

            inventory = (await storage.CaptureAsync(businessUnitId, [], ct))
                with { ReconstructedFromPrefixesOnly = true };

            var rebuiltJson = JsonSerializer.Serialize(inventory);
            await InTransactionAsync(async () =>
            {
                await context.Set<TenantOffboarding>()
                    .Where(r => r.TenantId == tenantId && r.PurgeAttemptId == purgeAttemptId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(r => r.StoragePurgeInventory, rebuiltJson), ct);
            }, ct);
        }

        var report = await storage.ExecuteAsync(inventory, ct);
        var completedOn = report.IsComplete ? UtcNow : (DateTime?)null;
        var detail = JsonSerializer.Serialize(report.Entries);

        await InTransactionAsync(async () =>
        {
            await context.Set<TenantOffboarding>()
                .Where(r => r.TenantId == tenantId && r.PurgeAttemptId == purgeAttemptId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.StoragePurgeDetail, detail)
                    .SetProperty(r => r.StoragePurgeOutstandingCount, report.Outstanding)
                    .SetProperty(r => r.StoragePurgeCompletedOn, completedOn), ct);
        }, ct);

        if (context.Entry(record).State != EntityState.Detached)
        {
            context.Entry(record).Property(r => r.StoragePurgeDetail).CurrentValue = detail;
            context.Entry(record).Property(r => r.StoragePurgeDetail).IsModified = false;
            context.Entry(record).Property(r => r.StoragePurgeOutstandingCount).CurrentValue = report.Outstanding;
            context.Entry(record).Property(r => r.StoragePurgeOutstandingCount).IsModified = false;
            context.Entry(record).Property(r => r.StoragePurgeCompletedOn).CurrentValue = completedOn;
            context.Entry(record).Property(r => r.StoragePurgeCompletedOn).IsModified = false;
        }

        return report;
    }

    private async Task RequireReadinessAsync(
        Tenant tenant, TenantOffboardingReadinessPhase phase, CancellationToken ct)
    {
        var result = await readiness.AssessAsync(tenant, phase, ct);
        if (result.Ready) return;

        var detail = string.Join(" ", result.Failures.Select(x => $"[{x.Code}] {x.Detail}"));
        throw TenantOffboardingRefusedException.Conflict(
            "Tenant offboarding readiness is blocked. " + detail);
    }

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
            OccurredOn = UtcNow
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

        record = new TenantOffboarding { TenantId = tenantId, CreatedOn = UtcNow };
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
        IReadOnlyList<TenantLifecycleEventDto> history, IReadOnlyList<TenantExportReceiptDto> exports,
        bool purgeRequiresDifferentApprover, string? deletionApprovedBy,
        TenantCommercialRetirementPosition commercialPosition,
        TenantOffboardingReadinessResult scheduleReadiness)
    {
        var now = UtcNow;
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
                                 && tenant.Status == TenantLifecycleGraph.DeletionRequiresStatus
                                 && scheduleReadiness.Ready,
            CanCancelDeletion: TenantLifecycleGraph.CanCancelDeletion(stage),
            // Mirrors the guard inside PurgeAsync, including the status predicate: a tenant that
            // has been brought back to life is no longer purgeable, so the console must stop
            // offering it rather than let an operator discover that at the confirmation dialog.
            CanPurge: TenantLifecycleGraph.CanPurge(stage) && eligible
                      && tenant.Status == TenantLifecycleGraph.DeletionRequiresStatus,
            CanErasePersonalData: TenantLifecycleGraph.CanErase(stage)
                                  && TenantLifecycleGraph.ErasureAllowedFrom(tenant.Status),

            PurgeRequiresDifferentApprover: purgeRequiresDifferentApprover,
            DeletionApprovedBy: deletionApprovedBy,

            ConfirmationRequired: tenant.Name,
            History: history,
            Exports: exports,
            Disclosures:
            [
                // First, when it applies: it changes what the rest of the list is describing.
                .. !commercialPosition.CommercialEvidenceApplies
                    ? new[] { TenantOffboardingDisclosure.NoCommercialEvidenceRequired }
                    : [],
                TenantOffboardingDisclosure.Irreversible,
                TenantOffboardingDisclosure.Survives,
                TenantOffboardingDisclosure.StatutoryRecordsMoveWithTheCustomer,
                TenantOffboardingDisclosure.ErasureIsNotDeletion,
                TenantOffboardingDisclosure.DeletionIsNotErasure
            ],
            CommercialEvidenceRequired: commercialPosition.CommercialEvidenceApplies,
            CanAttestNonCustomer: stage == TenantOffboardingStage.NotScheduled
                                  && tenant.Status == TenantLifecycleGraph.DeletionRequiresStatus
                                  && tenant.DeploymentProfile == TenantDeploymentProfile.Production
                                  && !commercialPosition.HasCommercialFootprint
                                  && !commercialPosition.HasNonCustomerAttestation,
            NonCustomerAttestedOn: commercialPosition.NonCustomerAttestedOn,
            NonCustomerAttestedBy: commercialPosition.NonCustomerAttestedBy,
            BillingStatementCount: commercialPosition.BillingStatementCount,
            SubscriptionInvoiceCount: commercialPosition.SubscriptionInvoiceCount,
            ReadinessFailures: scheduleReadiness.Failures);
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
