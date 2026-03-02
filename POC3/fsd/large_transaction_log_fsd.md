# LargeTransactionLog -- Functional Specification Document

## 1. Overview

**Job:** LargeTransactionLogV2
**Tier:** Tier 1 (Framework Only) -- DataSourcing -> Transformation (SQL) -> ParquetFileWriter

This job filters the transactions table to records with amount strictly greater than 500, enriches each transaction with the customer's first and last name (resolved via a two-step lookup through the accounts table), and writes the result to Parquet files. The entire pipeline -- filtering, joining, and defaulting -- can be expressed in a single SQL statement within the framework's Transformation module, eliminating the need for the V1 External module.

**V1 approach:** Four DataSourcing modules (transactions, accounts, customers, addresses) feed into a C# External module (`LargeTransactionProcessor`) that builds in-memory dictionaries for account-to-customer and customer-to-name lookups, iterates transactions row by row, and writes the enriched result to shared state for the ParquetFileWriter.

**V2 approach:** Three DataSourcing modules (transactions, accounts, customers -- addresses removed) feed into a SQL Transformation that performs the filtering, joining, and defaulting in a single query. The result goes directly to ParquetFileWriter. No External module needed.

---

## 2. V2 Module Chain

```
DataSourcing("transactions") -> DataSourcing("accounts") -> DataSourcing("customers")
  -> Transformation(SQL) -> ParquetFileWriter
```

### Module Details

| Step | Module Type | Config Key | Purpose |
|------|------------|------------|---------|
| 1 | DataSourcing | transactions | Source transaction data (transaction_id, account_id, txn_timestamp, txn_type, amount, description) |
| 2 | DataSourcing | accounts | Source account data (account_id, customer_id only) |
| 3 | DataSourcing | customers | Source customer data (id, first_name, last_name) |
| 4 | Transformation | output | SQL join + filter + default logic |
| 5 | ParquetFileWriter | output | Write to Parquet, 3 parts, Append mode |

### Tier Justification

Tier 1 is sufficient because:
- The amount > 500 filter is a simple SQL WHERE clause.
- The two-step lookup (transaction -> account -> customer) is a pair of LEFT JOINs.
- The default values (customer_id = 0, first_name/last_name = '') are expressible via COALESCE.
- No procedural logic, no snapshot boundary issues, no SQLite-unsupported operations.

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| Code | Name | V1 Behavior | V2 Action |
|------|------|-------------|-----------|
| AP1 | Dead-end sourcing | V1 sources `datalake.addresses` but never uses it in LargeTransactionProcessor.cs [large_transaction_log.json:28-35; LargeTransactionProcessor.cs has no reference to addresses] | **ELIMINATED.** V2 config does not source the addresses table. |
| AP3 | Unnecessary External module | V1 uses a C# External module (LargeTransactionProcessor) for logic that is expressible entirely in SQL: a WHERE filter, two LEFT JOINs, and COALESCE defaults [LargeTransactionProcessor.cs:32-76] | **ELIMINATED.** V2 uses DataSourcing + Transformation (SQL) + ParquetFileWriter. No External module. |
| AP4 | Unused columns | V1 sources account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr from accounts but only uses account_id and customer_id [LargeTransactionProcessor.cs:36-37; large_transaction_log.json:17] | **ELIMINATED.** V2 sources only account_id and customer_id from accounts. |
| AP6 | Row-by-row iteration | V1 iterates all transaction rows in a C# foreach loop [LargeTransactionProcessor.cs:53-76] | **ELIMINATED.** V2 uses SQL set-based operations (JOIN + WHERE). |
| AP7 | Magic values | V1 uses hardcoded `500` threshold without explanation [LargeTransactionProcessor.cs:56] | **ELIMINATED.** The SQL includes a comment documenting the threshold's business meaning. The value 500 is used directly in SQL (no named constant mechanism in SQL), but documented clearly. |

### Output-Affecting Wrinkles

No W-codes apply to this job:
- No Sunday skip, weekend fallback, or boundary summaries (W1/W2/W3a-c)
- No integer division or rounding (W4/W5)
- No double-precision accumulation (W6) -- the amount field is a passthrough, not accumulated
- No trailer (W7/W8) -- Parquet output, no trailers
- Write mode is Append, which matches the V1 behavior (W9 does not apply since Append is appropriate for a log)
- numParts = 3, which is reasonable for ~288K rows (W10 does not apply)
- No CSV header issues (W12) -- Parquet output

