# PeakTransactionTimes — Functional Specification Document

## 1. Job Summary

The `PeakTransactionTimes` job produces an hourly breakdown of transaction volume and total amounts, grouped by the hour of day (0-23) extracted from `txn_timestamp`. The output is a CSV file with a header, data rows ordered by `hour_of_day ASC`, and a trailer line. The trailer uses the **input transaction count** (before hourly grouping) rather than the output row count — a known wrinkle (W7) that must be replicated for output equivalence. V2 replaces V1's monolithic External module with DataSourcing + SQL Transformation for all business logic, using a minimal External module only to handle the W7 trailer count and the V1 encoding (UTF-8 with BOM).

## 2. V2 Module Chain

**Tier: 2 — Framework + Minimal External (SCALPEL)**

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Pull `txn_timestamp` and `amount` from `datalake.transactions` |
| 2 | Transformation | SQL: hourly aggregation, rounding, `as_of` column |
| 3 | External (minimal) | Write CSV with W7 inflated trailer and V1-matching UTF-8 BOM encoding |

**Tier justification:** Tier 1 is insufficient because:

1. **W7 (trailer inflated count):** The framework's CsvFileWriter trailer token `{row_count}` substitutes the output DataFrame's `df.Count` — which is the number of hourly buckets (up to 24). V1's trailer uses the input transaction count (e.g., thousands of rows). CsvFileWriter has no mechanism for a custom count token. A minimal External is required to write the CSV with the correct trailer count.
2. **BR-10 (UTF-8 with BOM):** V1 uses `new StreamWriter(path, append: false)` which defaults to UTF-8 with BOM. The framework's CsvFileWriter explicitly uses `new UTF8Encoding(false)` (no BOM). For byte-level output equivalence, the External must control the encoding.

The External module handles ONLY file I/O (CSV writing with correct trailer and encoding). All business logic (grouping, aggregation, rounding, ordering) lives in the SQL Transformation.

## 3. DataSourcing Config

### transactions (required)

| Property | Value |
|----------|-------|
| resultName | `transactions` |
| schema | `datalake` |
| table | `transactions` |
| columns | `txn_timestamp`, `amount` |

Effective dates: Injected by the executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`). No hardcoded dates in the config.

### accounts — REMOVED (AP1, AP4)

V1 sources `datalake.accounts` with columns `account_id`, `customer_id`, `account_type`, `interest_rate`. The External module never reads this DataFrame. All four columns are unused.

- **AP1 eliminated:** Dead-end sourcing of `accounts` table removed.
- **AP4 eliminated:** All four unused `accounts` columns removed.
- **AP4 eliminated:** Unused `transactions` columns (`transaction_id`, `account_id`, `txn_type`, `description`) removed. Only `txn_timestamp` and `amount` are needed for the business logic.

Evidence: [BRD: "The `accounts` table is sourced but **not used** by the External module."] [BRD: "Columns `transaction_id`, `account_id`, `txn_type`, `description` from transactions are also sourced but not used — only `txn_timestamp` and `amount` are referenced."] [PeakTransactionTimesWriter.cs:32-45 — only `row["txn_timestamp"]` and `row["amount"]` are accessed]

## 4. Transformation SQL

```sql
SELECT
    CAST(strftime('%H', txn_timestamp) AS INTEGER) AS hour_of_day,
    COUNT(*) AS txn_count,
    ROUND(SUM(CAST(amount AS REAL)), 2) AS total_amount,
    strftime('%Y-%m-%d', MAX(as_of)) AS as_of
