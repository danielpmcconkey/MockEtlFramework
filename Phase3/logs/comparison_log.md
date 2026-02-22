# Phase D: Iterative Comparison Log

## Iteration 1

Starting full comparison loop from 2024-10-01 through 2024-10-31.

### STEP_30: Full Reset
- Truncated all curated schema tables
- Truncated all double_secret_curated schema tables
- Deleted all job_runs rows

### 2024-10-01 (Iteration 1)
- Status: DISCREPANCY
- Issue: `double_secret_curated` tables were auto-created by DataFrameWriter with wrong types
  (TEXT instead of DATE/TIMESTAMP, BIGINT instead of INTEGER, DOUBLE PRECISION instead of NUMERIC).
  EXCEPT queries failed with type mismatch errors.
- Root cause: DDL script `Phase3/sql/create_double_secret_curated.sql` was not run before first execution.
  DataFrameWriter auto-created tables with inferred types that don't match the curated schema.
- Fix: Dropped all auto-created tables, ran the DDL script with proper types.

## Iteration 2

### STEP_30: Full Reset
- Truncated all curated/double_secret_curated tables
- Deleted all job_runs rows

### 2024-10-01 (Iteration 2)
- Status: DISCREPANCY
- 3 V2 jobs FAILED:
  1. **BranchVisitLogV2**: `column "visit_timestamp" is of type timestamp but expression is of type text`
  2. **CustomerDemographicsV2**: `column "birthdate" is of type date but expression is of type text`
  3. **LargeTransactionLogV2**: `column "txn_timestamp" is of type timestamp but expression is of type text`
- Root cause: SQLite's `Transformation.ToSqliteValue()` converts DateTime to `yyyy-MM-ddTHH:mm:ss` format (with 'T'),
  but DataFrameWriter's `CoerceValue()` only parses `yyyy-MM-dd HH:mm:ss` (with space). The 'T' format isn't recognized,
  so the value stays as string and PostgreSQL rejects text for timestamp columns.
- Fix for timestamps: Added `REPLACE(column, 'T', ' ')` in V2 SQL for visit_timestamp and txn_timestamp columns.
- Fix for birthdate: Added `strftime('%Y-%m-%d', c.birthdate)` to ensure exact date format output from SQLite.

## Iteration 3

### STEP_30: Full Reset

### 2024-10-01 (Iteration 3)
- Status: DISCREPANCY
- CustomerDemographicsV2 still FAILED with birthdate type error (timestamp fix was applied but birthdate fix not yet deployed)
- Fix: Applied `strftime('%Y-%m-%d', c.birthdate)` in CustomerDemographicsV2.cs SQL

## Iteration 4

### STEP_30: Full Reset

### 2024-10-01 (Iteration 4)
- Status: DISCREPANCY
- All 62 jobs succeeded (no failures)
- 29 of 31 tables matched
- Discrepancies found in 2 tables:
  1. **account_type_distribution**: 3/3 rows differ. Percentage values: curated=34.66, v2=34.6570... (precision mismatch)
  2. **customer_value_score**: 10/223 rows differ. balance_score off by 0.01 (rounding mismatch)
- Root cause (account_type_distribution): `double_secret_curated` NUMERIC columns lacked precision/scale.
  curated has `NUMERIC(5,2)` which auto-rounds to 2 decimals; double_secret_curated had plain `NUMERIC`.
