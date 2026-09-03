using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

/// <summary>
/// POST /api/commercial-intelligence/follow-ups — the door a rep uses to set a follow-up on a
/// quote by hand. <c>CreateFollowUpAsync</c> had no endpoint; the only follow-ups in the product
/// were the ones quote delivery created on its own.
/// </summary>
[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class QuoteFollowUpAuthenticatedHttpTests(Release01BHttpApplication app)
{
    // Distinct from every id block the shared fixture uses (see Release01BHttpApplication).
    private const long QuoteEditorRole = 82_901;
    private const long QuoteEditorUser = 83_901;
    private const long QuoteEditorPermission = 85_901;
    private const long QuotationsModuleId = 84_009;
    private const long TenantAQuote = 88_901;
    private const long TenantBQuote = 88_902;

    [Fact]
    public async Task A_rep_with_Quotations_Edit_can_set_a_follow_up_on_their_own_tenant_quote()
    {
        await PrepareAsync();
        var key = $"quote-follow-up-{Guid.NewGuid():N}";
        var dueAt = DateTime.UtcNow.Date.AddDays(3);

        using var anonymous = app.CreateClient();
        using var anonymousResponse = await anonymous.SendAsync(Create(TenantAQuote, dueAt, key));
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);

        // Quotations: View only — the fixture's reader role. Edit is the grant this endpoint names.
        using var viewer = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.GrowthRepUser);
        using var viewerResponse = await viewer.SendAsync(Create(TenantAQuote, dueAt, key));
        Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

        using var editor = Client(QuoteEditorRole, QuoteEditorUser);

        using var crossTenant = await editor.SendAsync(Create(TenantBQuote, dueAt, key));
        Assert.Equal(HttpStatusCode.NotFound, crossTenant.StatusCode);

        using var blankReason = await editor.SendAsync(Create(TenantAQuote, dueAt, key, reason: "   "));
        Assert.Equal(HttpStatusCode.BadRequest, blankReason.StatusCode);

        using var created = await editor.SendAsync(Create(TenantAQuote, dueAt, key));
        Assert.True(created.StatusCode == HttpStatusCode.Created,
            $"expected 201, got {(int)created.StatusCode}: {await created.Content.ReadAsStringAsync()}");
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var followUpId = body.RootElement.GetProperty("id").GetInt64();
        Assert.Equal(TenantAQuote, body.RootElement.GetProperty("quoteId").GetInt64());
        Assert.Equal("Call about the price hold", body.RootElement.GetProperty("reason").GetString());
        Assert.Equal("Open", body.RootElement.GetProperty("status").GetString());

        // Same key, same content: a retry is the same follow-up, not a second one.
        using var replay = await editor.SendAsync(Create(TenantAQuote, dueAt, key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        using var replayBody = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
        Assert.Equal(followUpId, replayBody.RootElement.GetProperty("id").GetInt64());

        // It lands in the list the Follow-ups screen reads, assigned to the caller.
        using var list = await editor.GetAsync($"/api/commercial-intelligence/follow-ups?sourceId={followUpId}");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var rows = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var row = Assert.Single(rows.RootElement.EnumerateArray());
        Assert.Equal(TenantAQuote, row.GetProperty("quoteId").GetInt64());
        Assert.Equal(QuoteEditorUser, row.GetProperty("ownerUserId").GetInt64());
        Assert.Equal("Call about the price hold", row.GetProperty("reason").GetString());
    }

    private HttpClient Client(long roleId, long userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, Release01BHttpApplication.TenantA, userId));
        return client;
    }

    private static HttpRequestMessage Create(long quoteId, DateTime dueAt, string key, string reason = "Call about the price hold")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/commercial-intelligence/follow-ups")
        {
            Content = JsonContent.Create(new { QuoteId = quoteId, DueAt = dueAt, Reason = reason }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private async Task PrepareAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        if (!await db.SetupMasters.IgnoreQueryFilters().AnyAsync(x => x.SetupId == QuoteEditorRole))
        {
            db.SetupMasters.Add(Release01BHttpApplication.Role(QuoteEditorRole, Release01BHttpApplication.TenantA, "Quote Follow-up Editor"));
            db.RolePermissions.Add(new RolePermission
            {
                Id = QuoteEditorPermission, RoleId = QuoteEditorRole, ModuleId = QuotationsModuleId,
                BusinessUnitId = Release01BHttpApplication.TenantA, CanCreate = true, CanEdit = true, CanDelete = false,
                CreatedBy = "quote-follow-up-tests", CreatedOn = DateTime.UtcNow,
            });
            db.Users.Add(new User
            {
                Id = QuoteEditorUser, Buid = Release01BHttpApplication.TenantA, RoleId = QuoteEditorRole,
                FirstName = "Quote", LastName = "Editor", Email = "quote-editor@nexora.invalid",
                PasswordHash = "not-used", ImageUrl = "n/a", IsActive = true,
                CreatedBy = "quote-follow-up-tests", CreatedOn = DateTime.UtcNow,
            });
        }
        if (!await db.Quotes.IgnoreQueryFilters().AnyAsync(x => x.Id == TenantAQuote))
        {
            db.Quotes.AddRange(
                SeedQuote(TenantAQuote, Release01BHttpApplication.TenantA, Release01BHttpApplication.TenantACustomerId, "QT-FU-A"),
                SeedQuote(TenantBQuote, Release01BHttpApplication.TenantB, Release01BHttpApplication.TenantBCustomerId, "QT-FU-B"));
        }
        await db.SaveChangesAsync();
        // The shared fixture seeds follow_up_tasks with explicit ids; the identity sequence does
        // not advance past them, so the first generated id can collide. Same high-water fix the
        // inventory PostgreSQL tests apply.
        await db.Database.ExecuteSqlRawAsync(
            "SELECT setval(pg_get_serial_sequence('follow_up_tasks', 'Id'), COALESCE((SELECT MAX(\"Id\") FROM follow_up_tasks), 0) + 1, false)");
    }

    private static Quote SeedQuote(long id, long tenant, long customerId, string quoteNo) => new()
    {
        Id = id, QuoteNo = quoteNo, BusinessUnitId = tenant, CustomerId = customerId,
        QuoteDate = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddDays(30),
        TotalAmount = 100, CreatedBy = "quote-follow-up-tests", CreatedDate = DateTime.UtcNow,
    };
}
