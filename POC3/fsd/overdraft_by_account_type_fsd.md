# OverdraftByAccountType -- Functional Specification Document

## 1. Job Summary

The `OverdraftByAccountTypeV2` job calculates overdraft rates per account type by joining overdraft events to account records, counting overdraft events and total account rows for each type (Checking, Savings, Credit), and computing an overdraft rate via integer division. The output is a Parquet file with one row per account type. A known integer-division bug (W4) causes `overdraft_rate` to always be 0 in practice, and V2 must reproduce this behavior for output equivalence. V2 eliminates V1's unnecessary External module (AP3), unused column sourcing (AP4), and row-by-row iteration (AP6) by expressing all business logic in a single SQL transformation.

---

## 2. V2 Module Chain

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** Every operation performed by V1's External module (`OverdraftByAccountTypeProcessor.cs`) is expressible in standard SQL:

- Account type lookup by last-seen `as_of` per `account_id` -> subquery with `MAX(as_of)` + JOIN
- Counting all account rows per type (inflated across dates) -> `COUNT(*) GROUP BY account_type`
- Counting overdraft events per type via lookup -> `JOIN` + `COUNT(*) GROUP BY`
- Integer division for overdraft_rate -> SQLite native integer division
- Scalar `as_of` from first overdraft row -> `MIN(as_of)`
- Exclusion of "Unknown" type from output -> `INNER JOIN` on lookup + `LEFT JOIN` from account counts

No procedural logic, snapshot fallback, or cross-boundary query pattern is required. Tier 1 is sufficient. The V1 External module exists solely because V1 chose a C# iteration approach (AP3), not because the logic demands it.

| Step | Module | Shared State Key | Purpose |
|------|--------|-----------------|---------|
| 1 | DataSourcing | `overdraft_events` | Pull overdraft event records for effective date range |
| 2 | DataSourcing | `accounts` | Pull account records for effective date range |
| 3 | Transformation | `output` | Execute all business logic in SQL |
| 4 | ParquetFileWriter | -- | Write `output` DataFrame to Parquet |

---

## 3. DataSourcing Config

### overdraft_events

| Property | Value |
|----------|-------|
| schema | `datalake` |
| table | `overdraft_events` |
| columns | `["account_id"]` |
| resultName | `overdraft_events` |
| minEffectiveDate | _(injected at runtime via shared state)_ |
| maxEffectiveDate | _(injected at runtime via shared state)_ |

The framework automatically appends `as_of` since it is not listed in the columns array. This provides both the `account_id` needed for the type lookup and the `as_of` needed for the scalar date value.

**Columns eliminated (AP4):** V1 sources `overdraft_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp` -- none are referenced in V1's processing logic. Only `account_id` (for the type lookup at [OverdraftByAccountTypeProcessor.cs:53]) and the framework-appended `as_of` (for the scalar date at [OverdraftByAccountTypeProcessor.cs:28]) are used.

### accounts

| Property | Value |
|----------|-------|
| schema | `datalake` |
| table | `accounts` |
| columns | `["account_id", "account_type"]` |
| resultName | `accounts` |
| minEffectiveDate | _(injected at runtime via shared state)_ |
| maxEffectiveDate | _(injected at runtime via shared state)_ |

The framework automatically appends `as_of` since it is not listed in the columns array. This provides the `as_of` column needed by the `last_seen_type` CTE.

**Columns eliminated (AP4):** V1 sources `customer_id` and `account_status` -- neither is referenced in V1's processing logic. Only `account_id` (for the lookup at [OverdraftByAccountTypeProcessor.cs:34]) and `account_type` (for grouping/counting at [OverdraftByAccountTypeProcessor.cs:43-44]) are used.

---

## 4. Transformation SQL

