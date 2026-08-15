using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;
using MimeKit.Utils;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// A minimal in-process IMAP4rev1 server: plaintext, loopback only, speaking exactly the
/// command subset MailKit's <c>ImapClient</c> issues on <c>EmailService</c>'s poll path —
/// greeting/CAPABILITY, LOGIN, LIST (namespace discovery), SELECT INBOX, UID SEARCH
/// SENTSINCE, UID FETCH (UID ENVELOPE), UID FETCH (BODY.PEEK[]), UID STORE +FLAGS (\Seen)
/// and LOGOUT.
///
/// It exists because the suite had no way to prove the pipeline READS: every prior "success
/// path" test wrote the EmailIngest ledger by hand and pointed the client at 127.0.0.1:1 so
/// only failure paths ran. This server serves REAL corpus bytes over a REAL socket to the
/// REAL MailKit client, so the journey tests exercise connect → authenticate → search →
/// envelope fetch → message fetch → \Seen exactly as production does.
///
/// Deliberately not a general IMAP implementation: one mailbox (INBOX), one user, sequential
/// sessions, no IDLE/TLS/extensions. MailKit is strict about untagged response shapes, so the
/// ENVELOPE serializer below sticks to the RFC 3501 grammar (RFC 2047-encoding anything
/// non-ASCII first, which keeps every string a plain quoted-string).
/// </summary>
public sealed class FakeImapServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoop;
    private readonly List<StoredMessage> _messages = new();
    private readonly object _mutex = new();
    private uint _nextUid = 1;

    public string Username { get; }
    public string Password { get; }
    public int Port { get; }

    /// <summary>Every command line any session received, in order — journey tests assert on
    /// the \Seen STORE without trusting the server's own bookkeeping.</summary>
    public ConcurrentQueue<string> CommandLog { get; } = new();

    /// <summary>UIDs the client marked \Seen.</summary>
    public ConcurrentQueue<uint> SeenUids { get; } = new();

    public FakeImapServer(string username = "user", string password = "secret")
    {
        Username = username;
        Password = password;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Adds a message to INBOX from raw RFC 5322 bytes; returns its UID.</summary>
    public uint AddMessage(byte[] emlBytes)
    {
        var parsed = MimeMessage.Load(new MemoryStream(emlBytes));
        lock (_mutex)
        {
            var message = new StoredMessage(_nextUid++, emlBytes, parsed);
            _messages.Add(message);
            return message.Uid;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch { /* a session blocked mid-read is fine at teardown */ }
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (SocketException) { return; }
            catch (ObjectDisposedException) { return; }

            try
            {
                using (client)
                {
                    await HandleSessionAsync(client.GetStream());
                }
            }
            catch
            {
                // A dropped session must not kill the accept loop; the poller reconnects.
            }
        }
    }

    private async Task HandleSessionAsync(NetworkStream stream)
    {
        await WriteLineAsync(stream, "* OK [CAPABILITY IMAP4rev1] Nexora fake IMAP ready");

        while (true)
        {
            var line = await ReadLineAsync(stream);
            if (line is null) return;
            CommandLog.Enqueue(line);

            var tokens = Tokenize(line);
            if (tokens.Count < 2)
            {
                await WriteLineAsync(stream, $"{(tokens.Count > 0 ? tokens[0] : "*")} BAD malformed");
                continue;
            }
            var tag = tokens[0];
            var command = tokens[1].ToUpperInvariant();

            // MailKit may send string arguments as literals ({N}\r\n...). Collect them.
            while (line.EndsWith("}", StringComparison.Ordinal) && line.Contains('{'))
            {
                var open = line.LastIndexOf('{');
                if (!int.TryParse(line[(open + 1)..^1].TrimEnd('+'), out var length)) break;
                if (!line[..open].Contains(' ')) break;
                if (!line.EndsWith("+}", StringComparison.Ordinal))
                    await WriteLineAsync(stream, "+ Ready");
                var literal = new byte[length];
                await ReadExactAsync(stream, literal);
                var continuation = await ReadLineAsync(stream) ?? "";
                tokens.Add(Encoding.UTF8.GetString(literal));
                line = continuation.Length > 0 ? continuation : "";
                if (line.Length > 0)
                    foreach (var extra in Tokenize(" " + line)) tokens.Add(extra);
                if (!line.EndsWith("}", StringComparison.Ordinal)) break;
            }

            switch (command)
            {
                case "CAPABILITY":
                    await WriteLineAsync(stream, "* CAPABILITY IMAP4rev1");
                    await WriteLineAsync(stream, $"{tag} OK CAPABILITY completed");
                    break;

                case "LOGIN":
                    if (tokens.Count >= 4 && tokens[2] == Username && tokens[3] == Password)
                        await WriteLineAsync(stream, $"{tag} OK LOGIN completed");
                    else
                        await WriteLineAsync(stream, $"{tag} NO LOGIN failed");
                    break;

                case "LIST":
                    // Namespace discovery (LIST "" "") and folder listing both get INBOX.
                    if (tokens.Count >= 4 && tokens[3].Length == 0)
                        await WriteLineAsync(stream, "* LIST (\\Noselect) \"/\" \"\"");
                    else
                        await WriteLineAsync(stream, "* LIST () \"/\" \"INBOX\"");
                    await WriteLineAsync(stream, $"{tag} OK LIST completed");
                    break;

                case "SELECT":
                case "EXAMINE":
                {
                    List<StoredMessage> snapshot;
                    uint uidNext;
                    lock (_mutex)
                    {
                        snapshot = _messages.ToList();
                        uidNext = _nextUid;
                    }
                    await WriteLineAsync(stream, "* FLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft)");
                    await WriteLineAsync(stream, "* OK [PERMANENTFLAGS (\\Answered \\Flagged \\Deleted \\Seen \\Draft \\*)] Flags permitted");
                    await WriteLineAsync(stream, $"* {snapshot.Count} EXISTS");
                    await WriteLineAsync(stream, "* 0 RECENT");
                    await WriteLineAsync(stream, "* OK [UIDVALIDITY 1] UIDs valid");
                    await WriteLineAsync(stream, $"* OK [UIDNEXT {uidNext}] Predicted next UID");
                    await WriteLineAsync(stream, $"{tag} OK [READ-WRITE] SELECT completed");
                    break;
                }

                case "UID":
                {
                    var sub = tokens.Count > 2 ? tokens[2].ToUpperInvariant() : "";
                    if (sub == "SEARCH")
                        await HandleUidSearchAsync(stream, tag, tokens);
                    else if (sub == "FETCH")
                        await HandleUidFetchAsync(stream, tag, tokens, line);
                    else if (sub == "STORE")
                        await HandleUidStoreAsync(stream, tag, tokens, line);
                    else
                        await WriteLineAsync(stream, $"{tag} BAD unsupported UID subcommand");
                    break;
                }

                case "NOOP":
                    await WriteLineAsync(stream, $"{tag} OK NOOP completed");
                    break;

                case "LOGOUT":
                    await WriteLineAsync(stream, "* BYE Nexora fake IMAP signing off");
                    await WriteLineAsync(stream, $"{tag} OK LOGOUT completed");
                    return;

                default:
                    await WriteLineAsync(stream, $"{tag} BAD unsupported command");
                    break;
            }
        }
    }

    private async Task HandleUidSearchAsync(NetworkStream stream, string tag, List<string> tokens)
    {
        // The poll path issues exactly: UID SEARCH SENTSINCE <d-MMM-yyyy>. SENTSINCE compares
        // the Date HEADER at day granularity, inclusive — mirrored here so the lookback-window
        // semantics the journey tests configure are honoured rather than bypassed.
        DateTime? sentSince = null;
        for (var index = 3; index < tokens.Count; index++)
        {
            if (!tokens[index].Equals("SENTSINCE", StringComparison.OrdinalIgnoreCase)) continue;
            if (index + 1 < tokens.Count && DateTime.TryParseExact(
                    tokens[index + 1], "d-MMM-yyyy", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var parsed))
                sentSince = parsed.Date;
        }

        List<StoredMessage> snapshot;
        lock (_mutex) snapshot = _messages.ToList();
        var hits = snapshot
            .Where(m => sentSince is null || m.Parsed.Date.Date >= sentSince.Value)
            .Select(m => m.Uid)
            .ToList();

        await WriteLineAsync(stream, hits.Count == 0
            ? "* SEARCH"
            : "* SEARCH " + string.Join(' ', hits));
        await WriteLineAsync(stream, $"{tag} OK SEARCH completed");
    }

    private async Task HandleUidFetchAsync(NetworkStream stream, string tag, List<string> tokens, string line)
    {
        var set = tokens.Count > 3 ? tokens[3] : "";
        var wantsEnvelope = line.Contains("ENVELOPE", StringComparison.OrdinalIgnoreCase);
        var wantsBody = line.Contains("BODY.PEEK[]", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("BODY[]", StringComparison.OrdinalIgnoreCase);

        List<StoredMessage> snapshot;
        lock (_mutex) snapshot = _messages.ToList();
        var selected = ResolveUidSet(set, snapshot);

        foreach (var message in selected)
        {
            var sequence = snapshot.IndexOf(message) + 1;
            if (wantsBody)
            {
                var header = Encoding.ASCII.GetBytes(
                    $"* {sequence} FETCH (UID {message.Uid} BODY[] {{{message.Bytes.Length}}}\r\n");
                await stream.WriteAsync(header);
                await stream.WriteAsync(message.Bytes);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(")\r\n"));
            }
            else if (wantsEnvelope)
            {
                await WriteLineAsync(stream,
                    $"* {sequence} FETCH (UID {message.Uid} ENVELOPE {Envelope(message.Parsed)})");
            }
            else
            {
                await WriteLineAsync(stream, $"* {sequence} FETCH (UID {message.Uid})");
            }
        }
        await WriteLineAsync(stream, $"{tag} OK FETCH completed");
    }

    private async Task HandleUidStoreAsync(NetworkStream stream, string tag, List<string> tokens, string line)
    {
        var set = tokens.Count > 3 ? tokens[3] : "";
        List<StoredMessage> snapshot;
        lock (_mutex) snapshot = _messages.ToList();
        var silent = line.Contains(".SILENT", StringComparison.OrdinalIgnoreCase);

        foreach (var message in ResolveUidSet(set, snapshot))
        {
            if (line.Contains("\\Seen", StringComparison.OrdinalIgnoreCase)
                && line.Contains("+FLAGS", StringComparison.OrdinalIgnoreCase))
            {
                message.Seen = true;
                SeenUids.Enqueue(message.Uid);
            }
            if (!silent)
            {
                var sequence = snapshot.IndexOf(message) + 1;
                await WriteLineAsync(stream,
                    $"* {sequence} FETCH (UID {message.Uid} FLAGS ({(message.Seen ? "\\Seen" : "")}))");
            }
        }
        await WriteLineAsync(stream, $"{tag} OK STORE completed");
    }

    private static List<StoredMessage> ResolveUidSet(string set, List<StoredMessage> snapshot)
    {
        var result = new List<StoredMessage>();
        foreach (var part in set.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "*")
            {
                if (snapshot.Count > 0) result.Add(snapshot[^1]);
                continue;
            }
            if (part.Contains(':'))
            {
                var bounds = part.Split(':');
                var low = bounds[0] == "*" ? uint.MaxValue : uint.Parse(bounds[0]);
                var high = bounds[1] == "*" ? uint.MaxValue : uint.Parse(bounds[1]);
                if (low > high) (low, high) = (high, low);
                result.AddRange(snapshot.Where(m => m.Uid >= low && m.Uid <= high));
                continue;
            }
            var uid = uint.Parse(part);
            var match = snapshot.FirstOrDefault(m => m.Uid == uid);
            if (match is not null) result.Add(match);
        }
        return result.Distinct().OrderBy(m => m.Uid).ToList();
    }

    // ------------------------------------------------------------------ ENVELOPE grammar

    /// <summary>RFC 3501 ENVELOPE: (date subject from sender reply-to to cc bcc in-reply-to
    /// message-id). Anything non-ASCII is RFC 2047-encoded first so every field stays a plain
    /// quoted-string — the exact shape real servers hand MailKit.</summary>
    internal static string Envelope(MimeMessage message)
    {
        var from = AddressList(message.From);
        return "("
            + Quoted(DateUtils.FormatDate(message.Date)) + " "
            + EncodedQuoted(message.Subject) + " "
            + from + " "
            + from + " " // sender
            + from + " " // reply-to
            + AddressList(message.To) + " "
            + "NIL " // cc
            + "NIL " // bcc
            + QuotedOrNil(WrapId(message.InReplyTo)) + " "
            + QuotedOrNil(WrapId(message.MessageId))
            + ")";
    }

    private static string? WrapId(string? id)
        => string.IsNullOrWhiteSpace(id) ? null : $"<{id}>";

    private static string AddressList(InternetAddressList list)
    {
        var mailboxes = list.Mailboxes.ToList();
        if (mailboxes.Count == 0) return "NIL";
        var builder = new StringBuilder("(");
        foreach (var mailbox in mailboxes)
        {
            var at = mailbox.Address.LastIndexOf('@');
            var local = at > 0 ? mailbox.Address[..at] : mailbox.Address;
            var host = at > 0 ? mailbox.Address[(at + 1)..] : "";
            builder.Append('(')
                .Append(EncodedQuoted(mailbox.Name)).Append(' ')
                .Append("NIL ")
                .Append(Quoted(local)).Append(' ')
                .Append(Quoted(host))
                .Append(')');
        }
        return builder.Append(')').ToString();
    }

    private static string EncodedQuoted(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "NIL";
        var ascii = value.All(c => c >= 0x20 && c < 0x7F)
            ? value
            : Encoding.ASCII.GetString(Rfc2047.EncodeText(
                FormatOptions.Default, Encoding.UTF8, value));
        return Quoted(ascii);
    }

    private static string QuotedOrNil(string? value)
        => string.IsNullOrEmpty(value) ? "NIL" : Quoted(value);

    private static string Quoted(string value)
        => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ------------------------------------------------------------------------- plumbing

    private static List<string> Tokenize(string line)
    {
        var tokens = new List<string>();
        var index = 0;
        while (index < line.Length)
        {
            while (index < line.Length && line[index] == ' ') index++;
            if (index >= line.Length) break;
            if (line[index] == '"')
            {
                var builder = new StringBuilder();
                index++;
                while (index < line.Length && line[index] != '"')
                {
                    if (line[index] == '\\' && index + 1 < line.Length) index++;
                    builder.Append(line[index++]);
                }
                index++; // closing quote
                tokens.Add(builder.ToString());
            }
            else if (line[index] == '(')
            {
                var depth = 0;
                var start = index;
                while (index < line.Length)
                {
                    if (line[index] == '(') depth++;
                    if (line[index] == ')' && --depth == 0) { index++; break; }
                    index++;
                }
                tokens.Add(line[start..index]);
            }
            else
            {
                var start = index;
                while (index < line.Length && line[index] != ' ') index++;
                tokens.Add(line[start..index]);
            }
        }
        return tokens;
    }

    private static async Task WriteLineAsync(NetworkStream stream, string line)
        => await stream.WriteAsync(Encoding.UTF8.GetBytes(line + "\r\n"));

    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var single = new byte[1];
        while (true)
        {
            int read;
            try { read = await stream.ReadAsync(single); }
            catch (IOException) { return null; }
            catch (ObjectDisposedException) { return null; }
            if (read == 0) return buffer.Length == 0 ? null : Decode(buffer);
            if (single[0] == (byte)'\n') return Decode(buffer);
            if (single[0] != (byte)'\r') buffer.WriteByte(single[0]);
        }

        static string Decode(MemoryStream ms) => Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset));
            if (read == 0) throw new IOException("Connection closed mid-literal.");
            offset += read;
        }
    }

    private sealed class StoredMessage
    {
        public StoredMessage(uint uid, byte[] bytes, MimeMessage parsed)
        {
            Uid = uid;
            Bytes = bytes;
            Parsed = parsed;
        }

        public uint Uid { get; }
        public byte[] Bytes { get; }
        public MimeMessage Parsed { get; }
        public bool Seen { get; set; }
    }
}
