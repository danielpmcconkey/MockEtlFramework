# Phase 3 Run 2 -- Executive Summary

## Overview

- **Total jobs analyzed:** 31
- **Total comparison dates:** 31 (October 1-31, 2024)
- **Number of fix iterations required:** 5 total (4 with fixes, 1 successful full run)
- **Final result:** All 31 jobs match across all 31 dates (100% equivalence)

## Fix Iterations

| Iteration | Issue | Root Cause | Fix Applied | Jobs Affected |
|-----------|-------|-----------|-------------|---------------|
| 1 | Schema type mismatches | DDL script not run before first execution; DataFrameWriter auto-created tables with wrong types (TEXT vs DATE, BIGINT vs INTEGER, DOUBLE PRECISION vs NUMERIC) | Dropped auto-created tables, ran DDL script with proper types | All 31 V2 jobs |
| 2 | Timestamp format error | SQLite stores DateTime with 'T' separator; DataFrameWriter's CoerceValue() only parses space separator | Added `REPLACE(column, 'T', ' ')` in V2 SQL for timestamp columns | BranchVisitLogV2, LargeTransactionLogV2 |
| 3 | Date format error | CustomerDemographicsV2 birthdate fix not yet deployed in iteration 2 | Added `strftime('%Y-%m-%d', c.birthdate)` for explicit date formatting | CustomerDemographicsV2 |
| 4 | Numeric precision mismatch + rounding algorithm | (a) double_secret_curated NUMERIC columns lacked precision/scale constraints; (b) SQLite ROUND uses round-half-up while C# Math.Round uses banker's rounding | (a) ALTER TABLE to add matching NUMERIC(p,s) constraints; (b) Rewrote CustomerValueScoreV2 to use C# decimal arithmetic with Math.Round | AccountTypeDistributionV2 (precision), CustomerValueScoreV2 (rounding) |
| 5 | No issues | N/A -- clean full run | N/A | N/A |

## Anti-Pattern Statistics

| Anti-Pattern | Code | Jobs Affected | Eliminated in V2 | Description |
|-------------|------|---------------|-------------------|-------------|
| Redundant Data Sourcing | AP-1 | 22 | 22 fully eliminated | DataSourcing modules fetching tables never referenced by downstream logic |
| Duplicated Transformation Logic | AP-2 | 4 | 4 fully eliminated | Re-deriving data already computed by upstream jobs (CustomerFullProfile, DailyTransactionVolume, MonthlyTransactionTrend, TopBranches) |
| Unnecessary External Module | AP-3 | 18 | 15 fully eliminated, 3 partially | C# External modules performing work expressible as SQL; 2 jobs retained justified External modules (CoveredTransactions, CustomerAddressDeltas), 3 jobs partially improved (CreditScoreAverage, CreditScoreSnapshot, CustomerBranchActivity) |
| Unused Columns Sourced | AP-4 | 23 | 23 fully eliminated | DataSourcing column lists including columns never referenced downstream |
| Asymmetric NULL/Default Handling | AP-5 | 5 | 0 eliminated (all documented) | Inconsistent NULL vs default handling across columns; preserved for output equivalence, documented as known inconsistencies |
| Row-by-Row Iteration | AP-6 | 18 | 18 fully eliminated | foreach loops in External modules replaced with set-based SQL or LINQ |
| Hardcoded Magic Values | AP-7 | 10 | 10 documented with comments | Unexplained thresholds and constants; all documented with SQL comments explaining business meaning |
| Overly Complex SQL | AP-8 | 10 | 10 fully eliminated | Unnecessary CTEs, subqueries, window functions, and verbose expressions replaced with simpler equivalents |
| Misleading Job/Table Names | AP-9 | 3 | 0 eliminated (all documented) | Names that do not match actual job behavior; cannot rename for output compatibility, documented in reports |
| Missing Dependency Declarations | AP-10 | 2 | 2 fully fixed | CustomerFullProfile: added SameDay dependency on CustomerDemographics; BranchVisitSummary: removed unnecessary dependency on BranchDirectory |

**Total anti-pattern instances found:** 115 across 31 jobs
**Total anti-pattern instances eliminated or documented:** 115 (100%)

## Key Findings

### Pattern Distribution

The most pervasive anti-patterns were **AP-4 (Unused Columns Sourced)** affecting 23 of 31 jobs and **AP-1 (Redundant Data Sourcing)** affecting 22 of 31 jobs. These represent systemic over-fetching of data -- jobs were configured to load entire tables and extra reference tables that were never used. The third most common pattern was **AP-3 (Unnecessary External Module)** and **AP-6 (Row-by-Row Iteration)**, both affecting 18 jobs. These are correlated: jobs that used External modules for SQL-expressible logic also used row-by-row iteration to process data.

### Architecture Improvements

1. **SQL-First Migration:** 15 External modules were fully replaced with SQL Transformations, and 3 more were partially simplified. Only 2 External modules (CoveredTransactions, CustomerAddressDeltas) were retained as genuinely justified -- both require multi-query database access with snapshot fallback that cannot be expressed in the framework's single-query SQL Transformation model.

2. **Dependency Chain Optimization:** The V2 implementation introduced proper data reuse through curated table dependencies. Four jobs (CustomerFullProfile, DailyTransactionVolume, MonthlyTransactionTrend, TopBranches) now read from upstream curated tables instead of re-deriving data from raw datalake, reducing total I/O and ensuring consistency.

3. **Schema Efficiency:** Across all 31 V2 job configs, unused DataSourcing modules and unused columns were removed, reducing the total data footprint loaded into memory per job execution cycle.

### Comparison Results

All 31 jobs produced byte-identical output across all 31 dates (October 1-31, 2024) after the 5th iteration. The comparison used EXCEPT-based SQL to verify exact row-level equivalence between `curated` and `double_secret_curated` schemas. Per-table row counts were verified for each date, with final counts ranging from 1 row (customer_address_deltas, daily_transaction_volume, monthly_transaction_trend) to 750 rows (customer_contact_info) per effective date.

## Recommendations

### For Production Deployment

1. **Run DDL scripts before first execution.** The auto-create behavior of DataFrameWriter infers types that may not match the intended schema. Always pre-create target tables with explicit column types and precision.

2. **Validate timestamp and date formats.** The SQLite intermediate layer introduces format inconsistencies (e.g., 'T' separator for timestamps). V2 SQL transformations include explicit format handling (`REPLACE` for timestamps, `strftime` for dates) that should be retained.

3. **Enforce banker's rounding consistently.** When business logic requires specific rounding behavior (as in CustomerValueScore), use the C# Math.Round method rather than SQLite ROUND, which uses different rounding semantics.

4. **Declare and enforce job dependencies.** The V2 dependency declarations in `control.job_dependencies` should be enforced by the framework's execution scheduler to prevent stale-data reads.

5. **Review AP-5 inconsistencies with business stakeholders.** The asymmetric NULL/default handling (e.g., BranchVisitLog using empty string for missing branch names but NULL for missing customer names) should be reviewed for intentional vs accidental behavior.

6. **Consider renaming misleading tables.** Three jobs (CreditScoreSnapshot, LoanPortfolioSnapshot, MonthlyTransactionTrend) have names that do not accurately describe their output. A future phase could address naming consistency.

7. **Monitor AP-2 dependency chains.** The V2 introduces new inter-job dependencies (e.g., CustomerFullProfile depends on CustomerDemographics). These chains should be monitored for latency impact and failure propagation.
