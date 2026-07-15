using ERP_RFQ_Automation.Services.Interfaces;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ERP_RFQ_Automation.Services
{
    public class OllamaLlmService : ILLMService
    {
        private readonly HttpClient _http;
        private readonly ILogger<OllamaLlmService> _log;
        private readonly string _model;
        private readonly JsonSerializerOptions _jsonOptions;

        // Configuration constants
        private const int MAX_PROMPT_CHARS = 30000; // Increased for larger context to improve accuracy and confidence
        private const double TEMPERATURE = 0.0; // Lowered to 0 for more deterministic outputs, potentially increasing consistency and confidence
        private const int TIMEOUT_SECONDS = 180; // Increased timeout for larger requests
        private const int MAX_RETRIES = 3; // Increased retries for better reliability

        public OllamaLlmService(HttpClient http, ILogger<OllamaLlmService> log, IConfiguration cfg)
        {
            _http = http;
            _log = log;

            // Load configuration
            _model = cfg["Ollama:Model"] ?? "deepseek-v3.1:671b-cloud";
            var apiKey = cfg["Ollama:ApiKey"]
                ?? throw new InvalidOperationException("Ollama API key is missing in configuration!");
            var baseUrl = cfg["Ollama:BaseUrl"] ?? "https://ollama.com/";

            // Configure HTTP client
            _http.BaseAddress = new Uri(baseUrl);
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            _http.Timeout = TimeSpan.FromSeconds(TIMEOUT_SECONDS);

            // Configure JSON serialization options
            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                AllowTrailingCommas = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = false,
                NumberHandling = JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString
            };
        }

        public async Task<LeadExtractionResult?> ExtractLeadDataAsync(string fullText)
        {
            if (string.IsNullOrWhiteSpace(fullText))
            {
                _log.LogWarning("Empty text provided for extraction");
                return null;
            }

            // Intelligent text truncation
            var processedText = PreprocessText(fullText);
            var prompt = BuildOptimizedPrompt(processedText);

            _log.LogInformation("Sending extraction request to Ollama Cloud. Text length: {Length} chars",
                processedText.Length);

            // Retry logic for transient failures
            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    var result = await SendExtractionRequestAsync(prompt);
                    if (result != null)
                    {
                        _log.LogInformation(
                            "Successfully extracted lead data. Overall confidence: {Confidence:P0}",
                            result.OverallConfidence);
                        return result;
                    }
                    if (attempt < MAX_RETRIES)
                    {
                        _log.LogWarning("Attempt {Attempt} failed, retrying...", attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); // Improved exponential backoff
                    }
                }
                catch (HttpRequestException ex)
                {
                    _log.LogError(ex, "HTTP error on attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt == MAX_RETRIES) return null;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                }
                catch (TaskCanceledException ex)
                {
                    _log.LogError(ex, "Request timeout on attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt == MAX_RETRIES) return null;
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Unexpected error during extraction attempt {Attempt}", attempt);
                    return null;
                }
            }
            _log.LogWarning("All extraction attempts failed");
            return null;
        }

        private async Task<LeadExtractionResult?> SendExtractionRequestAsync(string prompt)
        {
            var payload = new OllamaRequest(
                Model: _model,
                Messages: new[] { new OllamaMessage("user", prompt) },
                Stream: false,
                Format: "json", // Added to enforce strict JSON output for better parsing reliability
                Options: new OllamaOptions(Temperature: TEMPERATURE)
            );

            var response = await _http.PostAsJsonAsync("api/chat", payload, _jsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _log.LogWarning(
                    "Ollama API returned {StatusCode}. Error: {Error}",
                    response.StatusCode,
                    errorContent);
                return null;
            }

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(_jsonOptions);
            var rawContent = ollamaResponse?.Message?.Content?.Trim();

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                _log.LogWarning("Received empty response from Ollama");
                return null;
            }

            return ParseJsonResponse(rawContent);
        }

        private string PreprocessText(string fullText)
        {
            // Remove excessive whitespace and normalize
            var text = System.Text.RegularExpressions.Regex.Replace(fullText, @"\s+", " ").Trim();

            // Intelligent truncation - prioritize important sections
            if (text.Length <= MAX_PROMPT_CHARS)
                return text;

            // Split into paragraphs instead of lines for better context preservation
            var paragraphs = text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries);
            var sections = new StringBuilder();
            int charCount = 0;

            // Priority 1: Paragraphs with RFQ keywords
            var priorityParagraphs = paragraphs.Where(p =>
                ContainsRFQKeywords(p.ToLowerInvariant())).ToList();

            foreach (var para in priorityParagraphs)
            {
                if (charCount + para.Length > MAX_PROMPT_CHARS * 0.7) break;
                sections.Append(para).Append("\n\n");
                charCount += para.Length;
            }

            // Priority 2: Other paragraphs up to limit
            foreach (var para in paragraphs)
            {
                if (priorityParagraphs.Contains(para)) continue;
                if (charCount + para.Length > MAX_PROMPT_CHARS) break;
                sections.Append(para).Append("\n\n");
                charCount += para.Length;
            }

            var result = sections.ToString().Trim();
            if (result.Length < text.Length)
            {
                _log.LogInformation("Text truncated from {Original} to {Truncated} chars",
                    text.Length, result.Length);
            }
            return result;
        }

        private bool ContainsRFQKeywords(string text)
        {
            var keywords = new[]
            {
                "rfq", "quotation", "bid", "tender", "item", "qty", "quantity",
                "price", "delivery", "date", "buyer", "supplier", "material",
                "part number", "description", "unit", "total"
            };
            return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private LeadExtractionResult? ParseJsonResponse(string rawContent)
        {
            // --- LOG RAW RESPONSE ---
            _log.LogInformation("--- RAW OLLAMA RESPONSE START ---\n{RawContent}\n--- RAW OLLAMA RESPONSE END ---", rawContent);
            Console.WriteLine($"\n[Ollama Response]:\n{rawContent}\n");

            // ING-03: PREFER STRICT JSON PARSING. We never rewrite/patch the model's JSON with
            // heuristics (which can silently corrupt extracted values). If the output is not valid
            // JSON, we FAIL the extraction and return null so the lead is routed to a needs-review
            // state upstream — never fabricating or guessing values into a "trusted" lead.

            // Attempt 1: parse exactly as returned. With format:"json" this should already be clean.
            var strict = TryStrictParse(rawContent);
            if (strict.parsed)
                return strict.result;

            // Attempt 2: the model wrapped JSON in prose / markdown fences. Non-destructively extract
            // the outermost { ... } block. This only trims surrounding non-JSON text; it does NOT
            // alter any value inside the object.
            var extracted = ExtractJsonObject(rawContent);
            if (!string.IsNullOrWhiteSpace(extracted) && !string.Equals(extracted, rawContent, StringComparison.Ordinal))
            {
                var second = TryStrictParse(extracted);
                if (second.parsed)
                    return second.result;
            }

            _log.LogWarning(
                "Ollama output was not valid, trustworthy JSON after strict parsing. Failing extraction (routes lead to review). Content length: {RawLength}",
                rawContent.Length);
            return null;
        }

        /// <summary>
        /// Strictly deserializes the given JSON. Returns (parsed:true) when the content is definitively
        /// resolved — either a valid, validated result, or a valid-JSON-but-rejected result (null) that
        /// must NOT be re-parsed. Returns (parsed:false) only when the content is not valid JSON.
        /// </summary>
        private (bool parsed, LeadExtractionResult? result) TryStrictParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (false, null);

            try
            {
                var result = JsonSerializer.Deserialize<LeadExtractionResult>(json, _jsonOptions);
                if (result == null)
                {
                    _log.LogWarning("Deserialization resulted in null");
                    return (false, null);
                }

                if (!ValidateExtractionResult(result))
                {
                    // Parsed cleanly but failed sanity checks (e.g. confidence out of range).
                    // The values themselves are untrusted -> reject definitively, do not re-parse.
                    _log.LogWarning("Extraction result failed validation");
                    return (true, null);
                }

                return (true, result);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Strict JSON parse failed. Content length: {Len}", json.Length);
                return (false, null);
            }
        }

        /// <summary>
        /// Non-destructively isolates the outermost JSON object from surrounding prose / markdown
        /// fences. It only trims text outside the braces; it never edits characters within the object.
        /// </summary>
        private static string ExtractJsonObject(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return content;
            var trimmed = content.Trim();
            int start = trimmed.IndexOf('{');
            int end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
                return trimmed.Substring(start, end - start + 1);
            return trimmed;
        }

        private bool ValidateExtractionResult(LeadExtractionResult result)
        {
            // Basic validation checks
            if (result.OverallConfidence < 0 || result.OverallConfidence > 1)
            {
                _log.LogWarning("Invalid overall confidence: {Confidence}", result.OverallConfidence);
                return false;
            }

            // Check if items have valid quantities
            if (result.Items != null)
            {
                var invalidItems = result.Items.Where(i => i.Quantity <= 0).ToList(); // Updated to <= 0 to catch zeros
                if (invalidItems.Any())
                {
                    _log.LogWarning("Found {Count} items with non-positive quantities", invalidItems.Count);
                    return false;
                }
            }

            return true;
        }

        private static string BuildOptimizedPrompt(string text)
        {
            return $@"You are an expert RFQ (Request for Quotation) data extraction system. Your task is to analyze the provided text and extract structured information with high accuracy.

**CRITICAL RULES:**
1. Return ONLY valid JSON - no markdown, no explanations, no preamble
2. All confidence scores must be between 0.0 and 1.0
3. Use null for missing values, never use empty strings
4. Dates must be in YYYY-MM-DD format or null
5. Quantities must be positive integers
6. Assign confidence based on evidence in the text - aim for accuracy over conservatism where evidence is strong

**CONFIDENCE GUIDELINES (OPTIMIZED FOR HIGHER PRECISION):**
- 0.95-1.0: Explicitly stated in text with exact match and clear labeling
- 0.85-0.95: Directly stated with minimal inference needed
- 0.70-0.85: Strong contextual evidence supporting the extraction
- 0.50-0.70: Moderate inference from related information
- 0.0-0.50: Weak or uncertain matches - use sparingly

**INPUT TEXT:**
{text}

**REQUIRED JSON SCHEMA:**
{{
  ""Rfqno"": string | null,
  ""RfqnoConfidence"": number,
  ""BuyersName"": string | null,
  ""BuyersNameConfidence"": number,
  ""RecDate"": ""YYYY-MM-DD"" | null,
  ""RecDateConfidence"": number,
  ""BidClosingDate"": ""YYYY-MM-DD"" | null,
  ""BidClosingDateConfidence"": number,
  ""BiddingDecision"": string | null,
  ""BiddingDecisionConfidence"": number,
  ""AcknowledgmentDate"": ""YYYY-MM-DD"" | null,
  ""AcknowledgmentDateConfidence"": number,
  ""SubDate"": ""YYYY-MM-DD"" | null,
  ""SubDateConfidence"": number,
  ""HeaderRemarks"": ""A very brief (1-2 sentences) summary of any special instructions, delivery requirements, or unique terms found in the RFQ. IGNORE and EXCLUDE UI placeholders, field labels, or generic website text like 'Enter Email', 'Upload File', 'Contact Us'. If no special instructions exist, use null."",
  ""HeaderRemarksConfidence"": number,
  ""OpportunityNo"": string | null,
  ""OpportunityNoConfidence"": number,
  ""Rfqtype"": ""Agreement"" | ""Direct"" | null,
  ""RfqtypeConfidence"": number,
  ""DurationAgreement"": string | null,
  ""DurationAgreementConfidence"": number,
  ""OverallConfidence"": number,
  ""Items"": [
    {{
      ""CompanyRef"": string | null,
      ""CompanyRefConfidence"": number,
      ""CustomerAccountPortalId"": string | null,
      ""CustomerAccountPortalIdConfidence"": number,
      ""CustomerRfqno"": string | null,
      ""CustomerRfqnoConfidence"": number,
      ""ItemMaterialCode"": string | null,
      ""ItemMaterialCodeConfidence"": number,
      ""CommodityProduct"": string | null,
      ""CommodityProductConfidence"": number,
      ""BuyerName"": string | null,
      ""BuyerNameConfidence"": number,
      ""LineItemNo"": string | null,
      ""LineItemNoConfidence"": number,
      ""ProductShortName"": string | null,
      ""ProductShortNameConfidence"": number,
      ""Alternative"": string | null,
      ""AlternativeConfidence"": number,
      ""ProductShortDescription"": string | null,
      ""ProductShortDescriptionConfidence"": number,
      ""Currency"": string | null,
      ""CurrencyConfidence"": number,
      ""UnitOfMeasure"": string | null,
      ""UnitOfMeasureConfidence"": number,
      ""UnitPrice"": number | null,
      ""UnitPriceConfidence"": number,
      ""Quantity"": number,
      ""QuantityConfidence"": number,
      ""StorageLocation"": string | null,
      ""StorageLocationConfidence"": number,
      ""ManufacturerName"": string | null,
      ""ManufacturerNameConfidence"": number,
      ""ManufacturerPartNumber"": string | null,
      ""ManufacturerPartNumberConfidence"": number,
      ""AlternateProductName"": string | null,
      ""AlternateProductNameConfidence"": number,
      ""AlternatePartNumber"": string | null,
      ""AlternatePartNumberConfidence"": number,
      ""ItemText"": string | null,
      ""ItemTextConfidence"": number,
      ""MaterialPotext"": string | null,
      ""MaterialPotextConfidence"": number,
      ""LeadTime"": string | null,
      ""LeadTimeConfidence"": number,
      ""ReceivedDate"": ""YYYY-MM-DD"" | null,
      ""ReceivedDateConfidence"": number,
      ""BidClosingDateLine"": ""YYYY-MM-DD"" | null,
      ""BidClosingDateLineConfidence"": number,
      ""ItemConfidence"": number
    }}
  ]
}}

**IMPORTANT:** Calculate OverallConfidence as the weighted average of all header field confidences (weight 0.4) and average ItemConfidence values (weight 0.6) to emphasize item accuracy. Return ONLY the JSON object, nothing else.";
        }

        // DTOs for Ollama API
        private record OllamaRequest(
            [property: JsonPropertyName("model")] string Model,
            [property: JsonPropertyName("messages")] OllamaMessage[] Messages,
            [property: JsonPropertyName("stream")] bool Stream,
            [property: JsonPropertyName("format")] string Format,
            [property: JsonPropertyName("options")] OllamaOptions Options
        );

        private record OllamaOptions(
            [property: JsonPropertyName("temperature")] double Temperature
        );

        private record OllamaResponse(
            [property: JsonPropertyName("message")] OllamaMessage Message
        );

        public record OllamaMessage(
            [property: JsonPropertyName("role")] string Role,
            [property: JsonPropertyName("content")] string Content
        );
    }
}