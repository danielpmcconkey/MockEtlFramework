# CardAuthorizationSummary — Functional Specification Document

## 1. Overview & Tier

**Job:** CardAuthorizationSummaryV2
**Config:** `card_authorization_summary_v2.json`
**Tier:** 1 (Framework Only) — `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

This job produces a daily summary of card transaction authorization outcomes (approved vs declined) grouped by card type. The entire business logic is expressible in SQL: two table sources joined on `card_id`, grouped by `card_type` and `as_of`, with conditional aggregation. No procedural logic or SQLite-unsupported operations are needed. Tier 1 is the correct and complete solution.

**Traces to:** BRD Overview, BR-1 through BR-10

---

## 2. V2 Module Chain

```
[1] DataSourcing  -> card_transactions  (datalake.card_transactions)
[2] DataSourcing  -> cards              (datalake.cards)
[3] Transformation -> output            (SQL: join, group, aggregate)
[4] CsvFileWriter  <- output            (Output/double_secret_curated/card_authorization_summary.csv)
```

### Module Details

**Module 1: DataSourcing — card_transactions**
- Schema: `datalake`
- Table: `card_transactions`
- Columns: `card_txn_id`, `card_id`, `authorization_status`
- Effective dates: injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`)
- Notes: Removed `customer_id` and `amount` from V1 column list — neither is referenced in the SQL output. (Eliminates AP4)

**Module 2: DataSourcing — cards**
- Schema: `datalake`
- Table: `cards`
- Columns: `card_id`, `card_type`
- Effective dates: injected by executor via shared state
- Notes: Removed `customer_id` from V1 column list — not referenced in the SQL output. (Eliminates AP4)

**Module 3: Transformation — output**
- SQL: See Section 5 below
- Result stored in shared state as `output`

**Module 4: CsvFileWriter**
- Source: `output`
- Output path: `Output/double_secret_curated/card_authorization_summary.csv`
- includeHeader: `true`
- trailerFormat: `TRAILER|{row_count}|{date}`
- writeMode: `Overwrite`
- lineEnding: `LF`
- All writer params match V1 exactly (BRD Writer Configuration section)

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Reproduced)

| W-Code | Applies? | V1 Behavior | V2 Treatment |
|--------|----------|-------------|--------------|
| W4 | **YES** | `approval_rate` computed via `CAST(... AS INTEGER) / CAST(COUNT(*) AS INTEGER)` — integer division truncates to 0 or 1 | Reproduced in SQL using the same `CAST(... AS INTEGER) / CAST(COUNT(*) AS INTEGER)` expression. SQLite performs integer division when both operands are INTEGER, which is the exact same behavior as V1. A SQL comment documents this is intentional V1 replication. |
| W9 | **NO** | Overwrite mode — each run replaces the file. For auto-advance this means only the last effective date's output survives. | This is V1's actual behavior. Reproduced with `writeMode: Overwrite`. The BRD notes this correctly. It may be wrong from a business perspective, but it IS V1's behavior and we must match it. |

**W4 Note on implementation:** The KNOWN_ANTI_PATTERNS.md prescribes using `Math.Truncate((decimal)numerator / denominator)` in C# code. However, since this job is Tier 1 (pure SQL, no External module), the integer division must be handled in SQL. SQLite's `CAST(... AS INTEGER) / CAST(... AS INTEGER)` produces identical integer truncation behavior. This is the cleanest approach for a Tier 1 job — it produces the same output without introducing an unnecessary External module just to use `Math.Truncate`. The SQL contains a comment marking this as intentional V1 replication.

### Code-Quality Anti-Patterns (Eliminated)

| AP-Code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| AP4 | **YES** | V1 sources `customer_id` and `amount` from `card_transactions`, and `customer_id` from `cards` — none of these appear in the final SELECT output. | **Eliminated.** V2 DataSourcing configs source only columns used by the SQL: `card_txn_id`, `card_id`, `authorization_status` from `card_transactions`; `card_id`, `card_type` from `cards`. |
| AP8 | **YES** | V1 SQL contains two forms of dead code: (1) a `ROW_NUMBER() OVER (PARTITION BY c.card_type ORDER BY ct.card_txn_id) AS rn` column in the `txn_detail` CTE that is never referenced in the final SELECT, and (2) an entire CTE `unused_summary` that is defined but never referenced. | **Eliminated.** V2 SQL removes the `ROW_NUMBER()` window function and removes the `unused_summary` CTE entirely. The CTE structure is simplified to compute only what the output needs. |
| AP3 | **N/A** | V1 does not use an External module for this job. | No change needed. V2 remains Tier 1. |

### Anti-Patterns Not Applicable

