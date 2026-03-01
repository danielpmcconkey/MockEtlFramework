# RegulatoryExposureSummary — Business Requirements Document

## Overview
Calculates a regulatory exposure score per customer based on compliance event counts, wire transfer counts, account counts, and total account balances. Produces one row per customer with a composite exposure score. Includes weekend fallback logic.

## Output Type
ParquetFileWriter

## Writer Configuration
- **outputDirectory**: `Output/curated/regulatory_exposure_summary/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.compliance_events | event_id, customer_id, event_type, status | Effective date range via executor; counted per customer_id (all rows, no date filter within module) | [regulatory_exposure_summary.json:4-11] |
| datalake.wire_transfers | wire_id, customer_id, amount, direction | Effective date range via executor; counted per customer_id (all rows, no date filter within module) | [regulatory_exposure_summary.json:12-19] |
| datalake.accounts | account_id, customer_id, current_balance | Effective date range via executor; counted and summed per customer_id (all rows, no date filter within module) | [regulatory_exposure_summary.json:20-27] |
| datalake.customers | id, first_name, last_name | Effective date range via executor; filtered to target date (with weekend fallback) | [regulatory_exposure_summary.json:28-35] |

### Source Table Schemas (from database)

**compliance_events**: event_id (integer), customer_id (integer), event_type (varchar), event_date (date), status (varchar), review_date (date), as_of (date)

**wire_transfers**: wire_id (integer), customer_id (integer), account_id (integer), direction (varchar), amount (numeric), counterparty_name (varchar), counterparty_bank (varchar), status (varchar), wire_timestamp (timestamp), as_of (date)

**accounts**: account_id (integer), customer_id (integer), account_type (varchar), account_status (varchar), open_date (date), current_balance (numeric), interest_rate (numeric), credit_limit (numeric), apr (numeric), as_of (date)

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date)

## Business Rules

BR-1: Weekend fallback — on Saturday, the target date is shifted back 1 day (to Friday). On Sunday, the target date is shifted back 2 days (to Friday). Weekday dates are used as-is.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:29-32] — `DayOfWeek.Saturday => AddDays(-1)`, `DayOfWeek.Sunday => AddDays(-2)`

BR-2: Only customers whose `as_of` matches the target date (after weekend fallback) are included in the output. If no customers match the target date, ALL customer rows are used as a fallback.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:38-43] — filters by targetDate, falls back to all rows if count is 0

BR-3: Compliance events, wire transfers, and account aggregations use ALL rows from their respective DataFrames (no date filtering within the module). This means counts may be inflated if multiple as_of dates are present.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:46-55, 58-68, 72-89] — no `.Where()` filter on as_of for these DataFrames

BR-4: Exposure score formula: `(compliance_events * 30) + (wire_count * 20) + (total_balance / 10000)`, using **decimal** arithmetic, rounded to 2 decimal places.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:105-106] — `(complianceCount * 30.0m) + (wireCount * 20.0m) + (totalBalance / 10000.0m)` with `Math.Round(..., 2)`

BR-5: Unlike CustomerComplianceRisk (which uses double arithmetic), this job uses decimal arithmetic. Both jobs use banker's rounding (MidpointRounding.ToEven) — CustomerComplianceRisk specifies it explicitly, while this job uses it implicitly via the `Math.Round(decimal, int)` default. The key difference between the two jobs is decimal vs double arithmetic, not the rounding mode.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:105-106] — `30.0m`, `20.0m`, `10000.0m` suffixes are decimal; `Math.Round(value, 2)` defaults to `MidpointRounding.ToEven` in C#

BR-6: Total balance per customer is rounded to 2 decimal places using banker's rounding (implicit MidpointRounding.ToEven default) before being stored in output.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:114] — `Math.Round(totalBalance, 2)` defaults to `MidpointRounding.ToEven`

BR-7: Account count per customer is the raw count of account rows (all as_of dates).
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:80-82] — `accountCountByCustomer[customerId]++`

BR-8: One output row is produced per customer in the target date-filtered customer list. Customers with no compliance events, wires, or accounts get counts of 0 and exposure_score = 0.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:93-94] — `GetValueOrDefault(..., 0)`

BR-9: If the customers DataFrame is null or empty, an empty output DataFrame is produced.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:22-26]

BR-10: The output `as_of` column is set to the target date (after weekend fallback), applied uniformly to all rows.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:118] — `["as_of"] = targetDate`

BR-11: NULL first_name or last_name values are coalesced to empty string.
- Confidence: HIGH
- Evidence: [RegulatoryExposureCalculator.cs:96-97] — `?.ToString() ?? ""`

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| customer_id | customers.id | Convert.ToInt32 | [RegulatoryExposureCalculator.cs:95] |
| first_name | customers.first_name | NULL coalesced to "" | [RegulatoryExposureCalculator.cs:96] |
| last_name | customers.last_name | NULL coalesced to "" | [RegulatoryExposureCalculator.cs:97] |
| account_count | Computed | COUNT of account rows per customer_id (all dates) | [RegulatoryExposureCalculator.cs:99] |
| total_balance | Computed | SUM of current_balance per customer_id (all dates), rounded to 2 dp | [RegulatoryExposureCalculator.cs:100,114] |
| compliance_events | Computed | COUNT of all compliance_events per customer_id (all dates) | [RegulatoryExposureCalculator.cs:101] |
| wire_count | Computed | COUNT of all wire_transfers per customer_id (all dates) | [RegulatoryExposureCalculator.cs:102] |
| exposure_score | Computed | `(compliance * 30) + (wires * 20) + (balance / 10000)`, decimal, rounded 2 dp | [RegulatoryExposureCalculator.cs:105-106] |
| as_of | Computed | targetDate (after weekend fallback) | [RegulatoryExposureCalculator.cs:118] |

## Non-Deterministic Fields
None identified. Output row order follows the iteration order of the target date-filtered customer list.

## Write Mode Implications
- **Overwrite** mode: each run replaces all part files in the output directory. Multi-day runs retain only the last effective date's output.
- Evidence: [regulatory_exposure_summary.json:43]

## Edge Cases

1. **Weekend fallback**: Saturday/Sunday dates are mapped to Friday's data. If Friday's customer data doesn't exist, ALL customer rows are used as fallback.
   - Evidence: [RegulatoryExposureCalculator.cs:38-43]

2. **Cross-date inflation**: Compliance events, wire transfers, and accounts are NOT filtered by target date within the module. If the DataSourcing module returns multiple as_of dates, counts are inflated.
   - Confidence: HIGH
   - Evidence: [RegulatoryExposureCalculator.cs:46-89] — no date filter on these aggregations

3. **Empty input**: If customers is null/empty, empty output is produced.
   - Evidence: [RegulatoryExposureCalculator.cs:22-26]

4. **Fallback to all customers**: If no customers match the target date, all customer rows are used. This could produce duplicate customer entries (one per as_of date).
   - Confidence: MEDIUM
   - Evidence: [RegulatoryExposureCalculator.cs:40-43]

5. **Decimal vs double arithmetic**: This job uses decimal arithmetic unlike CustomerComplianceRisk which uses double. Both use banker's rounding (ToEven), but the underlying arithmetic precision differs — decimal has higher precision and no floating-point epsilon issues.
   - Confidence: HIGH
   - Evidence: Compare [RegulatoryExposureCalculator.cs:105] vs [CustomerComplianceRiskCalculator.cs:88]

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Weekend fallback | [RegulatoryExposureCalculator.cs:29-32] |
| BR-2: Target date customer filter with fallback | [RegulatoryExposureCalculator.cs:38-43] |
| BR-3: Unfiltered aggregations | [RegulatoryExposureCalculator.cs:46-89] |
| BR-4: Exposure score formula | [RegulatoryExposureCalculator.cs:105-106] |
| BR-5: Decimal arithmetic | [RegulatoryExposureCalculator.cs:105-106] |
| BR-6: Balance rounding | [RegulatoryExposureCalculator.cs:114] |
| BR-7: Account count aggregation | [RegulatoryExposureCalculator.cs:80-82] |
| BR-8: One row per customer | [RegulatoryExposureCalculator.cs:93-94] |
| BR-9: Empty input guard | [RegulatoryExposureCalculator.cs:22-26] |
| BR-10: as_of = targetDate | [RegulatoryExposureCalculator.cs:118] |
| BR-11: NULL coalescing | [RegulatoryExposureCalculator.cs:96-97] |

## Open Questions
1. The exposure formula is very similar to CustomerComplianceRisk's risk_score formula but uses decimal arithmetic and includes total_balance/10000. Is this intentional duplication or should these be consolidated?
   - Confidence: LOW — code comment says "AP2: duplicated logic"
2. Compliance events, wires, and accounts are not filtered to the target date. Is this intentional (showing cumulative exposure) or a bug?
   - Confidence: MEDIUM
