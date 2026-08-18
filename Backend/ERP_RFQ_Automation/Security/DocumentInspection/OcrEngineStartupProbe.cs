using System.Globalization;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Tesseract;

namespace ERP_RFQ_Automation.Security.DocumentInspection;

public enum OcrEngineProbeStatus
{
    /// <summary>Not run yet. Reported honestly rather than as green.</summary>
    NotProbed,

    /// <summary>Tesseract and pdfium both loaded and a real recognition pass completed.</summary>
    Operational,

    /// <summary>Tesseract loaded, but pdfium did not: images OCR, scanned PDFs do not.</summary>
    RasterizerUnavailable,

    /// <summary><c>tessdata/eng.traineddata</c> is missing from the deployed content root.</summary>
    LanguageDataMissing,

    /// <summary>The native Tesseract/Leptonica libraries could not be bound in this container.</summary>
    EngineUnavailable
}

/// <param name="EngineVersion">Tesseract's own version string, or null when it never loaded.</param>
/// <param name="Languages">Language packs found in the shipped tessdata directory.</param>
/// <param name="Reason">Operator-readable account of the status. Never rendered to a tenant.</param>
public sealed record OcrEngineProbeResult(
    OcrEngineProbeStatus Status,
    string? EngineVersion,
    IReadOnlyList<string> Languages,
    string Reason,
    string? Diagnostics = null)
{
    public bool IsOperational => Status == OcrEngineProbeStatus.Operational;

    public static OcrEngineProbeResult NotProbed { get; } = new(
        OcrEngineProbeStatus.NotProbed, null, Array.Empty<string>(),
        "The OCR engine has not been probed yet.");
}

/// <summary>Process-local OCR readiness ledger — the house heartbeat idiom
/// (<c>IEmailPollerHealth</c>, <c>IExtractionWorkerHeartbeat</c>).</summary>
public interface IOcrEngineHealth
{
    OcrEngineProbeResult Snapshot();
    void Record(OcrEngineProbeResult result);
}

public sealed class OcrEngineHealth : IOcrEngineHealth
{
    private readonly object _gate = new();
    private OcrEngineProbeResult _result = OcrEngineProbeResult.NotProbed;

    public OcrEngineProbeResult Snapshot() { lock (_gate) return _result; }

    public void Record(OcrEngineProbeResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate) _result = result;
    }
}

/// <summary>
/// Boot-time proof that optical character recognition actually works in THIS container.
///
/// <para>
/// WHY. As of 2026-08-06 <c>extraction_runs</c> held 21 rows, every one of them
/// <c>ocr_status='NotRequired'</c> and <c>ocr_page_count=0</c>: OCR had never executed on a
/// production job, and <c>grep -rn "Tesseract|tessdata|Ocr" HealthChecks/ Program.cs</c> returned
/// nothing. The managed Tesseract 5.2.0 wrapper binding the Debian <c>libtesseract</c> in the
/// <c>aspnet:8.0</c> image was PLAUSIBLE and UNVERIFIED, which meant the first OCR attempt in
/// front of a client would also be the first test of the native libraries. If that bind fails,
/// every scanned PDF and every photographed tender returns empty text with
/// <c>OcrStatus='Failed'</c> and looks like a bad document rather than a broken container.
/// </para>
///
/// <para>
/// WHAT IT PROVES, EXACTLY. It loads <c>tessdata/eng.traineddata</c> from the content root the
/// reader uses, constructs a <see cref="TesseractEngine"/> (which binds Leptonica and Tesseract
/// and parses the language data), runs a real <see cref="TesseractEngine.Process(Pix)"/> pass over
/// a generated raster, and then loads a generated one-page PDF through pdfium (Docnet) — the
/// rasteriser the scanned-PDF path needs just as much, and whose absence produces the identical
/// symptom. It does NOT claim recognition ACCURACY: asserting a specific transcription would need
/// a real rendered-text fixture, and a probe that fails for a font reason would be worse than no
/// probe. The claim it makes is the claim that was missing — the engines load and a recognition
/// pass runs to completion.
/// </para>
///
/// <para>
/// It runs off the startup path and never delays the listener, exactly like
/// <see cref="MalwareScannerStartupProbe"/>.
/// </para>
/// </summary>
public sealed class OcrEngineStartupProbe : IHostedService, IDisposable
{
    private readonly IOcrEngineHealth _health;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<OcrEngineStartupProbe> _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _probe;

    public OcrEngineStartupProbe(
        IOcrEngineHealth health,
        IHostEnvironment environment,
        ILogger<OcrEngineStartupProbe> log)
    {
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _probe = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping.Cancel();
        if (_probe is null) return;
        try
        {
            await _probe.WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is OperationCanceledException or TimeoutException)
        {
            // Shutdown wins; the probe is diagnostic only.
        }
    }

