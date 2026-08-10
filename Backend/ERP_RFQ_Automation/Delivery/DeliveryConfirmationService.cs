using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Delivery;

public interface IDeliveryConfirmationService
{
    Task<DeliveryProofView> ConfirmAsync(
        long businessUnitId, long shipmentId, string idempotencyKey,
        ConfirmDeliveryCommand command, string actor,
        CancellationToken cancellationToken = default);

    Task<DeliveryProofView?> GetAsync(
        long businessUnitId, long shipmentId, CancellationToken cancellationToken = default);

    Task<DeliveryProofLineView> RecordShortfallDecisionAsync(
        long businessUnitId, long proofLineId, RecordShortfallDecisionCommand command, string actor,
        CancellationToken cancellationToken = default);

    Task<string> TransitionAsync(
        long businessUnitId, long shipmentId, string nextStatus, string actor,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Gate 7 / FR-DLM-03 and FR-DLM-07. Records what happened at the door.
///
/// <para><b>The POD and the accepted quantities are one write.</b> A delivered quantity with no
/// proof behind it, or a proof with no quantities, is the half-record that makes a dispute
/// unwinnable — so the <see cref="DeliveryProof"/>, its <see cref="DeliveryProofLine"/> children
/// and the shipment's status change all commit together or none of them do.</para>
///
/// <para><b>The status is derived, never asserted.</b> The operator is asked what the customer
/// took, line by line. If that equals what was despatched the shipment becomes
/// <see cref="DeliveryStatuses.Delivered"/>; if it is less it becomes
/// <see cref="DeliveryStatuses.DeliveryException"/>. There is no field on the command for the
/// outcome, so nobody can stamp DELIVERED over a shortfall or record an exception with nothing
/// wrong — which is exactly the number FR-DLM-02 exists to make trustworthy.</para>
///
/// <para><b>Immutable once captured.</b> One proof per shipment, enforced by a unique index rather
/// than by a check the service performs; no update path exists on this class. Evidence a party to a
/// dispute could revise is not evidence.</para>
///
/// <para><b>What this deliberately does not do.</b> It records no carrier liability, opens no
/// claim, assigns no investigator and schedules no remediation. Damage in transit is the carrier's
/// process, run outside this system. What this system owes the business is the commercial
/// consequence — the customer is short, the shortfall is not invoiceable, and somebody has to
/// choose between re-supply and credit. That choice is
/// <see cref="RecordShortfallDecisionAsync"/> and it is surfaced, never automated.</para>
/// </summary>
public sealed class DeliveryConfirmationService(
    ErpRfqAutomationContext db,
    ILogger<DeliveryConfirmationService> log) : IDeliveryConfirmationService
{
    private readonly ErpRfqAutomationContext _db = db;
    private readonly ILogger<DeliveryConfirmationService> _log = log;

    public async Task<DeliveryProofView> ConfirmAsync(
        long businessUnitId, long shipmentId, string idempotencyKey,
        ConfirmDeliveryCommand command, string actor,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(actor))
            throw new DeliveryValidationException("An authenticated actor is required.");
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 160)
            throw new DeliveryValidationException("An idempotency key up to 160 characters is required.");

        var key = idempotencyKey.Trim();

        // An unsigned delivery note is not proof of anything. Every other evidential field on the
        // POD is optional — a driver with no signal cannot capture GPS, a storeman may refuse a
        // photograph — and refusing the confirmation for those would push operators to record the
        // delivery nowhere at all. The receiving contact's NAME is the one exception.
        var receivedByName = command.ReceivedByName?.Trim();
        if (string.IsNullOrWhiteSpace(receivedByName) || receivedByName.Length > 255)
            throw new DeliveryValidationException(
                "The name of the person who took receipt is required, up to 255 characters.");
        if (command.ReceivedOn == default)
            throw new DeliveryValidationException("The time the goods changed hands is required.");
        if (command.Lines is null || command.Lines.Count == 0)
            throw new DeliveryValidationException(
                "A delivery confirmation must state what was accepted on every line.");

        ValidateGps(command);

        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

            // Replay before anything else: a driver on a flaky connection retries, and a second POD
            // against one consignment would double every accepted unit on it.
            var replay = await _db.DeliveryProofs.AsNoTracking()
                .SingleOrDefaultAsync(p => p.BusinessUnitId == businessUnitId
                                           && p.IdempotencyKey == key, cancellationToken);
            if (replay is not null)
            {
                if (replay.ShipmentId != shipmentId)
                    throw new DeliveryConflictException(
                        "That idempotency key was already used for a different shipment.");
                await transaction.CommitAsync(cancellationToken);
                return await ProjectAsync(businessUnitId, replay.ShipmentId, cancellationToken)
                       ?? throw new DeliveryConflictException("The recorded proof could not be read back.");
            }

            var shipment = await _db.Shipments
                .SingleOrDefaultAsync(s => s.BusinessUnitId == businessUnitId && s.Id == shipmentId,
                    cancellationToken)
                ?? throw new DeliveryValidationException("Shipment was not found in this tenant.");
            if (!shipment.IsActive)
                throw new DeliveryConflictException("A withdrawn shipment cannot be confirmed received.");
            if (!DeliveryStatuses.Confirmable.Contains(shipment.DeliveryStatus))
                throw new DeliveryConflictException(
                    $"A {shipment.DeliveryStatus} shipment cannot be confirmed received. "
                    + "Only a dispatched or in-transit shipment has goods with the customer.");

            var alreadyProved = await _db.DeliveryProofs
                .AnyAsync(p => p.BusinessUnitId == businessUnitId && p.ShipmentId == shipmentId,
                    cancellationToken);
            if (alreadyProved)
                throw new DeliveryConflictException(
                    "This shipment already carries a proof of delivery. A POD is captured once.");

            var manifest = await _db.ShipmentItems.AsNoTracking()
                .Where(si => si.ShipmentId == shipmentId && si.IsActive)
                .Select(si => new
                {
                    si.Id,
                    si.OrderItemId,
                    si.Quantity,
                    OrderId = si.Shipment.OrderId
                })
                .ToListAsync(cancellationToken);
            if (manifest.Count == 0)
                throw new DeliveryConflictException("This shipment declares no goods to confirm.");

            var declared = new Dictionary<long, ConfirmDeliveryLineCommand>();
            foreach (var line in command.Lines)
            {
                if (!declared.TryAdd(line.ShipmentItemId, line))
                    throw new DeliveryValidationException(
                        $"Shipment line {line.ShipmentItemId} was declared twice.");
            }

            // EVERY line must be answered. Silently treating an omitted line as fully accepted is
            // wiring-contract failure #8 in the write direction: the value the operator never gave
            // becomes the value the invoice is raised against.
            var unanswered = manifest.Where(m => !declared.ContainsKey(m.Id))
                .Select(m => m.Id).OrderBy(x => x).ToList();
            if (unanswered.Count > 0)
                throw new DeliveryValidationException(
                    "Every despatched line must state what the customer accepted. Missing: "
                    + string.Join(", ", unanswered) + ".");
            var foreign = declared.Keys.Except(manifest.Select(m => m.Id)).OrderBy(x => x).ToList();
            if (foreign.Count > 0)
                throw new DeliveryValidationException(
                    "Line(s) " + string.Join(", ", foreign) + " are not on this shipment.");

            var now = DateTime.UtcNow;
            var proof = new DeliveryProof
            {
                BusinessUnitId = businessUnitId,
                ShipmentId = shipment.Id,
                ReceivedByName = receivedByName,
                ReceivedByContact = Truncate(command.ReceivedByContact?.Trim(), 255),
                ReceivedByPosition = Truncate(command.ReceivedByPosition?.Trim(), 255),
                ReceivedOn = command.ReceivedOn,
                SignatureEvidenceId = await ResolveEvidenceAsync(
                    businessUnitId, shipment.Id, command.SignatureEvidenceId, "signature", cancellationToken),
                StampEvidenceId = await ResolveEvidenceAsync(
                    businessUnitId, shipment.Id, command.StampEvidenceId, "stamp", cancellationToken),
                PhotoEvidenceId = await ResolveEvidenceAsync(
                    businessUnitId, shipment.Id, command.PhotoEvidenceId, "photograph", cancellationToken),
                GpsLatitude = command.GpsLatitude,
                GpsLongitude = command.GpsLongitude,
                GpsAccuracyMeters = command.GpsAccuracyMeters,
                GpsCapturedOn = command.GpsCapturedOn,
                Notes = Truncate(command.Notes?.Trim(), 1000),
                // Copied from the shipment so the proof states its own case, exactly as the note
                // does. Null where the order never came from a lead — a gap recorded as a gap.
                CommercialCaseId = shipment.CommercialCaseId,
                NexoraSerial = shipment.NexoraSerial,
                IdempotencyKey = key,
                RecordedBy = actor.Trim(),
                RecordedOn = now,
                Version = 1
            };
            _db.DeliveryProofs.Add(proof);
            await _db.SaveChangesAsync(cancellationToken);

            var short_ = false;
            foreach (var item in manifest.OrderBy(m => m.Id))
            {
                var line = declared[item.Id];
                if (line.AcceptedQuantity < 0)
                    throw new DeliveryValidationException(
                        $"Accepted quantity on line {item.Id} cannot be negative.");
                if (line.AcceptedQuantity > item.Quantity)
                    throw new DeliveryValidationException(
                        $"Line {item.Id} declares {line.AcceptedQuantity} accepted against "
                        + $"{item.Quantity} despatched. A customer cannot accept more than arrived.");

                var isShort = line.AcceptedQuantity < item.Quantity;
                var reason = line.ExceptionReasonCode?.Trim().ToUpperInvariant();
                if (isShort)
                {
                    if (string.IsNullOrWhiteSpace(reason))
                        throw new DeliveryValidationException(
                            $"Line {item.Id} is short by {item.Quantity - line.AcceptedQuantity} and "
                            + "must state why.");
                    if (!DeliveryExceptionReasons.All.Contains(reason))
                        throw new DeliveryValidationException(
                            $"'{line.ExceptionReasonCode}' is not a recognised delivery exception reason.");
                    short_ = true;
                }
                else if (!string.IsNullOrWhiteSpace(reason))
                {
                    throw new DeliveryValidationException(
                        $"Line {item.Id} was accepted in full and cannot carry an exception reason.");
                }

                _db.DeliveryProofLines.Add(new DeliveryProofLine
                {
                    BusinessUnitId = businessUnitId,
                    DeliveryProofId = proof.Id,
                    ShipmentId = shipment.Id,
                    ShipmentItemId = item.Id,
                    OrderItemId = item.OrderItemId,
                    OrderId = item.OrderId,
                    DespatchedQuantity = item.Quantity,
                    AcceptedQuantity = line.AcceptedQuantity,
                    ExceptionReasonCode = isShort ? reason : null,
                    ExceptionNote = isShort ? Truncate(line.ExceptionNote?.Trim(), 1000) : null,
                    Notes = Truncate(line.Notes?.Trim(), 500)
                });
            }

            var outcome = short_ ? DeliveryStatuses.DeliveryException : DeliveryStatuses.Delivered;
            shipment.ApplyConfirmedOutcome(outcome, actor.Trim(), now);
            // The tenant-facing date on the shipment now has a writer. It was settable through
            // UpdateShipment with nothing behind it; from here it means "the customer signed".
            shipment.ActualDeliveryDate = command.ReceivedOn;

            _db.ShipmentStatusHistories.Add(new ShipmentStatusHistory
            {
                ShipmentId = shipment.Id,
                PreviousStatusId = shipment.StatusId,
                NewStatusId = shipment.StatusId,
                ChangedBy = actor.Trim(),
                ChangedOn = now,
                Notes = $"Delivery confirmed as {outcome} by {receivedByName}."
            });

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _log.LogInformation(
                "Delivery confirmed {Outcome} for shipment {ShipmentId} (tenant {BusinessUnitId}) "
                + "by {Actor}; received by {ReceivedBy}.",
                outcome, shipment.Id, businessUnitId, actor, receivedByName);

            return await ProjectAsync(businessUnitId, shipment.Id, cancellationToken)
                   ?? throw new DeliveryConflictException("The recorded proof could not be read back.");
        });
    }

    public Task<DeliveryProofView?> GetAsync(
        long businessUnitId, long shipmentId, CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        return ProjectAsync(businessUnitId, shipmentId, cancellationToken);
    }

    /// <summary>
    /// FR-DLM-07's commercial half: re-supply, or credit.
    ///
    /// <para>Recording it is what closes the exception — see
    /// <c>CommercialExceptionApplicationService.DetectAsync</c>, which stops emitting the shortfall
    /// once a decision exists, and lets the framework resolve the case with SOURCE_RESOLVED. That
    /// is the whole of "resolution" here: the commercial decision was taken. There is no
    /// investigation to conclude.</para>
    /// </summary>
    public async Task<DeliveryProofLineView> RecordShortfallDecisionAsync(
        long businessUnitId, long proofLineId, RecordShortfallDecisionCommand command, string actor,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(actor))
            throw new DeliveryValidationException("An authenticated actor is required.");

        var decision = command.Decision?.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(decision) || !DeliveryShortfallDecisions.All.Contains(decision))
            throw new DeliveryValidationException(
                "The decision must be RESUPPLY or CREDIT.");
        var reason = command.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new DeliveryValidationException(
                "A reason up to 1000 characters is required. A decision with no reason behind it "
                + "tells the next person reading the case nothing.");

        var line = await _db.DeliveryProofLines.AsNoTracking()
            .SingleOrDefaultAsync(l => l.BusinessUnitId == businessUnitId && l.Id == proofLineId,
                cancellationToken)
            ?? throw new DeliveryValidationException("Delivery line was not found in this tenant.");
        if (!line.IsShort)
            throw new DeliveryConflictException(
                "That line was accepted in full. There is no shortfall to decide.");

        var existing = await _db.DeliveryShortfallDecisions.AsNoTracking()
            .AnyAsync(d => d.BusinessUnitId == businessUnitId && d.DeliveryProofLineId == proofLineId,
                cancellationToken);
        if (existing)
            throw new DeliveryConflictException(
                "A decision has already been recorded for this shortfall. It is append-only.");

        _db.DeliveryShortfallDecisions.Add(new DeliveryShortfallDecision
        {
            BusinessUnitId = businessUnitId,
            DeliveryProofLineId = line.Id,
            ShipmentId = line.ShipmentId,
            Decision = decision,
            Reason = reason,
            DecidedBy = actor.Trim(),
            DecidedOn = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(cancellationToken);

        _log.LogInformation(
            "Delivery shortfall {ProofLineId} on shipment {ShipmentId} (tenant {BusinessUnitId}) "
            + "decided {Decision} by {Actor}.",
            line.Id, line.ShipmentId, businessUnitId, decision, actor);

        var view = await ProjectAsync(businessUnitId, line.ShipmentId, cancellationToken);
        return view?.Lines.SingleOrDefault(l => l.Id == line.Id)
               ?? throw new DeliveryConflictException("The decision could not be read back.");
    }

    /// <summary>
    /// FR-DLM-05, the operator-facing half of the ladder: SCHEDULED to DISPATCHED to IN_TRANSIT, or
    /// CANCELLED from anything still in flight. DELIVERED and DELIVERY_EXCEPTION are not reachable
    /// here — <see cref="DeliveryStatuses.CanTransition"/> refuses them — because they are outcomes
    /// of what the customer accepted, not selections.
    /// </summary>
    public async Task<string> TransitionAsync(
        long businessUnitId, long shipmentId, string nextStatus, string actor,
        CancellationToken cancellationToken = default)
    {
        if (businessUnitId <= 0)
            throw new UnauthorizedAccessException("A valid authenticated tenant is required.");
        if (string.IsNullOrWhiteSpace(actor))
            throw new DeliveryValidationException("An authenticated actor is required.");

        var next = (nextStatus ?? string.Empty).Trim().ToUpperInvariant();
        if (!DeliveryStatuses.All.Contains(next))
            throw new DeliveryValidationException($"'{nextStatus}' is not a delivery status.");

        var shipment = await _db.Shipments
            .SingleOrDefaultAsync(s => s.BusinessUnitId == businessUnitId && s.Id == shipmentId,
                cancellationToken)
            ?? throw new DeliveryValidationException("Shipment was not found in this tenant.");
        if (!shipment.IsActive)
            throw new DeliveryConflictException("A withdrawn shipment has no lifecycle.");

        try
        {
            shipment.TransitionDeliveryStatus(next, actor.Trim(), DateTime.UtcNow);
        }
        catch (InvalidOperationException ex)
        {
            throw new DeliveryConflictException(ex.Message);
        }

        _db.ShipmentStatusHistories.Add(new ShipmentStatusHistory
        {
            ShipmentId = shipment.Id,
            PreviousStatusId = shipment.StatusId,
            NewStatusId = shipment.StatusId,
            ChangedBy = actor.Trim(),
            ChangedOn = DateTime.UtcNow,
            Notes = $"Delivery status set to {next}."
        });
        await _db.SaveChangesAsync(cancellationToken);
        return shipment.DeliveryStatus;
    }

    /// <summary>
    /// The evidence must belong to this tenant AND have been filed against this shipment. Accepting
    /// a bare attachment id would let a caller staple another tenant's photograph — or another
    /// shipment's signature — to a POD, which is the one document that has to be unimpeachable.
    /// </summary>
    private async Task<long?> ResolveEvidenceAsync(
        long businessUnitId, long shipmentId, long? attachmentId, string label,
        CancellationToken cancellationToken)
    {
        if (!attachmentId.HasValue) return null;

        var owned = await _db.Attachments.AsNoTracking()
            .AnyAsync(a => a.Id == attachmentId.Value
                           && a.ParentType == DeliveryProofEvidenceService.EvidenceParentType
                           && a.ParentId == shipmentId, cancellationToken)
            && await _db.Shipments.AsNoTracking()
                .AnyAsync(s => s.Id == shipmentId && s.BusinessUnitId == businessUnitId, cancellationToken);
        if (!owned)
            throw new DeliveryValidationException(
                $"The {label} evidence was not captured against this shipment.");
        return attachmentId;
    }

    private static void ValidateGps(ConfirmDeliveryCommand command)
    {
        var hasLat = command.GpsLatitude.HasValue;
        var hasLon = command.GpsLongitude.HasValue;
        if (hasLat != hasLon)
            throw new DeliveryValidationException(
                "A GPS fix needs both a latitude and a longitude. Half a coordinate is not a place.");
        if (hasLat && (command.GpsLatitude is < -90 or > 90 || command.GpsLongitude is < -180 or > 180))
            throw new DeliveryValidationException("The GPS coordinate is outside the valid range.");
        if (!hasLat && command.GpsCapturedOn.HasValue)
            throw new DeliveryValidationException(
                "A GPS timestamp with no coordinate behind it is evidence of nothing.");
        if (!hasLat && command.GpsAccuracyMeters.HasValue)
            throw new DeliveryValidationException(
                "A GPS accuracy with no coordinate behind it is evidence of nothing.");
        if (command.GpsAccuracyMeters is <= 0)
            throw new DeliveryValidationException("GPS accuracy is a positive number of metres.");
    }

    private async Task<DeliveryProofView?> ProjectAsync(
        long businessUnitId, long shipmentId, CancellationToken cancellationToken)
    {
        var proof = await _db.DeliveryProofs.AsNoTracking()
            .SingleOrDefaultAsync(p => p.BusinessUnitId == businessUnitId && p.ShipmentId == shipmentId,
                cancellationToken);
        if (proof is null) return null;

        var shipment = await _db.Shipments.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && s.Id == shipmentId)
            .Select(s => new { s.ShipmentNo, s.DeliveryStatus })
            .SingleAsync(cancellationToken);

        var lines = await _db.DeliveryProofLines.AsNoTracking()
            .Where(l => l.BusinessUnitId == businessUnitId && l.DeliveryProofId == proof.Id)
            .OrderBy(l => l.ShipmentItemId)
            .ToListAsync(cancellationToken);

        var lineIds = lines.Select(l => l.Id).ToArray();
        var decisions = await _db.DeliveryShortfallDecisions.AsNoTracking()
            .Where(d => d.BusinessUnitId == businessUnitId && lineIds.Contains(d.DeliveryProofLineId))
            .ToDictionaryAsync(d => d.DeliveryProofLineId, cancellationToken);

        var orderItemIds = lines.Select(l => l.OrderItemId).Distinct().ToArray();
        var productNames = await _db.Set<OrderItem>().AsNoTracking()
            .Where(i => orderItemIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Product.ProductName })
            .ToDictionaryAsync(x => x.Id, x => x.ProductName, cancellationToken);

        return new DeliveryProofView(
            proof.Id, proof.ShipmentId, shipment.ShipmentNo, shipment.DeliveryStatus,
            proof.ReceivedByName, proof.ReceivedByContact, proof.ReceivedByPosition, proof.ReceivedOn,
            proof.SignatureEvidenceId, proof.StampEvidenceId, proof.PhotoEvidenceId,
            proof.GpsLatitude, proof.GpsLongitude, proof.GpsAccuracyMeters, proof.GpsCapturedOn,
            proof.HasGpsFix, proof.Notes, proof.CommercialCaseId, proof.NexoraSerial,
            proof.RecordedBy, proof.RecordedOn,
            lines.Select(l =>
            {
                decisions.TryGetValue(l.Id, out var decision);
                return new DeliveryProofLineView(
                    l.Id, l.ShipmentItemId, l.OrderItemId,
                    productNames.GetValueOrDefault(l.OrderItemId),
                    l.DespatchedQuantity, l.AcceptedQuantity, l.RefusedQuantity,
                    l.ExceptionReasonCode, l.ExceptionNote, l.Notes,
                    decision?.Decision, decision?.Reason, decision?.DecidedBy, decision?.DecidedOn);
            }).ToList());
    }

    private static string? Truncate(string? value, int length)
        => string.IsNullOrWhiteSpace(value) ? null : value.Length <= length ? value : value[..length];
}
