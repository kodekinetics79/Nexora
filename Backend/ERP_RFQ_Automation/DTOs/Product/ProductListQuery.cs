namespace ERP_RFQ_Automation.DTOs.ProductDTOs;

/// <summary>Allow-listed register query; all filtering and sorting happen before paging.</summary>
public sealed record ProductListQuery(
    string Stock = "all", long? WarehouseId = null,
    string SortBy = "partNo", string SortDirection = "asc")
{
    public static readonly string[] StockFilters = ["all", "in-stock", "out-of-stock", "low-stock"];
    public static readonly string[] SortFields =
        ["name", "partNo", "unit", "onHand", "available", "lastPurchaseCost", "landedCost", "salePrice", "currency"];

    public string? ValidationError => !StockFilters.Contains(Stock) ? "Unknown stock filter."
        : WarehouseId is <= 0 ? "Warehouse ID must be positive."
        : !SortFields.Contains(SortBy) ? "Unknown product sort field."
        : SortDirection is not ("asc" or "desc") ? "Sort direction must be asc or desc."
        : null;
}
