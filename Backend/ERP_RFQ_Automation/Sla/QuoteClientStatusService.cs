using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Sla;

/// <summary>The SetupMaster types behind the client's own quote statuses.</summary>
public static class QuoteClientStatusTypes
{
    /// <summary>A customer step while the quote is SENT. ParentSetupId → the tenant's SENT row.</summary>
    public const string Step = "QuoteStep";

    /// <summary>How it ended. ParentSetupId → the ACCEPTED / REJECTED / EXPIRED row it counts as.</summary>
    public const string Ending = "QuoteEnding";

    /// <summary>Why. ParentSetupId → the outcome it is offered for; null = Lost and Expired (as before).</summary>
    public const string Reason = "QuoteOutcomeReason";

    /// <summary>The reason the SLA sweep writes. Nexora's, not the tenant's: never editable.</summary>
    public const string SystemReasonCode = "AUTO_EXPIRED";

    internal static readonly string[] All = { Step, Ending, Reason };

    /// <summary>"WON" / "LOST" / "EXPIRED" for the fixed status code an ending or reason points at.</summary>
    public static string? CountsAs(string? statusCode) => statusCode?.Trim().ToUpperInvariant() switch
    {
        "ACCEPTED" => "WON",
        "REJECTED" => "LOST",
        "EXPIRED" => "EXPIRED",
        _ => null
    };

    /// <summary>The fixed status code a "WON" / "LOST" / "EXPIRED" value stands for; null when it is none of them.</summary>
    public static string? StatusCodeFor(string? countsAs) => countsAs?.Trim().ToUpperInvariant() switch
    {
        "WON" => "ACCEPTED",
        "LOST" => "REJECTED",
        "EXPIRED" => "EXPIRED",
        _ => null
    };
}

public sealed class QuoteStepOptionDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystem { get; set; }
}

public sealed class QuoteEndingOptionDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystem { get; set; }
    /// <summary>"WON" | "LOST" | "EXPIRED". Fixed when the ending is added; never changes.</summary>
    public string CountsAs { get; set; } = string.Empty;
}

public sealed class QuoteReasonOptionDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public bool IsSystem { get; set; }
    public string Code { get; set; } = string.Empty;
    /// <summary>"WON" | "LOST" | "EXPIRED", or null = Lost and Expired.</summary>
    [JsonPropertyName("for")]
    public string? For { get; set; }
}

/// <summary>The client's own quote statuses. Won / Lost / Expired themselves are fixed and not listed.</summary>
public sealed class QuoteStatusCatalogDto
{
    public List<QuoteStepOptionDto> Steps { get; set; } = new();
    public List<QuoteEndingOptionDto> Endings { get; set; } = new();
    public List<QuoteReasonOptionDto> Reasons { get; set; } = new();
}

public sealed class QuoteStatusOptionCreateRequest
{
    /// <summary>"step" | "ending" | "reason".</summary>
    public string? Kind { get; set; }
    public string? Name { get; set; }
    /// <summary>Endings only, required: "WON" | "LOST" | "EXPIRED".</summary>
    public string? CountsAs { get; set; }
    /// <summary>Reasons only: "WON" | "LOST" | "EXPIRED", or null = Lost and Expired.</summary>
    [JsonPropertyName("for")]
    public string? For { get; set; }
}

/// <summary>Every field optional: only what is sent changes. <c>for: null</c> is a change (to Lost and Expired).</summary>
public sealed class QuoteStatusOptionUpdateRequest
{
    private string? _for;
    private string? _countsAs;

