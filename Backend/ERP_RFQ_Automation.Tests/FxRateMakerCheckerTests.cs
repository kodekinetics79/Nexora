using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Sec-D4. Creating an exchange rate and making it real are two acts, done by two people, each
/// requiring a permission the tenant granted on purpose.
///
/// <para><b>The defect these pin shut.</b> Every one of <c>CurrencyController</c>'s eleven actions
/// carried nothing but a class-level <c>[Authorize]</c>, and there was no "Currencies" or
/// "Exchange Rates" module in <see cref="ModuleCatalog"/> that could have gated them. A user with a
/// zero-permission role — no <c>RolePermissions</c> rows at all — could <c>POST fx-rates</c> and
/// then immediately <c>POST fx-rates/{id}/approve</c> on their own rate. Only <c>Approved</c> rows
/// are visible to <c>FxConversionService</c>, so approval is precisely the control that was being
/// bypassed, and the resulting rate converts quote totals, sets the below-floor pricing guard's
/// threshold and re-bases the AI agent's spend cap.</para>
/// </summary>
public sealed class FxRateMakerCheckerTests
{
    private const long Bu = 909;

    // ---- the gates ---------------------------------------------------------

    private static IEnumerable<(string Action, RequireModulePermissionAttribute[] Gates)> CurrencyActions()
        => typeof(CurrencyController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<Microsoft.AspNetCore.Mvc.Routing.HttpMethodAttribute>().Any())
            .Select(method => (method.Name,
                method.GetCustomAttributes<RequireModulePermissionAttribute>().ToArray()));

    [Fact]
    public void Every_action_on_the_currency_controller_is_permission_gated()
    {
        var ungated = CurrencyActions()
            .Where(action => action.Gates.Length == 0)
            .Select(action => action.Action)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(ungated.Count == 0,
            "These CurrencyController actions carry no [RequireModulePermission], so any "
            + "authenticated user of the tenant can call them:\n  " + string.Join("\n  ", ungated));

        // All eleven, so removing an action's gate cannot be masked by removing the action.
        Assert.Equal(11, CurrencyActions().Count());
    }

    [Fact]
    public void Approving_a_rate_requires_a_different_module_from_creating_one()
    {
        // If both sat on one module, the maker-checker rule below would hold only until someone
        // granted a single role both halves — which is the normal way roles get configured.
        var create = CurrencyActions().Single(a => a.Action == nameof(CurrencyController.CreateFxRate)).Gates;
        var approve = CurrencyActions().Single(a => a.Action == nameof(CurrencyController.ApproveFxRate)).Gates;

        Assert.Equal("Exchange Rates", Assert.Single(create).ModuleName);
        Assert.Equal("Exchange Rate Approval", Assert.Single(approve).ModuleName);
        Assert.NotEqual(create[0].ModuleName, approve[0].ModuleName);

        // And the approval module must be a real, grantable module — an unseeded name is not
        // "more secure", it is permanently denied to everyone with no error naming the cause.
        Assert.Contains("Exchange Rate Approval", ModuleCatalog.Names);
        Assert.Contains("Exchange Rates", ModuleCatalog.Names);
        Assert.Contains("Currencies", ModuleCatalog.Names);
    }

    [Fact]
    public void No_starter_role_can_both_raise_and_approve_an_exchange_rate()
    {
        // Segregation that a provisioning template hands away on day one is not segregation.
        var conflicted = ERP_RFQ_Automation.Platform.Services.TenantBaselineCatalog.StarterRoles
            .Where(role =>
                role.Grants.Any(g => g.Module == "Exchange Rates" && (g.CanCreate || g.CanEdit))
                && role.Grants.Any(g => g.Module == "Exchange Rate Approval"))
            .Select(role => role.Code)
            .ToList();

        Assert.True(conflicted.Count == 0,
            "These starter roles can both raise and approve an exchange rate:\n  "
            + string.Join("\n  ", conflicted));
    }

    // ---- the maker-checker rule -------------------------------------------

