using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class ProcurementAuthenticatedHttpTests(Release01BHttpApplication app)
{
    public static TheoryData<string, string> CriticalRoutes => new()
    {
        { HttpMethod.Get.Method, $"/api/procurement/rfqs/{Release01BHttpApplication.TenantAProcurementRfqId}/workbench" },
        { HttpMethod.Post.Method, "/api/procurement/sourcing-cases" },
        { HttpMethod.Get.Method, "/api/procurement/sourcing-cases/1" },
        { HttpMethod.Post.Method, "/api/procurement/sourcing-cases/1/supplier-candidates/search" },
        { HttpMethod.Post.Method, "/api/procurement/sourcing-cases/1/supplier-rfqs" },
        { HttpMethod.Post.Method, "/api/procurement/sourcing-cases/1/supplier-rfqs/1/queue" },
        { HttpMethod.Post.Method, "/api/procurement/solicitations" },
        { HttpMethod.Post.Method, "/api/procurement/supplier-quotes" },
        { HttpMethod.Get.Method, $"/api/procurement/rfq-items/{Release01BHttpApplication.TenantAProcurementRfqItemId}/quote-comparison" },
        { HttpMethod.Post.Method, "/api/procurement/awards" },
        { HttpMethod.Get.Method, "/api/procurement/purchase-orders" },
        { HttpMethod.Post.Method, "/api/procurement/purchase-orders" },
        { HttpMethod.Post.Method, "/api/procurement/purchase-orders/1/approve" },
        { HttpMethod.Post.Method, "/api/procurement/purchase-orders/1/issue" },
        { HttpMethod.Post.Method, "/api/procurement/goods-receipts" },
        { HttpMethod.Get.Method, "/api/procurement-integrations/status" },
        { HttpMethod.Post.Method, "/api/procurement-integrations/callbacks" }
    };

    [Fact]
    public async Task Integration_status_is_tenant_scoped_and_truthful_when_not_configured()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        using var response = await client.GetAsync("/api/procurement-integrations/status");

        await AssertStatusAsync(response, HttpStatusCode.OK);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(payload.RootElement.GetProperty("isConfigured").GetBoolean());
        Assert.Equal("NOT_INTEGRATED", payload.RootElement.GetProperty("connectorStatus").GetString());
    }

    [Theory]
    [MemberData(nameof(CriticalRoutes))]
    public async Task Critical_procurement_routes_challenge_unauthenticated_requests(string method, string path)
    {
        using var client = app.CreateClient();

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(CriticalRoutes))]
    public async Task Critical_procurement_routes_forbid_roles_without_required_permissions(string method, string path)
    {
        using var client = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);

        using var response = await client.SendAsync(Request(method, path));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Procurement_routes_require_the_authenticated_tenant_claim()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, tenantId: null);

        using var response = await client.GetAsync(
            $"/api/procurement/rfqs/{Release01BHttpApplication.TenantAProcurementRfqId}/workbench");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Direct_solicitation_route_is_retired_in_favour_of_governed_sourcing_cases()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        using var response = await SendJsonAsync(client, HttpMethod.Post, "/api/procurement/solicitations", new
        {
            RfqId = Release01BHttpApplication.TenantAProcurementRfqId,
            SupplierId = Release01BHttpApplication.TenantAProcurementSupplierId,
            RfqItemIds = new[] { Release01BHttpApplication.TenantAProcurementRfqItemId }
        }, "retired-direct-solicitation");

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
    }

    [Fact]
    public async Task Permitted_procurement_journey_is_tenant_scoped_from_workbench_through_receipt()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);

        using var workbench = await client.GetAsync(
            $"/api/procurement/rfqs/{Release01BHttpApplication.TenantAProcurementRfqId}/workbench");
        await AssertStatusAsync(workbench, HttpStatusCode.OK);
        using (var payload = JsonDocument.Parse(await workbench.Content.ReadAsStringAsync()))
        {
            Assert.Equal(Release01BHttpApplication.TenantAProcurementRfqId,
                payload.RootElement.GetProperty("rfqId").GetInt64());
            Assert.Equal(Release01BHttpApplication.TenantAProcurementRfqItemId,
                payload.RootElement.GetProperty("lines")[0].GetProperty("id").GetInt64());
        }

        var solicitationId = await app.CreateLegacyFixtureSolicitationAsync();

        using var quote = await SendJsonAsync(client, HttpMethod.Post, "/api/procurement/supplier-quotes", new
        {
            SolicitationId = solicitationId,
            SupplierQuoteReference = "HTTP-SUPPLIER-QUOTE-1",
            Revision = 1,
            ValidUntil = DateTime.UtcNow.AddDays(30),
            Lines = new[]
            {
                new
                {
                    RfqItemId = Release01BHttpApplication.TenantAProcurementRfqItemId,
                    ProductId = Release01BHttpApplication.TenantAProcurementProductId,
                    Quantity = 8m,
                    UnitPrice = 10m,
                    CurrencyId = Release01BHttpApplication.TenantAProcurementCurrencyId,
                    LeadTimeDays = 5,
                    AvailableQuantity = 8m,
                    FreightCost = 4m,
                    DutyCost = 2m,
                    OtherCost = 1m,
                    TaxAmount = 1m,
                    DiscountAmount = 0m,
                    MinimumOrderQuantity = 1m,
                    ReliabilitySnapshot = 95m
                }
            }
        }, "quote");
        await AssertStatusAsync(quote, HttpStatusCode.Created);
        var quotedItemId = await FirstLineIdAsync(quote);

        using var comparison = await client.GetAsync(
            $"/api/procurement/rfq-items/{Release01BHttpApplication.TenantAProcurementRfqItemId}/quote-comparison");
        await AssertStatusAsync(comparison, HttpStatusCode.OK);
        using (var payload = JsonDocument.Parse(await comparison.Content.ReadAsStringAsync()))
        {
            Assert.Equal(quotedItemId,
                payload.RootElement.GetProperty("recommendedSupplierQuotedItemId").GetInt64());
        }

        using var award = await SendJsonAsync(client, HttpMethod.Post, "/api/procurement/awards", new
        {
            SupplierQuotedItemId = quotedItemId,
            Quantity = 8m,
            ExpectedQuoteVersion = 1,
            Rationale = "Best eligible landed cost in authenticated HTTP acceptance"
        }, "award");
        await AssertStatusAsync(award, HttpStatusCode.Created);
        var awardId = await IdAsync(award);

        using var purchaseOrder = await SendJsonAsync(client, HttpMethod.Post, "/api/procurement/purchase-orders", new
        {
            RfqId = Release01BHttpApplication.TenantAProcurementRfqId,
            SupplierId = Release01BHttpApplication.TenantAProcurementSupplierId,
            CurrencyId = Release01BHttpApplication.TenantAProcurementCurrencyId,
            WarehouseId = Release01BHttpApplication.TenantAProcurementWarehouseId,
            ExpectedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(10)),
            AwardIds = new[] { awardId }
        }, "purchase-order");
        await AssertStatusAsync(purchaseOrder, HttpStatusCode.Created);
        var purchaseOrderId = await IdAsync(purchaseOrder);
        var purchaseOrderNumber = await StringPropertyAsync(purchaseOrder, "number");

        using var register = await client.GetAsync(
            $"/api/procurement/purchase-orders?search={Uri.EscapeDataString(purchaseOrderNumber)}&limit=10&businessUnitId={Release01BHttpApplication.TenantB}");
        await AssertStatusAsync(register, HttpStatusCode.OK);
        using (var payload = JsonDocument.Parse(await register.Content.ReadAsStringAsync()))
        {
            var row = Assert.Single(payload.RootElement.EnumerateArray());
            Assert.Equal(purchaseOrderId, row.GetProperty("id").GetInt64());
            Assert.Equal(Release01BHttpApplication.TenantAProcurementRfqId, row.GetProperty("rfqId").GetInt64());
        }

        // FR-SPO-01. The award above was approved by this client's user; segregation of duties
        // refuses the same user approving the purchase order, so the buyer who approves it is a
        // different authenticated user on the same role and tenant.
        using var selfApproval = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/procurement/purchase-orders/{purchaseOrderId}/approve", new { ExpectedVersion = 1 },
            "self-approve");
        await AssertStatusAsync(selfApproval, HttpStatusCode.BadRequest);

        using var secondBuyer = Client(Release01BHttpApplication.AllowedRole,
            Release01BHttpApplication.TenantA, userId: 83_002);
        using var approve = await SendJsonAsync(secondBuyer, HttpMethod.Post,
            $"/api/procurement/purchase-orders/{purchaseOrderId}/approve", new { ExpectedVersion = 1 },
            "approve");
        await AssertStatusAsync(approve, HttpStatusCode.OK);

        var deliveredOn = DateTime.UtcNow;
        using var issue = await SendJsonAsync(client, HttpMethod.Post,
            $"/api/procurement/purchase-orders/{purchaseOrderId}/issue", new
            {
                ExpectedVersion = 2,
                DeliveryEvidenceReference = $"provider-receipt:http-{purchaseOrderId}",
                DeliveryEvidenceSha256 = new string('a', 64),
                DeliveredOn = deliveredOn
            }, "issue");
        await AssertStatusAsync(issue, HttpStatusCode.OK);

        var receiptState = await app.PurchaseOrderReceiptStateAsync(purchaseOrderId);
        using var receipt = await SendJsonAsync(client, HttpMethod.Post, "/api/procurement/goods-receipts", new
        {
            PurchaseOrderId = purchaseOrderId,
            WarehouseId = Release01BHttpApplication.TenantAProcurementWarehouseId,
            ReceiptNumber = $"HTTP-GR-{purchaseOrderId}",
            ReceivedOn = DateTime.UtcNow,
            ExpectedPurchaseOrderVersion = receiptState.Version,
            Lines = new[] { new { PurchaseOrderLineId = receiptState.LineId, Quantity = 8m } }
        }, "receipt");
        await AssertStatusAsync(receipt, HttpStatusCode.Created);
        var receiptId = await IdAsync(receipt);

        using var crossTenant = await client.GetAsync(
            $"/api/procurement/rfqs/{Release01BHttpApplication.TenantBProcurementRfqId}/workbench?businessUnitId={Release01BHttpApplication.TenantB}");
        await AssertStatusAsync(crossTenant, HttpStatusCode.NotFound);

        var ownership = await app.ProcurementOwnershipAsync(solicitationId, purchaseOrderId, receiptId);
        Assert.Equal(Release01BHttpApplication.TenantA, ownership.SolicitationTenantId);
        Assert.Equal(Release01BHttpApplication.TenantA, ownership.PurchaseOrderTenantId);
        Assert.Equal(Release01BHttpApplication.TenantA, ownership.ReceiptTenantId);
    }

    private HttpClient Client(long roleId, long? tenantId, long userId = 83_001)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId, userId));
        return client;
    }

    private static HttpRequestMessage Request(string method, string path) => new(new HttpMethod(method), path)
    {
        Content = method == HttpMethod.Post.Method
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : null
    };

    private static async Task<HttpResponseMessage> SendJsonAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object body,
        string operation)
    {
        using var request = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body)
        };
        var unique = $"http-procurement-{operation}-{Guid.NewGuid():N}";
        request.Headers.Add("Idempotency-Key", unique);
        request.Headers.Add("X-Correlation-ID", unique);
        return await client.SendAsync(request);
    }

    private static async Task<long> IdAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("id").GetInt64();
    }

    private static async Task<long> FirstLineIdAsync(HttpResponseMessage response)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty("lineIds")[0].GetInt64();
    }

    private static async Task<string> StringPropertyAsync(HttpResponseMessage response, string property)
    {
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return payload.RootElement.GetProperty(property).GetString()!;
    }

    private static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected,
            $"Expected {(int)expected}, received {(int)response.StatusCode}: {body}");
    }
}
