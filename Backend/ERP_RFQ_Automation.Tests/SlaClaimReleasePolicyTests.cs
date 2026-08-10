using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The SLA claim RELEASE POLICY — the half of send-once that was wrong.
///
/// <para>The claim-before-send mechanism was already correct. What was not: the claim was
/// DELETED whenever the send did not report success, and "success" only ever meant "nothing
/// threw", because <c>SlaNotifications</c> caught every exception and discarded the
/// <see cref="EmailDeliveryReceipt"/> the transport returns. So SMTP that accepted a message
/// body and then dropped the connection before its 250 — routine, not exotic — deleted the
/// claim, and five minutes later the next sweep mailed the supervisor the same escalation
/// again with no record of the first attempt. The mirror failure was quieter: a provider that
/// returned nothing at all was read as delivered, the claim was kept forever, and the alert was
/// never sent.</para>
///
/// <para>These tests pin the three properties that fix it: an accepted receipt is required for
/// "delivered", an ambiguous failure is UNCERTAIN and never re-sent, and a release is a status
/// transition that leaves the audit row behind.</para>
/// </summary>
public sealed class SlaClaimReleasePolicyTests
{
    private const long Bu = 4_100;

    // ------------------------------------------------ the transport's own verdict

    [Fact]
    public async Task A_send_that_throws_after_transport_acceptance_is_uncertain_not_failed()
    {
        // SMTP took the body and then lost the connection before its 250.
        var sender = new ScriptedEmailSender { OnSend = _ => throw new IOException("connection reset by peer") };
        var notifications = new SlaNotifications(sender, NullLogger<SlaNotifications>.Instance);

        var result = await notifications.SendDeadlineAlertAsync(
            "supervisor@tenant.test", "Sam", "escalated", "PO-1", "head", "detail", Bu);

        Assert.Equal(SlaSendOutcome.Uncertain, result.Outcome);
        Assert.False(result.Delivered);
        Assert.Equal(1, sender.Calls);
    }

    [Fact]
    public async Task A_provider_that_returns_no_receipt_is_not_treated_as_delivered()
    {
        var sender = new ScriptedEmailSender { OnSend = _ => null };
        var notifications = new SlaNotifications(sender, NullLogger<SlaNotifications>.Instance);

        var result = await notifications.SendDeadlineAlertAsync(
            "supervisor@tenant.test", "Sam", "escalated", "PO-1", "head", "detail", Bu);

        // Unprovable in either direction, so it is uncertain — never "delivered", which is how
        // the alert used to be silently swallowed while its claim was kept forever.
        Assert.Equal(SlaSendOutcome.Uncertain, result.Outcome);
        Assert.Equal("ACCEPTANCE_EVIDENCE_MISSING", result.Reason);
    }

    [Fact]
    public async Task A_receipt_without_an_acceptance_reference_is_not_delivered()
    {
        var sender = new ScriptedEmailSender
        {
            OnSend = _ => new EmailDeliveryReceipt("smtp", "   ", DateTimeOffset.UtcNow)
        };
        var notifications = new SlaNotifications(sender, NullLogger<SlaNotifications>.Instance);

        var result = await notifications.SendDeadlineAlertAsync(
            "supervisor@tenant.test", "Sam", "escalated", "PO-1", "head", "detail", Bu);

        Assert.Equal(SlaSendOutcome.Uncertain, result.Outcome);
    }

    [Fact]
    public async Task An_accepted_receipt_is_delivered_and_carries_its_evidence()
    {
        var sender = new ScriptedEmailSender
        {
            OnSend = _ => new EmailDeliveryReceipt("smtp", "queued-as-12345", DateTimeOffset.UtcNow)
        };
        var notifications = new SlaNotifications(sender, NullLogger<SlaNotifications>.Instance);

        var result = await notifications.SendDeadlineAlertAsync(
            "supervisor@tenant.test", "Sam", "escalated", "PO-1", "head", "detail", Bu);

        Assert.Equal(SlaSendOutcome.Sent, result.Outcome);
        Assert.Equal("smtp", result.Provider);
        Assert.Equal("queued-as-12345", result.AcceptanceReference);
    }

    [Fact]
    public async Task No_recipient_is_definitely_not_sent_rather_than_uncertain()
    {
        // The one failure that PROVES the provider never had the message: the transport is
        // never entered. Only this class of failure may be retried.
        var sender = new ScriptedEmailSender
        {
            OnSend = _ => new EmailDeliveryReceipt("smtp", "queued", DateTimeOffset.UtcNow)
        };
        var notifications = new SlaNotifications(sender, NullLogger<SlaNotifications>.Instance);

        var result = await notifications.SendDeadlineAlertAsync(
            "   ", "Sam", "escalated", "PO-1", "head", "detail", Bu);

        Assert.Equal(SlaSendOutcome.NotSent, result.Outcome);
        Assert.Equal(0, sender.Calls);
    }

