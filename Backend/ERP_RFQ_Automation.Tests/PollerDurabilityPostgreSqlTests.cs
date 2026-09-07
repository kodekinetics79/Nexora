using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE FOUR WAYS ONE POLL CYCLE LOST A MESSAGE WITHOUT SAYING SO.
///
/// <para>Every test here drives the real entry point — <see cref="EmailService.FetchAndSaveLeadsAsync"/>
/// — against a real MailKit client, a real socket, the <see cref="FakeImapServer"/> and a real
/// PostgreSQL. That is deliberate. Three of the four defects were invisible to a unit test by
/// construction: the checkpoint gate is applied inside the mailbox loop, the bounded batch is
/// chosen between the envelope fetch and the download, and the column widths that reject a
/// multi-recipient tender exist only in PostgreSQL — SQLite ignores <c>varchar(n)</c> entirely, so
/// the portable lane can never see it.</para>
///
/// <para><b>Loopback.</b> These tests dial 127.0.0.1, which <see cref="MailEndpointPolicy"/>
/// refuses unless the Development-only allowance is granted. That allowance is process-global
/// static state, so — exactly like <c>AcceptanceJourneyTests</c> and
/// <c>MailEndpointPolicyLoopbackAllowanceTests</c> — this class lives in the serialized PostgreSQL
/// collection so it can never run beside the tests that assert loopback is refused.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class PollerDurabilityPostgreSqlTests : IDisposable
{
    private const long UnstorableTenant = 970_411;
    private const long HeaderlessTenant = 970_412;
    private const long TenderTenant = 970_413;
    private const long InlineDocumentTenant = 970_414;

    /// <summary>The inbound login identity IS the mailbox address (MailboxLoginIdentity.ForInbound),
    /// so the fake server must expect that name and not a separate username.</summary>
    private const string IntakeAddress = "intake@tenant.test";

    private readonly PostgreSqlTestDatabase _database;
    private readonly string _root;

    public PollerDurabilityPostgreSqlTests(PostgreSqlTestDatabase database)
    {
        _database = database;
        _root = Path.Combine(
            Path.GetTempPath(), "nexora-poller-durability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        // The environment is a PARAMETER to the grant, so this is the same allowance a
        // Development host performs in Program.cs — nothing is bypassed.
        Assert.True(MailEndpointPolicy.EnableLoopbackForLocalDevelopment(
            isDevelopmentEnvironment: true, requested: true));
    }

    public void Dispose()
    {
        MailEndpointPolicy.ResetLoopbackAllowance();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ================================================================ 1. the recovery checkpoint

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_message_the_cycle_could_not_store_holds_the_recovery_checkpoint()
    {
        // The evidence store is unavailable — the 2026-09-03 shape. The message is downloaded,
        // nothing durable is written, it is correctly left unread for the next cycle... and the
        // poll still reported success, so the checkpoint moved to now. Within a day the window
        // floor (now - MinLookbackDays) passed the message's sent date, SENTSINCE stopped
        // matching it, and it was gone: no ingest row, no assembly, nothing to replay.
        await using var imap = new FakeImapServer(IntakeAddress);
        var uid = imap.AddMessage(Bytes(Rfq("unstorable@buyer.example", "RFQ 8801")));
        var baseline = Baseline();
        var config = await SeedMailboxAsync(UnstorableTenant, imap, baseline);

        var report = await PollAsync(UnstorableTenant, new ScriptedIntake(safeToAcknowledge: false));

        var mailbox = Assert.Single(report.Mailboxes);
        Assert.True(mailbox.Succeeded, mailbox.FailureReason);
        Assert.Equal(1, mailbox.MessagesDownloaded);
        Assert.Equal(1, mailbox.MessagesNotAcknowledged);
        Assert.Equal(0, mailbox.MessagesDeferred);
        // Unread on the server, which is the half of the contract that already worked.
        Assert.DoesNotContain(uid, imap.SeenUids);

        await using var verify = _database.ContextFor(null);
        var persisted = await verify.EmailConfigurations.AsNoTracking()
            .SingleAsync(x => x.Id == config.Id);
        Assert.Equal(baseline, persisted.LastSuccessfulPollOn);
        // The cycle is still a success for the channel: the mailbox answered. It is only the
        // recovery point that must not move.
        Assert.Null(persisted.LastPollError);
        Assert.Equal(0, persisted.ConsecutivePollFailures);

        await CleanupAsync(config.Id);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_fully_drained_cycle_still_advances_the_recovery_checkpoint()
    {
        // The counterpart. A checkpoint that never moves is its own outage: the window would
        // grow to the 30-day cap and stay there, re-searching a month of mail every cycle.
        await using var imap = new FakeImapServer(IntakeAddress);
        var uid = imap.AddMessage(Bytes(Rfq("drained@buyer.example", "RFQ 8802")));
        var baseline = Baseline();
        var config = await SeedMailboxAsync(UnstorableTenant + 100, imap, baseline);

        var report = await PollAsync(UnstorableTenant + 100, new ScriptedIntake(safeToAcknowledge: true));

        var mailbox = Assert.Single(report.Mailboxes);
        Assert.Equal(0, mailbox.MessagesNotAcknowledged);
        Assert.Contains(uid, imap.SeenUids);

        await using var verify = _database.ContextFor(null);
        var persisted = await verify.EmailConfigurations.AsNoTracking()
            .SingleAsync(x => x.Id == config.Id);
        Assert.True(persisted.LastSuccessfulPollOn > baseline,
            "a cycle that stored everything it downloaded must move the recovery point forward");

        await CleanupAsync(config.Id);
    }

    // ============================================================= 2. the bounded batch's queue

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Header_less_envelopes_cannot_take_every_slot_in_the_bounded_batch()
    {
        // Scanner and ERP-gateway mail arrives with no Message-Id. Its durable key is a CONTENT
        // hash, which no envelope can reproduce, so it reads as "unhandled" on every cycle for as
        // long as it stays in the window however durably it was ingested. Being the oldest
        // unhandled envelopes, they filled the bounded batch — every slot, every cycle — and the
        // genuinely new enquiry behind them was never downloaded at all, while the mailbox went
        // on reporting success and the health check stayed green.
        await using var imap = new FakeImapServer(IntakeAddress);
        var firstScan = imap.AddMessage(Bytes(Headerless("Scanned document 0001")));
        var secondScan = imap.AddMessage(Bytes(Headerless("Scanned document 0002")));
        var enquiry = imap.AddMessage(Bytes(Rfq("buyer@customer.example", "RFQ 4711",
            messageId: "new-enquiry-4711@customer.example")));
        var config = await SeedMailboxAsync(HeaderlessTenant, imap, Baseline());

        // Two slots and three unhandled envelopes: the queue's ORDER is the whole test.
        var report = await PollAsync(HeaderlessTenant, new ScriptedIntake(safeToAcknowledge: true),
            maxNewMessagesPerMailboxAttempt: 2);

        var mailbox = Assert.Single(report.Mailboxes);
        Assert.Equal(2, mailbox.MessagesDownloaded);
        Assert.Equal(1, mailbox.MessagesDeferred);

        await using var verify = _database.ContextFor(null);
        Assert.True(
            await verify.EmailIngests.AsNoTracking().AnyAsync(x =>
                x.EmailConfigurationId == config.Id
                && x.MessageId == "new-enquiry-4711@customer.example"),
            "the customer's enquiry was never downloaded: two envelopes whose identity the ledger "
            + "cannot answer for took both slots, and they will do so again on every future cycle");
        // Nothing was skipped to achieve that — the header-less envelopes keep their place in the
        // queue, and one of them was processed with the slot that remained.
        Assert.Contains(enquiry, imap.SeenUids);
        Assert.Equal(2, imap.SeenUids.Count);
        Assert.True(imap.SeenUids.Contains(firstScan) ^ imap.SeenUids.Contains(secondScan));

        await CleanupAsync(config.Id);
    }

    // ================================================== 3. the multi-recipient tender broadcast

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task A_tender_issued_to_a_dozen_vendors_is_ingested_instead_of_failing_the_insert()
    {
        // ToEmail is varchar(255) and an RFC 5322 address header is bounded by nothing. One
        // tender to twelve vendors is ~400 characters, PostgreSQL raises 22001 rather than
        // truncating, and the insert that dies is the only durable record the message would ever
        // have had: no ingest row, so no triage decision, no assembly, no lead, no rejection, and
        // nothing for the stranded-Pending sweeper to find either — it queries EmailIngests. The
        // message was re-downloaded and re-failed on every cycle for ever, and the mailbox
        // reported a clean poll each time.
        await using var imap = new FakeImapServer(IntakeAddress);
        var uid = imap.AddMessage(Bytes(TenderToTwelveVendors()));
        var baseline = Baseline();
        var config = await SeedMailboxAsync(TenderTenant, imap, baseline);

        var report = await PollAsync(TenderTenant, new ScriptedIntake(safeToAcknowledge: true));

        var mailbox = Assert.Single(report.Mailboxes);
        Assert.True(mailbox.Succeeded, mailbox.FailureReason);
        Assert.Equal(1, mailbox.MessagesDownloaded);
        Assert.Equal(0, mailbox.MessagesNotAcknowledged);
        Assert.Contains(uid, imap.SeenUids);

        await using var verify = _database.ContextFor(null);
        var ingest = await verify.EmailIngests.AsNoTracking()
            .SingleAsync(x => x.EmailConfigurationId == config.Id);

        // Clipped to the column, and SAYING it is clipped: "who else was invited to bid this" is
        // exactly what a person reading a stranded tender needs to know.
        Assert.True(ingest.ToEmail!.Length <= 255, ingest.ToEmail);
        Assert.Contains("bids1@vendor.example", ingest.ToEmail);
        Assert.Contains("more)", ingest.ToEmail);
        Assert.DoesNotContain("(+0 more)", ingest.ToEmail);
        // The display name is decoration and goes first; the address is the fact we act on.
        Assert.Equal("tender@buyer.example", ingest.FromEmail);
        Assert.True(ingest.EmailSubject!.Length <= 500, ingest.EmailSubject.Length.ToString());
        Assert.StartsWith("Invitation to bid", ingest.EmailSubject);

        // And because it was stored, the checkpoint is free to move: the composite failure this
        // module was opened to close is a message that cannot be stored, is never acknowledged,
        // and is then passed by the window.
        var persisted = await verify.EmailConfigurations.AsNoTracking()
            .SingleAsync(x => x.Id == config.Id);
        Assert.True(persisted.LastSuccessfulPollOn > baseline);

        await CleanupAsync(config.Id);
    }

    // ================================================ 4. the attachment the triage gate cannot see

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_rfq_carried_by_an_inline_pdf_is_not_stopped_as_an_empty_message()
    {
        // "See attached", sent from a client that marks the PDF `inline` rather than
        // `attachment`. MimeKit's message.Attachments yields only entities whose
        // Content-Disposition literally says "attachment", so the gate read "no new text and
        // nothing attached", stopped the single most common shape of a real RFQ in this segment
        // as noise, and the Inbound Mail row then showed the attachment beside a chip saying no
        // attachment came with it.
        await using var imap = new FakeImapServer(IntakeAddress);
        var carrier = InlineDocumentRfq();
        // THE FIXTURE'S OWN CLAIM, asserted rather than assumed: this is the shape that defeats
        // the walk the gate used to use.
        Assert.False(carrier.Attachments.Any());
        Assert.Single(carrier.BodyParts.OfType<MimePart>().Where(p => p.FileName == "RFQ-4711.pdf"));
        imap.AddMessage(Bytes(carrier));
        var config = await SeedMailboxAsync(InlineDocumentTenant, imap, Baseline());

        await PollAsync(InlineDocumentTenant, new ScriptedIntake(safeToAcknowledge: true));

        await using var verify = _database.ContextFor(null);
        var ingest = await verify.EmailIngests.AsNoTracking()
            .SingleAsync(x => x.EmailConfigurationId == config.Id);
        Assert.Equal("Inquiry", ingest.TriageOutcome);
        Assert.DoesNotContain(EmailTriageReasonCodes.EmptyAfterQuoteStrip, ingest.TriageReasonJson!);
        Assert.NotEqual("Rejected", ingest.ParseStatus);

        await CleanupAsync(config.Id);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task An_empty_message_carrying_nothing_but_a_signature_logo_is_still_noise()
    {
        // The control that keeps the fix honest in both directions. A cid-referenced inline image
        // small enough to be decoration under the planner's own bar is not a document: if it were,
        // every corporate signature would stop the empty-after-strip rule from ever firing, and an
        // unattended sender's autoreply would look like an enquiry with commercial evidence.
        await using var imap = new FakeImapServer(IntakeAddress);
        var decorated = EmptyMessageWithSignatureLogo();
        imap.AddMessage(Bytes(decorated));
        var config = await SeedMailboxAsync(InlineDocumentTenant + 100, imap, Baseline());

        await PollAsync(InlineDocumentTenant + 100, new ScriptedIntake(safeToAcknowledge: true));

        await using var verify = _database.ContextFor(null);
        var ingest = await verify.EmailIngests.AsNoTracking()
            .SingleAsync(x => x.EmailConfigurationId == config.Id);
        Assert.Equal("Noise", ingest.TriageOutcome);
        Assert.Contains(EmailTriageReasonCodes.EmptyAfterQuoteStrip, ingest.TriageReasonJson!);

        await CleanupAsync(config.Id);
    }

    // ------------------------------------------------------------------------- the messages

    private static MimeMessage Rfq(string from, string subject, string? messageId = null)
    {
        var message = NewMessage(from, subject, messageId);
        message.Body = new TextPart("plain")
        {
            Text = "Kindly send your best price for 40 nos cable tray 300mm, delivery Jebel Ali."
        };
        return message;
    }

    /// <summary>What a scanner or an ERP gateway posts: no Message-Id header at all.</summary>
    private static MimeMessage Headerless(string subject)
    {
        var message = NewMessage("scanner@gateway.example", subject, messageId: null);
        message.Headers.RemoveAll(HeaderId.MessageId);
        message.Body = new TextPart("plain") { Text = "Scanned document attached." };
        return message;
    }

    /// <summary>
    /// One buyer, one tender, twelve vendors — the normal shape of competitive tendering in this
    /// segment, and ~400 characters of To header.
    /// </summary>
    private static MimeMessage TenderToTwelveVendors()
    {
        var message = new MimeMessage
        {
            Subject = "Invitation to bid " + new string('X', 600),
            MessageId = "tender-broadcast@buyer.example",
            Date = DateTimeOffset.UtcNow
        };
        message.From.Add(new MailboxAddress(new string('B', 300), "tender@buyer.example"));
        for (var vendor = 1; vendor <= 12; vendor++)
            message.To.Add(new MailboxAddress($"Vendor {vendor}", $"bids{vendor}@vendor.example"));
        message.Body = new TextPart("plain")
        {
            Text = "Please quote the attached schedule of rates before the closing date."
        };
        return message;
    }

    /// <summary>Empty covering note; the enquiry is the PDF, and the PDF is marked inline.</summary>
    private static MimeMessage InlineDocumentRfq()
    {
        var message = NewMessage("buyer@customer.example", "RFQ 4711",
            "inline-carrier-4711@customer.example");
        var pdf = new MimePart("application", "pdf")
        {
            Content = new MimeContent(new MemoryStream("%PDF-1.4 requirements"u8.ToArray())),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline)
            {
                FileName = "RFQ-4711.pdf"
            }
        };
        message.Body = new Multipart("mixed") { new TextPart("plain") { Text = "" }, pdf };
        return message;
    }

    private static MimeMessage EmptyMessageWithSignatureLogo()
    {
        var message = NewMessage("noreply@notifications.example", "Notification",
            "decorated@notifications.example");
        var logo = new MimePart("image", "png")
        {
            Content = new MimeContent(new MemoryStream(new byte[64])),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentId = "logo@signature",
            ContentDisposition = new ContentDisposition(ContentDisposition.Inline)
            {
                FileName = "image001.png",
                Size = 4096
            }
        };
        message.Body = new Multipart("related")
        {
            new TextPart("html") { Text = "<html><body><img src=\"cid:logo@signature\"></body></html>" },
            logo
        };
        return message;
    }

    private static MimeMessage NewMessage(string from, string subject, string? messageId)
    {
        var message = new MimeMessage
        {
            Subject = subject,
            Date = DateTimeOffset.UtcNow
        };
        if (messageId is not null) message.MessageId = messageId;
        message.From.Add(MailboxAddress.Parse(from));
        message.To.Add(MailboxAddress.Parse(IntakeAddress));
        return message;
    }

    private static byte[] Bytes(MimeMessage message)
    {
        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        return buffer.ToArray();
    }

    // -------------------------------------------------------------------------- the harness

    /// <summary>A whole second before now, so "did the checkpoint move" cannot be a rounding
    /// question, and truncated to the second because PostgreSQL keeps microseconds and .NET
    /// keeps ticks.</summary>
    private static DateTime Baseline()
    {
        var since = DateTime.UtcNow.AddDays(-2);
        return new DateTime(
            since.Ticks - since.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
    }

    private async Task<EmailConfiguration> SeedMailboxAsync(
        long tenant, FakeImapServer imap, DateTime lastSuccessfulPollOn)
    {
        await using var ctx = _database.ContextFor(null);
        // A previous FAILED run of this class leaves its mailbox behind, and the poll below polls
        // every active mailbox this tenant owns — so the leftover would show up as a second
        // mailbox in the report and turn one red test into a whole red class.
        var stale = await ctx.EmailConfigurations.Where(x => x.BusinessUnitId == tenant)
            .Select(x => x.Id).ToListAsync();
        if (stale.Count > 0)
        {
            await ctx.EmailIngests.Where(x => stale.Contains(x.EmailConfigurationId))
                .ExecuteDeleteAsync();
            await ctx.EmailConfigurations.Where(x => stale.Contains(x.Id)).ExecuteDeleteAsync();
        }
        Seed.EnsureBusinessUnit(ctx, tenant);
        var config = new EmailConfiguration
        {
            BusinessUnitId = tenant,
            ConfigurationName = $"poller-durability-{tenant}",
            EmailAddress = IntakeAddress,
            Protocol = "IMAP",
            Host = "127.0.0.1",
            Port = imap.Port,
            Username = imap.Username,
            Password = imap.Password,
            UseSsl = false,
            PollingInterval = 300,
            IsActive = true,
            LastSuccessfulPollOn = lastSuccessfulPollOn,
            CreatedOn = DateTime.UtcNow
        };
        ctx.EmailConfigurations.Add(config);
        await ctx.SaveChangesAsync();
        return config;
    }

    /// <summary>One real poller cycle: the same call the hosted background service makes.</summary>
    private async Task<MailboxPollReport> PollAsync(
        long tenant, IEmailInquiryIntakeService intake, int? maxNewMessagesPerMailboxAttempt = null)
    {
        var settings = new Dictionary<string, string?>();
        if (maxNewMessagesPerMailboxAttempt is int max)
            settings["Ingestion:Email:MaxNewMessagesPerMailboxAttempt"] = max.ToString();

        await using var provider = BuildProvider(intake);
        await using var discovery = _database.ContextFor(null);
        var service = new EmailService(
            context: discovery,
            env: new PollerEnvironment(_root),
            logger: new NoopLogger<EmailService>(),
            llmService: new StubLlm(),
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>(),
            configuration: new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            storage: new LocalFileStorage(_root, _root),
            tenantScope: provider.GetRequiredService<ITenantScopeAccessor>());
        return await service.FetchAndSaveLeadsAsync(tenant);
    }

    /// <summary>The poller's own scope shape: the ambient pushed tenant IS what the DbContext
    /// resolves, which is what its fail-closed scope guard checks before it writes anything.</summary>
    private ServiceProvider BuildProvider(IEmailInquiryIntakeService intake)
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_database.ConnectionString)
            .EnableDetailedErrors()
            .Options;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped(sp => new ErpRfqAutomationContext(
            options, new StubTenant(sp.GetRequiredService<ITenantScopeAccessor>().BusinessUnitId)));
        services.AddScoped<ILLMService>(_ => new StubLlm());
        services.AddScoped(_ => intake);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    /// <summary>This collection shares one migrated database; a test owns only the rows it made.</summary>
    private async Task CleanupAsync(long emailConfigurationId)
    {
        await using var ctx = _database.ContextFor(null);
        await ctx.EmailIngests.Where(x => x.EmailConfigurationId == emailConfigurationId)
            .ExecuteDeleteAsync();
        await ctx.EmailConfigurations.Where(x => x.Id == emailConfigurationId)
            .ExecuteDeleteAsync();
    }

    /// <summary>
    /// Capture, scripted. The refusal is production's own
    /// <see cref="EmailInquiryIntakeResult.Refused"/> — the verdict a real evidence-store outage
    /// returns — so the poller takes the same branch it takes when the object store is down.
    /// </summary>
    private sealed class ScriptedIntake(bool safeToAcknowledge) : IEmailInquiryIntakeService
    {
        public Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
        {
            return Task.FromResult(safeToAcknowledge
                ? new EmailInquiryIntakeResult(
                    AssemblyId: 1, BatchId: Guid.NewGuid(), Scheduled: 1, AlreadyScheduled: 0,
                    Held: 0, ExpectedComponents: 1, AlreadyCaptured: false,
                    SafeToAcknowledge: true, FailureReason: null)
                : EmailInquiryIntakeResult.Refused("the evidence store is unavailable"));
        }

        /// <summary>Not this stub's subject: no test here resumes a held message.</summary>
        public Task<EmailInquiryResumeResult> ResumeSchedulingAsync(
            long businessUnitId, long assemblyId, CancellationToken ct = default,
            EmailInquirySchedulingGrant? grant = null)
            => Task.FromResult(new EmailInquiryResumeResult(
                EmailInquiryResumeOutcome.NothingToResume, 0, 0));
    }

    private sealed class PollerEnvironment(string contentRoot) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = contentRoot;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "poller-durability-tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRoot;
        public string EnvironmentName { get; set; } = "Development";
    }
}
