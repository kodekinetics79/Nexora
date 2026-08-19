using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Sec-A1 for the product write path.
///
/// <para>POST /api/Product used to answer "The CreatedBy field is required" because the create
/// request carried a client-supplied actor, and it carried a client-supplied <c>Buid</c> beside it.
/// Both are server-authoritative under the tenant/audit rules: attribution a caller can type is
/// forgeable attribution, and a tenant id in a request body is a cross-tenant write vector the
/// moment anyone reads it. These tests drive the real MVC form binder so a forged field travels the
/// same road a real request would, and they assert the STORED row rather than the response echo.</para>
/// </summary>
public sealed class ProductWriteActorAndTenantAuthorityTests
{
    private const long TenantA = 8_101;
    private const long TenantB = 8_102;
    private const string ActorEmail = "buyer@tenant-a.test";
    private const string ForgedActor = "ceo@tenant-a.test";

    [Fact]
    public void Create_and_update_requests_carry_no_actor_or_tenant_field()
    {
        foreach (var contract in new[] { typeof(ProductCreateRequestDTO), typeof(ProductUpdateRequestDTO) })
        {
            foreach (var forbidden in new[] { "CreatedBy", "ModifiedBy", "Buid", "BusinessUnitId" })
            {
                Assert.Null(contract.GetProperty(forbidden,
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance
                    | System.Reflection.BindingFlags.IgnoreCase));
            }
        }

        // The reported symptom, at the layer that produced it: a complete product now validates.
        var request = new ProductCreateRequestDTO { PartNo = "PN-VALID", ProductName = "Valid product" };
        var failures = new List<ValidationResult>();

        Assert.True(Validator.TryValidateObject(
            request, new ValidationContext(request), failures, validateAllProperties: true),
            "A product create request must not require a client-supplied actor: "
            + string.Join("; ", failures.Select(x => x.ErrorMessage)));
    }

    [Fact]
    public async Task Creating_a_product_without_an_actor_field_stamps_the_authenticated_user()
    {
        using var database = new TestDb();
        SeedTenants(database);

        await using var context = database.ContextFor(TenantA);
        var controller = ControllerFor(context, TenantA, ActorEmail);
        var request = await BindFormAsync<ProductCreateRequestDTO>(controller, new()
        {
            ["ProductName"] = "Hydraulic pump",
            ["PartNo"] = "PN-CREATE",
            ["QtyOnHand"] = "0",
            ["ReorderPoint"] = "5",
        });

        var result = await controller.Create(request);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var stored = StoredProduct(database, "PN-CREATE");
        Assert.Equal(ActorEmail, stored.CreatedBy);
        Assert.Equal(TenantA, stored.Buid);
    }

    [Fact]
    public async Task Forged_actor_and_tenant_fields_on_create_are_not_honoured()
    {
        using var database = new TestDb();
        SeedTenants(database);

        await using var context = database.ContextFor(TenantA);
        var controller = ControllerFor(context, TenantA, ActorEmail);
        var request = await BindFormAsync<ProductCreateRequestDTO>(controller, new()
        {
            ["ProductName"] = "Forged pump",
            ["PartNo"] = "PN-FORGED",
            ["QtyOnHand"] = "0",
            ["ReorderPoint"] = "5",
            // The forgery: someone else's name in the audit trail, and another tenant's id.
            ["CreatedBy"] = ForgedActor,
            ["Buid"] = TenantB.ToString(CultureInfo.InvariantCulture),
            ["BusinessUnitId"] = TenantB.ToString(CultureInfo.InvariantCulture),
        });

        // The binder had nowhere to put either field, so the forged values never entered the model
        // at all — the server is not merely overwriting a value it accepted.
        Assert.DoesNotContain("CreatedBy", controller.ModelState.Keys);
        Assert.DoesNotContain("Buid", controller.ModelState.Keys);

        var result = await controller.Create(request);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        var stored = StoredProduct(database, "PN-FORGED");
        Assert.Equal(ActorEmail, stored.CreatedBy);
        Assert.NotEqual(ForgedActor, stored.CreatedBy);
        Assert.Equal(TenantA, stored.Buid);
    }

