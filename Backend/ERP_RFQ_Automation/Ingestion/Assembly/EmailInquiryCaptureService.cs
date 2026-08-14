using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <param name="Assembly">The durable aggregate, or null when capture could not complete.</param>
/// <param name="SafeToMarkSeen">
/// Whether the mailbox message may be flagged \Seen. FALSE until the raw <c>.eml</c> is in
/// durable object storage AND the component manifest is committed — the two things that make
/// the message replayable without re-reading the mailbox. Marking a message seen before that
/// is how mail is lost silently.
/// </param>
public sealed record EmailInquiryCaptureResult(
    EmailInquiryAssembly? Assembly,
    bool SafeToMarkSeen,
    bool AlreadyCaptured);

public interface IEmailInquiryCaptureService
{
    Task<EmailInquiryCaptureResult> CaptureAsync(
        MimeMessage message,
        EmailIngest ingest,
        EmailConfiguration configuration,
        string? freshBodyText,
        CancellationToken ct = default);
}

/// <summary>
/// Turns a fetched message into a durable, replayable <see cref="EmailInquiryAssembly"/> before
/// anything downstream is allowed to touch it.
///
/// <para>Order matters and is the point of the class:</para>
/// <list type="number">
///   <item>plan the manifest from the MIME tree, so what the message is expected to produce is
///   decided ONCE and not as a side effect of how enqueueing happened to go;</item>
///   <item>write the raw <c>.eml</c> to <see cref="IEvidenceObjectStorage"/>, because a local
///   path is not an authoritative production copy and the previous code kept nothing else;</item>
///   <item>commit the assembly and EVERY component row in ONE transaction, so a crash can leave
///   "four expected, two recorded" — a hold — but never "two expected, two recorded", which
///   would read as a complete message that had silently lost half its content.</item>
/// </list>
///
/// <para>Only after all three does the caller learn it may mark the message seen.</para>
/// </summary>
public sealed class EmailInquiryCaptureService : IEmailInquiryCaptureService
{
    /// <summary>
    /// Evidence zone for the raw inbound message.
    ///
    /// <para>It USED to be <c>"inbound-email"</c>, on the reasoning that a mailbox archive should
    /// carry its own retention rule. That zone does not exist: both storage providers validate
    /// against a two-value whitelist (<c>quarantine</c> | <c>cleared</c>) and throw
    /// <see cref="ArgumentException"/> on anything else. The throw is not the storage-unavailable
    /// contract, so it escaped the handler below and capture failed on EVERY message, on every
    /// provider — the whole path was dead on arrival and every test missed it because every test
    /// substituted the storage.</para>
    ///
    /// <para><c>quarantine</c> is also the correct answer on the merits, not merely the legal one:
    /// a raw message straight off a mailbox is untrusted content that has not been through
    /// security inspection, which is exactly what the quarantine zone means and exactly what
    /// <see cref="ERP_RFQ_Automation.Extraction.DocumentIngestionService"/> writes un-inspected
    /// bytes to. Retention stays settable: nothing sweeps by zone, and the raw message is
    /// addressed by the URI recorded on the assembly row.</para>
    /// </summary>
    internal const string RawEmailZone = "quarantine";

    private readonly ErpRfqAutomationContext _context;
    private readonly IEvidenceObjectStorage _evidence;
    private readonly ILogger<EmailInquiryCaptureService> _logger;
    private readonly EmailInquiryLimits _limits;

    public EmailInquiryCaptureService(
        ErpRfqAutomationContext context,
        IEvidenceObjectStorage evidence,
        ILogger<EmailInquiryCaptureService> logger,
        EmailInquiryLimits? limits = null)
    {
        _context = context;
        _evidence = evidence;
        _logger = logger;
        _limits = limits ?? EmailInquiryLimits.Default;
    }

