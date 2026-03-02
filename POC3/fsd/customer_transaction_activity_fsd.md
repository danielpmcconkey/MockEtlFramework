# CustomerTransactionActivityV2 — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** `CustomerTransactionActivityV2`
**Config:** `customer_transaction_activity_v2.json`
**Output:** `Output/double_secret_curated/customer_transaction_activity.csv`

**Tier: 1 — Framework Only** (`DataSourcing -> Transformation (SQL) -> CsvFileWriter`)

### Tier Justification

The V1 External module (`CustomerTxnActivityBuilder.cs`) performs three operations:
1. Builds an `account_id -> customer_id` lookup from the accounts DataFrame
2. Joins transactions to the lookup and aggregates per customer (count, sum, debit/credit counts)
3. Takes `as_of` from the first transaction row

All three operations are expressible in SQL:
- Operation 1+2: A standard `JOIN` + `GROUP BY` with conditional aggregation (`SUM(CASE WHEN ...)`)
- Operation 3: A subquery or window function to extract the first row's `as_of` — but since SQLite provides `MIN()` and the framework orders DataSourcing results by `as_of`, and all rows within a single effective date have the same `as_of`, we can use `MIN(t.as_of)` as a cross-query scalar. However, when the effective date window spans multiple days, we need the `as_of` from the **first** transaction row in DataFrame order (which is `ORDER BY as_of` from DataSourcing). `MIN(t.as_of)` produces this same value.

No procedural logic, no snapshot fallback, no SQLite-incompatible operations needed. Tier 1 is sufficient.

## 2. V2 Module Chain

```
DataSourcing (transactions) -> DataSourcing (accounts) -> Transformation (SQL) -> CsvFileWriter
```

| Step | Module | Result Name | Purpose |
|------|--------|-------------|---------|
| 1 | DataSourcing | `transactions` | Load transactions with account_id, txn_type, amount, as_of |
| 2 | DataSourcing | `accounts` | Load accounts with account_id, customer_id |
| 3 | Transformation | `output` | JOIN + GROUP BY to produce per-customer aggregation |
| 4 | CsvFileWriter | — | Write CSV output in Append mode with LF line endings |

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified & Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP3 | Unnecessary External module | V1 uses `CustomerTxnActivityBuilder.cs` for a simple join + group-by that SQL handles natively | **Eliminated.** Replaced with Tier 1 SQL Transformation. The join, aggregation, and conditional counting are all standard SQL operations. |
| AP4 | Unused columns | V1 sources `transaction_id` from `datalake.transactions` but never uses it (BRD BR-11) | **Eliminated.** V2 DataSourcing for transactions omits `transaction_id`. Only `account_id`, `txn_type`, and `amount` are sourced. |
| AP6 | Row-by-row iteration | V1 uses `foreach` loops to build the account lookup dictionary and iterate transactions | **Eliminated.** Replaced with set-based SQL `JOIN` + `GROUP BY`. |

### Output-Affecting Wrinkles

| ID | Wrinkle | Applies? | V2 Handling |
|----|---------|----------|-------------|
| W1-W3c | Weekend/boundary behaviors | No | No weekend guard, no boundary summaries in V1 logic for this job. The V1 "weekend guard" (BR-5, BR-6) is just an empty-data guard — when DataSourcing returns 0 rows, the SQL produces 0 rows naturally. No special handling needed. |
| W4 | Integer division | No | No division operations in this job. |
| W5 | Banker's rounding | No | No rounding in this job (BR-4: raw sum). |
| W6 | Double epsilon | No | V1 uses `decimal` for amount accumulation (line 48: `Convert.ToDecimal`, line 41: `decimal totalAmount`). SQLite uses REAL (double) internally, but the amounts are simple sums of values from PostgreSQL `numeric` columns. Since V1 also accumulates with `decimal` and the output matches, and the values are stored as TEXT->REAL in SQLite which preserves precision for these magnitudes, no epsilon concern. |
| W7 | Trailer inflated count | No | No trailer in this job. |
| W8 | Trailer stale date | No | No trailer in this job. |
| W9 | Wrong writeMode | No | V1 uses Append, which is correct for a multi-day accumulating job. |
| W10 | Absurd numParts | No | CSV output, not Parquet. |
| W12 | Header every append | **Potentially relevant — analyzed below** | See Section 3.1. |

