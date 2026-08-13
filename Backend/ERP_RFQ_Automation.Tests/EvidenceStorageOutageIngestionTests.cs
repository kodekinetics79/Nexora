using System.Text;
using Amazon.S3;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The data-level half of the 2026-08-12 incident, on the real EF write path.
///
/// <para>The controller tests prove what the operator is TOLD. These prove what the database
/// is left holding, which is the part that cannot be walked back: ingestion writes the
/// immutable source BEFORE it queues anything, so a store that cannot be written must leave
/// no corpus, no source, no occurrence and above all no extraction job — a job whose source
/// bytes do not exist is silent data loss, strictly worse than the refusal being fixed.</para>
///
/// <para>Relational SQLite against the real scaffolded model, so the foreign keys and unique
/// indexes that would hide a partial write are actually enforced. Every assertion reads from
/// a SECOND context, so it sees committed rows rather than the ingesting context's tracker.</para>
/// </summary>
public sealed class EvidenceStorageOutageIngestionTests : IDisposable
{
    private const long TenantId = 4_100_017;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nexora-evidence-outage-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// The incident itself: evidence storage repointed at a bucket that does not exist. The
    /// refusal must name the configuration fault, and the ledger must be exactly as empty as
    /// it was before the upload — no half-written source row to reconcile later.
    /// </summary>
    [Fact]
    public async Task MisconfiguredStore_RefusesTheDocumentAndLeavesNoLedgerRowBehind()
    {
        using var db = new TestDb();
        var storage = new FaultyEvidenceObjectStorage(
            new AmazonS3Exception("The specified bucket does not exist: NexoraB2")
            {
                ErrorCode = "NoSuchBucket"
            });
        await using var context = SeededContext(db);

        var refusal = await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() =>
            NewIngestion(context, storage).IngestAsync(
                ValidCsv(), "C001046526.doc", TenantId, ExtractionSourceType.ManualUpload));

