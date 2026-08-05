namespace ERP_RFQ_Automation.Extraction.Conversational;

/// <summary>
/// The extraction instructions for CONVERSATIONAL text — an email body written by a human,
/// with no table, no labels and often no RFQ number.
///
/// WHY THE STRUCTURED PROMPT CANNOT DO THIS JOB
/// (<c>OllamaLlmService.BuildExtractionInstructions</c>):
/// <list type="number">
/// <item>It demands 28 keys per line item — CompanyRef, StorageLocation, MaterialPotext,
/// CustomerAccountPortalId and the rest — that prose never supplies, so the model spends
/// its output budget writing nulls. (It demanded 52 until rfq-extraction-v2 dropped the 24
/// per-value-field confidences; the remaining mismatch is the SHAPE, not the size.)</item>
/// <item>Its direction-of-trade rule keys off <c>Vendor</c>/<c>Bidder</c>/<c>Quote To</c>
/// blocks that DO NOT EXIST in an email — so <c>CustomerCompanyName</c> comes back null
/// exactly when the From domain and the signature block name the buyer outright.</item>
/// <item>Its customer-evidence rule wants a verbatim snippet but never says a signature
/// block qualifies, which is the only place an email states the organisation.</item>
/// <item><c>"Quantity": number</c> is the one NON-NULLABLE item field, which forces an
/// invented quantity for "please quote cable tray 300mm".</item>
/// <item>Its confidence rubric reserves the top bands for "clear labeling", so an honest
/// model returns 0.4–0.6 on prose and trips the 0.60 acceptance gate.</item>
/// <item>Its multi-inquiry splitter fires on a forwarded thread and mints a second lead
/// from the quoted copy of the same request.</item>
/// </list>
///
/// The instructions below emit a STRICT SUBSET of the same JSON keys, so
/// <c>LeadExtractionResult</c>, the normalizer and the persister are untouched — plus two
/// anchor keys (<c>SourceSpan</c>, <c>QuantityToken</c>) that make invention detectable
/// without a row count to conserve.
/// </summary>
public static class ConversationalPrompt
{
    /// <summary>
    /// Governed prompt version. It is BOTH the ledger label for these calls and the switch
    /// the LLM client uses to select these instructions, so the prompt that is recorded is
    /// provably the prompt that was sent.
    /// </summary>
    /// v2 adds the unit-of-measure transcription rule (rule 12): the sender's own wording is
    /// copied verbatim and canonicalisation is left to <c>Services/Uom/UomCanonicalizer.cs</c>.
    public const string PromptVersion = "rfq-conversational-v2";

    public static bool IsConversational(string? promptVersion)
        => string.Equals(promptVersion, PromptVersion, System.StringComparison.OrdinalIgnoreCase);

    public static string BuildConversationalExtractionInstructions() => @"You read ONE email message written by a human and decide, honestly, what physical goods or services it asks us to quote. The message is conversational: there is no table, usually no RFQ number, and often no dates.

**CRITICAL RULES:**
1. Return ONLY valid JSON — no markdown, no explanation, no preamble.
2. DIRECTION OF TRADE IS FIXED: the SENDER of this message is the CUSTOMER. We are the supplier who received it. Never place our own company anywhere in the output.
3. IDENTIFY THE CUSTOMER FROM THE SIGNATURE BLOCK AND THE ADDRESS. The trailing signature block (company name, ""LLC"" / ""FZE"" / ""W.L.L"" / ""Trading"" / ""Contracting"", phone, address) and the ""From:"" line are legitimate, primary evidence of the buying organisation. Copy the organisation verbatim into ""CustomerCompanyName"" and put the signature LINE (or the sentence naming it), 120 characters or fewer, verbatim, into ""CustomerCompanyEvidence"". Put the sender's address into ""CustomerBuyerEmail"" and the person's name into ""BuyersName"". If the message names no organisation anywhere, return null for ""CustomerCompanyName"" — never guess one from the domain spelling.
4. EMIT AN ITEM ONLY FOR AN EXPLICIT REQUEST FOR A PHYSICAL GOOD OR SERVICE. One item per distinct requested thing.
5. NEVER create items from: greetings, closings, courtesy sentences, signature blocks, confidentiality disclaimers, project or site names, job titles, addresses, attachment filenames, or anything the sender merely mentions in passing.
6. ""Quantity"" IS NULLABLE. If the message does not state a quantity for an item, return null. NEVER return 1, and never invent a number. Copy the exact quantity text you read (e.g. ""40 nos"", ""12 pcs"") into ""QuantityToken"", or null when there is none.
7. EVERY ITEM MUST CARRY ""SourceSpan"": a VERBATIM quote of 120 characters or fewer, copied character-for-character out of the message, containing the request for that item. If you cannot quote the message for an item, do not emit that item. Spans must appear in the same order as the items and must not overlap.
8. IF THE MESSAGE ONLY REFERS TO AN ATTACHMENT (""please find attached"", ""see the enclosed BOQ"") and states no goods itself, return an EMPTY ""Items"" array and say so in one sentence in ""HeaderRemarks"". That is a correct, complete answer — the attachment is processed separately.
9. IGNORE ANYTHING AFTER THE FRESH-TEXT BOUNDARY. If quoted or forwarded material still appears (lines beginning ""&gt;"", ""-----Original Message-----"", ""On ... wrote:""), it is an EARLIER message: never extract items from it and never treat it as a second inquiry.
10. Dates must be ""YYYY-MM-DD"" or null. A relative deadline (""by the 20th"", ""next week"") that you cannot resolve to an exact date is null — put the sender's wording in ""HeaderRemarks"" instead.
11. Confidence is your honest reading of THIS message. Conversational prose without labels is normal here, not a defect: score what the sentence actually supports and do not deflate a clear request just because it is informal.
12. ""UnitOfMeasure"" IS THE SENDER'S OWN WORD for how the item is counted, TRANSCRIBED VERBATIM — ""nos"", ""pcs"", ""each"", ""sets"", ""m"", ""kg"" exactly as written. Do not translate, expand, abbreviate or standardise it; the platform maps spellings onto its own vocabulary (EA, SET, PR, DZ, LOT, M, MM, CM, M2, M3, FT, KG, MT, L, HR, DAY) afterwards, and rewriting the sender's wording destroys the evidence a reviewer checks against. If the sender states no unit, return null — NEVER default to ""EA"" or ""each"". If the sender counts PACKAGES or FORMS (""2 pallets"", ""5 boxes"", ""3 lengths"", ""a couple of drums""), copy that wording verbatim and NEVER convert it to a piece count: the message does not say how many are in one.

**REQUIRED JSON SCHEMA (return exactly these keys):**
{
  ""Rfqno"": string | null,
  ""BidClosingDate"": ""YYYY-MM-DD"" | null,
  ""CustomerCompanyName"": string | null,
  ""CustomerCompanyEvidence"": string | null,
  ""CustomerBuyerEmail"": string | null,
  ""BuyersName"": string | null,
  ""InquiryType"": ""product"" | ""service"" | ""mixed"" | null,
  ""HeaderRemarks"": string | null,
  ""OverallConfidence"": number,
  ""Items"": [
    {
      ""ProductShortDescription"": string,
      ""Quantity"": number | null,
      ""UnitOfMeasure"": string | null,
      ""ManufacturerPartNumber"": string | null,
      ""ItemText"": string | null,
      ""ItemConfidence"": number,
      ""SourceSpan"": ""verbatim quote from the message, <= 120 characters"",
      ""QuantityToken"": string | null
    }
  ]
}

Return ONLY the JSON object, nothing else.";
}
