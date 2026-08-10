using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Reporting;

public sealed class ReportSubscriptionValidationException(string message) : InvalidOperationException(message);

public sealed record UpsertReportSubscriptionCommand(
    long? Id,
    string ReportKey,
    string Cadence,
    string Format,
    int HourUtc,
    int DayOfWeek,
    int DayOfMonth,
    int WindowDays,
    string Recipients,
    bool IsActive);

public interface IReportSubscriptionService
{
    Task<IReadOnlyList<ReportSubscription>> ListAsync(long businessUnitId, CancellationToken ct = default);

    Task<ReportSubscription> UpsertAsync(long businessUnitId, string actor,
        UpsertReportSubscriptionCommand command, CancellationToken ct = default);

    Task<bool> DeleteAsync(long businessUnitId, long id, CancellationToken ct = default);
}

/// <summary>
/// The one way a tenant configures scheduled reporting.
///
/// <para><b>Validation rejects the wrong values, not merely the impossible ones.</b> An empty
/// recipient list, an unknown report key, a cadence nobody implements and a zero-day window are all
/// refused — each of them would otherwise produce a subscription that saves cleanly, schedules
/// cleanly, and silently delivers nothing.</para>
///
/// <para><b>The schedule is computed here, not in the worker.</b> <c>NextRunOn</c> is written on
/// every save, so a row is never in the database without a due date that someone chose. The worker
/// only ever advances it.</para>
/// </summary>
public sealed class ReportSubscriptionService : IReportSubscriptionService
{
    /// <summary>Widest window the report builders will scan. Matches the dashboard's own ceiling.</summary>
    public const int MaximumWindowDays = 732;

    /// <summary>Monthly schedules are capped at the 28th; see <see cref="ReportSubscription.DayOfMonth"/>.</summary>
    public const int MaximumDayOfMonth = 28;

    private readonly ErpRfqAutomationContext _db;

    public ReportSubscriptionService(ErpRfqAutomationContext db) => _db = db;

    public async Task<IReadOnlyList<ReportSubscription>> ListAsync(long businessUnitId, CancellationToken ct = default) =>
        await _db.Set<ReportSubscription>().AsNoTracking()
            .Where(s => s.BusinessUnitId == businessUnitId)
            .OrderBy(s => s.ReportKey).ThenBy(s => s.Cadence)
            .ToListAsync(ct);

