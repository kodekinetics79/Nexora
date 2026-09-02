using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The spreadsheet uploaders and the bank-statement import opened the posted file and handed it
/// straight to a parser — the most any of them checked was the four-byte ZIP signature. Every
/// asynchronous door already ran the shared inspection, so these were the only way to have an
/// uninspected file parsed on the server. They now go through <see cref="UploadInspectionGate"/>.
///
/// <para>These tests drive the CONTROLLER ACTIONS, not the gate on its own: the defect was that
/// the gate was not on the path, and a test of the gate in isolation would stay green if it were
/// unwired again.</para>
/// </summary>
public sealed class UploadInspectionGateTests
{
    private const long Tenant = 46_100;
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static TheoryData<string> SpreadsheetDoors => new()
    {
        "Product", "Supplier", "Quotation", "QuotationBackfill", "Category", "SubCategory"
    };

    [Theory]
    [MemberData(nameof(SpreadsheetDoors))]
    public async Task A_rejected_spreadsheet_is_refused_before_parsing_with_the_shared_problem_shape(string door)
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();
        var inspection = new RecordingInspection(RecordingInspection.Rejected("The file's contents are not an Excel workbook."));

        var result = await Invoke(door, context, inspection, GarbageWorkbook("import.xlsx"));

        var refusal = Assert.IsType<BadRequestObjectResult>(result);
        var problem = Assert.IsType<ProblemDetails>(refusal.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Equal("The file's contents are not an Excel workbook.", problem.Detail);
        Assert.Equal(DocumentInspectionErrorCodes.DocumentRejected, problem.Extensions["errorCode"]);
        Assert.Equal("Rejected", problem.Extensions["outcome"]);
        Assert.Equal(false, problem.Extensions["success"]);
        Assert.Equal(problem.Detail, problem.Extensions["message"]);
        Assert.Contains("application/problem+json", refusal.ContentTypes);

        // The inspector saw exactly the posted file, through the controller's own entry point.
        Assert.Equal(["import.xlsx"], inspection.FileNames);
        Assert.Equal(XlsxContentType, Assert.Single(inspection.DeclaredContentTypes));

        // And nothing was parsed or written: the parser would have thrown on garbage bytes and the
        // controller would have answered with its own "Internal server error" shape instead.
        Assert.Equal(0, await context.Products.CountAsync());
        Assert.Equal(0, await context.Suppliers.CountAsync());
        Assert.Equal(0, await context.Quotes.CountAsync());
    }

    [Theory]
    [MemberData(nameof(SpreadsheetDoors))]
    public async Task A_scanner_that_cannot_answer_fails_the_import_closed(string door)
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();
        var inspection = new RecordingInspection(RecordingInspection.ScannerUnavailable());
        var http = new DefaultHttpContext { User = Principal(Tenant) };

        var result = await Invoke(door, context, inspection, GarbageWorkbook("import.xlsx"), http);