    public string? Name { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsActive { get; set; }

    [JsonPropertyName("for")]
    public string? For
    {
        get => _for;
        set { _for = value; ForSpecified = true; }
    }

    [JsonIgnore] public bool ForSpecified { get; private set; }

    /// <summary>Not editable. Present only so a request that tries is refused rather than ignored.</summary>
    public string? CountsAs
    {
        get => _countsAs;
        set { _countsAs = value; CountsAsSpecified = true; }
    }

    [JsonIgnore] public bool CountsAsSpecified { get; private set; }
}

/// <summary>
/// The client's own quote statuses (owner request 2026-09-28): customer steps while a quote is
/// SENT, endings that each count as Won / Lost / Expired, and reasons with a "for".
///
/// <para>They are labels bound to the fixed lifecycle, never new states: auto-expiry, reminders,
/// the send gates, Capture Client PO and every dashboard keep reading the fixed status. A step
/// cannot move a quote anywhere, and an ending is recorded only through the outcome command,
/// which is what moves the fixed status.</para>
/// </summary>
public interface IQuoteClientStatusService
{
    /// <summary>The tenant's steps, endings and reasons, seeding the Gulf defaults on first use.</summary>
    Task<QuoteStatusCatalogDto> GetCatalogAsync(long businessUnitId, bool includeInactive, CancellationToken ct = default);

    /// <summary>Adds a step, ending or reason. Returns the new option's DTO.</summary>
    Task<object> AddAsync(long businessUnitId, string actor, QuoteStatusOptionCreateRequest request, CancellationToken ct = default);

    /// <summary>Renames, re-orders, switches on/off, or changes a reason's "for". Returns the option's DTO.</summary>
    Task<object> UpdateAsync(long businessUnitId, string actor, long id, QuoteStatusOptionUpdateRequest request, CancellationToken ct = default);

    /// <summary>
    /// Sets (or clears, with null) the customer step on a SENT quote that no revision replaced.
    /// Picking a step also records that the customer responded (owner decision OD1).
    /// </summary>
    Task SetStepAsync(long quoteId, long businessUnitId, string actor, long? stepId, CancellationToken ct = default);
}

public sealed class QuoteClientStatusService : IQuoteClientStatusService
{
    public const int MaxNameLength = 100;
    public const string QuoteStepSetMetric = "quote_step_set";

    // Owner decision OD4: the Gulf defaults, seeded when a tenant has none of that kind.
    private static readonly (string Code, string Name)[] DefaultSteps =
    {
        ("TECHNICAL_EVALUATION", "Technical evaluation"),
        ("CLARIFICATION_ASKED", "Clarification asked"),
        ("COMMERCIAL_EVALUATION", "Commercial evaluation"),
        ("NEGOTIATION_BAFO", "Negotiation / BAFO"),
    };

    private static readonly (string Code, string Name, string StatusCode)[] DefaultEndings =
    {
        ("PARTLY_WON", "Partly won", "ACCEPTED"),
        ("VERBAL_AWARD", "Verbal award, awaiting PO", "ACCEPTED"),
        ("TENDER_CANCELLED", "Tender cancelled", "REJECTED"),
        ("AWARDED_TO_ANOTHER", "Awarded to another bidder", "REJECTED"),
    };

    private static readonly (string Code, string Name)[] DefaultWonReasons =
    {
        ("ONLY_COMPLIANT_BIDDER", "Only compliant bidder"),
        ("BEST_PRICE", "Best price"),
        ("STOCK_AVAILABLE", "Stock available"),
    };

    private static readonly string[] EndingGroupOrder = { "WON", "LOST", "EXPIRED" };

    private readonly ErpRfqAutomationContext _context;
    private readonly IQuoteOutcomeService _outcomes;
    private readonly ILogger<QuoteClientStatusService>? _logger;
    private readonly ERP_RFQ_Automation.Metrics.IMetricRecorder? _metrics;

    public QuoteClientStatusService(
        ErpRfqAutomationContext context,
        IQuoteOutcomeService outcomes,
        ILogger<QuoteClientStatusService>? logger = null,
        ERP_RFQ_Automation.Metrics.IMetricRecorder? metrics = null)
    {
        _context = context;
        _outcomes = outcomes;
        _logger = logger;
        _metrics = metrics;
    }

    // ---------------- catalog ----------------

