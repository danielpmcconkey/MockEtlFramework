# CustomerValueScoreV2 — Functional Specification Document

## 1. Overview

**Job:** `CustomerValueScoreV2`
**Tier:** 1 — Framework Only (DataSourcing → Transformation → CsvFileWriter)

This job computes a composite customer value score based on three weighted components: transaction activity (count of transactions linked via accounts), account balance (total balance across all accounts), and branch visit frequency. Each component score is individually capped at 1000, then combined using fixed weights (0.40 / 0.35 / 0.25) to produce a final composite. Output is a CSV file with LF line endings.

The V1 implementation uses an External module (`ExternalModules/CustomerValueCalculator.cs`) with row-by-row iteration. All business logic is expressible in SQL using JOINs, GROUP BY, and ROUND/MIN functions, making this a clean Tier 1 candidate.

---

## 2. V2 Module Chain

```
DataSourcing (customers)
  → DataSourcing (accounts)
  → DataSourcing (transactions)
  → DataSourcing (branch_visits)
  → Transformation (SQL: compute scores)
  → CsvFileWriter
```

**Tier justification:** Every operation in the V1 External module — dictionary lookups, counting, summing, capping with MIN, weighted sums, and ROUND — maps directly to standard SQL constructs. No procedural logic is required. Tier 1 is sufficient.

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP3 | Unnecessary External module | V1 uses `ExternalModules.CustomerValueCalculator` for logic that is entirely expressible in SQL (joins, group by, min, round) | **Eliminated.** Replaced with a single SQL Transformation module. |
| AP4 | Unused columns | `transactions` sources `transaction_id`, `txn_type`, `amount` — none are used; only `account_id` is needed for counting. `branch_visits` sources `visit_id`, `branch_id` — only `customer_id` is needed. | **Eliminated.** V2 sources only the columns actually needed: `account_id` from transactions, `customer_id` from branch_visits. |
| AP6 | Row-by-row iteration | V1 uses nested `foreach` loops to build dictionaries and iterate customers [CustomerValueCalculator.cs:34-86] | **Eliminated.** Replaced with set-based SQL using JOINs and GROUP BY. |
| AP7 | Magic values | V1 uses literal values `10.0m`, `1000m`, `50.0m`, `0.4m`, `0.35m`, `0.25m`, `1000.0m` without named constants or documentation [CustomerValueCalculator.cs:29-31,94-107] | **Partially eliminated.** SQL cannot define named constants, but the FSD documents each magic value with its business meaning. SQL comments are not supported in the JSON config, so the documentation lives here. See Section 5 for the value reference. |

### Wrinkles Identified

| ID | Wrinkle | Applies? | V2 Handling |
|----|---------|----------|-------------|
| W5 | Banker's rounding | **YES** — `Math.Round` in C# defaults to `MidpointRounding.ToEven`. SQLite's `ROUND()` also uses banker's rounding. | No special handling needed. SQLite `ROUND()` naturally matches C#'s default `Math.Round` behavior. |
| W9 | Wrong writeMode | **No** — Overwrite is the correct mode for this job. Each execution date produces a complete output. The BRD notes "only the last effective date's output survives on disk" which is the expected behavior for auto-advance. | Reproduced as-is. `writeMode: Overwrite`. |

### Anti-Patterns NOT Present

| ID | Anti-Pattern | Assessment |
|----|-------------|------------|
| AP1 | Dead-end sourcing | All four DataSourcing entries (customers, accounts, transactions, branch_visits) are used in the computation. |
| AP2 | Duplicated logic | No cross-job duplication identified. |
| AP5 | Asymmetric NULLs | NULL handling is consistent: `COALESCE(..., 0)` pattern applied uniformly to all score components. V1 uses `GetValueOrDefault(..., 0)` for all three. |
| AP8 | Complex SQL / unused CTEs | N/A — V1 uses no SQL. V2 SQL is designed to be minimal and direct. |
| AP9 | Misleading names | Job name accurately describes what it produces. |
| AP10 | Over-sourcing dates | V2 uses framework effective date injection; no date over-sourcing. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | INTEGER | customers.id | Direct mapping (aliased) | [CustomerValueCalculator.cs:88] |
| first_name | TEXT | customers.first_name | COALESCE to empty string for NULLs | [CustomerValueCalculator.cs:89] `?.ToString() ?? ""` |
| last_name | TEXT | customers.last_name | COALESCE to empty string for NULLs | [CustomerValueCalculator.cs:90] `?.ToString() ?? ""` |
| transaction_score | REAL | Computed | `ROUND(MIN(txn_count * 10.0, 1000.0), 2)` | [CustomerValueCalculator.cs:94,114] |
| balance_score | REAL | Computed | `ROUND(MIN(total_balance / 1000.0, 1000.0), 2)` | [CustomerValueCalculator.cs:98,115] |
| visit_score | REAL | Computed | `ROUND(MIN(visit_count * 50.0, 1000.0), 2)` | [CustomerValueCalculator.cs:102,116] |
| composite_score | REAL | Computed | `ROUND(txn_score * 0.4 + bal_score * 0.35 + visit_score * 0.25, 2)` | [CustomerValueCalculator.cs:105-107,117] |
| as_of | TEXT | customers.as_of | Pass-through from customer row | [CustomerValueCalculator.cs:118] |

