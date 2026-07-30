using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialIntelligenceControllerFocusedTests
{
    [Theory]
    [InlineData(nameof(InventoryIntelligenceController.RfqResolutions), "RFQ Management")]
    [InlineData(nameof(InventoryIntelligenceController.QuoteResolutions), "Quotations")]
    public void InventoryResolutionEndpoints_UseCanonicalRbacModuleNames(string actionName, string expectedModule)
    {
        var action = typeof(InventoryIntelligenceController).GetMethod(actionName);
        var permission = Assert.Single(action!.GetCustomAttributes<RequireModulePermissionAttribute>());

        Assert.Equal(expectedModule, permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    [Fact]
    public async Task RepPipeline_IncludesSentQuoteAndKeepsCurrenciesSeparate()
    {
        const long tenant = 87_001;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        ((SqliteConnection)context.Database.GetDbConnection()).CreateFunction("now", () => DateTime.UtcNow);
        var lead = Seed.Lead(context, 87_010, tenant);
        var user = new User
        {
            Id = 87_020, FirstName = "Sales", LastName = "Owner", Email = "owner@test",
            PasswordHash = "not-used", ImageUrl = "n/a", Buid = tenant, IsActive = true,
            CreatedBy = "test", CreatedOn = DateTime.UtcNow
        };
        context.Users.Add(user);
        var quoteSentRfqStatus = Status(context, 87_031, tenant, "RfqStatus", "QUOTE_SENT");
        var draftRfqStatus = Status(context, 87_032, tenant, "RfqStatus", "DRAFT");
        var sentQuoteStatus = Status(context, 87_033, tenant, "QuoteStatus", "SENT");
        var draftQuoteStatus = Status(context, 87_034, tenant, "QuoteStatus", "DRAFT");
        var usd = Currency(context, 87_041, tenant, "USD");
        var eur = Currency(context, 87_042, tenant, "EUR");
        var decision = new LeadRoutingDecision
        {
            Id = 87_050, BusinessUnitId = tenant, LeadId = lead.Id, SelectedUserId = user.Id,
            MatchStatus = CustomerMatchStatus.NoEvidence, Outcome = RoutingOutcome.AssignedPrimary,
            DecisionCode = "TEST", Explanation = "Focused test", PolicyVersion = "test/v1",
            CorrelationId = "focused", IdempotencyKey = "focused-decision", CreatedOn = DateTime.UtcNow
        };
        context.Add(decision);
        context.Add(new LeadAssignment
        {
            Id = 87_051, BusinessUnitId = tenant, LeadId = lead.Id, ToUserId = user.Id,
            AssignmentScope = AssignmentScope.LeadOnly, RoutingDecisionId = decision.Id,
            ReasonCode = "TEST", EffectiveFrom = DateTime.UtcNow,
            CorrelationId = "focused", IdempotencyKey = "focused-assignment"
        });
        var sentRfq = Rfq(87_061, tenant, lead.Id, quoteSentRfqStatus.SetupId);
        var draftRfq = Rfq(87_062, tenant, lead.Id, draftRfqStatus.SetupId);
        context.Rfqs.AddRange(sentRfq, draftRfq);
        context.Quotes.AddRange(
            Quote(87_071, tenant, sentRfq.Id, sentQuoteStatus.SetupId, usd.Id, 100m, DateTime.UtcNow.AddDays(-1)),
            Quote(87_072, tenant, draftRfq.Id, draftQuoteStatus.SetupId, eur.Id, 200m, null));
        await context.SaveChangesAsync();

        var controller = new CommercialIntelligenceController(context, null!, null!, new TestRoleGate(true))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.Reps(default));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var rep = Assert.Single(document.RootElement.EnumerateArray());
        var groups = rep.GetProperty("pipelineGroups").EnumerateArray().ToDictionary(
            group => group.GetProperty("currencyCode").GetString()!,
            group => group);

        Assert.Equal(1, rep.GetProperty("openRfqs").GetInt32());
        Assert.Equal(1, rep.GetProperty("draftQuotes").GetInt32());
        Assert.Equal(2, groups.Count);
        Assert.Equal(100m, groups["USD"].GetProperty("pipelineValue").GetDecimal());
        Assert.Equal(30m, groups["USD"].GetProperty("weightedPipeline").GetDecimal());
        Assert.Equal(200m, groups["EUR"].GetProperty("pipelineValue").GetDecimal());
    }

    [Fact]
    public async Task AssignAccount_RejectsIdempotencyReplayWithDifferentOwner()
    {
        const long tenant = 87_101;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.Customer(context, 87_102, tenant, "Replay Account");
        context.Users.AddRange(
            User(87_103, tenant, "original@test"),
            User(87_104, tenant, "changed@test"));
        context.Add(new CustomerOwnership
        {
            Id = 87_105, BusinessUnitId = tenant, CustomerId = 87_102,
            PrimaryUserId = 87_103, Scope = OwnershipScope.GeneralCustomer,
            Priority = 100, EffectiveFrom = DateTime.UtcNow, IsActive = true,
            Source = "MANUAL", MutationIdempotencyKey = "ownership-replay", Version = 1
        });
        await context.SaveChangesAsync();
        var http = new DefaultHttpContext { User = Principal(tenant) };
        http.Request.Headers["Idempotency-Key"] = "ownership-replay";
        var controller = new CommercialIntelligenceController(context, null!, null!, new TestRoleGate(true))
        {
            ControllerContext = new ControllerContext { HttpContext = http }
        };

        var response = await controller.AssignAccount(
            87_102, new AssignAccountRequest(87_104, 1), default);

        Assert.IsType<ConflictObjectResult>(response);
        var ownership = await context.Set<CustomerOwnership>().SingleAsync();
        Assert.Equal(87_103, ownership.PrimaryUserId);
        Assert.True(ownership.IsActive);
    }

    [Fact]
    public async Task SalesToday_ForIndividualRepReturnsOnlyAssignedWork()
    {
        const long tenant = 87_201;
        const long signedInUser = 87_202;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        context.Users.AddRange(User(signedInUser, tenant, "signed-in@test"), User(87_203, tenant, "other@test"));
        var now = DateTime.UtcNow;
        context.FollowUpTasks.AddRange(
            FollowUp(87_210, tenant, signedInUser, now.AddHours(-1), "MY_TASK"),
            FollowUp(87_211, tenant, 87_203, now.AddHours(-1), "OTHER_TASK"));
        await context.SaveChangesAsync();
        var controller = new CommercialIntelligenceController(context, null!, null!, new TestRoleGate(false))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant, signedInUser) }
            }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.SalesToday(default));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        Assert.Equal("assigned_to_me", document.RootElement.GetProperty("scope").GetString());
        var item = Assert.Single(document.RootElement.GetProperty("attentionItems").EnumerateArray());
        Assert.Equal("MY_TASK", item.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task TeamOverview_ForIndividualRepIsForbidden()
    {
        const long tenant = 87_220;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        Seed.BusinessUnit(context, tenant);
        await context.SaveChangesAsync();
        var controller = new CommercialIntelligenceController(context, null!, null!, new TestRoleGate(false))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant, 87_221, 87_222) }
            }
        };

        Assert.IsType<ForbidResult>(await controller.TeamOverview(default));
    }

    [Fact]
    public async Task FollowUps_CustomerFilterReturnsOnlyTenantCustomerQuotes()
    {
        const long tenant = 87_230;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        ((SqliteConnection)context.Database.GetDbConnection()).CreateFunction("now", () => DateTime.UtcNow);
        var customer = Seed.Customer(context, 87_231, tenant, "Selected customer");
        var otherCustomer = Seed.Customer(context, 87_232, tenant, "Other customer");
        context.Users.Add(User(87_233, tenant, "owner@test"));
        var quoteStatus = Status(context, 87_234, tenant, "QuoteStatus", "SENT");
        var currency = Currency(context, 87_235, tenant, "USD");
        var selectedQuote = Quote(87_236, tenant, 0, quoteStatus.SetupId, currency.Id, 100m, DateTime.UtcNow);
        selectedQuote.CustomerId = customer.Id;
        selectedQuote.Rfqid = null;
        var otherQuote = Quote(87_237, tenant, 0, quoteStatus.SetupId, currency.Id, 100m, DateTime.UtcNow);
        otherQuote.CustomerId = otherCustomer.Id;
        otherQuote.Rfqid = null;
        context.Quotes.AddRange(selectedQuote, otherQuote);
        context.FollowUpTasks.AddRange(
            new ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpTask
            {
                Id = 87_238, BusinessUnitId = tenant, AggregateType = "Quote", AggregateId = selectedQuote.Id,
                AssignedToUserId = 87_233, DueAtUtc = DateTime.UtcNow, PurposeCode = "SELECTED",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, CreatedBy = "test",
                CorrelationId = "selected", CreationIdempotencyKey = "selected"
            },
            new ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpTask
            {
                Id = 87_239, BusinessUnitId = tenant, AggregateType = "Quote", AggregateId = otherQuote.Id,
                AssignedToUserId = 87_233, DueAtUtc = DateTime.UtcNow, PurposeCode = "OTHER",
                CreatedAtUtc = DateTime.UtcNow, UpdatedAtUtc = DateTime.UtcNow, CreatedBy = "test",
                CorrelationId = "other", CreationIdempotencyKey = "other"
            });
        await context.SaveChangesAsync();
        var controller = new CommercialIntelligenceController(context, null!, null!, new TestRoleGate(true))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(tenant) } }
        };

        var response = Assert.IsType<OkObjectResult>(await controller.FollowUps(null, customer.Id, null, default));
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("SELECTED", row.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task AccountOwnership_ExactCustomerFilterIsTenantQualified()
    {
        const long tenant = 87_250;
        const long otherTenant = 87_251;
        using var database = new TestDb();
        await using var context = database.ContextFor(tenant);
        var selected = Seed.Customer(context, 87_252, tenant, "Shared account name");
        Seed.Customer(context, 87_253, tenant, "Shared account name");
        await context.SaveChangesAsync();
        await using (var otherContext = database.ContextFor(otherTenant))
        {
            Seed.Customer(otherContext, 87_254, otherTenant, "Other tenant account");
            await otherContext.SaveChangesAsync();
        }
        var controller = new CommercialIntelligenceController(context, null!, null!, new TestRoleGate(true))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = Principal(tenant) }
            }
        };

        var exactResponse = Assert.IsType<OkObjectResult>(
            await controller.AccountOwnership(null, default, selected.Id));
        using var exactDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            exactResponse.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var exact = Assert.Single(exactDocument.RootElement.EnumerateArray());
        Assert.Equal(selected.Id, exact.GetProperty("customerId").GetInt64());

        var crossTenantResponse = Assert.IsType<OkObjectResult>(
            await controller.AccountOwnership(null, default, 87_254));
        using var crossTenantDocument = JsonDocument.Parse(JsonSerializer.Serialize(
            crossTenantResponse.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Empty(crossTenantDocument.RootElement.EnumerateArray());
    }

    private static ClaimsPrincipal Principal(long tenant, long userId = 1, long roleId = 1) => new(new ClaimsIdentity(
        [new Claim("businessUnitId", tenant.ToString()), new Claim(ClaimTypes.NameIdentifier, userId.ToString()), new Claim("roleId", roleId.ToString())], "focused-test"));

    private sealed class TestRoleGate(bool manager) : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(manager);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) => Task.FromResult(manager);
    }

    private static User User(long id, long tenant, string email) => new()
    {
        Id = id, FirstName = "Sales", LastName = "Owner", Email = email,
        PasswordHash = "not-used", ImageUrl = "n/a", Buid = tenant,
        IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
    };

    private static ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpTask FollowUp(
        long id, long tenant, long userId, DateTime dueAt, string purpose) => new()
    {
        Id = id, BusinessUnitId = tenant, AssignedToUserId = userId,
        AggregateType = "Other", AggregateId = id, DueAtUtc = dueAt,
        PurposeCode = purpose, CreatedAtUtc = DateTime.UtcNow,
        UpdatedAtUtc = DateTime.UtcNow, CreatedBy = "test",
        CorrelationId = $"follow-up-{id}", CreationIdempotencyKey = $"follow-up-{id}"
    };

    private static SetupMaster Status(ErpRfqAutomationContext context, long id, long tenant, string type, string code)
    {
        var status = new SetupMaster
        {
            SetupId = id, SetupType = type, SetupCode = code, SetupValue = code,
            BusinessUnitId = tenant, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(status);
        return status;
    }

    private static Currency Currency(ErpRfqAutomationContext context, long id, long tenant, string code)
    {
        var currency = new Currency
        {
            Id = id, BusinessUnitId = tenant, Code = code, CurrencyName = code,
            IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow
        };
        context.Currencies.Add(currency);
        return currency;
    }

    private static Rfq Rfq(long id, long tenant, long leadId, long statusId) => new()
    {
        Id = id, BusinessUnitId = tenant, LeadId = leadId, Rfqno = $"RFQ-{id}",
        RecDate = DateTime.UtcNow, RfqstatusId = statusId, CreatedBy = "test", CreatedDate = DateTime.UtcNow
    };

    private static Quote Quote(long id, long tenant, long rfqId, long statusId, long currencyId, decimal amount, DateTime? sentOn) => new()
    {
        Id = id, BusinessUnitId = tenant, Rfqid = rfqId, QuoteNo = $"Q-{id}",
        StatusId = statusId, CurrencyId = currencyId, TotalAmount = amount,
        SentOn = sentOn, CreatedBy = "test", CreatedDate = DateTime.UtcNow
    };
}