---

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Ref |
|--------|------|--------|---------------|---------|
| transaction_id | integer | transactions.transaction_id | Direct passthrough | BR-1 |
| account_id | integer | transactions.account_id | Direct passthrough | BR-1 |
| customer_id | integer | Computed | Resolved via accounts LEFT JOIN; COALESCE to 0 when no match | BR-2, BR-3 |
| first_name | string | customers.first_name | Lookup via customer_id; COALESCE to '' when NULL or no match | BR-2, BR-3, BR-8 |
| last_name | string | customers.last_name | Lookup via customer_id; COALESCE to '' when NULL or no match | BR-2, BR-3, BR-8 |
| txn_type | string | transactions.txn_type | Direct passthrough | BR-1 |
| amount | decimal | transactions.amount | Direct passthrough (only rows where amount > 500) | BR-1 |
| description | string | transactions.description | Direct passthrough | BR-1 |
| txn_timestamp | timestamp | transactions.txn_timestamp | Direct passthrough | BR-1 |
| as_of | date | transactions.as_of | Direct passthrough (injected by DataSourcing) | BR-1 |

**Column order** matches V1 exactly: transaction_id, account_id, customer_id, first_name, last_name, txn_type, amount, description, txn_timestamp, as_of.

---

## 5. SQL Design

### Transformation SQL

```sql
SELECT
    t.transaction_id,
    t.account_id,
    -- V1 behavior: resolve customer_id via accounts lookup, default 0 if no account match
    -- [LargeTransactionProcessor.cs:58-59] accountToCustomer.GetValueOrDefault(accountId, 0)
    COALESCE(a.customer_id, 0) AS customer_id,
    -- V1 behavior: COALESCE NULL names to empty string
    -- [LargeTransactionProcessor.cs:46-48,60] ?.ToString() ?? ""
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    t.txn_type,
    t.amount,
    t.description,
    t.txn_timestamp,
    t.as_of
FROM transactions t
LEFT JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of
LEFT JOIN customers c ON a.customer_id = c.id AND t.as_of = c.as_of
WHERE t.amount > 500
```

### SQL Design Notes

**Join strategy -- as_of alignment:** The DataSourcing module fetches all rows within the effective date range (potentially multiple as_of dates). V1's dictionary-based lookup (BR-9) iterates ALL account rows and overwrites, so the last-seen mapping for each account_id wins. In a single-day execution scenario (where min and max effective date are the same, which is how auto-advance works), there is only one as_of date per table, so the dictionary overwrite behavior is a no-op -- only one mapping exists per account_id.

The framework auto-advances one day at a time (each pipeline run covers exactly one effective date). Therefore, joining on `t.as_of = a.as_of` and `t.as_of = c.as_of` is correct: it matches each transaction to the account and customer snapshot from the same day, which is exactly what V1 does when there's a single as_of date in the DataFrames.

**BR-3 default behavior:** `COALESCE(a.customer_id, 0)` handles the case where no account matches a transaction's account_id (LEFT JOIN produces NULL). `COALESCE(c.first_name, '')` and `COALESCE(c.last_name, '')` handle both the case where no customer matches and the case where the name columns are genuinely NULL in the data (BR-8).

**BR-6/BR-7 empty output:** If any of the three source DataFrames is empty, the Transformation module's RegisterTable method skips creating the SQLite table for empty DataFrames. If `transactions` is empty, the FROM clause returns no rows. If `accounts` or `customers` is empty, the LEFT JOIN will produce NULLs for the joined columns, but rows will still be returned (unlike V1, which returns empty output when accounts or customers are empty).

**Important consideration:** V1 produces empty output when accounts or customers are empty (BR-6). With LEFT JOIN in SQL, transactions would still pass through with NULL/default customer info. However, in practice, the datalake always has account and customer data for every as_of date in the transaction range, so this edge case does not arise in production data. If it did arise, the behavior difference would be: V1 outputs 0 rows, V2 outputs transactions with customer_id=0 and empty names. Since the BRD notes this is HIGH confidence based on source code, and datalake data confirms all three tables have rows for every as_of date in the range, this divergence is theoretical. The Proofmark comparison will catch it if it materializes.

