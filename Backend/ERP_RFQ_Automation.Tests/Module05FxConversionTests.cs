using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Multi-currency FX authority.
///
/// The defect these cover: Currency.ExchangeRate was stored, CRUD'd and filtered but never
/// applied, so the commercial side summed AED, USD and EUR as bare decimals. These tests pin the
/// four properties the fix depends on — conversion correctness, effective-dated selection,
/// fail-closed behaviour, and the General Ledger rounding/precision conventions — plus the
/// approved-snapshot guarantee that makes a past conversion reproducible.
/// </summary>
public sealed class Module05FxConversionTests
{
    private const long Bu = 1;
    private const long Aed = 100;   // base
    private const long Usd = 101;
    private const long Eur = 102;
    private const long Gbp = 103;

    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Jun1 = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Aug1 = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Unspecified);

    private static void SeedCurrencies(ErpRfqAutomationContext db, bool aedIsBase = true)
    {
        Seed.EnsureBusinessUnit(db, Bu);
        db.Currencies.Add(new Currency
        {
            Id = Aed, BusinessUnitId = Bu, Code = "AED", CurrencyName = "UAE Dirham",
            IsBaseCurrency = aedIsBase, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
        });
        db.Currencies.Add(new Currency
        {
            Id = Usd, BusinessUnitId = Bu, Code = "USD", CurrencyName = "US Dollar",
            IsBaseCurrency = false, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
        });
        db.Currencies.Add(new Currency
        {
            Id = Eur, BusinessUnitId = Bu, Code = "EUR", CurrencyName = "Euro",
            IsBaseCurrency = false, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
        });
        db.Currencies.Add(new Currency
        {
            Id = Gbp, BusinessUnitId = Bu, Code = "GBP", CurrencyName = "Pound Sterling",
            IsBaseCurrency = false, IsActive = true, CreatedBy = "test", CreatedOn = DateTime.UtcNow,
        });
    }

    private static FxRate ApprovedRate(long from, long to, decimal rate, DateTime effectiveFrom,
        DateTime? effectiveTo = null) => new()
    {
        BusinessUnitId = Bu, FromCurrencyId = from, ToCurrencyId = to, Rate = rate,
        EffectiveFrom = effectiveFrom, EffectiveTo = effectiveTo,
        Source = "Manual", Status = FxRateStatuses.Approved,
        ApprovedBy = "treasury", ApprovedOn = effectiveFrom,
        Version = 1, CreatedBy = "test", CreatedOn = effectiveFrom,
    };

    // ------------------------------------------------------------- conversion correctness

    [Fact]
    public async Task Direct_rate_converts_and_identity_pair_needs_no_rate()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        var direct = await fx.ResolveRateAsync(Bu, Usd, Aed, Aug1);
        Assert.True(direct.Found);
        Assert.Equal(3.6725m, direct.Rate);
        Assert.Equal(FxResolutionPaths.Direct, direct.ResolutionPath);

        // A currency against itself is always exactly 1 and must never require a stored rate.
        var identity = await fx.ResolveRateAsync(Bu, Aed, Aed, Aug1);
        Assert.True(identity.Found);
        Assert.Equal(1m, identity.Rate);
        Assert.Equal(FxResolutionPaths.Identity, identity.ResolutionPath);

        var total = await fx.TotalAsync(Bu, new[] { new FxAmount(1000m, Usd), new FxAmount(500m, Aed) }, Aug1);
        Assert.True(total.Converted);
        // 1000 USD * 3.6725 = 3672.50, + 500 AED = 4172.50
        Assert.Equal(4172.50m, total.Total);
        Assert.Equal("AED", total.TargetCurrencyCode);
    }

    [Fact]
    public async Task Inverse_and_triangulated_paths_resolve_and_are_labelled()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            // Only the *->AED directions are quoted, as a Gulf trading house would hold them.
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            seed.FxRates.Add(ApprovedRate(Eur, Aed, 4.0000m, Jan1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        // AED -> USD exists only as the reciprocal of the stored USD -> AED row.
        var inverse = await fx.ResolveRateAsync(Bu, Aed, Usd, Aug1);
        Assert.True(inverse.Found);
        Assert.Equal(FxResolutionPaths.Inverse, inverse.ResolutionPath);
        Assert.Equal(decimal.Round(1m / 3.6725m, 10, MidpointRounding.AwayFromZero), inverse.Rate);

        // EUR -> USD is quoted in neither direction; it must cross through the AED base.
        var triangulated = await fx.ResolveRateAsync(Bu, Eur, Usd, Aug1);
        Assert.True(triangulated.Found);
        Assert.Equal(FxResolutionPaths.Triangulated, triangulated.ResolutionPath);
        // EUR->AED (4.0) * AED->USD (1/3.6725), rounded once at the 10dp rate scale.
        Assert.Equal(decimal.Round(4.0000m * (1m / 3.6725m), 10, MidpointRounding.AwayFromZero), triangulated.Rate);
    }

    // ------------------------------------------------------------- effective-dated selection

    [Fact]
    public async Task Rate_selection_honours_the_effective_window_and_picks_the_latest_start()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            // Two consecutive windows plus a later open-ended one.
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6000m, Jan1, Jun1));
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jun1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        // Inside the first window.
        var march = await fx.ResolveRateAsync(Bu, Usd, Aed, new DateTime(2026, 3, 15));
        Assert.True(march.Found);
        Assert.Equal(3.6000m, march.Rate);

        // EffectiveFrom is INCLUSIVE, EffectiveTo EXCLUSIVE: on the boundary the new rate wins.
        var boundary = await fx.ResolveRateAsync(Bu, Usd, Aed, Jun1);
        Assert.True(boundary.Found);
        Assert.Equal(3.6725m, boundary.Rate);

        var later = await fx.ResolveRateAsync(Bu, Usd, Aed, Aug1);
        Assert.True(later.Found);
        Assert.Equal(3.6725m, later.Rate);

        // Before ANY window starts there is no rate — the earliest row must not leak backwards.
        var before = await fx.ResolveRateAsync(Bu, Usd, Aed, new DateTime(2025, 12, 31));
        Assert.False(before.Found);
        Assert.Contains("No approved USD to AED exchange rate", before.Reason);
    }

    [Fact]
    public async Task Unapproved_and_expired_rates_are_invisible_to_conversion()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            var pending = ApprovedRate(Usd, Aed, 3.6725m, Jan1);
            pending.Status = FxRateStatuses.Pending;
            pending.ApprovedBy = null;
            pending.ApprovedOn = null;
            seed.FxRates.Add(pending);

            var superseded = ApprovedRate(Eur, Aed, 4.0m, Jan1);
            superseded.Status = FxRateStatuses.Superseded;
            seed.FxRates.Add(superseded);

            // Approved but its window has already closed.
            seed.FxRates.Add(ApprovedRate(Gbp, Aed, 4.6m, Jan1, Jun1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        Assert.False((await fx.ResolveRateAsync(Bu, Usd, Aed, Aug1)).Found);
        Assert.False((await fx.ResolveRateAsync(Bu, Eur, Aed, Aug1)).Found);
        Assert.False((await fx.ResolveRateAsync(Bu, Gbp, Aed, Aug1)).Found);
    }

    // ------------------------------------------------------------------ fail-closed

    [Fact]
    public async Task Total_fails_closed_when_any_currency_has_no_rate_and_still_reports_the_breakdown()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            // Deliberately NO EUR rate.
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        var total = await fx.TotalAsync(Bu, new[]
        {
            new FxAmount(1000m, Usd),
            new FxAmount(2000m, Eur),
            new FxAmount(500m, Aed),
        }, Aug1);

        // The whole answer is withheld — NOT the convertible subset summed and passed off as the total.
        Assert.False(total.Converted);
        Assert.Null(total.Total);
        Assert.NotNull(total.UnavailableReason);
        Assert.Contains("EUR", total.UnavailableReason);

        // ...but the honest per-currency breakdown is still available to show the user.
        Assert.Equal(3, total.Components.Count);
        var eur = Assert.Single(total.Components, c => c.CurrencyCode == "EUR");
        Assert.False(eur.Converted);
        Assert.Equal(2000m, eur.Subtotal);
        Assert.Null(eur.ConvertedSubtotal);

        var usd = Assert.Single(total.Components, c => c.CurrencyCode == "USD");
        Assert.True(usd.Converted);
        Assert.Equal(3672.50m, usd.ConvertedSubtotal);
    }

    [Fact]
    public async Task Amount_without_a_currency_is_never_folded_into_a_total()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        var total = await fx.TotalAsync(Bu, new[]
        {
            new FxAmount(1000m, Usd),
            new FxAmount(9999m, null),   // a quote with no CurrencyId
        }, Aug1);

        Assert.False(total.Converted);
        Assert.Null(total.Total);
        Assert.Contains("no currency", total.UnavailableReason);
    }

    [Fact]
    public async Task Ambiguous_or_missing_base_currency_fails_closed()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed, aedIsBase: false);   // nobody is the base
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        Assert.Null(await fx.ResolveBaseCurrencyIdAsync(Bu));
        var total = await fx.TotalAsync(Bu, new[] { new FxAmount(1000m, Usd) }, Aug1);
        Assert.False(total.Converted);
        Assert.Null(total.Total);
        Assert.Contains("no single active base currency", total.UnavailableReason);
    }

    [Fact]
    public async Task Capture_snapshot_throws_the_explicit_reason_when_no_rate_exists()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        var ex = await Assert.ThrowsAsync<FxConversionException>(() =>
            fx.CaptureSnapshotAsync(Bu, FxDocumentTypes.Quote, 77, Usd, Aed, Aug1, "auditor"));
        Assert.Contains("No approved USD to AED exchange rate", ex.Message);
    }

    // ------------------------------------------------------------- reproducible snapshot

    [Fact]
    public async Task Snapshot_freezes_the_rate_so_a_later_correction_cannot_restate_the_quote()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            await seed.SaveChangesAsync();
        }

        // The quote is converted and its rate frozen.
        await using (var db = database.ContextFor(Bu))
        {
            var fx = new FxConversionService(db);
            var snapshot = await fx.CaptureSnapshotAsync(Bu, FxDocumentTypes.Quote, 77, Usd, Aed, Aug1, "sales@nexora");
            Assert.Equal(3.6725m, snapshot.Rate);
            Assert.Equal(FxResolutionPaths.Direct, snapshot.ResolutionPath);

            // Idempotent: asking again returns the SAME frozen row, never a re-resolved one.
            var again = await fx.CaptureSnapshotAsync(Bu, FxDocumentTypes.Quote, 77, Usd, Aed, Aug1, "someone-else");
            Assert.Equal(snapshot.Id, again.Id);
            Assert.Equal("sales@nexora", again.CapturedBy);
        }

        // Treasury now supersedes the rate with a very different one.
        await using (var correct = database.ContextFor(null))
        {
            correct.FxRates.Add(ApprovedRate(Usd, Aed, 9.9999m, Aug1.AddDays(1)));
            await correct.SaveChangesAsync();
        }

        await using (var db = database.ContextFor(Bu))
        {
            var fx = new FxConversionService(db);
            var later = Aug1.AddDays(30);

            // A fresh conversion sees the new rate...
            var live = await fx.TotalAsync(Bu, new[] { new FxAmount(1000m, Usd) }, later);
            Assert.Equal(9999.90m, live.Total);

            // ...but the already-converted quote replays its frozen rate, unchanged.
            var replay = await fx.TotalForDocumentAsync(Bu, FxDocumentTypes.Quote, 77,
                new[] { new FxAmount(1000m, Usd) }, later);
            Assert.True(replay.Converted);
            Assert.Equal(3672.50m, replay.Total);

            // And the auditor's question has a stable answer.
            var snapshots = await fx.GetSnapshotsAsync(Bu, FxDocumentTypes.Quote, 77);
            var only = Assert.Single(snapshots);
            Assert.Equal(3.6725m, only.Rate);
            Assert.Equal(Aug1, only.AsOf);
            Assert.Equal("sales@nexora", only.CapturedBy);
        }
    }

    // ---------------------------------------------------------------- rounding / precision

    [Fact]
    public void Money_and_rate_rounding_match_the_general_ledger_conventions()
    {
        // Money: 2dp away-from-zero, identical to GeneralLedgerService.Round.
        Assert.Equal(2.35m, FxConversionService.RoundMoney(2.345m));
        Assert.Equal(-2.35m, FxConversionService.RoundMoney(-2.345m));
        Assert.Equal(2.34m, FxConversionService.RoundMoney(2.344m));
        // Away-from-zero, NOT banker's rounding (which would give 2.34 / 2.36 here).
        Assert.Equal(2.36m, FxConversionService.RoundMoney(2.355m));

        // Rate: 10dp away-from-zero, identical to GeneralLedgerService.NormalizeLines.
        Assert.Equal(0.1234567891m, FxConversionService.RoundRate(0.12345678905m));
        Assert.Equal(3.6725m, FxConversionService.RoundRate(3.6725m));
    }

    [Fact]
    public async Task Conversion_rounds_once_after_applying_the_rate_not_per_row()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        // Three rows that each convert to a half-cent; rounding per row would give
        // 3 * 0.37 = 1.11, whereas rounding the currency subtotal once gives 1.10.
        var total = await fx.TotalAsync(Bu, new[]
        {
            new FxAmount(0.1m, Usd), new FxAmount(0.1m, Usd), new FxAmount(0.1m, Usd),
        }, Aug1);

        Assert.True(total.Converted);
        // 0.3 USD * 3.6725 = 1.101750 -> 1.10
        Assert.Equal(1.10m, total.Total);

        var component = Assert.Single(total.Components);
        Assert.Equal(0.30m, component.Subtotal);
        Assert.Equal(3.6725m, component.Rate);
    }

    [Fact]
    public async Task Rate_is_persisted_and_read_back_at_full_ten_decimal_scale()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.1234567891m, Jan1));
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);
        var resolved = await fx.ResolveRateAsync(Bu, Usd, Aed, Aug1);

        Assert.True(resolved.Found);
        Assert.Equal(3.1234567891m, resolved.Rate);
    }

    // ---------------------------------------------------------- tenant isolation

    [Fact]
    public async Task Rates_from_another_business_unit_are_never_used()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            Seed.EnsureBusinessUnit(seed, 2);
            // A rate that belongs to a DIFFERENT tenant for the same currency pair.
            var foreign = ApprovedRate(Usd, Aed, 3.6725m, Jan1);
            foreign.BusinessUnitId = 2;
            seed.FxRates.Add(foreign);
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var fx = new FxConversionService(db);

        var resolved = await fx.ResolveRateAsync(Bu, Usd, Aed, Aug1);
        Assert.False(resolved.Found);
    }

    // ------------------------------------------------------- the original reported defect

    [Fact]
    public async Task QuoteStats_no_longer_sums_mixed_currency_totals_as_bare_decimals()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            seed.Quotes.Add(new Quote
            {
                Id = 1, BusinessUnitId = Bu, QuoteNo = "Q-1", TotalAmount = 1000m, CurrencyId = Usd,
                CreatedBy = "test", CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow,
            });
            seed.Quotes.Add(new Quote
            {
                Id = 2, BusinessUnitId = Bu, QuoteNo = "Q-2", TotalAmount = 500m, CurrencyId = Aed,
                CreatedBy = "test", CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var stats = await new QuoteRepository(db).GetQuoteStatsAsync(Bu);

        Assert.Equal(2, stats.TotalQuotes);
        // The old code returned 1500 — 1000 USD + 500 AED added as plain numbers.
        Assert.True(stats.TotalQuotedAmountConverted);
        Assert.Equal(4172.50m, stats.TotalQuotedAmount);
        Assert.Equal("AED", stats.TotalQuotedAmountCurrency);
        Assert.Equal(2, stats.QuotedAmountsByCurrency.Count);
    }

    [Fact]
    public async Task QuoteStats_withholds_the_total_when_a_quote_currency_has_no_rate()
    {
        using var database = new TestDb();
        await using (var seed = database.ContextFor(null))
        {
            SeedCurrencies(seed);
            // No EUR rate is recorded.
            seed.FxRates.Add(ApprovedRate(Usd, Aed, 3.6725m, Jan1));
            seed.Quotes.Add(new Quote
            {
                Id = 1, BusinessUnitId = Bu, QuoteNo = "Q-1", TotalAmount = 1000m, CurrencyId = Usd,
                CreatedBy = "test", CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow,
            });
            seed.Quotes.Add(new Quote
            {
                Id = 2, BusinessUnitId = Bu, QuoteNo = "Q-2", TotalAmount = 2000m, CurrencyId = Eur,
                CreatedBy = "test", CreatedDate = DateTime.UtcNow, QuoteDate = DateTime.UtcNow,
            });
            await seed.SaveChangesAsync();
        }

        await using var db = database.ContextFor(Bu);
        var stats = await new QuoteRepository(db).GetQuoteStatsAsync(Bu);

        Assert.Equal(2, stats.TotalQuotes);
        Assert.False(stats.TotalQuotedAmountConverted);
        Assert.Null(stats.TotalQuotedAmount);          // never 0, and never a partial sum
        Assert.NotNull(stats.TotalQuotedAmountUnavailableReason);
        Assert.Contains("EUR", stats.TotalQuotedAmountUnavailableReason);
        // The per-currency truth is still reported.
        Assert.Equal(2, stats.QuotedAmountsByCurrency.Count);
        Assert.Contains(stats.QuotedAmountsByCurrency, c => c.CurrencyCode == "EUR" && !c.Converted);
    }
}
