# LoanPortfolioSnapshot -- Functional Specification Document

## Design Approach

SQL-first. The original External module (LoanSnapshotBuilder) is a trivial pass-through that copies all loan rows while dropping two columns (origination_date, maturity_date). This is a simple SQL SELECT with explicit column list. The V2 replaces the External module with a Transformation step and removes unused DataSourcing modules.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y                   | Y                  | Removed `branches` DataSourcing module (never referenced by External module) |
| AP-2    | N                   | N/A                | No duplicated upstream logic |
| AP-3    | Y                   | Y                  | Replaced External module with SQL Transformation (simple column projection) |
| AP-4    | N                   | N/A                | All sourced columns are used in output (origination_date and maturity_date are excluded by not sourcing them) |
| AP-5    | N                   | N/A                | No NULL/default handling needed (pass-through) |
| AP-6    | Y                   | Y                  | Row-by-row foreach replaced by set-based SQL SELECT |
| AP-7    | N                   | N/A                | No magic values |
| AP-8    | N                   | N/A                | No complex SQL in original |
| AP-9    | Y                   | N (documented)     | Name "LoanPortfolioSnapshot" suggests aggregation but job is a column projection; cannot rename for output compatibility |
| AP-10   | N                   | N/A                | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `loan_accounts`: Read `loan_id`, `customer_id`, `loan_type`, `original_amount`, `current_balance`, `interest_rate`, `loan_status` from `datalake.loan_accounts` (excludes origination_date and maturity_date)
2. **Transformation** `loan_snapshot_result`: Simple SELECT of all columns
3. **DataFrameWriter**: Write `loan_snapshot_result` to `double_secret_curated.loan_portfolio_snapshot` in Overwrite mode

## SQL Transformation Logic

```sql
SELECT
    loan_id,
    customer_id,
    loan_type,
    original_amount,
    current_balance,
    interest_rate,
    loan_status,
    as_of
FROM loan_accounts
```

**Key design notes:**
- The DataSourcing step only requests the 7 needed columns (plus as_of auto-appended), so origination_date and maturity_date are never fetched
- No filtering: all loan account records are included regardless of status (BR-1)
- The Transformation is a simple pass-through SELECT that ensures column order matches expected output

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | No WHERE clause; all loan rows included |
| BR-2 | DataSourcing columns list excludes origination_date and maturity_date |
| BR-3 | All other columns are SELECT'd as pass-through |
| BR-4 | DataFrameWriter writeMode: "Overwrite" |
| BR-5 | Framework handles empty DataFrames natively; SQL produces empty result if no rows |
