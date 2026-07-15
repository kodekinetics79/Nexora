using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Services.Interfaces
{
    public interface IEmailService
    {
        Task FetchAndSaveLeadsAsync(long? businessUnitId = null);
        Task SendEmailAsync(string to, string subject, string body, List<(string FileName, byte[] FileContent, string ContentType)> attachments = null, string fromEmail = null, long? businessUnitId = null);
    }
}
