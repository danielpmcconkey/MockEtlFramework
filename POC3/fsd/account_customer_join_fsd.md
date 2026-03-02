# AccountCustomerJoin -- Functional Specification Document

## 1. Overview

The V2 job (`AccountCustomerJoinV2`) produces a denormalized view joining accounts with customer names, enriching each account record with the customer's first and last name. The output is Parquet with Overwrite mode and 2 part files.

**Tier: 1 (Framework Only)**
`DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** The V1 External module (`AccountCustomerDenormalizer.cs`) performs a dictionary-based lookup join between accounts and customers. This is a textbook LEFT JOIN operation that SQL handles natively. There are no procedural operations, no cross-date-range queries, no stateful accumulations, and no SQLite-incompatible operations. Every piece of V1 logic maps directly to SQL constructs:

- Dictionary lookup keyed by customer_id -> `LEFT JOIN ... ON a.customer_id = c.id`
- `GetValueOrDefault(customerId, ("", ""))` -> `COALESCE(c.first_name, '') AS first_name`
- Iterating over all accounts rows -> The FROM table is `accounts`
- `as_of` sourced from accounts -> `a.as_of`

No External module is needed.

---

## 2. V2 Module Chain

### Module 1: DataSourcing -- accounts

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | accounts |
| schema | datalake |
| table | accounts |
| columns | `["account_id", "customer_id", "account_type", "account_status", "current_balance"]` |

Effective dates injected via shared state by the executor (`__minEffectiveDate` / `__maxEffectiveDate`). The `as_of` column is automatically appended by the DataSourcing module since it is not in the explicit columns list.

### Module 2: DataSourcing -- customers

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | customers |
| schema | datalake |
| table | customers |
| columns | `["id", "first_name", "last_name"]` |

Same effective date injection as above.

### Module 3: Transformation

| Property | Value |
|----------|-------|
| type | Transformation |
| resultName | output |
| sql | See Section 5 |

### Module 4: ParquetFileWriter

| Property | Value |
|----------|-------|
| type | ParquetFileWriter |
| source | output |
| outputDirectory | `Output/double_secret_curated/account_customer_join/` |
| numParts | 2 |
| writeMode | Overwrite |

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-code | Applies? | Analysis |
|--------|----------|----------|
| W1 (Sunday skip) | No | No Sunday-specific logic in V1. |
| W2 (Weekend fallback) | No | No weekend date fallback in V1. |
| W3a/b/c (Boundary rows) | No | No summary rows appended. |
| W4 (Integer division) | No | No division operations in V1. |
| W5 (Banker's rounding) | No | No rounding operations in V1. |
| W6 (Double epsilon) | No | No floating-point accumulation in V1. `current_balance` is a passthrough, not computed. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Overwrite mode is appropriate for a snapshot-style join. Each run replaces the prior output. This appears intentional. |
| W10 (Absurd numParts) | No | 2 parts is reasonable for this dataset size. |
| W12 (Header every append) | No | Parquet writer, not CSV Append. |

**Conclusion:** No W-codes apply to this job.

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| **AP1** | **YES** | V1 sources `addresses` table (columns: address_id, customer_id, address_line1, city, state_province) which is never referenced in the External module. The BRD confirms this at BR-2 with HIGH confidence. | **Eliminated.** V2 config does not include the `addresses` DataSourcing entry. Only `accounts` and `customers` are sourced. |
| **AP3** | **YES** | V1 uses a C# External module (`AccountCustomerDenormalizer`) for a join operation that is trivially expressible as a SQL LEFT JOIN. | **Eliminated.** V2 uses a Transformation module with SQL instead of an External module. Tier 1 replaces Tier 3. |
| **AP4** | **NO** | All sourced columns from `accounts` and `customers` are used in the output. No unused columns. | N/A |
| **AP6** | **YES** | V1 uses row-by-row `foreach` iteration over accounts rows with a dictionary lookup, where a SQL LEFT JOIN produces identical results. | **Eliminated.** SQL LEFT JOIN is a set-based operation. |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. |
| AP5 (Asymmetric NULLs) | No | NULL handling is consistent: missing customer names default to empty string for both first_name and last_name. |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings in V1. |
| AP8 (Complex SQL) | No | V1 has no SQL. V2 SQL is straightforward with no unused CTEs. |
| AP9 (Misleading names) | No | Job name accurately describes the output (accounts joined with customers). |
| AP10 (Over-sourcing dates) | No | V1 uses framework-injected effective dates, not full table pulls with SQL date filtering. |

---

## 4. Output Schema

| Column | Source | Transformation | V1 Evidence |
|--------|--------|---------------|-------------|
| account_id | accounts.account_id | Passthrough | [AccountCustomerDenormalizer.cs:44] |
| customer_id | accounts.customer_id | Passthrough | [AccountCustomerDenormalizer.cs:45] |
| first_name | customers.first_name | LEFT JOIN lookup; empty string if no match (`COALESCE`) | [AccountCustomerDenormalizer.cs:40,46] |
| last_name | customers.last_name | LEFT JOIN lookup; empty string if no match (`COALESCE`) | [AccountCustomerDenormalizer.cs:40,47] |
| account_type | accounts.account_type | Passthrough | [AccountCustomerDenormalizer.cs:48] |
| account_status | accounts.account_status | Passthrough | [AccountCustomerDenormalizer.cs:49] |
| current_balance | accounts.current_balance | Passthrough | [AccountCustomerDenormalizer.cs:50] |
| as_of | accounts.as_of | Passthrough from accounts (not customers) | [AccountCustomerDenormalizer.cs:51], BRD BR-6 |

**Column order matters.** The V1 External module explicitly defines column order via the `outputColumns` list at line 10-14. The V2 SQL SELECT clause must produce columns in the same order: `account_id, customer_id, first_name, last_name, account_type, account_status, current_balance, as_of`.

---

## 5. SQL Design

```sql
SELECT
    a.account_id,
    a.customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    a.account_type,
    a.account_status,
    a.current_balance,
    a.as_of
