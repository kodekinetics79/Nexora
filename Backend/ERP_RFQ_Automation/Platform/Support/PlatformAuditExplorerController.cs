using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// The query surface over <c>platform.PlatformAuditLogs</c>.
///
/// <para><b>What was missing.</b> <c>/api/platform/audit</c> answers one question — "show me
/// something" — with four optional filters, no paging, and a hard <c>Take(500)</c>. An operator
/// asked "what did we do to Acme in March" gets the newest 500 rows across the whole platform and
/// has to scroll; asked "what changed when someone edited that plan" gets a string of JSON. The
/// four questions this controller exists to answer are: everything that happened to THIS tenant,
/// everything THIS operator did, every occurrence of an action within a window, and what actually
/// changed inside one privileged action.</para>
///
/// <para><b>Read-only by construction.</b> There is no non-GET action here and there never can be:
/// the audit log is append-only, enforced by REVOKE UPDATE, DELETE, TRUNCATE on the table from
/// <c>nexora_pipeline_app</c> — the role every <c>/api/platform</c> request executes under
/// (20260728134117_ConfigureDatabaseExecutionRoles, re-asserted by
/// 20260805034514_PlatformAdmin360ControlPlane after its blanket schema grant). Nothing in this
/// file loads an audit row as a tracked entity or calls SaveChanges, so there is no path by which
/// an explorer feature could become an editor. <c>PlatformSupportPostgreSqlTests</c> asserts the
/// database half against a real server, and a reflection test asserts the controller half.</para>
///
/// <para><b>Authorization: two gates, not one.</b> Reaching the controller at all requires
/// <see cref="PlatformPolicies.PlatformScope"/>, which every operator including ReadOnlyOps
/// satisfies — cross-tenant read-only observability is that tier's entire definition, and an
/// observability role that cannot see the audit log is not one. Reading an entry's PAYLOAD requires,
/// additionally, the policy that gates the endpoint which wrote it
/// (<see cref="PlatformAuditDisclosure"/>).</para>
///
/// <para>The second gate exists because the first one's original justification was incomplete. This
/// file used to argue that admitting the read-only tier "widens nobody's authority" since nothing
/// here mutates — which is an argument about INTEGRITY that says nothing about CONFIDENTIALITY. A
/// red-team audit then drove the real Owner|BillingAdmin commercial-terms endpoint and read
/// <c>Tenant.BillingModeReason</c> — a column two migrations deliberately withhold from the tenant
/// plane — straight back out of here as a ReadOnlyOps actor (finding R6). An audit explorer must
/// never be a more open door onto a payload than the endpoint that wrote it.</para>
///
/// <para>An impersonation token never reaches this controller at all — it is a TENANT token and the
/// policy pins the Platform scheme and the <c>scope=platform</c> claim.</para>
/// </summary>
[ApiController]
[Route("api/platform/audit")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class PlatformAuditExplorerController(
    ErpRfqAutomationContext context, IAuthorizationService authorization) : ControllerBase
{
    /// <summary>
    /// Paging bound. Generous enough that an investigation is not death by pagination, small enough
    /// that a console bug cannot ask one request to materialise the entire audit history.
    /// </summary>
    public const int MaxPageSize = 200;

    public const int DefaultPageSize = 50;

    /// <summary>Bound on one tenant-timeline response across all three sources.</summary>
    public const int MaxTimelineEntries = 500;

    // GET /api/platform/audit/query
    /// <summary>
    /// Filtered, paged audit search. Every filter composes with AND — including
    /// <paramref name="search"/>, whose internal OR is confined to a single predicate. That
    /// confinement is the whole guard: a search term that widened the tenant filter would turn this
    /// endpoint into "give me every tenant's rows that mention Acme", which is precisely the
    /// boundary an audit reader must not be able to cross.
    /// </summary>
    /// <param name="action">Exact action verb; repeatable for an OR over verbs.</param>
    /// <param name="actionPrefix">Verb family, e.g. <c>tenant.</c> or <c>support.ticket.</c>.</param>
    [HttpGet("query")]
    public async Task<ActionResult<PagedResultDto<PlatformAuditEntryDto>>> Query(
        [FromQuery] long? tenantId,
        [FromQuery] long? actorPlatformUserId,
        [FromQuery] string[]? action,
        [FromQuery] string? actionPrefix,
        [FromQuery] string? targetType,
        [FromQuery] string? targetId,
        [FromQuery] string? result,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (fromUtc is DateTime from && toUtc is DateTime to && from > to)
            return BadRequest(new { error = "fromUtc must not be later than toUtc." });

        var query = await BuildQueryAsync(
            tenantId, actorPlatformUserId, action, actionPrefix, targetType, targetId,
            result, fromUtc, toUtc, search, ct);

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = pageSize < 1 ? DefaultPageSize
            : pageSize > MaxPageSize ? MaxPageSize : pageSize;

        var total = await query.CountAsync(ct);

        // Id is the tie-breaker, not decoration. CreatedOn is written by the application at
        // millisecond resolution and rows produced inside one transaction routinely share a
        // timestamp; ordering on it alone gives PostgreSQL licence to return those rows in a
        // different order per page, which silently duplicates some and drops others as an
        // operator pages through an investigation.
        var rows = await query
            .OrderByDescending(a => a.CreatedOn).ThenByDescending(a => a.Id)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(a => new AuditRow(
                a.Id, a.CreatedOn, a.ActorPlatformUserId, a.ActAsTenantId, a.Action,
                a.TargetType, a.TargetId, a.Ip, a.Result, a.Metadata))
            .ToListAsync(ct);

        return Ok(new PagedResultDto<PlatformAuditEntryDto>
        {
            Items = await ProjectAsync(rows, ct),
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = total
        });
    }

    // GET /api/platform/audit/entries/{id}
    /// <summary>
    /// One entry, with its before/after decoded into field-level changes. This is the endpoint that
    /// answers "what did that privileged action actually change" without an operator reading raw
    /// JSON — see <see cref="PlatformAuditMetadata"/> for why the decoding reads three shapes and
    /// invents nothing when it recognises none of them.
    /// </summary>
    [HttpGet("entries/{id:long}")]
    public async Task<ActionResult<PlatformAuditEntryDetailDto>> Entry(long id, CancellationToken ct)
    {
        var row = await context.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new AuditRow(
                a.Id, a.CreatedOn, a.ActorPlatformUserId, a.ActAsTenantId, a.Action,
                a.TargetType, a.TargetId, a.Ip, a.Result, a.Metadata))
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return NotFound();

        var projected = (await ProjectAsync([row], ct))[0];

        // The gate is re-consulted rather than inferred from the projection: this is the endpoint the
        // red team used, and the before/after decode below is the richest disclosure in the module.
        var gate = await PlatformAuditDisclosure.ResolveAsync(authorization, User, [row.Action], ct);
        var payload = gate.Shape(row.Action, row.Metadata);

        return Ok(new PlatformAuditEntryDetailDto
        {
            Id = projected.Id,
            OccurredAtUtc = projected.OccurredAtUtc,
            ActorPlatformUserId = projected.ActorPlatformUserId,
            Actor = projected.Actor,
            ActorEmail = projected.ActorEmail,
            Action = projected.Action,
            TargetType = projected.TargetType,
            TargetId = projected.TargetId,
            TenantId = projected.TenantId,
            TenantName = projected.TenantName,
            Ip = projected.Ip,
            Result = projected.Result,
            Metadata = payload.Metadata,
            MetadataDisclosed = payload.Disclosed,
            MetadataPolicy = payload.RequiredPolicy,
            Before = payload.Before,
            After = payload.After,
            Changes = payload.Changes
        });
    }

    // GET /api/platform/audit/actions
    /// <summary>
    /// The action vocabulary actually present in the log, with counts. The console builds its verb
    /// filter from this rather than from a hard-coded list, so a verb introduced by a new endpoint
    /// becomes filterable the moment it is first written instead of the next time somebody
    /// remembers to update the front end.
    /// </summary>
    [HttpGet("actions")]
    public async Task<ActionResult<IEnumerable<PlatformAuditActionDto>>> Actions(
        [FromQuery] long? tenantId, [FromQuery] DateTime? fromUtc, CancellationToken ct)
    {
        var query = context.Set<PlatformAuditLog>().AsNoTracking().AsQueryable();
        if (tenantId is long id) query = query.Where(a => a.ActAsTenantId == id);
        if (fromUtc is DateTime from) query = query.Where(a => a.CreatedOn >= from);

        var actions = await query
            .GroupBy(a => a.Action)
            .Select(g => new PlatformAuditActionDto
            {
                Action = g.Key,
                Count = g.Count(),
                LastSeenAtUtc = g.Max(a => a.CreatedOn)
            })
            .ToListAsync(ct);

        return Ok(actions.OrderBy(a => a.Action, StringComparer.Ordinal));
    }

    // GET /api/platform/audit/tenants/{tenantId}/timeline
    /// <summary>
    /// One tenant's operational history across all three registers the operator plane keeps:
    /// privileged actions, impersonation sessions, and support tickets.
    ///
    /// <para><b>Why the three are merged here and not in the console.</b> Each register has its own
    /// notion of "when" — <c>CreatedOn</c>, <c>IssuedAtUtc</c>, <c>UpdatedAtUtc</c> — and a client
    /// that fetches three lists and interleaves them is a client that has to re-derive the ordering
    /// every time, and gets it wrong at the boundaries where a page ends. Merging server-side also
    /// means the tenant predicate is applied once, in one place, to all three.</para>
    ///
    /// <para><b>Rows with no tenant are not this tenant's.</b> Platform-wide actions
    /// (<c>plan.create</c>, <c>platform.login</c>) carry a null <c>ActAsTenantId</c>. They are
    /// excluded, because including them would put another customer's operator activity on this
    /// customer's page under the word "timeline".</para>
    /// </summary>
    [HttpGet("tenants/{tenantId:long}/timeline")]
    public async Task<ActionResult<IEnumerable<TenantTimelineEntryDto>>> TenantTimeline(
        long tenantId,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int limit = 100,
        CancellationToken ct = default)
    {
        var tenantExists = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(t => t.Id == tenantId, ct);
        if (!tenantExists)
            return NotFound();

        var bounded = limit < 1 ? 100 : limit > MaxTimelineEntries ? MaxTimelineEntries : limit;

        var auditQuery = context.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.ActAsTenantId == tenantId);
        var impersonationQuery = context.Set<ImpersonationSession>().AsNoTracking()
            .Where(s => s.TenantId == tenantId);
        var ticketQuery = context.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.TenantId == tenantId);

        if (fromUtc is DateTime from)
        {
            auditQuery = auditQuery.Where(a => a.CreatedOn >= from);
            impersonationQuery = impersonationQuery.Where(s => s.IssuedAtUtc >= from);
            ticketQuery = ticketQuery.Where(t => t.CreatedAtUtc >= from);
        }
        if (toUtc is DateTime to)
        {
            auditQuery = auditQuery.Where(a => a.CreatedOn <= to);
            impersonationQuery = impersonationQuery.Where(s => s.IssuedAtUtc <= to);
            ticketQuery = ticketQuery.Where(t => t.CreatedAtUtc <= to);
        }

        // Each source is capped at the requested limit before the merge, so the merged result is
        // still correct for the most recent `bounded` entries no matter how the three are
        // distributed in time.
        var auditRows = await auditQuery
            .OrderByDescending(a => a.CreatedOn).ThenByDescending(a => a.Id)
            .Take(bounded)
            .Select(a => new AuditRow(
                a.Id, a.CreatedOn, a.ActorPlatformUserId, a.ActAsTenantId, a.Action,
                a.TargetType, a.TargetId, a.Ip, a.Result, a.Metadata))
            .ToListAsync(ct);
        var sessions = await impersonationQuery
            .OrderByDescending(s => s.IssuedAtUtc)
            .Take(bounded)
            .Select(s => new { s.Jti, s.ActorPlatformUserId, s.Reason, s.IssuedAtUtc, s.ExpiresAtUtc, s.RevokedAtUtc })
            .ToListAsync(ct);
        var mayReadSupportContent = (await authorization.AuthorizeAsync(
            User, null, PlatformPolicies.TenantAdmin)).Succeeded;
        var tickets = mayReadSupportContent
            ? await ticketQuery
                .OrderByDescending(t => t.CreatedAtUtc)
                .Take(bounded)
                .Select(t => new TicketTimelineRow(
                    t.Id, t.Subject, t.Severity, t.Status, t.CreatedAtUtc, t.OpenedByPlatformUserId))
                .ToListAsync(ct)
            : [];

        var actorIds = auditRows.Select(a => a.ActorPlatformUserId)
            .Concat(sessions.Select(s => s.ActorPlatformUserId))
            .Concat(tickets.Where(t => t.OpenedByPlatformUserId != null).Select(t => t.OpenedByPlatformUserId!.Value))
            .Distinct().ToArray();
        var actors = await LoadActorsAsync(actorIds, ct);
        var gate = await PlatformAuditDisclosure.ResolveAsync(
            authorization, User, auditRows.Select(a => a.Action), ct);

        var now = DateTime.UtcNow;
        var entries = auditRows.Select(a => new TenantTimelineEntryDto
        {
            Kind = "audit",
            Id = a.Id.ToString(),
            OccurredAtUtc = a.CreatedOn,
            Action = a.Action,
            Actor = ActorLabel(a.ActorPlatformUserId, actors),
            Summary = string.IsNullOrEmpty(a.TargetType) ? null : $"{a.TargetType} {a.TargetId}".Trim(),
            Result = a.Result,
            Metadata = gate.Metadata(a.Action, a.Metadata),
            MetadataDisclosed = gate.MayDisclose(a.Action),
            MetadataPolicy = AuditDisclosureGate.RequiredPolicyFor(a.Action)
        }).Concat(sessions.Select(s => new TenantTimelineEntryDto
        {
            Kind = "impersonation",
            Id = s.Jti,
            OccurredAtUtc = s.IssuedAtUtc,
            Action = "impersonate.session",
            Actor = ActorLabel(s.ActorPlatformUserId, actors),
            // The session's reason is NOT withheld, and that is not an oversight: the register it
            // comes from is GET /api/platform/impersonation/sessions, which is itself gated on
            // PlatformScope alone. The rule is "no more open than the endpoint that wrote it", so
            // reproducing that endpoint's own openness is correct. It is the AUDIT payload of
            // impersonate.issue — same reason text, but written under Platform.Impersonate — that
            // the gate above restricts.
            Summary = s.Reason,
            Result = s.RevokedAtUtc != null ? "revoked" : s.ExpiresAtUtc <= now ? "expired" : "active"
        })).Concat(tickets.Select(t => new TenantTimelineEntryDto
        {
            Kind = "ticket",
            Id = t.Id.ToString(),
            OccurredAtUtc = t.CreatedAtUtc,
            Action = "support.ticket",
            Actor = t.OpenedByPlatformUserId is long opener ? ActorLabel(opener, actors) : null,
            Summary = t.Subject,
            Result = $"{t.Status}/{t.Severity}"
        }))
        .OrderByDescending(e => e.OccurredAtUtc)
        .Take(bounded)
        .ToList();

        return Ok(entries);
    }

    // ---- shared query construction ------------------------------------------------------------

    /// <summary>
    /// Builds the filtered audit query. Async because the free-text search resolves operator and
    /// tenant names to id sets first: <c>Metadata</c> is a <c>jsonb</c> column with no portable SQL
    /// text-search translation, and matching on names in memory after a Take() would silently miss
    /// anything older than the first page — the defect the existing endpoint already had to fix.
    /// </summary>
    private async Task<IQueryable<PlatformAuditLog>> BuildQueryAsync(
        long? tenantId, long? actorPlatformUserId, string[]? actions, string? actionPrefix,
        string? targetType, string? targetId, string? result,
        DateTime? fromUtc, DateTime? toUtc, string? search, CancellationToken ct)
    {
        var query = context.Set<PlatformAuditLog>().AsNoTracking().AsQueryable();

        if (tenantId is long tenant) query = query.Where(a => a.ActAsTenantId == tenant);
        if (actorPlatformUserId is long actor) query = query.Where(a => a.ActorPlatformUserId == actor);

        var verbs = (actions ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (verbs.Length > 0) query = query.Where(a => verbs.Contains(a.Action));
        if (!string.IsNullOrWhiteSpace(actionPrefix))
        {
            var prefix = actionPrefix.Trim();
            query = query.Where(a => a.Action.StartsWith(prefix));
        }

        if (!string.IsNullOrWhiteSpace(targetType))
        {
            var type = targetType.Trim();
            query = query.Where(a => a.TargetType == type);
        }
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            var target = targetId.Trim();
            query = query.Where(a => a.TargetId == target);
        }

        // Canonical values only. A non-canonical value means "no filter", matching the lenience of
        // the existing endpoint rather than 400-ing an operator who typed "all".
        if (!string.IsNullOrWhiteSpace(result))
        {
            var normalized = result.Trim().ToLowerInvariant();
            if (normalized is PlatformAuditResults.Success or PlatformAuditResults.Failure)
                query = query.Where(a => a.Result == normalized);
        }

        if (fromUtc is DateTime from) query = query.Where(a => a.CreatedOn >= from);
        if (toUtc is DateTime to) query = query.Where(a => a.CreatedOn <= to);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            var matchingActorIds = await context.Set<PlatformUser>().AsNoTracking()
                .Where(u => u.Email.ToLower().Contains(term) ||
                            (u.DisplayName != null && u.DisplayName.ToLower().Contains(term)))
                .Select(u => u.Id).ToListAsync(ct);
            var matchingTenantIds = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.Name.ToLower().Contains(term) || t.Slug.ToLower().Contains(term))
                .Select(t => t.Id).ToListAsync(ct);

            // ONE Where. The OR lives entirely inside it, so it can only ever narrow the result of
            // the filters above — it can never re-admit a row the tenant or actor filter excluded.
            query = query.Where(a =>
                a.Action.ToLower().Contains(term) ||
                (a.TargetType != null && a.TargetType.ToLower().Contains(term)) ||
                (a.TargetId != null && a.TargetId.ToLower().Contains(term)) ||
                matchingActorIds.Contains(a.ActorPlatformUserId) ||
                (a.ActAsTenantId != null && matchingTenantIds.Contains(a.ActAsTenantId.Value)));
        }

        return query;
    }

    /// <summary>
    /// Attaches operator and tenant names to a page of rows with two extra queries rather than a
    /// join per row. Both lookups PROJECT: platform.Tenants and platform.PlatformUsers carry
    /// column-level grants for the tenant/identity roles
    /// (20260805105320_HardenPlatformGrantsAndBillingImmutability), and a query that materialises a
    /// whole entity to read two of its columns is a query that works here and fails with 42501 the
    /// moment the same code is reached under a narrower role.
    /// </summary>
    private async Task<IReadOnlyList<PlatformAuditEntryDto>> ProjectAsync(
        IReadOnlyList<AuditRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var actorIds = rows.Select(r => r.ActorPlatformUserId).Distinct().ToArray();
        var tenantIds = rows.Where(r => r.ActAsTenantId != null)
            .Select(r => r.ActAsTenantId!.Value).Distinct().ToArray();

        var actors = await LoadActorsAsync(actorIds, ct);
        var tenants = tenantIds.Length == 0
            ? new Dictionary<long, string>()
            : await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
                .Where(t => tenantIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name })
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

        // Resolved once for the whole page: a 200-row response asks at most a handful of distinct
        // authorization questions, and evaluating them per row would run 200.
        var gate = await PlatformAuditDisclosure.ResolveAsync(
            authorization, User, rows.Select(r => r.Action), ct);

        return rows.Select(row => new PlatformAuditEntryDto
        {
            Id = row.Id,
            OccurredAtUtc = row.CreatedOn,
            ActorPlatformUserId = row.ActorPlatformUserId,
            Actor = ActorLabel(row.ActorPlatformUserId, actors),
            ActorEmail = actors.TryGetValue(row.ActorPlatformUserId, out var actor) ? actor.Email : null,
            Action = row.Action,
            TargetType = row.TargetType,
            TargetId = row.TargetId,
            TenantId = row.ActAsTenantId,
            TenantName = row.ActAsTenantId is long id && tenants.TryGetValue(id, out var name) ? name : null,
            Ip = row.Ip,
            Result = row.Result,
            Metadata = gate.Metadata(row.Action, row.Metadata),
            MetadataDisclosed = gate.MayDisclose(row.Action),
            MetadataPolicy = AuditDisclosureGate.RequiredPolicyFor(row.Action)
        }).ToList();
    }

    private async Task<Dictionary<long, ActorLabelRow>> LoadActorsAsync(
        long[] actorIds, CancellationToken ct)
        => actorIds.Length == 0
            ? []
            : await context.Set<PlatformUser>().AsNoTracking()
                .Where(u => actorIds.Contains(u.Id))
                .Select(u => new ActorLabelRow(u.Id, u.Email, u.DisplayName))
                .ToDictionaryAsync(u => u.Id, ct);

    /// <summary>
    /// Reserved actor 0 renders as "system", matching the existing endpoint. An id with no
    /// PlatformUsers row renders as the id rather than as blank: an operator row that was removed
    /// must not make its actions look unattributed.
    /// </summary>
    internal static string ActorLabel(long actorId, IReadOnlyDictionary<long, ActorLabelRow> actors)
        => actors.TryGetValue(actorId, out var actor)
            ? actor.DisplayName ?? actor.Email
            : actorId == PlatformAuditService.SystemActorId
                ? "system"
                : $"Platform user {actorId}";

    internal sealed record ActorLabelRow(long Id, string Email, string? DisplayName);

    /// <summary>
    /// The exact column set the explorer reads. Naming it keeps every audit projection in this
    /// module identical, so adding a column to the DTO cannot quietly turn one of them back into a
    /// whole-entity materialisation.
    /// </summary>
    internal sealed record AuditRow(
        long Id, DateTime CreatedOn, long ActorPlatformUserId, long? ActAsTenantId, string Action,
        string? TargetType, string? TargetId, string? Ip, string Result, string? Metadata);

    internal sealed record TicketTimelineRow(
        long Id, string Subject, SupportTicketSeverity Severity, SupportTicketStatus Status,
        DateTime CreatedAtUtc, long? OpenedByPlatformUserId);
}
