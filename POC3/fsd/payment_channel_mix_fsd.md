# PaymentChannelMix — Functional Specification Document

## 1. Job Summary

PaymentChannelMix produces a daily breakdown of transaction volume and total amounts across three payment channels — regular transactions, card transactions, and wire transfers. Each channel is aggregated independently by `as_of` date using `COUNT(*)` and `ROUND(SUM(amount), 2)`, then the three result sets are combined via `UNION ALL` into a single Parquet output file that is overwritten on each run. This is a straightforward Tier 1 framework job with no procedural logic, no External module, and no output-affecting wrinkles.

## 2. V2 Module Chain

**Tier: 1 — Framework Only (DEFAULT)**

```
DataSourcing (transactions) → DataSourcing (card_transactions) → DataSourcing (wire_transfers) → Transformation (SQL) → ParquetFileWriter
```

**Justification:** The entire business logic is a SQL aggregation with `GROUP BY`, `COUNT(*)`, `ROUND(SUM(...), 2)`, and `UNION ALL`. No procedural logic, no snapshot fallback, no cross-date-range queries, no operations outside SQL's capabilities. Tier 1 is the correct and simplest choice. V1 already uses this exact pattern — the V2 rewrite keeps the same module chain while eliminating code-quality anti-patterns.

## 3. DataSourcing Config

V2 sources ONLY the columns actually used in the Transformation SQL. V1 sources many unused columns (AP4 — see Section 7).

### Source 1: transactions

| Property | Value |
|----------|-------|
| resultName | `transactions` |
| schema | `datalake` |
| table | `transactions` |
| columns | `["amount"]` |

- `as_of` is automatically appended by the DataSourcing module when not explicitly listed [DataSourcing.cs:69-72]
- Effective dates injected at runtime via shared state keys `__minEffectiveDate` / `__maxEffectiveDate` [Architecture.md, DataSourcing.cs:51-61]
- V1 sourced `transaction_id`, `account_id`, `amount`, `description` — only `amount` is used in SQL [payment_channel_mix.json:10, BRD Edge Case 4]

### Source 2: card_transactions

| Property | Value |
|----------|-------|
| resultName | `card_transactions` |
| schema | `datalake` |
| table | `card_transactions` |
| columns | `["amount"]` |

- V1 sourced `card_txn_id`, `card_id`, `amount`, `merchant_name` — only `amount` is used in SQL [payment_channel_mix.json:17, BRD Edge Case 4]

### Source 3: wire_transfers

| Property | Value |
|----------|-------|
| resultName | `wire_transfers` |
| schema | `datalake` |
| table | `wire_transfers` |
| columns | `["amount"]` |

- V1 sourced `wire_id`, `customer_id`, `amount`, `counterparty_bank` — only `amount` is used in SQL [payment_channel_mix.json:24, BRD Edge Case 4]

### Effective Date Handling

No `minEffectiveDate` or `maxEffectiveDate` is hardcoded in the job config. The executor injects these into shared state at runtime. The DataSourcing module reads `__minEffectiveDate` and `__maxEffectiveDate` from shared state and applies a `WHERE as_of >= @minDate AND as_of <= @maxDate` filter at the database level [DataSourcing.cs:74-78]. This is the correct pattern — no AP10 issue exists in V1 for this job.

## 4. Transformation SQL

The V2 SQL is identical to V1's SQL. The logic is clean and correct — no simplification needed.

```sql
SELECT 'transaction' AS payment_channel,
       COUNT(*) AS txn_count,
       ROUND(SUM(amount), 2) AS total_amount,
       transactions.as_of
FROM transactions
GROUP BY transactions.as_of
UNION ALL
SELECT 'card' AS payment_channel,
       COUNT(*) AS txn_count,
       ROUND(SUM(amount), 2) AS total_amount,
       card_transactions.as_of
FROM card_transactions
GROUP BY card_transactions.as_of
UNION ALL
SELECT 'wire' AS payment_channel,
       COUNT(*) AS txn_count,
       ROUND(SUM(amount), 2) AS total_amount,
       wire_transfers.as_of
FROM wire_transfers
GROUP BY wire_transfers.as_of
```

**Key behaviors preserved:**
- Table-qualified `as_of` references (BR-7) — required because each SELECT references a different source table registered in SQLite
- `UNION ALL` (BR-5) — preserves all rows, no deduplication
- No `ORDER BY` (BR-6) — row order depends on SQLite's UNION ALL execution order
- `ROUND(SUM(amount), 2)` (BR-4) — standard two-decimal rounding for monetary totals
- `COUNT(*)` (BR-3) — counts all rows per channel per date