| AP-Code | Reason Not Applicable |
|---------|----------------------|
| AP1 | All sourced tables (card_transactions, cards) are used in the SQL join. No dead-end sourcing. |
| AP2 | No evidence of cross-job duplication for this specific job's logic. |
| AP5 | No NULL/empty asymmetries identified — all fields are aggregation results. |
| AP6 | No row-by-row iteration — this is a pure SQL job. |
| AP7 | No magic values — the only string literals are `'Approved'` and `'Declined'`, which are domain values, not thresholds. |
| AP9 | Job name accurately describes its output (card authorization summary). |
| AP10 | V1 already uses framework-injected effective dates (no explicit date filters in SQL). V2 continues this pattern. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Traces to BRD |
|--------|------|--------|---------------|---------------|
| card_type | TEXT | cards.card_type | Pass-through via GROUP BY | BR-1, BR-2, BR-9 |
| total_count | INTEGER | card_transactions | COUNT(*) per group | BR-2 |
| approved_count | INTEGER | card_transactions.authorization_status | SUM(CASE WHEN 'Approved' THEN 1 ELSE 0 END) | BR-3 |
| declined_count | INTEGER | card_transactions.authorization_status | SUM(CASE WHEN 'Declined' THEN 1 ELSE 0 END) | BR-4 |
| approval_rate | INTEGER | Derived | Integer division: approved_count / total_count (truncates to 0 or 1) | BR-5, W4 |
| as_of | TEXT | card_transactions.as_of | Pass-through via GROUP BY | BR-2 |

Column order matches V1 exactly (determined by SELECT clause order).

**Non-deterministic fields:** None. All output fields are deterministic given the same input data and effective date range. (Traces to BRD Non-Deterministic Fields section.)

---

## 5. SQL Design

### V1 SQL (for reference — do NOT use)

The V1 SQL uses two CTEs (`txn_detail` with an unused `ROW_NUMBER()`, and `unused_summary` that is never referenced). This is dead code (AP8) that V2 eliminates.

### V2 SQL

```sql
-- V2: Simplified SQL — removed unused ROW_NUMBER() window function (AP8)
-- and removed unused_summary CTE (AP8).
-- W4: approval_rate uses CAST(... AS INTEGER) / CAST(... AS INTEGER)
-- to replicate V1's integer division truncation (always yields 0 or 1).
SELECT
    c.card_type,
    COUNT(*) AS total_count,
    SUM(CASE WHEN ct.authorization_status = 'Approved' THEN 1 ELSE 0 END) AS approved_count,
    SUM(CASE WHEN ct.authorization_status = 'Declined' THEN 1 ELSE 0 END) AS declined_count,
    CAST(SUM(CASE WHEN ct.authorization_status = 'Approved' THEN 1 ELSE 0 END) AS INTEGER)
        / CAST(COUNT(*) AS INTEGER) AS approval_rate,
    ct.as_of
FROM card_transactions ct
INNER JOIN cards c ON ct.card_id = c.card_id
GROUP BY c.card_type, ct.as_of
```

### SQL Design Rationale

1. **Direct join, no CTE:** V1's `txn_detail` CTE served no purpose beyond adding a `ROW_NUMBER()` that was never used. V2 performs the join and aggregation directly, producing identical results with less complexity.

2. **Integer division preserved (W4):** The expression `CAST(... AS INTEGER) / CAST(COUNT(*) AS INTEGER)` is preserved exactly from V1. In SQLite, when both operands of division are INTEGER, the result is integer division (truncation toward zero). Since `SUM(CASE ... END)` and `COUNT(*)` already return integers in SQLite, the explicit CASTs are technically redundant but kept for clarity and to make the W4 replication obvious.

3. **INNER JOIN preserved (BR-10):** The join type is INNER, matching V1. Transactions without a matching card are excluded.

4. **GROUP BY columns match V1:** Grouping by `c.card_type` and `ct.as_of` produces one row per card type per date, matching V1 behavior (BR-2).

