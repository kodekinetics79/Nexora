namespace ERP_RFQ_Automation.DTOs.CustomerDTOs
{
    public class PaginatedCustomerResponseDTO
    {
        public List<CustomerResponseDTO> Items { get; set; } = new();
        public int TotalItems { get; set; }
        public int PageNumber { get; set; }
        public int PageSize { get; set; }
        public int TotalPages { get; set; }
    }
}
