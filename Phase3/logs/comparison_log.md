# Phase D: Comparison Log

## Overview
This log tracks the iterative comparison loop for validating V2 jobs against original jobs across October 1-31, 2024.

---

## Iteration 1 - Infrastructure Fixes (no data comparison)

### Problem 1: Assembly Name Collision
- All V2 jobs failed with "Assembly with same name is already loaded"
- Root cause: Original jobs load `ExternalModules.dll` from Phase2 path. V2 jobs tried to load a different `ExternalModules.dll` from Phase3 path. .NET cannot load two assemblies with the same name.
- Fix: Renamed Phase3 assembly to `ExternalModulesV2` via `<AssemblyName>` in ExternalModules.csproj and updated all 32 V2 job config JSON files to reference `ExternalModulesV2.dll`

### Problem 2: TRUNCATE Permission Denied
- 16 V2 jobs with Overwrite mode failed with "permission denied for table X"
- Root cause: `double_secret_curated` tables are owned by `postgres`, not `dansdev`. TRUNCATE requires table ownership or explicit TRUNCATE privilege.
- Fix: Changed `DscWriterUtil.cs` to use `DELETE FROM` instead of `TRUNCATE TABLE` for overwrite mode. DELETE only requires DELETE privilege.

---

## Iteration 2 - Rounding Precision Fixes (2024-10-01 only)

### STEP_30-60: Full run for 2024-10-01
- All 64 jobs succeeded
- Row counts matched for all 32 tables
- EXCEPT comparison found 4 tables with discrepancies:

### Discrepancy 1: account_type_distribution (6 differing rows)
- Column: `percentage` -- curated has 34.66, DSC has 34.6570397111913
- Root cause: curated table has `NUMERIC(5,2)` which auto-rounds on INSERT; DSC table has `NUMERIC` (no precision constraint). Cannot ALTER DDL (not table owner).
- Fix: Added `Math.Round(..., 2)` in `AccountTypeDistributionV2Processor.cs` line 41

### Discrepancy 2: credit_score_average (276 differing rows)
- Column: `avg_score` -- curated rounds to 2dp via `NUMERIC(6,2)`; DSC stores full precision
- Fix: Added `Math.Round(..., 2)` in `CreditScoreAverageV2Processor.cs` line 63

### Discrepancy 3: customer_credit_summary (276 differing rows)
- Column: `avg_credit_score` -- same rounding issue
- Fix: Added `Math.Round(..., 2)` in `CustomerCreditSummaryV2Processor.cs` line 84

### Discrepancy 4: loan_risk_assessment (116 differing rows)
- Column: `avg_credit_score` -- same rounding issue
- Fix: Added `Math.Round(..., 2)` in `LoanRiskAssessmentV2Processor.cs` line 43

---

## Iteration 3 - Full 31-Day Comparison (FINAL)

### STEP_30: Full Reset
- Truncated all tables in curated and double_secret_curated schemas
- Deleted all rows from control.job_runs

### Results by Date

| Date | Result | Notes |
|------|--------|-------|
| 2024-10-01 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-02 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-03 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-04 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-05 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-06 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-07 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-08 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-09 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-10 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-11 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-12 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-13 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-14 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-15 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-16 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-17 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-18 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-19 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-20 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-21 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-22 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-23 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-24 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-25 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-26 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-27 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-28 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-29 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-30 | ALL 32 TABLES MATCH | 64 jobs succeeded |
| 2024-10-31 | ALL 32 TABLES MATCH | 64 jobs succeeded |

### Per-Job Row Counts (2024-10-01 sample)

| Table | Rows |
|-------|------|
| account_balance_snapshot | 277 |
| account_customer_join | 277 |
| account_status_summary | 3 |
| account_type_distribution | 3 |
| branch_directory | 40 |
| branch_visit_log | 29 |
| branch_visit_purpose_breakdown | 28 |
| branch_visit_summary | 20 |
| covered_transactions | 85 |
| credit_score_average | 223 |
| credit_score_snapshot | 669 |
| customer_account_summary_v2 | 223 |
| customer_address_deltas | 1 |
| customer_address_history | 223 |
| customer_branch_activity | 29 |
| customer_contact_info | 750 |
| customer_credit_summary | 223 |
| customer_demographics | 223 |
| customer_full_profile | 223 |
| customer_segment_map | 291 |
| customer_transaction_activity | 196 |
| customer_value_score | 223 |
| daily_transaction_summary | 241 |
| daily_transaction_volume | 1 |
| executive_dashboard | 9 |
| high_balance_accounts | 54 |
| large_transaction_log | 294 |
| loan_portfolio_snapshot | 90 |
| loan_risk_assessment | 90 |
| monthly_transaction_trend | 1 |
| top_branches | 20 |
| transaction_category_summary | 2 |

### Comparison Method
- For each date: EXCEPT-based SQL comparison between `curated.{table}` and `double_secret_curated.{table}` for rows WHERE `as_of::text = '{date}'`
- Both directions checked (curated EXCEPT dsc, and dsc EXCEPT curated) to catch rows present in one but not the other
- Row count comparison also performed as a sanity check

---

## PHASE D COMPLETE

All 32 V2 jobs produce output identical to the original 32 jobs across all 31 dates (October 1-31, 2024).

### Summary of Fixes Required
1. **Assembly naming**: Renamed V2 assembly to `ExternalModulesV2` to avoid .NET assembly loading collision
2. **TRUNCATE to DELETE**: Changed `DscWriterUtil.cs` overwrite mode from TRUNCATE to DELETE due to table ownership permissions
3. **Numeric precision rounding**: Added explicit `Math.Round(..., 2)` in 4 V2 processors where the original code relied on implicit rounding by the `curated` schema's `NUMERIC(n,2)` column constraints, but the `double_secret_curated` schema uses unconstrained `NUMERIC` columns

### Total Iterations
- 3 iterations total (2 fix iterations + 1 clean pass)
- 0 behavioral logic differences found -- all discrepancies were infrastructure or schema-level issues
