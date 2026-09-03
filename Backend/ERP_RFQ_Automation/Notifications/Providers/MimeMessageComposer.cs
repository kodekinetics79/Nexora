using MimeKit;

namespace ERP_RFQ_Automation.Notifications.Providers
{
    /// <summary>
    /// Builds a MimeKit message from an <see cref="EmailMessage"/>. Shared by the platform SMTP
    /// sender and the tenant-mailbox sender so the two cannot drift on how a body, an attachment
    /// or a display name is encoded.
    /// </summary>
    internal static class MimeMessageComposer
    {
        public static MimeMessage Compose(
            EmailMessage message, string messageId, EmailAddress from, EmailAddress? defaultReplyTo)
        {
            var mime = new MimeMessage { MessageId = messageId, Subject = message.Subject };

            mime.From.Add(Address(from));

            foreach (var to in message.To) mime.To.Add(Address(to));
            foreach (var cc in message.Cc) mime.Cc.Add(Address(cc));
            foreach (var bcc in message.Bcc) mime.Bcc.Add(Address(bcc));

            var replyTo = message.ReplyTo ?? defaultReplyTo;
            if (replyTo is not null) mime.ReplyTo.Add(Address(replyTo));

            var builder = new BodyBuilder();
            if (!string.IsNullOrWhiteSpace(message.HtmlBody)) builder.HtmlBody = message.HtmlBody;
            if (!string.IsNullOrWhiteSpace(message.TextBody)) builder.TextBody = message.TextBody;

            // A body-less message is still a valid send (a bare subject line), but MimeKit needs
            // something to build; an empty text part keeps the message well-formed.
            if (string.IsNullOrWhiteSpace(builder.HtmlBody) && string.IsNullOrWhiteSpace(builder.TextBody))
                builder.TextBody = string.Empty;

            foreach (var attachment in message.Attachments)
                builder.Attachments.Add(
                    attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));

            mime.Body = builder.ToMessageBody();
            return mime;
        }

        /// <summary>
        /// MimeKit parses a display name and address together; the two are kept apart here because
        /// a display name containing a comma or an angle bracket would otherwise be read as extra
        /// recipients.
        /// </summary>
        public static MailboxAddress Address(EmailAddress address)
            => new(address.DisplayName ?? string.Empty, address.Address);
    }
}
