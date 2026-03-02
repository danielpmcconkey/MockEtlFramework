# OverdraftCustomerProfile — V2 Test Plan

## Job Info
- **V2 Config**: `overdraft_customer_profile_v2.json`
- **Tier**: 1 (Framework Only)
- **External Module**: None (V1 External `ExternalModules.OverdraftCustomerProfileProcessor` eliminated via AP3)

## Pre-Conditions
1. PostgreSQL is accessible at `172.18.0.1` with `datalake` schema intact
2. Tables `datalake.overdraft_events` and `datalake.customers` contain data for the effective date range starting `2024-10-01`
3. V1 baseline output exists at `Output/curated/overdraft_customer_profile/`
4. V2 config `overdraft_customer_profile_v2.json` is deployed to `JobExecutor/Jobs/`
5. V2 output directory is `Output/double_secret_curated/overdraft_customer_profile/`
6. The `dotnet build` succeeds with no errors
7. Know which effective dates in the range fall on weekdays vs. weekends (Saturday/Sunday) for W2 testing

## Test Cases

### TC-1: Output Schema Validation
**Objective:** Verify V2 output Parquet file contains exactly the expected columns in the correct order.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 job for a weekday effective date with known overdraft events | Job completes successfully |
| 2 | Read the output Parquet schema | Columns are: `customer_id`, `first_name`, `last_name`, `overdraft_count`, `total_overdraft_amount`, `avg_overdraft`, `as_of` |
| 3 | Verify no extra or missing columns vs. V1 | Column names match exactly |

**Note:** V2 column CLR types may differ from V1 due to SQLite's type system:
- `customer_id`: V1=int, V2=long (INT64)
- `overdraft_count`: V1=int, V2=long (INT64)
- `total_overdraft_amount`: V1=decimal, V2=double (REAL)
- `avg_overdraft`: V1=decimal, V2=double (REAL)
- `as_of`: V1=string, V2=string (both string -- should match)

If Proofmark fails on type differences, escalate to Tier 2 per FSD guidance.

### TC-2: Row Count Equivalence
**Objective:** Verify V2 produces the same number of output rows as V1 for every effective date.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run both V1 and V2 for the full auto-advance date range | Both complete successfully |
| 2 | Compare row counts in V1 and V2 output for each effective date | Row counts match exactly |
| 3 | For weekday dates, verify row count equals distinct customer_ids with overdraft events on that date | One row per customer with overdrafts |
| 4 | For weekend dates (Saturday/Sunday), verify output has 0 rows | Weekend fallback (W2) produces empty output |

### TC-3: Data Content Equivalence (note W-codes)
**Objective:** Verify V2 output data matches V1 within Proofmark's comparison model.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run Proofmark comparison between V1 and V2 output | 100% match threshold met |
| 2 | Verify `customer_id` values match V1 | Same set of customers in both outputs |
| 3 | Verify `first_name` and `last_name` values match V1 | Customer name lookup produces identical results |
| 4 | Verify `overdraft_count` values match V1 | Per-customer event counts match |
| 5 | Verify `total_overdraft_amount` values match V1 | Sum of overdraft amounts per customer matches |
| 6 | Verify `avg_overdraft` values match V1 | Rounded to 2 decimal places; W5 rounding risk acknowledged |
| 7 | Verify `as_of` column shows the weekend-adjusted target date | On weekdays: effective date as-is. On weekends: previous Friday. |

**W2 caveat:** On weekends, `as_of` reflects Friday's date, not the effective date. Empty output is expected on weekends since no events have Friday's `as_of` when the executor runs Saturday/Sunday.

**W5 caveat:** If `avg_overdraft` mismatches on exact midpoint values (X.XX5), escalate to Tier 2 with a minimal rounding External module per FSD Open Question #2.

### TC-4: Writer Configuration
**Objective:** Verify the ParquetFileWriter config matches V1 behavior.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Verify V2 config `source` is `"output"` | Matches V1 |
| 2 | Verify V2 config `numParts` is `1` | Matches V1 |
| 3 | Verify V2 config `writeMode` is `"Overwrite"` | Matches V1 |
| 4 | Run job for two consecutive weekday effective dates | Only the second date's output survives in the output directory |
| 5 | Verify output directory is `Output/double_secret_curated/overdraft_customer_profile/` | V2 convention path |

