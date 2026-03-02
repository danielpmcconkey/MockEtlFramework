# SuspiciousWireFlags — Functional Specification Document

## 1. Job Summary

This job scans the `wire_transfers` table for wires that meet one of two suspicious-activity criteria: counterparty name containing the substring "OFFSHORE" (case-sensitive), or transfer amount strictly greater than $50,000. Flags are mutually exclusive with OFFSHORE taking priority over HIGH_AMOUNT. Wires matching neither criterion are excluded entirely. With the current dataset, no wires trigger either condition (max amount is $49,959, no "OFFSHORE" counterparties), so the output is an empty Parquet file with the correct schema. V1 also sources `accounts` and `customers` but never uses them — pure dead-end sourcing.

## 2. V2 Module Chain

**Tier:** 1 — Framework Only (`DataSourcing → Transformation (SQL) → ParquetFileWriter`)

**Tier Justification:** The V1 External module (`SuspiciousWireFlagProcessor.cs`) performs two operations: (1) a `foreach` loop checking each wire against two filter conditions, and (2) construction of output rows with a computed `flag_reason` column. Both are trivially expressible in SQL using `CASE WHEN` for the flag logic and a `WHERE` clause for exclusion. There is no procedural logic, no cross-date-range querying, no snapshot fallback, and no I/O quirk requiring an External module. Tier 1 is sufficient.

```
DataSourcing ("wire_transfers")
  → Transformation (SQL: CASE WHEN filter + flag assignment)
    → ParquetFileWriter
```

## 3. DataSourcing Config

### Module 1: DataSourcing — wire_transfers

| Property | Value |
|----------|-------|
| resultName | `wire_transfers` |
| schema | `datalake` |
| table | `wire_transfers` |
| columns | `wire_id`, `customer_id`, `direction`, `amount`, `counterparty_name`, `status` |

- Effective dates injected by executor via shared state (`__minEffectiveDate` / `__maxEffectiveDate`). No hardcoded dates in config.
- **Removed vs V1:** `counterparty_bank` — sourced by V1 but never referenced in output or logic (AP4, BR-6).
- **Removed vs V1:** The entire `accounts` DataSourcing entry (AP1, BR-4) and the entire `customers` DataSourcing entry (AP1, BR-5). Neither table is referenced anywhere in the processing logic.

### Effective Date Handling

The framework's DataSourcing module filters `wire_transfers.as_of` to the effective date range automatically. No additional date filtering is needed in the SQL. This matches V1 behavior where the External module receives pre-filtered data from DataSourcing and applies no further date logic.

## 4. Transformation SQL

```sql
-- SuspiciousWireFlagsV2: Flag wires with OFFSHORE counterparty or amount > $50,000
-- BR-1: OFFSHORE check is case-sensitive (V1 uses String.Contains without StringComparison)
-- BR-2: HIGH_AMOUNT check is mutually exclusive with OFFSHORE via CASE priority
-- BR-8: NULL counterparty_name coalesced to '' — will never match OFFSHORE
-- BR-11: Flag priority: OFFSHORE_COUNTERPARTY > HIGH_AMOUNT (CASE WHEN order)

SELECT
    wire_id,
    customer_id,
    direction,
    CAST(amount AS REAL) AS amount,                -- V1 converts via Convert.ToDecimal; CAST preserves value
    COALESCE(counterparty_name, '') AS counterparty_name,  -- BR-8: NULL → '' matches V1 ?? "" behavior
    status,
    CASE
        WHEN COALESCE(counterparty_name, '') LIKE '%OFFSHORE%'
            THEN 'OFFSHORE_COUNTERPARTY'           -- BR-1: case-sensitive LIKE in SQLite (default)
        WHEN CAST(amount AS REAL) > 50000
            THEN 'HIGH_AMOUNT'                     -- BR-2: strictly greater than $50,000
    END AS flag_reason,
    as_of
FROM wire_transfers
WHERE COALESCE(counterparty_name, '') LIKE '%OFFSHORE%'
   OR CAST(amount AS REAL) > 50000                 -- BR-3: exclude wires matching neither condition
```

### SQL Design Notes

1. **CASE WHEN ordering replicates if/else-if priority.** The CASE expression evaluates conditions top-to-bottom and returns the first match. This is semantically identical to V1's `if (contains OFFSHORE) ... else if (amount > 50000)` structure. A wire matching both conditions receives only `OFFSHORE_COUNTERPARTY` (BR-11).

2. **COALESCE for NULL handling.** V1 uses `row["counterparty_name"]?.ToString() ?? ""` which coalesces NULL to empty string. `COALESCE(counterparty_name, '')` produces the same behavior. An empty string will never match `%OFFSHORE%` (BR-8).