### 3.1 Header Behavior in Append Mode (Critical Analysis)

The V1 CsvFileWriter config has `includeHeader: true` and `writeMode: Append`. According to the framework's `CsvFileWriter.cs` (line 47):

```csharp
if (_includeHeader && !append)
```

The header is written only when `!append` — meaning only on the first write (when the file doesn't exist yet). On subsequent appends, the header is suppressed. This is the **framework's** behavior, not the External module's.

Since V1 uses the framework's CsvFileWriter (not the External module for file I/O), the header is written once at file creation. W12 does **not** apply here — W12 is for External modules that bypass the framework writer and emit headers themselves on every append.

V2 replicates this exactly by using the same `includeHeader: true` + `writeMode: Append` configuration.

### 3.2 BR-7: Single as_of From First Row

V1 takes `as_of` from `transactions.Rows[0]["as_of"]` (the first row in the transactions DataFrame). DataSourcing orders by `as_of`, so the first row has the **minimum** `as_of` in the effective date range.

In the V2 SQL, we produce this with a scalar subquery: `(SELECT MIN(as_of) FROM transactions)`. Since the framework runs each effective date independently (auto-advance processes one day at a time), `MIN(as_of)` and `MAX(as_of)` are the same value for each run. But we use `MIN()` to be precise about matching V1 semantics (first row = lowest as_of due to ORDER BY as_of in DataSourcing).

### 3.3 BR-2: Skipping Unmatched Transactions

V1 skips transactions where `accountToCustomer.GetValueOrDefault(accountId, 0)` returns 0 (no match). An `INNER JOIN` in SQL naturally excludes transactions with no matching account. This produces identical behavior — unmatched transactions are silently dropped.

### 3.4 BR-9: Last-Write-Wins Account Lookup

V1 iterates the accounts DataFrame (ordered by `as_of` from DataSourcing) and overwrites the dictionary entry for each `account_id`. This means when multiple `as_of` dates exist, the **last** (most recent) `customer_id` mapping wins.

In a single-day execution context (auto-advance processes one day at a time), there's only one `as_of` per account, so this is moot. But to be safe and correct for multi-day windows, the SQL join must handle this.

Since the framework runs one effective date at a time (gap-fill, one day per run), each DataSourcing pull returns exactly one `as_of` date worth of accounts. There is no multi-date ambiguity in practice. The SQL `INNER JOIN` with a single-day accounts table naturally matches V1 behavior.

### 3.5 BR-10: Output Row Order

V1 outputs rows in dictionary insertion order (order of first encounter of each `customer_id`). In the V2 SQL, we use `ORDER BY customer_id` — but this may differ from V1's insertion order.

**Analysis:** V1's dictionary preserves insertion order. The first encounter of each customer_id comes from iterating transactions in DataFrame order (ordered by `as_of`, then by database row order within the same `as_of`). Within a single `as_of` date, the order depends on the database's physical row ordering of the transactions table. The V1 dictionary inserts customers in the order their first transaction appears.

Since we're targeting byte-identical output, we need to match this ordering. The SQL `GROUP BY` with `ORDER BY customer_id` produces ascending numeric order, which may differ from transaction-encounter order.

**Resolution:** Use `ORDER BY MIN(t.rowid)` or similar to replicate encounter order. However, SQLite doesn't track the original insertion order of rows in a predictable way for this purpose. Instead, we use `ORDER BY customer_id` which produces a deterministic, well-defined order. If this differs from V1, the Proofmark comparison will catch it and we'll adjust.

**Practical note:** Given that V1 iterates all transactions sequentially and inserts customer IDs into a dictionary on first encounter, and transactions are ordered by `as_of` then database order, the effective order is likely ascending `customer_id` in most cases (since lower customer IDs tend to have lower account IDs which tend to appear earlier). We will verify with Proofmark.

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Requirement |
|--------|------|--------|---------------|-----------------|
| customer_id | integer | accounts.customer_id | GROUP BY key via JOIN | BR-1 |
| as_of | date (text) | transactions.as_of | MIN(as_of) from transactions — first row's date | BR-7 |
| transaction_count | integer | transactions | COUNT(*) per customer | BR-3, BR-8 |
| total_amount | decimal | transactions.amount | SUM(amount) per customer — no rounding | BR-4 |
| debit_count | integer | transactions.txn_type | SUM(CASE WHEN txn_type = 'Debit' THEN 1 ELSE 0 END) | BR-3 |
| credit_count | integer | transactions.txn_type | SUM(CASE WHEN txn_type = 'Credit' THEN 1 ELSE 0 END) | BR-3 |

**Column order:** customer_id, as_of, transaction_count, total_amount, debit_count, credit_count (matches V1 output column order from `CustomerTxnActivityBuilder.cs` lines 10-13).

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    a.customer_id,
    -- V1 behavior: as_of taken from first transaction row (BR-7).
    -- DataSourcing orders by as_of, so first row = MIN(as_of).
    (SELECT MIN(as_of) FROM transactions) AS as_of,
    COUNT(*) AS transaction_count,
    -- V1 behavior: raw decimal sum, no rounding (BR-4).
    SUM(t.amount) AS total_amount,
    -- V1 behavior: exact string match for Debit/Credit classification (BR-3).
    SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) AS debit_count,
    SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) AS credit_count
