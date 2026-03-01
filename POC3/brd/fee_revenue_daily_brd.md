# FeeRevenueDaily — Business Requirements Document

## Overview
Calculates daily fee revenue from overdraft events, breaking down charged vs. waived fees and computing net revenue. Appends a monthly summary row on the last day of each month. Output is a single CSV file.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/fee_revenue_daily.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not configured (no trailer)

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, overdraft_amount, fee_amount, fee_waived, event_timestamp | Hardcoded date range: `minEffectiveDate: 2024-10-01`, `maxEffectiveDate: 2024-12-31` | [fee_revenue_daily.json:5-13] |

### Table Schema (from database)

**overdraft_events**: overdraft_id (integer), account_id (integer), customer_id (integer), overdraft_amount (numeric), fee_amount (numeric), fee_waived (boolean), event_timestamp (timestamp), as_of (date)

## Business Rules

BR-1: DataSourcing uses **hardcoded** effective dates (`2024-10-01` to `2024-12-31`) rather than executor-injected dates. This means the full date range is sourced every run regardless of the current effective date.
- Confidence: HIGH
- Evidence: [fee_revenue_daily.json:12-13] `"minEffectiveDate": "2024-10-01"`, `"maxEffectiveDate": "2024-12-31"`

BR-2: The External module filters the over-sourced data to the **prior business day** (`__maxEffectiveDate` minus 1 day). Only rows where `as_of` matches `maxDate.AddDays(-1)` are used for the daily output row.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:30-33] `currentDateRows = overdraftEvents.Rows.Where(r => r["as_of"]?.ToString() == maxDate.AddDays(-1).ToString("yyyy-MM-dd") ...)`

BR-3: Fee categorization: if `fee_waived` is true, the `fee_amount` is accumulated into `waived_fees`; otherwise into `charged_fees`.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:46-53] `if (feeWaived) waivedFees += feeAmount; else chargedFees += feeAmount;`

BR-4: Net revenue is calculated as `charged_fees - waived_fees`.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:55] `double netRevenue = chargedFees - waivedFees;`

BR-5: **Double-precision floating point** is used for fee accumulation (not decimal), which can introduce floating-point epsilon errors in the output.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:42-43] `double chargedFees = 0.0; double waivedFees = 0.0;` — Comment: `W6: Double epsilon`

BR-6: On the **last day of the month**, an additional `MONTHLY_TOTAL` summary row is appended. This row sums ALL rows in the sourced overdraft_events DataFrame (full hardcoded date range), not just the current month.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:69-93] `if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))` — iterates `overdraftEvents.Rows` (all sourced data)

BR-7: The monthly total row uses `"MONTHLY_TOTAL"` as the `event_date` value, with the same `as_of` as the daily row.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:86-93] `["event_date"] = "MONTHLY_TOTAL"`

BR-8: If no overdraft events exist for the current effective date, an empty DataFrame is returned (no output rows).
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:34-38] `if (currentDateRows.Count == 0)` returns empty DataFrame

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| event_date | Derived from `__maxEffectiveDate` | Formatted as `yyyy-MM-dd` string; or `"MONTHLY_TOTAL"` for summary rows | [FeeRevenueDailyProcessor.cs:62,86] |
| charged_fees | overdraft_events.fee_amount | Sum of fee_amount where fee_waived=false; double precision | [FeeRevenueDailyProcessor.cs:52] |
| waived_fees | overdraft_events.fee_amount | Sum of fee_amount where fee_waived=true; double precision | [FeeRevenueDailyProcessor.cs:50] |
| net_revenue | Derived | charged_fees minus waived_fees; double precision | [FeeRevenueDailyProcessor.cs:55] |
| as_of | `__maxEffectiveDate` | Formatted as `yyyy-MM-dd` string | [FeeRevenueDailyProcessor.cs:65] |

## Non-Deterministic Fields
None identified. All output is deterministic given the same effective date and source data.

## Write Mode Implications
- **Overwrite** mode: Each execution replaces the entire CSV file. On multi-day auto-advance, only the last effective date's output survives.
- The daily row always contains exactly one day's data. The MONTHLY_TOTAL row (when present) aggregates the full hardcoded source range, not just the current month.
- Confidence: HIGH
- Evidence: [fee_revenue_daily.json:23] `"writeMode": "Overwrite"`

## Edge Cases

EC-1: **Monthly total uses full source range, not just current month** — The MONTHLY_TOTAL row iterates `overdraftEvents.Rows` which contains ALL data from the hardcoded range (2024-10-01 to 2024-12-31), not filtered to the current month. This means the monthly total is actually a full-period total.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:75] `foreach (var row in overdraftEvents.Rows)` — no month filter applied

EC-2: **Floating-point precision** — Using `double` instead of `decimal` for fee accumulation can cause tiny rounding differences (e.g., 35.00 + 35.00 might yield 69.99999999999999 instead of 70.00).
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:42-43] `double chargedFees = 0.0; double waivedFees = 0.0;` — Comment: `W6: Double epsilon`

EC-3: **Overwrite on multi-day runs** — During auto-advance gap-fill, each effective date overwrites the CSV. Only the final date's output persists.
- Confidence: HIGH
- Evidence: [fee_revenue_daily.json:23] `"writeMode": "Overwrite"`

EC-4: **Days with no overdraft events** — If `__maxEffectiveDate` falls on a date with no overdraft events, an empty DataFrame is produced and the CSV will contain only a header row.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:34-38]

EC-5: **Fallback for missing `__maxEffectiveDate`** — If not set in shared state, falls back to `DateTime.Today`.
- Confidence: HIGH
- Evidence: [FeeRevenueDailyProcessor.cs:19-20]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Hardcoded date range | [fee_revenue_daily.json:12-13] |
| BR-2: Filter to prior business day | [FeeRevenueDailyProcessor.cs:30-33] |
| BR-3: Fee categorization | [FeeRevenueDailyProcessor.cs:46-53] |
| BR-4: Net revenue formula | [FeeRevenueDailyProcessor.cs:55] |
| BR-5: Double precision | [FeeRevenueDailyProcessor.cs:42-43] |
| BR-6: Monthly total row | [FeeRevenueDailyProcessor.cs:69-93] |
| BR-7: MONTHLY_TOTAL label | [FeeRevenueDailyProcessor.cs:86] |
| BR-8: Empty result on no data | [FeeRevenueDailyProcessor.cs:34-38] |
| EC-1: Monthly total scope bug | [FeeRevenueDailyProcessor.cs:75] |
| EC-2: Float precision | [FeeRevenueDailyProcessor.cs:42-43] |

## Open Questions
1. **Monthly total scope** — Is the MONTHLY_TOTAL row intended to sum only the current month, or the full source range? The code sums all rows (full range), which appears to be a bug. Confidence: HIGH that this is unintended behavior.
2. **Why hardcoded dates?** — The DataSourcing uses explicit date range `2024-10-01` to `2024-12-31` instead of executor-injected dates. This means the job always pulls the same data regardless of effective date. This may be intentional (External filters to single day) or an oversight. Confidence: MEDIUM.