3. **SQLite LIKE is case-sensitive for ASCII by default.** SQLite's `LIKE` operator is case-insensitive only for ASCII A-Z. However, since V1 uses `String.Contains("OFFSHORE")` which is case-sensitive ordinal, and "OFFSHORE" is all uppercase, `LIKE '%OFFSHORE%'` in SQLite will match exactly the same strings — any string containing the literal `OFFSHORE`. If the string contained lowercase letters in the search term, we'd need `GLOB` instead, but `OFFSHORE` is all-caps so `LIKE` is safe here. **Correction:** SQLite's default LIKE is actually case-*insensitive* for ASCII letters. This means `LIKE '%OFFSHORE%'` would also match `'offshore'` or `'Offshore'`, while V1's `String.Contains("OFFSHORE")` would not. Per BR-1 and Edge Case 4, the check must be case-sensitive. **Use `INSTR()` instead:**

```sql
-- REVISED: Use INSTR for case-sensitive substring matching (matches V1 String.Contains)
SELECT
    wire_id,
    customer_id,
    direction,
    CAST(amount AS REAL) AS amount,
    COALESCE(counterparty_name, '') AS counterparty_name,
    status,
    CASE
        WHEN INSTR(COALESCE(counterparty_name, ''), 'OFFSHORE') > 0
            THEN 'OFFSHORE_COUNTERPARTY'
        WHEN CAST(amount AS REAL) > 50000
            THEN 'HIGH_AMOUNT'
    END AS flag_reason,
    as_of
FROM wire_transfers
WHERE INSTR(COALESCE(counterparty_name, ''), 'OFFSHORE') > 0
   OR CAST(amount AS REAL) > 50000
```

4. **INSTR is case-sensitive in SQLite.** `INSTR(haystack, 'OFFSHORE')` returns the 1-based position of the first occurrence, or 0 if not found. It is always case-sensitive, matching V1's `String.Contains("OFFSHORE")` behavior exactly (BR-1, Edge Case 4).

5. **WHERE clause mirrors the flag logic.** The WHERE clause excludes rows where `flag_reason` would be NULL (neither condition met). This is equivalent to V1's `if (flagReason != null)` guard (BR-3). We cannot use `WHERE flag_reason IS NOT NULL` because `flag_reason` is a computed alias not available in WHERE, so we duplicate the conditions.

6. **CAST(amount AS REAL).** V1 converts amount via `Convert.ToDecimal()`. The CAST ensures SQLite treats the amount as a numeric type for the `> 50000` comparison. Since the current dataset has no amounts exceeding 50,000 (BR-10), this comparison produces no matches regardless. The CAST in the SELECT list preserves the numeric value for Parquet output.

7. **Empty result set.** Per BR-10, the current dataset contains no OFFSHORE counterparties and no amounts > $50,000 (max is $49,959). The output will be a valid Parquet file with the correct 8-column schema but zero data rows. This matches V1 behavior.

## 5. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | `ParquetFileWriter` | YES |
| source | `output` | YES |
| outputDirectory | `Output/double_secret_curated/suspicious_wire_flags/` | Path updated per V2 convention |
| numParts | `1` | YES — [suspicious_wire_flags.json:35] |
| writeMode | `Overwrite` | YES — [suspicious_wire_flags.json:36] |

V1 output path: `Output/curated/suspicious_wire_flags/`
V2 output path: `Output/double_secret_curated/suspicious_wire_flags/`

## 6. Wrinkle Replication

