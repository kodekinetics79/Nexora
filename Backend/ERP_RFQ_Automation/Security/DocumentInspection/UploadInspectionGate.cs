using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Security.DocumentInspection;

/// <summary>
/// The bytes of an upload that inspection has looked at, with its verdict. <see cref="Content"/>
/// is positioned at 0 and is the ONLY stream the caller should parse from: it is the same bytes
/// inspection saw, so nothing can be substituted between the verdict and the parse.
/// </summary>
public sealed class InspectedUpload(MemoryStream content, FileInspectionResult inspection) : IAsyncDisposable
{
    public MemoryStream Content { get; } = content;
    public FileInspectionResult Inspection { get; } = inspection;
    public bool IsCleared => Inspection.IsCleared;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>
/// The one way a synchronous upload door (spreadsheet imports, bank statement import) puts a file
/// through <see cref="IFileInspectionService"/> before parsing it, and the one refusal it answers
/// with when inspection says no.
///
/// <para><b>Why this exists.</b> Six spreadsheet uploaders and the treasury import opened the
/// posted file and handed it straight to a parser; the most any of them checked was the four-byte
/// ZIP signature. Every asynchronous door (extraction, e-mail, purchase-order documents, delivery
/// evidence, certificates) already runs the shared inspection — signature, archive safety and a
/// malware verdict — so the spreadsheet doors were the only way to get an uninspected file parsed
/// on the server. This gives them the same inspection and the same refusal shape.</para>
///
/// <para><b>Fail closed.</b> A scanner that cannot answer produces a Quarantined, retryable
/// verdict. The asynchronous doors hold such a document and replay it later; a synchronous import
/// has nowhere to hold it, so the gate REFUSES with 503 and a Retry-After rather than parsing an
/// unscanned file. "Scanner down" must never read as "file clean".</para>
///
/// <para><b>The refusal shape.</b> A <see cref="ProblemDetails"/> with the inspection reason as
/// <c>detail</c> and the machine code as the <c>errorCode</c> extension — exactly what
/// <c>DeliveryController</c> and <c>MaterialTraceabilityController</c> answer, so the UI has one
/// contract to branch on. <c>success=false</c> and <c>message</c> ride along as extensions because
/// the spreadsheet pages have only ever read <c>message</c> from a refusal; both readers see the
/// same sentence.</para>
/// </summary>
public static class UploadInspectionGate
{
    /// <summary>Copies the posted file into memory and inspects those bytes.</summary>
    public static async Task<InspectedUpload> InspectAsync(
        IFileInspectionService inspection, IFormFile file, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        ArgumentNullException.ThrowIfNull(file);

        var content = new MemoryStream();
        try
        {
            await file.CopyToAsync(content, cancellationToken);
            content.Position = 0;
            var verdict = await inspection.InspectAsync(
                new FileInspectionRequest(
                    content,
                    Path.GetFileName(file.FileName),
                    file.ContentType,
                    file.Length),
                cancellationToken);
            content.Position = 0;
            return new InspectedUpload(content, verdict);
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// The response for a verdict that is not Cleared. 400 for a terminal rejection; 503 with
    /// Retry-After when the verdict is retryable (the scanner could not answer).
    /// </summary>
    public static ObjectResult Refuse(ControllerBase controller, FileInspectionResult inspection, string title)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(inspection);

        var status = inspection.IsRetryable
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status400BadRequest;
        var problem = new ProblemDetails
        {
            Status = status,
            Title = inspection.IsRetryable ? "Security scanning is unavailable" : title,
            Detail = inspection.Reason
        };
        problem.Extensions["errorCode"] = inspection.ErrorCode;
        problem.Extensions["outcome"] = inspection.IsRetryable ? "AwaitingSecurityScan" : inspection.Status.ToString();
        problem.Extensions["success"] = false;
        problem.Extensions["message"] = inspection.Reason;

        if (inspection.IsRetryable)
        {
            if (controller.HttpContext is { } http)
                http.Response.Headers.RetryAfter = "30";
            return new ObjectResult(problem)
            {
                StatusCode = status,
                ContentTypes = { "application/problem+json" }
            };
        }

        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }
}
