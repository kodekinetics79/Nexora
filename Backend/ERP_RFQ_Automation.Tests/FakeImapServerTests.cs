using ERP_RFQ_Automation.Tests.Support;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Bring-up proof for <see cref="FakeImapServer"/>: the REAL MailKit <see cref="ImapClient"/>
/// (the exact client <c>EmailService</c> uses) walks the full poll-path command surface —
/// connect, LOGIN, SELECT INBOX, UID SEARCH SENTSINCE, envelope fetch, message download,
/// +FLAGS \Seen, LOGOUT — against it. MailKit is strict about untagged response grammar, so
/// this is the test that catches a malformed ENVELOPE before a journey test drowns it in
/// pipeline noise.
/// </summary>
public sealed class FakeImapServerTests
{
    [Fact]
    public async Task MailKit_can_walk_the_entire_poll_command_surface()
    {
        await using var server = new FakeImapServer();
        var englishUid = server.AddMessage(CorpusManifest.Bytes("email-simple-english.eml"));
        var arabicUid = server.AddMessage(CorpusManifest.Bytes("email-simple-arabic.eml"));

        using var client = new ImapClient();
        await client.ConnectAsync("127.0.0.1", server.Port, SecureSocketOptions.None);
        await client.AuthenticateAsync(server.Username, server.Password);

        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadWrite);

        // SENTSINCE at day granularity, inclusive — corpus messages are dated 2026-08-10.
        var uids = await inbox.SearchAsync(SearchQuery.SentSince(new DateTime(2026, 8, 1)));
        Assert.Equal(2, uids.Count);

        var none = await inbox.SearchAsync(SearchQuery.SentSince(new DateTime(2030, 1, 1)));
        Assert.Empty(none);

        var summaries = await inbox.FetchAsync(uids,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Envelope);
        Assert.Equal(2, summaries.Count);
        var english = summaries.Single(s => s.UniqueId.Id == englishUid);
        Assert.Equal("corpus-simple-en@corpus.nexora.example", english.Envelope!.MessageId);
        Assert.Contains("RFQ-CORPUS-001", english.Envelope.Subject);
        Assert.Equal("ahmed@alnoortrading.ae",
            english.Envelope.From.Mailboxes.Single().Address);

        // Non-ASCII subject round-trips through the RFC 2047-encoded envelope.
        var arabic = summaries.Single(s => s.UniqueId.Id == arabicUid);
        Assert.Contains("RFQ-CORPUS-002", arabic.Envelope!.Subject);
        Assert.Contains("طلب", arabic.Envelope.Subject);

        // Full message download returns the corpus bytes, parseable and value-identical.
        var message = await inbox.GetMessageAsync(new UniqueId(englishUid));
        Assert.Equal("corpus-simple-en@corpus.nexora.example", message.MessageId);
        Assert.Contains("40 nos cable tray", message.TextBody);

        await inbox.AddFlagsAsync(new UniqueId(englishUid), MessageFlags.Seen, true);
        Assert.Contains(englishUid, server.SeenUids);

        await client.DisconnectAsync(true);
        Assert.Contains(server.CommandLog, line =>
            line.Contains("UID SEARCH", StringComparison.OrdinalIgnoreCase)
            && line.Contains("SENTSINCE", StringComparison.OrdinalIgnoreCase));
    }
}
