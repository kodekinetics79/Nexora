using System.Text.Json;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Support;
using Microsoft.AspNetCore.Mvc;
using static ERP_RFQ_Automation.Tests.PlatformSupportFixture;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The four questions the audit explorer exists to answer — everything that happened to THIS
/// tenant, everything THIS operator did, every occurrence of an action inside a window, and what
/// actually changed in a privileged action — plus the boundary a reader must not be able to cross
/// on the way to answering them.
/// </summary>
public sealed class PlatformAuditExplorerTests
{
    // ---- the boundary ------------------------------------------------------

    [Fact]
    public async Task A_search_term_cannot_widen_a_tenant_filter_to_another_tenants_rows()
    {
        // The failure shape this pins: composing the free-text predicate as an OR alongside the
        // tenant predicate instead of an AND. The endpoint would then answer "Acme's audit trail"
        // with every row on the platform that happens to mention the search term — a cross-tenant
        // read produced by a search box.
        using var db = new PlatformSupportTestDb();
        var acme = await SeedTenantAsync(db, "acme", name: "Acme Industrial");
        var initech = await SeedTenantAsync(db, "initech", name: "Initech Holdings");

        await SeedAuditAsync(db, "tenant.suspend", acme, OwnerActorId);
        await SeedAuditAsync(db, "tenant.suspend", initech, OwnerActorId);
        await SeedAuditAsync(db, "tenant.archive", initech, OwnerActorId);
        await SeedAuditAsync(db, "plan.create", null, OwnerActorId);

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            tenantId: acme, actorPlatformUserId: null, action: null, actionPrefix: null,
            targetType: null, targetId: null, result: null, fromUtc: null, toUtc: null,
            search: "initech", page: 1, pageSize: 50, ct: CancellationToken.None));

        // Initech's name matches the term and its rows exist — and none of them come back.
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task Everything_that_happened_to_this_tenant_excludes_platform_wide_rows()
    {
        using var db = new PlatformSupportTestDb();
        var acme = await SeedTenantAsync(db, "acme-scope");
        var other = await SeedTenantAsync(db, "other-scope");

        await SeedAuditAsync(db, "tenant.suspend", acme, OwnerActorId);
        await SeedAuditAsync(db, "tenant.resume", acme, OwnerActorId);
        await SeedAuditAsync(db, "tenant.suspend", other, OwnerActorId);
        await SeedAuditAsync(db, "platform.login", null, OwnerActorId);

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            tenantId: acme, actorPlatformUserId: null, action: null, actionPrefix: null,
            targetType: null, targetId: null, result: null, fromUtc: null, toUtc: null,
            search: null, page: 1, pageSize: 50, ct: CancellationToken.None));

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, entry => Assert.Equal(acme, entry.TenantId));
    }

    [Fact]
    public async Task A_tenant_timeline_carries_only_that_tenants_records_from_all_three_registers()
    {
        using var db = new PlatformSupportTestDb();
        var acme = await SeedTenantAsync(db, "acme-timeline");
        var initech = await SeedTenantAsync(db, "initech-timeline");

        await SeedAuditAsync(db, "tenant.suspend", acme, OwnerActorId);
        await SeedAuditAsync(db, "tenant.suspend", initech, OwnerActorId);
        await SeedAuditAsync(db, "plan.update", null, OwnerActorId);
        await SeedImpersonationAsync(db, acme, OwnerActorId, "Acme login investigation");
        await SeedImpersonationAsync(db, initech, OwnerActorId, "Initech quote investigation");
        await RaiseTicketAsync(db, acme, "Acme cannot log in");
        await RaiseTicketAsync(db, initech, "Initech quote totals wrong");

        await using var context = db.ContextFor(null);
        var result = await Explorer(context).TenantTimeline(acme, null, null, 100, CancellationToken.None);
        var entries = Assert.IsType<List<TenantTimelineEntryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Contains(entries, e => e.Kind == "audit");
        Assert.Contains(entries, e => e.Kind == "impersonation");
        Assert.Contains(entries, e => e.Kind == "ticket");
        Assert.DoesNotContain(entries, e => (e.Summary ?? string.Empty).Contains("Initech"));
        Assert.DoesNotContain(entries, e => e.Action == "plan.update");

        // Descending by time, so the console renders it without re-sorting.
        Assert.Equal(entries.OrderByDescending(e => e.OccurredAtUtc).Select(e => e.Id), entries.Select(e => e.Id));
    }

    [Fact]
    public async Task A_timeline_for_a_tenant_that_does_not_exist_is_a_not_found_rather_than_an_empty_list()
    {
        // An empty list would tell a caller "this customer has had nothing done to them", which is
        // a materially different claim from "there is no such customer".
        using var db = new PlatformSupportTestDb();
        await using var context = db.ContextFor(null);

        var result = await Explorer(context).TenantTimeline(4_242, null, null, 100, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    // ---- the other three questions ----------------------------------------

    [Fact]
    public async Task Everything_this_operator_did_is_one_filter()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "actor-filter");
        var alice = await SeedOperatorAsync(db, "alice@example.test");
        var bob = await SeedOperatorAsync(db, "bob@example.test");

        await SeedAuditAsync(db, "tenant.suspend", tenantId, alice);
        await SeedAuditAsync(db, "tenant.resume", tenantId, alice);
        await SeedAuditAsync(db, "tenant.archive", tenantId, bob);

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            tenantId: null, actorPlatformUserId: alice, action: null, actionPrefix: null,
            targetType: null, targetId: null, result: null, fromUtc: null, toUtc: null,
            search: null, page: 1, pageSize: 50, ct: CancellationToken.None));

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, entry =>
        {
            Assert.Equal(alice, entry.ActorPlatformUserId);
            Assert.Equal("alice@example.test", entry.ActorEmail);
        });
    }

    [Fact]
    public async Task Every_action_of_a_given_type_within_a_window()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "window");
        var anchor = new DateTime(2026, 3, 15, 12, 0, 0, DateTimeKind.Utc);

        await SeedAuditAsync(db, "impersonate.issue", tenantId, OwnerActorId, at: anchor.AddDays(-40));
        await SeedAuditAsync(db, "impersonate.issue", tenantId, OwnerActorId, at: anchor.AddDays(-3));
        await SeedAuditAsync(db, "impersonate.issue", tenantId, OwnerActorId, at: anchor.AddDays(-1));
        await SeedAuditAsync(db, "impersonate.revoke", tenantId, OwnerActorId, at: anchor.AddDays(-1));

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            tenantId: null, actorPlatformUserId: null, action: ["impersonate.issue"], actionPrefix: null,
            targetType: null, targetId: null, result: null,
            fromUtc: anchor.AddDays(-7), toUtc: anchor,
            search: null, page: 1, pageSize: 50, ct: CancellationToken.None));

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, entry => Assert.Equal("impersonate.issue", entry.Action));
    }

    [Fact]
    public async Task An_action_prefix_selects_a_whole_verb_family()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "prefix");
        await RaiseTicketAsync(db, tenantId);
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId);

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            tenantId: null, actorPlatformUserId: null, action: null, actionPrefix: "support.ticket.",
            targetType: null, targetId: null, result: null, fromUtc: null, toUtc: null,
            search: null, page: 1, pageSize: 50, ct: CancellationToken.None));

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(PlatformSupportTicketsController.Actions.Create, page.Items[0].Action);
    }

    [Fact]
    public async Task Paging_is_stable_and_reports_a_real_total()
    {
        // Rows written inside one transaction routinely share a CreatedOn. Ordering on the
        // timestamp alone lets the database return them in a different order per page, silently
        // duplicating some rows and dropping others as an operator pages through an investigation.
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "paging");
        var sameInstant = DateTime.UtcNow;
        for (var i = 0; i < 25; i++)
            await SeedAuditAsync(db, "tenant.noise", tenantId, OwnerActorId, at: sameInstant);

        await using var context = db.ContextFor(null);
        var first = Page(await Explorer(context).Query(
            tenantId, null, null, null, null, null, null, null, null, null, 1, 10, CancellationToken.None));
        var second = Page(await Explorer(context).Query(
            tenantId, null, null, null, null, null, null, null, null, null, 2, 10, CancellationToken.None));
        var third = Page(await Explorer(context).Query(
            tenantId, null, null, null, null, null, null, null, null, null, 3, 10, CancellationToken.None));

        Assert.Equal(25, first.TotalCount);
        Assert.True(first.HasMore);
        Assert.False(third.HasMore);
        Assert.Equal(5, third.Items.Count);

        var ids = first.Items.Concat(second.Items).Concat(third.Items).Select(e => e.Id).ToList();
        Assert.Equal(25, ids.Distinct().Count());
    }

    [Fact]
    public async Task An_oversized_page_is_clamped_rather_than_honoured()
    {
        using var db = new PlatformSupportTestDb();
        await using var context = db.ContextFor(null);

        var page = Page(await Explorer(context).Query(
            null, null, null, null, null, null, null, null, null, null,
            page: 1, pageSize: 10_000, ct: CancellationToken.None));

        Assert.Equal(PlatformAuditExplorerController.MaxPageSize, page.PageSize);
    }

    [Fact]
    public async Task An_inverted_window_is_refused_rather_than_silently_returning_nothing()
    {
        using var db = new PlatformSupportTestDb();
        await using var context = db.ContextFor(null);

        var result = await Explorer(context).Query(
            null, null, null, null, null, null, null,
            fromUtc: DateTime.UtcNow, toUtc: DateTime.UtcNow.AddDays(-1),
            search: null, page: 1, pageSize: 50, ct: CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    // ---- what changed ------------------------------------------------------

    [Fact]
    public async Task A_before_after_record_is_decoded_into_field_level_changes()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "diffable");
        var row = await SeedAuditAsync(db, "plan.update", tenantId, OwnerActorId, metadata: """
            {"before":{"Code":"pro","MonthlyPriceUsd":499.00,"IsActive":true},
             "after":{"Code":"pro","MonthlyPriceUsd":599.00,"IsActive":false}}
            """);

        // An OWNER actor, because plan.update is written by an Owner-gated endpoint and payload
        // disclosure follows the writer's policy (PlatformAuditDisclosure). The decoding under test
        // here is the same for every tier; what differs is whether the values arrive at all, which
        // PlatformAuditExplorerDisclosureTests covers.
        await using var context = db.ContextFor(null);
        var detail = Assert.IsType<PlatformAuditEntryDetailDto>(
            Assert.IsType<OkObjectResult>((await Explorer(context, Owner()).Entry(row.Id, CancellationToken.None)).Result).Value);

        Assert.Equal(2, detail.Changes.Count);
        var price = Assert.Single(detail.Changes, c => c.Field == "MonthlyPriceUsd");
        Assert.Equal("499.00", price.Before);
        Assert.Equal("599.00", price.After);
        var active = Assert.Single(detail.Changes, c => c.Field == "IsActive");
        Assert.Equal("true", active.Before);
        Assert.Equal("false", active.After);

        // Unchanged fields are not reported as changes.
        Assert.DoesNotContain(detail.Changes, c => c.Field == "Code");
        Assert.NotNull(detail.Before);
        Assert.NotNull(detail.After);
    }

    [Fact]
    public async Task The_from_to_shape_every_lifecycle_transition_writes_is_decoded_too()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "from-to");
        var status = await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId,
            metadata: """{"from":"Active","to":"Suspended","reason":"Non-payment"}""");
        var plan = await SeedAuditAsync(db, "tenant.plan.change", tenantId, OwnerActorId,
            metadata: """{"fromPlanId":1,"toPlanId":3,"planCode":"scale","reason":"Upgrade"}""");

        // Owner: tenant.plan.change is the Sec9 BILLING verb, so its payload needs Platform.Billing.
        await using var context = db.ContextFor(null);

        var statusDetail = Detail(await Explorer(context, Owner()).Entry(status.Id, CancellationToken.None));
        var statusChange = Assert.Single(statusDetail.Changes);
        Assert.Equal("state", statusChange.Field);
        Assert.Equal("Active", statusChange.Before);
        Assert.Equal("Suspended", statusChange.After);

        var planDetail = Detail(await Explorer(context, Owner()).Entry(plan.Id, CancellationToken.None));
        var planChange = Assert.Single(planDetail.Changes);
        Assert.Equal("planId", planChange.Field);
        Assert.Equal("1", planChange.Before);
        Assert.Equal("3", planChange.After);
    }

    [Fact]
    public async Task A_record_with_no_recognisable_change_pair_reports_no_changes_rather_than_a_guess()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "opaque");
        var opaque = await SeedAuditAsync(db, "impersonate.issue", tenantId, OwnerActorId,
            metadata: """{"slug":"opaque","readOnly":true}""");
        var broken = await SeedAuditAsync(db, "legacy.action", tenantId, OwnerActorId,
            metadata: "not json at all");

        // Owner: "legacy.action" is in no disclosure table, so it fails closed to Owner — which is
        // the property that makes the R6 fix generalise to verbs that do not exist yet.
        await using var context = db.ContextFor(null);

        var opaqueDetail = Detail(await Explorer(context, Owner()).Entry(opaque.Id, CancellationToken.None));
        Assert.Empty(opaqueDetail.Changes);
        Assert.NotNull(opaqueDetail.Metadata);

        // A row whose metadata predates the jsonb column must not 500 the one endpoint an operator
        // opened to investigate a problem.
        var brokenDetail = Detail(await Explorer(context, Owner()).Entry(broken.Id, CancellationToken.None));
        Assert.Empty(brokenDetail.Changes);
        Assert.Null(brokenDetail.Metadata);
    }

    [Fact]
    public async Task Metadata_travels_as_structured_json_not_as_a_string_containing_json()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "structured");
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId,
            metadata: """{"from":"Active","to":"Suspended"}""");

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            tenantId, null, null, null, null, null, null, null, null, null, 1, 50, CancellationToken.None));

        var metadata = Assert.Single(page.Items).Metadata;
        Assert.NotNull(metadata);
        Assert.Equal(JsonValueKind.Object, metadata!.Value.ValueKind);
        Assert.Equal("Suspended", metadata.Value.GetProperty("to").GetString());
    }

    // ---- vocabulary --------------------------------------------------------

    [Fact]
    public async Task The_action_vocabulary_is_read_from_the_log_rather_than_hard_coded()
    {
        using var db = new PlatformSupportTestDb();
        var tenantId = await SeedTenantAsync(db, "vocabulary");
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId);
        await SeedAuditAsync(db, "tenant.suspend", tenantId, OwnerActorId);
        await SeedAuditAsync(db, "impersonate.issue", tenantId, OwnerActorId);

        await using var context = db.ContextFor(null);
        var result = await Explorer(context).Actions(tenantId, null, CancellationToken.None);
        var actions = Assert.IsAssignableFrom<IEnumerable<PlatformAuditActionDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value).ToList();

        Assert.Equal(2, actions.Count);
        Assert.Equal(2, Assert.Single(actions, a => a.Action == "tenant.suspend").Count);
        Assert.Equal(1, Assert.Single(actions, a => a.Action == "impersonate.issue").Count);
    }

    [Fact]
    public async Task The_reserved_system_actor_renders_as_system_and_an_unknown_actor_as_its_id()
    {
        using var db = new PlatformSupportTestDb();
        await SeedAuditAsync(db, "platform.login.failed", null, 0, result: PlatformAuditResults.Failure);
        await SeedAuditAsync(db, "tenant.suspend", null, 991);

        await using var context = db.ContextFor(null);
        var page = Page(await Explorer(context).Query(
            null, null, null, null, null, null, null, null, null, null, 1, 50, CancellationToken.None));

        Assert.Equal("system", Assert.Single(page.Items, e => e.ActorPlatformUserId == 0).Actor);
        Assert.Equal("Platform user 991", Assert.Single(page.Items, e => e.ActorPlatformUserId == 991).Actor);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>
    /// An Owner principal. Used by the tests that assert on decoded PAYLOADS, because payload
    /// disclosure follows the writing endpoint's policy and Owner satisfies every one of them.
    /// </summary>
    private static System.Security.Claims.ClaimsPrincipal Owner()
        => Actor(role: PlatformRole.Owner);

    private static PagedResultDto<PlatformAuditEntryDto> Page(
        ActionResult<PagedResultDto<PlatformAuditEntryDto>> result)
        => Assert.IsType<PagedResultDto<PlatformAuditEntryDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value);

    private static PlatformAuditEntryDetailDto Detail(ActionResult<PlatformAuditEntryDetailDto> result)
        => Assert.IsType<PlatformAuditEntryDetailDto>(
            Assert.IsType<OkObjectResult>(result.Result).Value);
}
