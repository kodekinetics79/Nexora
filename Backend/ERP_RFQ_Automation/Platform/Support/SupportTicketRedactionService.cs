using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Services;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Platform.Support;

/// <summary>
/// What happens to support history when a tenant is purged.
///
/// <para><b>The decision, and why.</b> Tickets are NOT deleted with the tenant. A ticket is the
/// record of what Nexora did for a customer — the same class of record as
/// <c>platform.PlatformAuditLogs</c> — and a purge that took it would destroy exactly the evidence
/// a disputed offboarding, a regulator, or an insurer asks for, at the one moment the relationship
/// has gone wrong. The tenant foreign key is therefore RESTRICT, so a plain
/// <c>DELETE FROM platform."Tenants"</c> fails loudly instead of silently vacuuming the history.</para>
///
/// <para><b>But erasure is real.</b> Ticket bodies, thread notes and requester addresses are the
/// most likely place in the whole control plane for a customer's personal data to be sitting, pasted
/// out of an email. A retention model that could not empty them would turn a contractual delete
/// obligation into a schema change made under time pressure. So the line is: the SHAPE of the
/// history survives — which tickets existed, when they were opened, who worked them, how they moved,
/// and the entire append-only audit trail of those moves — and the CONTENT is erased.</para>
///
/// <para><b>Why notes are deleted rather than blanked.</b> <c>platform.SupportTicketNotes</c> has
/// UPDATE revoked from <c>nexora_pipeline_app</c>, which is what makes a thread un-rewritable. That
/// same revocation means erasure cannot overwrite a note body, so it removes the rows and leaves one
/// <see cref="SupportTicketAuthorKind.System"/> tombstone recording how many were erased — a ticket
/// whose thread simply vanished would read as a ticket nobody ever worked.</para>
///
/// <para><b>Contract for the caller (tenant deletion).</b> Call this INSIDE the purge transaction
/// and BEFORE deleting anything else. It writes through the same request-scoped
/// <c>ErpRfqAutomationContext</c>, so its writes and the purge share one fate. It does not commit,
/// does not check tenant status, and is idempotent: a tenant whose tickets are already redacted
/// yields <see cref="SupportTicketRedactionResult.TicketsRedacted"/> = 0 and writes no audit rows.</para>
/// </summary>
public interface ISupportTicketRedactionService
{
    Task<SupportTicketRedactionResult> RedactForPurgedTenantAsync(
        ClaimsPrincipal actor, long tenantId, string reason, CancellationToken ct = default);
}

/// <summary>What a redaction pass touched. Returned so the purge can report it and assert on it.</summary>
public sealed record SupportTicketRedactionResult(int TicketsRedacted, int NotesErased);

/// <inheritdoc cref="ISupportTicketRedactionService"/>
public class SupportTicketRedactionService(
    ErpRfqAutomationContext context, IPlatformAuditService audit) : ISupportTicketRedactionService
{
    /// <summary>
    /// What a redacted subject reads as. A tombstone rather than an empty string, because a blank
    /// subject in a list looks like a data bug and sends someone investigating a deliberate act.
    /// </summary>
    public const string RedactedSubject = "[redacted — tenant purged]";

    /// <summary>The audit verb the purge trail carries, one row per ticket.</summary>
    public const string AuditAction = "support.ticket.redact";

    public async Task<SupportTicketRedactionResult> RedactForPurgedTenantAsync(
        ClaimsPrincipal actor, long tenantId, string reason, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException(
                "A reason is required: an erasure with no recorded cause cannot be justified later.",
                nameof(reason));

        var tickets = await context.Set<SupportTicket>()
            .Where(t => t.TenantId == tenantId && t.RedactedAtUtc == null)
            .ToListAsync(ct);
        if (tickets.Count == 0)
            return new SupportTicketRedactionResult(0, 0);

        var now = DateTime.UtcNow;
        var trimmedReason = reason.Trim();
        var ticketIds = tickets.Select(t => t.Id).ToArray();

        var notes = await context.Set<SupportTicketNote>()
            .Where(n => ticketIds.Contains(n.SupportTicketId))
            .ToListAsync(ct);
        var erasedByTicket = notes.GroupBy(n => n.SupportTicketId)
            .ToDictionary(g => g.Key, g => g.Count());
        context.Set<SupportTicketNote>().RemoveRange(notes);

        foreach (var ticket in tickets)
        {
            ticket.Subject = RedactedSubject;
            ticket.Body = null;
            ticket.Resolution = null;
            ticket.RequesterEmail = null;
            ticket.RequesterTenantUserId = null;
            ticket.RedactedAtUtc = now;
            ticket.RedactedReason = trimmedReason.Length > 200 ? trimmedReason[..200] : trimmedReason;
            ticket.UpdatedAtUtc = now;
            ticket.Version++;

            var erased = erasedByTicket.TryGetValue(ticket.Id, out var count) ? count : 0;
            if (erased > 0)
                context.Set<SupportTicketNote>().Add(new SupportTicketNote
                {
                    SupportTicketId = ticket.Id,
                    AuthorPlatformUserId = null,
                    AuthorKind = SupportTicketAuthorKind.System,
                    AuthorLabel = "nexora",
                    Body = $"{erased} thread {(erased == 1 ? "entry" : "entries")} erased when this " +
                           "tenant was purged. The lifecycle of this ticket remains in the platform audit log.",
                    IsInternal = true,
                    CreatedAtUtc = now
                });
        }

        await context.SaveChangesAsync(ct);

        // One audit row per ticket, not one for the batch. The audit log's unit is the thing that
        // changed, and "tenant 42's support history was erased" cannot later be used to establish
        // that ticket 118 in particular was among them.
        foreach (var ticket in tickets)
            await audit.WriteAsync(actor, AuditAction, PlatformSupportTicketsController.AuditTargetType,
                ticket.Id.ToString(),
                new
                {
                    reason = trimmedReason,
                    notesErased = erasedByTicket.TryGetValue(ticket.Id, out var count) ? count : 0,
                    status = ticket.Status.ToString()
                },
                actAsTenantId: tenantId, ct: ct);

        return new SupportTicketRedactionResult(tickets.Count, notes.Count);
    }
}
