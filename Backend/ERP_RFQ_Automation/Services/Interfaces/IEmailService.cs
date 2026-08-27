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

        /// <summary>
        /// Cancellable polling contract used by the hosted email worker. The default keeps
        /// existing test doubles and non-worker callers source-compatible; production
        /// <see cref="EmailService"/> overrides it and carries the token through every IMAP
        /// network boundary.
        /// </summary>
        async Task<MailboxPollReport> FetchAndSaveLeadsAsync(
            long? businessUnitId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await FetchAndSaveLeadsAsync(businessUnitId);
        }
        Task SendEmailAsync(string to, string subject, string body, List<(string FileName, byte[] FileContent, string ContentType)> attachments = null, string fromEmail = null, long? businessUnitId = null);
    }
}