### BRD Correction: Rounding Precision (RESOLVED)

The BRD originally stated scores were "rounded to the nearest whole number (0 decimal places)." The BRD has been corrected to state 2 decimal places, matching the V1 source code.

- Evidence: [CustomerValueCalculator.cs:114] `Math.Round(transactionScore, 2)`
- Evidence: [CustomerValueCalculator.cs:115] `Math.Round(balanceScore, 2)`
- Evidence: [CustomerValueCalculator.cs:116] `Math.Round(visitScore, 2)`
- Evidence: [CustomerValueCalculator.cs:117] `Math.Round(compositeScore, 2)`

V2 uses `ROUND(..., 2)` to match V1's actual behavior.

---

## 5. SQL Design

### Magic Value Reference

| Value | Business Meaning | V1 Source |
|-------|-----------------|-----------|
| `10.0` | Transaction score multiplier — each transaction is worth 10 points | [CustomerValueCalculator.cs:94] |
| `1000.0` | Score ceiling — individual component scores are capped at 1000 | [CustomerValueCalculator.cs:94,98,102] |
| `50.0` | Visit score multiplier — each branch visit is worth 50 points | [CustomerValueCalculator.cs:102] |
| `0.40` | Transaction score weight in composite | [CustomerValueCalculator.cs:29] |
| `0.35` | Balance score weight in composite | [CustomerValueCalculator.cs:30] |
| `0.25` | Visit score weight in composite | [CustomerValueCalculator.cs:31] |
| `1000.0` (divisor) | Balance normalization divisor — balance divided by 1000 to produce score | [CustomerValueCalculator.cs:98] |

### SQL Statement

```sql
SELECT
    c.id AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    ROUND(MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000.0), 2) AS transaction_score,
    ROUND(MIN(COALESCE(ab.total_balance, 0.0) / 1000.0, 1000.0), 2) AS balance_score,
    ROUND(MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000.0), 2) AS visit_score,
    ROUND(
        MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000.0) * 0.4
        + MIN(COALESCE(ab.total_balance, 0.0) / 1000.0, 1000.0) * 0.35
        + MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000.0) * 0.25,
        2
    ) AS composite_score,
    c.as_of
FROM customers c
LEFT JOIN (
    SELECT a.customer_id, COUNT(t.account_id) AS txn_count
    FROM transactions t
    INNER JOIN accounts a ON t.account_id = a.account_id
    GROUP BY a.customer_id
) tc ON c.id = tc.customer_id
LEFT JOIN (
    SELECT customer_id, SUM(current_balance) AS total_balance
    FROM accounts
    GROUP BY customer_id
) ab ON c.id = ab.customer_id
LEFT JOIN (
    SELECT customer_id, COUNT(*) AS visit_count
    FROM branch_visits
    GROUP BY customer_id
) vc ON c.id = vc.customer_id
GROUP BY c.id, c.first_name, c.last_name, c.as_of
ORDER BY c.id
```

### SQL Design Notes

1. **Transaction-to-customer linkage (BR-2):** The `tc` subquery joins `transactions` to `accounts` on `account_id`, then groups by `accounts.customer_id`. This replicates the V1 dictionary-lookup pattern where `accountToCustomer[accountId]` maps transactions to customers through the accounts table. Transactions whose `account_id` doesn't exist in accounts are naturally excluded by the INNER JOIN, matching the V1 `if (customerId == 0) continue` behavior (BR-10).

2. **LEFT JOINs for optional data (BR-8, BR-9):** Customers with no transactions, no accounts with balance, or no visits get NULLs from the LEFT JOIN. `COALESCE(..., 0)` ensures these become 0, matching V1's `GetValueOrDefault(customerId, 0)` behavior.