FROM transactions
GROUP BY CAST(strftime('%H', txn_timestamp) AS INTEGER)
ORDER BY hour_of_day ASC
```

**resultName:** `peak_transaction_times`

**Business rule mapping:**

| Rule | SQL Implementation | Evidence |
|------|-------------------|----------|
| BR-1: Group by hour of day (0-23) | `CAST(strftime('%H', txn_timestamp) AS INTEGER)` — extracts hour component from timestamp | [PeakTransactionTimesWriter.cs:36-39] `hour = dt.Hour` |
| BR-2: txn_count = count per hour | `COUNT(*)` | [PeakTransactionTimesWriter.cs:44] `current.count + 1` |
| BR-3: total_amount = rounded sum | `ROUND(SUM(CAST(amount AS REAL)), 2)` — note: SQLite ROUND uses round-half-away-from-zero vs V1's banker's rounding (W5); see Section 6 | [PeakTransactionTimesWriter.cs:45,55] `Math.Round(kvp.Value.total, 2)` |
| BR-4: Ordered by hour_of_day ASC | `ORDER BY hour_of_day ASC` | [PeakTransactionTimesWriter.cs:49] `.OrderBy(k => k.Key)` |
| BR-6: as_of from __maxEffectiveDate | `strftime('%Y-%m-%d', MAX(as_of))` — the `as_of` column is injected by DataSourcing from the effective date range; since all rows share the same max effective date in single-day runs, `MAX(as_of)` yields the correct value | [PeakTransactionTimesWriter.cs:28-29] `maxDate.ToString("yyyy-MM-dd")` |

**Note on BR-3 precision (W5, W6):** V1 uses `decimal` arithmetic for accumulation (`Convert.ToDecimal(row["amount"])`) and `Math.Round(total, 2)` which defaults to `MidpointRounding.ToEven` (banker's rounding). The SQL uses `CAST(amount AS REAL)` (64-bit IEEE 754 float in SQLite), then `ROUND(..., 2)` which uses round-half-away-from-zero. Two divergence vectors: (1) W6 — float vs decimal intermediate sums, (2) W5 — different rounding at exact midpoints. See Section 6 (W5 wrinkle) for full analysis and mitigation strategy. Starting strict in Proofmark; if divergence is detected in Phase D, the rounding will be moved into the Tier 2 External module or `total_amount` will be promoted to FUZZY.

**Anti-pattern AP6 eliminated:** V1 uses a `foreach` loop with a `Dictionary<int, (int count, decimal total)>` to accumulate hourly groups row by row. V2 replaces this with a single SQL `GROUP BY` — a set-based operation. Evidence: [PeakTransactionTimesWriter.cs:32-46 — row-by-row iteration replaced by SQL aggregation]

**Anti-pattern AP8 consideration:** The SQL is minimal — a single `GROUP BY` with no unused CTEs or window functions. No simplification needed.

## 5. Writer Config

The V2 External module writes the CSV file directly (Tier 2 SCALPEL — file I/O only).

| Property | Value | V1 Evidence |
|----------|-------|-------------|
| Output path | `Output/double_secret_curated/peak_transaction_times.csv` | V1: `Output/curated/peak_transaction_times.csv` [PeakTransactionTimesWriter.cs:70] |
| Header | Yes (column names comma-joined) | [PeakTransactionTimesWriter.cs:80] `string.Join(",", columns)` |
| Line ending | LF (`\n`) | [PeakTransactionTimesWriter.cs:77] `writer.NewLine = "\n"` |
| Trailer format | `TRAILER|{inputCount}|{dateStr}` | [PeakTransactionTimesWriter.cs:90] |
| Trailer count | Input transaction count (W7 — inflated) | [PeakTransactionTimesWriter.cs:25,61] |
| Write mode | Overwrite (`append: false`) | [PeakTransactionTimesWriter.cs:76] |
| Encoding | UTF-8 with BOM | [PeakTransactionTimesWriter.cs:76] — `new StreamWriter(path, false)` defaults to UTF-8 with BOM |

**Output columns (in order):** `hour_of_day`, `txn_count`, `total_amount`, `as_of`

## 6. Wrinkle Replication

### W7 — Trailer Inflated Count

**V1 behavior:** The trailer line is `TRAILER|{inputCount}|{dateStr}` where `inputCount` is `transactions.Count` — the number of raw transaction rows BEFORE hourly bucketing. If 4000 transactions span 15 hours, the trailer says `TRAILER|4000|2024-10-15` even though the output has only 15 data rows.

**V2 replication strategy:** The V2 External module reads the original `transactions` DataFrame from shared state to obtain the input count (before aggregation). It also reads the aggregated `peak_transaction_times` DataFrame for the output rows. The trailer is written using the transactions count, not the output row count.

```csharp
// W7: Trailer uses input transaction count (before hourly grouping), not output row count.
// V1 behavior: transactions.Count is written to trailer regardless of how many hourly buckets exist.
var inputCount = transactions.Count;
writer.WriteLine($"TRAILER|{inputCount}|{dateStr}");
```

**Evidence:** [PeakTransactionTimesWriter.cs:25] `var inputCount = transactions.Count;` [PeakTransactionTimesWriter.cs:90] `writer.WriteLine($"TRAILER|{inputCount}|{dateStr}")`

### W5 — Banker's Rounding vs SQLite Round-Half-Away-From-Zero

**V1 behavior:** V1 accumulates `amount` values using C# `decimal` arithmetic (`Convert.ToDecimal(row["amount"])`) and rounds per-hour totals with `Math.Round(kvp.Value.total, 2)`. `Math.Round` with two arguments defaults to `MidpointRounding.ToEven` (banker's rounding). At exact midpoints (e.g., a sum of 1234.565), banker's rounding rounds to the nearest even digit (1234.56), whereas round-half-away-from-zero would produce 1234.57.

**Technical note:** SQLite ROUND() uses round-half-away-from-zero. V1 uses C# Math.Round() which defaults to MidpointRounding.ToEven (banker's rounding). These will diverge at exact midpoints (e.g., 2.5 rounds to 2 in C# but 3 in SQLite).

**V2 impact:** The SQL Transformation uses `ROUND(SUM(CAST(amount AS REAL)), 2)`. This introduces two divergence vectors:
1. **W6 (type mismatch):** V1 uses `decimal` (exact base-10) for accumulation; SQLite uses REAL (64-bit IEEE 754 float). Intermediate sums may differ.
2. **W5 (rounding mode):** Even if the sums were identical, banker's rounding and round-half-away-from-zero produce different results at exact midpoints.

**Risk assessment:** The source `amount` values are financial amounts with 2 decimal places. When summed across potentially hundreds of transactions per hour, the accumulated total could land on or near a midpoint (X.XX5). The probability is non-trivial — especially since IEEE 754 float representation can shift sums slightly toward or away from midpoints relative to `decimal` accumulation.

**Mitigation strategy (phased):**
1. **Phase D (initial):** Start with the SQL ROUND approach and strict Proofmark comparison (threshold 100.0, no FUZZY). Run Proofmark and see if any hourly `total_amount` values diverge.
2. **If divergence detected — Option A (preferred):** Move the rounding into the Tier 2 External module. The SQL Transformation would return the raw `SUM(CAST(amount AS REAL))` without ROUND. The External module applies `Math.Round((decimal)totalAmount, 2, MidpointRounding.ToEven)` before writing each row, exactly matching V1's rounding behavior. This is a minimal change — the External module already exists for W7 trailer handling, so adding rounding is trivial.
3. **If divergence detected — Option B (fallback):** Add `total_amount` as a FUZZY column in Proofmark with `tolerance: 0.01, tolerance_type: absolute`. This is acceptable if the divergence is bounded to ±0.01 and rare.

```csharp
// W5: V1 uses Math.Round(total, 2) which defaults to MidpointRounding.ToEven (banker's rounding).
// SQLite ROUND() uses round-half-away-from-zero. If Proofmark detects divergence,
// move rounding here: Math.Round((decimal)totalAmount, 2, MidpointRounding.ToEven)
```

## 7. Anti-Pattern Elimination

### AP1 — Dead-End Sourcing (ELIMINATED)

**V1 problem:** The `accounts` table is sourced but never referenced by the External module. The entire DataSourcing step for `accounts` is wasted I/O.

**V2 solution:** The `accounts` DataSourcing entry is removed entirely from the V2 job config. Only `transactions` is sourced.

**Evidence:** [BRD: "The `accounts` table is sourced but **not used** by the External module."] [peak_transaction_times.json:13-18 — accounts DataSourcing config] [PeakTransactionTimesWriter.cs — no reference to `sharedState["accounts"]`]

### AP3 — Unnecessary External Module (PARTIALLY ELIMINATED)

**V1 problem:** The entire pipeline — data access, hourly grouping, aggregation, rounding, ordering, and file I/O — lives in a single monolithic External module (`PeakTransactionTimesWriter`). All business logic (BR-1 through BR-4, BR-6) is expressible in SQL.

**V2 solution:** Business logic is moved to a SQL Transformation (Tier 1 portion). The External module is retained ONLY for file I/O to handle:
1. W7 trailer with inflated input count (CsvFileWriter's `{row_count}` token cannot produce this)
2. UTF-8 with BOM encoding (CsvFileWriter uses `UTF8Encoding(false)`)

The External module contains zero business logic — it reads the pre-computed aggregated DataFrame from shared state and writes it to disk.

**Evidence:** [KNOWN_ANTI_PATTERNS.md: AP3 — "Replace with framework modules."] [W7 prescription: "Use the framework's CsvFileWriter with trailer support rather than writing the file manually." — this is impossible here because CsvFileWriter's `{row_count}` gives output rows, not input rows]

### AP4 — Unused Columns (ELIMINATED)

**V1 problem:** `transactions` sources 6 columns (`transaction_id`, `account_id`, `txn_timestamp`, `txn_type`, `amount`, `description`) but only `txn_timestamp` and `amount` are used. `accounts` sources 4 columns, none used.

**V2 solution:** `transactions` sources only `txn_timestamp` and `amount`. `accounts` is removed entirely (see AP1).

**Evidence:** [PeakTransactionTimesWriter.cs:34] `row["txn_timestamp"]` [PeakTransactionTimesWriter.cs:45] `row["amount"]` — no other column references exist.

### AP6 — Row-by-Row Iteration (ELIMINATED)

**V1 problem:** Hourly grouping is implemented as a `foreach` loop iterating over every transaction row, maintaining a `Dictionary<int, (int count, decimal total)>` accumulator.

**V2 solution:** Replaced with a single SQL `GROUP BY` in the Transformation module — a set-based operation.

**Evidence:** [PeakTransactionTimesWriter.cs:32-46 — `foreach (var row in transactions.Rows)` with manual dictionary accumulation]

## 8. Proofmark Config

```yaml
comparison_target: "peak_transaction_times"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

