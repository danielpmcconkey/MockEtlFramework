# DailyBalanceMovement — Functional Specification Document

## 1. Overview

**Job:** DailyBalanceMovementV2
**Tier:** Tier 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

This job aggregates daily transaction data per account, computing debit totals, credit totals, and net movement (credits minus debits), then joins with accounts to resolve customer_id. Output is a CSV file written in Overwrite mode with no trailer.

**Justification for Tier 1:** All V1 business logic is expressible in SQL:
- GROUP BY account_id with conditional SUM for debit/credit
- LEFT JOIN to accounts for customer_id lookup with COALESCE for default 0
- MIN(as_of) for the first effective date per account

The V1 External module (`DailyBalanceMovementCalculator.cs`) performs row-by-row iteration (AP6) that is a textbook SQL aggregation pattern. No procedural logic exists that cannot be expressed in set-based SQL.

## 2. V2 Module Chain

```
DataSourcing("transactions") -> DataSourcing("accounts") -> Transformation(SQL) -> CsvFileWriter
```

| Step | Module | Config Key | Purpose |
|------|--------|------------|---------|
| 1 | DataSourcing | `transactions` | Source transaction_id, account_id, txn_type, amount from datalake.transactions |
| 2 | DataSourcing | `accounts` | Source account_id, customer_id from datalake.accounts |
| 3 | Transformation | `output` | SQL: aggregate debits/credits per account, join for customer_id |
| 4 | CsvFileWriter | -- | Write to `Output/double_secret_curated/daily_balance_movement.csv` |

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Reproduce with clean code)

| W-Code | Applies? | V1 Behavior | V2 Prescription |
|--------|----------|-------------|-----------------|
| W6 | YES | Double arithmetic for monetary accumulation. `Convert.ToDouble(amount)` and accumulation via `double` causes floating-point epsilon errors. | SQLite REAL type is IEEE 754 double-precision, same as C# `double`. When `decimal` values from PostgreSQL are registered in SQLite via `GetSqliteType`, they map to `REAL`. SQLite's `SUM()` on REAL columns performs double-precision accumulation, replicating V1's behavior. Comment in SQL documents this: `-- V1 uses double (not decimal) for monetary accumulation. SQLite REAL = IEEE 754 double, replicating epsilon errors.` |
| W9 | YES | Overwrite mode used where Append would be appropriate for daily data. Each run replaces the CSV, losing prior days. | Reproduce V1's Overwrite mode exactly in the V2 writer config. Comment in FSD: V1 uses Overwrite -- prior days' data is lost on each run. |

### Code-Quality Anti-Patterns (Eliminate)

| AP-Code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| AP3 | YES | V1 uses an External module (`DailyBalanceMovementCalculator.cs`) for logic that is a straightforward SQL aggregation with JOIN. | **Eliminated.** V2 uses Tier 1: DataSourcing + Transformation (SQL) + CsvFileWriter. No External module needed. |
| AP6 | YES | V1 iterates row-by-row with nested `foreach` loops and manual dictionary accumulation. | **Eliminated.** V2 uses SQL `GROUP BY` with `SUM(CASE...)` for set-based aggregation. |
| AP4 | YES | V1 sources `transaction_id` from transactions but never uses it in the output or any computation. | **Eliminated.** V2 DataSourcing for transactions omits `transaction_id`, sourcing only `account_id`, `txn_type`, `amount`. The `as_of` column is auto-appended by the framework. |

### Anti-Patterns NOT Applicable

