using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.CommercialFinance;

public sealed class FinanceOutboxDispatcherOptions
{
    public const string SectionName = "CommercialFinance:OutboxDispatcher";

    public bool Enabled { get; set; }
    public string? Endpoint { get; set; }
    public string? HmacSecret { get; set; }
    public int BatchSize { get; set; } = 20;
    public int MaxConcurrency { get; set; } = 4;
    public int MaxAttempts { get; set; } = 10;
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(2);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromSeconds(10);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan MaximumDispatcherBackoff { get; set; } = TimeSpan.FromMinutes(1);
}

internal sealed class FinanceOutboxDispatcherOptionsValidator
    : IValidateOptions<FinanceOutboxDispatcherOptions>
{
    private readonly IHostEnvironment _environment;

    public FinanceOutboxDispatcherOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, FinanceOutboxDispatcherOptions options)
    {
        var failures = new List<string>();

        if (options.BatchSize is < 1 or > 200)
            failures.Add("BatchSize must be between 1 and 200.");
        if (options.MaxConcurrency is < 1 or > 32)
            failures.Add("MaxConcurrency must be between 1 and 32.");
        if (options.MaxConcurrency > options.BatchSize)
            failures.Add("MaxConcurrency cannot exceed BatchSize.");
        if (options.MaxAttempts is < 1 or > 100)
            failures.Add("MaxAttempts must be between 1 and 100.");

        ValidateRange(options.LeaseDuration, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(15),
            nameof(options.LeaseDuration), failures);
        ValidateRange(options.RequestTimeout, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(10),
            nameof(options.RequestTimeout), failures);
        ValidateRange(options.PollInterval, TimeSpan.FromMilliseconds(250), TimeSpan.FromMinutes(5),
            nameof(options.PollInterval), failures);
        ValidateRange(options.InitialRetryDelay, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24),
            nameof(options.InitialRetryDelay), failures);
        ValidateRange(options.MaximumRetryDelay, TimeSpan.FromSeconds(1), TimeSpan.FromHours(24),
            nameof(options.MaximumRetryDelay), failures);
        ValidateRange(options.MaximumDispatcherBackoff, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(15),
            nameof(options.MaximumDispatcherBackoff), failures);

        if (options.LeaseDuration - options.RequestTimeout < TimeSpan.FromSeconds(5))
            failures.Add("LeaseDuration must exceed RequestTimeout by at least five seconds.");
        if (options.MaximumRetryDelay < options.InitialRetryDelay)
            failures.Add("MaximumRetryDelay cannot be shorter than InitialRetryDelay.");

        if (!string.IsNullOrEmpty(options.HmacSecret) && options.HmacSecret.Length < 32)
            failures.Add("HmacSecret must contain at least 32 characters when configured.");
        if (options.Enabled && _environment.IsProduction() && string.IsNullOrEmpty(options.HmacSecret))
            failures.Add("HmacSecret is required when the dispatcher is enabled in production.");

        if (options.Enabled)
        {
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            {
                failures.Add("Endpoint must be an absolute HTTP or HTTPS URL when the dispatcher is enabled.");
            }
            else if (_environment.IsProduction() && endpoint.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add("Endpoint must use HTTPS in production.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateRange(
        TimeSpan value,
        TimeSpan minimum,
        TimeSpan maximum,
        string propertyName,
        ICollection<string> failures)
    {
        if (value < minimum || value > maximum)
            failures.Add($"{propertyName} must be between {minimum} and {maximum}.");
    }
}
