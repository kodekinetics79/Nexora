using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// Brotli and gzip for the API's JSON (PERF-07, pilot audit 2026-09-28).
///
/// <para>No response was compressed. Decide on the 1,500-line Aramco request moved 6.6 MB of JSON
/// (the workbench alone 3.77 MB, 299 KB gzipped). JSON and text compress by roughly 90%.</para>
///
/// <para><b>Only text types.</b> The framework's default list (JSON, text, XML, JavaScript,
/// wasm) plus problem+json. PDF, XLSX, DOCX and images are already compressed or binary and are
/// never listed, so file downloads pass through byte for byte. The agent stream
/// (<c>text/event-stream</c>) is excluded explicitly: a compressor buffers, and a stream must reach
/// the browser event by event.</para>
///
/// <para><b>HTTPS on purpose.</b> TLS ends at the hosting edge and the app learns the scheme from
/// forwarded headers, so compression has to be allowed on HTTPS or it would never run in
/// production. BREACH needs a secret in the body that an attacker can make a victim's browser
/// request with ambient credentials; this API authenticates with a bearer token the SPA sends in a
/// header (kept in localStorage), not a cookie, so a cross-site page cannot make such a request.</para>
///
/// <para><b>Fastest level.</b> These are dynamic responses; the fastest level still takes the
/// workbench from 3.77 MB to a few hundred KB without adding noticeable CPU per request.</para>
/// </summary>
public static class ResponseCompressionSetup
{
    public static readonly string[] ExcludedMimeTypes = ["text/event-stream"];

    public static IServiceCollection AddNexoraResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
            options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/problem+json"]);
            options.ExcludedMimeTypes = ExcludedMimeTypes;
        });
        services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
        return services;
    }
}
