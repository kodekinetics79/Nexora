using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Mvc;
using static ERP_RFQ_Automation.Tests.PlatformSupportFixture;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The one call a tenant's page makes. Two properties matter more than the shape of the payload:
/// that everything in it belongs to the tenant that was asked for, and that a suspended customer's
/// page still shows their support work.
/// </summary>
public sealed class PlatformSupportTenantOperationsTests
{
    [Fact]
    public async Task The_summary_contains_only_the_requested_tenants_data()
    {
        // The whole reason this endpoint exists instead of four fetches the console stitches
        // together is that four fetches are four chances to render a mixture of two customers.
        using var db = new PlatformSupportTestDb();
        var acme = await SeedTenantAsync(db, "acme-summary", name: "Acme Industrial");
        var initech = await SeedTenantAsync(db, "initech-summary", name: "Initech Holdings");

        await RaiseTicketAsync(db, acme, "Acme extraction stuck");
        await RaiseTicketAsync(db, initech, "Initech quote totals wrong");
        await RaiseTicketAsync(db, initech, "Initech cannot log in");

        await SeedAuditAsync(db, "tenant.suspend", acme, OwnerActorId);
        await SeedAuditAsync(db, "tenant.suspend", initech, OwnerActorId);
        await SeedAuditAsync(db, "plan.create", null, OwnerActorId);

        await SeedImpersonationAsync(db, acme, OwnerActorId, "Acme investigation");
        await SeedImpersonationAsync(db, initech, OwnerActorId, "Initech investigation");

        await using var context = db.ContextFor(null);
        var summary = Summary(await Operations(context).OperationsSummary(acme, CancellationToken.None));

        Assert.Equal(acme, summary.Lifecycle.TenantId);
        Assert.Equal("Acme Industrial", summary.Lifecycle.Name);

        Assert.Equal(1, summary.Support.OpenTicketCount);
        Assert.All(summary.Support.RecentTickets, t => Assert.Equal(acme, t.TenantId));
        Assert.DoesNotContain(summary.Support.RecentTickets, t => t.Subject.Contains("Initech"));

        Assert.All(summary.Audit.RecentEntries, e => Assert.Equal(acme, e.TenantId));
        // The platform-wide plan.create row belongs to nobody's page: acme's ticket create plus
        // the seeded suspend, and nothing else.
        Assert.Equal(2, summary.Audit.EntryCountLast30Days);

        Assert.Equal(1, summary.Impersonation.ActiveSessionCount);
        Assert.All(summary.Impersonation.Sessions, s => Assert.Equal("Acme investigation", s.Reason));
    }

