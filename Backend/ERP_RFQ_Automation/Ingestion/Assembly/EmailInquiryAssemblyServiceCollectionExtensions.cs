using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// THE composition of the email-inquiry assembly capability.
///
/// <para>It exists so that <c>Program</c> and the resolution test call the SAME code. The test
/// previously re-declared these registrations, which meant it asserted properties of itself:
/// deleting every <c>AddScoped</c> from <c>Program</c> left it green — the precise failure
/// ("registered nowhere", so the package is unreachable at runtime) that the test was written to
/// prevent.</para>
///
/// <para>They are registered together because they are one capability. Capture writes the durable
/// message, the coordinator is the only writer of its state afterwards, and the evidence reader
/// is how anything gets the original bytes back. A graph missing any one of them is not a
/// degraded email pipeline; it is a broken one.</para>
/// </summary>
public static class EmailInquiryAssemblyServiceCollectionExtensions
{
    public static IServiceCollection AddEmailInquiryAssembly(this IServiceCollection services)
    {
        services.AddScoped<IEmailInquiryCaptureService, EmailInquiryCaptureService>();
        services.AddScoped<IEmailInquiryAssemblyCoordinator, EmailInquiryAssemblyCoordinator>();
        services.AddScoped<IRawEmailEvidenceReader, RawEmailEvidenceReader>();

        // The declared per-message boundaries, as an injected value rather than a static, so an
        // operator can tune them in configuration without the planner reaching for a default.
        services.AddSingleton(EmailInquiryLimits.Default);

        return services;
    }
}
