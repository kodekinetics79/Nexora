using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ERP_RFQ_Automation.DTOs.DocumentIntelligence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Extraction.Quantities;

namespace ERP_RFQ_Automation.Services.DocumentIntelligence;

public interface ICanonicalRfqNormalizer
{
    CanonicalRfqImportResult NormalizeSpreadsheetRows(IEnumerable<RfqSpreadsheetRow> rows, long businessUnitId);
}

public sealed class CanonicalRfqNormalizer : ICanonicalRfqNormalizer
{
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
                BidClosingDate = DateValue(first.BidClosingDate, first, RfqSpreadsheetFields.BidClosingDate, true, "BID_CLOSING_DATE"),
                DeliveryLocation = TextValue(first.DeliveryLocation, first, RfqSpreadsheetFields.DeliveryLocation),
                RequiredDeliveryDate = DateValue(first.RequiredDeliveryDate, first, RfqSpreadsheetFields.RequiredDeliveryDate, true, "REQUIRED_DELIVERY_DATE"),
                AgreementReference = TextValue(first.AgreementReference, first, RfqSpreadsheetFields.AgreementReference)
            };

            // What THIS document states, decided from the document's own rows before any
            // line is judged. See CanonicalValue.StatedInDocument for why the review signal
            // is worthless without it.
            var stated = StatedFields(group);
            MarkHeaderExpectations(document, stated);

            var lineOrdinal = 0;
            foreach (var row in group)
            {
                // The line's own number, not its physical row. A Word or Excel table puts its
                // header on a real row, so numbering by row started every document's first line
                // at 2. The source ADDRESS still points at the physical cell for evidence.
                lineOrdinal++;
                var line = new CanonicalRfqLineItem
                {
                    LineItemNo = TextValue(lineOrdinal.ToString(CultureInfo.InvariantCulture), row, "row", CanonicalValueKind.Derived, 1.0m),
                    ProductName = RequiredText(row.ProductName, row, RfqSpreadsheetFields.ProductName, "PRODUCT_NAME", "Product name is required."),
                    Quantity = QuantityValue(row.Quantity, row, RfqSpreadsheetFields.Quantity, "QUANTITY"),
                    UnitOfMeasure = TextValue(row.UnitOfMeasure, row, RfqSpreadsheetFields.UnitOfMeasure),
                    UnitPrice = DecimalValue(row.UnitPrice, row, RfqSpreadsheetFields.UnitPrice, true, "UNIT_PRICE"),
                    Currency = TextValue(row.Currency, row, RfqSpreadsheetFields.Currency),
                    ManufacturerName = TextValue(row.ManufacturerName, row, RfqSpreadsheetFields.ManufacturerName),
                    ManufacturerPartNumber = TextValue(row.ManufacturerPartNumber, row, RfqSpreadsheetFields.ManufacturerPartNumber),
                    LeadTimeDays = IntValue(row.LeadTimeDays, row, RfqSpreadsheetFields.LeadTimeDays, true, "LEAD_TIME_DAYS"),
                    ItemText = TextValue(row.ItemText, row, RfqSpreadsheetFields.ItemText)
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

                // Expectation BEFORE judgement. A field this document states nowhere is not a
                // gap in the reading of it, so it is resolved to Valid here and drops out of
                // the confidence average; a field the document DOES state and this line omits
                // is left alone and still flags below.
                MarkLineExpectations(line, stated);

                line.ValidationStatus = HasInvalid(line.ProductName, line.Quantity, line.UnitPrice, line.LeadTimeDays)
                    ? ValidationStatus.Invalid
                    : HasNeedsReview(line.UnitOfMeasure, line.UnitPrice, line.Currency, line.ManufacturerName, line.ManufacturerPartNumber, line.LeadTimeDays)
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
            // An ambiguous date is READ, not missing — so it must not be reported as missing. The
            // reviewer is told which token is ambiguous and which way it was read, exactly as the
            // customer-purchase-order path already does.
            ("RECEIVED_DATE",
                IsAmbiguous(document.ReceivedDate)
                    ? AmbiguityMessage(document.ReceivedDate, "received date")
                    : "Received date is missing or invalid.",
                document.ReceivedDate.Evidence.FirstOrDefault(), document.ReceivedDate.ValidationStatus),
            ("BID_CLOSING_DATE",
                IsAmbiguous(document.BidClosingDate)
                    ? AmbiguityMessage(document.BidClosingDate, "bid closing date")
                    : "Bid closing date needs review.",
                document.BidClosingDate.Evidence.FirstOrDefault(), document.BidClosingDate.ValidationStatus)
        };

        foreach (var value in values)
        {
            if (value.Status == ValidationStatus.Invalid)
                yield return Issue(ValidationSeverity.Error, value.Code, value.Message, value.Evidence ?? Evidence(row, "row", null));
            else if (value.Status == ValidationStatus.NeedsReview)
                yield return Issue(ValidationSeverity.Warning, value.Code, value.Message, value.Evidence ?? Evidence(row, "row", null));
        }

        // The requested delivery date reports ONLY its ambiguity. It is optional, so "missing"
        // is a normal reading and warning on it would flag every document that omits one; an
        // ambiguous one, by contrast, is a value that is stored and may be wrong.
        if (IsAmbiguous(document.RequiredDeliveryDate))
        {
            yield return Issue(
                ValidationSeverity.Warning,
                "REQUIRED_DELIVERY_DATE",
                AmbiguityMessage(document.RequiredDeliveryDate, "requested delivery date"),
                document.RequiredDeliveryDate.Evidence.FirstOrDefault() ?? Evidence(row, "row", null));
        }

        foreach (var line in document.LineItems)
        {
            foreach (var field in new[]
            {
                ("PRODUCT_NAME", "Product name is missing or invalid.", line.ProductName.ValidationStatus, line.ProductName.Evidence.FirstOrDefault()),
                ("QUANTITY", QuantityMessage(line.Quantity), line.Quantity.ValidationStatus, line.Quantity.Evidence.FirstOrDefault()),
                ("UNIT_PRICE", "Unit price must be a positive amount when supplied.", line.UnitPrice.ValidationStatus, line.UnitPrice.Evidence.FirstOrDefault()),
                ("LEAD_TIME_DAYS", "Lead time must be zero or greater when supplied.", line.LeadTimeDays.ValidationStatus, line.LeadTimeDays.Evidence.FirstOrDefault())
            })
            {
                if (field.ValidationStatus == ValidationStatus.Invalid)
                    yield return Issue(ValidationSeverity.Error, field.Item1, field.Item2, field.Item4);
            }
        }
    }

    /// <summary>
    /// Names the quantity the document actually stated. "Quantity must be a positive number"
    /// on a line that plainly reads "2,500 PCS" tells the reviewer nothing; the reading the parser
    /// refused, and why, is what lets them correct it.
    /// </summary>
    private static string QuantityMessage(CanonicalValue<decimal> quantity)
    {
        if (string.IsNullOrWhiteSpace(quantity.OriginalValue))
            return "Quantity is required and the document states none.";

        return quantity.Transformations.Contains($"quantity_origin:{QuantityOrigin.Ambiguous}")
            ? $"Quantity \"{quantity.OriginalValue.Trim()}\" is ambiguous — \".\" could be a decimal point or a "
              + "thousands separator, and the two readings differ a thousandfold. Confirm it."
            : $"Quantity \"{quantity.OriginalValue.Trim()}\" could not be read as a positive number with at most six decimal places.";
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

        // Shared with every ingestion door (see RfqDateParser). This method used to carry a
        // seventh private format list that omitted ISO 8601 with a 'T' — and the legacy .xls
        // reader renders a genuine Excel date cell as exactly that round-trip form, so a real
        // date cell was reported as "unsupported date format" and came out Invalid.
        //
        // Read, not Parse. Parse throws away the two things the reading establishes and this
        // path then has no way to recover:
        //
        //   * whether the source stated a TIME. FR-RFQ-04 requires the closing date AND time.
        //     A tender closing "2026-09-01 14:00" was parsed correctly, truncated to midnight
        //     by a .Date that no longer applies, and presented as a whole-day deadline — so a
        //     quote submitted at 15:00 looked on time and was late.
        //   * whether the numeric day/month order is AMBIGUOUS. "03/04/2026" is 3 April or
        //     4 March; a day-first reading is returned because that is Gulf convention, and it
        //     used to be stamped Confidence 1.0 / Valid with no note anywhere. The customer-PO
        //     path already tells its reviewer; this path now says the same thing.
        //
        // The time is kept only when the source stated one. A date-only token still lands on
        // midnight, so nothing that reads an existing value changes meaning.
        var reading = RfqDateParser.Read(raw);
        if (reading.Value is { } parsed)
        {
            value.Value = DateTime.SpecifyKind(
                reading.HasExplicitTime ? parsed : parsed.Date, DateTimeKind.Utc);
            value.Kind = CanonicalValueKind.Normalized;
            value.Transformations.Add(reading.HasExplicitTime ? "parse_exact_date_time" : "parse_exact_date");

            if (reading.IsDayMonthAmbiguous)
            {
                // A value we cannot certify is not a value with full confidence. It is read,
                // stored and SHOWN as needing confirmation — never silently asserted.
                value.Confidence = AmbiguousDateConfidence;
                value.ValidationStatus = ValidationStatus.NeedsReview;
                value.Transformations.Add(AmbiguousDateTransformation);
            }
            else
            {
                value.Confidence = 1.0m;
                value.ValidationStatus = ValidationStatus.Valid;
            }

            return value;
        }

        value.Kind = CanonicalValueKind.Extracted;
        value.Confidence = 0.2m;
        value.ValidationStatus = ValidationStatus.Invalid;
        value.Transformations.Add($"{fieldName}: unsupported date format");
        return value;
    }

    /// <summary>Marker left on a date the parser could read but not disambiguate.</summary>
    private const string AmbiguousDateTransformation = "ambiguous_day_month";

    /// <summary>A day-first reading of an ambiguous token is a reading, not a certainty.</summary>
    private const decimal AmbiguousDateConfidence = 0.6m;

    private static bool IsAmbiguous(CanonicalValue<DateTime> value)
        => value.Transformations.Contains(AmbiguousDateTransformation);

    /// <summary>The reviewer-facing wording, identical to the customer-purchase-order path.</summary>
    private static string AmbiguityMessage(CanonicalValue<DateTime> value, string field)
        => $"\"{value.OriginalValue}\" is ambiguous — both parts of the {field} are 12 or lower, so it could be "
           + "either day/month or month/day. It has been read day-first; confirm it.";

    /// <summary>
    /// Reads a line's demand quantity through the shared <see cref="QuantityParser"/>.
    ///
    /// <para><b>Why this is not <c>int.TryParse</c>.</b> It was, with
    /// <see cref="NumberStyles.Integer"/> and the invariant culture, and every one of these
    /// silently became the number 0 — which then travelled as a real quantity because the struct's
    /// default is indistinguishable from a parsed value once the parse result is discarded:</para>
    /// <code>
    ///   "2,500"     -> 0   (customer asked for two and a half thousand)
    ///   "500 PCS"   -> 0
    ///   "12.00"     -> 0
    ///   "1 000"     -> 0
    ///   "10-20"     -> 0
    ///   "٥٠٠"       -> 0   (Arabic-Indic digits, routine in Saudi tender packs)
    /// </code>
    /// <para>The correct parser already existed in this repository, wired into the customer-PO
    /// path and the legacy uploader but not into the RFQ path. It refuses to invent a number,
    /// reports WHY through <see cref="QuantityOrigin"/>, and — critically — refuses the genuinely
    /// ambiguous "1.234" instead of picking a reading that is wrong by a factor of a thousand.
    /// Arabic-Indic digits are mapped first, by the same routine the date parser uses.</para>
    ///
    /// <para>A quantity is REQUIRED, so an unreadable one is Invalid and blocks the document. It
    /// is never Normalized, which is what <c>ChunkedExtractionService.MapCanonicalItem</c> tests
    /// before it emits anything downstream.</para>
    /// </summary>
    private static CanonicalValue<decimal> QuantityValue(string? raw, RfqSpreadsheetRow row, string column, string fieldName)
    {
        var value = new CanonicalValue<decimal>
        {
            OriginalValue = raw,
            Evidence = new List<SourceEvidence> { Evidence(row, column, raw) }
        };

        var reading = QuantityParser.Parse(NormalizeNumerals(raw), allowFractional: true);

        // Never round into the numeric(20,6) persistence boundary. An unsupported precision
        // is reviewable source data, not permission to silently change the requested amount.
        if (reading.Value is { } quantity && QuantityParser.FitsPersistedQuantity(quantity))
        {
            value.Value = quantity;
            value.Kind = CanonicalValueKind.Normalized;
            value.Confidence = 1.0m;
            value.ValidationStatus = ValidationStatus.Valid;
            value.Transformations.Add("parse_quantity");
            value.Transformations.Add($"quantity_origin:{reading.Origin}");
            if (!string.IsNullOrWhiteSpace(reading.UnitToken))
                value.Transformations.Add($"unit_token:{reading.UnitToken}");
            return value;
        }

        if (reading.Origin == QuantityOrigin.Absent)
        {
            value.Kind = CanonicalValueKind.Missing;
            value.Confidence = 0m;
            value.ValidationStatus = ValidationStatus.Invalid;
            value.Transformations.Add($"{fieldName}: missing required number");
            return value;
        }

        value.Kind = CanonicalValueKind.Extracted;
        value.Confidence = 0.2m;
        value.ValidationStatus = ValidationStatus.Invalid;
        value.Transformations.Add($"{fieldName}: {QuantityReason(reading, "not a positive number with at most six decimal places")}");
        value.Transformations.Add($"quantity_origin:{reading.Origin}");
        return value;
    }

    private static string QuantityReason(QuantityReading reading, string unreadable) => reading.Origin switch
    {
        QuantityOrigin.Ambiguous => "ambiguous separators — \".\" could be a decimal point or a thousands "
            + "separator and the two readings differ a thousandfold",
        _ => unreadable,
    };

    /// <summary>
    /// Maps Arabic-Indic digits onto ASCII before any numeric parse. Shared with the date parser
    /// so one document's numbers are read the same way whichever field they land in.
    /// </summary>
    private static string? NormalizeNumerals(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? raw : RfqDateParser.NormalizeDigits(raw);

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

        if (int.TryParse(NormalizeNumerals(raw)!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && (optional ? parsed >= 0 : parsed > 0))
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
        // An OPTIONAL field that cannot be parsed is a value to review, never a reason to
        // invalidate the line. A buyer writing "Requested Delivery: 9 weeks" has told us
        // something useful and entirely legitimate; treating it as Invalid condemned every line
        // of the document — and, because the unparsed value still defaulted to 0, wrote a lead
        // time of "deliver immediately" onto every item. A required field still invalidates.
        value.ValidationStatus = optional ? ValidationStatus.NeedsReview : ValidationStatus.Invalid;
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

        // Same parser as the quantity, for the same reason: decimal.TryParse with the invariant
        // culture read "1,250.00" but silently rejected "1.250,00" and "SAR 1,250", and read
        // "1.250" as one and a quarter rather than refusing a token whose two readings differ a
        // thousandfold. A price is money; guessing its magnitude is not an option.
        var reading = QuantityParser.Parse(NormalizeNumerals(raw));
        if (reading.Value is { } parsed)
        {
            value.Value = parsed;
            value.Kind = CanonicalValueKind.Normalized;
            value.Confidence = 1.0m;
            value.ValidationStatus = ValidationStatus.Valid;
            value.Transformations.Add("parse_decimal");
            if (!string.IsNullOrWhiteSpace(reading.UnitToken))
                value.Transformations.Add($"currency_token:{reading.UnitToken}");
            return value;
        }

        value.Kind = CanonicalValueKind.Extracted;
        value.Confidence = 0.2m;
        // An OPTIONAL amount that cannot be read is a value to review, never a reason to condemn
        // the line — the same rule IntValue already applies, and for the same reason. This branch
        // used to be Invalid unconditionally, so one unreadable price ("On application", "TBC",
        // "SAR 1.250") invalidated every line of the document and the whole RFQ with it. A
        // required amount still invalidates.
        value.ValidationStatus = optional ? ValidationStatus.NeedsReview : ValidationStatus.Invalid;
        value.Transformations.Add($"{fieldName}: {QuantityReason(reading, "not a positive amount")}");
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
        RfqSpreadsheetFields.UnitOfMeasure => "F1",
        RfqSpreadsheetFields.UnitPrice => "G",
        RfqSpreadsheetFields.Currency => "H",
        RfqSpreadsheetFields.ManufacturerName => "I",
        RfqSpreadsheetFields.ManufacturerPartNumber => "J",
        RfqSpreadsheetFields.LeadTimeDays => "K",
        RfqSpreadsheetFields.ItemText => "L",
        RfqSpreadsheetFields.DeliveryLocation => "M",
        RfqSpreadsheetFields.RequiredDeliveryDate => "N",
        RfqSpreadsheetFields.AgreementReference => "O",
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

    // ---- document expectation -------------------------------------------
    //
    // "Expected fields" is a function of what the document in hand actually asserts, not of
    // the full canonical schema. See CanonicalValue.StatedInDocument for the reasoning; the
    // three members below are the whole of the rule.

    /// <summary>
    /// The fields this document states: those carrying source text on at least one of its
    /// rows. Evidence, not inference — no document classifier, no configuration, nothing to
    /// keep in step with a new buyer template.
    /// </summary>
    private static HashSet<string> StatedFields(IEnumerable<RfqSpreadsheetRow> rows)
    {
        var stated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            Consider(RfqSpreadsheetFields.RfqNo, row.RfqNo);
            Consider(RfqSpreadsheetFields.BuyerName, row.BuyerName);
            Consider(RfqSpreadsheetFields.ReceivedDate, row.ReceivedDate);
            Consider(RfqSpreadsheetFields.BidClosingDate, row.BidClosingDate);
            Consider(RfqSpreadsheetFields.ProductName, row.ProductName);
            Consider(RfqSpreadsheetFields.Quantity, row.Quantity);
            Consider(RfqSpreadsheetFields.UnitOfMeasure, row.UnitOfMeasure);
            Consider(RfqSpreadsheetFields.UnitPrice, row.UnitPrice);
            Consider(RfqSpreadsheetFields.Currency, row.Currency);
            Consider(RfqSpreadsheetFields.ManufacturerName, row.ManufacturerName);
            Consider(RfqSpreadsheetFields.ManufacturerPartNumber, row.ManufacturerPartNumber);
            Consider(RfqSpreadsheetFields.LeadTimeDays, row.LeadTimeDays);
            Consider(RfqSpreadsheetFields.ItemText, row.ItemText);
            Consider(RfqSpreadsheetFields.DeliveryLocation, row.DeliveryLocation);
            Consider(RfqSpreadsheetFields.RequiredDeliveryDate, row.RequiredDeliveryDate);
            Consider(RfqSpreadsheetFields.AgreementReference, row.AgreementReference);
        }

        return stated;

        void Consider(string field, string? raw)
        {
            if (!string.IsNullOrWhiteSpace(raw)) stated.Add(field);
        }
    }

    /// <summary>
    /// Header fields the document never states are excluded from the confidence average —
    /// confidence measures how well we READ, and there is nothing there to read. Their
    /// review status is deliberately left alone: unlike a solicited price, a closing date or
    /// a delivery date is a fact the BUYER states, so its absence is a commercial gap a
    /// human still has to resolve, and the existing issue for it still fires.
    /// </summary>
    private static void MarkHeaderExpectations(CanonicalRfqDocument document, HashSet<string> stated)
    {
        MarkUnstated(document.BidClosingDate, RfqSpreadsheetFields.BidClosingDate, stated, resolveToValid: false);
        MarkUnstated(document.DeliveryLocation, RfqSpreadsheetFields.DeliveryLocation, stated, resolveToValid: false);
        MarkUnstated(document.RequiredDeliveryDate, RfqSpreadsheetFields.RequiredDeliveryDate, stated, resolveToValid: false);
        MarkUnstated(document.AgreementReference, RfqSpreadsheetFields.AgreementReference, stated, resolveToValid: false);
    }

    /// <summary>
    /// The optional LINE fields. Each is present in some documents and legitimately absent
    /// from others: on an inbound RFQ, price, currency and lead time are precisely what the
    /// buyer is ASKING the supplier to supply, and a unit or a brand is stated only when the
    /// buyer chose to. The required fields (product, quantity) are deliberately not listed —
    /// their absence is always a defect and always flags.
    /// </summary>
    private static void MarkLineExpectations(CanonicalRfqLineItem line, HashSet<string> stated)
    {
        MarkUnstated(line.UnitOfMeasure, RfqSpreadsheetFields.UnitOfMeasure, stated, resolveToValid: true);
        MarkUnstated(line.UnitPrice, RfqSpreadsheetFields.UnitPrice, stated, resolveToValid: true);
        MarkUnstated(line.Currency, RfqSpreadsheetFields.Currency, stated, resolveToValid: true);
        MarkUnstated(line.ManufacturerName, RfqSpreadsheetFields.ManufacturerName, stated, resolveToValid: true);
        MarkUnstated(line.ManufacturerPartNumber, RfqSpreadsheetFields.ManufacturerPartNumber, stated, resolveToValid: true);
        MarkUnstated(line.LeadTimeDays, RfqSpreadsheetFields.LeadTimeDays, stated, resolveToValid: true);
        MarkUnstated(line.ItemText, RfqSpreadsheetFields.ItemText, stated, resolveToValid: true);
    }

    /// <summary>
    /// Marks one value as absent from the document — but ONLY when nothing was read for it.
    /// A value whose source text is present and unparseable has <c>Kind != Missing</c> and is
    /// untouched here, so a reading failure keeps its low confidence and its review flag.
    /// That guard is what stops this becoming the opposite defect: a signal that never fires.
    /// </summary>
    private static void MarkUnstated<T>(
        CanonicalValue<T> value, string field, HashSet<string> stated, bool resolveToValid)
    {
        if (value.Kind != CanonicalValueKind.Missing || stated.Contains(field)) return;

        value.StatedInDocument = false;
        value.Transformations.Add($"{field}: not stated anywhere in this document");
        if (resolveToValid) value.ValidationStatus = ValidationStatus.Valid;
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
