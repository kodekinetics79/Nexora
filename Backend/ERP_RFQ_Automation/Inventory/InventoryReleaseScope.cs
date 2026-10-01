namespace ERP_RFQ_Automation.Inventory;

/// <summary>
/// Inventory capabilities offered by this release. Historical reservation rows remain readable,
/// but cannot reserve stock or be changed while reservations are outside the release scope.
/// </summary>
public static class InventoryReleaseScope
{
    public static bool ReservationsEnabled => false;
    public const string ReservationsDisabledMessage = "Inventory reservations are disabled for this release.";

    public static void RequireReservations()
    {
        if (!ReservationsEnabled)
            throw new InvalidOperationException(ReservationsDisabledMessage);
    }
}
