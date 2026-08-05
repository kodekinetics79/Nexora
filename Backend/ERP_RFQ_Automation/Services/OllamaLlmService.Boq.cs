using ERP_RFQ_Automation.Services.Interfaces;
using System.Text.Json;
using System.Diagnostics;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Services
{
    // WP-BOQ partial: service-scope → BOQ drafting. Additive — mirrors the shape of
    // ExtractLeadDataAsync in OllamaLlmService.cs (retry loop, strict-then-extracted
    // JSON parsing, validation, never patching model output) with its own prompt and
    // schema. The extraction path in the main file is untouched.
    public partial class OllamaLlmService
    {
        public async Task<BoqDraftResult?> DraftServiceBoqAsync(
            string scopeText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(scopeText))
            {
                _log.LogWarning("Empty text provided for BOQ drafting");
                return null;
            }

            // Reuse the extraction preprocessor (whitespace normalization + intelligent
            // truncation) — service scopes are prose-heavy, the same limits apply.
            var processedText = PrepareProviderInput(scopeText);
            var instructions = BuildBoqInstructions();
            var maximumRequestBytes = MeasureRequestBytes(instructions, processedText);
            var governedContext = context with { ProviderClass = _providerClass };
            var reservation = await _governance.ReserveAsync(
                governedContext, "Ollama", _model, processedText, maximumRequestBytes,
                _maximumOutputTokens, MAX_RETRIES, cancellationToken);
            long totalInputTokens = 0;
            long totalOutputTokens = 0;
            var aggregateSource = AiTokenSources.ProviderExact;

            _log.LogInformation("Sending BOQ draft request to Ollama Cloud. Text length: {Length} chars",
                processedText.Length);

            string? lastErrorCode = null;

            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                var started = DateTime.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                var providerCallCompleted = false;
                try
                {
                    var call = await SendBoqDraftRequestAsync(instructions, processedText, cancellationToken);
                    providerCallCompleted = true;
                    lastErrorCode = call.ErrorCode;
                    stopwatch.Stop();
                    var usage = Usage(call, maximumRequestBytes);
                    totalInputTokens += usage.InputTokens;
                    totalOutputTokens += usage.OutputTokens;
                    if (usage.TokenSource != AiTokenSources.ProviderExact)
                        aggregateSource = usage.TokenSource;
                    await _governance.RecordAttemptAsync(reservation, new AiAttemptCompletion(
                        attempt, call.Result is not null ? AiCallStatuses.Succeeded : AiCallStatuses.Failed,
                        call.HttpStatus, call.ProviderRequestId, usage.InputTokens, usage.OutputTokens,
                        usage.TokenSource, stopwatch.ElapsedMilliseconds, call.ProviderDurationNanoseconds,
                        string.IsNullOrEmpty(call.RawContent) ? null : AiGovernanceService.Hash(call.RawContent),
                        call.ErrorCode, started, DateTime.UtcNow), CancellationToken.None);
                    if (call.Result != null)
                    {
                        await _governance.CompleteAsync(reservation, AiCallStatuses.Succeeded,
                            totalInputTokens, totalOutputTokens, aggregateSource, call.RawContent, null, CancellationToken.None);
                        _log.LogInformation(
                            "Successfully drafted BOQ. Sections: {Sections}, overall confidence: {Confidence:P0}",
                            call.Result.Sections?.Count ?? 0, call.Result.OverallConfidence);
                        return call.Result;
                    }

                    // Truncation: re-sending the identical scope re-truncates identically.
                    // There is no smaller request to fall back to on this path (see the note
                    // in SendBoqDraftRequestAsync), so stop and let BoqBuilderService fall
                    // back to its honest TBD-heavy skeleton instead of burning the budget.
                    if (call.ErrorCode == AiErrorCodes.OutputTruncated)
                        break;

                    if (!IsTransient(call.HttpStatus))
                        break;
                    if (attempt < MAX_RETRIES)
                    {
                        _log.LogWarning("BOQ draft attempt {Attempt} failed, retrying...", attempt);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                    }
                }
                catch (HttpRequestException ex)
                {
                    stopwatch.Stop();
                    var estimatedInput = AiGovernanceService.ConservativeTokenUpperBound(maximumRequestBytes);
                    totalInputTokens += estimatedInput;
                    totalOutputTokens += _maximumOutputTokens;
                    aggregateSource = AiTokenSources.Estimated;
                    await RecordExceptionAttemptAsync(reservation, attempt, AiCallStatuses.Unknown,
                        "transport_unknown", estimatedInput, _maximumOutputTokens,
                        stopwatch.ElapsedMilliseconds, started, CancellationToken.None);
                    _log.LogError(ex, "HTTP error on BOQ draft attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt < MAX_RETRIES)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    var estimatedInput = AiGovernanceService.ConservativeTokenUpperBound(maximumRequestBytes);
                    totalInputTokens += estimatedInput;
                    totalOutputTokens += _maximumOutputTokens;
                    aggregateSource = AiTokenSources.Estimated;
                    await RecordExceptionAttemptAsync(reservation, attempt, AiCallStatuses.Unknown,
                        "provider_timeout", estimatedInput, _maximumOutputTokens,
                        stopwatch.ElapsedMilliseconds, started, CancellationToken.None);
                    _log.LogError(ex, "BOQ draft timeout on attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt < MAX_RETRIES)
                        await Task.Delay(TimeSpan.FromSeconds(5 * attempt), cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    stopwatch.Stop();
                    if (providerCallCompleted)
                    {
                        await _governance.CompleteAsync(reservation, AiCallStatuses.Failed,
                            totalInputTokens, totalOutputTokens, aggregateSource, null,
                            "cancelled_during_backoff", CancellationToken.None);
                        throw;
                    }
                    var estimatedInput = AiGovernanceService.ConservativeTokenUpperBound(maximumRequestBytes);
                    await RecordExceptionAttemptAsync(reservation, attempt, AiCallStatuses.Unknown,
                        "caller_cancelled", estimatedInput, _maximumOutputTokens,
                        stopwatch.ElapsedMilliseconds, started, CancellationToken.None);
                    await _governance.CompleteAsync(reservation, AiCallStatuses.Unknown,
                        totalInputTokens + estimatedInput, totalOutputTokens + _maximumOutputTokens, AiTokenSources.Estimated,
                        null, "caller_cancelled", CancellationToken.None);
                    throw;
                }
                catch (Exception ex)
                {
                    if (providerCallCompleted)
                        throw;
                    stopwatch.Stop();
                    await RecordExceptionAttemptAsync(reservation, attempt, AiCallStatuses.Failed,
                        "unexpected_error", 0, 0, stopwatch.ElapsedMilliseconds, started, CancellationToken.None);
                    _log.LogError(ex, "Unexpected error during BOQ draft attempt {Attempt}", attempt);
                    break;
                }
            }
            var settledErrorCode = lastErrorCode ?? AiErrorCodes.AttemptsExhausted;
            await _governance.CompleteAsync(reservation, AiCallStatuses.Failed,
                totalInputTokens, totalOutputTokens, aggregateSource, null, settledErrorCode, CancellationToken.None);
            _log.LogWarning("All BOQ draft attempts failed. Reason={ErrorCode}", settledErrorCode);
            return null;
        }

        private async Task<ProviderCallResult<BoqDraftResult>> SendBoqDraftRequestAsync(
            string trustedInstructions, string untrustedDocument, CancellationToken ct)
        {
            var payload = new OllamaRequest(
                Model: _model,
                Messages: BuildGovernedMessages(trustedInstructions, untrustedDocument),
                Stream: false,
                Format: "json",
                // BoQ drafting is structured extraction — same rationale as the extraction
                // path above: disable hidden thinking so reasoning models spend the output
                // budget on JSON content. Non-reasoning models ignore the field.
                Think: false,
                Options: new OllamaOptions(Temperature: TEMPERATURE, NumPredict: _maximumOutputTokens)
            );

            using var response = await _http.PostAsJsonAsync("api/chat", payload, _jsonOptions, ct);
            var providerRequestId = ReadProviderRequestId(response);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Ollama API returned {StatusCode} for BOQ draft.", response.StatusCode);
                return new(null, null, (int)response.StatusCode, providerRequestId, null, null, null, "provider_http_error");
            }

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(_jsonOptions, ct);
            var rawContent = ollamaResponse?.Message?.Content?.Trim();
            // Same request shape as extraction, therefore the same failure mode. BOQ output
            // is NOT bounded by the caller: a 30,000-character scope-of-work can legitimately
            // draft an unbounded number of BOQ lines, and there is no chunking on this path
            // (BoqBuilderService issues exactly one call per document). It is therefore
            // MORE important, not less, that truncation is named honestly here — a
            // half-written BOQ that happened to parse would be a silently short bill of
            // quantities, and one that did not parse used to be blamed on the model.
            var truncated = IsOutputTruncated(ollamaResponse?.DoneReason);

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                if (truncated)
                {
                    LogOutputTruncated("BOQ draft", ollamaResponse?.EvalCount, null, 0);
                    return new(null, null, (int)response.StatusCode, providerRequestId,
                        ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                        ollamaResponse?.TotalDuration, AiErrorCodes.OutputTruncated);
                }
                _log.LogWarning("Received empty BOQ draft response from Ollama");
                return new(null, null, (int)response.StatusCode, providerRequestId,
                    ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                    ollamaResponse?.TotalDuration, AiErrorCodes.EmptyResponse);
            }

            var parsed = ParseBoqJsonResponse(rawContent);
            if (truncated)
            {
                // Note the asymmetry with extraction: a truncated BOQ response can still
                // PARSE (the model may close the JSON early), and that parse is a bill of
                // quantities missing its tail. Never return it — an incomplete BOQ presented
                // as complete is exactly the fabrication this path refuses everywhere else.
                LogOutputTruncated("BOQ draft", ollamaResponse?.EvalCount, null, rawContent.Length);
                return new(null, rawContent, (int)response.StatusCode, providerRequestId,
                    ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                    ollamaResponse?.TotalDuration, AiErrorCodes.OutputTruncated);
            }
            return new(parsed, rawContent, (int)response.StatusCode, providerRequestId,
                ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                ollamaResponse?.TotalDuration, parsed is null ? AiErrorCodes.InvalidOutput : null);
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

        private static string BuildBoqInstructions()
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
