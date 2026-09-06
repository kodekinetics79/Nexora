using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Ingestion.CanonicalRecord;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The reads that reach a lead by a route other than <c>api/leads/{id}</c>, held to the same
/// answer. <c>CommercialAccessScopeTests</c> pins the row filter itself; this file pins that the
/// intake surface actually applies it, because a scope nothing calls protects nothing.
///
/// <para>Four endpoints answered a lead id on the tenant predicate alone, while
/// <c>LeadController.GetLeadById</c> and <c>ProcessingEvidenceController</c> answered 404 for the
/// same id: the canonical intake record (the whole extracted RFQ — header, lines, evidence spans,
/// audit trail), the revision history (the before/after JSON of every changed field), and the
/// accepted-lead queue's list and detail (line items and attachments). A tenant with two reps got
/// one rep's whole pipeline out of any of them.</para>
///
/// <para>The scope is resolved by the REAL <see cref="CommercialAccessContext"/> over the REAL
/// <see cref="AccountTeamScopeResolver"/> and the real filters: a stub that answered "no" would
/// prove only that a stub can say no. What each test asserts, beyond the status code, is that the
/// out-of-scope row never reached the caller.</para>
///
/// <para>The unassigned lead is here for the opposite reason. Refusing it would be a bug: an
/// owner-less inquiry is deliberately visible tenant-wide, because the routing queue is where a
/// rep claims one (<c>CommercialAccessFilters</c>). That refusal-shaped ALLOWANCE is pinned too.</para>
/// </summary>
public sealed class IntakeReadCommercialScopeTests
{
    private const long Bu = 94_200;
    private const long RepA = 94_201;
    private const long RepB = 94_202;
    private const long AdminUser = 94_203;

    private const long MemberRole = 1;
    private const long AdminRole = 2;

    private const long RepALead = 94_401;
    private const long RepBLead = 94_412;
    private const long UnassignedLead = 94_477;

    // ---- GET api/intake-records/by-lead/{leadId} ------------------------------------------

    [Fact]
    public async Task The_canonical_intake_record_of_another_reps_lead_is_not_readable()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = IntakeRecords(db, RepA, MemberRole);

        var result = await controller.ByLead(RepBLead, default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task A_rep_can_still_read_the_canonical_intake_record_of_their_own_lead()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = IntakeRecords(db, RepA, MemberRole);

        var result = await controller.ByLead(RepALead, default);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var record = Assert.IsType<CanonicalIntakeRecord>(ok.Value);
        Assert.Equal(RepALead, record.Header?.LeadId);
    }

    /// <summary>
    /// The same record is reachable by the MESSAGE id, which is a small integer a caller can count
    /// through. Guarding only the by-lead route would leave the same payload one route away.
    /// </summary>
    [Fact]
    public async Task The_canonical_intake_record_is_not_reachable_by_the_email_ingest_id_either()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = IntakeRecords(db, RepA, MemberRole);

        // Proven reachable first, so the refusal below cannot be an accident of the fixture.
        var asAdmin = await IntakeRecords(database.ContextFor(Bu), AdminUser, AdminRole)
            .ByEmailIngest(IngestIdOf(RepBLead), default);
        var visible = Assert.IsType<CanonicalIntakeRecord>(Assert.IsType<OkObjectResult>(asAdmin.Result).Value);
        Assert.Equal(RepBLead, visible.Header?.LeadId);

        var result = await controller.ByEmailIngest(IngestIdOf(RepBLead), default);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- GET api/LeadIngestion/leads/{leadId}/revisions -----------------------------------

    [Fact]
    public async Task The_revision_history_of_another_reps_lead_is_not_readable()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = LeadIngestion(db, RepA, MemberRole);

