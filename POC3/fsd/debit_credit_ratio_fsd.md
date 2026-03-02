# DebitCreditRatio — Functional Specification Document

## 1. Overview

**Job:** DebitCreditRatioV2
**Tier:** Tier 1 (Framework Only) — `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

This job calculates debit-to-credit ratios per account, including per-type transaction counts and summed amounts. V1 used an External module (`DebitCreditRatioCalculator`) for aggregation logic that is fully expressible in SQL. V2 replaces the External module with a single Transformation SQL step, eliminating AP3 (unnecessary External), AP6 (row-by-row iteration), AP1/AP4 (unused data sources/columns), while faithfully reproducing W4 (integer division) and W6 (double-precision epsilon) behaviors for output equivalence.

### Tier Justification

All V1 business logic is expressible in standard SQL:
- Per-account aggregation: `GROUP BY account_id`
- Conditional counting: `SUM(CASE WHEN txn_type = 'Debit' THEN 1 ELSE 0 END)`
- Conditional summing: `SUM(CASE WHEN txn_type = 'Debit' THEN amount ELSE 0.0 END)`
- Integer division: SQLite natively performs integer division for `int / int` expressions (e.g., `3 / 5 = 0`), matching V1's W4 behavior
- Double-precision arithmetic: SQLite stores REAL as 8-byte IEEE 754 doubles, and `SUM()` accumulates in double precision, matching V1's W6 behavior
- LEFT JOIN with COALESCE for customer_id lookup with default 0
- MIN(as_of) for deterministic as_of selection (equivalent to V1's "first encountered" since DataSourcing orders by as_of)

No procedural logic, external data access patterns, or SQLite-unsupported operations are required. Tier 1 is sufficient.

## 2. V2 Module Chain

```
DataSourcing (transactions) -> DataSourcing (accounts) -> Transformation (SQL) -> ParquetFileWriter
```

| Step | Module | Config Key | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `transactions` | Fetch transaction data for effective date range |
| 2 | DataSourcing | `accounts` | Fetch account-to-customer mapping for effective date range |
| 3 | Transformation | `output` | SQL aggregation: counts, sums, ratios, customer lookup |
| 4 | ParquetFileWriter | — | Write to `Output/double_secret_curated/debit_credit_ratio/` |

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Reproduce with clean code)

| W-Code | V1 Behavior | V2 Approach | Evidence |
|--------|-------------|-------------|----------|
| W4 | `debit_count / credit_count` uses C# integer division, truncating to 0 when debit_count < credit_count | SQLite natively performs integer division on integer operands. The SQL expression `debit_count / credit_count` produces identical truncation behavior. `CASE WHEN credit_count > 0 THEN debit_count / credit_count ELSE 0 END` handles the zero guard. SQL comment documents the V1 replication. | [DebitCreditRatioCalculator.cs:60-61] |
| W6 | Amount accumulation uses `double` (not `decimal`), causing floating-point epsilon errors | SQLite REAL type is IEEE 754 double-precision. DataSourcing delivers `decimal` values from PostgreSQL; the Transformation module's type inference maps `decimal` to SQLite `REAL` ([Transformation.cs:98] `decimal => "REAL"`). `SUM()` on REAL columns accumulates as double-precision, matching V1's `Convert.ToDouble()` + addition loop. The division `debit_amount / credit_amount` is also double-precision in SQLite, matching V1's `double / double`. | [DebitCreditRatioCalculator.cs:41, 48-50, 64] |

### Code-Quality Anti-Patterns (Eliminated)

| AP-Code | V1 Problem | V2 Resolution |
|---------|------------|---------------|
| AP1 | `accounts` DataSourcing pulls `interest_rate` and `credit_limit` columns that are never used in the External module. Only `account_id` and `customer_id` are referenced. | **Eliminated.** V2 sources only `account_id` and `customer_id` from `accounts`. | [DebitCreditRatioCalculator.cs:26-31] — only `account_id` and `customer_id` are read from accounts rows |
| AP3 | V1 uses a full External module (`DebitCreditRatioCalculator`) for aggregation logic that is expressible in a single SQL query. | **Eliminated.** V2 replaces the External module with a Transformation SQL step. The entire pipeline is Tier 1: DataSourcing -> Transformation -> ParquetFileWriter. | [DebitCreditRatioCalculator.cs:34-78] — pure aggregation/join logic |
| AP4 | V1 sources `transaction_id` and `description` from `transactions`, neither of which is used in the External module. | **Eliminated.** V2 sources only `account_id`, `txn_type`, and `amount` from `transactions`. The `as_of` column is automatically appended by DataSourcing. | [DebitCreditRatioCalculator.cs:36-51] — only `account_id`, `txn_type`, `amount`, and `as_of` are referenced |
| AP6 | V1 uses `foreach` loops to iterate transactions row-by-row, building a `Dictionary<int, ...>` for aggregation, then another `foreach` to build output rows. | **Eliminated.** V2 uses a single set-based SQL `GROUP BY` with conditional aggregation and a `LEFT JOIN`, replacing all procedural iteration. | [DebitCreditRatioCalculator.cs:36-78] |

### Anti-Patterns Not Applicable

| AP-Code | Reason |
|---------|--------|
| AP2 | No cross-job duplication identified for this job's specific output |
| AP5 | No asymmetric NULL handling — customer_id defaults to 0 consistently |
| AP7 | No magic values — the only threshold is 0 for division-by-zero guards, which is self-documenting |
| AP8 | N/A — V1 uses no SQL |
| AP9 | Job name `DebitCreditRatio` accurately describes the output |
| AP10 | N/A — V1 uses no SQL date filtering; DataSourcing handles effective dates correctly via executor injection |

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| account_id | int | transactions.account_id | Aggregation key (GROUP BY) | BR-1 |
| customer_id | int | accounts.customer_id | LEFT JOIN on account_id; COALESCE to 0 if no match | BR-7 |
| debit_count | int | transactions | `SUM(CASE WHEN txn_type = 'Debit' THEN 1 ELSE 0 END)` | BR-2 |
| credit_count | int | transactions | `SUM(CASE WHEN txn_type = 'Credit' THEN 1 ELSE 0 END)` | BR-2 |
| debit_credit_ratio | int | Computed | `CASE WHEN credit_count > 0 THEN debit_count / credit_count ELSE 0 END` (integer division, W4) | BR-3, BR-4 |
| debit_amount | double | transactions.amount | `SUM(CASE WHEN txn_type = 'Debit' THEN amount ELSE 0.0 END)` (double precision, W6) | BR-5 |
| credit_amount | double | transactions.amount | `SUM(CASE WHEN txn_type = 'Credit' THEN amount ELSE 0.0 END)` (double precision, W6) | BR-5 |
| amount_ratio | double | Computed | `CASE WHEN credit_amount > 0.0 THEN debit_amount / credit_amount ELSE 0.0 END` (double division, W6) | BR-6 |
| as_of | text/date | transactions.as_of | `MIN(as_of)` — deterministic equivalent of V1's "first encountered" given DataSourcing orders by as_of | BR-8 |

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    t.account_id,
    -- BR-7: Customer lookup with default 0 for unmatched accounts
    COALESCE(a.customer_id, 0) AS customer_id,
    -- BR-2: Conditional debit/credit counts
    SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) AS debit_count,
    SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) AS credit_count,
    -- BR-3/BR-4 + W4: Integer division truncates (SQLite int/int = int). Zero guard returns 0.
    CASE
        WHEN SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) > 0
        THEN SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END)
           / SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END)
        ELSE 0
    END AS debit_credit_ratio,
    -- BR-5 + W6: Double-precision accumulation (SQLite REAL = IEEE 754 double)
    SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0.0 END) AS debit_amount,
    SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0.0 END) AS credit_amount,
    -- BR-6 + W6: Double-precision division with zero guard
    CASE
        WHEN SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0.0 END) > 0.0
        THEN SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0.0 END)
           / SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0.0 END)
        ELSE 0.0
    END AS amount_ratio,
    -- BR-8: Deterministic as_of (MIN equivalent to V1 "first encountered" since DataSourcing orders by as_of)
    MIN(t.as_of) AS as_of
FROM transactions t
LEFT JOIN (
    SELECT account_id, customer_id
    FROM accounts
    GROUP BY account_id
) a ON t.account_id = a.account_id
GROUP BY t.account_id, a.customer_id
```

