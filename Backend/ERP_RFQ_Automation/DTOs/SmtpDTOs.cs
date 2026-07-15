using Microsoft.AspNetCore.Http;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs
{
    public class SmtpSettingsDTO
    {
        public string Host { get; set; } = null!;
        public int Port { get; set; }
        public string Username { get; set; } = null!;
        public string Password { get; set; } = null!;
        public bool UseSsl { get; set; }
    }

    public class SendSupplierEmailRequestDTO
    {
        public SmtpSettingsDTO SmtpSettings { get; set; } = null!;
        public string ToEmail { get; set; } = null!;
        public string Subject { get; set; } = null!;
        public string Body { get; set; } = null!;
        public List<IFormFile>? Attachments { get; set; }
    }
}
