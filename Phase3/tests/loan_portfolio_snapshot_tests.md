# Test Plan: LoanPortfolioSnapshotV2

## Test Cases
| ID | BRD Req | Description | Expected Result |
|----|---------|------------|-----------------|
| TC-1 | BR-1 | Verify all loan_accounts rows are included | Row count matches datalake.loan_accounts count for the date |
| TC-2 | BR-2 | Verify output has 8 columns (7 loan fields + as_of) | No origination_date or maturity_date columns |
| TC-3 | BR-3 | Verify values are passed through without transformation | All values match source exactly |
| TC-4 | BR-4 | Verify Overwrite mode: only latest effective date data present | Only one as_of in output table after run |
| TC-5 | BR-5 | Verify empty output when loan_accounts is empty | 0 rows on weekend effective dates |
| TC-6 | BR-6 | Verify branches loaded but unused | Job succeeds; output unaffected by branches data |
| TC-7 | BR-7 | Verify as_of comes from each loan_accounts row | as_of matches the effective date |

## Edge Case Tests
| ID | Scenario | Expected Result |
|----|----------|-----------------|
| EC-1 | Weekend effective date (no loan_accounts data) | Empty output (0 rows), no error |
| EC-2 | NULL values in loan_accounts columns | NULL passed through as-is |
| EC-3 | Comparison with curated.loan_portfolio_snapshot for same date | Row-for-row match on all columns |