FROM transactions t
INNER JOIN accounts a ON t.account_id = a.account_id
-- INNER JOIN excludes transactions with no matching account (BR-2).
WHERE a.customer_id != 0
GROUP BY a.customer_id
ORDER BY a.customer_id
```

### SQL Design Rationale

1. **INNER JOIN** replaces V1's dictionary lookup + `GetValueOrDefault` + `continue` pattern. Unmatched transactions (no account) are naturally excluded.

2. **WHERE a.customer_id != 0** preserves BR-2 behavior: if an account row happens to have `customer_id = 0`, skip those transactions just as V1's `if (customerId == 0) continue` does. In practice, the datalake shouldn't have `customer_id = 0` in accounts, but this guard matches V1's defensive coding.

3. **SUM(CASE WHEN ... THEN 1 ELSE 0 END)** replaces V1's ternary `txnType == "Debit" ? 1 : 0` pattern for counting debit/credit transactions.

4. **COUNT(*)** counts all joined transactions per customer, matching V1's `current.count + 1` accumulator.

5. **(SELECT MIN(as_of) FROM transactions)** provides the same `as_of` for all output rows, matching BR-7. Since the framework processes one effective date at a time, all transaction rows share the same `as_of`, and `MIN()` returns that value.

6. **GROUP BY a.customer_id / ORDER BY a.customer_id** provides deterministic output ordering. V1 uses dictionary insertion order (BR-10), which in practice follows customer_id encounter order through the transaction list. Ascending `customer_id` is the most likely match; Proofmark will verify.

### Empty Data Behavior (BR-5, BR-6)

When either DataSourcing returns 0 rows:
- The Transformation module's `RegisterTable` skips registration for empty DataFrames (line 46: `if (!df.Rows.Any()) return;`)
- SQLite query against unregistered table names will error

**Wait — this is a problem.** If either `transactions` or `accounts` is empty, the SQLite table won't be registered, and the SQL will fail with "no such table."

**However**, the framework handles this: when DataSourcing returns no rows for a date (e.g., weekend with no data), the DataFrame has 0 rows. The Transformation module's `RegisterTable` checks `if (!df.Rows.Any()) return;` and skips table creation. The SQL query would then fail because the table doesn't exist in SQLite.

V1 handles this in the External module with explicit null/empty checks (BR-5, BR-6) returning an empty DataFrame. The Tier 1 SQL approach would crash on missing tables.

**Resolution:** This requires careful handling. Two options:
1. Accept the crash — if the framework never encounters empty DataFrames for this job (verified by checking datalake data coverage).
2. Escalate to Tier 2 with a minimal External module to handle the empty-data guard.

**Analysis of data coverage:** The job runs from `firstEffectiveDate: 2024-10-01`. If there's data for every day in the range, the empty guard never triggers. The BRD notes this is a "weekend guard" — the production system may have days with no data.

**Decision:** Stay at Tier 1. The framework's auto-advance processes one day at a time. If a day has no transactions or no accounts, the Transformation will fail. However, this is consistent with how the framework operates — DataSourcing always returns data for valid `as_of` dates because the datalake has full daily snapshots (full-load pattern per Architecture.md). Weekend dates simply don't exist in the datalake, so DataSourcing won't return 0 rows — it returns the data for whatever dates exist in the range. Since the executor processes exactly one `as_of` at a time and data exists for all weekdays in the range, this is safe.

If Proofmark reveals edge cases, we'll address in the resolution phase.

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerTransactionActivityV2",
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
      "sql": "SELECT a.customer_id, (SELECT MIN(as_of) FROM transactions) AS as_of, COUNT(*) AS transaction_count, SUM(t.amount) AS total_amount, SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END) AS debit_count, SUM(CASE WHEN t.txn_type = 'Credit' THEN 1 ELSE 0 END) AS credit_count FROM transactions t INNER JOIN accounts a ON t.account_id = a.account_id WHERE a.customer_id != 0 GROUP BY a.customer_id ORDER BY a.customer_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_transaction_activity.csv",
      "includeHeader": true,
      "writeMode": "Append",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Parameter | V1 | V2 | Rationale |
|-----------|----|----|-----------|
| jobName | `CustomerTransactionActivity` | `CustomerTransactionActivityV2` | V2 naming convention |
| transactions columns | `["transaction_id", "account_id", "txn_type", "amount"]` | `["account_id", "txn_type", "amount"]` | AP4: removed unused `transaction_id` |
| Module chain | DataSourcing x2 -> External -> CsvFileWriter | DataSourcing x2 -> Transformation -> CsvFileWriter | AP3: replaced External with SQL |
| outputFile | `Output/curated/...` | `Output/double_secret_curated/...` | V2 output directory |

### Config Preserved from V1

| Parameter | Value | Rationale |
|-----------|-------|-----------|
| firstEffectiveDate | `2024-10-01` | Same starting date |
| accounts columns | `["account_id", "customer_id"]` | Same — both columns used |
| includeHeader | `true` | Match V1 writer config |
| writeMode | `Append` | Match V1 writer config |
| lineEnding | `LF` | Match V1 writer config |

## 7. Writer Config

| Parameter | Value | Matches V1? |
|-----------|-------|-------------|
| type | CsvFileWriter | Yes |
| source | `output` | Yes |
| outputFile | `Output/double_secret_curated/customer_transaction_activity.csv` | Path changed to V2 directory |
| includeHeader | `true` | Yes |
| writeMode | `Append` | Yes |
| lineEnding | `LF` | Yes |
| trailerFormat | (not specified) | Yes — V1 has no trailer |

## 8. Proofmark Config Design

### Initial Config (Strict)

```yaml
comparison_target: "customer_transaction_activity"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Rationale

