# ADR-0001 — Move the LLM extraction/drafting layer from Ollama/deepseek to Claude

- Status: **Proposed** (approved in principle by product owner; implementation pending)
- Date: 2026-07-14
- Deciders: CTO/CIO (orchestrator), Chief AI Officer, CISO
- Related: ADR-0002 (technology stack), `SECURITY.md` (Ollama key rotation)

## Context

Today the AI layer is `Services/OllamaLlmService.cs`, which sends normalized RFQ
text to **`https://ollama.com/` (cloud) running `deepseek-v3.1:671b-cloud`**,
authenticated with an API key that was committed in plaintext (now removed,
pending rotation).

Problems with the status quo:

1. **Data egress / DPA risk.** Customer RFQ content — prices, part numbers,
   contacts, commercial terms — is sent to a third-party inference endpoint with
   no data-processing agreement reviewed and no PII minimization. For an
   enterprise procurement product this is a blocker to selling into regulated or
   public-sector buyers.
2. **Non-deterministic structured output.** The current flow expects the model
   to "return JSON". Free-form JSON from a chat endpoint is not schema-guaranteed,
   so extraction can silently drift or fail to parse — violating the
   evidence/accuracy contract.
3. **Single-endpoint reliability.** No graceful degradation when the endpoint is
   slow/down; a failed call risks a lead being dropped instead of retried.
4. **Model governance.** No versioning/eval harness tied to the model; upgrading
   or changing the model is uncontrolled.

## Decision

Adopt **Claude (Anthropic API)** as the LLM provider for document/RFQ
intelligence, behind a **provider abstraction** so the choice stays reversible.
Route by task tier to balance cost and quality:

| Task | Model (current latest IDs) | Why |
|------|-----|-----|
| Message/document classification, routing, cheap triage | **Haiku 4.5** (`claude-haiku-4-5-20251001`) | Fast, cheap, high-volume |
| Field/line-item extraction, cross-doc reconciliation, drafting acknowledgements/clarifications | **Sonnet 5** (`claude-sonnet-5`) | Strong structured extraction at moderate cost |
| Hardest ambiguous reconciliation / low-confidence escalation | **Opus 4.8** (`claude-opus-4-8`) | Highest reasoning, used sparingly |

Non-negotiable guardrails carried into the design:

- **Schema-constrained output via tool-use** — the model is forced to return a
  validated structure (header fields, line items, per-field confidence + source
  span), not free text. Invalid output is retried, not accepted.
- **The LLM is never the authoritative financial calculator** (see ADR-0002 and
  the financial-control engine). It may explain/flag, not compute binding prices.
- **PII/commercial-data minimization** before send (redact where possible;
  document what is sent) and a reviewed data-processing posture.
- **Graceful degradation** — on model unavailability, the intake is queued and
  retried with backoff; **a lead is never silently lost**, it lands in a review
  state.
- **Prompt-injection containment** — document-sourced text is treated as
  untrusted; instructions embedded in documents must not be executed.
- **Versioned + evaluated** — model + prompt versions are pinned and every change
  runs the gold-dataset eval (LOOP_5) before promotion.

## Consequences

- Removes the committed third-party key from the runtime story and gives us an
  enterprise-grade data posture we can put in front of buyers.
- Requires: an `ILlmProvider` seam (keep `OllamaLlmService` as one impl for
  fallback/local-offline), a `ClaudeLlmService`, schema-tool definitions, a small
  gold dataset of real RFQs for accuracy/cost benchmarking, and an
  `ANTHROPIC_API_KEY` supplied via user-secrets/env (never committed).
- Cost/accuracy of Claude vs deepseek must be **measured** on real RFQ samples
  before full cutover — this ADR authorizes the direction, not a blind swap.
- For the **client demo**, this can run against Claude for the marquee extraction
  path while the abstraction keeps Ollama available as a fallback.

## Follow-ups (tracked as work items)
- Define `ILlmProvider` + `ClaudeLlmService` + schema tools.
- Build a 10–20 doc gold dataset from real RFQs already on disk.
- Benchmark accuracy + cost per document (Claude tiers vs deepseek).
- Add PII redaction pre-send and the queue/retry fallback.
