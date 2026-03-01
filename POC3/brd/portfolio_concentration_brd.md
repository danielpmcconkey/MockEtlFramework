# PortfolioConcentration — Business Requirements Document

## Overview
Computes sector concentration percentages for each customer's portfolio, showing what fraction of a customer's total holdings value is in each sector per investment account. Contains two significant computational bugs: it uses `double` arithmetic (introducing floating-point epsilon errors) and truncates values to integers before computing percentages (producing mostly 0% results via integer division).

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/portfolio_concentration/`
- **numParts**: 1
- **writeMode**: Overwrite

Evidence: [portfolio_concentration.json:32-38]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.holdings | holding_id, investment_id, security_id, customer_id, quantity, current_value | None beyond effective date range | [portfolio_concentration.json:6-11] |
| datalake.securities | security_id, ticker, security_name, security_type, sector | Used for sector lookup | [portfolio_concentration.json:13-18] |
| datalake.investments | investment_id, customer_id, account_type, current_value | Sourced but NOT used by External module | [portfolio_concentration.json:20-25] |

### Table Schemas (from database)

**holdings**: holding_id (integer), investment_id (integer), security_id (integer), customer_id (integer), quantity (numeric), cost_basis (numeric), current_value (numeric), as_of (date)

**securities**: security_id (integer), ticker (varchar), security_name (varchar), security_type (varchar), sector (varchar), exchange (varchar), as_of (date)

**investments**: investment_id (integer), customer_id (integer), account_type (varchar), current_value (numeric), risk_profile (varchar), advisor_id (integer), as_of (date)

## Business Rules

BR-1: The module computes sector concentration per (customer_id, investment_id, sector) tuple. Holdings are grouped by this composite key.
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:51-64] — `sectorValues` dictionary keyed by `(customerId, investmentId, sector)`

BR-2: Total portfolio value (`total_value`) is computed per customer_id across ALL of that customer's holdings (regardless of investment or sector).
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:38-48] — `customerTotalValue` dictionary keyed by `customerId`

BR-3: Sector lookup maps security_id to sector. Unknown security_ids default to `"Unknown"` sector.
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:29-34,57]

BR-4: **BUG — Double arithmetic**: Values are accumulated using `double` instead of `decimal`, introducing floating-point epsilon errors in value sums.
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:43,44] — `double value = Convert.ToDouble(row["current_value"]);` with comment "W6: Double arithmetic for accumulation (epsilon errors)"

BR-5: **BUG — Integer division for percentage**: The sector percentage is computed by truncating both sector_value and total_value to integers, then dividing. Since `int / int` produces integer division in C#, the result is almost always 0 (the percentage rounds down to 0).
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:75-77] — `int sectorInt = (int)sectorValue; int totalInt = (int)totalValue; decimal sectorPct = (decimal)(sectorInt / totalInt);` with comment "W4: Integer division for percentage"

BR-6: If holdings or securities data is null or empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:20-24]

BR-7: The `as_of` column in the output is set to `__maxEffectiveDate` from shared state, not from source data rows.
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:26,86]

BR-8: Investments data is sourced by the job config but never referenced by the External module.
- Confidence: HIGH
- Evidence: [portfolio_concentration.json:20-25] sources investments, but [PortfolioConcentrationCalculator.cs] never accesses `sharedState["investments"]`

BR-9: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [portfolio_concentration.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

BR-10: Null sector values default to `"Unknown"`.
- Confidence: HIGH
- Evidence: [PortfolioConcentrationCalculator.cs:33] — `secRow["sector"]?.ToString() ?? "Unknown"`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | holdings.customer_id | Cast to int | [PortfolioConcentrationCalculator.cs:53,80] |
| investment_id | holdings.investment_id | Cast to int | [PortfolioConcentrationCalculator.cs:54,81] |
| sector | securities.sector (via security_id lookup) | Null coalesced to "Unknown" | [PortfolioConcentrationCalculator.cs:33,57,82] |
| sector_value | SUM(holdings.current_value) per (customer, investment, sector) | Accumulated as double (epsilon errors) | [PortfolioConcentrationCalculator.cs:62-63,83] |
| total_value | SUM(holdings.current_value) per customer | Accumulated as double (epsilon errors) | [PortfolioConcentrationCalculator.cs:46-47,84] |
| sector_pct | sector_value / total_value | **BUG**: Integer division produces 0 in most cases | [PortfolioConcentrationCalculator.cs:75-77,85] |
| as_of | __maxEffectiveDate from shared state | DateOnly value | [PortfolioConcentrationCalculator.cs:26,86] |

## Non-Deterministic Fields
None identified. Although double arithmetic introduces epsilon errors, these are deterministic for the same input data on the same platform.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire Parquet output directory.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- The `as_of` column records `__maxEffectiveDate`, so each run's output reflects only the last processed date.

## Edge Cases

1. **Integer division produces 0%**: For sector_pct, values are truncated to int before division. For example, if sector_value = 15000.50 and total_value = 100000.75, the computation is `(int)15000 / (int)100000 = 0` (integer division). Only when sector_value >= total_value would the result be non-zero (i.e., 1 = 100%). (HIGH confidence — [PortfolioConcentrationCalculator.cs:75-77])

2. **Double precision errors**: Using `double` instead of `decimal` for financial accumulation introduces floating-point representation errors. For example, repeated additions of values like 4680.72 may accumulate small epsilon errors. (HIGH confidence — [PortfolioConcentrationCalculator.cs:43-47])

3. **Customer with total_value of 0**: If `totalInt` is 0, `sectorInt / totalInt` would throw a `DivideByZeroException`. This could happen if all holdings have current_value between 0 and 1 (which truncate to 0). (MEDIUM confidence — [PortfolioConcentrationCalculator.cs:77])

4. **Holdings with no securities match**: Holdings whose security_id has no match in securities are grouped under "Unknown" sector. (HIGH confidence — [PortfolioConcentrationCalculator.cs:57])

5. **Investments sourced but unused**: The investments DataSourcing module runs but its data is never consumed. (HIGH confidence — code review)

6. **Cross-date aggregation**: When the effective date range spans multiple days, all holdings and securities rows are processed together. The sector lookup dictionary overwrites per security_id, keeping only the last-seen mapping. Holdings from multiple dates may be summed together. (HIGH confidence — no date filtering in aggregation)

7. **sector_value and total_value stored as double**: The Parquet output will contain double-precision floating-point values, not exact decimal values. This is a data quality concern for financial data. (HIGH confidence — [PortfolioConcentrationCalculator.cs:83-84])

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Sector concentration per (customer, investment, sector) | [PortfolioConcentrationCalculator.cs:51-64] |
| Customer total value aggregation | [PortfolioConcentrationCalculator.cs:38-48] |
| Sector lookup with Unknown default | [PortfolioConcentrationCalculator.cs:29-34] |
| Double arithmetic bug | [PortfolioConcentrationCalculator.cs:43-44] |
| Integer division bug | [PortfolioConcentrationCalculator.cs:75-77] |
| Empty output on null/empty input | [PortfolioConcentrationCalculator.cs:20-24] |
| as_of from __maxEffectiveDate | [PortfolioConcentrationCalculator.cs:26,86] |
| Parquet output with Overwrite | [portfolio_concentration.json:32-38] |
| Unused investments source | [portfolio_concentration.json:20-25] |

## Open Questions

1. **Integer division intentionality**: The comment "W4" marks this as known, suggesting the integer division is a deliberate bug planted for testing purposes. In a production context, this would produce incorrect sector_pct values (almost always 0). The correct computation would be `(decimal)sectorValue / (decimal)totalValue`. (HIGH confidence it's a bug)

2. **Double vs. decimal**: The comment "W6" marks the double arithmetic as known. Using `decimal` would provide exact financial arithmetic. (HIGH confidence it's intentional for testing)

3. **Why are investments sourced?**: The job config sources investments but the External module never uses it. The module computes everything from holdings + securities. (HIGH confidence — clear from code review)

4. **Division by zero risk**: If a customer's total holdings value truncates to 0 after int cast, the computation would throw. This may not occur with current data but is a latent risk. (MEDIUM confidence)
