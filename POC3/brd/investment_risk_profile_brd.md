# InvestmentRiskProfile — Business Requirements Document

## Overview
Produces a per-investment risk classification that enriches each investment record with a computed `risk_tier` based on the investment's current value. The risk_tier is determined by hardcoded monetary thresholds, categorizing investments into "High Value", "Medium Value", or "Low Value" tiers. Note: despite the name, the risk_tier is based on value, not on the existing `risk_profile` field.

## Output Type
CsvFileWriter

## Writer Configuration
- **source**: `output`
- **outputFile**: `Output/curated/investment_risk_profile.csv`
- **includeHeader**: true
- **writeMode**: Overwrite
- **lineEnding**: LF
- **trailerFormat**: not specified (no trailer)

Evidence: [investment_risk_profile.json:24-31]

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.investments | investment_id, customer_id, account_type, current_value, risk_profile | None beyond effective date range | [investment_risk_profile.json:6-11] |
| datalake.customers | id, first_name, last_name | Sourced but NOT used by External module | [investment_risk_profile.json:13-18] |

### Table Schemas (from database)

**investments**: investment_id (integer), customer_id (integer), account_type (varchar), current_value (numeric), risk_profile (varchar), advisor_id (integer), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: Each investment row produces one output row. This is a 1:1 mapping with an additional computed `risk_tier` column.
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:24-60] — iterates `investments.Rows` and adds one row per investment

BR-2: **Risk tier classification** is based on `current_value` with hardcoded thresholds:
- `current_value > 250000` → "High Value"
- `current_value > 50000` → "Medium Value"
- `current_value <= 50000` → "Low Value"
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:39-45] with comment "AP7: Magic values — hardcoded thresholds for risk tier"

BR-3: **Asymmetric NULL handling**: NULL `current_value` defaults to 0 (decimal), but NULL `risk_profile` defaults to "Unknown" (string).
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:32-36] with comment "AP5: Asymmetric NULLs"

BR-4: If investments data is null or empty, an empty DataFrame is returned.
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:18-22]

BR-5: The `as_of` column in the output comes from each investment row's own `as_of` value, NOT from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:55] — `["as_of"] = row["as_of"]`

BR-6: Customers data is sourced by the job config but never referenced by the External module.
- Confidence: HIGH
- Evidence: [investment_risk_profile.json:13-18] sources customers, but [InvestmentRiskClassifier.cs] never accesses `sharedState["customers"]`

BR-7: The `account_type` field is null-coalesced to empty string.
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:30] — `row["account_type"]?.ToString() ?? ""`

BR-8: Data is sourced for the effective date range injected by the executor (no explicit date filters in the job config).
- Confidence: HIGH
- Evidence: [investment_risk_profile.json] — no minEffectiveDate/maxEffectiveDate in DataSourcing modules

BR-9: The risk_tier is based purely on `current_value`, NOT on the existing `risk_profile` field. The `risk_profile` is passed through unmodified.
- Confidence: HIGH
- Evidence: [InvestmentRiskClassifier.cs:39-45] — only references `currentValue` for tier computation

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| investment_id | investments.investment_id | Cast to int via `Convert.ToInt32` | [InvestmentRiskClassifier.cs:27] |
| customer_id | investments.customer_id | Cast to int via `Convert.ToInt32` | [InvestmentRiskClassifier.cs:28] |
| account_type | investments.account_type | Null coalesced to empty string | [InvestmentRiskClassifier.cs:30] |
| current_value | investments.current_value | NULL defaults to 0m (decimal) | [InvestmentRiskClassifier.cs:32-34] |
| risk_profile | investments.risk_profile | NULL defaults to "Unknown" | [InvestmentRiskClassifier.cs:36] |
| risk_tier | Computed from current_value | "High Value" (>250k), "Medium Value" (>50k), "Low Value" (<=50k) | [InvestmentRiskClassifier.cs:39-45] |
| as_of | investments.as_of (row-level) | Passed through as-is | [InvestmentRiskClassifier.cs:55] |

## Non-Deterministic Fields
None identified. All values are deterministic given the same input data and effective date.

## Write Mode Implications
- **Overwrite** mode: Each run replaces the entire CSV output file.
- For multi-day auto-advance runs, only the final effective date's output persists. Prior dates are overwritten.
- However, since as_of preserves per-row dates from source data, multi-day range outputs would contain rows with different as_of values.

## Edge Cases

1. **NULL current_value**: Defaults to 0m, which classifies as "Low Value". This is an asymmetric NULL handling choice — the code treats missing value as zero rather than raising an error. (HIGH confidence — [InvestmentRiskClassifier.cs:32-34])

2. **NULL risk_profile**: Defaults to "Unknown" string. This is different from the current_value NULL handling (which defaults to 0). (HIGH confidence — [InvestmentRiskClassifier.cs:36])

3. **Boundary values**: An investment with exactly 250000 is classified as "Medium Value" (not "High Value") because the threshold is strictly greater than. Similarly, exactly 50000 is "Low Value". (HIGH confidence — [InvestmentRiskClassifier.cs:40-41] — `> 250000` and `> 50000`)

4. **Customers sourced but unused**: The customers DataSourcing module runs but its data is never consumed by the External module. (HIGH confidence — code review)

5. **Weekend dates**: Investments table skips weekends in the datalake. If the effective date falls on a weekend, DataSourcing may return no data, resulting in an empty output. No weekend fallback logic exists in this module. (MEDIUM confidence — database observation)

6. **Multi-day effective date range**: Each investment row preserves its own `as_of` from source data. Rows from multiple dates would all appear in the output with their respective dates. (HIGH confidence — [InvestmentRiskClassifier.cs:55])

7. **Negative current_value**: Would be classified as "Low Value" (< 50000). No validation exists for negative values. (MEDIUM confidence — [InvestmentRiskClassifier.cs:39-45])

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| 1:1 investment mapping | [InvestmentRiskClassifier.cs:24-60] |
| Risk tier thresholds | [InvestmentRiskClassifier.cs:39-45] |
| Asymmetric NULL handling | [InvestmentRiskClassifier.cs:32-36] |
| Empty output on null/empty input | [InvestmentRiskClassifier.cs:18-22] |
| Row-level as_of preservation | [InvestmentRiskClassifier.cs:55] |
| CSV output with header, no trailer | [investment_risk_profile.json:24-31] |
| Unused customers source | [investment_risk_profile.json:13-18] |
| risk_tier independent of risk_profile | [InvestmentRiskClassifier.cs:39-45] |

## Open Questions

1. **Why are customers sourced?**: The job config sources the customers table, but the External module never uses it. This may be a leftover from a design that intended to include customer names in the output. (HIGH confidence they are unused)

2. **risk_tier vs. risk_profile**: The job is named "InvestmentRiskProfile" and the source data already contains a `risk_profile` field (values: Aggressive, Conservative, Moderate), but the computed `risk_tier` is based purely on dollar value, not on risk_profile. These are conceptually different dimensions — one is risk tolerance, the other is value size. The naming may be misleading. (MEDIUM confidence — naming observation)

3. **Hardcoded thresholds**: The 250000 and 50000 thresholds are magic numbers with no configurability. If these thresholds need to change, the External module code must be modified. (HIGH confidence — [InvestmentRiskClassifier.cs:40-41])