    [Fact]
    public async Task An_empty_digest_is_definitely_not_sent()
    {
        var sender = new ScriptedEmailSender
        {
            OnSend = _ => new EmailDeliveryReceipt("smtp", "queued", DateTimeOffset.UtcNow)
        };
        var notifications = new SlaNotifications(sender, NullLogger<SlaNotifications>.Instance);

        var result = await notifications.SendStaleQuotesDigestAsync(
            "owner@tenant.test", "Owner", Array.Empty<StaleQuoteDigestLine>(), Bu);

        Assert.Equal(SlaSendOutcome.NotSent, result.Outcome);
        Assert.Equal(0, sender.Calls);
    }

    // ------------------------------------------------ what the ledger does with that verdict

    [Fact]
    public async Task A_released_claim_leaves_an_audit_row_and_frees_the_key()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu);
        var claim = await SlaSweepWorker.TryClaimEventAsync(ctx, Bu, "quote", 9, "expired", null, default);
        Assert.NotNull(claim);

        await SlaSweepWorker.ReleaseEventClaimAsync(ctx, claim!, default);

        // The row is STILL THERE — SlaEvent is the audit trail as well as the dedup ledger, and
        // deleting it destroyed the only evidence that an attempt was ever made.
        using var verify = db.ContextFor(null);
        var audit = Assert.Single(await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SlaEventStatuses.Released, audit.Status);
        Assert.NotNull(audit.SettledOn);

        // And the key is free, so the next sweep genuinely retries.
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(ctx, Bu, "quote", 9, "expired", null, default));
    }

    [Fact]
    public async Task An_uncertain_claim_is_kept_so_the_alert_is_never_sent_twice()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu);
        var claim = await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "supplier-order-ack", 55, "escalated", null, "boss@tenant.test", default);
        Assert.NotNull(claim);

        await SlaSweepWorker.SettleClaimAsync(ctx, claim!, SlaEventStatuses.Uncertain,
            "SEND_THREW:IOException", null, null, default);

        // A later sweep must NOT be able to claim it again: the provider may already have it.
        Assert.Null(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "supplier-order-ack", 55, "escalated", null, "boss@tenant.test", default));

        using var verify = db.ContextFor(null);
        var audit = Assert.Single(await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SlaEventStatuses.Uncertain, audit.Status);
        Assert.Equal("SEND_THREW:IOException", audit.OutcomeReason);
        Assert.Equal("boss@tenant.test", audit.Recipient);
    }

    [Fact]
    public async Task A_sent_claim_records_the_acceptance_evidence()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu);
        var claim = await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "lead", 7, "overdue", null, "owner@tenant.test", default);
        await SlaSweepWorker.SettleClaimAsync(ctx, claim!, SlaEventStatuses.Sent, null,
            "smtp", "queued-as-9", default);

        using var verify = db.ContextFor(null);
        var audit = Assert.Single(await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
        Assert.Equal(SlaEventStatuses.Sent, audit.Status);
        Assert.Equal("smtp", audit.Provider);
        Assert.Equal("queued-as-9", audit.AcceptanceReference);
    }

    // ------------------------------------------------ one claim per recipient

    [Fact]
    public async Task A_failure_for_recipient_B_does_not_suppress_recipient_A()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu);

        // Same alert, two people. Under one shared claim these were indistinguishable.
        var claimA = await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "supplier-order-ack", 88, "escalated", null, "a@tenant.test", default);
        var claimB = await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "supplier-order-ack", 88, "escalated", null, "b@tenant.test", default);
        Assert.NotNull(claimA);
        Assert.NotNull(claimB);
        Assert.NotEqual(claimA!.DedupKey, claimB!.DedupKey);

        await SlaSweepWorker.SettleClaimAsync(ctx, claimA, SlaEventStatuses.Sent, null, "smtp", "ok", default);
        await SlaSweepWorker.ReleaseEventClaimAsync(ctx, claimB, default);

        // B is retried on the next sweep...
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "supplier-order-ack", 88, "escalated", null, "b@tenant.test", default));
        // ...and A, who already has the email, is not mailed a second time.
        Assert.Null(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "supplier-order-ack", 88, "escalated", null, "a@tenant.test", default));
    }

    [Fact]
    public async Task An_alert_already_sent_under_the_legacy_recipient_less_key_is_not_re_sent()
    {
        // The migration window. Rows written before the recipient joined the key never recorded
        // who they were mailed to, so no backfill can reconstruct it; the legacy key is consulted
        // as a suppression instead. It can cost a copy, never produce a duplicate.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu);
            seed.Set<SlaEvent>().Add(new SlaEvent
            {
                BusinessUnitId = Bu,
                EntityType = "lead",
                EntityId = 4_242,
                Level = "overdue",
                DedupKey = SlaEvent.BuildDedupKey("lead", 4_242, "overdue"),
                Status = SlaEventStatuses.Sent,
                CreatedOn = DateTime.UtcNow.AddDays(-1)
            });
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu);
        Assert.Null(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu, "lead", 4_242, "overdue", null, "owner@tenant.test", default));
    }

    // ------------------------------------------------ harness

    private sealed class ScriptedEmailSender : IEmailSender
    {
        public Func<EmailMessage, EmailDeliveryReceipt?> OnSend { get; init; } = _ => null;
        public int Calls { get; private set; }

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(OnSend(message));
        }
    }
}
