using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.ProductCategory;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE SAME DEFECT PRODUCTS HAD, ONE SCREEN ACROSS. <c>ProductCategoryCreateRequestDTO.Description</c>
/// was <c>[StringLength(500)]</c> over <c>ProductCategories."Description"</c>, which is
/// <c>character varying(255)</c>. A 300-character category description therefore passed ModelState,
/// reached the INSERT and died there as Postgres <c>22001</c>. <c>ProductCategoryController.Create</c>
/// had only a blanket <c>catch (Exception)</c> answering <c>this.ServerError(...)</c>, so what came
/// back was <c>{"error":"Error."}</c> with a 500 — indistinguishable from the database being down,
/// and silent about the one thing the operator could have acted on.
///
/// <para>These tests pin the SHAPE of every answer this screen can give: a duplicate name is a 409,
/// an over-long value or a bad parent is a 400 that names it, and anything that is neither — a
/// foreign-key violation, an RLS denial, a null-argument bug in our own code — still reaches the
/// catch-all, which LOGS it and answers 500. The narrow catches must never be the thing that
/// removes a log entry.</para>
/// </summary>
public sealed class ProductCategoryWriteFailureSurfacingTests
{
    private const long Bu = 9_791;

    private static PostgresException ValueTooLong() => new(
        messageText: "value too long for type character varying(255)",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "22001");

    private static PostgresException ForeignKeyViolation() => new(
        messageText: "insert or update on table \"ProductCategories\" violates foreign key constraint",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "23503");

