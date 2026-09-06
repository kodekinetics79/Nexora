using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The mailbox password is write-only on the way out and the screen never receives it, so
/// "leave it blank and I will use the stored one" is the only way an operator who does not know
/// the credential can fix a host or a port. That convenience is deliberate
/// (<see cref="MailboxUpdateRequestDTO.Password"/>) and these tests keep it.
///
/// <para>What it may not do is pay the credential out. <c>POST api/Mailbox/test</c> took the
/// mailbox id for the password and EVERY connection parameter from the body, so a caller holding
/// "Email &amp; SMTP: Edit" — granted so somebody can correct a typo, never accompanied by the
/// password itself — could name a host they control, ask for IMAP without TLS, and have this
/// server sign in there with the tenant's live customer-mailbox credential in cleartext. The
/// audit row recorded a failed connection test. <c>PUT api/Mailbox/{id}</c> with
/// <c>VerifyBeforeSave</c> is the same primitive and leaves no row change behind at all, because a
/// failed verification returns 422 before anything is written.</para>
///
/// <para>Every credential here is an obvious placeholder: this module handles live customer
/// mailbox passwords, and a realistic-looking one in a fixture is a real one to whoever finds it
/// next.</para>
/// </summary>
public sealed class MailboxStoredCredentialReplayTests
{
    private const long Bu = 9_431;
    private const long MailboxId = 1;

    /// <summary>The stored credential. No test may put this on the wire toward any other host.</summary>
    private const string StoredPassword = "not-a-real-stored-password";

    private const string StoredHost = "imap.tenant.test";
    private const string StoredAddress = "intake@tenant.test";
    private const int StoredPort = 993;

    private const string AttackerHost = "collector.attacker.example";

    // ---- POST /api/Mailbox/test ----------------------------------------------------------

    [Fact]
    public async Task A_blank_password_may_not_be_spent_on_a_host_from_the_request_body()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Test(new MailboxTestRequestDTO
        {
            MailboxId = MailboxId,
            Protocol = "IMAP",
            Host = AttackerHost,
            Port = 143,
            EmailAddress = "x@attacker.example",
            Username = "x",
            Password = "",
            UseSsl = false
        });

        // Asserted before the status code, because the status code is not the defect: what
        // matters is that the stored credential was never aimed at the caller's host.
        Assert.DoesNotContain(tester.Requests,
            r => r.Secret == StoredPassword && r.Host == AttackerHost);
        Assert.Empty(tester.Requests);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    /// <summary>A same-host test that only drops TLS is the cheapest version of the same read:
    /// the credential arrives at the real provider, in the clear, on a port the caller chose.</summary>
    [Fact]
    public async Task A_blank_password_may_not_be_downgraded_to_a_cleartext_session()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Test(StoredMailboxTest() with { Port = 143, UseSsl = false });

