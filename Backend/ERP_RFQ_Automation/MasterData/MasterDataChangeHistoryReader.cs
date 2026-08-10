using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.MasterData;

/// <summary>One recorded change, with the fields that moved. Shaped to match
/// <c>PlatformAuditEntryDetailDto</c>/<c>PlatformAuditFieldChangeDto</c> so the tenant console can
/// reuse the reading conventions the platform audit explorer already established.</summary>
public sealed class MasterDataChangeEventDto
{
    public long Id { get; set; }
    public DateTime OccurredOn { get; set; }

    /// <summary>CREATED, UPDATED or DELETED.</summary>
    public string ChangeType { get; set; } = null!;

    public string Actor { get; set; } = null!;
    public long? ActorUserId { get; set; }

    /// <summary>API, IMPORT or SYSTEM — a bulk spreadsheet edit reads very differently from a
    /// single screen edit and the console must be able to show which it was.</summary>
    public string ChangeSource { get; set; } = null!;

    public string? CorrelationId { get; set; }
    public string? Reason { get; set; }
    public string? EntityLabel { get; set; }

    /// <summary>Number of fields recorded. Equals <see cref="Changes"/>.Count; carried separately
    /// so a future summary view can page the header list without loading the fields.</summary>
    public int FieldCount { get; set; }

    public IReadOnlyList<MasterDataFieldChangeDto> Changes { get; set; } = [];
}

public sealed class MasterDataFieldChangeDto
{
    public string Field { get; set; } = null!;

    /// <summary>Null when the field genuinely had no value. Nothing is withheld — see
    /// <c>MasterDataEntityDescriptor.ClassifyField</c> for why.</summary>
    public string? Before { get; set; }

    public string? After { get; set; }

    /// <summary>COMMERCIAL, PERSONAL, or null. Lets the console flag a cost or terms edit without
    /// the reader having to recognise the column name.</summary>
    public string? Sensitivity { get; set; }
}

public interface IMasterDataChangeHistoryReader
{
    /// <summary>
    /// The change history of one master-data record, newest first.
    ///
    /// <para><paramref name="businessUnitId"/> is the caller's authenticated tenant and is applied
    /// as an EXPLICIT predicate as well as being covered by the global query filter. Belt and
    /// braces is the house style for anything that reads an audit trail: a filter that is bypassed
    /// by an <c>IgnoreQueryFilters</c> added elsewhere would turn this endpoint into a
    /// cross-tenant read of who changed whose costs.</para>
    /// </summary>
    Task<IReadOnlyList<MasterDataChangeEventDto>> ReadAsync(
        string entityType, long entityId, long businessUnitId, int limit,
        CancellationToken cancellationToken = default);
}

public sealed class MasterDataChangeHistoryReader(ErpRfqAutomationContext context)
    : IMasterDataChangeHistoryReader
{
    /// <summary>Hard ceiling on one page of history. A record with ten thousand import-driven
    /// edits must not be able to turn one screen open into a full-table read.</summary>
    public const int MaxLimit = 200;

    public async Task<IReadOnlyList<MasterDataChangeEventDto>> ReadAsync(
        string entityType, long entityId, long businessUnitId, int limit,
        CancellationToken cancellationToken = default)
    {
        var resolved = MasterDataEntityTypes.Resolve(entityType)
            ?? throw new ArgumentOutOfRangeException(nameof(entityType), "Not a governed master-data type.");
        if (businessUnitId <= 0)
            throw new ArgumentOutOfRangeException(nameof(businessUnitId), "An authenticated tenant is required.");

        var take = Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxLimit);

        var events = await context.MasterDataChangeEvents.AsNoTracking()
            .Where(e => e.BusinessUnitId == businessUnitId
                        && e.EntityType == resolved
                        && e.EntityId == entityId)
            .OrderByDescending(e => e.OccurredOn)
            .ThenByDescending(e => e.Id)
            .Take(take)
            .Select(e => new MasterDataChangeEventDto
            {
                Id = e.Id,
                OccurredOn = e.OccurredOn,
                ChangeType = e.ChangeType,
                Actor = e.Actor,
                ActorUserId = e.ActorUserId,
                ChangeSource = e.ChangeSource,
                CorrelationId = e.CorrelationId,
                Reason = e.Reason,
                EntityLabel = e.EntityLabel,
                FieldCount = e.FieldCount,
                Changes = e.Fields
                    .OrderBy(f => f.FieldName)
                    .Select(f => new MasterDataFieldChangeDto
                    {
                        Field = f.FieldName,
                        Before = f.BeforeValue,
                        After = f.AfterValue,
                        Sensitivity = f.Sensitivity
                    })
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        return events;
    }
}