### SQL Design Notes

1. **Integer division (W4):** SQLite performs integer division when both operands are integers. `SUM(CASE WHEN ... THEN 1 ELSE 0 END)` produces integer results, so dividing two such SUMs yields truncated integer division, exactly matching V1's C# `int / int` behavior.

2. **Double-precision (W6):** The `amount` column enters SQLite as REAL (double) because the Transformation module's `GetSqliteType` maps C# `decimal` to SQLite `REAL` [Transformation.cs:98]. `SUM()` on REAL values accumulates as double-precision. The `ELSE 0.0` literal ensures the CASE expression remains in the REAL domain.

3. **Accounts subquery:** The accounts LEFT JOIN uses a subquery with `GROUP BY account_id` to deduplicate accounts within the date range. If multiple snapshots of the same account exist (multiple as_of dates), V1's dictionary-based lookup retains the last one iterated (which is the latest as_of since DataSourcing orders by as_of). The SQL subquery's `GROUP BY account_id` without an aggregate on customer_id returns an arbitrary row per group in SQLite, but since customer_id is functionally dependent on account_id in the source data, this produces the same result.

4. **as_of (BR-8):** V1 captures the as_of from the first transaction row encountered per account. DataSourcing orders by as_of, so the first row has the minimum as_of. `MIN(t.as_of)` is the deterministic SQL equivalent. For single-day effective date runs (the standard execution pattern), all rows have the same as_of, making this trivially equivalent.