    private static CurrencyController Controller(ErpRfqAutomationContext context, long userId, string email)
    {
        var controller = new CurrencyController(new StubCurrencyRepository(), context)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                        new Claim(ClaimTypes.Email, email),
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim("roleId", "5")
                    ], "test"))
                }
            }
        };
        return controller;
    }

    private static void SeedCurrencies(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, Bu);
        context.Currencies.AddRange(
            new Currency { Id = 1, Code = "SAR", CurrencyName = "Saudi Riyal", BusinessUnitId = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow },
            new Currency { Id = 2, Code = "USD", CurrencyName = "US Dollar", BusinessUnitId = Bu, IsActive = true, CreatedBy = "seed", CreatedOn = DateTime.UtcNow });
        context.SaveChanges();
    }

    private static FxRateCreateRequestDTO NewRate() => new()
    {
        FromCurrencyId = 2,
        ToCurrencyId = 1,
        Rate = 3.75m,
        EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task The_creator_of_a_rate_cannot_approve_it()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(Bu);
        SeedCurrencies(context);

        var maker = Controller(context, userId: 11, email: "maker@example.test");
        var created = await maker.CreateFxRate(NewRate());
        var rate = Assert.IsType<FxRateResponseDTO>(Assert.IsType<OkObjectResult>(created.Result).Value);
        Assert.Equal(FxRateStatuses.Pending, rate.Status);

        // THE assertion. Before Sec-D4 this returned 200 and the rate went live.
        var selfApproval = await maker.ApproveFxRate(rate.Id);
        var conflict = Assert.IsType<ConflictObjectResult>(selfApproval.Result);
        Assert.Contains("cannot approve", conflict.Value!.ToString(), StringComparison.OrdinalIgnoreCase);

        // And it is genuinely still inert — the status is what conversion reads, not the response.
        using var verify = db.ContextFor(Bu);
        Assert.Equal(FxRateStatuses.Pending,
            (await verify.FxRates.SingleAsync(r => r.Id == rate.Id)).Status);
    }

    [Fact]
    public async Task A_second_person_can_approve_it()
    {
        // The control. Without it, "approval always fails" would satisfy the test above and the
        // product would simply have no working FX rates.
        using var db = new TestDb();
        using var context = db.ContextFor(Bu);
        SeedCurrencies(context);

        var created = await Controller(context, 11, "maker@example.test").CreateFxRate(NewRate());
        var rate = Assert.IsType<FxRateResponseDTO>(Assert.IsType<OkObjectResult>(created.Result).Value);

        var approved = await Controller(context, 12, "checker@example.test").ApproveFxRate(rate.Id);
        var payload = Assert.IsType<FxRateResponseDTO>(Assert.IsType<OkObjectResult>(approved.Result).Value);

        Assert.Equal(FxRateStatuses.Approved, payload.Status);
        Assert.NotEqual(payload.CreatedBy, payload.ApprovedBy);
    }

    [Fact]
    public async Task A_rate_whose_maker_was_never_recorded_cannot_be_approved_at_all()
    {
        // Every rate written before this change carries CreatedBy = "System", because the tenant
        // bearer scheme never populated User.Identity.Name and the old code fell back to that
        // literal. Approving one cannot be shown to involve two people, so it is refused rather
        // than waved through — the same line ProcurementApplicationService takes when a sourcing
        // award does not record its approver.
        using var db = new TestDb();
        using var context = db.ContextFor(Bu);
        SeedCurrencies(context);
        context.FxRates.Add(new FxRate
        {
            BusinessUnitId = Bu,
            FromCurrencyId = 2,
            ToCurrencyId = 1,
            Rate = 3.75m,
            EffectiveFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Source = "Manual",
            Status = FxRateStatuses.Pending,
            Version = 1,
            CreatedBy = "System",
            CreatedOn = DateTime.UtcNow
        });
        context.SaveChanges();

        var legacyId = context.FxRates.Single().Id;
        var result = await Controller(context, 12, "checker@example.test").ApproveFxRate(legacyId);

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("Segregation of duties cannot be verified",
            conflict.Value!.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Attribution_comes_from_the_token_and_not_from_the_request_body()
    {
        // The maker-checker comparison is only as good as the value it compares. If CreatedBy
        // could still be influenced by the caller, a maker could write someone else's name onto
        // their own rate and then approve it themselves.
        using var db = new TestDb();
        using var context = db.ContextFor(Bu);
        SeedCurrencies(context);

        var created = await Controller(context, 11, "maker@example.test").CreateFxRate(NewRate());
        var rate = Assert.IsType<FxRateResponseDTO>(Assert.IsType<OkObjectResult>(created.Result).Value);

        Assert.Contains("user:11", rate.CreatedBy);
        Assert.NotEqual("System", rate.CreatedBy);

        // The request contract carries no actor field to send in the first place.
        Assert.Null(typeof(FxRateCreateRequestDTO).GetProperty("CreatedBy"));
        Assert.Null(typeof(CurrencyCreateRequestDTO).GetProperty("CreatedBy"));
        Assert.Null(typeof(CurrencyUpdateRequestDTO).GetProperty("ModifiedBy"));
    }

    /// <summary>The FX endpoints under test never touch ICurrencyRepository; it is a constructor
    /// dependency only.</summary>
    private sealed class StubCurrencyRepository : ICurrencyRepository
    {
        public Task<IEnumerable<Currency>> GetAllAsync(long businessUnitId)
            => Task.FromResult(Enumerable.Empty<Currency>());
        public Task<Currency> GetByIdAsync(long id, long businessUnitId)
            => throw new KeyNotFoundException();
        public Task AddAsync(Currency currency) => Task.CompletedTask;
        public Task UpdateAsync(Currency currency) => Task.CompletedTask;
        public Task DeleteAsync(long id, long businessUnitId) => Task.CompletedTask;
    }
}