    public async Task<QuoteStatusCatalogDto> GetCatalogAsync(long businessUnitId, bool includeInactive, CancellationToken ct = default)
    {
        await EnsureSeededAsync(businessUnitId, ct);

        var rows = await _context.SetupMasters.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && QuoteClientStatusTypes.All.Contains(s.SetupType)
                        && (includeInactive || s.IsActive == true || s.IsActive == null))
            .Select(s => new CatalogRow(s.SetupId, s.SetupType, s.SetupCode, s.SetupValue, s.Description,
                s.ParentSetupId, s.SortOrder, s.IsActive))
            .ToListAsync(ct);
        var parentCodes = await ParentCodesAsync(rows.Select(r => r.ParentSetupId), ct);

        var catalog = new QuoteStatusCatalogDto();
        foreach (var row in rows.OrderBy(r => r.SortOrder).ThenBy(r => r.Id))
        {
            var countsAs = row.ParentSetupId is long parent && parentCodes.TryGetValue(parent, out var code)
                ? QuoteClientStatusTypes.CountsAs(code)
                : null;
            switch (row.Type)
            {
                case QuoteClientStatusTypes.Step:
                    catalog.Steps.Add(ToStep(row));
                    break;
                case QuoteClientStatusTypes.Ending when countsAs is not null:
                    catalog.Endings.Add(ToEnding(row, countsAs));
                    break;
                case QuoteClientStatusTypes.Reason:
                    catalog.Reasons.Add(ToReason(row, countsAs));
                    break;
            }
        }
        // Endings sit in their group — Won, then Lost, then Expired — in the tenant's order within it.
        catalog.Endings = catalog.Endings
            .OrderBy(e => Array.IndexOf(EndingGroupOrder, e.CountsAs))
            .ThenBy(e => e.SortOrder).ThenBy(e => e.Id)
            .ToList();
        return catalog;
    }

    // ---------------- add ----------------

    public async Task<object> AddAsync(long businessUnitId, string actor, QuoteStatusOptionCreateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var type = request.Kind?.Trim().ToLowerInvariant() switch
        {
            "step" => QuoteClientStatusTypes.Step,
            "ending" => QuoteClientStatusTypes.Ending,
            "reason" => QuoteClientStatusTypes.Reason,
            _ => throw new ArgumentException("Choose what to add: a step, an ending or a reason.")
        };
        var name = ValidName(request.Name);

        // The defaults go in first, so adding one's own step never stops the defaults from arriving.
        await EnsureSeededAsync(businessUnitId, ct);
        var fixedIds = await FixedStatusIdsAsync(businessUnitId, ct);

        long? parentId;
        string? countsAs = null;
        if (type == QuoteClientStatusTypes.Step)
        {
            parentId = fixedIds.GetValueOrDefault("SENT")
                ?? throw new InvalidOperationException("This workspace has no Sent quote status to add steps to.");
        }
        else if (type == QuoteClientStatusTypes.Ending)
        {
            var statusCode = QuoteClientStatusTypes.StatusCodeFor(request.CountsAs)
                ?? throw new ArgumentException("Choose what the ending counts as: Won, Lost or Expired.");
            countsAs = QuoteClientStatusTypes.CountsAs(statusCode);
            parentId = fixedIds.GetValueOrDefault(statusCode)
                ?? throw new InvalidOperationException($"This workspace has no {Word(countsAs)} quote status.");
        }
        else
        {
            parentId = ReasonParent(request.For, fixedIds);
            countsAs = QuoteClientStatusTypes.CountsAs(QuoteClientStatusTypes.StatusCodeFor(request.For));
        }

        await EnsureNameIsFreeAsync(businessUnitId, type, name, exceptId: null, ct);

        var sameKind = await _context.SetupMasters
            .Where(s => s.BusinessUnitId == businessUnitId && s.SetupType == type)
            .Select(s => new { s.SetupCode, s.SortOrder })
            .ToListAsync(ct);
        var nextOrder = sameKind.Count == 0 ? 1 : sameKind.Max(s => (int)s.SortOrder) + 1;
        var row = new SetupMaster
        {
            SetupType = type,
            SetupCode = UniqueCode(name, sameKind.Select(s => s.SetupCode)),
            SetupValue = name,
            Description = name,
            ParentSetupId = parentId,
            BusinessUnitId = businessUnitId,
            SortOrder = (short)Math.Min(nextOrder, short.MaxValue),
            IsActive = true,
            CreatedBy = Actor(actor),
            CreatedOn = DateTime.UtcNow
        };
        _context.SetupMasters.Add(row);
        await _context.SaveChangesAsync(ct);

        return ToOption(Row(row), countsAs);
    }