    public async Task<EmailInquiryCaptureResult> CaptureAsync(
        MimeMessage message,
        EmailIngest ingest,
        EmailConfiguration configuration,
        string? freshBodyText,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(ingest);
        ArgumentNullException.ThrowIfNull(configuration);

        var businessUnitId = configuration.BusinessUnitId;

        // Replay: an assembly already exists for this message. Do not re-plan and do not
        // re-write — return it so the caller resumes rather than forking a second view of the
        // same message. This is the restart path as well as the duplicate-delivery path.
        var existing = await _context.EmailInquiryAssemblies
            .Include(x => x.Components)
            .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                                      && x.EmailIngestId == ingest.Id, ct);
        if (existing is not null)
        {
            _logger.LogDebug(
                "Email inquiry assembly {AssemblyId} already exists for ingest {IngestId} ({Status}); resuming it.",
                existing.Id, ingest.Id, existing.Status);
            // Safe to mark seen only if the raw evidence actually landed. A prior attempt that
            // died between the row and the upload must not leave the message flagged read.
            return new EmailInquiryCaptureResult(
                existing, SafeToMarkSeen: existing.RawEvidenceUri is { Length: > 0 }, AlreadyCaptured: true);
        }

        // (1) Decide the shape of the message up front.
        EmailInquiryManifest manifest;
        try
        {
            manifest = await EmailInquiryManifestPlanner.PlanAsync(
                message, ingest.MessageId, freshBodyText, _limits, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The message could not even be read as MIME. It is NOT safe to mark seen: the raw
            // bytes are the only remaining record and they are still only in the mailbox.
            _logger.LogError(exception,
                "Could not plan the component manifest for ingest {IngestId}; the message stays unread.",
                ingest.Id);
            return new EmailInquiryCaptureResult(null, SafeToMarkSeen: false, AlreadyCaptured: false);
        }

        // (2) The raw .eml goes to durable object storage BEFORE anything is marked seen.
        byte[] rawBytes;
        using (var buffer = new MemoryStream())
        {
            await message.WriteToAsync(buffer, ct);
            rawBytes = buffer.ToArray();
        }
        var rawSha256 = Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant();

        EvidenceObject rawEvidence;
        try
        {
            rawEvidence = await _evidence.WriteImmutableAsync(
                businessUnitId, RawEmailZone, rawSha256, ".eml", rawBytes, ct);
        }
        catch (EvidenceStorageUnavailableException exception)
        {
            // The single storage-failure contract (PR #26). The store is down, not the message:
            // refuse to mark it seen so the next poll re-reads it, and say why without naming a
            // bucket, endpoint or provider type.
            _logger.LogError(exception,
                "Durable evidence storage is unavailable while capturing the raw message for ingest "
                + "{IngestId} (configuration fault: {IsConfigurationFault}). The message stays unread "
                + "and will be re-fetched.",
                ingest.Id, exception.IsConfigurationFault);
            return new EmailInquiryCaptureResult(null, SafeToMarkSeen: false, AlreadyCaptured: false);
        }

        // (3) Assembly + EVERY component row, in one transaction.
        var now = DateTimeOffset.UtcNow;
        var assembly = new EmailInquiryAssembly
        {
            BusinessUnitId = businessUnitId,
            EmailIngestId = ingest.Id,
            EmailConfigurationId = configuration.Id,
            MessageKey = ingest.MessageId,
            RawEvidenceUri = rawEvidence.StorageUri,
            RawEvidenceSha256 = rawSha256,
            RawEvidenceVersionId = Truncate(rawEvidence.Version, 256),
            SenderAddress = Truncate(message.From?.Mailboxes.FirstOrDefault()?.Address, 512),
            RecipientsJson = SerializeRecipients(message),
            Subject = Truncate(message.Subject, 1000),
            ReceivedAtUtc = message.Date,
            ManifestContractVersion = manifest.ContractVersion,
            ExpectedComponentCount = manifest.ExpectedComponentCount,
            CompletedComponentCount = 0,
            Status = EmailInquiryAssemblyStatus.Captured,
            StatusReason = manifest.QuotedOnlyBody
                ? "This message replied to an existing thread without adding new text."
                : null,
            SkippedPartsJson = SerializeSkips(manifest),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        foreach (var plan in manifest.Components)
        {
            assembly.Components.Add(new EmailInquiryComponent
            {
                BusinessUnitId = businessUnitId,
                ComponentKey = plan.ComponentKey,
                Kind = plan.Kind,
                Ordinal = plan.Ordinal,
                FileName = Truncate(plan.FileName, 512),
                MimeType = Truncate(plan.MimeType, 255),
                ByteSize = plan.ByteSize,
                ContentHash = string.IsNullOrEmpty(plan.ContentHash) ? null : plan.ContentHash,
                // A part that will not be processed is terminal the moment it is recorded, with
                // its reason. It still occupies a row so that "dropped" and "never there" stay
                // different observations.
                //
                // Skipped and Ignored are BOTH terminal and differ only in what they mean for
                // the message: Skipped is a part the sender attached that we could not read, and
                // one of those sends the whole message to review; Ignored is a provable inline
                // decoration and costs the message nothing.
                Status = plan.Disposition switch
                {
                    EmailInquiryComponentDisposition.Process => EmailInquiryComponentStatus.Pending,
                    EmailInquiryComponentDisposition.IgnoreInlineAsset => EmailInquiryComponentStatus.Ignored,
                    EmailInquiryComponentDisposition.StructuralContainer => EmailInquiryComponentStatus.StructuralOnly,
                    _ => EmailInquiryComponentStatus.Skipped
                },
                ReasonCode = plan.ReasonCode,
                ReasonDetail = Truncate(plan.ReasonDetail, 1000),
                NestingDepth = plan.NestingDepth,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            });
        }

        _context.EmailInquiryAssemblies.Add(assembly);
        try
        {
            await _context.SaveChangesAsync(ct);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Two pollers, or a retry racing itself. The unique index on EmailIngestId is the
            // authority; the loser reloads the winner's assembly rather than creating a rival.
            _context.ChangeTracker.Clear();
            var winner = await _context.EmailInquiryAssemblies
                .Include(x => x.Components)
                .FirstOrDefaultAsync(x => x.BusinessUnitId == businessUnitId
                                          && x.EmailIngestId == ingest.Id, ct);
            _logger.LogInformation(
                "A concurrent capture won the race for ingest {IngestId}; resuming assembly {AssemblyId}.",
                ingest.Id, winner?.Id);
            return new EmailInquiryCaptureResult(
                winner, SafeToMarkSeen: winner?.RawEvidenceUri is { Length: > 0 }, AlreadyCaptured: true);
        }

        _logger.LogInformation(
            "Captured email inquiry assembly {AssemblyId} for ingest {IngestId}: {Expected} expected part(s), "
            + "{Skipped} recorded as skipped, raw message in durable storage.",
            assembly.Id, ingest.Id, assembly.ExpectedComponentCount,
            manifest.Components.Count(c => c.Disposition == EmailInquiryComponentDisposition.Skip));

        return new EmailInquiryCaptureResult(assembly, SafeToMarkSeen: true, AlreadyCaptured: false);
    }

    private static string? SerializeRecipients(MimeMessage message)
    {
        var addresses = message.To.Mailboxes.Select(m => m.Address)
            .Concat(message.Cc.Mailboxes.Select(m => m.Address))
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        return addresses.Length == 0 ? null : JsonSerializer.Serialize(addresses);
    }

    private static string? SerializeSkips(EmailInquiryManifest manifest)
    {
        // Inline assets are deliberately excluded: this list is the record of what the message
        // LOST, and a signature logo is not a loss. Recording it here would put a decoration in
        // front of a reviewer on every message that carries one.
        var skips = manifest.Components
            .Where(c => c.Disposition == EmailInquiryComponentDisposition.Skip)
            .Select(c => new { file = c.FileName, reasonCode = c.ReasonCode, reason = c.ReasonDetail })
            .ToArray();
        return skips.Length == 0 ? null : JsonSerializer.Serialize(skips);
    }

    private static string? Truncate(string? value, int max)
        => value is null || value.Length <= max ? value : value[..max];

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is Npgsql.PostgresException { SqlState: "23505" }
           || exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 };
}
