# LoanRiskAssessment — Business Requirements Document

## Overview
Enriches loan account records with average credit scores across bureaus and assigns a risk tier classification based on the borrower's creditworthiness. Output is a Parquet file split across 2 parts per effective date.

## Output Type
ParquetFileWriter

## Writer Configuration
- **source**: `output`
- **outputDirectory**: `Output/curated/loan_risk_assessment/`
- **numParts**: 2
- **writeMode**: Overwrite

## Source Tables

| Table | Columns Used | Filters | Evidence |
|-------|-------------|---------|----------|
| datalake.loan_accounts | loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status | Effective date range (injected by executor) | [loan_risk_assessment.json:8-12] |
| datalake.credit_scores | credit_score_id, customer_id, bureau, score | Effective date range (injected by executor) | [loan_risk_assessment.json:14-18] |
| datalake.customers | id, first_name, last_name | Effective date range (injected by executor) | [loan_risk_assessment.json:20-24] |
| datalake.segments | segment_id, segment_name | Effective date range (injected by executor) | [loan_risk_assessment.json:26-30] |

### Table Schemas (from database)

**loan_accounts**: loan_id (integer), customer_id (integer), loan_type (varchar: Auto/Mortgage/Personal/Student), original_amount (numeric), current_balance (numeric), interest_rate (numeric), origination_date (date), maturity_date (date), loan_status (varchar: Active/Delinquent), as_of (date). ~894 rows per as_of date.

**credit_scores**: credit_score_id (integer), customer_id (integer), bureau (varchar: Equifax/Experian/TransUnion), score (integer), as_of (date). ~6,690 rows per as_of date (3 bureaus x ~2,230 customers).

**customers**: id (integer), prefix (varchar), first_name (varchar), last_name (varchar), sort_name (varchar), suffix (varchar), birthdate (date), as_of (date). ~2,230 rows per date.

**segments**: segment_id (integer), segment_name (varchar), segment_code (varchar), as_of (date).

## Business Rules

BR-1: Average credit score is computed per customer by averaging all `score` values from credit_scores for that customer_id (across all bureaus and all as_of dates in the range).
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:28-42] — groups scores by customer_id, computes average using LINQ `.Average()`

BR-2: Risk tier is assigned based on average credit score thresholds:
  - avg_credit_score >= 700 → "Low Risk"
  - avg_credit_score >= 650 → "Medium Risk"
  - avg_credit_score >= 550 → "High Risk"
  - avg_credit_score < 550 → "Very High Risk"
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:58-64] — pattern matching switch expression

BR-3: If a loan's customer_id has no credit score records, avg_credit_score is set to `DBNull.Value` (null) and risk_tier is set to "Unknown".
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:67-69] — explicit fallback for missing credit scores

BR-4: The `customers` table is sourced but is NOT used by the External module.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs] — no reference to `customers` DataFrame; only `loan_accounts` and `credit_scores` are retrieved (lines 16-17)

BR-5: The `segments` table is sourced but is NOT used by the External module.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs] — no reference to `segments` DataFrame

BR-6: Empty output is produced if EITHER loan_accounts OR credit_scores is null or empty.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:19-23] — compound null/empty check: `loanAccounts == null || loanAccounts.Count == 0 || creditScores == null || creditScores.Count == 0`

BR-7: Loan fields (loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status) are passed through without transformation.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:73-78] — direct `loanRow["column"]` assignment

BR-8: The `as_of` column comes from the loan row (per-row), not from `__maxEffectiveDate`.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:81] — `["as_of"] = loanRow["as_of"]`

BR-9: Credit score averaging uses `decimal` arithmetic (LINQ `.Average()` on `List<decimal>`), preserving precision.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:31, 36, 41] — scores collected as `decimal`, averaged via LINQ

## Output Schema

| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| loan_id | loan_accounts.loan_id | Direct pass-through | [LoanRiskCalculator.cs:73] |
| customer_id | loan_accounts.customer_id | Direct pass-through | [LoanRiskCalculator.cs:74] |
| loan_type | loan_accounts.loan_type | Direct pass-through | [LoanRiskCalculator.cs:75] |
| current_balance | loan_accounts.current_balance | Direct pass-through | [LoanRiskCalculator.cs:76] |
| interest_rate | loan_accounts.interest_rate | Direct pass-through | [LoanRiskCalculator.cs:77] |
| loan_status | loan_accounts.loan_status | Direct pass-through | [LoanRiskCalculator.cs:78] |
| avg_credit_score | Computed | Average of all credit_scores.score for this customer; DBNull.Value if no scores | [LoanRiskCalculator.cs:55-56, 68] |
| risk_tier | Computed | Categorical: "Low Risk" / "Medium Risk" / "High Risk" / "Very High Risk" / "Unknown" | [LoanRiskCalculator.cs:58-64, 69] |
| as_of | loan_accounts.as_of | Per-row pass-through | [LoanRiskCalculator.cs:81] |

## Non-Deterministic Fields
None identified. All computations are deterministic given the same input data.

## Write Mode Implications
**Overwrite** mode with **2 parts**: Each effective date run replaces the entire output directory. Data is split across `part-00000.parquet` and `part-00001.parquet`. In multi-day gap-fill scenarios, only the last day's output survives.

## Edge Cases

1. **Customer with no credit scores**: avg_credit_score = DBNull.Value (null), risk_tier = "Unknown". The loan row is still included in output.
   - Evidence: [LoanRiskCalculator.cs:67-69]

2. **Empty credit_scores table**: If credit_scores has zero rows, the entire output is empty (zero rows), even if loan_accounts has data.
   - Evidence: [LoanRiskCalculator.cs:19] — `creditScores.Count == 0` triggers empty output

3. **Multi-bureau averaging**: A customer with scores from all 3 bureaus (Equifax, Experian, TransUnion) has their scores averaged across all 3. Database shows 6,690 credit score rows per date / 2,230 customers ≈ 3 scores per customer.
   - Evidence: [LoanRiskCalculator.cs:28-42]; [Database query: 6,690 credit scores per date, 3 bureaus]

4. **Multi-date score accumulation**: If the effective date range spans multiple days, credit scores from all days are averaged together (not just the latest day). This could dilute or amplify score changes over time.
   - Evidence: [LoanRiskCalculator.cs:28-37] — no as_of filter on credit score grouping

5. **Unused sourced tables**: Both `customers` and `segments` are sourced but unused. They consume database and memory resources without contributing to output.
   - Evidence: [loan_risk_assessment.json:20-30]; [LoanRiskCalculator.cs] — neither DataFrame is accessed

6. **Decimal precision for avg_credit_score**: Since credit_scores.score is integer in the database but converted to decimal in code, the average will be a decimal value (e.g., 843.0, 836.333...). No rounding is applied to the average.
   - Evidence: [LoanRiskCalculator.cs:31, 41] — `Convert.ToDecimal(row["score"])`, `.Average()` returns decimal

## Traceability Matrix

| Requirement | Evidence Citation |
|-------------|------------------|
| BR-1: Avg credit score computation | [LoanRiskCalculator.cs:28-42] |
| BR-2: Risk tier thresholds | [LoanRiskCalculator.cs:58-64] |
| BR-3: Missing score → Unknown | [LoanRiskCalculator.cs:67-69] |
| BR-4: Customers unused | [LoanRiskCalculator.cs] |
| BR-5: Segments unused | [LoanRiskCalculator.cs] |
| BR-6: Empty output guard | [LoanRiskCalculator.cs:19-23] |
| BR-7: Loan field pass-through | [LoanRiskCalculator.cs:73-78] |
| BR-8: Per-row as_of | [LoanRiskCalculator.cs:81] |
| BR-9: Decimal precision | [LoanRiskCalculator.cs:31, 36, 41] |
| Output: Parquet, 2 parts, Overwrite | [loan_risk_assessment.json:38-43] |

## Open Questions

1. **Why are customers and segments sourced?** Neither table is used by the LoanRiskCalculator. Possibly intended for enriching output with customer names and segment classifications but not implemented.
   - Confidence: HIGH — code clearly shows neither is accessed

2. **Empty credit_scores blocks all output**: The guard condition returns empty output if credit_scores is empty, even if loan_accounts has data. This means a date with no credit score data produces zero loan risk assessments. Is this intentional?
   - Confidence: MEDIUM — guard condition is explicit, but could be a design choice or bug