    // ---------------- update ----------------

    public async Task<object> UpdateAsync(long businessUnitId, string actor, long id, QuoteStatusOptionUpdateRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await _context.SetupMasters
            .FirstOrDefaultAsync(s => s.SetupId == id && s.BusinessUnitId == businessUnitId
                                      && QuoteClientStatusTypes.All.Contains(s.SetupType), ct)
            ?? throw new KeyNotFoundException($"Quote status {id} was not found.");

        if (row.SetupType == QuoteClientStatusTypes.Reason
            && string.Equals(row.SetupCode, QuoteClientStatusTypes.SystemReasonCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{row.Description ?? row.SetupValue}' is set by Nexora when a quote expires on its own. It cannot be changed.");

        var parentCodes = await ParentCodesAsync(new[] { row.ParentSetupId }, ct);
        var currentCountsAs = row.ParentSetupId is long parent && parentCodes.TryGetValue(parent, out var parentCode)
            ? QuoteClientStatusTypes.CountsAs(parentCode)
            : null;

        if (request.CountsAsSpecified)
        {
            if (row.SetupType != QuoteClientStatusTypes.Ending)
                throw new ArgumentException("Only an ending counts as Won, Lost or Expired.");
            if (!string.Equals(request.CountsAs?.Trim(), currentCountsAs, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"What '{row.SetupValue}' counts as never changes. Switch it off and add a new ending.");
        }

        if (request.Name is not null)
        {
            var name = ValidName(request.Name);
            await EnsureNameIsFreeAsync(businessUnitId, row.SetupType, name, row.SetupId, ct);
            row.SetupValue = name;
            row.Description = name;
        }

        if (request.SortOrder.HasValue)
        {
            if (request.SortOrder.Value < 0 || request.SortOrder.Value > short.MaxValue)
                throw new ArgumentException($"The position must be between 0 and {short.MaxValue}.");
            row.SortOrder = (short)request.SortOrder.Value;
        }

        if (request.IsActive.HasValue) row.IsActive = request.IsActive.Value;

        if (request.ForSpecified && row.SetupType == QuoteClientStatusTypes.Reason)
        {
            var fixedIds = await FixedStatusIdsAsync(businessUnitId, ct);
            row.ParentSetupId = ReasonParent(request.For, fixedIds);
            currentCountsAs = QuoteClientStatusTypes.CountsAs(QuoteClientStatusTypes.StatusCodeFor(request.For));
        }

        row.ModifiedBy = Actor(actor);
        row.ModifiedOn = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);

        return ToOption(Row(row), currentCountsAs);
    }

    // ---------------- the step on a quote ----------------

    public async Task SetStepAsync(long quoteId, long businessUnitId, string actor, long? stepId, CancellationToken ct = default)
    {
        var quote = await _context.Quotes
            .Include(q => q.Status)
            .FirstOrDefaultAsync(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId, ct)
            ?? throw new KeyNotFoundException($"Quote with ID {quoteId} not found.");

        var statusCode = (quote.Status?.SetupCode ?? quote.Status?.SetupValue)?.Trim().ToUpperInvariant();
        if (quote.RemovedOn.HasValue || statusCode != "SENT" || quote.OutcomeOn.HasValue)
            throw new InvalidOperationException(
                $"Quote '{quote.QuoteNo}' is not with the customer. A customer step can only be set on a sent quote.");

        // Same rule as the outcome command: once a revision exists, the chain's news belongs on it.
        // A withdrawn (removed) revision replaced nothing.
        var successor = await _context.Quotes.AsNoTracking()
            .Where(q => q.RevisionOfQuoteId == quote.Id && q.BusinessUnitId == businessUnitId && q.RemovedOn == null)
            .Select(q => new { q.QuoteNo, q.SentOn })
            .FirstOrDefaultAsync(ct);
        if (successor is not null)
            throw new InvalidOperationException(successor.SentOn is null
                ? $"Revision '{successor.QuoteNo}' is waiting to be sent. Send it, then update its status."
                : $"Quote '{quote.QuoteNo}' has been replaced by revision '{successor.QuoteNo}'. Set the step on '{successor.QuoteNo}' instead.");

        var now = DateTime.UtcNow;
        if (stepId is null)
        {
            if (quote.SubStatusId is null) return;
            quote.SubStatusId = null;
            quote.SubStatusOn = null;
            quote.ModifiedBy = Actor(actor);
            quote.ModifiedDate = now;
            await _context.SaveChangesAsync(ct);
            await RecordStepMetricAsync(quote, null, null, actor, now, ct);
            return;
        }

        var step = await _context.SetupMasters.AsNoTracking()
            .Where(s => s.SetupId == stepId.Value && s.BusinessUnitId == businessUnitId
                        && s.SetupType == QuoteClientStatusTypes.Step)
            .Select(s => new { s.SetupId, s.SetupValue, s.IsActive })
            .FirstOrDefaultAsync(ct)
            ?? throw new ArgumentException("That step is not on this workspace's list of customer steps.");
        if (step.IsActive == false)
            throw new ArgumentException($"'{step.SetupValue}' is switched off. Switch it on in Setup to use it.");

        if (quote.SubStatusId != step.SetupId)
        {
            quote.SubStatusId = step.SetupId;
            quote.SubStatusOn = now;
        }
        // OD1: knowing the customer's step means the customer (or their portal) told us something.
        // Stamped in the same save as the step; the activity follows through the one shared
        // "customer responded" path so follow-ups and the pipeline see exactly what the button does.
        quote.RespondedOn ??= now;
        quote.ModifiedBy = Actor(actor);
        quote.ModifiedDate = now;
        await _context.SaveChangesAsync(ct);
        await _outcomes.MarkRespondedAsync(quote.Id, businessUnitId, Actor(actor), ct);

        _logger?.LogInformation("Quote {QuoteId} customer step set to {StepId} by {Actor}.", quote.Id, step.SetupId, actor);
        await RecordStepMetricAsync(quote, step.SetupId, step.SetupValue, actor, now, ct);
    }

    /// <summary>Who set which step when, append-only. Never throws, never blocks the step.</summary>
    private async Task RecordStepMetricAsync(Quote quote, long? stepId, string? stepName, string actor, DateTime on, CancellationToken ct)
    {
        if (_metrics is null) return;
        await _metrics.RecordAsync(quote.BusinessUnitId, QuoteStepSetMetric, quote.Id, new
        {
            quoteId = quote.Id,
            quoteNo = quote.QuoteNo,
            stepId,
            stepName,
            actor = Actor(actor),
            on
        }, ct);
    }

    // ---------------- seeding (idempotent, per-BU, lazy on first use) ----------------

    /// <summary>
    /// Seeds the Gulf defaults (OD4) for each kind the tenant has none of — steps, endings, won
    /// reasons — and the original lost/expired reasons through their one owner. Never overwrites,
    /// never re-adds a row the tenant switched off.
    /// </summary>
    public async Task EnsureSeededAsync(long businessUnitId, CancellationToken ct = default)
    {
        await _outcomes.GetOutcomeReasonsAsync(businessUnitId, ct);

        var fixedIds = await FixedStatusIdsAsync(businessUnitId, ct);
        var existing = await _context.SetupMasters
            .Where(s => s.BusinessUnitId == businessUnitId
                        && (s.SetupType == QuoteClientStatusTypes.Step || s.SetupType == QuoteClientStatusTypes.Ending
                            || s.SetupType == QuoteClientStatusTypes.Reason))
            .Select(s => new { s.SetupType, s.SetupCode, s.ParentSetupId })
            .ToListAsync(ct);

        var now = DateTime.UtcNow;
        var added = false;
        void Add(string type, string code, string name, long parentId, int order)
        {
            _context.SetupMasters.Add(new SetupMaster
            {
                SetupType = type,
                SetupCode = code,
                SetupValue = name,
                Description = name,
                ParentSetupId = parentId,
                BusinessUnitId = businessUnitId,
                SortOrder = (short)order,
                IsActive = true,
                CreatedBy = "system:quote-status-seed",
                CreatedOn = now
            });
            added = true;
        }

        if (!existing.Any(s => s.SetupType == QuoteClientStatusTypes.Step) && fixedIds.GetValueOrDefault("SENT") is long sentId)
        {
            for (var i = 0; i < DefaultSteps.Length; i++)
                Add(QuoteClientStatusTypes.Step, DefaultSteps[i].Code, DefaultSteps[i].Name, sentId, i + 1);
        }

        if (!existing.Any(s => s.SetupType == QuoteClientStatusTypes.Ending))
        {
            for (var i = 0; i < DefaultEndings.Length; i++)
                if (fixedIds.GetValueOrDefault(DefaultEndings[i].StatusCode) is long parentId)
                    Add(QuoteClientStatusTypes.Ending, DefaultEndings[i].Code, DefaultEndings[i].Name, parentId, i + 1);
        }

        if (fixedIds.GetValueOrDefault("ACCEPTED") is long acceptedId
            && !existing.Any(s => s.SetupType == QuoteClientStatusTypes.Reason && s.ParentSetupId == acceptedId))
        {
            var codes = new HashSet<string>(
                existing.Where(s => s.SetupType == QuoteClientStatusTypes.Reason && s.SetupCode != null).Select(s => s.SetupCode!),
                StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < DefaultWonReasons.Length; i++)
                if (!codes.Contains(DefaultWonReasons[i].Code))
                    Add(QuoteClientStatusTypes.Reason, DefaultWonReasons[i].Code, DefaultWonReasons[i].Name, acceptedId, i + 1);
        }

        if (added) await _context.SaveChangesAsync(ct);
    }

    // ---------------- helpers ----------------

    private sealed record CatalogRow(
        long Id, string Type, string? Code, string Value, string? Description,
        long? ParentSetupId, short SortOrder, bool? IsActive);

    private static CatalogRow Row(SetupMaster s) => new(
        s.SetupId, s.SetupType, s.SetupCode, s.SetupValue, s.Description, s.ParentSetupId, s.SortOrder, s.IsActive);

    private static object ToOption(CatalogRow row, string? countsAs) => row.Type switch
    {
        QuoteClientStatusTypes.Step => ToStep(row),
        QuoteClientStatusTypes.Ending => ToEnding(row, countsAs ?? string.Empty),
        _ => ToReason(row, countsAs)
    };

    private static QuoteStepOptionDto ToStep(CatalogRow row) => new()
    {
        Id = row.Id, Name = row.Value, SortOrder = row.SortOrder, IsActive = row.IsActive != false, IsSystem = false
    };

    private static QuoteEndingOptionDto ToEnding(CatalogRow row, string countsAs) => new()
    {
        Id = row.Id, Name = row.Value, SortOrder = row.SortOrder, IsActive = row.IsActive != false, IsSystem = false,
        CountsAs = countsAs
    };

    private static QuoteReasonOptionDto ToReason(CatalogRow row, string? countsAs) => new()
    {
        Id = row.Id,
        Name = row.Description ?? row.Value,
        SortOrder = row.SortOrder,
        IsActive = row.IsActive != false,
        IsSystem = string.Equals(row.Code, QuoteClientStatusTypes.SystemReasonCode, StringComparison.OrdinalIgnoreCase),
        Code = row.Code ?? string.Empty,
        For = countsAs
    };

    private static string ValidName(string? name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) throw new ArgumentException("Enter a name.");
        if (trimmed.Length > MaxNameLength) throw new ArgumentException($"Keep the name to {MaxNameLength} characters.");
        return trimmed;
    }

    /// <summary>
    /// Names the quote-status window already shows beside the tenant's steps and endings. A step
    /// or ending with one of them would put two identical choices in the same window.
    /// </summary>
    internal static readonly string[] ReservedNames = { "Won", "Lost", "Expired", "Customer replied" };

    private async Task EnsureNameIsFreeAsync(long businessUnitId, string type, string name, long? exceptId, CancellationToken ct)
    {
        // Steps and endings are offered side by side in one window, so they share one name space.
        // Reasons are a separate list (the "why"), checked only against other reasons.
        var sharesWindow = type == QuoteClientStatusTypes.Step || type == QuoteClientStatusTypes.Ending;
        if (sharesWindow)
        {
            var reserved = ReservedNames.FirstOrDefault(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
            if (reserved is not null)
                throw new InvalidOperationException($"'{reserved}' is a fixed status. Pick another name.");
        }

        var kinds = sharesWindow
            ? new[] { QuoteClientStatusTypes.Step, QuoteClientStatusTypes.Ending }
            : new[] { type };

        // Switched-off rows count: a second "Tender cancelled" beside a retired one would split its history.
        var names = await _context.SetupMasters.AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId && kinds.Contains(s.SetupType)
                        && (exceptId == null || s.SetupId != exceptId))
            .Select(s => new { s.SetupType, Name = type == QuoteClientStatusTypes.Reason ? s.Description ?? s.SetupValue : s.SetupValue, s.IsActive })
            .ToListAsync(ct);
        var clash = names.FirstOrDefault(n => string.Equals(n.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase));
        if (clash is null) return;
        if (clash.SetupType != type)
            throw new InvalidOperationException(clash.SetupType == QuoteClientStatusTypes.Step
                ? $"'{name}' is already a step. Pick another name."
                : $"'{name}' is already an ending. Pick another name.");
        throw new InvalidOperationException(clash.IsActive == false
            ? $"'{name}' is already on the list, switched off. Switch it back on instead."
            : $"'{name}' is already on the list.");
    }

    private static long? ReasonParent(string? forValue, IReadOnlyDictionary<string, long?> fixedIds)
    {
        if (string.IsNullOrWhiteSpace(forValue)) return null;
        var statusCode = QuoteClientStatusTypes.StatusCodeFor(forValue)
            ?? throw new ArgumentException("A reason is for Won, Lost or Expired, or left empty for Lost and Expired.");
        return fixedIds.GetValueOrDefault(statusCode)
            ?? throw new InvalidOperationException($"This workspace has no {Word(QuoteClientStatusTypes.CountsAs(statusCode))} quote status.");
    }

    /// <summary>
    /// The tenant's SENT / ACCEPTED / REJECTED / EXPIRED rows: the business unit's own first, then
    /// the legacy shared row — the same resolution the outcome command uses.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, long?>> FixedStatusIdsAsync(long businessUnitId, CancellationToken ct)
    {
        var codes = new[] { "SENT", "ACCEPTED", "REJECTED", "EXPIRED" };
        var rows = await _context.SetupMasters.AsNoTracking()
            .Where(s => s.SetupType == "QuoteStatus" && s.SetupCode != null && codes.Contains(s.SetupCode))
            .Select(s => new { s.SetupId, s.SetupCode, s.BusinessUnitId })
            .ToListAsync(ct);
        var result = new Dictionary<string, long?>(StringComparer.OrdinalIgnoreCase);
        foreach (var code in codes)
        {
            var own = rows.Where(r => r.SetupCode == code && r.BusinessUnitId == businessUnitId).OrderBy(r => r.SetupId).FirstOrDefault()
                      ?? rows.Where(r => r.SetupCode == code).OrderBy(r => r.SetupId).FirstOrDefault();
            result[code] = own?.SetupId;
        }
        return result;
    }

    private async Task<Dictionary<long, string?>> ParentCodesAsync(IEnumerable<long?> parentIds, CancellationToken ct)
    {
        var ids = parentIds.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();
        if (ids.Count == 0) return new Dictionary<long, string?>();
        return await _context.SetupMasters.AsNoTracking()
            .Where(s => ids.Contains(s.SetupId))
            .ToDictionaryAsync(s => s.SetupId, s => s.SetupCode, ct);
    }

    /// <summary>A stable code from the name ("Tender cancelled" → TENDER_CANCELLED), unique within the kind.</summary>
    internal static string UniqueCode(string name, IEnumerable<string?> taken)
    {
        var builder = new StringBuilder();
        foreach (var ch in name.ToUpperInvariant())
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') builder.Append(ch);
            else if (builder.Length > 0 && builder[^1] != '_') builder.Append('_');
        }
        var stem = builder.ToString().Trim('_');
        if (stem.Length == 0) stem = "CUSTOM";
        if (stem.Length > 80) stem = stem[..80].TrimEnd('_');

        var used = new HashSet<string>(taken.Where(c => c != null)!, StringComparer.OrdinalIgnoreCase);
        var code = stem;
        for (var n = 2; used.Contains(code); n++) code = $"{stem}_{n}";
        return code;
    }