- Root cause (customer_value_score): V2 used SQLite ROUND (round-half-up) while original uses C# `Math.Round`
  (banker's rounding / round-half-to-even). For values like 0.125, SQLite rounds to 0.13 but C# rounds to 0.12.
- Fix (precision): Applied ALTER TABLE on 30 NUMERIC columns to add matching precision/scale constraints.
- Fix (rounding): Rewrote CustomerValueScoreV2.cs to use C# decimal arithmetic with Math.Round instead of SQLite ROUND.

## Iteration 5 (Final Successful Run)

### STEP_30: Full Reset
- Truncated all curated schema tables
- Truncated all double_secret_curated schema tables
- Deleted all job_runs rows

### 2024-10-01
- Status: MATCH
- Per-table row counts: account_balance_snapshot=277, account_customer_join=277, account_status_summary=3, account_type_distribution=3, branch_directory=40, branch_visit_log=29, branch_visit_purpose_breakdown=28, branch_visit_summary=20, covered_transactions=85, credit_score_average=223, credit_score_snapshot=669, customer_address_deltas=1, customer_address_history=223, customer_branch_activity=29, customer_contact_info=750, customer_credit_summary=223, customer_demographics=223, customer_full_profile=223, customer_segment_map=291, customer_transaction_activity=196, customer_value_score=223, daily_transaction_summary=241, daily_transaction_volume=1, executive_dashboard=9, high_balance_accounts=54, large_transaction_log=294, loan_portfolio_snapshot=90, loan_risk_assessment=90, monthly_transaction_trend=1, top_branches=20, transaction_category_summary=2

### 2024-10-02
- Status: MATCH

### 2024-10-03
- Status: MATCH

### 2024-10-04
- Status: MATCH

### 2024-10-05 (Saturday)
- Status: MATCH

### 2024-10-06 (Sunday)
- Status: MATCH

### 2024-10-07
- Status: MATCH

### 2024-10-08
- Status: MATCH

### 2024-10-09
- Status: MATCH

### 2024-10-10
- Status: MATCH

### 2024-10-11
- Status: MATCH

### 2024-10-12 (Saturday)
- Status: MATCH

### 2024-10-13 (Sunday)
- Status: MATCH

### 2024-10-14
- Status: MATCH

### 2024-10-15
- Status: MATCH

### 2024-10-16
- Status: MATCH

### 2024-10-17
- Status: MATCH

### 2024-10-18
- Status: MATCH

### 2024-10-19 (Saturday)
- Status: MATCH

### 2024-10-20 (Sunday)
- Status: MATCH

### 2024-10-21
- Status: MATCH

### 2024-10-22
- Status: MATCH

### 2024-10-23
- Status: MATCH

### 2024-10-24
- Status: MATCH

### 2024-10-25
- Status: MATCH

### 2024-10-26 (Saturday)
- Status: MATCH

### 2024-10-27 (Sunday)
- Status: MATCH

### 2024-10-28
- Status: MATCH

### 2024-10-29
- Status: MATCH

### 2024-10-30
- Status: MATCH

### 2024-10-31
- Status: MATCH

## Summary

- **Total iterations**: 5 (4 with fixes, 1 successful full run)
- **Total comparison dates**: 31 (Oct 1-31, 2024)
- **Final result**: ALL 31 tables match across ALL 31 dates
- **Fix iterations required**: 4
  - Iteration 1: DDL schema type fix (auto-created tables had wrong column types)
  - Iteration 2: Timestamp format fix (SQLite 'T' separator vs space in CoerceValue)
  - Iteration 3: Date format fix (explicit strftime for birthdate column)
  - Iteration 4: Numeric precision fix (NUMERIC column scale) + rounding fix (banker's rounding vs round-half-up)

### Jobs requiring V2 code fixes:
1. **BranchVisitLogV2** - Added REPLACE for timestamp format
2. **LargeTransactionLogV2** - Added REPLACE for timestamp format
3. **CustomerDemographicsV2** - Added strftime for date format
4. **CustomerValueScoreV2** - Rewrote to use C# decimal arithmetic for rounding consistency

### Final per-table row counts (as_of=2024-10-31, Overwrite mode):
| Table | Curated | Double Secret Curated |
|-------|---------|----------------------|
| account_balance_snapshot | 277 | 277 |
| account_customer_join | 277 | 277 |
| account_status_summary | 3 | 3 |
| account_type_distribution | 3 | 3 |
| branch_directory | 40 | 40 |
| branch_visit_log | 29 | 29 |
| branch_visit_purpose_breakdown | 28 | 28 |
| branch_visit_summary | 20 | 20 |
| covered_transactions | 85 | 85 |
| credit_score_average | 223 | 223 |
| credit_score_snapshot | 669 | 669 |
| customer_address_deltas | 1 | 1 |
| customer_address_history | 223 | 223 |
| customer_branch_activity | 29 | 29 |
| customer_contact_info | 750 | 750 |
| customer_credit_summary | 223 | 223 |
| customer_demographics | 223 | 223 |
| customer_full_profile | 223 | 223 |
| customer_segment_map | 291 | 291 |
| customer_transaction_activity | 196 | 196 |
| customer_value_score | 223 | 223 |
| daily_transaction_summary | 241 | 241 |
| daily_transaction_volume | 1 | 1 |
| executive_dashboard | 9 | 9 |
| high_balance_accounts | 54 | 54 |
| large_transaction_log | 294 | 294 |
| loan_portfolio_snapshot | 90 | 90 |
| loan_risk_assessment | 90 | 90 |
| monthly_transaction_trend | 1 | 1 |
| top_branches | 20 | 20 |
| transaction_category_summary | 2 | 2 |
