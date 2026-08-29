using System.Text.Json;
using System.Text.Json.Serialization;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.LeadIdentity;

/// <summary>
/// The customer-facing commercial values frozen by an immutable Lead revision.
///
/// Identity fingerprints intentionally normalize and prune values for matching. They are not
/// business data. This versioned snapshot is the opposite: it preserves the exact values that
/// RFQ promotion is allowed to copy. Keep the lower-case identity fields for readable diffs and
/// backward-compatible line indexing, but never use them as the formal values.
/// </summary>
internal sealed record LeadRevisionCommercialSnapshot(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("rfq")] string? NormalizedRfq,
    [property: JsonPropertyName("buyer")] string? NormalizedBuyer,
    [property: JsonPropertyName("closing")] string? NormalizedClosing,
    [property: JsonPropertyName("customerRfqReference")] string? CustomerRfqReference,
    [property: JsonPropertyName("buyersName")] string? BuyersName,
    [property: JsonPropertyName("recDate")] DateTime RecDate,
    [property: JsonPropertyName("bidClosingDate")] DateTime? BidClosingDate,
    [property: JsonPropertyName("acknowledgmentDate")] DateTime? AcknowledgmentDate,
    [property: JsonPropertyName("submissionDate")] DateTime? SubmissionDate,
    [property: JsonPropertyName("headerRemarks")] string? HeaderRemarks,
    [property: JsonPropertyName("opportunityNo")] string? OpportunityNo,
    [property: JsonPropertyName("rfqType")] string? RfqType,
    [property: JsonPropertyName("durationAgreement")] string? DurationAgreement,
    [property: JsonPropertyName("requiredDeliveryDate")] DateTime? RequiredDeliveryDate,
    [property: JsonPropertyName("deliveryLocation")] string? DeliveryLocation,
    [property: JsonPropertyName("agreementReference")] string? AgreementReference,
    [property: JsonPropertyName("bidClosingDateHijri")] string? BidClosingDateHijri,
    [property: JsonPropertyName("inquiryType")] string? InquiryType,
    [property: JsonPropertyName("commercialCaseId")] long CommercialCaseId,
    [property: JsonPropertyName("commercialCaseReference")] string? CommercialCaseReference,
    [property: JsonPropertyName("customerId")] long? CustomerId,
    [property: JsonPropertyName("contactId")] long? ContactId,
    [property: JsonPropertyName("items")] IReadOnlyList<LeadRevisionLineCommercialSnapshot> Items)
{
    internal const int CurrentSchemaVersion = 2;

    internal static LeadRevisionCommercialSnapshot Capture(
        Lead lead, Func<string?, string?> normalize, Func<string?, string?> normalizeUom,
        Func<LeadItem, string?> normalizedClosing)
    {
        var items = lead.LeadItems.Where(x => x.IsCurrentRevisionProjection)
            .Select(x => LeadRevisionLineCommercialSnapshot.Capture(x, normalize, normalizeUom, normalizedClosing))
            .OrderBy(x => x.Part, StringComparer.Ordinal)
            .ThenBy(x => x.Line, StringComparer.Ordinal)
            .ToArray();
        return new LeadRevisionCommercialSnapshot(
            CurrentSchemaVersion,
            normalize(lead.Rfqno),
            normalize(lead.BuyersName),
            lead.BidClosingDate?.ToUniversalTime().ToString("O"),
            lead.Rfqno,
            lead.BuyersName,
            lead.RecDate,
            lead.BidClosingDate,
            lead.AcknowledgmentDate,
            lead.SubDate,
            lead.HeaderRemarks,
            lead.OpportunityNo,
            lead.Rfqtype,
            lead.DurationAgreement,
            lead.RequiredDeliveryDate,
            lead.DeliveryLocation,
            lead.AgreementReference,
            lead.BidClosingDateHijri,
            lead.InquiryType,
            lead.CommercialCaseId,
            lead.CommercialCaseReference,
            lead.CustomerId,
            lead.ContactId,
            items);
    }

    internal static bool TryParse(string? json, out LeadRevisionCommercialSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != CurrentSchemaVersion)
                return false;
            snapshot = JsonSerializer.Deserialize<LeadRevisionCommercialSnapshot>(json);
            return snapshot is { SchemaVersion: CurrentSchemaVersion };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

internal sealed record LeadRevisionLineCommercialSnapshot(
    // Stable normalized identity fields retained at their historical JSON paths.
    [property: JsonPropertyName("line")] string? Line,
    [property: JsonPropertyName("part")] string? Part,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("Quantity")] decimal? NormalizedQuantity,
    [property: JsonPropertyName("uom")] string? Uom,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    // Exact commercial values consumed by promotion.
    [property: JsonPropertyName("companyRef")] string? CompanyRef,
    [property: JsonPropertyName("customerAccountPortalId")] string? CustomerAccountPortalId,
    [property: JsonPropertyName("customerRfqno")] string? CustomerRfqno,
    [property: JsonPropertyName("itemMaterialCode")] string? ItemMaterialCode,
    [property: JsonPropertyName("lineItemNo")] string? LineItemNo,
    [property: JsonPropertyName("commodityProduct")] string? CommodityProduct,
    [property: JsonPropertyName("productShortName")] string? ProductShortName,
    [property: JsonPropertyName("productShortDescription")] string? ProductShortDescription,
    [property: JsonPropertyName("alternative")] string? Alternative,
    [property: JsonPropertyName("buyerName")] string? BuyerName,
    [property: JsonPropertyName("currency")] string? Currency,
    [property: JsonPropertyName("unitOfMeasure")] string? UnitOfMeasure,
    [property: JsonPropertyName("unitPrice")] decimal? UnitPrice,
    [property: JsonPropertyName("quantity")] decimal? Quantity,
    [property: JsonPropertyName("storageLocation")] string? StorageLocation,
    [property: JsonPropertyName("manufacturerName")] string? ManufacturerName,
    [property: JsonPropertyName("manufacturerPartNumber")] string? ManufacturerPartNumber,
    [property: JsonPropertyName("alternateProductName")] string? AlternateProductName,
    [property: JsonPropertyName("alternatePartNumber")] string? AlternatePartNumber,
    [property: JsonPropertyName("itemText")] string? ItemText,
    [property: JsonPropertyName("materialPoText")] string? MaterialPoText,
    [property: JsonPropertyName("leadTime")] int? LeadTime,
    [property: JsonPropertyName("receivedDate")] DateTime? ReceivedDate,
    [property: JsonPropertyName("bidClosingDateLine")] DateTime? BidClosingDateLine,
    [property: JsonPropertyName("aiConfidence")] decimal? AiConfidence,
    [property: JsonPropertyName("extraFields")] string? ExtraFields)
{
    internal static LeadRevisionLineCommercialSnapshot Capture(
        LeadItem item, Func<string?, string?> normalize, Func<string?, string?> normalizeUom,
        Func<LeadItem, string?> normalizedClosing) => new(
        normalize(item.LineItemNo),
        normalize(item.ManufacturerPartNumber ?? item.ItemMaterialCode),
        normalize(item.ProductShortDescription ?? item.ItemText),
        item.Quantity,
        normalizeUom(item.UnitOfMeasure),
        normalizedClosing(item),
        LeadRevisionCommercialSnapshot.CurrentSchemaVersion,
        item.CompanyRef,
        item.CustomerAccountPortalId,
        item.CustomerRfqno,
        item.ItemMaterialCode,
        item.LineItemNo,
        item.CommodityProduct,
        item.ProductShortName,
        item.ProductShortDescription,
        item.Alternative,
        item.BuyerName,
        item.Currency,
        item.UnitOfMeasure,
        item.UnitPrice,
        item.Quantity,
        item.StorageLocation,
        item.ManufacturerName,
        item.ManufacturerPartNumber,
        item.AlternateProductName,
        item.AlternatePartNumber,
        item.ItemText,
        item.MaterialPotext,
        item.LeadTime,
        item.ReceivedDate,
        item.BidClosingDateLine,
        item.Aiconfidence,
        item.ExtraFields);

    internal static bool TryParse(string? json, out LeadRevisionLineCommercialSnapshot? snapshot)
    {
        snapshot = null;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var version)
                || version.ValueKind != JsonValueKind.Number
                || version.GetInt32() != LeadRevisionCommercialSnapshot.CurrentSchemaVersion)
                return false;
            snapshot = JsonSerializer.Deserialize<LeadRevisionLineCommercialSnapshot>(json);
            return snapshot is { SchemaVersion: LeadRevisionCommercialSnapshot.CurrentSchemaVersion };
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
