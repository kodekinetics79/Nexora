using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.SetupDTOs
{
    public class PaginatedSetupMasterResponseDTO
    {
        public IEnumerable<SetupMasterResponseDTO> Items { get; set; } = new List<SetupMasterResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}