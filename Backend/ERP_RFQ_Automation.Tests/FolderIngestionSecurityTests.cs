using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class FolderIngestionSecurityTests
{
    [Theory]
    [InlineData(nameof(EmailController.ManualFetchAndSaveLeads))]
    [InlineData(nameof(EmailController.UploadLeadsToFolder))]
    [InlineData(nameof(EmailController.ProcessAllFolderLeads))]
    public void EmailIngestionMutations_RequireLeadCreatePermission(string methodName)
    {
        var method = typeof(EmailController).GetMethods().Single(x => x.Name == methodName);
        var permission = method.GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>().Single();

        Assert.Equal("Leads", permission.ModuleName);
        Assert.Equal(PermissionAction.Create, permission.Action);
    }

    [Fact]
    public async Task WatchedFolders_AreTenantScopedAndOnlyRequestedTenantIsEnqueued()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-folder-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var storage = new LocalFileStorage(root, root);
            var ingestion = new RecordingIngestion();
            var service = new FolderService(
                context,
                new TestEnvironment(root),
                NullLogger<FolderService>.Instance,
                new NullLlmService(),
                storage,
                ingestion);

            await service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("tenant-42.pdf") }, "Shared", 42);
            await service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("tenant-84.pdf") }, "Shared", 84);

            var report = await service.ProcessAllFoldersAsync(42);

            Assert.Equal(1, report.Enqueued);
            Assert.Equal(0, report.Failed);
            Assert.Single(ingestion.Requests);
            Assert.Equal(42, ingestion.Requests[0].BusinessUnitId);
            Assert.Contains("tenant-42.pdf", ingestion.Requests[0].FileName);
            Assert.Empty(Directory.GetFiles(storage.GetPath("Tenants", "42", "Watched", "Shared")));
            Assert.Single(Directory.GetFiles(storage.GetPath("Tenants", "84", "Watched", "Shared")));
            Assert.Single(Directory.GetFiles(storage.GetPath("Tenants", "42", "Processed", "Shared_Leads")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WatchedFolderUpload_RejectsUnknownFolderType()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-folder-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var service = new FolderService(
                context,
                new TestEnvironment(root),
                NullLogger<FolderService>.Instance,
                new NullLlmService(),
                new LocalFileStorage(root, root),
                new RecordingIngestion());

            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("rfq.pdf") }, "../../escape", 42));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RepeatedStagingFailure_IsQuarantinedAfterThreeAttempts()
    {
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var ingestion = new RecordingIngestion { Failure = new IOException("unavailable") };
            var (service, storage) = Service(context, root, ingestion);
            await service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("poison.pdf") }, "Shared", 42);

            Assert.Equal(1, (await service.ProcessAllFoldersAsync(42)).Failed);
            Assert.Equal(1, (await service.ProcessAllFoldersAsync(42)).Failed);
            var final = await service.ProcessAllFoldersAsync(42);

            Assert.Equal(1, final.Rejected);
            Assert.Empty(Directory.GetFiles(storage.GetPath("Tenants", "42", "Watched", "Shared")));
            Assert.Equal(2, Directory.GetFiles(storage.GetPath("Tenants", "42", "Quarantine", "Shared_Leads")).Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeadLetterDuplicate_IsQuarantinedInsteadOfArchived()
    {
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var ingestion = new RecordingIngestion
            {
                Outcome = EnqueueOutcome.Duplicate,
                ExistingStatus = ExtractionStatus.DeadLetter
            };
            var (service, storage) = Service(context, root, ingestion);
            await service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("dead.pdf") }, "Shared", 42);

            var report = await service.ProcessAllFoldersAsync(42);

            Assert.Equal(1, report.Rejected);
            Assert.Equal(0, report.Duplicates);
            Assert.False(Directory.Exists(storage.GetPath("Tenants", "42", "Processed", "Shared_Leads")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Cancellation_PropagatesAndLeavesNoPartialUpload()
    {
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var (service, storage) = Service(context, root, new RecordingIngestion());
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("cancelled.pdf") }, "Shared", 42, cancellation.Token));

            var watched = storage.GetPath("Tenants", "42", "Watched", "Shared");
            Assert.Empty(Directory.GetFiles(watched, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SymlinkFile_IsRejectedWithoutReadingExternalContent()
    {
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var ingestion = new RecordingIngestion();
            var (service, storage) = Service(context, root, ingestion);
            await service.ProcessAllFoldersAsync(42);
            var external = Path.Combine(root, "external.pdf");
            await File.WriteAllTextAsync(external, "%PDF-1.7 external");
            File.CreateSymbolicLink(
                Path.Combine(storage.GetPath("Tenants", "42", "Watched", "Shared"), "linked.pdf"), external);

            var report = await service.ProcessAllFoldersAsync(42);

            Assert.Equal(1, report.Rejected);
            Assert.Empty(ingestion.Requests);
            Assert.Equal("%PDF-1.7 external", await File.ReadAllTextAsync(external));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentSweeps_AtomicallyClaimAFileOnce()
    {
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var firstContext = database.ContextFor(null);
            await using var secondContext = database.ContextFor(null);
            var ingestion = new RecordingIngestion { Delay = TimeSpan.FromMilliseconds(100) };
            var (first, _) = Service(firstContext, root, ingestion);
            var (second, _) = Service(secondContext, root, ingestion);
            await first.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("single.pdf") }, "Shared", 42);
            var watched = Path.Combine(root, "Tenants", "42", "Watched", "Shared");
            File.SetLastWriteTimeUtc(Directory.GetFiles(watched).Single(), DateTime.UtcNow.AddHours(-1));

            var reports = await Task.WhenAll(
                first.ProcessAllFoldersAsync(42), second.ProcessAllFoldersAsync(42));

            Assert.Single(ingestion.Requests);
            Assert.Equal(1, reports.Sum(x => x.Enqueued));
            Assert.Equal(0, reports.Sum(x => x.Failed));
            Assert.Empty(Directory.GetFiles(watched, "*.nexora-retry.json"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentUnsupportedFile_IsClaimedAndQuarantinedOnce()
    {
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var firstContext = database.ContextFor(null);
            await using var secondContext = database.ContextFor(null);
            var ingestion = new RecordingIngestion();
            var (first, storage) = Service(firstContext, root, ingestion);
            var (second, _) = Service(secondContext, root, ingestion);
            await first.ProcessAllFoldersAsync(42);
            await File.WriteAllTextAsync(
                Path.Combine(storage.GetPath("Tenants", "42", "Watched", "Shared"), "payload.exe"), "MZ");

            var reports = await Task.WhenAll(
                first.ProcessAllFoldersAsync(42), second.ProcessAllFoldersAsync(42));

            Assert.Equal(1, reports.Sum(x => x.Rejected));
            Assert.Empty(ingestion.Requests);
            var quarantine = storage.GetPath("Tenants", "42", "Quarantine", "Shared_Leads");
            Assert.Equal(2, Directory.GetFiles(quarantine).Length);
            Assert.Empty(Directory.GetFiles(quarantine, "*.pending"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadStagingSymlink_CannotRedirectOutsideTenantStorage()
    {
        var root = TempRoot();
        var external = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var (service, storage) = Service(context, root, new RecordingIngestion());
            await service.ProcessAllFoldersAsync(42);
            File.CreateSymbolicLink(
                Path.Combine(storage.GetPath("Tenants", "42", "Watched", "Shared"), ".staging"), external);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("redirect.pdf") }, "Shared", 42));

            Assert.Empty(Directory.GetFiles(external));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(external, recursive: true);
        }
    }

    // ------------------------------------------------- what a folder is allowed to read

    [Fact]
    public async Task AramcoFolder_ReadsTheLegacyDocFormatItsCustomerActuallySends()
    {
        // THE DEFECT, EXACTLY. The Aramco watched folder was gated to `ext == ".docx"`, and
        // every Aramco bid list in the live corpus is a legacy .doc. The folder named after
        // that customer therefore skipped that customer's own documents, silently: the sweep
        // logged nothing, raised nothing, and left the file sitting there for ever.
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var ingestion = new RecordingIngestion();
            var (service, storage) = Service(context, root, ingestion);

            await service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("aramco-bid-C001046933.doc") }, "Aramco", 42);

            var report = await service.ProcessAllFoldersAsync(42);

            Assert.Equal(1, report.Enqueued);
            Assert.Equal(0, report.Rejected);
            Assert.Contains("aramco-bid-C001046933.doc", Assert.Single(ingestion.Requests).FileName);
            // Read means CONSUMED: the file leaves the watched folder rather than being swept
            // again on every cycle for ever.
            Assert.Empty(Directory.GetFiles(storage.GetPath("Tenants", "42", "Watched", "Aramco")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("SEC")]
    [InlineData("Aramco")]
    [InlineData("Shared")]
    public async Task EveryWatchedFolder_AcceptsEveryFormatIntakeCanRead(string folderType)
    {
        // The three folders used to disagree about file types — .doc here, .docx there, five
        // formats in the third — so which folder a document was filed in decided whether it was
        // read at all. The folder says who a document is FROM; it has never had anything to say
        // about what a document is allowed to BE.
        //
        // Both halves are covered: SaveFilesToSharedFolderAsync throws if the upload gate
        // refuses a format, and Enqueued falling short of the count catches a sweep filter that
        // silently skips one.
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var ingestion = new RecordingIngestion();
            var (service, _) = Service(context, root, ingestion);

            var formats = ERP_RFQ_Automation.Security.DocumentInspection
                .DocumentIntakeAllowList.Extensions
                .OrderBy(x => x, StringComparer.Ordinal).ToList();

            await service.SaveFilesToSharedFolderAsync(
                formats.Select((ext, i) => (IFormFile)FormFile($"bid-{i}{ext}")).ToList(),
                folderType, 42);

            var report = await service.ProcessAllFoldersAsync(42);

            Assert.Equal(formats.Count, report.Enqueued);
            Assert.Equal(0, report.Rejected);
            Assert.Equal(0, report.Failed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AFormatInspectionWouldRefuse_IsStillRefusedAtTheFolderDoor()
    {
        // Widening the door must not mean opening it. An extension inspection does not admit is
        // refused before anything is written to disk — the folder door and the inspection layer
        // read from ONE list, so they cannot drift into disagreeing about this.
        var root = TempRoot();
        try
        {
            using var database = new TestDb();
            await using var context = database.ContextFor(null);
            var (service, storage) = Service(context, root, new RecordingIngestion());

            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveFilesToSharedFolderAsync(
                new List<IFormFile> { FormFile("payload.exe") }, "Shared", 42));

            Assert.Empty(Directory.GetFiles(storage.GetPath("Tenants", "42", "Watched", "Shared")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FormFile FormFile(string name)
    {
        var bytes = "%PDF-1.7\nRFQ"u8.ToArray();
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "files", name);
    }

    private static string TempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-folder-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static (FolderService Service, LocalFileStorage Storage) Service(
        ErpRfqAutomationContext context, string root, RecordingIngestion ingestion)
    {
        var storage = new LocalFileStorage(root, root);
        return (new FolderService(
            context, new TestEnvironment(root), NullLogger<FolderService>.Instance,
            new NullLlmService(), storage, ingestion), storage);
    }

    private sealed class RecordingIngestion : IDocumentIngestion
    {
        public List<(string FileName, long BusinessUnitId)> Requests { get; } = new();
        public Exception? Failure { get; init; }
        public EnqueueOutcome Outcome { get; init; } = EnqueueOutcome.Enqueued;
        public ExtractionStatus? ExistingStatus { get; init; }
        public TimeSpan Delay { get; init; }

        public async Task<IngestedDocument> IngestAsync(
            byte[] bytes, string fileName, long businessUnitId, ExtractionSourceType sourceType,
            Guid? batchId = null, int priority = 0, ExtractionJobMetadata? metadata = null,
            long? emailInquiryComponentId = null,
            CancellationToken ct = default)
        {
            if (Failure is not null) throw Failure;
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
            lock (Requests) Requests.Add((fileName, businessUnitId));
            return new IngestedDocument
            {
                JobId = Requests.Count, BatchId = batchId ?? Guid.NewGuid(), ContentHash = "hash",
                StoragePath = "immutable", Outcome = Outcome, ExistingStatus = ExistingStatus
            };
        }
    }

    private sealed class NullLlmService : ILLMService
    {
        public Task<LeadExtractionResult?> ExtractLeadDataAsync(
            string fullText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<LeadExtractionResult?>(null);

        public Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
            => Task.FromResult<BoqDraftResult?>(null);
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
