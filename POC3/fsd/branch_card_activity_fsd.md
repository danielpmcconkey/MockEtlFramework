# BranchCardActivity — Functional Specification Document

## 1. Overview

BranchCardActivityV2 produces a per-branch, per-date summary of card transaction activity. Customers are synthetically mapped to branches via a modulo operation (`customer_id % MAX(branch_id) + 1`), then card transactions are aggregated per branch per `as_of` date, yielding transaction counts and rounded monetary totals.

**Tier: 1 (Framework Only)** — `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Justification:** The entire V1 pipeline is already DataSourcing + Transformation + ParquetFileWriter. All business logic (modulo branch assignment, aggregation, rounding) is expressible in SQLite SQL. There is no procedural logic, no snapshot fallback, no cross-boundary date queries, and no operation that requires an External module. Tier 1 is the correct and simplest choice.

## 2. V2 Module Chain

### Module 1: DataSourcing — `card_transactions`
| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | card_transactions |
| schema | datalake |
| table | card_transactions |
| columns | `["card_txn_id", "customer_id", "amount"]` |

**Changes from V1:** Removed `card_id` and `authorization_status` columns. Neither is referenced in the transformation SQL. This eliminates AP4 (unused columns).

### Module 2: DataSourcing — `branches`
| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | branches |
| schema | datalake |
| table | branches |
| columns | `["branch_id", "branch_name"]` |

**Changes from V1:** Removed `country` column. It is sourced in V1 but never referenced in the transformation SQL. This eliminates AP4 (unused columns).

### Module 3: DataSourcing — `customers`
| Field | Value |
|-------|-------|
| type | DataSourcing |
| resultName | customers |
| schema | datalake |
| table | customers |
| columns | `["id"]` |

**Changes from V1:** Removed `first_name` and `last_name` columns. The `customers` table is joined solely as an existence filter (inner join eliminates card transactions without a matching customer). No customer columns appear in the output. Only `id` is needed for the join. This eliminates AP4 (unused columns).

### Module 4: DataSourcing — `segments` (REMOVED)

**Change from V1:** The `segments` DataSourcing module is completely removed. The `segments` table is sourced in V1 but never referenced in the transformation SQL. This eliminates AP1 (dead-end sourcing).

### Module 5: Transformation
| Field | Value |
|-------|-------|
| type | Transformation |
| resultName | output |
| sql | See Section 5 |

### Module 6: ParquetFileWriter
| Field | Value |
|-------|-------|
| type | ParquetFileWriter |
| source | output |
| outputDirectory | `Output/double_secret_curated/branch_card_activity/` |
| numParts | 50 |
| writeMode | Overwrite |

**Note on numParts:** V1 uses 50 parts for a dataset with at most 40 rows per date (one per branch). This is W10 (absurd numParts). We reproduce the same value for output equivalence. Most part files will be empty.

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles to Reproduce

| W-Code | Applies? | V2 Handling |
|--------|----------|-------------|
| W10 | YES | `numParts: 50` reproduced exactly. With max ~40 branches per date, most parts will be empty. This does not affect data correctness. |

No other W-codes apply to this job:
- W1/W2/W3a/W3b/W3c: No Sunday skip, weekend fallback, or boundary summary rows in this job.
- W4: No integer division. `ROUND(SUM(ct.amount), 2)` uses proper rounding.
- W5: SQLite's `ROUND()` function is used identically in V1 and V2, so banker's rounding behavior (if any) is naturally reproduced.
- W6: No double-precision accumulation in External module code -- aggregation happens in SQLite.
- W7/W8/W12: No trailer rows (Parquet writer, not CSV).
- W9: Overwrite mode is correct for this job (produces all branches for all dates in one pass).

### Code-Quality Anti-Patterns to Eliminate

| AP-Code | Applies? | V1 Problem | V2 Fix |
|---------|----------|------------|--------|
| AP1 | YES | `segments` table sourced but never used in SQL | Removed `segments` DataSourcing module entirely |
| AP3 | NO | V1 already uses framework modules (DataSourcing + Transformation + Writer), not an External | N/A |
| AP4 | YES | `card_id`, `authorization_status` sourced from card_transactions but unused; `country` sourced from branches but unused; `first_name`, `last_name` sourced from customers but unused | Removed all unused columns from DataSourcing configs |
| AP8 | NO | V1 SQL is straightforward -- no unused CTEs or window functions | N/A |
| AP9 | NO | Job name accurately describes what it produces | N/A |
| AP10 | NO | DataSourcing uses framework effective date injection (no explicit date columns in V1 config, no WHERE clause on dates in SQL) -- the framework handles date filtering at the source | N/A |

Anti-patterns AP2, AP5, AP6, AP7 do not apply to this job.

## 4. Output Schema

| Column | Type | Source Table | Source Column | Transformation | BRD Ref |
|--------|------|-------------|---------------|----------------|---------|
| branch_id | INTEGER | branches | branch_id | Derived via modulo mapping: `(customer_id % MAX(branch_id)) + 1` | BR-1 |
| branch_name | TEXT | branches | branch_name | Direct lookup via branch_id join | BR-7 |
| card_txn_count | INTEGER | card_transactions | card_txn_id | `COUNT(ct.card_txn_id)` per group | BR-7 |
| total_card_amount | REAL | card_transactions | amount | `ROUND(SUM(ct.amount), 2)` per group | BR-8 |
| as_of | TEXT | card_transactions | as_of | Passthrough from card_transactions, used as grouping key | BR-7 |

## 5. SQL Design

The V2 SQL is functionally identical to V1. The transformation logic has not changed -- only the sourced columns and tables feeding into it are cleaned up.

```sql
SELECT
    b.branch_id,
    b.branch_name,
    COUNT(ct.card_txn_id) AS card_txn_count,
    ROUND(SUM(ct.amount), 2) AS total_card_amount,
    ct.as_of
