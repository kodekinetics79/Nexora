using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.SupplierDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 2, FR-MDM-03 first clause: the two supplier master-data columns the weighted comparison
/// reads — <c>Tier</c> and <c>CreditDays</c> — reaching every surface a person actually uses.
///
/// <para><b>The trap these tests exist for.</b> <c>SupplierResponseDTO</c> is built in TWO
/// independent places: <c>SupplierController.MapToResponse</c> maps it from a loaded entity for the
/// detail endpoint, and <c>SupplierRepository.GetAllAsync</c> projects it in LINQ for the list
/// endpoint. Neither knows about the other, so a field added to one and forgotten in the other
/// compiles, ships, and is simply null on half the API. That had already happened:
/// <c>TaxRegistrationNumber</c> was set in <c>MapToResponse</c> and never in the projection, so the
/// supplier grid showed no registration number for any supplier while the detail page showed it.
/// <see cref="The_list_endpoint_returns_every_field_the_detail_endpoint_sets"/> is the guard that
/// makes the next omission a red test instead of a silent null.</para>
/// </summary>
public sealed class Gate2SupplierTierFieldTests
{
    private const long Tenant = 71_041;
    private const long OtherTenant = 71_042;

    /// <summary>SYNTHETIC. Correct KSA shape (15 digits, 3…3); not a real registration.</summary>
    private const string SyntheticVatNumber = "399999999999993";

    // ============================================================================
    // Round trip through the API
    // ============================================================================

    /// <summary>
    /// The owner's test for tier: a master-data administrator sets it, and it is still there when
    /// the record is read back. Then they change it, and the change sticks — including clearing it
    /// back to "not yet classified", which is a legitimate state and not a no-op.
    /// </summary>
    [Fact]
    public async Task Create_and_update_round_trip_tier_and_credit_days()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        long supplierId;
        await using (var context = database.ContextFor(Tenant))
        {
            var controller = Controller(context, Tenant);
            var created = await Create(controller, new SupplierCreateRequestDTO
            {
                Name = "Tiered Parts",
                Tier = SupplierTiers.Tier1Partner,
                CreditDays = 45
            });

            var body = Assert.IsType<SupplierResponseDTO>(
                Assert.IsType<CreatedAtActionResult>(created.Result).Value);
            Assert.Equal(SupplierTiers.Tier1Partner, body.Tier);
            Assert.Equal(45, body.CreditDays);
            supplierId = body.Id;
        }