        Assert.DoesNotContain(tester.Requests,
            r => r.Secret == StoredPassword && r.Tls == MailTlsMode.None);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task A_blank_password_may_not_be_pointed_at_a_different_sign_in_identity()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        // IMAP signs in as the ADDRESS, so this is a different login against the same server —
        // enough to hand the password to a mailbox the caller does control.
        var result = await controller.Test(StoredMailboxTest() with { EmailAddress = "attacker@tenant.test" });

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(tester.Requests);
    }

    /// <summary>
    /// THE CONVENIENCE, WHICH IS THE POINT OF THE FEATURE. Re-testing the mailbox as it is saved
    /// still works without the password, and still authenticates with the stored one — otherwise
    /// the fix has simply deleted the button.
    /// </summary>
    [Fact]
    public async Task Re_testing_the_stored_mailbox_unchanged_still_uses_the_stored_password()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Test(StoredMailboxTest());

        Assert.IsType<OkObjectResult>(result.Result);
        var sent = Assert.Single(tester.Requests);
        Assert.Equal(StoredPassword, sent.Secret);
        Assert.Equal(StoredHost, sent.Host);
    }

    /// <summary>The restriction is on the STORED secret, not on hosts. A caller who types the
    /// password may still test wherever they like — they already know what they are sending.</summary>
    [Fact]
    public async Task A_typed_password_may_still_be_tested_against_any_permitted_host()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Test(StoredMailboxTest() with
        {
            Host = "imap.newprovider.test",
            Password = "typed-by-the-operator"
        });

        Assert.IsType<OkObjectResult>(result.Result);
        var sent = Assert.Single(tester.Requests);
        Assert.Equal("typed-by-the-operator", sent.Secret);
        Assert.NotEqual(StoredPassword, sent.Secret);
    }

    // ---- PUT /api/Mailbox/{id} -----------------------------------------------------------

    [Fact]
    public async Task Verifying_an_edit_that_moves_the_endpoint_may_not_spend_the_kept_password()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Update(MailboxId, StoredMailboxUpdate() with
        {
            Host = AttackerHost,
            Port = 143,
            UseSsl = false,
            Password = null,
            VerifyBeforeSave = true
        });

        Assert.DoesNotContain(tester.Requests,
            r => r.Secret == StoredPassword && r.Host == AttackerHost);
        Assert.IsType<BadRequestObjectResult>(result.Result);

        // And the refusal is not a silent save either.
        await using var reread = database.ContextFor(Bu);
        var row = await reread.EmailConfigurations.SingleAsync(x => x.Id == MailboxId);
        Assert.Equal(StoredHost, row.Host);
        Assert.Equal(StoredPort, row.Port);
    }

    /// <summary>Editing anything that is not the endpoint still verifies without the password —
    /// the ordinary "fix the polling interval" save.</summary>
    [Fact]
    public async Task Verifying_an_edit_that_keeps_the_endpoint_still_uses_the_kept_password()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Update(MailboxId, StoredMailboxUpdate() with
        {
            PollingInterval = 15,
            Password = null,
            VerifyBeforeSave = true
        });

        Assert.IsType<OkObjectResult>(result.Result);
        var sent = Assert.Single(tester.Requests);
        Assert.Equal(StoredPassword, sent.Secret);
        Assert.Equal(StoredHost, sent.Host);
    }

    /// <summary>A genuine move to a new provider is still verifiable — with the credential that
    /// provider issued, typed by the person who has it.</summary>
    [Fact]
    public async Task Moving_the_endpoint_with_a_rotated_password_still_verifies()
    {
        using var database = new TestDb();
        await SeedMailboxAsync(database);
        await using var context = database.ContextFor(Bu);
        var tester = new RecordingTester();
        var controller = ControllerFor(context, tester);

        var result = await controller.Update(MailboxId, StoredMailboxUpdate() with
        {
            Host = "imap.newprovider.test",
            Password = "rotated-not-a-real-password",
            VerifyBeforeSave = true
        });

        Assert.IsType<OkObjectResult>(result.Result);
        var sent = Assert.Single(tester.Requests);
        Assert.Equal("rotated-not-a-real-password", sent.Secret);
    }

    // ---- harness ------------------------------------------------------------------------

    /// <summary>The body the screen sends when an operator re-tests a saved mailbox: every field
    /// carries the stored value and the password is blank.</summary>
    private static MailboxTestRequestDTO StoredMailboxTest() => new()
    {
        MailboxId = MailboxId,
        Protocol = "IMAP",
        Host = StoredHost,
        Port = StoredPort,
        EmailAddress = StoredAddress,
        Username = StoredAddress,
        Password = null,
        UseSsl = true
    };

    private static MailboxUpdateRequestDTO StoredMailboxUpdate() => new()
    {
        ConfigurationName = "RFQ intake",
        EmailAddress = StoredAddress,
        Host = StoredHost,
        Port = StoredPort,
        Username = StoredAddress,
        Password = null,
        UseSsl = true,
        PollingInterval = 5,
        IsActive = true,
        VerifyBeforeSave = true
    };

    private static MailboxController ControllerFor(
        ErpRfqAutomationContext context, IMailConnectionTester tester) =>
        new(context, tester, new RecordingAuditWriter(), NullLogger<MailboxController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                        new Claim(ClaimTypes.Email, "editor@tenant.test")
                    ], "test"))
                }
            }
        };

    /// <summary>The row as <see cref="MailboxController.Create"/> writes one: protocol upper-cased
    /// by <c>TryProtocol</c>, host normalised, address and username trimmed, password stored
    /// through the value converter that decrypts it again on read.</summary>
    private static async Task SeedMailboxAsync(TestDb database)
    {
        await using var ctx = database.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.EmailConfigurations.Add(new EmailConfiguration
        {
            Id = MailboxId,
            BusinessUnitId = Bu,
            ConfigurationName = "RFQ intake",
            EmailAddress = StoredAddress,
            Protocol = "IMAP",
            Host = StoredHost,
            Port = StoredPort,
            Username = StoredAddress,
            Password = StoredPassword,
            UseSsl = true,
            PollingInterval = 5,
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>Records what would have gone on the wire and reports success, so a test can
    /// assert on the connection this server was about to open.</summary>
    private sealed class RecordingTester : IMailConnectionTester
    {
        public List<MailConnectionTestRequest> Requests { get; } = [];

        public Task<MailConnectionTestResult> TestAsync(
            MailConnectionTestRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new MailConnectionTestResult(
                Succeeded: true, Summary: "Connected.", request.Direction, request.Transport,
                request.Host, request.Port, request.Tls, Steps: [], NegotiatedSecurity: "TLS 1.3",
                InboxMessageCount: 0, CredentialsSentInClear: false, ProviderKey: null,
                ProviderDisplayName: null, ProviderNotes: []));
        }
    }

    private sealed class RecordingAuditWriter : IIamAuditWriter
    {
        public List<IamAuditEntry> Entries { get; } = [];

        public IamAuditEvent Enlist(ClaimsPrincipal? principal, IamAuditEntry entry)
        {
            Entries.Add(entry);
            return new IamAuditEvent { Action = entry.Action, TargetType = entry.TargetType };
        }

        public Task<IamAuditEvent> WriteAsync(
            ClaimsPrincipal? principal, IamAuditEntry entry, CancellationToken cancellationToken = default)
            => Task.FromResult(Enlist(principal, entry));

        public Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAtomicAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?>(null);

        public Task ExecuteAtomicAsync(Func<Task> work, CancellationToken cancellationToken = default) => work();
    }
}
