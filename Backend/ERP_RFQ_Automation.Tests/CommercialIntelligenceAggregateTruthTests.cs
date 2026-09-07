using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The commercial-intelligence screens state figures the caller is not allowed to recompute, so
/// each figure has to be true of the caller's whole scope rather than of the rows the endpoint
/// happened to return. Two ways it was not: <c>sales-today</c> counted the page instead of the
/// book of work, and <c>performance</c> published no scope-level conversion rate at all, which
/// left every client dividing won by decided and bypassing the sample floor while doing it.
///
/// Follow-ups and outcome activities are written through the production services
/// (<see cref="SalesApplicationService"/>) against real quote rows, because the read paths group
/// outcomes by aggregate and count follow-ups by status: a hand-built row with the wrong
/// aggregate identity or a defaulted status passes assertions the product would fail.
/// </summary>
public sealed class CommercialIntelligenceAggregateTruthTests
{
    [Fact]
    public async Task SalesToday_counts_every_open_follow_up_not_only_the_page_of_rows_it_returns()
    {
        const long tenant = 88_401;
        const long rep = 88_402;
        const int overdueCount = 115;
        const int futureCount = 15;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.Add(SalesUser(rep, tenant, "rep@test"));
        await context.SaveChangesAsync();
        var sales = new SalesApplicationService(new EfSalesPersistence(context));
        var now = DateTime.UtcNow;
        for (var index = 0; index < overdueCount + futureCount; index++)
        {
            var overdue = index < overdueCount;
            var quote = await SeedQuoteAsync(context, tenant, $"Q-TODAY-{index}");
            await sales.CreateFollowUpAsync(tenant, new CreateFollowUpTaskCommand(
                rep, "Quote", quote.Id, null,
                overdue ? now.AddDays(-index - 1) : now.AddDays(index + 1),
                2, overdue ? "OVERDUE_CHASE" : "SCHEDULED_CHASE", "rep@test",
                $"quote:{quote.Id}:commercial-activity", $"quote:{quote.Id}:follow-up"), default);
        }
        var controller = Controller(context, sales, new TestRoleGate(false), Principal(tenant, rep));

        var response = Assert.IsType<OkObjectResult>(await controller.SalesToday(default));
        var payload = Serialize(response.Value);

        Assert.Equal(overdueCount + futureCount, Metric(payload, "open-follow-ups"));
        Assert.Equal(overdueCount, Metric(payload, "overdue-follow-ups"));
        Assert.Equal(100, payload.GetProperty("attentionItems").GetArrayLength());
        Assert.Equal(100, payload.GetProperty("attentionItemLimit").GetInt32());
        Assert.True(payload.GetProperty("attentionItemsTruncated").GetBoolean());
    }

    [Fact]
    public async Task SalesToday_does_not_claim_truncation_when_the_whole_list_is_returned()
    {
        const long tenant = 88_410;
        const long rep = 88_411;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.Add(SalesUser(rep, tenant, "rep@test"));
        await context.SaveChangesAsync();
        var sales = new SalesApplicationService(new EfSalesPersistence(context));
        for (var index = 0; index < 3; index++)
        {
            var quote = await SeedQuoteAsync(context, tenant, $"Q-SHORT-{index}");
            await sales.CreateFollowUpAsync(tenant, new CreateFollowUpTaskCommand(
                rep, "Quote", quote.Id, null, DateTime.UtcNow.AddDays(index + 1), 2, "SCHEDULED_CHASE",
                "rep@test", $"quote:{quote.Id}:commercial-activity", $"quote:{quote.Id}:follow-up"), default);
        }
        var controller = Controller(context, sales, new TestRoleGate(false), Principal(tenant, rep));

        var response = Assert.IsType<OkObjectResult>(await controller.SalesToday(default));
        var payload = Serialize(response.Value);

        Assert.Equal(3, Metric(payload, "open-follow-ups"));
        Assert.Equal(0, Metric(payload, "overdue-follow-ups"));
        Assert.Equal(3, payload.GetProperty("attentionItems").GetArrayLength());
        Assert.False(payload.GetProperty("attentionItemsTruncated").GetBoolean());
    }

    [Fact]
    public async Task Performance_states_the_scope_conversion_rate_so_no_caller_has_to_divide_it()
    {
        const long tenant = 88_420;
        const long rep = 88_421;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.Add(SalesUser(rep, tenant, "manager@test"));
        await context.SaveChangesAsync();
        var sales = new SalesApplicationService(new EfSalesPersistence(context));
        await RecordOutcomesAsync(context, sales, tenant, rep, won: 4, lost: 2, prefix: "WHOLE");
        var controller = Controller(context, sales, new TestRoleGate(true), Principal(tenant, rep));

        var response = Assert.IsType<OkObjectResult>(await controller.Performance(
            DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1), default));
        var payload = Serialize(response.Value);

