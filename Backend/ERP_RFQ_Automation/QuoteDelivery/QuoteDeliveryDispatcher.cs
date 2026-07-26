using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;

namespace ERP_RFQ_Automation.QuoteDelivery;

public interface IQuoteDeliverySender
{
    Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct);
}

public sealed class QuoteDeliveryPreSendException(string errorCode, Exception innerException)
    : Exception(errorCode, innerException);

public sealed class QuoteDeliverySender(IQuoteService quotes, IEmailService email) : IQuoteDeliverySender
{
    public async Task SendAsync(QuoteDeliveryEnvelope request, CancellationToken ct)
    {
        byte[] pdf;
        try
        {
            pdf = await quotes.GenerateQuotePdfAsync(request.QuoteId, request.BusinessUnitId);
        }
        catch (Exception exception)
        {
            throw new QuoteDeliveryPreSendException(exception.GetType().Name, exception);
        }
        await email.SendEmailAsync(request.RecipientEmail, request.Subject, request.Body,
            new List<(string FileName, byte[] FileContent, string ContentType)>
            {
                (request.AttachmentFileName, pdf, "application/pdf")
            }, request.FromEmail, request.BusinessUnitId);
    }
}

public sealed class QuoteDeliveryDispatcher(IServiceScopeFactory scopes, ILogger<QuoteDeliveryDispatcher> logger)
{
    private readonly string _workerId = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        IReadOnlyList<QuoteDeliveryEnvelope> requests;
        await using (var scope = scopes.CreateAsyncScope())
            requests = await scope.ServiceProvider.GetRequiredService<IQuoteDeliveryStore>()
                .ClaimAsync(_workerId, 10, TimeSpan.FromMinutes(2), ct);

        foreach (var request in requests)
        {
            await using var scope = scopes.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IQuoteDeliveryStore>();
            try
            {
                await scope.ServiceProvider.GetRequiredService<IQuoteDeliverySender>().SendAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (QuoteDeliveryPreSendException exception)
            {
                var errorCode = exception.Message;
                await store.FailAsync(request.Id, _workerId, request.LeaseToken, errorCode, 8, ct);
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

            try
            {
                await scope.ServiceProvider.GetRequiredService<IQuoteService>()
                    .FinalizeQuoteDeliveryAsync(request.QuoteId, request.BusinessUnitId, ct);
                await store.CompleteAsync(request.Id, _workerId, request.LeaseToken, ct);
                logger.LogInformation("Quote delivery {DeliveryId} completed on attempt {AttemptCount}.",
                    request.Id, request.AttemptCount);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                var errorCode = exception.GetType().Name;
                await store.MarkOutcomeUncertainAsync(request.Id, _workerId, request.LeaseToken, errorCode, ct);
                logger.LogCritical("Quote delivery {DeliveryId} has an uncertain external outcome after {ErrorCode} on attempt {AttemptCount}.",
                    request.Id, errorCode, request.AttemptCount);
            }
        }
        return requests.Count;
    }
}

public sealed class QuoteDeliveryWorker(QuoteDeliveryDispatcher dispatcher, ILogger<QuoteDeliveryWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Quote delivery worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var count = await dispatcher.DispatchOnceAsync(stoppingToken);
                if (count == 0) await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                logger.LogError("Quote delivery worker cycle failed with {ErrorCode}.", exception.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }
    }
}