    [Fact]
    public async Task A_suspended_tenants_page_shows_the_suspension_and_the_support_work()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "suspended-summary", TenantStatus.Suspended);
        var ticketId = await RaiseTicketAsync(db, tenantId, "Locked out since this morning", nameof(SupportTicketSeverity.Critical));

        await using (var working = db.ContextFor(null))
            await Tickets(working).Transition(ticketId, new TransitionSupportTicketRequest
            {
                Status = nameof(SupportTicketStatus.Open), Reason = "Picked up"
            }, CancellationToken.None);

        await using var context = db.ContextFor(null);
        var summary = Summary(await Operations(context).OperationsSummary(tenantId, CancellationToken.None));

        Assert.Equal(nameof(TenantStatus.Suspended), summary.Lifecycle.Status);
        Assert.Equal("Non-payment", summary.Lifecycle.StatusReason);
        Assert.Equal(1, summary.Support.OpenTicketCount);
        Assert.Equal(1, summary.Support.OpenBySeverity[nameof(SupportTicketSeverity.Critical)]);
        Assert.Equal(1, summary.Support.OpenByStatus[nameof(SupportTicketStatus.Open)]);
        Assert.Equal(1, summary.Support.UnassignedOpenTicketCount);
        Assert.NotNull(summary.Support.OldestOpenTicketCreatedAtUtc);
    }

    [Fact]
    public async Task Finished_tickets_leave_the_open_counts_but_stay_in_the_recent_list()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "counts");
        var closedId = await RaiseTicketAsync(db, tenantId, "Already handled");
        await RaiseTicketAsync(db, tenantId, "Still open");

        await using (var working = db.ContextFor(null))
            await Tickets(working).Transition(closedId, new TransitionSupportTicketRequest
            {
                Status = nameof(SupportTicketStatus.Closed), Reason = "Duplicate"
            }, CancellationToken.None);

        await using var context = db.ContextFor(null);
        var summary = Summary(await Operations(context).OperationsSummary(tenantId, CancellationToken.None));

        Assert.Equal(1, summary.Support.OpenTicketCount);
        Assert.Equal(2, summary.Support.RecentTickets.Count);
    }

    [Fact]
    public async Task An_impersonation_session_reports_the_ticket_that_authorised_it()
    {
        // "We were in their account at 14:02" and "we were in their account at 14:02 BECAUSE of
        // ticket 41" are completely different answers to give a customer. An empty list here is
        // itself the answer: nobody recorded why.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "attributed");
        var ticketId = await RaiseTicketAsync(db, tenantId);
        var explained = await SeedImpersonationAsync(db, tenantId, OwnerActorId, "Reproducing the failure");
        var unexplained = await SeedImpersonationAsync(db, tenantId, OwnerActorId, "Looking around");

        await using (var working = db.ContextFor(null))
            await Tickets(working).Link(ticketId, new LinkSupportTicketRequest
            {
                Kind = nameof(SupportTicketLinkKind.ImpersonationSession), TargetKey = explained
            }, CancellationToken.None);

        await using var context = db.ContextFor(null);
        var summary = Summary(await Operations(context).OperationsSummary(tenantId, CancellationToken.None));

        Assert.Equal([ticketId],
            Assert.Single(summary.Impersonation.Sessions, s => s.Jti == explained).LinkedTicketIds);
        Assert.Empty(Assert.Single(summary.Impersonation.Sessions, s => s.Jti == unexplained).LinkedTicketIds);
    }

    [Fact]
    public async Task A_revoked_or_expired_session_is_not_counted_as_active()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "session-states");
        var now = DateTime.UtcNow;
        await SeedImpersonationAsync(db, tenantId, OwnerActorId, "Live", now);
        await SeedImpersonationAsync(db, tenantId, OwnerActorId, "Expired",
            now.AddHours(-4), TimeSpan.FromMinutes(30));
        await SeedImpersonationAsync(db, tenantId, OwnerActorId, "Revoked",
            now.AddMinutes(-5), TimeSpan.FromMinutes(30), revokedAt: now.AddMinutes(-1));

        await using var context = db.ContextFor(null);
        var summary = Summary(await Operations(context).OperationsSummary(tenantId, CancellationToken.None));

        Assert.Equal(1, summary.Impersonation.ActiveSessionCount);
        Assert.Equal(3, summary.Impersonation.SessionCountLast30Days);
        Assert.Equal("active", Assert.Single(summary.Impersonation.Sessions, s => s.Reason == "Live").Status);
        Assert.Equal("expired", Assert.Single(summary.Impersonation.Sessions, s => s.Reason == "Expired").Status);
        Assert.Equal("revoked", Assert.Single(summary.Impersonation.Sessions, s => s.Reason == "Revoked").Status);
    }

    [Fact]
    public async Task Failed_privileged_actions_are_counted_separately()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "failures");
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId);
        await SeedAuditAsync(db, "impersonate.issue", tenantId, OwnerActorId,
            result: PlatformAuditResults.Failure);

        await using var context = db.ContextFor(null);
        var summary = Summary(await Operations(context).OperationsSummary(tenantId, CancellationToken.None));

        Assert.Equal(2, summary.Audit.EntryCountLast30Days);
        Assert.Equal(1, summary.Audit.FailureCountLast30Days);
        Assert.NotNull(summary.Audit.LastActionAtUtc);
    }

    [Fact]
    public async Task An_unknown_tenant_is_a_not_found()
    {
        using var db = new PlatformSupportTestDb();
        await using var context = db.ContextFor(null);

        var result = await Operations(context).OperationsSummary(8_888, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    private static TenantOperationsSummaryDto Summary(ActionResult<TenantOperationsSummaryDto> result)
        => Assert.IsType<TenantOperationsSummaryDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
