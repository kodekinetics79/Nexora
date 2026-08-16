using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Decides, from a FILENAME alone, whether an attachment is recognisably legal or corporate
/// boilerplate rather than commercial content.
///
/// <para><b>The problem it solves.</b> Only a deterministic classifier may mark a part
/// <see cref="EmailInquiryComponentStatus.Ignored"/>; everything else that cannot be read becomes
/// <see cref="EmailInquiryComponentStatus.Skipped"/>, and one Skipped part sends the whole
/// message to review. So an unreadable "Terms &amp; Conditions.pdf" — a file the buyer attaches
/// to every mail they send and which contains not one priced line — downgraded an RFQ whose real
/// content had extracted perfectly. A review queue full of standard conditions of contract is how
/// a review gate stops being read, which costs the messages that genuinely needed a human.</para>
///
/// <para><b>The doctrine, unchanged.</b> Wrongly ignoring a part produces a Lead priced against
/// content nobody saw; wrongly reviewing one costs a few seconds of a human's attention. Those
/// two errors are not comparable, so this classifier only ever answers "yes" when the name is
/// UNAMBIGUOUS, and two hard exclusions are checked before any pattern is consulted:</para>
/// <list type="number">
///   <item>A spreadsheet is NEVER boilerplate. A bill of quantities is nearly always .xls/.xlsx,
///   and "Terms.xlsx" is far likelier to be a priced schedule with a terms tab than a legal
///   notice.</item>
///   <item>A name carrying RFQ/BOQ/quotation/tender/schedule/pricing vocabulary is NEVER
///   boilerplate, whatever else it says. "RFQ Terms and Pricing Schedule" is the enquiry.</item>
/// </list>
///
/// <para>Matching is on the NAME only. Nothing here reads or decodes a byte, so a hostile
/// attachment cannot influence the decision, the classification costs nothing, and it is
/// reproducible from the stored manifest forever.</para>
///
/// <para><b>Images are out of scope, deliberately.</b> Signature blocks, logos and social icons
/// belong to <see cref="InlineAssetClassifier"/>, whose bar is measured size plus a real cid
/// reference from the body — a bar that exists precisely because Outlook gives a signature logo
/// and a pasted screenshot of a requirements table the same name. A filename rule here would be
/// weaker evidence overriding stronger, so this classifier refuses image parts outright.</para>
///
/// <para><b>Matching is insensitive to case, spacing, punctuation and separators</b> — senders
/// write "Terms &amp; Conditions.pdf", "terms_and_conditions.PDF" and "Terms-and-Conditions
/// (2).pdf" for the same document. Arabic names are folded for the letter forms that vary between
/// keyboards (alef hamza, alef maqsura, taa marbuta, tatweel, diacritics), because a Saudi/GCC
/// sender's "الشروط والأحكام" and "الشروط والاحكام" are the same words. Both the candidate name
/// and every pattern below go through the SAME normalizer, so the two cannot drift apart.</para>
/// </summary>
public static class NonCommercialAttachmentClassifier
{
    /// <summary>
    /// Extensions that can carry a bill of quantities, and are therefore never boilerplate
    /// however they are named. This is the single most valuable line in the file.
    /// </summary>
    private static readonly HashSet<string> NeverIgnorableExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".xls", ".xlsx", ".xlsm", ".xlsb", ".xltx", ".csv", ".ods", ".tsv" };

    /// <summary>The same rule expressed on the declared MIME type, for a sender whose client
    /// sent the file without a recognisable extension.</summary>
    private static readonly string[] NeverIgnorableMimeMarkers =
    [
        "spreadsheet", "excel", "csv", "comma-separated", "tab-separated"
    ];

    /// <summary>
    /// Vocabulary that makes an attachment commercially significant regardless of what else its
    /// name says. Multi-word and long forms only — short ones live in
    /// <see cref="CommercialTokens"/> and are matched as whole words, so "response" does not
    /// match "rfq" and "spot" does not match "po".
    /// </summary>
    private static readonly string[] CommercialPhraseSource =
    [
        "request for quotation", "request for proposal", "request for information",
        "bill of quantities", "bill of quantity", "scope of work", "statement of work",
        "material take off", "material takeoff", "purchase order", "delivery order",
        "price list", "pricelist", "unit rate", "quotation", "quote", "tender", "bidding",
        "schedule", "pricing", "price", "enquiry", "inquiry", "requirement",
        "specification", "datasheet", "data sheet", "drawing", "proforma", "pro forma",
        "invoice", "costing", "estimate", "line item", "item list", "packing list", "boq",
        // Arabic. A GCC buyer's commercial documents are as often Arabic-named as English.
        "عرض سعر",       // quotation / price offer
        "طلب عرض",       // request for quotation
        "طلب شراء",      // purchase requisition
        "أمر شراء",      // purchase order
        "مناقصة",        // tender
        "جدول الكميات",  // bill of quantities
        "الكميات",       // quantities
        "فاتورة",        // invoice
        "قائمة أسعار",   // price list
        "المواصفات"      // specifications
    ];

    /// <summary>Short commercial signals, matched as WHOLE WORDS only.</summary>
    private static readonly HashSet<string> CommercialTokens =
        new(StringComparer.Ordinal)
        { "rfq", "rfp", "rfi", "boq", "bom", "po", "lpo", "sow", "mto", "mr", "itb", "itt", "qty" };

    /// <summary>
    /// THE boilerplate vocabulary. One list, one place, every entry deliberate.
    ///
    /// <para>Each entry names a document type that carries no priced or specified content — the
    /// paperwork that rides along with commercial mail. Entries are matched as substrings of the
    /// normalised name.</para>
    ///
    /// <para><b>What is deliberately NOT here.</b> A bare "certificate" is ambiguous in this
    /// market: a material test certificate is a quality document a buyer genuinely prices
    /// against, and ignoring one would be exactly the failure this module exists to prevent. Only
    /// the corporate registrations — VAT, zakat, commercial registration, ISO, chamber of
    /// commerce, trade licence — are named, because those are unmistakably administrative. The
    /// same reasoning excludes a bare "brochure" and a bare "catalogue": a product catalogue can
    /// BE the specification. Only a COMPANY profile or brochure is listed.</para>
    /// </summary>
    private static readonly string[] BoilerplatePhraseSource =
    [
        // ---- Terms, conditions, contract boilerplate -------------------------------------
        // Both "terms and conditions" and "terms conditions" appear because "&" normalises to a
        // separator: "Terms & Conditions" and "Terms and Conditions" are the same document
        // written two ways, and deleting the word "and" instead would corrupt phrases like
        // "conditions of contract" into forms nobody writes.
        "terms and conditions", "terms conditions", "terms of business", "terms of trade",
        "terms of use", "terms of service", "terms of purchase", "terms of sale",
        "standard terms", "general terms", "general conditions", "standard conditions",
        "conditions of contract", "conditions of purchase", "conditions of sale",
        "contract conditions", "purchasing conditions", "supplier terms",
        "code of conduct",
        "الشروط والأحكام",       // terms and conditions
        "الأحكام والشروط",       // conditions and terms (reversed, equally common)
        "الشروط العامة",         // general conditions
        "شروط التعاقد",          // conditions of contract

        // ---- Confidentiality -------------------------------------------------------------
        "non disclosure agreement", "nondisclosure agreement", "non disclosure",
        "nondisclosure", "confidentiality agreement", "confidentiality undertaking",
        "mutual nda", "signed nda",
        "اتفاقية عدم الإفصاح",   // non-disclosure agreement
        "اتفاقية السرية",        // confidentiality agreement

        // ---- Privacy, disclaimers --------------------------------------------------------
        "privacy notice", "privacy policy", "privacy statement", "data protection notice",
        "email disclaimer", "e mail disclaimer", "legal disclaimer", "legal notice",
        "confidentiality notice", "disclaimer",
        "سياسة الخصوصية",        // privacy policy
        "إخلاء المسؤولية",       // disclaimer

        // ---- Corporate self-description --------------------------------------------------
        "company profile", "corporate profile", "company brochure", "corporate brochure",
        "company introduction", "corporate introduction", "company presentation",
        "corporate presentation", "about us", "who we are", "our company",
        "الملف التعريفي",        // company profile
        "ملف الشركة",            // company file / profile
        "نبذة عن الشركة",        // about the company

        // ---- Administrative registrations and certificates -------------------------------
        // Qualified forms only — see the remarks above on why a bare "certificate" is not here.
        "commercial registration", "cr certificate", "vat certificate", "vat registration",
        "tax certificate", "tax registration", "zakat certificate", "zakat and tax",
        "gosi certificate", "saudization certificate", "nitaqat certificate",
        "chamber of commerce", "trade license", "trade licence", "business license",
        "business licence", "iso certificate", "iso 9001", "iso 14001", "iso 45001",
        "registration certificate",
        "السجل التجاري",         // commercial registration
        "شهادة الزكاة",          // zakat certificate
        "الغرفة التجارية"        // chamber of commerce
    ];

    // Normalised ONCE, through the same function the candidate name goes through. Hand-folding
    // the Arabic entries above would work until someone added one and folded it differently.
    private static readonly string[] CommercialPhrases = Normalized(CommercialPhraseSource);
    private static readonly string[] BoilerplatePhrases = Normalized(BoilerplatePhraseSource);

    /// <summary>
    /// Whether this attachment may go unread without sending its message to a human.
    /// </summary>
    /// <param name="fileName">The attachment's name as the sender wrote it. Null or blank is
    /// never ignorable — an unnamed part is already handled as an unnamed part.</param>
    /// <param name="mimeType">The canonical media type, where one was declared.</param>
    /// <param name="matchedPattern">The pattern that decided it, for the audit log.</param>
    public static bool IsNonCommercialBoilerplate(
        string? fileName, string? mimeType, out string? matchedPattern)
    {
        matchedPattern = null;
        if (string.IsNullOrWhiteSpace(fileName)) return false;

        // EXCLUSION 0 — IMAGES ARE NOT THIS CLASSIFIER'S TO JUDGE.
        //
        // Signature blocks, logos and social icons are exactly what
        // <see cref="InlineAssetClassifier"/> exists for, and its bar is deliberately higher than
        // a name: measured decoded size, an inline disposition, a Content-Id, and an actual cid
        // reference from the body. That bar was raised on purpose, because Outlook names BOTH a
        // signature logo and a pasted screenshot of a requirements table "image001.png" — the
        // filename is precisely the evidence that cannot separate them. Letting a name override
        // that verdict here would quietly weaken the guard: an image the sender explicitly marked
        // as an attachment, or one no cid reference points at, is content by that classifier's
        // reasoning and must stay content whatever it is called.
        if (mimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true) return false;

        // EXCLUSION 1 — a spreadsheet is never boilerplate, whatever it is called.
        var extension = SafeExtension(fileName);
        if (extension.Length > 0 && NeverIgnorableExtensions.Contains(extension)) return false;
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            foreach (var marker in NeverIgnorableMimeMarkers)
                if (mimeType.Contains(marker, StringComparison.OrdinalIgnoreCase)) return false;
        }

        var normalized = Normalize(WithoutExtension(fileName));
        if (normalized.Length == 0) return false;
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // EXCLUSION 2 — commercial vocabulary anywhere in the name wins outright.
        foreach (var phrase in CommercialPhrases)
            if (normalized.Contains(phrase, StringComparison.Ordinal)) return false;
        foreach (var word in words)
            if (CommercialTokens.Contains(word)) return false;

        // POSITIVE EVIDENCE — and only then.
        foreach (var phrase in BoilerplatePhrases)
        {
            if (!normalized.Contains(phrase, StringComparison.Ordinal)) continue;
            matchedPattern = phrase;
            return true;
        }

        // "nda" as a bare word, which is how it is nearly always named, but never as a substring:
        // "agenda.pdf" and "calendar.pdf" contain those letters and are not agreements.
        if (words.Contains("nda", StringComparer.Ordinal))
        {
            matchedPattern = "nda";
            return true;
        }

        return false;
    }

    /// <summary>Convenience overload for callers that do not need the audit detail.</summary>
    public static bool IsNonCommercialBoilerplate(string? fileName, string? mimeType)
        => IsNonCommercialBoilerplate(fileName, mimeType, out _);

    private static string[] Normalized(string[] source)
        => source.Select(Normalize).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Lower case, punctuation and separators folded to single spaces, Arabic letter forms
    /// folded, diacritics dropped.
    /// </summary>
    internal static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousWasSpace = true;
        foreach (var raw in value.Normalize(NormalizationForm.FormC))
        {
            var character = FoldArabic(char.ToLowerInvariant(raw));
            if (character == '\0') continue;                 // dropped: tatweel, diacritics
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
                continue;
            }
            // Everything else — spaces, underscores, hyphens, ampersands, brackets, dots — is one
            // separator, so "Terms&Conditions", "terms_and_conditions" and "Terms - Conditions"
            // all reduce to the same words.
            if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Folds the Arabic letter forms that vary between keyboards and transliterations, and drops
    /// the marks that carry no lexical weight. Returns '\0' for a character to be dropped.
    /// Code points, not literals, so the fold survives any re-encoding of this file.
    /// </summary>
    private static char FoldArabic(char character) => character switch
    {
        'ـ' => '\0',                                              // tatweel (kashida)
        >= 'ً' and <= 'ْ' => '\0',                           // harakat / diacritics
        'آ' or 'أ' or 'إ' or 'ٱ' => 'ا',      // alef forms -> alef
        'ى' => 'ي',                                          // alef maqsura -> yaa
        'ة' => 'ه',                                          // taa marbuta -> haa
        'ؤ' => 'و',                                          // waw + hamza -> waw
        'ئ' => 'ي',                                          // yaa + hamza -> yaa
        'ء' => '\0',                                              // bare hamza, dropped
        _ => character
    };

    /// <summary>
    /// The name without its extension. Hand-rolled rather than
    /// <see cref="Path.GetFileNameWithoutExtension(string)"/> so that a MIME filename carrying
    /// path separators or invalid characters — which a hostile sender controls entirely — can
    /// never make the classifier throw and take a message's whole capture with it.
    /// </summary>
    private static string WithoutExtension(string fileName)
    {
        var extension = SafeExtension(fileName);
        return extension.Length == 0 ? fileName : fileName[..^extension.Length];
    }

    /// <summary>The trailing ".ext", or empty when there is no plausible one.</summary>
    private static string SafeExtension(string fileName)
    {
        var lastDot = fileName.LastIndexOf('.');
        if (lastDot < 0 || lastDot == fileName.Length - 1) return string.Empty;
        var extension = fileName[(lastDot + 1)..];
        if (extension.Length is 0 or > 8) return string.Empty;
        foreach (var character in extension)
            if (!char.IsLetterOrDigit(character)) return string.Empty;
        return "." + extension;
    }
}
