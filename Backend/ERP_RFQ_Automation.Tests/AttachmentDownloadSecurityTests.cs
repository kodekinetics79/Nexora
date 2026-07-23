using System.Security.Claims;
using System.Text;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class AttachmentDownloadSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-attachment-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadAttachment_ReturnsOnlyFilesOwnedByClaimedTenant()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        Seed.Lead(context, 202, 2);
        context.Attachments.AddRange(
            Attachment(1001, 101, "Manual_Attachments/tenant-one.txt"),
            Attachment(2002, 202, "Manual_Attachments/tenant-two.txt"));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/tenant-one.txt", Encoding.UTF8.GetBytes("tenant one"));
        await storage.WriteImmutableAsync("Manual_Attachments/tenant-two.txt", Encoding.UTF8.GetBytes("tenant two"));
        var controller = Controller(context, storage, businessUnitId: 1);

        var ownResult = await controller.DownloadAttachment(1001, CancellationToken.None);
        var ownFile = Assert.IsType<FileStreamResult>(ownResult);
        await using (ownFile.FileStream)
        using (var reader = new StreamReader(ownFile.FileStream))
            Assert.Equal("tenant one", await reader.ReadToEndAsync());

        var otherTenantResult = await controller.DownloadAttachment(2002, CancellationToken.None);
        Assert.IsType<NotFoundResult>(otherTenantResult);
    }

    [Fact]
    public async Task DownloadAttachment_RejectsMissingTenantClaim()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var controller = Controller(context, storage, businessUnitId: null);

        var result = await controller.DownloadAttachment(1, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadFile_RetiresPathAddressedAccess()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var controller = Controller(context, storage, businessUnitId: 1);

        var result = Assert.IsType<ObjectResult>(controller.DownloadFile("Uploads/secret.txt"));

        Assert.Equal(StatusCodes.Status410Gone, result.StatusCode);
    }

    private static Attachment Attachment(long id, long leadId, string path) => new()
    {
        Id = id,
        ParentType = "Lead",
        ParentId = leadId,
        FileName = Path.GetFileName(path),
        FilePath = path,
        MimeType = "text/plain",
        FileSize = 10,
        ContentType = "text",
        CreatedOn = DateTime.UtcNow
    };

    private static FileController Controller(
        ErpRfqAutomationContext context,
        IFileStorage storage,
        long? businessUnitId)
    {
        var claims = businessUnitId.HasValue
            ? new[] { new Claim("businessUnitId", businessUnitId.Value.ToString()) }
            : Array.Empty<Claim>();
        var controller = new FileController(context, storage, NullLogger<FileController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            }
        };
        return controller;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