FROM card_transactions ct
JOIN customers c ON ct.customer_id = c.id
JOIN branches b ON b.branch_id = (ct.customer_id % (SELECT MAX(branch_id) FROM branches)) + 1
GROUP BY b.branch_id, b.branch_name, ct.as_of
```

**SQL Walkthrough:**

1. **FROM card_transactions ct** — Start with all card transactions in the effective date range (loaded by DataSourcing with framework date injection).

2. **JOIN customers c ON ct.customer_id = c.id** — Inner join filters out card transactions where the customer_id has no match in the customers table. No customer columns are selected. (BR-4)

3. **JOIN branches b ON b.branch_id = (ct.customer_id % (SELECT MAX(branch_id) FROM branches)) + 1** — Synthetic branch assignment. The subquery `SELECT MAX(branch_id) FROM branches` operates over the full `branches` DataFrame (all as_of dates in the effective range), yielding the maximum branch_id across all snapshots. (BR-1, BR-6)

4. **COUNT(ct.card_txn_id) AS card_txn_count** — Counts all card transactions per group, regardless of authorization_status. (BR-2)

5. **ROUND(SUM(ct.amount), 2) AS total_card_amount** — Sums transaction amounts and rounds to 2 decimal places. (BR-8)

6. **GROUP BY b.branch_id, b.branch_name, ct.as_of** — One row per branch per date. (BR-7)

**Note on segments:** The `segments` table is not referenced anywhere in this SQL. V1 sources it but never uses it. V2 does not source it at all. (BR-5, AP1)

**Note on authorization_status:** All transactions are included regardless of status (Approved/Declined). V1 does not filter on this column, and neither does V2. The column is simply not sourced in V2 since it is not referenced in the SQL. (BR-2, AP4)

## 6. V2 Job Config

```json
{
  "jobName": "BranchCardActivityV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["card_txn_id", "customer_id", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "branches",
      "schema": "datalake",
      "table": "branches",
      "columns": ["branch_id", "branch_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT b.branch_id, b.branch_name, COUNT(ct.card_txn_id) AS card_txn_count, ROUND(SUM(ct.amount), 2) AS total_card_amount, ct.as_of FROM card_transactions ct JOIN customers c ON ct.customer_id = c.id JOIN branches b ON b.branch_id = (ct.customer_id % (SELECT MAX(branch_id) FROM branches)) + 1 GROUP BY b.branch_id, b.branch_name, ct.as_of"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/branch_card_activity/",
      "numParts": 50,
      "writeMode": "Overwrite"
    }
  ]
}
```

## 7. Writer Configuration

| Parameter | Value | Matches V1? | Notes |
|-----------|-------|-------------|-------|
| Writer type | ParquetFileWriter | YES | Same writer type as V1 |
| source | output | YES | Same source DataFrame name |
| outputDirectory | `Output/double_secret_curated/branch_card_activity/` | PATH CHANGE | V1: `Output/curated/branch_card_activity/`. V2 writes to `double_secret_curated` per project convention. |
| numParts | 50 | YES | Reproduced exactly (W10 -- excessive for ~40 rows/date, but required for output equivalence) |
| writeMode | Overwrite | YES | Each run replaces entire output directory |

## 8. Proofmark Config Design

**Starting assumption:** Zero exclusions, zero fuzzy overrides.

**Analysis of each output column:**

| Column | Deterministic? | Precision Concern? | Decision |
|--------|---------------|-------------------|----------|
| branch_id | YES -- derived from deterministic modulo operation | No | STRICT |
| branch_name | YES -- direct lookup | No | STRICT |
| card_txn_count | YES -- COUNT is deterministic | No | STRICT |
| total_card_amount | YES -- SUM + ROUND in SQLite, same engine for V1 and V2 | No -- both V1 and V2 use SQLite ROUND(x, 2) identically | STRICT |
| as_of | YES -- passthrough from source data | No | STRICT |

**Non-deterministic fields from BRD:** None identified.

**Conclusion:** All columns should use strict comparison. No exclusions or fuzzy overrides are warranted.

**Proofmark Config:**

```yaml
comparison_target: "branch_card_activity"
reader: parquet
threshold: 100.0
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Modulo branch assignment | Sec 5 (SQL), Sec 4 (Output Schema) | SQL JOIN condition `b.branch_id = (ct.customer_id % (SELECT MAX(branch_id) FROM branches)) + 1` preserved exactly |
| BR-2: No authorization_status filter | Sec 3 (Anti-Pattern), Sec 5 (SQL) | No WHERE filter on authorization_status. Column removed from DataSourcing (AP4) since it is not referenced in SQL |
| BR-3: card_id unused | Sec 2 (Module 1), Sec 3 (Anti-Pattern) | `card_id` removed from DataSourcing columns (AP4) |
| BR-4: customers JOIN as filter only | Sec 2 (Module 3), Sec 5 (SQL) | Inner join on `ct.customer_id = c.id` preserved. Only `id` column sourced from customers (AP4) |
| BR-5: segments unused | Sec 2 (Module 4 REMOVED), Sec 3 (Anti-Pattern) | Entire `segments` DataSourcing module removed (AP1) |
| BR-6: MAX across all dates | Sec 5 (SQL Walkthrough) | Subquery `SELECT MAX(branch_id) FROM branches` operates on full DataFrame across all as_of dates in effective range -- behavior preserved |
| BR-7: Group by branch+date | Sec 4 (Output Schema), Sec 5 (SQL) | `GROUP BY b.branch_id, b.branch_name, ct.as_of` preserved exactly |
| BR-8: ROUND to 2 decimals | Sec 4 (Output Schema), Sec 5 (SQL) | `ROUND(SUM(ct.amount), 2)` preserved exactly |
| BR-9: country unused | Sec 2 (Module 2), Sec 3 (Anti-Pattern) | `country` removed from branches DataSourcing columns (AP4) |
| OQ-1: Vestigial columns | Sec 3 (Anti-Pattern Analysis) | All vestigial columns and tables eliminated via AP1 and AP4 |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed. All business logic is expressed in SQL within the Transformation module.
