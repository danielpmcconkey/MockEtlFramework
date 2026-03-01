# WireTransferDaily — Business Requirements Document

## Overview
Aggregates wire transfer activity by date, producing daily counts, totals, and averages. Appends a special "MONTHLY_TOTAL" summary row when the effective date falls on the last day of a month. Output is a Parquet file per effective date.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/wire_transfer_daily/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.wire_transfers | wire_id, customer_id, account_id, direction, amount, wire_timestamp, status | Effective date range (injected by executor) | [wire_transfer_daily.json:8-12] |
| datalake.accounts | account_id, customer_id, account_type | Effective date range (injected by executor) | [wire_transfer_daily.json:14-18] |

### Table Schemas (from database)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar: Inbound/Outbound), amount (numeric, range ~1012-49959), counterparty_name (varchar), counterparty_bank (varchar), status (varchar: Completed/Pending/Rejected), wire_timestamp (timestamp), as_of (date). ~35-62 rows per as_of date.

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date).

## Business Rules

BR-1: Wire transfers are grouped by `as_of` date (used as `wire_date`). All statuses and directions are included — no filtering.
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:31-44] — groups by `row["as_of"]`, no status/direction check

BR-2: Per-date aggregations:
  - `wire_count` = count of wires on that date
  - `total_amount` = SUM(amount), rounded to 2 decimal places
  - `avg_amount` = total_amount / wire_count, rounded to 2 decimal places
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:49-52]

BR-3: The `wire_date` column is set to the `as_of` value from the grouped data. The `as_of` output column is also set to the same value (identical to wire_date).
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:55-56] — `["wire_date"] = wireDate` and `["as_of"] = wireDate`

BR-4: When `__maxEffectiveDate` falls on the last day of a month, a special "MONTHLY_TOTAL" summary row is appended.
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:65] — `if (maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month))`

BR-5: The MONTHLY_TOTAL row contains:
  - `wire_date` = "MONTHLY_TOTAL" (string, not a date)
  - `wire_count` = sum of all daily counts
  - `total_amount` = sum of all daily totals, rounded to 2dp
  - `avg_amount` = total_amount / total_wire_count, rounded to 2dp
  - `as_of` = maxDate (the effective date, as DateOnly)
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:67-77]

BR-6: The `accounts` table is sourced but NOT used by the External module.
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs] — no reference to `accounts` DataFrame; only `wire_transfers` is retrieved (line 15)

BR-7: Empty output (zero-row DataFrame with correct schema) is produced if `wire_transfers` is null or empty. No MONTHLY_TOTAL row is appended when input is empty.
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:21-25] — returns before the monthly total check

BR-8: Null `as_of` values in wire_transfers rows are silently skipped.
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:37] — `if (asOf == null) continue;`

BR-9: The `__maxEffectiveDate` is read from shared state; if absent, falls back to today's date.
- Confidence: HIGH
- Evidence: [WireTransferDailyProcessor.cs:17-19] — conditional with `DateOnly.FromDateTime(DateTime.Today)` fallback

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| wire_date | wire_transfers.as_of | Group key (object type); "MONTHLY_TOTAL" for summary row | [WireTransferDailyProcessor.cs:55, 72] |
| wire_count | Computed | COUNT per as_of date; sum of all counts for MONTHLY_TOTAL | [WireTransferDailyProcessor.cs:50, 67] |
| total_amount | Computed | SUM(amount) ROUND 2dp; sum of all totals for MONTHLY_TOTAL | [WireTransferDailyProcessor.cs:51, 68] |
| avg_amount | Computed | total/count ROUND 2dp; overall avg for MONTHLY_TOTAL | [WireTransferDailyProcessor.cs:52, 75] |
| as_of | wire_transfers.as_of | Same as wire_date for daily rows; maxDate for MONTHLY_TOTAL | [WireTransferDailyProcessor.cs:56, 76] |