**Result stored as:** `output` (matches V1's `resultName`)

## 5. Writer Config

| Property | V1 Value | V2 Value | Notes |
|----------|----------|----------|-------|
| type | `ParquetFileWriter` | `ParquetFileWriter` | Same writer type (CRITICAL requirement) |
| source | `output` | `output` | Same DataFrame name |
| outputDirectory | `Output/curated/payment_channel_mix/` | `Output/double_secret_curated/payment_channel_mix/` | V2 output path per BLUEPRINT conventions |
| numParts | `1` | `1` | Single part file: `part-00000.parquet` |
| writeMode | `Overwrite` | `Overwrite` | Each run replaces the entire output directory |

**Write mode implications (from BRD):** On multi-day auto-advance, each subsequent day's run overwrites the previous day's output. Only the final effective date's data survives in the output directory.

## 6. Wrinkle Replication

**No output-affecting wrinkles (W-codes) apply to this job.**

Review of all W-codes against this job:

| W-code | Applicable? | Rationale |
|--------|-------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in V1 config or SQL |
| W2 (Weekend fallback) | No | No weekend date manipulation |
| W3a/b/c (Boundary rows) | No | No summary row generation |
| W4 (Integer division) | No | No division operations in SQL — only COUNT and SUM |
| W5 (Banker's rounding) | No | `ROUND(SUM(amount), 2)` uses SQLite's ROUND, which uses standard rounding (not banker's) |
| W6 (Double epsilon) | No | No accumulation in C# code — all math is in SQL via SQLite |
| W7 (Trailer inflated count) | No | ParquetFileWriter — no trailer |
| W8 (Trailer stale date) | No | ParquetFileWriter — no trailer |
| W9 (Wrong writeMode) | No | Overwrite is appropriate for a single-day snapshot output |
| W10 (Absurd numParts) | No | `numParts: 1` is reasonable for this dataset size |
| W12 (Header every append) | No | ParquetFileWriter in Overwrite mode — not applicable |

## 7. Anti-Pattern Elimination

### AP4: Unused Columns — ELIMINATED

**V1 problem:** V1 sources 4 columns per table but only uses `amount` (and `as_of`, which is auto-appended). The unused columns are:
- `transactions`: `transaction_id`, `account_id`, `description` [payment_channel_mix.json:10]
- `card_transactions`: `card_txn_id`, `card_id`, `merchant_name` [payment_channel_mix.json:17]
- `wire_transfers`: `wire_id`, `customer_id`, `counterparty_bank` [payment_channel_mix.json:24]

**V2 fix:** Each DataSourcing module sources only `["amount"]`. The `as_of` column is automatically appended by the DataSourcing module [DataSourcing.cs:69-72], so it does not need to be listed. This reduces data transfer from the database and memory footprint without affecting output.

**Evidence:** The SQL [payment_channel_mix.json:29] references only `amount` and `{table}.as_of` — no other column names appear anywhere in the transformation.

### Other AP-codes — Not Applicable

| AP-code | Applicable? | Rationale |
|---------|-------------|-----------|
| AP1 (Dead-end sourcing) | No | All three DataSourcing entries feed the Transformation SQL |
| AP2 (Duplicated logic) | No | No cross-job duplication identified for this specific aggregation |
| AP3 (Unnecessary External) | No | V1 already uses the framework-only pattern (no External module) |
| AP5 (Asymmetric NULLs) | No | No NULL handling logic — `COUNT(*)` and `SUM(amount)` handle NULLs via SQL standard behavior |
| AP6 (Row-by-row iteration) | No | No C# iteration — all logic is SQL |
| AP7 (Magic values) | No | No hardcoded thresholds — only string literals `'transaction'`, `'card'`, `'wire'` which are domain labels, not magic values |
| AP8 (Complex SQL / unused CTEs) | No | SQL is straightforward — three SELECT + GROUP BY + UNION ALL, no CTEs, no window functions |
| AP9 (Misleading names) | No | Job name "payment_channel_mix" accurately describes the output (payment channel breakdown) |
| AP10 (Over-sourcing dates) | No | V1 already uses the framework's effective date injection — no hardcoded dates, no WHERE clause date filtering in SQL |

## 8. Proofmark Config

```yaml
comparison_target: "payment_channel_mix"
reader: parquet
threshold: 100.0
```

**Justification for strict-only config (no exclusions, no fuzzy):**

- **Reader:** Parquet — matches V1 writer type (ParquetFileWriter) [payment_channel_mix.json:32]
- **Threshold:** 100.0 — all rows must match exactly
- **No excluded columns:** All four output columns (`payment_channel`, `txn_count`, `total_amount`, `as_of`) are deterministic. No timestamps, no UUIDs, no execution-time values. [BRD Non-Deterministic Fields: only row ordering is non-deterministic, which Proofmark handles via set comparison]
- **No fuzzy columns:** `txn_count` is an integer (`COUNT(*)`). `total_amount` uses `ROUND(SUM(amount), 2)` in SQLite — both V1 and V2 execute the same SQL in the same SQLite engine, so results are bit-identical. `payment_channel` is a string literal. `as_of` is a date.

**Note on row ordering:** The BRD identifies row ordering as non-deterministic (no `ORDER BY` clause). Proofmark's Parquet comparison operates as a set comparison (row-order-independent), so this is not a concern for the comparison config.

## 9. Open Questions

None. This is a clean Tier 1 job with no wrinkles, one anti-pattern (AP4, eliminated), straightforward SQL, and deterministic output. The V2 implementation is a direct simplification of V1 with unused columns removed.