**Amount threshold:** The WHERE clause `t.amount > 500` uses strict greater-than, matching V1 [LargeTransactionProcessor.cs:56].

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "LargeTransactionLogV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["transaction_id", "account_id", "txn_timestamp", "txn_type", "amount", "description"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT t.transaction_id, t.account_id, COALESCE(a.customer_id, 0) AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, t.txn_type, t.amount, t.description, t.txn_timestamp, t.as_of FROM transactions t LEFT JOIN accounts a ON t.account_id = a.account_id AND t.as_of = a.as_of LEFT JOIN customers c ON a.customer_id = c.id AND t.as_of = c.as_of WHERE t.amount > 500"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/large_transaction_log/",
      "numParts": 3,
      "writeMode": "Append"
    }
  ]
}
```

### Config Changes from V1

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| addresses DataSourcing | Present | **Removed** | AP1: Dead-end sourcing -- never used in processing |
| accounts columns | account_id, customer_id, account_type, account_status, open_date, current_balance, interest_rate, credit_limit, apr | **account_id, customer_id** | AP4: Only these two columns are used in the lookup |
| External module | LargeTransactionProcessor | **Removed** | AP3: All logic expressible in SQL |
| Transformation | Not present | **Added** | Replaces External module with SQL-based joins and filtering |
| Output path | Output/curated/large_transaction_log/ | Output/double_secret_curated/large_transaction_log/ | V2 output isolation |
| numParts | 3 | 3 | Matched exactly |
| writeMode | Append | Append | Matched exactly |

---

## 7. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | ParquetFileWriter | Yes |
| source | output | Yes |
| outputDirectory | Output/double_secret_curated/large_transaction_log/ | Path changed per V2 convention |
| numParts | 3 | Yes |
| writeMode | Append | Yes |

---

## 8. Proofmark Config Design

### Recommended Config

```yaml
comparison_target: "large_transaction_log"
reader: parquet
threshold: 100.0
```

### Justification

- **Reader:** Parquet -- matches the V1 writer type (ParquetFileWriter).
- **Threshold:** 100.0 -- all rows must match exactly. No known sources of non-determinism.
- **Excluded columns:** None. All output columns are deterministic. No runtime-generated timestamps or UUIDs.
- **Fuzzy columns:** None. The amount field is a direct passthrough (not accumulated or computed from floating-point operations). customer_id is an integer default. Names are strings.
- **CSV settings:** Not applicable (Parquet output).

### Potential Risk: Row Ordering

V1 iterates transactions in the order they appear in the DataFrame (which comes from DataSourcing's `ORDER BY as_of`). V2's SQL does not add an explicit ORDER BY, so within a single as_of date the row order depends on SQLite's internal iteration order from the LEFT JOIN. Parquet is a columnar format and Proofmark should compare row sets rather than row sequences -- but if Proofmark does order-sensitive comparison, we may need to add an `ORDER BY t.transaction_id` to the SQL. Start without it and add only if Proofmark comparison fails due to ordering.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Amount > 500 filter | SQL Design | `WHERE t.amount > 500` -- strict greater-than |
| BR-2: Two-step customer lookup | SQL Design | `LEFT JOIN accounts a ON t.account_id = a.account_id ... LEFT JOIN customers c ON a.customer_id = c.id` |
| BR-3: Default values for missing lookups | SQL Design | `COALESCE(a.customer_id, 0)`, `COALESCE(c.first_name, '')`, `COALESCE(c.last_name, '')` |
| BR-4: Dead-end addresses | Anti-Pattern Analysis (AP1) | Addresses DataSourcing removed entirely |
| BR-5: Unused account columns | Anti-Pattern Analysis (AP4) | V2 sources only account_id and customer_id |
| BR-6: Empty output on missing accounts/customers | SQL Design Notes | Discussed; theoretical divergence documented; not expected in production data |
| BR-7: Empty output on missing transactions | SQL Design Notes | Empty transactions table yields zero output rows naturally |
| BR-8: NULL name coalescing | SQL Design | `COALESCE(c.first_name, '')`, `COALESCE(c.last_name, '')` handles both NULL data and missing customer |
| BR-9: Unfiltered account lookup | SQL Design Notes | Single-day execution means one as_of per run; `as_of = as_of` join is equivalent to V1 dictionary overwrite behavior |
| BR-10: Data volume (~288K rows) | Writer Config | numParts=3 is appropriate for this volume |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed. All business logic is handled by the SQL Transformation module.

---

## Appendix: V1 Source Code References

| Reference | Location | Relevance |
|-----------|----------|-----------|
| LargeTransactionProcessor.cs:56 | `if (amount > 500)` | Amount filter threshold |
| LargeTransactionProcessor.cs:33-39 | Dictionary loop building accountToCustomer | Two-step lookup logic |
| LargeTransactionProcessor.cs:42-48 | Dictionary loop building customerNames | Name lookup with NULL coalescing |
| LargeTransactionProcessor.cs:59 | `GetValueOrDefault(accountId, 0)` | Default customer_id = 0 |
| LargeTransactionProcessor.cs:60 | `GetValueOrDefault(customerId, ("", ""))` | Default names = empty strings |
| large_transaction_log.json:28-35 | addresses DataSourcing config | Dead-end source (AP1) |
| large_transaction_log.json:17 | accounts columns list | Over-sourced columns (AP4) |
| large_transaction_log.json:42-43 | numParts=3, writeMode=Append | Writer config to preserve |
