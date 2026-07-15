using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.Warehouse
{
    public class PaginatedWarehouseResponseDTO
    {
        public IEnumerable<WarehouseResponseDTO> Items { get; set; } = new List<WarehouseResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}