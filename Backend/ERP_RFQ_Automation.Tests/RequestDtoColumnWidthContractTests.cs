using System.ComponentModel.DataAnnotations;
using System.Reflection;
using ERP_RFQ_Automation.DTOs.ProductCategory;
using ERP_RFQ_Automation.DTOs.ProductDTOs;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

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

    // ProductCategories."Description" is NARROWER than the Products one. Both DTOs carried the
    // same [StringLength(500)], which is how the category door stayed broken after the product
    // door was fixed: the number looked right against the wrong column.
    private const int CategoryDescriptionColumn = 255;

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

    /// <summary>
    /// THE SECOND INSTANCE, on categories. A 300-character description is the exact shape the
    /// reviewer traced: <c>[StringLength(500)]</c> accepted it, <c>varchar(255)</c> refused it, and
    /// <c>ProductCategoryController.Create</c> answered with a 500 that said nothing.
    /// </summary>
    [Fact]
    public void A_category_description_the_column_cannot_hold_is_refused_on_both_doors()
    {
        var create = new ProductCategoryCreateRequestDTO
        {
            CategoryName = "Valves",
            Description = new string('D', 300),
            BusinessUnitId = 1
        };
        var update = new ProductCategoryUpdateRequestDTO
        {
            CategoryName = "Valves",
            Description = new string('D', 300),
            BusinessUnitId = 1
        };

        Assert.True(create.Description!.Length > CategoryDescriptionColumn, "fixture must exceed the column");
        Assert.True(Rejects(create, nameof(ProductCategoryCreateRequestDTO.Description)),
            "a description longer than the varchar(255) column must fail validation, not the INSERT");
        Assert.True(Rejects(update, nameof(ProductCategoryUpdateRequestDTO.Description)));
    }

    /// <summary>Tightening must not have cost anything the column can actually hold.</summary>
    [Fact]
    public void A_category_description_that_fits_the_column_is_still_accepted()
    {
        var create = new ProductCategoryCreateRequestDTO
        {
            CategoryName = "Valves",
            Description = new string('D', CategoryDescriptionColumn),
            BusinessUnitId = 1
        };

        Assert.False(Rejects(create, nameof(ProductCategoryCreateRequestDTO.Description)));
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
        var unmapped = new List<string>();
        var assembly = typeof(ProductCreateRequestDTO).Assembly;

        foreach (var dto in RequestDtos(assembly))
        {
            // Only DTOs that actually promise a length are in scope; one with no cap at all cannot
            // lie about one.
            var capped = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => (Property: p, Declared: DeclaredCap(p)))
                .Where(x => x.Declared is not null)
                .ToList();
            if (capped.Count == 0) continue;

            var entity = EntityFor(model, dto.Name);
            if (entity is null)
            {
                // NOT silently skipped any more. Before this sweep was widened, eight capped request
                // DTOs fell out here without a word — which is exactly where the next instance of
                // this defect would have hidden.
                if (!NotBackedByOneEntity.Any(x => x.Dto == dto.Name)) unmapped.Add(dto.Name);
                continue;
            }

            foreach (var (property, declared) in capped)
            {
                var column = entity.FindProperty(property.Name)?.GetMaxLength();
                if (column is null) continue;
                if (declared <= column) continue;

                var finding = $"{dto.Name}.{property.Name}: [{CapAttributeName(property)}({declared})] > {entity.ClrType.Name}.{property.Name} varchar({column})";
                (Excluded(dto.Name, property.Name) ? outstanding : violations).Add(finding);
            }
        }

        Assert.True(violations.Count == 0,
            "Request DTOs promise more text than the database column can hold:\n  " + string.Join("\n  ", violations));

        // A capped DTO that resolves to no entity is a HOLE in this sweep, not a pass. Every one
        // must be named in NotBackedByOneEntity with the reason it cannot be checked here, so the
        // hole is a decision somebody wrote down rather than an accident of a naming convention.
        Assert.True(unmapped.Count == 0,
            "These request DTOs carry length caps but resolve to no entity, so nothing checks them. "
            + "Add an alias to EntityAliases, or list them in NotBackedByOneEntity with the reason:\n  "
            + string.Join("\n  ", unmapped));

        // The exclusions must still be REAL. If somebody fixes one, this fails and the entry comes
        // out of the list — an exclusion that has quietly stopped applying is how a suppression list
        // rots into a place defects hide.
        Assert.Equal(Exclusions.Count, outstanding.Count);
    }

    /// <summary>
    /// Every request DTO in the assembly. Matched case-insensitively on purpose — <c>RequestDto</c>
    /// and <c>RequestDTO</c> are both spelled in this codebase, and an Ordinal match silently
    /// dropped the first spelling.
    /// </summary>
    private static IEnumerable<Type> RequestDtos(Assembly assembly) =>
        assembly.GetTypes().Where(t => t.IsClass && !t.IsAbstract
            && t.Name.EndsWith("RequestDTO", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The declared cap, from EITHER attribute. <c>[MaxLength]</c> lies exactly as loudly as
    /// <c>[StringLength]</c> — <c>RolePermissionBulkApplyRequestDTO.Reason</c> uses it — and reading
    /// only one of the two is how a whole family of DTOs escaped the first version of this sweep.
    /// </summary>
    private static int? DeclaredCap(PropertyInfo property)
    {
        if (property.PropertyType != typeof(string)) return null;
        var stringLength = property.GetCustomAttribute<StringLengthAttribute>()?.MaximumLength;
        if (stringLength is not null) return stringLength;
        var maxLength = property.GetCustomAttribute<MaxLengthAttribute>()?.Length;
        return maxLength > 0 ? maxLength : null;
    }

    private static string CapAttributeName(PropertyInfo property) =>
        property.GetCustomAttribute<StringLengthAttribute>() is not null ? "StringLength" : "MaxLength";

    private static IEntityType? EntityFor(IModel model, string dtoTypeName)
    {
        var entityName = EntityAliases.TryGetValue(dtoTypeName, out var alias) ? alias : EntityNameFor(dtoTypeName);
        return model.GetEntityTypes().FirstOrDefault(e => e.ClrType.Name == entityName);
    }

    /// <summary>
    /// DTOs whose entity does not follow the "strip the verb" convention. Each pairing was read off
    /// the controller or repository that performs the write, not guessed from the name.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> EntityAliases = new Dictionary<string, string>
    {
        // MailboxController.Create/Update map these onto EmailConfiguration.
        ["MailboxCreateRequestDTO"] = "EmailConfiguration",
        ["MailboxUpdateRequestDTO"] = "EmailConfiguration",
        // LeadRepository.LinkClientAsync writes request.Reason onto a LeadReviewAudit row.
        ["LeadClientLinkRequestDTO"] = "LeadReviewAudit",
        // LeadRepository.RequestClarificationAsync writes request.Note onto LeadReviewAudit.Reason.
        ["LeadClarificationRequestDTO"] = "LeadReviewAudit",
        // QuoteController.Transition hands the command to LifecycleApplicationService, which
        // persists CorrelationId and IdempotencyKey on CommercialLifecycleEvent.
        ["QuoteLifecycleTransitionRequestDTO"] = "CommercialLifecycleEvent",
        // QuoteService.ExtendValidityAsync records the reason on a QuoteValidityExtension row.
        ["QuoteExtendValidityRequestDTO"] = "QuoteValidityExtension",
        // AuthController compares against User; only Email is a column, and it is unbounded.
        ["LoginRequestDTO"] = "User",
    };

    /// <summary>
    /// Capped DTOs this sweep genuinely cannot check, each with the reason. Listed rather than
    /// skipped, so the gap is visible.
    /// </summary>
    private static readonly IReadOnlyList<(string Dto, string Reason)> NotBackedByOneEntity =
    [
        ("MailboxTestRequestDTO", "Writes nothing — it opens a connection with the supplied settings and discards them."),
        ("MailboxSendTestRequestDTO", "Writes nothing — the recipient becomes a To header on one test message and the audit row records only the outcome."),
        ("RolePermissionBulkApplyRequestDTO", "Reason reaches the database only through IamAuditWriter, which calls Truncate(entry.Reason, 512) before assigning it, so the cap cannot overflow the column."),
    ];

    /// <summary>
    /// Mismatches this sweep finds that are NOT fixed here, each with the reason.
    ///
    /// <list type="bullet">
    /// <item><b>Supplier.Tier</b> — a false positive of the rule, not a defect. The DTO also carries
    /// <c>[SupplierTier]</c>, which restricts the value to the enumerated tier constants (longest 22
    /// characters, column 32). The 64 is
    /// <c>SupplierTierInput.MaximumCanonicalisableLength</c>, a deliberate bound on the stack buffer
    /// <c>Normalize</c> allocates during model binding — it is not a promise that 64 characters can
    /// be stored, and no value that long can reach the column. Excluded permanently.</item>
    /// </list>
    ///
    /// <para><c>ProductCategory.Description</c> WAS on this list and is now FIXED: both DTOs are
    /// <c>[StringLength(255)]</c> over the <c>varchar(255)</c> column, and
    /// <c>ProductCategoryController</c> Create and Update carry the same 22001 / ArgumentException
    /// handling <c>ProductController</c> does. The entries are gone rather than kept-and-passing,
    /// which is what the count assertion enforces.</para>
    ///
    /// <para><b>Mailbox EmailAddress and Username</b> were the last four entries, and they are now
    /// FIXED too. Both were <c>[StringLength(320)]</c> — the RFC 5321 address limit, correct about
    /// email and wrong about the table — over <c>character varying(255)</c> columns, with no
    /// <c>22001</c> handling anywhere in <c>MailboxController</c>. All four attributes now read 255
    /// and both write doors carry the same narrow catches. The judgement was to bring the ATTRIBUTE
    /// down rather than widen the columns: a genuinely valid 260-character address is now refused
    /// with a clean 400 it can act on, which is survivable, where an unexplained 500 on the mailbox
    /// setup screen — the one screen that must work before this product ingests anything — is not.
    /// Nothing was added to this list to make that pass.</para>
    /// </summary>
    private static readonly IReadOnlyList<(string Dto, string Property)> Exclusions =
    [
        ("SupplierCreateRequestDTO", "Tier"),
        ("SupplierUpdateRequestDTO", "Tier"),
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
