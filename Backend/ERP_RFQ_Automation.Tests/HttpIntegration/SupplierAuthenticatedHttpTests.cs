using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class SupplierAuthenticatedHttpTests(Release01BHttpApplication app)
{
    public static TheoryData<string, string> CriticalRoutes => new()
    {
        { HttpMethod.Get.Method, "/api/Supplier" },
        { HttpMethod.Get.Method, $"/api/Supplier/{Release01BHttpApplication.TenantAProcurementSupplierId}" },
        { HttpMethod.Post.Method, "/api/Supplier" },
        { HttpMethod.Put.Method, $"/api/Supplier/{Release01BHttpApplication.TenantAProcurementSupplierId}" },
        { HttpMethod.Delete.Method, $"/api/Supplier/{Release01BHttpApplication.TenantAProcurementSupplierId}" },
        { HttpMethod.Post.Method, "/api/Supplier/compose-quote-email" }
    };

    [Theory]
    [MemberData(nameof(CriticalRoutes))]
    public async Task Supplier_routes_challenge_unauthenticated_requests(string method, string path)
    {
        using var response = await app.CreateClient().SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CriticalRoutes))]
    public async Task Supplier_routes_forbid_roles_without_supplier_permissions(string method, string path)
    {
        using var client = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        using var response = await client.SendAsync(Request(method, path));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Supplier_read_cannot_cross_the_authenticated_tenant()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var response = await client.GetAsync(
            $"/api/Supplier/{Release01BHttpApplication.TenantBProcurementSupplierId}?businessUnitId={Release01BHttpApplication.TenantB}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private HttpClient Client(long roleId, long? tenantId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId));
        return client;
    }

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method is "POST" or "PUT"
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : null
    };
}
