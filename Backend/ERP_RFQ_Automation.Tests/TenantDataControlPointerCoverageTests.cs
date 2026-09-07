using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.Delivery;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Retention;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The orphan sweep's one claim is "nothing points at these bytes". That claim is only as wide as
/// the set of tables it reads, and it was reading three of the eight places a pointer can live.
///
/// <para>The three it skipped are the ones with no source document behind them at all: proof of
/// delivery, mill certificates and the customer's own purchase-order document each write a cleared
/// object and record it on an <see cref="Attachment"/> row and nowhere else. A tenant owner ticking
/// "stored files nothing points to any more" destroyed them, permanently, while the audit event
/// asserted four proofs the code had never performed.</para>
///
/// <para>So these tests come in two halves. The first is the evidence itself — captured through
/// the production writer, not a hand-built row — surviving a real sweep. The second is the reason
/// this cannot happen again: the sweep now asks the model what pointer columns exist and refuses
/// to run at all when one of them is a column nobody has taught it about.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantDataControlPointerCoverageTests(PostgreSqlTestDatabase database)
    : IAsyncLifetime
{
    /// <summary>The business-unit band this class owns, so what it leaves behind is removable
    /// without guessing. Every id it writes is derived from one inside it.</summary>
    private const long FirstBu = 8_900_000;
    private const long LastBu = 9_400_000;

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Deletes the rows this class created. Several sweeps in this system are PLATFORM-wide, and
    /// so is the "Assembled always names a Lead" invariant another class asserts, so a message or
    /// a job left behind here is not inert — it is an input to somebody else's test.
    /// </summary>
    public async Task DisposeAsync()
    {
        await using var connection = await database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SET session_replication_role = replica;
            DELETE FROM public."EmailInquiryComponents" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."EmailInquiryAssemblies" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."ExtractionJobs" WHERE "BusinessUnitId" BETWEEN {FirstBu} AND {LastBu};
            DELETE FROM public."EmailIngests" WHERE "EmailConfigurationID" IN
                (SELECT "ID" FROM public."Email_Configurations"
                 WHERE "BusinessUnitID" BETWEEN {FirstBu} AND {LastBu});
            DELETE FROM public."Attachments" WHERE "FilePath" LIKE '%{StorageRootPrefix}%';
            SET session_replication_role = origin;
            """;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// THE test this fix exists for, and it is deliberately driven end to end.
    ///
    /// <para>The POD is captured by <see cref="DeliveryProofEvidenceService"/> itself — the real
    /// inspection, the real content-addressed write, the real <see cref="Attachment"/> row — so the
    /// fixture cannot disagree with what production emits. Then the tenant owner's own entry point
    /// runs, and the assertion that matters is the last one: the bytes are still readable through
    /// the same verified open <c>FileController.DownloadDeliveryProofEvidence</c> uses, because a
    /// surviving row pointing at a destroyed object is the failure this defect produced.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_proof_of_delivery_the_real_writer_captured_survives_a_tenant_owner_sweep()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var shipmentId = await SeedShipmentAsync(db, tenantId);

            var files = new LocalFileStorage(root, root);
            var storage = new LocalEvidenceObjectStorage(files);
            var pod = "%PDF-1.7\nsignature of the receiving storekeeper"u8.ToArray();
            var captured = await new DeliveryProofEvidenceService(db,
                new DocumentFileInspectionService(new CleanScanner()), storage,
                new NoopLogger<DeliveryProofEvidenceService>())
                .CaptureAsync(tenantId, shipmentId, DeliveryProofEvidenceService.SignatureKind,
                    "pod-signature.pdf", pod, "application/pdf", "storekeeper", default);

            db.ChangeTracker.Clear();
            var attachment = await db.Attachments.AsNoTracking()
                .SingleAsync(x => x.Id == captured.AttachmentId);

            // The fixture is only worth anything if the object it wrote is one the sweep would
            // otherwise consider: same prefix, same content-addressed shape, no source document.
            Assert.False(await db.Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocument>()
                .AnyAsync(x => x.BusinessUnitId == tenantId));
            var key = attachment.FilePath[attachment.FilePath.IndexOf("Evidence/tenants/",
                StringComparison.Ordinal)..].Replace('\\', '/');
            Assert.StartsWith($"Evidence/tenants/{tenantId}/cleared/", key, StringComparison.Ordinal);
            Assert.True(File.Exists(files.ResolvePath(key)));

            var service = NewService(db, files);
            var bucket = Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.OrphanedStoredFiles);
            Assert.Equal(0, bucket.Count);

            var result = await service.RunCleanupAsync(tenantId, 9, "sweep-pod",
                Clear(TenantDataBuckets.OrphanedStoredFiles), default);

            Assert.Equal(0, result.FilesDeleted);
            Assert.True(File.Exists(files.ResolvePath(key)),
                "The proof of delivery is the legal evidence behind the handover and has no second copy.");

            // What the tenant actually loses when this breaks: the download 404s forever.
            await using var opened = await storage.OpenVerifiedReadAsync(
                attachment.FilePath, captured.ContentSha256, default);
            Assert.Equal(pod.Length, opened.Length);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static TheoryData<string> PointerCases() =>
        ["email-inquiry-component", "extraction-job", "attachment-of-another-tenants-shape"];

    /// <summary>
    /// The same hole, in the other tables that hold a pointer and nothing else. A component's
    /// evidence object and an extraction job's immutable copy usually have a source document
    /// behind them — and "usually" is exactly the reasoning that lost the POD, because a document
    /// whose purge has completed releases its claim while these rows go on pointing at the object.
    ///
    /// <para>The third case is the other edge, and it has to be here or the first two prove only
    /// that the sweep has stopped working. Attachments carry no tenant column, so a row pointing
    /// into a NEIGHBOUR's prefix must not pin this tenant's object by content hash alone: that
    /// object still has nothing pointing at it and is still swept.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(PointerCases))]
    [Trait("Category", "PostgreSQL")]
    public async Task An_object_is_kept_exactly_when_a_live_row_here_points_at_it(string pointer)
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var (key, survives) = await SeedPointerOnlyObjectAsync(db, tenantId, files, pointer);

            var service = NewService(db, files);
            var bucket = Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.OrphanedStoredFiles);
            Assert.Equal(survives ? 0 : 1, bucket.Count);

            var result = await service.RunCleanupAsync(tenantId, 9, $"sweep-{pointer}",
                Clear(TenantDataBuckets.OrphanedStoredFiles), default);

            Assert.Equal(survives ? 0 : 1, result.FilesDeleted);
            Assert.Equal(survives, File.Exists(files.ResolvePath(key)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The standing guard, asserted against the model this build ships.
    ///
    /// <para>This is the test that fails the day someone adds a column that can hold a storage
    /// pointer, and it fails BEFORE the sweep can delete anything it does not understand. If it
    /// goes red, the fix is one query in <c>ReferencedKeysAsync</c> and one line in
    /// <c>ReviewedPointerColumns</c> — not an edit to this assertion.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public void Every_pointer_column_in_the_model_has_been_decided_about()
    {
        using var db = database.ContextFor(null);
        Assert.Null(TenantDataControlService.UnreviewedPointerColumn(db.Model));
    }

    /// <summary>
    /// And what happens when it has not been. A column the sweep has never been taught about makes
    /// "nothing points at these bytes" unprovable, so the whole bucket is withdrawn and named —
    /// including on the run path, where a genuine orphan is sitting on disk waiting to be deleted
    /// and is deliberately left alone.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_pointer_column_the_sweep_has_never_heard_of_withdraws_the_bucket()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = ContextWithAnUntaughtPointerColumn();
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var orphan = WriteObject(files, tenantId, "quarantine", ".pdf", "would have been swept");

            var service = NewService(db, files);
            var bucket = Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.OrphanedStoredFiles);
            Assert.Equal(0, bucket.Count);
            Assert.False(bucket.CanClear);
            Assert.Contains("ShipmentPhotoArchive.StorageUri", bucket.BlockedReason!);

            var result = await service.RunCleanupAsync(tenantId, 9, "sweep-untaught",
                Clear(TenantDataBuckets.OrphanedStoredFiles), default);
            Assert.Equal(0, result.FilesDeleted);
            Assert.True(File.Exists(files.ResolvePath(orphan)));
            Assert.Contains(result.Refused, x => x.Why!.Contains("ShipmentPhotoArchive.StorageUri"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ fixtures

    private static TenantDataControlService NewService(ErpRfqAutomationContext db, IFileStorage files) =>
        new(db, new LocalEvidenceObjectStorage(files), files,
            new CommercialDocumentArchiveService(db),
            new NoopLogger<TenantDataControlService>());

    private static TenantDataBucketView Bucket(TenantDataControlView view, string code) =>
        view.Buckets.Single(x => x.Code == code);

    private static TenantDataCleanupCommand Clear(params string[] buckets) =>
        new(buckets, false, "Free the space.", TenantDataControlCopy.ConfirmationPhrase);

    private sealed class CleanScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(MalwareScanResult.Clean("pointer-coverage-scanner"));
    }

    /// <summary>
    /// The production context, plus one entity nobody has told the sweep about. The model cache key
    /// is made unique as well, otherwise EF hands back the model it already built for this context
    /// type and the extra column never appears.
    /// </summary>
    private ErpRfqAutomationContext ContextWithAnUntaughtPointerColumn()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(database.ConnectionString)
            .ReplaceService<IModelCustomizer, AddsAnUntaughtPointerColumn>()
            .ReplaceService<IModelCacheKeyFactory, NeverCachedModel>()
            .EnableDetailedErrors()
            .Options;
        return new ErpRfqAutomationContext(options, new StubTenant(null));
    }

    private sealed class ShipmentPhotoArchive
    {
        public long Id { get; set; }
        public string StorageUri { get; set; } = null!;
    }

    private sealed class AddsAnUntaughtPointerColumn(ModelCustomizerDependencies dependencies)
        : RelationalModelCustomizer(dependencies)
    {
        public override void Customize(ModelBuilder modelBuilder, DbContext context)
        {
            base.Customize(modelBuilder, context);
            modelBuilder.Entity<ShipmentPhotoArchive>().ToTable("ShipmentPhotoArchive");
        }
    }

    private sealed class NeverCachedModel : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (context.GetType(), designTime, Guid.NewGuid());
    }

    private static async Task SeedTenantAsync(ErpRfqAutomationContext db, long tenantId)
    {
        Seed.EnsureBusinessUnit(db, tenantId);
        await db.SaveChangesAsync();
        db.Set<Tenant>().Add(new Tenant
        {
            Name = $"Pointer coverage tenant {tenantId}",
            Slug = $"pointer-coverage-{tenantId}",
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = tenantId,
            CreatedBy = "pointer-coverage-test",
            CreatedOn = DateTime.UtcNow
        });
        db.EmailConfigurations.Add(new EmailConfiguration
        {
            BusinessUnitId = tenantId,
            ConfigurationName = $"intake-{tenantId}",
            EmailAddress = $"intake-{tenantId}@tenant.test",
            Protocol = "IMAP",
            Host = "127.0.0.1",
            Port = 1,
            Username = $"intake-{tenantId}",
            Password = "secret",
            UseSsl = false,
            PollingInterval = 300,
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>A dispatched shipment — the only state a POD may be captured against.</summary>
    private static async Task<long> SeedShipmentAsync(ErpRfqAutomationContext db, long tenantId)
    {
        var now = DateTime.UtcNow;
        var status = Seed.LeadStatus(db, tenantId + 500_000, tenantId, "Dispatched");
        var currency = new Currency
        {
            BusinessUnitId = tenantId,
            Code = "SAR",
            CurrencyName = "Saudi Riyal",
            CreatedBy = "pointer-coverage-test",
            CreatedOn = now
        };
        db.Currencies.Add(currency);
        var customer = Seed.Customer(db, tenantId + 600_000, tenantId, "Receiving customer");
        await db.SaveChangesAsync();

        var order = new Order
        {
            OrderNo = $"SO-{tenantId}",
            CustomerId = customer.Id,
            BusinessUnitId = tenantId,
            CurrencyId = currency.Id,
            StatusId = status.SetupId,
            TotalAmount = 400m,
            OrderDate = now,
            CreatedBy = "pointer-coverage-test",
            CreatedOn = now,
            IsActive = true
        };
        db.Set<Order>().Add(order);
        await db.SaveChangesAsync();

        var shipment = new Shipment
        {
            ShipmentNo = $"DN-{tenantId}",
            OrderId = order.Id,
            BusinessUnitId = tenantId,
            StatusId = status.SetupId,
            ShipmentDate = now,
            DeliveryStatus = DeliveryStatuses.Dispatched,
            DeliveryStatusChangedBy = "pointer-coverage-test",
            DeliveryStatusChangedOn = now,
            CreatedBy = "pointer-coverage-test",
            CreatedOn = now,
            IsActive = true
        };
        db.Set<Shipment>().Add(shipment);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return shipment.Id;
    }

    private static string WriteObject(IFileStorage files, long tenantId, string zone,
        string extension, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content + Guid.NewGuid().ToString("N"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var key = LocalEvidenceObjectStorage.BuildKey(tenantId, zone, hash, extension)
            .Replace('\\', '/');
        var path = files.ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return key;
    }

    /// <summary>Places a stored object whose only database pointer is the row named, and reports
    /// whether the sweep must leave it alone.</summary>
    private static async Task<(string Key, bool Survives)> SeedPointerOnlyObjectAsync(
        ErpRfqAutomationContext db, long tenantId, IFileStorage files, string pointer)
    {
        var bytes = Encoding.UTF8.GetBytes("pointer-only evidence " + Guid.NewGuid().ToString("N"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var evidence = new LocalEvidenceObjectStorage(files);
        var now = DateTime.UtcNow;

        switch (pointer)
        {
            case "email-inquiry-component":
            {
                var stored = await evidence.WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);
                var config = await db.EmailConfigurations.SingleAsync(x => x.BusinessUnitId == tenantId);
                var ingest = new EmailIngest
                {
                    MessageId = $"component-{Guid.NewGuid():N}@example.test",
                    EmailSubject = "assembled",
                    FromEmail = "sender@example.test",
                    EmailConfigurationId = config.Id,
                    CreatedOn = now.AddDays(-400),
                    ParseStatus = "Parsed"
                };
                db.EmailIngests.Add(ingest);
                await db.SaveChangesAsync();

                var assembly = new EmailInquiryAssembly
                {
                    BusinessUnitId = tenantId,
                    EmailIngestId = ingest.Id,
                    EmailConfigurationId = config.Id,
                    MessageKey = ingest.MessageId,
                    ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
                    ExpectedComponentCount = 1,
                    // A settled message, not one mid-flight: an assembly the recovery sweep would
                    // still be re-driving is a fixture about scheduling, and this test is about an
                    // object that outlives the work that produced it. NeedsReview rather than
                    // Assembled because Assembled must name a Lead and this fixture has none.
                    Status = EmailInquiryAssemblyStatus.NeedsReview,
                    StatusReason = "Held for a person to read.",
                    CreatedAtUtc = now.AddDays(-400),
                    UpdatedAtUtc = now.AddDays(-400)
                };
                db.EmailInquiryAssemblies.Add(assembly);
                await db.SaveChangesAsync();

                db.EmailInquiryComponents.Add(new EmailInquiryComponent
                {
                    BusinessUnitId = tenantId,
                    AssemblyId = assembly.Id,
                    ComponentKey = $"email:{ingest.MessageId}:attachment:1",
                    Kind = EmailInquiryComponentKind.Attachment,
                    Ordinal = 1,
                    FileName = "enquiry.pdf",
                    MimeType = "application/pdf",
                    ByteSize = bytes.LongLength,
                    ContentHash = hash,
                    EvidenceUri = stored.StorageUri,
                    Status = EmailInquiryComponentStatus.Completed,
                    CreatedAtUtc = now.AddDays(-400),
                    UpdatedAtUtc = now.AddDays(-400)
                });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                return (stored.Key, true);
            }
            case "extraction-job":
            {
                var stored = await evidence.WriteImmutableAsync(tenantId, "quarantine", hash, ".pdf", bytes);
                db.Set<ExtractionJob>().Add(new ExtractionJob
                {
                    BusinessUnitId = tenantId,
                    BatchId = Guid.NewGuid(),
                    SourceType = ExtractionSourceType.ManualUpload,
                    StoragePath = stored.StorageUri,
                    FileName = "enquiry.pdf",
                    ContentHash = hash,
                    Status = ExtractionStatus.DeadLetter,
                    CreatedOn = now.AddDays(-400),
                    UpdatedOn = now.AddDays(-400)
                });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                return (stored.Key, true);
            }
            case "attachment-of-another-tenants-shape":
            {
                // The neighbour's POD, recorded on an attachment row that carries no tenant of its
                // own. Its digest must not pin an unrelated object of this tenant's: over-wide
                // protection retires the sweep as surely as a hole destroys evidence.
                var neighbour = await evidence.WriteImmutableAsync(tenantId + 1, "cleared", hash, ".pdf", bytes);
                db.Attachments.Add(new Attachment
                {
                    ParentType = DeliveryProofEvidenceService.EvidenceParentType,
                    ParentId = 99,
                    FileName = "pod-signature.pdf",
                    FilePath = neighbour.StorageUri,
                    ContentSha256 = hash,
                    CreatedOn = now,
                    UploadedDate = now
                });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();

                var ours = await evidence.WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);
                return (ours.Key, false);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(pointer), pointer, null);
        }
    }

    private static long NewTenantId() => Random.Shared.Next((int)FirstBu, (int)LastBu);

    /// <summary>Names every file this class writes, which is also how its attachment rows are
    /// found again for cleanup — attachments carry no tenant column to delete them by.</summary>
    private const string StorageRootPrefix = "nexora-pointer-coverage-";

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), StorageRootPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
