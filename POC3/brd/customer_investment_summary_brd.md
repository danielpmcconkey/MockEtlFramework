# CustomerInvestmentSummary — Business Requirements Document

## Overview
Produces a per-customer summary of investment accounts, aggregating the count of investments and total portfolio value for each customer. Joins customer demographic data (name) with investment aggregates using an External module.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/customer_investment_summary.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not specified (no trailer)

Evidence: [customer_investment_summary.json:31-37]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.investments | investment_id, customer_id, account_type, current_value, advisor_id | None beyond effective date range | [customer_investment_summary.json:6-11] |
| datalake.customers | id, first_name, last_name, birthdate | None beyond effective date range | [customer_investment_summary.json:13-18] |
| datalake.securities | security_id, ticker, security_name, security_type, sector | Sourced but NOT used by External module | [customer_investment_summary.json:20-25] |

### Table Schemas (from database)

**investments**: investment_id (integer), customer_id (integer), account_type (varchar), current_value (numeric), risk_profile (varchar), advisor_id (integer), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

## Business Rules

BR-1: Investments are aggregated per customer_id. `investment_count` is the count of investment rows and `total_value` is the sum of `current_value` across all investments for that customer.
- Confidence: HIGH
- Evidence: [CustomerInvestmentSummaryBuilder.cs:39-49]

BR-2: If investments or customers data is null or empty, an empty DataFrame with the output schema is returned.
- Confidence: HIGH
- Evidence: [CustomerInvestmentSummaryBuilder.cs:19-23]

BR-3: `total_value` uses Banker's rounding (MidpointRounding.ToEven) to 2 decimal places.
- Confidence: HIGH
- Evidence: [CustomerInvestmentSummaryBuilder.cs:62] — `Math.Round(totalValue, 2, MidpointRounding.ToEven)` with explicit comment "W5: Banker's rounding"

BR-4: Customer name lookup is keyed by `customers.id` matching `investments.customer_id`. If no matching customer exists, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [CustomerInvestmentSummaryBuilder.cs:28-36,57-59]

BR-5: The `as_of` column in the output is set to `__maxEffectiveDate` from shared state, not from source data rows.
- Confidence: HIGH
- Evidence: [CustomerInvestmentSummaryBuilder.cs:25,71]

BR-6: Output rows iterate in dictionary insertion order of customer_id (order of first encounter in investments data). No explicit ORDER BY is applied.
- Confidence: MEDIUM
- Evidence: [CustomerInvestmentSummaryBuilder.cs:53] — iterates `customerAgg` dictionary

BR-7: Securities data is sourced by the job config but never referenced by the External module.
- Confidence: HIGH
- Evidence: [customer_investment_summary.json:20-25] sources securities, but [CustomerInvestmentSummaryBuilder.cs] never accesses `sharedState["securities"]`

BR-8: The `birthdate` column is sourced from customers but never used in the External module logic.
- Confidence: HIGH
- Evidence: [customer_investment_summary.json:17] includes birthdate, [CustomerInvestmentSummaryBuilder.cs] only uses id, first_name, last_name

BR-9: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [customer_investment_summary.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | investments.customer_id | Cast to int via `Convert.ToInt32` | [CustomerInvestmentSummaryBuilder.cs:42] |
| first_name | customers.first_name | Null coalesced to empty string | [CustomerInvestmentSummaryBuilder.cs:33] |
| last_name | customers.last_name | Null coalesced to empty string | [CustomerInvestmentSummaryBuilder.cs:34] |
| investment_count | COUNT of investments per customer_id | Integer count | [CustomerInvestmentSummaryBuilder.cs:49] |
| total_value | SUM(investments.current_value) per customer_id | Banker's rounding to 2 decimal places | [CustomerInvestmentSummaryBuilder.cs:43,62] |
| as_of | __maxEffectiveDate from shared state | DateOnly value | [CustomerInvestmentSummaryBuilder.cs:25,71] |

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date. Row order depends on dictionary insertion order, which is deterministic for the same input sequence.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire CSV output file.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- The `as_of` column records `__maxEffectiveDate`, so each run's output reflects only the last processed date.

## Edge Cases

1. **Customer with no investments**: Such customers do not appear in the output. The aggregation iterates investments, so only customers who have at least one investment row are included. (HIGH confidence — [CustomerInvestmentSummaryBuilder.cs:39-53])

2. **Investment with no matching customer**: The investment is still aggregated and appears in output with empty first_name and last_name. (HIGH confidence — [CustomerInvestmentSummaryBuilder.cs:57-59])

3. **Cross-date aggregation**: When the effective date range spans multiple days, all investment rows across dates are aggregated together per customer_id. This may inflate counts and totals if the same investment appears on multiple dates. (HIGH confidence — [CustomerInvestmentSummaryBuilder.cs:39-49] — no date filtering)

4. **NULL current_value**: `Convert.ToDecimal(null)` would throw an exception. No null guard exists for current_value. (MEDIUM confidence — [CustomerInvestmentSummaryBuilder.cs:43])

5. **Securities sourced but unused**: The securities DataSourcing module runs but its data is never consumed, adding unnecessary overhead. (HIGH confidence — code review of CustomerInvestmentSummaryBuilder.cs)

6. **Weekend dates**: Investments and customers tables skip weekends in the datalake. If the effective date falls on a weekend, DataSourcing may return no data, resulting in an empty output. (MEDIUM confidence — database observation)

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Per-customer aggregation | [CustomerInvestmentSummaryBuilder.cs:39-49] |
| Banker's rounding | [CustomerInvestmentSummaryBuilder.cs:62] |
| Empty output on null/empty input | [CustomerInvestmentSummaryBuilder.cs:19-23] |
| Customer name lookup | [CustomerInvestmentSummaryBuilder.cs:28-36] |
| as_of from __maxEffectiveDate | [CustomerInvestmentSummaryBuilder.cs:25,67] |
| CSV output with header, no trailer | [customer_investment_summary.json:31-37] |
| Unused securities source | [customer_investment_summary.json:20-25] vs [CustomerInvestmentSummaryBuilder.cs] |
| Unused birthdate column | [customer_investment_summary.json:17] vs [CustomerInvestmentSummaryBuilder.cs] |

## Open Questions

1. **Why are securities sourced?**: The job config sources the securities table, but the External module never references it. This may be a leftover from a prior version or an oversight. (HIGH confidence — clear from code review)

2. **Why is birthdate sourced?**: The customers DataSourcing includes `birthdate` but the External module only uses `id`, `first_name`, `last_name`. (HIGH confidence — clear from code review)

3. **Cross-date inflation**: With multi-day effective date ranges, the same investment may be counted multiple times (once per as_of date). This appears to be a latent bug rather than intentional behavior. (MEDIUM confidence)
