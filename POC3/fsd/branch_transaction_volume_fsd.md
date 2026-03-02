# BranchTransactionVolume — Functional Specification Document

## 1. Overview

**V2 Job Name:** `BranchTransactionVolumeV2`
**V2 Config File:** `JobExecutor/Jobs/branch_transaction_volume_v2.json`
**Tier:** 1 — Framework Only (DataSourcing → Transformation → ParquetFileWriter)

**What V2 does:** Joins transactions to accounts on `account_id` and `as_of`, then aggregates per account per date to produce `txn_count` (COUNT) and `total_amount` (ROUND(SUM, 2)). Output is Parquet, one part file, Overwrite mode. No External module is needed — all logic is expressible in SQL.

**Why Tier 1:** The V1 job is already a pure Tier 1 chain (DataSourcing → Transformation → ParquetFileWriter). The transformation SQL is a straightforward JOIN + GROUP BY + aggregation, all of which SQLite handles natively. There is no procedural logic, no snapshot fallback, no multi-pass processing, and no operation that requires C#. Tier 1 is the correct and simplest choice.

## 2. V2 Module Chain

### Module 1: DataSourcing — `transactions`

| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | transactions |
| schema | datalake |
| table | transactions |
| columns | transaction_id, account_id, txn_type, amount |

**Notes:**
- `description` is sourced in V1 but never referenced in the transformation SQL (BR-7, AP4). Removed in V2.
- Effective dates are injected by the executor via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys. The framework automatically adds `as_of` to the result and filters by the date range.

### Module 2: DataSourcing — `accounts`

| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | accounts |
| schema | datalake |
| table | accounts |
| columns | account_id, customer_id |

**Notes:**
- `interest_rate` is sourced in V1 but never referenced in the transformation SQL (BR-7, AP4). Removed in V2.

### Module 3: Transformation — `branch_txn_vol`

| Field | Value |
|-------|-------|
| type | Transformation |
| resultName | branch_txn_vol |
| sql | See Section 5 |

### Module 4: ParquetFileWriter

| Field | Value |
|-------|-------|
| type | ParquetFileWriter |
| source | branch_txn_vol |
| outputDirectory | Output/double_secret_curated/branch_transaction_volume/ |
| numParts | 1 |
| writeMode | Overwrite |

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes) — Must Reproduce

**None identified.** This job has no wrinkles that affect output:
- No Sunday skip (W1), no weekend fallback (W2), no boundary rows (W3a/b/c)
- No integer division (W4) — uses COUNT(*) and ROUND(SUM, 2)
- No banker's rounding concern (W5) — SQLite ROUND uses standard rounding, matching V1 behavior
- No double-precision accumulation (W6) — SUM and ROUND are handled in SQLite, same as V1
- No trailer (W7, W8) — Parquet output
- writeMode is Overwrite, which is correct for this job's use case (W9 not applicable — Overwrite is the right mode here)
- numParts is 1, which is reasonable for this dataset (W10 not applicable)
- No CSV headers (W12) — Parquet output

### Code-Quality Anti-Patterns (AP-codes) — Must Eliminate

| AP Code | Identified? | V1 Evidence | V2 Action |
|---------|-------------|-------------|-----------|
| AP1 | YES | V1 sources `branches` table (resultName "branches") — never referenced in SQL [branch_transaction_volume.json:20-22, :36] | **Eliminated.** Removed `branches` DataSourcing entry entirely. |
| AP1 | YES | V1 sources `customers` table (resultName "customers") — never referenced in SQL [branch_transaction_volume.json:26-28, :36] | **Eliminated.** Removed `customers` DataSourcing entry entirely. |
| AP3 | NO | V1 already uses framework modules (no External module). No action needed. | N/A |
| AP4 | YES | V1 sources `description` from transactions — never used in SQL [branch_transaction_volume.json:10, :36] | **Eliminated.** Removed `description` from transactions columns list. |
| AP4 | YES | V1 sources `interest_rate` from accounts — never used in SQL [branch_transaction_volume.json:16, :36] | **Eliminated.** Removed `interest_rate` from accounts columns list. |
| AP4 | YES | V1 sources `txn_type` from transactions — never used in SQL [branch_transaction_volume.json:10, :36]. While `txn_type` is selected by DataSourcing, it is not referenced in the Transformation SQL SELECT, WHERE, or GROUP BY. | **Eliminated.** Removed `txn_type` from transactions columns list. |
| AP9 | YES | Job is named "BranchTransactionVolume" but output contains no branch dimension — no branch_id, branch_name, or branch join [branch_transaction_volume.json:36; BRD OQ-1] | **Cannot rename.** Output filenames and job structure must match V1. Documented here as a known misleading name. |