        Assert.IsType<NotFoundResult>(await controller.Revisions(RepBLead, default));
        Assert.IsType<OkObjectResult>(await controller.Revisions(RepALead, default));
    }

    // ---- GET api/UnAssignedLead/{id} ------------------------------------------------------

    [Fact]
    public async Task Another_reps_accepted_lead_does_not_open_with_its_line_items()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, RepA, MemberRole);

        var result = await controller.GetAcceptedLeadById(RepBLead);

        var notFound = Assert.IsType<NotFoundObjectResult>(result.Result);
        Assert.DoesNotContain("Rival", notFound.Value?.ToString() ?? string.Empty);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task A_rep_can_still_open_their_own_accepted_lead()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, RepA, MemberRole);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetAcceptedLeadById(RepALead)).Result);
        Assert.Equal(RepALead, Assert.IsType<AcceptedLeadResponseDTO>(ok.Value).Id);
    }

    /// <summary>
    /// THE ALLOWANCE, which is as deliberate as the refusal. An unassigned inquiry belongs to the
    /// governed routing queue and any rep who might claim it has to be able to open it; scoping
    /// this endpoint to owners would empty the screen it exists to serve.
    /// </summary>
    [Fact]
    public async Task An_unassigned_lead_still_opens_for_any_rep_because_that_is_the_routing_queue()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, RepA, MemberRole);

        var ok = Assert.IsType<OkObjectResult>((await controller.GetAcceptedLeadById(UnassignedLead)).Result);
        Assert.Equal(UnassignedLead, Assert.IsType<AcceptedLeadResponseDTO>(ok.Value).Id);
    }

    // ---- GET api/UnAssignedLead/assigned and api/UnAssignedLead ---------------------------

    [Fact]
    public async Task The_assigned_queue_does_not_page_through_another_reps_pipeline()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, RepA, MemberRole);

        var page = await Page(controller.GetAssignedLeads(null, 1, 1000, null, null, null, null));

        Assert.Equal([RepALead], page.Items.Select(x => x.Id).ToArray());
        Assert.Equal(1, page.TotalCount);
    }

    /// <summary>
    /// The list guard lives in the query, not in the action, so the sibling endpoint that reaches
    /// the same repository method with the same flags cannot be used to walk around it. The
    /// unassigned rows stay — this endpoint IS the routing queue.
    /// </summary>
    [Fact]
    public async Task The_outstanding_queue_shows_unassigned_work_and_the_reps_own_but_not_a_rivals()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, RepA, MemberRole);

        var page = await Page(controller.GetAcceptedLeads(
            null, 1, 1000, null, null, null, null, excludeAssigned: false, onlyAssigned: false));

        Assert.Equal([RepALead, UnassignedLead], page.Items.Select(x => x.Id).Order().ToArray());
        Assert.DoesNotContain(page.Items, x => x.BuyersName == "Rival Buyer");
    }

    /// <summary>A forged owner filter cannot widen the scope: it can only narrow it, and naming
    /// somebody else returns nothing rather than their queue.</summary>
    [Fact]
    public async Task Filtering_the_queue_by_another_reps_id_returns_nothing()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, RepA, MemberRole);

        var page = await Page(controller.GetAssignedLeads(null, 1, 1000, RepB, null, null, null));

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    /// <summary>An administrator holds the tenant plane, and must still see all of it — otherwise
    /// the fix has traded a leak for a blind spot.</summary>
    [Fact]
    public async Task An_administrator_still_sees_the_whole_tenant()
    {
        using var database = new TestDb();
        await SeedAsync(database);
        await using var db = database.ContextFor(Bu);
        var controller = UnassignedLeads(db, AdminUser, AdminRole);

        var page = await Page(controller.GetAcceptedLeads(
            null, 1, 1000, null, null, null, null, excludeAssigned: false, onlyAssigned: false));

        Assert.Equal([RepALead, RepBLead, UnassignedLead], page.Items.Select(x => x.Id).Order().ToArray());
        Assert.IsType<OkObjectResult>((await controller.GetAcceptedLeadById(RepBLead)).Result);
    }

    // ---- harness ------------------------------------------------------------------------

    private static async Task<PaginatedResponseDTO<AcceptedLeadResponseDTO>> Page(
        Task<ActionResult<PaginatedResponseDTO<AcceptedLeadResponseDTO>>> action)
    {
        var ok = Assert.IsType<OkObjectResult>((await action).Result);
        return Assert.IsType<PaginatedResponseDTO<AcceptedLeadResponseDTO>>(ok.Value);
    }

    /// <summary>The EmailIngest id <see cref="Seed.Lead"/> links a lead to.</summary>
    private static long IngestIdOf(long leadId) => 20_000 + leadId;

    private static IntakeRecordsController IntakeRecords(
        ErpRfqAutomationContext db, long userId, long roleId) =>
        new(new CanonicalIntakeRecordService(db), AccessFor(db, userId, roleId, out var http))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

    private static LeadIngestionController LeadIngestion(
        ErpRfqAutomationContext db, long userId, long roleId) =>
        new(new LeadIdentityApplicationService(db), null!, AccessFor(db, userId, roleId, out var http),
            NullLogger<LeadIngestionController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

    private static UnAssignedLeadController UnassignedLeads(
        ErpRfqAutomationContext db, long userId, long roleId) =>
        new(new LeadRepository(db), null!, AccessFor(db, userId, roleId, out var http))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

    /// <summary>The production access plane — real resolver, real filters — over the caller's own
    /// claims. Only the role RANK is stubbed, which is what a Roles row would supply.</summary>
    private static ICommercialAccessContext AccessFor(
        ErpRfqAutomationContext db, long userId, long roleId, out HttpContext http)
    {
        http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("businessUnitId", Bu.ToString()),
                new Claim("roleId", roleId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
            ], "intake-scope-test"))
        };
        // Deliberately NOT HttpContextAccessor: its backing store is a static AsyncLocal, so two
        // controllers built in one test would share whichever principal was set last — and the
        // rep would quietly resolve as the administrator this file compares them against.
        return new CommercialAccessContext(
            new FixedHttpContext(http), new AccountTeamScopeResolver(db, new RankByRoleIdGate()), db);
    }

    /// <summary>Two leads with an owner apiece and one still in the routing queue, all qualified
    /// and none converted — the state the accepted-lead queue is written against.</summary>
    private static async Task SeedAsync(TestDb database)
    {
        await using var ctx = database.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Bu);
        Seed.LeadStatus(ctx, 24, Bu, "Qualified");
        ctx.Users.AddRange(User(RepA, "rep-a"), User(RepB, "rep-b"), User(AdminUser, "admin"));

        var mine = Seed.Lead(ctx, RepALead, Bu, leadStatusId: 24, buyersName: "Own Buyer");
        mine.AssignTo = RepA;
        var theirs = Seed.Lead(ctx, RepBLead, Bu, leadStatusId: 24, buyersName: "Rival Buyer");
        theirs.AssignTo = RepB;
        var queued = Seed.Lead(ctx, UnassignedLead, Bu, leadStatusId: 24, buyersName: "Queued Buyer");
        queued.AssignTo = null;

        await ctx.SaveChangesAsync();
    }

    private static User User(long id, string name) => new()
    {
        Id = id,
        FirstName = "Scope",
        LastName = name,
        Email = $"{name}@tenant.test",
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        Buid = Bu,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    /// <summary>One request's context, held per instance rather than per async flow.</summary>
    private sealed class FixedHttpContext(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    /// <summary>Rank by role id: <see cref="MemberRole"/> is a plain rep, <see cref="AdminRole"/>
    /// holds the tenant plane. The resolver reads nothing else from the gate.</summary>
    private sealed class RankByRoleIdGate : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) =>
            Task.FromResult(roleId == AdminRole);

        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) =>
            Task.FromResult(roleId == AdminRole ? RoleRanks.Admin : RoleRanks.Member);

        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) =>
            Task.FromResult(roleId == AdminRole);

        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) =>
            Task.FromResult(callerRoleId == AdminRole);
    }
}
