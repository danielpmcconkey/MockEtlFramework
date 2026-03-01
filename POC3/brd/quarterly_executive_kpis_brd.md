# QuarterlyExecutiveKpis — Business Requirements Document

## Overview
Produces a set of 8 key performance indicators (KPIs) covering customers, accounts, transactions, investments, and compliance events. Despite the name "Quarterly", the job runs daily. Implements a weekend fallback to Friday for the as_of date. Output is written to Parquet with Overwrite mode.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/quarterly_executive_kpis/`
- **numParts**: 1
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.customers | id, first_name, last_name | Effective date range (injected) | [quarterly_executive_kpis.json:8-10] |
| datalake.accounts | account_id, customer_id, current_balance | Effective date range (injected) | [quarterly_executive_kpis.json:13-15] |
| datalake.transactions | transaction_id, account_id, amount | Effective date range (injected) | [quarterly_executive_kpis.json:18-20] |
| datalake.investments | investment_id, customer_id, current_value | Effective date range (injected) | [quarterly_executive_kpis.json:23-25] |
| datalake.compliance_events | event_id, customer_id, event_type, status | Effective date range (injected) | [quarterly_executive_kpis.json:28-31] |

## Business Rules

BR-1: **Weekend fallback**: If __maxEffectiveDate is Saturday, the output as_of is set to Friday (subtract 1 day). If Sunday, subtract 2 days. Weekday dates are used as-is.
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:28-31] — explicit DayOfWeek checks

BR-2: The guard clause only checks if customers is null or empty. Accounts, transactions, investments, and compliance_events being empty does NOT prevent output.
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:21-25] — only checks customers

BR-3: **Misleading name**: Code comment explicitly notes "AP9: Misleading name — 'quarterly' but actually produces daily KPIs".
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:33] — comment

BR-4: **Overlapping logic with ExecutiveDashboard**: Code comment notes "AP2: Duplicates logic from executive_dashboard and other summary jobs".
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:34] — comment

BR-5: Eight KPIs are produced in a fixed order, each as a row with kpi_name, kpi_value, and as_of.
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:79-89] — kpis list with 8 entries

BR-6: All KPI values are rounded to 2 decimal places using default Math.Round (MidpointRounding.ToEven / banker's rounding).
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:82-89] — `Math.Round(value, 2)`

BR-7: total_customers, total_accounts, total_transactions, total_investments, and compliance_events are row counts (not distinct counts).
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:37,40-48,51-59,62-70,76] — iterates with count++

BR-8: total_balance sums accounts.current_balance; total_txn_amount sums transactions.amount; total_investment_value sums investments.current_value.
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:47,58,69]

BR-9: compliance_events count includes ALL events regardless of event_type or status. The sourced event_type and status columns are not used for filtering.
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:76] — `complianceEvents?.Count ?? 0` — simple count, no filtering

BR-10: first_name and last_name from customers are sourced but never used.
- Confidence: HIGH
- Evidence: [QuarterlyExecutiveKpiBuilder.cs:15-25] — only customers.Count is used

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| kpi_name | Fixed strings | One of 8 KPI names | [QuarterlyExecutiveKpiBuilder.cs:82-89] |
| kpi_value | Computed | Rounded to 2 decimal places | [QuarterlyExecutiveKpiBuilder.cs:82-89] |
| as_of | __maxEffectiveDate | Weekend fallback to Friday | [QuarterlyExecutiveKpiBuilder.cs:28-31,98] |

### KPI Names and Computations

| KPI Name | Computation | Evidence |
|----------|-------------|----------|
| total_customers | COUNT of customer rows | [QuarterlyExecutiveKpiBuilder.cs:37] |
| total_accounts | COUNT of account rows | [QuarterlyExecutiveKpiBuilder.cs:42-44] |
| total_balance | SUM of accounts.current_balance | [QuarterlyExecutiveKpiBuilder.cs:47] |
| total_transactions | COUNT of transaction rows | [QuarterlyExecutiveKpiBuilder.cs:53-55] |
| total_txn_amount | SUM of transactions.amount | [QuarterlyExecutiveKpiBuilder.cs:58] |
| total_investments | COUNT of investment rows | [QuarterlyExecutiveKpiBuilder.cs:64-66] |
| total_investment_value | SUM of investments.current_value | [QuarterlyExecutiveKpiBuilder.cs:69] |
| compliance_events | COUNT of compliance_events rows (all, unfiltered) | [QuarterlyExecutiveKpiBuilder.cs:76] |

## Non-Deterministic Fields
None identified.

## Write Mode Implications
- **Overwrite mode**: Each run replaces the Parquet directory. Only the latest effective date's KPIs persist.
- Multi-day gap-fill: only the final day survives.

## Edge Cases
- **Weekend fallback**: On Saturday/Sunday effective dates, the output as_of shifts to Friday. However, the source data is still sourced for the original weekend date. Customers, accounts, and investments are weekday-only and will be empty on weekends, triggering the guard clause (empty customers) and producing an empty DataFrame.
  - This means the weekend fallback code is effectively dead code — the guard clause fires first.
  - Confidence: HIGH
- **Compliance events on weekends**: Compliance events have data every day including weekends, but customers being empty on weekends means no output regardless.
- **Transactions on weekends**: Same situation — transactions exist on weekends but the guard clause prevents output.
- **Overlap with ExecutiveDashboard**: Both jobs produce similar metrics (total_customers, total_accounts, total_balance, total_transactions, total_txn_amount). ExecutiveDashboard also adds total_loans, total_loan_balance, total_branch_visits, and avg_txn_amount. QuarterlyExecutiveKpis adds total_investments, total_investment_value, and compliance_events.
- **event_type and status unused**: These columns are sourced from compliance_events but never used for filtering or grouping.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| Weekend fallback to Friday | QuarterlyExecutiveKpiBuilder.cs:28-31 |
| Guard on customers only | QuarterlyExecutiveKpiBuilder.cs:21-25 |
| 8 KPIs produced | QuarterlyExecutiveKpiBuilder.cs:79-89 |
| Banker's rounding to 2dp | QuarterlyExecutiveKpiBuilder.cs:82-89 |
| Compliance events unfiltered count | QuarterlyExecutiveKpiBuilder.cs:76 |
| Misleading "quarterly" name | QuarterlyExecutiveKpiBuilder.cs:33 comment |
| Duplicates ExecutiveDashboard logic | QuarterlyExecutiveKpiBuilder.cs:34 comment |
| Overwrite write mode | quarterly_executive_kpis.json:49 |
| 1 Parquet part file | quarterly_executive_kpis.json:48 |
| First effective date 2024-10-01 | quarterly_executive_kpis.json:3 |

## Open Questions
1. The weekend fallback is effectively dead code because the guard clause on customers fires first on weekends (customers has no weekend data). Should the guard be adjusted or the fallback removed? (Confidence: HIGH)
2. "Quarterly" name is explicitly called out as misleading in the code. Should it be renamed? (Confidence: HIGH — per code comment)
3. Should compliance_events be filtered by event_type or status rather than counted blindly? (Confidence: MEDIUM)
4. Significant overlap with ExecutiveDashboard — are both needed or should they be consolidated? (Confidence: MEDIUM — per code comment)
