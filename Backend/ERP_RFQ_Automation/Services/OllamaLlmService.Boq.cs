using ERP_RFQ_Automation.Services.Interfaces;
using System.Text.Json;

namespace ERP_RFQ_Automation.Services
{
    // WP-BOQ partial: service-scope → BOQ drafting. Additive — mirrors the shape of
    // ExtractLeadDataAsync in OllamaLlmService.cs (retry loop, strict-then-extracted
    // JSON parsing, validation, never patching model output) with its own prompt and
    // schema. The extraction path in the main file is untouched.
    public partial class OllamaLlmService
    {
        public async Task<BoqDraftResult?> DraftServiceBoqAsync(string scopeText)
        {
            if (string.IsNullOrWhiteSpace(scopeText))
            {
                _log.LogWarning("Empty text provided for BOQ drafting");
                return null;
            }

            // Reuse the extraction preprocessor (whitespace normalization + intelligent
            // truncation) — service scopes are prose-heavy, the same limits apply.
            var processedText = PreprocessText(scopeText);
            var prompt = BuildBoqPrompt(processedText);

            _log.LogInformation("Sending BOQ draft request to Ollama Cloud. Text length: {Length} chars",
                processedText.Length);

            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                try
                {
                    var result = await SendBoqDraftRequestAsync(prompt);
                    if (result != null)
                    {
                        _log.LogInformation(
                            "Successfully drafted BOQ. Sections: {Sections}, overall confidence: {Confidence:P0}",
                            result.Sections?.Count ?? 0, result.OverallConfidence);
                        return result;
                    }
                    if (attempt < MAX_RETRIES)
                    {
                        _log.LogWarning("BOQ draft attempt {Attempt} failed, retrying...", attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                    }
                }
                catch (HttpRequestException ex)
                {
                    _log.LogError(ex, "HTTP error on BOQ draft attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt == MAX_RETRIES) return null;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)));
                }
                catch (TaskCanceledException ex)
                {
                    _log.LogError(ex, "BOQ draft timeout on attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt == MAX_RETRIES) return null;
                    await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
                }
                catch (Exception ex)
                {
                    _log.LogError(ex, "Unexpected error during BOQ draft attempt {Attempt}", attempt);
                    return null;
                }
            }
            _log.LogWarning("All BOQ draft attempts failed");
            return null;
        }

        private async Task<BoqDraftResult?> SendBoqDraftRequestAsync(string prompt)
        {
            var payload = new OllamaRequest(
                Model: _model,
                Messages: new[] { new OllamaMessage("user", prompt) },
                Stream: false,
                Format: "json",
                Options: new OllamaOptions(Temperature: TEMPERATURE)
            );

            var response = await _http.PostAsJsonAsync("api/chat", payload, _jsonOptions);

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                _log.LogWarning("Ollama API returned {StatusCode} for BOQ draft. Error: {Error}",
                    response.StatusCode, errorContent);
                return null;
            }

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(_jsonOptions);
            var rawContent = ollamaResponse?.Message?.Content?.Trim();

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                _log.LogWarning("Received empty BOQ draft response from Ollama");
                return null;
            }

            return ParseBoqJsonResponse(rawContent);
        }

        private BoqDraftResult? ParseBoqJsonResponse(string rawContent)
        {
            // Same trust policy as extraction (ING-03): strict parse first; a single
            // non-destructive outermost-object trim second; never heuristic rewriting.
            var strict = TryStrictBoqParse(rawContent);
            if (strict.parsed)
                return strict.result;

            var extracted = ExtractJsonObject(rawContent);
            if (!string.IsNullOrWhiteSpace(extracted) && !string.Equals(extracted, rawContent, StringComparison.Ordinal))
            {
                var second = TryStrictBoqParse(extracted);
                if (second.parsed)
                    return second.result;
            }

            _log.LogWarning(
                "Ollama BOQ output was not valid, trustworthy JSON after strict parsing. Content length: {RawLength}",
                rawContent.Length);
            return null;
        }

        private (bool parsed, BoqDraftResult? result) TryStrictBoqParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (false, null);