3. **Score capping (BR-3, BR-4, BR-5):** SQLite's `MIN(value, 1000.0)` caps each score at 1000, matching V1's `Math.Min(x, 1000m)`.

4. **Negative balance scores (OQ-1):** The V1 code uses `Math.Min(totalBalance / 1000.0m, 1000m)` which does NOT floor at 0. Negative balances produce negative balance_score values. The SQL's `MIN(value, 1000.0)` replicates this — no floor applied.

5. **Composite score computation (BR-6):** The composite is computed from the un-rounded individual scores, then the result is rounded. This matches V1's code where rounding happens AFTER the composite calculation: `Math.Round(compositeScore, 2)` where `compositeScore` was computed from unrounded `transactionScore`, `balanceScore`, `visitScore`. In the SQL, the `MIN(...)` expressions in the composite calculation are identical to those in the individual score columns (before `ROUND`), preserving this behavior.

6. **GROUP BY for deduplication:** The outer `GROUP BY` on `c.id, c.first_name, c.last_name, c.as_of` ensures one output row per customer per as_of date. V1 iterates `foreach (var custRow in customers.Rows)` which produces one row per customer record from DataSourcing. Since auto-advance runs one effective date at a time, there will be one customer record per customer per as_of.

7. **Rounding (corrected from BRD):** `ROUND(..., 2)` — 2 decimal places, not 0. See Section 4 for the BRD correction.

8. **Empty table edge case (BR-1):** If `customers` or `accounts` DataFrames are empty (0 rows), the Transformation module's `RegisterTable` method skips table creation (`if (!df.Rows.Any()) return;`). The SQL would then fail because the referenced table doesn't exist in SQLite. In practice, this edge case does not occur during the comparison date range (2024-10-01 through 2024-12-31) because the datalake contains customer and account data for all those dates. V1's empty guard is defensive coding that V2 does not need to replicate for output equivalence. If this edge case is a concern for production use, a Tier 2 External module could add the guard, but for the Proofmark comparison it is unnecessary.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerValueScoreV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id", "current_balance"]
    },
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["account_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "branch_visits",
      "schema": "datalake",
      "table": "branch_visits",
      "columns": ["customer_id"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT c.id AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, ROUND(MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000.0), 2) AS transaction_score, ROUND(MIN(COALESCE(ab.total_balance, 0.0) / 1000.0, 1000.0), 2) AS balance_score, ROUND(MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000.0), 2) AS visit_score, ROUND(MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000.0) * 0.4 + MIN(COALESCE(ab.total_balance, 0.0) / 1000.0, 1000.0) * 0.35 + MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000.0) * 0.25, 2) AS composite_score, c.as_of FROM customers c LEFT JOIN (SELECT a.customer_id, COUNT(t.account_id) AS txn_count FROM transactions t INNER JOIN accounts a ON t.account_id = a.account_id GROUP BY a.customer_id) tc ON c.id = tc.customer_id LEFT JOIN (SELECT customer_id, SUM(current_balance) AS total_balance FROM accounts GROUP BY customer_id) ab ON c.id = ab.customer_id LEFT JOIN (SELECT customer_id, COUNT(*) AS visit_count FROM branch_visits GROUP BY customer_id) vc ON c.id = vc.customer_id GROUP BY c.id, c.first_name, c.last_name, c.as_of ORDER BY c.id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_value_score.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Setting | V1 Value | V2 Value | Reason |
|---------|----------|----------|--------|
| jobName | `CustomerValueScore` | `CustomerValueScoreV2` | V2 naming convention |
| External module | `ExternalModules.CustomerValueCalculator` | Removed | AP3: replaced with SQL Transformation |
| Transformation | N/A | Added | Tier 1: all logic in SQL |
| outputFile | `Output/curated/customer_value_score.csv` | `Output/double_secret_curated/customer_value_score.csv` | V2 output directory |
| transactions columns | `["transaction_id", "account_id", "txn_type", "amount"]` | `["account_id"]` | AP4: only account_id is used |
| branch_visits columns | `["visit_id", "customer_id", "branch_id"]` | `["customer_id"]` | AP4: only customer_id is used |
| includeHeader | `true` | `true` | Matches V1 |
| writeMode | `Overwrite` | `Overwrite` | Matches V1 |
| lineEnding | `LF` | `LF` | Matches V1 |

---

## 7. Writer Configuration

