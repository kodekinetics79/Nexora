using System.Data;
using System.Security.Cryptography;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Services
{
    /// <summary>A quote the rep made outside Nexora, and the file the customer received.</summary>
    public sealed record UploadedQuoteCommand(
        long BusinessUnitId,
        long RfqId,
        string QuoteNumber,
        DateTime SentOn,
        DateTime? ValidUntil,
        long CurrencyId,
        decimal AmountBeforeTax,
        string FileName,
        byte[] FileBytes,
        string? DetectedContentType,
        string Actor,
        long? ActorUserId);

    /// <param name="ReplacedDraftNo">The unsent draft on the RFQ that the upload replaced, if there was one.</param>
    public sealed record UploadedQuoteResult(long QuoteId, string QuoteNo, string? ReplacedDraftNo = null);

    public partial class QuoteService
    {
        /// <summary>Matches <c>DocumentFileInspectionService.DefaultMaximumFileBytes</c>.</summary>
        public const long MaximumUploadedQuoteBytes = 25L * 1024 * 1024;

        /// <summary>
        /// Reps do not always quote through Nexora. They price a tender in Excel, type a quote in
        /// Word, or fill the customer's portal directly. Until that quote is in here the RFQ reads
        /// as never quoted, nobody is reminded to chase it, and the pipeline is short by its value.
        ///
        /// <para>The upload is an ordinary quote on the RFQ, created through
        /// <see cref="CreateQuoteAsync"/> so it gets its number, lineage and tax the same way as
        /// every other, with ONE line for the amount the rep quoted and their file kept beside it.
        /// It is recorded as SENT on the day the rep says it went out, which is what starts the
        /// follow-up clock.</para>
        ///
        /// <para>The send gates (price confirmation, below-floor approval) are not applied. They
        /// exist to stop a quote leaving; this one has already left, and refusing to record it
        /// would only make Nexora wrong about what the customer holds.</para>
        ///
        /// <para>An RFQ carries one quote. When the one it has is a draft Nexora never sent — often
        /// started from sourcing before the rep chose to quote elsewhere — the upload replaces it,
        /// through the same removal that writes a tombstone. A draft that carries evidence (prices
        /// confirmed, validity extended, an order, a delivery under way) is not replaced; neither is
        /// anything that reached the customer.</para>
        /// </summary>
        public async Task<UploadedQuoteResult> RecordUploadedQuoteAsync(
            UploadedQuoteCommand command, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (command.BusinessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(command), "A tenant is required.");
            if (string.IsNullOrWhiteSpace(command.Actor)) throw new ArgumentException("Authenticated actor is required.", nameof(command));
            if (_evidence is null) throw new InvalidOperationException("File storage is not available, so the quote file cannot be kept.");

            var reference = command.QuoteNumber?.Trim() ?? string.Empty;
            if (reference.Length == 0) throw new ArgumentException("Enter the quote number printed on the file.");
            if (reference.Length > 100) throw new ArgumentException("Keep the quote number under 100 characters.");
            if (command.AmountBeforeTax <= 0) throw new ArgumentException("Enter the amount before VAT.");
            if (command.CurrencyId <= 0) throw new ArgumentException("Choose the currency.");
            if (command.FileBytes is null || command.FileBytes.Length == 0) throw new ArgumentException("Choose the quote file.");
            if (command.FileBytes.LongLength > MaximumUploadedQuoteBytes) throw new ArgumentException("The file is larger than 25 MB.");

            var utcNow = DateTime.UtcNow;
            if (command.SentOn == default) throw new ArgumentException("Enter the date the quote was sent.");
            // One day of slack: the rep's "today" can already be tomorrow in UTC.
            if (command.SentOn.Date > utcNow.Date.AddDays(1)) throw new ArgumentException("The date sent cannot be in the future.");
            var sentOn = UploadedQuoteSentOn(command.SentOn, utcNow);
            var validUntil = command.ValidUntil?.Date;
            if (validUntil.HasValue && validUntil.Value < command.SentOn.Date)
                throw new ArgumentException("Valid until cannot be before the date sent.");

            var rfq = await _context.Rfqs.AsNoTracking()
                .Where(item => item.Id == command.RfqId && item.BusinessUnitId == command.BusinessUnitId)
                .Select(item => new { item.Id, item.Rfqno, item.CustomerId, item.CommercialCaseId, item.NexoraSerial })
                .SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("The RFQ was not found in this tenant.");
            if (!rfq.CustomerId.HasValue)
                throw new InvalidOperationException($"RFQ '{rfq.Rfqno}' has no customer yet. Set the customer on the RFQ first.");

            var replaceable = await ReplaceableDraftOnRfqAsync(command.BusinessUnitId, rfq.Id, rfq.Rfqno, ct);
            var sameNumber = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == command.BusinessUnitId && q.ExternalQuoteReference == reference)
                .Select(q => q.QuoteNo).FirstOrDefaultAsync(ct);
            if (sameNumber is not null)
                throw new InvalidOperationException($"Quote number {reference} is already in Nexora as {sameNumber}.");

            // The bytes are kept before the quote exists. A refusal after this point leaves an
            // object nothing points at, which the tenant's storage sweep already reclaims; the
            // other order could leave a quote pointing at nothing.
            var safeName = Path.GetFileName(command.FileName?.Trim() ?? string.Empty);
            if (safeName.Length == 0) safeName = "quote";
            var hash = Convert.ToHexString(SHA256.HashData(command.FileBytes)).ToLowerInvariant();
            var stored = await _evidence.WriteImmutableAsync(
                command.BusinessUnitId, "cleared", hash, Path.GetExtension(safeName), command.FileBytes, ct);

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

                if (replaceable is not null)
                {
                    // Re-checked inside the transaction: the draft may have been sent a moment ago.
                    await ReplaceableDraftOnRfqAsync(command.BusinessUnitId, rfq.Id, rfq.Rfqno, ct);
                    var removal = await new Repositories.QuoteRepository(_context).RemoveAsync(
                        replaceable.Value.Id, command.BusinessUnitId,
                        $"Replaced by the uploaded quote {reference}.", command.Actor);
                    if (removal?.Mode != QuoteRemovalModes.DraftDiscarded)
                        throw new InvalidOperationException($"RFQ '{rfq.Rfqno}' already has quote {replaceable.Value.QuoteNo}.");
                    _context.ChangeTracker.Clear();
                }

                QuoteResponseDTO created;
                try
                {
                    created = await CreateQuoteAsync(new QuoteCreateRequestDTO
                    {
                        RfqId = rfq.Id,
                        CustomerId = rfq.CustomerId,
                        BusinessUnitId = command.BusinessUnitId,
                        QuoteDate = sentOn,
                        ValidUntil = validUntil,
                        StatusId = await ResolveQuoteStatusIdAsync("DRAFT", command.BusinessUnitId),
                        CurrencyId = command.CurrencyId,
                        ExternalQuoteReference = reference,
                        CreatedBy = command.Actor,
                        QuoteItems =
                        [
                            new QuoteItemCreateRequestDTO
                            {
                                ItemDescription = $"As per quote {reference} (uploaded file)",
                                Quantity = 1,
                                UnitOfMeasure = "LOT",
                                UnitPrice = command.AmountBeforeTax,
                            },
                        ],
                    });
                }
                catch (DbUpdateException exception) when (IsExternalReferenceViolation(exception))
                {
                    throw new InvalidOperationException($"Quote number {reference} is already in Nexora.");
                }

                // Same sentence the send and the PDF give: a quote with no tax figured out is a
                // setup gap, and recording it would put a wrong total on the pipeline.
                if (await EvaluateTaxDerivationAsync(created.Id, command.BusinessUnitId, ct) is { } taxBlocker)
                    throw new InvalidOperationException(taxBlocker);

                var quote = await _context.Quotes
                    .Include(q => q.Status)
                    .Include(q => q.Rfq).ThenInclude(r => r!.Lead)
                    .SingleAsync(q => q.Id == created.Id && q.BusinessUnitId == command.BusinessUnitId, ct);
                quote.SentOn = sentOn;
                quote.UploadedFileName = Clip(safeName, 255);
                quote.UploadedFileStorageUri = Clip(stored.StorageUri, 500);
                quote.UploadedFileSha256 = hash;
                quote.UploadedFileContentType = Clip(command.DetectedContentType, 100);
                quote.UploadedFileSize = command.FileBytes.LongLength;
                quote.ModifiedBy = command.Actor;
                quote.ModifiedDate = utcNow;
                var note = $"Quoted outside Nexora as {reference}. File uploaded by {command.Actor}.";
                if (_lifecycle is not null)
                {
                    await _lifecycle.TransitionQuoteInCurrentTransactionAsync(
                        command.BusinessUnitId, quote.Id,
                        new LifecycleActor(command.Actor, "quote-upload"),
                        new LifecycleTransitionCommand(
                            "SENT", quote.LifecycleVersion, null, note, "quote-uploaded",
                            Guid.NewGuid().ToString("N"), $"quote:{quote.Id}:upload",
                            $"quote-uploaded:{quote.Id}"),
                        false, ct);
                }
                else
                {
                    quote.StatusId = await ResolveQuoteStatusIdAsync("SENT", command.BusinessUnitId);
                }

                await _context.SaveChangesAsync(ct);

                await RecordQuoteSentWorkAsync(quote,
                    new QuoteSendOptions { RequestedBy = command.Actor, RequestedByUserId = command.ActorUserId }, ct);
                await transaction.CommitAsync(ct);

                _logger?.LogInformation(
                    "Uploaded quote {Reference} recorded as {QuoteNo} on RFQ {RfqNo} (tenant {BusinessUnitId}) by {Actor}.",
                    reference, quote.QuoteNo, rfq.Rfqno, command.BusinessUnitId, command.Actor);
                return new UploadedQuoteResult(quote.Id, quote.QuoteNo, replaceable?.QuoteNo);
            });
        }

        /// <summary>
        /// Null when the RFQ has no quote; the draft to replace when its only quote is a clean,
        /// unsent draft; otherwise a refusal naming the quote it already has.
        /// </summary>
        private async Task<(long Id, string QuoteNo)?> ReplaceableDraftOnRfqAsync(
            long businessUnitId, long rfqId, string rfqNo, CancellationToken ct)
        {
            var onRfq = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId && q.Rfqid == rfqId)
                .OrderBy(q => q.Id).ToListAsync(ct);
            if (onRfq.Count == 0) return null;

            var draft = onRfq[0];
            var clean = onRfq.Count == 1
                && draft.SentOn is null && draft.RemovedOn is null && draft.RevisionOfQuoteId is null
                && await IsQuoteInDraftAsync(draft)
                && !await _context.QuotePriceAttestations.AnyAsync(x => x.BusinessUnitId == businessUnitId && x.QuoteId == draft.Id, ct)
                && !await _context.QuoteValidityExtensions.AnyAsync(x => x.BusinessUnitId == businessUnitId && x.QuoteId == draft.Id, ct)
                && !await _context.QuoteDeliveryRequests.AnyAsync(x => x.BusinessUnitId == businessUnitId && x.QuoteId == draft.Id, ct)
                && !await _context.Orders.AnyAsync(o => o.BusinessUnitId == businessUnitId && o.QuoteId == draft.Id, ct);
            if (!clean)
                throw new InvalidOperationException($"RFQ '{rfqNo}' already has quote {draft.QuoteNo}.");
            return (draft.Id, draft.QuoteNo);
        }

        /// <summary>
        /// The moment to record for a quote the rep says went out on <paramref name="day"/>.
        /// Midday UTC keeps the calendar day in every time zone the tenant is likely to be in
        /// (a midnight value reads as the day before west of UTC). A quote sent today is stamped
        /// now, never a time that has not happened yet.
        /// </summary>
        internal static DateTime UploadedQuoteSentOn(DateTime day, DateTime utcNow)
        {
            var midday = DateTime.SpecifyKind(day.Date.AddHours(12), DateTimeKind.Utc);
            return midday > utcNow ? utcNow : midday;
        }

        private static bool IsExternalReferenceViolation(DbUpdateException exception)
            => (exception.InnerException?.Message ?? exception.Message)
                .Contains("UX_Quotes_BU_ExternalQuoteReference", StringComparison.Ordinal);

        private static string? Clip(string? value, int length)
            => string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];
    }
}
