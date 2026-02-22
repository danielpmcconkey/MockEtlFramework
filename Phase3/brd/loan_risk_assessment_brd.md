# LoanRiskAssessment -- Business Requirements Document

## Overview

This job enriches each loan account record with the borrower's average credit score (across all bureaus) and assigns a risk tier based on that score. The output is written in Overwrite mode.

## Source Tables

### datalake.loan_accounts
- **Columns used**: `loan_id`, `customer_id`, `loan_type`, `current_balance`, `interest_rate`, `loan_status`, `as_of`
- **Filter**: None -- all loan accounts are included

### datalake.credit_scores
- **Columns used**: `customer_id`, `score`
- **Column sourced but unused**: `credit_score_id`, `bureau` (see AP-4)
- **Join logic**: Credit scores are grouped by `customer_id` to compute average score per customer
- **Evidence**: [ExternalModules/LoanRiskCalculator.cs:26-42] groups scores by customer_id and averages them

### datalake.customers (UNUSED)
- **Columns sourced**: `id`, `first_name`, `last_name`
- **Usage**: NONE -- the LoanRiskCalculator External module never references the `customers` DataFrame
- **Evidence**: [ExternalModules/LoanRiskCalculator.cs] No reference to `customers` key in sharedState. The module only accesses `loan_accounts` and `credit_scores`.
- See AP-1.

### datalake.segments (UNUSED)
- **Columns sourced**: `segment_id`, `segment_name`
- **Usage**: NONE -- the LoanRiskCalculator External module never references the `segments` DataFrame
- **Evidence**: [ExternalModules/LoanRiskCalculator.cs] No reference to `segments` key in sharedState.
- See AP-1.

## Business Rules

BR-1: For each loan account, compute the average credit score across all bureau entries for that loan's customer_id.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:28-42] Groups credit scores by customer_id into lists, then computes `kvp.Value.Average()`
- Evidence: Verified for loan_id=1 (customer_id=1004): bureaus Equifax(850), Experian(744), TransUnion(746) -> avg = 780.00, matches curated output

BR-2: Risk tier is assigned based on the average credit score using the following thresholds:
- >= 750: "Low Risk"
- >= 650 and < 750: "Medium Risk"
- >= 550 and < 650: "High Risk"
- < 550: "Very High Risk"

- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:58-64] C# switch expression with pattern matching:
  ```
  >= 750 => "Low Risk"
  >= 650 => "Medium Risk"
  >= 550 => "High Risk"
  _ => "Very High Risk"
  ```
- Evidence: [curated.loan_risk_assessment] `SELECT DISTINCT risk_tier` yields: Low Risk, Medium Risk, High Risk, Very High Risk

BR-3: If a customer has no credit score records, avg_credit_score is set to DBNull.Value (NULL) and risk_tier is "Unknown".
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:67-69] `avgCreditScore = DBNull.Value; riskTier = "Unknown";`
- Note: In current data, all customers with loans have credit scores (0 Unknown-tier loans observed), but the code handles the edge case.

BR-4: All loan accounts are included in output regardless of loan_status or loan_type.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:45-83] Iterates all `loanAccounts.Rows` with no filter condition
- Evidence: [curated.loan_risk_assessment] 90 rows = [datalake.loan_accounts] 90 rows for same date

BR-5: The output uses Overwrite mode.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/loan_risk_assessment.json:44] `"writeMode": "Overwrite"`

BR-6: If loan_accounts or credit_scores DataFrames are null or empty, an empty output is produced.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:18-23] null/empty check on both inputs

## Output Schema

| Column | Source | Transformation |
|--------|--------|----------------|
| loan_id | datalake.loan_accounts.loan_id | Pass-through |
| customer_id | datalake.loan_accounts.customer_id | Pass-through |
| loan_type | datalake.loan_accounts.loan_type | Pass-through |
| current_balance | datalake.loan_accounts.current_balance | Pass-through |
| interest_rate | datalake.loan_accounts.interest_rate | Pass-through |
| loan_status | datalake.loan_accounts.loan_status | Pass-through |
| avg_credit_score | Derived: AVG of datalake.credit_scores.score grouped by customer_id | NULL if no credit scores exist for customer |
| risk_tier | Derived: Tiered classification based on avg_credit_score | "Unknown" if no credit scores; see BR-2 for thresholds |
| as_of | datalake.loan_accounts.as_of | Pass-through |

## Edge Cases

- **Customer with no credit scores**: avg_credit_score = NULL, risk_tier = "Unknown"
- **Customer with scores from multiple bureaus**: All scores are averaged (e.g., 3 bureaus = average of 3 scores)
- **Credit score on threshold boundary**: >= 750 is "Low Risk" (inclusive), >= 650 is "Medium Risk" (inclusive), etc.
- **Empty DataFrames**: Returns empty output if loan_accounts or credit_scores are null/empty
- **Overwrite mode**: Only latest effective date persists
- **Average precision**: C# `decimal.Average()` produces full decimal precision; the curated table stores as numeric(6,2)

## Anti-Patterns Identified

- **AP-1: Redundant Data Sourcing** -- Two DataSourcing modules are entirely unused:
  1. `customers` DataSourcing fetches `id`, `first_name`, `last_name` from `datalake.customers` but the External module never accesses the `customers` DataFrame.
  2. `segments` DataSourcing fetches `segment_id`, `segment_name` from `datalake.segments` but the External module never accesses the `segments` DataFrame.
  V2 approach: Remove both DataSourcing modules.

- **AP-4: Unused Columns Sourced** -- The credit_scores DataSourcing includes `credit_score_id` and `bureau`, neither of which is referenced by the External module (only `customer_id` and `score` are used). V2 approach: Source only `customer_id` and `score` from credit_scores.

- **AP-6: Row-by-Row Iteration in External Module** -- The External module uses three `foreach` loops: one to group scores, one to compute averages, and one to iterate loans and assign tiers. All of this is expressible as SQL with `GROUP BY` and `CASE`. V2 approach: Replace with SQL Transformation.

- **AP-7: Hardcoded Magic Values** -- The risk tier thresholds (750, 650, 550) appear as literals without business context documentation. V2 approach: Document thresholds in FSD; add SQL comments explaining each tier boundary.

- **AP-3: Unnecessary External Module** -- The entire logic is: average scores per customer, LEFT JOIN to loans, CASE for risk tier. This is straightforward SQL. V2 approach: Replace with SQL Transformation using AVG + GROUP BY + LEFT JOIN + CASE WHEN.

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|-------------------|
| BR-1 | [ExternalModules/LoanRiskCalculator.cs:28-42] score grouping and averaging |
| BR-2 | [ExternalModules/LoanRiskCalculator.cs:58-64] risk tier switch expression |
| BR-3 | [ExternalModules/LoanRiskCalculator.cs:67-69] DBNull.Value and "Unknown" |
| BR-4 | [ExternalModules/LoanRiskCalculator.cs:45-83] no filter on loan rows |
| BR-5 | [JobExecutor/Jobs/loan_risk_assessment.json:44] `"writeMode": "Overwrite"` |
| BR-6 | [ExternalModules/LoanRiskCalculator.cs:18-23] null/empty guard |

## Open Questions

- None. All business rules are directly observable with HIGH confidence.
