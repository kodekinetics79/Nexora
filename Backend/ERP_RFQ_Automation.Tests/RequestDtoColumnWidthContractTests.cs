using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A request DTO whose <c>[StringLength(n)]</c> is WIDER than the column it lands in is not a
/// cosmetic mismatch — it is a guaranteed HTTP 500. ModelState accepts the value, the controller
/// maps it onto the entity, and PostgreSQL refuses the INSERT with
/// <c>22001: value too long for type character varying(...)</c> far too late for anything to turn
/// it into an answer the caller can act on.
///
/// <para>This is what happened to products in production on 2026-08-20: twelve unhandled
/// <c>22001</c>s out of <c>ProductRepository.AddAsync</c> in six minutes, because
/// <c>ProductCreateRequestDTO.ProductName</c> was <c>[StringLength(200)]</c> while
/// <c>Products."ProductName"</c> is <c>varchar(100)</c>. The part numbers were innocent; the
/// validation attribute was lying about how much text the system could store.</para>
///
/// <para>The fix direction is FORCED: the column cannot be widened. <c>View_SupplierPriceList</c>
/// selects <c>ProductName</c>, PostgreSQL refuses "cannot alter type of a column used by a view or
/// rule", and <c>Program.cs</c> runs <c>MigrateAsync()</c> unguarded at startup — so a widening
/// migration fails the deploy itself. The attribute must come down to meet the column.</para>
///
/// <para>The sweep below locks the invariant for EVERY request DTO the model can be resolved for,
/// not just this one, so the next instance of this defect is caught at build time instead of in a
/// production log.</para>
/// </summary>
public sealed class RequestDtoColumnWidthContractTests
{
    // The two Products columns at issue, as configured in ErpRfqAutomationContext.
    private const int ProductNameColumn = 100;
    private const int ProductDescriptionColumn = 500;

    private static IReadOnlyList<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    private static bool Rejects(object dto, string property) =>
        Validate(dto).Any(r => r.MemberNames.Contains(property));

    /// <summary>
    /// The exact production shape: a 150-character product name. Under the old
    /// <c>[StringLength(200)]</c> this sails through ModelState and dies at the INSERT.
    /// </summary>
    [Fact]
    public void A_product_name_the_column_cannot_hold_is_refused_before_the_insert()
    {
        var request = new ProductCreateRequestDTO
        {
            PartNo = "PN-001",
            ProductName = new string('A', 150),
            CreatedBy = "tester@example.com",
            Buid = 1
        };

        Assert.True(request.ProductName!.Length > ProductNameColumn, "fixture must exceed the column");
        Assert.True(Rejects(request, nameof(ProductCreateRequestDTO.ProductName)),
            "a name longer than the varchar(100) column must fail validation, not the INSERT");
    }

    /// <summary>Editing is the same defect: the update DTO carried the same lie.</summary>
    [Fact]
    public void Editing_a_product_to_a_name_the_column_cannot_hold_is_refused_too()
    {
        var request = new ProductUpdateRequestDTO
        {
            PartNo = "PN-001",
            ProductName = new string('A', 150),
            ModifiedBy = "tester@example.com",
            Buid = 1
        };

        Assert.True(Rejects(request, nameof(ProductUpdateRequestDTO.ProductName)));
    }

    [Fact]
    public void A_description_the_column_cannot_hold_is_refused_on_both_doors()
    {
        var create = new ProductCreateRequestDTO
        {
            PartNo = "PN-001",
            Description = new string('D', ProductDescriptionColumn + 1),
            CreatedBy = "tester@example.com",
            Buid = 1
        };
        var update = new ProductUpdateRequestDTO
        {
            PartNo = "PN-001",
            Description = new string('D', ProductDescriptionColumn + 1),
            ModifiedBy = "tester@example.com",
            Buid = 1
        };

        Assert.True(Rejects(create, nameof(ProductCreateRequestDTO.Description)));
        Assert.True(Rejects(update, nameof(ProductUpdateRequestDTO.Description)));
    }

    /// <summary>A name that fits must still be accepted — the cap mirrors the column exactly.</summary>
    [Fact]
    public void A_name_that_fits_the_column_is_still_accepted()
    {
        var request = new ProductCreateRequestDTO
        {
            PartNo = "PN-001",
            ProductName = new string('A', ProductNameColumn),
            CreatedBy = "tester@example.com",
            Buid = 1
        };

        Assert.False(Rejects(request, nameof(ProductCreateRequestDTO.ProductName)));
    }