5. **Row ordering (BR-11):** V1's output order is non-deterministic (dictionary enumeration order). The SQL has no ORDER BY, so output order is also non-deterministic. Parquet is a columnar format where row order is not semantically meaningful, and Proofmark should handle row-order-independent comparison for Parquet files.

6. **Transactions with non-Debit/non-Credit txn_type (BR-10):** Such rows are included in the GROUP BY (they contribute an account_id entry and an as_of) but add 0 to both debit_count/credit_count and 0.0 to both debit_amount/credit_amount. This matches V1's behavior where the `stats.ContainsKey` check initializes the entry but only Debit/Credit branches increment counts/amounts.

7. **Empty input (BR-9):** If transactions or accounts is empty, the Transformation module's `RegisterTable` skips registration for empty DataFrames [Transformation.cs:46]. With no `transactions` table registered, the SQL query will fail. However, V1 produces an empty output DataFrame in this case. This edge case needs attention during implementation — the SQL may need to be wrapped to handle the empty-table case, or the developer should verify that an empty transactions table still gets registered (it won't if `df.Rows.Any()` is false). **This is a known limitation of Tier 1 for this job.** If the empty-input case must produce an empty DataFrame with correct columns, the developer may need to handle this in the Transformation SQL or accept that the edge case behaves differently. In practice, every effective date in the data range has transactions, so this edge case may never be exercised. The developer should verify with Proofmark.

## 6. V2 Job Config JSON