    private static ProductCategoryController ControllerFor(IProductCategoryRepository repository) =>
        new(repository)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                        new Claim(ClaimTypes.Email, "buyer@tenant.test")
                    ], "test"))
                }
            }
        };

    private static ProductCategoryCreateRequestDTO CreateRequest() => new()
    {
        CategoryName = "Valves",
        Description = "Ball and gate valves",
        BusinessUnitId = Bu
    };

    private static ProductCategoryUpdateRequestDTO UpdateRequest() => new()
    {
        CategoryName = "Valves",
        Description = "Ball and gate valves",
        BusinessUnitId = Bu
    };

    // ---- Create -------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_category_whose_name_is_taken_is_a_conflict_not_a_500()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new ArgumentException("Category name 'Valves' already exists in this Business Unit.")));

        var result = await controller.Create(CreateRequest());

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Contains("Valves", problem.Detail);
    }

    [Fact]
    public async Task Creating_a_category_under_a_parent_that_is_not_this_tenants_is_a_400_that_names_it()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new ArgumentException("Parent category does not exist or belongs to different Business Unit.")));

        var result = await controller.Create(CreateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("Parent category", problem.Detail);
    }

    /// <summary>
    /// The defect itself. Reverting the DTO cap to 500 puts this back on the wire as a 500 with the
    /// word "Error." in it.
    /// </summary>
    [Fact]
    public async Task Creating_a_category_with_a_value_too_long_for_its_column_is_a_400_that_says_so()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new DbUpdateException("insert failed", ValueTooLong())));

        var result = await controller.Create(CreateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("too long", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The correction that matters most. Only 22001 is claimed. A foreign-key violation, a unique
    /// violation and an RLS denial (42501 — this codebase is deny-by-default under
    /// <c>nexora_tenant_isolation</c>) must NOT be reported as "shorten the description"; they fall
    /// through to the catch-all, which logs and answers 500.
    /// </summary>
    [Fact]
    public async Task A_database_failure_that_is_not_an_over_long_value_is_not_reported_as_one()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new DbUpdateException("insert failed", ForeignKeyViolation())));

        var result = await controller.Create(CreateRequest());

        var server = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, server.StatusCode);
        Assert.IsNotType<ProblemDetails>(server.Value);
    }

    /// <summary>
    /// ArgumentNullException derives from ArgumentException. Caught by the conflict/bad-request
    /// handler it would be reported to the operator as a duplicate category name — a sentence about
    /// their data describing a bug in ours, sending them to fix a field that was never wrong. The
    /// filter excludes it, so it reaches the catch-all and stays logged.
    /// </summary>
    [Fact]
    public async Task A_null_argument_bug_in_our_own_code_is_never_reported_as_a_duplicate_or_a_bad_field()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new ArgumentNullException("path1", "Value cannot be null.")));

        var result = await controller.Create(CreateRequest());

        Assert.IsNotType<ConflictObjectResult>(result.Result);
        Assert.IsNotType<BadRequestObjectResult>(result.Result);
        var server = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, server.StatusCode);
    }

    /// <summary>
    /// ArgumentOutOfRangeException is the same story and the same base class.
    /// </summary>
    [Fact]
    public async Task An_out_of_range_argument_bug_is_not_reported_as_a_bad_field_either()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new ArgumentOutOfRangeException("index", "Index was out of range.")));

        var result = await controller.Create(CreateRequest());

        Assert.IsNotType<ConflictObjectResult>(result.Result);
        Assert.IsNotType<BadRequestObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result.Result).StatusCode);
    }

    // ---- Update -------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_category_onto_a_taken_name_is_a_conflict_not_a_500()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new ArgumentException("Category name 'Valves' already exists."), Existing()));

        var result = await controller.Update(1, UpdateRequest());

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        Assert.Contains("Valves", Assert.IsType<ProblemDetails>(conflict.Value).Detail);
    }

    [Fact]
    public async Task Editing_a_category_to_a_value_too_long_for_its_column_is_a_400_that_says_so()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new DbUpdateException("update failed", ValueTooLong()), Existing()));

        var result = await controller.Update(1, UpdateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("too long", Assert.IsType<ProblemDetails>(bad.Value).Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_edit_that_fails_for_some_other_database_reason_is_not_reported_as_an_over_long_value()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new DbUpdateException("update failed", ForeignKeyViolation()), Existing()));

        var result = await controller.Update(1, UpdateRequest());

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    [Fact]
    public async Task A_null_argument_bug_on_the_edit_path_is_not_reported_as_a_duplicate_either()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(
            new ArgumentNullException("path1", "Value cannot be null."), Existing()));

        var result = await controller.Update(1, UpdateRequest());

        Assert.IsNotType<ConflictObjectResult>(result);
        Assert.IsNotType<BadRequestObjectResult>(result);
        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result).StatusCode);
    }

    /// <summary>
    /// The narrow catches wrap ONLY the repository write. Everything before it — the tenant claim,
    /// the business-unit guard — must keep answering as it did, or a fix for one failure has
    /// rewritten the meaning of another.
    /// </summary>
    [Fact]
    public async Task A_successful_create_is_still_a_201_pointing_at_the_new_row()
    {
        var controller = ControllerFor(new ThrowingProductCategoryRepository(failure: null));

        var result = await controller.Create(CreateRequest());

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<ProductCategoryResponseDTO>(created.Value);
        Assert.Equal("Valves", response.CategoryName);
        Assert.Equal(Bu, response.BusinessUnitId);
    }

    private static ProductCategory Existing() => new()
    {
        Id = 1,
        BusinessUnitId = Bu,
        CategoryName = "Valves (old)",
        CreatedBy = "seed",
        CreatedOn = DateTime.UtcNow,
        IsActive = true
    };

    /// <summary>
    /// A repository whose write path fails the way the real one does. A null <paramref name="failure"/>
    /// means the write succeeds. Everything else throws, so a test that accidentally exercises
    /// another member fails loudly instead of quietly passing.
    /// </summary>
    private sealed class ThrowingProductCategoryRepository(Exception? failure, ProductCategory? existing = null)
        : IProductCategoryRepository
    {
        public Task AddAsync(ProductCategory category)
            => failure is null ? Task.CompletedTask : Task.FromException(failure);

        public Task UpdateAsync(ProductCategory category)
            => failure is null ? Task.CompletedTask : Task.FromException(failure);

        public Task<ProductCategory> GetByIdAsync(long id, long businessUnitId)
            => existing is null
                ? Task.FromException<ProductCategory>(new InvalidOperationException("GetByIdAsync was not expected here."))
                : Task.FromResult(existing);

        public Task<IEnumerable<ProductCategory>> GetAllAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<ProductCategory?> GetByIdWithParentAsync(long id, long businessUnitId) => throw new NotSupportedException();
        public Task DeleteAsync(long id, long businessUnitId) => throw new NotSupportedException();
    }
}
