using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.GeneralLedger;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Retention;

/// <summary>
/// Lets a tenant's system admin decide what to keep and what to remove — without imposing a rule.
///
/// <para><b>The problem this exists to solve.</b> The age-based purge next door is correct and
/// unusable: it selects on ingestion date, and zero documents in this deployment are 30 days old,
/// so it ships and deletes nothing. Worse, date is the wrong axis in principle here — the four
/// test messages a tenant wants gone arrived the same afternoon as forty real ones.</para>
///
/// <para><b>The axis that works.</b> OUTCOME. A record that produced no inquiry, no lead, no
/// extraction and no assembly is one a human can judge without opening it, and — not
/// coincidentally — one nothing else in the database points at. The 30-day floor exists so a
/// tenant cannot destroy documents he may still need; that rationale does not reach a message the
/// system sent to ITSELF, or one intake already rejected as an autoreply. So the floor is kept
/// for the age policy and waived here, deliberately and in exactly one place
/// (<see cref="SettledCutoff"/>).</para>
///
/// <para><b>What replaces the floor.</b> Not nothing — a much smaller, differently-motivated
/// guard. Assembly is asynchronous, so a message that arrived a minute ago may be mid-flight and
/// simply have not produced its inquiry YET. A short settle window means "produced nothing" reads
/// the finished state rather than a race. It is a correctness guard, not a retention period, and
/// it is measured in hours rather than days.</para>
///
/// <para><b>Refusal over guessing.</b> Everything here can decline. A storage provider that
/// cannot enumerate refuses the sweep instead of reporting a clean store; a stored key whose
/// shape is not recognised is KEPT and named; a hash referenced anywhere is KEPT. The failure
/// mode is "we left this alone and told you", never a silently confident deletion.</para>
/// </summary>
public sealed class TenantDataControlService(
    ErpRfqAutomationContext db,
    IEvidenceObjectStorage storage,
    IFileStorage files,
    CommercialDocumentArchiveService archive,
    ILogger<TenantDataControlService> log)
{
    private const string Area = "TenantDataControl";

    public const string ActionCleanupRun = "TENANT_DATA_CLEANUP_RUN";
    public const string ActionMessagePurged = "EMAIL_INGEST_BYTES_PURGED";
    public const string ActionOrphanSwept = "ORPHANED_EVIDENCE_OBJECT_DELETED";
    public const string ActionSweepRefused = "ORPHANED_EVIDENCE_SWEEP_REFUSED";

    /// <summary>
    /// How long a message must have been settled before "it produced nothing" is a finished fact
    /// rather than a race with the assembly coordinator. NOT a retention period and deliberately
    /// not derived from <see cref="EvidenceRetentionPolicy.MinimumRetentionDays"/>: the 30-day
    /// floor protects documents a tenant may still need, and a bulk-mail autoreply that produced
    /// nothing is not one of them.
    /// </summary>
    public const int SettleHours = 24;

    private const int MaxMessagesPerRun = 500;
    private const int MaxObjectsPerRun = 2000;
    private const int MaxRefusalsReported = 50;

    /// <summary>The stored-object key shape every writer in this system produces:
    /// <c>Evidence/tenants/{bu}/{zone}/sha256/{first two hex}/{64 hex}{.ext}</c>. A key that does
    /// not match is not understood, and what is not understood is not deleted.</summary>
    private static readonly Regex EvidenceKeyShape = new(
        @"^Evidence/tenants/(?<bu>\d+)/(?<zone>quarantine|cleared|raw-mail)/sha256/(?<shard>[0-9a-f]{2})/(?<hash>[0-9a-f]{64})(?<ext>\.[a-z0-9]{1,11})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static DateTime SettledCutoff(DateTime now) => now.AddHours(-SettleHours);

    // ------------------------------------------------------------------ read

    public async Task<TenantDataControlView> GetAsync(long tenantId, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(tenantId);
        var now = DateTime.UtcNow;

        var producedNothing = await MailThatProducedNothingAsync(tenantId, now, ct);
        var noise = producedNothing.Where(IsNoise).ToList();
        var orphans = await OrphanScanAsync(tenantId, ct);

        var buckets = new List<TenantDataBucketView>
        {
            Bucket(TenantDataBuckets.MailThatProducedNothing,
                TenantDataControlCopy.MailProducedNothingTitle,
                TenantDataControlCopy.MailProducedNothingDetail,
                producedNothing.Count, MeasureMessages(producedNothing, ct), null),
            Bucket(TenantDataBuckets.MailTriagedAsNoise,
                TenantDataControlCopy.MailNoiseTitle,
                TenantDataControlCopy.MailNoiseDetail,
                noise.Count, MeasureMessages(noise, ct), null),
            Bucket(TenantDataBuckets.OrphanedStoredFiles,
                TenantDataControlCopy.OrphanedFilesTitle,
                TenantDataControlCopy.OrphanedFilesDetail,
                orphans.Deletable.Count, orphans.Deletable.Sum(x => x.ByteSize),
                orphans.BlockedReason)
        };

        var kept = await KeptAndWhyAsync(tenantId, ct);
        return new TenantDataControlView(buckets, kept, KeptSummary(kept));
    }

    private static TenantDataBucketView Bucket(string code, string title, string detail,
        int count, long bytes, string? blockedReason) =>
        new(code, title, detail, count, bytes,
            CanClear: blockedReason is null && count > 0,
            BlockedReason: blockedReason
                ?? (count == 0 ? TenantDataControlCopy.NothingToClear : null));

    private static bool IsNoise(MailCandidate candidate) =>
        string.Equals(candidate.TriageOutcome, "Noise", StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ selection: mail

    /// <summary>One ingested message that produced nothing.</summary>
    private sealed record MailCandidate(long Id, string MessageId, string? Subject, string FromEmail,
        DateTime CreatedOn, string? TriageOutcome, string? RawEmailPath);

    /// <summary>
    /// THE mail selection query. Every exclusion is applied in SQL, before a candidate reaches
    /// code that can delete anything — the same discipline the byte purge uses, and for the same
    /// reason: an excluded record must be impossible to select, not merely hard to click.
    ///
    /// <para>There is no age cutoff here beyond <see cref="SettleHours"/>. That is the floor
    /// waiver, and it is the whole reason this feature deletes anything at all: no production
    /// document is 30 days old, so an age-gated version of this bucket would ship and free zero
    /// bytes.</para>
    /// </summary>
    private async Task<List<MailCandidate>> MailThatProducedNothingAsync(
        long tenantId, DateTime now, CancellationToken ct)
    {
        // An unresolvable tenant identity cannot prove the absence of a legal hold, and a hold
        // freezes everything. Unknown resolves to "keep", never to "safe to delete".
        if (await TenantHoldBlocksAsync(tenantId, ct))
            return [];

        var cutoff = SettledCutoff(now);
        var inFlight = TenantDataInFlightMail.ParseStatuses.ToArray();
        var mailboxIds = db.EmailConfigurations.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId).Select(x => x.Id);

        return await db.EmailIngests.AsNoTracking()
            .Where(e => mailboxIds.Contains(e.EmailConfigurationId))
            // Already cleared. The tombstone is permanent; the bytes go once.
            .Where(e => e.BytesPurgedOn == null)
            // Settled, so "produced nothing" is a finished fact and not a race with assembly.
            .Where(e => e.CreatedOn < cutoff)
            // Still in flight: something will come back for these bytes. See TenantDataInFlightMail.
            .Where(e => e.ParseStatus == null || !inFlight.Contains(e.ParseStatus))
            // No inquiry was ever assembled from it...
            .Where(e => !db.EmailInquiryAssemblies.Any(a => a.EmailIngestId == e.Id))
            // ...and no lead ever came out of it. Either one makes the message load-bearing.
            .Where(e => !db.Leads.Any(l => l.EmailIngestsId == e.Id))
            .OrderBy(e => e.CreatedOn).ThenBy(e => e.Id)
            .Take(MaxMessagesPerRun)
            .Select(e => new MailCandidate(e.Id, e.MessageId, e.EmailSubject, e.FromEmail,
                e.CreatedOn, e.TriageOutcome, e.RawEmailPath))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Bytes a real run would free, measured against the files it would actually delete rather
    /// than assumed from a recorded size. A message whose stored copy is already gone contributes
    /// zero, so the figure on the confirmation screen cannot promise space that vanished with an
    /// ephemeral disk years ago.
    /// </summary>
    private long MeasureMessages(IReadOnlyList<MailCandidate> candidates, CancellationToken ct)
    {
        long total = 0;
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            total += MeasureRawMessage(candidate);
        }
        return total;
    }

    private long MeasureRawMessage(MailCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.RawEmailPath))
            return 0;
        // An object-store copy is the SAME object the assembly's RawEvidenceUri names (identical
        // bytes, identical content-addressed key), governed by the assembly evidence purge; this
        // path only ever measured and deleted the disk compatibility copy.
        if (EvidenceObjectUris.IsObjectUri(candidate.RawEmailPath))
            return 0;
        try
        {
            var path = files.ResolvePath(candidate.RawEmailPath);
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or ArgumentException or IOException
                                              or NotSupportedException)
        {
            // Outside the storage root, or unreadable. Contributes nothing to the promise and
            // will be refused rather than deleted when the run reaches it.
            log.LogDebug(exception, "Could not measure the stored copy of message {IngestId}.",
                candidate.Id);
            return 0;
        }
    }

    // ------------------------------------------------------------------ selection: orphans

    private sealed record OrphanScan(
        IReadOnlyList<StoredEvidenceObject> Deletable,
        IReadOnlyList<TenantDataRefusal> Refused,
        string? BlockedReason);

    /// <summary>
    /// Finds stored objects under THIS tenant's prefix that no live row points at.
    ///
    /// <para>62% of this deployment's stored objects are in exactly this state — quarantine
    /// siblings the code itself documents as "nothing has ever deleted either", and raw
    /// <c>.eml</c> written to a zone <see cref="EvidenceRetentionEligibility.ZoneKeysFor"/>
    /// cannot address — so no amount of row-driven purging will ever reach them.</para>
    ///
    /// <para><b>Four independent nets, and a key must fall through all four to be deleted.</b>
    /// The exact key must be unreferenced; its opposite-zone sibling must be unreferenced; its
    /// content hash must be referenced by nothing; and the key must match the shape every writer
    /// in this system produces. Hash matching is the important one — it catches a stored object
    /// whose extension differs from the one recorded on the row, which exact-key matching alone
    /// would call an orphan and destroy.</para>
    /// </summary>
    private async Task<OrphanScan> OrphanScanAsync(long tenantId, CancellationToken ct)
    {
        if (await TenantHoldBlocksAsync(tenantId, ct))
            return new OrphanScan([], [], "A legal hold is active on this tenant. Nothing can be deleted.");

        var prefix = TenantPrefix(tenantId);
        IReadOnlyList<StoredEvidenceObject> stored;
        try
        {
            stored = await storage.ListObjectsUnderPrefixAsync(prefix, ct);
        }
        catch (Exception exception) when (exception is NotSupportedException
                                              or UnauthorizedAccessException
                                              or IOException
                                              or EvidenceStorageUnavailableException)
        {
            // Refuse rather than report an empty, clean-looking store. "We could not look" and
            // "there is nothing there" are different answers and must never share a rendering.
            log.LogWarning(exception, "Could not list stored evidence for tenant {TenantId}.", tenantId);
            return new OrphanScan([], [], TenantDataControlCopy.StorageCannotList);
        }

        var referenced = await ReferencedKeysAsync(tenantId, ct);
        var deletable = new List<StoredEvidenceObject>();
        var refused = new List<TenantDataRefusal>();

        foreach (var found in stored.Take(MaxObjectsPerRun))
        {
            ct.ThrowIfCancellationRequested();
            var key = found.Key.Replace('\\', '/');
            var match = EvidenceKeyShape.Match(key);

            if (!match.Success)
            {
                Refuse(refused, key, "We do not recognise this file's name, so we cannot prove "
                    + "nothing is using it. It has been left where it is.", found.ByteSize);
                continue;
            }

            // Belt and braces: the regex already pins the business unit, but a listing that
            // returned a neighbouring prefix must never be swept under this tenant's authority.
            if (match.Groups["bu"].Value != tenantId.ToString())
            {
                Refuse(refused, key, "This file is filed under a different business unit and was "
                    + "not touched.", found.ByteSize);
                continue;
            }

            if (referenced.Keys.Contains(key))
                continue;

            var sibling = EvidenceRetentionEligibility.ZoneKeysFor(key)
                .FirstOrDefault(x => !string.Equals(x, key, StringComparison.Ordinal));
            if (sibling is not null && referenced.Keys.Contains(sibling))
                continue;

            if (referenced.Hashes.Contains(match.Groups["hash"].Value))
                continue;

            deletable.Add(found with { Key = key });
        }

        if (stored.Count > MaxObjectsPerRun)
            Refuse(refused, $"{stored.Count - MaxObjectsPerRun} further file(s)",
                "Only the first "
                + $"{MaxObjectsPerRun:N0} files were checked this time. Run this again to continue.", 0);

        return new OrphanScan(deletable, refused, null);
    }

    private static void Refuse(List<TenantDataRefusal> into, string what, string why, long bytes)
    {
        if (into.Count < MaxRefusalsReported)
            into.Add(new TenantDataRefusal(what, why, bytes));
    }

    private static string TenantPrefix(long tenantId) => $"Evidence/tenants/{tenantId}/";

    private sealed record ReferencedEvidence(HashSet<string> Keys, HashSet<string> Hashes);

    /// <summary>
    /// Every stored key and every content hash this tenant's live rows still depend on.
    ///
    /// <para>Only <see cref="EvidencePurgeState.Purged"/> source documents release their claim —
    /// that row has already asserted its bytes are destroyed, so an object still sitting under
    /// its hash is by definition unreferenced. A row in <c>PurgeRequested</c> is mid-purge and
    /// keeps its claim: sweeping underneath an in-flight purge would race its own tombstone.</para>
    /// </summary>
    private async Task<ReferencedEvidence> ReferencedKeysAsync(long tenantId, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var documents = await db.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.PurgeState != EvidencePurgeState.Purged)
            .Select(x => new { x.ObjectKey, x.ContentHash })
            .ToListAsync(ct);
        foreach (var document in documents)
        {
            hashes.Add(document.ContentHash);
            foreach (var key in EvidenceRetentionEligibility.ZoneKeysFor(document.ObjectKey))
                keys.Add(key);
        }

        // The authoritative raw .eml an assembly points at. These live in the raw-mail zone,
        // which ZoneKeysFor deliberately cannot reach, so without this every stored message
        // would look like an orphan. This is the single most dangerous omission available here.
        var assemblies = await db.EmailInquiryAssemblies.AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId)
            .Select(x => new { x.RawEvidenceUri, x.RawEvidenceSha256 })
            .ToListAsync(ct);
        foreach (var assembly in assemblies)
        {
            if (!string.IsNullOrWhiteSpace(assembly.RawEvidenceSha256))
                hashes.Add(assembly.RawEvidenceSha256.Trim());
            foreach (var key in KeysFromStorageUri(assembly.RawEvidenceUri))
                keys.Add(key);
        }

        // Raw local copies still recorded on a message we are not clearing.
        var rawPaths = await db.EmailIngests.AsNoTracking()
            .Where(e => db.EmailConfigurations.Any(c =>
                c.Id == e.EmailConfigurationId && c.BusinessUnitId == tenantId))
            .Where(e => e.RawEmailPath != null && e.BytesPurgedOn == null)
            .Select(e => e.RawEmailPath!)
            .ToListAsync(ct);
        foreach (var path in rawPaths)
            foreach (var key in KeysFromStorageUri(path))
                keys.Add(key);

        return new ReferencedEvidence(keys, hashes);
    }

    /// <summary>
    /// Turns whatever a provider recorded as a storage URI back into a comparable key.
    ///
    /// <para>The two providers record different things — local storage writes an absolute
    /// filesystem path, S3 writes <c>s3://bucket/key</c> — and a sweep that understood only one
    /// of them would treat every object written by the other as unreferenced. Both forms are
    /// reduced here, and anything reduced is added with its zone sibling so a URI pointing at the
    /// cleared copy also protects the quarantine one.</para>
    /// </summary>
    private static IEnumerable<string> KeysFromStorageUri(string? storageUri)
    {
        if (string.IsNullOrWhiteSpace(storageUri))
            yield break;

        var normalized = storageUri.Replace('\\', '/').Trim();
        var marker = normalized.IndexOf("Evidence/tenants/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            yield break;

        var key = normalized[marker..];
        foreach (var zoneKey in EvidenceRetentionEligibility.ZoneKeysFor(key))
            yield return zoneKey;
    }

    // ------------------------------------------------------------------ kept, and why

    /// <summary>
    /// The standing answer to "what will you never touch?", counted rather than asserted.
    ///
    /// <para>Every one of these reasons was already computed by the purge — and then shown only
    /// as a footnote to a run that had already happened, which answers the question far too late
    /// to reassure anyone. Two more are added that the DATABASE enforces and no screen has ever
    /// mentioned: a document behind an invoice you have issued, and one behind a payment already
    /// posted to your books. Both are physically undeletable, and a tenant discovering that from
    /// an error message rather than from this panel is a support call we caused.</para>
    /// </summary>
    private async Task<IReadOnlyList<TenantDataKeptView>> KeptAndWhyAsync(
        long tenantId, CancellationToken ct)
    {
        var present = db.Set<SourceDocument>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenantId && x.PurgeState == EvidencePurgeState.Present);

        var statutoryTypes = EvidenceRetentionEligibility.StatutoryDocumentTypes.ToArray();
        var terminalIntake = EvidenceRetentionEligibility.TerminalIntakeStatuses.ToArray();
        var openInquiryStatuses = EvidenceRetentionEligibility.OpenInquiryStatuses.ToArray();
        var openLeadIds = OpenLeadIdsQuery(tenantId);

        var statutory = await present.CountAsync(d => db.CommercialDocumentClassifications.Any(c =>
            c.BusinessUnitId == tenantId && c.SourceDocumentId == d.Id
            && statutoryTypes.Contains(c.DocumentType)), ct);

        var governance = await archive.GovernanceStateAsync(tenantId, ct);
        var heldOccurrences = governance.Values.Where(x => x.LegalHold).Select(x => x.OccurrenceId).ToArray();
        var legalHold = heldOccurrences.Length == 0 ? 0 : await db.Set<SourceDocumentOccurrence>()
            .AsNoTracking()
            .Where(o => o.BusinessUnitId == tenantId && heldOccurrences.Contains(o.Id))
            .Select(o => o.SourceDocumentId).Distinct().CountAsync(ct);

        var openIntake = await present.CountAsync(d => db.Set<SourceDocumentOccurrence>().Any(o =>
            o.BusinessUnitId == tenantId && o.SourceDocumentId == d.Id
            && !terminalIntake.Contains(o.IntakeStatus)), ct);

        var openCase = await present.CountAsync(d => db.Set<LeadIngestionOccurrence>().Any(l =>
            l.BusinessUnitId == tenantId && l.SourceDocumentId == d.Id
            && l.LeadId.HasValue && openLeadIds.Contains(l.LeadId.Value)), ct);

        var extractionPending = await present.CountAsync(d => db.Set<ExtractionJob>().Any(j =>
            j.BusinessUnitId == tenantId
            && j.Status != EvidenceRetentionEligibility.TerminalExtractionStatus
            && (j.Id == d.ExtractionJobId
                || db.Set<SourceDocumentOccurrence>().Any(o =>
                    o.BusinessUnitId == tenantId && o.SourceDocumentId == d.Id
                    && o.ExtractionJobId == j.Id))), ct);

        var openInquiry = await present.CountAsync(d => db.Set<CanonicalInquiry>().Any(i =>
            i.BusinessUnitId == tenantId && i.CorpusId == d.CorpusId
            && openInquiryStatuses.Contains(i.Status)), ct);

        var quarantined = await present
            .CountAsync(d => d.SecurityStatus == DocumentSecurityStatus.Quarantined, ct);

        // --- the two the database enforces and the product has never said out loud ---

        // "receivable documents cannot be deleted" (nexora_receivable_issued_immutable) fires on
        // any document that has been issued. Reached from evidence through the commercial case
        // its lead belongs to, entirely on foreign keys — no string parsing, so this cannot drift.
        var issuedCaseIds = db.Set<ReceivableDocument>().AsNoTracking()
            .Where(r => r.BusinessUnitId == tenantId && r.IssuedOn != null && r.CommercialCaseId != null)
            .Select(r => r.CommercialCaseId!.Value);
        var issuedInvoice = await present.CountAsync(d => db.Set<LeadIngestionOccurrence>().Any(l =>
            l.BusinessUnitId == tenantId && l.SourceDocumentId == d.Id && l.LeadId.HasValue
            && db.Leads.Any(lead => lead.Id == l.LeadId.Value
                && issuedCaseIds.Contains(lead.CommercialCaseId))), ct);

        // "journals cannot be deleted" (nexora_gl_guard_journal). A posted entry reaches evidence
        // through the customer payment that produced it, which carries the commercial case.
        var postedCaseIds = db.Set<CustomerPayment>().AsNoTracking()
            .Where(p => p.BusinessUnitId == tenantId && p.CommercialCaseId != null
                && p.JournalEntryId != null
                && db.Set<JournalEntry>().Any(j => j.Id == p.JournalEntryId.Value
                    && j.BusinessUnitId == tenantId && j.Status == JournalEntryStatuses.Posted))
            .Select(p => p.CommercialCaseId!.Value);
        var postedJournal = await present.CountAsync(d => db.Set<LeadIngestionOccurrence>().Any(l =>
            l.BusinessUnitId == tenantId && l.SourceDocumentId == d.Id && l.LeadId.HasValue
            && db.Leads.Any(lead => lead.Id == l.LeadId.Value
                && postedCaseIds.Contains(lead.CommercialCaseId))), ct);

        return
        [
            new("Invoices, purchase orders, customer orders, delivery notes and supplier confirmations",
                "Tax and commercial law require these to be kept for years. They are never deleted, "
                + "whatever you choose here.", statutory),
            new("Anything you have put on legal hold",
                "Release the hold first if you want it included.", legalHold),
            new("Invoices you have already issued to a customer",
                "Once an invoice is issued it is fixed. Nexora cannot delete it and neither can we.",
                issuedInvoice),
            new("Anything already posted to your accounts",
                "Payments that reached your books are permanent accounting records.", postedJournal),
            new("Documents still being processed",
                "We have not finished reading these yet, so the file is still needed.", openIntake),
            new("Documents whose reading can still be retried",
                "If extraction has not succeeded, the original is what a retry reads.", extractionPending),
            new("Documents on an inquiry still awaiting review",
                "Someone is still expected to open the original.", openInquiry),
            new("Documents on a live deal",
                "An open quote, order or RFQ still needs the original producible.", openCase),
            new("Files quarantined by the virus scanner",
                "The quarantined copy IS the evidence of what was blocked. It stays.", quarantined)
        ];
    }

    private static string KeptSummary(IReadOnlyList<TenantDataKeptView> kept)
    {
        var total = kept.Sum(x => x.Count);
        return total == 0
            ? "Nothing is currently being held back. When something must be kept, it will be listed here with the reason."
            : $"{total:N0} document(s) are protected and will not be deleted by anything on this page.";
    }

    /// <summary>Leads that are not in a terminal state. A lead with NO status counts as open:
    /// unknown must never resolve to "safe to delete".</summary>
    private IQueryable<long> OpenLeadIdsQuery(long tenantId)
    {
        var terminalCodes = EvidenceRetentionEligibility.TerminalLeadStatusCodes.ToArray();
        var terminalStatusIds = db.SetupMasters.AsNoTracking()
            .Where(s => s.BusinessUnitId == tenantId
                && s.SetupType.ToLower().Replace(" ", "") == "leadstatus"
                && s.SetupCode != null && terminalCodes.Contains(s.SetupCode.ToUpper()))
            .Select(s => s.SetupId);
        return db.Leads.AsNoTracking()
            .Where(l => l.BusinessUnitId == tenantId
                && (l.LeadStatusId == null || !terminalStatusIds.Contains(l.LeadStatusId.Value)))
            .Select(l => l.Id);
    }

    private async Task<bool> TenantHoldBlocksAsync(long businessUnitId, CancellationToken ct)
    {
        var platformTenantId = await TenantLegalHoldFence.ResolvePlatformTenantIdAsync(db, businessUnitId, ct);
        return platformTenantId is null
               || await TenantLegalHoldFence.HasActiveAsync(db, platformTenantId.Value, ct);
    }

    // ------------------------------------------------------------------ execute

    /// <summary>
    /// Clears the buckets the tenant chose — or simulates it.
    ///
    /// <para>A dry run walks the SAME selection and the SAME byte accounting as the real run, so
    /// the number on the confirmation screen is the number the real run produces. An estimate
    /// produced by different code from the deletion is not a confirmation, it is a guess.</para>
    /// </summary>
    public async Task<TenantDataCleanupResult> RunCleanupAsync(
        long tenantId, long actorUserId, string idempotencyKey,
        TenantDataCleanupCommand command, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureActor(tenantId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160,
            "Idempotency-Key is required.");
        var reason = PlatformGovernanceService.Required(command.Reason, 1000,
            "A reason is required: an irreversible deletion must say why it happened.");

        var selected = (command.Buckets ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().ToUpperInvariant())
            .Distinct()
            .ToList();
        if (selected.Count == 0)
            throw new PlatformGovernanceValidationException(
                "Choose at least one group to clear before running this.");
        foreach (var code in selected)
            if (!TenantDataBuckets.IsKnown(code))
                throw new PlatformGovernanceValidationException(
                    "One of the groups in this request is not something this version can clear. "
                    + "Reload the page and try again.");

        if (!command.IsDryRun)
        {
            // Verified on the server. A phrase checked only in the browser is a decoration on a
            // request anyone can send directly, not a gate.
            if (!string.Equals(command.Confirmation?.Trim(), TenantDataControlCopy.ConfirmationPhrase,
                    StringComparison.Ordinal))
                throw new PlatformGovernanceValidationException(
                    $"Type {TenantDataControlCopy.ConfirmationPhrase} to confirm before anything is deleted.");

            if (await ReplayAsync(tenantId, idempotencyKey, ct) is { } replay)
                return Deserialize(replay.EvidenceJson) with { IdempotentReplay = true };
        }

        var now = DateTime.UtcNow;
        var wantsAllMail = selected.Contains(TenantDataBuckets.MailThatProducedNothing);
        var wantsNoise = selected.Contains(TenantDataBuckets.MailTriagedAsNoise);
        var wantsOrphans = selected.Contains(TenantDataBuckets.OrphanedStoredFiles);

        var mail = new List<MailCandidate>();
        if (wantsAllMail || wantsNoise)
        {
            var producedNothing = await MailThatProducedNothingAsync(tenantId, now, ct);
            // The noise bucket is a SUBSET of the other, so the union is taken rather than the
            // sum. Ticking both must never be read as "clear them twice", and the receipt must
            // report the distinct number a human can go and count.
            mail = wantsAllMail ? producedNothing : producedNothing.Where(IsNoise).ToList();
        }

        var refusals = new List<TenantDataRefusal>();
        var orphans = new List<StoredEvidenceObject>();
        if (wantsOrphans)
        {
            var scan = await OrphanScanAsync(tenantId, ct);
            refusals.AddRange(scan.Refused);
            if (scan.BlockedReason is not null)
                refusals.Add(new TenantDataRefusal("Your stored files", scan.BlockedReason, 0));
            else
                orphans.AddRange(scan.Deletable);
        }

        long bytes = 0;
        var messagesCleared = 0;
        var filesDeleted = 0;

        foreach (var candidate in mail)
        {
            ct.ThrowIfCancellationRequested();
            if (command.IsDryRun)
            {
                bytes += MeasureRawMessage(candidate);
                messagesCleared++;
                continue;
            }

            var freed = await ClearOneMessageAsync(tenantId, actorUserId, reason, candidate, refusals, ct);
            bytes += freed;
            messagesCleared++;
        }

        foreach (var orphan in orphans)
        {
            ct.ThrowIfCancellationRequested();
            if (command.IsDryRun)
            {
                bytes += orphan.ByteSize;
                filesDeleted++;
                continue;
            }

            var freed = await DeleteOrphanAsync(tenantId, actorUserId, reason, orphan, refusals, ct);
            if (freed is null)
                continue;
            bytes += freed.Value;
            filesDeleted++;
        }

        var result = new TenantDataCleanupResult(
            command.IsDryRun, messagesCleared, filesDeleted, bytes,
            refusals.Take(MaxRefusalsReported).ToList(),
            TenantDataControlCopy.Summarise(command.IsDryRun, messagesCleared, filesDeleted, bytes),
            TenantDataControlCopy.NotErasure, false);

        if (!command.IsDryRun)
            await RecordRunAsync(tenantId, actorUserId, idempotencyKey, reason, selected, result, ct);
        return result;
    }

    /// <summary>
    /// Applies the source-document tombstone shape to one message: commit the intent, delete the
    /// stored copy, keep the row.
    ///
    /// <para>Ordering matters for the same reason it does on the byte purge. The tombstone is
    /// written and committed BEFORE the file is touched, so the forbidden state — a row promising
    /// a stored message that no longer exists — is structurally impossible. The tolerable state,
    /// tombstone written and file still present, self-heals: a delete that finds nothing is
    /// success, and the row is already excluded from the next selection.</para>
    /// </summary>
    private async Task<long> ClearOneMessageAsync(long tenantId, long actorUserId, string reason,
        MailCandidate candidate, List<TenantDataRefusal> refusals, CancellationToken ct)
    {
        var measured = MeasureRawMessage(candidate);
        var storedPath = candidate.RawEmailPath;

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            // Re-read under the tenant predicate, not by id alone. EmailIngests carries no
            // tenant column and the pipeline role is BYPASSRLS, so the mailbox join is the only
            // thing standing between tenants on a write that destroys bytes.
            var ingest = await db.EmailIngests.SingleAsync(x => x.Id == candidate.Id
                && db.EmailConfigurations.Any(c => c.Id == x.EmailConfigurationId
                    && c.BusinessUnitId == tenantId), ct);
            if (ingest.BytesPurgedOn is not null)
            {
                await tx.RollbackAsync(ct);
                return;
            }

            ingest.BytesPurgedOn = DateTime.UtcNow;
            ingest.PurgedByUserId = actorUserId;
            ingest.PurgeReason = reason;
            // The pointer goes with the bytes. A path to a file that is about to be deleted is a
            // promise the row cannot keep.
            ingest.RawEmailPath = null;

            db.TenantGovernanceAuditEvents.Add(Audit(tenantId, actorUserId,
                "EmailIngest", $"email-ingest:{candidate.Id}", ActionMessagePurged, reason,
                $"tenant-data-mail:{tenantId}:{candidate.Id}",
                new
                {
                    emailIngestId = candidate.Id,
                    messageId = candidate.MessageId,
                    subject = candidate.Subject,
                    from = candidate.FromEmail,
                    receivedOn = candidate.CreatedOn,
                    triageOutcome = candidate.TriageOutcome,
                    bucket = IsNoise(candidate)
                        ? TenantDataBuckets.MailTriagedAsNoise
                        : TenantDataBuckets.MailThatProducedNothing,
                    producedNoAssembly = true,
                    producedNoLead = true,
                    retained = new
                    {
                        emailIngestRow = true,
                        messageId = true,
                        sender = true,
                        subject = true,
                        arrivalTime = true,
                        triageVerdict = true
                    },
                    actor = new { userId = actorUserId, mode = "TENANT_INITIATED" },
                    irreversible = true,
                    notErasure = TenantDataControlCopy.NotErasure
                }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        });

        if (string.IsNullOrWhiteSpace(storedPath) || EvidenceObjectUris.IsObjectUri(storedPath))
            return 0;

        try
        {
            var deleted = await files.TryDeleteAsync(storedPath, ct);
            return deleted ? measured : 0;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or IOException or NotSupportedException
                                              or ArgumentException)
        {
            // The row is already a tombstone and stays one — the message is unreachable either
            // way. The leftover file is named so the tenant knows those bytes are still there.
            log.LogWarning(exception, "Could not delete the stored copy of message {IngestId}.",
                candidate.Id);
            Refuse(refusals, $"The stored copy of one message from {candidate.FromEmail}",
                "The message record has been cleared, but its stored file could not be removed and "
                + "is still taking up space. Report this so it can be tidied up.", measured);
            return 0;
        }
    }

    private async Task<long?> DeleteOrphanAsync(long tenantId, long actorUserId, string reason,
        StoredEvidenceObject orphan, List<TenantDataRefusal> refusals, CancellationToken ct)
    {
        EvidenceObjectPurgeResult purge;
        try
        {
            purge = await storage.TryDeletePurgedObjectAsync(
                orphan.Bucket, orphan.Key, orphan.Version, ct);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException
                                              or InvalidDataException
                                              or NotSupportedException
                                              or IOException)
        {
            log.LogWarning(exception, "Refused to sweep orphaned evidence object {Key}.", orphan.Key);
            Refuse(refusals, "One leftover file",
                "Your storage refused to delete it, so it was left alone.", orphan.ByteSize);
            return null;
        }

        db.ChangeTracker.Clear();
        db.TenantGovernanceAuditEvents.Add(Audit(tenantId, actorUserId,
            "EvidenceObject", $"evidence-object:{orphan.Key}", ActionOrphanSwept, reason,
            $"tenant-data-orphan:{tenantId}:{orphan.Key}",
            new
            {
                bucket = orphan.Bucket,
                key = orphan.Key,
                byteSize = orphan.ByteSize,
                outcome = purge.Deleted ? "DELETED" : "ALREADY_ABSENT",
                bytesFreed = purge.BytesFreed,
                provedUnreferencedBy = new[]
                {
                    "no source document row holds this key",
                    "no source document row holds its opposite-zone sibling",
                    "no live row holds its content hash",
                    "the key matches the shape this system writes"
                },
                actor = new { userId = actorUserId, mode = "TENANT_INITIATED" },
                irreversible = true
            }));
        await db.SaveChangesAsync(ct);
        return purge.BytesFreed;
    }

    // ------------------------------------------------------------------ plumbing

    private Task<TenantGovernanceAuditEvent?> ReplayAsync(long tenantId, string key, CancellationToken ct) =>
        db.TenantGovernanceAuditEvents.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == tenantId && x.IdempotencyKey == key, ct);

    private async Task RecordRunAsync(long tenantId, long actorUserId, string idempotencyKey,
        string reason, IReadOnlyList<string> buckets, TenantDataCleanupResult result,
        CancellationToken ct)
    {
        var kept = await KeptAndWhyAsync(tenantId, ct);
        db.ChangeTracker.Clear();
        db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
        {
            BusinessUnitId = tenantId,
            Area = Area,
            AggregateType = "TenantDataCleanupRun",
            AggregateReference = $"tenant:{tenantId}",
            Action = ActionCleanupRun,
            Reason = reason,
            EvidenceJson = JsonSerializer.Serialize(new
            {
                result,
                buckets,
                // What was KEPT and why, recorded alongside what was destroyed. An audit that
                // answers only "what did you delete" cannot answer "what did you decide to keep,
                // and on whose authority" — and that is the half a regulator asks about.
                kept = kept.Select(x => new { reason = x.Title, count = x.Count }),
                actor = new { userId = actorUserId, mode = "TENANT_INITIATED" }
            }),
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            OccurredOn = DateTime.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static TenantDataCleanupResult Deserialize(string evidenceJson)
    {
        using var document = JsonDocument.Parse(evidenceJson);
        var payload = document.RootElement.GetProperty("result");
        return payload.Deserialize<TenantDataCleanupResult>(JsonOptions)
            ?? throw new PlatformGovernanceConflictException(
                "A previous cleanup under this Idempotency-Key cannot be replayed.");
    }

    private static TenantGovernanceAuditEvent Audit(long tenantId, long actorUserId,
        string aggregateType, string aggregateReference, string action, string reason,
        string idempotencyKey, object evidence) => new()
        {
            BusinessUnitId = tenantId,
            Area = Area,
            AggregateType = aggregateType,
            AggregateReference = aggregateReference,
            Action = action,
            Reason = reason,
            EvidenceJson = JsonSerializer.Serialize(evidence),
            IdempotencyKey = idempotencyKey,
            ActorUserId = actorUserId,
            OccurredOn = DateTime.UtcNow
        };
}
