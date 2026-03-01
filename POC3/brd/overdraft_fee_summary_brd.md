# OverdraftFeeSummary — Business Requirements Document

## Overview
Summarizes overdraft fee data grouped by fee waiver status and date. Includes total fees, event count, and average fee per group. Uses a CTE with ROW_NUMBER (though the row number is not used in the final output). Output is a single CSV file.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `fee_summary`
- **outputFile**: `Output/curated/overdraft_fee_summary.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not configured (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [overdraft_fee_summary.json:4-11] |

### Table Schema (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

## Business Rules

BR-1: The SQL uses a CTE (`all_events`) that selects from overdraft_events and computes `ROW_NUMBER() OVER (PARTITION BY as_of ORDER BY overdraft_id)`. However, the `rn` column is **never referenced** in the outer query.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] CTE defines `rn` but outer SELECT does not reference it

BR-2: Results are grouped by `fee_waived` status and `as_of` date.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] SQL: `GROUP BY ae.fee_waived, ae.as_of`

BR-3: `total_fees` is `ROUND(SUM(fee_amount), 2)` — rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] SQL: `ROUND(SUM(ae.fee_amount), 2) AS total_fees`

BR-4: `event_count` is `COUNT(*)` per group.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] SQL: `COUNT(*) AS event_count`

BR-5: `avg_fee` is `ROUND(AVG(fee_amount), 2)` — rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] SQL: `ROUND(AVG(ae.fee_amount), 2) AS avg_fee`

BR-6: Output is ordered by `fee_waived` ascending (false before true).
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] SQL: `ORDER BY ae.fee_waived`

BR-7: No NULL coalescing is applied to `fee_amount` — NULL values (if any) would be excluded from SUM and AVG by standard SQL semantics, but included in COUNT(*).
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] Direct `SUM(ae.fee_amount)` and `AVG(ae.fee_amount)` without COALESCE

BR-8: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:4-11] No hardcoded dates in DataSourcing config

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| fee_waived | overdraft_events.fee_waived | Direct pass-through, GROUP BY key | [overdraft_fee_summary.json:15] |
| total_fees | overdraft_events.fee_amount | ROUND(SUM(fee_amount), 2) per group | [overdraft_fee_summary.json:15] |
| event_count | Derived | COUNT(*) per group | [overdraft_fee_summary.json:15] |
| avg_fee | overdraft_events.fee_amount | ROUND(AVG(fee_amount), 2) per group | [overdraft_fee_summary.json:15] |
| as_of | overdraft_events.as_of | Direct pass-through, GROUP BY key | [overdraft_fee_summary.json:15] |

## Non-Deterministic Fields
None identified. All output is deterministic.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the entire CSV file. On multi-day auto-advance, only the final effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:23] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Unused ROW_NUMBER** — The CTE computes `ROW_NUMBER() OVER (PARTITION BY as_of ORDER BY overdraft_id)` but the `rn` column is never used. This adds unnecessary computation.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:15] `rn` defined in CTE but not in outer SELECT

EC-2: **NULL fee_amount** — If fee_amount is NULL for any row, SUM and AVG ignore it (standard SQL), but COUNT(*) still counts the row. This could make event_count and avg_fee inconsistent.
- Confidence: MEDIUM
- Evidence: [overdraft_fee_summary.json:15] No COALESCE; actual data shows min fee_amount = 0.00 (no NULLs observed)

EC-3: **Overwrite on multi-day runs** — Only the last effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_fee_summary.json:23] `"writeMode": "Overwrite"`

EC-4: **Empty data** — If no overdraft events exist for the effective date range, the Transformation produces an empty DataFrame, and the CSV will contain only a header row.
- Confidence: MEDIUM
- Evidence: Inferred from framework behavior

EC-5: **Unused sourced columns** — `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are sourced but not referenced in the SQL (though `overdraft_id` is used in the CTE's ROW_NUMBER).
- Confidence: HIGH
- Evidence: Comparison of DataSourcing columns vs. SQL usage

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Unused ROW_NUMBER | [overdraft_fee_summary.json:15] |
| BR-2: Group by fee_waived + as_of | [overdraft_fee_summary.json:15] |
| BR-3: total_fees rounding | [overdraft_fee_summary.json:15] |
| BR-4: event_count | [overdraft_fee_summary.json:15] |
| BR-5: avg_fee rounding | [overdraft_fee_summary.json:15] |
| BR-6: Order by fee_waived | [overdraft_fee_summary.json:15] |
| BR-7: No NULL coalescing | [overdraft_fee_summary.json:15] |
| EC-1: Dead ROW_NUMBER | [overdraft_fee_summary.json:15] |

## Open Questions
1. **Why the ROW_NUMBER CTE?** The `rn` column is computed but never used. This may be leftover from a prior iteration that used row numbering for deduplication or pagination, or it may have been intended for a different purpose. Confidence: HIGH that it is dead code.
2. **Difference from FeeWaiverAnalysis** — This job and FeeWaiverAnalysis produce very similar outputs (grouped by fee_waived + as_of). Key differences: this job does not join to accounts, does not COALESCE NULLs, and has the unused ROW_NUMBER CTE. Confidence: HIGH.
