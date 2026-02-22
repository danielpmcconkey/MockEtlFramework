# TopBranches -- Test Plan

## Test Cases

### TC-1: Visit count matches upstream (BR-1, AP-2 fix)
- **Objective**: Verify total_visits = visit_count from curated.branch_visit_summary
- **Method**: Join double_secret_curated.top_branches with curated.branch_visit_summary on branch_id and as_of; verify total_visits = visit_count for all rows

### TC-2: RANK function with ties (BR-2)
- **Objective**: Verify RANK() produces gaps after ties (not DENSE_RANK behavior)
- **Method**: Check that branches with equal visit counts share the same rank, and the next rank skips appropriately (e.g., 1, 2, 2, 2, 5)

### TC-3: Branch name enrichment (BR-3)
- **Objective**: Verify branch_name is correct for each branch_id
- **Method**: Compare branch_name in output with curated.branch_visit_summary (and transitively with datalake.branches)

### TC-4: as_of source (BR-4)
- **Objective**: Verify as_of column matches the effective date
- **Method**: All rows should have the same as_of matching the run's effective date

### TC-5: Branches with zero visits excluded (BR-5)
- **Objective**: Verify only branches with at least one visit appear in output
- **Method**: `SELECT COUNT(*) FROM double_secret_curated.top_branches WHERE total_visits = 0` -- must be 0

### TC-6: Ordering (BR-6)
- **Objective**: Verify rows are ordered by rank ASC, then branch_id ASC
- **Method**: Visual inspection; verify rank values are non-decreasing and branch_id is ascending within tied ranks

### TC-7: Overwrite mode (BR-7)
- **Objective**: Verify only latest effective date's data exists
- **Method**: After running for date X, verify `SELECT DISTINCT as_of FROM double_secret_curated.top_branches` returns only date X

### TC-8: Exact match with original (all dates)
- **Objective**: Verify V2 output is identical to original for every date Oct 1-31
- **Method**: EXCEPT-based comparison:
  ```sql
  SELECT * FROM curated.top_branches WHERE as_of = '{date}'
  EXCEPT
  SELECT * FROM double_secret_curated.top_branches WHERE as_of = '{date}'
  ```
  Must return 0 rows in both directions.

### TC-9: Column schema match
- **Objective**: Verify output columns match original schema exactly
- **Method**: Compare column names and types between curated and double_secret_curated tables

### TC-10: AP-2 validation -- no raw datalake access
- **Objective**: Verify V2 reads from curated.branch_visit_summary, not datalake.branch_visits
- **Method**: Inspect V2 job config to confirm DataSourcing uses schema "curated" and table "branch_visit_summary"

### TC-11: Upstream dependency
- **Objective**: Verify BranchVisitSummary runs before TopBranchesV2
- **Method**: Confirm SameDay dependency is declared in control.job_dependencies
