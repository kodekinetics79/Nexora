using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using MailKit.Search;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// ING-08 — THE TWO WAYS THE MAILBOX QUERY LOST MAIL, AND THE WAY THE DUPLICATE CHECK DID.
///
/// The query was <c>SentSince(Today - 7 days) AND NotSeen</c>:
/// <list type="number">
///   <item><description>the fixed 7-day floor meant an outage longer than a week made every
///   older message PERMANENTLY invisible — no row, no log, nothing to replay. The poller had
///   not reached the mailbox since 2026-07-30;</description></item>
///   <item><description><c>NotSeen</c> made the IMAP reading flag an ingestion ledger, so any
///   message a human opened in Outlook first was never ingested at all.</description></item>
/// </list>
/// And the duplicate check skipped on (From, To, Subject), so a customer's SECOND enquiry under
/// the same subject line — "RFQ", "Quotation Request", any reply in a thread — was dropped on
/// arrival.
/// </summary>
public sealed class EmailPollWindowAndIngestKeyTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 9, 0, 0, DateTimeKind.Utc);

    // ------------------------------------------------------------------- the lookback window

    [Fact]
    public void TheWindowIsDerivedFromTheLastSuccessfulPollNotAFixedSevenDays()
    {
        // Nineteen days of outage. Under the old fixed window, days 8-19 were unrecoverable.
        var lastSuccess = Now.AddDays(-19);
        var window = Service().ResolveLookbackWindow(Mailbox(lastSuccess), Now);

        Assert.Equal(lastSuccess, window.SinceUtc);
        Assert.Equal(0, window.CappedDays);
        Assert.False(window.FirstEverPoll);
    }

    [Fact]
    public void AShortOutageStillReReadsAtLeastTheFloor()
    {
        // A poll five minutes ago must not produce a five-minute window: SENTSINCE has day
        // granularity and server/client clocks are not identical.
        var window = Service().ResolveLookbackWindow(Mailbox(Now.AddMinutes(-5)), Now);

        Assert.Equal(Now.AddDays(-1), window.SinceUtc);
    }

    [Fact]
    public void AVeryLongOutageIsCappedAndTheUnreachableDaysAreCountedNotHidden()
    {
        // The cap is the one remaining way this design can miss a message, so it is announced:
        // CappedDays is non-zero and the poller logs a warning naming the exact history that is
        // out of reach. Silent truncation is what the old code did.
        var window = Service().ResolveLookbackWindow(Mailbox(Now.AddDays(-100)), Now);

        Assert.Equal(Now.AddDays(-30), window.SinceUtc);
        Assert.Equal(70, window.CappedDays);
    }

    [Fact]
    public void AMailboxThatHasNeverBeenPolledKeepsTheBoundedInitialWindow()
    {
        // First deploy of this change must not become an unbounded re-read of every mailbox.
        var window = Service().ResolveLookbackWindow(Mailbox(lastSuccessfulPollOn: null), Now);

        Assert.Equal(Now.AddDays(-7), window.SinceUtc);
        Assert.True(window.FirstEverPoll);
        Assert.Equal(0, window.CappedDays);
    }

    [Fact]
    public void TheWindowBoundsAreConfigurable()
    {
        var service = Service(new Dictionary<string, string?>
        {
            ["Ingestion:Email:InitialLookbackDays"] = "2",
            ["Ingestion:Email:MinLookbackDays"] = "0.5",
            ["Ingestion:Email:MaxLookbackDays"] = "10"
        });

        Assert.Equal(Now.AddDays(-2), service.ResolveLookbackWindow(Mailbox(null), Now).SinceUtc);
        Assert.Equal(Now.AddDays(-10), service.ResolveLookbackWindow(Mailbox(Now.AddDays(-45)), Now).SinceUtc);
    }

    [Fact]
    public void TheMailboxQueryNoLongerFiltersOnTheSeenFlag()
    {
        // The IMAP \Seen flag is a READING state set by any human with the mailbox open. Using
        // it as the ingestion ledger meant a colleague glancing at an RFQ deleted it from the
        // system's view of the world.
        var query = EmailService.BuildRFQSearchQuery(Now.AddDays(-3));

        Assert.Equal(SearchTerm.SentSince, query.Term);
        Assert.IsType<DateSearchQuery>(query);
        Assert.DoesNotContain("NotSeen", Describe(query));
    }

    // ---------------------------------------------------------------------- the ingest key

    [Fact]
    public void TwoDifferentRfqsUnderTheSameSubjectAreDifferentMessages()
    {
        // The P0 regression. Same sender, same recipient, same subject line, different
        // enquiry — the old (From, To, Subject) skip dropped the second one entirely.
        var first = Message("RFQ", "Please quote 40 nos cable tray 300mm.");
        var second = Message("RFQ", "Please quote 12 nos gate valve 6 inch.");

        Assert.NotEqual(EmailService.ResolveIngestKey(first), EmailService.ResolveIngestKey(second));
    }

    [Fact]
    public async Task ASameSubjectDifferentMessageIdEmailIsIngestedNotSkipped()
    {
        // Proven against the real ledger query rather than by inspection: the first message is
        // recorded, and the second — identical envelope, different Message-Id — is NOT treated
        // as already ingested.
        using var db = new TestDb();
        await SeedAsync(db);

        var first = Message("Quotation Request", "Please quote 40 nos cable tray 300mm.");
        var second = Message("Quotation Request", "Please quote 12 nos gate valve 6 inch.");
        var firstKey = EmailService.ResolveIngestKey(first);
        var secondKey = EmailService.ResolveIngestKey(second);

        await using (var ctx = db.ContextFor(null))
        {
            ctx.EmailIngests.Add(new EmailIngest
            {
                MessageId = firstKey,
                EmailSubject = first.Subject,
                FromEmail = first.From.ToString(),
                ToEmail = first.To.ToString(),
                EmailConfigurationId = 1,
                CreatedOn = DateTime.UtcNow,
                ParseStatus = "Queued"
            });
            await ctx.SaveChangesAsync();
        }

        await using var verify = db.ContextFor(null);
        Assert.True(verify.EmailIngests.Any(e => e.EmailConfigurationId == 1 && e.MessageId == firstKey));
        Assert.False(verify.EmailIngests.Any(e => e.EmailConfigurationId == 1 && e.MessageId == secondKey));
    }

    [Fact]
    public void TheSameMessageReadTwiceProducesTheSameKey()
    {
        // The counterpart guarantee: dropping NotSeen means the window is re-read every cycle,
        // so a message must hash to the same ledger key each time or it is ingested forever.
        var message = Message("RFQ 4711", "Please quote the attached BOQ.");
        Assert.Equal(EmailService.ResolveIngestKey(message), EmailService.ResolveIngestKey(message));
    }

    [Fact]
    public void AMessageWithNoMessageIdGetsAStableContentKeyNotARandomGuid()
    {
        // The old fallback was Guid.NewGuid(): a header-less message looked brand new on every
        // single cycle and was re-ingested without limit.
        var first = Message("RFQ", "Please quote 40 nos cable tray 300mm.", messageId: null);
        var replay = Message("RFQ", "Please quote 40 nos cable tray 300mm.", messageId: null);

        var key = EmailService.ResolveIngestKey(first);
        Assert.StartsWith("sha256:", key);
        Assert.Equal(key, EmailService.ResolveIngestKey(replay));
        Assert.NotEqual(key, EmailService.ResolveIngestKey(
            Message("RFQ", "Please quote 12 nos gate valve 6 inch.", messageId: null)));
    }

    [Fact]
    public void AnOverlongMessageIdFallsBackToTheHashInsteadOfBeingTruncatedIntoACollision()
    {
        // MessageID is varchar(255). Truncating would manufacture a collision between two
        // genuinely different messages — the same class of bug, one layer down.
        var prefix = new string('a', 300);
        var a = Message("RFQ", "one", messageId: prefix + "1@sender.example");
        var b = Message("RFQ", "two", messageId: prefix + "2@sender.example");

        Assert.StartsWith("sha256:", EmailService.ResolveIngestKey(a));
        Assert.NotEqual(EmailService.ResolveIngestKey(a), EmailService.ResolveIngestKey(b));
    }

    [Fact]
    public void MessageIdsAreNormalisedIdenticallyWhereverTheyComeFrom()
    {
        // The ledger is written from the parsed message and read from the IMAP ENVELOPE. If the
        // angle brackets survived on one side only, every message would look unseen forever.
        Assert.Equal("abc@sender.example", EmailService.NormalizeMessageId("<abc@sender.example>"));
        Assert.Equal("abc@sender.example", EmailService.NormalizeMessageId("  abc@sender.example "));
        Assert.Null(EmailService.NormalizeMessageId("   "));
        Assert.Null(EmailService.NormalizeMessageId(null));
    }

    // ------------------------------------------------------------------------ test plumbing

    private static string Describe(SearchQuery query) => query.GetType().Name + ":" + query.Term;

    private static EmailConfiguration Mailbox(DateTime? lastSuccessfulPollOn) => new()
    {
        Id = 1,
        BusinessUnitId = 3,
        ConfigurationName = "intake",
        EmailAddress = "intake@tenant.example",
        Protocol = "IMAP",
        Host = "127.0.0.1",
        Port = 1,
        Username = "intake",
        Password = "secret",
        LastSuccessfulPollOn = lastSuccessfulPollOn
    };

    private static MimeMessage Message(string subject, string body, string? messageId = "")
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Ahmed", "ahmed@alnoortrading.ae"));
        message.To.Add(new MailboxAddress("Intake", "intake@tenant.example"));
        message.Subject = subject;
        message.Date = new DateTimeOffset(2026, 8, 6, 8, 0, 0, TimeSpan.Zero);
        // MimeMessage's default constructor mints a Message-Id, so "no Message-Id" has to be
        // produced by removing the header — which is exactly what real header-stripping
        // gateways and some ERP mailers do.
        if (messageId is null)
            message.Headers.RemoveAll(HeaderId.MessageId);
        else
            message.MessageId = messageId.Length == 0 ? MimeKit.Utils.MimeUtils.GenerateMessageId() : messageId;
        message.Body = new BodyBuilder { TextBody = body }.ToMessageBody();
        return message;
    }

    private static async Task SeedAsync(TestDb db)
    {
        await using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, 3);
        ctx.EmailConfigurations.Add(new EmailConfiguration
        {
            Id = 1,
            BusinessUnitId = 3,
            ConfigurationName = "intake",
            EmailAddress = "intake@tenant.example",
            Protocol = "IMAP",
            Host = "127.0.0.1",
            Port = 1,
            Username = "intake",
            Password = "secret",
            IsActive = true,
            PollingInterval = 300,
            CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    private static EmailService Service(Dictionary<string, string?>? settings = null)
    {
        var temp = Path.Combine(Path.GetTempPath(), "nexora-email-window-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        return new EmailService(
            context: null!, // ResolveLookbackWindow / BuildRFQSearchQuery never touch the DbContext
            env: new StubEnvironment(temp),
            logger: new NoopLogger<EmailService>(),
            llmService: new StubLlm(),
            scopeFactory: new UnusedScopeFactory(),
            configuration: new ConfigurationBuilder()
                .AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build(),
            storage: new TempStorage(temp));
    }

    private sealed class UnusedScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("The window tests never resolve a scope.");
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public StubEnvironment(string root)
        {
            WebRootPath = root;
            ContentRootPath = root;
        }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class TempStorage : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
    {
        private readonly string _root;
        public TempStorage(string root) => _root = root;
        public string RootPath => _root;
        public string ResolvePath(string storagePath) => Path.Combine(_root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([_root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("The window tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The window tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The window tests never delete storage.");
    }
}
