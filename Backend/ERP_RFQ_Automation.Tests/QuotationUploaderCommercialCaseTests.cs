using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OfficeOpenXml;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A spreadsheet upload used to mint a priced customer quotation with no RFQ behind it, so the
/// quote carried no commercial case and could never be traced from inquiry to delivery — the same
/// defect <c>POST /api/Order</c> had, one document upstream.
///
/// <para>The originating RFQ is now mandatory, resolved inside the caller's tenant, and refused
/// when absent, unknown, ambiguous or itself case-less. It is a refusal, not an allocation: a
/// commercial case is the one-to-one principal of a Lead, so minting one for a spreadsheet row
/// would manufacture a phantom inquiry.</para>
/// </summary>
public sealed class QuotationUploaderCommercialCaseTests
{
    private const long Tenant = 97_701;
    private const long ForeignTenant = 97_702;
    private static readonly DateTime Now = new(2026, 8, 9, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task An_upload_that_names_no_rfq_is_refused_and_writes_nothing()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);
        await using var context = db.ContextFor(Tenant);

        var result = await UploadAsync(context, customerRfqNo: null);

        Assert.False(result.Success);
        Assert.Contains("Customer RFQ No", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Quotes.ToListAsync());
        Assert.True(graph.CaseId > 0);
    }

    /// <summary>
    /// The RFQ is re-read inside the caller's tenant, so a spreadsheet cannot name another business
    /// unit's inquiry and borrow its commercial case.
    /// </summary>
    [Fact]
    public async Task An_upload_that_names_another_tenants_rfq_is_refused()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);
        await using var context = db.ContextFor(Tenant);

        var result = await UploadAsync(context, graph.ForeignRfqNo);

        Assert.False(result.Success);
        Assert.Contains("not found", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Quotes.ToListAsync());
    }

    [Fact]
    public async Task An_upload_that_names_an_rfq_with_no_case_is_refused()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);
        await using var context = db.ContextFor(Tenant);

        var result = await UploadAsync(context, graph.CaselessRfqNo);

        Assert.False(result.Success);
        Assert.Contains("no commercial case", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Quotes.ToListAsync());
    }

    [Fact]
    public async Task An_upload_naming_its_rfq_inherits_that_rfqs_commercial_case()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);
        await using var context = db.ContextFor(Tenant);

        var result = await UploadAsync(context, graph.RfqNo);

        Assert.True(result.Success, result.Message);
        var quote = await context.Quotes.SingleAsync();
        Assert.Equal(graph.CaseId, quote.CommercialCaseId);
        Assert.Equal(graph.Serial, quote.NexoraSerial);
        Assert.Equal(graph.RfqId, quote.Rfqid);
    }

    /// <summary>
    /// Two rows grouped under one quote number but naming different inquiries would otherwise
    /// silently take the first row's case.
    /// </summary>
    [Fact]
    public async Task An_upload_grouping_one_quote_across_two_rfqs_is_refused()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);
        await using var context = db.ContextFor(Tenant);

        var result = await UploadAsync(context, graph.RfqNo, secondRowRfqNo: graph.SecondRfqNo);

        Assert.False(result.Success);
        Assert.Contains("cannot answer two RFQs", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await context.Quotes.ToListAsync());
    }

    /// <summary>
    /// The generated template carries the mandatory column, so a freshly downloaded workbook is
    /// importable and an operator is never told to supply a field the template does not offer.
    /// </summary>
    [Fact]
    public async Task The_generated_template_carries_the_mandatory_rfq_column()
    {
        using var db = new TestDb();
        var graph = await SeedAsync(db);
        await using var context = db.ContextFor(Tenant);
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        var workbook = await new QuotationUploaderService(
            context, NullLogger<QuotationUploaderService>.Instance).GenerateTemplateAsync(Tenant);

        using var package = new ExcelPackage(new MemoryStream(workbook));
        var sheet = package.Workbook.Worksheets["QuotationTemplate"];
        Assert.Equal("Customer RFQ No*", sheet.Cells[1, 14].Text);
        // Populated from a real RFQ in this tenant, never an invented reference that would be
        // refused on upload.
        Assert.Equal(graph.SecondRfqNo, sheet.Cells[2, 14].Text);
    }

    // ---- fixture ---------------------------------------------------------------------------

    private sealed record Graph(
        long CaseId, string Serial, long RfqId, string RfqNo,
        string SecondRfqNo, string CaselessRfqNo, string ForeignRfqNo);

    private static async Task<ERP_RFQ_Automation.Models.ServiceResult<string>> UploadAsync(
        ErpRfqAutomationContext context, string? customerRfqNo, string? secondRowRfqNo = null)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
        using var package = new ExcelPackage();
        var sheet = package.Workbook.Worksheets.Add("QuotationTemplate");
        for (var column = 1; column <= 14; column++)
            sheet.Cells[1, column].Value = $"Column {column}";

        WriteRow(sheet, 2, customerRfqNo);
        if (secondRowRfqNo is not null)
            WriteRow(sheet, 3, secondRowRfqNo);

        await using var stream = new MemoryStream(await package.GetAsByteArrayAsync());
        return await new QuotationUploaderService(context, NullLogger<QuotationUploaderService>.Instance)
            .UploadTemplateAsync(stream, Tenant, "importer@spine");
    }

    private static void WriteRow(ExcelWorksheet sheet, int row, string? customerRfqNo)
    {
        sheet.Cells[row, 1].Value = "QT-UPLOAD-SPINE";
        sheet.Cells[row, 2].Value = "Spine customer";
        sheet.Cells[row, 3].Value = "2026-08-01";
        sheet.Cells[row, 5].Value = "SPN";
        sheet.Cells[row, 6].Value = "Spine product";
        sheet.Cells[row, 7].Value = 2;
        sheet.Cells[row, 8].Value = 50;
        if (customerRfqNo is not null)
            sheet.Cells[row, 14].Value = customerRfqNo;
    }

    private static async Task<Graph> SeedAsync(TestDb db)
    {
        await using var seed = db.ContextFor(null);
        Seed.EnsureBusinessUnit(seed, Tenant);
        Seed.EnsureBusinessUnit(seed, ForeignTenant);
        var customer = Seed.Customer(seed, Tenant, Tenant, "Spine customer");
        Seed.Customer(seed, ForeignTenant, ForeignTenant, "Foreign customer");
        seed.Currencies.Add(new Currency
        {
            Id = 97_711, BusinessUnitId = Tenant, Code = "SPN", CurrencyName = "Spine currency",
            ExchangeRate = 1m, IsBaseCurrency = true, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        seed.Products.Add(new Product
        {
            Id = 97_712, Buid = Tenant, PartNo = "PART-SPINE", ProductName = "Spine product",
            IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        // QuotationUploaderService stamps StatusId 42 (Draft) directly rather than resolving it,
        // so the row has to exist for the insert's foreign key.
        seed.SetupMasters.Add(new SetupMaster
        {
            SetupId = 42, SetupType = "QuoteStatus", SetupCode = "DRAFT", SetupValue = "DRAFT",
            BusinessUnitId = Tenant, IsActive = true, CreatedBy = "qa", CreatedOn = Now
        });
        var lead = Seed.Lead(seed, 97_721, Tenant, buyersName: "Spine buyer");
        var foreignLead = Seed.Lead(seed, 97_722, ForeignTenant, buyersName: "Foreign buyer");
        // One lead per additional RFQ: the partial unique index on RFQ."LeadID" (one lead,
        // one RFQ) makes the earlier all-on-one-lead shape unrepresentable, as intended.
        var secondLead = Seed.Lead(seed, 97_723, Tenant, buyersName: "Spine buyer");
        var caselessLead = Seed.Lead(seed, 97_724, Tenant, buyersName: "Spine buyer");
        await seed.SaveChangesAsync();

        lead.ResolveCommercialIdentity(customer.Id, null, "CONFIRMED");
        secondLead.ResolveCommercialIdentity(customer.Id, null, "CONFIRMED");
        var caseId = lead.CommercialCaseId;
        var serial = lead.CommercialCaseReference;

        var rfq = NewRfq(97_731, "RFQ-SPINE-1", Tenant, lead.Id);
        rfq.InheritCommercialIdentity(lead);
        // A second linked RFQ, so "one quote, two RFQs" is testable and so the generated template
        // has a real sample reference to print.
        var second = NewRfq(97_732, "RFQ-SPINE-2", Tenant, secondLead.Id);
        second.InheritCommercialIdentity(secondLead);
        // Deliberately never inherits: an RFQ created outside the spine.
        var caseless = NewRfq(97_733, "RFQ-SPINE-CASELESS", Tenant, caselessLead.Id);
        var foreign = NewRfq(97_734, "RFQ-SPINE-FOREIGN", ForeignTenant, foreignLead.Id);
        foreign.InheritCommercialIdentity(foreignLead);
        seed.Rfqs.AddRange(rfq, second, caseless, foreign);
        await seed.SaveChangesAsync();

        return new Graph(caseId, serial, rfq.Id, rfq.Rfqno, second.Rfqno, caseless.Rfqno, foreign.Rfqno);
    }

    private static Rfq NewRfq(long id, string number, long businessUnitId, long leadId) => new()
    {
        Id = id, Rfqno = number, RecDate = Now, BusinessUnitId = businessUnitId, LeadId = leadId,
        CreatedBy = "qa", CreatedDate = Now
    };
}
