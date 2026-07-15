using System;

namespace ERP_RFQ_Automation.DTOs.Warehouse
{
    public class WarehouseResponseDTO
    {
        public long Id { get; set; }
        public string WarehouseCode { get; set; } = null!;
        public string WarehouseName { get; set; } = null!;
        public string? Location { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public string? City { get; set; }
        public string? State { get; set; }
        public string? Country { get; set; }
        public string? PostalCode { get; set; }
        public decimal? Capacity { get; set; }
        public string? ManagerName { get; set; }
        public string? ContactPhone { get; set; }
        public string? ContactEmail { get; set; }
        public long BusinessUnitId { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }
}