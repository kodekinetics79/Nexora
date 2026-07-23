using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs
{
    public class SendSupplierEmailRequestDTO
    {
        public long SupplierId { get; set; }
        public string ToEmail { get; set; } = null!;
        public string Subject { get; set; } = null!;
        public string Body { get; set; } = null!;
        public List<IFormFile>? Attachments { get; set; }
    }
}
