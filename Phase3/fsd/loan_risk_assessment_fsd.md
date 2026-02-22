# FSD: LoanRiskAssessmentV2

## Overview
Replaces the original LoanRiskAssessment job with a V2 implementation that writes to the `double_secret_curated` schema instead of `curated`. The V2 External module replicates the exact same business logic as the original LoanRiskCalculator (computing average credit scores per customer and assigning risk tiers to each loan) and then writes directly to `double_secret_curated.loan_risk_assessment` via DscWriterUtil.

## Design Decisions
- **Pattern A (External module replacement)**: The original job uses DataSourcing (x4) -> External (LoanRiskCalculator) -> DataFrameWriter. The V2 replaces both the External and DataFrameWriter steps with a single V2 External module.
- The V2 module replicates the original's credit score averaging logic (group by customer_id, compute average of all bureau scores) and risk tier assignment thresholds.
- Write mode is Overwrite (matching original), so DscWriterUtil.Write is called with overwrite=true.
- The customers and segments DataSourcing steps are retained in the config (matching the original) even though they are unused.

## Module Pipeline
| Step | Module Type | Config/Details |
|------|------------|----------------|
| 1 | DataSourcing | schema=datalake, table=loan_accounts, columns=[loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status], resultName=loan_accounts |
| 2 | DataSourcing | schema=datalake, table=credit_scores, columns=[credit_score_id, customer_id, bureau, score], resultName=credit_scores |
| 3 | DataSourcing | schema=datalake, table=customers, columns=[id, first_name, last_name], resultName=customers (unused) |
| 4 | DataSourcing | schema=datalake, table=segments, columns=[segment_id, segment_name], resultName=segments (unused) |
| 5 | External | LoanRiskAssessmentV2Processor -- computes avg credit score, assigns risk tier, writes to dsc |

## V2 External Module: LoanRiskAssessmentV2Processor
- File: ExternalModules/LoanRiskAssessmentV2Processor.cs
- Processing logic: Groups credit_scores by customer_id, computes average score via List.Average(). For each loan, looks up customer's avg credit score and assigns risk tier (>=750: Low Risk, >=650: Medium Risk, >=550: High Risk, <550: Very High Risk). If no scores found, avg_credit_score=DBNull.Value, risk_tier="Unknown". Writes to double_secret_curated via DscWriterUtil with overwrite=true.
- Output columns: loan_id, customer_id, loan_type, current_balance, interest_rate, loan_status, avg_credit_score, risk_tier, as_of
- Target table: double_secret_curated.loan_risk_assessment
- Write mode: Overwrite (overwrite=true)

## Traceability
| BRD Requirement | FSD Design Element |
|----------------|-------------------|
| BR-1 | V2 processor groups credit_scores by customer_id and computes average |
| BR-2 | Risk tier switch expression with thresholds: >=750 Low, >=650 Medium, >=550 High, <550 Very High |
| BR-3 | Missing credit scores: avg_credit_score=DBNull.Value, risk_tier="Unknown" |
| BR-4 | V2 processor iterates all loan_accounts rows without filtering |
| BR-5 | DscWriterUtil.Write with overwrite=true |
| BR-6 | Empty DataFrame guard if loan_accounts or credit_scores are null/empty |
| BR-7 | avg_credit_score uses raw List.Average() result, no explicit rounding |
| BR-8 | as_of from each loanRow["as_of"] |
| BR-9 | customers and segments sourced in config but not referenced in V2 processor |
| BR-10 | All bureau scores included in average, no bureau filter |