```json
{
  "jobName": "DebitCreditRatioV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["account_id", "txn_type", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT t.account_id, COALESCE(a.customer_id, 0) AS customer_id, SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) AS debit_count, SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) AS credit_count, CASE WHEN SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) > 0 THEN SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) / SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) ELSE 0 END AS debit_credit_ratio, SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0.0 END) AS debit_amount, SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0.0 END) AS credit_amount, CASE WHEN SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0.0 END) > 0.0 THEN SUM(CASE WHEN t.txn_type = 'Debit' THEN t.amount ELSE 0.0 END) / SUM(CASE WHEN t.txn_type = 'Credit' THEN t.amount ELSE 0.0 END) ELSE 0.0 END AS amount_ratio, MIN(t.as_of) AS as_of FROM transactions t LEFT JOIN (SELECT account_id, customer_id FROM accounts GROUP BY account_id) a ON t.account_id = a.account_id GROUP BY t.account_id, a.customer_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/debit_credit_ratio/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Config Changes from V1

| Field | V1 | V2 | Reason |
|-------|----|----|--------|
| jobName | `DebitCreditRatio` | `DebitCreditRatioV2` | V2 naming convention |
| transactions columns | `["transaction_id", "account_id", "txn_type", "amount", "description"]` | `["account_id", "txn_type", "amount"]` | AP4: removed unused `transaction_id`, `description` |
| accounts columns | `["account_id", "customer_id", "interest_rate", "credit_limit"]` | `["account_id", "customer_id"]` | AP1/AP4: removed unused `interest_rate`, `credit_limit` |
| Module 3 | External (DebitCreditRatioCalculator) | Transformation (SQL) | AP3: replaced unnecessary External with SQL |
| outputDirectory | `Output/curated/debit_credit_ratio/` | `Output/double_secret_curated/debit_credit_ratio/` | V2 output path |

## 7. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | ParquetFileWriter | Yes |
| source | `output` | Yes |
| outputDirectory | `Output/double_secret_curated/debit_credit_ratio/` | Path changed per V2 convention; format identical |
| numParts | 1 | Yes |
| writeMode | Overwrite | Yes |

**Write mode note:** Overwrite mode means each effective date run replaces the previous output. Only the latest day's results persist. This matches V1 behavior exactly. [debit_credit_ratio.json:29]

## 8. Proofmark Config Design

### Starting Position: Strict

```yaml
comparison_target: "debit_credit_ratio"
reader: parquet
threshold: 100.0
```

### Column Analysis for Overrides

| Column | Override? | Type | Justification |
|--------|-----------|------|---------------|
| account_id | No | STRICT | Deterministic aggregation key |
| customer_id | No | STRICT | Deterministic lookup with consistent default |
| debit_count | No | STRICT | Integer count, deterministic |
| credit_count | No | STRICT | Integer count, deterministic |
| debit_credit_ratio | No | STRICT | Integer division produces identical truncation in both C# and SQLite |
| debit_amount | Possible FUZZY | See below | W6: double-precision accumulation |
| credit_amount | Possible FUZZY | See below | W6: double-precision accumulation |
| amount_ratio | Possible FUZZY | See below | W6: double-precision division |
| as_of | No | STRICT | MIN(as_of) is deterministic; single-day runs have uniform as_of |

### W6 Double-Precision Assessment

Both V1 and V2 accumulate amounts as IEEE 754 doubles:
- **V1:** `Convert.ToDouble(row["amount"])` then `+=` in a loop
- **V2:** SQLite `REAL` type (IEEE 754 double), `SUM()` accumulation

The conversion path differs slightly:
- V1: PostgreSQL decimal -> C# decimal (in DataFrame) -> `Convert.ToDouble()` -> double addition loop
- V2: PostgreSQL decimal -> C# decimal (in DataFrame) -> SQLite parameterized insert (decimal -> REAL via SQLite affinity) -> `SUM()` as double

IEEE 754 addition is **not associative** — `(a + b) + c` may differ from `a + (b + c)` at the epsilon level. V1 accumulates left-to-right in row iteration order. SQLite's `SUM()` implementation may accumulate in a different order or use compensated summation.

**Recommendation:** Start with STRICT. If Proofmark fails on these columns, add FUZZY with a tight absolute tolerance (e.g., 1e-10). The developer should run Proofmark STRICT first and only relax if evidence of epsilon divergence appears.

### Final Proofmark Config (Initial — may need FUZZY after comparison)

```yaml
comparison_target: "debit_credit_ratio"
reader: parquet
threshold: 100.0
```

If double-precision columns fail strict comparison, update to:

```yaml
comparison_target: "debit_credit_ratio"
reader: parquet
threshold: 100.0
columns:
  fuzzy:
    - name: "debit_amount"
      tolerance: 0.0000000001
      tolerance_type: absolute
      reason: "W6: Double-precision accumulation order may differ between C# loop and SQLite SUM() — IEEE 754 addition is not associative [DebitCreditRatioCalculator.cs:41,48-50] [Transformation.cs:98]"
    - name: "credit_amount"
      tolerance: 0.0000000001
      tolerance_type: absolute
      reason: "W6: Double-precision accumulation order may differ between C# loop and SQLite SUM() — IEEE 754 addition is not associative [DebitCreditRatioCalculator.cs:41,48-50] [Transformation.cs:98]"
    - name: "amount_ratio"
      tolerance: 0.0000000001
      tolerance_type: absolute
      reason: "W6: Double-precision division on potentially epsilon-divergent numerator/denominator [DebitCreditRatioCalculator.cs:64]"
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Anti-Pattern Handling |
|----------------|-------------|-----------------|----------------------|
| BR-1: Per-account aggregation | SQL: `GROUP BY t.account_id` | SQL GROUP BY replaces C# Dictionary keying | AP6 eliminated |
| BR-2: Debit/Credit count split | SQL: `SUM(CASE WHEN txn_type = ...)` | Conditional aggregation replaces C# if/else branching | AP6 eliminated |
| BR-3: Integer division (W4) | SQL: `int_sum / int_sum` | SQLite integer division natively truncates, matching C# int/int | W4 reproduced |
| BR-4: Zero-credit guard | SQL: `CASE WHEN credit_count > 0 ... ELSE 0 END` | SQL CASE replaces C# ternary | W4 reproduced |
| BR-5: Double-precision amounts (W6) | SQL: `SUM(... ELSE 0.0 END)` on REAL columns | SQLite REAL = IEEE 754 double; SUM accumulates as double | W6 reproduced |
| BR-6: Amount ratio with zero guard | SQL: `CASE WHEN credit_amount > 0.0 ... ELSE 0.0 END` | SQL CASE with double division | W6 reproduced |
| BR-7: Customer lookup, default 0 | SQL: `LEFT JOIN ... COALESCE(a.customer_id, 0)` | SQL LEFT JOIN + COALESCE replaces C# Dictionary.GetValueOrDefault | AP3/AP6 eliminated |
| BR-8: as_of from first transaction | SQL: `MIN(t.as_of)` | Deterministic MIN equivalent to "first encountered" given ORDER BY as_of in DataSourcing | — |
| BR-9: Empty input guard | See Section 5, Note 7 | Edge case — empty DataFrames may not register in SQLite; verify with Proofmark | — |
| BR-10: Non-Debit/Credit txn_types | SQL: CASE WHEN handles only Debit/Credit; other types contribute 0 to all aggregates | Rows still participate in GROUP BY (as in V1) | — |
| BR-11: Non-deterministic row order | SQL: no ORDER BY; Parquet row order not semantically significant | Proofmark handles row-order-independent Parquet comparison | — |
| BRD: Overwrite write mode | Writer: `writeMode: Overwrite` | Matches V1 exactly | — |
| BRD: 1 Parquet part | Writer: `numParts: 1` | Matches V1 exactly | — |
| BRD: firstEffectiveDate 2024-10-01 | Config: `firstEffectiveDate: "2024-10-01"` | Matches V1 exactly | — |
| — | DataSourcing: transactions columns | Removed `transaction_id`, `description` | AP4 eliminated |
| — | DataSourcing: accounts columns | Removed `interest_rate`, `credit_limit` | AP1/AP4 eliminated |
| — | Module chain | Tier 1: DataSourcing -> Transformation -> ParquetFileWriter | AP3 eliminated |

## 10. External Module Design

**Not applicable.** This job is Tier 1 — no External module required. The V1 External module (`DebitCreditRatioCalculator`) is fully replaced by the Transformation SQL step.

### Justification for No External Module

Every operation performed by V1's `DebitCreditRatioCalculator.cs` maps directly to standard SQL:

| V1 C# Operation | SQL Equivalent |
|-----------------|----------------|
| `Dictionary<int, int>` lookup for customer_id | `LEFT JOIN` + `COALESCE` |
| `Dictionary<int, (...)>` aggregation with foreach | `GROUP BY` + conditional `SUM`/`CASE` |
| `Convert.ToDouble()` + double addition | SQLite `REAL` type + `SUM()` |
| `int / int` integer division | SQLite integer division on integer operands |
| `GetValueOrDefault(accountId, 0)` | `COALESCE(a.customer_id, 0)` |
| First-encountered `as_of` | `MIN(t.as_of)` |

No procedural logic, external I/O, cross-date-range queries, or SQLite-unsupported features are needed.
