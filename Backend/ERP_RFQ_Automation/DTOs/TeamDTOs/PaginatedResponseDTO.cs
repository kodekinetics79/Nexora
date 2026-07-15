using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.TeamDTOs
{
    public class PaginatedResponseDTO<T>
    {
        public IEnumerable<T> Items { get; set; } = new List<T>();
        public int TotalCount { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
    }
}