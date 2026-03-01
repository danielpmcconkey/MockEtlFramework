# InvestmentAccountOverview — Business Requirements Document

## Overview
Produces a denormalized view of investment accounts enriched with customer names (first_name, last_name) from the customers table. Each investment row is output with its associated customer information. The External module includes a Sunday skip behavior that returns an empty DataFrame on Sundays.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/investment_account_overview.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

Evidence: [investment_account_overview.json:24-32]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.investments | investment_id, customer_id, account_type, current_value, risk_profile, advisor_id | None beyond effective date range | [investment_account_overview.json:6-11] |
| datalake.customers | id, prefix, first_name, last_name, suffix | Used for name lookup; only first_name and last_name are output | [investment_account_overview.json:13-18] |

### Table Schemas (from database)

**investments**: investment_id (integer), customer_id (integer), account_type (varchar), current_value (numeric), risk_profile (varchar), advisor_id (integer), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: **Sunday skip**: If `__maxEffectiveDate` falls on a Sunday, the External module returns an empty DataFrame. No data is processed or output.
- Confidence: HIGH
- Evidence: [InvestmentAccountOverviewBuilder.cs:20-28] — `if (maxDate.DayOfWeek == DayOfWeek.Sunday)` with comment "W1: Sunday skip"

BR-2: If investments or customers data is null or empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [InvestmentAccountOverviewBuilder.cs:30-34]

BR-3: Customer name lookup is keyed by `customers.id` matching `investments.customer_id`. If no matching customer exists, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [InvestmentAccountOverviewBuilder.cs:37-45,51-53]

BR-4: Each investment row produces one output row. This is a 1:1 mapping from investments, not an aggregation.
- Confidence: HIGH
- Evidence: [InvestmentAccountOverviewBuilder.cs:48-68] — iterates `investments.Rows` and adds one row per investment

BR-5: The `as_of` column in the output comes from each investment row's own `as_of` value, NOT from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [InvestmentAccountOverviewBuilder.cs:64] — `["as_of"] = row["as_of"]`

BR-6: The `current_value` is passed through as-is via `Convert.ToDecimal`. No rounding is applied.
- Confidence: HIGH
- Evidence: [InvestmentAccountOverviewBuilder.cs:62] — `Convert.ToDecimal(row["current_value"])`

BR-7: The `prefix` and `suffix` columns are sourced from customers but NOT included in the output schema.
- Confidence: HIGH
- Evidence: [investment_account_overview.json:17] sources prefix/suffix; [InvestmentAccountOverviewBuilder.cs:10-14] output columns do not include them

BR-8: The `advisor_id` column is sourced from investments but NOT included in the output schema.
- Confidence: HIGH
- Evidence: [investment_account_overview.json:11] includes advisor_id; [InvestmentAccountOverviewBuilder.cs:10-14] output columns do not include it

BR-9: The trailer format uses `{row_count}` (number of data rows) and `{date}` (the `__maxEffectiveDate`). This is handled by the framework's CsvFileWriter.
- Confidence: HIGH
- Evidence: [investment_account_overview.json:29] and Architecture.md CsvFileWriter trailer documentation

BR-10: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [investment_account_overview.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| investment_id | investments.investment_id | Cast to int via `Convert.ToInt32` | [InvestmentAccountOverviewBuilder.cs:57] |
| customer_id | investments.customer_id | Cast to int via `Convert.ToInt32` | [InvestmentAccountOverviewBuilder.cs:50,58] |
| first_name | customers.first_name | Null coalesced to empty string via lookup | [InvestmentAccountOverviewBuilder.cs:42,59] |
| last_name | customers.last_name | Null coalesced to empty string via lookup | [InvestmentAccountOverviewBuilder.cs:43,60] |
| account_type | investments.account_type | Null coalesced to empty string | [InvestmentAccountOverviewBuilder.cs:61] |
| current_value | investments.current_value | Cast to decimal, no rounding | [InvestmentAccountOverviewBuilder.cs:62] |
| risk_profile | investments.risk_profile | Null coalesced to empty string | [InvestmentAccountOverviewBuilder.cs:63] |
| as_of | investments.as_of (row-level) | Passed through as-is | [InvestmentAccountOverviewBuilder.cs:64] |

### Trailer Row
Format: `TRAILER|{row_count}|{date}` — handled by CsvFileWriter framework module.

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire CSV output file.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- Note: On Sundays, the output DataFrame is empty, so the file would be overwritten with just a header and trailer.

## Edge Cases

1. **Sunday effective date**: The External module returns an empty DataFrame on Sundays. The CsvFileWriter will write a header-only file with a trailer showing 0 row count. (HIGH confidence — [InvestmentAccountOverviewBuilder.cs:20-28])

2. **Saturday effective date**: No special handling for Saturdays. Data is processed normally, but since investments/customers tables skip weekends in the datalake, DataSourcing may return no rows for Saturday dates. (MEDIUM confidence — database observation shows weekday-only data)

3. **Investment with no matching customer**: The investment row still appears in output with empty first_name and last_name. (HIGH confidence — [InvestmentAccountOverviewBuilder.cs:51-53])

4. **Customer with no investments**: Such customers do not appear in the output since iteration is over investments, not customers. (HIGH confidence — [InvestmentAccountOverviewBuilder.cs:48])

5. **NULL current_value**: `Convert.ToDecimal(null)` would throw an exception. No null guard exists. (MEDIUM confidence — [InvestmentAccountOverviewBuilder.cs:62])

6. **Multi-day effective date range**: When spanning multiple dates, each investment row preserves its own `as_of` from the source data. Unlike other jobs that use `__maxEffectiveDate` for as_of, this job preserves per-row dates. (HIGH confidence — [InvestmentAccountOverviewBuilder.cs:64])

7. **Prefix/suffix/advisor_id sourced but unused**: These columns are pulled from the database but not included in output. (HIGH confidence — schema comparison)

8. **__maxEffectiveDate fallback**: If `__maxEffectiveDate` is not in shared state, the code falls back to `DateOnly.FromDateTime(DateTime.Today)`. This is a defensive pattern. (HIGH confidence — [InvestmentAccountOverviewBuilder.cs:20-22])

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Sunday skip | [InvestmentAccountOverviewBuilder.cs:20-28] |
| Empty output on null/empty input | [InvestmentAccountOverviewBuilder.cs:30-34] |
| Customer name lookup | [InvestmentAccountOverviewBuilder.cs:37-45] |
| 1:1 investment-to-output mapping | [InvestmentAccountOverviewBuilder.cs:48-68] |
| Row-level as_of preservation | [InvestmentAccountOverviewBuilder.cs:64] |
| No rounding on current_value | [InvestmentAccountOverviewBuilder.cs:62] |
| CsvFileWriter with trailer | [investment_account_overview.json:24-32] |
| Unused columns (prefix, suffix, advisor_id) | [investment_account_overview.json:11,17] |

## Open Questions

1. **Sunday skip vs. Saturday**: The job skips Sundays but not Saturdays. This is asymmetric. Since the datalake has no weekend data for investments/customers, Saturday runs would likely produce empty output regardless. It's unclear why only Sunday is explicitly skipped. (MEDIUM confidence)

2. **Unused sourced columns**: prefix, suffix, and advisor_id are sourced but not used. These may have been intended for a richer output format or are leftovers from an earlier design. (HIGH confidence they are unused)