    public void Dispose() => _stopping.Dispose();

    internal Task<OcrEngineProbeResult> RunAsync()
    {
        var result = Probe(Path.Combine(_environment.ContentRootPath, "tessdata"));
        _health.Record(result);
        Report(result);
        return Task.FromResult(result);
    }

    /// <summary>
    /// Runs the probe against <paramref name="tessDataPath"/> — the SAME path
    /// <c>ProductionDocumentReader</c> resolves, so a probe that passes proves the reader's own
    /// configuration rather than a parallel one.
    /// </summary>
    public static OcrEngineProbeResult Probe(string tessDataPath)
    {
        var languages = ReadLanguages(tessDataPath);
        if (!languages.Contains("eng", StringComparer.OrdinalIgnoreCase))
        {
            return new OcrEngineProbeResult(
                OcrEngineProbeStatus.LanguageDataMissing, null, languages,
                "The English Tesseract language data (tessdata/eng.traineddata) is not present in the "
                + "deployed content root, so no OCR can run. Confirm the csproj tessdata content copy "
                + "survived publish.",
                $"tessdata path: {tessDataPath}");
        }

        string? version;
        try
        {
            using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
            version = engine.Version;

            using var raster = Pix.LoadFromMemory(CreateProbeBitmap());
            using var page = engine.Process(raster);
            _ = page.GetText();
        }
        catch (Exception exception)
        {
            return new OcrEngineProbeResult(
                OcrEngineProbeStatus.EngineUnavailable, null, languages,
                "The Tesseract OCR engine could not be loaded in this container, so every scanned PDF "
                + "and every image will return no text. Read the DETAIL below before installing "
                + "anything: the packages are usually already present, and the load fails on the "
                + "NAMES the wrapper probes for (libtesseract50.so, libleptonica-1.82.0.so, in an "
                + "x64/ directory beside the assembly).",
                Detail(exception));
        }

        try
        {
            using var reader = DocLib.Instance.GetDocReader(CreateProbePdf(), new PageDimensions(1.0));
            _ = reader.GetPageCount();
        }
        catch (Exception exception)
        {
            return new OcrEngineProbeResult(
                OcrEngineProbeStatus.RasterizerUnavailable, version, languages,
                "Tesseract loaded, but the pdfium PDF rasteriser did not. Images will OCR; SCANNED PDFs "
                + "will not, because their pages cannot be turned into rasters to recognise.",
                Detail(exception));
        }

        return new OcrEngineProbeResult(
            OcrEngineProbeStatus.Operational, version, languages,
            $"Tesseract {version} loaded language data [{string.Join(", ", languages)}] and completed a "
            + "recognition pass; the pdfium rasteriser loaded.");
    }

    /// <summary>
    /// The whole exception chain, because the outer one says nothing.
    ///
    /// <para>A native load failure surfaces as <c>TargetInvocationException</c>, whose message is
    /// the fixed sentence "Exception has been thrown by the target of an invocation." This probe
    /// reported exactly that and stopped, so production logged only that OCR was unavailable —
    /// and the remedy it suggested, install the native libraries, was WRONG, because they were
    /// already installed. Diagnosing it meant rebuilding the container by hand.</para>
    ///
    /// <para>The sentence that actually identifies the fault is one level down:</para>
    /// <code>DllNotFoundException: Failed to find library "libleptonica-1.82.0.so" for platform x64.</code>
    ///
    /// <para>Bounded to five links so a deep chain cannot flood the log.</para>
    /// </summary>
    private static string Detail(Exception exception)
    {
        var parts = new List<string>();
        for (var current = exception; current is not null && parts.Count < 5;
             current = current.InnerException)
            parts.Add($"{current.GetType().Name}: {current.Message}");
        return string.Join(" -> ", parts);
    }

