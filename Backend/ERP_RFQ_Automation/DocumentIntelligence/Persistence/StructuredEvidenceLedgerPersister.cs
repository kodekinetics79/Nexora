using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using LedgerSeverity = ERP_RFQ_Automation.DocumentIntelligence.Persistence.ValidationSeverity;
using CanonicalDtos = ERP_RFQ_Automation.DTOs.DocumentIntelligence;

namespace ERP_RFQ_Automation.DocumentIntelligence.Persistence;

public sealed class StructuredEvidenceLedgerPersister
{
    private const string ParserVersion = "native-spreadsheet/v2";
    private static readonly Regex CellAddress = new(
        "^'(?<sheet>(?:''|[^'])+)'!(?<column>[A-Z]+)(?<row>[1-9][0-9]*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LegacyAddress = new(
        "^row (?<row>[1-9][0-9]*)(?:, column (?<column>[A-Z]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly ErpRfqAutomationContext _context;

    public StructuredEvidenceLedgerPersister(ErpRfqAutomationContext context) => _context = context;

    public async Task PersistAsync(
        ExtractionJob job,
        ChunkedExtractionOutcome outcome,
        IReadOnlyList<Lead> leads,
        CancellationToken ct = default)
    {
        var import = outcome.CanonicalImport
            ?? throw new InvalidOperationException("Structured evidence persistence requires the canonical import graph.");
        if (import.Documents.Count == 0 || leads.Count == 0)
            throw new InvalidOperationException("Structured evidence persistence requires canonical inquiries and leads.");

        var source = await _context.Set<SourceDocument>()
            .Include(x => x.Corpus)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == job.BusinessUnitId
                                       && x.ContentHash == job.ContentHash, ct)
            ?? throw new InvalidOperationException(
                $"Extraction job {job.Id} has no authoritative source-document record.");
        if (_context.Database.IsNpgsql())
        {
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({"evidence-source:" + source.Id}, 0))", ct);
            await _context.Entry(source).ReloadAsync(ct);
            await _context.Entry(source).Reference(x => x.Corpus).LoadAsync(ct);
        }
        if (source.SecurityStatus != DocumentSecurityStatus.Cleared)
            throw new InvalidOperationException($"Source document {source.Id} has not passed security inspection.");

        var runId = DeterministicRunId(job);
        if (await _context.Set<ExtractionRun>().AnyAsync(
                x => x.BusinessUnitId == job.BusinessUnitId && x.RunId == runId, ct))
            return;

        // A content-addressed source can have many authorized receipt occurrences.
        // Its document lifecycle describes the immutable content, so a later run
        // must not rewind a source that was already completed or sent to review.
        var ownsSourceLifecycle = source.ProcessingStatus == DocumentProcessingStatus.Received;
        if (!ownsSourceLifecycle)
        {
            var priorRun = await _context.Set<ExtractionRun>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == job.BusinessUnitId
                            && x.SourceDocumentId == source.Id
                            && x.Status == ExtractionRunStatus.Completed)
                .OrderByDescending(x => x.CreatedOn)
                .FirstOrDefaultAsync(ct);
            if (priorRun is null)
                throw new InvalidOperationException(
                    $"Source document {source.Id} is terminal but has no reusable completed evidence run.");