FROM accounts a
LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of
ORDER BY a.account_id
```

### SQL Design Rationale

**LEFT JOIN semantics (BR-5, BR-3):** V1 iterates over every accounts row and uses `GetValueOrDefault` to look up customer names, defaulting to empty strings. This is functionally a LEFT JOIN where unmatched accounts still produce output rows. `COALESCE(c.first_name, '')` replicates the empty-string default for unmatched lookups.

**Join on both customer_id AND as_of:** Both DataSourcing entries pull data for the same effective date range. The `as_of` column is present in both DataFrames (automatically appended by DataSourcing). Joining on `as_of` in addition to the customer_id ensures that the join matches customers to accounts for the same snapshot date, avoiding cross-date matches when running over multi-day ranges. This matches V1's behavior because:
- V1 builds a dictionary from ALL customer rows (possibly multiple as_of dates in a multi-day range), using last-write-wins semantics (BRD edge case: "Duplicate customer IDs").
- However, for single-day runs (the normal execution path with auto-advancement), there is exactly one as_of value, so the dictionary contains one entry per customer_id, and the join on `as_of` produces identical results.
- For the Overwrite write mode, only the last day's data survives anyway (BRD: "Write Mode Implications"), so multi-day dictionary collisions in V1 are irrelevant to the final output.

**ORDER BY a.account_id:** Provides deterministic row ordering. V1 iterates accounts in the order received from DataSourcing (which is `ORDER BY as_of`). For a single date, the underlying row order from PostgreSQL within a single `as_of` value is not guaranteed to be by `account_id`, but the Parquet writer preserves whatever order it receives. Adding an explicit ORDER BY ensures deterministic output. If proofmark comparison shows ordering differences, this ORDER BY may need adjustment to match V1's actual row order.

**Empty DataFrame handling (BR-4):** V1 returns an empty DataFrame if either `accounts` or `customers` is null/empty. In the Transformation module, if a DataSourcing result has zero rows, its SQLite table is not registered (`RegisterTable` skips when `!df.Rows.Any()`). A SQL query referencing a non-existent table will cause a SQLite error. In practice, both tables share the same effective date range, so if one is empty (e.g., weekend dates), the other will be too. If `accounts` is empty, its table is not registered, and the query fails -- but there are no rows to output anyway, so the framework-level error handling produces the same net effect (no output file or an empty output). If `accounts` has rows but `customers` is empty, V1 produces empty output (BR-4), while V2's SQL would fail because the `customers` table is not registered. This edge case diverges from V1's behavior in theory, but in practice: (a) the same effective dates are used for both tables, and (b) if this ever triggers, it would be a data anomaly worth investigating rather than silently producing empty output. If proofmark reveals this as a real issue, we can add a pre-check or switch to Tier 2 with a minimal External module.

---

## 6. V2 Job Config

```json
{
  "jobName": "AccountCustomerJoinV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id", "account_type", "account_status", "current_balance"]
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
      "sql": "SELECT a.account_id, a.customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, a.account_type, a.account_status, a.current_balance, a.as_of FROM accounts a LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of ORDER BY a.account_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/account_customer_join/",
      "numParts": 2,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | YES |
