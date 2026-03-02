# WealthTierAnalysis -- Business Requirements Document

## Overview
Classifies customers into wealth tiers (Bronze, Silver, Gold, Platinum) based on their combined account balances and investment values, then aggregates statistics per tier including customer count, total wealth, average wealth, and percentage of customers. Output is a CSV with a trailer line.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/wealth_tier_analysis.csv`
- **includeHeader**: true
- **trailerFormat**: `TRAILER|{row_count}|{date}`
- **writeMode**: Overwrite
- **lineEnding**: LF

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.accounts | account_id, customer_id, current_balance | Effective date range (injected by executor) | [wealth_tier_analysis.json:8-10] |
| datalake.investments | investment_id, customer_id, current_value | Effective date range (injected by executor) | [wealth_tier_analysis.json:14-17] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [wealth_tier_analysis.json:20-23] |

## Business Rules

BR-1: Total wealth per customer is computed by summing all account balances (current_balance) and all investment values (current_value) across the entire effective date range.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:30-47] -- two loops accumulate `wealthByCustomer[custId]` from accounts and investments.

BR-2: Wealth tier thresholds are:
- Bronze: wealth < $10,000
- Silver: $10,000 <= wealth < $100,000
- Gold: $100,000 <= wealth < $500,000
- Platinum: wealth >= $500,000
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:59-65] -- explicit if/else chain with 10000m, 100000m, 500000m boundaries.

BR-3: Only customers who have at least one account or investment record appear in the wealth calculation. Customers with neither are not represented.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:58] -- `foreach (var kvp in wealthByCustomer)` only contains customers who had at least one account or investment row.

BR-4: The output always has exactly 4 rows (one per tier), even if a tier has 0 customers.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:50-56,74] -- pre-initialized with all four tiers, iterated in fixed order.

BR-5: Output tier order is fixed: Bronze, Silver, Gold, Platinum.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:74] -- `foreach (var tier in new[] { "Bronze", "Silver", "Gold", "Platinum" })`.

BR-6: `pct_of_customers` uses banker's rounding (MidpointRounding.ToEven) to 2 decimal places. The percentage is `(count / totalCustomers) * 100`.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:79-80] -- `Math.Round((decimal)count / totalCustomers * 100m, 2, MidpointRounding.ToEven)`.

BR-7: `total_wealth` and `avg_wealth` also use banker's rounding (MidpointRounding.ToEven) to 2 decimal places.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:87-88] -- `Math.Round(totalWealth, 2, MidpointRounding.ToEven)` and `Math.Round(avgWealth, 2, MidpointRounding.ToEven)`.

BR-8: If a tier has 0 customers, avg_wealth is 0 (guarded by `count > 0 ? totalWealth / count : 0m`).
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:77] -- division guard.

BR-9: The `as_of` column is set to `__maxEffectiveDate` from shared state.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:26,90] -- `maxDate = (DateOnly)sharedState["__maxEffectiveDate"]`.

BR-10: When the customers DataFrame is null or empty, the output is an empty DataFrame. Note: the customers table is used only for the empty guard -- it is NOT used in the wealth calculation itself.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:20-24] -- null/empty guard on customers. [WealthTierAnalyzer.cs:30-47] -- wealth calculation uses accounts and investments only.

BR-11: `totalCustomers` is the count of distinct customers with wealth data (from wealthByCustomer dictionary), NOT the total from the customers table.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:71] -- `var totalCustomers = wealthByCustomer.Count`.

BR-12: The customers table's first_name and last_name columns are sourced but NOT used in the output or calculation.
- Confidence: HIGH
- Evidence: [WealthTierAnalyzer.cs:18] -- customers retrieved but only used for empty guard. Output columns do not include name fields.

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| wealth_tier | Computed | "Bronze", "Silver", "Gold", or "Platinum" based on thresholds | [WealthTierAnalyzer.cs:59-65,84] |
| customer_count | Computed | Count of customers in this tier | [WealthTierAnalyzer.cs:85] |
| total_wealth | Computed | Sum of all wealth in this tier, banker's rounded to 2 decimals | [WealthTierAnalyzer.cs:87] |
| avg_wealth | Computed | total_wealth / customer_count (or 0 if count is 0), banker's rounded to 2 decimals | [WealthTierAnalyzer.cs:77,88] |
| pct_of_customers | Computed | (customer_count / totalCustomers) * 100, banker's rounded to 2 decimals | [WealthTierAnalyzer.cs:79-80,89] |
| as_of | __maxEffectiveDate | From shared state | [WealthTierAnalyzer.cs:26,90] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite**: Each execution replaces the entire output file. For multi-day auto-advance runs, only the last effective date's output survives on disk.

## Edge Cases
- **Empty tier**: Gets customer_count=0, total_wealth=0, avg_wealth=0, pct_of_customers=0. Still appears in output.
- **Customer with negative balances**: Could reduce total wealth, potentially shifting tier assignment. No floor is applied.
- **Customer with accounts but no investments (or vice versa)**: Their wealth is based on whichever data exists.
- **Customers table for guard only**: The empty guard checks customers, but the wealth calculation is driven entirely by accounts and investments. A customer with no accounts or investments would pass the guard but not appear in wealth calculations.
- **Banker's rounding for all fields**: MidpointRounding.ToEven is used consistently for pct_of_customers, total_wealth, and avg_wealth. With banker's rounding, 0.5 rounds to 0, 1.5 rounds to 2 (rounds to even).
- **Trailer line**: Contains row_count (always 4 in normal execution) and date (from __maxEffectiveDate).

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Wealth = accounts + investments | [WealthTierAnalyzer.cs:30-47] |
| Tier thresholds | [WealthTierAnalyzer.cs:59-65] |
| Fixed 4-row output | [WealthTierAnalyzer.cs:50-56,74] |
| Banker's rounding | [WealthTierAnalyzer.cs:79-80,87-88] |
| as_of from __maxEffectiveDate | [WealthTierAnalyzer.cs:26,90] |
| Empty guard on customers | [WealthTierAnalyzer.cs:20-24] |
| totalCustomers from wealth data | [WealthTierAnalyzer.cs:71] |
| Trailer format | [wealth_tier_analysis.json:36] |
| LF line endings | [wealth_tier_analysis.json:38] |
| Overwrite mode | [wealth_tier_analysis.json:37] |

## Open Questions
- OQ-1: The customers table is sourced (with first_name, last_name) but is only used for the empty guard. The wealth calculation is driven entirely by accounts and investments. A customer in the customers table with no accounts or investments would not affect the output. Whether this is the intended behavior is unclear. Confidence: MEDIUM.
