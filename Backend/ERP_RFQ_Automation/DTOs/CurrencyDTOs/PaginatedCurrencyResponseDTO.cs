using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.CurrencyDTOs
{
    public class PaginatedCurrencyResponseDTO
    {
        public IEnumerable<CurrencyResponseDTO> Items { get; set; } = new List<CurrencyResponseDTO>();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}