**Justification:**

- **reader: csv** — Output is a CSV file (both V1 and V2).
- **header_rows: 1** — The file has a single header row with column names. [PeakTransactionTimesWriter.cs:80]
- **trailer_rows: 1** — The file has a single trailer row at the end. Write mode is Overwrite, so each run produces exactly one trailer at the file's end. [PeakTransactionTimesWriter.cs:90] [CONFIG_GUIDE.md: "Overwrite-mode jobs produce a file with exactly one trailer at the end."]
- **threshold: 100.0** — All output values are deterministic. No EXCLUDED or FUZZY columns needed initially.
- **No excluded columns** — BRD identifies zero non-deterministic fields.
- **No fuzzy columns (initial)** — Starting strict per best practices. W5 (rounding mode: SQLite round-half-away-from-zero vs C# banker's rounding) and W6 (float vs decimal accumulation) are both applicable to `total_amount`. See Section 6 (W5 wrinkle) for full analysis. If Phase D Proofmark comparison reveals differences, `total_amount` will either be promoted to FUZZY with `tolerance: 0.01, tolerance_type: absolute`, or the rounding will be moved into the Tier 2 External module for exact V1 equivalence.

**Encoding note:** Proofmark reads CSV data, not raw bytes. The UTF-8 BOM difference between V1 (with BOM) and a hypothetical framework CsvFileWriter (without BOM) does not affect Proofmark comparison. The V2 External module replicates the BOM anyway for byte-level equivalence.

## 9. Open Questions

### OQ-1: RESOLVED — Moved to W5 Wrinkle Analysis

The SQLite REAL vs C# decimal rounding divergence is now fully analyzed in Section 6 under "W5 — Banker's Rounding vs SQLite Round-Half-Away-From-Zero". This is not an open question — it is a known wrinkle with a concrete phased mitigation strategy.

### OQ-2: Timestamp Parsing Edge Case (VERY LOW RISK)

V1 has a fallback path: if `txn_timestamp` is not a `DateTime`, it tries `DateTime.TryParse` on the string representation, defaulting `hour` to 0 on failure (BR-9). In the V2 SQL Transformation, `strftime('%H', txn_timestamp)` will return `NULL` for unparseable timestamps, and `CAST(NULL AS INTEGER)` is `NULL` — these rows would be excluded from the `GROUP BY` rather than bucketed into hour 0.

**Assessment:** This is extremely unlikely to matter because `txn_timestamp` values in `datalake.transactions` are proper timestamps loaded from PostgreSQL. If it does matter, the resolution is to add a `COALESCE` in the SQL: `COALESCE(CAST(strftime('%H', txn_timestamp) AS INTEGER), 0)`.

### OQ-3: Empty Input Handling

V1 handles empty/null transactions by writing an output file with only a header and `TRAILER|0|{date}` (BR-8, Edge Case 1). The V2 External module must handle this case: if the aggregated DataFrame has zero rows, it should still write the header and trailer. This is straightforward but must be tested.