    private static string Word(string? countsAs) => countsAs switch
    {
        "WON" => "Won",
        "LOST" => "Lost",
        "EXPIRED" => "Expired",
        _ => "matching"
    };

    private static string Actor(string? actor) => string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();
}

/// <summary>
/// Projects the client's status and the owner's name onto quote DTOs, batch-loaded once for a
/// page (no query per row). A step left over on a quote that is no longer SENT is never shown:
/// the fixed status moved on, so "Technical evaluation" would be a false statement.
/// </summary>
public static class QuoteClientStatusReadModel
{
    public sealed record Row(QuoteResponseDTO Dto, long? SubStatusId, DateTime? SubStatusOn, long? OwnerUserId);

    public static async Task ApplyAsync(
        ErpRfqAutomationContext context, long businessUnitId, IReadOnlyCollection<Row> rows, CancellationToken ct = default)
    {
        if (rows.Count == 0) return;

        var statusIds = rows.Where(r => r.SubStatusId.HasValue).Select(r => r.SubStatusId!.Value).Distinct().ToList();
        var statuses = statusIds.Count == 0
            ? new Dictionary<long, (string Type, string Name)>()
            : (await context.SetupMasters.AsNoTracking()
                .Where(s => statusIds.Contains(s.SetupId) && s.BusinessUnitId == businessUnitId
                            && (s.SetupType == QuoteClientStatusTypes.Step || s.SetupType == QuoteClientStatusTypes.Ending))
                .Select(s => new { s.SetupId, s.SetupType, s.SetupValue })
                .ToListAsync(ct))
                .ToDictionary(s => s.SetupId, s => (Type: s.SetupType, Name: s.SetupValue));

        var ownerIds = rows.Where(r => r.OwnerUserId.HasValue).Select(r => r.OwnerUserId!.Value).Distinct().ToList();
        var owners = ownerIds.Count == 0
            ? new Dictionary<long, string?>()
            : (await context.Users.AsNoTracking()
                .Where(u => ownerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.FirstName, u.LastName })
                .ToListAsync(ct))
                .ToDictionary(u => u.Id, u => FullName(u.FirstName, u.LastName));

        foreach (var row in rows)
        {
            var dto = row.Dto;
            dto.SubStatusId = null;
            dto.SubStatusName = null;
            dto.SubStatusKind = null;
            dto.SubStatusOn = null;
            if (row.SubStatusId is long id && statuses.TryGetValue(id, out var status))
            {
                var kind = status.Type == QuoteClientStatusTypes.Step ? "STEP" : "ENDING";
                var isSent = string.Equals((dto.StatusCode ?? dto.StatusValue)?.Trim(), "SENT", StringComparison.OrdinalIgnoreCase);
                if (kind == "ENDING" || isSent)
                {
                    dto.SubStatusId = id;
                    dto.SubStatusName = status.Name;
                    dto.SubStatusKind = kind;
                    dto.SubStatusOn = row.SubStatusOn;
                }
            }
            dto.OwnerName = row.OwnerUserId is long owner && owners.TryGetValue(owner, out var ownerName) ? ownerName : null;
        }
    }

    private static string? FullName(string? first, string? last)
    {
        var name = $"{first?.Trim()} {last?.Trim()}".Trim();
        return name.Length == 0 ? null : name;
    }
}
