using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ExtractionDeadLetterPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Governed_retry_reopens_the_owned_email_component_and_message_atomically()
    {
        const long tenant = 98_305;
        long jobId;
        long componentId;
        long assemblyId;
        await using (var owner = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenant);
            var config = Seed.EmailConfig(owner, 98_305, tenant);
            var ingest = Seed.EmailIngest(owner, 98_305, config.Id, "Failed");
            await owner.SaveChangesAsync();

            var now = DateTimeOffset.UtcNow;
            var assembly = new EmailInquiryAssembly
            {
                BusinessUnitId = tenant,
                EmailIngestId = ingest.Id,
                EmailConfigurationId = config.Id,
                MessageKey = ingest.MessageId,
                RawEvidenceUri = "s3://test-evidence/raw-mail/message.eml",
                RawEvidenceSha256 = new string('a', 64),
                ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
                ExpectedComponentCount = 1,
                Status = EmailInquiryAssemblyStatus.NeedsReview,
                StatusReason = "extraction_dead_letter",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            var component = new EmailInquiryComponent
            {
                BusinessUnitId = tenant,
                ComponentKey = $"email:{ingest.MessageId}:body",
                Kind = EmailInquiryComponentKind.Body,
                Ordinal = 0,
                MimeType = "text/plain",
                ContentHash = new string('c', 64),
                Status = EmailInquiryComponentStatus.Skipped,
                ReasonCode = "processing_timeout",
                ReasonDetail = "Extraction stopped after its retry budget.",
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            assembly.Components.Add(component);
            owner.Add(assembly);
            await owner.SaveChangesAsync();

            var job = Job(tenant, 'c');
            job.SourceType = ExtractionSourceType.Email;
            job.EmailInquiryComponentId = component.Id;
            owner.Add(job);
            await owner.SaveChangesAsync();
            component.ExtractionJobId = job.Id;
            await owner.SaveChangesAsync();

            jobId = job.Id;
            componentId = component.Id;
            assemblyId = assembly.Id;
        }

        await using var runtime = database.TenantContextWithRls(tenant);
        var result = await Service(runtime).RecoverAsync(
            tenant, jobId, "email-recovery-operator",
            new("The transient extraction dependency is healthy.", "email-component-retry"), default);

        Assert.Equal("RetryQueued", result.Status);
        var componentAfter = await runtime.EmailInquiryComponents.AsNoTracking()
            .SingleAsync(x => x.Id == componentId);
        var assemblyAfter = await runtime.EmailInquiryAssemblies.AsNoTracking()
            .SingleAsync(x => x.Id == assemblyId);
        Assert.Equal(EmailInquiryComponentStatus.Pending, componentAfter.Status);
        Assert.Null(componentAfter.ReasonCode);
        Assert.Equal(EmailInquiryAssemblyStatus.Extracting, assemblyAfter.Status);
        Assert.Null(assemblyAfter.AssembledLeadId);
        Assert.Equal(ExtractionStatus.Pending,
            (await runtime.Set<ExtractionJob>().AsNoTracking().SingleAsync(x => x.Id == jobId)).Status);
        Assert.Single(await runtime.ExtractionDeadLetterEvents.AsNoTracking()
            .Where(x => x.ExtractionJobId == jobId && x.IdempotencyKey == "email-component-retry")
            .ToListAsync());

        // This collection deliberately shares one migrated PostgreSQL database. Return the
        // fixture to a terminal hold so later platform-wide recovery sweeps do not mistake this
        // test's successfully reopened component for their own stranded work.
        await runtime.Set<ExtractionJob>().Where(x => x.Id == jobId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, ExtractionStatus.DeadLetter));
        await runtime.EmailInquiryComponents.Where(x => x.Id == componentId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, EmailInquiryComponentStatus.Skipped)
            .SetProperty(x => x.ReasonCode, "test_fixture_terminal"));
        await runtime.EmailInquiryAssemblies.Where(x => x.Id == assemblyId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.Status, EmailInquiryAssemblyStatus.NeedsReview));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RuntimeRole_ExecutesAuthoritativeRecoveryThroughRls()
    {
        const long tenant = 98_303;
        long jobId;
        await using (var owner = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenant);
            var job = Job(tenant, 'c');
            owner.Add(job);
            await owner.SaveChangesAsync();
            jobId = job.Id;
        }

        await using var runtime = database.TenantContextWithRls(tenant);
        var service = Service(runtime);
        var result = await service.RecoverAsync(tenant, jobId, "runtime-operator",
            new("Runtime RLS recovery verification.", "runtime-recovery"), default);

        Assert.Equal("RetryQueued", result.Status);
        Assert.False(result.BlocksReadiness);
        Assert.Equal(ExtractionStatus.Pending,
            (await runtime.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId)).Status);
        Assert.Equal(IntakeOccurrenceStatus.Queued,
            (await runtime.Set<SourceDocumentOccurrence>().SingleAsync(x =>
                x.ExtractionJobId == jobId)).IntakeStatus);
        Assert.Equal(ExtractionDeadLetterAction.RetryQueued,
            (await runtime.ExtractionDeadLetterEvents.SingleAsync(x => x.ExtractionJobId == jobId)).Action);

        var claimed = await runtime.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ExtractionJobs"
            SET "Status" = 'Leased',
                "LeasedBy" = 'postgres-test-worker',
                "LeaseExpiresAt" = now() + interval '1 minute',
                "Attempts" = "Attempts" + 1,
                "UpdatedOn" = now()
            WHERE "BusinessUnitId" = {tenant} AND "Id" = {jobId}
            """);
        Assert.Equal(1, claimed);
        Assert.Equal(IntakeOccurrenceStatus.Processing,
            (await runtime.Set<SourceDocumentOccurrence>().AsNoTracking().SingleAsync(x =>
                x.ExtractionJobId == jobId)).IntakeStatus);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentSameKey_QueuesExactlyOneRetryWithoutServerError()
    {
        const long tenant = 98_304;
        long jobId;
        await using (var owner = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(owner, tenant);
            var job = Job(tenant, 'd');
            owner.Add(job);
            await owner.SaveChangesAsync();
            jobId = job.Id;
        }

        await using var firstContext = database.TenantContextWithRls(tenant);
        await using var secondContext = database.TenantContextWithRls(tenant);
        var command = new RecoverExtractionDeadLetterCommand("Concurrent operator retry.", "concurrent-recovery");
        var results = await Task.WhenAll(
            Service(firstContext).RecoverAsync(tenant, jobId, "first-operator", command, default),
            Service(secondContext).RecoverAsync(tenant, jobId, "second-operator", command, default));

        Assert.All(results, result => Assert.Equal("RetryQueued", result.Status));
        Assert.Single(results, result => result.IdempotentReplay);
        Assert.Single(results, result => !result.IdempotentReplay);
        await using var verify = database.ContextFor(null);
        Assert.Equal(1, await verify.ExtractionDeadLetterEvents.CountAsync(x =>
            x.BusinessUnitId == tenant && x.ExtractionJobId == jobId));
        Assert.Equal(ExtractionStatus.Pending,
            (await verify.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId)).Status);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task EventLedger_IsTenantIsolatedAppendOnlyAndLeastPrivilege()
    {
        const long tenantA = 98_301;
        const long tenantB = 98_302;
        long jobA;
        long jobB;
        await using (var db = database.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(db, tenantA);
            Seed.EnsureBusinessUnit(db, tenantB);
            var first = Job(tenantA, 'a');
            var second = Job(tenantB, 'b');
            db.AddRange(first, second);
            await db.SaveChangesAsync();
            jobA = first.Id;
            jobB = second.Id;
        }

        await using var connection = await database.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE nexora_tenant_app");
        await ExecuteAsync(connection, transaction, $"SET LOCAL nexora.business_unit_id = '{tenantA}'");
        await InsertAsync(connection, transaction, tenantA, jobA, "tenant-a-recovery");

        await transaction.SaveAsync("before_cross_tenant");
        var denied = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(connection, transaction, tenantB, jobB, "tenant-b-recovery"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await transaction.RollbackAsync("before_cross_tenant");

        await using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT count(*) FROM public.extraction_dead_letter_events";
            Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
        }
        await transaction.RollbackAsync();

        await using (var privileges = connection.CreateCommand())
        {
            privileges.CommandText = """
                SELECT has_table_privilege('nexora_tenant_app', 'public.extraction_dead_letter_events', 'SELECT'),
                       has_table_privilege('nexora_tenant_app', 'public.extraction_dead_letter_events', 'INSERT'),
                       has_table_privilege('nexora_tenant_app', 'public.extraction_dead_letter_events', 'UPDATE'),
                       has_table_privilege('nexora_tenant_app', 'public.extraction_dead_letter_events', 'DELETE'),
                       has_sequence_privilege('nexora_tenant_app', pg_get_serial_sequence(
                           'public.extraction_dead_letter_events', 'Id'), 'USAGE');
                """;
            await using var reader = await privileges.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetBoolean(0));
            Assert.True(reader.GetBoolean(1));
            Assert.False(reader.GetBoolean(2));
            Assert.False(reader.GetBoolean(3));
            Assert.True(reader.GetBoolean(4));
        }

        await using var owner = database.ContextFor(null);
        owner.ExtractionDeadLetterEvents.Add(new ExtractionDeadLetterEvent
        {
            BusinessUnitId = tenantA,
            ExtractionJobId = jobA,
            AttemptNumber = 3,
            Action = ExtractionDeadLetterAction.SourceObjectUnavailable,
            Reason = "Owner append-only verification.",
            ActorId = "postgres-test",
            IdempotencyKey = "owner-append-test",
            CreatedOn = DateTimeOffset.UtcNow,
        });
        await owner.SaveChangesAsync();
        var persisted = await owner.ExtractionDeadLetterEvents.SingleAsync(x => x.IdempotencyKey == "owner-append-test");
        persisted.Reason = "mutation must fail";
        var appendOnly = await Assert.ThrowsAsync<InvalidOperationException>(() => owner.SaveChangesAsync());
        var databaseError = Assert.IsType<DbUpdateException>(appendOnly.InnerException);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            Assert.IsType<PostgresException>(databaseError.InnerException).SqlState);

        await using var latest = database.ContextFor(null);
        latest.ExtractionDeadLetterEvents.Add(new ExtractionDeadLetterEvent
        {
            BusinessUnitId = tenantA,
            ExtractionJobId = jobA,
            AttemptNumber = 3,
            Action = ExtractionDeadLetterAction.EvidenceIntegrityFailure,
            Reason = "Later integrity verification must restore the blocker.",
            ActorId = "postgres-test",
            IdempotencyKey = "owner-latest-integrity-test",
            CreatedOn = DateTimeOffset.UtcNow,
        });
        await latest.SaveChangesAsync();
        var unresolved = await latest.Set<ExtractionJob>().AsNoTracking().CountAsync(job =>
            job.BusinessUnitId == tenantA
            && job.Status == ExtractionStatus.DeadLetter
            && latest.ExtractionDeadLetterEvents
                .Where(disposition => disposition.BusinessUnitId == tenantA
                    && disposition.ExtractionJobId == job.Id
                    && disposition.AttemptNumber == job.Attempts)
                .OrderByDescending(disposition => disposition.Id)
                .Select(disposition => (ExtractionDeadLetterAction?)disposition.Action)
                .FirstOrDefault() != ExtractionDeadLetterAction.SourceObjectUnavailable);
        Assert.Equal(1, unresolved);
    }

    private static ExtractionJob Job(long tenantId, char hashCharacter)
    {
        var now = DateTime.UtcNow;
        return new ExtractionJob
        {
            BatchId = Guid.NewGuid(),
            BusinessUnitId = tenantId,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = new string(hashCharacter, 64),
            StoragePath = $"evidence://tenant/{tenantId}/source",
            FileName = "tenant-rfq.csv",
            FileType = "csv",
            Status = ExtractionStatus.DeadLetter,
            Attempts = 3,
            MaxAttempts = 3,
            NextAttemptAt = now,
            CreatedOn = now,
            UpdatedOn = now,
        };
    }

    private static ExtractionDeadLetterService Service(ERP_RFQ_Automation.Models.ErpRfqAutomationContext db) =>
        new(db, new AvailableStorage(), new CleanScanner());

    private sealed class AvailableStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceObject(
                $"s3://test-evidence/{zone}/{sha256}{extension}", "test-evidence",
                $"{zone}/{sha256}{extension}", sha256, sha256, content.Length));
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream("clean source"u8.ToArray(), writable: false));
    }

    private sealed class CleanScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(
            Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(MalwareScanResult.Clean("postgres-test", "test-signatures"));
    }

    private static async Task InsertAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        long tenantId, long jobId, string idempotencyKey)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO public.extraction_dead_letter_events
                ("BusinessUnitId", "ExtractionJobId", "AttemptNumber", "Action", "Reason",
                 "ActorId", "IdempotencyKey", "CreatedOn")
            VALUES (@tenant, @job, 3, 'RetryQueued', 'PostgreSQL RLS verification.',
                    'postgres-test', @idempotency, now());
            """;
        command.Parameters.AddWithValue("tenant", tenantId);
        command.Parameters.AddWithValue("job", jobId);
        command.Parameters.AddWithValue("idempotency", idempotencyKey);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