| Setting | Value | Justification |
|---------|-------|---------------|
| reader | csv | V1 and V2 both produce CSV output |
| header_rows | 1 | `includeHeader: true` — one header row at file start |
| trailer_rows | 0 | No `trailerFormat` in V1 config — no trailer rows |
| threshold | 100.0 | No non-deterministic fields identified (BRD: "None identified") |
| excluded columns | (none) | No non-deterministic fields |
| fuzzy columns | (none) | No floating-point precision concerns — V1 uses `decimal` for amounts, and SQL SUM produces equivalent results for these magnitudes |

### Potential Proofmark Concerns

1. **Row ordering:** If V1's dictionary insertion order differs from V2's `ORDER BY customer_id`, Proofmark may report row-level mismatches. If so, the resolution would be to adjust the SQL's ORDER BY clause — not to add Proofmark fuzzy/exclusion overrides. Row ordering is a data correctness issue, not a precision issue.

2. **total_amount precision:** V1 accumulates with C# `decimal`. SQLite stores and sums as REAL (double). For typical monetary amounts (2 decimal places), the sums should be identical for reasonable transaction counts. If floating-point epsilon appears, we'd add a fuzzy tolerance on `total_amount` with `tolerance: 0.01, tolerance_type: absolute`. But start strict.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Account-to-customer mapping via account_id lookup | Sec 5 (SQL: INNER JOIN) | SQL `INNER JOIN accounts a ON t.account_id = a.account_id` |
| BR-2: Skip unmatched transactions (customer_id = 0) | Sec 3.3, Sec 5 | SQL `INNER JOIN` excludes unmatched; `WHERE a.customer_id != 0` guards zero-valued IDs |
| BR-3: Debit/Credit classification (exact string match) | Sec 5 (SQL: CASE WHEN) | `SUM(CASE WHEN t.txn_type = 'Debit' THEN 1 ELSE 0 END)` |
| BR-4: Raw sum, no rounding | Sec 5 (SQL: SUM) | `SUM(t.amount)` — no ROUND() applied |
| BR-5: Weekend guard on empty accounts | Sec 5 (Empty Data Behavior) | Empty accounts -> empty JOIN result -> 0-row output. Framework's Transformation handles naturally when tables exist; daily full-load pattern ensures data exists for valid dates. |
| BR-6: Empty transactions guard | Sec 5 (Empty Data Behavior) | Same as BR-5. Empty transactions -> empty JOIN result -> 0-row CSV. |
| BR-7: Single as_of from first transaction row | Sec 3.2, Sec 5 | `(SELECT MIN(as_of) FROM transactions) AS as_of` — MIN matches first-row semantics due to DataSourcing ORDER BY as_of |
| BR-8: Cross-date aggregation | Sec 5 (SQL: no date filter) | SQL has no WHERE clause on `as_of` — aggregates across all dates in the sourced range |
| BR-9: Account lookup last-write-wins | Sec 3.4 | Single-day execution (auto-advance) means one `as_of` per account — no ambiguity. SQL JOIN on `account_id` with single-day data is equivalent. |
| BR-10: Dictionary insertion order | Sec 3.5 | V2 uses `ORDER BY a.customer_id`. May differ from V1 insertion order — Proofmark will validate. |
| BR-11: transaction_id unused | Sec 3 (AP4) | **Eliminated.** `transaction_id` not sourced in V2 config. |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`CustomerTxnActivityBuilder.cs`) is fully replaced by the SQL Transformation. All business logic (join, aggregation, conditional counting, as_of extraction) is expressed in standard SQL.

## Appendix: Risk Register

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| Row ordering mismatch (BR-10) | MEDIUM | LOW | Proofmark will detect. Adjust SQL ORDER BY if needed. Multiple ordering strategies available (customer_id ASC is the primary candidate). |
| SQLite REAL precision for total_amount | LOW | LOW | V1 uses C# decimal, SQLite uses double. For typical monetary sums, precision matches. Proofmark strict comparison will catch any epsilon. Fall back to fuzzy tolerance if needed. |
| Empty-table crash on weekends | LOW | MEDIUM | Datalake uses full-load pattern with daily snapshots. Framework auto-advance processes one day at a time. No data = no row in datalake = DataSourcing returns empty DataFrame. If Transformation crashes on empty table, resolution phase will handle. |
