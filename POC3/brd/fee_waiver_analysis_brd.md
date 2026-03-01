# FeeWaiverAnalysis — Business Requirements Document

## Overview
Analyzes overdraft events grouped by fee waiver status, producing summary statistics (event count, total fees, average fee) per waiver category per date. Output is a single CSV file.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `fee_waiver_summary`
- **outputFile**: `Output/curated/fee_waiver_analysis.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not configured (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [fee_waiver_analysis.json:4-11] |
| datalake.accounts | account_id, customer_id, account_type, account_status, interest_rate, credit_limit, apr | Effective date range injected via shared state | [fee_waiver_analysis.json:13-19] |

### Table Schemas (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

## Business Rules

BR-1: The Transformation uses a LEFT JOIN from overdraft_events to accounts on `account_id` AND `as_of`, but NO columns from accounts appear in the SELECT or GROUP BY. The join is effectively a dead-end.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] SQL: `LEFT JOIN accounts a ON oe.account_id = a.account_id AND oe.as_of = a.as_of` — SELECT only references `oe.*` columns

BR-2: Results are grouped by `fee_waived` status and `as_of` date.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] SQL: `GROUP BY oe.fee_waived, oe.as_of`

BR-3: NULL `fee_amount` values are coalesced to 0.0 using a CASE expression.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] SQL: `CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END`

BR-4: `total_fees` is the ROUND(SUM(...), 2) of fee amounts (with NULL->0 coalescing), rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] SQL: `ROUND(SUM(CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END), 2) AS total_fees`

BR-5: `avg_fee` is the ROUND(AVG(...), 2) of fee amounts (with NULL->0 coalescing), rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] SQL: `ROUND(AVG(CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END), 2) AS avg_fee`

BR-6: Output is ordered by `fee_waived` ascending (false before true).
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] SQL: `ORDER BY oe.fee_waived`

BR-7: The LEFT JOIN to accounts can produce duplicate overdraft event rows if an account has multiple rows for the same `as_of` date, inflating counts and sums.
- Confidence: MEDIUM
- Evidence: [fee_waiver_analysis.json:22] LEFT JOIN without deduplication; if accounts has duplicate (account_id, as_of) pairs, rows multiply

BR-8: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:4-19] No `minEffectiveDate` / `maxEffectiveDate` in DataSourcing configs

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| fee_waived | overdraft_events.fee_waived | Direct pass-through, used as GROUP BY key | [fee_waiver_analysis.json:22] |
| event_count | Derived | COUNT(*) per group | [fee_waiver_analysis.json:22] |
| total_fees | overdraft_events.fee_amount | ROUND(SUM(COALESCE(fee_amount, 0.0)), 2) | [fee_waiver_analysis.json:22] |
| avg_fee | overdraft_events.fee_amount | ROUND(AVG(COALESCE(fee_amount, 0.0)), 2) | [fee_waiver_analysis.json:22] |
| as_of | overdraft_events.as_of | Direct pass-through, used as GROUP BY key | [fee_waiver_analysis.json:22] |

## Non-Deterministic Fields
None identified. All output is deterministic.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the entire CSV file. On multi-day auto-advance, only the last effective date's output survives.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:29] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Accounts join is a dead-end** — The LEFT JOIN to accounts contributes no columns to the output. This may cause row duplication if the join produces multiple matches.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] No `a.*` columns in SELECT

EC-2: **NULL fee_amount handling** — NULL fee amounts are treated as 0.0, which means they are included in COUNT(*) and AVG calculations (pulling the average down).
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:22] CASE WHEN NULL THEN 0.0

EC-3: **Overwrite on multi-day runs** — During auto-advance, only the last day's run output persists.
- Confidence: HIGH
- Evidence: [fee_waiver_analysis.json:29] `"writeMode": "Overwrite"`

EC-4: **Empty data** — If no overdraft events exist for the effective date range, an empty CSV with only a header row is produced.
- Confidence: MEDIUM
- Evidence: Inferred from framework behavior with empty Transformation results

EC-5: **Unused sourced columns** — `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` from overdraft_events, and ALL columns from accounts are sourced but unused in the Transformation SQL output.
- Confidence: HIGH
- Evidence: Comparison of DataSourcing columns vs. Transformation SELECT list

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Dead-end LEFT JOIN | [fee_waiver_analysis.json:22] |
| BR-2: Group by fee_waived + as_of | [fee_waiver_analysis.json:22] |
| BR-3: NULL coalescing | [fee_waiver_analysis.json:22] |
| BR-4: Total fees rounding | [fee_waiver_analysis.json:22] |
| BR-5: Avg fee rounding | [fee_waiver_analysis.json:22] |
| BR-6: Order by fee_waived | [fee_waiver_analysis.json:22] |
| BR-7: Potential row duplication | [fee_waiver_analysis.json:22] |
| BR-8: Runtime date injection | [fee_waiver_analysis.json:4-19] |

## Open Questions
1. **Why join to accounts?** The LEFT JOIN to accounts serves no purpose in the current SQL — no account columns appear in the output. This is either a bug (intended to include account_type in the analysis) or dead code from a prior iteration. Confidence: HIGH that this is unintended.
2. **Row duplication risk** — If the accounts table has multiple rows per (account_id, as_of), the LEFT JOIN will multiply overdraft event rows, inflating counts and sums. Confidence: MEDIUM — depends on whether accounts has unique (account_id, as_of) pairs.