        Assert.Equal("tenant", payload.GetProperty("scope").GetString());
        Assert.Equal(4, Metric(payload, "won"));
        Assert.Equal(2, Metric(payload, "lost"));
        Assert.Equal(6, Metric(payload, "decided"));
        Assert.Equal(6, payload.GetProperty("decidedQuotes").GetInt32());
        Assert.True(payload.GetProperty("conversionEligible").GetBoolean());
        Assert.Equal(66.67m, payload.GetProperty("conversionRate").GetDecimal());
    }

    [Fact]
    public async Task Performance_withholds_the_scope_conversion_rate_below_the_sample_floor()
    {
        const long tenant = 88_430;
        const long rep = 88_431;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.Add(SalesUser(rep, tenant, "manager@test"));
        await context.SaveChangesAsync();
        var sales = new SalesApplicationService(new EfSalesPersistence(context));
        await RecordOutcomesAsync(context, sales, tenant, rep, won: 3, lost: 1, prefix: "THIN");
        var controller = Controller(context, sales, new TestRoleGate(true), Principal(tenant, rep));

        var response = Assert.IsType<OkObjectResult>(await controller.Performance(
            DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1), default));
        var payload = Serialize(response.Value);

        Assert.Equal(4, payload.GetProperty("decidedQuotes").GetInt32());
        Assert.False(payload.GetProperty("conversionEligible").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("conversionRate").ValueKind);
        Assert.Equal(5, payload.GetProperty("minimumConversionSample").GetInt32());
    }

    /// <summary>
    /// A rate served under a manager's heading must be their team's, never the tenant's. The
    /// out-of-scope rep here wins everything, so a leak reads as a materially better number: 14
    /// decided at 78.57% instead of the team's 10 decided at 50%.
    /// </summary>
    [Fact]
    public async Task Performance_computes_the_conversion_rate_from_the_resolved_scope_only()
    {
        const long tenant = 88_440;
        const long manager = 88_441;
        const long managedRep = 88_442;
        const long otherRep = 88_443;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.AddRange(
            SalesUser(manager, tenant, "manager@test"),
            SalesUser(managedRep, tenant, "managed@test"),
            SalesUser(otherRep, tenant, "other-team@test"));
        await context.SaveChangesAsync();
        var sales = new SalesApplicationService(new EfSalesPersistence(context));
        await RecordOutcomesAsync(context, sales, tenant, managedRep, won: 5, lost: 5, prefix: "TEAM");
        await RecordOutcomesAsync(context, sales, tenant, otherRep, won: 4, lost: 0, prefix: "OTHER");
        var scope = new AccountTeamScope(AccountScopeTier.ManagedScope, manager, [], [manager, managedRep]);
        var controller = Controller(context, sales, new TestRoleGate(true), Principal(tenant, manager),
            new FixedAccountScopeResolver(scope));

        var response = Assert.IsType<OkObjectResult>(await controller.Performance(
            DateTime.UtcNow.AddDays(-7), DateTime.UtcNow.AddDays(1), default));
        var payload = Serialize(response.Value);

        Assert.Equal("managed_scope", payload.GetProperty("scope").GetString());
        Assert.Equal(10, payload.GetProperty("decidedQuotes").GetInt32());
        Assert.True(payload.GetProperty("conversionEligible").GetBoolean());
        Assert.Equal(50m, payload.GetProperty("conversionRate").GetDecimal());
    }

    private static async Task RecordOutcomesAsync(ErpRfqAutomationContext context,
        SalesApplicationService sales, long tenant, long userId, int won, int lost, string prefix)
    {
        var occurredOn = DateTime.UtcNow.AddDays(-1);
        for (var index = 0; index < won + lost; index++)
        {
            var isWon = index < won;
            var quote = await SeedQuoteAsync(context, tenant, $"Q-{prefix}-{index}");
            var eventCode = isWon ? "WON" : "LOST";
            await sales.AppendActivityAsync(tenant, new AppendCommercialActivityCommand(
                userId, isWon ? CommercialActivityType.Won : CommercialActivityType.Lost,
                "Quote", quote.Id, null, null, occurredOn, eventCode,
                $"quote:{quote.Id}:{eventCode.ToLowerInvariant()}", "outcome@test",
                $"quote:{quote.Id}:commercial-activity",
                $"quote:{quote.Id}:commercial-activity:v1:{eventCode}"), default);
        }
    }

    private static async Task<Quote> SeedQuoteAsync(ErpRfqAutomationContext context, long tenant, string quoteNo)
    {
        var quote = new Quote
        {
            BusinessUnitId = tenant, QuoteNo = quoteNo, CreatedBy = "aggregate-truth-test",
            CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow.AddDays(-2)
        };
        context.Quotes.Add(quote);
        await context.SaveChangesAsync();
        return quote;
    }

    private static CommercialIntelligenceController Controller(
        ErpRfqAutomationContext context, ISalesApplicationService sales, IRoleGate roleGate,
        ClaimsPrincipal user, IAccountTeamScopeResolver? scope = null) =>
        new(context, sales, null!, roleGate, scope)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } }
        };

    private static JsonElement Serialize(object? value) => JsonSerializer.SerializeToElement(
        value, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static decimal Metric(JsonElement payload, string key) => payload.GetProperty("metrics")
        .EnumerateArray().Single(metric => metric.GetProperty("key").GetString() == key)
        .GetProperty("value").GetDecimal();

    private static ClaimsPrincipal Principal(long tenant, long userId) => new(new ClaimsIdentity(
        [new Claim("businessUnitId", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim("roleId", "1")], "aggregate-truth-test"));

    private static User SalesUser(long id, long tenant, string email) => new()
    {
        Id = id, FirstName = "Sales", LastName = "Owner", Email = email,
        PasswordHash = "not-used", ImageUrl = "n/a", Buid = tenant,
        IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private sealed class TestRoleGate(bool manager) : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) =>
            Task.FromResult(manager ? RoleRanks.Manager : RoleRanks.Member);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(manager);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) =>
            Task.FromResult(manager);
    }

    private sealed class FixedAccountScopeResolver(AccountTeamScope scope) : IAccountTeamScopeResolver
    {
        public Task<AccountTeamScope> ResolveAsync(long userId, long roleId, long businessUnitId,
            DateTime asOfUtc, CancellationToken ct = default) => Task.FromResult(scope);
    }
}
