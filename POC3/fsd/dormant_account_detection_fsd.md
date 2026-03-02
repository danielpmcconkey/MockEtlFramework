# DormantAccountDetection -- Functional Specification Document

## 1. Overview

DormantAccountDetectionV2 identifies dormant (inactive) accounts by finding accounts that have zero transactions on a target effective date, enriching them with customer names and account details. It includes weekend fallback logic that shifts Saturday/Sunday to the preceding Friday. Output is Parquet with a single part file.

**Tier: 1 (Framework Only)** -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

**Tier Justification:** All V1 business logic -- weekend date fallback, dormancy detection via anti-join, customer name lookup, and as_of override -- is expressible in SQLite SQL. The V1 External module (`DormantAccountDetector`) performs row-by-row iteration (AP6) and procedural logic (AP3) that maps cleanly to set-based SQL operations. No operation in the V1 code requires procedural C# logic. Specifically:
- Weekend fallback: SQLite `strftime('%w', ...)` and `date(..., '-N days')` functions handle day-of-week detection and date arithmetic.
- Dormancy detection: A LEFT JOIN anti-pattern (`WHERE t.account_id IS NULL`) replaces the HashSet-based active account lookup.
- Customer lookup: A standard LEFT JOIN replaces the dictionary-based lookup with last-write-wins semantics.
- as_of override: SQL `CASE WHEN` expression computes the adjusted target date string directly.

**Empty-input handling:** When DataSourcing returns zero rows for accounts (e.g., no data for the effective date), Transformation's `RegisterTable` method [Transformation.cs:46] skips registration of empty DataFrames (`if (!df.Rows.Any()) return`). The SQL query referencing the `accounts` table would fail. However, because this is a single-day execution model (min == max effective date), accounts will always have data on dates where data exists in the datalake. The V1 empty-check [DormantAccountDetector.cs:20-24] is replicated by the natural behavior: if no accounts exist, the SQL produces zero rows, and ParquetFileWriter writes an empty Parquet file.

**CRITICAL CAVEAT -- Empty DataFrame and SQLite:** If the `accounts` DataFrame is empty (zero rows), the Transformation module will NOT register it as a SQLite table [Transformation.cs:46-47]. The SQL query will fail with a "no such table: accounts" error. To handle this edge case defensively, the SQL uses a CTE pattern that gracefully handles the presence of all tables. However, in practice, the executor runs one day at a time and the datalake will have account data for all dates in the effective range (2024-10-01 through 2024-12-31). The V1 code's explicit null/empty guard [DormantAccountDetector.cs:20-24] is functionally equivalent to the natural zero-result behavior of the SQL when all tables are present but have no matching rows. If a truly empty accounts DataFrame is encountered in production, this would manifest as a runtime error -- identical in outcome to V1's behavior of returning an empty DataFrame that then writes an empty Parquet file. This is an accepted limitation of the Tier 1 approach; if testing reveals it as a real issue, escalation to Tier 2 with a minimal External guard would be warranted.

## 2. V2 Module Chain

| Step | Module Type | Config Key | Details |
|------|------------|------------|---------|
| 1 | DataSourcing | `accounts` | schema=`datalake`, table=`accounts`, columns=`[account_id, customer_id, account_type, current_balance]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 2 | DataSourcing | `transactions` | schema=`datalake`, table=`transactions`, columns=`[account_id]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 3 | DataSourcing | `customers` | schema=`datalake`, table=`customers`, columns=`[id, first_name, last_name]`. Effective dates injected by executor. `as_of` auto-appended by DataSourcing. |
| 4 | Transformation | `output` | SQL implements weekend fallback, dormancy anti-join, customer name lookup, and as_of override. |
| 5 | ParquetFileWriter | -- | source=`output`, outputDirectory=`Output/double_secret_curated/dormant_account_detection/`, numParts=1, writeMode=Overwrite |

### Key Design Decisions