## Non-Deterministic Fields
- The `__maxEffectiveDate` fallback uses `DateTime.Today`, which varies by execution date. However, in normal operation the executor injects the effective date.

## Write Mode Implications
**Overwrite** mode: Each effective date run replaces the entire output directory. In multi-day gap-fill scenarios, only the last day's output survives. The MONTHLY_TOTAL row only appears if the final effective date in the gap-fill is a month-end date.

## Edge Cases

1. **Month-end detection**: Uses `DateTime.DaysInMonth` to check if maxDate is the last day. This correctly handles variable-length months (28/29/30/31 days) and leap years.
   - Evidence: [WireTransferDailyProcessor.cs:65]

2. **MONTHLY_TOTAL wire_date is a string**: The `wire_date` column contains date objects for daily rows but the string "MONTHLY_TOTAL" for the summary row. This creates a mixed-type column in the Parquet output.
   - Evidence: [WireTransferDailyProcessor.cs:72] — `["wire_date"] = "MONTHLY_TOTAL"`

3. **MONTHLY_TOTAL counts all daily groups**: The summary uses `dailyGroups.Values.Sum(...)`, which sums across all dates in the current effective range, not just the current month. If the range spans multiple months, the total covers all of them.
   - Evidence: [WireTransferDailyProcessor.cs:67-68]

4. **Accounts unused**: The `accounts` DataFrame is loaded but never referenced.
   - Evidence: [wire_transfer_daily.json:14-18]; [WireTransferDailyProcessor.cs] — not accessed

5. **Single-day vs multi-day ranges**: With daily gap-fill and Overwrite mode, the typical run has a single-day range. The MONTHLY_TOTAL would only appear on actual month-end dates (Oct 31, Nov 30, Dec 31 in the Q4 2024 range).
   - Evidence: Architecture.md — executor gap-fills one day at a time

6. **Dictionary iteration order**: `dailyGroups` is a `Dictionary<object, ...>`. Output row order depends on dictionary iteration order, which is not guaranteed to be sorted by date.
   - Evidence: [WireTransferDailyProcessor.cs:47] — `foreach (var kvp in dailyGroups)`

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Group by as_of, no filters | [WireTransferDailyProcessor.cs:31-44] |
| BR-2: Count, sum, avg aggregations | [WireTransferDailyProcessor.cs:49-52] |
| BR-3: wire_date = as_of | [WireTransferDailyProcessor.cs:55-56] |
| BR-4: Month-end MONTHLY_TOTAL | [WireTransferDailyProcessor.cs:65] |
| BR-5: MONTHLY_TOTAL contents | [WireTransferDailyProcessor.cs:67-77] |
| BR-6: Accounts unused | [WireTransferDailyProcessor.cs] |
| BR-7: Empty output guard | [WireTransferDailyProcessor.cs:21-25] |
| BR-8: Null as_of skip | [WireTransferDailyProcessor.cs:37] |
| BR-9: maxEffectiveDate fallback | [WireTransferDailyProcessor.cs:17-19] |
| Output: Parquet, 1 part, Overwrite | [wire_transfer_daily.json:24-29] |

## Open Questions

1. **Why is accounts sourced?** The `accounts` DataFrame is loaded but never used. Possibly a leftover from a version that filtered by account type or joined to account data.
   - Confidence: HIGH — code clearly shows it is unused

2. **Mixed-type wire_date column**: The column contains both date objects and the string "MONTHLY_TOTAL". This may cause issues for downstream consumers expecting a homogeneous date column. Is this the intended design?
   - Confidence: HIGH — behavior is explicit in code but unusual for a typed column

3. **MONTHLY_TOTAL with single-day ranges**: With daily gap-fill, each run has a single-date range, so the MONTHLY_TOTAL row on month-end dates will only aggregate that single day's data. If multi-day totals are desired, the effective range would need to span the full month.
   - Confidence: MEDIUM — depends on how the executor manages date ranges
