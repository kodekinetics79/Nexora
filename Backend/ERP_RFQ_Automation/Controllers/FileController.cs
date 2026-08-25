using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.PlatformGovernance;
using ERP_RFQ_Automation.Traceability;
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FileController : ControllerBase
    {
        /// <summary>
        /// FR-RFQ-08. Tells the caller whether the bytes in this response were proved to be the
        /// bytes captured at intake. Present on every successful attachment download, because
        /// "we could not check" and "we checked and it was fine" are different facts and a
        /// reader that cannot tell them apart is being misled by omission.
        /// </summary>
        internal const string IntegrityHeader = "X-Nexora-Content-Integrity";

        /// <summary>The served bytes hash to the digest recorded when they were captured.</summary>
        internal const string IntegrityVerified = "verified";

        /// <summary>
        /// The attachment row predates the digest column, so there is nothing to verify against.
        /// The bytes are served — refusing every historic attachment would be a self-inflicted
        /// outage — but UNKNOWN is reported as unknown, never as verified.
        /// </summary>
        internal const string IntegrityUnverified = "unverified-no-recorded-digest";

        internal const string IntegrityAuditArea = "EvidenceIntegrity";
        internal const string IntegrityAuditAggregateType = "Attachment";
        internal const string AttachmentIntegrityFailedAction = "ATTACHMENT_INTEGRITY_FAILED";

        private readonly ErpRfqAutomationContext _context;
        private readonly IEvidenceObjectStorage _evidenceStorage;
        private readonly IFileStorage? _legacyStorage;
        private readonly ILogger<FileController> _logger;

        public FileController(
            ErpRfqAutomationContext context,
            IFileStorage legacyStorage,
            ILogger<FileController> logger)
        {
            _context = context;
            _legacyStorage = legacyStorage;
            _evidenceStorage = new LocalEvidenceObjectStorage(legacyStorage);
            _logger = logger;
        }

        [ActivatorUtilitiesConstructor]
        public FileController(
            ErpRfqAutomationContext context,
            IFileStorage legacyStorage,
            IEvidenceObjectStorage evidenceStorage,
            ILogger<FileController> logger)
        {
            _context = context;
            _legacyStorage = null;
            _evidenceStorage = evidenceStorage;
            _logger = logger;
        }

        // Path-addressed downloads cannot prove record ownership and are intentionally retired.
        [HttpGet("DownloadFile")]
        public IActionResult DownloadFile([FromQuery] string filePath)
            => StatusCode(StatusCodes.Status410Gone, new
            {
                message = "Path-based downloads are no longer supported. Use the attachment endpoint."
            });

        [HttpGet("attachment/{attachmentId:long}")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<IActionResult> DownloadAttachment(long attachmentId, CancellationToken ct)
        {
            var rawBusinessUnitId = User.FindFirst("businessUnitId")?.Value;
            if (!long.TryParse(rawBusinessUnitId, out var businessUnitId) || businessUnitId <= 0)
                return BadRequest("Business Unit ID is required.");

            try
            {
                // Attachments are polymorphic today, but production records currently use Lead.
                // The explicit BU predicate is deliberate defense-in-depth even though the Lead
                // query filter is also tenant-scoped.
                var attachment = await _context.Attachments
                    .AsNoTracking()
                    .SingleOrDefaultAsync(a => a.Id == attachmentId, ct);

                // Two parent types are served here. A customer purchase order is source evidence
                // in exactly the way an RFQ document is — it is what a price discrepancy is
                // resolved against — so refusing to serve it would leave the commercial record
                // pointing at a document nobody can open. FR-MTR-02 lot certificates are served by
                // DownloadLotCertificate below rather than here, because this route is gated on
                // Leads/View and a warehouse or quality user has no business needing lead access to
                // open a certificate of conformity.
                var isLead = string.Equals(attachment?.ParentType, "Lead", StringComparison.OrdinalIgnoreCase);
                var isCustomerPurchaseOrder = string.Equals(
                    attachment?.ParentType, "CustomerPurchaseOrder", StringComparison.OrdinalIgnoreCase);

                if (attachment is null || (!isLead && !isCustomerPurchaseOrder))
                    return NotFound();

                // Ownership is proved against the parent's OWN table. The explicit business-unit
                // predicate is deliberate defence in depth even though both are query-filtered.
                var belongsToTenant = isLead
                    ? await _context.Leads
                        .AsNoTracking()
                        .AnyAsync(l => l.Id == attachment.ParentId && l.BusinessUnitId == businessUnitId, ct)
                    : await _context.CustomerPurchaseOrders
                        .AsNoTracking()
                        .AnyAsync(po => po.Id == attachment.ParentId && po.BusinessUnitId == businessUnitId, ct);
                if (!belongsToTenant)
                    return NotFound();

                // The governed-evidence lookup below is keyed on a LEAD occurrence. Running it
                // with a purchase-order id would join on an unrelated identifier, so a purchase
                // order takes the raw-storage branch — still digest-verified on the way out.
                var sourceDocumentId = !isLead ? (long?)null : await _context.Set<LeadOccurrenceDocument>()
                    .AsNoTracking()
                    .Where(link => link.BusinessUnitId == businessUnitId
                                   && link.Occurrence.LeadId == attachment.ParentId)
                    .OrderBy(link => link.Ordinal)
                    .ThenBy(link => link.Id)
                    .Select(link => (long?)link.SourceDocumentId)
                    .FirstOrDefaultAsync(ct);

                var source = sourceDocumentId.HasValue
                    ? await _context.Set<SourceDocument>().AsNoTracking()
                        .SingleOrDefaultAsync(document => document.BusinessUnitId == businessUnitId
                            && document.Id == sourceDocumentId.Value
                            && document.OriginalFileName == attachment.FileName, ct)
                    : null;
                var job = source?.ExtractionJobId is { } extractionJobId
                    ? await _context.Set<ExtractionJob>().AsNoTracking()
                        .SingleOrDefaultAsync(candidate => candidate.BusinessUnitId == businessUnitId
                            && candidate.Id == extractionJobId, ct)
                    : null;

                if (source is null || job is null)
                {
                    // Compatibility is limited to direct legacy construction. The application DI
                    // path always supplies IEvidenceObjectStorage and therefore fails closed.
                    if (_legacyStorage is null)
                        return NotFound();
                    var legacyStream = await _legacyStorage.OpenReadAsync(attachment.FilePath, ct);
                    return await ServeVerifiedAttachmentAsync(
                        legacyStream,
                        attachment,
                        attachment.MimeType ?? "application/octet-stream",
                        businessUnitId,
                        ct);
                }

                if (attachment.FileSize.HasValue && attachment.FileSize.Value != source.ByteSize)
                {
                    _logger.LogWarning("Attachment {AttachmentId} does not match authoritative evidence size.", attachmentId);
                    return Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "The evidence object failed integrity verification.");
                }

                var stream = await _evidenceStorage.OpenVerifiedReadAsync(
                    job.StoragePath, source.ContentHash, ct);
                var contentType = string.IsNullOrWhiteSpace(source.DetectedMimeType)
                    ? "application/octet-stream"
                    : source.DetectedMimeType;
                return await ServeVerifiedAttachmentAsync(stream, attachment, contentType, businessUnitId, ct);
            }
            catch (FileNotFoundException)
            {
                return NotFound("The requested file was not found in evidence storage.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Rejected unsafe storage path for attachment {AttachmentId}.", attachmentId);
                return NotFound();
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Evidence integrity verification failed for attachment {AttachmentId}.", attachmentId);
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "The evidence object failed integrity verification.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading attachment {AttachmentId}.", attachmentId);
                return StatusCode(500, "An error occurred while retrieving the file.");
            }
        }

        /// <summary>
        /// Serves the immutable evidence object addressed by its durable SourceDocument id.
        /// Authorization is proved through the tenant-scoped Lead occurrence/document ledger;
        /// filenames and legacy attachment paths are never used as identity.
        /// </summary>
        [HttpGet("source-document/{sourceDocumentId:long}")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<IActionResult> DownloadSourceDocument(long sourceDocumentId, CancellationToken ct)
        {
            var rawBusinessUnitId = User.FindFirst("businessUnitId")?.Value;
            if (!long.TryParse(rawBusinessUnitId, out var businessUnitId) || businessUnitId <= 0)
                return BadRequest("Business Unit ID is required.");

            try
            {
                var isAuthorizedLeadEvidence = await _context.Set<LeadOccurrenceDocument>()
                    .AsNoTracking()
                    .AnyAsync(link => link.BusinessUnitId == businessUnitId
                        && link.SourceDocumentId == sourceDocumentId
                        && link.Occurrence.LeadId.HasValue, ct);
                if (!isAuthorizedLeadEvidence)
                {
                    isAuthorizedLeadEvidence = await _context.Set<LeadIngestionOccurrence>()
                        .AsNoTracking()
                        .AnyAsync(occurrence => occurrence.BusinessUnitId == businessUnitId
                            && occurrence.SourceDocumentId == sourceDocumentId
                            && occurrence.LeadId.HasValue, ct);
                }
                if (!isAuthorizedLeadEvidence)
                    return NotFound();

                var document = await _context.Set<SourceDocument>().AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.BusinessUnitId == businessUnitId
                        && candidate.Id == sourceDocumentId, ct);
                if (document is null)
                    return NotFound();
                if (document.PurgeState != EvidencePurgeState.Present)
                    return StatusCode(StatusCodes.Status410Gone, new
                    {
                        message = "The retained evidence bytes were removed under the tenant retention policy.",
                        document.BytesPurgedOn,
                        document.PurgePolicyCode
                    });
                if (document.SecurityStatus != DocumentSecurityStatus.Cleared)
                    return Conflict("The source document is not cleared for viewing.");
                if (!document.ExtractionJobId.HasValue)
                    return NotFound("The source document has no exact stored-object relation.");

                var job = await _context.Set<ExtractionJob>().AsNoTracking()
                    .SingleOrDefaultAsync(candidate => candidate.BusinessUnitId == businessUnitId
                        && candidate.Id == document.ExtractionJobId.Value
                        && candidate.ContentHash == document.ContentHash, ct);
                if (job is null || string.IsNullOrWhiteSpace(job.StoragePath))
                    return NotFound("The source document has no exact stored-object relation.");

                var stream = await _evidenceStorage.OpenVerifiedReadAsync(
                    job.StoragePath, document.ContentHash, ct);
                Response.Headers[IntegrityHeader] = IntegrityVerified;
                var contentType = string.IsNullOrWhiteSpace(document.DetectedMimeType)
                    ? "application/octet-stream"
                    : document.DetectedMimeType;
                return File(stream, contentType, document.OriginalFileName, enableRangeProcessing: true);
            }
            catch (FileNotFoundException)
            {
                return NotFound("The requested source document was not found in evidence storage.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Rejected unsafe storage identity for source document {SourceDocumentId}.", sourceDocumentId);
                return NotFound();
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Evidence integrity verification failed for source document {SourceDocumentId}.", sourceDocumentId);
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "The evidence object failed integrity verification.");
            }
        }

        /// <summary>
        /// FR-MTR-02. Serves a material-lot compliance certificate.
        ///
        /// <para><b>A separate route, not a third branch on the one above.</b> Two reasons, and
        /// both are load-bearing. The permission is different — this is gated on <c>Products</c>,
        /// because requiring <c>Leads</c>/View to open a certificate of conformity would put the
        /// document out of reach of the warehouse and quality users it exists for. And the storage
        /// resolution is different: the route above reaches the evidence store only through a lead's
        /// <c>ExtractionJob</c>, and falls back to a legacy file store that the DI construction path
        /// never supplies — so any non-lead attachment takes a branch that returns 404. A
        /// certificate is written straight to content-addressed evidence storage with its digest on
        /// the attachment row, so it is opened directly and verified against that digest.</para>
        ///
        /// <para>Ownership is proved against <c>material_lots</c>, which is tenant-scoped by both an
        /// EF query filter and a row-level-security policy.</para>
        /// </summary>
        [HttpGet("lot-certificate/{attachmentId:long}")]
        [RequireModulePermission("Products", PermissionAction.View)]
        public async Task<IActionResult> DownloadLotCertificate(long attachmentId, CancellationToken ct)
        {
            var rawBusinessUnitId = User.FindFirst("businessUnitId")?.Value;
            if (!long.TryParse(rawBusinessUnitId, out var businessUnitId) || businessUnitId <= 0)
                return BadRequest("Business Unit ID is required.");

            try
            {
                var attachment = await _context.Attachments
                    .AsNoTracking()
                    .SingleOrDefaultAsync(a => a.Id == attachmentId, ct);
                if (attachment is null || !string.Equals(
                        attachment.ParentType,
                        MaterialLotCertificateService.CertificateParentType,
                        StringComparison.OrdinalIgnoreCase))
                    return NotFound();

                // The certificate row is the authority on which lot this document belongs to; the
                // attachment's parent id agrees with it, and requiring both to match means a
                // re-parented attachment cannot be served under a lot it was never filed against.
                var certificate = await _context.MaterialLotCertificates
                    .AsNoTracking()
                    .SingleOrDefaultAsync(c => c.BusinessUnitId == businessUnitId
                                               && c.AttachmentId == attachmentId
                                               && c.MaterialLotId == attachment.ParentId, ct);
                if (certificate is null)
                    return NotFound();

                var owned = await _context.MaterialLots
                    .AsNoTracking()
                    .AnyAsync(lot => lot.Id == certificate.MaterialLotId
                                     && lot.BusinessUnitId == businessUnitId, ct);
                if (!owned)
                    return NotFound();

                var stream = await _evidenceStorage.OpenVerifiedReadAsync(
                    attachment.FilePath, certificate.ContentSha256, ct);
                var contentType = string.IsNullOrWhiteSpace(attachment.MimeType)
                    ? "application/octet-stream"
                    : attachment.MimeType;
                return await ServeVerifiedAttachmentAsync(stream, attachment, contentType, businessUnitId, ct);
            }
            catch (FileNotFoundException)
            {
                return NotFound("The certificate was not found in evidence storage.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Rejected unsafe storage path for lot certificate {AttachmentId}.", attachmentId);
                return NotFound();
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Certificate integrity verification failed for attachment {AttachmentId}.", attachmentId);
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "The stored certificate failed integrity verification.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading lot certificate {AttachmentId}.", attachmentId);
                return StatusCode(500, "An error occurred while retrieving the certificate.");
            }
        }

        /// <summary>
        /// FR-DLM-03. Serves proof-of-delivery evidence — the signature, the stamp, or the
        /// photograph of the goods at handover.
        ///
        /// <para><b>Its own route, for the reasons the lot-certificate route has its own.</b> The
        /// permission is different: this is gated on <c>Shipments</c>, because a despatch clerk
        /// reviewing a delivery dispute has no business needing <c>Leads</c>/View to open the
        /// signature. And the storage resolution is different: <see cref="DownloadAttachment"/>
        /// reaches the evidence store only through a lead's <c>ExtractionJob</c> and otherwise falls
        /// through to a legacy provider the DI construction path never supplies (recorded as E54),
        /// so any non-lead attachment on that route returns 404. POD evidence is written straight to
        /// content-addressed evidence storage with its digest on the attachment row, so it is opened
        /// directly and verified against that digest.</para>
        ///
        /// <para>Ownership is proved against <c>Shipments</c>, which is tenant-scoped by an EF query
        /// filter and by an explicit business-unit predicate here. The attachment's parent type must
        /// also match, so a re-parented attachment cannot be served as a POD it was never filed
        /// against.</para>
        /// </summary>
        [HttpGet("delivery-proof/{attachmentId:long}")]
        [RequireModulePermission("Shipments", PermissionAction.View)]
        public async Task<IActionResult> DownloadDeliveryProofEvidence(long attachmentId, CancellationToken ct)
        {
            var rawBusinessUnitId = User.FindFirst("businessUnitId")?.Value;
            if (!long.TryParse(rawBusinessUnitId, out var businessUnitId) || businessUnitId <= 0)
                return BadRequest("Business Unit ID is required.");

            try
            {
                var attachment = await _context.Attachments
                    .AsNoTracking()
                    .SingleOrDefaultAsync(a => a.Id == attachmentId, ct);
                if (attachment is null || !string.Equals(
                        attachment.ParentType,
                        ERP_RFQ_Automation.Delivery.DeliveryProofEvidenceService.EvidenceParentType,
                        StringComparison.OrdinalIgnoreCase))
                    return NotFound();

                var owned = await _context.Shipments
                    .AsNoTracking()
                    .AnyAsync(s => s.Id == attachment.ParentId && s.BusinessUnitId == businessUnitId, ct);
                if (!owned)
                    return NotFound();

                if (string.IsNullOrWhiteSpace(attachment.ContentSha256))
                {
                    // POD evidence is only ever written through DeliveryProofEvidenceService, which
                    // always records a digest. A row without one did not come from that path, and
                    // evidence of unknown provenance is not served on the route a dispute reads.
                    _logger.LogWarning(
                        "Delivery proof attachment {AttachmentId} (tenant {BusinessUnitId}) has no "
                        + "recorded digest and was refused.", attachmentId, businessUnitId);
                    return Problem(statusCode: StatusCodes.Status409Conflict,
                        title: "The evidence object failed integrity verification.");
                }

                var stream = await _evidenceStorage.OpenVerifiedReadAsync(
                    attachment.FilePath, attachment.ContentSha256, ct);
                var contentType = string.IsNullOrWhiteSpace(attachment.MimeType)
                    ? "application/octet-stream"
                    : attachment.MimeType;
                return await ServeVerifiedAttachmentAsync(stream, attachment, contentType, businessUnitId, ct);
            }
            catch (FileNotFoundException)
            {
                return NotFound("The delivery proof evidence was not found in evidence storage.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Rejected unsafe storage path for delivery proof {AttachmentId}.", attachmentId);
                return NotFound();
            }
            catch (InvalidDataException ex)
            {
                _logger.LogWarning(ex, "Delivery proof integrity verification failed for {AttachmentId}.", attachmentId);
                return Problem(statusCode: StatusCodes.Status409Conflict,
                    title: "The stored evidence failed integrity verification.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading delivery proof {AttachmentId}.", attachmentId);
                return StatusCode(500, "An error occurred while retrieving the evidence.");
            }
        }

        /// <summary>
        /// FR-RFQ-08 enforcement point. Recording a digest at capture only proves what the bytes
        /// WERE; this is where a reader proves what they still ARE, on the one route that hands
        /// attachment bytes to a user.
        ///
        /// <para>The check happens BEFORE the first byte leaves, because a response cannot be
        /// un-sent: a hash computed while streaming could only tell the client afterwards that
        /// what it already holds was wrong.</para>
        ///
        /// <para>Authorization and tenant scoping are decided by the caller and are untouched
        /// here — this method is reached only after the attachment has been proved to belong to
        /// the authenticated tenant.</para>
        /// </summary>
        private async Task<IActionResult> ServeVerifiedAttachmentAsync(
            Stream bytes,
            Attachment attachment,
            string contentType,
            long businessUnitId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(attachment.ContentSha256))
            {
                // Rows created before the digest column existed. Absent is UNKNOWN, not verified:
                // refusing them all would take out every historic attachment, so they are served
                // — but the unverifiable state is stated out loud, in the log and on the wire,
                // rather than being indistinguishable from a successful verification.
                _logger.LogWarning(
                    "FR-RFQ-08: attachment {AttachmentId} (tenant {BusinessUnitId}) has no recorded "
                    + "content digest; serving UNVERIFIED bytes.",
                    attachment.Id, businessUnitId);
                Response.Headers[IntegrityHeader] = IntegrityUnverified;
                return File(bytes, contentType, attachment.FileName, enableRangeProcessing: true);
            }

            Stream servable;
            string actualDigest;
            try
            {
                (servable, actualDigest) = await ComputeDigestAsync(bytes, ct);
            }
            catch
            {
                await bytes.DisposeAsync();
                throw;
            }

            if (!DigestMatches(actualDigest, attachment.ContentSha256))
            {
                // Fail closed: the handle is dropped without a single byte being written to the
                // response body. An integrity failure must never degrade into a successful
                // response carrying wrong or empty content.
                await servable.DisposeAsync();
                await RecordIntegrityFailureAsync(attachment, businessUnitId, actualDigest);

                // Deliberately says nothing beyond "this object is not trustworthy": no storage
                // path, no expected digest, no observed digest, no byte count. A caller who can
                // read the digests back learns exactly how to forge past the check.
                return Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "The stored document failed integrity verification and was not served.");
            }

            Response.Headers[IntegrityHeader] = IntegrityVerified;
            return File(servable, contentType, attachment.FileName, enableRangeProcessing: true);
        }

        /// <summary>
        /// Digest of the bytes that are about to be served, plus the stream to serve them from,
        /// rewound to the start.
        ///
        /// <para>Memory behaviour is deliberate. Both storage providers in this repository hand
        /// back a SEEKABLE stream (a 64 KB-buffered <see cref="FileStream"/> from local storage;
        /// a <see cref="MemoryStream"/> that <see cref="IEvidenceObjectStorage"/> has already
        /// materialized to verify against the source-document hash). For those, the digest is
        /// taken by reading that same handle once with a chunked hash and seeking back — constant
        /// memory, no second copy, and no TOCTOU window, since the very handle that was hashed is
        /// the one that serves. Range processing still works because the stream is still
        /// seekable.</para>
        ///
        /// <para>A forward-only stream is the only case that forces buffering, and it is reported
        /// rather than done silently: the bytes have to be held somewhere to be checked before
        /// they are sent, and holding them in memory is bounded by one request where spilling to
        /// disk would create a second, unverified copy of evidence.</para>
        /// </summary>
        private async Task<(Stream Servable, string Digest)> ComputeDigestAsync(Stream bytes, CancellationToken ct)
        {
            if (!bytes.CanSeek)
            {
                _logger.LogWarning(
                    "FR-RFQ-08: buffering a non-seekable attachment stream in memory to verify its "
                    + "digest before serving.");
                var buffered = new MemoryStream();
                try
                {
                    await bytes.CopyToAsync(buffered, ct);
                }
                finally
                {
                    await bytes.DisposeAsync();
                }

                buffered.Position = 0;
                var bufferedDigest = Convert.ToHexString(await SHA256.HashDataAsync(buffered, ct)).ToLowerInvariant();
                buffered.Position = 0;
                return (buffered, bufferedDigest);
            }

            bytes.Position = 0;
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(bytes, ct)).ToLowerInvariant();
            bytes.Position = 0;
            return (bytes, digest);
        }

        /// <summary>
        /// Constant-time comparison against the recorded digest. A recorded value that is not a
        /// well-formed lowercase SHA-256 is treated as a FAILURE, not as "nothing to check":
        /// a column that can be filled with junk to disable verification is not a control.
        /// </summary>
        internal static bool DigestMatches(string actualSha256, string? recordedSha256)
        {
            var expected = recordedSha256?.Trim().ToLowerInvariant();
            if (expected is not { Length: 64 } || !expected.All(Uri.IsHexDigit))
                return false;

            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSha256), Convert.FromHexString(expected));
        }

        /// <summary>
        /// Persists the refusal as a tenant-scoped, append-only governance event, so "the stored
        /// document changed after capture" is answerable months later by someone reading the audit
        /// trail rather than by someone who happened to be tailing the logs that day.
        ///
        /// <para>Written with <see cref="CancellationToken.None"/>: the client aborting the
        /// download must not erase the record that the object failed. Keyed by attachment plus the
        /// digest actually observed, so repeated downloads of the same corrupted object record
        /// once while a NEW divergence records again.</para>
        ///
        /// <para>The refusal has already been decided by the caller and does not depend on this
        /// write succeeding — a failure to audit can never become a decision to serve.</para>
        /// </summary>
        private async Task RecordIntegrityFailureAsync(
            Attachment attachment, long businessUnitId, string actualDigest)
        {
            _logger.LogError(
                "FR-RFQ-08 integrity failure: attachment {AttachmentId} (tenant {BusinessUnitId}) does "
                + "not match the digest recorded at capture. Refusing to serve the bytes.",
                attachment.Id, businessUnitId);

            try
            {
                var actor = ActorContext.From(User, HttpContext?.TraceIdentifier);
                var idempotencyKey = $"attachment-integrity:{attachment.Id}:{actualDigest}";
                var alreadyRecorded = await _context.TenantGovernanceAuditEvents
                    .AsNoTracking()
                    .AnyAsync(x => x.BusinessUnitId == businessUnitId
                                   && x.IdempotencyKey == idempotencyKey, CancellationToken.None);
                if (alreadyRecorded)
                    return;

                _context.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
                {
                    BusinessUnitId = businessUnitId,
                    Area = IntegrityAuditArea,
                    AggregateType = IntegrityAuditAggregateType,
                    AggregateReference = $"attachment:{attachment.Id}",
                    Action = AttachmentIntegrityFailedAction,
                    Reason = "The stored bytes do not match the SHA-256 recorded when the document "
                             + "was captured. The download was refused.",
                    EvidenceJson = JsonSerializer.Serialize(new
                    {
                        attachmentId = attachment.Id,
                        parentType = attachment.ParentType,
                        parentId = attachment.ParentId,
                        fileName = attachment.FileName,
                        recordedSha256 = attachment.ContentSha256,
                        observedSha256 = actualDigest,
                        route = "GET api/File/attachment/{attachmentId}",
                        correlationId = actor.CorrelationId
                    }),
                    IdempotencyKey = idempotencyKey,
                    ActorUserId = actor.UserId ?? 0,
                    OccurredOn = DateTime.UtcNow
                });
                await _context.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogCritical(ex,
                    "FR-RFQ-08: failed to persist the integrity-failure audit event for attachment "
                    + "{AttachmentId}. The download was still refused.", attachment.Id);
            }
        }
    }
}