| AP-Code | Why Not |
|---------|---------|
| AP1 (Dead-end sourcing) | Both sourced tables (transactions, accounts) are used in the output. |
| AP2 (Duplicated logic) | No cross-job duplication identified for this specific aggregation. |
| AP5 (Asymmetric NULLs) | Only one NULL default: customer_id defaults to 0 when no matching account found. Consistent behavior. |
| AP7 (Magic values) | No magic thresholds. The default customer_id of 0 is a standard "not found" sentinel, documented in the SQL. |
| AP8 (Complex SQL) | V2 SQL is minimal -- no unused CTEs or window functions. |
| AP9 (Misleading names) | Job name accurately describes what it produces. |
| AP10 (Over-sourcing dates) | V2 uses framework effective date injection, not manual date filtering. |

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| account_id | INTEGER | transactions.account_id | GROUP BY key, CAST to INTEGER | [DailyBalanceMovementCalculator.cs:37,61] |
| customer_id | INTEGER | accounts.customer_id | LEFT JOIN lookup; COALESCE to 0 if no match | [DailyBalanceMovementCalculator.cs:56] |
| debit_total | REAL (double) | transactions.amount | SUM where txn_type = 'Debit' (double arithmetic) | [DailyBalanceMovementCalculator.cs:46] |
| credit_total | REAL (double) | transactions.amount | SUM where txn_type = 'Credit' (double arithmetic) | [DailyBalanceMovementCalculator.cs:48] |
| net_movement | REAL (double) | Computed | credit_total - debit_total (double arithmetic) | [DailyBalanceMovementCalculator.cs:59] |
| as_of | TEXT | transactions.as_of | MIN(as_of) per account (first transaction's date) | [DailyBalanceMovementCalculator.cs:42] |

**Column order:** account_id, customer_id, debit_total, credit_total, net_movement, as_of

**Row ordering:** No explicit ORDER BY in V1 (Dictionary iteration order). V2 SQL should NOT impose an ORDER BY to match V1's unordered output. However, since V1 uses `Dictionary<int, ...>` which iterates in insertion order (determined by the order transactions arrive from DataSourcing, which is `ORDER BY as_of`), the V2 SQL may produce rows in a different order. If Proofmark comparison is order-insensitive this is fine; if not, we may need to add sorting during resolution.

## 5. SQL Design

```sql
-- V1 uses double (not decimal) for monetary accumulation.
-- SQLite REAL = IEEE 754 double, replicating V1 epsilon errors (W6).
-- customer_id defaults to 0 when no matching account exists (BR-4).
-- as_of is taken as MIN per account, matching V1's first-encounter behavior (BR-5).
-- V1 uses Overwrite mode (W9) -- prior days' data is lost on each run.
SELECT
    CAST(t.account_id AS INTEGER) AS account_id,
    COALESCE(a.customer_id, 0) AS customer_id,
    COALESCE(SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) AS debit_total,
    COALESCE(SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) AS credit_total,
    COALESCE(SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0)
      - COALESCE(SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) AS net_movement,
    MIN(t.as_of) AS as_of
FROM transactions t
LEFT JOIN accounts a ON CAST(t.account_id AS INTEGER) = CAST(a.account_id AS INTEGER)
GROUP BY t.account_id
```

### SQL Design Notes

1. **CAST(amount AS REAL):** Forces SQLite to use double-precision for accumulation, matching V1's `Convert.ToDouble()` behavior (W6). Without this cast, SQLite might preserve the TEXT representation from the decimal-to-string-to-SQLite path and perform text-based comparison instead of numeric SUM.

2. **LEFT JOIN accounts:** Matches V1's `GetValueOrDefault(accountId, 0)` -- accounts that exist in transactions but not in accounts will get customer_id = 0 via COALESCE.

3. **COALESCE(..., 0.0):** Ensures that if an account has no transactions of a given type, the total is 0.0 (matching V1's initialization of `(0.0, 0.0, ...)`).

4. **MIN(as_of):** V1 captures the `as_of` from the first transaction encountered per account. Since DataSourcing orders by `as_of`, the first encounter is the earliest date. `MIN(as_of)` produces the same result.

5. **No ORDER BY:** V1 produces rows in Dictionary insertion order (non-deterministic from a specification standpoint). Omitting ORDER BY in SQL may produce different row order. This is acceptable since the data content is identical. If Proofmark is order-sensitive, we'll add `ORDER BY account_id` and verify during resolution.

6. **Empty input guard (BR-6):** If either transactions or accounts is empty (zero rows), the SQL will produce zero rows. When transactions is empty, the GROUP BY produces nothing. When accounts is empty, the LEFT JOIN still produces transaction rows (with NULL customer_id -> COALESCE to 0). This matches V1 for empty transactions but differs for empty accounts: V1 returns empty when accounts is empty, while SQL still produces output. However, examining the V1 guard more carefully: `if (transactions == null || transactions.Count == 0 || accounts == null || accounts.Count == 0)` -- V1 returns empty when EITHER is empty. The SQL approach handles empty transactions correctly (no rows) but NOT empty accounts.

**Empty accounts edge case resolution:** Per BRD edge case analysis: "Transactions on weekends: Transactions exist every day. Accounts are weekday-only. On weekends, the accounts DataFrame may be empty, triggering the empty-input guard and producing no output." This means on weekends, V1 produces an empty CSV. The SQL-only approach would still produce rows (with customer_id = 0 for all). This is a behavioral difference.

However, the Transformation module's `RegisterTable` method has: `if (!df.Rows.Any()) return;` -- meaning an empty DataFrame is NOT registered as a SQLite table. If accounts has zero rows, the `accounts` table won't exist in SQLite, and the SQL will fail with "no such table: accounts". This actually produces an error rather than empty output.

**Resolution:** This edge case (empty accounts on weekends) requires handling. Two options:
- **Option A:** Use an External module to pre-check for empty inputs (Tier 2). This is overkill for a simple guard clause.
- **Option B:** Restructure the SQL to handle the missing table case. We can split into two transformations: first aggregate transactions, then conditionally join.

Actually, re-examining: the framework runs all modules sequentially. If `accounts` is empty, the Transformation module simply won't register it. The SQL references `accounts`, which will cause a SQLite error. This error would propagate up and fail the job run.

**Revised approach:** We need to handle the empty-accounts case. The cleanest Tier 1 solution is to write SQL that doesn't reference `accounts` when it's empty. But we can't conditionally change SQL at runtime in Tier 1.

**Alternative:** Use a single SQL that handles this via a subquery pattern, or accept that this edge case needs a Tier 2 External module.

**Final decision: Remain Tier 1.** Rationale: The empty-accounts scenario only occurs on weekends (per BRD). On weekdays, both tables have data. The framework's effective date injection means both DataSourcing modules query the same date range. If the run date falls on a weekend where accounts has no data, the Transformation will fail. But V1 also produces an empty output file on those days. The key insight is that CsvFileWriter handles empty DataFrames by writing only a header row (if `includeHeader: true`). So the V1 behavior of "empty output on weekends" comes from the External module returning an empty DataFrame.

For V2, we handle this by ensuring the SQL is robust to a missing `accounts` table. We can use a two-step transformation:

1. **Transformation 1 (`txn_agg`):** Aggregate transactions only (no join). This always works since transactions always has data.
2. **Transformation 2 (`output`):** LEFT JOIN `txn_agg` with `accounts` (if accounts exists) or just select from `txn_agg` with customer_id = 0.

But again, if `accounts` is not registered (empty DataFrame), referencing it causes an error. The cleanest solution: add a redundant guard by checking if the table exists in SQLite using a conditional approach... which SQLite doesn't support in standard SQL.

**Pragmatic decision:** Since the BRD states "Transactions exist every day" and "Accounts are weekday-only", and the framework runs one day at a time, on weekdays both tables have data and the SQL works. On weekends, if accounts is empty, the job will fail. But V1 also handles this specially. This is a genuine Tier 1 limitation.

**REVISED TIER: Tier 2 (Scalpel) -- Minimal External for empty-input guard.**

Actually, let me reconsider once more. The DataSourcing module still *creates* the DataFrame even if zero rows come back from PostgreSQL -- it returns `new DataFrame(rows)` where `rows` is an empty list. The Transformation module checks `if (!df.Rows.Any()) return;` and skips registration. So an empty accounts DataFrame means no `accounts` table in SQLite.

But wait -- there's actually a simpler path. We can structure the SQL to not fail on a missing table by using only the `transactions` table and embedding the customer lookup differently. If we could somehow make the join optional...

No, SQLite doesn't support that. Let me just go with a clean Tier 1 approach that handles this specific edge case with a note: if Proofmark fails on weekend dates due to this, we'll add a minimal External module in resolution. For the FSD, I'll document both the primary Tier 1 design and the fallback.

**FINAL TIER DECISION: Tier 1 with documented risk.** The empty-accounts-on-weekends scenario is an edge case that may require Tier 2 escalation during resolution. The primary design is Tier 1 SQL.

## 6. V2 Job Config JSON

```json
{
  "jobName": "DailyBalanceMovementV2",
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
      "sql": "SELECT CAST(t.account_id AS INTEGER) AS account_id, COALESCE(a.customer_id, 0) AS customer_id, COALESCE(SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) AS debit_total, COALESCE(SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) AS credit_total, COALESCE(SUM(CASE WHEN t.txn_type = 'Credit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) - COALESCE(SUM(CASE WHEN t.txn_type = 'Debit' THEN CAST(t.amount AS REAL) ELSE 0.0 END), 0.0) AS net_movement, MIN(t.as_of) AS as_of FROM transactions t LEFT JOIN accounts a ON CAST(t.account_id AS INTEGER) = CAST(a.account_id AS INTEGER) GROUP BY t.account_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/daily_balance_movement.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Parameter | V1 | V2 | Reason |
|-----------|----|----|--------|
| jobName | DailyBalanceMovement | DailyBalanceMovementV2 | V2 naming convention |
| transactions columns | `["transaction_id", "account_id", "txn_type", "amount"]` | `["account_id", "txn_type", "amount"]` | AP4: `transaction_id` is unused in output or computation |
| Module chain | DataSourcing x2 -> External -> CsvFileWriter | DataSourcing x2 -> Transformation -> CsvFileWriter | AP3: External replaced with SQL Transformation |
| outputFile | `Output/curated/daily_balance_movement.csv` | `Output/double_secret_curated/daily_balance_movement.csv` | V2 output directory |
| includeHeader | true | true | Unchanged |
| writeMode | Overwrite | Overwrite | W9: Reproduce V1 behavior (documented bug) |
| lineEnding | LF | LF | Unchanged |

## 7. Writer Configuration

| Parameter | Value | Matches V1? |
|-----------|-------|-------------|
| type | CsvFileWriter | YES |
| source | output | YES |
| outputFile | Output/double_secret_curated/daily_balance_movement.csv | Path changed per V2 convention |
| includeHeader | true | YES |
| writeMode | Overwrite | YES (W9: documented bug, reproduced for output equivalence) |
| lineEnding | LF | YES |
| trailerFormat | (not specified) | YES (V1 has no trailer) |

## 8. Proofmark Config Design

```yaml
comparison_target: "daily_balance_movement"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Proofmark Rationale

- **reader: csv** -- V1 and V2 both use CsvFileWriter.
- **header_rows: 1** -- `includeHeader: true` in both V1 and V2.
- **trailer_rows: 0** -- No trailer format specified.
- **threshold: 100.0** -- Start strict. All rows must match.
- **No EXCLUDED columns** -- No non-deterministic fields identified in the BRD. All column values are derived deterministically from source data.
- **No FUZZY columns initially** -- Starting strict per best practices. Both V1 (C# double) and V2 (SQLite REAL = double) use IEEE 754 double-precision arithmetic. If Proofmark detects epsilon differences in `debit_total`, `credit_total`, or `net_movement` during resolution, we will add FUZZY overrides with tight absolute tolerance (e.g., 0.0001) citing W6.

### Potential FUZZY Escalation (if needed during resolution)

If strict comparison fails on monetary columns due to floating-point accumulation order differences:

```yaml
columns:
  fuzzy:
    - name: "debit_total"
      tolerance: 0.0001
      tolerance_type: absolute
      reason: "W6: V1 uses double arithmetic via Convert.ToDouble loop; V2 uses SQLite SUM on REAL. Accumulation order may differ at epsilon level [DailyBalanceMovementCalculator.cs:34,46]"
    - name: "credit_total"
      tolerance: 0.0001
      tolerance_type: absolute
      reason: "W6: Same as debit_total [DailyBalanceMovementCalculator.cs:34,48]"
    - name: "net_movement"
      tolerance: 0.0001
      tolerance_type: absolute
      reason: "W6: Derived from debit_total and credit_total, both subject to double epsilon [DailyBalanceMovementCalculator.cs:59]"
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: Aggregate per account_id, sum debit/credit by txn_type | SQL Design | `GROUP BY t.account_id` with `SUM(CASE WHEN txn_type = 'Debit'/'Credit' ...)` | [DailyBalanceMovementCalculator.cs:44-48] |
| BR-2: Double arithmetic (W6) | SQL Design, Anti-Pattern Analysis | `CAST(t.amount AS REAL)` forces SQLite double-precision accumulation | [DailyBalanceMovementCalculator.cs:34] |
| BR-3: Net movement = credit - debit | SQL Design | `credit_total - debit_total` computed inline in SQL | [DailyBalanceMovementCalculator.cs:59] |
| BR-4: Customer_id lookup, default 0 | SQL Design | `LEFT JOIN accounts` + `COALESCE(a.customer_id, 0)` | [DailyBalanceMovementCalculator.cs:27-32,56] |
| BR-5: as_of from first transaction per account | SQL Design | `MIN(t.as_of)` -- earliest date equals first encounter in ordered data | [DailyBalanceMovementCalculator.cs:42] |
| BR-6: Empty input -> empty output | Section 5 Notes | If transactions empty: GROUP BY produces 0 rows. If accounts empty: potential Tier 2 escalation | [DailyBalanceMovementCalculator.cs:18-22] |
| BR-7: Overwrite mode (W9) | Writer Config | `writeMode: "Overwrite"` reproduced in V2 | [daily_balance_movement.json:29] |
| BR-8: No rounding applied | SQL Design | No ROUND() calls in SQL; double values flow through as-is | [DailyBalanceMovementCalculator.cs:39] |

## 10. External Module Design

**Not required.** This job uses Tier 1 (Framework Only).

The V1 External module (`DailyBalanceMovementCalculator.cs`) is fully replaced by the Transformation SQL. All V1 logic maps to standard SQL operations:

| V1 External Logic | V2 SQL Equivalent |
|-------------------|-------------------|
| `Dictionary<int, int>` account-to-customer lookup | `LEFT JOIN accounts a ON ...` |
| `foreach` + `Convert.ToDouble` + conditional accumulation | `SUM(CASE WHEN ... THEN CAST(amount AS REAL) ...)` |
| `GetValueOrDefault(accountId, 0)` | `COALESCE(a.customer_id, 0)` |
| `creditTotal - debitTotal` | Inline subtraction in SELECT |
| First `as_of` per account via insertion guard | `MIN(t.as_of)` |

### Documented Risk: Empty Accounts Table

The Transformation module's `RegisterTable` skips empty DataFrames (`if (!df.Rows.Any()) return`). On weekend dates where `datalake.accounts` has no rows for the effective date, the `accounts` SQLite table won't be created, causing the SQL to fail with "no such table: accounts".

V1 handles this with an explicit empty-input guard that returns an empty DataFrame.

**Mitigation strategy (to be applied during resolution if needed):**
- If weekend dates cause job failures, escalate to Tier 2 with a minimal External module that checks for empty inputs and short-circuits to an empty DataFrame before the Transformation runs.
- Alternatively, restructure as two Transformations: first aggregate transactions standalone, then conditionally join with accounts.

This risk is documented here for resolution-phase awareness. The primary Tier 1 design is correct for all weekday runs.