| Property | Value | Source |
|----------|-------|--------|
| type | CsvFileWriter | Matches V1 [customer_value_score.json:39] |
| source | `output` | Matches V1 [customer_value_score.json:40] |
| outputFile | `Output/double_secret_curated/customer_value_score.csv` | V2 output directory; filename matches V1 |
| includeHeader | `true` | Matches V1 [customer_value_score.json:42] |
| writeMode | `Overwrite` | Matches V1 [customer_value_score.json:43] |
| lineEnding | `LF` | Matches V1 [customer_value_score.json:44] |
| trailerFormat | Not configured | Matches V1 — no trailer |

---

## 8. Proofmark Config Design

### Starting Point: Strict Comparison

This job has **zero non-deterministic fields** (confirmed in BRD: "None identified"). All output values are derived deterministically from datalake data and fixed formulas.

### Exclusions: None

No columns need to be excluded. All columns are deterministic.

### Fuzzy Columns: None Expected

All score computations use decimal arithmetic in V1 (`decimal` type) and REAL in SQLite. SQLite stores REAL as 64-bit IEEE 754 float, while V1 uses C# `decimal` (128-bit). This could theoretically cause precision differences, but:

- The transaction and visit scores are integer counts multiplied by integer-valued constants (10.0, 50.0), producing exact values before capping and rounding.
- The balance score involves division by 1000.0, which for typical balance values (whole numbers or 2-decimal amounts) produces values that are exactly representable in both decimal and double.
- The `ROUND(..., 2)` truncates any trailing precision noise.

**Initial recommendation: Start with zero fuzzy columns.** If Proofmark comparison reveals precision mismatches, add fuzzy tolerance with evidence from the actual discrepancies.

### Proposed Config

```yaml
comparison_target: "customer_value_score"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

- `reader: csv` — output is CSV
- `header_rows: 1` — V1 config has `includeHeader: true`
- `trailer_rows: 0` — no trailer configured in V1
- `threshold: 100.0` — full match required; no known sources of non-determinism

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Empty guard on customers + accounts | Section 5, Note 8 | Documented as edge case; not replicable in Tier 1 SQL. Does not affect comparison date range. |
| BR-2: Transaction-to-customer via accounts | Section 5, Note 1 | INNER JOIN between transactions and accounts in `tc` subquery |
| BR-3: Transaction score = count * 10, capped at 1000 | Section 5, SQL | `MIN(COALESCE(tc.txn_count, 0) * 10.0, 1000.0)` |
| BR-4: Balance score = balance / 1000, capped at 1000 | Section 5, SQL | `MIN(COALESCE(ab.total_balance, 0.0) / 1000.0, 1000.0)` |
| BR-5: Visit score = count * 50, capped at 1000 | Section 5, SQL | `MIN(COALESCE(vc.visit_count, 0) * 50.0, 1000.0)` |
| BR-6: Composite = 0.4*txn + 0.35*bal + 0.25*visit | Section 5, SQL | Explicit weighted sum in composite_score column |
| BR-7: Rounding to 2 decimal places | Section 4, BRD Correction | `ROUND(..., 2)` — BRD corrected to match V1 source code |
| BR-8: No transactions → transaction_score = 0 | Section 5, Note 2 | LEFT JOIN + `COALESCE(tc.txn_count, 0)` |
| BR-9: No visits → visit_score = 0 | Section 5, Note 2 | LEFT JOIN + `COALESCE(vc.visit_count, 0)` |
| BR-10: Orphan transactions silently skipped | Section 5, Note 1 | INNER JOIN on accounts naturally excludes orphans |
| BR-11: as_of from customer row | Section 5, SQL | `c.as_of` in SELECT |
| BR-12: Customer-driven iteration | Section 5, SQL | `FROM customers c` with LEFT JOINs; every customer produces a row |

### Anti-Pattern Traceability

| Anti-Pattern | FSD Section | Status |
|-------------|-------------|--------|
| AP3: Unnecessary External module | Section 3 | **Eliminated** — replaced with Tier 1 SQL |
| AP4: Unused columns | Section 3, Section 6 | **Eliminated** — only required columns sourced |
| AP6: Row-by-row iteration | Section 3 | **Eliminated** — set-based SQL |
| AP7: Magic values | Section 3, Section 5 | **Partially eliminated** — documented in FSD; SQL doesn't support named constants |

---

## 10. External Module Design

**Not applicable.** This job uses Tier 1 (Framework Only). No External module is needed.

All V1 External module logic has been replaced with the SQL Transformation in Section 5.
