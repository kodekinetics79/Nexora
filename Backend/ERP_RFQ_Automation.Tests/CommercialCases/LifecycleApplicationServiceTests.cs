using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.CommercialCases;

public sealed class LifecycleApplicationServiceTests
{
    [Fact]
    public async Task LeadTransition_CommitsVersionAuditAndOutboxAtomically()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(71);
        Seed.Lead(context, 101, 71);
        Status(context, 501, 71, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        await context.SaveChangesAsync();
        var reference = context.Leads.Single().CommercialCaseReference;

        var result = await Service(context).TransitionLeadAsync(71, 101, Actor(),
            Command("PENDING_IDENTIFICATION", 1, "lead-101-pending"), false, default);

        context.ChangeTracker.Clear();
        var lead = await context.Leads.SingleAsync(x => x.Id == 101);
        var lifecycleEvent = await context.CommercialLifecycleEvents.SingleAsync();
        var outbox = await context.LifecycleOutboxMessages.SingleAsync();
        Assert.Equal(501, lead.LeadStatusId);
        Assert.Equal(2, lead.LifecycleVersion);
        Assert.Equal(reference, lead.CommercialCaseReference);
        Assert.Equal("PENDING_IDENTIFICATION", lifecycleEvent.NewStatusCode);
        Assert.Equal(LifecyclePolicy.Version, lifecycleEvent.PolicyVersion);
        Assert.Equal(lifecycleEvent.Id, outbox.LifecycleEventId);
        Assert.Contains(reference, outbox.Payload);
        Assert.Equal(result.EventId, lifecycleEvent.Id);
    }

