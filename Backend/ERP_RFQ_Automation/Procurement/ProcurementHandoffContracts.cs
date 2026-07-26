namespace ERP_RFQ_Automation.Procurement;

public sealed record CreateProcurementHandoffCommand(long CustomerOrderLineId, string DestinationType,
    long? WarehouseId, string? DeliveryLocation, DateOnly? RequiredOn);

public sealed record SynchronizeProcurementHandoffCommand(long ExpectedVersion, string ExternalSupplierPoNumber,
    string ExternalSupplierPoLineNumber, decimal OrderedQuantity, decimal ApprovedUnitCost,
    DateOnly ExpectedOn, string Status, DateTime SynchronizedOn);

public sealed record ProcurementHandoffView(long Id, long CustomerOrderId, string CustomerOrderNumber,
    long CustomerOrderLineId, long CommercialDemandLineId, long SourcingAwardId,
    long SupplierQuotedItemId, long SupplierId, string SupplierName, string NexoraSerial,
    decimal RequiredQuantity, decimal SelectedUnitCost, long CurrencyId, string CurrencyCode,
    DateOnly? RequiredOn, string DestinationType, long? WarehouseId, string? DeliveryLocation,
    string ExternalSystemTarget, string Status, string? ExternalSupplierPoNumber,
    string? ExternalSupplierPoLineNumber, decimal? ExternalOrderedQuantity,
    decimal? ExternalApprovedUnitCost, DateOnly? ExternalExpectedOn, string? ExternalStatus,
    DateTime? LastSynchronizedOn, string? SourceOfTruth, bool IsAuthoritative, long Version);

public sealed record ProcurementHandoffCandidateView(long CustomerOrderId, string CustomerOrderNumber,
    long CustomerOrderLineId, long CommercialDemandLineId, long SourcingAwardId,
    long SupplierQuotedItemId, long SupplierId, string SupplierName, string NexoraSerial,
    decimal RequiredQuantity, decimal SelectedUnitCost, long CurrencyId, string CurrencyCode);

public interface IProcurementHandoffService
{
    Task<IReadOnlyCollection<ProcurementHandoffView>> SearchAsync(long businessUnitId, long? customerOrderId,
        string? search, int limit, CancellationToken cancellationToken = default);
    Task<IReadOnlyCollection<ProcurementHandoffCandidateView>> CandidatesAsync(long businessUnitId,
        CancellationToken cancellationToken = default);
    Task<ProcurementHandoffView> GetAsync(long businessUnitId, long id,
        CancellationToken cancellationToken = default);
    Task<ProcurementHandoffView> CreateAsync(long businessUnitId, string idempotencyKey,
        string correlationId, string actor, CreateProcurementHandoffCommand command,
        CancellationToken cancellationToken = default);
    Task<ProcurementHandoffView> SynchronizeAsync(long businessUnitId, long id, string idempotencyKey,
        string correlationId, string actor, SynchronizeProcurementHandoffCommand command,
        CancellationToken cancellationToken = default);
}
