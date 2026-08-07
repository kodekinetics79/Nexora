using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// The one call a tenant's page in the operator console makes to answer "how is this customer being
/// operated": open tickets, recent privileged activity, live impersonation sessions, and lifecycle
/// state.
///
/// <para><b>Why one call and not six.</b> The console previously had to fetch tenants, audit,
/// impersonation sessions and (now) tickets separately and stitch them together. Four fetches are
/// four chances for the page to be showing a mixture of two tenants — a stale tab, a race on
/// navigation, an id the caller forgot to thread through one of them — and the whole purpose of the
/// page is that it is about exactly one customer. Here the tenant id is applied once, in one place,
/// to every source, which is also what makes "the summary returns only the requested tenant's data"
/// a property that can be tested rather than a habit that must be maintained.</para>
///
/// <para><b>Authorization.</b> <see cref="PlatformPolicies.PlatformScope"/> only, and every
/// endpoint is a GET. ReadOnlyOps is defined as cross-tenant read-only observability, so it reads
/// this; nothing here mutates, so nobody's authority is widened. Nothing under
/// <see cref="PlatformPolicies.Billing"/> is served: no statement, no rate card, no price. The
/// plan CODE is included because it is already on <c>TenantSummaryDto</c>, which every
/// PlatformScope holder can already read from <c>GET /api/platform/tenants/{id}</c> — including it
/// grants nothing that was not already granted, and omitting it would leave the desk unable to see
/// which entitlements a customer is complaining about.</para>
/// </summary>
[ApiController]
[Route("api/platform/tenants/{tenantId:long}")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class TenantOperationsController(
    ErpRfqAutomationContext context, IAuthorizationService authorization) : ControllerBase
{
    /// <summary>How much recent detail rides along with the counts. Enough to render, not to page.</summary>
    public const int RecentTicketCount = 10;

    public const int RecentAuditCount = 20;

    public const int RecentSessionCount = 10;

    /// <summary>
    /// The activity window the counts describe. Thirty days because it spans a billing cycle and a
    /// typical support escalation, and because a count with no window is a number that grows
    /// forever and stops meaning anything.
    /// </summary>
    public static readonly TimeSpan ActivityWindow = TimeSpan.FromDays(30);

    // GET /api/platform/tenants/{tenantId}/operations-summary
    [HttpGet("operations-summary")]
    public async Task<ActionResult<TenantOperationsSummaryDto>> OperationsSummary(
        long tenantId, CancellationToken ct)
    {
        // IgnoreQueryFilters for the reason every platform read does: this request carries no
        // tenant scope, and Tenant has no filter of its own, but the call is explicit so the intent
        // survives a future change to the platform model. Projected rather than materialised
        // because platform.Tenants carries column-level grants for the narrower execution roles.
        var tenant = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new TenantLifecycleSnapshotDto
            {
                TenantId = t.Id,
                Name = t.Name,
                Slug = t.Slug,
                Status = t.Status.ToString(),
                StatusReason = t.StatusReason,
                PlanId = t.PlanId,
                PlanCode = t.Plan != null ? t.Plan.Code : null,
                PrimaryBusinessUnitId = t.PrimaryBusinessUnitId,
                CreatedOn = t.CreatedOn,
                ModifiedOn = t.ModifiedOn,
                ModifiedBy = t.ModifiedBy
            })
            .FirstOrDefaultAsync(ct);
        if (tenant is null)
            return NotFound();

        var now = DateTime.UtcNow;
        var windowStart = now - ActivityWindow;

        return Ok(new TenantOperationsSummaryDto
        {
            GeneratedAtUtc = now,
            Lifecycle = tenant,
            Support = await SupportSnapshotAsync(tenantId, tenant, ct),
            Audit = await AuditSnapshotAsync(tenantId, windowStart, ct),
            Impersonation = await ImpersonationSnapshotAsync(tenantId, now, windowStart, ct)
        });
    }

    private async Task<TenantSupportSnapshotDto> SupportSnapshotAsync(
        long tenantId, TenantLifecycleSnapshotDto tenant, CancellationToken ct)
    {
        var live = context.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.TenantId == tenantId && SupportTicketLifecycle.Live.Contains(t.Status));

        // Grouped in the database rather than counted with one round trip per status: the whole
        // point of this endpoint is that opening a tenant page is cheap.
        var byStatus = await live
            .GroupBy(t => t.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var bySeverity = await live
            .GroupBy(t => t.Severity)
            .Select(g => new { Severity = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var unassigned = await live.CountAsync(t => t.AssignedToPlatformUserId == null, ct);
        var oldest = await live.OrderBy(t => t.CreatedAtUtc)
            .Select(t => (DateTime?)t.CreatedAtUtc).FirstOrDefaultAsync(ct);

        var recent = await context.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.TenantId == tenantId)
            .OrderByDescending(t => t.UpdatedAtUtc).ThenByDescending(t => t.Id)
            .Take(RecentTicketCount)
            .Select(t => new SupportTicketSummaryDto
            {
                Id = t.Id,
                TenantId = t.TenantId,
                Subject = t.Subject,
                Severity = t.Severity.ToString(),
                Status = t.Status.ToString(),
                Origin = t.Origin.ToString(),
                AssignedToPlatformUserId = t.AssignedToPlatformUserId,
                OpenedByPlatformUserId = t.OpenedByPlatformUserId,
                RequesterEmail = t.RequesterEmail,
                CreatedAtUtc = t.CreatedAtUtc,
                UpdatedAtUtc = t.UpdatedAtUtc,
                FirstRespondedAtUtc = t.FirstRespondedAtUtc,
                ResolvedAtUtc = t.ResolvedAtUtc,
                ClosedAtUtc = t.ClosedAtUtc,
                NoteCount = t.Notes.Count,
                LinkCount = t.Links.Count,
                IsRedacted = t.RedactedAtUtc != null,
                Version = t.Version
            })
            .ToListAsync(ct);

        var assigneeIds = recent.Where(t => t.AssignedToPlatformUserId != null)
            .Select(t => t.AssignedToPlatformUserId!.Value)
            .Concat(recent.Where(t => t.OpenedByPlatformUserId != null)
                .Select(t => t.OpenedByPlatformUserId!.Value))
            .Distinct().ToArray();
        var operators = await LoadOperatorEmailsAsync(assigneeIds, ct);
        foreach (var ticket in recent)
        {
            // The tenant identity is stamped on here rather than joined per row: the summary
            // already resolved it once, and re-joining platform.Tenants inside the ticket query
            // would be a join that fetches the same three constants for every row.
            ticket.TenantName = tenant.Name;
            ticket.TenantSlug = tenant.Slug;
            ticket.TenantStatus = tenant.Status;
            if (ticket.AssignedToPlatformUserId is long assignee && operators.TryGetValue(assignee, out var email))
                ticket.AssignedToEmail = email;
            if (ticket.OpenedByPlatformUserId is long opener && operators.TryGetValue(opener, out var openerEmail))
                ticket.OpenedByEmail = openerEmail;
        }

        return new TenantSupportSnapshotDto
        {
            OpenTicketCount = byStatus.Sum(s => s.Count),
            UnassignedOpenTicketCount = unassigned,
            OpenByStatus = byStatus.ToDictionary(s => s.Status.ToString(), s => s.Count),
            OpenBySeverity = bySeverity.ToDictionary(s => s.Severity.ToString(), s => s.Count),
            OldestOpenTicketCreatedAtUtc = oldest,
            RecentTickets = recent
        };
    }

    private async Task<TenantAuditSnapshotDto> AuditSnapshotAsync(
        long tenantId, DateTime windowStart, CancellationToken ct)
    {
        // ActAsTenantId == tenantId, and NOTHING else. Rows with a null tenant are platform-wide
        // actions (plan.create, platform.login); putting them on a customer's page under the word
        // "activity for this tenant" would be a claim the data does not support.
        var scoped = context.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.ActAsTenantId == tenantId);

        var windowed = scoped.Where(a => a.CreatedOn >= windowStart);
        var total = await windowed.CountAsync(ct);
        var failures = await windowed.CountAsync(a => a.Result == PlatformAuditResults.Failure, ct);
        var last = await scoped.OrderByDescending(a => a.CreatedOn)
            .Select(a => (DateTime?)a.CreatedOn).FirstOrDefaultAsync(ct);

        var rows = await scoped
            .OrderByDescending(a => a.CreatedOn).ThenByDescending(a => a.Id)
            .Take(RecentAuditCount)
            .Select(a => new
            {
                a.Id, a.CreatedOn, a.ActorPlatformUserId, a.Action,
                a.TargetType, a.TargetId, a.Ip, a.Result, a.Metadata
            })
            .ToListAsync(ct);

        var actors = await LoadActorsAsync(rows.Select(r => r.ActorPlatformUserId).Distinct().ToArray(), ct);

        // Same rule as the explorer (finding R6): this endpoint republishes the same audit payloads
        // onto a tenant's page, so it cannot be a wider door than the endpoints that wrote them. The
        // counts and the entry identities are unaffected — a tier that may not read a billing payload
        // can still see that a billing action happened here, and when.
        var gate = await PlatformAuditDisclosure.ResolveAsync(
            authorization, User, rows.Select(r => r.Action), ct);

        return new TenantAuditSnapshotDto
        {
            EntryCountLast30Days = total,
            FailureCountLast30Days = failures,
            LastActionAtUtc = last,
            RecentEntries = rows.Select(r => new PlatformAuditEntryDto
            {
                Id = r.Id,
                OccurredAtUtc = r.CreatedOn,
                ActorPlatformUserId = r.ActorPlatformUserId,
                Actor = PlatformAuditExplorerController.ActorLabel(r.ActorPlatformUserId, actors),
                ActorEmail = actors.TryGetValue(r.ActorPlatformUserId, out var actor) ? actor.Email : null,
                Action = r.Action,
                TargetType = r.TargetType,
                TargetId = r.TargetId,
                TenantId = tenantId,
                Ip = r.Ip,
                Result = r.Result,
                Metadata = gate.Metadata(r.Action, r.Metadata),
                MetadataDisclosed = gate.MayDisclose(r.Action),
                MetadataPolicy = AuditDisclosureGate.RequiredPolicyFor(r.Action)
            }).ToList()
        };
    }

    private async Task<TenantImpersonationSnapshotDto> ImpersonationSnapshotAsync(
        long tenantId, DateTime now, DateTime windowStart, CancellationToken ct)
    {
        var scoped = context.Set<ImpersonationSession>().AsNoTracking()
            .Where(s => s.TenantId == tenantId);

        var active = await scoped.CountAsync(s => s.RevokedAtUtc == null && s.ExpiresAtUtc > now, ct);
        var windowed = await scoped.CountAsync(s => s.IssuedAtUtc >= windowStart, ct);

        // Live sessions first regardless of age, because a session still open right now is the one
        // an operator needs to see and revoke; recent history follows.
        var rows = await scoped
            .OrderByDescending(s => s.RevokedAtUtc == null && s.ExpiresAtUtc > now)
            .ThenByDescending(s => s.IssuedAtUtc)
            .Take(RecentSessionCount)
            .Select(s => new
            {
                s.Jti, s.ActorPlatformUserId, s.Reason, s.IssuedAtUtc,
                s.ExpiresAtUtc, s.RevokedAtUtc, s.RevokedBy
            })
            .ToListAsync(ct);

        var actors = await LoadActorsAsync(rows.Select(r => r.ActorPlatformUserId).Distinct().ToArray(), ct);

        var jtis = rows.Select(r => r.Jti).ToArray();
        var ticketLinks = jtis.Length == 0
            ? []
            : await context.Set<SupportTicketLink>().AsNoTracking()
                .Where(l => l.Kind == SupportTicketLinkKind.ImpersonationSession && jtis.Contains(l.TargetKey))
                .Select(l => new { l.TargetKey, l.SupportTicketId })
                .ToListAsync(ct);

        return new TenantImpersonationSnapshotDto
        {
            ActiveSessionCount = active,
            SessionCountLast30Days = windowed,
            Sessions = rows.Select(r => new TenantImpersonationSessionDto
            {
                Jti = r.Jti,
                ActorPlatformUserId = r.ActorPlatformUserId,
                ActorEmail = actors.TryGetValue(r.ActorPlatformUserId, out var actor) ? actor.Email : null,
                Reason = r.Reason,
                IssuedAtUtc = r.IssuedAtUtc,
                ExpiresAtUtc = r.ExpiresAtUtc,
                RevokedAtUtc = r.RevokedAtUtc,
                RevokedBy = r.RevokedBy,
                Status = r.RevokedAtUtc != null ? "revoked" : r.ExpiresAtUtc <= now ? "expired" : "active",
                LinkedTicketIds = ticketLinks.Where(l => l.TargetKey == r.Jti)
                    .Select(l => l.SupportTicketId).Distinct().ToList()
            }).ToList()
        };
    }

    private async Task<Dictionary<long, PlatformAuditExplorerController.ActorLabelRow>> LoadActorsAsync(
        long[] ids, CancellationToken ct)
        => ids.Length == 0
            ? []
            : await context.Set<PlatformUser>().AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new PlatformAuditExplorerController.ActorLabelRow(u.Id, u.Email, u.DisplayName))
                .ToDictionaryAsync(u => u.Id, ct);

    private async Task<Dictionary<long, string>> LoadOperatorEmailsAsync(long[] ids, CancellationToken ct)
        => ids.Length == 0
            ? []
            : await context.Set<PlatformUser>().AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new { u.Id, u.Email })
                .ToDictionaryAsync(u => u.Id, u => u.Email, ct);
}
