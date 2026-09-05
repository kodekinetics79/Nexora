using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Notifications;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.QuoteDelivery;

public interface IQuoteDeliverySender
{
    Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct);
}

public sealed class QuoteDeliveryPreSendException(string errorCode, Exception innerException)
    : Exception(errorCode, innerException)
{
    /// <summary>
    /// true when retrying can never succeed, so the row must be dead-lettered on this attempt
    /// instead of consuming the retry budget. Set for the R5 price-binding refusal: a price
    /// that changed after it was attested has still changed on the eighth attempt, and every
    /// extra attempt keeps an unauthorised send alive in the outbox.
    /// </summary>
    public bool Permanent { get; init; }
}

public sealed class QuoteDeliverySender(IQuoteService quotes, IEmailSender email) : IQuoteDeliverySender
{
    public async Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct)
    {
        byte[] pdf;
        try
        {
            // The fingerprint bound when this send was AUTHORISED is handed to the renderer,
            // which refuses to produce the document unless the quote's prices still hash to
            // it AND a current attestation still covers them. Fail closed, before any bytes
            // exist: an email cannot be un-sent.
            pdf = await quotes.GenerateQuotePdfAsync(
                request.QuoteId, request.BusinessUnitId, request.AttestedPriceFingerprint, ct);
        }
        catch (ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException exception)
        {
            throw new QuoteDeliveryPreSendException(exception.GetType().Name, exception) { Permanent = true };
        }
        catch (ERP_RFQ_Automation.Services.QuoteIssuerIdentityMissingException exception)
        {
            // Permanent for the same reason: nobody's setup screen fills itself in between
            // attempt one and attempt eight. Retrying a configuration gap keeps a doomed send
            // alive in the outbox and reports a fixable omission as flaky infrastructure.
            throw new QuoteDeliveryPreSendException(exception.GetType().Name, exception) { Permanent = true };
        }
        catch (Exception exception)
        {
            throw new QuoteDeliveryPreSendException(exception.GetType().Name, exception);
        }
        var message = new EmailMessage
        {
            Subject = request.Subject,
            HtmlBody = request.Body,
            BusinessUnitId = request.BusinessUnitId.ToString(),
            TenantId = request.BusinessUnitId.ToString(),
            // The quote leaves from the VERIFIED sender of the tenant that owns it: the tenant's
            // own active SMTP mailbox when one exists, the platform address otherwise (issue #54).
            // Resolved by IOutboundSenderResolver, the same authority the mailbox screen reads.
            OwningBusinessUnitId = request.BusinessUnitId,
            // The transport's verified From identity remains authoritative. A tenant's company
            // address is a Reply-To only; promoting arbitrary profile text to SMTP From would
            // break SPF/DMARC and let one tenant impersonate another domain.
            ReplyTo = string.IsNullOrWhiteSpace(request.FromEmail)
                ? null
                : new EmailAddress(request.FromEmail),
            Attachments =
            {
                new EmailAttachment(request.AttachmentFileName, pdf, "application/pdf")
            }
        };
        message.AddTo(request.RecipientEmail);

        // A null receipt is how the guarded/console transport says that no provider accepted the
        // message. Never turn that into Quote.SentOn: the outbox records an uncertain terminal
        // outcome and an operator can see that nothing was proven delivered.
        var receipt = await email.SendAsync(message, ct);
        if (receipt is null || string.IsNullOrWhiteSpace(receipt.AcceptanceReference))
            throw new InvalidOperationException("The outbound provider returned no acceptance evidence.");
    }
}

