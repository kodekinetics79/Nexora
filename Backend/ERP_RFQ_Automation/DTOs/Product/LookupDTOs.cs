namespace ERP_RFQ_Automation.DTOs.LookupDTOs
{
    public class BusinessUnitLookupDTO
    {
        public long Id { get; set; }
        public string BusinessUnitName { get; set; } = null!;
        public string? BusinessUnitCode { get; set; }
    }

    public class ProductCategoryLookupDTO
    {
        public long Id { get; set; }
        public string CategoryName { get; set; } = null!;
    }

    public class LookupItemDTO
    {
        public long Id { get; set; }
        public string Value { get; set; } = null!;
    }

    public class SupplierLookupDTO
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
    }

    public class ProductSubCategoryLookupDTO
    {
        public int Id { get; set; }
        public string SubCategoryName { get; set; } = null!;
    }

    public class WarehouseLookupDTO
    {
        public long Id { get; set; }
        public string WarehouseName { get; set; } = null!;
    }
    public class RFQTypeLookupDTO
    {
        public long Id { get; set; }
        public string RFQType { get; set; } = null!;
    }
}