        var refusal = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, refusal.StatusCode);
        var problem = Assert.IsType<ProblemDetails>(refusal.Value);
        Assert.Equal("security_scanner_unavailable", problem.Extensions["errorCode"]);
        Assert.Equal("AwaitingSecurityScan", problem.Extensions["outcome"]);
        Assert.Equal("30", http.Response.Headers.RetryAfter.ToString());
        Assert.Single(inspection.FileNames);
    }

    /// <summary>
    /// The other half: a CLEARED file reaches the parser, and it is the bytes inspection saw that
    /// get parsed. The product uploader's own blank template is a real workbook the parser
    /// recognises (and refuses, with its own message, for having no rows) — so the answer proves
    /// the parser ran on what the gate handed it, not that the gate short-circuited.
    /// </summary>
    [Fact]
    public async Task A_cleared_workbook_reaches_the_parser_with_the_inspected_bytes()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        Seed.EnsureBusinessUnit(context, Tenant);
        await context.SaveChangesAsync();
        var service = new ProductUploaderService(context, NullLogger<ProductUploaderService>.Instance);
        var template = await service.GenerateTemplateAsync(Tenant);
        var inspection = new RecordingInspection(RecordingInspection.Cleared());
        var controller = new ProductUploaderController(service, inspection, NullLogger<ProductUploaderController>.Instance)
        {
            ControllerContext = Context(new DefaultHttpContext { User = Principal(Tenant) })
        };

        var result = await controller.UploadTemplate(File(template, "ProductTemplate.xlsx"));

        Assert.Equal(["ProductTemplate.xlsx"], inspection.FileNames);
        var answer = Assert.IsAssignableFrom<ObjectResult>(result);
        Assert.Contains(answer.StatusCode ?? StatusCodes.Status200OK,
            new[] { StatusCodes.Status200OK, StatusCodes.Status400BadRequest });
        Assert.IsNotType<ProblemDetails>(answer.Value); // the parser's verdict, not the gate's
        var payload = System.Text.Json.JsonSerializer.Serialize(answer.Value);
        Assert.Contains("\"message\"", payload); // the uploader's own {success, message} contract
        Assert.DoesNotContain("errorCode", payload);
    }

    /// <summary>
    /// The lead template door was never a parser: it hands the bytes to
    /// <see cref="IDocumentIngestion"/>, which is the inspected asynchronous path. It is asserted
    /// here so that a future "optimisation" that parses the workbook inline shows up as a failure.
    /// </summary>
    [Fact]
    public async Task The_lead_template_door_hands_the_bytes_to_the_inspected_ingestion_path()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var ingestion = new RecordingIngestion();
        // The template action never consults LeadUploaderService (the workbook goes to ingestion
        // whole), so the service is deliberately absent: if the action ever starts parsing inline
        // it dereferences null here and this test fails for the right reason.
        var controller = new LeadUploaderController(
            service: null!,
            ingestion,
            NullLogger<LeadUploaderController>.Instance)
        {
            ControllerContext = Context(new DefaultHttpContext { User = Principal(Tenant) })
        };

        var result = await controller.UploadTemplate(GarbageWorkbook("leads.xlsx"));

        Assert.Equal(StatusCodes.Status202Accepted, Assert.IsType<ObjectResult>(result).StatusCode);
        Assert.Equal(["leads.xlsx"], ingestion.FileNames);
        Assert.Equal([Tenant], ingestion.BusinessUnitIds);
    }

    [Fact]
    public async Task The_retired_rfq_spreadsheet_door_still_answers_409_and_parses_nothing()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(Tenant);
        var inspection = new RecordingInspection(RecordingInspection.Cleared());
        var controller = new RfqUploaderController(
            new RfqUploaderService(context, NullLogger<RfqUploaderService>.Instance, new CanonicalRfqNormalizer()),
            inspection,
            NullLogger<RfqUploaderController>.Instance)
        {
            ControllerContext = Context(new DefaultHttpContext { User = Principal(Tenant) })
        };

        var result = await controller.UploadTemplate(GarbageWorkbook("rfq.xlsx"));

        Assert.IsType<ConflictObjectResult>(result);
        Assert.Empty(inspection.FileNames);
        Assert.Equal(0, await context.Rfqs.CountAsync());
    }

    /// <summary>
    /// Every controller action that accepts a posted file is on the upload rate-limit policy (the
    /// SMTP send keeps its own, stricter one). Found by reflection over the real assembly so a new
    /// door cannot arrive without it.
    /// </summary>
    [Fact]
    public void Every_file_accepting_action_is_rate_limited_on_the_upload_policy()
    {
        var actions = typeof(ProductUploaderController).Assembly.GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && !type.IsAbstract)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>(true).Any())
            .Where(method => method.GetParameters().Any(AcceptsFile))
            .ToList();

        Assert.NotEmpty(actions);
        Assert.Contains(actions, action => action.DeclaringType == typeof(TreasuryController));
        Assert.Contains(actions, action => action.DeclaringType == typeof(SupplierQuoteInboxController));

        // Exempt on purpose: the customer, product and user create/update forms carry an OPTIONAL
        // image or attachment list on ordinary CRUD traffic. They are not upload doors — most
        // calls carry no file at all — and the file, when present, is inspected where it is
        // written (CustomerController.SaveCustomerImageAsync, ProductRepository).
        static bool IsCrudFormWithAnOptionalFile(MethodInfo action) =>
            action.DeclaringType == typeof(CustomerController)
            || action.DeclaringType == typeof(ProductController)
            || action.DeclaringType == typeof(UserController);

        var unlimited = actions
            .Where(action => action.DeclaringType != typeof(SmtpController))
            .Where(action => !IsCrudFormWithAnOptionalFile(action))
            .Where(action => action.GetCustomAttribute<EnableRateLimitingAttribute>(true)?.PolicyName
                             != RateLimitingExtensions.UploadPolicy)
            .Select(action => $"{action.DeclaringType!.Name}.{action.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(unlimited.Count == 0,
            "These file-accepting actions are not on the upload rate-limit policy:\n  " + string.Join("\n  ", unlimited));
    }

    /// <summary>
    /// The lead-folder door accepted 200 MB per request while inspection reads at most 25 MB —
    /// eight times what could ever be accepted, buffered and then refused. It now states the same
    /// ceiling as the other single-document doors and as the inspection limit.
    /// </summary>
    [Fact]
    public void The_lead_folder_upload_door_accepts_no_more_than_the_inspection_limit()
    {
        var action = typeof(EmailController).GetMethod(nameof(EmailController.UploadLeadsToFolder))!;
        var limit = action.CustomAttributes.Single(x => x.AttributeType == typeof(RequestSizeLimitAttribute));
        var bytes = Convert.ToInt64(limit.ConstructorArguments.Single().Value);

        Assert.Equal(25L * 1024 * 1024, bytes);
        Assert.Equal(DocumentInspectionOptions.DefaultMaximumFileBytes, bytes);

        var supplierQuoteDoor = typeof(SupplierQuoteInboxController).GetMethod(nameof(SupplierQuoteInboxController.Upload))!
            .CustomAttributes.Single(x => x.AttributeType == typeof(RequestSizeLimitAttribute));
        Assert.Equal(bytes, Convert.ToInt64(supplierQuoteDoor.ConstructorArguments.Single().Value));
    }

    private static bool AcceptsFile(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (IsFileType(type)) return true;
        // A [FromForm] model carrying the file as a property (SupplierQuoteDocumentUploadRequest).
        return type.IsClass && type != typeof(string) && type.Namespace?.StartsWith("ERP_RFQ_Automation", StringComparison.Ordinal) == true
               && type.GetProperties().Any(property => IsFileType(property.PropertyType));
    }

    private static bool IsFileType(Type type)
        => typeof(IFormFile).IsAssignableFrom(type)
           || (type.IsGenericType && type.GetGenericArguments().Length == 1
               && typeof(IFormFile).IsAssignableFrom(type.GetGenericArguments()[0]));

    // ------------------------------------------------------------------ support

    private static async Task<IActionResult> Invoke(
        string door, ErpRfqAutomationContext context, IFileInspectionService inspection, IFormFile file,
        DefaultHttpContext? http = null)
    {
        var controllerContext = Context(http ?? new DefaultHttpContext { User = Principal(Tenant) });
        switch (door)
        {
            case "Product":
                return await new ProductUploaderController(
                    new ProductUploaderService(context, NullLogger<ProductUploaderService>.Instance),
                    inspection, NullLogger<ProductUploaderController>.Instance)
                    { ControllerContext = controllerContext }.UploadTemplate(file);
            case "Supplier":
                return await new SupplierUploaderController(
                    new SupplierUploaderService(context, NullLogger<SupplierUploaderService>.Instance),
                    inspection, NullLogger<SupplierUploaderController>.Instance)
                    { ControllerContext = controllerContext }.UploadTemplate(file);
            case "Quotation":
                return await new QuotationUploaderController(
                    new QuotationUploaderService(context, NullLogger<QuotationUploaderService>.Instance),
                    inspection, NullLogger<QuotationUploaderController>.Instance)
                    { ControllerContext = controllerContext }.UploadTemplate(file);
            case "QuotationBackfill":
                return await new QuotationUploaderController(
                    new QuotationUploaderService(context, NullLogger<QuotationUploaderService>.Instance),
                    inspection, NullLogger<QuotationUploaderController>.Instance)
                    { ControllerContext = controllerContext }.UploadBackfill(file);
            case "Category":
                return await new ProductCategoryUploaderController(
                    new ProductCategoryUploaderService(context, NullLogger<ProductCategoryUploaderService>.Instance),
                    inspection, NullLogger<ProductCategoryUploaderController>.Instance)
                    { ControllerContext = controllerContext }.UploadCategoryTemplate(file);
            case "SubCategory":
                return await new ProductCategoryUploaderController(
                    new ProductCategoryUploaderService(context, NullLogger<ProductCategoryUploaderService>.Instance),
                    inspection, NullLogger<ProductCategoryUploaderController>.Instance)
                    { ControllerContext = controllerContext }.UploadSubCategoryTemplate(file);
            default:
                throw new ArgumentOutOfRangeException(nameof(door), door, null);
        }
    }

    private static ControllerContext Context(DefaultHttpContext http) => new() { HttpContext = http };

    private static ClaimsPrincipal Principal(long tenant) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim("businessUnitId", tenant.ToString())],
        "Test"));

    private static FormFile GarbageWorkbook(string name) => File("not-an-xlsx-workbook"u8.ToArray(), name);

    private static FormFile File(byte[] bytes, string name) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = XlsxContentType
        };

    /// <summary>Records what the controller asked it to inspect and answers with a fixed verdict.</summary>
    private sealed class RecordingInspection(FileInspectionResult verdict) : IFileInspectionService
    {
        public List<string> FileNames { get; } = [];
        public List<string?> DeclaredContentTypes { get; } = [];

        public Task<FileInspectionResult> InspectAsync(FileInspectionRequest request, CancellationToken cancellationToken = default)
        {
            FileNames.Add(request.FileName);
            DeclaredContentTypes.Add(request.DeclaredContentType);
            return Task.FromResult(verdict);
        }

        public static FileInspectionResult Rejected(string reason) =>
            new(FileInspectionStatus.Rejected, null, 0, reason, "recording", null);

        public static FileInspectionResult ScannerUnavailable() =>
            new(FileInspectionStatus.Quarantined, XlsxContentType, 0, "No clamd is listening.", "recording", null)
            {
                MalwareStatus = MalwareScanStatus.Unavailable,
                IsRetryable = true,
                ErrorCode = "security_scanner_unavailable"
            };

        public static FileInspectionResult Cleared() =>
            new(FileInspectionStatus.Cleared, XlsxContentType, 0, "Cleared.", "recording", null)
            {
                MalwareStatus = MalwareScanStatus.Clean,
                ErrorCode = "security_scan_cleared"
            };
    }

    private sealed class RecordingIngestion : IDocumentIngestion
    {
        public List<string> FileNames { get; } = [];
        public List<long> BusinessUnitIds { get; } = [];

        public Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null, CancellationToken ct = default)
        {
            FileNames.Add(fileName);
            BusinessUnitIds.Add(businessUnitId);
            return Task.FromResult(new IngestedDocument { JobId = 1, Outcome = EnqueueOutcome.Enqueued, ContentHash = "hash", StoragePath = "test://leads" });
        }
    }
}
