# AccountOverdraftHistory — Business Requirements Document

## Overview
Produces a historical record of all overdraft events enriched with account type information by joining overdraft events with account data. The output is a date-partitioned Parquet dataset used for downstream overdraft analytics.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `overdraft_history`
- **outputDirectory**: `Output/curated/account_overdraft_history/`
- **numParts**: 50
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state (`__minEffectiveDate` / `__maxEffectiveDate`) | [account_overdraft_history.json:4-11] |
| datalake.accounts | account_id, customer_id, account_type, account_status, interest_rate, credit_limit | Effective date range injected via shared state | [account_overdraft_history.json:13-18] |

### Table Schemas (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

## Business Rules

BR-1: Join overdraft events to accounts on `account_id` AND `as_of` date (same-day snapshot join).
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:22] SQL: `JOIN accounts a ON oe.account_id = a.account_id AND oe.as_of = a.as_of`

BR-2: This is an INNER JOIN, meaning overdraft events that cannot be matched to an account record on the same `as_of` date are excluded from output.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:22] SQL uses `JOIN` (not `LEFT JOIN`)

BR-3: Output is ordered by `as_of` ascending, then `overdraft_id` ascending.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:22] SQL: `ORDER BY oe.as_of, oe.overdraft_id`

BR-4: Effective dates are injected by the executor at runtime (no hardcoded dates in config). DataSourcing modules have no `minEffectiveDate` / `maxEffectiveDate` fields.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:4-18] No date fields in DataSourcing configs; [Architecture.md:44] executor injects dates via shared state

BR-5: Only `account_type` is used from the accounts table in the output. The columns `account_status`, `interest_rate`, and `credit_limit` are sourced but not referenced in the transformation SQL.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:22] SQL SELECT list only references `a.account_type`; columns `account_status`, `interest_rate`, `credit_limit` are sourced at line 17 but unused in SQL

BR-6: The `event_timestamp` column is sourced from `overdraft_events` but is NOT included in the transformation output.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:22] SQL SELECT list does not include `event_timestamp`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| overdraft_id | overdraft_events.overdraft_id | Direct pass-through | [account_overdraft_history.json:22] `oe.overdraft_id` |
| account_id | overdraft_events.account_id | Direct pass-through | [account_overdraft_history.json:22] `oe.account_id` |
| customer_id | overdraft_events.customer_id | Direct pass-through | [account_overdraft_history.json:22] `oe.customer_id` |
| account_type | accounts.account_type | Direct pass-through via JOIN | [account_overdraft_history.json:22] `a.account_type` |
| overdraft_amount | overdraft_events.overdraft_amount | Direct pass-through | [account_overdraft_history.json:22] `oe.overdraft_amount` |
| fee_amount | overdraft_events.fee_amount | Direct pass-through | [account_overdraft_history.json:22] `oe.fee_amount` |
| fee_waived | overdraft_events.fee_waived | Direct pass-through | [account_overdraft_history.json:22] `oe.fee_waived` |
| as_of | overdraft_events.as_of | Direct pass-through | [account_overdraft_history.json:22] `oe.as_of` |

## Non-Deterministic Fields
None identified. All output fields are deterministic based on source data and effective date range.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the entire output directory. On multi-day auto-advance, the final day's run overwrites all prior days' output. This means only the last effective date's data persists.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:29] `"writeMode": "Overwrite"`; [Architecture.md:206-215] ParquetFileWriter Overwrite semantics

## Edge Cases

EC-1: **Unmatched overdraft events** — If an overdraft event references an `account_id` that has no matching accounts row for the same `as_of` date, the event is silently dropped (INNER JOIN).
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:22] `JOIN` (not `LEFT JOIN`)

EC-2: **Empty source data** — If either table returns zero rows for the effective date range, the Transformation produces an empty DataFrame and ParquetFileWriter writes empty part files.
- Confidence: MEDIUM
- Evidence: Inferred from framework behavior; Transformation SQL with empty inputs yields empty result

EC-3: **50 Parquet part files** — Output is split across 50 part files regardless of data volume, which may produce many empty or near-empty part files for small datasets.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:28] `"numParts": 50`

EC-4: **Overwrite on multi-day runs** — During auto-advance gap-fill, each effective date run overwrites the previous. Only the final effective date's result survives.
- Confidence: HIGH
- Evidence: [account_overdraft_history.json:29] `"writeMode": "Overwrite"`; [Architecture.md:96] gap-fill behavior

EC-5: **Sourced but unused columns** — `account_status`, `interest_rate`, `credit_limit` from accounts and `event_timestamp` from overdraft_events are sourced but never appear in output.
- Confidence: HIGH
- Evidence: Comparison of DataSourcing column lists vs. Transformation SQL SELECT

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Inner join on account_id + as_of | [account_overdraft_history.json:22] |
| BR-2: Inner join excludes unmatched | [account_overdraft_history.json:22] |
| BR-3: Order by as_of, overdraft_id | [account_overdraft_history.json:22] |
| BR-4: Runtime date injection | [account_overdraft_history.json:4-18], [Architecture.md:44] |
| BR-5: Unused sourced columns (accounts) | [account_overdraft_history.json:17,22] |
| BR-6: event_timestamp sourced but excluded | [account_overdraft_history.json:10,22] |
| EC-1: Unmatched events dropped | [account_overdraft_history.json:22] |
| EC-3: 50 part files | [account_overdraft_history.json:28] |
| EC-4: Overwrite on multi-day | [account_overdraft_history.json:29] |

## Open Questions
1. **Why 50 part files?** The overdraft_events table has only 139 total rows across 69 dates. Splitting into 50 parts seems excessive. The numParts value may be copied from a production config where data volumes are much larger. Confidence: LOW.
2. **Why source unused columns?** `account_status`, `interest_rate`, `credit_limit`, and `event_timestamp` are sourced but never used. This may be an oversight or intentional for potential future use. Confidence: LOW.
