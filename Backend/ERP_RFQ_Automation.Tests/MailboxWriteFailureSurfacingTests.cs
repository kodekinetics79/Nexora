using System.ComponentModel.DataAnnotations;
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
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// THE SAME DEFECT PRODUCTS AND CATEGORIES HAD, ON THE FRONT DOOR OF THE PRODUCT.
/// <c>Mailbox{Create,Update}RequestDTO.EmailAddress</c> and <c>.Username</c> were
/// <c>[StringLength(320)]</c> — the RFC 5321 maximum address length — over
/// <c>Email_Configurations."EmailAddress"</c> and <c>."Username"</c>, both
/// <c>character varying(255)</c>. A 262-character address therefore passed ModelState, was mapped
/// onto the entity and died at the INSERT as Postgres <c>22001</c>. <c>MailboxController</c>
/// contained no <c>22001</c> handling of any kind, so it reached the operator through the global
/// handler as <c>{"error":"An unexpected error occurred."}</c>.
///
/// <para>This is the mailbox setup screen: email in, lead out. An administrator who cannot tell
/// "that address is too long" from "the server is broken" has no way to finish onboarding, and the
/// product ingests nothing at all until they do.</para>
///
/// <para>These tests pin the SHAPE of every answer the two write doors can give: an over-long
/// value is a 400 that names the field, a duplicate is a 409, and anything else — a foreign-key
/// violation, a unique violation, an RLS denial, a null-argument bug in our own code — still
/// escapes to the global handler, where it stays logged. The narrow catches must never be the
/// thing that removes a log entry.</para>
///
/// <para>Every credential in this file is an obvious placeholder. This module handles live
/// customer mailbox passwords; a realistic-looking one in a fixture is a real one to whoever finds
/// it next.</para>
/// </summary>
public sealed class MailboxWriteFailureSurfacingTests
{
    private const long Bu = 9_792;

    /// <summary>varchar(255), as configured on EmailConfiguration in ErpRfqAutomationContext.</summary>
    private const int AddressColumn = 255;

    private const string FakePassword = "not-a-real-password";

    private static PostgresException ValueTooLong() => new(
        messageText: "value too long for type character varying(255)",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "22001");

    private static PostgresException ForeignKeyViolation() => new(
        messageText: "insert or update on table \"Email_Configurations\" violates foreign key constraint",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "23503");

    private static PostgresException UniqueViolation() => new(
        messageText: "duplicate key value violates unique constraint",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: "23505");

    // ---- the DTO cap must now stop the value before it reaches the column -----------------

    private static IReadOnlyList<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    private static bool Rejects(object dto, string property) =>
        Validate(dto).Any(r => r.MemberNames.Contains(property));

    /// <summary>A syntactically valid address longer than the column — 250 characters of local
    /// part plus "@tenant.test" is 262.</summary>
    private static string OverLongAddress() => new string('a', 250) + "@tenant.test";

    /// <summary>Exactly 255: the longest address the column can actually hold.</summary>
    private static string LongestStorableAddress() => new string('a', 243) + "@tenant.test";

    [Fact]
    public void An_address_the_column_cannot_hold_is_refused_before_the_insert_on_both_doors()
    {
        var create = CreateRequest() with { EmailAddress = OverLongAddress() };
        var update = UpdateRequest() with { EmailAddress = OverLongAddress() };

        Assert.True(OverLongAddress().Length > AddressColumn, "fixture must exceed the column");
        Assert.True(Rejects(create, nameof(MailboxCreateRequestDTO.EmailAddress)),
            "an address longer than the varchar(255) column must fail validation, not the INSERT");
        Assert.True(Rejects(update, nameof(MailboxUpdateRequestDTO.EmailAddress)));
    }

    [Fact]
    public void A_username_the_column_cannot_hold_is_refused_before_the_insert_on_both_doors()
    {
        var create = CreateRequest() with { Username = new string('u', AddressColumn + 1) };
        var update = UpdateRequest() with { Username = new string('u', AddressColumn + 1) };

        Assert.True(Rejects(create, nameof(MailboxCreateRequestDTO.Username)));
        Assert.True(Rejects(update, nameof(MailboxUpdateRequestDTO.Username)));
    }

    /// <summary>Tightening must not have cost anything the column can actually hold.</summary>
    [Fact]
    public void An_address_that_fits_the_column_is_still_accepted()
    {
        var create = CreateRequest() with { EmailAddress = LongestStorableAddress() };

        Assert.Equal(AddressColumn, LongestStorableAddress().Length);
        Assert.False(Rejects(create, nameof(MailboxCreateRequestDTO.EmailAddress)));
    }

