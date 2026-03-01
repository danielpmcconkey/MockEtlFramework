# OverdraftDailySummary — Business Requirements Document

## Overview
Produces a daily summary of overdraft activity including event count, total overdraft amount, and total fees per date. Appends a weekly summary row on Sundays. Output is a CSV file with a trailer line.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/overdraft_daily_summary.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: `TRAILER|{row_count}|{date}`

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Effective date range injected via shared state | [overdraft_daily_summary.json:4-11] |
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount, description | Effective date range injected via shared state; **NEVER USED** | [overdraft_daily_summary.json:13-19] |

### Table Schemas (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

**transactions**: transaction_id (integer), account_id (integer), txn_timestamp (timestamp), txn_type (varchar), amount (numeric), description (varchar), as_of (date)

## Business Rules

BR-1: Overdraft events are grouped by `as_of` date, with count, total_overdraft_amount, and total_fees accumulated per group.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:32-45] Group by `asOf` string, accumulating count/amount/fees

BR-2: `total_fees` includes ALL fees (both charged and waived) — there is no filtering by `fee_waived` status.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:38] `var fee = Convert.ToDecimal(row["fee_amount"]);` — no fee_waived check

BR-3: The `event_date` and `as_of` columns in each output row are set to the same value (the `as_of` date string from the source data).
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:50-56] `["event_date"] = kvp.Key` and `["as_of"] = kvp.Key` where key is as_of string

BR-4: **Weekly summary row on Sundays** — If `__maxEffectiveDate` is a Sunday, an additional `WEEKLY_TOTAL` row is appended. This row sums ALL groups in the current run (across all dates in the effective range), not just the current week.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:61-75] `if (maxDate.DayOfWeek == DayOfWeek.Sunday)` — sums `groups.Values`; Comment: `W3a: End-of-week boundary`

BR-5: The WEEKLY_TOTAL row uses `"WEEKLY_TOTAL"` as event_date and `maxDate.ToString("yyyy-MM-dd")` as as_of.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:67-73]

BR-6: **Transactions table is sourced but never used** — The DataSourcing module loads transactions, but the External processor never accesses `sharedState["transactions"]`.
- Confidence: HIGH
- Evidence: [overdraft_daily_summary.json:13-19]; [OverdraftDailySummaryProcessor.cs:23] Comment: `AP1: transactions sourced but never used (dead-end)`

BR-7: Decimal arithmetic is used for amount and fee accumulation.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:32] `Dictionary<string, (int count, decimal totalAmount, decimal totalFees)>`

BR-8: The trailer line follows format `TRAILER|{row_count}|{date}` where `{row_count}` is the count of data rows in the output DataFrame (including the WEEKLY_TOTAL row if present), and `{date}` is `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [overdraft_daily_summary.json:29] `"trailerFormat": "TRAILER|{row_count}|{date}"`; [Architecture.md:241] trailer token semantics

BR-9: Effective dates are injected by the executor at runtime.
- Confidence: HIGH
- Evidence: [overdraft_daily_summary.json:4-19] No hardcoded dates in DataSourcing configs

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| event_date | overdraft_events.as_of | String value of as_of date; or `"WEEKLY_TOTAL"` for summary row | [OverdraftDailySummaryProcessor.cs:51,68] |
| overdraft_count | Derived | COUNT of events per as_of date | [OverdraftDailySummaryProcessor.cs:42] |
| total_overdraft_amount | overdraft_events.overdraft_amount | SUM per as_of date (decimal) | [OverdraftDailySummaryProcessor.cs:43] |
| total_fees | overdraft_events.fee_amount | SUM of all fees per as_of date (decimal, includes both charged and waived) | [OverdraftDailySummaryProcessor.cs:44] |
| as_of | overdraft_events.as_of / maxDate | Same as event_date for daily rows; maxDate for WEEKLY_TOTAL | [OverdraftDailySummaryProcessor.cs:55,73] |

**Trailer row**: `TRAILER|{row_count}|{date}` — standard framework trailer

## Non-Deterministic Fields
None identified. All output is deterministic given the same source data and effective date.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the entire CSV file. On multi-day auto-advance, only the final effective date's output survives.
- The trailer `{row_count}` includes the WEEKLY_TOTAL row if present (it counts all DataFrame rows).
- Confidence: HIGH
- Evidence: [overdraft_daily_summary.json:30] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Weekly total scope** — The WEEKLY_TOTAL row sums ALL groups across the entire effective date range, not just the current week (Monday-Sunday). If the effective range spans multiple weeks, the "weekly" total is actually a multi-week total.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:62-64] `groups.Values.Sum(...)` — sums ALL groups

EC-2: **Dead-end transactions table** — Transactions data is sourced but never accessed by the External processor, wasting query resources.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:23] Comment: `AP1`

EC-3: **Trailer row count includes WEEKLY_TOTAL** — The `{row_count}` token counts all DataFrame rows, including the synthetic WEEKLY_TOTAL row if present.
- Confidence: MEDIUM
- Evidence: Inferred from CsvFileWriter behavior counting DataFrame rows; WEEKLY_TOTAL is a row in the DataFrame

EC-4: **Empty source data** — If no overdraft events exist, an empty DataFrame is returned. The CSV will contain a header and trailer only (with `{row_count}` = 0).
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:25-29]

EC-5: **Overwrite on multi-day runs** — Only the last effective date's output survives.
- Confidence: HIGH
- Evidence: [overdraft_daily_summary.json:30] `"writeMode": "Overwrite"`

EC-6: **Fee_waived not considered** — Total fees include both charged and waived fees. The `fee_waived` column is sourced but not used in the summary logic.
- Confidence: HIGH
- Evidence: [OverdraftDailySummaryProcessor.cs:38] No fee_waived filter; [overdraft_daily_summary.json:10] fee_waived is sourced

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by as_of | [OverdraftDailySummaryProcessor.cs:32-45] |
| BR-2: All fees included | [OverdraftDailySummaryProcessor.cs:38] |
| BR-3: event_date = as_of | [OverdraftDailySummaryProcessor.cs:50-56] |
| BR-4: Weekly total on Sunday | [OverdraftDailySummaryProcessor.cs:61-75] |
| BR-5: WEEKLY_TOTAL label | [OverdraftDailySummaryProcessor.cs:67-73] |
| BR-6: Dead-end transactions | [OverdraftDailySummaryProcessor.cs:23] |
| BR-7: Decimal arithmetic | [OverdraftDailySummaryProcessor.cs:32] |
| BR-8: Trailer format | [overdraft_daily_summary.json:29] |
| EC-1: Weekly total scope | [OverdraftDailySummaryProcessor.cs:62-64] |

## Open Questions
1. **Weekly total scope** — Is the WEEKLY_TOTAL row intended to sum only the current week's data, or all data in the effective range? The code sums all groups, which may span multiple weeks. Confidence: HIGH that this is worth investigating.
2. **Why source transactions?** The transactions table is loaded but never used. This is likely dead code from a planned feature that was never implemented. Confidence: HIGH.
