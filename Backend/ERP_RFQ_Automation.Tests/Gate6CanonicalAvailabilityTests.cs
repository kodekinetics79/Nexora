using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Inventory.Commercial;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Gate 6 — available-to-promise is defined once.
///
/// <para><b>Why one of these tests reads source code.</b> The four sites that re-derived ATP by
/// hand all produced the same number as the canonical function on the day they were found. A
/// behavioural test therefore cannot distinguish "routes through
/// <see cref="InventoryQuantityMath.AvailableToPromise"/>" from "happens to agree with it today" —
/// it would pass identically before and after the fix, which makes it worthless as proof that the
/// wiring exists. The failure mode being guarded against is not a wrong answer now; it is a
/// seventh bucket added to the canonical function next year and missed by a hand-written copy,
/// exactly as the file's own comment records happening once already. The only thing that fails
/// when that wiring is removed is the absence of the hand-written chain, so that is what is
/// asserted.</para>
/// </summary>
public sealed class Gate6CanonicalAvailabilityTests
{
    /// <summary>The subtraction chain a hand-rolled ATP always ends with.</summary>
    private static readonly string[] HandRolledFragments =
    [
        "QuarantineQuantity - x.DamagedQuantity",
        "QuarantineQuantity - stock.DamagedQuantity",
        "Quarantine - Damaged - Expired - SafetyStock",
    ];

    /// <summary>
    /// The files that used to carry their own copy. Named individually rather than scanned
    /// repo-wide so a new site is a deliberate addition to this list, not a silent pass.
    /// </summary>
    public static TheoryData<string> Sites =>
    [
        "Procurement/ProcurementApplicationService.cs",
        "CommercialLearning/CommercialLearningService.cs",
        "Inventory/Commercial/CommercialInventoryEntities.cs",
        "Repositories/ProductRepository.cs",
        "Controllers/InventoryIntelligenceController.cs",
    ];

    [Theory]
    [MemberData(nameof(Sites))]
    public void No_module_re_derives_available_to_promise_by_hand(string relativePath)
    {
        var source = File.ReadAllText(SourcePath(relativePath));

        foreach (var fragment in HandRolledFragments)
            Assert.DoesNotContain(fragment, source, StringComparison.Ordinal);

        Assert.Contains("InventoryQuantityMath.AvailableToPromise", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_commercial_snapshot_agrees_with_the_canonical_function_bucket_for_bucket()
    {
        // Every bucket carries a distinct value, so a snapshot that dropped or double-counted any
        // one of them lands on a different number rather than coincidentally on the right one.
        var snapshot = new InventorySnapshot
        {
            BusinessUnitId = 1, ProductId = 1, InventoryId = 1, WarehouseId = 1, WarehouseCode = "W",
            OnHand = 1000m, Reserved = 17m, Allocated = 31m, Quarantine = 53m, Damaged = 71m,
            Expired = 97m, SafetyStock = 113m, AsOf = DateTime.UtcNow,
        };

        Assert.Equal(
            InventoryQuantityMath.AvailableToPromise(1000m, 17m, 31m, 53m, 71m, 97m, 113m),
            snapshot.AvailableToPromise);
        Assert.Equal(618m, snapshot.AvailableToPromise);
    }

    [Fact]
    public void The_canonical_function_never_promises_a_negative_quantity()
    {
        // The clamp is what keeps every downstream sum monotonic: a row 40 units oversold must
        // contribute zero to a multi-warehouse total, not subtract from its siblings.
        Assert.Equal(0m, InventoryQuantityMath.AvailableToPromise(10m, 50m, 0m, 0m, 0m, 0m, 0m));
    }

    private static string SourcePath(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "ERP_RFQ_Automation", relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(
            $"Could not locate {relativePath} above {AppContext.BaseDirectory}.");
    }
}
