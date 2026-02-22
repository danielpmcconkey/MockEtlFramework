# BRD: LoanRiskAssessment

## Overview
This job produces a risk assessment for each loan by calculating the average credit score across all bureaus for each customer and assigning a risk tier based on that average. The output is written to `curated.loan_risk_assessment` in Overwrite mode.

## Source Tables
| Table | Schema | Columns Used | Join/Filter Logic | Evidence |
|-------|--------|-------------|-------------------|----------|
| loan_accounts | datalake | loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status | All rows included; customer_id used to join with credit scores | [JobExecutor/Jobs/loan_risk_assessment.json:5-11] DataSourcing config; [ExternalModules/LoanRiskCalculator.cs:46-84] iteration |
| credit_scores | datalake | credit_score_id, customer_id, bureau, score | Grouped by customer_id to compute average score | [loan_risk_assessment.json:13-18] DataSourcing config; [LoanRiskCalculator.cs:28-42] grouping logic |
| customers | datalake | id, first_name, last_name | Sourced but NOT used in External module logic | [loan_risk_assessment.json:20-24] DataSourcing config; Not referenced in LoanRiskCalculator.cs |
| segments | datalake | segment_id, segment_name | Sourced but NOT used in External module logic | [loan_risk_assessment.json:26-30] DataSourcing config; Not referenced in LoanRiskCalculator.cs |

## Business Rules

BR-1: For each customer, the average credit score is computed across all bureau scores (Equifax, Experian, TransUnion) for the effective date.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:28-42] Iterates credit_scores, groups by customer_id, computes average via `kvp.Value.Average()`
- Evidence: [curated.loan_risk_assessment] Sample: customer 1008 has avg_credit_score=814.33, consistent with average of 3 bureau scores

BR-2: Risk tier is assigned based on the average credit score using these thresholds:
- >= 750: "Low Risk"
- >= 650 and < 750: "Medium Risk"
- >= 550 and < 650: "High Risk"
- < 550: "Very High Risk"
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:58-64] C# switch expression with exact thresholds
- Evidence: [curated.loan_risk_assessment] Sample data confirms: 780.00 -> Low Risk, 591.67 -> High Risk, 511.00 -> Very High Risk

BR-3: If a loan's customer has no credit score records, avg_credit_score is set to DBNull.Value (NULL) and risk_tier is set to "Unknown".
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:67-69] `avgCreditScore = DBNull.Value; riskTier = "Unknown";`

BR-4: Every loan_accounts row produces an output row — there is no filter on loan_type, loan_status, or any other loan attribute.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:46] Iterates all loanAccounts.Rows without filter
- Evidence: [curated.loan_risk_assessment] Row count of 90 matches datalake.loan_accounts count of 90

BR-5: Output is written in Overwrite mode — each run truncates the entire table before writing.
- Confidence: HIGH
- Evidence: [JobExecutor/Jobs/loan_risk_assessment.json:42] `"writeMode": "Overwrite"`
- Evidence: [curated.loan_risk_assessment] Only one as_of date (2024-10-31) present

BR-6: If loan_accounts or credit_scores DataFrames are null or empty, the job produces an empty output DataFrame.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:19-23] Null/empty check returns empty DataFrame

BR-7: The avg_credit_score is NOT explicitly rounded — it uses the raw result of `List<decimal>.Average()`.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:55-56] `var avgScore = avgScoreByCustomer[customerId]; avgCreditScore = avgScore;` — no Math.Round applied
- Evidence: [curated.loan_risk_assessment] Values like 814.33 suggest the database may display with precision but the code doesn't explicitly round

BR-8: The as_of column in the output comes from each loan_accounts row.
- Confidence: HIGH
- Evidence: [ExternalModules/LoanRiskCalculator.cs:82] `["as_of"] = loanRow["as_of"]`

BR-9: The customers and segments DataFrames are sourced by the job config but are NOT used by the External module.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs] No references to "customers" or "segments" keys in sharedState retrieval

BR-10: The credit score averaging considers ALL scores for a customer across all bureaus — there is no filtering by bureau.
- Confidence: HIGH
- Evidence: [LoanRiskCalculator.cs:28-37] Iterates all credit score rows, groups by customer_id without bureau filter
- Evidence: [datalake.credit_scores] 3 bureaus (Equifax, Experian, TransUnion) × 223 customers = 669 rows per as_of

## Output Schema
| Column | Source | Transformation | Evidence |
|--------|--------|---------------|----------|
| loan_id | loan_accounts.loan_id | Pass-through | [LoanRiskCalculator.cs:74] |
| customer_id | loan_accounts.customer_id | Pass-through | [LoanRiskCalculator.cs:75] |
| loan_type | loan_accounts.loan_type | Pass-through | [LoanRiskCalculator.cs:76] |
| current_balance | loan_accounts.current_balance | Pass-through | [LoanRiskCalculator.cs:77] |
| interest_rate | loan_accounts.interest_rate | Pass-through | [LoanRiskCalculator.cs:78] |
| loan_status | loan_accounts.loan_status | Pass-through | [LoanRiskCalculator.cs:79] |
| avg_credit_score | credit_scores (computed) | Average of all bureau scores for the customer; NULL if no scores | [LoanRiskCalculator.cs:55-56, 68] |
| risk_tier | Derived from avg_credit_score | Tiered assignment: Low/Medium/High/Very High Risk or Unknown | [LoanRiskCalculator.cs:58-64, 69] |
| as_of | loan_accounts.as_of | Pass-through | [LoanRiskCalculator.cs:82] |

## Edge Cases
- **NULL handling**: If customer has no credit scores, avg_credit_score is DBNull.Value (NULL in database) and risk_tier is "Unknown" (BR-3).
- **Weekend/date fallback**: loan_accounts and credit_scores are weekday-only. On weekend effective dates, DataFrames would be empty, triggering the empty-output guard (BR-6).
- **Zero-row behavior**: Empty DataFrame is valid and written (table truncated with no rows inserted).
- **Multiple loans per customer**: Each loan gets its own row with the same avg_credit_score and risk_tier for that customer. The credit score is customer-level, not loan-level.
- **Unused data**: customers and segments tables are loaded but unused.

## Traceability Matrix
| Requirement | Evidence Citations |
|-------------|-------------------|
| BR-1 | [LoanRiskCalculator.cs:28-42], [curated data verification] |
| BR-2 | [LoanRiskCalculator.cs:58-64], [curated data verification] |
| BR-3 | [LoanRiskCalculator.cs:67-69] |
| BR-4 | [LoanRiskCalculator.cs:46], [curated row count verification] |
| BR-5 | [loan_risk_assessment.json:42], [curated data observation] |
| BR-6 | [LoanRiskCalculator.cs:19-23] |
| BR-7 | [LoanRiskCalculator.cs:55-56] |
| BR-8 | [LoanRiskCalculator.cs:82] |
| BR-9 | [loan_risk_assessment.json:20-30], [LoanRiskCalculator.cs] |
| BR-10 | [LoanRiskCalculator.cs:28-37], [datalake.credit_scores observation] |

## Open Questions
- The customers and segments tables are sourced but unused. This could be intended for future use or is a configuration oversight. Confidence: MEDIUM that this is an oversight.
- The avg_credit_score precision: `List<decimal>.Average()` in C# produces a decimal with full precision. The database stores it as numeric. The exact precision of intermediate calculations may vary depending on the input score values. Confidence: HIGH that the behavior is deterministic given identical input.