```sql
/*
 * OverdraftByAccountTypeV2 -- Transformation SQL
 *
 * Replicates V1 OverdraftByAccountTypeProcessor behavior:
 * 1. Counts ALL account rows per account_type (inflated across all as_of dates) [BR-2, EC-2]
 * 2. Builds a last-seen account_type lookup per account_id [BR-7]
 * 3. Counts overdraft events per account type using the last-seen lookup [BR-3]
 * 4. Computes overdraft_rate via integer division (W4: always 0) [BR-4]
 * 5. Uses MIN(as_of) from overdraft_events as the scalar as_of for all rows [BR-5]
 * 6. Iterates account types from accounts only, silently dropping "Unknown" [BR-6, EC-3]
 */
WITH
  /* CTE 1: Count ALL account rows per type (BR-2, EC-2)
     V1 iterates ALL accounts.Rows without date filtering, counting each
     row once. An account appearing on 92 as_of dates is counted 92 times.
     Evidence: [OverdraftByAccountTypeProcessor.cs:40-47] */
  account_type_counts AS (
    SELECT
      COALESCE(account_type, '') AS account_type,
      COUNT(*) AS account_count
    FROM accounts
    GROUP BY COALESCE(account_type, '')
  ),

  /* CTE 2: Last-seen account_type per account_id (BR-7)
     V1 iterates accounts rows (ordered by as_of ascending via DataSourcing)
     and overwrites accountTypeLookup[accountId] = accountType on each row.
     The last row for each account_id wins, which is the row with MAX(as_of).
     Evidence: [OverdraftByAccountTypeProcessor.cs:34] */
  last_seen_type AS (
    SELECT a.account_id, COALESCE(a.account_type, '') AS account_type
    FROM accounts a
    INNER JOIN (
      SELECT account_id, MAX(as_of) AS max_as_of
      FROM accounts
      GROUP BY account_id
    ) latest ON a.account_id = latest.account_id AND a.as_of = latest.max_as_of
  ),

  /* CTE 3: Count overdraft events per account type (BR-3)
     V1 iterates overdraft_events, looks up each event's account_id in the
     last-seen type lookup via INNER JOIN -- events with unmapped account_ids
     are excluded here. Additionally, the final SELECT iterates account_type_counts
     keys only, so "Unknown" type overdrafts never appear in output (EC-3).
     Evidence: [OverdraftByAccountTypeProcessor.cs:50-61, 54-56, 64] */
  overdraft_type_counts AS (
    SELECT
      lst.account_type,
      COUNT(*) AS overdraft_count
    FROM overdraft_events oe
    INNER JOIN last_seen_type lst ON oe.account_id = lst.account_id
    GROUP BY lst.account_type
  ),

  /* CTE 4: Scalar as_of from the first overdraft_events row (BR-5)
     DataSourcing returns rows ORDER BY as_of, so Rows[0]["as_of"] is MIN(as_of).
     Evidence: [OverdraftByAccountTypeProcessor.cs:28, DataSourcing.cs:85] */
  first_as_of AS (
    SELECT MIN(as_of) AS as_of FROM overdraft_events
  )

/* Final SELECT: One row per account type from account_type_counts (BR-6).
   LEFT JOIN to overdraft_type_counts ensures types with zero overdrafts
   still appear with overdraft_count = 0.
   CROSS JOIN to first_as_of applies the scalar as_of to every row.
   Evidence: [OverdraftByAccountTypeProcessor.cs:64-82] */
SELECT
  atc.account_type,
  atc.account_count,
  COALESCE(otc.overdraft_count, 0) AS overdraft_count,
  /* W4: integer division truncates to 0 -- V1 bug replicated for output equivalence.
     V1 code: (decimal)(odCount / accountCount) where both are int.
     SQLite integer division produces the same truncation when both operands are integers.
     Evidence: [OverdraftByAccountTypeProcessor.cs:71] */
  COALESCE(otc.overdraft_count, 0) / atc.account_count AS overdraft_rate,
  fa.as_of
FROM account_type_counts atc
LEFT JOIN overdraft_type_counts otc ON atc.account_type = otc.account_type
CROSS JOIN first_as_of fa
```

### SQL Design Notes

1. **Account count inflation (BR-2, EC-2):** `account_type_counts` counts ALL rows in the accounts DataFrame, not distinct accounts. V1 iterates `accounts.Rows` without filtering by date [OverdraftByAccountTypeProcessor.cs:40-47]. An account present across 92 `as_of` dates is counted 92 times.

2. **Last-seen type lookup (BR-7):** The `last_seen_type` CTE finds each `account_id`'s type from the row with `MAX(as_of)`. This replicates V1's dictionary overwrite behavior where iterating rows ordered by `as_of` means the last `as_of`'s value wins [OverdraftByAccountTypeProcessor.cs:34]. Within a single `as_of`, each `account_id` appears at most once, so the `MAX(as_of)` subquery is unambiguous.

