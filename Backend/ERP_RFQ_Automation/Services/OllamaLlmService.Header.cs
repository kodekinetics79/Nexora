using ERP_RFQ_Automation.Services.Interfaces;
using System.Text.Json;
using System.Diagnostics;
using ERP_RFQ_Automation.AI;

namespace ERP_RFQ_Automation.Services
{
    // Header completion partial: the labelled lines and prose around a structured document's
    // table → the inquiry-level facts they state. Additive — mirrors the BOQ partial (retry
    // loop, governance ledger, strict-then-extracted JSON parsing, never patching model output)
    // with its own prompt and schema. The extraction path in the main file is untouched.
    public partial class OllamaLlmService
    {
        public async Task<HeaderCompletionResult?> CompleteHeaderAsync(
            string headerText, AiCallContext context, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(headerText))
                return null;

            var processedText = PrepareProviderInput(headerText);
            var instructions = BuildHeaderCompletionInstructions();
            var maximumRequestBytes = MeasureRequestBytes(instructions, processedText);
            var governedContext = context with { ProviderClass = _providerClass };
            var reservation = await _governance.ReserveAsync(
                governedContext, "Ollama", _model, processedText, maximumRequestBytes,
                _maximumOutputTokens, MAX_RETRIES, cancellationToken);
            long totalInputTokens = 0;
            long totalOutputTokens = 0;
            var aggregateSource = AiTokenSources.ProviderExact;

            _log.LogInformation("Sending header completion request. Text length: {Length} chars", processedText.Length);

            for (int attempt = 1; attempt <= MAX_RETRIES; attempt++)
            {
                var started = DateTime.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                var providerCallCompleted = false;
                try
                {
                    var call = await SendHeaderCompletionRequestAsync(instructions, processedText, cancellationToken);
                    providerCallCompleted = true;
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
                        return call.Result;
                    }

                    // The header text is a few hundred characters; a truncated answer is a
                    // provider fault a retry will not fix, and there is nothing smaller to send.
                    if (call.ErrorCode == AiErrorCodes.OutputTruncated)
                        break;
                    if (!IsTransient(call.HttpStatus))
                        break;
                    if (attempt < MAX_RETRIES)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
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
                    _log.LogError(ex, "HTTP error on header completion attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
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
                    _log.LogError(ex, "Header completion timeout on attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
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
                        "transport_unknown", 0, 0, stopwatch.ElapsedMilliseconds, started, CancellationToken.None);
                    _log.LogError(ex, "Unexpected error on header completion attempt {Attempt}/{MaxRetries}", attempt, MAX_RETRIES);
                    if (attempt < MAX_RETRIES)
                        await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }

            await _governance.CompleteAsync(reservation, AiCallStatuses.Failed,
                totalInputTokens, totalOutputTokens, aggregateSource, null,
                AiErrorCodes.AttemptsExhausted, CancellationToken.None);
            _log.LogWarning("Header completion produced no trustworthy result after {MaxRetries} attempts.", MAX_RETRIES);
            return null;
        }

        private async Task<ProviderCallResult<HeaderCompletionResult>> SendHeaderCompletionRequestAsync(
            string trustedInstructions, string untrustedDocument, CancellationToken ct)
        {
            var payload = new OllamaRequest(
                Model: _model,
                Messages: BuildGovernedMessages(trustedInstructions, untrustedDocument),
                Stream: false,
                Format: "json",
                Think: false,
                Options: new OllamaOptions(Temperature: TEMPERATURE, NumPredict: _maximumOutputTokens)
            );

            using var response = await _http.PostAsJsonAsync("api/chat", payload, _jsonOptions, ct);
            var providerRequestId = ReadProviderRequestId(response);

            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Ollama API returned {StatusCode} for header completion.", response.StatusCode);
                return new(null, null, (int)response.StatusCode, providerRequestId, null, null, null, "provider_http_error");
            }

            var ollamaResponse = await response.Content.ReadFromJsonAsync<OllamaResponse>(_jsonOptions, ct);
            var rawContent = ollamaResponse?.Message?.Content?.Trim();
            var truncated = IsOutputTruncated(ollamaResponse?.DoneReason);

            if (string.IsNullOrWhiteSpace(rawContent))
            {
                return new(null, null, (int)response.StatusCode, providerRequestId,
                    ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                    ollamaResponse?.TotalDuration, truncated ? AiErrorCodes.OutputTruncated : AiErrorCodes.EmptyResponse);
            }

