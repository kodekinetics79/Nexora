using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Retention;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The retention purge deletes bytes and keeps evidence. These tests hold that line from
/// both directions: an eligible document really loses its bytes and gains a readable
/// tombstone, and every exclusion is enforced by the SELECTION QUERY so an excluded
/// document cannot reach the deleting code at all.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EvidenceRetentionPurgeTests(PostgreSqlTestDatabase database)
{
    // ------------------------------------------------------------------ happy path

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Eligible_document_loses_its_bytes_and_keeps_its_evidence_and_tombstone()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-eligible.pdf");

            var clearedPath = files.ResolvePath(document.ClearedKey);
            var quarantinePath = files.ResolvePath(document.QuarantineKey);
            Assert.True(File.Exists(clearedPath));
            Assert.True(File.Exists(quarantinePath));

            var service = NewService(db, files);
            var result = await service.RunPurgeAsync(tenantId, 9, "purge-1",
                new EvidenceRetentionPurgeCommand(false, "Reclaiming space under the 90-day policy."), default);

            Assert.False(result.DryRun);
            Assert.Equal(1, result.Eligible);
            Assert.Equal(1, result.Purged);
            Assert.True(result.BytesReclaimed > 0);

            // The bytes are gone from BOTH zones. Ingestion writes each document twice and
            // nothing used to delete either copy; purging one and leaving the other would
            // mean telling the tenant a file was deleted while a readable copy remained.
            Assert.False(File.Exists(clearedPath));
            Assert.False(File.Exists(quarantinePath));

            db.ChangeTracker.Clear();
            var stored = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == document.Id);

            // The record and every lineage column survive untouched — this is the whole point.
            Assert.Equal(EvidencePurgeState.Purged, stored.PurgeState);
            Assert.NotNull(stored.BytesPurgedOn);
            Assert.Equal(9, stored.PurgedByUserId);
            Assert.Equal(document.Hash, stored.ContentHash);
            Assert.Equal("rfq-eligible.pdf", stored.OriginalFileName);
            Assert.Equal(document.ByteSize, stored.ByteSize);
            Assert.Equal(document.ClearedKey, stored.ObjectKey);
            Assert.StartsWith("retention/v", stored.PurgePolicyCode);

            // Lineage still resolves: the lead, its items and the field evidence are all there.
            Assert.True(await db.Set<FieldEvidence>()
                .AnyAsync(x => x.BusinessUnitId == tenantId), "Field evidence must survive a byte purge.");
            Assert.True(await db.Leads.AnyAsync(x => x.BusinessUnitId == tenantId && x.Id == document.LeadId));
            Assert.True(await db.LeadItems.AnyAsync(x => x.LeadId == document.LeadId));

            var tombstone = await db.TenantGovernanceAuditEvents.AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId
                    && x.Action == EvidenceRetentionService.ActionPurged);
            Assert.Equal($"source-document:{document.Id}", tombstone.AggregateReference);
            Assert.Equal(9, tombstone.ActorUserId);

            // The tombstone has to answer, on its own, "which file was this, under which
            // rule, by whom, and what survived" — so it is read as data, not as a string.
            using var evidence = JsonDocument.Parse(tombstone.EvidenceJson);
            var tomb = evidence.RootElement;
            var documentEvidence = tomb.GetProperty("document");
            Assert.Equal(document.Hash, documentEvidence.GetProperty("contentHash").GetString());
            Assert.Equal("SHA-256", documentEvidence.GetProperty("hashAlgorithm").GetString());
            Assert.Equal("rfq-eligible.pdf", documentEvidence.GetProperty("originalFileName").GetString());
            Assert.Equal(document.ByteSize, documentEvidence.GetProperty("byteSize").GetInt64());

            var policyEvidence = tomb.GetProperty("policy");
            Assert.Equal(EvidenceRetentionPolicy.DefaultRetentionDays,
                policyEvidence.GetProperty("retainDaysAfterExtraction").GetInt32());
            Assert.Contains("PDPL", policyEvidence.GetProperty("basis").GetString());

            Assert.True(tomb.GetProperty("irreversible").GetBoolean());
            Assert.Equal(EvidenceRetentionDisclosure.NotErasure, tomb.GetProperty("notErasure").GetString());

            // What survived is counted, not asserted.
            var retained = tomb.GetProperty("retained");
            Assert.True(retained.GetProperty("sourceDocumentRow").GetBoolean());
            Assert.Equal(1, retained.GetProperty("documentPages").GetInt32());
            Assert.Equal(1, retained.GetProperty("documentRegions").GetInt32());
            Assert.Equal(1, retained.GetProperty("fieldEvidence").GetInt32());
            Assert.Contains(document.LeadId,
                retained.GetProperty("leadIds").EnumerateArray().Select(x => x.GetInt64()));

            // Both zone objects are named with what happened to each.
            var objects = tomb.GetProperty("objects").EnumerateArray().ToList();
            Assert.Equal(2, objects.Count);
            Assert.All(objects, o => Assert.Equal("DELETED", o.GetProperty("outcome").GetString()));

            // Intent was committed before the bytes went, so the request event exists too.
            Assert.True(await db.TenantGovernanceAuditEvents.AnyAsync(x =>
                x.BusinessUnitId == tenantId
                && x.Action == EvidenceRetentionService.ActionPurgeRequested));

            // Purging bytes is not erasure, and the result says so in words.
            Assert.Contains(EvidenceRetentionDisclosure.NotErasure, result.Disclosure);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Second_run_is_a_no_op_and_a_replayed_key_returns_the_first_result()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-once.pdf");
            var service = NewService(db, files);

            var first = await service.RunPurgeAsync(tenantId, 9, "purge-a",
                new EvidenceRetentionPurgeCommand(false, "First run."), default);
            Assert.Equal(1, first.Purged);

            // A fresh key: nothing is eligible any more, so the run does nothing.
            db.ChangeTracker.Clear();
            var second = await service.RunPurgeAsync(tenantId, 9, "purge-b",
                new EvidenceRetentionPurgeCommand(false, "Second run."), default);
            Assert.Equal(0, second.Eligible);
            Assert.Equal(0, second.Purged);
            Assert.Equal(0, second.BytesReclaimed);

            // The ORIGINAL key replays the original answer instead of re-running.
            db.ChangeTracker.Clear();
            var replay = await service.RunPurgeAsync(tenantId, 9, "purge-a",
                new EvidenceRetentionPurgeCommand(false, "First run."), default);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(first.Purged, replay.Purged);
            Assert.Equal(first.BytesReclaimed, replay.BytesReclaimed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Document_whose_bytes_were_already_lost_reconciles_instead_of_failing()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-lost.pdf");

            // Reproduce the ~15 production rows whose bytes vanished with ephemeral storage
            // before the persistent disk existed: the record is there, the files are not.
            File.Delete(files.ResolvePath(document.ClearedKey));
            File.Delete(files.ResolvePath(document.QuarantineKey));

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-lost",
                new EvidenceRetentionPurgeCommand(false, "Reconciling documents whose bytes are gone."),
                default);

            Assert.Equal(1, result.Purged);
            Assert.Equal(0, result.BytesReclaimed);

            db.ChangeTracker.Clear();
            var stored = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == document.Id);
            Assert.Equal(EvidencePurgeState.Purged, stored.PurgeState);
            Assert.Equal(0, stored.PurgedByteCount);
            Assert.Equal(EvidenceRetentionEligibility.ReasonBytesAlreadyAbsent, stored.PurgeReason);
            Assert.True(await db.TenantGovernanceAuditEvents.AnyAsync(x =>
                x.BusinessUnitId == tenantId && x.Action == EvidenceRetentionService.ActionBytesAbsent));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Dry_run_deletes_nothing_and_reports_what_the_real_run_would_free()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-dry.pdf");
            var service = NewService(db, files);

            var dry = await service.RunPurgeAsync(tenantId, 9, "purge-dry",
                new EvidenceRetentionPurgeCommand(true, "Estimate before confirming."), default);

            Assert.True(dry.DryRun);
            Assert.Equal(1, dry.Eligible);
            Assert.Equal(1, dry.Purged);
            Assert.True(dry.BytesReclaimed > 0);
            Assert.Contains("Nothing was deleted", dry.Disclosure);
            Assert.Contains(EvidenceRetentionDisclosure.NotErasure, dry.Disclosure);

            Assert.True(File.Exists(files.ResolvePath(document.ClearedKey)));
            Assert.True(File.Exists(files.ResolvePath(document.QuarantineKey)));
            db.ChangeTracker.Clear();
            var stored = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == document.Id);
            Assert.Equal(EvidencePurgeState.Present, stored.PurgeState);
            Assert.Empty(await db.TenantGovernanceAuditEvents.AsNoTracking()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync());

            // The estimate is the promise the real run has to keep.
            db.ChangeTracker.Clear();
            var real = await service.RunPurgeAsync(tenantId, 9, "purge-real",
                new EvidenceRetentionPurgeCommand(false, "Confirmed."), default);
            Assert.Equal(dry.BytesReclaimed, real.BytesReclaimed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_absent_dry_run_flag_never_deletes()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-default.pdf");

            // A body that omits dryRun — or sends null — must simulate, not destroy.
            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-null",
                new EvidenceRetentionPurgeCommand(null, "Body omitted the flag."), default);

            Assert.True(result.DryRun);
            Assert.True(File.Exists(files.ResolvePath(document.ClearedKey)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Deletion_is_refused_until_the_tenant_opts_in()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: false);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-optin.pdf");
            var service = NewService(db, files);

            await Assert.ThrowsAsync<PlatformGovernanceConflictException>(() =>
                service.RunPurgeAsync(tenantId, 9, "purge-disabled",
                    new EvidenceRetentionPurgeCommand(false, "Not opted in."), default));
            Assert.True(File.Exists(files.ResolvePath(document.ClearedKey)));

            // A dry run still works, so a tenant can see the estimate before committing.
            db.ChangeTracker.Clear();
            var dry = await service.RunPurgeAsync(tenantId, 9, "purge-disabled-dry",
                new EvidenceRetentionPurgeCommand(true, "Estimate only."), default);
            Assert.Equal(1, dry.Eligible);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ exclusions

    public static TheoryData<string> ExclusionCases() =>
    [
        "legal-hold", "statutory-classification", "open-intake",
        "extraction-not-succeeded", "open-inquiry", "open-lead", "open-human-action",
        "too-recent", "quarantined", "not-completed"
    ];

    [Theory]
    [MemberData(nameof(ExclusionCases))]
    [Trait("Category", "PostgreSQL")]
    public async Task Excluded_documents_are_never_selected_and_never_lose_their_bytes(string exclusion)
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files,
                $"rfq-{exclusion}.pdf", exclusion: exclusion);

            var clearedPath = files.ResolvePath(document.ClearedKey);
            var quarantinePath = files.ResolvePath(document.QuarantineKey);

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, $"purge-{exclusion}",
                new EvidenceRetentionPurgeCommand(false, $"Attempting to purge a {exclusion} document."),
                default);

            Assert.Equal(0, result.Eligible);
            Assert.Equal(0, result.Purged);
            Assert.Equal(0, result.BytesReclaimed);
            Assert.True(File.Exists(clearedPath), $"{exclusion}: cleared bytes must survive.");
            Assert.True(File.Exists(quarantinePath), $"{exclusion}: quarantine bytes must survive.");

            db.ChangeTracker.Clear();
            var stored = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == document.Id);
            Assert.Equal(EvidencePurgeState.Present, stored.PurgeState);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Excluded_documents_are_reported_with_the_rule_that_excluded_them()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var held = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-held.pdf",
                exclusion: "legal-hold");

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-report",
                new EvidenceRetentionPurgeCommand(true, "What would be excluded?"), default);

            var skip = Assert.Single(result.Skipped);
            Assert.Equal(held.Id, skip.DocumentId);
            Assert.Equal("rfq-held.pdf", skip.FileName);
            Assert.Equal(EvidenceRetentionEligibility.Skip.LegalHold, skip.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The inverse of the exclusion theory, and the whole point of removing the trap.
    ///
    /// <para>"Request deletion review" had no approver anywhere in the product, and the purge read
    /// its flag as an EXCLUSION — so pressing the only button that mentioned deletion was the one
    /// reliable way to guarantee the document was never deleted. This asserts the document that
    /// was stuck behind such a request is now purged like any other, which honours what the tenant
    /// asked for instead of freezing it forever.</para>
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_document_stuck_behind_an_unapprovable_deletion_request_is_purged()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files,
                "rfq-stuck-request.pdf", exclusion: "deletion-requested");

            // The stuck request really is in the log — this is not a test that forgot to seed.
            Assert.True(await db.TenantGovernanceAuditEvents.AsNoTracking().AnyAsync(x =>
                x.BusinessUnitId == tenantId && x.Action == "DELETION_REQUESTED"));

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-stuck",
                new EvidenceRetentionPurgeCommand(false, "Clearing a document a tenant asked us to delete."),
                default);

            Assert.Equal(1, result.Eligible);
            Assert.Equal(1, result.Purged);
            Assert.False(File.Exists(files.ResolvePath(document.ClearedKey)));
            Assert.False(File.Exists(files.ResolvePath(document.QuarantineKey)));

            db.ChangeTracker.Clear();
            var stored = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == document.Id);
            Assert.Equal(EvidencePurgeState.Purged, stored.PurgeState);

            // And the reason it used to be skipped is gone from the vocabulary entirely.
            Assert.DoesNotContain(result.Skipped, x =>
                x.Reason.Contains("approved", StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    /// <summary>
    /// The other half of the tombstone, at run level: the audit answers "what did you decide to
    /// KEEP, and why", not only "what did you delete".
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_run_records_what_it_kept_and_why_grouped_by_reason()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-kept-a.pdf",
                exclusion: "legal-hold");
            await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-kept-b.pdf",
                exclusion: "statutory-classification");

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-kept",
                new EvidenceRetentionPurgeCommand(false, "Reclaiming space."), default);
            Assert.Equal(0, result.Purged);

            var kept = await db.TenantGovernanceAuditEvents.AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId
                    && x.Action == EvidenceRetentionService.ActionRunKept);
            Assert.Equal("purge-kept:kept", kept.IdempotencyKey);
            Assert.Equal(9, kept.ActorUserId);

            using var evidence = JsonDocument.Parse(kept.EvidenceJson);
            var root_ = evidence.RootElement;
            Assert.Equal(2, root_.GetProperty("keptCount").GetInt32());
            Assert.True(root_.GetProperty("statutoryOverridesTenantPreference").GetBoolean());

            // Grouped by the rule that refused, with a count each. A tenant asked to account for
            // his own data has to be able to answer this without re-deriving it per document.
            var byReason = root_.GetProperty("kept").EnumerateArray()
                .ToDictionary(x => x.GetProperty("reason").GetString()!,
                    x => x.GetProperty("count").GetInt32());
            Assert.Equal(1, byReason[EvidenceRetentionEligibility.Skip.LegalHold]);
            Assert.Equal(1, byReason[EvidenceRetentionEligibility.Skip.StatutoryDocumentType]);

            // The whole run, including the kept event, replays rather than doubling.
            db.ChangeTracker.Clear();
            await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-kept",
                new EvidenceRetentionPurgeCommand(false, "Reclaiming space."), default);
            Assert.Single(await db.TenantGovernanceAuditEvents.AsNoTracking()
                .Where(x => x.BusinessUnitId == tenantId
                    && x.Action == EvidenceRetentionService.ActionRunKept)
                .ToListAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ isolation

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task One_tenant_cannot_purge_another_tenants_documents()
    {
        var tenantA = NewTenantId();
        var tenantB = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantA, enabled: true);
            await SeedAsync(db, tenantB, enabled: true);
            var files = new LocalFileStorage(root, root);
            var documentB = await SeedPurgeableDocumentAsync(db, tenantB, files, "rfq-tenant-b.pdf");

            // Tenant A runs a purge. Tenant B has an eligible document; A must not see it.
            var result = await NewService(db, files).RunPurgeAsync(tenantA, 9, "purge-cross",
                new EvidenceRetentionPurgeCommand(false, "Tenant A run."), default);

            Assert.Equal(0, result.Scanned);
            Assert.Equal(0, result.Purged);
            Assert.True(File.Exists(files.ResolvePath(documentB.ClearedKey)));

            db.ChangeTracker.Clear();
            var storedB = await db.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantB && x.Id == documentB.Id);
            Assert.Equal(EvidencePurgeState.Present, storedB.PurgeState);

            // And tenant A's storage view reports none of tenant B's bytes.
            db.ChangeTracker.Clear();
            var view = await NewService(db, files).GetAsync(tenantA, default);
            Assert.Equal(0, view.Storage.DocumentCount);
            Assert.Equal(0, view.Storage.UsedBytes);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ containment

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_crafted_stored_path_cannot_delete_outside_the_storage_root()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        var outsideDirectory = NewRoot();
        var outside = Path.Combine(outsideDirectory, "must-survive.txt");
        await File.WriteAllTextAsync(outside, "not evidence");
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);

            // A document whose stored key climbs out of the root. Ingestion cannot produce
            // this, but a purge must be safe against a key that was tampered with or
            // migrated in from elsewhere.
            var corpus = DocumentCorpus.Create(tenantId, Guid.NewGuid(), CorpusSourceType.Api);
            db.Add(corpus);
            await db.SaveChangesAsync();
            var traversalKey = "Evidence/tenants/../../../" +
                Path.GetFileName(outsideDirectory) + "/must-survive.txt";
            var document = SourceDocument.Create(tenantId, corpus.Id, new string('e', 64),
                "traversal.txt", "text/plain", "local", traversalKey, "v1", 12,
                DateTimeOffset.UtcNow.AddDays(-400));
            db.Add(document);
            await db.SaveChangesAsync();
            document.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            document.StartExtraction();
            document.StartNormalization();
            document.Complete(1);
            await db.SaveChangesAsync();
            var occurrence = SourceDocumentOccurrence.Create(tenantId, document.Id, corpus.Id,
                "traversal-occurrence", "{}");
            db.Add(occurrence);
            await db.SaveChangesAsync();
            occurrence.MarkRejected("SecurityInspection", "unsupported_format",
                "{\"reason\":\"containment regression fixture\"}");
            await db.SaveChangesAsync();

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-traversal",
                new EvidenceRetentionPurgeCommand(false, "Containment regression."), default);

            // The document is selectable — containment is enforced at the deletion boundary,
            // not by hoping such a row never exists.
            Assert.Equal(1, result.Purged);
            Assert.Equal(0, result.BytesReclaimed);
            Assert.True(File.Exists(outside), "A crafted key must never delete outside the storage root.");

            var tombstone = await db.TenantGovernanceAuditEvents.AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId
                    && x.Action == EvidenceRetentionService.ActionBytesAbsent);
            Assert.Contains("REFUSED", tombstone.EvidenceJson);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    // ------------------------------------------------------------------ policy

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Retention_policy_is_bounded_versioned_and_audited()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: false, createPolicy: false);
            var service = NewService(db, new LocalFileStorage(root, root));

            var initial = await service.GetAsync(tenantId, default);
            Assert.Equal(EvidenceRetentionPolicy.DefaultRetentionDays, initial.Policy.RetentionDays);
            Assert.False(initial.Policy.IsEnabled);

            await Assert.ThrowsAsync<PlatformGovernanceValidationException>(() =>
                service.UpdatePolicyAsync(tenantId, 11, "policy-too-short",
                    new UpdateEvidenceRetentionPolicyCommand(7, true, "Too aggressive."), default));

            db.ChangeTracker.Clear();
            var updated = await service.UpdatePolicyAsync(tenantId, 11, "policy-ok",
                new UpdateEvidenceRetentionPolicyCommand(120, true, "Dispute window agreed with legal."),
                default);
            Assert.Equal(120, updated.Policy.RetentionDays);
            Assert.True(updated.Policy.IsEnabled);
            Assert.Equal(2, updated.Policy.Version);

            var audit = await db.TenantGovernanceAuditEvents.AsNoTracking()
                .SingleAsync(x => x.BusinessUnitId == tenantId
                    && x.Action == EvidenceRetentionService.ActionPolicyUpdated);
            Assert.Equal(11, audit.ActorUserId);
            Assert.Contains("Dispute window agreed with legal.", audit.Reason);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ------------------------------------------------------------------ database guarantees

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task The_database_still_refuses_to_delete_the_evidence_record_after_a_purge()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-immutable.pdf");
            await NewService(db, files).RunPurgeAsync(tenantId, 9, "purge-immutable",
                new EvidenceRetentionPurgeCommand(false, "Purge then attempt tampering."), default);

            await using var connection = await database.OpenConnectionAsync();

            // The record cannot be deleted — the purge did not weaken that.
            await using (var delete = connection.CreateCommand())
            {
                delete.CommandText = "DELETE FROM source_documents WHERE id = @id";
                delete.Parameters.AddWithValue("id", document.Id);
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync());
                Assert.Equal("55000", refusal.SqlState);
            }

            // Nor can "purged" be walked back to "present" to fake the file's continued
            // existence. This is the new trigger doing its job.
            await using (var rewind = connection.CreateCommand())
            {
                rewind.CommandText =
                    "UPDATE source_documents SET purge_state = 'Present', purge_requested_on = NULL, "
                    + "bytes_purged_on = NULL WHERE id = @id";
                rewind.Parameters.AddWithValue("id", document.Id);
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => rewind.ExecuteNonQueryAsync());
                Assert.Equal("23514", refusal.SqlState);
            }

            // Nor can the hash be rewritten to make the tombstone describe a different file.
            await using (var tamper = connection.CreateCommand())
            {
                tamper.CommandText = "UPDATE source_documents SET content_hash = @hash WHERE id = @id";
                tamper.Parameters.AddWithValue("hash", new string('f', 64));
                tamper.Parameters.AddWithValue("id", document.Id);
                var refusal = await Assert.ThrowsAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());
                Assert.Equal("23514", refusal.SqlState);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Active_tenant_legal_hold_removes_evidence_from_eligibility_and_preserves_bytes()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        try
        {
            await using var db = database.ContextFor(null);
            await SeedAsync(db, tenantId, enabled: true);
            var files = new LocalFileStorage(root, root);
            var document = await SeedPurgeableDocumentAsync(db, tenantId, files, "rfq-held.pdf");
            var platformTenant = await db.Set<Tenant>().IgnoreQueryFilters()
                .SingleAsync(t => t.PrimaryBusinessUnitId == tenantId);
            db.Set<TenantLegalHold>().Add(new TenantLegalHold
            {
                TenantId = platformTenant.Id,
                Scope = "AllTenantData",
                Authority = "Litigation counsel",
                Reason = "Preserve all tenant evidence for the active litigation matter.",
                EvidenceReference = "case://retention-hold",
                PlacedOn = DateTime.UtcNow,
                PlacedByPlatformUserId = 17,
                PlacedBy = "legal@nexora.test"
            });
            await db.SaveChangesAsync();

            var result = await NewService(db, files).RunPurgeAsync(tenantId, 9, "held-run",
                new EvidenceRetentionPurgeCommand(false, "Attempt retention while held."), default);

            Assert.Equal(0, result.Eligible);
            Assert.Equal(0, result.Purged);
            Assert.Contains(result.Skipped,
                skip => skip.DocumentId == document.Id
                        && skip.Reason == EvidenceRetentionEligibility.Skip.LegalHold);
            Assert.True(File.Exists(files.ResolvePath(document.ClearedKey)));
            Assert.True(File.Exists(files.ResolvePath(document.QuarantineKey)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Legal_hold_placement_waits_for_in_flight_retention_deletion_to_finish()
    {
        var tenantId = NewTenantId();
        var root = NewRoot();
        var storage = default(BlockingDeleteEvidenceStorage);
        try
        {
            long platformTenantId;
            await using (var seedDb = database.ContextFor(null))
            {
                await SeedAsync(seedDb, tenantId, enabled: true);
                var files = new LocalFileStorage(root, root);
                await SeedPurgeableDocumentAsync(seedDb, tenantId, files, "rfq-racing-hold.pdf");
                platformTenantId = await seedDb.Set<Tenant>().IgnoreQueryFilters()
                    .Where(t => t.PrimaryBusinessUnitId == tenantId)
                    .Select(t => t.Id)
                    .SingleAsync();
            }

            var raceFiles = new LocalFileStorage(root, root);
            storage = new BlockingDeleteEvidenceStorage(new LocalEvidenceObjectStorage(raceFiles));
            await using var purgeDb = database.ContextFor(null);
            var purgeService = NewService(purgeDb, raceFiles, storage);
            var purgeTask = purgeService.RunPurgeAsync(tenantId, 9, "deletion-wins-race",
                new EvidenceRetentionPurgeCommand(false, "Exercise legal-hold deletion fencing."), default);

            await storage.DeleteEntered.WaitAsync(TestWaits.Liveness);

            await using var holdDb = database.ContextFor(null);
            var holdService = new TenantLegalHoldService(holdDb,
                new PlatformAuditService(holdDb, NullLogger<PlatformAuditService>.Instance));
            var holdTask = holdService.PlaceAsync(platformTenantId, new PlaceTenantLegalHoldRequest
            {
                Scope = "AllTenantData",
                Authority = "Litigation counsel",
                Reason = "Preserve all tenant records for the newly received litigation order.",
                EvidenceReference = "case://retention-race"
            }, TenantLifecycleHarness.Operator("legal@nexora.test", 17), null, default);

            await Task.Delay(200);
            Assert.False(holdTask.IsCompleted,
                "Hold placement must wait while irreversible evidence deletion owns the shared fence.");

            storage.AllowDelete();
            var purge = await purgeTask.WaitAsync(TestWaits.Liveness);
            var hold = await holdTask.WaitAsync(TestWaits.Liveness);

            Assert.Equal(1, purge.Purged);
            Assert.True(hold.IsActive);
        }
        finally
        {
            storage?.AllowDelete();
            Directory.Delete(root, recursive: true);
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record SeededDocument(long Id, string Hash, long ByteSize, string ClearedKey,
        string QuarantineKey, long LeadId);

    private static EvidenceRetentionService NewService(
        ErpRfqAutomationContext db, IFileStorage files, IEvidenceObjectStorage? evidenceStorage = null) =>
        new(db, evidenceStorage ?? new LocalEvidenceObjectStorage(files),
            new LegacyAttachmentPurgeResolver(db, files),
            new CommercialDocumentArchiveService(db),
            new NoopLogger<EvidenceRetentionService>());

    private static async Task SeedAsync(ErpRfqAutomationContext db, long tenantId,
        bool enabled, bool createPolicy = true)
    {
        Seed.EnsureBusinessUnit(db, tenantId);
        await db.SaveChangesAsync();
        if (!await db.Set<Tenant>().IgnoreQueryFilters()
                .AnyAsync(t => t.PrimaryBusinessUnitId == tenantId))
        {
            db.Set<Tenant>().Add(new Tenant
            {
                Name = $"Retention tenant {tenantId}",
                Slug = $"retention-{tenantId}",
                Status = TenantStatus.Active,
                PrimaryBusinessUnitId = tenantId,
                CreatedBy = "retention-test",
                CreatedOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }
        if (!createPolicy)
            return;
        db.EvidenceRetentionPolicies.Add(new EvidenceRetentionPolicy
        {
            BusinessUnitId = tenantId,
            RetentionDays = EvidenceRetentionPolicy.DefaultRetentionDays,
            IsEnabled = enabled,
            Version = 1,
            UpdatedByUserId = 9,
            UpdatedOn = DateTime.UtcNow,
            CreatedOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private sealed class BlockingDeleteEvidenceStorage(IEvidenceObjectStorage inner)
        : IEvidenceObjectStorage
    {
        private readonly TaskCompletionSource _deleteEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowDelete =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _blocked;

        public Task DeleteEntered => _deleteEntered.Task;
        public bool IsDurable => inner.IsDurable;
        public void AllowDelete() => _allowDelete.TrySetResult();
        public Task ProbeAsync(CancellationToken ct = default) => inner.ProbeAsync(ct);

        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone,
            string sha256, string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            inner.WriteImmutableAsync(businessUnitId, zone, sha256, extension, content, ct);

        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => inner.OpenVerifiedReadAsync(storageUri, expectedSha256, ct);

        public async Task<EvidenceObjectPurgeResult> TryDeletePurgedObjectAsync(
            string bucket, string key, string version, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _blocked, 1) == 0)
            {
                _deleteEntered.TrySetResult();
                await _allowDelete.Task.WaitAsync(ct);
            }
            return await inner.TryDeletePurgedObjectAsync(bucket, key, version, ct);
        }

        public Task<long?> TryMeasureObjectAsync(string bucket, string key, string version,
            CancellationToken ct = default) => inner.TryMeasureObjectAsync(bucket, key, version, ct);
    }

    /// <summary>
    /// Builds a document in the exact shape the purge considers eligible — Completed,
    /// Cleared, resolved intake, succeeded extraction, terminal lead, both zone objects on
    /// disk — and then applies one <paramref name="exclusion"/> so a single test can prove
    /// that one rule alone is enough to protect it.
    /// </summary>
    private static async Task<SeededDocument> SeedPurgeableDocumentAsync(
        ErpRfqAutomationContext db, long tenantId, IFileStorage files, string fileName,
        string? exclusion = null)
    {
        var bytes = Encoding.UTF8.GetBytes($"evidence bytes for {fileName} {Guid.NewGuid():N}");
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var storage = new LocalEvidenceObjectStorage(files);
        var quarantine = await storage.WriteImmutableAsync(tenantId, "quarantine", hash, ".pdf", bytes);
        var cleared = await storage.WriteImmutableAsync(tenantId, "cleared", hash, ".pdf", bytes);

        var corpus = DocumentCorpus.Create(tenantId, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        db.Add(corpus);
        await db.SaveChangesAsync();

        var ingestedOn = exclusion == "too-recent"
            ? DateTimeOffset.UtcNow.AddDays(-2)
            : DateTimeOffset.UtcNow.AddDays(-400);
        var document = SourceDocument.Create(tenantId, corpus.Id, hash, fileName, "application/pdf",
            quarantine.Bucket, quarantine.Key, quarantine.Version, bytes.LongLength, ingestedOn);
        db.Add(document);
        await db.SaveChangesAsync();

        if (exclusion == "quarantined")
        {
            // Quarantined bytes ARE the malware evidence for the document; they are never
            // reclaimed, however old the document is.
            document.MarkSecurityStatus(DocumentSecurityStatus.Quarantined);
            await db.SaveChangesAsync();
        }
        else
        {
            document.ReleaseFromQuarantine(cleared.Bucket, cleared.Key, cleared.Version);
            if (exclusion != "not-completed")
            {
                document.StartExtraction();
                document.StartNormalization();
                document.Complete(1);
            }
            await db.SaveChangesAsync();
        }

        // Terminal lead status, so the open-commercial-case rule does not fire by accident
        // on the documents that are supposed to be eligible.
        var terminalStatus = new SetupMaster
        {
            SetupType = "LeadStatus",
            SetupCode = "COMPLETED",
            SetupValue = "Completed",
            BusinessUnitId = tenantId,
            IsActive = true,
            CreatedBy = "retention-test",
            CreatedOn = DateTime.UtcNow
        };
        db.SetupMasters.Add(terminalStatus);
        await db.SaveChangesAsync();

        var lead = new Lead
        {
            BusinessUnitId = tenantId,
            Rfqno = "RFQ-RETENTION-" + Guid.NewGuid().ToString("N")[..8],
            BuyersName = "Ahmed K",
            Clientemail = "ahmed.k@example.com",
            RecDate = DateTime.UtcNow.AddDays(-400),
            LeadSource = "ManualUpload",
            CreatedBy = "retention-test",
            CreatedDate = DateTime.UtcNow.AddDays(-400),
            // A lead with NO status counts as an open commercial case: "unknown" must never
            // resolve to "safe to delete".
            LeadStatusId = exclusion == "open-lead" ? null : terminalStatus.SetupId
        };
        db.Leads.Add(lead);
        await db.SaveChangesAsync();
        db.LeadItems.Add(new LeadItem
        {
            LeadId = lead.Id,
            ProductShortName = "Pressure sensor",
            ManufacturerPartNumber = "PS-100",
            Quantity = 5
        });
        await db.SaveChangesAsync();

        var batchId = Guid.NewGuid();
        db.Set<LeadIngestionBatch>().Add(new LeadIngestionBatch
        {
            Id = batchId,
            BusinessUnitId = tenantId,
            SourceChannel = "ManualUpload",
            CreatedBy = "retention-test",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var occurrence = SourceDocumentOccurrence.Create(tenantId, document.Id, corpus.Id,
            "retention-" + Guid.NewGuid().ToString("N"), "{\"source\":\"retention-test\"}");
        db.Add(occurrence);
        await db.SaveChangesAsync();

        db.Set<LeadIngestionOccurrence>().Add(new LeadIngestionOccurrence
        {
            BusinessUnitId = tenantId,
            BatchId = batchId,
            LeadId = lead.Id,
            SourceDocumentId = document.Id,
            SourceDocumentOccurrenceId = occurrence.Id,
            SourceChannel = "ManualUpload",
            IdempotencyKey = "retention-" + Guid.NewGuid().ToString("N"),
            OriginalFileName = fileName,
            ContentHash = hash,
            LogicalInquiryFingerprint = new string('a', 64),
            Classification = LeadOccurrenceClassification.New,
            Confidence = 0.99m,
            ProcessingPath = LeadProcessingPath.Deterministic,
            SourceReceivedAtUtc = DateTimeOffset.UtcNow.AddDays(-400),
            IngestedAtUtc = DateTimeOffset.UtcNow.AddDays(-400),
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-400),
            ActorType = "Test",
            ActorId = "retention-test",
            CorrelationId = "retention-" + Guid.NewGuid().ToString("N")[..8]
        });
        await db.SaveChangesAsync();

        var job = new ExtractionJob
        {
            BusinessUnitId = tenantId,
            BatchId = batchId,
            SourceDocumentOccurrenceId = occurrence.Id,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = cleared.StorageUri,
            FileName = fileName,
            ContentHash = hash,
            Status = exclusion == "extraction-not-succeeded"
                ? ExtractionStatus.DeadLetter
                : ExtractionStatus.Succeeded,
            CreatedOn = DateTime.UtcNow.AddDays(-400),
            UpdatedOn = DateTime.UtcNow.AddDays(-400)
        };
        db.Add(job);
        await db.SaveChangesAsync();
        document.BindExtractionJob(job.Id);

        // The occurrence follows the real intake path — Accepted -> Queued (on binding a
        // job) -> Processing -> terminal — so the eligibility rules are exercised against
        // states the product actually produces.
        occurrence.BindExtractionJob(job.Id);
        occurrence.MarkProcessing();
        if (exclusion == "open-intake")
            occurrence.MarkReviewRequired();
        else
            occurrence.MarkResolved();
        await db.SaveChangesAsync();

        // Page, region, run, inquiry and field evidence: the derived evidence graph that
        // must survive the purge intact. "Ahmed K" is deliberately real-looking personal
        // data — it is exactly what a byte purge does NOT erase.
        var page = DocumentPage.Create(tenantId, document.Id, 1, 612, 792, 0,
            new string('b', 64), DocumentPageKind.PhysicalPage);
        db.Add(page);
        await db.SaveChangesAsync();
        var region = DocumentRegion.Create(tenantId, page.Id, DocumentRegionType.Text,
            10, 10, 100, 20, "Buyer: Ahmed K", 0.98m);
        db.Add(region);
        await db.SaveChangesAsync();

        var runId = Guid.NewGuid();
        var run = ExtractionRun.Create(tenantId, document.Id, runId, job.Id, 1,
            "native-parser/v1", "canonical-rfq/v1");
        db.Add(run);
        await db.SaveChangesAsync();

        // canonical_inquiries is append-only (trg_canonical_inquiries_append_only), so the
        // terminal status has to be reached BEFORE the insert — there is no UPDATE path,
        // which is exactly the immutability this feature is built around.
        var inquiry = CanonicalInquiry.Create(tenantId, corpus.Id, 1);
        if (exclusion != "open-inquiry")
            inquiry.Validate();
        db.Add(inquiry);
        await db.SaveChangesAsync();

        db.Add(FieldEvidence.ForInquiry(tenantId, region.Id, inquiry.Id, "BuyersName",
            "Ahmed K", "Ahmed K", 0.98m, "native-parser/v1", runId));
        await db.SaveChangesAsync();

        switch (exclusion)
        {
            case "legal-hold":
                await new CommercialDocumentArchiveService(db).GovernAsync(tenantId, 9, occurrence.Id,
                    "hold-" + occurrence.Id, new(0, "HOLD_APPLIED", "Litigation hold."), default);
                break;
            case "deletion-requested":
                // Written straight into the append-only log rather than through GovernAsync,
                // which no longer accepts the action. This is exactly the shape of the record
                // stuck on business unit 7 occurrence 299 since 2026-08-12: a request nothing
                // could ever approve, sitting in a log that cannot be edited.
                db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
                {
                    BusinessUnitId = tenantId,
                    Area = "CommercialDocumentArchive",
                    AggregateType = "SourceDocumentOccurrence",
                    AggregateReference = $"occurrence:{occurrence.Id}",
                    Action = "DELETION_REQUESTED",
                    Reason = "Awaiting a decision that had no decider.",
                    EvidenceJson = "{}",
                    IdempotencyKey = "deletion-" + occurrence.Id,
                    ActorUserId = 9,
                    OccurredOn = DateTime.UtcNow.AddDays(-12)
                });
                await db.SaveChangesAsync();
                break;
            case "statutory-classification":
                db.CommercialDocumentClassifications.Add(CommercialDocumentClassification.Create(
                    tenantId, document.Id, hash, cleared.Version,
                    "classify-" + Guid.NewGuid().ToString("N"), new string('c', 64),
                    new CommercialDocumentDecision(CommercialDocumentType.SupplierInvoice,
                        0.95m, "LocalDeterministic/v1", "{}", false)));
                await db.SaveChangesAsync();
                break;
            case "open-human-action":
                db.HumanActionItems.Add(new HumanActionItem
                {
                    BusinessUnitId = tenantId,
                    ActionType = "REVIEW",
                    SourceType = "SourceDocumentOccurrence",
                    SourceReference = $"occurrence:{occurrence.Id}",
                    Title = "Confirm buyer identity",
                    Summary = "Open action referencing this document.",
                    Recommendation = "Review the original.",
                    EvidenceJson = "{}",
                    Confidence = 0.5m,
                    CommercialImpact = "Blocks quoting",
                    ResumeActionCode = "RESUME",
                    Priority = HumanActionPriority.Medium,
                    Status = HumanActionStatus.Open,
                    DueOn = DateTime.UtcNow.AddDays(1),
                    CreatedOn = DateTime.UtcNow,
                    CreatedByUserId = 9,
                    UpdatedOn = DateTime.UtcNow
                });
                await db.SaveChangesAsync();
                break;
        }

        db.ChangeTracker.Clear();
        return new SeededDocument(document.Id, hash, bytes.LongLength, cleared.Key,
            quarantine.Key, lead.Id);
    }

    private static long NewTenantId() => Random.Shared.Next(9_100_000, 9_800_000);

    private static string NewRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