5. **Column aliases match V1:** All output column names (`card_type`, `total_count`, `approved_count`, `declined_count`, `approval_rate`, `as_of`) match V1's SELECT clause exactly, ensuring the CSV header row is byte-identical.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CardAuthorizationSummaryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["card_txn_id", "card_id", "authorization_status"]
    },
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["card_id", "card_type"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT c.card_type, COUNT(*) AS total_count, SUM(CASE WHEN ct.authorization_status = 'Approved' THEN 1 ELSE 0 END) AS approved_count, SUM(CASE WHEN ct.authorization_status = 'Declined' THEN 1 ELSE 0 END) AS declined_count, CAST(SUM(CASE WHEN ct.authorization_status = 'Approved' THEN 1 ELSE 0 END) AS INTEGER) / CAST(COUNT(*) AS INTEGER) AS approval_rate, ct.as_of FROM card_transactions ct INNER JOIN cards c ON ct.card_id = c.card_id GROUP BY c.card_type, ct.as_of"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/card_authorization_summary.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | output | output | YES |
| outputFile | Output/curated/card_authorization_summary.csv | Output/double_secret_curated/card_authorization_summary.csv | Path changed per spec |
| includeHeader | true | true | YES |
| trailerFormat | TRAILER\|{row_count}\|{date} | TRAILER\|{row_count}\|{date} | YES |
| writeMode | Overwrite | Overwrite | YES |
| lineEnding | LF | LF | YES |

The only change is the output directory (`curated` -> `double_secret_curated`), as required by the project spec.

### Trailer Behavior

- `{row_count}` resolves to the number of data rows in the output DataFrame (excludes header and trailer). Handled by the framework's CsvFileWriter (see `CsvFileWriter.cs:63` — uses `df.Count`).
- `{date}` resolves to `__maxEffectiveDate` from shared state, formatted as `yyyy-MM-dd`. Handled by the framework's CsvFileWriter (see `CsvFileWriter.cs:60-62`).
- With Overwrite mode, each run produces exactly one trailer at the end of the file.

---

## 8. Proofmark Config Design

### Config

```yaml
comparison_target: "card_authorization_summary"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Rationale

| Setting | Value | Justification |
|---------|-------|---------------|
| reader | csv | V1 and V2 both use CsvFileWriter |
| threshold | 100.0 | All fields are deterministic; byte-identical output expected |
| header_rows | 1 | `includeHeader: true` in writer config |
| trailer_rows | 1 | `trailerFormat` present + `writeMode: Overwrite` means exactly one trailer at end of file |

### Column Overrides

**Excluded columns:** None. All output columns are deterministic.

**Fuzzy columns:** None. The `approval_rate` column uses integer division in both V1 and V2 (same SQLite engine, same expression), so values will be identical -- no fuzzy tolerance needed.

### Why No Overrides Are Needed

- `approval_rate` (W4): Both V1 and V2 execute the same integer division expression in SQLite. The computation path is identical, so the output will be bit-for-bit identical. No fuzzy tolerance is warranted.
- No timestamps, UUIDs, or other non-deterministic fields exist in the output.
- No floating-point accumulation (W6) is involved -- all aggregations produce integers.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: Join on card_id | Section 5 (SQL: INNER JOIN cards c ON ct.card_id = c.card_id) | SQL in Transformation module |
| BR-2: Group by card_type, as_of | Section 5 (SQL: GROUP BY c.card_type, ct.as_of) | SQL in Transformation module |
| BR-3: approved_count calculation | Section 5 (SQL: SUM(CASE WHEN ... 'Approved' ...)) | SQL in Transformation module |
| BR-4: declined_count calculation | Section 5 (SQL: SUM(CASE WHEN ... 'Declined' ...)) | SQL in Transformation module |
| BR-5: Integer division approval_rate | Section 5 (SQL: CAST/INTEGER division), Section 3 (W4) | SQL in Transformation module, W4 documented |
| BR-6: Dead code ROW_NUMBER | Section 3 (AP8 — eliminated) | Removed from V2 SQL |
| BR-7: Dead code unused_summary CTE | Section 3 (AP8 — eliminated) | Removed from V2 SQL |
| BR-8: Authorization status values | Section 5 (SQL uses 'Approved' and 'Declined' literals) | SQL in Transformation module |
| BR-9: Card type values | Section 4 (card_type column in output) | Pass-through from cards table |
| BR-10: INNER JOIN exclusion | Section 5 (SQL: INNER JOIN) | SQL in Transformation module |
| BRD Writer Config | Section 7 (full parameter match) | CsvFileWriter config in V2 job JSON |
| BRD Trailer Format | Section 7 (trailer behavior) | trailerFormat in V2 job JSON |
| BRD Edge Case 1 (integer division) | Section 3 (W4) | Replicated in V2 SQL |
| BRD Edge Case 2 (weekend data) | Section 5 note | Handled identically — INNER JOIN + same DataSourcing date range |
| BRD Edge Case 3 (zero card_type) | Section 5 (GROUP BY produces rows only for existing groups) | Identical behavior — no zero-fill |
| BRD Edge Case 4 (trailer format) | Section 7 (trailer behavior) | Framework CsvFileWriter handles token substitution |
| AP4: Unused columns | Section 2 (reduced column lists), Section 3 | Eliminated — V2 sources only needed columns |
| AP8: Unused CTEs/window functions | Section 5 (simplified SQL), Section 3 | Eliminated — V2 SQL removes dead code |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). No External module is needed.

All business logic is expressed in a single SQL Transformation. The framework's DataSourcing handles data retrieval with effective date injection, and CsvFileWriter handles output with header, trailer, and line ending configuration.

---

## Appendix: V1 vs V2 Diff Summary

| Aspect | V1 | V2 | Change Type |
|--------|----|----|-------------|
| DataSourcing: card_transactions columns | card_txn_id, card_id, customer_id, amount, authorization_status | card_txn_id, card_id, authorization_status | AP4 eliminated |
| DataSourcing: cards columns | card_id, customer_id, card_type | card_id, card_type | AP4 eliminated |
| SQL: ROW_NUMBER() in txn_detail CTE | Present (unused) | Removed | AP8 eliminated |
| SQL: unused_summary CTE | Present (unreferenced) | Removed | AP8 eliminated |
| SQL: CTE structure | 2 CTEs (txn_detail, unused_summary) + final SELECT | Direct SELECT (no CTEs) | Simplification |
| SQL: Core logic (join, group, aggregate) | Same | Same | No change |
| SQL: Integer division (approval_rate) | CAST/INTEGER division | CAST/INTEGER division | W4 preserved |
| Writer config | All params identical | Path changed to double_secret_curated | Required by spec |
| Output columns | card_type, total_count, approved_count, declined_count, approval_rate, as_of | Same | No change |
