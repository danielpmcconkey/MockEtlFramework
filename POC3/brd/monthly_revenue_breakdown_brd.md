# MonthlyRevenueBreakdown — Business Requirements Document

## Overview
Produces a daily breakdown of revenue from two sources: overdraft fees (non-waived) and credit transaction amounts (as an interest proxy). On fiscal quarter-end boundaries (October 31), additional quarterly summary rows are appended. Output is written to CSV with a TRAILER and Overwrite mode.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/monthly_revenue_breakdown.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.overdraft_events | overdraft_id, account_id, customer_id, fee_amount, fee_waived | Effective date range (injected) | [monthly_revenue_breakdown.json:8-10] |
| datalake.transactions | transaction_id, account_id, txn_type, amount | Effective date range (injected) | [monthly_revenue_breakdown.json:13-16] |
| datalake.customers | id, first_name, last_name | Effective date range (injected) | [monthly_revenue_breakdown.json:19-21] |

## Business Rules

BR-1: Overdraft revenue is computed by summing fee_amount for overdraft events where fee_waived is false. Events with fee_waived=true are excluded from both the revenue total and the count.
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:26-33] — `if (!feeWaived)` gate on both revenue accumulation and count increment

BR-2: Credit interest proxy revenue is computed by summing the amount of all transactions with txn_type == "Credit". This is explicitly labeled as a "proxy for interest" in the code.
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:37-49] — filters on `txnType == "Credit"`

BR-3: Revenue values are rounded to 2 decimal places using banker's rounding (MidpointRounding.ToEven).
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:58,64] — `Math.Round(value, 2, MidpointRounding.ToEven)` — comment "W5: Banker's rounding"

BR-4: The output always contains exactly 2 rows (overdraft_fees and credit_interest_proxy), EXCEPT on October 31 when 2 additional quarterly summary rows are appended (total of 4 rows).
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:53-68] — always 2 rows; [MonthlyRevenueBreakdownBuilder.cs:72-94] — Oct 31 adds 2 more

BR-5: The quarterly summary logic triggers when maxDate.Month == 10 AND maxDate.Day == 31. The comment says "Fiscal quarter boundary: Q4 starts Nov 1, so Oct 31 is the last day of Q3".
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:73] — `if (maxDate.Month == 10 && maxDate.Day == 31)`

BR-6: **NOTE**: The quarterly summary rows use the SAME day's values (not accumulated quarter totals). The quarterly overdraft revenue equals the daily overdraft revenue, and the quarterly credit revenue equals the daily credit revenue for October 31 only.
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:75-78] — `decimal qOverdraftRevenue = overdraftRevenue;` — copies same-day values

BR-7: Quarterly summary rows use prefixed revenue_source names: "QUARTERLY_TOTAL_overdraft_fees" and "QUARTERLY_TOTAL_credit_interest_proxy".
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:82,89]

BR-8: The customers DataFrame is sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:8-99] — no reference to "customers"

BR-9: The as_of value is set to __maxEffectiveDate (a DateOnly value from shared state), not from the source data rows.
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:18,60,66] — `maxDate` used directly as as_of

BR-10: No guard clause on empty data. If overdraft_events or transactions are null, their respective revenues default to 0 with count 0. The output always produces at least 2 rows.
- Confidence: HIGH
- Evidence: [MonthlyRevenueBreakdownBuilder.cs:20-49] — null checks with conditional processing, no early return

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| revenue_source | Fixed strings | "overdraft_fees", "credit_interest_proxy", or QUARTERLY_TOTAL variants | [MonthlyRevenueBreakdownBuilder.cs:57,63,82,89] |
| total_revenue | Computed | SUM of fee_amount (overdraft) or amount (credit), banker's rounded to 2dp | [MonthlyRevenueBreakdownBuilder.cs:58,64] |
| transaction_count | Computed | COUNT of qualifying events/transactions | [MonthlyRevenueBreakdownBuilder.cs:59,65] |
| as_of | __maxEffectiveDate | DateOnly from shared state | [MonthlyRevenueBreakdownBuilder.cs:60,66] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the CSV. Only the latest effective date's revenue breakdown persists.
- Multi-day gap-fill: intermediate days are overwritten.

## Edge Cases
- **October 31 quarterly logic**: The quarterly summary duplicates the daily values rather than accumulating across the quarter. This appears to be a bug or placeholder implementation.
- **No overdraft events**: If there are no overdraft events for a date, overdraft_fees row shows 0 revenue and 0 count.
- **No credit transactions**: Similarly, credit_interest_proxy shows 0.
- **Weekend overdraft data**: Overdraft events have data on all days including weekends (Oct 5, 6 present). Transactions also have weekend data. This job will produce non-empty output on weekends.
- **Unused customers table**: Sourced but never referenced.
- **Banker's rounding**: MidpointRounding.ToEven may produce different results than standard rounding for values exactly at .5 midpoints.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Overdraft fee summing (non-waived only) | MonthlyRevenueBreakdownBuilder.cs:26-33 |
| Credit transaction proxy | MonthlyRevenueBreakdownBuilder.cs:37-49 |
| Banker's rounding (W5) | MonthlyRevenueBreakdownBuilder.cs:58,64 |
| Oct 31 quarterly summary | MonthlyRevenueBreakdownBuilder.cs:72-94 |
| Quarterly values = daily values | MonthlyRevenueBreakdownBuilder.cs:75-78 |
| Customers sourced but unused | MonthlyRevenueBreakdownBuilder.cs (no reference) |
| TRAILER format | monthly_revenue_breakdown.json:36 |
| Overwrite write mode | monthly_revenue_breakdown.json:37 |
| First effective date 2024-10-01 | monthly_revenue_breakdown.json:3 |

## Open Questions
1. The quarterly summary rows duplicate the daily values instead of accumulating across the quarter. Is this intentional or a bug? (Confidence: HIGH — likely a bug or stub implementation)
2. Why are customers sourced if they are never used? (Confidence: LOW)
3. The job name says "monthly" but the output is daily with a quarterly boundary event. Is the naming misleading? (Confidence: MEDIUM)
