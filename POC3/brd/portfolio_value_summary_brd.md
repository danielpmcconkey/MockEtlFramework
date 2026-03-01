# PortfolioValueSummary — Business Requirements Document

## Overview
Produces a per-customer portfolio value summary by aggregating holdings data, computing the total portfolio value and holding count for each customer. Includes a weekend fallback mechanism that substitutes Friday's data when the effective date falls on Saturday or Sunday. Joins with customer data for name enrichment.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/portfolio_value_summary/`
- **numParts**: 1
- **writeMode**: Overwrite

Evidence: [portfolio_value_summary.json:32-38]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.investments | investment_id, customer_id, account_type, current_value, risk_profile | Sourced but NOT used by External module | [portfolio_value_summary.json:6-11] |
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, current_value | Filtered to targetDate (weekend fallback applied) | [portfolio_value_summary.json:13-18] |
| datalake.customers | id, first_name, last_name | Used for name lookup | [portfolio_value_summary.json:20-25] |

### Table Schemas (from database)

**investments**: investment_id (integer), customer_id (integer), account_type (varchar), current_value (numeric), risk_profile (varchar), advisor_id (integer), as_of (date)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: **Weekend fallback**: If `__maxEffectiveDate` falls on Saturday, the module uses Friday (maxDate - 1 day). If Sunday, it uses Friday (maxDate - 2 days). Weekday dates are used as-is.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:26-29] with comment "W2: Weekend fallback — use Friday's data on Sat/Sun"

BR-2: After determining the target date, holdings are filtered to only rows where `as_of == targetDate`.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:31-33] — `.Where(r => ((DateOnly)r["as_of"]) == targetDate)`

BR-3: Holdings are aggregated per customer_id. `total_portfolio_value` is the SUM of `current_value` and `holding_count` is the COUNT of holdings rows per customer.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:47-58]

BR-4: `total_portfolio_value` is rounded to 2 decimal places using default rounding.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:73] — `Math.Round(totalValue, 2)`

BR-5: If holdings or customers data is null or empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:19-23]

BR-6: Customer name lookup is keyed by `customers.id` matching `holdings.customer_id`. If no matching customer exists, first_name and last_name default to empty strings.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:36-44,66-68]

BR-7: The `as_of` column in the output is set to the `targetDate` (the possibly-adjusted weekend fallback date), NOT `__maxEffectiveDate` directly.
- Confidence: HIGH
- Evidence: [PortfolioValueCalculator.cs:75] — `["as_of"] = targetDate`

BR-8: Investments data is sourced by the job config but never referenced by the External module.
- Confidence: HIGH
- Evidence: [portfolio_value_summary.json:6-11] sources investments, but [PortfolioValueCalculator.cs] never accesses `sharedState["investments"]`

BR-9: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [portfolio_value_summary.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

BR-10: Output rows are ordered by dictionary insertion order (order of first encounter of customer_id in the filtered holdings). No explicit ORDER BY is applied.
- Confidence: MEDIUM
- Evidence: [PortfolioValueCalculator.cs:61] — iterates `customerTotals` dictionary

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | holdings.customer_id | Cast to int via `Convert.ToInt32` | [PortfolioValueCalculator.cs:50] |
| first_name | customers.first_name | Null coalesced to empty string via lookup | [PortfolioValueCalculator.cs:39,71] |
| last_name | customers.last_name | Null coalesced to empty string via lookup | [PortfolioValueCalculator.cs:40,72] |
| total_portfolio_value | SUM(holdings.current_value) per customer_id | Rounded to 2 decimal places; decimal arithmetic | [PortfolioValueCalculator.cs:51,73] |
| holding_count | COUNT of holdings rows per customer_id | Integer count | [PortfolioValueCalculator.cs:56,74] |
| as_of | targetDate (weekend-adjusted __maxEffectiveDate) | DateOnly value | [PortfolioValueCalculator.cs:27-29,75] |

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire Parquet output directory.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- The `as_of` column records the targetDate (weekend-adjusted), so Saturday/Sunday runs produce output with Friday's date.

## Edge Cases

1. **Saturday effective date**: The module falls back to Friday (maxDate - 1). If Friday data exists in holdings, it is used. If Friday is also missing (holiday), holdings would be empty for that as_of, resulting in an empty output. (HIGH confidence — [PortfolioValueCalculator.cs:28])

2. **Sunday effective date**: The module falls back to Friday (maxDate - 2). Same behavior as Saturday. (HIGH confidence — [PortfolioValueCalculator.cs:29])

3. **No holdings for targetDate**: If the filtered holdings produce zero rows (e.g., a holiday), the `customerTotals` dictionary is empty and the output DataFrame has zero rows. (HIGH confidence — [PortfolioValueCalculator.cs:47-58])

4. **Customer with no holdings**: Such customers do not appear in the output since iteration is over holdings, not customers. (HIGH confidence — [PortfolioValueCalculator.cs:47])

5. **Holdings with no matching customer**: The holding is still aggregated but appears with empty first_name and last_name. (HIGH confidence — [PortfolioValueCalculator.cs:66-68])

6. **Investments sourced but unused**: The investments DataSourcing module runs but its data is never consumed. (HIGH confidence — code review)

7. **Multi-day effective date range**: The DataSourcing returns rows for the full date range, but the External module filters to only `targetDate` rows. This means most of the sourced data is discarded. Only the last effective date (weekend-adjusted) is used. (HIGH confidence — [PortfolioValueCalculator.cs:31-33])

8. **NULL current_value in holdings**: `Convert.ToDecimal(null)` would throw an exception. No null guard exists. (MEDIUM confidence — [PortfolioValueCalculator.cs:51])

9. **Weekend data in holdings**: The holdings table in the datalake has no weekend data (only weekday dates). The weekend fallback to Friday aligns with this pattern. (MEDIUM confidence — database observation)

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Weekend fallback (Sat→Fri, Sun→Fri) | [PortfolioValueCalculator.cs:26-29] |
| Holdings filtered to targetDate | [PortfolioValueCalculator.cs:31-33] |
| Per-customer aggregation | [PortfolioValueCalculator.cs:47-58] |
| Total value rounding to 2dp | [PortfolioValueCalculator.cs:73] |
| Empty output on null/empty input | [PortfolioValueCalculator.cs:19-23] |
| Customer name lookup | [PortfolioValueCalculator.cs:36-44] |
| as_of from targetDate | [PortfolioValueCalculator.cs:75] |
| Parquet output with Overwrite | [portfolio_value_summary.json:32-38] |
| Unused investments source | [portfolio_value_summary.json:6-11] |

## Open Questions

1. **Why are investments sourced?**: The job config sources investments but the External module computes everything from holdings + customers. The investments table has its own `current_value` and `customer_id` which are not used. (HIGH confidence — clear from code review)

2. **Weekend fallback vs. Sunday skip**: This job uses weekend fallback (Friday data), while InvestmentAccountOverview uses Sunday skip (empty output). Different jobs have different weekend strategies, which may or may not be intentional. (MEDIUM confidence)

3. **Holiday handling**: The Friday fallback doesn't account for holidays. If Friday is a market holiday with no data, the output would be empty even though Thursday data may exist. (LOW confidence — no holiday logic in code)
