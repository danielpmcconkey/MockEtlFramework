# WeekendTransactionPattern — Business Requirements Document

## Overview
Classifies each day's transactions as "Weekday" or "Weekend" based on the day of week of the effective date, producing counts and amounts for each category. On Sundays, appends weekly summary rows aggregating the full Mon-Sun week. Uses a hardcoded date range for data sourcing rather than executor-injected dates.

## Output Type
CsvFileWriter (framework module, not direct file I/O)

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/weekend_transaction_pattern.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns | Filters | Evidence |
|-------|---------|---------|----------|
| datalake.transactions | transaction_id, account_id, txn_timestamp, txn_type, amount | **Hardcoded** minEffectiveDate=2024-10-01, maxEffectiveDate=2024-12-31 in the DataSourcing config | [weekend_transaction_pattern.json:10-12] |

Note: Unlike most jobs, this job specifies explicit `minEffectiveDate` and `maxEffectiveDate` in the DataSourcing module config rather than relying on executor-injected dates. This means it always sources the full Q4 2024 date range regardless of which effective date the executor is processing. This is annotated as AP10 ("over-sourced full date range").

## Business Rules

BR-1: The DataSourcing module uses hardcoded dates `minEffectiveDate: 2024-10-01` and `maxEffectiveDate: 2024-12-31`, sourcing the entire Q4 2024 range on every run.
- Confidence: HIGH
- Evidence: [weekend_transaction_pattern.json:11-12] `"minEffectiveDate": "2024-10-01", "maxEffectiveDate": "2024-12-31"`

BR-2: Despite sourcing the full date range, the External module filters to only process transactions where `as_of == maxDate` (the executor's `__maxEffectiveDate`).
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:37] `if (asOf != maxDate) continue;`

BR-3: Each day's transactions are classified as either "Weekday" (Monday-Friday) or "Weekend" (Saturday, Sunday) based on the `as_of` date's day of week.
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:41] `if (asOf.DayOfWeek == DayOfWeek.Saturday || asOf.DayOfWeek == DayOfWeek.Sunday)`

BR-4: Two rows are always output — "Weekday" and "Weekend" — even if one has zero transactions. When a row has zero count, `avg_amount` is 0.
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:55-71] Both rows are unconditionally added; [WeekendTransactionPatternProcessor.cs:60] `weekdayCount > 0 ? Math.Round(...) : 0m`

BR-5: The "Weekday" row is always first, "Weekend" row second.
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:55-71] Weekday added first, Weekend added second

BR-6: On **Sundays** (`maxDate.DayOfWeek == DayOfWeek.Sunday`), two additional rows are appended: `WEEKLY_TOTAL_Weekday` and `WEEKLY_TOTAL_Weekend`, aggregating the full Mon-Sun week (6 days back from Sunday).
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:74] `if (maxDate.DayOfWeek == DayOfWeek.Sunday)`; [WeekendTransactionPatternProcessor.cs:77] `var mondayOfWeek = maxDate.AddDays(-6);`

BR-7: The weekly summary aggregates all transactions in the range [Monday of that week, Sunday (maxDate)], classifying each by its `as_of` day of week.
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:87] `if (asOf < mondayOfWeek || asOf > maxDate) continue;`

BR-8: Amounts use `decimal` arithmetic with `Math.Round(..., 2)` for all computations.
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:29-31, 39, 45-50, 59-60, 69-70] All amount variables are `decimal`

BR-9: `as_of` in all output rows is set to the `__maxEffectiveDate` formatted as `yyyy-MM-dd`.
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:25] `var dateStr = maxDate.ToString("yyyy-MM-dd");`; used in all row constructions

BR-10: If the transactions DataFrame is null or empty, an empty output DataFrame is returned (no rows at all, not even Weekday/Weekend placeholders).
- Confidence: HIGH
- Evidence: [WeekendTransactionPatternProcessor.cs:19-23] Early return with empty DataFrame