    [Fact]
    public async Task Replay_ReturnsOriginalResultWithoutAnotherWrite()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(72);
        Seed.Lead(context, 102, 72);
        Status(context, 502, 72, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        await context.SaveChangesAsync();
        var command = Command("PENDING_IDENTIFICATION", 1, "lead-102-pending");
        var service = Service(context);

        var first = await service.TransitionLeadAsync(72, 102, Actor(), command, false, default);
        var replayCommand = command with { CorrelationId = "retry-correlation", RequestReference = "retry-request" };
        var replay = await service.TransitionLeadAsync(72, 102, Actor(), replayCommand, false, default);

        Assert.False(first.Replayed);
        Assert.True(replay.Replayed);
        Assert.Equal(first.EventId, replay.EventId);
        Assert.Equal(1, await context.CommercialLifecycleEvents.CountAsync());
        Assert.Equal(1, await context.LifecycleOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ReplayKeyBoundToCanonicalRequest()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(73);
        Seed.Lead(context, 103, 73);
        Status(context, 503, 73, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        Status(context, 504, 73, "LeadStatus", "CANCELLED", "Cancelled");
        await context.SaveChangesAsync();
        var service = Service(context);
        await service.TransitionLeadAsync(73, 103, Actor(), Command("PENDING_IDENTIFICATION", 1, "same-key"), false, default);

        await Assert.ThrowsAsync<LifecycleConflictException>(() => service.TransitionLeadAsync(
            73, 103, Actor(), Command("CANCELLED", 1, "same-key", "WITHDRAWN"), false, default));
        Assert.Equal(1, await context.CommercialLifecycleEvents.CountAsync());
    }

    [Fact]
    public async Task IllegalSkippedTransitionWritesNothing()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(74);
        Seed.Lead(context, 104, 74);
        Status(context, 505, 74, "LeadStatus", "QUALIFIED", "Qualified");
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context).TransitionLeadAsync(
            74, 104, Actor(), Command("QUALIFIED", 1, "lead-104-qualified"), false, default));
        Assert.Empty(context.CommercialLifecycleEvents);
        Assert.Empty(context.LifecycleOutboxMessages);
        Assert.Equal(1, context.Leads.Single().LifecycleVersion);
    }

    [Fact]
    public async Task UnverifiedAiLead_CannotBeQualified()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(741);
        var lead = Seed.Lead(context, 1041, 741, leadStatusId: 5051, parseStatus: "NeedsReview");
        lead.RequiresCommercialReview = true;
        lead.CommercialFactsVerified = false;
        Status(context, 5051, 741, "LeadStatus", "UNDER_REVIEW", "Under Review");
        Status(context, 5052, 741, "LeadStatus", "QUALIFIED", "Qualified");
        await context.SaveChangesAsync();

        var error = await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context).TransitionLeadAsync(
            741, 1041, Actor(), Command("QUALIFIED", 1, "lead-1041-qualified"), false, default));

        Assert.Contains("commercial facts", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(context.CommercialLifecycleEvents);
        Assert.Equal(5051, context.Leads.Single().LeadStatusId);
    }

    [Fact]
    public async Task DirectLeadStatusMutationIsRejectedWithoutLifecycleEvent()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(740);
        var lead = Seed.Lead(context, 1040, 740);
        Status(context, 5050, 740, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        await context.SaveChangesAsync();

        lead.LeadStatusId = 5050;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        Assert.Contains("governed lifecycle command", error.Message);
        Assert.Empty(context.CommercialLifecycleEvents);
    }

    [Fact]
    public async Task TerminalTransitionRequiresStructuredReasonCode()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(75);
        Seed.Lead(context, 105, 75);
        Status(context, 506, 75, "LeadStatus", "CANCELLED", "Cancelled");
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context).TransitionLeadAsync(
            75, 105, Actor(), Command("CANCELLED", 1, "lead-105-cancel"), false, default));
        Assert.Empty(context.CommercialLifecycleEvents);
    }

    [Fact]
    public async Task ManagerCommandCanReopenEligibleTerminalStateAndPreservesOutcome()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(76);
        Seed.Lead(context, 106, 76);
        Status(context, 507, 76, "LeadStatus", "CANCELLED", "Cancelled");
        Status(context, 508, 76, "LeadStatus", "UNDER_REVIEW", "Under Review");
        await context.SaveChangesAsync();
        var service = Service(context);
        await service.TransitionLeadAsync(76, 106, Actor(),
            Command("CANCELLED", 1, "lead-106-cancel", "CUSTOMER_WITHDREW"), false, default);

        var reopened = await service.TransitionLeadAsync(76, 106, Actor(),
            Command("UNDER_REVIEW", 2, "lead-106-reopen", "NEW_INFORMATION"), true, default);

        Assert.Equal("Reopened", reopened.EventType);
        Assert.Equal(3, reopened.Version);
        var events = await context.CommercialLifecycleEvents.OrderBy(x => x.Id).ToArrayAsync();
        Assert.Equal(2, events.Length);
        Assert.Equal("CUSTOMER_WITHDREW", events[0].ReasonCode);
        Assert.Equal("NEW_INFORMATION", events[1].ReasonCode);
    }

    [Fact]
    public async Task CompletedLeadCannotUseOrdinaryReopen()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(77);
        Status(context, 509, 77, "LeadStatus", "COMPLETED", "Completed");
        var lead = Seed.Lead(context, 107, 77, 509);
        Status(context, 510, 77, "LeadStatus", "UNDER_REVIEW", "Under Review");
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context).TransitionLeadAsync(
            77, lead.Id, Actor(), Command("UNDER_REVIEW", 1, "lead-107-reopen", "CORRECTION"), true, default));
        Assert.Empty(context.CommercialLifecycleEvents);
    }

    [Fact]
    public async Task StaleVersionLosesWithoutSideEffects()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(78);
        Seed.Lead(context, 108, 78);
        Status(context, 511, 78, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        await context.SaveChangesAsync();
        var service = Service(context);
        await service.TransitionLeadAsync(78, 108, Actor(), Command("PENDING_IDENTIFICATION", 1, "winner"), false, default);

        await Assert.ThrowsAsync<LifecycleConflictException>(() => service.TransitionLeadAsync(
            78, 108, Actor(), Command("PENDING_IDENTIFICATION", 1, "stale"), false, default));
        Assert.Equal(1, await context.CommercialLifecycleEvents.CountAsync());
        Assert.Equal(1, await context.LifecycleOutboxMessages.CountAsync());
    }

    [Fact]
    public async Task ForeignTenantGetsNotFoundAndNoExistenceOracle()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(79))
        {
            Seed.Lead(seed, 109, 79);
            Status(seed, 512, 79, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
            await seed.SaveChangesAsync();
        }
        await using var foreign = db.ContextFor(80);

        await Assert.ThrowsAsync<LifecycleNotFoundException>(() => Service(foreign).TransitionLeadAsync(
            80, 109, Actor(), Command("PENDING_IDENTIFICATION", 1, "foreign"), false, default));
        Assert.Empty(foreign.CommercialLifecycleEvents.IgnoreQueryFilters());
    }

    [Fact]
    public async Task RfqTransitionUsesSameCaseReferenceAndGovernance()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(81);
        var lead = Seed.Lead(context, 110, 81);
        Status(context, 513, 81, "RFQStatus", "DRAFT", "Draft");
        Status(context, 514, 81, "RFQStatus", "VALIDATING", "Validating");
        await context.SaveChangesAsync();
        var rfq = new Rfq
        {
            Id = 210,
            Rfqno = "RFQ-LIFE-210",
            RecDate = DateTime.UtcNow,
            LeadId = lead.Id,
            BusinessUnitId = 81,
            RfqstatusId = 513,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        };
        context.Rfqs.Add(rfq);
        await context.SaveChangesAsync();

        var result = await Service(context).TransitionRfqAsync(81, rfq.Id, Actor(),
            Command("VALIDATING", 1, "rfq-210-validating"), false, default);

        Assert.Equal("Rfq", result.AggregateType);
        Assert.Equal(lead.CommercialCaseReference, result.CommercialCaseReference);
        Assert.Equal(2, result.Version);
        Assert.Equal(514, (await context.Rfqs.SingleAsync(x => x.Id == rfq.Id)).RfqstatusId);
    }

    [Fact]
    public async Task RfqWithoutCommercialCaseLinkFailsClosed()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(82);
        Seed.BusinessUnit(context, 82);
        Status(context, 515, 82, "RFQStatus", "DRAFT", "Draft");
        Status(context, 516, 82, "RFQStatus", "VALIDATING", "Validating");
        context.Rfqs.Add(new Rfq
        {
            Id = 211,
            Rfqno = "RFQ-ORPHAN-211",
            RecDate = DateTime.UtcNow,
            BusinessUnitId = 82,
            RfqstatusId = 515,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        await Assert.ThrowsAsync<LifecycleValidationException>(() => Service(context).TransitionRfqAsync(
            82, 211, Actor(), Command("VALIDATING", 1, "rfq-orphan"), false, default));
        Assert.Empty(context.CommercialLifecycleEvents);
    }

    [Fact]
    public async Task StateViewReturnsOnlyConfiguredLegalTargets()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(83);
        Seed.Lead(context, 112, 83);
        Status(context, 517, 83, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        Status(context, 518, 83, "LeadStatus", "QUALIFIED", "Qualified");
        Status(context, 519, 83, "LeadStatus", "CANCELLED", "Cancelled");
        await context.SaveChangesAsync();

        var state = await Service(context).GetLeadStateAsync(83, 112, default);

        Assert.Equal("RECEIVED", state.CurrentStatusCode);
        Assert.Equal(new[] { "CANCELLED", "PENDING_IDENTIFICATION" }, state.AllowedTransitions.Select(x => x.StatusCode));
        Assert.DoesNotContain(state.AllowedTransitions, x => x.StatusCode == "QUALIFIED");
    }

    [Fact]
    public async Task OutboxLeaseCanBeCompletedOnlyByOwningWorker()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(84);
        Seed.Lead(context, 113, 84);
        Status(context, 520, 84, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        await context.SaveChangesAsync();
        await Service(context).TransitionLeadAsync(84, 113, Actor(),
            Command("PENDING_IDENTIFICATION", 1, "lead-113-pending"), false, default);
        var store = new LifecycleOutboxStore(context);

        var claimed = Assert.Single(await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(1), default));
        await Assert.ThrowsAsync<LifecycleConflictException>(() => store.CompleteAsync(claimed.Id, "worker-b", default));
        await store.CompleteAsync(claimed.Id, "worker-a", default);

        context.ChangeTracker.Clear();
        var message = await context.LifecycleOutboxMessages.SingleAsync();
        Assert.NotNull(message.ProcessedOn);
        Assert.Equal(1, message.AttemptCount);
        Assert.Null(message.LockedBy);
        Assert.Empty(await store.ClaimAsync("worker-a", 10, TimeSpan.FromMinutes(1), default));
    }

    [Fact]
    public async Task RepeatedOutboxFailureDeadLettersAtConfiguredLimit()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(85);
        Seed.Lead(context, 114, 85);
        Status(context, 521, 85, "LeadStatus", "PENDING_IDENTIFICATION", "Pending Identification");
        await context.SaveChangesAsync();
        await Service(context).TransitionLeadAsync(85, 114, Actor(),
            Command("PENDING_IDENTIFICATION", 1, "lead-114-pending"), false, default);
        var store = new LifecycleOutboxStore(context);

        var claimed = Assert.Single(await store.ClaimAsync("worker-c", 1, TimeSpan.FromMinutes(1), default));
        await store.FailAsync(claimed.Id, "worker-c", "downstream rejected payload", 1, default);

        context.ChangeTracker.Clear();
        var message = await context.LifecycleOutboxMessages.SingleAsync();
        Assert.NotNull(message.DeadLetteredOn);
        Assert.Equal("downstream rejected payload", message.LastError);
        Assert.Empty(await store.ClaimAsync("worker-c", 1, TimeSpan.FromMinutes(1), default));
    }

    private static LifecycleApplicationService Service(ErpRfqAutomationContext context) => new(context);
    private static LifecycleActor Actor() => new("user-42", "AuthenticatedUser");

    private static LifecycleTransitionCommand Command(string target, int version, string key, string? reasonCode = null)
        => new(target, version, reasonCode, reasonCode == null ? null : "reviewed notes", "Api", "corr-1", "req-1", key);

    private static SetupMaster Status(
        ErpRfqAutomationContext context, long id, long businessUnitId, string type, string code, string label)
    {
        Seed.EnsureBusinessUnit(context, businessUnitId);
        var status = new SetupMaster
        {
            SetupId = id,
            BusinessUnitId = businessUnitId,
            SetupType = type,
            SetupCode = code,
            SetupValue = label,
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(status);
        return status;
    }
}
