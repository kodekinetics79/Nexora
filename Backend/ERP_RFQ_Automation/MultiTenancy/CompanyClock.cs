using ERP_RFQ_Automation.Platform.Entitlements;

namespace ERP_RFQ_Automation.MultiTenancy
{
    /// <summary>
    /// "Now" on the company's wall clock, in the time zone chosen when the company was created.
    ///
    /// <para>A buyer's closing date is stored as the buyer printed it: "9/6/2026 5:00 PM" is
    /// 2026-09-06 17:00 with no zone (a <c>timestamp without time zone</c> column). Comparing that
    /// with <c>DateTime.UtcNow</c> read a Riyadh 5 PM close as 5 PM UTC, so a bid stayed open three
    /// hours after it closed and "today" turned over at 3 AM. Every deadline comparison — days
    /// left, past deadline, closing soon, reminders — compares with this instead.</para>
    ///
    /// <para>No zone set, or the column unreadable: UTC, which is what every comparison did
    /// before. Never guessed from the country or the browser.</para>
    /// </summary>
    public interface ICompanyClock
    {
        /// <summary>The company's wall-clock time now (<see cref="DateTimeKind.Unspecified"/>).</summary>
        Task<DateTime> NowAsync(long businessUnitId, CancellationToken ct = default);
    }

    public sealed class CompanyClock : ICompanyClock
    {
        /// <summary>The clock for code built without one (tests, legacy constructors): UTC.</summary>
        public static readonly ICompanyClock Utc = new UtcCompanyClock(TimeProvider.System);

        private readonly ITenantAccessService _access;
        private readonly TimeProvider _time;

        public CompanyClock(ITenantAccessService access, TimeProvider? time = null)
        {
            _access = access;
            _time = time ?? TimeProvider.System;
        }

        public async Task<DateTime> NowAsync(long businessUnitId, CancellationToken ct = default)
        {
            var snapshot = await _access.GetAccessAsync(businessUnitId, ct);
            return WallClock(_time.GetUtcNow(), snapshot.TimeZoneId);
        }

        /// <summary>The wall-clock time in <paramref name="timeZoneId"/> at <paramref name="utcNow"/>;
        /// UTC when the zone is empty or unknown to this server.</summary>
        public static DateTime WallClock(DateTimeOffset utcNow, string? timeZoneId)
        {
            var zone = Find(timeZoneId);
            var local = zone is null ? utcNow.UtcDateTime : TimeZoneInfo.ConvertTime(utcNow, zone).DateTime;
            return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        }

        private static TimeZoneInfo? Find(string? timeZoneId)
        {
            if (string.IsNullOrWhiteSpace(timeZoneId))
                return null;
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                return null;
            }
        }

        private sealed class UtcCompanyClock(TimeProvider time) : ICompanyClock
        {
            public Task<DateTime> NowAsync(long businessUnitId, CancellationToken ct = default)
                => Task.FromResult(DateTime.SpecifyKind(time.GetUtcNow().UtcDateTime, DateTimeKind.Unspecified));
        }
    }
}