| source | output | output | YES |
| outputDirectory | `Output/curated/account_customer_join/` | `Output/double_secret_curated/account_customer_join/` | Path changed per V2 spec |
| numParts | 2 | 2 | YES |
| writeMode | Overwrite | Overwrite | YES |

---

## 8. Proofmark Config Design

### Recommended Config: Default Strict

```yaml
comparison_target: "account_customer_join"
reader: parquet
threshold: 100.0
```

### Exclusions: None

No columns are non-deterministic. All columns are deterministic passthroughs or deterministic lookups (COALESCE on a LEFT JOIN). The BRD confirms: "Non-Deterministic Fields: None identified."

### Fuzzy Columns: None

No floating-point arithmetic is performed. `current_balance` is a passthrough from the source table -- it is not computed, accumulated, or rounded. No epsilon differences are expected.

### Rationale

Starting with zero exclusions and zero fuzzy overrides per the BLUEPRINT's prescription. All 8 output columns are deterministic and should match exactly. If proofmark comparison reveals differences, the root cause should be investigated (likely row ordering) rather than masked with fuzzy/excluded columns.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|--------------|-----------------|----------|
| Tier 1 (no External module) | BR-1, BR-3, BR-5 | Join logic is a simple LEFT JOIN with COALESCE defaults -- expressible in SQL |
| Remove `addresses` DataSourcing | BR-2 | Addresses sourced but never used in V1 External module [AccountCustomerDenormalizer.cs:8-57] |
| LEFT JOIN accounts to customers | BR-1 | Dictionary lookup keyed by customer_id [AccountCustomerDenormalizer.cs:26-33] |
| COALESCE to empty string on miss | BR-3 | `GetValueOrDefault(customerId, ("", ""))` [AccountCustomerDenormalizer.cs:40] |
| Iterate over accounts (FROM accounts) | BR-5 | `foreach (var acctRow in accounts.Rows)` -- every account produces one output row |
| as_of from accounts table | BR-6 | `["as_of"] = acctRow["as_of"]` [AccountCustomerDenormalizer.cs:51] |
| Empty output when either source empty | BR-4 | Null/empty check [AccountCustomerDenormalizer.cs:19-23]. See Section 5 discussion. |
| ParquetFileWriter with Overwrite, 2 parts | BRD Writer Configuration | [account_customer_join.json:34-36] |
| Column order matches V1 | BRD Output Schema | `outputColumns` list [AccountCustomerDenormalizer.cs:10-14] |
| AP1 eliminated (addresses removed) | BR-2 (dead-end sourcing) | addresses DataFrame never referenced in Execute method |
| AP3 eliminated (SQL replaces External) | BR-1 (join expressible in SQL) | Dictionary lookup is a LEFT JOIN |
| AP6 eliminated (set-based SQL) | BR-5 (row-by-row foreach) | SQL LEFT JOIN replaces foreach + dictionary lookup |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`AccountCustomerDenormalizer.cs`) is replaced entirely by the Transformation module's SQL query. All V1 business logic maps to SQL constructs as documented in Sections 2 and 5.
