namespace ERP_RFQ_Automation.Security.DocumentInspection;

/// <summary>
/// SINGLE SOURCE OF TRUTH for the document-intake extension allow-list.
///
/// Every intake door (email attachments, manual upload, watched folders) and the
/// security inspection layer (<see cref="DocumentFileInspectionService"/>) must derive
/// their extension filter from THIS set — never from a private copy. Historically the
/// email door kept its own list, which drifted in both directions: it accepted .pptx
/// (which inspection then rejected, producing a confusing tenant-facing rejection) and
/// silently dropped .xls/.csv attachments (which inspection would have accepted) —
/// losing supplier quotes and customer RFQs that routinely arrive in those formats.
///
/// Adding or removing a format here changes BOTH the intake filters and the inspection
/// gate at once, so they can no longer disagree. Any new format must also be handled by
/// the content-signature detection in <see cref="DocumentFileInspectionService"/> and
/// the extraction reader (Extraction/ProductionDocumentReader.cs), which currently
/// covers exactly this set.
///
/// Intentionally narrower door-specific filters (e.g. the SEC watched folder accepts
/// only .doc) are allowed, but they must be SUBSETS of this list — see the drift-guard
/// tests in DocumentIntakeAllowListTests.
/// </summary>
public static class DocumentIntakeAllowList
{
    /// <summary>
    /// The canonical set of file extensions (lower-case, dot-prefixed, compared
    /// case-insensitively) accepted by document intake and cleared for inspection.
    /// </summary>
    public static readonly IReadOnlySet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".csv", ".txt",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".webp",
        // FR-RFQ-02 required formats added 2026-08. Each is admitted only because inspection
        // can TYPE it from its bytes and a reader exists for it:
        //
        //   .html/.htm — typed by a document-start markup signature (HtmlDocumentTextExtractor
        //     .HasHtmlSignature); read by a table-aware HtmlAgilityPack extractor. HtmlAgilityPack
        //     is a parser, not a browser: no script executes and no external reference resolves.
        //     Attack considered: stored XSS via an uploaded page. Nexora never serves an uploaded
        //     document as HTML — only its extracted TEXT is stored — and script/iframe/object
        //     subtrees are removed before any text is read.
        //
        //   .eml/.msg — an email is an ENVELOPE. Its attachments are decoded and put through THIS
        //     same inspection before anything reads them, so a message cannot be used to smuggle
        //     an unscanned or macro-bearing document past intake. Attack considered: an .eml as an
        //     inspection bypass, and message-in-message recursion — refused past one nested level.
        ".html", ".htm", ".eml", ".msg"
    };

    /// <summary>True when <paramref name="extension"/> (e.g. ".xls") is on the allow-list.</summary>
    public static bool IsAllowed(string? extension) =>
        !string.IsNullOrEmpty(extension) && Extensions.Contains(extension);
}
