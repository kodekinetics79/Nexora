# Nexora BRD v3.0 Overbuild Register — Gate 0

This register does not label tenancy, RLS, authorization, audit, evidence integrity, queues, idempotency, health checks, observability, provisioning safety, or retention governance as overbuild. Those are operational controls even when they have no module ID.

| Capability outside or not attributable to Phase 1 | Business value | Operational cost | Risk to core delivery | Dependencies | Recommendation |
|---|---|---|---|---|---|
| Platform subscription billing, usage rating, dunning and accounting outbox | Monetization and SaaS operations | Very high: 2026-08 billing migrations, workers, tax/rating rules and support burden | High distraction and migration surface for an RFQ pilot | Platform plans, metering, finance provider | **Feature-flag; hide from pilot tenant navigation.** |
| Platform-owner 360 control plane, tenant provisioning/offboarding/support desk | Necessary multi-tenant operations and support | High | Medium; broad privileged surface, but security-critical | Platform IAM, MFA, network boundary, audit | **Keep active for operators; never expose to normal tenant users.** Not overbuild in the security sense. |
| Opportunity-priority shadow and commercial digital twin | Better prioritization and pricing scenarios | High calibration/data cost | High if presented as authoritative before outcome cohorts mature | Orders, quotes, inventory, supplier offers, FX | **Feature-flag; hide from pilot.** |
| Supplier bid quality and negotiation intelligence | Better sourcing decisions | Medium-high | Medium; can obscure basic supplier RFQ/quote loop | Supplier quote evidence and outcomes | **Feature-flag; retain factual comparison.** |
| Sales coaching, customer health, growth and revenue-recovery recommendations | Manager insight | High data-quality/explainability cost | Medium-high distraction from transaction closure | Customer 360, outcomes, follow-ups | **Hide from pilot; defer.** |
| Agent/Copilot and BOQ service workspace | Operator assistance and service quotation | High model/governance and UX cost | High scope expansion; separate workflows | AI gateway, approvals, BOQ entities | **Feature-flag; hide from pilot.** |
| General ledger, bank reconciliation, refunds, write-offs, statements and automated dunning | Finance operations beyond simple invoice/payment status | Very high compliance and reconciliation cost | High until payment-collection scope is decided | Bank data, finance HMACs, accounting authority | **Feature-flag; keep inactive for pilot.** |
| Taxonomy/skill studio, artifact release center and quality analytics | Governance and repeatability | Medium | Medium navigation/support burden | Platform governance records | **Hide from pilot; retain for controlled operator use.** |
| Commercial exception center and predictive recovery queues | Exception-first operations | Medium | Medium; may duplicate basic work queues | Lifecycle/outbox/ownership | **Feature-flag; defer unless explicitly selected for pilot.** |
| Multi-provider email administration and external AI authorization controls | Operational flexibility and safety | Medium | Low-to-medium; necessary when channels/providers are enabled | Secrets, provider configuration | **Keep controls; expose only enabled provider paths.** |

No item should be deleted on this audit. Feature flags and navigation hiding are Gate 1 product decisions, not actions taken here.
