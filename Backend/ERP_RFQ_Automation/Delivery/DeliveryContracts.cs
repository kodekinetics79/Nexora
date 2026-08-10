namespace ERP_RFQ_Automation.Delivery;

/// <summary>One shipment line, and how much of it the customer took. FR-DLM-02 / FR-DLM-07.</summary>
public sealed record ConfirmDeliveryLineCommand(
    long ShipmentItemId,
    decimal AcceptedQuantity,
    string? ExceptionReasonCode,
    string? ExceptionNote,
    string? Notes);

/// <summary>
/// FR-DLM-03. Everything captured at the door, in one command, because it is one physical event.
///
/// <para>The evidence ids are <c>Attachment</c> ids produced by
/// <see cref="IDeliveryProofEvidenceService"/> — the POD upload goes through the same inspection,
/// malware scan and content-addressed store as every other document in the estate. This command
/// carries no bytes.</para>
/// </summary>
public sealed record ConfirmDeliveryCommand(
    string ReceivedByName,
    string? ReceivedByContact,
    string? ReceivedByPosition,
    DateTime ReceivedOn,
    long? SignatureEvidenceId,
    long? StampEvidenceId,
    long? PhotoEvidenceId,
    decimal? GpsLatitude,
    decimal? GpsLongitude,
    decimal? GpsAccuracyMeters,
    DateTime? GpsCapturedOn,
    string? Notes,
    IReadOnlyList<ConfirmDeliveryLineCommand> Lines);

public sealed record DeliveryProofLineView(
    long Id,
    long ShipmentItemId,
    long OrderItemId,
    string? ProductName,
    decimal DespatchedQuantity,
    decimal AcceptedQuantity,
    decimal RefusedQuantity,
    string? ExceptionReasonCode,
    string? ExceptionNote,
    string? Notes,
    string? CommercialDecision,
    string? CommercialDecisionReason,
    string? CommercialDecisionBy,
    DateTime? CommercialDecisionOn);

public sealed record DeliveryProofView(
    long Id,
    long ShipmentId,
    string ShipmentNo,
    string Outcome,
    string ReceivedByName,
    string? ReceivedByContact,
    string? ReceivedByPosition,
    DateTime ReceivedOn,
    long? SignatureEvidenceId,
    long? StampEvidenceId,
    long? PhotoEvidenceId,
    decimal? GpsLatitude,
    decimal? GpsLongitude,
    decimal? GpsAccuracyMeters,
    DateTime? GpsCapturedOn,
    bool HasGpsFix,
    string? Notes,
    long? CommercialCaseId,
    string? NexoraSerial,
    string RecordedBy,
    DateTime RecordedOn,
    IReadOnlyList<DeliveryProofLineView> Lines);

public sealed record RecordShortfallDecisionCommand(string Decision, string Reason);

/// <summary>FR-DLM-01. The delivery note, assembled from the record rather than the browser.</summary>
public sealed record DeliveryNoteLine(
    long ShipmentItemId,
    long OrderItemId,
    string? ProductName,
    string? Description,
    string? UnitOfMeasure,
    decimal OrderedQuantity,
    decimal DespatchedQuantity,
    decimal PreviouslyAcceptedQuantity,
    decimal RemainingQuantity,
    IReadOnlyList<DeliveryNoteLot> Lots);

/// <summary>FR-DLM-01 requires the note to reference the material lots that left on it.</summary>
public sealed record DeliveryNoteLot(string LotNumber, decimal Quantity, string? CountryOfOrigin);

public sealed record DeliveryNoteView(
    long ShipmentId,
    string ShipmentNo,
    DateTime ShipmentDate,
    string DeliveryStatus,
    long OrderId,
    string OrderNo,
    long? CommercialCaseId,
    string? NexoraSerial,
    string? CustomerName,
    string? CustomerPurchaseOrderNumber,
    DateTime? CustomerPurchaseOrderDate,
    string? ShippingAddress,
    int? DeliveryCityId,
    string? DeliveryCityName,
    string? DeliveryRegionName,
    string? DeliveryCountryName,
    string? Carrier,
    string? TrackingNumber,
    IssuerIdentity Issuer,
    IReadOnlyList<DeliveryNoteLine> Lines,
    DeliveryProofView? Proof,
    IReadOnlyList<string> Gaps);

/// <summary>
/// Who is despatching. A document that cannot identify its sender says so on its face rather than
/// borrowing a plausible-looking name: every field here is read from the tenant's own business-unit
/// record, and any of them may be null.
/// </summary>
public sealed record IssuerIdentity(
    string? LegalName,
    string? TaxRegistrationNumber,
    string? AddressLine,
    string? Phone,
    string? Email);
