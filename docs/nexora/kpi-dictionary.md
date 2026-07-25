# KPI Dictionary

All times are UTC. Unless stated otherwise, the cohort is the tenant-visible set whose anchor event occurred in `[from, to)`. Each result includes `definitionVersion = release-01`, `generatedAt`, `from`, `to`, `value`, `state`, and drill-down record IDs. `state` is `available` or `insufficient_data`; zero is never used to hide missing evidence.

| KPI | Numerator / value | Denominator | Cohort and exclusions | Drill-down |
|---|---|---|---|---|
| Leads received | Distinct canonical Leads classified `New` | None | Canonical Lead creation occurred in window; duplicates, revisions and possible matches excluded | Lead IDs and Nexora Serials |
| Ingestion volume | All ingestion occurrences | None | Occurrence `CreatedAtUtc` in window, including duplicates, revisions and possible matches | Occurrence and canonical Lead IDs where resolved |
| Duplicate rate | Exact-duplicate occurrences | All ingestion occurrences | Identical tenant and time window | Occurrence IDs and canonical Lead IDs |
| Revision rate | Revision occurrences | All ingestion occurrences | Identical tenant and time window; denominator version `release-01a` | Occurrence, Lead and revision IDs |
| Possible-match rate | Possible-match-review occurrences | All ingestion occurrences | Identical tenant and time window | Occurrence and candidate Lead IDs |
| Leads requiring review | Latest Lead state is review-required | None | Received by window end and not terminal/converted | Lead IDs |
| Qualification rate | Cases with `LEAD_QUALIFIED` | Cases with a qualification decision (`QUALIFIED` or `DISQUALIFIED`) | Decision occurred in window; first valid decision per case | Qualified and disqualified Lead IDs |
| Median time to qualify | Median duration from `LEAD_RECEIVED` to first valid qualification decision | None | Same case, nonnegative timestamps, both events present | Lead IDs with durations |
| Assignment SLA | Cases assigned within configured SLA | Cases with `LEAD_RECEIVED` requiring assignment | Received in window; excludes policy-exempt cases | On-time and late Lead IDs |
| Active workload | Weighted open work at `generatedAt` | None | Nonterminal Lead/RFQ/Quote work; weight from line count, urgency, sourcing, review and follow-up burden | Work item IDs by owner |
| RFQs created | Distinct cases with `RFQ_CREATED` | None | Event occurred in window; one canonical RFQ creation per case/revision policy | RFQ IDs |
| Lead to RFQ conversion | Cases with `RFQ_CREATED` | Cases with `LEAD_RECEIVED` | Received cohort in window; RFQ may occur after `to`, controlled by reported maturity cutoff | Converted and unconverted Lead IDs |
| Quotes ready | Distinct latest quote revisions with `QUOTE_READY` | None | Event occurred in window; superseded revisions excluded | Quote IDs |
| Quote value sent | Sum of latest-version quote value at `QUOTE_SENT` in tenant base currency | None | Sent in window; void/superseded revisions excluded; unknown FX is insufficient data | Quote IDs and values |
| Quote response rate | Cases with `QUOTE_RESPONDED` | Cases with `QUOTE_SENT` | Sent cohort in window and past response maturity cutoff | Responded and pending Quote IDs |
| Win rate | Cases with latest terminal outcome `QUOTE_WON` | Cases with latest terminal outcome `QUOTE_WON` or `QUOTE_LOST` | Outcome event occurred in window; partial/no-quote reported separately | Won and lost Quote IDs |
| Partial outcome rate | Cases with `QUOTE_PARTIAL` | Cases with any terminal quote outcome | Outcome occurred in window | Quote IDs |
| No-quote rate | Cases with `NO_QUOTE` | Cases reaching quote decision | Decision occurred in window; reason required | Case and RFQ IDs |
| Follow-ups overdue | Open `FOLLOW_UP_DUE` events past due without later completion | None | Snapshot at `generatedAt` | Quote/case IDs and due dates |
| Order conversion | Cases with `ORDER_CREATED` | Cases with `QUOTE_SENT` | Sent cohort in window, latest valid quote only, maturity cutoff disclosed | Quote and Order IDs |
| Straight-through processing rate | Leads completed without human review | Leads with a completed processing outcome | Processing completed in window; provider and path recorded | Lead/extraction run IDs |
| Extraction correction rate | Reviewed extracted fields corrected by a user | Reviewed extracted fields | Review decision in window; requires correction ledger | Extraction run and field evidence IDs |

## Reconciliation Rules

- Counts use distinct tenant-qualified Commercial Case IDs unless the KPI explicitly measures documents or fields.
- Release 01A ingestion rates use occurrence counts; `Leads received` uses canonical Lead creation and therefore cannot be inflated by resends or revisions.
- Quote revision KPIs use the latest valid, non-superseded revision per commercial decision.
- Currency totals require an authoritative tenant base currency and effective conversion rate; otherwise the value is `insufficient_data`.
- Every displayed value must reconcile to the exact identifiers returned by its drill-down query under the same filters and freshness boundary.
- Self-reported model confidence is not AI accuracy. Accuracy requires reviewed ground truth and is withheld until correction evidence exists.