    /// <summary>
    /// End to end through the real action: the refusal the DTO cap produces must reach the caller
    /// as a 400 that NAMES the field. <see cref="CopyValidationInto"/> runs the same
    /// DataAnnotations validation the <c>[ApiController]</c> model binder runs, so this exercises
    /// the path a browser takes rather than a hand-made ModelState error.
    /// </summary>
    [Fact]
    public async Task Creating_a_mailbox_with_an_over_long_address_is_a_400_naming_the_field_not_a_500()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var request = CreateRequest() with { EmailAddress = OverLongAddress() };
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new InvalidOperationException("the write must never be reached")));
        CopyValidationInto(controller, request);

        var result = await controller.Create(request);

        // BadRequest(ModelState) publishes a SerializableError keyed by property name — the field
        // the caller has to shorten is named in the body, which is the whole point.
        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var state = Assert.IsType<SerializableError>(bad.Value);
        Assert.Contains(nameof(MailboxCreateRequestDTO.EmailAddress), state.Keys);
    }

    [Fact]
    public async Task Editing_a_mailbox_to_an_over_long_address_is_a_400_naming_the_field_not_a_500()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        await SeedMailboxAsync(database);
        var request = UpdateRequest() with { EmailAddress = OverLongAddress() };
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new InvalidOperationException("the write must never be reached")));
        CopyValidationInto(controller, request);

        var result = await controller.Update(1, request);

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var state = Assert.IsType<SerializableError>(bad.Value);
        Assert.Contains(nameof(MailboxUpdateRequestDTO.EmailAddress), state.Keys);
    }

    // ---- the 22001 backstop, for the fields this screen does not cap ---------------------

    [Fact]
    public async Task Creating_a_mailbox_with_a_value_too_long_for_its_column_is_a_400_that_says_so()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new DbUpdateException("insert failed", ValueTooLong())));

        var result = await controller.Create(CreateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("too long", problem.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.True(problem.Extensions.ContainsKey("traceId"));
    }

    [Fact]
    public async Task Editing_a_mailbox_to_a_value_too_long_for_its_column_is_a_400_that_says_so()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        await SeedMailboxAsync(database);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new DbUpdateException("update failed", ValueTooLong())));

        var result = await controller.Update(1, UpdateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("too long", problem.Detail, StringComparison.OrdinalIgnoreCase);
    }

    // ---- everything that is NOT 22001 must still reach the global handler ----------------

    /// <summary>
    /// The correction that matters most. A blanket <c>catch (DbUpdateException)</c> would report a
    /// foreign-key violation — <c>Email_Configurations."BusinessUnitID"</c> has one — as "shorten
    /// the address", and would consume the log entry that says what really happened.
    /// </summary>
    [Fact]
    public async Task A_foreign_key_violation_on_create_still_reaches_the_global_handler()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new DbUpdateException("insert failed", ForeignKeyViolation())));

        var escaped = await Assert.ThrowsAsync<DbUpdateException>(() => controller.Create(CreateRequest()));

        Assert.Equal("23503", Assert.IsType<PostgresException>(escaped.InnerException).SqlState);
    }

    [Fact]
    public async Task A_unique_violation_on_create_is_not_reported_as_a_length_problem()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new DbUpdateException("insert failed", UniqueViolation())));

        var escaped = await Assert.ThrowsAsync<DbUpdateException>(() => controller.Create(CreateRequest()));

        Assert.Equal("23505", Assert.IsType<PostgresException>(escaped.InnerException).SqlState);
    }

    [Fact]
    public async Task A_unique_violation_on_edit_is_not_reported_as_a_length_problem_either()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        await SeedMailboxAsync(database);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new DbUpdateException("update failed", UniqueViolation())));

        var escaped = await Assert.ThrowsAsync<DbUpdateException>(() => controller.Update(1, UpdateRequest()));

        Assert.Equal("23505", Assert.IsType<PostgresException>(escaped.InnerException).SqlState);
    }

    // ---- ArgumentException must not swallow its own subclasses --------------------------

    /// <summary>
    /// THE LIE THIS PREVENTS. <c>ArgumentNullException</c> derives from <c>ArgumentException</c>,
    /// and there is a real path that throws one on this very call: <c>IamAuditWriter</c> begins
    /// <c>ExecuteAtomicAsync</c> with <c>ArgumentNullException.ThrowIfNull(work)</c>, and
    /// <c>ProtectedSecretConverter</c> refuses a null secret the same way. An unfiltered catch
    /// would report a bug in our own process to the operator as a mailbox that already exists —
    /// a sentence about their data describing a fault in ours, sending them to change an address
    /// that was never wrong, while the log entry naming the real cause was consumed by the catch.
    /// </summary>
    [Fact]
    public async Task A_null_argument_bug_in_our_own_code_is_not_converted_into_a_409()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new ArgumentNullException("work", "Value cannot be null.")));

        var escaped = await Assert.ThrowsAsync<ArgumentNullException>(() => controller.Create(CreateRequest()));

        Assert.Equal("work", escaped.ParamName);
    }

    [Fact]
    public async Task An_out_of_range_argument_bug_is_not_converted_into_an_answer_either()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new ArgumentOutOfRangeException("port", "Index was out of range.")));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.Create(CreateRequest()));
    }

    [Fact]
    public async Task A_null_argument_bug_on_the_edit_path_is_not_converted_into_a_409_either()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        await SeedMailboxAsync(database);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new ArgumentNullException("work", "Value cannot be null.")));

        var escaped = await Assert.ThrowsAsync<ArgumentNullException>(() => controller.Update(1, UpdateRequest()));

        Assert.Equal("work", escaped.ParamName);
    }

    /// <summary>
    /// And the genuine ArgumentException must still be claimed. Excluding the subclasses must not
    /// have excluded the base case with them.
    /// </summary>
    [Fact]
    public async Task A_duplicate_reported_as_a_plain_ArgumentException_is_a_409()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new ArgumentException("A mailbox for intake@tenant.test already exists in this Business Unit.")));

        var result = await controller.Create(CreateRequest());

        var conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(conflict.Value);
        Assert.Equal(StatusCodes.Status409Conflict, problem.Status);
        Assert.Contains("already exists", problem.Detail);
    }

    [Fact]
    public async Task A_plain_ArgumentException_that_is_not_a_duplicate_is_a_400_that_names_it()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(Bu);
        var controller = ControllerFor(context, new ThrowingAuditWriter(
            new ArgumentException("Protocol IMAPS is not supported.")));

        var result = await controller.Create(CreateRequest());

        var bad = Assert.IsType<BadRequestObjectResult>(result.Result);
        var problem = Assert.IsType<ProblemDetails>(bad.Value);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Contains("IMAPS", problem.Detail);
    }

    // ---- harness ------------------------------------------------------------------------

    /// <summary>
    /// Runs the same DataAnnotations validation the <c>[ApiController]</c> model binder runs and
    /// copies the result into the controller's ModelState, so a test exercises the real refusal
    /// rather than a hand-authored error whose key could be anything.
    /// </summary>
    private static void CopyValidationInto(ControllerBase controller, object request)
    {
        foreach (var failure in Validate(request))
            foreach (var member in failure.MemberNames)
                controller.ModelState.AddModelError(member, failure.ErrorMessage ?? "invalid");
    }

    private static MailboxController ControllerFor(ErpRfqAutomationContext context, IIamAuditWriter audit) =>
        new(context, new UnreachableTester(), audit, NullLogger<MailboxController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("businessUnitId", Bu.ToString()),
                        new Claim(ClaimTypes.NameIdentifier, "7"),
                        new Claim(ClaimTypes.Email, "admin@tenant.test")
                    ], "test"))
                }
            }
        };

    /// <summary>VerifyBeforeSave is off on every fixture, so the probe must never run. Anything
    /// that dials a host during a unit test is a defect in the test, not a slow test.</summary>
    private static MailboxCreateRequestDTO CreateRequest() => new()
    {
        ConfigurationName = "RFQ intake",
        EmailAddress = "intake@tenant.test",
        Protocol = "IMAP",
        Host = "imap.example.com",
        Port = 993,
        Username = "intake@tenant.test",
        Password = FakePassword,
        UseSsl = true,
        PollingInterval = 5,
        IsActive = true,
        VerifyBeforeSave = false
    };

    private static MailboxUpdateRequestDTO UpdateRequest() => new()
    {
        ConfigurationName = "RFQ intake",
        EmailAddress = "intake@tenant.test",
        Host = "imap.example.com",
        Port = 993,
        Username = "intake@tenant.test",
        Password = null,
        UseSsl = true,
        PollingInterval = 5,
        IsActive = true,
        VerifyBeforeSave = false
    };

    private static async Task SeedMailboxAsync(TestDb database)
    {
        await using var ctx = database.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Bu);
        ctx.EmailConfigurations.Add(new EmailConfiguration
        {
            Id = 1,
            BusinessUnitId = Bu,
            ConfigurationName = "RFQ intake",
            EmailAddress = "intake@tenant.test",
            Protocol = "IMAP",
            Host = "imap.example.com",
            Port = 993,
            Username = "intake@tenant.test",
            Password = FakePassword,
            UseSsl = true,
            PollingInterval = 5,
            IsActive = true,
            CreatedOn = DateTime.UtcNow
        });
        await ctx.SaveChangesAsync();
    }

    /// <summary>
    /// An audit writer whose atomic write fails the way the real one does. Every other member
    /// throws, so a test that accidentally exercises one fails loudly instead of quietly passing.
    /// </summary>
    private sealed class ThrowingAuditWriter(Exception failure) : IIamAuditWriter
    {
        public Task ExecuteAtomicAsync(Func<Task> work, CancellationToken cancellationToken = default)
            => Task.FromException(failure);

        public IamAuditEvent Enlist(ClaimsPrincipal? principal, IamAuditEntry entry) => throw new NotSupportedException();

        public Task<IamAuditEvent> WriteAsync(ClaimsPrincipal? principal, IamAuditEntry entry, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction?> BeginAtomicAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    /// <summary>Every fixture sets VerifyBeforeSave = false, so reaching the tester is itself the
    /// failure.</summary>
    private sealed class UnreachableTester : IMailConnectionTester
    {
        public Task<MailConnectionTestResult> TestAsync(MailConnectionTestRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException("No unit test may open a mail connection.");
    }
}