            try
            {
                var result = JsonSerializer.Deserialize<BoqDraftResult>(json, _jsonOptions);
                if (result == null)
                {
                    _log.LogWarning("BOQ draft deserialization resulted in null");
                    return (false, null);
                }

                if (!ValidateBoqDraft(result))
                {
                    // Valid JSON but untrustworthy content -> reject definitively.
                    _log.LogWarning("BOQ draft result failed validation");
                    return (true, null);
                }

                return (true, result);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Strict BOQ JSON parse failed. Content length: {Len}", json.Length);
                return (false, null);
            }
        }

        private bool ValidateBoqDraft(BoqDraftResult result)
        {
            if (result.OverallConfidence is < 0 or > 1)
            {
                _log.LogWarning("Invalid BOQ overall confidence: {Confidence}", result.OverallConfidence);
                return false;
            }

            if (result.Sections is null || result.Sections.Count == 0)
            {
                _log.LogWarning("BOQ draft has no sections");
                return false;
            }

            foreach (var section in result.Sections)
            {
                foreach (var item in section.Items ?? new List<BoqDraftItem>())
                {
                    if (string.IsNullOrWhiteSpace(item.Description))
                    {
                        _log.LogWarning("BOQ draft contains an item without a description");
                        return false;
                    }
                    // A negative quantity is never trustworthy. Zero/null are fine —
                    // they mean "not stated" and become TBD lines downstream.
                    if (item.Quantity is < 0)
                    {
                        _log.LogWarning("BOQ draft contains a negative quantity");
                        return false;
                    }
                    if (item.Confidence is < 0 or > 1)
                    {
                        _log.LogWarning("BOQ draft contains an out-of-range item confidence");
                        return false;
                    }
                }
            }

            return true;
        }

        private static string BuildBoqPrompt(string text)
        {
            return $@"You are an expert estimation engineer building a BILL OF QUANTITIES (BOQ) from a service request / scope of work. Service types include maintenance scopes, installation & commissioning, testing, supply-and-install, and manpower/equipment hire.

**CRITICAL RULES:**
1. Return ONLY valid JSON - no markdown, no explanations, no preamble.
2. NEVER INVENT QUANTITIES. If the document does not state a quantity for a line, set ""Quantity"": null, ""Tbd"": true and explain what is missing in ""TbdReason"" (e.g. ""Cable sizes not stated — quantity TBD""). It is FAR better to mark a line TBD than to guess.
3. NEVER invent prices or rates. This schema has no price fields on purpose.
4. Group items into logical sections (e.g. ""Supply"", ""Installation"", ""Testing & Commissioning"", ""Manpower""). Use the document's own structure when it has one.
5. Units: use standard abbreviations - EA, m, m2, m3, lot, hr, day, set, kg, km. If the unit is unclear, use ""lot"" and mark the line Tbd with a reason.
6. ItemType must be one of: ""Material"", ""Labor"", ""Equipment"", ""Subcontract"".
7. All confidence scores are 0.0-1.0 and reflect how explicitly the text supports the line.
8. ""Assumptions"": list every assumption you had to make (site access, working hours, exclusions, standards assumed). Empty array if none.
9. ""ServiceCategory"": one of ""electrical"", ""mechanical"", ""civil"", ""maintenance"", ""manpower"", ""mixed"", ""other"".
10. Do not duplicate the same physical work in two lines. Do not include headings as items.

**INPUT SCOPE TEXT:**
{text}

**REQUIRED JSON SCHEMA:**
{{
  ""ServiceCategory"": ""electrical"" | ""mechanical"" | ""civil"" | ""maintenance"" | ""manpower"" | ""mixed"" | ""other"",
  ""OverallConfidence"": number,
  ""Sections"": [
    {{
      ""Title"": string,
      ""Items"": [
        {{
          ""Description"": string,
          ""Unit"": string,
          ""Quantity"": number | null,
          ""ItemType"": ""Material"" | ""Labor"" | ""Equipment"" | ""Subcontract"",
          ""Confidence"": number,
          ""Tbd"": boolean,
          ""TbdReason"": string | null,
          ""ItemCode"": string | null
        }}
      ]
    }}
  ],
  ""Assumptions"": [ string ]
}}

**IMPORTANT:** ""Quantity"": null with ""Tbd"": true is the REQUIRED output for any under-specified line. Calculate OverallConfidence as the average of item confidences, reduced when many lines are TBD. Return ONLY the JSON object, nothing else.";
        }
    }
}
