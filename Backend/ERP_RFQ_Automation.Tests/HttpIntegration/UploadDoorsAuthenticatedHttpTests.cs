using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

/// <summary>
/// The upload doors through the REAL host: DI resolves the real
/// <c>DocumentFileInspectionService</c>, and the Testing environment selects the fail-closed
/// ClamAV scanner at an endpoint where nothing listens — exactly the posture a misconfigured
/// deployment would have. So a workbook whose bytes are not a workbook is rejected on its
/// signature before any scanner is consulted, and a well-formed CSV reaches the scanner, gets no
/// answer, and is REFUSED rather than parsed.
/// </summary>
[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class UploadDoorsAuthenticatedHttpTests(Release01BHttpApplication app)
{
    [Fact]
    public async Task A_spreadsheet_whose_bytes_are_not_a_workbook_is_rejected_with_the_shared_problem_shape()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("this is not a workbook"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        form.Add(file, "file", "products.xlsx");

        var response = await client.PostAsync("/api/ProductUploader/upload-template", form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("document_rejected", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("Rejected", body.RootElement.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("detail").GetString()));
        Assert.Equal(body.RootElement.GetProperty("detail").GetString(), body.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_bank_statement_is_refused_when_the_malware_scanner_cannot_answer()
    {
        using var client = Client(Release01BHttpApplication.AllowedRole, Release01BHttpApplication.TenantA);
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent("Date,Description,Amount\n2026-09-01,Opening,100.00\n"u8.ToArray());
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        form.Add(new StringContent("1"), "bankAccountId");
        form.Add(new StringContent("CSV"), "sourceType");
        form.Add(file, "file", "statement.csv");
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));

        var response = await client.PostAsync("/api/treasury/statements/import", form);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("30", response.Headers.RetryAfter?.ToString());
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("security_scanner_unavailable", body.RootElement.GetProperty("errorCode").GetString());
        Assert.Equal("AwaitingSecurityScan", body.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task The_upload_doors_still_deny_a_role_without_the_module()
    {
        using var denied = Client(Release01BHttpApplication.DeniedRole, Release01BHttpApplication.TenantA);
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent("x"u8.ToArray()), "file", "products.xlsx");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await denied.PostAsync("/api/ProductUploader/upload-template", form)).StatusCode);
    }

    private HttpClient Client(long roleId, long? tenantId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", app.Token(roleId, tenantId));
        return client;
    }
}