    /// <summary>
    /// THE DURABLE ONE. For every request DTO whose entity can be resolved from its type name,
    /// every <c>[StringLength(n)]</c> must satisfy <c>n &lt;= </c> the <c>HasMaxLength</c>
    /// configured for the identically named entity property. Anything else is a 500 waiting for
    /// the first customer who types past the limit.
    ///
    /// <para>A metadata test is the only lane that can catch this. The
    /// <c>PostgreSqlProductionDialectTests</c> lane migrates an EMPTY database, so a DB-level
    /// assertion would exercise a schema no customer row has ever touched; and the widening that
    /// would "fix" the mismatch is exactly the migration that breaks the deploy.</para>
    /// </summary>
    [Fact]
    public void No_request_dto_promises_more_text_than_its_column_can_hold()
    {
        using var database = new TestDb();
        using var context = database.ContextFor(null);
        var model = context.Model;

        var violations = new List<string>();
        var outstanding = new List<string>();
        var assembly = typeof(ProductCreateRequestDTO).Assembly;

        foreach (var dto in assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.Name.EndsWith("RequestDTO", StringComparison.Ordinal)))
        {
            var entityName = EntityNameFor(dto.Name);
            var entity = model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == entityName);
            if (entity is null) continue;

            foreach (var property in dto.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var declared = property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;
                if (declared is null) continue;

                var column = entity.FindProperty(property.Name)?.GetMaxLength();
                if (column is null) continue;

                if (declared <= column) continue;

                var finding = $"{dto.Name}.{property.Name}: [StringLength({declared})] > {entity.ClrType.Name}.{property.Name} varchar({column})";
                (Excluded(dto.Name, property.Name) ? outstanding : violations).Add(finding);
            }
        }

        Assert.True(violations.Count == 0,
            "Request DTOs promise more text than the database column can hold:\n  " + string.Join("\n  ", violations));

        // The exclusions must still be REAL. If somebody fixes one, this fails and the entry comes
        // out of the list — an exclusion that has quietly stopped applying is how a suppression list
        // rots into a place defects hide.
        Assert.Equal(Exclusions.Count, outstanding.Count);
    }

    /// <summary>
    /// Mismatches this sweep found that are NOT fixed here, each with the reason. Two entries, and
    /// they are not the same kind of thing.
    ///
    /// <list type="bullet">
    /// <item><b>Supplier.Tier</b> — a false positive of the rule, not a defect. The DTO also carries
    /// <c>[SupplierTier]</c>, which restricts the value to the enumerated tier constants (longest 22
    /// characters, column 32). The 64 is
    /// <c>SupplierTierInput.MaximumCanonicalisableLength</c>, a deliberate bound on the stack buffer
    /// <c>Normalize</c> allocates during model binding — it is not a promise that 64 characters can
    /// be stored, and no value that long can reach the column. Excluded permanently.</item>
    ///
    /// <item><b>ProductCategory.Description</b> — a GENUINE second instance of exactly this defect,
    /// live on <c>ProductCategoryController.Create</c>: <c>[StringLength(500)]</c> over a
    /// <c>varchar(255)</c> column, so a 300-character category description is the same unhandled
    /// <c>22001</c> that products just produced. It is not fixed here only because it lives in
    /// <c>DTOs/Product/ProductCategoryResponseDTO.cs</c>, outside this change's file scope. It needs
    /// the identical two-line fix and it is reported, not forgotten.</item>
    /// </list>
    /// </summary>
    private static readonly IReadOnlyList<(string Dto, string Property)> Exclusions =
    [
        ("SupplierCreateRequestDTO", "Tier"),
        ("SupplierUpdateRequestDTO", "Tier"),
        ("ProductCategoryCreateRequestDTO", "Description"),
        ("ProductCategoryUpdateRequestDTO", "Description"),
    ];

    private static bool Excluded(string dto, string property) =>
        Exclusions.Any(x => x.Dto == dto && x.Property == property);

    /// <summary>
    /// "ProductCreateRequestDTO" -> "Product". Strips the request suffix and the verb, which is the
    /// naming convention every DTO in this assembly follows.
    /// </summary>
    private static string EntityNameFor(string dtoTypeName)
    {
        var name = dtoTypeName[..^"RequestDTO".Length];
        foreach (var verb in new[] { "Create", "Update", "Patch", "Upsert", "Edit" })
            if (name.EndsWith(verb, StringComparison.Ordinal))
                return name[..^verb.Length];
        return name;
    }
}