            if (truncated)
            {
                LogOutputTruncated("header completion", ollamaResponse?.EvalCount, null, rawContent.Length);
                return new(null, rawContent, (int)response.StatusCode, providerRequestId,
                    ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                    ollamaResponse?.TotalDuration, AiErrorCodes.OutputTruncated);
            }

            var parsed = ParseHeaderCompletionJson(rawContent);
            return new(parsed, rawContent, (int)response.StatusCode, providerRequestId,
                ollamaResponse?.PromptEvalCount, ollamaResponse?.EvalCount,
                ollamaResponse?.TotalDuration, parsed is null ? AiErrorCodes.InvalidOutput : null);
        }

        private HeaderCompletionResult? ParseHeaderCompletionJson(string rawContent)
        {
            // Same trust policy as extraction (ING-03): strict parse first; a single
            // non-destructive outermost-object trim second; never heuristic rewriting.
            var strict = TryStrictHeaderParse(rawContent);
            if (strict.parsed)
                return strict.result;

            var extracted = ExtractJsonObject(rawContent);
            if (!string.IsNullOrWhiteSpace(extracted) && !string.Equals(extracted, rawContent, StringComparison.Ordinal))
            {
                var second = TryStrictHeaderParse(extracted);
                if (second.parsed)
                    return second.result;
            }

            _log.LogWarning("Ollama header completion output was not valid JSON. Content length: {RawLength}", rawContent.Length);
            return null;
        }

        private (bool parsed, HeaderCompletionResult? result) TryStrictHeaderParse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (false, null);
            try
            {
                var result = JsonSerializer.Deserialize<HeaderCompletionResult>(json, _jsonOptions);
                if (result is null)
                    return (false, null);
                if (result.OverallConfidence is < 0 or > 1)
                {
                    _log.LogWarning("Header completion returned an out-of-range confidence: {Confidence}", result.OverallConfidence);
                    return (true, null);
                }
                return (true, result);
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Strict header completion JSON parse failed. Content length: {Len}", json.Length);
                return (false, null);
            }
        }

        private static string BuildHeaderCompletionInstructions()
        {
            return @"You read the HEADER TEXT of a request for quotation — the labelled lines and prose that sit around its line-item table — and report the inquiry-level facts it states. The line items themselves are NOT in this text and must never be invented.

**CRITICAL RULES:**
1. Return ONLY valid JSON — no markdown, no explanation, no preamble.
2. COPY, NEVER INFER. Fill a field only when the text states it. Buyers head the same fact many ways: a closing date may be called ""BCD"", ""Bid Close"", ""Due date"", ""Response date"", ""Closing date & time"", ""Last date for submission"" or ""Deadline""; an RFQ number may be called an ""Event"", ""Enquiry"", ""Tender"", ""RFP"", ""PR"", ""Bid"" or ""Reference"" number. Read the MEANING of the label, not its spelling.
3. EVERY filled field carries its Span: a VERBATIM quote of at most 120 characters, copied character-for-character from the text, that contains the value together with its label. No span, no value.
4. Dates are ""YYYY-MM-DD"", or ""YYYY-MM-DD HH:mm"" when the text states a time of day. If the day/month order of a numeric date is ambiguous, read it day-first.
5. Never fill a field from your own knowledge, from a file name, or from anything that looks like a line item.
6. ""RequiredDeliveryDate"" is when the BUYER wants the goods delivered — never the closing date. ""AgreementReference"" is a standing contract, framework or agreement number — never the RFQ number itself.
7. Return null for anything the text does not state.
8. ""OverallConfidence"" is your honest reading, 0.0–1.0.

**REQUIRED JSON SCHEMA (return exactly these keys):**
{
  ""Rfqno"": string | null,
  ""RfqnoSpan"": string | null,
  ""BidClosingDate"": ""YYYY-MM-DD"" | ""YYYY-MM-DD HH:mm"" | null,
  ""BidClosingDateSpan"": string | null,
  ""RequiredDeliveryDate"": ""YYYY-MM-DD"" | null,
  ""RequiredDeliveryDateSpan"": string | null,
  ""DeliveryLocation"": string | null,
  ""DeliveryLocationSpan"": string | null,
  ""AgreementReference"": string | null,
  ""AgreementReferenceSpan"": string | null,
  ""OverallConfidence"": number
}

Return ONLY the JSON object, nothing else.";
        }
    }
}