    private static IReadOnlyList<string> ReadLanguages(string tessDataPath)
    {
        try
        {
            if (!Directory.Exists(tessDataPath)) return Array.Empty<string>();
            return Directory.EnumerateFiles(tessDataPath, "*.traineddata")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void Report(OcrEngineProbeResult result)
    {
        switch (result.Status)
        {
            case OcrEngineProbeStatus.Operational:
                _log.LogInformation(
                    "OCR startup probe passed. Engine={Engine} Languages={Languages} Environment={Environment}.",
                    result.EngineVersion, string.Join(",", result.Languages), _environment.EnvironmentName);
                return;

            default:
                _log.LogError(
                    "OCR STARTUP PROBE FAILED — SCANNED DOCUMENTS WILL RETURN NO TEXT. Status={Status} "
                    + "Languages={Languages} Environment={Environment}. {Reason} Detail: {Diagnostics} "
                    + "Text-based documents are unaffected and continue to extract normally.",
                    result.Status, string.Join(",", result.Languages), _environment.EnvironmentName,
                    result.Reason, result.Diagnostics ?? "(none)");
                return;
        }
    }

    /// <summary>
    /// A 240x80 24-bit bitmap carrying high-contrast dark bars on white. Enough for Leptonica to
    /// accept and Tesseract to run a full recognition pass over; it deliberately asserts nothing
    /// about what comes back (see the type remarks).
    /// </summary>
    internal static byte[] CreateProbeBitmap()
    {
        const int width = 240;
        const int height = 80;
        var rowSize = ((24 * width + 31) / 32) * 4;
        const int headerSize = 54;
        var bitmap = new byte[headerSize + rowSize * height];

        bitmap[0] = 0x42;
        bitmap[1] = 0x4D;
        WriteInt32(bitmap, 2, bitmap.Length);
        WriteInt32(bitmap, 10, headerSize);
        WriteInt32(bitmap, 14, 40);
        WriteInt32(bitmap, 18, width);
        WriteInt32(bitmap, 22, height);
        bitmap[26] = 1;
        bitmap[28] = 24;
        WriteInt32(bitmap, 34, rowSize * height);
        WriteInt32(bitmap, 38, 2835);
        WriteInt32(bitmap, 42, 2835);

        for (var y = 0; y < height; y++)
        {
            var row = headerSize + y * rowSize;
            for (var x = 0; x < width; x++)
            {
                var dark = y is > 24 and < 56 && (x / 12) % 2 == 0 && x is > 20 and < 220;
                var value = (byte)(dark ? 0x00 : 0xFF);
                bitmap[row + x * 3] = value;
                bitmap[row + x * 3 + 1] = value;
                bitmap[row + x * 3 + 2] = value;
            }
        }
        return bitmap;
    }

    /// <summary>
    /// A minimal, structurally valid one-page PDF carrying <paramref name="text"/> in Helvetica.
    /// Used by the probe to load pdfium, and exposed so tests can build a REAL native-PDF fixture
    /// instead of a 45-byte stub — the corpus's only .pdf is exactly such a stub, and it proves
    /// nothing about a path that has never run.
    /// </summary>
    internal static byte[] CreateProbePdf(string text = "NEXORA OCR PROBE")
    {
        var escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal);
        var content = $"BT /F1 18 Tf 56 720 Td ({escaped}) Tj ET\n";
        var contentBytes = Encoding.ASCII.GetBytes(content);

        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {contentBytes.Length} >>\nstream\n{content}endstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
        };

        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        foreach (var (body, index) in objects.Select((body, index) => (body, index)))
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{index + 1} 0 obj\n{body}\nendobj\n");
        }

        var xrefOffset = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            pdf.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");

        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private static void WriteInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value & 0xFF);
        buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}

/// <summary>
/// Reports the OCR engine on <c>/ready</c>.
///
/// <para>
/// DEGRADED, NOT UNHEALTHY, and the distinction is deliberate. OCR being unavailable disables ONE
/// capability — scanned PDFs and images. Every text-based document still extracts. Turning
/// <c>/ready</c> red would drain the instance and stop ALL ingestion because a subset of formats
/// cannot be read, which is a worse outcome than the fault. Degraded keeps the instance serving
/// while putting the truth — and the remediation — on the operator surface, which is precisely
/// what was missing when OCR was believed to work for three months without ever running.
/// </para>
/// </summary>
public sealed class OcrEngineHealthCheck : IHealthCheck
{
    private readonly IOcrEngineHealth _health;

    public OcrEngineHealthCheck(IOcrEngineHealth health) => _health = health;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var result = _health.Snapshot();
        var data = new Dictionary<string, object>
        {
            ["status"] = result.Status.ToString(),
            ["engine"] = result.EngineVersion ?? "not-loaded",
            ["languages"] = result.Languages.Count == 0 ? "none" : string.Join(",", result.Languages)
        };
        if (result.Diagnostics is { } diagnostics) data["diagnostics"] = diagnostics;

        return Task.FromResult(result.Status switch
        {
            OcrEngineProbeStatus.Operational => HealthCheckResult.Healthy(result.Reason, data),
            OcrEngineProbeStatus.NotProbed => HealthCheckResult.Degraded(
                "The OCR engine has not been probed yet; scanned-document support is unverified in this process.",
                data: data),
            _ => HealthCheckResult.Degraded(result.Reason, data: data)
        });
    }
}
