using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

public interface ICanonicalRfqNormalizer
{
    CanonicalRfqImportResult NormalizeSpreadsheetRows(IEnumerable<RfqSpreadsheetRow> rows, long businessUnitId);
}

public sealed class CanonicalRfqNormalizer : ICanonicalRfqNormalizer
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "dd/MM/yyyy",
        "MM/dd/yyyy",
        "dd-MM-yyyy",
        "d/M/yyyy",
        "yyyy/MM/dd"
    };

    public CanonicalRfqImportResult NormalizeSpreadsheetRows(IEnumerable<RfqSpreadsheetRow> rows, long businessUnitId)
    {
        var result = new CanonicalRfqImportResult();
        var materialRows = rows
            .Where(r => HasAnyValue(r.RfqNo, r.BuyerName, r.ProductName, r.Quantity, r.UnitPrice, r.Currency))
            .ToList();

        var duplicateKeys = materialRows
            .GroupBy(r => BuildLineKey(r))
            .Where(g => !string.IsNullOrWhiteSpace(g.Key) && g.Count() > 1)
            .ToDictionary(g => g.Key, g => g.Select(r => r.RowNumber).ToList());

        foreach (var group in materialRows.GroupBy(r => BuildRfqKey(r)))
        {
            var first = group.First();
            var document = new CanonicalRfqDocument
            {
                BusinessUnitId = businessUnitId,
                RfqNo = RequiredText(first.RfqNo, first, RfqSpreadsheetFields.RfqNo, "RFQ_NO", "RFQ number is required."),
                BuyerName = RequiredText(first.BuyerName, first, RfqSpreadsheetFields.BuyerName, "BUYER_NAME", "Buyer name is required."),
                ReceivedDate = DateValue(first.ReceivedDate, first, RfqSpreadsheetFields.ReceivedDate, false, "RECEIVED_DATE"),
                BidClosingDate = DateValue(first.BidClosingDate, first, RfqSpreadsheetFields.BidClosingDate, true, "BID_CLOSING_DATE")
            };

            foreach (var row in group)
            {
                var line = new CanonicalRfqLineItem
                {
                    LineItemNo = TextValue(row.RowNumber.ToString(CultureInfo.InvariantCulture), row, "row", CanonicalValueKind.Derived, 1.0m),
                    ProductName = RequiredText(row.ProductName, row, RfqSpreadsheetFields.ProductName, "PRODUCT_NAME", "Product name is required."),
                    Quantity = IntValue(row.Quantity, row, RfqSpreadsheetFields.Quantity, false, "QUANTITY"),
                    UnitPrice = DecimalValue(row.UnitPrice, row, RfqSpreadsheetFields.UnitPrice, true, "UNIT_PRICE"),
                    Currency = TextValue(row.Currency, row, RfqSpreadsheetFields.Currency),
                    ManufacturerName = TextValue(row.ManufacturerName, row, RfqSpreadsheetFields.ManufacturerName),
                    ManufacturerPartNumber = TextValue(row.ManufacturerPartNumber, row, RfqSpreadsheetFields.ManufacturerPartNumber),
                    LeadTimeDays = IntValue(row.LeadTimeDays, row, RfqSpreadsheetFields.LeadTimeDays, true, "LEAD_TIME_DAYS")
                };

                var lineKey = BuildLineKey(row);
                if (duplicateKeys.TryGetValue(lineKey, out var duplicateRows))
                {
                    document.Issues.Add(Issue(
                        ValidationSeverity.Warning,
                        "DUPLICATE_LINE",
                        $"Potential duplicate RFQ line across rows {string.Join(", ", duplicateRows)}.",
                        Evidence(row, "row", lineKey)));
                }

                line.ValidationStatus = HasInvalid(line.ProductName, line.Quantity, line.UnitPrice, line.LeadTimeDays)
                    ? ValidationStatus.Invalid
                    : HasNeedsReview(line.UnitPrice, line.Currency, line.ManufacturerName, line.ManufacturerPartNumber, line.LeadTimeDays)
                        ? ValidationStatus.NeedsReview
                        : ValidationStatus.Valid;

                document.LineItems.Add(line);
            }

            document.Issues.AddRange(CollectValueIssues(document, first));

            if (document.LineItems.Count == 0)
            {
                document.Issues.Add(Issue(
                    ValidationSeverity.Error,
                    "NO_LINE_ITEMS",
                    "RFQ must contain at least one material line item.",
                    Evidence(first, "row", null)));
            }

            document.ValidationStatus = document.Issues.Any(i => i.Severity == ValidationSeverity.Error)
                ? ValidationStatus.Invalid
                : document.Issues.Any(i => i.Severity == ValidationSeverity.Warning) ||
                  document.LineItems.Any(i => i.ValidationStatus == ValidationStatus.NeedsReview)
                    ? ValidationStatus.NeedsReview
                    : ValidationStatus.Valid;

            result.Documents.Add(document);
            result.Issues.AddRange(document.Issues);
        }

        return result;
    }

    private static IEnumerable<CanonicalValidationIssue> CollectValueIssues(CanonicalRfqDocument document, RfqSpreadsheetRow row)
    {
        var values = new (string Code, string Message, SourceEvidence? Evidence, ValidationStatus Status)[]
        {
            ("RFQ_NO", "RFQ number is missing or invalid.", document.RfqNo.Evidence.FirstOrDefault(), document.RfqNo.ValidationStatus),
            ("BUYER_NAME", "Buyer name is missing or invalid.", document.BuyerName.Evidence.FirstOrDefault(), document.BuyerName.ValidationStatus),
            ("RECEIVED_DATE", "Received date is missing or invalid.", document.ReceivedDate.Evidence.FirstOrDefault(), document.ReceivedDate.ValidationStatus),
            ("BID_CLOSING_DATE", "Bid closing date needs review.", document.BidClosingDate.Evidence.FirstOrDefault(), document.BidClosingDate.ValidationStatus)
        };

        foreach (var value in values)
        {
            if (value.Status == ValidationStatus.Invalid)
                yield return Issue(ValidationSeverity.Error, value.Code, value.Message, value.Evidence ?? Evidence(row, "row", null));
            else if (value.Status == ValidationStatus.NeedsReview)
                yield return Issue(ValidationSeverity.Warning, value.Code, value.Message, value.Evidence ?? Evidence(row, "row", null));
        }

        foreach (var line in document.LineItems)
        {
            foreach (var field in new[]
            {
                ("PRODUCT_NAME", "Product name is missing or invalid.", line.ProductName.ValidationStatus, line.ProductName.Evidence.FirstOrDefault()),
                ("QUANTITY", "Quantity must be a positive whole number.", line.Quantity.ValidationStatus, line.Quantity.Evidence.FirstOrDefault()),
                ("UNIT_PRICE", "Unit price must be zero or greater when supplied.", line.UnitPrice.ValidationStatus, line.UnitPrice.Evidence.FirstOrDefault()),
                ("LEAD_TIME_DAYS", "Lead time must be zero or greater when supplied.", line.LeadTimeDays.ValidationStatus, line.LeadTimeDays.Evidence.FirstOrDefault())
            })
            {
                if (field.ValidationStatus == ValidationStatus.Invalid)
                    yield return Issue(ValidationSeverity.Error, field.Item1, field.Item2, field.Item4);
            }
        }
    }

    private static CanonicalValue<string> RequiredText(string? raw, RfqSpreadsheetRow row, string column, string code, string message)
    {
        var value = TextValue(raw, row, column);
        if (!string.IsNullOrWhiteSpace(value.Value)) return value;

        value.Kind = CanonicalValueKind.Missing;
        value.ValidationStatus = ValidationStatus.Invalid;
        value.Transformations.Add($"{code}: {message}");
        return value;
    }

    private static CanonicalValue<string> TextValue(string? raw, RfqSpreadsheetRow row, string column, CanonicalValueKind kind = CanonicalValueKind.Extracted, decimal confidence = 1.0m)
    {
        var trimmed = raw?.Trim();
        return new CanonicalValue<string>
        {
            OriginalValue = raw,
            Value = string.IsNullOrWhiteSpace(trimmed) ? null : trimmed,
            Kind = string.IsNullOrWhiteSpace(trimmed) ? CanonicalValueKind.Missing : kind,
            Confidence = string.IsNullOrWhiteSpace(trimmed) ? 0m : confidence,
            ValidationStatus = string.IsNullOrWhiteSpace(trimmed) ? ValidationStatus.NeedsReview : ValidationStatus.Valid,
            Evidence = new List<SourceEvidence> { Evidence(row, column, raw) },
            Transformations = string.IsNullOrWhiteSpace(trimmed) ? new List<string>() : new List<string> { "trim" }
        };
    }

    private static CanonicalValue<DateTime> DateValue(string? raw, RfqSpreadsheetRow row, string column, bool optional, string fieldName)
    {
        var value = new CanonicalValue<DateTime>
        {
            OriginalValue = raw,
            Evidence = new List<SourceEvidence> { Evidence(row, column, raw) }
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            value.Kind = CanonicalValueKind.Missing;
            value.Confidence = 0m;
            value.ValidationStatus = optional ? ValidationStatus.NeedsReview : ValidationStatus.Invalid;
            value.Transformations.Add(optional ? $"{fieldName}: missing optional date" : $"{fieldName}: missing required date");
            return value;
        }

        if (DateTime.TryParseExact(raw.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value.Value = DateTime.SpecifyKind(parsed.Date, DateTimeKind.Utc);
            value.Kind = CanonicalValueKind.Normalized;
            value.Confidence = 1.0m;
            value.ValidationStatus = ValidationStatus.Valid;
            value.Transformations.Add("parse_exact_date");
            return value;
        }

        value.Kind = CanonicalValueKind.Extracted;
        value.Confidence = 0.2m;
        value.ValidationStatus = ValidationStatus.Invalid;
        value.Transformations.Add($"{fieldName}: unsupported date format");
        return value;
    }

    private static CanonicalValue<int> IntValue(string? raw, RfqSpreadsheetRow row, string column, bool optional, string fieldName)
    {
        var value = new CanonicalValue<int>
        {
            OriginalValue = raw,
            Evidence = new List<SourceEvidence> { Evidence(row, column, raw) }
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            value.Kind = CanonicalValueKind.Missing;
            value.Confidence = 0m;
            value.ValidationStatus = optional ? ValidationStatus.NeedsReview : ValidationStatus.Invalid;
            value.Transformations.Add(optional ? $"{fieldName}: missing optional number" : $"{fieldName}: missing required number");
            return value;
        }

        if (int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && (optional ? parsed >= 0 : parsed > 0))
        {
            value.Value = parsed;
            value.Kind = CanonicalValueKind.Normalized;
            value.Confidence = 1.0m;
            value.ValidationStatus = ValidationStatus.Valid;
            value.Transformations.Add("parse_integer");
            return value;
        }

        value.Kind = CanonicalValueKind.Extracted;
        value.Confidence = 0.2m;
        value.ValidationStatus = ValidationStatus.Invalid;
        value.Transformations.Add(optional ? $"{fieldName}: not a non-negative integer" : $"{fieldName}: not a positive integer");
        return value;
    }

    private static CanonicalValue<decimal> DecimalValue(string? raw, RfqSpreadsheetRow row, string column, bool optional, string fieldName)
    {
        var value = new CanonicalValue<decimal>
        {
            OriginalValue = raw,
            Evidence = new List<SourceEvidence> { Evidence(row, column, raw) }
        };

        if (string.IsNullOrWhiteSpace(raw))
        {
            value.Kind = CanonicalValueKind.Missing;
            value.Confidence = 0m;
            value.ValidationStatus = optional ? ValidationStatus.NeedsReview : ValidationStatus.Invalid;
            value.Transformations.Add(optional ? $"{fieldName}: missing optional amount" : $"{fieldName}: missing required amount");
            return value;
        }

        if (decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
        {
            value.Value = parsed;
            value.Kind = CanonicalValueKind.Normalized;
            value.Confidence = 1.0m;
            value.ValidationStatus = ValidationStatus.Valid;
            value.Transformations.Add("parse_decimal");
            return value;
        }

        value.Kind = CanonicalValueKind.Extracted;
        value.Confidence = 0.2m;
        value.ValidationStatus = ValidationStatus.Invalid;
        value.Transformations.Add($"{fieldName}: not a non-negative decimal");
        return value;
    }

    private static SourceEvidence Evidence(RfqSpreadsheetRow row, string column, string? rawValue)
    {
        var location = row.SourceAddress(column, LegacyColumn(column));
        return new SourceEvidence
        {
            SourceDocumentName = row.SourceDocumentName,
            Location = location,
            RawValue = rawValue
        };
    }

    private static string LegacyColumn(string fieldName) => fieldName switch
    {
        RfqSpreadsheetFields.RfqNo => "A",
        RfqSpreadsheetFields.BuyerName => "B",
        RfqSpreadsheetFields.ReceivedDate => "C",
        RfqSpreadsheetFields.BidClosingDate => "D",
        RfqSpreadsheetFields.ProductName => "E",
        RfqSpreadsheetFields.Quantity => "F",
        RfqSpreadsheetFields.UnitPrice => "G",
        RfqSpreadsheetFields.Currency => "H",
        RfqSpreadsheetFields.ManufacturerName => "I",
        RfqSpreadsheetFields.ManufacturerPartNumber => "J",
        RfqSpreadsheetFields.LeadTimeDays => "K",
        _ => "row"
    };

    private static CanonicalValidationIssue Issue(ValidationSeverity severity, string code, string message, SourceEvidence? evidence)
    {
        return new CanonicalValidationIssue
        {
            Severity = severity,
            Code = code,
            Message = message,
            Evidence = evidence
        };
    }

    private static string BuildRfqKey(RfqSpreadsheetRow row)
    {
        var rfqNo = (row.RfqNo ?? "").Trim().ToLowerInvariant();
        var buyer = (row.BuyerName ?? "").Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(rfqNo) && string.IsNullOrWhiteSpace(buyer)
            ? $"row:{row.RowNumber}"
            : $"{rfqNo}|{buyer}";
    }

    private static string BuildLineKey(RfqSpreadsheetRow row)
    {
        return string.Join("|", new[]
        {
            row.RfqNo,
            row.BuyerName,
            row.ProductName,
            row.Quantity,
            row.ManufacturerPartNumber
        }.Select(v => (v ?? "").Trim().ToLowerInvariant()));
    }

    private static bool HasAnyValue(params string?[] values) => values.Any(v => !string.IsNullOrWhiteSpace(v));

    private static bool HasInvalid(params object[] values)
    {
        return values.Any(v => v switch
        {
            CanonicalValue<string> text => text.ValidationStatus == ValidationStatus.Invalid,
            CanonicalValue<int> number => number.ValidationStatus == ValidationStatus.Invalid,
            CanonicalValue<decimal> amount => amount.ValidationStatus == ValidationStatus.Invalid,
            _ => false
        });
    }

    private static bool HasNeedsReview(params object[] values)
    {
        return values.Any(v => v switch
        {
            CanonicalValue<string> text => text.ValidationStatus == ValidationStatus.NeedsReview,
            CanonicalValue<int> number => number.ValidationStatus == ValidationStatus.NeedsReview,
            CanonicalValue<decimal> amount => amount.ValidationStatus == ValidationStatus.NeedsReview,
            _ => false
        });
    }
}
