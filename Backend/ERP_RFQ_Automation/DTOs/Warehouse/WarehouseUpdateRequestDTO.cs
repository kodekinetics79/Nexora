using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.Warehouse
{
    public class WarehouseUpdateRequestDTO
    {
        [Required]
        public string WarehouseCode { get; set; } = null!;

        [Required]
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

        [Phone]
        public string? ContactPhone { get; set; }

        [EmailAddress]
        public string? ContactEmail { get; set; }

        [Required]
        public long BusinessUnitId { get; set; }

        public bool? IsActive { get; set; }

        public string? ModifiedBy { get; set; }
    }
}