3. **Unknown type handling (EC-3):** The `overdraft_type_counts` CTE uses `INNER JOIN` to `last_seen_type`, so overdraft events with `account_id` values not found in accounts are excluded from the count entirely. The final SELECT drives from `account_type_counts` via `LEFT JOIN`, so only account types present in the accounts table appear in output. This matches V1 where "Unknown" overdrafts accumulate in `overdraftCounts` but never appear in the output because the output loop iterates `accountCounts` keys [OverdraftByAccountTypeProcessor.cs:54-56, 64].

4. **Integer division (W4):** SQLite performs integer division when both operands are integers. `COALESCE(otc.overdraft_count, 0) / atc.account_count` returns an integer, truncating to 0 when `overdraft_count < account_count`. This matches V1's `(decimal)(odCount / accountCount)` where the integer division happens before the cast to decimal [OverdraftByAccountTypeProcessor.cs:71].

5. **Scalar as_of (BR-5):** `MIN(as_of)` from `overdraft_events` matches V1's `overdraftEvents.Rows[0]["as_of"]` because DataSourcing returns rows `ORDER BY as_of` ascending [DataSourcing.cs:85], making `Rows[0]` the row with the minimum date.

6. **NULL handling:** `COALESCE(account_type, '')` replicates V1's `acct["account_type"]?.ToString() ?? ""` null coalescence [OverdraftByAccountTypeProcessor.cs:35, 43]. This ensures null account types are treated consistently as empty string in both the lookup and the count.

---

## 5. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | `ParquetFileWriter` | YES |
| source | `output` | YES |
| outputDirectory | `Output/double_secret_curated/overdraft_by_account_type/` | Path change only (V2 convention) |
| numParts | `1` | YES -- [overdraft_by_account_type.json:28] |
| writeMode | `Overwrite` | YES -- [overdraft_by_account_type.json:29] |

No trailer, no lineEnding, no includeHeader (Parquet, not CSV).

---

## 6. Wrinkle Replication

### W4: Integer Division

- **V1 behavior:** `decimal overdraftRate = (decimal)(odCount / accountCount);` where both `odCount` and `accountCount` are `int`. The integer division `odCount / accountCount` truncates to 0 before the cast to decimal. Since `overdraft_count` per type is always much less than `account_count` per type (overdrafts are rare events vs. inflated account counts across all snapshot dates), the rate is always 0.
- **V2 replication:** SQLite natively performs integer division when both operands are integer types. `COALESCE(otc.overdraft_count, 0) / atc.account_count` produces the same truncation. Both `COUNT(*)` results are integers in SQLite.
- **Evidence:** [OverdraftByAccountTypeProcessor.cs:71], [BRD:BR-4], [BRD:EC-1]
- **Comment in SQL:** `/* W4: integer division truncates to 0 -- V1 bug replicated for output equivalence */`

### W9: Overwrite Write Mode

- **V1 behavior:** `writeMode: "Overwrite"` in the job config means each execution replaces the entire Parquet directory. On multi-day auto-advance runs, only the final effective date's output survives.
- **V2 replication:** V2 uses the same `"writeMode": "Overwrite"` configuration. The framework's ParquetFileWriter handles this identically.
- **Evidence:** [overdraft_by_account_type.json:29], [BRD:EC-5]

### Other W-codes: Not Applicable

