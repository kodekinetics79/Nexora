using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Retention;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The tenant decides what to keep. These tests hold that line from both directions: what
/// produced NOTHING really is selectable and really loses its bytes, and everything with a
/// downstream artefact — or that we cannot positively prove is unused — is untouchable.
///
/// <para>The two that matter most are the floor waiver (without it the feature ships and deletes
/// nothing, because no production document is 30 days old) and the sweep's conservative refusal
/// (without it a sweep that misunderstands a key destroys evidence and reports success).</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class TenantDataControlTests(PostgreSqlTestDatabase database)
{
    // ------------------------------------------------------------------ the floor waiver

    /// <summary>
    /// THE test this feature exists for.
    ///
    /// <para>The age policy's floor is 30 days and its minimum is enforced by a database check
    /// constraint, so an age-gated cleanup cannot reach a two-day-old message however it is
    /// configured. A message that produced no inquiry and no lead has no downstream artefact to
    /// protect, so the floor's rationale — "a tenant must not destroy documents he may still
    /// need" — does not reach it, and it is waived. Both halves are asserted together: the
    /// message IS cleared, and the age policy still refuses the same window.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_message_that_produced_nothing_is_cleared_far_inside_the_thirty_day_floor()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, "no-outcome",
                ageDays: 2, triage: "Uncertain");

            Assert.True(2 < EvidenceRetentionPolicy.MinimumRetentionDays,
                "The fixture must sit inside the floor or it proves nothing.");
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)));

            var service = NewService(db, files);
            var view = await service.GetAsync(tenantId, default);
            var bucket = Bucket(view, TenantDataBuckets.MailThatProducedNothing);
            Assert.Equal(1, bucket.Count);
            Assert.True(bucket.Bytes > 0);
            Assert.True(bucket.CanClear);

            var result = await service.RunCleanupAsync(tenantId, 9, "clear-1",
                Clear(TenantDataBuckets.MailThatProducedNothing), default);

            Assert.Equal(1, result.MessagesCleared);
            Assert.True(result.BytesReclaimed > 0);
            Assert.False(File.Exists(files.ResolvePath(message.RawKey)));

            // The tombstone shape: the row survives, complete with who, when and why.
            db.ChangeTracker.Clear();
            var stored = await db.EmailIngests.AsNoTracking().SingleAsync(x => x.Id == message.Id);
            Assert.NotNull(stored.BytesPurgedOn);
            Assert.Equal(9, stored.PurgedByUserId);
            Assert.False(string.IsNullOrWhiteSpace(stored.PurgeReason));
            Assert.False(stored.RawMessageAvailable);
            // The identity that makes the row an answer rather than a gap.
            Assert.Equal(message.MessageId, stored.MessageId);
            Assert.Equal("no-outcome subject", stored.EmailSubject);
            Assert.Equal("sender@example.test", stored.FromEmail);
            // The pointer goes with the bytes: a row promising a file that is gone is the one
            // state nothing downstream could repair.
            Assert.Null(stored.RawEmailPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The control that proves the waiver is a real change and not a suite that is always green:
    /// the AGE policy still refuses the identical window, so the floor is waived only where the
    /// rationale does not reach.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_age_policy_floor_is_untouched_and_still_refuses_a_short_window()
    {
        var tenantId = NewTenantId();
        await using var db = database.ContextFor(null);
        await SeedTenantAsync(db, tenantId);

        var retention = new EvidenceRetentionService(db,
            new LocalEvidenceObjectStorage(new LocalFileStorage(Path.GetTempPath(), Path.GetTempPath())),
            new LegacyAttachmentPurgeResolver(db,
                new LocalFileStorage(Path.GetTempPath(), Path.GetTempPath())),
            new CommercialDocumentArchiveService(db),
            new NoopLogger<EvidenceRetentionService>());

        var refused = await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
            retention.UpdatePolicyAsync(tenantId, 9, "floor-check",
                new UpdateEvidenceRetentionPolicyCommand(2, true, "Try to configure the floor away."),
                default));
        Assert.Contains("30", refused.Message);
    }

    /// <summary>
    /// What replaces the floor. Assembly is asynchronous, so a message that arrived minutes ago
    /// may simply not have produced its inquiry YET. The settle window is a race guard measured in
    /// hours, not a retention period — but it must actually hold, or the waiver becomes a way to
    /// delete a message the coordinator is still working on.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_message_that_has_only_just_arrived_is_left_alone()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, "just-arrived",
                ageHours: 1, triage: "Noise");

            var service = NewService(db, files);
            Assert.Equal(0, Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.MailThatProducedNothing).Count);

            var result = await service.RunCleanupAsync(tenantId, 9, "clear-fresh",
                Clear(TenantDataBuckets.MailThatProducedNothing, TenantDataBuckets.MailTriagedAsNoise), default);
            Assert.Equal(0, result.MessagesCleared);
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)));

            db.ChangeTracker.Clear();
            Assert.Null((await db.EmailIngests.AsNoTracking()
                .SingleAsync(x => x.Id == message.Id)).BytesPurgedOn);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ "produced nothing" really means it

    public static TheoryData<string> ProducedSomethingCases() => ["assembly", "lead"];

    /// <summary>
    /// A message that produced an inquiry or a lead is load-bearing, and either one alone is
    /// enough to protect it. This is also why the tombstone shape exists at all: the assembly's
    /// foreign key is RESTRICT, so the row is undeletable anyway.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProducedSomethingCases))]
    [Trait("Category", "PostgreSQL")]
    public async Task A_message_that_produced_something_is_never_selected(string produced)
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, produced,
                ageDays: 400, triage: "Noise", produced: produced);

            var service = NewService(db, files);
            var view = await service.GetAsync(tenantId, default);
            Assert.Equal(0, Bucket(view, TenantDataBuckets.MailThatProducedNothing).Count);
            Assert.Equal(0, Bucket(view, TenantDataBuckets.MailTriagedAsNoise).Count);

            var result = await service.RunCleanupAsync(tenantId, 9, $"clear-{produced}",
                Clear(TenantDataBuckets.MailThatProducedNothing, TenantDataBuckets.MailTriagedAsNoise), default);
            Assert.Equal(0, result.MessagesCleared);
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)),
                $"{produced}: the stored message must survive.");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The noise row is a SUBSET of the row above it, so ticking both must clear the union once —
    /// never twice, and never a sum a human cannot go and count.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Noise_is_a_subset_and_ticking_both_rows_clears_the_union_once()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            await SeedMessageAsync(db, tenantId, files, "noise-a", ageDays: 3, triage: "Noise");
            await SeedMessageAsync(db, tenantId, files, "noise-b", ageDays: 3, triage: "Noise");
            await SeedMessageAsync(db, tenantId, files, "unsure", ageDays: 3, triage: "Uncertain");

            var service = NewService(db, files);
            var view = await service.GetAsync(tenantId, default);
            Assert.Equal(3, Bucket(view, TenantDataBuckets.MailThatProducedNothing).Count);
            Assert.Equal(2, Bucket(view, TenantDataBuckets.MailTriagedAsNoise).Count);

            var both = await service.RunCleanupAsync(tenantId, 9, "clear-both",
                Preview(TenantDataBuckets.MailThatProducedNothing, TenantDataBuckets.MailTriagedAsNoise), default);
            Assert.Equal(3, both.MessagesCleared);

            db.ChangeTracker.Clear();
            var noiseOnly = await service.RunCleanupAsync(tenantId, 9, "clear-noise",
                Clear(TenantDataBuckets.MailTriagedAsNoise), default);
            Assert.Equal(2, noiseOnly.MessagesCleared);

            // Scoped to THIS tenant's mailbox: the integration database is shared, so an
            // unscoped count reads every other test's rows and passes or fails by accident.
            db.ChangeTracker.Clear();
            var mailboxIds = db.EmailConfigurations.AsNoTracking()
                .Where(x => x.BusinessUnitId == tenantId).Select(x => x.Id);
            Assert.Equal(1, await db.EmailIngests.AsNoTracking()
                .CountAsync(x => mailboxIds.Contains(x.EmailConfigurationId)
                    && x.BytesPurgedOn == null));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static TheoryData<string> InFlightCases() => ["Pending", "Reprocessing"];

    /// <summary>
    /// A message still in flight keeps its stored copy, whatever its outcome looks like today.
    ///
    /// <para>This is not caution for its own sake — it is the poison-message guard. A message
    /// left at <c>Pending</c> is one the poller's stranded-ingest sweeper comes back for, and it
    /// recovers by re-reading the retained raw <c>.eml</c>. Clear one and the sweeper finds
    /// nothing and stamps the row <i>"Failed - raw message lost"</i>: the tenant is told we lost
    /// his mail when in fact he asked us to delete it. A loud FALSE answer is no better than a
    /// silent one.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(InFlightCases))]
    [Trait("Category", "PostgreSQL")]
    public async Task A_message_still_in_flight_keeps_its_stored_copy(string parseStatus)
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, "in-flight",
                ageDays: 400, triage: "Noise", parseStatus: parseStatus);

            var service = NewService(db, files);
            Assert.Equal(0, Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.MailThatProducedNothing).Count);

            var result = await service.RunCleanupAsync(tenantId, 9, $"clear-{parseStatus}",
                Clear(TenantDataBuckets.MailThatProducedNothing, TenantDataBuckets.MailTriagedAsNoise), default);
            Assert.Equal(0, result.MessagesCleared);
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)),
                $"{parseStatus}: the stored message must survive so recovery can still read it.");

            db.ChangeTracker.Clear();
            var stored = await db.EmailIngests.AsNoTracking().SingleAsync(x => x.Id == message.Id);
            Assert.Null(stored.BytesPurgedOn);
            Assert.Equal(message.RawKey, stored.RawEmailPath);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ the orphan sweep

    /// <summary>
    /// The highest-yield change: 62% of stored objects have no database pointer at all, so no
    /// row-driven purge can ever reach them however long it runs.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_object_no_row_points_at_is_swept_and_the_bytes_come_back()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var orphan = WriteObject(files, tenantId, "quarantine", ".pdf", "abandoned bytes");

            var service = NewService(db, files);
            var bucket = Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.OrphanedStoredFiles);
            Assert.Equal(1, bucket.Count);
            Assert.True(bucket.Bytes > 0);

            var result = await service.RunCleanupAsync(tenantId, 9, "sweep-1",
                Clear(TenantDataBuckets.OrphanedStoredFiles), default);

            Assert.Equal(1, result.FilesDeleted);
            Assert.True(result.BytesReclaimed > 0);
            Assert.False(File.Exists(files.ResolvePath(orphan)));

            var swept = await db.TenantGovernanceAuditEvents.AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId
                    && x.Action == TenantDataControlService.ActionOrphanSwept);
            using var evidence = JsonDocument.Parse(swept.EvidenceJson);
            Assert.Equal(orphan, evidence.RootElement.GetProperty("key").GetString());
            // The tombstone states the proof, not just the outcome.
            Assert.Equal(4, evidence.RootElement.GetProperty("provedUnreferencedBy")
                .EnumerateArray().Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    public static TheoryData<string> ProtectedObjectCases() =>
        ["exact-key", "zone-sibling", "same-hash-different-extension", "raw-mail-of-an-assembly",
         "unrecognised-name", "another-tenants-prefix"];

    /// <summary>
    /// The conservative refusal, one net per case. Anything the sweep cannot POSITIVELY prove is
    /// unreferenced must be kept — and named, because a sweep that quietly skips what it does not
    /// understand is indistinguishable from one that had nothing to do.
    ///
    /// <para><c>same-hash-different-extension</c> is the subtle one and the reason hash matching
    /// exists at all: exact-key matching alone would call it an orphan and destroy live
    /// evidence.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ProtectedObjectCases))]
    [Trait("Category", "PostgreSQL")]
    public async Task Anything_it_cannot_prove_unused_is_kept_and_reported(string protection)
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var (key, expectRefusalReported) = await SeedProtectedObjectAsync(
                db, tenantId, files, protection);

            var service = NewService(db, files);
            Assert.Equal(0, Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.OrphanedStoredFiles).Count);

            var result = await service.RunCleanupAsync(tenantId, 9, $"sweep-{protection}",
                Clear(TenantDataBuckets.OrphanedStoredFiles), default);

            Assert.Equal(0, result.FilesDeleted);
            Assert.Equal(0, result.BytesReclaimed);
            Assert.True(File.Exists(files.ResolvePath(key)), $"{protection}: the object must survive.");

            // A key we could not understand is REPORTED, not silently skipped: the tenant is
            // still paying for those bytes and is owed the fact.
            if (expectRefusalReported)
                Assert.Contains(result.Refused, x => x.Why!.Length > 0);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// A provider that cannot enumerate must refuse the sweep, never report a clean store. "We
    /// could not look" and "there is nothing there" are different answers, and collapsing them is
    /// the silent-wrong-answer failure this whole subsystem is built to avoid.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Storage_that_cannot_be_listed_refuses_the_sweep_instead_of_reporting_it_clean()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            WriteObject(files, tenantId, "quarantine", ".pdf", "would have been swept");

            var service = NewService(db, files, new CannotListEvidenceStorage(
                new LocalEvidenceObjectStorage(files)));

            var bucket = Bucket(await service.GetAsync(tenantId, default),
                TenantDataBuckets.OrphanedStoredFiles);
            Assert.Equal(0, bucket.Count);
            Assert.False(bucket.CanClear);
            Assert.Equal(TenantDataControlCopy.StorageCannotList, bucket.BlockedReason);

            var result = await service.RunCleanupAsync(tenantId, 9, "sweep-blind",
                Clear(TenantDataBuckets.OrphanedStoredFiles), default);
            Assert.Equal(0, result.FilesDeleted);
            Assert.Contains(result.Refused, x => x.Why == TenantDataControlCopy.StorageCannotList);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ the gate

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_absent_dry_run_flag_never_deletes()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, "default-safe",
                ageDays: 5, triage: "Noise");

            var result = await NewService(db, files).RunCleanupAsync(tenantId, 9, "clear-null",
                new TenantDataCleanupCommand([TenantDataBuckets.MailThatProducedNothing], null,
                    "Body omitted the flag.", null), default);

            Assert.True(result.DryRun);
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)));
            db.ChangeTracker.Clear();
            Assert.Null((await db.EmailIngests.AsNoTracking()
                .SingleAsync(x => x.Id == message.Id)).BytesPurgedOn);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The confirmation phrase is verified on the SERVER. A phrase checked only in the browser is
    /// a decoration on a request anyone can send directly.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_confirmation_phrase_is_checked_on_the_server()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, "unconfirmed",
                ageDays: 5, triage: "Noise");
            var service = NewService(db, files);

            await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
                service.RunCleanupAsync(tenantId, 9, "clear-nophrase",
                    new TenantDataCleanupCommand([TenantDataBuckets.MailThatProducedNothing],
                        false, "No phrase supplied.", null), default));
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)));

            db.ChangeTracker.Clear();
            await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
                service.RunCleanupAsync(tenantId, 9, "clear-wrongphrase",
                    new TenantDataCleanupCommand([TenantDataBuckets.MailThatProducedNothing],
                        false, "Wrong phrase.", "delete"), default));
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)));

            // A written reason is required too.
            db.ChangeTracker.Clear();
            await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
                service.RunCleanupAsync(tenantId, 9, "clear-noreason",
                    new TenantDataCleanupCommand([TenantDataBuckets.MailThatProducedNothing],
                        false, "   ", TenantDataControlCopy.ConfirmationPhrase), default));
            Assert.True(File.Exists(files.ResolvePath(message.RawKey)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_replayed_key_returns_the_first_answer_rather_than_deleting_twice()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            await SeedMessageAsync(db, tenantId, files, "replayed", ageDays: 5, triage: "Noise");
            var service = NewService(db, files);

            var first = await service.RunCleanupAsync(tenantId, 9, "clear-once",
                Clear(TenantDataBuckets.MailThatProducedNothing), default);
            Assert.Equal(1, first.MessagesCleared);
            Assert.False(first.IdempotentReplay);

            db.ChangeTracker.Clear();
            var replay = await service.RunCleanupAsync(tenantId, 9, "clear-once",
                Clear(TenantDataBuckets.MailThatProducedNothing), default);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(first.MessagesCleared, replay.MessagesCleared);
            Assert.Equal(first.BytesReclaimed, replay.BytesReclaimed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task One_tenant_cannot_clear_another_tenants_mail_or_files()
    {
        var tenantA = NewTenantId();
        var tenantB = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantA);
            await SeedTenantAsync(db, tenantB);
            var files = new LocalFileStorage(root, root);
            var messageB = await SeedMessageAsync(db, tenantB, files, "tenant-b",
                ageDays: 400, triage: "Noise");
            var orphanB = WriteObject(files, tenantB, "quarantine", ".pdf", "tenant b bytes");

            var service = NewService(db, files);
            var view = await service.GetAsync(tenantA, default);
            Assert.Equal(0, Bucket(view, TenantDataBuckets.MailThatProducedNothing).Count);
            Assert.Equal(0, Bucket(view, TenantDataBuckets.OrphanedStoredFiles).Count);

            var result = await service.RunCleanupAsync(tenantA, 9, "clear-cross",
                Clear(TenantDataBuckets.MailThatProducedNothing, TenantDataBuckets.OrphanedStoredFiles), default);
            Assert.Equal(0, result.MessagesCleared);
            Assert.Equal(0, result.FilesDeleted);
            Assert.True(File.Exists(files.ResolvePath(messageB.RawKey)));
            Assert.True(File.Exists(files.ResolvePath(orphanB)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The tombstone is enforced by the DATABASE, not by the service that writes it — the same
    /// property that makes the <c>source_documents</c> tombstone worth more than the file it
    /// replaces. Three refusals, each proving one clause of the migration.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_message_tombstone_is_enforced_by_the_database()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var message = await SeedMessageAsync(db, tenantId, files, "enforced",
                ageDays: 5, triage: "Noise");

            // 1. A HALF tombstone is refused. A timestamp with no author, or an author with no
            //    reason, reads as a record while answering neither "who" nor "why".
            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"""UPDATE public."EmailIngests" SET "bytes_purged_on" = now() WHERE "ID" = {message.Id};""");
            });

            db.ChangeTracker.Clear();
            await NewService(db, files).RunCleanupAsync(tenantId, 9, "clear-enforced",
                Clear(TenantDataBuckets.MailThatProducedNothing), default);

            // 2. A recorded purge cannot be un-stamped or rewritten.
            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"""UPDATE public."EmailIngests" SET "bytes_purged_on" = NULL, "purged_by_user_id" = NULL, "purge_reason" = NULL WHERE "ID" = {message.Id};""");
            });

            // 3. A purged message cannot regain a stored copy — a row pointing at bytes that no
            //    longer exist is the one state nothing downstream could repair.
            await Assert.ThrowsAnyAsync<Npgsql.PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlRawAsync(
                    $"""UPDATE public."EmailIngests" SET "RawEmailPath" = 'Evidence/anywhere.eml' WHERE "ID" = {message.Id};""");
            });

            // And the row is still there, still readable, still answering for itself.
            db.ChangeTracker.Clear();
            var stored = await db.EmailIngests.AsNoTracking().SingleAsync(x => x.Id == message.Id);
            Assert.NotNull(stored.BytesPurgedOn);
            Assert.Equal(message.MessageId, stored.MessageId);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ kept, and why

    /// <summary>
    /// The reassurance panel, and the two lines the DATABASE enforces that no screen has ever
    /// mentioned: a document behind an issued invoice, and one behind a payment already posted to
    /// the books. Both are physically undeletable; a tenant learning that from an error message
    /// rather than from this panel is a support call we caused.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_kept_panel_counts_the_reasons_including_the_two_the_database_enforces()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            await SeedFinanceProtectedDocumentAsync(db, tenantId, files);

            var view = await NewService(db, files).GetAsync(tenantId, default);

            var invoice = Kept(view, "Invoices you have already issued");
            var posted = Kept(view, "Anything already posted to your accounts");
            Assert.Equal(1, invoice.Count);
            Assert.Equal(1, posted.Count);

            // The statutory line is always present, whether or not it has a count today.
            Assert.Contains(view.Kept, x => x.Title.Contains("purchase orders"));
            Assert.Contains(view.Kept, x => x.Title.Contains("legal hold"));
            Assert.Contains("protected", view.KeptSummary);

            // Not one internal name reaches the panel.
            foreach (var line in view.Kept)
            {
                Assert.DoesNotContain("source_document", line.Title + line.Detail);
                Assert.DoesNotContain("Skip.", line.Title + line.Detail);
                Assert.DoesNotContain("_", line.Title);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The bucket copy is finished product text, not codes for a client to decorate. Whatever the
    /// screen renders must be readable by someone who has never heard the word "assembly".
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task No_internal_vocabulary_reaches_the_screen()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            var view = await NewService(db, files).GetAsync(tenantId, default);

            Assert.Equal(3, view.Buckets.Count);
            foreach (var bucket in view.Buckets)
            {
                var human = bucket.Title + " " + bucket.Detail + " " + (bucket.BlockedReason ?? "");
                Assert.False(string.IsNullOrWhiteSpace(bucket.Title));
                Assert.False(string.IsNullOrWhiteSpace(bucket.Detail));
                foreach (var jargon in new[]
                         {
                             "assembly", "EmailIngest", "SourceDocument", "source_document",
                             "occurrence", "enum", "MAIL_", "ORPHANED_", "_STORED_", "triage",
                             "quarantine zone", "purge_state"
                         })
                    Assert.DoesNotContain(jargon, human, StringComparison.OrdinalIgnoreCase);

                // The code is carried for the request and never doubles as copy.
                Assert.DoesNotContain(bucket.Code, human, StringComparison.Ordinal);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A run records what it KEPT, not only what it destroyed.</summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_cleanup_run_records_what_was_kept_and_why()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedTenantAsync(db, tenantId);
            var files = new LocalFileStorage(root, root);
            await SeedFinanceProtectedDocumentAsync(db, tenantId, files);
            await SeedMessageAsync(db, tenantId, files, "cleared-one", ageDays: 5, triage: "Noise");

            await NewService(db, files).RunCleanupAsync(tenantId, 9, "clear-audit",
                Clear(TenantDataBuckets.MailThatProducedNothing), default);

            var run = await db.TenantGovernanceAuditEvents.AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId
                    && x.Action == TenantDataControlService.ActionCleanupRun);
            using var evidence = JsonDocument.Parse(run.EvidenceJson);
            var kept = evidence.RootElement.GetProperty("kept").EnumerateArray()
                .ToDictionary(x => x.GetProperty("reason").GetString()!,
                    x => x.GetProperty("count").GetInt32());
            Assert.Contains(kept, entry => entry.Key.Contains("issued to a customer") && entry.Value == 1);
            Assert.Contains(kept, entry => entry.Key.Contains("posted to your accounts") && entry.Value == 1);
            Assert.Equal(TenantDataBuckets.MailThatProducedNothing,
                evidence.RootElement.GetProperty("buckets").EnumerateArray().Single().GetString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>A disclosure may not point at a process that does not exist.</summary>
    [Fact]
    public void The_disclosure_no_longer_directs_anyone_at_a_process_that_does_not_exist()
    {
        Assert.DoesNotContain("Data Subject Request", EvidenceRetentionDisclosure.NotErasure);
        Assert.DoesNotContain("Data Subject Request", TenantDataControlCopy.NotErasure);
        Assert.Contains("does not erase personal data", EvidenceRetentionDisclosure.NotErasure);
        // It still has to say what CAN be done, or it is only half a disclosure.
        Assert.Contains("lead", EvidenceRetentionDisclosure.NotErasure);
    }

    // ------------------------------------------------------------------ helpers

    private sealed record SeededMessage(long Id, string MessageId, string RawKey);

    private static TenantDataCleanupCommand Clear(params string[] buckets) =>
        new(buckets, false, "Clearing what produced nothing.", TenantDataControlCopy.ConfirmationPhrase);

    private static TenantDataCleanupCommand Preview(params string[] buckets) =>
        new(buckets, true, "Preview.", null);

    private static TenantDataBucketView Bucket(TenantDataControlView view, string code) =>
        view.Buckets.Single(x => x.Code == code);

    private static TenantDataKeptView Kept(TenantDataControlView view, string startsWith) =>
        view.Kept.Single(x => x.Title.StartsWith(startsWith, StringComparison.Ordinal));

    private static TenantDataControlService NewService(ErpRfqAutomationContext db,
        IFileStorage files, IEvidenceObjectStorage? evidenceStorage = null) =>
        new(db, evidenceStorage ?? new LocalEvidenceObjectStorage(files), files,
            new CommercialDocumentArchiveService(db),
            new NoopLogger<TenantDataControlService>());

    /// <summary>A provider that can do everything except enumerate — the shape a sweep must
    /// refuse rather than misread as an empty store.</summary>
    private sealed class CannotListEvidenceStorage(IEvidenceObjectStorage inner) : IEvidenceObjectStorage
    {
        public bool IsDurable => inner.IsDurable;
        public Task ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);

        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone,
            string sha256, string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            inner.WriteImmutableAsync(businessUnitId, zone, sha256, extension, content, ct);

        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => inner.OpenVerifiedReadAsync(storageUri, expectedSha256, ct);

        public Task<EvidenceObjectPurgeResult> TryDeletePurgedObjectAsync(string bucket, string key,
            string version, CancellationToken ct = default) =>
            inner.TryDeletePurgedObjectAsync(bucket, key, version, ct);

        public Task<long?> TryMeasureObjectAsync(string bucket, string key, string version,
            CancellationToken ct = default) => inner.TryMeasureObjectAsync(bucket, key, version, ct);

        public Task<IReadOnlyList<StoredEvidenceObject>> ListObjectsUnderPrefixAsync(
            string keyPrefix, CancellationToken ct = default) =>
            throw new NotSupportedException("This provider cannot list stored evidence objects.");
    }

    private static async Task SeedTenantAsync(ErpRfqAutomationContext db, long tenantId)
    {
        Seed.EnsureBusinessUnit(db, tenantId);
        await db.SaveChangesAsync();
        if (!await db.Set<Tenant>().IgnoreQueryFilters()
                .AnyAsync(t => t.PrimaryBusinessUnitId == tenantId))
        {
            db.Set<Tenant>().Add(new Tenant
            {
                Name = $"Data control tenant {tenantId}",
                Slug = $"data-control-{tenantId}",
                Status = TenantStatus.Active,
                PrimaryBusinessUnitId = tenantId,
                CreatedBy = "tenant-data-test",
                CreatedOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        if (!await db.EmailConfigurations.AnyAsync(x => x.BusinessUnitId == tenantId))
        {
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
        }
    }

    /// <summary>
    /// One ingested message with a raw <c>.eml</c> on disk, optionally having produced an
    /// assembly or a lead. Written through the real evidence writer so the stored key has exactly
    /// the shape production produces — a fixture on a shape the product never emits proves
    /// nothing.
    /// </summary>
    private static async Task<SeededMessage> SeedMessageAsync(ErpRfqAutomationContext db,
        long tenantId, IFileStorage files, string label, string triage,
        int ageDays = 0, int ageHours = 0, string? produced = null,
        string parseStatus = "Success")
    {
        var config = await db.EmailConfigurations.SingleAsync(x => x.BusinessUnitId == tenantId);
        var arrivedOn = DateTime.UtcNow.AddDays(-ageDays).AddHours(-ageHours);
        var bytes = Encoding.UTF8.GetBytes($"From: sender@example.test\r\nSubject: {label}\r\n\r\n{Guid.NewGuid():N}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var evidence = new LocalEvidenceObjectStorage(files);
        var raw = await evidence.WriteImmutableAsync(tenantId, "raw-mail", hash, ".eml", bytes);

        var ingest = new EmailIngest
        {
            MessageId = $"{label}-{Guid.NewGuid():N}@example.test",
            EmailSubject = $"{label} subject",
            FromEmail = "sender@example.test",
            ToEmail = config.EmailAddress,
            EmailConfigurationId = config.Id,
            CreatedOn = arrivedOn,
            ParseStatus = parseStatus,
            RawEmailPath = raw.Key,
            TriageOutcome = triage,
            TriageDecidedOn = arrivedOn
        };
        db.EmailIngests.Add(ingest);
        await db.SaveChangesAsync();

        if (produced == "assembly")
        {
            db.EmailInquiryAssemblies.Add(new EmailInquiryAssembly
            {
                BusinessUnitId = tenantId,
                EmailIngestId = ingest.Id,
                EmailConfigurationId = config.Id,
                MessageKey = ingest.MessageId,
                RawEvidenceUri = raw.StorageUri,
                RawEvidenceSha256 = hash,
                ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
                ExpectedComponentCount = 1,
                Status = EmailInquiryAssemblyStatus.Captured,
                CreatedAtUtc = arrivedOn,
                UpdatedAtUtc = arrivedOn
            });
            await db.SaveChangesAsync();
        }

        if (produced == "lead")
        {
            var lead = new Lead
            {
                BusinessUnitId = tenantId,
                Rfqno = "RFQ-" + Guid.NewGuid().ToString("N")[..8],
                BuyersName = "Ahmed K",
                Clientemail = "ahmed.k@example.com",
                RecDate = arrivedOn,
                LeadSource = "Email",
                CreatedBy = "tenant-data-test",
                CreatedDate = arrivedOn,
                EmailIngestsId = ingest.Id
            };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
        }

        db.ChangeTracker.Clear();
        return new SeededMessage(ingest.Id, ingest.MessageId, raw.Key);
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

    /// <summary>Places one stored object that the sweep must refuse to touch, one protection at
    /// a time, and reports whether that protection should also produce a printed refusal.</summary>
    private static async Task<(string Key, bool RefusalReported)> SeedProtectedObjectAsync(
        ErpRfqAutomationContext db, long tenantId, IFileStorage files, string protection)
    {
        var bytes = Encoding.UTF8.GetBytes("protected evidence " + Guid.NewGuid().ToString("N"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var evidence = new LocalEvidenceObjectStorage(files);

        switch (protection)
        {
            case "exact-key":
            {
                var cleared = await evidence.WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);
                await AddDocumentAsync(db, tenantId, hash, cleared);
                return (cleared.Key, false);
            }
            case "zone-sibling":
            {
                // The row records the CLEARED key; the object on disk is its quarantine twin.
                // Ingestion writes both and nothing has ever deleted either.
                var cleared = await evidence.WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);
                var quarantine = await evidence.WriteImmutableAsync(tenantId, "quarantine", hash, ".pdf", bytes);
                await AddDocumentAsync(db, tenantId, hash, cleared);
                File.Delete(files.ResolvePath(cleared.Key));
                return (quarantine.Key, false);
            }
            case "same-hash-different-extension":
            {
                // The subtle one. Exact-key matching alone calls this an orphan and destroys live
                // evidence; hash matching is what saves it.
                var recorded = await evidence.WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);
                await AddDocumentAsync(db, tenantId, hash, recorded);
                File.Delete(files.ResolvePath(recorded.Key));
                var onDisk = await evidence.WriteImmutableAsync(tenantId, "cleared", hash, ".eml", bytes);
                return (onDisk.Key, false);
            }
            case "raw-mail-of-an-assembly":
            {
                // ZoneKeysFor deliberately cannot address the raw-mail zone, so without the
                // assembly's own URI every stored message would look like an orphan. This is the
                // single most dangerous omission available in the sweep.
                var raw = await evidence.WriteImmutableAsync(tenantId, "raw-mail", hash, ".eml", bytes);
                var config = await db.EmailConfigurations.SingleAsync(x => x.BusinessUnitId == tenantId);
                var ingest = new EmailIngest
                {
                    MessageId = $"assembled-{Guid.NewGuid():N}@example.test",
                    EmailSubject = "assembled",
                    FromEmail = "sender@example.test",
                    EmailConfigurationId = config.Id,
                    CreatedOn = DateTime.UtcNow.AddDays(-400),
                    ParseStatus = "Parsed"
                };
                db.EmailIngests.Add(ingest);
                await db.SaveChangesAsync();
                db.EmailInquiryAssemblies.Add(new EmailInquiryAssembly
                {
                    BusinessUnitId = tenantId,
                    EmailIngestId = ingest.Id,
                    EmailConfigurationId = config.Id,
                    MessageKey = ingest.MessageId,
                    RawEvidenceUri = raw.StorageUri,
                    RawEvidenceSha256 = hash,
                    ManifestContractVersion = EmailInquiryManifestPlanner.ContractVersion,
                    ExpectedComponentCount = 1,
                    Status = EmailInquiryAssemblyStatus.Captured,
                    CreatedAtUtc = DateTime.UtcNow.AddDays(-400),
                    UpdatedAtUtc = DateTime.UtcNow.AddDays(-400)
                });
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                return (raw.Key, false);
            }
            case "unrecognised-name":
            {
                var key = $"Evidence/tenants/{tenantId}/cleared/legacy/not-content-addressed.pdf";
                var path = files.ResolvePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes);
                return (key, true);
            }
            case "another-tenants-prefix":
            {
                // A listing that returned a neighbour must never be swept under this tenant's
                // authority, whatever the prefix said.
                var neighbour = tenantId + 1;
                var key = LocalEvidenceObjectStorage.BuildKey(neighbour, "cleared", hash, ".pdf")
                    .Replace('\\', '/');
                var path = files.ResolvePath(key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes);
                return (key, false);
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(protection), protection, null);
        }
    }

    private static async Task AddDocumentAsync(ErpRfqAutomationContext db, long tenantId,
        string hash, EvidenceObject stored)
    {
        var corpus = ERP_RFQ_Automation.DocumentIntelligence.Persistence.DocumentCorpus.Create(tenantId, Guid.NewGuid(),
            ERP_RFQ_Automation.DocumentIntelligence.Persistence.CorpusSourceType.ManualUpload);
        db.Add(corpus);
        await db.SaveChangesAsync();
        db.Add(ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocument.Create(tenantId, corpus.Id, hash,
            "protected.pdf", "application/pdf", stored.Bucket, stored.Key, stored.Version,
            1024, DateTimeOffset.UtcNow.AddDays(-400)));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }

    /// <summary>
    /// A document on a commercial case that carries BOTH an issued invoice and a posted journal
    /// entry — the two protections the database enforces and the product has never said out loud.
    /// </summary>
    private static async Task SeedFinanceProtectedDocumentAsync(ErpRfqAutomationContext db,
        long tenantId, IFileStorage files)
    {
        var bytes = Encoding.UTF8.GetBytes("finance protected " + Guid.NewGuid().ToString("N"));
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var stored = await new LocalEvidenceObjectStorage(files)
            .WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);

        var corpus = ERP_RFQ_Automation.DocumentIntelligence.Persistence.DocumentCorpus.Create(tenantId, Guid.NewGuid(),
            ERP_RFQ_Automation.DocumentIntelligence.Persistence.CorpusSourceType.ManualUpload);
        db.Add(corpus);
        await db.SaveChangesAsync();
        var document = ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocument.Create(tenantId, corpus.Id,
            hash, "invoice-source.pdf", "application/pdf", stored.Bucket, stored.Key,
            stored.Version, bytes.LongLength, DateTimeOffset.UtcNow.AddDays(-400));
        db.Add(document);
        await db.SaveChangesAsync();

        var lead = new Lead
        {
            BusinessUnitId = tenantId,
            Rfqno = "RFQ-FIN-" + Guid.NewGuid().ToString("N")[..8],
            BuyersName = "Ahmed K",
            Clientemail = "ahmed.k@example.com",
            RecDate = DateTime.UtcNow.AddDays(-400),
            LeadSource = "ManualUpload",
            CreatedBy = "tenant-data-test",
            CreatedDate = DateTime.UtcNow.AddDays(-400)
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync();

        var batchId = Guid.NewGuid();
        db.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionBatch>().Add(new ERP_RFQ_Automation.LeadIdentity.LeadIngestionBatch
        {
            Id = batchId,
            BusinessUnitId = tenantId,
            SourceChannel = "ManualUpload",
            CreatedBy = "tenant-data-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        db.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>().Add(new ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence
        {
            BusinessUnitId = tenantId,
            BatchId = batchId,
            LeadId = lead.Id,
            SourceDocumentId = document.Id,
            SourceChannel = "ManualUpload",
            IdempotencyKey = "fin-" + Guid.NewGuid().ToString("N"),
            OriginalFileName = "invoice-source.pdf",
            ContentHash = hash,
            LogicalInquiryFingerprint = new string('a', 64),
            Classification = ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceClassification.New,
            Confidence = 0.99m,
            ProcessingPath = ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath.Deterministic,
            SourceReceivedAtUtc = DateTimeOffset.UtcNow.AddDays(-400),
            IngestedAtUtc = DateTimeOffset.UtcNow.AddDays(-400),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-400),
            ActorType = "Test",
            ActorId = "tenant-data-test",
            CorrelationId = "fin-" + Guid.NewGuid().ToString("N")[..8]
        });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var caseId = (await db.Leads.AsNoTracking().SingleAsync(x => x.Id == lead.Id)).CommercialCaseId;

        // Seeded in the FINAL shape rather than walked through the governed issue path.
        //
        // Reaching Status='Issued' legitimately needs an order, a quote, an eligible order
        // status and reconciling lines — a chain the finance suite already proves end to end.
        // What THIS test is about is whether the "kept, and why" panel reads the issued STATE
        // correctly, so the row is written exactly as production holds it (Status Issued,
        // IssuedOn set, Version 2) with the governing triggers stood down for the insert alone.
        // The shape is production's; only the path is short-circuited.
        // The connection is opened explicitly and held: SET is session state, and EF's pooling
        // would otherwise put the SET and the INSERT on different connections — the setting
        // would apply to a connection that never runs the statement it was for.
        await db.Database.OpenConnectionAsync();
        try
        {
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = replica;");
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO public."ReceivableDocuments"
                ("BusinessUnitId", "CommercialCaseId", "CustomerId", "DocumentType", "Status",
                 "DocumentNumber", "DocumentDate", "DueDate", "IssuedOn", "SubTotal",
                 "DiscountAmount", "TaxAmount", "TotalAmount", "IdempotencyKey", "RequestHash",
                 "Version", "CreatedBy", "CreatedOn", "IssuedBy")
            VALUES ({tenantId}, {caseId}, 1, '{ReceivableDocumentTypes.Invoice}',
                 '{ReceivableDocumentStatuses.Issued}', 'INV-TEST-{Guid.NewGuid():N}'::text,
                 now(), now() + interval '30 days', now(), 100, 0, 0, 100,
                 'fin-{Guid.NewGuid():N}', '{new string('d', 64)}', 2, 'tenant-data-test', now(),
                 'tenant-data-issuer');
            """);

        var periodId = await EnsureAccountingPeriodAsync(db, tenantId);
        var journalId = await db.Database.SqlQueryRaw<long>($"""
            INSERT INTO public."JournalEntries"
                ("BusinessUnitId", "AccountingPeriodId", "FunctionalCurrencyId", "EntryNumber",
                 "AccountingDate", "Status", "Description", "SourceType", "TotalDebit",
                 "TotalCredit", "IdempotencyKey", "RequestHash", "Version", "CreatedBy",
                 "CreatedOn", "PostedBy", "PostedOn")
            VALUES ({tenantId}, {periodId}, 1, 'JE-TEST-{Guid.NewGuid():N}'::text, now(),
                 '{JournalEntryStatuses.Posted}', 'Customer payment', 'CustomerPayment', 100, 100,
                 'je-{Guid.NewGuid():N}', '{new string('e', 64)}', 1, 'tenant-data-test', now(),
                 'tenant-data-test', now())
            RETURNING "Id" AS "Value";
            """).ToListAsync();

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO public."CustomerPayments"
                ("BusinessUnitId", "CustomerId", "CommercialCaseId", "ReceiptNumber", "Status",
                 "PaymentDate", "Amount", "JournalEntryId", "AccountingBridgeRequired",
                 "IdempotencyKey", "RequestHash", "Version", "CreatedBy", "CreatedOn")
            VALUES ({tenantId}, 1, {caseId}, 'RCPT-{Guid.NewGuid():N}'::text,
                 '{CustomerPaymentStatuses.Posted}', now(), 100, {journalId[0]}, true,
                 'pay-{Guid.NewGuid():N}', '{new string('f', 64)}', 1, 'tenant-data-test', now());
            """);
        await db.Database.ExecuteSqlRawAsync("SET session_replication_role = origin;");
        }
        finally { await db.Database.CloseConnectionAsync(); }
        db.ChangeTracker.Clear();
    }

    private static async Task<long> EnsureAccountingPeriodAsync(ErpRfqAutomationContext db, long tenantId)
    {
        var period = new AccountingPeriod
        {
            BusinessUnitId = tenantId,
            FiscalYear = DateTime.UtcNow.Year,
            PeriodNumber = DateTime.UtcNow.Month,
            Name = $"P-{DateTime.UtcNow:yyyy-MM}",
            StartsOn = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1),
            EndsOn = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1).AddDays(-1),
            Status = AccountingPeriodStatuses.Open,
            IdempotencyKey = "period-" + Guid.NewGuid().ToString("N"),
            RequestHash = new string('a', 64),
            Version = 1,
            CreatedBy = "tenant-data-test",
            CreatedOn = DateTime.UtcNow
        };
        db.Set<AccountingPeriod>().Add(period);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return period.Id;
    }

    private static long NewTenantId() => Random.Shared.Next(8_100_000, 8_800_000);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-tenant-data-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