**Note on AP4 and `txn_type`:** The BRD (BR-8) documents that all transaction types are included with no filtering. The column `txn_type` is sourced but never appears in the SQL — not in SELECT, not in WHERE, not in GROUP BY. It is dead weight. V2 removes it. This does NOT affect output because the column was never used in any computation.

**Note on AP4 and `transaction_id`:** The column `transaction_id` is sourced and also not explicitly used in the SQL SELECT output. However, it is implicitly counted by `COUNT(*)`. Since `COUNT(*)` counts rows regardless of column content, `transaction_id` is not needed for the aggregation. However, removing it has no effect on output because the Transformation module registers all sourced columns as SQLite table columns, and the SQL only SELECTs what it needs. We keep `transaction_id` removed from the sourcing? No — let's be precise. `COUNT(*)` counts rows, not columns. The presence or absence of `transaction_id` in the source doesn't change row counts. But to be conservative and match the data shape V1 provides to the SQL engine, we keep `transaction_id` in the DataSourcing columns. This ensures the SQLite table has the same rows. Actually — the rows are determined by the effective date filter, not by which columns are selected. Removing a column from DataSourcing does not change row count. However, `transaction_id` is explicitly listed in V1's sourcing, so let's examine whether removing it could affect anything. It cannot — the SQL never references it. **Decision: Remove `transaction_id` from V2 DataSourcing columns.** It is AP4 — sourced but unused. COUNT(*) counts rows regardless.

**Revised Module 1 columns:** `account_id, amount`

Wait — we need to be careful. Let me re-examine the SQL:

```sql
SELECT t.account_id, a.customer_id, COUNT(*) AS txn_count,
       ROUND(SUM(t.amount), 2) AS total_amount, t.as_of
FROM transactions t
JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
GROUP BY t.account_id, a.customer_id, t.as_of
ORDER BY t.as_of, t.account_id
```

Columns actually referenced from `transactions`:
- `t.account_id` — used in SELECT, JOIN, GROUP BY, ORDER BY
- `t.amount` — used in SUM
- `t.as_of` — used in JOIN, SELECT, GROUP BY, ORDER BY (auto-included by DataSourcing)

Columns actually referenced from `accounts`:
- `a.account_id` — used in JOIN
- `a.customer_id` — used in SELECT, GROUP BY
- `a.as_of` — used in JOIN (auto-included by DataSourcing)

**Final V2 DataSourcing columns:**
- transactions: `account_id`, `amount` (as_of auto-included)
- accounts: `account_id`, `customer_id` (as_of auto-included)

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| account_id | integer | transactions.account_id | Direct passthrough, grouped | BR-2 |
| customer_id | integer | accounts.customer_id | Via JOIN on account_id + as_of | BR-1, BR-2 |
| txn_count | integer | transactions (all rows) | COUNT(*) per group | BR-9 |
| total_amount | real | transactions.amount | ROUND(SUM(amount), 2) per group | BR-3 |
| as_of | text (date) | transactions.as_of | Passthrough, grouped | BR-2 |

**Column order:** account_id, customer_id, txn_count, total_amount, as_of — determined by the SQL SELECT clause.

**Type notes:**
- `account_id` and `customer_id` are integers from PostgreSQL, mapped to INTEGER in SQLite, then to `int?` in Parquet.
- `txn_count` is COUNT(*) result — INTEGER in SQLite, `int?` in Parquet (via the `GetParquetType` mapping for `long` → `long?`; actually SQLite COUNT returns `long`, so this maps to `long?` in Parquet).
- `total_amount` is ROUND(SUM(...)) — REAL in SQLite, `double?` in Parquet.
- `as_of` is stored as TEXT in SQLite (DateOnly → "yyyy-MM-dd" via `ToSqliteValue`), so it will be `string` in Parquet.

## 5. SQL Design

```sql
SELECT t.account_id,
       a.customer_id,
       COUNT(*) AS txn_count,
       ROUND(SUM(t.amount), 2) AS total_amount,
       t.as_of
FROM transactions t
JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
GROUP BY t.account_id, a.customer_id, t.as_of
ORDER BY t.as_of, t.account_id
```

**This SQL is identical to V1.** The V1 SQL is clean, correct, and produces the right output. There are no anti-patterns in the SQL itself — no unused CTEs (AP8), no magic values (AP7), no complex unnecessary logic. The only changes are at the DataSourcing level (removing dead-end sources and unused columns).

**SQLite compatibility:** All functions used (COUNT, SUM, ROUND, JOIN, GROUP BY, ORDER BY) are standard SQL supported by SQLite. No PostgreSQL-specific syntax. No window functions. No CTEs.

## 6. V2 Job Config

