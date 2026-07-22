namespace ERP_RFQ_Automation.CommercialCases;

public sealed record CommercialCaseSearchResult(
    long Id,
    string MasterReference,
    long LeadId,
    string? CustomerRfqNumber,
    string? BuyerName,
    string? CustomerEmail,
    string? Status,
    DateTime CreatedOn,
    int RfqCount,
    int QuoteCount,
    int OrderCount,
    int ShipmentCount);

public sealed record CommercialCaseDocument(
    string DocumentType,
    long DocumentId,
    string Reference,
    string? Status,
    DateTime? OccurredOn);

public sealed record CommercialCaseStatusEvent(
    long Id,
    string EventType,
    string? PreviousStatus,
    string? NewStatus,
    string? ChangedBy,
    string ActorSource,
    DateTime ChangedOn,
    string? Reason,
    string? AggregateType = null,
    string? CorrelationId = null,
    string? RequestReference = null,
    string? PolicyVersion = null,
    string? ReasonCode = null);

public sealed record CommercialCaseDetail(
    long Id,
    string MasterReference,
    long AllocationNumber,
    long BusinessUnitId,
    DateTime CreatedOn,
    long LeadId,
    string? CustomerRfqNumber,
    string? BuyerName,
    string? CustomerEmail,
    string? OpportunityNumber,
    string? CurrentStatus,
    IReadOnlyList<CommercialCaseDocument> Documents,
    IReadOnlyList<CommercialCaseStatusEvent> StatusHistory);