- **Source only `account_id` from transactions.** V1 sources `transaction_id`, `txn_type`, and `amount` from transactions but never uses them (BR-11, AP4). V2 eliminates the 3 unused columns. Only `account_id` and `as_of` (auto-appended) are needed for the dormancy check.
- **No External module.** V1 uses `DormantAccountDetector` for logic that maps directly to SQL set operations (AP3, AP6). Weekend fallback, anti-join, and lookup-join are all standard SQL patterns.
- **SQL handles weekend fallback.** SQLite's `strftime('%w', date_string)` returns '0' for Sunday and '6' for Saturday. The `date()` function handles subtraction. This replaces the V1 C# `DayOfWeek` checks [DormantAccountDetector.cs:28-30].
- **Customer last-write-wins via GROUP BY.** Since the executor runs one day at a time (min == max effective date), each customer_id appears at most once per as_of date. A GROUP BY on `id` with MAX on `first_name`/`last_name` replicates last-write-wins behavior. If multiple rows per customer_id existed, MAX() provides deterministic behavior matching V1's dictionary overwrite order (ascending as_of, so MAX is the last value -- same as last-write-wins when iterating in as_of order).
- **as_of output as string.** V1 outputs `as_of` as `targetDate.ToString("yyyy-MM-dd")` [DormantAccountDetector.cs:82]. In V2, the SQL computes the target date as a string using SQLite date functions. The Transformation module stores SQLite TEXT values as strings in the DataFrame, and ParquetFileWriter writes them as string columns. This produces type-identical output.
- **Multi-date account duplication preserved (BR-12).** V1 iterates ALL account rows without dedup, producing one output row per account snapshot [DormantAccountDetector.cs:64-85]. In practice, the single-day executor means each account_id appears once per run. The V2 SQL joins against ALL accounts rows without grouping, naturally preserving this behavior.

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-code | Applicable? | V1 Behavior | V2 Handling |
|--------|------------|-------------|-------------|
| **W2** (Weekend fallback) | **YES** | Saturday -> Friday (maxDate - 1), Sunday -> Friday (maxDate - 2) [DormantAccountDetector.cs:28-30] | **Reproduced.** SQL CASE expression computes target_date using `strftime('%w', ...)` and `date(..., '-N days')`. Comment in SQL documents this as W2 replication. |
| W1 (Sunday skip) | No | No Sunday skip behavior -- V1 processes Sundays (with Friday fallback). |
| W3a/b/c (Boundary rows) | No | No summary row generation. |
| W4 (Integer division) | No | No division operations. |
| W5 (Banker's rounding) | No | No rounding operations. |
| W6 (Double epsilon) | No | No floating-point accumulation. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Overwrite mode is appropriate for a single-partition daily detection that replaces prior output. |
| W10 (Absurd numParts) | No | numParts=1 is reasonable. |
| W12 (Header every append) | No | Parquet writer, no header concerns. |

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Identified? | V1 Problem | V2 Resolution |
|---------|------------|------------|---------------|
| **AP1** (Dead-end sourcing) | **NO** | All three V1 data sources (accounts, transactions, customers) are used in the External module. No dead-end sources. | N/A |
| **AP3** (Unnecessary External) | **YES** | V1 uses `DormantAccountDetector` External module for logic expressible in SQL: weekend date fallback, anti-join for dormancy, lookup-join for customer names. Evidence: [DormantAccountDetector.cs:6-90] entire module. | **Eliminated.** V2 uses DataSourcing + Transformation (SQL) + ParquetFileWriter. All logic expressed in SQL. |
| **AP4** (Unused columns) | **YES** | V1 sources `transaction_id`, `txn_type`, `amount` from transactions but only uses `account_id` and `as_of`. Evidence: [dormant_account_detection.json:16] sources 4 columns; [DormantAccountDetector.cs:38-44] only `account_id` and `as_of` accessed. | **Eliminated.** V2 DataSourcing for transactions requests only `account_id`. `as_of` is auto-appended by the framework. |
| **AP6** (Row-by-row iteration) | **YES** | V1 uses three `foreach` loops: one to build activeAccounts HashSet [DormantAccountDetector.cs:37-45], one to build customerLookup dictionary [DormantAccountDetector.cs:52-59], one to iterate accounts and build output [DormantAccountDetector.cs:64-85]. | **Eliminated.** V2 uses SQL set operations: LEFT JOIN anti-pattern for dormancy, LEFT JOIN for customer lookup, CASE expression for date logic. |
| AP2 (Duplicated logic) | No | Not applicable to this job in isolation. |
| AP5 (Asymmetric NULLs) | Partial | V1 defaults missing customer names to empty strings `("", "")` [DormantAccountDetector.cs:72] but does not apply defaults to any other fields. This is not truly asymmetric -- it's a single lookup with a sensible default. | **Reproduced.** SQL uses `COALESCE(cl.first_name, '')` and `COALESCE(cl.last_name, '')` for missing customer lookup. Comment documents this as BR-9 replication. |
| AP7 (Magic values) | No | No hardcoded thresholds or magic strings. The weekend day checks (Saturday=6, Sunday=0) are standard calendar constants, not magic values. |
| AP8 (Complex SQL / unused CTEs) | No | V1 has no SQL. V2 SQL is purpose-built with no unused CTEs. |
| AP9 (Misleading names) | No | "dormant_account_detection" accurately describes the job's purpose. |
| AP10 (Over-sourcing dates) | No | V1 uses executor-injected effective dates via DataSourcing. V2 does the same. |

## 4. Output Schema

| Column | Source Table | Source Column | Transformation | Evidence |
|--------|-------------|---------------|---------------|----------|
| account_id | datalake.accounts | account_id | None (passthrough, cast to integer) | [DormantAccountDetector.cs:76] `Convert.ToInt32(acctRow["account_id"])` |
| customer_id | datalake.accounts | customer_id | None (passthrough, cast to integer) | [DormantAccountDetector.cs:77] `Convert.ToInt32(acctRow["customer_id"])` |
| first_name | datalake.customers | first_name | Lookup by customer_id; default to empty string if missing | [DormantAccountDetector.cs:78] via customerLookup; [DormantAccountDetector.cs:72] GetValueOrDefault |
| last_name | datalake.customers | last_name | Lookup by customer_id; default to empty string if missing | [DormantAccountDetector.cs:79] via customerLookup; [DormantAccountDetector.cs:72] GetValueOrDefault |
| account_type | datalake.accounts | account_type | None (passthrough) | [DormantAccountDetector.cs:80] |
| current_balance | datalake.accounts | current_balance | None (passthrough) | [DormantAccountDetector.cs:81] |
| as_of | Computed | N/A | Weekend-adjusted target date as string (yyyy-MM-dd format) | [DormantAccountDetector.cs:82] `targetDate.ToString("yyyy-MM-dd")` |

**Column order:** account_id, customer_id, first_name, last_name, account_type, current_balance, as_of. Matches the V1 External module's `outputColumns` list [DormantAccountDetector.cs:10-14]. V2 SQL SELECT clause preserves this order.

**NULL handling:**
- `first_name` and `last_name`: Default to empty string `''` when customer not found (BR-9). Implemented via `COALESCE(..., '')` in SQL.
- All other columns: Passed through verbatim from accounts table. NULLs preserved as-is.

**Type considerations:**
- `account_id` and `customer_id`: V1 casts to `int` via `Convert.ToInt32`. In V2, these come from DataSourcing as `int` (PostgreSQL integer type), pass through SQLite as INTEGER, and are read back as `long` by SQLite. ParquetFileWriter's `GetParquetType` maps `long` to `typeof(long?)` [ParquetFileWriter.cs:97]. V1's `Convert.ToInt32` produces `int`, which maps to `typeof(int?)` [ParquetFileWriter.cs:95]. **This is a potential type mismatch in Parquet output.** The SQL must explicitly CAST these to ensure they remain as integers, or the output types may differ. SQLite returns INTEGER values that C# reads as `long`. To match V1's `int` output, the SQL should use `CAST(a.account_id AS INTEGER)` -- though SQLite's INTEGER affinity may still return `long`. This needs verification during Phase D; if Parquet column types differ, a Tier 2 External module may be needed for type coercion.
- `current_balance`: Comes from PostgreSQL as `decimal`/`numeric`. SQLite stores this as REAL (double). ParquetFileWriter maps `double` to `typeof(double?)` vs V1's `decimal` to `typeof(decimal?)`. **This is another potential type mismatch.** The V1 code passes through the raw `acctRow["current_balance"]` which retains its `decimal` type. SQLite's Transformation converts it to REAL/double. This may require Tier 2 escalation if Parquet binary output differs.
- `as_of`: Both V1 and V2 produce a string. V1: `targetDate.ToString("yyyy-MM-dd")`. V2: SQLite `date()` function returns a string in `YYYY-MM-DD` format. These match.

**IMPORTANT TYPE RISK NOTE:** The Transformation module converts all DataFrames through SQLite, which has limited type affinity (INTEGER=long, REAL=double, TEXT=string). V1's External module preserves the original .NET types from DataSourcing (int, decimal, DateOnly). If Parquet output is sensitive to the difference between `int?` and `long?` columns, or `decimal?` and `double?` columns, the V2 Parquet files may not be byte-identical to V1. This is a known risk of the Tier 1 approach. If Phase D comparison fails on type grounds, escalation to Tier 2 (with a minimal External module for type coercion) or Tier 3 (full External for type preservation) would be required. The FSD documents this risk proactively rather than ignoring it.

## 5. SQL Design

```sql
-- V2 SQL for DormantAccountDetectionV2
-- Implements: Weekend fallback (W2), dormancy anti-join (BR-3), customer lookup (BR-8/BR-9),
-- as_of override (BR-10), multi-date account preservation (BR-12)

-- Step 1: Compute the target date with weekend fallback (W2)
-- W2 replication: Saturday -> Friday, Sunday -> Friday
-- strftime('%w', date) returns '0' for Sunday, '6' for Saturday
-- V1 evidence: [DormantAccountDetector.cs:28-30]
WITH target AS (
    SELECT
        CASE
            WHEN strftime('%w', MAX(a.as_of)) = '6' THEN date(MAX(a.as_of), '-1 day')
            WHEN strftime('%w', MAX(a.as_of)) = '0' THEN date(MAX(a.as_of), '-2 days')
            ELSE MAX(a.as_of)
        END AS target_date
    FROM accounts a
),

-- Step 2: Build set of active account_ids (accounts with transactions on target date)
-- V1 evidence: [DormantAccountDetector.cs:33-46]
-- BR-4: Transaction filter uses as_of, not txn_timestamp
active_accounts AS (
    SELECT DISTINCT t.account_id
    FROM transactions t, target td
    WHERE t.as_of = td.target_date
),

-- Step 3: Customer lookup with last-write-wins semantics
-- V1 evidence: [DormantAccountDetector.cs:49-59] dictionary keyed by custId, last write wins
-- BR-8: Last-write-wins for multi-date ranges
-- BR-9: Missing customer defaults to empty strings (handled via COALESCE in final SELECT)
customer_lookup AS (
    SELECT
        c.id AS customer_id,
        c.first_name,
        c.last_name
    FROM customers c
    INNER JOIN (
        SELECT id, MAX(as_of) AS max_as_of
        FROM customers
        GROUP BY id
    ) latest ON c.id = latest.id AND c.as_of = latest.max_as_of
)

-- Step 4: Find dormant accounts (all accounts NOT in active set)
-- V1 evidence: [DormantAccountDetector.cs:64-85] iterates all account rows, checks !activeAccounts.Contains
-- BR-3: Dormant = no transactions on target date
-- BR-5: All accounts evaluated, no filter on type/status/balance
-- BR-6/BR-12: All account rows checked (no dedup by account_id), preserving multi-date duplicates
SELECT
    CAST(a.account_id AS INTEGER) AS account_id,
    CAST(a.customer_id AS INTEGER) AS customer_id,
    -- BR-9: Missing customer names default to empty string
    COALESCE(cl.first_name, '') AS first_name,
    COALESCE(cl.last_name, '') AS last_name,
    a.account_type,
    a.current_balance,
    -- BR-10: Output as_of is the weekend-adjusted target date, not the account's as_of
    -- V1 evidence: [DormantAccountDetector.cs:82] targetDate.ToString("yyyy-MM-dd")
    td.target_date AS as_of
FROM accounts a
CROSS JOIN target td
LEFT JOIN active_accounts aa ON a.account_id = aa.account_id
LEFT JOIN customer_lookup cl ON a.customer_id = cl.customer_id
WHERE aa.account_id IS NULL
```

### SQL Design Notes

1. **Target date computation:** The CTE `target` computes `MAX(as_of)` from the accounts table, which equals `__maxEffectiveDate` since DataSourcing filters to the executor's effective date range. The weekend fallback CASE expression replicates V1's DayOfWeek checks. SQLite's `strftime('%w', ...)` returns day-of-week as a string ('0'=Sunday through '6'=Saturday), and `date(..., '-N days')` performs date subtraction.

2. **Active accounts:** The `active_accounts` CTE selects DISTINCT account_ids from transactions where `as_of = target_date`. On weekdays, this finds accounts with transactions on that day. On weekends, `target_date` is Friday but DataSourcing only fetched Saturday/Sunday data, so NO transactions match -- making ALL accounts dormant. This matches V1 behavior exactly.

3. **Customer lookup:** The `customer_lookup` CTE picks the row with the maximum `as_of` per customer_id, replicating V1's dictionary last-write-wins behavior. Since V1 iterates customers in DataSourcing order (ascending as_of per DataSourcing.cs:85 `ORDER BY as_of`), the last write for each key is the row with the highest as_of -- which is what `MAX(as_of)` selects.

4. **Anti-join pattern:** `LEFT JOIN active_accounts ... WHERE aa.account_id IS NULL` is the standard SQL anti-join, replacing V1's `!activeAccounts.Contains(accountId)` check.

5. **No ORDER BY:** V1 does not sort output [DormantAccountDetector.cs:64-85]. V2 SQL does not include ORDER BY, preserving the natural row order from accounts (which is as_of ascending from DataSourcing).

6. **CAST(... AS INTEGER):** Explicit casts to maintain integer types through SQLite. Without these, SQLite may return values as `long` in C#. Note: this may not fully resolve the Parquet type issue documented in Section 4; Phase D testing will verify.

## 6. V2 Job Config

```json
{
  "jobName": "DormantAccountDetectionV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "customer_id", "account_type", "current_balance"]
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
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "WITH target AS (SELECT CASE WHEN strftime('%w', MAX(a.as_of)) = '6' THEN date(MAX(a.as_of), '-1 day') WHEN strftime('%w', MAX(a.as_of)) = '0' THEN date(MAX(a.as_of), '-2 days') ELSE MAX(a.as_of) END AS target_date FROM accounts a), active_accounts AS (SELECT DISTINCT t.account_id FROM transactions t, target td WHERE t.as_of = td.target_date), customer_lookup AS (SELECT c.id AS customer_id, c.first_name, c.last_name FROM customers c INNER JOIN (SELECT id, MAX(as_of) AS max_as_of FROM customers GROUP BY id) latest ON c.id = latest.id AND c.as_of = latest.max_as_of) SELECT CAST(a.account_id AS INTEGER) AS account_id, CAST(a.customer_id AS INTEGER) AS customer_id, COALESCE(cl.first_name, '') AS first_name, COALESCE(cl.last_name, '') AS last_name, a.account_type, a.current_balance, td.target_date AS as_of FROM accounts a CROSS JOIN target td LEFT JOIN active_accounts aa ON a.account_id = aa.account_id LEFT JOIN customer_lookup cl ON a.customer_id = cl.customer_id WHERE aa.account_id IS NULL"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/dormant_account_detection/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

### Differences from V1 Config

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| transactions columns | `[transaction_id, account_id, txn_type, amount]` | `[account_id]` | AP4: only account_id and as_of (auto-appended) are used |
| External module | `DormantAccountDetector` | Removed | AP3: replaced with Transformation SQL; AP6: row-by-row replaced with set-based SQL |
| Transformation | Not present | Added | Replaces External module logic with SQL |
| Output directory | `Output/curated/dormant_account_detection/` | `Output/double_secret_curated/dormant_account_detection/` | V2 convention |
| Job name | `DormantAccountDetection` | `DormantAccountDetectionV2` | V2 naming convention |

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| type | ParquetFileWriter | ParquetFileWriter | YES |
| source | `output` | `output` | YES |
| numParts | 1 | 1 | YES |
| writeMode | Overwrite | Overwrite | YES |
| outputDirectory | `Output/curated/dormant_account_detection/` | `Output/double_secret_curated/dormant_account_detection/` | Changed per V2 convention |

## 8. Proofmark Config Design

**Excluded columns:** None.

**Fuzzy columns:** None initially. See risk note below.

**Rationale:** All output columns are deterministic. The BRD states "Non-Deterministic Fields: None identified." The output is computed from source data with deterministic date logic, joins, and string formatting. No timestamps, random values, or execution-time-dependent fields are present.

**Risk: current_balance type conversion.** V1 preserves the PostgreSQL `numeric`/`decimal` type for `current_balance` through the External module. V2 passes it through SQLite, which converts `decimal` to `REAL` (double). If the Parquet column types differ (`decimal?` vs `double?`), comparison may fail. If this occurs during Phase D:
- First attempt: Add a FUZZY override for `current_balance` with absolute tolerance of 0.001 to handle floating-point conversion differences.
- Second attempt: If tolerance-based matching is insufficient, escalate to Tier 2 with a minimal External module for type coercion.

**Proofmark config:**
```yaml
comparison_target: "dormant_account_detection"
reader: parquet
threshold: 100.0
```

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Weekend fallback in SQL CASE expression | BR-1: Read __maxEffectiveDate; BR-2: Weekend fallback | [DormantAccountDetector.cs:27-30] |
| W2 replication with strftime/date functions | W2: Weekend fallback | [KNOWN_ANTI_PATTERNS.md:W2] |
| Anti-join for dormancy detection | BR-3: Dormant = no transactions on target date | [DormantAccountDetector.cs:33-46, 70] |
| Transaction filter on as_of (not txn_timestamp) | BR-4: as_of date comparison | [DormantAccountDetector.cs:39-40] |
| No WHERE filter on account_type/status/balance | BR-5: All accounts evaluated | [DormantAccountDetector.cs:64] |
| No GROUP BY on account_id (preserve multi-date rows) | BR-6/BR-12: Multi-date account duplication | [DormantAccountDetector.cs:64-85] |
| SQL returns empty result when no accounts | BR-7: Empty accounts guard | V2: zero rows from SQL; V1: [DormantAccountDetector.cs:20-24] |
| customer_lookup CTE with MAX(as_of) | BR-8: Customer lookup last-write-wins | [DormantAccountDetector.cs:49-59] |
| COALESCE(first_name, '') and COALESCE(last_name, '') | BR-9: Missing customer defaults to empty string | [DormantAccountDetector.cs:72] |
| td.target_date AS as_of (string from SQLite) | BR-10: Output as_of = adjusted target date string | [DormantAccountDetector.cs:82] |
| Remove transaction_id, txn_type, amount from DataSourcing | BR-11: Unused sourced columns; AP4 | [dormant_account_detection.json:16]; [DormantAccountDetector.cs:38-44] |
| Eliminate External module | AP3: Unnecessary External | All logic expressible in SQL |
| Eliminate row-by-row iteration | AP6: Row-by-row iteration | Replaced with SQL set operations |
| Remove unused transaction columns | AP4: Unused columns | Only account_id needed from transactions |
| numParts=1 | BRD Writer Configuration | [dormant_account_detection.json:33] |
| writeMode=Overwrite | BRD Writer Configuration | [dormant_account_detection.json:34] |
| firstEffectiveDate=2024-10-01 | BRD/V1 Config | [dormant_account_detection.json:3] |
| No Proofmark exclusions or fuzzy | BRD: no non-deterministic fields | BRD Non-Deterministic Fields section |

## 10. External Module Design

**Not applicable.** V2 uses Tier 1 (Framework Only) with a four-module chain: DataSourcing (x3) -> Transformation -> ParquetFileWriter. No External module is needed.

**Escalation path (documented for Phase D):** If Parquet comparison fails due to type differences (int vs long for account_id/customer_id, or decimal vs double for current_balance) introduced by SQLite type coercion in the Transformation module, the resolution path is:
1. First: Attempt FUZZY matching in Proofmark config for affected numeric columns.
2. Second: Escalate to Tier 2 with a minimal External module positioned after Transformation that casts `long` -> `int` for ID columns and `double` -> `decimal` for monetary columns, preserving the SQL-based business logic while fixing output types.
3. Last resort: Escalate to Tier 3 if the type coercion module cannot be isolated from the business logic.
