using System.Globalization;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.SupplierQuotes;

public sealed record SupplierQuoteDocumentIntakeCommand(
    long BusinessUnitId,
    long SupplierId,
    long SupplierSolicitationId,
    long SourcingCaseId,
    string NexoraSerial,
    string SupplierQuoteReference,
    int RevisionNumber,
    long CurrencyId,
    DateTime? ValidUntil,
    string? Incoterms,
    string? PaymentTerms,
    string? Notes,
    string IdempotencyKey,
    string Actor,
    string CorrelationId);

public sealed record SupplierQuoteDocumentIntakeResult(
    long SourceDocumentId,
    long SourceDocumentOccurrenceId,
    long ExtractionJobId,
    Guid ClassificationId,
    string ClassificationStatus,
    long? SupplierQuoteId,
    long? RevisionId,
    string ProjectionStatus);

public sealed class SupplierQuoteDocumentIntakeService(
    ErpRfqAutomationContext context,
    IDocumentIngestion ingestion,
    CommercialDocumentClassificationService classification,
    SupplierQuoteInboxService inbox)
{
    private static readonly HashSet<string> AcceptedExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".csv", ".xlsx", ".pdf", ".doc", ".docx", ".png", ".jpg", ".jpeg", ".tif", ".tiff" };
    private static readonly HashSet<string> DeterministicExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".csv", ".xlsx" };
    private const int MaxBytes = 25 * 1024 * 1024;

    public async Task<SupplierQuoteDocumentIntakeResult> IngestAsync(
        SupplierQuoteDocumentIntakeCommand command,
        Stream content,
        string fileName,
        long declaredLength,
        CancellationToken cancellationToken = default)
    {
        if (command.BusinessUnitId <= 0 || context.ScopedTenantId != command.BusinessUnitId)
            throw new UnauthorizedAccessException("A matching authenticated tenant is required.");
        var safeName = Path.GetFileName(fileName ?? string.Empty);
        var extension = Path.GetExtension(safeName);
        if (string.IsNullOrWhiteSpace(safeName) || !AcceptedExtensions.Contains(extension))
            throw new SupplierQuoteValidationException("The Supplier Quote file type is not supported.");
        if (declaredLength is <= 0 or > MaxBytes)
            throw new SupplierQuoteValidationException("Supplier Quote files must be between 1 byte and 25 MB.");

        await using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length is <= 0 or > MaxBytes || buffer.Length != declaredLength)
            throw new SupplierQuoteValidationException("The Supplier Quote file length is invalid.");
        var bytes = buffer.ToArray();

        var ingested = await ingestion.IngestAsync(bytes, safeName, command.BusinessUnitId,
            ExtractionSourceType.ManualUpload, priority: 20,
            metadata: new ExtractionJobMetadata
            {
                SourceOccurrenceId = command.IdempotencyKey,
                LogicalGroupKey = $"supplier-rfq:{command.SupplierSolicitationId}",
                LeadSource = "Supplier Quote Inbox",
                EmailSource = extension.TrimStart('.').ToUpperInvariant(),
                CommercialDocumentTypeHint = command.RevisionNumber > 1
                    ? "SUPPLIER_QUOTE_REVISION"
                    : "SUPPLIER_QUOTE"
            }, ct: cancellationToken);

        var sourceDocumentId = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
            .Where(x => x.BusinessUnitId == command.BusinessUnitId && x.Id == ingested.SourceDocumentOccurrenceId)
            .Select(x => x.SourceDocumentId)
            .SingleAsync(cancellationToken);

        var classified = await classification.ClassifyAsync(command.BusinessUnitId,
            new ClassifyCommercialDocumentRequest(sourceDocumentId,
                $"supplier-quote-classification:{command.IdempotencyKey}",
                new CommercialDocumentClassificationSignals(safeName,
                    Subject: command.SupplierQuoteReference,
                    SenderPartyType: "SUPPLIER",
                    BodyExcerpt: "Supplier quotation response",
                    ReferenceKind: command.RevisionNumber > 1 ? "SUPPLIER_QUOTE_REVISION" : "SUPPLIER_QUOTE"),
                new CommercialDocumentMatchReferences(SupplierRfqId: command.SupplierSolicitationId,
                    SourcingCaseId: command.SourcingCaseId)),
            cancellationToken);

        SupplierQuoteCaptureResult? projected = null;
        if (DeterministicExtensions.Contains(extension))
        {
            var lines = await ParseStructuredLinesAsync(command, bytes, safeName, extension, cancellationToken);
            projected = await inbox.CaptureAsync(new CaptureSupplierQuoteCommand(
                command.BusinessUnitId, command.SupplierId, command.SupplierSolicitationId,
                command.SourcingCaseId, command.NexoraSerial, command.SupplierQuoteReference,
                command.RevisionNumber, SupplierQuoteCaptureChannels.Upload, sourceDocumentId,
                $"source-document:{sourceDocumentId}", ingested.ContentHash, command.CurrencyId,
                command.ValidUntil, command.Incoterms, 0, 0, command.PaymentTerms, command.Notes,
                lines, [], command.IdempotencyKey, command.Actor, command.CorrelationId), cancellationToken);
            classified = await classification.LinkSupplierQuoteAsync(command.BusinessUnitId,
                classified.Id, classified.Version, projected.SupplierQuoteId, cancellationToken);
        }

        return new SupplierQuoteDocumentIntakeResult(sourceDocumentId,
            ingested.SourceDocumentOccurrenceId, ingested.JobId, classified.Id,
            classified.ReviewStatus.ToString(), projected?.SupplierQuoteId, projected?.RevisionId,
            projected is null ? "REVIEW_REQUIRED" : projected.InboxStatus);
    }

    private async Task<IReadOnlyCollection<CaptureSupplierQuoteLine>> ParseStructuredLinesAsync(
        SupplierQuoteDocumentIntakeCommand command, byte[] bytes, string fileName, string extension,
        CancellationToken cancellationToken)
    {
        var parser = new NativeSpreadsheetParser();
        var rows = extension.Equals(".csv", StringComparison.OrdinalIgnoreCase)
            ? parser.ParseCsv(bytes, fileName)
            : parser.ParseXlsx(bytes, fileName);
        if (rows.Count == 0)
            throw new SupplierQuoteValidationException("The Supplier Quote spreadsheet has no extractable lines.");

        var solicitation = await context.Set<SupplierSolicitation>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == command.BusinessUnitId && x.Id == command.SupplierSolicitationId &&
            x.SupplierId == command.SupplierId && x.SourcingCaseId == command.SourcingCaseId,
            cancellationToken) ?? throw new SupplierQuoteValidationException(
            "The Supplier RFQ does not match this tenant, Supplier, and Sourcing Case.");
        var lineAnchors = await (from item in context.Set<Rfqitem>().AsNoTracking()
            join demand in context.Set<CommercialDemandLine>().AsNoTracking()
                on new { BusinessUnitId = command.BusinessUnitId, RfqItemId = item.Id }
                equals new { demand.BusinessUnitId, demand.RfqItemId }
            where item.Rfqid == solicitation.RfqId
            select new { Item = item, DemandLineId = demand.Id }).ToListAsync(cancellationToken);

        var result = new List<CaptureSupplierQuoteLine>();
        foreach (var row in rows)
        {
            var part = Normalize(row.ManufacturerPartNumber);
            var matches = lineAnchors.Where(x => part is not null &&
                (Normalize(x.Item.ManufacturerPartNumber) == part || Normalize(x.Item.ItemMaterialCode) == part))
                .ToArray();
            if (matches.Length != 1)
                throw new SupplierQuoteValidationException(
                    $"Spreadsheet row {row.RowNumber} does not uniquely match a Customer RFQ line.");
            if (!TryPositiveDecimal(row.Quantity, out var quantity) ||
                !TryNonNegativeDecimal(row.UnitPrice, out var unitPrice))
                throw new SupplierQuoteValidationException(
                    $"Spreadsheet row {row.RowNumber} has an invalid quantity or unit price.");
            int? leadTime = null;
            if (!string.IsNullOrWhiteSpace(row.LeadTimeDays) &&
                (!int.TryParse(row.LeadTimeDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLead) || parsedLead < 0))
                throw new SupplierQuoteValidationException($"Spreadsheet row {row.RowNumber} has an invalid lead time.");
            else if (int.TryParse(row.LeadTimeDays, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validLead))
                leadTime = validLead;

            var anchor = matches[0];
            var evidence = new[]
            {
                Evidence("Quantity", row.Quantity, quantity.ToString(CultureInfo.InvariantCulture), row, RfqSpreadsheetFields.Quantity),
                Evidence("UnitPrice", row.UnitPrice, unitPrice.ToString(CultureInfo.InvariantCulture), row, RfqSpreadsheetFields.UnitPrice),
                Evidence("PartNumber", row.ManufacturerPartNumber, part, row, RfqSpreadsheetFields.ManufacturerPartNumber),
                Evidence("LeadTimeDays", row.LeadTimeDays, leadTime?.ToString(CultureInfo.InvariantCulture), row, RfqSpreadsheetFields.LeadTimeDays)
            };
            result.Add(new CaptureSupplierQuoteLine(result.Count + 1, anchor.Item.Id, anchor.DemandLineId,
                row.ManufacturerPartNumber, row.ManufacturerName, null,
                row.ProductName ?? anchor.Item.ProductShortDescription ?? anchor.Item.ProductShortName ?? part!,
                quantity, quantity, anchor.Item.UnitOfMeasure ?? "EA", unitPrice, null, leadTime,
                "QUOTED", null, null, false, null, evidence));
        }
        return result;
    }

    private static CaptureSupplierQuoteEvidence Evidence(string field, string? original, string? normalized,
        RfqSpreadsheetRow row, string spreadsheetField) => new(field, original, normalized, 1m,
        "NATIVE_SPREADSHEET", "NativeSpreadsheetParser/v1", null,
        row.SourceAddress(spreadsheetField, "row"), true);
    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? null : new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
    private static bool TryPositiveDecimal(string? value, out decimal parsed) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed) && parsed > 0;
    private static bool TryNonNegativeDecimal(string? value, out decimal parsed) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out parsed) && parsed >= 0;
}
