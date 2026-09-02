using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

/// <summary>
/// <c>POST /api/quotes/backfill</c> carried only <c>[Authorize]</c> and the plan entitlement, so
/// any authenticated user of an entitled tenant could write quotes into its pipeline. It now
/// carries the same Quotations Create gate as <c>QuoteController</c>'s creating actions, and this
/// proves it through the real pipeline — authentication, tenant guard, entitlement filter and the
/// permission handler reading RolePermissions rows — rather than by reflection alone.
/// </summary>
[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class QuoteBackfillAuthenticatedHttpTests(Release01BHttpApplication app)
{
    private const string Path = "/api/quotes/backfill";

    [Fact]
    public async Task A_role_without_quotations_create_is_forbidden_and_a_role_with_it_reaches_the_action()
    {
        // The fixture's Denied role holds no module rows at all; the Growth Manager role holds
        // Quotations VIEW only. Both must be stopped by the gate, which is what distinguishes
        // "Create" from "any row on the module".
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        using var viewOnly = Client(Release01BHttpApplication.GrowthManagerRole, Release01BHttpApplication.TenantA,
            userId: Release01BHttpApplication.GrowthManagerUser);
        using var allowed = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        var body = new { customerId = 0, externalQuoteReference = "", currencyId = 0 };

        Assert.Equal(HttpStatusCode.Forbidden, (await denied.PostAsJsonAsync(Path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewOnly.PostAsJsonAsync(Path, body)).StatusCode);

        // The control: a role holding Quotations Create passes the gate and reaches the action,
        // which then refuses the deliberately invalid body on its merits. Without this line a
        // tenant-wide 403 (a missing entitlement, say) would make the two assertions above pass
        // for the wrong reason.
        var reached = await allowed.PostAsJsonAsync(Path, body);
        Assert.NotEqual(HttpStatusCode.Forbidden, reached.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, reached.StatusCode);
        Assert.True((int)reached.StatusCode is >= 400 and < 500,
            $"expected the action to refuse the invalid body, got {(int)reached.StatusCode}");
    }

    [Fact]
    public async Task An_unauthenticated_call_is_challenged()
    {
        using var anonymous = app.CreateClient();
        var response = await anonymous.PostAsJsonAsync(Path, new { });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_backfill_action_carries_exactly_the_gate_the_other_quote_creating_actions_carry()
    {
        var backfill = typeof(QuoteBackfillController).GetMethod(nameof(QuoteBackfillController.Backfill))!;
        var gate = Assert.Single(backfill.GetCustomAttributes<RequireModulePermissionAttribute>(true));
        Assert.Equal("Quotations", gate.ModuleName);
        Assert.Equal(PermissionAction.Create, gate.Action);
    }

    private HttpClient Client(long roleId, long? tenantId, long userId = Release01BHttpApplication.GrowthRepUser)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId, userId));
        return client;
    }
}