| W-code | Reason |
|--------|--------|
| W1 (Sunday skip) | No day-of-week logic in V1 |
| W2 (Weekend fallback) | No weekend date substitution |
| W3a/b/c (Boundary summaries) | No summary rows appended |
| W5 (Banker's rounding) | No rounding operations |
| W6 (Double epsilon) | No double-precision monetary accumulation |
| W7 (Trailer inflated count) | No trailer (Parquet output) |
| W8 (Trailer stale date) | No trailer |
| W10 (Absurd numParts) | numParts=1, reasonable |
| W12 (Header every append) | Not CSV, not Append mode |

---

## 7. Anti-Pattern Elimination

### AP3: Unnecessary External Module -- ELIMINATED

- **V1 problem:** V1 uses `ExternalModules.OverdraftByAccountTypeProcessor` as an External module, but every operation it performs (dictionary lookup, counting via foreach, integer division, scalar extraction) is directly expressible in SQL.
- **V2 elimination:** V2 uses Tier 1 (DataSourcing + Transformation + ParquetFileWriter). All business logic is in the Transformation SQL. No External module is needed.
- **Evidence:** [OverdraftByAccountTypeProcessor.cs:8-85] -- the entire External module is three foreach loops, a dictionary, and arithmetic.

### AP4: Unused Columns -- ELIMINATED

- **V1 problem:** V1 sources 7 columns from `overdraft_events` (`overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`) but only uses `account_id` and the framework-appended `as_of`. V1 sources 4 columns from `accounts` (`account_id`, `customer_id`, `account_type`, `account_status`) but only uses `account_id` and `account_type`.
- **V2 elimination:** V2 sources only `account_id` from `overdraft_events` and `account_id`, `account_type` from `accounts`. The framework appends `as_of` automatically.
- **Evidence:** [overdraft_by_account_type.json:10-11, 17-18] vs. actual usage in [OverdraftByAccountTypeProcessor.cs:28,34-35,43,53]

### AP6: Row-by-Row Iteration -- ELIMINATED

- **V1 problem:** V1 uses three separate `foreach` loops: one to build the account type lookup dictionary [lines 32-37], one to count accounts per type [lines 41-47], and one to count overdrafts per type [lines 51-61]. All are classic row-by-row iteration that SQL can replace with set-based operations.
- **V2 elimination:** V2 replaces all three loops with SQL: `GROUP BY` for counting, a subquery with `MAX(as_of)` for the last-seen type lookup, and `JOIN` for combining results.
- **Evidence:** [OverdraftByAccountTypeProcessor.cs:32-37, 41-47, 51-61] -- BRD explicitly marks these as AP6.

### Other AP-codes: Not Applicable

| AP-code | Status | Reason |
|---------|--------|--------|
| AP1 (Dead-end sourcing) | N/A | Both sourced tables are used in the logic |
| AP2 (Duplicated logic) | N/A | No cross-job duplication identified |
| AP5 (Asymmetric NULLs) | N/A | V1's null-to-empty-string coalescence is consistent across both lookup and count; V2 replicates with `COALESCE` |
| AP7 (Magic values) | N/A | No hardcoded thresholds. The only string literal is `"Unknown"`, a fallback that never reaches output (EC-3) |
| AP8 (Complex SQL / unused CTEs) | N/A | V1 has no SQL. V2's CTEs are all consumed by the final SELECT |
| AP9 (Misleading names) | N/A | Job name accurately describes what it produces |
| AP10 (Over-sourcing dates) | N/A | V1 uses framework-injected effective dates via shared state, not manual date filtering. V2 does the same |

---

## 8. Proofmark Config

### Column Analysis

| Column | Comparison Mode | Justification |
|--------|----------------|---------------|
| account_type | STRICT | String values, deterministic, no precision concern |
| account_count | STRICT | Integer count, deterministic |
| overdraft_count | STRICT | Integer count, deterministic |
| overdraft_rate | STRICT | Always 0 due to W4. Integer zero is exact regardless of type representation |
| as_of | STRICT | Derived from MIN(as_of) on overdraft_events, deterministic for a given effective date range |

**Excluded columns:** None. All columns are deterministic (BRD: "Non-Deterministic Fields: None identified").

**Fuzzy columns:** None. No floating-point accumulation, no rounding differences. The `overdraft_rate` is always exactly 0.

**Row ordering:** Proofmark is order-independent (hash-based row identification per README), so output row order differences between V1's dictionary iteration and V2's SQL result order will not cause comparison failures.

### YAML Config

```yaml
comparison_target: "overdraft_by_account_type"
reader: parquet
threshold: 100.0
```

---

## 9. Open Questions

### OQ-1: Parquet Column Type Differences (SQLite Type System)

**Issue:** The Transformation module processes data through SQLite, which has a limited type system. When the SQL result is read back into a DataFrame, the CLR types differ from V1:

| Column | V1 CLR Type | V2 CLR Type (via SQLite) | V1 Parquet Type | V2 Parquet Type |
|--------|-------------|--------------------------|-----------------|-----------------|
| account_type | string | string | STRING | STRING |
| account_count | int | long | INT32 | INT64 |
| overdraft_count | int | long | INT32 | INT64 |
| overdraft_rate | decimal | long | DECIMAL | INT64 |
| as_of | DateOnly | string | DATE | STRING |

SQLite's `COUNT(*)` returns 64-bit integers (CLR `long`), not 32-bit (CLR `int`). SQLite stores dates as TEXT. The ParquetFileWriter maps CLR types to Parquet types, so the Parquet schema will differ between V1 and V2.

**Risk assessment:** MEDIUM. Proofmark uses "hash-based row identification" and "byte-for-byte match" per its README. If Proofmark normalizes values to strings for comparison (e.g., comparing "0" vs "0" regardless of source Parquet type), this is a non-issue. If Proofmark is type-aware and considers INT32(0) different from INT64(0), comparison will fail.

**Mitigation if Proofmark fails on types:** Escalate to Tier 2 with a minimal External module that casts the SQL result's column values to the correct CLR types (`int` for counts, `decimal` for rate, `DateOnly` for as_of) before the ParquetFileWriter runs. This would be the ONLY function of the External module -- no business logic, just type conversion. The Tier 2 design would be:

```
DataSourcing -> Transformation (SQL) -> External (type cast only) -> ParquetFileWriter
```

**Recommendation:** Proceed with Tier 1. If Phase D Proofmark comparison fails with type-related mismatches, apply the Tier 2 escalation documented above.

### OQ-2: Dictionary Iteration Order vs. SQL Output Order

**Issue:** V1's output row order depends on `Dictionary<string, int>` iteration order, which in .NET follows insertion order. The insertion order depends on the order account types are first encountered when iterating accounts rows (ordered by `as_of`). V2's SQL output order depends on SQLite's `GROUP BY` behavior, which is not guaranteed.

**Risk assessment:** LOW. Proofmark is explicitly order-independent per its README ("Row order does not affect comparison results"). This should not cause comparison failure.

### OQ-3: Is the Integer Division Intentional?

**Issue:** The BRD notes that `overdraft_rate` is always 0 due to W4 integer division [BRD:BR-4, EC-1]. The `W4` comment in V1 source confirms this is a known issue. The column is functionally useless.

**Resolution:** Not within scope of V2 rewrite. V2 must reproduce the behavior for output equivalence. The W4 wrinkle is replicated via SQLite's native integer division.

---

## Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|----------------|-------------|-----------------|
| BR-1: Account type lookup | SQL CTE `last_seen_type` | JOIN-based lookup replaces dictionary |
| BR-2: Account count (all rows, inflated) | SQL CTE `account_type_counts` | `COUNT(*)` on all rows, no date filtering |
| BR-3: Overdraft count per type | SQL CTE `overdraft_type_counts` | `COUNT(*)` with JOIN to `last_seen_type` |
| BR-4: Integer division bug (W4) | SQL final SELECT | SQLite native integer division replicates truncation |
| BR-5: as_of from first row | SQL CTE `first_as_of` | `MIN(as_of)` matches `Rows[0]["as_of"]` |
| BR-6: All types included (even zero overdrafts) | SQL final SELECT | `LEFT JOIN` from `account_type_counts` ensures all types appear |
| BR-7: Last-seen account type | SQL CTE `last_seen_type` | `MAX(as_of)` subquery replicates dictionary overwrite |
| BR-8: Runtime date injection | DataSourcing config | No hardcoded dates; framework injects effective dates |
| EC-1: Rate always 0 | W4 replication | SQLite integer division produces 0 |
| EC-2: Account count inflation | SQL CTE `account_type_counts` | `COUNT(*)` without date filtering, same as V1 |
| EC-3: Unknown type lost | SQL INNER JOIN + LEFT JOIN | Unknown overdrafts excluded by INNER JOIN; output driven by account types only |
| EC-4: Empty source data | Framework behavior | Transformation with empty DataFrames returns empty result; writer outputs empty Parquet |
| EC-5: Overwrite on multi-day | Writer config | `writeMode: Overwrite` matches V1 |
| W4: Integer division | Section 6 | Replicated via SQLite integer division |
| W9: Wrong writeMode | Section 6 | Replicated with identical `Overwrite` config |
| AP3: Unnecessary External | Section 7 | Eliminated -- Tier 1 replaces External with SQL |
| AP4: Unused columns | Section 7 | Eliminated -- only needed columns sourced |
| AP6: Row-by-row iteration | Section 7 | Eliminated -- SQL set-based operations replace foreach loops |
