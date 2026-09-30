using System.IO.Compression;
using System.Net.Http;
using System.Text;
using ERP_RFQ_Automation.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// PERF-07 (pilot audit 2026-09-28): no API response was compressed, so Decide on the
/// 1,500-line request moved 6.6 MB of JSON. The registration Program.cs uses must compress JSON
/// over HTTPS (TLS ends at the edge) and leave files and the event stream exactly as written.
/// </summary>
public sealed class ResponseCompressionTests
{
    private static readonly byte[] PdfBytes = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 4_000));
    private static readonly byte[] XlsxBytes = Enumerable.Range(0, 4_000).Select(i => (byte)(i % 251)).ToArray();

    [Theory]
    [InlineData("br", "br")]
    [InlineData("gzip", "gzip")]
    [InlineData("br, gzip", "br")]
    public async Task Json_is_compressed_over_https(string accept, string expected)
    {
        using var host = await StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/json");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", accept);

        using var response = await client.SendAsync(request);
        var compressed = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(expected, Assert.Single(response.Content.Headers.ContentEncoding));
        var json = Decompress(expected, compressed);
        Assert.StartsWith("{\"lines\":[", json);
        Assert.True(compressed.Length * 5 < Encoding.UTF8.GetByteCount(json),
            $"{compressed.Length} bytes on the wire for {Encoding.UTF8.GetByteCount(json)} bytes of JSON");
    }

    [Theory]
    [InlineData("/pdf", "application/pdf")]
    [InlineData("/xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task Files_are_sent_exactly_as_written(string path, string contentType)
    {
        using var host = await StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        using var response = await client.SendAsync(request);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(path == "/pdf" ? PdfBytes : XlsxBytes, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_event_stream_is_never_compressed()
    {
        using var host = await StartAsync();
        using var client = Client(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/sse");
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "br, gzip");

        using var response = await client.SendAsync(request);

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.StartsWith("data: one", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public void Program_registers_the_compression_and_runs_it_before_the_controllers()
    {
        var program = File.ReadAllText(Path.Combine(RepositoryRoot(), "Backend/ERP_RFQ_Automation/Program.cs"));
        Assert.Contains("builder.Services.AddNexoraResponseCompression();", program, StringComparison.Ordinal);
        var use = program.IndexOf("app.UseResponseCompression();", StringComparison.Ordinal);
        Assert.True(use > 0, "app.UseResponseCompression() is not in the pipeline");
        Assert.True(use < program.IndexOf("app.MapControllers()", StringComparison.Ordinal));
        Assert.True(use < program.IndexOf("app.UseAuthentication();", StringComparison.Ordinal));
    }

    private static async Task<IHost> StartAsync() => await new HostBuilder()
        .ConfigureWebHost(web => web
            .UseTestServer()
            .ConfigureServices(services => services.AddRouting().AddNexoraResponseCompression())
            .Configure(app =>
            {
                app.UseResponseCompression();
                app.UseRouting();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGet("/json", () => Results.Json(new
                    {
                        lines = Enumerable.Range(1, 500).Select(i => new
                        {
                            id = i,
                            verificationDetail = "Read from the document; not yet checked by a person.",
                            catalogPolicyVersion = "lead-conversion-preview/v1",
                        })
                    }));
                    endpoints.MapGet("/pdf", () => Results.File(PdfBytes, "application/pdf", "quote.pdf"));
                    endpoints.MapGet("/xlsx", () => Results.File(XlsxBytes,
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "lines.xlsx"));
                    endpoints.MapGet("/sse", async (HttpContext context) =>
                    {
                        context.Response.ContentType = "text/event-stream";
                        await context.Response.WriteAsync("data: one\n\n" + new string(' ', 2_000));
                        await context.Response.Body.FlushAsync();
                        await context.Response.WriteAsync("data: two\n\n");
                    });
                });
            }))
        .StartAsync();

    private static HttpClient Client(IHost host)
    {
        var server = host.GetTestServer();
        server.BaseAddress = new Uri("https://localhost/");
        return server.CreateClient();
    }

    private static string Decompress(string encoding, byte[] body)
    {
        using var input = new MemoryStream(body);
        using Stream decoder = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decoder, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Backend", "ERP_RFQ_Automation")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Nexora repository root.");
    }
}