        Assert.True(refusal.IsConfigurationFault);
        // Waiting cannot fix a typo, so the sentence must not offer waiting as the remedy.
        Assert.Equal(
            "Retrying will not help until an administrator corrects the document storage settings.",
            refusal.OperatorNextAction);
        // The write was attempted, not skipped: the refusal is the store's own answer.
        Assert.Equal(1, storage.WriteAttempts);
        await AssertLedgerIsEmptyAsync(db);
    }

    /// <summary>
    /// A provider having a bad minute is the same shape of refusal but the OPPOSITE next
    /// action, and it must still not leave a job behind. Collapsing the two would either tell
    /// someone to wait out a typo or send them to an administrator over a thirty-second blip.
    /// </summary>
    [Fact]
    public async Task UnreachableStore_RefusesAsATransientOutageAndStillLeavesNoLedgerRowBehind()
    {
        using var db = new TestDb();
        var storage = new FaultyEvidenceObjectStorage(
            new AmazonS3Exception("We encountered an internal error. Please try again.")
            {
                ErrorCode = "ServiceUnavailable"
            });
        await using var context = SeededContext(db);

        var refusal = await Assert.ThrowsAsync<EvidenceStorageUnavailableException>(() =>
            NewIngestion(context, storage).IngestAsync(
                ValidCsv(), "customer-rfq.csv", TenantId, ExtractionSourceType.ManualUpload));

        Assert.False(refusal.IsConfigurationFault);
        Assert.Equal("Document storage is unavailable, so uploads are paused.", refusal.Message);
        Assert.Contains("try again shortly", refusal.OperatorNextAction, StringComparison.Ordinal);
        await AssertLedgerIsEmptyAsync(db);
    }

    /// <summary>
    /// The inverse guard. A verdict about ONE document must reach the caller as itself, so the
    /// upload door keeps answering it per file. Flattening it into a storage outage would pause
    /// every tenant's uploads over a single malformed file — the 2026-08-12 defect inverted.
    /// </summary>
    [Fact]
    public async Task PerDocumentFault_StaysThatDocumentsOwnFaultAndNeverBecomesAStoreOutage()
    {
        using var db = new TestDb();
        await using var context = SeededContext(db);
        Directory.CreateDirectory(_root);

        // A real refusal from the real local store: the extension is not a legal evidence
        // extension, which is a fact about this file and about nothing else.
        var fault = await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            NewIngestion(context, LocalStorage()).IngestAsync(
                ValidCsv(), "quarterly.rfq-attachment", TenantId, ExtractionSourceType.ManualUpload));

        Assert.IsNotType<EvidenceStorageUnavailableException>(fault);
        await AssertLedgerIsEmptyAsync(db);
    }

    // The healthy-store regression is the twin of these three and lives in
    // EvidenceStorageOutageIngestionPostgreSqlTests: a successful ingest reaches the
    // duplicate-candidate lookup, which orders by a DateTimeOffset that SQLite cannot
    // translate. Every full-ingest test in this repo is PostgreSQL-only for that reason.
    // The refusals above stop before that query, which is why they run here.

    /// <summary>
    /// Every table ingestion touches, checked from a context that can only see committed rows.
    /// Naming them individually rather than counting is deliberate: a future table added to the
    /// intake transaction should make this test fail to compile-by-inspection, not pass quietly.
    /// </summary>
    private static async Task AssertLedgerIsEmptyAsync(TestDb db)
    {
        await using var verify = db.ContextFor(TenantId);
        Assert.Empty(await verify.Set<ExtractionJob>().Where(x => x.BusinessUnitId == TenantId).ToListAsync());
        Assert.Empty(await verify.Set<SourceDocument>().Where(x => x.BusinessUnitId == TenantId).ToListAsync());
        Assert.Empty(await verify.Set<SourceDocumentOccurrence>().Where(x => x.BusinessUnitId == TenantId).ToListAsync());
        Assert.Empty(await verify.Set<DocumentCorpus>().Where(x => x.BusinessUnitId == TenantId).ToListAsync());
        Assert.Empty(await verify.Set<LeadIngestionBatch>().Where(x => x.BusinessUnitId == TenantId).ToListAsync());
    }

    private static ErpRfqAutomationContext SeededContext(TestDb db)
    {
        var context = db.ContextFor(null);
        Seed.EnsureBusinessUnit(context, TenantId);
        context.SaveChanges();
        context.ChangeTracker.Clear();
        return context;
    }

    private static DocumentIngestionService NewIngestion(
        ErpRfqAutomationContext context, IEvidenceObjectStorage storage) =>
        new(new ExtractionQueue(context, new NoopLogger<ExtractionQueue>(), new StubTenant(null)),
            storage,
            new ClearedInspectionService(),
            context,
            new NoopLogger<DocumentIngestionService>());

    private LocalEvidenceObjectStorage LocalStorage() => new(new LocalFileStorage(_root, _root));

    private static byte[] ValidCsv() => Encoding.UTF8.GetBytes(
        "RFQ No,Buyer Name,Product Name,Quantity,Unit Price,Currency\n"
        + "RFQ-OUTAGE-1,Acme Procurement,Pressure Sensor,5,125.50,USD\n");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// A store that answers every write with one provider fault, classified by the SAME
    /// allow-list the real S3 store uses. Constructing the wrapped exception directly would let
    /// a test assert the label it chose itself; routing through
    /// <see cref="EvidenceStorageFaults"/> means these tests fail if the classification drifts.
    /// </summary>
    private sealed class FaultyEvidenceObjectStorage : IEvidenceObjectStorage
    {
        private readonly Exception _providerFault;

        public FaultyEvidenceObjectStorage(Exception providerFault) => _providerFault = providerFault;

        public int WriteAttempts { get; private set; }

        public bool IsDurable => true;

        public Task ProbeAsync(CancellationToken ct = default) => throw Classify(ct);

        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> content, CancellationToken ct = default)
        {
            WriteAttempts++;
            throw Classify(ct);
        }

        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default) =>
            throw Classify(ct);

        private Exception Classify(CancellationToken ct) =>
            EvidenceStorageFaults.IsStoreUnavailable(_providerFault, ct)
                ? EvidenceStorageFaults.Unavailable(_providerFault)
                : _providerFault;
    }

    private sealed class ClearedInspectionService : IFileInspectionService
    {
        public Task<FileInspectionResult> InspectAsync(
            FileInspectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileInspectionResult(
                FileInspectionStatus.Cleared, "text/csv",
                request.DeclaredLength ?? 0, "Inspection and malware scan passed.",
                "test-scanner", "clean")
            {
                MalwareStatus = MalwareScanStatus.Clean,
                ErrorCode = "security_scan_cleared"
            });
    }
}
