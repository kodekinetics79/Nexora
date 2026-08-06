using System.Collections.Generic;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Services.Interfaces
{
    public interface IEmailService
    {
        /// <summary>
        /// Polls every active IMAP mailbox and returns what actually happened.
        ///
        /// ING-08: this returned <c>Task</c> until 2026-08-06, so a caller could not tell a
        /// drained mailbox from <c>AuthenticationException</c> — and both the poll loop and the
        /// manual-fetch endpoint reported success either way. The report is the truth; callers
        /// must not log success without consulting it.
        /// </summary>
        Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null);
        Task SendEmailAsync(string to, string subject, string body, List<(string FileName, byte[] FileContent, string ContentType)> attachments = null, string fromEmail = null, long? businessUnitId = null);
    }
}