        // Read back through the detail endpoint on a fresh context: the values are persisted, not
        // just echoed out of the request object the controller was handed.
        Guid token;
        await using (var context = database.ContextFor(Tenant))
        {
            var fetched = await Detail(Controller(context, Tenant), supplierId);
            Assert.Equal(SupplierTiers.Tier1Partner, fetched.Tier);
            Assert.Equal(45, fetched.CreditDays);
            token = fetched.ConcurrencyToken!.Value;
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var update = await Update(Controller(context, Tenant), supplierId, new SupplierUpdateRequestDTO
            {
                Name = "Tiered Parts",
                Tier = SupplierTiers.Tier3OutOfNetwork,
                CreditDays = 0,
                ConcurrencyToken = token
            });
            Assert.IsType<NoContentResult>(update);
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var fetched = await Detail(Controller(context, Tenant), supplierId);
            Assert.Equal(SupplierTiers.Tier3OutOfNetwork, fetched.Tier);
            // 0 is the positive assertion "cash on delivery" and must survive as 0, not become the
            // null that means NOT CONFIGURED.
            Assert.Equal(0, fetched.CreditDays);
        }
    }

    /// <summary>
    /// Clearing the tier is a real edit — a supplier can be un-classified — so an empty value must
    /// land as null rather than being ignored as "nothing supplied".
    /// </summary>
    [Fact]
    public async Task A_supplier_can_be_returned_to_not_yet_classified()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        var supplierId = await CreateSupplierAsync(database, tier: SupplierTiers.Tier2Extended, creditDays: 30);

        await using (var context = database.ContextFor(Tenant))
        {
            var controller = Controller(context, Tenant);
            var current = await Detail(controller, supplierId);
            var update = await Update(controller, supplierId, new SupplierUpdateRequestDTO
            {
                Name = current.Name,
                Tier = null,
                CreditDays = null,
                ConcurrencyToken = current.ConcurrencyToken
            });
            Assert.IsType<NoContentResult>(update);
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var fetched = await Detail(Controller(context, Tenant), supplierId);
            Assert.Null(fetched.Tier);
            Assert.Null(fetched.CreditDays);
        }
    }

    /// <summary>
    /// One classification, one stored spelling. The database CHECK constraint only knows the
    /// canonical form, so a value typed with spaces or in lower case has to be folded before it is
    /// stored — otherwise the API accepts a value the database will reject.
    /// </summary>
    [Theory]
    [InlineData("tier_1_partner")]
    [InlineData("Tier 1 Partner")]
    [InlineData("  TIER-1-PARTNER  ")]
    public async Task A_tier_is_stored_in_its_canonical_spelling(string typed)
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        await using var context = database.ContextFor(Tenant);
        var created = await Create(Controller(context, Tenant), new SupplierCreateRequestDTO
        {
            Name = "Canonical Parts",
            Tier = typed
        });

        var body = Assert.IsType<SupplierResponseDTO>(
            Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        Assert.Equal(SupplierTiers.Tier1Partner, body.Tier);
    }

    // ============================================================================
    // The regression guard for the two mapping sites
    // ============================================================================

    /// <summary>
    /// THE TEST THAT MATTERS HERE. Every scalar the detail endpoint returns, the list endpoint
    /// returns too — compared property by property rather than field by field, so a column added to
    /// <c>MapToResponse</c> and forgotten in the repository projection fails this test on the day it
    /// is written instead of being discovered as an empty grid column months later.
    ///
    /// <para>This is exactly how <c>TaxRegistrationNumber</c> went missing: it was assigned in the
    /// controller and never in the projection, and nothing complained.</para>
    /// </summary>
    [Fact]
    public async Task The_list_endpoint_returns_every_field_the_detail_endpoint_sets()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant, withGeography: true);
        var supplierId = await CreateSupplierAsync(
            database, tier: SupplierTiers.Tier1Partner, creditDays: 45, fullyPopulated: true);

        await using var context = database.ContextFor(Tenant);
        var controller = Controller(context, Tenant);
        var detail = await Detail(controller, supplierId);
        var listed = await ListOne(controller, supplierId);

        var drifted = typeof(SupplierResponseDTO)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => !Equals(property.GetValue(detail), property.GetValue(listed)))
            .Select(property =>
                $"{property.Name}: detail={Describe(property.GetValue(detail))}, "
                + $"list={Describe(property.GetValue(listed))}")
            .ToList();

        Assert.True(drifted.Count == 0,
            "The list projection in SupplierRepository.GetAllAsync has drifted from "
            + "SupplierController.MapToResponse. Every field below is served by the detail endpoint "
            + "and dropped by the list endpoint, which shows in the UI as an empty column rather "
            + "than as an error:\n  " + string.Join("\n  ", drifted));

        // Named explicitly as well, so the failure message says which capability broke rather than
        // only that two mappings disagree.
        Assert.Equal(SyntheticVatNumber, listed.TaxRegistrationNumber);
        Assert.Equal(SupplierTiers.Tier1Partner, listed.Tier);
        Assert.Equal(45, listed.CreditDays);
    }

    // ============================================================================
    // Rejecting a tier nobody chose
    // ============================================================================

    /// <summary>
    /// An unrecognised tier is refused at the API edge, and the refusal names the permitted values.
    /// Storing it would put a classification in the record that no administrator selected, and the
    /// database CHECK constraint would reject it anyway — as a 500, long after the operator left.
    /// </summary>
    [Theory]
    [InlineData("GOLD")]
    [InlineData("Tier 1")]
    [InlineData("TIER_4")]
    public async Task An_unknown_tier_is_rejected(string tier)
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        await using var context = database.ContextFor(Tenant);
        var controller = Controller(context, Tenant);
        var created = await Create(controller, new SupplierCreateRequestDTO
        {
            Name = "Unclassifiable Parts",
            Tier = tier
        });

        Assert.IsType<BadRequestObjectResult>(created.Result);
        var message = Assert.Single(controller.ModelState[nameof(SupplierCreateRequestDTO.Tier)]!.Errors)
            .ErrorMessage;
        Assert.Contains(tier.Trim(), message);
        foreach (var permitted in SupplierTiers.All)
            Assert.Contains(permitted, message);

        await using var verify = database.ContextFor(Tenant);
        Assert.False(await verify.Suppliers.AnyAsync());
    }

    /// <summary>Negative credit is not a thing, on either write path.</summary>
    [Fact]
    public async Task Negative_credit_days_are_rejected()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        await using var context = database.ContextFor(Tenant);
        var controller = Controller(context, Tenant);
        var created = await Create(controller, new SupplierCreateRequestDTO
        {
            Name = "Backwards Credit Parts",
            CreditDays = -1
        });

        Assert.IsType<BadRequestObjectResult>(created.Result);
        Assert.True(controller.ModelState.ContainsKey(nameof(SupplierCreateRequestDTO.CreditDays)));
        await using var verify = database.ContextFor(Tenant);
        Assert.False(await verify.Suppliers.AnyAsync());
    }

    // ============================================================================
    // Bulk import and export
    // ============================================================================

    /// <summary>
    /// A spreadsheet is the way most tenants will classify their existing supplier list, so the two
    /// columns have to survive the importer — and the exporter, or the operator cannot review or
    /// correct what they loaded.
    /// </summary>
    [Fact]
    public async Task Tier_and_credit_days_survive_a_bulk_import_and_the_export()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        var workbook = Workbook(("Imported Parts", "tier 2 extended", "60"));
        await using (var context = database.ContextFor(Tenant))
        {
            using var stream = new MemoryStream(workbook);
            var result = await Uploader(context).UploadTemplateAsync(stream, Tenant, "importer@example.test");
            Assert.True(result.Success, result.Message);
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var imported = await context.Suppliers.AsNoTracking().SingleAsync();
            Assert.Equal(SupplierTiers.Tier2Extended, imported.Tier);
            Assert.Equal(60, imported.CreditDays);
        }

        await using (var context = database.ContextFor(Tenant))
        {
            var exported = await Uploader(context).ExportSuppliersAsync(Tenant);
            using var package = new ExcelPackage(new MemoryStream(exported));
            var sheet = package.Workbook.Worksheets[0];
            Assert.Equal("Tier", sheet.Cells[1, 10].Text);
            Assert.Equal("Credit Days", sheet.Cells[1, 11].Text);
            Assert.Equal(SupplierTiers.Tier2Extended, sheet.Cells[2, 10].Text);
            Assert.Equal("60", sheet.Cells[2, 11].Text);
        }
    }

    /// <summary>
    /// An unrecognised tier in a spreadsheet row is REFUSED, with the row number, the supplier name
    /// and the permitted values in the message. Importing the supplier with the tier quietly nulled
    /// would report success while discarding the one value the operator filled the column in to set,
    /// and nothing afterwards could distinguish it from a supplier never classified.
    /// </summary>
    [Fact]
    public async Task An_uploaded_row_with_an_unknown_tier_is_refused_with_a_usable_error()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        var workbook = Workbook(
            ("Good Parts", SupplierTiers.Tier1Partner, "30"),
            ("Platinum Parts", "PLATINUM", "30"));

        await using (var context = database.ContextFor(Tenant))
        {
            using var stream = new MemoryStream(workbook);
            var result = await Uploader(context).UploadTemplateAsync(stream, Tenant, "importer@example.test");

            Assert.True(result.Success, result.Message);
            Assert.Contains("Row 3 (Platinum Parts)", result.Message);
            Assert.Contains("PLATINUM", result.Message);
            foreach (var permitted in SupplierTiers.All)
                Assert.Contains(permitted, result.Message);
        }

        await using (var context = database.ContextFor(Tenant))
        {
            // The good row still lands; one bad tier does not cost the operator the whole file.
            var supplier = await context.Suppliers.AsNoTracking().SingleAsync();
            Assert.Equal("Good Parts", supplier.Name);
            Assert.Equal(SupplierTiers.Tier1Partner, supplier.Tier);
        }
    }

    /// <summary>Credit days follows the same rule: a value present but unusable is refused, not
    /// silently turned into the null that means NOT CONFIGURED.</summary>
    [Theory]
    [InlineData("thirty")]
    [InlineData("-5")]
    [InlineData("30.5")]
    public async Task An_uploaded_row_with_unusable_credit_days_is_refused(string creditDays)
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        var workbook = Workbook(("Vague Credit Parts", SupplierTiers.Tier1Partner, creditDays));
        await using var context = database.ContextFor(Tenant);
        using var stream = new MemoryStream(workbook);
        var result = await Uploader(context).UploadTemplateAsync(stream, Tenant, "importer@example.test");

        Assert.Contains("Row 2 (Vague Credit Parts)", result.Message);
        Assert.Contains("Credit days", result.Message);
        await using var verify = database.ContextFor(Tenant);
        Assert.False(await verify.Suppliers.AnyAsync());
    }

    /// <summary>
    /// The shipped template carries both columns. Without this the fields are invisible to every
    /// tenant that onboards by spreadsheet, which is most of them.
    /// </summary>
    [Fact]
    public async Task The_import_template_offers_the_tier_and_credit_days_columns()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);

        await using var context = database.ContextFor(Tenant);
        var template = await Uploader(context).GenerateTemplateAsync(Tenant);

        using var package = new ExcelPackage(new MemoryStream(template));
        var sheet = package.Workbook.Worksheets[0];
        var tierHeader = sheet.Cells[1, 12].Text;
        Assert.StartsWith("Tier", tierHeader);
        foreach (var permitted in SupplierTiers.All)
            Assert.Contains(permitted, tierHeader);
        Assert.StartsWith("Credit Days", sheet.Cells[1, 13].Text);
    }

    // ============================================================================
    // Tenant isolation
    // ============================================================================

    /// <summary>
    /// A tier is commercial intelligence about who a tenant trades with. Another tenant sees neither
    /// the supplier nor its classification — not on the detail endpoint, not in the list.
    /// </summary>
    [Fact]
    public async Task A_cross_tenant_supplier_read_returns_nothing()
    {
        using var database = new TestDb();
        await SeedTenantAsync(database, Tenant);
        await SeedTenantAsync(database, OtherTenant);
        var supplierId = await CreateSupplierAsync(database, SupplierTiers.Tier1Partner, creditDays: 45);

        await using var context = database.ContextFor(OtherTenant);
        var intruder = Controller(context, OtherTenant);

        Assert.IsType<NotFoundObjectResult>((await intruder.GetById(supplierId)).Result);

        var list = Assert.IsType<PaginatedResponseDTO<SupplierResponseDTO>>(
            Assert.IsType<OkObjectResult>((await intruder.GetAll()).Result).Value);
        Assert.Equal(0, list.TotalCount);
        Assert.Empty(list.Items);
    }

    // ============================================================================
    // Helpers
    // ============================================================================

    private static string Describe(object? value) => value is null ? "<null>" : $"'{value}'";

    private static async Task SeedTenantAsync(TestDb database, long tenant, bool withGeography = false)
    {
        await using var context = database.ContextFor(null);
        Seed.EnsureBusinessUnit(context, tenant);
        await context.SaveChangesAsync();

        if (!withGeography) return;

        context.Currencies.Add(new Currency
        {
            Id = tenant + 1,
            BusinessUnitId = tenant,
            Code = "SAR",
            CurrencyName = "Saudi Riyal",
            IsActive = true,
            CreatedBy = "seed",
            CreatedOn = DateTime.UtcNow
        });
        context.SetCountries.Add(new SetCountry
        {
            CountryId = 9_001,
            CountryCode = "SA",
            CountryName = "Saudi Arabia",
            Buid = tenant,
            IsActive = true,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        context.SetStates.Add(new SetState
        {
            StateId = 9_002,
            StateCode = "RD",
            StateName = "Riyadh Province",
            CountryId = 9_001,
            Buid = tenant,
            IsActive = true,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        context.SetCities.Add(new SetCity
        {
            CityId = 9_003,
            CityName = "Riyadh",
            StateId = 9_002,
            CountryId = 9_001,
            Buid = tenant,
            IsActive = true,
            CreatedBy = "seed",
            CreatedDate = DateTime.UtcNow
        });
        await context.SaveChangesAsync();
    }

    private static async Task<long> CreateSupplierAsync(
        TestDb database, string? tier, int? creditDays, bool fullyPopulated = false)
    {
        await using var context = database.ContextFor(Tenant);
        var request = new SupplierCreateRequestDTO
        {
            Name = "Tiered Parts",
            Tier = tier,
            CreditDays = creditDays
        };

        if (fullyPopulated)
        {
            request.ContactEmail = "sales@tiered.example";
            request.PaymentTerms = "Net 45 from invoice date";
            request.AddressLine1 = "1 Industrial Road";
            request.AddressLine2 = "Building 4";
            request.PostalCode = "12345";
            request.Tags = "valves, fittings";
            request.Comments = "Standing agreement renewed annually.";
            request.TaxRegistrationNumber = SyntheticVatNumber;
            request.CityId = 9_003;
            request.CountryId = 9_001;
            request.CurrencyId = Tenant + 1;
        }

        var created = await Create(Controller(context, Tenant), request);
        var body = Assert.IsType<SupplierResponseDTO>(
            Assert.IsType<CreatedAtActionResult>(created.Result).Value);
        return body.Id;
    }

    private static async Task<SupplierResponseDTO> Detail(SupplierController controller, long id)
        => Assert.IsType<SupplierResponseDTO>(
            Assert.IsType<OkObjectResult>((await controller.GetById(id)).Result).Value);

    private static async Task<SupplierResponseDTO> ListOne(SupplierController controller, long id)
    {
        var page = Assert.IsType<PaginatedResponseDTO<SupplierResponseDTO>>(
            Assert.IsType<OkObjectResult>((await controller.GetAll(id: id)).Result).Value);
        return Assert.Single(page.Items);
    }

    /// <summary>
    /// Runs the DataAnnotations pass MVC runs during model binding, then calls the action — so a
    /// validation attribute on the DTO is exercised here the same way it is over HTTP, rather than
    /// being skipped because a direct method call bypasses binding.
    /// </summary>
    private static Task<ActionResult<SupplierResponseDTO>> Create(
        SupplierController controller, SupplierCreateRequestDTO request)
    {
        BindAndValidate(request, controller.ModelState);
        return controller.Create(request);
    }

    private static Task<ActionResult> Update(
        SupplierController controller, long id, SupplierUpdateRequestDTO request)
    {
        BindAndValidate(request, controller.ModelState);
        return controller.Update(id, request);
    }

    private static void BindAndValidate(object model, ModelStateDictionary modelState)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        foreach (var result in results)
            foreach (var member in result.MemberNames.DefaultIfEmpty(string.Empty))
                modelState.AddModelError(member, result.ErrorMessage ?? "Invalid value.");
    }

    private static SupplierController Controller(ErpRfqAutomationContext context, long tenant)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "master-data-admin"),
            new("businessUnitId", tenant.ToString())
        };
        return new SupplierController(
            new SupplierRepository(context, new DeterministicSupplierNumberGenerator()),
            new StubMasterDataChangeHistoryReader())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"))
                }
            }
        };
    }

    private static SupplierUploaderService Uploader(ErpRfqAutomationContext context)
        => new(context, NullLogger<SupplierUploaderService>.Instance);

    /// <summary>
    /// A filled-in copy of the import template. Only the columns under test are written; the reader
    /// indexes by column number, which is precisely why the header list is append-only.
    /// </summary>
    private static byte[] Workbook(params (string Name, string Tier, string CreditDays)[] rows)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("SupplierTemplate");
        sheet.Cells[1, 1].Value = "Supplier Name*";
        sheet.Cells[1, 12].Value = "Tier";
        sheet.Cells[1, 13].Value = "Credit Days";

        var row = 2;
        foreach (var (name, tier, creditDays) in rows)
        {
            sheet.Cells[row, 1].Value = name;
            sheet.Cells[row, 12].Value = tier;
            sheet.Cells[row, 13].Value = creditDays;
            row++;
        }

        return package.GetAsByteArray();
    }
}
