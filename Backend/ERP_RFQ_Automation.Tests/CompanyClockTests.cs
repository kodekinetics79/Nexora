using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A buyer's closing time is stored as the buyer printed it (SEC "9/6/2026 5:00 PM" →
/// 2026-09-06 17:00, no zone) and is read on the company's clock, in the time zone chosen when
/// the company was created. Before, every deadline compared it with UTC: a Riyadh 5 PM close
/// stayed open until 8 PM and "today" turned over at 3 AM.
/// </summary>
public sealed class CompanyClockTests
{
    private const long Bu = 7_301;
    private const string Riyadh = "Asia/Riyadh";

    // 2026-09-05 22:00 UTC = 2026-09-06 01:00 in Riyadh: the closing day has begun there, not in UTC.
    private static readonly DateTimeOffset JustAfterRiyadhMidnight = new(2026, 9, 5, 22, 0, 0, TimeSpan.Zero);
    private static readonly DateTime SecClose = new(2026, 9, 6, 17, 0, 0);

    [Fact]
    public void Wall_clock_is_the_company_zone_and_utc_when_none_is_set()
    {
        var at = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 9, 6, 17, 30, 0), CompanyClock.WallClock(at, Riyadh));
        Assert.Equal(new DateTime(2026, 9, 6, 14, 30, 0), CompanyClock.WallClock(at, null));
        Assert.Equal(new DateTime(2026, 9, 6, 14, 30, 0), CompanyClock.WallClock(at, "  "));
        // A zone this server does not know is not guessed at.
        Assert.Equal(new DateTime(2026, 9, 6, 14, 30, 0), CompanyClock.WallClock(at, "Mars/Olympus"));
        Assert.Equal(DateTimeKind.Unspecified, CompanyClock.WallClock(at, Riyadh).Kind);
    }

    [Fact]
    public void A_5pm_riyadh_close_has_passed_at_5_30pm_riyadh_although_utc_says_2_30pm()
    {
        var at = new DateTimeOffset(2026, 9, 6, 14, 30, 0, TimeSpan.Zero);

        Assert.True(SecClose < CompanyClock.WallClock(at, Riyadh));
        Assert.False(SecClose < CompanyClock.WallClock(at, null));
    }

    [Fact]
    public async Task The_company_zone_rides_on_the_access_snapshot()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed, Riyadh);
            seed.SaveChanges();
        }

        using var ctx = db.ContextFor(null);
        var access = Access(ctx);
        Assert.Equal(Riyadh, (await access.GetAccessAsync(Bu)).TimeZoneId);

        var clock = new CompanyClock(access, new FixedTime(JustAfterRiyadhMidnight));
        Assert.Equal(new DateTime(2026, 9, 6, 1, 0, 0), await clock.NowAsync(Bu));
    }

    [Theory]
    [InlineData(Riyadh, 0, "today")]
    [InlineData(null, 1, "days_1_3")]
    public async Task Deadline_board_counts_days_on_the_company_calendar(
        string? timeZoneId, int expectedDaysLeft, string expectedBucket)
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            SeedTenant(seed, timeZoneId);
            var lead = Seed.Lead(seed, 7_302, Bu);
            lead.BidClosingDate = SecClose;
            seed.SaveChanges();
        }

        await using var ctx = db.ContextFor(Bu);
        var clock = new CompanyClock(Access(ctx), new FixedTime(JustAfterRiyadhMidnight));
        var board = await new DashboardRepository(ctx, clock).GetDeadlineBoardAsync(Bu);

        var row = Assert.Single(board.Leads);
        Assert.Equal(expectedDaysLeft, row.DaysLeft);
        Assert.Equal(expectedBucket, row.Bucket);
    }

    private static ITenantAccessService Access(ERP_RFQ_Automation.Models.ErpRfqAutomationContext ctx)
        => new TenantAccessService(ctx, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<TenantAccessService>.Instance);

    private static void SeedTenant(ERP_RFQ_Automation.Models.ErpRfqAutomationContext ctx, string? timeZoneId)
    {
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.Set<Tenant>().Add(new Tenant
        {
            Id = 7_301,
            Name = "Riyadh Trading",
            Slug = "riyadh-trading",
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = Bu,
            TimeZoneId = timeZoneId
        });
    }

    private sealed class FixedTime(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