BR-11: The trailer uses `{row_count}` (number of data rows in the output DataFrame) and `{date}` (__maxEffectiveDate). On non-Sundays, row_count = 2; on Sundays, row_count = 4.
- Confidence: HIGH
- Evidence: [weekend_transaction_pattern.json:24] trailerFormat; [WeekendTransactionPatternProcessor.cs:55-71, 103-119] Row construction logic

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| day_type | Derived from as_of day of week | `"Weekday"`, `"Weekend"`, `"WEEKLY_TOTAL_Weekday"`, or `"WEEKLY_TOTAL_Weekend"` | [WeekendTransactionPatternProcessor.cs:57, 65, 105, 113] |
| txn_count | transactions | Count per category | [WeekendTransactionPatternProcessor.cs:58, 66, 106, 114] |
| total_amount | transactions.amount | `Math.Round(SUM(decimal), 2)` per category | [WeekendTransactionPatternProcessor.cs:59, 67, 107, 115] |
| avg_amount | Computed | `Math.Round(total/count, 2)` or 0 if count is 0 | [WeekendTransactionPatternProcessor.cs:60, 68, 108, 116] |
| as_of | __maxEffectiveDate | Formatted as `yyyy-MM-dd` | [WeekendTransactionPatternProcessor.cs:61, 69, 109, 117] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite** mode: Each run completely replaces the CSV file. Only the latest effective date's results persist.
- On multi-day auto-advance, each day overwrites the previous. Only the final day's data survives.
- The trailer's `{date}` reflects `__maxEffectiveDate` for the run.

## Edge Cases

1. **Empty transactions**: Returns an empty DataFrame with zero rows. The CsvFileWriter writes only a header and trailer `TRAILER|0|{date}`. [WeekendTransactionPatternProcessor.cs:19-23]

2. **Weekday effective date**: Two output rows: "Weekday" (with data) and "Weekend" (with txn_count=0, total_amount=0, avg_amount=0). The classification is based on `as_of` date, not individual transaction timestamps.

3. **Saturday effective date**: Two output rows: "Weekday" (zero) and "Weekend" (with data). No weekly summary rows since it's not Sunday.

4. **Sunday effective date**: Four output rows: "Weekday" (zero), "Weekend" (today's data), "WEEKLY_TOTAL_Weekday" (Mon-Fri aggregate from sourced data), "WEEKLY_TOTAL_Weekend" (Sat-Sun aggregate from sourced data).

5. **Over-sourced data (AP10)**: The DataSourcing module loads ALL transactions from 2024-10-01 to 2024-12-31 on every run, but the processor only uses `maxDate` transactions for the daily rows and Monday-to-Sunday range for weekly summaries. This is wasteful but functionally correct because the filter in the code narrows the data.

6. **Week boundary**: The Monday of the week is calculated as `maxDate.AddDays(-6)`, which correctly gives Monday when maxDate is Sunday. The range is inclusive on both ends.

7. **First week (2024-10-06 is first Sunday)**: For Oct 6, Monday = Sep 30. But sourced data starts at Oct 1, so the Mon Sep 30 data would not be in the source. The weekly total for that first partial week would only include Oct 1-Oct 6 data.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Hardcoded date range 2024-10-01 to 2024-12-31 | [weekend_transaction_pattern.json:11-12] |
| Filter to maxDate only for daily rows | [WeekendTransactionPatternProcessor.cs:37] |
| Weekday/Weekend classification by DayOfWeek | [WeekendTransactionPatternProcessor.cs:41] |
| Two rows always output (Weekday + Weekend) | [WeekendTransactionPatternProcessor.cs:55-71] |
| Sunday weekly summary rows | [WeekendTransactionPatternProcessor.cs:74-119] |
| Monday = maxDate.AddDays(-6) | [WeekendTransactionPatternProcessor.cs:77] |
| Decimal arithmetic with rounding | [WeekendTransactionPatternProcessor.cs:29-31, 39, 45-50] |
| Zero-count avg_amount = 0 | [WeekendTransactionPatternProcessor.cs:60, 68] |
| Empty input returns empty DataFrame | [WeekendTransactionPatternProcessor.cs:19-23] |
| Overwrite write mode | [weekend_transaction_pattern.json:25] |
| TRAILER format | [weekend_transaction_pattern.json:24] |
| LF line ending | [weekend_transaction_pattern.json:26] |
| firstEffectiveDate = 2024-10-01 | [weekend_transaction_pattern.json:3] |

## Open Questions

1. **Over-sourced data range**: The hardcoded date range loads ~92 days of data on every run even though only one day (or one week on Sundays) is used. This is annotated as AP10 and may be intentional to ensure weekly summary availability, but it is inefficient.
- Confidence: MEDIUM (behavior is clearly defined; intent is inferred from the AP10 annotation)
