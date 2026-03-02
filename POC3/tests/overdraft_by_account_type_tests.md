# OverdraftByAccountType — V2 Test Plan

## Job Info
- **V2 Config**: `overdraft_by_account_type_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 External `ExternalModules.OverdraftByAccountTypeProcessor` eliminated via AP3)

## Pre-Conditions
1. PostgreSQL is accessible at `172.18.0.1` with `datalake` schema intact
2. Tables `datalake.overdraft_events` and `datalake.accounts` contain data for the effective date range starting `2024-10-01`
3. V1 baseline output exists at `Output/curated/overdraft_by_account_type/`
4. V2 config `overdraft_by_account_type_v2.json` is deployed to `JobExecutor/Jobs/`
5. V2 output directory is `Output/double_secret_curated/overdraft_by_account_type/`
6. The `dotnet build` succeeds with no errors

## Test Cases

### TC-1: Output Schema Validation
**Objective:** Verify V2 output Parquet file contains exactly the expected columns in the correct order.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 job for a single effective date | Job completes successfully |
| 2 | Read the output Parquet schema | Columns are: `account_type`, `account_count`, `overdraft_count`, `overdraft_rate`, `as_of` |
| 3 | Verify no extra or missing columns vs. V1 | Column names match exactly |

**Note (OQ-1):** V2 column CLR types will differ from V1 due to SQLite's type system:
- `account_count`: V1=INT32, V2=INT64
- `overdraft_count`: V1=INT32, V2=INT64
- `overdraft_rate`: V1=DECIMAL, V2=INT64
- `as_of`: V1=DATE, V2=STRING

If Proofmark fails on type differences, escalate to Tier 2 with a type-casting External module per FSD OQ-1 mitigation.

### TC-2: Row Count Equivalence
**Objective:** Verify V2 produces the same number of output rows as V1 for every effective date.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run both V1 and V2 for the full auto-advance date range | Both complete successfully |
| 2 | Compare row counts in V1 and V2 Parquet output for each effective date | Row counts match exactly |
| 3 | Verify output contains one row per account type present in accounts table | Expected types: Checking, Savings, Credit (3 rows) |

**Edge case:** If either source table is empty for a given date, output should have 0 rows (EC-4).

### TC-3: Data Content Equivalence (note W-codes)
**Objective:** Verify V2 output data matches V1 byte-for-byte (within Proofmark's comparison model).

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run Proofmark comparison between V1 and V2 output | 100% match threshold met |
| 2 | Verify `account_type` values match V1 exactly | String values identical |
| 3 | Verify `account_count` values match V1 | Inflated counts (all rows across all as_of dates) match |
| 4 | Verify `overdraft_count` values match V1 | Per-type overdraft counts match |
| 5 | Verify `overdraft_rate` is 0 for all rows (W4) | Integer division truncation reproduced |
| 6 | Verify `as_of` matches V1 (MIN(as_of) from overdraft_events) | Date value identical |

**W4 caveat:** `overdraft_rate` is always 0 due to integer division. If any row shows a non-zero rate, the W4 replication has failed.

### TC-4: Writer Configuration
**Objective:** Verify the ParquetFileWriter config matches V1 behavior.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Verify V2 config `source` is `"output"` | Matches V1 |
| 2 | Verify V2 config `numParts` is `1` | Matches V1 |
| 3 | Verify V2 config `writeMode` is `"Overwrite"` | Matches V1 (W9) |
| 4 | Run job for two consecutive effective dates | Only the second date's output survives in the output directory |
| 5 | Verify output directory is `Output/double_secret_curated/overdraft_by_account_type/` | V2 convention path |

### TC-5: Anti-Pattern Elimination Verification
**Objective:** Confirm all identified V1 anti-patterns are eliminated in V2.

| AP-Code | Check | Expected Result |
|---------|-------|-----------------|
| AP3 | V2 config has no `"type": "External"` module | No External module in the V2 module chain |
| AP4 (overdraft_events) | V2 sources only `["account_id"]` from overdraft_events | V1's `overdraft_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp` are removed |
| AP4 (accounts) | V2 sources only `["account_id", "account_type"]` from accounts | V1's `customer_id`, `account_status` are removed |
| AP6 | V2 uses SQL Transformation instead of C# foreach loops | All business logic is in a single SQL statement |

### TC-6: Edge Cases

#### TC-6a: Empty Source Data (EC-4)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 for an effective date with no overdraft events | Output is an empty Parquet file (0 rows) |
| 2 | Run V2 for an effective date with no accounts | Output is an empty Parquet file (0 rows) |

#### TC-6b: Account Count Inflation (EC-2)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Query `datalake.accounts` to count total rows per account_type across all as_of dates in the effective range | Get expected inflated counts |
| 2 | Compare V2 `account_count` per type against the inflated counts | Values match -- counts are NOT deduplicated by account_id |

#### TC-6c: Unknown Account Type Handling (EC-3)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Verify if any overdraft events reference account_ids not in the accounts table | Identify "Unknown" type events if any |
| 2 | Confirm output does NOT contain an "Unknown" account_type row | "Unknown" overdrafts are silently dropped (V1 behavior) |

#### TC-6d: Overwrite on Multi-Day Runs (EC-5)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 auto-advance across multiple effective dates | Job completes for each date |
| 2 | Inspect output directory after completion | Only the last effective date's output file exists |

### TC-7: Proofmark Configuration
**Objective:** Verify the Proofmark YAML config is correct and produces a valid comparison.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Validate Proofmark YAML structure | Valid YAML |
| 2 | Verify `comparison_target` is `"overdraft_by_account_type"` | Matches job name |
| 3 | Verify `reader` is `parquet` | Correct for Parquet output |
| 4 | Verify `threshold` is `100.0` | Full match required |
| 5 | Verify no `columns.excluded` entries | All columns are deterministic -- no exclusions needed |
| 6 | Verify no `columns.fuzzy` entries | All values are exact integers or strings -- no fuzzy tolerance needed |
| 7 | Run Proofmark with the config against V1 and V2 output | Comparison executes without config errors |

**Expected Proofmark config:**
```yaml
comparison_target: "overdraft_by_account_type"
reader: parquet
threshold: 100.0
```

## W-Code Test Cases

### TC-W4: Integer Division (W4)
**Objective:** Confirm that `overdraft_rate` is always 0 due to integer division, matching V1's bug.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 for any effective date with overdraft events | Job completes |
| 2 | Read all `overdraft_rate` values from V2 output | Every value is exactly `0` |
| 3 | Verify mathematically: for each row, `overdraft_count < account_count` | True for all rows (overdraft events are rare vs. inflated account counts) |
| 4 | Confirm V1 output also has all-zero `overdraft_rate` | V1 and V2 match on this column |
| 5 | Verify the SQL uses `COALESCE(otc.overdraft_count, 0) / atc.account_count` (integer operands) | SQLite performs integer division, truncating to 0 |

**Root cause:** V1 computes `(decimal)(odCount / accountCount)` where both are `int`. The integer division `odCount / accountCount` truncates before the cast to decimal. SQLite's integer division replicates this behavior natively.

### TC-W9: Overwrite Write Mode (W9)
**Objective:** Confirm that V2 uses Overwrite write mode, matching V1 behavior where only the last effective date's output survives.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Verify V2 config has `"writeMode": "Overwrite"` | Config matches V1 |
| 2 | Run V2 for effective date 2024-10-01 | Output file created |
| 3 | Run V2 for effective date 2024-10-02 | Output file replaced -- only 2024-10-02 data present |
| 4 | Compare behavior with V1 on the same dates | Both produce only the last date's output |

## Notes

1. **Type mismatch risk (OQ-1):** The biggest risk for this job is Parquet column type differences between V1 (INT32, DECIMAL, DATE) and V2 (INT64, INT64, STRING) due to SQLite's type system. If Proofmark normalizes values to strings for comparison, this is fine. If Proofmark is type-aware, it will fail, and the job must escalate to Tier 2 with a type-casting External module.

2. **Row ordering (OQ-2):** V1's output row order depends on .NET dictionary insertion order. V2's SQL output order depends on SQLite's GROUP BY behavior. Proofmark is order-independent (hash-based), so this should not cause failures.

3. **NULL handling:** V1 and V2 both coalesce NULL `account_type` to empty string. Verify this consistency if any accounts have NULL types in the source data.

4. **Data sourcing columns:** Verify that the framework automatically appends `as_of` to both DataSourcing queries, since it is not listed in the explicit columns arrays. The SQL depends on `as_of` being present in both `overdraft_events` and `accounts` tables within SQLite.

5. **Last-seen type lookup (BR-7):** The `last_seen_type` CTE uses `MAX(as_of)` to replicate V1's dictionary-overwrite behavior. If an account changes type across snapshot dates, the latest type wins. This is a MEDIUM-confidence replication -- Proofmark comparison will validate correctness.
