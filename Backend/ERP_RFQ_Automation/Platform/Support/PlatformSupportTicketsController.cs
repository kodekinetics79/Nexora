using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// The operator support desk: tickets raised and worked by platform staff against a tenant.
///
/// <para><b>Authorization, and why it is these two policies.</b> The class gate is
/// <see cref="PlatformPolicies.PlatformScope"/>, so every operator — including ReadOnlyOps, whose
/// entire definition is cross-tenant read-only observability — can READ the desk. Every mutation
/// carries <see cref="PlatformPolicies.TenantAdmin"/> (Owner | SupportAdmin), which is exactly the
/// policy tenant suspension and read-only impersonation already use. Support ticketing is support
/// work, so SupportAdmin must be able to do all of it without borrowing an Owner; and nothing here
/// touches a plan, a rate card or a price, so the separation of duties recorded at
/// <c>TenantsController.ChangePlan</c> ("Sec9: plan assignment is a BILLING operation ... SupportAdmin
/// must not be able to change what a customer is charged") is untouched — no endpoint on this
/// controller reads or writes anything under <see cref="PlatformPolicies.Billing"/>.</para>
///
/// <para><b>Suspension does not close the desk.</b> No write path here consults
/// <c>Tenant.Status</c>. A suspended tenant is the customer most likely to be calling, and a desk
/// that refuses to record their problem is a desk that fails precisely when it is needed. The one
/// precondition is that the tenant row exists.</para>
///
/// <para><b>Every state change is auditable, and atomically so.</b> Each mutation runs inside an
/// execution-strategy transaction that commits the ticket write and its
/// <c>platform.PlatformAuditLogs</c> row together — the Sec3 discipline the rest of the control
/// plane uses. A ticket can never have moved without a trail, and a trail can never describe a move
/// that rolled back. No separate ticket-event table exists on purpose: the audit log already IS the
/// append-only record of privileged action, and a parallel ledger would be a second version of the
/// truth that drifts from the first.</para>
/// </summary>
[ApiController]
[Route("api/platform/support/tickets")]
[Authorize(Policy = PlatformPolicies.PlatformScope)]
public class PlatformSupportTicketsController(
    ErpRfqAutomationContext context,
    IPlatformAuditService audit,
    IAuthorizationService authorization,
    ILogger<PlatformSupportTicketsController> logger) : ControllerBase
{
    public const int MaxPageSize = 200;
    public const int DefaultPageSize = 50;

    /// <summary>Audit verbs this controller writes. Named so tests and the console agree on them.</summary>
    public static class Actions
    {
        public const string Create = "support.ticket.create";
        public const string Note = "support.ticket.note";
        public const string Transition = "support.ticket.transition";
        public const string Assign = "support.ticket.assign";
        public const string Severity = "support.ticket.severity";
        public const string Link = "support.ticket.link";
        public const string Unlink = "support.ticket.unlink";
    }

    /// <summary>The <c>TargetType</c> every ticket audit row carries, so the trail is queryable by target.</summary>
    public const string AuditTargetType = nameof(SupportTicket);

    // ---- read ---------------------------------------------------------------------------------

    // GET /api/platform/support/tickets
    /// <summary>
    /// The queue. Defaults to LIVE tickets only (New / Open / Pending), because a desk list that
    /// opens on two years of closed work is a list nobody uses; pass <c>includeFinished=true</c> or
    /// an explicit <c>status</c> to widen it.
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<PagedResultDto<SupportTicketSummaryDto>>> List(
        [FromQuery] long? tenantId,
        [FromQuery] string[]? status,
        [FromQuery] string[]? severity,
        [FromQuery] long? assignedToPlatformUserId,
        [FromQuery] bool? unassigned,
        [FromQuery] bool includeFinished = false,
        [FromQuery] string? search = null,
        [FromQuery] DateTime? fromUtc = null,
        [FromQuery] DateTime? toUtc = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        var query = context.Set<SupportTicket>().AsNoTracking().AsQueryable();

        if (tenantId is long tenant) query = query.Where(t => t.TenantId == tenant);

        if (ParseEnums<SupportTicketStatus>(status) is not { } statuses)
            return BadRequest(new { error = $"status must be one of {Names<SupportTicketStatus>()}." });
        if (ParseEnums<SupportTicketSeverity>(severity) is not { } severities)
            return BadRequest(new { error = $"severity must be one of {Names<SupportTicketSeverity>()}." });

        if (statuses.Length > 0)
            query = query.Where(t => statuses.Contains(t.Status));
        else if (!includeFinished)
            query = query.Where(t => SupportTicketLifecycle.Live.Contains(t.Status));

        if (severities.Length > 0) query = query.Where(t => severities.Contains(t.Severity));
        if (assignedToPlatformUserId is long assignee) query = query.Where(t => t.AssignedToPlatformUserId == assignee);
        if (unassigned == true) query = query.Where(t => t.AssignedToPlatformUserId == null);
        if (fromUtc is DateTime from) query = query.Where(t => t.CreatedAtUtc >= from);
        if (toUtc is DateTime to) query = query.Where(t => t.CreatedAtUtc <= to);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            // Subject and requester only. The BODY is excluded deliberately: it is the field most
            // likely to hold a customer's pasted personal data, and a substring scan over it is
            // both the slowest predicate here and the one most likely to surface content an
            // operator was not looking for.
            query = query.Where(t => t.Subject.ToLower().Contains(term) ||
                                     (t.RequesterEmail != null && t.RequesterEmail.ToLower().Contains(term)));
        }

        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = pageSize < 1 ? DefaultPageSize : pageSize > MaxPageSize ? MaxPageSize : pageSize;
        var total = await query.CountAsync(ct);

        var rows = await query
            .OrderByDescending(t => t.UpdatedAtUtc).ThenByDescending(t => t.Id)
            .Skip((normalizedPage - 1) * normalizedPageSize)
            .Take(normalizedPageSize)
            .Select(t => new TicketRow(
                t.Id, t.TenantId, t.Subject, t.Severity, t.Status, t.Origin,
                t.AssignedToPlatformUserId, t.OpenedByPlatformUserId, t.RequesterEmail,
                t.CreatedAtUtc, t.UpdatedAtUtc, t.FirstRespondedAtUtc, t.ResolvedAtUtc, t.ClosedAtUtc,
                t.RedactedAtUtc, t.Version,
                t.Notes.Count, t.Links.Count))
            .ToListAsync(ct);

        return Ok(new PagedResultDto<SupportTicketSummaryDto>
        {
            Items = await ProjectAsync(rows, ct),
            Page = normalizedPage,
            PageSize = normalizedPageSize,
            TotalCount = total
        });
    }

    // GET /api/platform/support/tickets/{id}
    [HttpGet("{id:long}")]
    public async Task<ActionResult<SupportTicketDetailDto>> Get(long id, CancellationToken ct)
    {
        var detail = await LoadDetailAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    // GET /api/platform/support/tickets/{id}/timeline
    /// <summary>
    /// The thread and the privileged-action trail for one ticket, merged in time order. The audit
    /// half is read straight out of <c>platform.PlatformAuditLogs</c> by target, which is what makes
    /// this view and the audit explorer agree by construction instead of by convention.
    /// </summary>
    [HttpGet("{id:long}/timeline")]
    public async Task<ActionResult<SupportTicketTimelineDto>> Timeline(long id, CancellationToken ct)
    {
        var ticket = await context.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.TenantId })
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
            return NotFound();

        var notes = await context.Set<SupportTicketNote>().AsNoTracking()
            .Where(n => n.SupportTicketId == id)
            .OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id)
            .Select(n => new { n.Id, n.AuthorKind, n.AuthorLabel, n.Body, n.IsInternal, n.CreatedAtUtc })
            .ToListAsync(ct);

        var targetId = id.ToString();
        var auditRows = await context.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.TargetType == AuditTargetType && a.TargetId == targetId)
            .OrderBy(a => a.CreatedOn).ThenBy(a => a.Id)
            .Select(a => new { a.Id, a.CreatedOn, a.ActorPlatformUserId, a.Action, a.Result, a.Metadata })
            .ToListAsync(ct);

        var actors = await LoadActorsAsync(auditRows.Select(a => a.ActorPlatformUserId).Distinct().ToArray(), ct);

        // This timeline republishes audit payloads exactly as the explorer does, so it is subject to
        // the same rule (finding R6). Support mutations are TenantAdmin-gated; a ReadOnlyOps operator
        // therefore reads the thread and the shape of every transition, but not the payloads.
        var gate = await PlatformAuditDisclosure.ResolveAsync(
            authorization, User, auditRows.Select(a => a.Action), ct);

        var entries = notes.Select(n => new SupportTicketTimelineEntryDto
        {
            Kind = "note",
            Id = n.Id.ToString(),
            OccurredAtUtc = n.CreatedAtUtc,
            Action = n.IsInternal ? "note.internal" : "note.customer-visible",
            Actor = n.AuthorLabel,
            Body = n.Body
        }).Concat(auditRows.Select(a => new SupportTicketTimelineEntryDto
        {
            Kind = "audit",
            Id = a.Id.ToString(),
            OccurredAtUtc = a.CreatedOn,
            Action = a.Action,
            Actor = PlatformAuditExplorerController.ActorLabel(a.ActorPlatformUserId, actors),
            Result = a.Result,
            Metadata = gate.Metadata(a.Action, a.Metadata),
            MetadataDisclosed = gate.MayDisclose(a.Action),
            MetadataPolicy = AuditDisclosureGate.RequiredPolicyFor(a.Action)
        }))
        .OrderBy(e => e.OccurredAtUtc).ThenBy(e => e.Id, StringComparer.Ordinal)
        .ToList();

        return Ok(new SupportTicketTimelineDto
        {
            TicketId = ticket.Id, TenantId = ticket.TenantId, Entries = entries
        });
    }

    // ---- write --------------------------------------------------------------------------------

    // POST /api/platform/support/tickets
    [HttpPost]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<SupportTicketDetailDto>> Create(
        [FromBody] CreateSupportTicketRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        // EXISTS, not "is Active". See the class docs: a suspended tenant is exactly the customer
        // support exists for, and refusing to open their ticket is the failure this guards against.
        var tenantExists = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(t => t.Id == request.TenantId, ct);
        if (!tenantExists)
            return BadRequest(new { error = $"Tenant {request.TenantId} does not exist." });

        if (!TryParseEnum<SupportTicketSeverity>(request.Severity, SupportTicketSeverity.Normal, out var severity))
            return BadRequest(new { error = $"severity must be one of {Names<SupportTicketSeverity>()}." });
        if (!TryParseEnum<SupportTicketOrigin>(request.Origin, SupportTicketOrigin.Operator, out var origin))
            return BadRequest(new { error = $"origin must be one of {Names<SupportTicketOrigin>()}." });
        if (origin != SupportTicketOrigin.Operator)
            return BadRequest(new
            {
                error = "Only operator-raised tickets can be created today. The customer and email " +
                        "channels exist in the model so that admitting them is an authorization " +
                        "change rather than a migration against live support history."
            });

        if (request.AssignToPlatformUserId is long assignee
            && await ValidateAssigneeAsync(assignee, ct) is string assigneeError)
            return BadRequest(new { error = assigneeError });

        var now = DateTime.UtcNow;
        var ticket = new SupportTicket
        {
            TenantId = request.TenantId,
            Subject = request.Subject.Trim(),
            Body = request.Body.Trim(),
            Severity = severity,
            Status = SupportTicketStatus.New,
            Origin = origin,
            OpenedByPlatformUserId = CurrentActorId(),
            AssignedToPlatformUserId = request.AssignToPlatformUserId,
            RequesterEmail = Normalize(request.RequesterEmail),
            RequesterTenantUserId = request.RequesterTenantUserId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        };

        try
        {
            await InTransactionAsync(async ct2 =>
            {
                context.Set<SupportTicket>().Add(ticket);
                await context.SaveChangesAsync(ct2);

                await audit.WriteAsync(User, Actions.Create, AuditTargetType, ticket.Id.ToString(),
                    new
                    {
                        ticket.TenantId, ticket.Subject, severity = severity.ToString(),
                        origin = origin.ToString(), assignedTo = ticket.AssignedToPlatformUserId,
                        requesterEmail = ticket.RequesterEmail
                    },
                    actAsTenantId: ticket.TenantId, httpContext: HttpContext, ct: ct2);
            }, ct);
        }
        catch (Exception)
        {
            logger.LogError("Failed to create a support ticket for tenant {TenantId}", request.TenantId);
            return StatusCode(500, new { error = "Support ticket creation failed." });
        }

        var detail = await LoadDetailAsync(ticket.Id, ct);
        return CreatedAtAction(nameof(Get), new { id = ticket.Id }, detail);
    }

    // POST /api/platform/support/tickets/{id}/notes
    [HttpPost("{id:long}/notes")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<SupportTicketDetailDto>> AddNote(
        long id, [FromBody] AddSupportTicketNoteRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Task.FromResult<ActionResult<SupportTicketDetailDto>>(BadRequest(ModelState));

        return MutateAsync(id, request.ExpectedVersion, (ticket, now) =>
        {
            RejectIfRedacted(ticket);

            var note = new SupportTicketNote
            {
                SupportTicketId = ticket.Id,
                AuthorPlatformUserId = CurrentActorId(),
                AuthorKind = SupportTicketAuthorKind.Operator,
                AuthorLabel = CurrentActorLabel(),
                Body = request.Body.Trim(),
                IsInternal = request.IsInternal ?? true,
                CreatedAtUtc = now
            };
            context.Set<SupportTicketNote>().Add(note);

            // The opening description lives on the ticket, so the first NOTE is the first thing
            // anybody said after it. "Has anyone answered this customer at all" is a different
            // question from "is it fixed", and only this column can answer it.
            ticket.FirstRespondedAtUtc ??= now;

            // Deliberately NOT stored on the audit row: a note body is customer-facing prose that
            // erasure has to be able to remove, and the audit log cannot be rewritten. The audit
            // row records that a note was added, by whom, and whether it was internal; the text
            // itself lives in the one place a purge can reach.
            return new TicketMutation(Actions.Note, new
            {
                noteId = (long?)null, internalNote = note.IsInternal, length = note.Body.Length
            });
        }, ct);
    }

    // POST /api/platform/support/tickets/{id}/transition
    [HttpPost("{id:long}/transition")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<SupportTicketDetailDto>> Transition(
        long id, [FromBody] TransitionSupportTicketRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Task.FromResult<ActionResult<SupportTicketDetailDto>>(BadRequest(ModelState));

        return MutateAsync(id, request.ExpectedVersion, (ticket, now) =>
        {
            RejectIfRedacted(ticket);

            if (!TryParseEnum<SupportTicketStatus>(request.Status, SupportTicketStatus.New, out var target)
                || string.IsNullOrWhiteSpace(request.Status))
                throw new TicketBadRequestException($"status must be one of {Names<SupportTicketStatus>()}.");

            if (!SupportTicketLifecycle.CanTransition(ticket.Status, target))
                throw new TicketConflictException(
                    $"A {ticket.Status} ticket cannot move to {target}. Allowed: " +
                    $"{string.Join(", ", SupportTicketLifecycle.NextFrom(ticket.Status))}.");

            var previous = ticket.Status;
            ticket.Status = target;

            if (SupportTicketLifecycle.IsReopen(previous, target))
            {
                // Cleared rather than kept. A reopened ticket that carries its old resolution
                // timestamp makes every time-to-resolution figure derived from this table measure
                // the attempt that did not work and report it as the outcome.
                ticket.ResolvedAtUtc = null;
                ticket.ClosedAtUtc = null;
                ticket.Resolution = null;
            }
            if (target == SupportTicketStatus.Resolved) ticket.ResolvedAtUtc = now;
            if (target == SupportTicketStatus.Closed) ticket.ClosedAtUtc ??= now;
            if (!string.IsNullOrWhiteSpace(request.Resolution)
                && target is SupportTicketStatus.Resolved or SupportTicketStatus.Closed)
                ticket.Resolution = request.Resolution.Trim();

            // {from, to} is the shape every tenant lifecycle transition already writes, so the
            // audit explorer's change decoder reads support transitions with no special case.
            return new TicketMutation(Actions.Transition, new
            {
                from = previous.ToString(),
                to = target.ToString(),
                reason = request.Reason.Trim(),
                resolution = ticket.Resolution
            });
        }, ct);
    }

    // POST /api/platform/support/tickets/{id}/assign
    [HttpPost("{id:long}/assign")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<SupportTicketDetailDto>> Assign(
        long id, [FromBody] AssignSupportTicketRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (request.AssignToPlatformUserId is long assignee
            && await ValidateAssigneeAsync(assignee, ct) is string assigneeError)
            return BadRequest(new { error = assigneeError });

        return await MutateAsync(id, request.ExpectedVersion, (ticket, _) =>
        {
            RejectIfRedacted(ticket);
            var previous = ticket.AssignedToPlatformUserId;
            ticket.AssignedToPlatformUserId = request.AssignToPlatformUserId;
            return new TicketMutation(Actions.Assign, new
            {
                fromAssignee = previous,
                toAssignee = request.AssignToPlatformUserId,
                reason = Normalize(request.Reason)
            });
        }, ct);
    }

    // POST /api/platform/support/tickets/{id}/severity
    [HttpPost("{id:long}/severity")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public Task<ActionResult<SupportTicketDetailDto>> ChangeSeverity(
        long id, [FromBody] ChangeSupportTicketSeverityRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return Task.FromResult<ActionResult<SupportTicketDetailDto>>(BadRequest(ModelState));

        return MutateAsync(id, request.ExpectedVersion, (ticket, _) =>
        {
            RejectIfRedacted(ticket);
            if (!TryParseEnum<SupportTicketSeverity>(request.Severity, SupportTicketSeverity.Normal, out var target)
                || string.IsNullOrWhiteSpace(request.Severity))
                throw new TicketBadRequestException($"severity must be one of {Names<SupportTicketSeverity>()}.");

            var previous = ticket.Severity;
            if (previous == target)
                throw new TicketConflictException($"The ticket is already {target}.");
            ticket.Severity = target;

            return new TicketMutation(Actions.Severity, new
            {
                fromSeverity = previous.ToString(),
                toSeverity = target.ToString(),
                reason = request.Reason.Trim()
            });
        }, ct);
    }

    // POST /api/platform/support/tickets/{id}/links
    /// <summary>
    /// Attach an audit entry or an impersonation session to the ticket that caused it.
    ///
    /// <para>The target MUST belong to the ticket's tenant. Without that check this endpoint would
    /// be a general-purpose primitive for reading any tenant's privileged-action metadata: attach
    /// their audit row to a ticket you control, then read it back off your own ticket's detail
    /// page. See <see cref="SupportTicketLink"/>.</para>
    /// </summary>
    [HttpPost("{id:long}/links")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<SupportTicketDetailDto>> Link(
        long id, [FromBody] LinkSupportTicketRequest request, CancellationToken ct)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        if (!TryParseEnum<SupportTicketLinkKind>(request.Kind, SupportTicketLinkKind.AuditLog, out var kind)
            || string.IsNullOrWhiteSpace(request.Kind))
            return BadRequest(new { error = $"kind must be one of {Names<SupportTicketLinkKind>()}." });

        var ticket = await context.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new { t.Id, t.TenantId, t.RedactedAtUtc })
            .FirstOrDefaultAsync(ct);
        if (ticket is null)
            return NotFound();
        if (ticket.RedactedAtUtc is not null)
            return Conflict(new { error = "A redacted ticket cannot be modified." });

        var targetKey = request.TargetKey.Trim();
        var resolution = await ResolveLinkTargetAsync(kind, targetKey, ticket.TenantId, ct);
        if (resolution.Error is string error)
            return BadRequest(new { error });

        var alreadyLinked = await context.Set<SupportTicketLink>().AsNoTracking()
            .AnyAsync(l => l.SupportTicketId == id && l.Kind == kind && l.TargetKey == targetKey, ct);
        if (alreadyLinked)
            return Conflict(new { error = "That target is already linked to this ticket." });

        return await MutateAsync(id, expectedVersion: null, (t, now) =>
        {
            context.Set<SupportTicketLink>().Add(new SupportTicketLink
            {
                SupportTicketId = t.Id,
                Kind = kind,
                TargetKey = targetKey,
                Note = Normalize(request.Note),
                LinkedByPlatformUserId = CurrentActorId(),
                LinkedByLabel = CurrentActorLabel(),
                LinkedAtUtc = now
            });
            return new TicketMutation(Actions.Link, new
            {
                kind = kind.ToString(), targetKey, note = Normalize(request.Note)
            });
        }, ct);
    }

    // DELETE /api/platform/support/tickets/{id}/links/{linkId}
    /// <summary>
    /// Detach a link. A mis-attached audit row is a clerical error rather than history, and the
    /// detachment is itself audited. Nothing the link pointed AT is affected: both registers are
    /// append-only and this endpoint never touches them.
    /// </summary>
    [HttpDelete("{id:long}/links/{linkId:long}")]
    [Authorize(Policy = PlatformPolicies.TenantAdmin)]
    public async Task<ActionResult<SupportTicketDetailDto>> Unlink(
        long id, long linkId, CancellationToken ct)
    {
        var link = await context.Set<SupportTicketLink>().AsNoTracking()
            .Where(l => l.Id == linkId && l.SupportTicketId == id)
            .Select(l => new { l.Id, l.Kind, l.TargetKey })
            .FirstOrDefaultAsync(ct);
        if (link is null)
            return NotFound();

        return await MutateAsync(id, expectedVersion: null, (_, _) =>
        {
            var tracked = new SupportTicketLink { Id = linkId, SupportTicketId = id };
            context.Set<SupportTicketLink>().Attach(tracked);
            context.Set<SupportTicketLink>().Remove(tracked);
            return new TicketMutation(Actions.Unlink, new
            {
                kind = link.Kind.ToString(), targetKey = link.TargetKey
            });
        }, ct);
    }

    // ---- mutation plumbing --------------------------------------------------------------------

    private sealed record TicketMutation(string Action, object Metadata);

    /// <summary>
    /// The single write path. Every mutation loads the ticket fresh INSIDE the transaction, applies
    /// <paramref name="mutate"/>, bumps the concurrency token and the activity clock, saves, writes
    /// the audit row through the same context, and commits — so the ticket change and its audit
    /// record share one fate (Sec3).
    ///
    /// <para>The reload is inside the retry loop for the reason <c>TenantsController.ChangeStatus</c>
    /// documents: reusing an entity that already passed through SaveChanges in a rolled-back attempt
    /// leaves it Unchanged, and the retry then commits only the audit row while the ticket silently
    /// does not move.</para>
    /// </summary>
    private async Task<ActionResult<SupportTicketDetailDto>> MutateAsync(
        long id, long? expectedVersion,
        Func<SupportTicket, DateTime, TicketMutation> mutate,
        CancellationToken ct)
    {
        try
        {
            await InTransactionAsync(async ct2 =>
            {
                var ticket = await context.Set<SupportTicket>().FirstOrDefaultAsync(t => t.Id == id, ct2)
                             ?? throw new TicketNotFoundException();

                if (expectedVersion is long expected && ticket.Version != expected)
                    throw new TicketConflictException(
                        "The ticket changed since it was loaded; reload and try again.");

                var now = DateTime.UtcNow;
                var mutation = mutate(ticket, now);

                ticket.UpdatedAtUtc = now;
                ticket.Version++;
                await context.SaveChangesAsync(ct2);

                await audit.WriteAsync(User, mutation.Action, AuditTargetType, ticket.Id.ToString(),
                    mutation.Metadata, actAsTenantId: ticket.TenantId, httpContext: HttpContext, ct: ct2);
            }, ct);
        }
        catch (TicketNotFoundException)
        {
            return NotFound();
        }
        catch (TicketBadRequestException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (TicketConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (DbUpdateConcurrencyException)
        {
            // The EF concurrency token caught a write that another operator's transaction had
            // already superseded. This is the branch the optional expectedVersion cannot cover:
            // two writes that both read the same version and race to commit.
            return Conflict(new { error = "The ticket changed while this update was being saved; reload and try again." });
        }
        catch (Exception)
        {
            logger.LogError("Failed to update support ticket {TicketId}", id);
            return StatusCode(500, new { error = "Support ticket update failed." });
        }

        var detail = await LoadDetailAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    private Task InTransactionAsync(Func<CancellationToken, Task> body, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var tx = await context.Database.BeginTransactionAsync(ct);
            await body(ct);
            await tx.CommitAsync(ct);
        });
    }

    private static void RejectIfRedacted(SupportTicket ticket)
    {
        if (ticket.RedactedAtUtc is not null)
            throw new TicketConflictException(
                "This ticket was redacted when its tenant was purged and can no longer be modified.");
    }

    private sealed class TicketNotFoundException : Exception;

    private sealed class TicketConflictException(string message) : Exception(message);

    private sealed class TicketBadRequestException(string message) : Exception(message);

    // ---- link target resolution ---------------------------------------------------------------

    private sealed record LinkResolution(string? Error);

    /// <summary>
    /// Resolves a link target and refuses it unless it belongs to the ticket's tenant. The refusal
    /// message deliberately does not distinguish "does not exist" from "belongs to another tenant":
    /// telling an operator which of the two it was turns this endpoint into an oracle for probing
    /// the existence of other tenants' audit rows by id.
    /// </summary>
    private async Task<LinkResolution> ResolveLinkTargetAsync(
        SupportTicketLinkKind kind, string targetKey, long tenantId, CancellationToken ct)
    {
        switch (kind)
        {
            case SupportTicketLinkKind.AuditLog:
                if (!long.TryParse(targetKey, out var auditId))
                    return new LinkResolution("An AuditLog link target must be a numeric audit entry id.");
                var auditMatches = await context.Set<PlatformAuditLog>().AsNoTracking()
                    .AnyAsync(a => a.Id == auditId && a.ActAsTenantId == tenantId, ct);
                return new LinkResolution(auditMatches
                    ? null
                    : "No audit entry for this tenant matches that id.");

            case SupportTicketLinkKind.ImpersonationSession:
                var sessionMatches = await context.Set<ImpersonationSession>().AsNoTracking()
                    .AnyAsync(s => s.Jti == targetKey && s.TenantId == tenantId, ct);
                return new LinkResolution(sessionMatches
                    ? null
                    : "No impersonation session for this tenant matches that jti.");

            default:
                return new LinkResolution($"kind must be one of {Names<SupportTicketLinkKind>()}.");
        }
    }

    // ---- projection ---------------------------------------------------------------------------

    private async Task<SupportTicketDetailDto?> LoadDetailAsync(long id, CancellationToken ct)
    {
        var row = await context.Set<SupportTicket>().AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => new TicketDetailRow(
                new TicketRow(
                    t.Id, t.TenantId, t.Subject, t.Severity, t.Status, t.Origin,
                    t.AssignedToPlatformUserId, t.OpenedByPlatformUserId, t.RequesterEmail,
                    t.CreatedAtUtc, t.UpdatedAtUtc, t.FirstRespondedAtUtc, t.ResolvedAtUtc, t.ClosedAtUtc,
                    t.RedactedAtUtc, t.Version, t.Notes.Count, t.Links.Count),
                t.Body, t.Resolution, t.RequesterTenantUserId, t.RedactedReason))
            .FirstOrDefaultAsync(ct);
        if (row is null)
            return null;

        var summary = (await ProjectAsync([row.Summary], ct))[0];

        var notes = await context.Set<SupportTicketNote>().AsNoTracking()
            .Where(n => n.SupportTicketId == id)
            .OrderBy(n => n.CreatedAtUtc).ThenBy(n => n.Id)
            .Select(n => new SupportTicketNoteDto
            {
                Id = n.Id,
                AuthorPlatformUserId = n.AuthorPlatformUserId,
                AuthorKind = n.AuthorKind.ToString(),
                AuthorLabel = n.AuthorLabel,
                Body = n.Body,
                IsInternal = n.IsInternal,
                CreatedAtUtc = n.CreatedAtUtc
            })
            .ToListAsync(ct);

        var links = await LoadLinksAsync(id, ct);

        return new SupportTicketDetailDto
        {
            Id = summary.Id,
            TenantId = summary.TenantId,
            TenantName = summary.TenantName,
            TenantSlug = summary.TenantSlug,
            TenantStatus = summary.TenantStatus,
            Subject = summary.Subject,
            Severity = summary.Severity,
            Status = summary.Status,
            Origin = summary.Origin,
            AssignedToPlatformUserId = summary.AssignedToPlatformUserId,
            AssignedToEmail = summary.AssignedToEmail,
            OpenedByPlatformUserId = summary.OpenedByPlatformUserId,
            OpenedByEmail = summary.OpenedByEmail,
            RequesterEmail = summary.RequesterEmail,
            CreatedAtUtc = summary.CreatedAtUtc,
            UpdatedAtUtc = summary.UpdatedAtUtc,
            FirstRespondedAtUtc = summary.FirstRespondedAtUtc,
            ResolvedAtUtc = summary.ResolvedAtUtc,
            ClosedAtUtc = summary.ClosedAtUtc,
            NoteCount = summary.NoteCount,
            LinkCount = summary.LinkCount,
            IsRedacted = summary.IsRedacted,
            Version = summary.Version,
            Body = row.Body,
            Resolution = row.Resolution,
            RequesterTenantUserId = row.RequesterTenantUserId,
            RedactedAtUtc = row.Summary.RedactedAtUtc,
            RedactedReason = row.RedactedReason,
            Notes = notes,
            Links = links,
            AllowedTransitions = SupportTicketLifecycle
                .NextFrom(Enum.Parse<SupportTicketStatus>(summary.Status))
                .Select(s => s.ToString()).ToList()
        };
    }

    /// <summary>
    /// Links plus a one-line rendering of what each points at, resolved at read time. A target that
    /// no longer resolves leaves <c>TargetSummary</c> null and the link visible — silently dropping
    /// it would hide the fact that a ticket once claimed a session that is now unaccounted for.
    /// </summary>
    private async Task<IReadOnlyList<SupportTicketLinkDto>> LoadLinksAsync(long ticketId, CancellationToken ct)
    {
        var links = await context.Set<SupportTicketLink>().AsNoTracking()
            .Where(l => l.SupportTicketId == ticketId)
            .OrderBy(l => l.LinkedAtUtc).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id, l.Kind, l.TargetKey, l.Note, l.LinkedByLabel, l.LinkedAtUtc
            })
            .ToListAsync(ct);
        if (links.Count == 0) return [];

        var auditIds = links.Where(l => l.Kind == SupportTicketLinkKind.AuditLog)
            .Select(l => long.TryParse(l.TargetKey, out var parsed) ? parsed : -1)
            .Where(parsed => parsed > 0).Distinct().ToArray();
        var jtis = links.Where(l => l.Kind == SupportTicketLinkKind.ImpersonationSession)
            .Select(l => l.TargetKey).Distinct().ToArray();

        var auditTargets = auditIds.Length == 0
            ? []
            : await context.Set<PlatformAuditLog>().AsNoTracking()
                .Where(a => auditIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Action, a.Result, a.CreatedOn })
                .ToDictionaryAsync(a => a.Id.ToString(), ct);
        var sessionTargets = jtis.Length == 0
            ? []
            : await context.Set<ImpersonationSession>().AsNoTracking()
                .Where(s => jtis.Contains(s.Jti))
                .Select(s => new { s.Jti, s.Reason, s.IssuedAtUtc })
                .ToDictionaryAsync(s => s.Jti, ct);

        return links.Select(l =>
        {
            string? summary = null;
            DateTime? occurred = null;
            if (l.Kind == SupportTicketLinkKind.AuditLog
                && auditTargets.TryGetValue(l.TargetKey, out var auditTarget))
            {
                summary = $"{auditTarget.Action} ({auditTarget.Result})";
                occurred = auditTarget.CreatedOn;
            }
            else if (l.Kind == SupportTicketLinkKind.ImpersonationSession
                     && sessionTargets.TryGetValue(l.TargetKey, out var sessionTarget))
            {
                summary = sessionTarget.Reason;
                occurred = sessionTarget.IssuedAtUtc;
            }

            return new SupportTicketLinkDto
            {
                Id = l.Id,
                Kind = l.Kind.ToString(),
                TargetKey = l.TargetKey,
                Note = l.Note,
                LinkedByLabel = l.LinkedByLabel,
                LinkedAtUtc = l.LinkedAtUtc,
                TargetSummary = summary,
                TargetOccurredAtUtc = occurred
            };
        }).ToList();
    }

    /// <summary>
    /// Adds tenant and operator labels to a page of tickets with two extra queries. Both PROJECT:
    /// platform.Tenants and platform.PlatformUsers carry column-level grants for the narrower
    /// execution roles, and materialising a whole entity to read two columns is the shape that
    /// passes on SQLite and fails with 42501 in production.
    /// </summary>
    private async Task<IReadOnlyList<SupportTicketSummaryDto>> ProjectAsync(
        IReadOnlyList<TicketRow> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var tenantIds = rows.Select(r => r.TenantId).Distinct().ToArray();
        var tenants = await context.Set<Tenant>().IgnoreQueryFilters().AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Name, t.Slug, t.Status })
            .ToDictionaryAsync(t => t.Id, ct);

        var operatorIds = rows.Where(r => r.AssignedToPlatformUserId != null)
            .Select(r => r.AssignedToPlatformUserId!.Value)
            .Concat(rows.Where(r => r.OpenedByPlatformUserId != null)
                .Select(r => r.OpenedByPlatformUserId!.Value))
            .Distinct().ToArray();
        var operators = await LoadActorsAsync(operatorIds, ct);

        return rows.Select(r =>
        {
            tenants.TryGetValue(r.TenantId, out var tenant);
            return new SupportTicketSummaryDto
            {
                Id = r.Id,
                TenantId = r.TenantId,
                TenantName = tenant?.Name,
                TenantSlug = tenant?.Slug,
                TenantStatus = tenant?.Status.ToString(),
                Subject = r.Subject,
                Severity = r.Severity.ToString(),
                Status = r.Status.ToString(),
                Origin = r.Origin.ToString(),
                AssignedToPlatformUserId = r.AssignedToPlatformUserId,
                AssignedToEmail = Email(r.AssignedToPlatformUserId, operators),
                OpenedByPlatformUserId = r.OpenedByPlatformUserId,
                OpenedByEmail = Email(r.OpenedByPlatformUserId, operators),
                RequesterEmail = r.RequesterEmail,
                CreatedAtUtc = r.CreatedAtUtc,
                UpdatedAtUtc = r.UpdatedAtUtc,
                FirstRespondedAtUtc = r.FirstRespondedAtUtc,
                ResolvedAtUtc = r.ResolvedAtUtc,
                ClosedAtUtc = r.ClosedAtUtc,
                NoteCount = r.NoteCount,
                LinkCount = r.LinkCount,
                IsRedacted = r.RedactedAtUtc is not null,
                Version = r.Version
            };
        }).ToList();
    }

    private static string? Email(
        long? id, IReadOnlyDictionary<long, PlatformAuditExplorerController.ActorLabelRow> operators)
        => id is long value && operators.TryGetValue(value, out var actor) ? actor.Email : null;

    private async Task<Dictionary<long, PlatformAuditExplorerController.ActorLabelRow>> LoadActorsAsync(
        long[] ids, CancellationToken ct)
        => ids.Length == 0
            ? []
            : await context.Set<PlatformUser>().AsNoTracking()
                .Where(u => ids.Contains(u.Id))
                .Select(u => new PlatformAuditExplorerController.ActorLabelRow(u.Id, u.Email, u.DisplayName))
                .ToDictionaryAsync(u => u.Id, ct);

    // ---- helpers ------------------------------------------------------------------------------

    /// <summary>
    /// Refuses an assignee that does not exist or is deactivated. Assigning to a deactivated
    /// operator is not a harmless typo: the ticket leaves the unassigned queue, acquires an owner
    /// who will never log in again, and is then invisible to every list an ops lead actually reads.
    /// </summary>
    private async Task<string?> ValidateAssigneeAsync(long assigneeId, CancellationToken ct)
    {
        var assignee = await context.Set<PlatformUser>().AsNoTracking()
            .Where(u => u.Id == assigneeId)
            .Select(u => new { u.Id, u.IsActive })
            .FirstOrDefaultAsync(ct);
        if (assignee is null)
            return $"Platform user {assigneeId} does not exist.";
        return assignee.IsActive ? null : $"Platform user {assigneeId} is deactivated and cannot own tickets.";
    }

    private long? CurrentActorId()
        => long.TryParse(
            User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
            out var id) && id > 0
            ? id
            : null;

    /// <summary>
    /// The author label frozen onto notes and links. Falls back to the actor id, then to
    /// "platform" — the same fallback <c>TenantsController</c> uses for <c>ModifiedBy</c> — so a
    /// thread entry is never attributed to nobody.
    /// </summary>
    private string CurrentActorLabel()
        => User.FindFirst("email")?.Value
           ?? User.FindFirst(ClaimTypes.Email)?.Value
           ?? (CurrentActorId() is long id ? $"Platform user {id}" : "platform");

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Names<TEnum>() where TEnum : struct, Enum
        => string.Join(", ", Enum.GetNames<TEnum>());

    private static bool TryParseEnum<TEnum>(string? value, TEnum fallback, out TEnum parsed)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = fallback;
            return true;
        }
        return Enum.TryParse(value.Trim(), ignoreCase: true, out parsed)
               && Enum.IsDefined(parsed);
    }

    /// <summary>Null signals "one of the supplied values is not a member", which callers turn into a 400.</summary>
    private static TEnum[]? ParseEnums<TEnum>(string[]? values) where TEnum : struct, Enum
    {
        if (values is null || values.Length == 0) return [];
        var parsed = new List<TEnum>();
        foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
        {
            if (!Enum.TryParse<TEnum>(value.Trim(), ignoreCase: true, out var member) || !Enum.IsDefined(member))
                return null;
            parsed.Add(member);
        }
        return parsed.Distinct().ToArray();
    }

    /// <summary>The exact ticket columns every list/detail read projects. See <see cref="ProjectAsync"/>.</summary>
    private sealed record TicketRow(
        long Id, long TenantId, string Subject, SupportTicketSeverity Severity, SupportTicketStatus Status,
        SupportTicketOrigin Origin, long? AssignedToPlatformUserId, long? OpenedByPlatformUserId,
        string? RequesterEmail, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, DateTime? FirstRespondedAtUtc,
        DateTime? ResolvedAtUtc, DateTime? ClosedAtUtc, DateTime? RedactedAtUtc, long Version,
        int NoteCount, int LinkCount);

    private sealed record TicketDetailRow(
        TicketRow Summary, string? Body, string? Resolution, long? RequesterTenantUserId, string? RedactedReason);
}
