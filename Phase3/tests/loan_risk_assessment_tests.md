# Test Plan: LoanRiskAssessmentV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify avg_credit_score is average of all bureau scores per customer | Score matches manual calculation of Equifax+Experian+TransUnion / 3 |
| TC-2 | BR-2 | Verify risk tier assignment: >=750 Low Risk | Customers with avg >= 750 labeled "Low Risk" |
| TC-3 | BR-2 | Verify risk tier assignment: >=650 and <750 Medium Risk | Customers with avg 650-749 labeled "Medium Risk" |
| TC-4 | BR-2 | Verify risk tier assignment: >=550 and <650 High Risk | Customers with avg 550-649 labeled "High Risk" |
| TC-5 | BR-2 | Verify risk tier assignment: <550 Very High Risk | Customers with avg < 550 labeled "Very High Risk" |
| TC-6 | BR-3 | Verify missing credit scores: NULL and Unknown | Customers with no scores get avg_credit_score=NULL, risk_tier="Unknown" |
| TC-7 | BR-4 | Verify all loan_accounts rows produce output | Row count matches datalake.loan_accounts count |
| TC-8 | BR-5 | Verify Overwrite mode: only latest effective date data present | Only one as_of in output table after run |
| TC-9 | BR-6 | Verify empty output when loan_accounts or credit_scores are empty | 0 rows on weekend effective dates |
| TC-10 | BR-7 | Verify avg_credit_score is raw average (not explicitly rounded) | Values like 814.333... possible |
| TC-11 | BR-8 | Verify as_of comes from loan_accounts row | as_of matches the effective date |
| TC-12 | BR-9 | Verify customers and segments loaded but unused | Job succeeds; output unaffected |
| TC-13 | BR-10 | Verify all bureaus included in average | Average considers all 3 bureau scores |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend effective date (no loan_accounts/credit_scores data) | Empty output (0 rows), no error |
| EC-2 | Customer with only 1 bureau score | avg_credit_score = that single score |
| EC-3 | Multiple loans for same customer | Each loan gets same avg_credit_score and risk_tier |
| EC-4 | Comparison with curated.loan_risk_assessment for same date | Row-for-row match on all columns |