### TC-5: Anti-Pattern Elimination Verification
**Objective:** Confirm all identified V1 anti-patterns are eliminated in V2.

| AP-Code | Check | Expected Result |
|---------|-------|-----------------|
| AP1 | V2 config does NOT source `datalake.accounts` | Dead-end sourcing eliminated -- accounts table was never used by V1 processor |
| AP3 | V2 config has no `"type": "External"` module | External module replaced with SQL Transformation |
| AP4 (overdraft_events) | V2 sources only `["customer_id", "overdraft_amount"]` from overdraft_events | V1's `overdraft_id`, `account_id`, `fee_amount`, `fee_waived`, `event_timestamp` are removed |
| AP4 (customers) | V2 sources only `["id", "first_name", "last_name"]` from customers | V1's `prefix`, `suffix`, `birthdate` are removed |
| AP6 | V2 uses SQL Transformation instead of three C# foreach loops | All business logic (weekend fallback, filtering, joining, grouping, aggregation) is in a single SQL statement |

### TC-6: Edge Cases

#### TC-6a: Weekend Fallback Empty Output (EC-1)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Identify a Saturday effective date in the range (e.g., 2024-10-05) | Date confirmed as Saturday |
| 2 | Run V2 for that Saturday | Job completes, output has 0 rows |
| 3 | Identify a Sunday effective date in the range (e.g., 2024-10-06) | Date confirmed as Sunday |
| 4 | Run V2 for that Sunday | Job completes, output has 0 rows |
| 5 | Run V1 for the same weekend dates | V1 also produces 0 rows |

**Explanation:** W2 shifts the target date to Friday, but the sourced data has Saturday/Sunday `as_of` values. The `WHERE oe.as_of = e.target_date` filter finds no matches, producing empty output.

#### TC-6b: No Events on Target Date (EC-2)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 for a weekday date known to have zero overdraft events | Job completes |
| 2 | Verify output is empty (0 rows) | Empty DataFrame written to Parquet |

#### TC-6c: Missing Customer in Lookup (BR-8)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Check if any `customer_id` in overdraft_events is absent from customers table | Identify orphan customer_ids |
| 2 | If orphan exists, verify V2 output has empty strings for `first_name` and `last_name` | LEFT JOIN + COALESCE defaults to `''` |
| 3 | Compare with V1 output for same customer_id | V1 also defaults to empty strings |

#### TC-6d: Customer Name Staleness (EC-4)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Check if any customer has name changes across `as_of` dates | Identify if applicable |
| 2 | If applicable, verify V2 uses the name from the latest `as_of` date | `GROUP BY id HAVING as_of = MAX(as_of)` selects latest |
| 3 | Compare with V1 (which uses last-loaded dictionary overwrite) | Results should match -- both use latest snapshot |

#### TC-6e: Overwrite on Multi-Day Runs (EC-6)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 auto-advance across multiple effective dates | Job completes for each date |
| 2 | Inspect output directory after completion | Only the last effective date's output file exists |

#### TC-6f: Decimal Precision in Aggregation (EC-5)
| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | For a customer with known overdraft amounts, manually compute `SUM / COUNT` rounded to 2dp | Get expected `avg_overdraft` |
| 2 | Compare V2 output `avg_overdraft` against manual calculation | Values match |
| 3 | Compare V2 `total_overdraft_amount` against manual SUM | Values match |

### TC-7: Proofmark Configuration
**Objective:** Verify the Proofmark YAML config is correct and produces a valid comparison.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Validate Proofmark YAML structure | Valid YAML |
| 2 | Verify `comparison_target` is `"overdraft_customer_profile"` | Matches job name |
| 3 | Verify `reader` is `parquet` | Correct for Parquet output |
| 4 | Verify `threshold` is `100.0` | Full match required |
| 5 | Verify no `columns.excluded` entries | All columns are deterministic -- no exclusions needed |
| 6 | Verify no `columns.fuzzy` entries | Starting strict per FSD guidance; escalate if W5 triggers |
| 7 | Run Proofmark with the config against V1 and V2 output | Comparison executes without config errors |

**Expected Proofmark config:**
```yaml
comparison_target: "overdraft_customer_profile"
reader: parquet
threshold: 100.0
```

