using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// <c>ProductController.Create</c> and <c>Update</c> had no <c>try/catch</c> at all. Every refusal
/// the repository can raise — a duplicate part number, a category id that is not this tenant's, a
/// value too long for its column — fell through to the global handler in <c>Program.cs</c> and
/// reached the caller as <c>{"error":"An unexpected error occurred."}</c>. A salesman who typed a
/// part number twice got the same sentence as a genuine server fault, and nothing on the screen
/// told him which.
///
/// <para>These tests fix the SHAPE of the answer: a duplicate is a 409 the caller can act on, a
/// bad reference or an over-length field is a 400 that names it, and everything else still reaches
/// the global handler, where it stays logged.</para>
/// </summary>
public sealed class ProductWriteFailureSurfacingTests
{
    private const long Bu = 9_790;

    private static PostgresException ValueTooLong() => new(
        messageText: "value too long for type character varying(100)",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "22001");

    private static PostgresException ForeignKeyViolation() => new(
        messageText: "insert or update on table \"Products\" violates foreign key constraint",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "23503");

    private static ProductController ControllerFor(ErpRfqAutomationContext context, IProductRepository repository) =>
        new(repository, context, new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.Email, "buyer@tenant.test")
                    ], "test"))
                }
            }
        };

    private static ProductCreateRequestDTO CreateRequest() => new()
    {
        PartNo = "PN-DUP-1",
        ProductName = "Ball valve",
        CreatedBy = "ignored — the token is authoritative",
        Buid = Bu
    };

    private static ProductUpdateRequestDTO UpdateRequest() => new()
    {
        PartNo = "PN-DUP-1",
        ProductName = "Ball valve",
        ModifiedBy = "ignored — the token is authoritative",
        Buid = Bu
    };

    // ---- Create -------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_product_whose_part_number_is_taken_is_a_conflict_not_a_500()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new ArgumentException("PartNo PN-DUP-1 already exists in this Business Unit.")));

        var result = await controller.Create(CreateRequest());

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Contains("PN-DUP-1", problem.Detail);
    }

    [Fact]
    public async Task Creating_a_product_against_a_category_that_is_not_this_tenants_is_a_400_that_names_it()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new ArgumentException("Category ID 44 does not exist in this Business Unit.")));

        var result = await controller.Create(CreateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("Category ID 44", problem.Detail);
    }

    [Fact]
    public async Task Creating_a_product_with_a_value_too_long_for_its_column_is_a_400_that_says_so()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new DbUpdateException("insert failed", ValueTooLong())));

        var result = await controller.Create(CreateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("too long", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The correction that matters most. A blanket <c>catch (DbUpdateException)</c> would report a
    /// foreign-key violation, a unique violation, an RLS denial (42501 — this codebase is
    /// deny-by-default under <c>nexora_tenant_isolation</c>) and a serialization failure from the
    /// serializable transaction in <c>AllocateProductDocIdAsync</c> all as "shorten the product
    /// name", and would swallow the log entry that says what really happened. Only 22001 is
    /// claimed; everything else must still escape to the global handler.
    /// </summary>
    [Fact]
    public async Task A_database_failure_that_is_not_an_over_long_value_still_reaches_the_global_handler()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new DbUpdateException("insert failed", ForeignKeyViolation())));

        var escaped = await Assert.ThrowsAsync<DbUpdateException>(() => controller.Create(CreateRequest()));

        Assert.Equal("23503", Assert.IsType<PostgresException>(escaped.InnerException).SqlState);
    }

    // ---- Update -------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_product_onto_a_taken_part_number_is_a_conflict_not_a_500()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new ArgumentException("PartNo PN-DUP-1 already exists in this Business Unit."), existing: Existing()));

        var result = await controller.Update(1, UpdateRequest());

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Contains("PN-DUP-1", Assert.IsType<ProblemDetails>(conflict.Value).Detail);
    }

    [Fact]
    public async Task Editing_a_product_to_a_value_too_long_for_its_column_is_a_400_that_says_so()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new DbUpdateException("update failed", ValueTooLong()), existing: Existing()));

        var result = await controller.Update(1, UpdateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Contains("too long", Assert.IsType<ProblemDetails>(bad.Value).Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edit_that_fails_for_some_other_database_reason_still_reaches_the_global_handler()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new DbUpdateException("update failed", ForeignKeyViolation()), existing: Existing()));

        var escaped = await Assert.ThrowsAsync<DbUpdateException>(() => controller.Update(1, UpdateRequest()));

        Assert.Equal("23503", Assert.IsType<PostgresException>(escaped.InnerException).SqlState);
    }

    // ---- ArgumentException must not swallow its own subclasses --------------------------

    /// <summary>
    /// THE LIE THIS PREVENTS. <c>ArgumentNullException</c> derives from <c>ArgumentException</c>, so
    /// an unfiltered <c>catch (ArgumentException)</c> claims it too — and there is a real path that
    /// throws one: <c>ProductRepository.PersistAttachmentAsync</c> calls
    /// <c>Path.Combine(_environment.WebRootPath, subFolder)</c>, and <c>WebRootPath</c> is null on
    /// any deployment without a <c>wwwroot</c> directory. A product saved WITH an attachment on such
    /// a deployment would then be reported to the user as a taken part number or a bad category id.
    /// That is a sentence about their data describing a fault in ours: they go and change a part
    /// number that was never wrong, the save fails again, and the log entry that would have named
    /// the real cause was consumed by the catch.
    ///
    /// <para>Excluded, it escapes to the global handler in <c>Program.cs</c>, which logs it. The
    /// caller still gets a 500 — correctly, because a missing wwwroot IS a server fault.</para>
    /// </summary>
    [Fact]
    public async Task A_null_argument_bug_in_our_own_code_is_not_reported_as_a_duplicate_or_a_bad_field()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            // What Path.Combine(null, "products") actually throws.
            new ArgumentNullException("path1", "Value cannot be null.")));

        var escaped = await Assert.ThrowsAsync<ArgumentNullException>(() => controller.Create(CreateRequest()));

        Assert.Equal("path1", escaped.ParamName);
    }

    /// <summary><c>ArgumentOutOfRangeException</c> is the same story and the same base class.</summary>
    [Fact]
    public async Task An_out_of_range_argument_bug_is_not_reported_as_a_bad_field_either()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new ArgumentOutOfRangeException("index", "Index was out of range.")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.Create(CreateRequest()));
    }

    /// <summary>The edit door carries the identical filter, so it carries the identical proof.</summary>
    [Fact]
    public async Task A_null_argument_bug_on_the_edit_path_is_not_reported_as_a_duplicate_either()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new ArgumentNullException("path1", "Value cannot be null."), existing: Existing()));

        var escaped = await Assert.ThrowsAsync<ArgumentNullException>(() => controller.Update(1, UpdateRequest()));

        Assert.Equal("path1", escaped.ParamName);
    }

    /// <summary>
    /// And the genuine ArgumentException — the one the repository raises deliberately — must still
    /// be claimed. Excluding the subclasses must not have excluded the base case with them.
    /// </summary>
    [Fact]
    public async Task A_plain_ArgumentException_from_the_repository_is_still_turned_into_an_answer()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingProductRepository(
            new ArgumentException("Category ID 44 does not exist in this Business Unit.")));

        var result = await controller.Create(CreateRequest());

        Assert.Equal(StatusCodes.Status400BadRequest,
            Assert.IsType<ProblemDetails>(Assert.IsType<BadRequestObjectResult>(result.Result).Value).Status);
    }

    private static Product Existing() => new()
    {
        Id = 1, Buid = Bu, PartNo = "PN-OLD", ProductName = "Ball valve",
        CreatedBy = "seed", CreatedOn = DateTime.UtcNow, IsActive = true
    };

    /// <summary>
    /// A repository whose write path fails the way the real one does. Everything else throws, so a
    /// test that accidentally exercises another member fails loudly instead of quietly passing.
    /// </summary>
    private sealed class ThrowingProductRepository(Exception failure, Product? existing = null) : IProductRepository
    {
        public Task AddAsync(Product product, List<IFormFile>? attachments) => Task.FromException(failure);

        public Task UpdateAsync(Product product, long businessUnitId, List<IFormFile>? attachments)
            => Task.FromException(failure);

        public Task<Product> GetByIdAsync(long id, long businessUnitId)
            => existing is null
                ? Task.FromException<Product>(new InvalidOperationException("GetByIdAsync was not expected here."))
                : Task.FromResult(existing);

        public Task<(IEnumerable<ProductResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null, bool? isActive = null) => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task<List<BusinessUnitLookupDTO>> GetActiveBusinessUnitsAsync() => throw new NotSupportedException();
        public Task<List<ProductCategoryLookupDTO>> GetProductCategoriesAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<List<LookupItemDTO>> GetItemStatusesAsync() => throw new NotSupportedException();
        public Task<List<SupplierLookupDTO>> GetSuppliersAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<List<ProductSubCategoryLookupDTO>> GetProductSubCategoriesAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<List<WarehouseLookupDTO>> GetWarehousesAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<List<LookupItemDTO>> GetUomsAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<ProductMatchResponseDTO> MatchProductAsync(ProductMatchRequestDTO request) => throw new NotSupportedException();
        public Task<StockDetailsDTO> GetStockDetailsAsync(long productId, long businessUnitId) => throw new NotSupportedException();
        public Task<PurchaseHistoryDTO> GetPurchaseHistoryAsync(long productId, long businessUnitId) => throw new NotSupportedException();
    }
}
