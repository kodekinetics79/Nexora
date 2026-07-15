using System.Text.Json;

namespace ERP_RFQ_Automation.Services.Interfaces
{
    public interface ILLMService
    {
        Task<LeadExtractionResult?> ExtractLeadDataAsync(string fullText);
    }
    public record LeadExtractionResult(
        string? Rfqno, double? RfqnoConfidence,
        string? BuyersName, double? BuyersNameConfidence,
        string? RecDate, double? RecDateConfidence,
        string? BidClosingDate, double? BidClosingDateConfidence,
        string? BiddingDecision, double? BiddingDecisionConfidence,
        string? AcknowledgmentDate, double? AcknowledgmentDateConfidence,
        string? SubDate, double? SubDateConfidence,
        string? HeaderRemarks, double? HeaderRemarksConfidence,
        string? OpportunityNo, double? OpportunityNoConfidence,
        string? Rfqtype, double? RfqtypeConfidence,
        string? DurationAgreement, double? DurationAgreementConfidence,
        double? OverallConfidence,
        List<LeadItemData> Items);
    public record LeadItemData(
        string? CompanyRef, double? CompanyRefConfidence,
        string? CustomerAccountPortalId, double? CustomerAccountPortalIdConfidence,
        string? CustomerRfqno, double? CustomerRfqnoConfidence,
        string? ItemMaterialCode, double? ItemMaterialCodeConfidence,
        string? CommodityProduct, double? CommodityProductConfidence,
        string? BuyerName, double? BuyerNameConfidence,
        string? LineItemNo, double? LineItemNoConfidence,
        string? ProductShortName, double? ProductShortNameConfidence,
        string? Alternative, double? AlternativeConfidence,
        string? ProductShortDescription, double? ProductShortDescriptionConfidence,
        string? Currency, double? CurrencyConfidence,
        string? UnitOfMeasure, double? UnitOfMeasureConfidence,
        decimal? UnitPrice, double? UnitPriceConfidence,
        int? Quantity, double? QuantityConfidence,
        string? StorageLocation, double? StorageLocationConfidence,
        string? ManufacturerName, double? ManufacturerNameConfidence,
        string? ManufacturerPartNumber, double? ManufacturerPartNumberConfidence,
        string? AlternateProductName, double? AlternateProductNameConfidence,
        string? AlternatePartNumber, double? AlternatePartNumberConfidence,
        string? ItemText, double? ItemTextConfidence,
        string? MaterialPotext, double? MaterialPotextConfidence,
        string? LeadTime, double? LeadTimeConfidence,
        string? ReceivedDate, double? ReceivedDateConfidence,
        string? BidClosingDateLine, double? BidClosingDateLineConfidence,
        double? ItemConfidence);
}