    public async Task<ReportSubscription> UpsertAsync(long businessUnitId, string actor,
        UpsertReportSubscriptionCommand command, CancellationToken ct = default)
    {
        if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));

        if (!ReportKeys.IsKnown(command.ReportKey))
            throw new ReportSubscriptionValidationException(
                $"'{command.ReportKey}' is not a report this system produces. Choose one of: {string.Join(", ", ReportKeys.All)}.");
        if (!ReportCadences.IsKnown(command.Cadence))
            throw new ReportSubscriptionValidationException(
                $"'{command.Cadence}' is not a supported cadence. Choose one of: {string.Join(", ", ReportCadences.All)}.");
        if (!ReportFormats.IsKnown(command.Format))
            throw new ReportSubscriptionValidationException(
                $"'{command.Format}' is not a supported format. Choose one of: {string.Join(", ", ReportFormats.All)}.");
        if (command.HourUtc is < 0 or > 23)
            throw new ReportSubscriptionValidationException("The delivery hour must be between 0 and 23 UTC.");
        if (command.Cadence == ReportCadences.Weekly && command.DayOfWeek is < 0 or > 6)
            throw new ReportSubscriptionValidationException(
                "The weekly delivery day must be between 0 (Sunday) and 6 (Saturday).");
        if (command.Cadence == ReportCadences.Monthly && command.DayOfMonth is < 1 or > MaximumDayOfMonth)
            throw new ReportSubscriptionValidationException(
                $"The monthly delivery day must be between 1 and {MaximumDayOfMonth}. Later days do not exist "
                + "in every month, and a schedule that skips February is one nobody notices missing.");
        if (command.WindowDays is < 1 or > MaximumWindowDays)
            throw new ReportSubscriptionValidationException(
                $"The reporting window must be between 1 and {MaximumWindowDays} days.");

        var recipients = NormaliseRecipients(command.Recipients);
        if (recipients.Length == 0)
            throw new ReportSubscriptionValidationException(
                "At least one recipient email address is required. A subscription with no recipients would "
                + "schedule, run and deliver nothing.");

        var now = DateTime.UtcNow;
        var entity = command.Id is { } id and > 0
            ? await _db.Set<ReportSubscription>()
                  .FirstOrDefaultAsync(s => s.Id == id && s.BusinessUnitId == businessUnitId, ct)
              ?? throw new ReportSubscriptionValidationException("That report subscription does not exist.")
            : new ReportSubscription { BusinessUnitId = businessUnitId, CreatedOn = now, CreatedBy = actor };

        if (entity.Id == 0) _db.Set<ReportSubscription>().Add(entity);
        else { entity.ModifiedOn = now; entity.ModifiedBy = actor; }

        entity.ReportKey = command.ReportKey;
        entity.Cadence = command.Cadence;
        entity.Format = command.Format;
        entity.HourUtc = command.HourUtc;
        entity.DayOfWeek = command.Cadence == ReportCadences.Weekly ? command.DayOfWeek : 0;
        entity.DayOfMonth = command.Cadence == ReportCadences.Monthly ? command.DayOfMonth : 1;
        entity.WindowDays = command.WindowDays;
        entity.Recipients = string.Join(';', recipients);
        entity.IsActive = command.IsActive;

        // A paused subscription has no due date at all. Null is "not scheduled", which is the
        // fail-safe reading; leaving a stale past date behind would make an un-pause fire instantly.
        entity.NextRunOn = command.IsActive ? NextOccurrence(entity, now) : null;

        await _db.SaveChangesAsync(ct);
        return entity;
    }

    public async Task<bool> DeleteAsync(long businessUnitId, long id, CancellationToken ct = default)
    {
        var deleted = await _db.Set<ReportSubscription>()
            .Where(s => s.Id == id && s.BusinessUnitId == businessUnitId)
            .ExecuteDeleteAsync(ct);
        return deleted > 0;
    }

    internal static string[] NormaliseRecipients(string? raw) =>
        (raw ?? string.Empty)
        .Split(new[] { ';', ',', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(address => address.Contains('@') && address.Length <= 254)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// The next due instant strictly after <paramref name="after"/>.
    ///
    /// <para>Strictly after, never equal: a run that completes at exactly the due instant must not
    /// re-schedule itself for the same instant, or the worker loops on one occurrence forever.</para>
    /// </summary>
    public static DateTime NextOccurrence(ReportSubscription subscription, DateTime after)
    {
        var hour = Math.Clamp(subscription.HourUtc, 0, 23);

        switch (subscription.Cadence)
        {
            case ReportCadences.Daily:
            {
                var candidate = new DateTime(after.Year, after.Month, after.Day, hour, 0, 0, DateTimeKind.Utc);
                return candidate > after ? candidate : candidate.AddDays(1);
            }

            case ReportCadences.Monthly:
            {
                var day = Math.Clamp(subscription.DayOfMonth, 1, MaximumDayOfMonth);
                var candidate = new DateTime(after.Year, after.Month, day, hour, 0, 0, DateTimeKind.Utc);
                return candidate > after ? candidate : candidate.AddMonths(1);
            }

            default:
            {
                var target = Math.Clamp(subscription.DayOfWeek, 0, 6);
                var candidate = new DateTime(after.Year, after.Month, after.Day, hour, 0, 0, DateTimeKind.Utc);
                var shift = (target - (int)candidate.DayOfWeek + 7) % 7;
                candidate = candidate.AddDays(shift);
                return candidate > after ? candidate : candidate.AddDays(7);
            }
        }
    }
}