**Escalation note:** If Proofmark reports mismatches on `avg_overdraft` due to W5 (banker's rounding vs. half-away-from-zero), do NOT add a fuzzy tolerance. Instead, escalate to Tier 2 and fix the code to match V1 output exactly (per FSD Open Question #2).

## W-Code Test Cases

### TC-W2: Weekend Fallback (W2)
**Objective:** Confirm that V2 replicates V1's Saturday-to-Friday and Sunday-to-Friday date fallback behavior.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 for a known Saturday (e.g., 2024-10-05) | Output is empty (0 rows) |
| 2 | Run V2 for a known Sunday (e.g., 2024-10-06) | Output is empty (0 rows) |
| 3 | Run V2 for the preceding Friday (e.g., 2024-10-04) | Output contains rows (if overdraft events exist on that date) |
| 4 | If Friday has output, check `as_of` column value | Shows `2024-10-04` (Friday's date) |
| 5 | Run V1 for the same Saturday and Sunday | V1 also produces 0 rows |
| 6 | Verify the SQL `effective` CTE: Saturday (strftime '%w' = 6) shifts -1 day, Sunday (strftime '%w' = 0) shifts -2 days | Logic matches V1's `AddDays(-1)` / `AddDays(-2)` |

**Mechanism:** The SQL uses `CASE CAST(strftime('%w', as_of) AS INTEGER) WHEN 6 THEN date(as_of, '-1 day') WHEN 0 THEN date(as_of, '-2 days') ELSE as_of END`. On weekends, the target_date shifts to Friday but no sourced rows have that `as_of`, so the filter produces zero rows.

### TC-W5: Banker's Rounding Risk (W5)
**Objective:** Assess whether SQLite's `ROUND()` produces different results from V1's `Math.Round()` for `avg_overdraft`.

| Step | Action | Expected Result |
|------|--------|-----------------|
| 1 | Run V2 for all effective dates in the range | All jobs complete |
| 2 | Run Proofmark comparison on `avg_overdraft` column | 100% match (optimistic -- midpoint hits are rare) |
| 3 | If Proofmark reports mismatches, inspect the failing values | Check if the third decimal is exactly 5 (midpoint) |
| 4 | If midpoint mismatch confirmed, escalate to Tier 2 | Add minimal External module for `Math.Round(value, 2, MidpointRounding.ToEven)` |
| 5 | Manual spot-check: for a few customers, compute `SUM(overdraft_amount) / COUNT(*)` and verify rounding behavior | Both V1 and V2 produce the same rounded value |

**Risk level:** MEDIUM. V1 uses `Math.Round(value, 2)` which defaults to `MidpointRounding.ToEven`. SQLite's `ROUND(value, 2)` uses half-away-from-zero. These differ only when the third decimal digit is exactly 5. For this dataset, the probability is low but non-zero.

## Notes

1. **Dead-end accounts table (AP1):** V1 sources `datalake.accounts` but the External module never reads `sharedState["accounts"]`. V2 eliminates this entirely. Verify the V2 config has exactly 2 DataSourcing entries (`overdraft_events` and `customers`), not 3.

2. **Weekend behavior vs. empty data:** Weekend empty output (EC-1 via W2) and no-events empty output (EC-2) are different scenarios but produce the same result (0 rows). Test both paths independently: a weekend date with no matching data due to date shift, and a weekday date with genuinely no overdraft events.

3. **Single-day execution model:** The FSD assumes min=max effective date (one day at a time). The `effective` CTE uses `LIMIT 1` from `overdraft_events` to extract the single `as_of` value. If the executor ever passes a multi-day range (min != max), the `LIMIT 1` could pick an arbitrary row. This is acceptable because the executor always calls with single-day ranges.

4. **Customer lookup confidence (OQ-1):** The `customer_lookup` CTE uses `GROUP BY id HAVING as_of = MAX(as_of)` to select the latest name. V1 uses dictionary-overwrite (last row wins). With single-day sourcing, there is one row per customer, making this a degenerate case. Risk is LOW for single-day execution.

5. **as_of output format (BR-9):** V1 formats `as_of` as `targetDate.ToString("yyyy-MM-dd")`. V2's SQL outputs `e.target_date` which is already a `yyyy-MM-dd` string from SQLite's `date()` function. The string format should match.

6. **Parquet type differences:** Similar to `overdraft_by_account_type`, SQLite's type system may produce different Parquet column types (INT64 instead of INT32, REAL instead of DECIMAL). Monitor Proofmark results for type-related failures.