    [Fact]
    public async Task Forged_actor_and_tenant_fields_on_update_are_not_honoured()
    {
        using var database = new TestDb();
        SeedTenants(database);
        long productId;
        await using (var seed = database.ContextFor(null))
        {
            var product = new Product
            {
                Buid = TenantA, PartNo = "PN-UPDATE", ProductName = "Original name", DocId = "PR00000001",
                QtyOnHand = 0m, ReorderPoint = 1m, IsActive = true,
                CreatedBy = "seed@tenant-a.test", CreatedOn = DateTime.UtcNow,
            };
            seed.Products.Add(product);
            await seed.SaveChangesAsync();
            productId = product.Id;
        }

        await using var context = database.ContextFor(TenantA);
        var controller = ControllerFor(context, TenantA, ActorEmail);
        var request = await BindFormAsync<ProductUpdateRequestDTO>(controller, new()
        {
            ["ProductName"] = "Renamed product",
            ["PartNo"] = "PN-UPDATE",
            ["QtyOnHand"] = "0",
            ["ReorderPoint"] = "9",
            ["ModifiedBy"] = ForgedActor,
            ["Buid"] = TenantB.ToString(CultureInfo.InvariantCulture),
        });

        Assert.DoesNotContain("ModifiedBy", controller.ModelState.Keys);
        Assert.DoesNotContain("Buid", controller.ModelState.Keys);

        var result = await controller.Update(productId, request);

        Assert.IsType<OkObjectResult>(result.Result);
        var stored = StoredProduct(database, "PN-UPDATE");
        Assert.Equal("Renamed product", stored.ProductName);
        Assert.Equal(ActorEmail, stored.ModifiedBy);
        Assert.NotEqual(ForgedActor, stored.ModifiedBy);
        // The record stays where it was created, and its creator is untouched by an edit.
        Assert.Equal(TenantA, stored.Buid);
        Assert.Equal("seed@tenant-a.test", stored.CreatedBy);
    }

    [Fact]
    public async Task A_product_created_in_one_tenant_is_not_visible_to_another()
    {
        using var database = new TestDb();
        SeedTenants(database);

        await using (var contextA = database.ContextFor(TenantA))
        {
            var controllerA = ControllerFor(contextA, TenantA, ActorEmail);
            var request = await BindFormAsync<ProductCreateRequestDTO>(controllerA, new()
            {
                ["ProductName"] = "Tenant A only",
                ["PartNo"] = "PN-ISOLATED",
                ["QtyOnHand"] = "0",
                ["ReorderPoint"] = "2",
                // Aimed at the other tenant on the way in.
                ["Buid"] = TenantB.ToString(CultureInfo.InvariantCulture),
            });
            Assert.IsType<CreatedAtActionResult>((await controllerA.Create(request)).Result);
        }

        var created = StoredProduct(database, "PN-ISOLATED");
        Assert.Equal(TenantA, created.Buid);

        await using var contextB = database.ContextFor(TenantB);
        var controllerB = ControllerFor(contextB, TenantB, "buyer@tenant-b.test");

        var list = Assert.IsType<OkObjectResult>((await controllerB.GetAll()).Result).Value;
        Assert.Empty(Assert.IsType<PaginatedProductResponseDTO>(list).Items);
        Assert.IsType<NotFoundResult>((await controllerB.GetById(created.Id)).Result);
    }

    private static void SeedTenants(TestDb database)
    {
        using var seed = database.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, TenantA);
        Seed.EnsureBusinessUnit(seed, TenantB);
        seed.SaveChanges();
    }

    private static Product StoredProduct(TestDb database, string partNo)
    {
        using var verify = database.ContextFor(null);
        return verify.Products.AsNoTracking().Single(x => x.PartNo == partNo);
    }

    private static ProductController ControllerFor(
        ErpRfqAutomationContext context, long businessUnitId, string actorEmail)
    {
        var repository = new ProductRepository(context, new TestEnvironment(), new ClearingFileInspection());
        return new ProductController(repository, context, new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = MvcServices,
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", businessUnitId.ToString(CultureInfo.InvariantCulture)),
                        new Claim(ClaimTypes.Email, actorEmail),
                        new Claim(ClaimTypes.NameIdentifier, "user-" + businessUnitId),
                    ], "test")),
                }
            }
        };
    }

    /// <summary>
    /// Binds a form through the real MVC model binder, so a field the request contract no longer
    /// declares is dropped exactly where a live request would drop it, and the resulting ModelState
    /// is the one the action sees.
    /// </summary>
    private static async Task<TModel> BindFormAsync<TModel>(
        ProductController controller, Dictionary<string, StringValues> fields)
        where TModel : class, new()
    {
        var form = new FormCollection(fields);
        controller.HttpContext.Request.Method = HttpMethods.Post;
        controller.HttpContext.Request.ContentType = "application/x-www-form-urlencoded";
        controller.HttpContext.Request.Form = form;

        var model = new TModel();
        await controller.TryUpdateModelAsync(model, prefix: string.Empty,
            valueProvider: new FormValueProvider(BindingSource.Form, form, CultureInfo.InvariantCulture));
        return model;
    }

    private static readonly IServiceProvider MvcServices = BuildMvcServices();

    private static IServiceProvider BuildMvcServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMvcCore().AddDataAnnotations();
        return services.BuildServiceProvider();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "ProductWriteAuthorityTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
