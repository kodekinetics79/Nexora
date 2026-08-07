using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using static ERP_RFQ_Automation.Tests.PlatformSupportFixture;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The support desk's write path: the lifecycle graph, the audit trail each transition leaves, the
/// concurrency guard, and the two properties the desk was specified around — that a suspended tenant
/// can still be supported, and that a link cannot be used to pull another tenant's records onto a
/// ticket.
/// </summary>
public sealed class PlatformSupportTicketLifecycleTests
{
    // ---- suspension --------------------------------------------------------

    [Theory]
    [InlineData(TenantStatus.Active)]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Archived)]
    [InlineData(TenantStatus.Provisioning)]
    public async Task A_ticket_can_be_raised_and_worked_for_a_tenant_in_any_lifecycle_state(TenantStatus status)
    {
        // A suspended tenant is the customer most likely to be calling. A desk that refuses to
        // record their problem fails at the only moment it was bought for, so nothing on the write
        // path may consult Tenant.Status.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, $"lifecycle-{status}".ToLowerInvariant(), status);

        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        var moved = Ok(await Tickets(context).Transition(ticketId,
            new TransitionSupportTicketRequest { Status = nameof(SupportTicketStatus.Open), Reason = "Triaged" },
            CancellationToken.None));

        Assert.Equal(nameof(SupportTicketStatus.Open), moved.Status);
        Assert.Equal(status.ToString(), moved.TenantStatus);
    }

    [Fact]
    public async Task A_ticket_for_a_tenant_that_does_not_exist_is_refused()
    {
        using var db = new PlatformSupportTestDb();
        await using var context = db.ContextFor(null);

        var result = await Tickets(context).Create(new CreateSupportTicketRequest
        {
            TenantId = 9_999, Subject = "Ghost", Body = "No such customer."
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(await context.Set<SupportTicket>().ToListAsync());
    }

    // ---- lifecycle + audit trail -------------------------------------------

    [Fact]
    public async Task The_full_lifecycle_walks_the_graph_and_leaves_one_audit_row_per_move()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "walker");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await Move(db, ticketId, SupportTicketStatus.Open, "Picked up");
        await Move(db, ticketId, SupportTicketStatus.Pending, "Waiting on the customer");
        var resolved = await Move(db, ticketId, SupportTicketStatus.Resolved, "Reset the credential", "Password reset");
        Assert.NotNull(resolved.ResolvedAtUtc);
        Assert.Equal("Password reset", resolved.Resolution);

        var closed = await Move(db, ticketId, SupportTicketStatus.Closed, "Customer confirmed");
        Assert.NotNull(closed.ClosedAtUtc);

        var reopened = await Move(db, ticketId, SupportTicketStatus.Open, "Customer says it is back");

        // A reopened ticket that kept its resolution timestamp would make every
        // time-to-resolution figure derived from this table measure the attempt that did not work.
        Assert.Null(reopened.ResolvedAtUtc);
        Assert.Null(reopened.ClosedAtUtc);
        Assert.Null(reopened.Resolution);

        await using var verification = db.ContextFor(null);
        var trail = await verification.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.TargetType == PlatformSupportTicketsController.AuditTargetType
                        && a.TargetId == ticketId.ToString())
            .OrderBy(a => a.Id)
            .ToListAsync();

        Assert.Equal(
            [
                PlatformSupportTicketsController.Actions.Create,
                PlatformSupportTicketsController.Actions.Transition,
                PlatformSupportTicketsController.Actions.Transition,
                PlatformSupportTicketsController.Actions.Transition,
                PlatformSupportTicketsController.Actions.Transition,
                PlatformSupportTicketsController.Actions.Transition
            ],
            trail.Select(a => a.Action));
        Assert.All(trail, a => Assert.Equal(tenantId, a.ActAsTenantId));
        Assert.All(trail, a => Assert.Equal(PlatformAuditResults.Success, a.Result));
        Assert.All(trail, a => Assert.Equal(OwnerActorId, a.ActorPlatformUserId));

        // The transition metadata uses the same {from, to} shape every tenant lifecycle change
        // writes, so the audit explorer decodes support transitions with no special case.
        Assert.Contains("\"from\":\"Pending\"", trail[3].Metadata);
        Assert.Contains("\"to\":\"Resolved\"", trail[3].Metadata);
        Assert.Contains("Reset the credential", trail[3].Metadata);
    }

    [Fact]
    public async Task An_illegal_transition_is_refused_and_writes_no_audit_row()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "illegal");
        var ticketId = await RaiseTicketAsync(db, tenantId);
        await Move(db, ticketId, SupportTicketStatus.Closed, "Duplicate of an existing ticket");

        await using var context = db.ContextFor(null);
        var result = await Tickets(context).Transition(ticketId, new TransitionSupportTicketRequest
        {
            Status = nameof(SupportTicketStatus.Resolved), Reason = "Trying to resolve a closed ticket"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);

        await using var verification = db.ContextFor(null);
        var persisted = await verification.Set<SupportTicket>().SingleAsync(t => t.Id == ticketId);
        Assert.Equal(SupportTicketStatus.Closed, persisted.Status);

        // The mutation and its audit row share one transaction, so a refused move must leave
        // neither behind: exactly two transitions were ever accepted (create + close).
        var transitions = await verification.Set<PlatformAuditLog>()
            .CountAsync(a => a.TargetId == ticketId.ToString()
                             && a.Action == PlatformSupportTicketsController.Actions.Transition);
        Assert.Equal(1, transitions);
    }

    [Fact]
    public async Task Nothing_can_return_a_ticket_to_New()
    {
        // "Untriaged" is a claim about a ticket nobody has looked at yet. Once somebody has, it is
        // false forever, so the graph has no edge back to it from anywhere.
        Assert.All(Enum.GetValues<SupportTicketStatus>(),
            status => Assert.DoesNotContain(SupportTicketStatus.New, SupportTicketLifecycle.NextFrom(status)));
    }

    // ---- concurrency -------------------------------------------------------

    [Fact]
    public async Task A_stale_expected_version_is_refused()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "concurrent");
        var ticketId = await RaiseTicketAsync(db, tenantId);
        var opened = await Move(db, ticketId, SupportTicketStatus.Open, "Picked up");

        await using var context = db.ContextFor(null);
        var result = await Tickets(context).Transition(ticketId, new TransitionSupportTicketRequest
        {
            Status = nameof(SupportTicketStatus.Resolved),
            Reason = "Second operator, stale tab",
            ExpectedVersion = opened.Version - 1
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Every_accepted_mutation_advances_the_concurrency_token()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "versioned");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        var afterNote = Ok(await Tickets(context).AddNote(ticketId,
            new AddSupportTicketNoteRequest { Body = "Asked for a HAR file." }, CancellationToken.None));
        var afterSeverity = Ok(await Tickets(context).ChangeSeverity(ticketId,
            new ChangeSupportTicketSeverityRequest
            {
                Severity = nameof(SupportTicketSeverity.High), Reason = "Whole team blocked"
            }, CancellationToken.None));

        Assert.True(afterNote.Version > 1);
        Assert.True(afterSeverity.Version > afterNote.Version);
    }

    // ---- notes -------------------------------------------------------------

    [Fact]
    public async Task The_first_note_stamps_first_response_and_its_text_never_reaches_the_audit_log()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "notes");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        const string body = "Customer's SSO certificate expired on the 3rd.";
        var first = Ok(await Tickets(context).AddNote(ticketId,
            new AddSupportTicketNoteRequest { Body = body }, CancellationToken.None));
        Assert.NotNull(first.FirstRespondedAtUtc);
        Assert.Equal(OwnerEmail, Assert.Single(first.Notes).AuthorLabel);
        Assert.True(Assert.Single(first.Notes).IsInternal);

        var second = Ok(await Tickets(context).AddNote(ticketId,
            new AddSupportTicketNoteRequest { Body = "Reissued." }, CancellationToken.None));
        Assert.Equal(first.FirstRespondedAtUtc, second.FirstRespondedAtUtc);
        Assert.Equal(2, second.Notes.Count);

        // A note body is customer-facing prose that erasure has to be able to remove, and the audit
        // log cannot be rewritten. The audit row records that a note happened, never what it said.
        await using var verification = db.ContextFor(null);
        var noteAudit = await verification.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.Action == PlatformSupportTicketsController.Actions.Note)
            .ToListAsync();
        Assert.Equal(2, noteAudit.Count);
        Assert.All(noteAudit, a => Assert.DoesNotContain(body, a.Metadata ?? string.Empty));
    }

    [Fact]
    public async Task A_note_defaults_to_internal_even_when_the_caller_says_nothing()
    {
        // The desk is operator-only today, so nothing reads IsInternal. It defaults closed anyway:
        // a customer view added later must not retroactively publish years of internal commentary
        // because the column arrived with a permissive default.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "internal-default");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        var detail = Ok(await Tickets(context).AddNote(ticketId,
            new AddSupportTicketNoteRequest { Body = "Suspect their proxy." }, CancellationToken.None));

        Assert.True(Assert.Single(detail.Notes).IsInternal);
    }

    [Fact]
    public async Task An_explicitly_customer_visible_note_is_not_silently_flipped_back_by_the_column_default()
    {
        // The column carries DEFAULT TRUE, and a database default over a bool is the classic way to
        // lose a `false`: if the provider treats the CLR default as "unset", every note the caller
        // marked customer-visible would come back internal — a data-integrity bug invisible until
        // the customer channel exists and nothing is ever published.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "visible-note");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using (var context = db.ContextFor(null))
            await Tickets(context).AddNote(ticketId,
                new AddSupportTicketNoteRequest { Body = "We have reset your SSO.", IsInternal = false },
                CancellationToken.None);

        await using var verification = db.ContextFor(null);
        var note = await verification.Set<SupportTicketNote>().AsNoTracking()
            .SingleAsync(n => n.SupportTicketId == ticketId);
        Assert.False(note.IsInternal);
    }

    // ---- assignment --------------------------------------------------------

    [Fact]
    public async Task A_deactivated_operator_cannot_be_given_a_ticket()
    {
        // Not a harmless typo: the ticket leaves the unassigned queue, acquires an owner who will
        // never log in again, and becomes invisible to every list an ops lead reads.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "assignment");
        var retired = await SeedOperatorAsync(db, "retired@example.test", isActive: false);
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        var result = await Tickets(context).Assign(ticketId,
            new AssignSupportTicketRequest { AssignToPlatformUserId = retired }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Assignment_and_unassignment_are_both_audited_with_before_and_after()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "assign-audit");
        var engineer = await SeedOperatorAsync(db, "engineer@example.test");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        var assigned = Ok(await Tickets(context).Assign(ticketId,
            new AssignSupportTicketRequest { AssignToPlatformUserId = engineer, Reason = "Owns SSO" },
            CancellationToken.None));
        Assert.Equal(engineer, assigned.AssignedToPlatformUserId);
        Assert.Equal("engineer@example.test", assigned.AssignedToEmail);

        var unassigned = Ok(await Tickets(context).Assign(ticketId,
            new AssignSupportTicketRequest { AssignToPlatformUserId = null, Reason = "Back to the queue" },
            CancellationToken.None));
        Assert.Null(unassigned.AssignedToPlatformUserId);

        await using var verification = db.ContextFor(null);
        var trail = await verification.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.Action == PlatformSupportTicketsController.Actions.Assign)
            .OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, trail.Count);
        Assert.Contains($"\"toAssignee\":{engineer}", trail[0].Metadata);
        Assert.Contains($"\"fromAssignee\":{engineer}", trail[1].Metadata);
        Assert.Contains("\"toAssignee\":null", trail[1].Metadata);
    }

    // ---- links, and the boundary they must not cross -----------------------

    [Fact]
    public async Task An_audit_entry_belonging_to_another_tenant_cannot_be_linked()
    {
        // Linking RENDERS the target inside the ticket detail. Without the tenant check this
        // endpoint would be a general-purpose primitive for reading any tenant's privileged-action
        // metadata: attach their row to a ticket you control, then read it off your own page.
        using var db = new PlatformSupportTestDb();
        var mine = await SeedTenantAsync(db, "acme");
        var theirs = await SeedTenantAsync(db, "initech");
        var ticketId = await RaiseTicketAsync(db, mine);

        var foreignRow = await SeedAuditAsync(db, "tenant.suspend", theirs, OwnerActorId,
            metadata: "{\"reason\":\"Initech is 90 days overdue\"}");

        await using var context = db.ContextFor(null);
        var result = await Tickets(context).Link(ticketId, new LinkSupportTicketRequest
        {
            Kind = nameof(SupportTicketLinkKind.AuditLog), TargetKey = foreignRow.Id.ToString()
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);

        await using var verification = db.ContextFor(null);
        Assert.Empty(await verification.Set<SupportTicketLink>().ToListAsync());
    }

    [Fact]
    public async Task An_impersonation_session_belonging_to_another_tenant_cannot_be_linked()
    {
        using var db = new PlatformSupportTestDb();
        var mine = await SeedTenantAsync(db, "acme-sessions");
        var theirs = await SeedTenantAsync(db, "initech-sessions");
        var ticketId = await RaiseTicketAsync(db, mine);
        var foreignJti = await SeedImpersonationAsync(db, theirs, OwnerActorId, "Investigating Initech's quotes");

        await using var context = db.ContextFor(null);
        var result = await Tickets(context).Link(ticketId, new LinkSupportTicketRequest
        {
            Kind = nameof(SupportTicketLinkKind.ImpersonationSession), TargetKey = foreignJti
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_matching_session_links_renders_and_can_be_detached()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "linkable");
        var ticketId = await RaiseTicketAsync(db, tenantId);
        var jti = await SeedImpersonationAsync(db, tenantId, OwnerActorId, "Reproducing the login failure");

        await using var context = db.ContextFor(null);
        var linked = Ok(await Tickets(context).Link(ticketId, new LinkSupportTicketRequest
        {
            Kind = nameof(SupportTicketLinkKind.ImpersonationSession),
            TargetKey = jti,
            Note = "Entered the account to reproduce"
        }, CancellationToken.None));

        var link = Assert.Single(linked.Links);
        Assert.Equal(jti, link.TargetKey);
        Assert.Equal("Reproducing the login failure", link.TargetSummary);

        var duplicate = await Tickets(context).Link(ticketId, new LinkSupportTicketRequest
        {
            Kind = nameof(SupportTicketLinkKind.ImpersonationSession), TargetKey = jti
        }, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(duplicate.Result);

        var detached = Ok(await Tickets(context).Unlink(ticketId, link.Id, CancellationToken.None));
        Assert.Empty(detached.Links);

        await using var verification = db.ContextFor(null);
        // The register the link pointed at is append-only and must be entirely unaffected.
        Assert.Equal(1, await verification.Set<ImpersonationSession>().CountAsync(s => s.Jti == jti));
        Assert.Equal(2, await verification.Set<PlatformAuditLog>().CountAsync(a =>
            a.Action == PlatformSupportTicketsController.Actions.Link ||
            a.Action == PlatformSupportTicketsController.Actions.Unlink));
    }

    // ---- purge -------------------------------------------------------------

    [Fact]
    public async Task Purge_erases_ticket_content_keeps_the_ticket_and_keeps_the_audit_trail()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "purged");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using (var working = db.ContextFor(null))
        {
            await Tickets(working).AddNote(ticketId,
                new AddSupportTicketNoteRequest { Body = "Called Jane on 07700 900123." }, CancellationToken.None);
            await Tickets(working).Transition(ticketId, new TransitionSupportTicketRequest
            {
                Status = nameof(SupportTicketStatus.Resolved), Reason = "Fixed", Resolution = "Reset SSO"
            }, CancellationToken.None);
        }

        await using (var purge = db.ContextFor(null))
        {
            var result = await Redactor(purge).RedactForPurgedTenantAsync(
                Actor(), tenantId, "Tenant purged under the offboarding contract");
            Assert.Equal(1, result.TicketsRedacted);
            Assert.Equal(1, result.NotesErased);
        }

        await using var verification = db.ContextFor(null);
        var ticket = await verification.Set<SupportTicket>().AsNoTracking().SingleAsync(t => t.Id == ticketId);

        // The SHAPE survives: which ticket existed, when, who worked it, how it moved.
        Assert.Equal(tenantId, ticket.TenantId);
        Assert.Equal(SupportTicketStatus.Resolved, ticket.Status);
        Assert.NotNull(ticket.ResolvedAtUtc);
        Assert.NotNull(ticket.RedactedAtUtc);

        // The CONTENT is gone.
        Assert.Equal(SupportTicketRedactionService.RedactedSubject, ticket.Subject);
        Assert.Null(ticket.Body);
        Assert.Null(ticket.Resolution);
        Assert.Null(ticket.RequesterEmail);

        var notes = await verification.Set<SupportTicketNote>().AsNoTracking()
            .Where(n => n.SupportTicketId == ticketId).ToListAsync();
        var tombstone = Assert.Single(notes);
        Assert.Equal(SupportTicketAuthorKind.System, tombstone.AuthorKind);
        Assert.DoesNotContain("07700 900123", tombstone.Body);

        // The append-only trail of what was done for this customer is untouched, and the erasure
        // itself is now part of it.
        var trail = await verification.Set<PlatformAuditLog>().AsNoTracking()
            .Where(a => a.TargetId == ticketId.ToString()).ToListAsync();
        Assert.Contains(trail, a => a.Action == PlatformSupportTicketsController.Actions.Create);
        Assert.Contains(trail, a => a.Action == PlatformSupportTicketsController.Actions.Transition);
        Assert.Contains(trail, a => a.Action == SupportTicketRedactionService.AuditAction);
    }

    [Fact]
    public async Task A_redacted_ticket_refuses_every_further_mutation()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "redacted-frozen");
        var ticketId = await RaiseTicketAsync(db, tenantId);

        await using (var purge = db.ContextFor(null))
            await Redactor(purge).RedactForPurgedTenantAsync(Actor(), tenantId, "Purged");

        await using var context = db.ContextFor(null);
        Assert.IsType<ConflictObjectResult>((await Tickets(context).AddNote(ticketId,
            new AddSupportTicketNoteRequest { Body = "Late note" }, CancellationToken.None)).Result);
        Assert.IsType<ConflictObjectResult>((await Tickets(context).Transition(ticketId,
            new TransitionSupportTicketRequest { Status = nameof(SupportTicketStatus.Open), Reason = "Reopen" },
            CancellationToken.None)).Result);
    }

    [Fact]
    public async Task Redaction_is_idempotent_and_refuses_an_unexplained_erasure()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "idempotent-purge");
        await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Redactor(context).RedactForPurgedTenantAsync(Actor(), tenantId, "   "));

        Assert.Equal(1, (await Redactor(context).RedactForPurgedTenantAsync(Actor(), tenantId, "Purged")).TicketsRedacted);
        Assert.Equal(0, (await Redactor(context).RedactForPurgedTenantAsync(Actor(), tenantId, "Purged")).TicketsRedacted);
    }

    [Fact]
    public async Task A_tenant_delete_that_would_orphan_support_history_fails_loudly()
    {
        // RESTRICT, not CASCADE. A purge that silently vacuumed the support history would destroy
        // exactly the evidence a disputed offboarding needs, so the raw delete must not be quiet.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "restricted");
        await RaiseTicketAsync(db, tenantId);

        await using var context = db.ContextFor(null);
        var tenant = await context.Set<Tenant>().SingleAsync(t => t.Id == tenantId);
        context.Set<Tenant>().Remove(tenant);

        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }

    // ---- helpers -----------------------------------------------------------

    private static async Task<SupportTicketDetailDto> Move(
        PlatformSupportTestDb db, long ticketId, SupportTicketStatus target, string reason,
        string? resolution = null)
    {
        await using var context = db.ContextFor(null);
        return Ok(await Tickets(context).Transition(ticketId, new TransitionSupportTicketRequest
        {
            Status = target.ToString(), Reason = reason, Resolution = resolution
        }, CancellationToken.None));
    }
}