            var replayRun = ExtractionRun.Create(job.BusinessUnitId, source.Id, runId, job.Id, job.Attempts,
                ParserVersion, import.Documents[0].SchemaVersion);
            replayRun.RecordCostStatus("LocalNoCharge", "NotRequired", 0m, "USD");
            replayRun.Start();
            replayRun.Complete(0, 0, 0, 0, 0, 0);
            _context.Add(replayRun);
            await _context.SaveChangesAsync(ct);
            return;
        }

        if (ownsSourceLifecycle)
            source.StartExtraction();
        var run = ExtractionRun.Create(job.BusinessUnitId, source.Id, runId, job.Id, job.Attempts,
            ParserVersion, import.Documents[0].SchemaVersion);
        run.RecordCostStatus("LocalNoCharge", "NotRequired", 0m, "USD");
        run.Start();
        _context.Add(run);
        await _context.SaveChangesAsync(ct);

        var pendingFields = new List<PendingField>();
        var inquiryByDocument = new Dictionary<CanonicalRfqDocument, CanonicalInquiry>();
        var lineByCanonical = new Dictionary<CanonicalRfqLineItem, CanonicalLineItem>();
        var leadItemOffsets = leads.ToDictionary(x => x.Id, _ => 0);
        var firstInquiryNumber = await ReserveInquiryNumbersAsync(
            source.CorpusId, import.Documents.Count, ct);

        for (var documentIndex = 0; documentIndex < import.Documents.Count; documentIndex++)
        {
            var document = import.Documents[documentIndex];
            var lead = leads.Count == import.Documents.Count ? leads[documentIndex] : leads[0];
            var leadItemOffset = leadItemOffsets[lead.Id];
            var inquiry = CanonicalInquiry.Create(
                job.BusinessUnitId, source.CorpusId, firstInquiryNumber + documentIndex);
            inquiry.PopulateHeader(document.RfqNo.Value, document.BuyerName.Value,
                ToOffset(document.ReceivedDate.Value), ToOffset(document.BidClosingDate.Value));
            inquiry.BindLead(lead.Id);
            if (document.ValidationStatus == CanonicalDtos.ValidationStatus.Valid)
                inquiry.Validate();
            else
                inquiry.RequireReview();
            _context.Add(inquiry);
            await _context.SaveChangesAsync(ct);
            inquiryByDocument[document] = inquiry;

            AddField(pendingFields, inquiry, null, "RfqNo", document.RfqNo);
            AddField(pendingFields, inquiry, null, "BuyerName", document.BuyerName);
            AddField(pendingFields, inquiry, null, "ReceivedDate", document.ReceivedDate);
            AddField(pendingFields, inquiry, null, "BidClosingDate", document.BidClosingDate);

            for (var lineIndex = 0; lineIndex < document.LineItems.Count; lineIndex++)
            {
                var canonical = document.LineItems[lineIndex];
                var description = canonical.ProductName.Value;
                if (string.IsNullOrWhiteSpace(description))
                    description = canonical.ProductName.OriginalValue ?? "[missing product description]";
                var line = CanonicalLineItem.Create(job.BusinessUnitId, inquiry.Id, lineIndex + 1,
                    description, canonical.Quantity.Value > 0 ? canonical.Quantity.Value : null);
                line.Enrich(canonical.ManufacturerName.Value, canonical.ManufacturerPartNumber.Value,
                    canonical.Currency.Value, ValueOrNull(canonical.UnitPrice), ValueOrNull(canonical.LeadTimeDays),
                    JsonSerializer.Serialize(canonical), MapLineStatus(canonical.ValidationStatus));
                var leadItems = lead.LeadItems.OrderBy(x => x.Id).ToArray();
                if (leadItemOffset + lineIndex < leadItems.Length)
                    line.BindLeadItem(leadItems[leadItemOffset + lineIndex].Id);
                _context.Add(line);
                await _context.SaveChangesAsync(ct);
                lineByCanonical[canonical] = line;

                AddField(pendingFields, null, line, "LineItemNo", canonical.LineItemNo);
                AddField(pendingFields, null, line, "ProductName", canonical.ProductName);
                AddField(pendingFields, null, line, "Quantity", canonical.Quantity);
                AddField(pendingFields, null, line, "UnitPrice", canonical.UnitPrice);
                AddField(pendingFields, null, line, "Currency", canonical.Currency);
                AddField(pendingFields, null, line, "ManufacturerName", canonical.ManufacturerName);
                AddField(pendingFields, null, line, "ManufacturerPartNumber", canonical.ManufacturerPartNumber);
                AddField(pendingFields, null, line, "LeadTimeDays", canonical.LeadTimeDays);
            }
            leadItemOffsets[lead.Id] += document.LineItems.Count;
        }

        var coordinates = pendingFields.Select(x => ParseCoordinate(x.Evidence.Location, job.FileType))
            .Distinct().OrderBy(x => x.Sheet, StringComparer.Ordinal).ThenBy(x => x.Row).ThenBy(x => x.Column)
            .ToArray();
        var pages = new Dictionary<string, DocumentPage>(StringComparer.Ordinal);
        var pageNumber = 0;
        foreach (var sheet in coordinates.GroupBy(x => x.Sheet, StringComparer.Ordinal))
        {
            var page = DocumentPage.Create(job.BusinessUnitId, source.Id, ++pageNumber,
                Math.Max(1, sheet.Max(x => x.Column)), Math.Max(1, sheet.Max(x => x.Row)),
                pageKind: string.Equals(job.FileType, "csv", StringComparison.OrdinalIgnoreCase)
                    ? DocumentPageKind.CsvSheet : DocumentPageKind.Worksheet,
                sheetName: sheet.Key);
            page.MarkOcrNotRequired();
            pages.Add(sheet.Key, page);
            _context.Add(page);
        }
        await _context.SaveChangesAsync(ct);

        var regions = new Dictionary<string, DocumentRegion>(StringComparer.Ordinal);
        foreach (var field in pendingFields)
        {
            if (regions.ContainsKey(field.Evidence.Location))
                continue;
            var coordinate = ParseCoordinate(field.Evidence.Location, job.FileType);
            var region = DocumentRegion.Create(job.BusinessUnitId, pages[coordinate.Sheet].Id,
                DocumentRegionType.TableCell, Math.Max(0, coordinate.Column - 1), Math.Max(0, coordinate.Row - 1),
                1, 1, field.Evidence.RawValue, field.Confidence,
                sourceAddress: field.Evidence.Location, rowNumber: coordinate.Row,
                columnNumber: coordinate.Column == 0 ? null : coordinate.Column);
            regions.Add(field.Evidence.Location, region);
            _context.Add(region);
        }
        await _context.SaveChangesAsync(ct);

        foreach (var field in pendingFields)
        {
            var regionId = regions[field.Evidence.Location].Id;
            FieldEvidence evidence = field.Inquiry is not null
                ? FieldEvidence.ForInquiry(job.BusinessUnitId, regionId, field.Inquiry.Id, field.FieldName,
                    field.Evidence.RawValue, field.NormalizedValue, field.Confidence, ParserVersion, runId,
                    valueKind: field.ValueKind, validationStatus: field.ValidationStatus,
                    transformationsJson: field.TransformationsJson)
                : FieldEvidence.ForLineItem(job.BusinessUnitId, regionId, field.LineItem!.Id, field.FieldName,
                    field.Evidence.RawValue, field.NormalizedValue, field.Confidence, ParserVersion, runId,
                    valueKind: field.ValueKind, validationStatus: field.ValidationStatus,
                    transformationsJson: field.TransformationsJson);
            _context.Add(evidence);
        }
        await _context.SaveChangesAsync(ct);

        var findingCount = 0;
        foreach (var document in import.Documents)
        {
            foreach (var issue in document.Issues)
            {
                var regionId = issue.Evidence is not null
                               && regions.TryGetValue(issue.Evidence.Location, out var issueRegion)
                    ? issueRegion.Id : (long?)null;
                var targetLine = issue.Evidence is null ? null : document.LineItems.FirstOrDefault(line =>
                    LineEvidence(line).Any(e => e.Location == issue.Evidence.Location));
                ValidationFinding finding = targetLine is not null
                    ? ValidationFinding.ForLineItem(job.BusinessUnitId, run.Id, lineByCanonical[targetLine].Id,
                        issue.Code, MapSeverity(issue.Severity), issue.Message, regionId)
                    : ValidationFinding.ForInquiry(job.BusinessUnitId, run.Id, inquiryByDocument[document].Id,
                        issue.Code, MapSeverity(issue.Severity), issue.Message, regionId);
                _context.Add(finding);
                findingCount++;
            }
        }

        if (ownsSourceLifecycle)
            source.StartNormalization();
        var requiresReview = outcome.Status != ExtractionOutcomeStatus.Ok
                             || import.Documents.Any(x => x.ValidationStatus != CanonicalDtos.ValidationStatus.Valid);
        if (requiresReview)
        {
            if (ownsSourceLifecycle)
                source.RequireReview(pages.Count);
            if (source.Corpus.Status == CorpusStatus.Processing)
                source.Corpus.RequireReview();
        }
        else
        {
            if (ownsSourceLifecycle)
                source.Complete(pages.Count);
            if (source.Corpus.Status == CorpusStatus.Processing)
                source.Corpus.Complete();
        }
        run.Complete(pages.Count, regions.Count, import.Documents.Count,
            import.Documents.Sum(x => x.LineItems.Count), pendingFields.Count, findingCount);
        await _context.SaveChangesAsync(ct);
    }

    private async Task<int> ReserveInquiryNumbersAsync(
        long corpusId,
        int inquiryCount,
        CancellationToken ct)
    {
        if (inquiryCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(inquiryCount));

        // Extraction persistence already runs in a transaction. Serializing on the
        // corpus keeps independent workers from allocating the same inquiry range.
        if (_context.Database.IsNpgsql())
            await _context.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock({corpusId})", ct);

        var currentMaximum = await _context.Set<CanonicalInquiry>()
            .Where(x => x.CorpusId == corpusId)
            .Select(x => (int?)x.InquiryNumber)
            .MaxAsync(ct) ?? 0;
        return checked(currentMaximum + 1);
    }

    private static IEnumerable<SourceEvidence> LineEvidence(CanonicalRfqLineItem line) =>
        line.LineItemNo.Evidence
            .Concat(line.ProductName.Evidence)
            .Concat(line.Quantity.Evidence)
            .Concat(line.UnitPrice.Evidence)
            .Concat(line.Currency.Evidence)
            .Concat(line.ManufacturerName.Evidence)
            .Concat(line.ManufacturerPartNumber.Evidence)
            .Concat(line.LeadTimeDays.Evidence);

    private static void AddField<T>(List<PendingField> fields, CanonicalInquiry? inquiry,
        CanonicalLineItem? line, string name, CanonicalValue<T> value)
    {
        foreach (var evidence in value.Evidence)
        {
            fields.Add(new PendingField(inquiry, line, name, evidence, Format(value.Value), value.Confidence,
                MapKind(typeof(T)), MapFieldStatus(value.ValidationStatus),
                JsonSerializer.Serialize(value.Transformations)));
        }
    }

    private static decimal? ValueOrNull(CanonicalValue<decimal> value) =>
        value.Confidence == 0 && value.Value == 0 ? null : value.Value;

    private static int? ValueOrNull(CanonicalValue<int> value) =>
        value.Confidence == 0 && value.Value == 0 ? null : value.Value;

    private static string? Format<T>(T? value) => value switch
    {
        null => null,
        DateTime date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString()
    };

    private static DateTimeOffset? ToOffset(DateTime? value) => value is null
        ? null
        : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));

    private static Guid DeterministicRunId(ExtractionJob job)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{job.BusinessUnitId}|{job.Id}|{job.Attempts}|{job.ContentHash}|{ParserVersion}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static Coordinate ParseCoordinate(string location, string? fileType)
    {
        var match = CellAddress.Match(location);
        if (match.Success)
            return new Coordinate(match.Groups["sheet"].Value.Replace("''", "'", StringComparison.Ordinal),
                int.Parse(match.Groups["row"].Value, CultureInfo.InvariantCulture),
                ColumnNumber(match.Groups["column"].Value));
        match = LegacyAddress.Match(location);
        if (match.Success)
            return new Coordinate(string.Equals(fileType, "csv", StringComparison.OrdinalIgnoreCase) ? "CSV" : "Worksheet 1",
                int.Parse(match.Groups["row"].Value, CultureInfo.InvariantCulture),
                match.Groups["column"].Success ? ColumnNumber(match.Groups["column"].Value) : 1);
        throw new InvalidDataException($"Unsupported structured evidence address '{location}'.");
    }

    private static int ColumnNumber(string letters)
    {
        var result = 0;
        foreach (var letter in letters.ToUpperInvariant())
            result = checked(result * 26 + letter - 'A' + 1);
        return result;
    }

    private static FieldValueKind MapKind(Type type) => type == typeof(DateTime)
        ? FieldValueKind.Date
        : type == typeof(int) || type == typeof(decimal) ? FieldValueKind.Number : FieldValueKind.Text;

    private static FieldValidationStatus MapFieldStatus(CanonicalDtos.ValidationStatus status) => status switch
    {
        CanonicalDtos.ValidationStatus.Valid => FieldValidationStatus.Valid,
        CanonicalDtos.ValidationStatus.NeedsReview => FieldValidationStatus.Warning,
        CanonicalDtos.ValidationStatus.Invalid => FieldValidationStatus.Invalid,
        _ => FieldValidationStatus.Unvalidated
    };

    private static CanonicalValidationStatus MapLineStatus(CanonicalDtos.ValidationStatus status) => status switch
    {
        CanonicalDtos.ValidationStatus.Valid => CanonicalValidationStatus.Valid,
        CanonicalDtos.ValidationStatus.NeedsReview => CanonicalValidationStatus.Warning,
        CanonicalDtos.ValidationStatus.Invalid => CanonicalValidationStatus.Invalid,
        _ => CanonicalValidationStatus.Unvalidated
    };

    private static LedgerSeverity MapSeverity(CanonicalDtos.ValidationSeverity severity) => severity switch
    {
        CanonicalDtos.ValidationSeverity.Error => LedgerSeverity.Error,
        CanonicalDtos.ValidationSeverity.Warning => LedgerSeverity.Warning,
        _ => LedgerSeverity.Information
    };

    private sealed record PendingField(
        CanonicalInquiry? Inquiry,
        CanonicalLineItem? LineItem,
        string FieldName,
        SourceEvidence Evidence,
        string? NormalizedValue,
        decimal Confidence,
        FieldValueKind ValueKind,
        FieldValidationStatus ValidationStatus,
        string TransformationsJson);

    private sealed record Coordinate(string Sheet, int Row, int Column);
}