| W-Code | Name | Applies? | V1 Evidence | V2 Disposition |
|--------|------|----------|-------------|----------------|
| W1 | Sunday skip | NO | No Sunday-specific behavior in V1 External module | N/A |
| W2 | Weekend fallback | NO | No weekend date logic in V1 | N/A |
| W3a/b/c | Boundary rows | NO | No summary row generation | N/A |
| W4 | Integer division | NO | V1 uses `Convert.ToDecimal` — no integer division | N/A |
| W5 | Banker's rounding | NO | No rounding in V1 — amounts are passed through | N/A |
| W6 | Double epsilon | NO | V1 uses `decimal` for amount comparison, not `double` | N/A |
| W7 | Trailer inflated count | NO | Parquet output — no trailer | N/A |
| W8 | Trailer stale date | NO | Parquet output — no trailer | N/A |
| W9 | Wrong writeMode | POSSIBLE | V1 uses Overwrite — multi-day auto-advance retains only last day's output. May be intentional given this is a "current flags" snapshot. | **REPRODUCED.** V2 uses `Overwrite` to match V1 exactly. Comment: `// V1 uses Overwrite — prior days' output replaced on each run.` |
| W10 | Absurd numParts | NO | numParts = 1, which is reasonable for a potentially-empty dataset | N/A |
| W12 | Header every append | NO | Not an Append-mode CSV job | N/A |

**Summary:** No confirmed output-affecting wrinkles for this job. W9 is a judgment call — Overwrite is plausible for a "current state" flags job but could also be a mistake. Either way, V2 matches V1's writeMode exactly.

## 7. Anti-Pattern Elimination

| AP-Code | Name | Applies? | V1 Evidence | V2 Disposition |
|---------|------|----------|-------------|----------------|
| AP1 | Dead-end sourcing | **YES** | `accounts` table sourced in `suspicious_wire_flags.json:12-19` but never referenced in `SuspiciousWireFlagProcessor.cs`. `customers` table sourced in `suspicious_wire_flags.json:20-27` but never referenced. (BR-4, BR-5) | **ELIMINATED.** V2 sources only `wire_transfers`. Both `accounts` and `customers` DataSourcing entries removed. |
| AP3 | Unnecessary External | **YES** | `SuspiciousWireFlagProcessor.cs` — entire logic is a `foreach` filter with two conditions and a `CASE`-equivalent flag assignment. Trivially expressible in SQL. | **ELIMINATED.** Replaced with Tier 1 SQL Transformation using `CASE WHEN` + `WHERE`. |
| AP4 | Unused columns | **YES** | `counterparty_bank` sourced from `wire_transfers` (json:10) but not in output schema and not referenced in logic (BR-6). `suffix` sourced from `customers` (json:24) but customers entirely unused (BR-7). | **ELIMINATED.** `counterparty_bank` removed from V2 wire_transfers column list. `customers` table removed entirely (see AP1). |
| AP6 | Row-by-row iteration | **YES** | `SuspiciousWireFlagProcessor.cs:29-58` — `foreach (var row in wireTransfers.Rows)` loop evaluates each wire individually. | **ELIMINATED.** Replaced with set-based SQL `CASE WHEN` + `WHERE` clause. |
| AP7 | Magic values | **YES** | `SuspiciousWireFlagProcessor.cs:35` — hardcoded `"OFFSHORE"` string. Line 39 — hardcoded `50000` threshold. (Comment on line 27 acknowledges this.) | **ELIMINATED.** SQL includes descriptive comments for each threshold: `-- BR-1: case-sensitive OFFSHORE substring check` and `-- BR-2: strictly greater than $50,000`. The values are the same (output must match) but are now documented with business context. |
| AP2 | Duplicated logic | NO | No cross-job duplication identified | N/A |
| AP5 | Asymmetric NULLs | NO | Only one NULL handling: `counterparty_name` coalesced to `""` (BR-8). No asymmetry since there's only one nullable field in the logic. | N/A |
| AP8 | Complex SQL / unused CTEs | NO | V1 doesn't use SQL — this is an External module replacement. V2 SQL is straightforward with no CTEs. | N/A |
| AP9 | Misleading names | NO | Job name `suspicious_wire_flags` accurately describes the output. | N/A |
| AP10 | Over-sourcing dates | NO | V1 uses framework effective date injection correctly via DataSourcing. | N/A |

## 8. Proofmark Config

```yaml
comparison_target: "suspicious_wire_flags"
reader: parquet
threshold: 100.0
```

### Proofmark Design Rationale

- **reader: parquet** — V1 and V2 both output Parquet files via `ParquetFileWriter`.
- **threshold: 100.0** — All fields are deterministic. No non-deterministic fields identified (BRD: "None identified" under Non-Deterministic Fields). Output must be identical.
- **No EXCLUDED columns** — No non-deterministic fields exist. Every output column is either a direct passthrough from source data or a deterministic computed value (`flag_reason`).
- **No FUZZY columns** — No floating-point accumulation or rounding is performed. The `amount` field is a passthrough (V1 converts to decimal then writes it out; V2 casts to REAL for comparison but the value is unchanged). If Proofmark reveals epsilon-level differences in the `amount` column due to CAST(amount AS REAL), a FUZZY override with tight absolute tolerance (e.g., 0.01) would be the fallback — but this is not expected since amounts are stored as `numeric` in PostgreSQL and should survive the round-trip.
- **Empty output note:** With current data, both V1 and V2 produce zero data rows. Proofmark should still validate that the schema (column names and types) matches between the two empty Parquet files.

## 9. V2 Job Config JSON

```json
{
  "jobName": "SuspiciousWireFlagsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "wire_transfers",
      "schema": "datalake",
      "table": "wire_transfers",
      "columns": ["wire_id", "customer_id", "direction", "amount", "counterparty_name", "status"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT wire_id, customer_id, direction, CAST(amount AS REAL) AS amount, COALESCE(counterparty_name, '') AS counterparty_name, status, CASE WHEN INSTR(COALESCE(counterparty_name, ''), 'OFFSHORE') > 0 THEN 'OFFSHORE_COUNTERPARTY' WHEN CAST(amount AS REAL) > 50000 THEN 'HIGH_AMOUNT' END AS flag_reason, as_of FROM wire_transfers WHERE INSTR(COALESCE(counterparty_name, ''), 'OFFSHORE') > 0 OR CAST(amount AS REAL) > 50000"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/suspicious_wire_flags/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Config Changes from V1

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| jobName | `SuspiciousWireFlags` | `SuspiciousWireFlagsV2` | V2 naming convention |
| DataSourcing entries | 3 (wire_transfers, accounts, customers) | 1 (wire_transfers only) | AP1: removed dead-end `accounts` and `customers` sources |
| wire_transfers columns | 7 (incl. `counterparty_bank`) | 6 (excl. `counterparty_bank`) | AP4: removed unused column |
| Module 2 (was accounts) | DataSourcing for accounts | Removed | AP1: dead-end source |
| Module 3 (was customers) | DataSourcing for customers | Removed | AP1: dead-end source |
| Module 4 (was External) | External (SuspiciousWireFlagProcessor) | Transformation (SQL) | AP3, AP6: replaced unnecessary External + row-by-row iteration with SQL |
| Output path | `Output/curated/suspicious_wire_flags/` | `Output/double_secret_curated/suspicious_wire_flags/` | V2 output directory convention |
| Writer config | numParts=1, writeMode=Overwrite | Identical | All writer params preserved |

## 10. Output Schema

| # | Column | Source | Transformation | Evidence |
|---|--------|--------|---------------|----------|
| 1 | wire_id | wire_transfers.wire_id | Pass-through | SuspiciousWireFlagProcessor.cs:48 |
| 2 | customer_id | wire_transfers.customer_id | Pass-through | SuspiciousWireFlagProcessor.cs:49 |
| 3 | direction | wire_transfers.direction | Pass-through | SuspiciousWireFlagProcessor.cs:50 |
| 4 | amount | wire_transfers.amount | CAST(amount AS REAL) — preserves numeric value | SuspiciousWireFlagProcessor.cs:32,51 |
| 5 | counterparty_name | wire_transfers.counterparty_name | COALESCE(counterparty_name, '') — NULL → empty string | SuspiciousWireFlagProcessor.cs:31,52 |
| 6 | status | wire_transfers.status | Pass-through | SuspiciousWireFlagProcessor.cs:53 |
| 7 | flag_reason | Computed | CASE WHEN: 'OFFSHORE_COUNTERPARTY' or 'HIGH_AMOUNT' | SuspiciousWireFlagProcessor.cs:37,40,54 |
| 8 | as_of | wire_transfers.as_of | Pass-through (injected by DataSourcing) | SuspiciousWireFlagProcessor.cs:55 |

## 11. Open Questions

1. **Empty output validation.** With current data, both V1 and V2 produce zero rows (BR-10). Proofmark comparison of two empty Parquet files should pass, but this means the business logic (OFFSHORE check, amount threshold) is effectively untested against real data. Is this acceptable, or should synthetic test data be injected to validate the flag logic?

2. **Amount type in Parquet.** V1 converts amount to `decimal` via `Convert.ToDecimal()` before writing to the output DataFrame. The Parquet schema may encode this as `Decimal128` or similar. V2's `CAST(amount AS REAL)` produces a `double` (SQLite REAL). If the Parquet writer encodes these differently (decimal vs double column type), Proofmark may flag a schema mismatch even though the values are identical. This needs verification during implementation. If it's an issue, the SQL can be adjusted or the CAST removed (SQLite may handle the comparison without an explicit cast depending on how DataSourcing loads the `amount` column).

3. **Row ordering.** V1's `foreach` iterates rows in DataSourcing return order. The V2 SQL has no explicit `ORDER BY`. Since the current output is empty, ordering is moot — but if future data triggers flags, row order between V1 and V2 could diverge. The SQL should add `ORDER BY as_of, wire_id` if Proofmark comparison fails on ordering. Not adding it now because V1 does not explicitly sort and adding an unnecessary ORDER BY could mask a real behavioral difference.

4. **Case sensitivity edge case (theoretical).** SQLite's `INSTR()` is case-sensitive for ASCII, matching V1's `String.Contains("OFFSHORE")`. However, if counterparty names ever contain non-ASCII characters, the case-sensitivity behavior could differ between C# and SQLite. With current data this is moot (no OFFSHORE strings at all), but worth noting for future datasets.

5. **BRD Open Question inheritance.** The BRD raises two open questions: (a) whether the job is effectively a no-op given current data thresholds, and (b) whether the OFFSHORE check should be case-insensitive. Both remain unresolved. V2 faithfully replicates V1 behavior regardless — these are product decisions, not implementation decisions.
