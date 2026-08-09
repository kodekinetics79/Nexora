using ERP_RFQ_Automation.CommercialIntelligence.Exceptions;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.DTOs.AuthDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialExceptionApplicationServiceTests
{
    private const long TenantId = 9_101;
    private const long OwnerUserId = 9_201;
    private const long OtherUserId = 9_202;

    [Fact]
    public async Task Refresh_detects_supported_rules_deterministically_and_deduplicates_replay()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var sources = await SeedSourcesAsync(context);
        var service = Service(context, TenantId);

        var first = await service.RefreshAsync(TenantId, Refresh("detect-1"), default);
        var replay = await service.RefreshAsync(TenantId, Refresh("detect-1"), default);

        Assert.Equal(2, first.Detected);
        Assert.Equal(0, first.Reopened);
        Assert.Equal(0, first.Resolved);
        Assert.Equal(first, replay);
        var rows = await context.CommercialExceptionCases.OrderBy(x => x.ExceptionType).ToArrayAsync();
        Assert.Equal(2, rows.Length);
        Assert.Contains(rows, x => x.ExceptionType == CommercialExceptionType.UnassignedLead
            && x.UnassignedWorkItemId == sources.WorkItem.Id);
        Assert.Contains(rows, x => x.ExceptionType == CommercialExceptionType.OverdueFollowUp
            && x.FollowUpTaskId == sources.FollowUp.Id && x.OwnerUserId == OwnerUserId);
        Assert.All(rows, x => Assert.Equal("commercial-exceptions-v1", x.RuleVersion));
        Assert.Equal(2, rows.Select(x => x.ExceptionKey).Distinct().Count());
        Assert.Single(await context.CommercialExceptionOperations.ToArrayAsync());
    }

    [Fact]
    public async Task Empty_refresh_persists_and_replays_exact_receipt_when_correlation_changes()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var service = Service(context, TenantId);
        var firstCommand = new RefreshCommercialExceptionsCommand("transport-one", "empty-refresh", "user-9201");
        var retryCommand = firstCommand with { CorrelationId = "transport-two" };

        var first = await service.RefreshAsync(TenantId, firstCommand, default);
        var replay = await service.RefreshAsync(TenantId, retryCommand, default);

        Assert.Equal(first, replay);
        Assert.Equal(0, first.Detected);
        var operation = Assert.Single(await context.CommercialExceptionOperations.AsNoTracking().ToArrayAsync());
        Assert.Equal("Refresh", operation.OperationType);
        Assert.Equal("transport-one", operation.CorrelationId);
        Assert.Contains("\"reconciledAtUtc\"", operation.ResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_op_refresh_keeps_case_versions_and_events_stable()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("no-op-initial"), default);
        var before = await context.CommercialExceptionCases.AsNoTracking()
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version }).ToArrayAsync();

        var result = await service.RefreshAsync(TenantId, Refresh("no-op-second"), default);

        var after = await context.CommercialExceptionCases.AsNoTracking()
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version }).ToArrayAsync();
        Assert.Equal(before, after);
        Assert.Equal(2, result.Refreshed);
        Assert.Equal(2, await context.CommercialExceptionEvents.CountAsync());
        Assert.Equal(2, await context.CommercialExceptionOutboxMessages.CountAsync());
        Assert.Equal(2, await context.CommercialExceptionOperations.CountAsync());
    }

    [Fact]
    public async Task Refresh_reopens_active_source_and_auto_resolves_source_that_is_no_longer_active()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var sources = await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("initial"), default);
        var exceptions = await context.CommercialExceptionCases.OrderBy(x => x.ExceptionType).ToArrayAsync();
        var unassigned = exceptions.Single(x => x.ExceptionType == CommercialExceptionType.UnassignedLead);
        var followUp = exceptions.Single(x => x.ExceptionType == CommercialExceptionType.OverdueFollowUp);
        unassigned.Status = CommercialExceptionStatus.Resolved;
        unassigned.ResolvedAtUtc = DateTime.UtcNow;
        var trackedFollowUp = await context.FollowUpTasks.SingleAsync(x => x.Id == sources.FollowUp.Id);
        trackedFollowUp.Status = FollowUpStatus.Completed;
        trackedFollowUp.Version++;
        await context.SaveChangesAsync();

        var result = await service.RefreshAsync(TenantId, Refresh("reconcile"), default);

        Assert.Equal(1, result.Reopened);
        Assert.Equal(1, result.Resolved);
        context.ChangeTracker.Clear();
        Assert.Equal(CommercialExceptionStatus.Open,
            (await context.CommercialExceptionCases.SingleAsync(x => x.Id == unassigned.Id)).Status);
        var resolved = await context.CommercialExceptionCases.SingleAsync(x => x.Id == followUp.Id);
        Assert.Equal(CommercialExceptionStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedAtUtc);
    }

    [Fact]
    public async Task Transition_appends_one_event_and_atomic_outbox_and_replays_without_side_effects()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-transition"), default);
        var exception = await context.CommercialExceptionCases
            .SingleAsync(x => x.ExceptionType == CommercialExceptionType.OverdueFollowUp);
        var command = Transition(exception.Version, CommercialExceptionStatus.Acknowledged, "ACKNOWLEDGE", "Reviewed", "decision-1");

        var first = await service.TransitionAsync(
            TenantId, exception.Id, command, CommercialExceptionAccessScope.ForOwner(OwnerUserId), default);
        var replay = await service.TransitionAsync(
            TenantId, exception.Id, command, CommercialExceptionAccessScope.ForOwner(OwnerUserId), default);

        Assert.Equal(first, replay);
        var occurrence = await context.CommercialExceptionEvents
            .SingleAsync(x => x.IdempotencyKey == command.IdempotencyKey);
        var outbox = await context.CommercialExceptionOutboxMessages
            .SingleAsync(x => x.CommercialExceptionEventId == occurrence.Id);
        Assert.Equal(CommercialExceptionStatus.Open, occurrence.FromStatus);
        Assert.Equal(CommercialExceptionStatus.Acknowledged, occurrence.ToStatus);
        Assert.Equal(occurrence.Id, outbox.CommercialExceptionEventId);
        Assert.Contains(exception.NexoraSerial, outbox.Payload, StringComparison.Ordinal);
        Assert.Equal(3, await context.CommercialExceptionEvents.CountAsync());
        Assert.Equal(3, await context.CommercialExceptionOutboxMessages.CountAsync());
        Assert.Equal(2, await context.CommercialExceptionOperations.CountAsync());
    }

    [Fact]
    public async Task Transition_replay_returns_frozen_result_after_a_later_state_change()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-frozen"), default);
        var exception = await context.CommercialExceptionCases
            .SingleAsync(x => x.ExceptionType == CommercialExceptionType.OverdueFollowUp);
        var acknowledge = Transition(exception.Version, CommercialExceptionStatus.Acknowledged,
            "ACKNOWLEDGE", "Reviewed", "frozen-ack");
        var acknowledged = await service.TransitionAsync(
            TenantId, exception.Id, acknowledge, CommercialExceptionAccessScope.ForTenant(), default);
        var resolved = await service.TransitionAsync(
            TenantId, exception.Id,
            Transition(acknowledged.Version, CommercialExceptionStatus.Resolved,
                "RESOLVE", "Completed", "frozen-resolve"),
            CommercialExceptionAccessScope.ForTenant(), default);

        var replay = await service.TransitionAsync(
            TenantId, exception.Id, acknowledge with { CorrelationId = "different-transport" },
            CommercialExceptionAccessScope.ForTenant(), default);

        Assert.Equal(CommercialExceptionStatus.Resolved, resolved.Status);
        Assert.Equal(acknowledged, replay);
        Assert.Equal(CommercialExceptionStatus.Acknowledged, replay.Status);
        Assert.True(replay.Version < resolved.Version);
    }

    [Fact]
    public async Task Transition_rejects_same_idempotency_key_with_a_different_request_hash()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-hash"), default);
        var exception = await context.CommercialExceptionCases.FirstAsync();
        await service.TransitionAsync(TenantId, exception.Id,
            Transition(1, CommercialExceptionStatus.Acknowledged, "ACKNOWLEDGE", "Reviewed", "same-key"),
            CommercialExceptionAccessScope.ForTenant(), default);

        await Assert.ThrowsAsync<CommercialExceptionConflictException>(() => service.TransitionAsync(
            TenantId, exception.Id,
            Transition(2, CommercialExceptionStatus.Resolved, "RESOLVE", "Source handled", "same-key"),
            CommercialExceptionAccessScope.ForTenant(), default));
        Assert.Equal(3, await context.CommercialExceptionEvents.CountAsync());
        Assert.Equal(3, await context.CommercialExceptionOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Transition_rejects_action_code_that_contradicts_target_status()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-action-validation"), default);
        var exception = await context.CommercialExceptionCases.FirstAsync();

        var error = await Assert.ThrowsAsync<ArgumentException>(() => service.TransitionAsync(
            TenantId,
            exception.Id,
            Transition(exception.Version, CommercialExceptionStatus.Dismissed,
                "APPROVE", "Not commercially actionable", "bad-action"),
            CommercialExceptionAccessScope.ForTenant(),
            default));

        Assert.Contains("ActionCode must be DISMISS", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, await context.CommercialExceptionEvents.CountAsync());
        Assert.Equal(2, await context.CommercialExceptionOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Transition_rejects_stale_version_without_event_or_outbox()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-stale"), default);
        var exception = await context.CommercialExceptionCases.FirstAsync();
        await service.RefreshAsync(TenantId, Refresh("advance-stale"), default);

        await Assert.ThrowsAsync<CommercialExceptionConflictException>(() => service.TransitionAsync(
            TenantId, exception.Id,
            Transition(exception.Version - 1, CommercialExceptionStatus.Acknowledged,
                "ACKNOWLEDGE", "Stale review", "stale-key"),
            CommercialExceptionAccessScope.ForTenant(), default));
        Assert.Equal(2, await context.CommercialExceptionEvents.CountAsync());
        Assert.Equal(2, await context.CommercialExceptionOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task Query_scopes_individuals_to_owned_follow_ups_while_manager_sees_tenant_queue()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-scope"), default);

        var individual = await service.QueryAsync(
            TenantId, new CommercialExceptionQuery(), CommercialExceptionAccessScope.ForOwner(OwnerUserId), default);
        var other = await service.QueryAsync(
            TenantId, new CommercialExceptionQuery(), CommercialExceptionAccessScope.ForOwner(OtherUserId), default);
        var manager = await service.QueryAsync(
            TenantId, new CommercialExceptionQuery(), CommercialExceptionAccessScope.ForTenant(), default);

        var owned = Assert.Single(individual.Items);
        Assert.Equal(CommercialExceptionType.OverdueFollowUp, owned.ExceptionType);
        Assert.Equal(OwnerUserId, owned.OwnerUserId);
        Assert.Empty(other.Items);
        Assert.Equal(2, manager.Total);
        Assert.Equal(2, manager.Active);
        Assert.Equal(2, manager.Items.Count);
        Assert.Equal("tenant", manager.Scope, ignoreCase: true);
        Assert.Equal("complete", manager.CoverageStatus, ignoreCase: true);
        Assert.All(manager.SourceCoverage, source => Assert.True(source.IsAvailable));
        Assert.All(manager.Items, item => Assert.True(item.SourceVersion >= 1));
        Assert.Equal(CommercialExceptionApplicationService.RuleVersion, manager.RuleVersion);
    }

    [Fact]
    public async Task Overdue_filter_excludes_terminal_records_and_matches_overdue_kpi_semantics()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        await SeedSourcesAsync(context);
        var service = Service(context, TenantId);
        await service.RefreshAsync(TenantId, Refresh("seed-overdue-filter"), default);
        var exception = await context.CommercialExceptionCases
            .SingleAsync(x => x.ExceptionType == CommercialExceptionType.OverdueFollowUp);
        await service.TransitionAsync(
            TenantId,
            exception.Id,
            Transition(exception.Version, CommercialExceptionStatus.Resolved,
                "RESOLVE", "Follow-up completed", "resolve-overdue"),
            CommercialExceptionAccessScope.ForTenant(),
            default);

        var result = await service.QueryAsync(
            TenantId,
            new CommercialExceptionQuery(OverdueOnly: true),
            CommercialExceptionAccessScope.ForTenant(),
            default);

        Assert.DoesNotContain(result.Items, x => x.Status is CommercialExceptionStatus.Resolved or CommercialExceptionStatus.Dismissed);
        Assert.Equal(result.Overdue, result.Total);
        Assert.All(result.Items, x => Assert.True(x.IsOverdue));
    }

    [Fact]
    public async Task Refresh_detects_overdue_order_follow_up_with_canonical_lineage()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        var lead = Seed.Lead(context, 9_401, TenantId);
        var customer = Seed.Customer(context, 9_402, TenantId, "Order Customer");
        var status = new SetupMaster
        {
            SetupId = 9_403,
            SetupType = "OrderStatus",
            SetupCode = "OPEN",
            SetupValue = "Open",
            BusinessUnitId = TenantId,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(status);
        context.Users.Add(User(OwnerUserId, "OrderOwner"));
        await context.SaveChangesAsync();
        var order = new Order
        {
            Id = 9_404,
            OrderNo = "ORDER-9404",
            LeadId = lead.Id,
            CustomerId = customer.Id,
            BusinessUnitId = TenantId,
            StatusId = status.SetupId,
            OrderDate = DateTime.UtcNow.AddDays(-1),
            TotalAmount = 100m,
            PaidAmount = 0m,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow.AddDays(-1),
            IsActive = true
        };
        order.InheritCommercialIdentity(lead);
        context.Orders.Add(order);
        context.FollowUpTasks.Add(new FollowUpTask
        {
            BusinessUnitId = TenantId,
            AssignedToUserId = OwnerUserId,
            AggregateType = CommercialAggregateType.Order,
            AggregateId = order.Id,
            CustomerId = customer.Id,
            DueAtUtc = DateTime.UtcNow.AddHours(-1),
            Status = FollowUpStatus.Open,
            Priority = 70,
            PurposeCode = "ORDER_CONFIRMATION",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Version = 1,
            CreatedBy = "tests",
            CorrelationId = "order-follow-up",
            CreationIdempotencyKey = "order-follow-up"
        });
        await context.SaveChangesAsync();

        var result = await Service(context, TenantId)
            .RefreshAsync(TenantId, Refresh("order-follow-up"), default);

        Assert.Equal(1, result.Detected);
        var exception = Assert.Single(await context.CommercialExceptionCases.AsNoTracking().ToArrayAsync());
        Assert.Contains("Order follow-up", exception.Summary, StringComparison.Ordinal);
        Assert.Equal(lead.CommercialCaseReference, exception.NexoraSerial);
    }

    [Fact]
    public async Task Login_response_exposes_role_gate_aligned_manager_and_super_admin_capabilities()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        Seed.EnsureBusinessUnit(context, TenantId);
        var role = new SetupMaster
        {
            SetupId = 9_501,
            SetupType = "Role",
            SetupCode = "SUPER_ADMINISTRATOR",
            SetupValue = "Super Administrator",
            // The NAME no longer confers anything — the rank column does.
            RoleRank = ERP_RFQ_Automation.Authorization.RoleRanks.Owner,
            BusinessUnitId = TenantId,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(role);
        context.Users.Add(new User
        {
            Id = 9_502,
            Buid = TenantId,
            RoleId = role.SetupId,
            FirstName = "Platform",
            LastName = "Owner",
            Email = "platform-owner@nexora.invalid",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("Nexora-Test-Password"),
            ImageUrl = "n/a",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-only-jwt-key-with-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "nexora-tests",
            ["Jwt:ExpiryMinutes"] = "30"
        }).Build();
        var repository = new AuthRepository(context, configuration, NullLogger<AuthRepository>.Instance);

        var response = await repository.LoginAsync(new LoginRequestDTO
        {
            Email = "platform-owner@nexora.invalid",
            Password = "Nexora-Test-Password"
        });

        Assert.True(response.IsSuperAdmin);
        Assert.True(response.IsManager);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(9102L)]
    public async Task Service_rejects_missing_or_mismatched_tenant_context(long? authenticatedTenant)
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(authenticatedTenant);
        var service = Service(context, authenticatedTenant);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.QueryAsync(
            TenantId, new CommercialExceptionQuery(), CommercialExceptionAccessScope.ForTenant(), default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.RefreshAsync(
            TenantId, Refresh($"tenant-{authenticatedTenant}"), default));
    }

    private static CommercialExceptionApplicationService Service(
        ErpRfqAutomationContext context,
        long? tenantId) => new(context, new StubTenant(tenantId));

    private static RefreshCommercialExceptionsCommand Refresh(string key) =>
        new($"corr-{key}", key, "user-9201");

    private static TransitionCommercialExceptionCommand Transition(
        long version,
        CommercialExceptionStatus status,
        string action,
        string reason,
        string key) => new(version, status, action, reason, $"corr-{key}", key, "user-9201");

    private static async Task<(FollowUpTask FollowUp, UnassignedWorkItem WorkItem)> SeedSourcesAsync(
        ErpRfqAutomationContext context)
    {
        var lead = Seed.Lead(context, 9_301, TenantId);
        context.Users.AddRange(User(OwnerUserId, "Owner"), User(OtherUserId, "Other"));
        await context.SaveChangesAsync();

        var decision = new LeadRoutingDecision
        {
            BusinessUnitId = TenantId,
            LeadId = lead.Id,
            MatchStatus = CustomerMatchStatus.NoEvidence,
            Outcome = RoutingOutcome.Unassigned,
            MatchConfidence = 0m,
            DecisionCode = "NO_OWNER",
            Explanation = "No deterministic owner was found.",
            PolicyVersion = "routing-v1",
            CorrelationId = "seed-routing",
            IdempotencyKey = "seed-routing",
            CreatedOn = DateTime.UtcNow.AddHours(-3)
        };
        var workItem = new UnassignedWorkItem
        {
            BusinessUnitId = TenantId,
            LeadId = lead.Id,
            RoutingDecision = decision,
            ReasonCode = "NO_OWNER",
            Status = WorkItemStatus.Open,
            Priority = 90,
            EnteredOn = DateTime.UtcNow.AddHours(-3),
            SlaDueOn = DateTime.UtcNow.AddHours(-1),
            RequiredAction = "Assign an owner",
            IdempotencyKey = "seed-unassigned",
            Version = 1
        };
        var followUp = new FollowUpTask
        {
            BusinessUnitId = TenantId,
            AssignedToUserId = OwnerUserId,
            AggregateType = CommercialAggregateType.Lead,
            AggregateId = lead.Id,
            DueAtUtc = DateTime.UtcNow.AddHours(-2),
            Status = FollowUpStatus.Open,
            Priority = 80,
            PurposeCode = "CUSTOMER_RESPONSE",
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            UpdatedAtUtc = DateTime.UtcNow.AddDays(-1),
            Version = 1,
            CreatedBy = "tests",
            CorrelationId = "seed-follow-up",
            CreationIdempotencyKey = "seed-follow-up"
        };
        context.AddRange(workItem, followUp);
        await context.SaveChangesAsync();
        return (followUp, workItem);
    }

    private static User User(long id, string firstName) => new()
    {
        Id = id,
        Buid = TenantId,
        FirstName = firstName,
        LastName = "Tester",
        Email = $"{firstName.ToLowerInvariant()}@nexora.invalid",
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        IsActive = true,
        CreatedBy = "tests",
        CreatedOn = DateTime.UtcNow
    };
}