```json
{
  "jobName": "BranchTransactionVolumeV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["account_id", "amount"]
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
      "resultName": "branch_txn_vol",
      "sql": "SELECT t.account_id, a.customer_id, COUNT(*) AS txn_count, ROUND(SUM(t.amount), 2) AS total_amount, t.as_of FROM transactions t JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of GROUP BY t.account_id, a.customer_id, t.as_of ORDER BY t.as_of, t.account_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "branch_txn_vol",
      "outputDirectory": "Output/double_secret_curated/branch_transaction_volume/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### V1 → V2 Diff Summary

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| jobName | BranchTransactionVolume | BranchTransactionVolumeV2 | Naming convention |
| DataSourcing: branches | Present | **Removed** | AP1: dead-end source, never used in SQL |
| DataSourcing: customers | Present | **Removed** | AP1: dead-end source, never used in SQL |
| transactions columns | transaction_id, account_id, txn_type, amount, description | account_id, amount | AP4: removed unused columns |
| accounts columns | account_id, customer_id, interest_rate | account_id, customer_id | AP4: removed unused column |
| Transformation SQL | (identical) | (identical) | SQL is clean, no changes needed |
| outputDirectory | Output/curated/branch_transaction_volume/ | Output/double_secret_curated/branch_transaction_volume/ | V2 output path convention |
| numParts | 1 | 1 | Matches V1 |
| writeMode | Overwrite | Overwrite | Matches V1 |

## 7. Writer Configuration

| Field | Value | Matches V1? |
|-------|-------|-------------|
| Writer Type | ParquetFileWriter | YES — V1 uses ParquetFileWriter |
| source | branch_txn_vol | YES — same result name |
| outputDirectory | Output/double_secret_curated/branch_transaction_volume/ | Path changed per V2 convention |
| numParts | 1 | YES |
| writeMode | Overwrite | YES |

## 8. Proofmark Config Design

**Reader:** `parquet` (V1 uses ParquetFileWriter)
**Threshold:** `100.0` (strict — no reason to lower)

**Excluded columns:** None.
- No non-deterministic fields identified in the BRD.
- All output columns are deterministic: account_id, customer_id, txn_count, total_amount, as_of.

**Fuzzy columns:** None.
- `total_amount` uses ROUND(SUM, 2) in SQLite. Both V1 and V2 run the same SQL in the same SQLite Transformation engine, so the rounding behavior is identical. No epsilon concerns.
- No double-precision accumulation outside SQLite (W6 not applicable).

**Proposed Proofmark config:**

```yaml
comparison_target: "branch_transaction_volume"
reader: parquet
threshold: 100.0
```

Zero exclusions, zero fuzzy. Starting strict as prescribed. If comparison fails, evidence from the failure will drive any config adjustments.

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|--------------|-----------------|----------|
| Tier 1 (no External module) | N/A — V1 is already Tier 1 | [branch_transaction_volume.json] — no External module in V1 |
| Remove branches DataSourcing | BR-5: branches table sourced but unused | [branch_transaction_volume.json:20-22, :36] |
| Remove customers DataSourcing | BR-6: customers table sourced but unused | [branch_transaction_volume.json:26-28, :36] |
| Remove description column | BR-7: description sourced but unused | [branch_transaction_volume.json:10, :36] |
| Remove interest_rate column | BR-7: interest_rate sourced but unused | [branch_transaction_volume.json:16, :36] |
| Remove transaction_id column | AP4: sourced but unused in SQL | [branch_transaction_volume.json:10, :36] — not in SELECT/WHERE/GROUP BY |
| Remove txn_type column | BR-8, AP4: all types included, column not referenced | [branch_transaction_volume.json:10, :36] — no WHERE on txn_type |
| JOIN on account_id AND as_of | BR-1: date-aligned snapshot join | [branch_transaction_volume.json:36] |
| GROUP BY account_id, customer_id, as_of | BR-2: one row per account per date | [branch_transaction_volume.json:36] |
| ROUND(SUM(amount), 2) | BR-3: 2 decimal places | [branch_transaction_volume.json:36] |
| ORDER BY as_of, account_id | BR-4: ordering | [branch_transaction_volume.json:36] |
| COUNT(*) | BR-9: count all rows, not specific column | [branch_transaction_volume.json:36] |
| ParquetFileWriter, numParts=1 | BRD Writer Configuration | [branch_transaction_volume.json:39-44] |
| writeMode Overwrite | BRD Write Mode Implications | [branch_transaction_volume.json:43] |
| Proofmark: zero exclusions | BRD Non-Deterministic Fields: "None identified" | No evidence for any non-deterministic output |
| AP9: misleading job name documented | BRD OQ-1 | Job name implies branch data but output has no branch dimension |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

All business logic is expressed in the Transformation SQL. The framework's DataSourcing handles data ingestion with effective date filtering, the Transformation module handles the JOIN/GROUP BY/aggregation in SQLite, and the ParquetFileWriter handles output.
