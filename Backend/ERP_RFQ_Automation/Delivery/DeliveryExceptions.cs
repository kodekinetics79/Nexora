namespace ERP_RFQ_Automation.Delivery;

/// <summary>The caller asked for something that is not a valid delivery record. 400.</summary>
public sealed class DeliveryValidationException(string message) : Exception(message);

/// <summary>
/// The delivery record is valid but the shipment is not in a state that permits it. 409, never a
/// 200 with a warning — a warning on a delivery screen is a warning nobody reads at the loading bay.
/// </summary>
public sealed class DeliveryConflictException(string message) : Exception(message);
