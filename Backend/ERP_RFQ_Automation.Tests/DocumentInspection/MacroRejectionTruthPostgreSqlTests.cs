using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests.DocumentInspection;

/// <summary>
/// The whole path the owner's upload actually travels: real inspection → real ingestion gateway →
/// occurrence row → batch reconciliation DTO. On the production dialect, because
/// <c>DocumentIngestionService</c> orders occurrences by a <c>DateTimeOffset</c>, which SQLite
/// cannot translate.
///
/// What is under test is not that the file is refused — it always was — but that the reason the
/// user is given survives every hop instead of being replaced by a generic sentence.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class MacroRejectionTruthPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public MacroRejectionTruthPostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Macro_rejection_reaches_the_batch_read_model_intact()
    {
        var tenantId = NewTenantId();
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();

            var ingestion = NewIngestion(context, root,
                new DocumentFileInspectionService(new EicarMalwareScanner()));
            var batchId = Guid.NewGuid();

            var stopped = await Assert.ThrowsAsync<DocumentInspectionException>(() => ingestion.IngestAsync(
                MacroPolicyAndRejectionTruthTests.CreateOleCompound("Workbook", "_VBA_PROJECT_CUR"),
                "C001046190.xls", tenantId, ExtractionSourceType.ManualUpload, batchId));

            // (a) what ExtractionController turns into the governed-upload row's `reason`.
            Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, stopped.Inspection.ErrorCode);
            Assert.Contains("contains macros", stopped.Inspection.Reason, StringComparison.OrdinalIgnoreCase);

            // (b) persisted with the occurrence — this is the record that outlives the request.
            context.ChangeTracker.Clear();
            var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(IntakeOccurrenceStatus.Rejected, occurrence.IntakeStatus);
            Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, occurrence.LastErrorCode);
            using var details = JsonDocument.Parse(occurrence.LastErrorDetailsJson!);
            Assert.Contains("contains macros",
                details.RootElement.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);

            // (c) projected onto the DTO the ingestion page reads.
            context.ChangeTracker.Clear();
            var batch = await new LeadIdentityApplicationService(context).GetBatchAsync(tenantId, batchId);
            var item = Assert.Single(batch!.Items);
            Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, item.ErrorCode);
            Assert.False(item.RecoverableSecurityHold);
            Assert.Contains(item.Reasons, reason =>
                reason.Contains("contains macros", StringComparison.OrdinalIgnoreCase)
                && reason.Contains(".xlsx", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The retry path used to degrade the explanation to vacuum: a re-upload whose OWN inspection
    /// passes, attached to a source document an earlier occurrence already rejected, replaced the
    /// recorded reason with "The authoritative source document has not passed security inspection."
    /// A user's account of what is wrong must not get worse because they tried again.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Reupload_of_an_already_rejected_source_carries_the_recorded_reason_forward()
    {
        var tenantId = NewTenantId();
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();

            var bytes = MacroPolicyAndRejectionTruthTests.CreateOleCompound("Workbook", "_VBA_PROJECT_CUR");
            var verdict = new SwitchableInspection(
                new DocumentFileInspectionService(new EicarMalwareScanner()));
            var ingestion = NewIngestion(context, root, verdict);

            var first = await Assert.ThrowsAsync<DocumentInspectionException>(() => ingestion.IngestAsync(
                bytes, "C001046190.xls", tenantId, ExtractionSourceType.ManualUpload, Guid.NewGuid(),
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "macro-upload-1" }));
            Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, first.Inspection.ErrorCode);

            // The verdict flips to "clear" for the retry, but the SOURCE document stays Rejected —
            // this is the exact shape that used to produce the generic sentence.
            verdict.ClearEverything = true;
            context.ChangeTracker.Clear();
            var retry = await Assert.ThrowsAsync<DocumentInspectionException>(() => ingestion.IngestAsync(
                bytes, "C001046190.xls", tenantId, ExtractionSourceType.ManualUpload, Guid.NewGuid(),
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "macro-upload-2" }));

            Assert.DoesNotContain("has not passed security inspection", retry.Inspection.Reason,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains("contains macros", retry.Inspection.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, retry.Inspection.ErrorCode);

            context.ChangeTracker.Clear();
            var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == retry.SourceDocumentOccurrenceId);
            Assert.Equal(DocumentInspectionErrorCodes.MacroEnabledDocument, occurrence.LastErrorCode);
            using var details = JsonDocument.Parse(occurrence.LastErrorDetailsJson!);
            Assert.Contains("contains macros",
                details.RootElement.GetProperty("reason").GetString()!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DocumentIngestionService NewIngestion(
        ErpRfqAutomationContext context,
        string root,
        IFileInspectionService inspection) => new(
            new ExtractionQueue(context, new NoopLogger<ExtractionQueue>()),
            new LocalEvidenceObjectStorage(new LocalFileStorage(root, root)),
            inspection,
            context,
            new NoopLogger<DocumentIngestionService>());

    private static void SeedTenant(ErpRfqAutomationContext context, long tenantId)
    {
        Seed.EnsureBusinessUnit(context, tenantId);
        Seed.EmailConfig(context, tenantId * 10 + 1, tenantId);
    }

    private static long NewTenantId() => Random.Shared.Next(2_000_000, 8_000_000);

    private static string NewStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-macro-truth-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    /// <summary>Real inspection until <see cref="ClearEverything"/> flips, then a clean verdict.</summary>
    private sealed class SwitchableInspection(IFileInspectionService inner) : IFileInspectionService
    {
        public bool ClearEverything { get; set; }

        public async Task<FileInspectionResult> InspectAsync(
            FileInspectionRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await inner.InspectAsync(request, cancellationToken);
            if (!ClearEverything)
                return result;
            return new FileInspectionResult(
                FileInspectionStatus.Cleared, "application/vnd.ms-excel", result.InspectedLength,
                "File signature, archive safety, and malware checks passed.", "test-scanner", "clean")
            {
                MalwareStatus = MalwareScanStatus.Clean,
                ErrorCode = "security_scan_cleared"
            };
        }
    }
}