/// <summary>
/// Drains the quote-delivery outbox.
///
/// Tenant isolation
/// ----------------
/// This used to claim in ONE unscoped batch: <c>QuoteDeliveryRequest</c>'s global query filter is
/// <c>CurrentTenantId == null || BusinessUnitId == CurrentTenantId</c>, and a background worker
/// has no ambient tenant, so the filter degraded to "do not filter" and a single claim took the
/// oldest ten rows across EVERY tenant, leasing and then processing them under the bypass role.
///
/// It now follows the SlaSweepWorker shape (Sla/SlaSweepWorker.cs:136-175): resolve the tenant
/// ids ONCE — the only query that runs unscoped, and it reads nothing but business unit ids —
/// then do all claiming and all sending inside a pushed tenant scope, with a fail-closed guard
/// that refuses to run a tenant's batch if the DbContext did not pick the scope up. One
/// misbehaving tenant is logged and skipped rather than stopping the others.
/// </summary>
public sealed class QuoteDeliveryDispatcher(
    IServiceScopeFactory scopes,
    ILogger<QuoteDeliveryDispatcher> logger,
    ITenantScopeAccessor tenantScope)
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        var businessUnits = await ResolvePendingBusinessUnitsAsync(ct);
        var dispatched = 0;

        foreach (var businessUnitId in businessUnits)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                dispatched += await DispatchTenantAsync(businessUnitId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                logger.LogError(exception,
                    "Quote delivery dispatch failed for BU {BusinessUnitId}; continuing with next tenant.",
                    businessUnitId);
            }
        }

        return dispatched;
    }

    /// <summary>
    /// The ONLY query in this worker that runs without a tenant scope (and therefore under the
    /// BYPASSRLS pipeline role): the distinct business units with a deliverable row. It reads
    /// nothing but tenant ids, and the predicate mirrors the store's own claim eligibility so a
    /// tenant with nothing to send is never scoped up at all.
    ///
    /// <para><b>Suspended and archived tenants are dropped here, and this is the worker where that
    /// matters most.</b> It sends a quote PDF, from the customer's own address, to the customer's
    /// CLIENT. A suspended tenant that keeps dispatching means the product is transacting on behalf
    /// of an account we have told the customer is switched off, in front of a third party who
    /// cannot know that — a conduct problem before it is a cost one.</para>
    ///
    /// <para>Skipping DEFERS rather than drops: the row is never claimed, so no lease is taken,
    /// <c>AttemptCount</c> is untouched, no retry budget is spent, and the delivery goes out
    /// unchanged on the first cycle after reinstatement. The gate is consulted in THIS scope
    /// because it is the only one with no tenant pushed — under a pushed scope its platform read is
    /// refused at column level and fails OPEN (see <c>ITenantWorkGate</c>).</para>
    /// </summary>
    private async Task<IReadOnlyList<long>> ResolvePendingBusinessUnitsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
        var now = DateTime.UtcNow;
        var deliveries = db.Set<QuoteDeliveryRequest>().AsNoTracking().IgnoreQueryFilters();
        var pending = deliveries
            .Where(x => x.CompletedOn == null && x.DeadLetteredOn == null)
            .Where(x => x.AvailableOn <= now || x.LeaseUntil != null)
            .Select(x => x.BusinessUnitId);
        // A delivery the provider ACCEPTED whose quote never reached SENT: the process died, or
        // the quote's own bookkeeping threw, between sealing the row and updating the quote.
        // The tenant needs a visit so the status can catch up without anything being resent.
        // Bounded by AvailableOn, which the reconcile pushes forward when a catch-up fails.
        var unfinalized = deliveries
            .Where(x => x.CompletedOn != null && x.AvailableOn <= now)
            .Where(x => db.Quotes.IgnoreQueryFilters().Any(q =>
                q.Id == x.QuoteId && q.BusinessUnitId == x.BusinessUnitId && q.SentOn == null))
            .Select(x => x.BusinessUnitId);
        var businessUnits = await pending.Concat(unfinalized)
            .Distinct()
            .OrderBy(id => id)
            .ToListAsync(ct);

        // Resolved from THIS scope rather than injected: the dispatcher is a singleton and the gate
        // is scoped, so a constructor dependency would fail startup scope validation.
        var gate = scope.ServiceProvider
            .GetService<ERP_RFQ_Automation.Platform.Lifecycle.ITenantWorkGate>();
        if (gate is null || businessUnits.Count == 0) return businessUnits;

        return await gate.FilterServiceableAsync(businessUnits, ct);
    }

    private async Task<int> DispatchTenantAsync(long businessUnitId, CancellationToken ct)
    {
        using var tenant = tenantScope.Push(businessUnitId);

        // Catch-up BEFORE claiming anything new: a quote whose delivery was sealed on a previous
        // cycle (or by a process that died right after sealing it) is marked SENT here. This
        // never sends — it only reads the delivery ledger and derives the quote's status from it.
        await using (var scope = scopes.CreateAsyncScope())
        {
            EnsureScoped(scope.ServiceProvider, businessUnitId);
            // Optional only for compositions that send without a quote service (delivery
            // harnesses); the production container always has one, and the send below
            // requires it regardless.
            var reconciled = scope.ServiceProvider.GetService<IQuoteService>() is { } quotes
                ? await quotes.ReconcileDeliveredQuotesAsync(businessUnitId, ct)
                : new DeliveredQuoteReconciliation(0, 0);
            if (reconciled.Deferred > 0)
                logger.LogError(
                    "{Count} delivered quote(s) for BU {BusinessUnitId} could not be marked SENT; the ledger "
                    + "row names the error and the status update will be retried. Nothing was resent.",
                    reconciled.Deferred, businessUnitId);
        }

        IReadOnlyList<QuoteDeliveryEnvelope> requests;
        await using (var scope = scopes.CreateAsyncScope())
        {
            EnsureScoped(scope.ServiceProvider, businessUnitId);
            requests = await scope.ServiceProvider.GetRequiredService<IQuoteDeliveryStore>()
                .ClaimAsync(_workerId, 10, TimeSpan.FromMinutes(2), ct);
        }

        foreach (var request in requests)
        {
            await using var scope = scopes.CreateAsyncScope();
            EnsureScoped(scope.ServiceProvider, businessUnitId);
            var store = scope.ServiceProvider.GetRequiredService<IQuoteDeliveryStore>();

            // A claim that came back for another tenant means the scope was not honoured
            // somewhere; refuse to act on it rather than sending one tenant's quote under
            // another tenant's identity.
            if (request.BusinessUnitId != businessUnitId)
                throw new InvalidOperationException(
                    $"Quote delivery {request.Id} was claimed for BU {businessUnitId} but belongs to BU " +
                    $"{request.BusinessUnitId}; refusing to dispatch cross-tenant work.");

            try
            {
                await scope.ServiceProvider.GetRequiredService<IQuoteDeliverySender>().SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (QuoteDeliveryPreSendException exception)
            {
                var errorCode = exception.Message;
                // maxAttempts 1 dead-letters on THIS attempt (QuoteDeliveryStore.MutateLeaseAsync
                // dead-letters once AttemptCount >= maxAttempts, and the claim already incremented
                // it). A permanent pre-send refusal must not sit in the outbox waiting to be
                // retried against content nobody attested to.
                await store.FailAsync(request.Id, _workerId, request.LeaseToken, errorCode,
                    exception.Permanent ? 1 : 8, ct);
                if (exception.Permanent)
                    logger.LogError(exception.InnerException,
                        "Quote delivery {DeliveryId} for quote {QuoteId} was refused permanently with {ErrorCode}: "
                        + "the quote's prices no longer match what was attested when the send was authorised. "
                        + "Nothing was emailed.",
                        request.Id, request.QuoteId, errorCode);
                else
                    logger.LogWarning("Quote delivery {DeliveryId} failed before external send with {ErrorCode} on attempt {AttemptCount}.",
                        request.Id, errorCode, request.AttemptCount);
                continue;
            }
            catch (Exception exception)
            {
                var errorCode = exception.GetType().Name;
                await store.MarkOutcomeUncertainAsync(request.Id, _workerId, request.LeaseToken, errorCode, ct);
                logger.LogCritical("Quote delivery {DeliveryId} has an uncertain SMTP outcome after {ErrorCode} on attempt {AttemptCount}.",
                    request.Id, errorCode, request.AttemptCount);
                continue;
            }

            // The provider has ACCEPTED the message with evidence. From here on nothing is
            // uncertain about the send — only about our own bookkeeping — so the order of the
            // two writes that follow is load-bearing:
            //
            //   1. Seal the ledger row (CompletedOn). This is the at-most-once fact: a sealed
            //      row can never be claimed again and can never be "recovered" into a resend.
            //   2. Mark the quote SENT. This is derived from (1) and is idempotent, so if it
            //      fails here — a lifecycle refusal, a follow-up-task write, a dropped
            //      connection — ReconcileDeliveredQuotesAsync repeats it on the next cycle.
            //
            // It used to run the other way round, with any failure after the send recorded as
            // DeliveryOutcomeUncertain. That told the rep "the customer may or may not have
            // received it" about a quote the provider had just confirmed accepting, left the
            // quote in DRAFT — editable, with the customer holding the PDF — and made the quote
            // number unsendable for good.
            try
            {
                await store.CompleteAsync(request.Id, _workerId, request.LeaseToken, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Accepted by the provider, but the acceptance could not be written. The ledger
                // cannot prove the send, so the honest terminal state IS uncertain; the sweep
                // would reach the same verdict when the lease expires. Recording it now keeps
                // the error code; if even that fails, the sweep still fences it.
                var errorCode = exception.GetType().Name;
                logger.LogCritical(exception,
                    "Quote delivery {DeliveryId} was accepted by the provider but the acceptance could not be "
                    + "recorded ({ErrorCode}); the delivery is fenced as uncertain and will not be resent.",
                    request.Id, errorCode);
                try
                {
                    await store.MarkOutcomeUncertainAsync(request.Id, _workerId, request.LeaseToken, errorCode, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception fenceException)
                {
                    logger.LogError(fenceException,
                        "Quote delivery {DeliveryId} could not be fenced; the expired-lease sweep will fence it.",
                        request.Id);
                }
                continue;
            }

            try
            {
                await scope.ServiceProvider.GetRequiredService<IQuoteService>()
                    .FinalizeQuoteDeliveryAsync(request.QuoteId, request.BusinessUnitId, ct);
                logger.LogInformation("Quote delivery {DeliveryId} completed on attempt {AttemptCount}.",
                    request.Id, request.AttemptCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Delivered and sealed. Only the quote's status is behind, and the next cycle's
                // reconcile repeats this step without touching the outbox.
                logger.LogError(exception,
                    "Quote delivery {DeliveryId} was delivered and sealed, but quote {QuoteId} could not be marked "
                    + "SENT ({ErrorCode}); the status update will be retried without resending.",
                    request.Id, request.QuoteId, exception.GetType().Name);
            }
        }
        return requests.Count;
    }

    /// <summary>
    /// Fail closed. If the DbContext in this scope did not resolve the pushed tenant, every
    /// query below it would silently run cross-tenant under the bypass role again — which is
    /// exactly the defect being fixed, so it must be an error and not a warning.
    /// </summary>
    private static void EnsureScoped(IServiceProvider provider, long businessUnitId)
    {
        var db = provider.GetRequiredService<ErpRfqAutomationContext>();
        if (db.ScopedTenantId == businessUnitId) return;
        throw new InvalidOperationException(
            $"Quote delivery dispatch refused to run for BU {businessUnitId}: the DbContext resolved tenant " +
            $"{db.ScopedTenantId?.ToString() ?? "<none>"}. Tenant scope is mandatory for this worker.");
    }
}

public sealed class QuoteDeliveryWorker(
    QuoteDeliveryDispatcher dispatcher,
    ILogger<QuoteDeliveryWorker> logger,
    IQuoteDeliveryWorkerHeartbeat heartbeat)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Quote delivery worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            heartbeat.Beat();
            try
            {
                var count = await dispatcher.DispatchOnceAsync(stoppingToken);
                heartbeat.RecordSuccess();
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                heartbeat.RecordFailure();
                logger.LogError("Quote delivery worker cycle failed with {ErrorCode}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
