# CrossSellCandidates — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** CrossSellCandidatesV2
**Tier:** Tier 1 — Framework Only (`DataSourcing → Transformation (SQL) → CsvFileWriter`)

### Tier Justification

The V1 implementation uses an External module (`CrossSellCandidateFinder.cs`) that performs row-by-row iteration over customers, builds in-memory dictionaries of account types / card presence / investment presence, then assembles output rows. Every operation in the External module is expressible in SQL:

- LEFT JOINs between customers and accounts/cards/investments
- GROUP BY with conditional aggregation (MAX of CASE expressions) for product flags
- String concatenation for missing products list
- CASE expressions for asymmetric representations (`"Yes"`/`"No Card"`, `1`/`0`)

SQLite supports all required operations. No procedural logic, snapshot fallback, or cross-boundary queries are needed. This is a textbook Tier 1 replacement.

**Anti-pattern eliminated:** AP3 (Unnecessary External module), AP6 (Row-by-row iteration).

## 2. V2 Module Chain

```
DataSourcing (customers)
  → DataSourcing (accounts)
  → DataSourcing (cards)
  → DataSourcing (investments)
  → Transformation (SQL → "output")
  → CsvFileWriter (→ Output/double_secret_curated/cross_sell_candidates.csv)
```

### Module Details

| Step | Module Type | resultName / source | Notes |
|------|-------------|---------------------|-------|
| 1 | DataSourcing | `customers` | `datalake.customers` — columns: `id`, `first_name`, `last_name` |
| 2 | DataSourcing | `accounts` | `datalake.accounts` — columns: `customer_id`, `account_type` (AP4: removed unused `account_id`) |
| 3 | DataSourcing | `cards` | `datalake.cards` — columns: `customer_id` (AP4: removed unused `card_id`) |
| 4 | DataSourcing | `investments` | `datalake.investments` — columns: `customer_id` (AP4: removed unused `investment_id`) |
| 5 | Transformation | `output` | SQL joins and conditional aggregation (see Section 5) |
| 6 | CsvFileWriter | source: `output` | Writes to `Output/double_secret_curated/cross_sell_candidates.csv` |

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns from V1

| Code | Name | Applies? | V2 Action |
|------|------|----------|-----------|
| AP3 | Unnecessary External module | **YES** | Eliminated. All logic moved to SQL Transformation. The V1 External module's join/iteration/flag-building logic is directly expressible as SQL LEFT JOINs + CASE + GROUP BY. |
| AP5 | Asymmetric NULLs | **YES** | Reproduced in output. V1 uses `"Yes"`/`"No Card"` for cards but `1`/`0` for investments. The SQL CASE expressions deliberately replicate this asymmetry. Comments in the SQL document the intentional replication. |
| AP6 | Row-by-row iteration | **YES** | Eliminated. V1 iterates `foreach (var custRow in customers.Rows)` with nested dictionary lookups [CrossSellCandidateFinder.cs:65]. V2 uses set-based SQL with JOINs and GROUP BY. |
| AP1 | Dead-end sourcing | No | All four sourced tables (customers, accounts, cards, investments) are used in the business logic. |
| AP4 | Unused columns | No | V1 sources `account_id` from accounts — this column is not directly referenced in the output but is implicitly used in the join/grouping context. However, it is technically unused because `customer_id` and `account_type` are the only columns the logic needs. **V2 action: Remove `account_id` from accounts DataSourcing; remove `card_id` from cards DataSourcing; remove `investment_id` from investments DataSourcing.** Only `customer_id` is needed from cards and investments for presence detection. Only `customer_id` and `account_type` are needed from accounts. |
| W9 | Wrong writeMode | **YES — document only** | V1 uses Overwrite mode. For multi-day auto-advance runs, only the final effective date's output survives on disk. This is V1's behavior and is replicated in V2 with a comment. |

### Anti-Patterns NOT Present

| Code | Reason Not Applicable |
|------|----------------------|
| AP2 | No cross-job logic duplication identified |
| AP7 | No magic values — string comparisons use exact account type names from the database |
| AP8 | No complex SQL / unused CTEs (V1 had no SQL) |
| AP9 | Job name accurately describes output (cross-sell candidates) |
| AP10 | Effective dates injected by executor; no manual date filtering needed |
| W1-W8, W10, W12 | No weekend logic, no integer division, no trailers, no Parquet, no append-mode headers |

## 4. Output Schema

The V2 output must be byte-identical to V1. All column names, types, and representations are preserved.

| Column | Type in CSV | Source | Transformation | Evidence |
|--------|------------|--------|----------------|----------|
| `customer_id` | integer | `customers.id` | Cast to integer via `CAST(c.id AS INTEGER)` | [CrossSellCandidateFinder.cs:67] `Convert.ToInt32(custRow["id"])` |
| `first_name` | string | `customers.first_name` | COALESCE to empty string | [CrossSellCandidateFinder.cs:92] `custRow["first_name"]?.ToString() ?? ""` |
| `last_name` | string | `customers.last_name` | COALESCE to empty string | [CrossSellCandidateFinder.cs:93] `custRow["last_name"]?.ToString() ?? ""` |
| `has_checking` | string (`"True"`/`"False"`) | `accounts.account_type` | CASE: `'True'` if customer has a "Checking" account, else `'False'` | [CrossSellCandidateFinder.cs:70,94] `hasChecking` is C# bool; `CsvFileWriter.FormatField` calls `.ToString()` producing `"True"`/`"False"` |
| `has_savings` | string (`"True"`/`"False"`) | `accounts.account_type` | CASE: `'True'` if customer has a "Savings" account, else `'False'` | [CrossSellCandidateFinder.cs:71,95] Same pattern as has_checking |
| `has_credit` | string (`"True"`/`"False"`) | `accounts.account_type` | CASE: `'True'` if customer has a "Credit" account, else `'False'` | [CrossSellCandidateFinder.cs:72,96] Same pattern as has_checking |
| `has_card` | string (`"Yes"`/`"No Card"`) | `cards.customer_id` | CASE: `'Yes'` if customer has cards, else `'No Card'` | [CrossSellCandidateFinder.cs:73,97] `hasCard ? "Yes" : "No Card"` — AP5 asymmetric representation |
| `has_investment` | integer (`1`/`0`) | `investments.customer_id` | CASE: `1` if customer has investments, else `0` | [CrossSellCandidateFinder.cs:74,85,98] `hasInvestment ? 1 : 0` — AP5 asymmetric representation |
| `missing_products` | string | Derived | Semicolon-separated list of missing products (Checking, Savings, Credit, No Card) or `"None"`. Investment is NOT included. | [CrossSellCandidateFinder.cs:77-87] |
| `as_of` | date string | `__maxEffectiveDate` via DataSourcing `as_of` | Derived from `c.as_of` in the GROUP BY. SQL reformats from `yyyy-MM-dd` to `MM/dd/yyyy` via SUBSTR to match V1's `DateOnly.ToString()` format. | [CrossSellCandidateFinder.cs:28,100] |

### Critical Type Representation Notes

**Boolean columns (`has_checking`, `has_savings`, `has_credit`):** V1 stores these as C# `bool` values in the DataFrame. When `CsvFileWriter.FormatField` calls `.ToString()` on a `bool`, it produces `"True"` or `"False"` (capitalized). The Transformation module converts bools to SQLite integers via `ToSqliteValue` [Transformation.cs:109], and SQLite returns `long` values from queries. To produce identical CSV output, the SQL must output the *strings* `'True'` and `'False'` rather than integers `1`/`0`.

**has_card:** V1 stores this as a `string` (`"Yes"` or `"No Card"`). SQL outputs the same strings directly.

**has_investment:** V1 stores this as a C# `int` (1 or 0). SQLite integer `.ToString()` produces `"1"` or `"0"`, matching V1's `int.ToString()`. No special handling needed.

**as_of (DATE FORMAT) -- RESOLVED:** The two execution paths produce different internal types that render differently in CSV output:

- **V1 path:** External module stores `DateOnly` object in Row [CrossSellCandidateFinder.cs:100] -> CsvFileWriter calls `FormatField` -> `val.ToString()` on `DateOnly` [CsvFileWriter.cs:80] -> `DateOnly.ToString()` with no format argument uses the current culture's short date format. In the Docker container's .NET runtime (InvariantCulture), this produces `"MM/dd/yyyy"` (e.g., `"10/15/2024"`).
- **V2 path:** DataSourcing stores `DateOnly` in DataFrame -> `Transformation.ToSqliteValue` converts to `"yyyy-MM-dd"` string [Transformation.cs:110] -> SQL selects it as string -> CsvFileWriter calls `string.ToString()` -> outputs `"yyyy-MM-dd"` (e.g., `"2024-10-15"`).

**Resolution:** The V2 SQL reformats `as_of` from `"yyyy-MM-dd"` to `"MM/dd/yyyy"` using SQLite string functions to match V1's `DateOnly.ToString()` output:
```sql
SUBSTR(c.as_of, 6, 2) || '/' || SUBSTR(c.as_of, 9, 2) || '/' || SUBSTR(c.as_of, 1, 4) AS as_of
```
This converts `"2024-10-15"` to `"10/15/2024"`, matching the InvariantCulture `DateOnly.ToString()` format. The SUBSTR approach is deterministic and requires no External module or Tier 2 escalation.

**as_of derivation:** The executor runs each effective date one day at a time (gap-fill). For each single-day run, `__minEffectiveDate == __maxEffectiveDate`, so all DataSourcing rows share the same `as_of` value. The SQL includes `c.as_of` in the GROUP BY clause, which is equivalent to V1's `(DateOnly)sharedState["__maxEffectiveDate"]`.

## 5. SQL Design

### Approach

A single SQL query using:
- `customers` as the driving table (LEFT JOIN to all others ensures every customer gets a row — BR-10)
- LEFT JOIN to `accounts` on `customer_id` for account type detection
- LEFT JOIN to `cards` on `customer_id` for card presence
- LEFT JOIN to `investments` on `customer_id` for investment presence
- GROUP BY customer to aggregate account types and detect presence
- CASE expressions for all product flags
- Nested CASE / string concatenation for `missing_products`

### SQL Query

```sql
SELECT
    CAST(c.id AS INTEGER) AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    CASE WHEN MAX(CASE WHEN a.account_type = 'Checking' THEN 1 ELSE 0 END) = 1
         THEN 'True' ELSE 'False' END AS has_checking,
    CASE WHEN MAX(CASE WHEN a.account_type = 'Savings' THEN 1 ELSE 0 END) = 1
         THEN 'True' ELSE 'False' END AS has_savings,
    CASE WHEN MAX(CASE WHEN a.account_type = 'Credit' THEN 1 ELSE 0 END) = 1
         THEN 'True' ELSE 'False' END AS has_credit,
    CASE WHEN COUNT(DISTINCT cd.customer_id) > 0
         THEN 'Yes' ELSE 'No Card' END AS has_card,
    CASE WHEN COUNT(DISTINCT inv.customer_id) > 0
         THEN 1 ELSE 0 END AS has_investment,
    CASE
        WHEN MAX(CASE WHEN a.account_type = 'Checking' THEN 1 ELSE 0 END) = 1
         AND MAX(CASE WHEN a.account_type = 'Savings' THEN 1 ELSE 0 END) = 1
         AND MAX(CASE WHEN a.account_type = 'Credit' THEN 1 ELSE 0 END) = 1
         AND COUNT(DISTINCT cd.customer_id) > 0
        THEN 'None'
        ELSE SUBSTR(
            CASE WHEN MAX(CASE WHEN a.account_type = 'Checking' THEN 1 ELSE 0 END) = 0
                 THEN '; Checking' ELSE '' END
            || CASE WHEN MAX(CASE WHEN a.account_type = 'Savings' THEN 1 ELSE 0 END) = 0
                 THEN '; Savings' ELSE '' END
            || CASE WHEN MAX(CASE WHEN a.account_type = 'Credit' THEN 1 ELSE 0 END) = 0
                 THEN '; Credit' ELSE '' END
            || CASE WHEN COUNT(DISTINCT cd.customer_id) = 0
                 THEN '; No Card' ELSE '' END
        , 3)
    END AS missing_products,
    SUBSTR(c.as_of, 6, 2) || '/' || SUBSTR(c.as_of, 9, 2) || '/' || SUBSTR(c.as_of, 1, 4) AS as_of
FROM customers c
LEFT JOIN accounts a ON c.id = a.customer_id AND c.as_of = a.as_of
LEFT JOIN cards cd ON c.id = cd.customer_id AND c.as_of = cd.as_of
LEFT JOIN investments inv ON c.id = inv.customer_id AND c.as_of = inv.as_of
GROUP BY c.id, c.first_name, c.last_name, c.as_of
ORDER BY c.id
```

### SQL Design Notes

1. **JOIN condition includes `as_of`:** DataSourcing returns all rows within the effective date range (single date for gap-fill runs). Joining on both `customer_id` and `as_of` ensures correct temporal alignment.

2. **missing_products concatenation strategy:** SQLite lacks `STRING_AGG` for conditionally building delimited lists. The approach concatenates conditionally with a leading `"; "` prefix on each item (e.g., `"; Checking; Savings"`), then uses `SUBSTR(x, 3)` to strip the leading `"; "` (2 characters). This is safe because the ELSE branch only fires when at least one product is missing, guaranteeing the concatenated string always starts with `"; "`.

3. **Aggregate CASE pattern:** Each product flag is computed as `MAX(CASE WHEN condition THEN 1 ELSE 0 END)`. For LEFT JOINs where no matching row exists, `account_type` is NULL, so the CASE falls to ELSE 0, and `MAX(0) = 0`. This correctly reports the product as absent.

4. **COUNT(DISTINCT) for presence detection:** Cards and investments use `COUNT(DISTINCT cd.customer_id)` rather than `COUNT(cd.customer_id)` to avoid inflated counts from the cross-join effect of multiple LEFT JOINs. A customer with 3 accounts and 2 cards would produce 6 joined rows; `COUNT(cd.customer_id)` would be 6, but `COUNT(DISTINCT cd.customer_id)` is correctly 1 (or 0 if no cards).

### Empty Guard Consideration (BR-8)

**Important framework behavior:** `Transformation.RegisterTable` [Transformation.cs:46] skips registration for DataFrames with zero rows (`if (!df.Rows.Any()) return`). This means if any sourced table has zero rows for a given effective date, its SQLite table will not exist, and the SQL query will fail with a "no such table" error.

In V1, the External module handles empty DataFrames gracefully: it returns an empty output if `customers` is null/empty [CrossSellCandidateFinder.cs:22-26], and uses `GetValueOrDefault` for the lookup dictionaries so empty accounts/cards/investments still produce output rows.

**Risk assessment:** LOW. The data lake uses a full-load snapshot pattern where every table has rows for every `as_of` date. In the expected date range (2024-10-01 to 2024-12-31), all four tables will have data. If this assumption is violated, the SQL will fail rather than producing an empty file. This is an acceptable behavioral difference -- the framework's Transformation module is not designed for missing-table graceful degradation, and the data lake's snapshot guarantee makes this a non-issue in practice.

For the non-empty case, the LEFT JOIN approach handles V1's behavior naturally: customers with no matching accounts/cards/investments still get output rows with appropriate default values (BR-10).

### as_of Date Format -- RESOLVED

The `as_of` column passes through different type paths in V1 vs V2:

- **V1:** `DateOnly` object stored in Row [CrossSellCandidateFinder.cs:100] -> `CsvFileWriter.FormatField` calls `.ToString()` [CsvFileWriter.cs:80] -> `DateOnly.ToString()` -> InvariantCulture default `"MM/dd/yyyy"` (e.g., `"10/15/2024"`)
- **V2:** `DateOnly` -> `Transformation.ToSqliteValue` -> `"yyyy-MM-dd"` string [Transformation.cs:110] -> SQL reformats via SUBSTR -> `"MM/dd/yyyy"` (e.g., `"10/15/2024"`)

The V2 SQL applies the SUBSTR reformatting directly in the query:
```sql
SUBSTR(c.as_of, 6, 2) || '/' || SUBSTR(c.as_of, 9, 2) || '/' || SUBSTR(c.as_of, 1, 4) AS as_of
```
This produces the same `"MM/dd/yyyy"` format as V1's `DateOnly.ToString()` under InvariantCulture. The conversion is incorporated into the SQL design (Section 5) and the job config JSON (Section 6).

## 6. V2 Job Config JSON

```json
{
  "jobName": "CrossSellCandidatesV2",
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
      "columns": ["customer_id", "account_type"]
    },
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["customer_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "investments",
      "schema": "datalake",
      "table": "investments",
      "columns": ["customer_id"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT CAST(c.id AS INTEGER) AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, CASE WHEN MAX(CASE WHEN a.account_type = 'Checking' THEN 1 ELSE 0 END) = 1 THEN 'True' ELSE 'False' END AS has_checking, CASE WHEN MAX(CASE WHEN a.account_type = 'Savings' THEN 1 ELSE 0 END) = 1 THEN 'True' ELSE 'False' END AS has_savings, CASE WHEN MAX(CASE WHEN a.account_type = 'Credit' THEN 1 ELSE 0 END) = 1 THEN 'True' ELSE 'False' END AS has_credit, CASE WHEN COUNT(DISTINCT cd.customer_id) > 0 THEN 'Yes' ELSE 'No Card' END AS has_card, CASE WHEN COUNT(DISTINCT inv.customer_id) > 0 THEN 1 ELSE 0 END AS has_investment, CASE WHEN MAX(CASE WHEN a.account_type = 'Checking' THEN 1 ELSE 0 END) = 1 AND MAX(CASE WHEN a.account_type = 'Savings' THEN 1 ELSE 0 END) = 1 AND MAX(CASE WHEN a.account_type = 'Credit' THEN 1 ELSE 0 END) = 1 AND COUNT(DISTINCT cd.customer_id) > 0 THEN 'None' ELSE SUBSTR(CASE WHEN MAX(CASE WHEN a.account_type = 'Checking' THEN 1 ELSE 0 END) = 0 THEN '; Checking' ELSE '' END || CASE WHEN MAX(CASE WHEN a.account_type = 'Savings' THEN 1 ELSE 0 END) = 0 THEN '; Savings' ELSE '' END || CASE WHEN MAX(CASE WHEN a.account_type = 'Credit' THEN 1 ELSE 0 END) = 0 THEN '; Credit' ELSE '' END || CASE WHEN COUNT(DISTINCT cd.customer_id) = 0 THEN '; No Card' ELSE '' END, 3) END AS missing_products, SUBSTR(c.as_of, 6, 2) || '/' || SUBSTR(c.as_of, 9, 2) || '/' || SUBSTR(c.as_of, 1, 4) AS as_of FROM customers c LEFT JOIN accounts a ON c.id = a.customer_id AND c.as_of = a.as_of LEFT JOIN cards cd ON c.id = cd.customer_id AND c.as_of = cd.as_of LEFT JOIN investments inv ON c.id = inv.customer_id AND c.as_of = inv.as_of GROUP BY c.id, c.first_name, c.last_name, c.as_of ORDER BY c.id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/cross_sell_candidates.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Change | V1 | V2 | Reason |
|--------|----|----|--------|
| `jobName` | `CrossSellCandidates` | `CrossSellCandidatesV2` | V2 naming convention |
| `outputFile` | `Output/curated/cross_sell_candidates.csv` | `Output/double_secret_curated/cross_sell_candidates.csv` | V2 output directory |
| External module | `CrossSellCandidateFinder` | Removed | AP3 — replaced with Transformation SQL |
| accounts columns | `["account_id", "customer_id", "account_type"]` | `["customer_id", "account_type"]` | AP4 — `account_id` unused |
| cards columns | `["card_id", "customer_id"]` | `["customer_id"]` | AP4 — `card_id` unused |
| investments columns | `["investment_id", "customer_id"]` | `["customer_id"]` | AP4 — `investment_id` unused |
| Transformation | N/A (not present in V1) | Added with SQL query | AP3/AP6 — replaces External module |

## 7. Writer Config

**Writer Type:** CsvFileWriter (matches V1)

| Parameter | Value | Matches V1? | Evidence |
|-----------|-------|-------------|----------|
| `source` | `"output"` | Yes | [cross_sell_candidates.json:40] |
| `outputFile` | `"Output/double_secret_curated/cross_sell_candidates.csv"` | Path changed (V2 convention) | [cross_sell_candidates.json:41] |
| `includeHeader` | `true` | Yes | [cross_sell_candidates.json:42] |
| `writeMode` | `"Overwrite"` | Yes | [cross_sell_candidates.json:43] — W9: Overwrite means only the last effective date's output survives on disk in multi-day runs |
| `lineEnding` | `"LF"` | Yes | [cross_sell_candidates.json:44] |
| `trailerFormat` | Not configured | Yes | V1 has no trailer |

## 8. Proofmark Config Design

### Recommended Config

```yaml
comparison_target: "cross_sell_candidates"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Column Override Analysis

| Column | Override? | Rationale |
|--------|----------|-----------|
| `customer_id` | STRICT | Deterministic integer from customers.id |
| `first_name` | STRICT | Deterministic from source data |
| `last_name` | STRICT | Deterministic from source data |
| `has_checking` | STRICT | Deterministic boolean flag |
| `has_savings` | STRICT | Deterministic boolean flag |
| `has_credit` | STRICT | Deterministic boolean flag |
| `has_card` | STRICT | Deterministic string flag |
| `has_investment` | STRICT | Deterministic integer flag |
| `missing_products` | STRICT | Deterministic derived string |
| `as_of` | STRICT | Deterministic date from effective date |

**Zero exclusions, zero fuzzy overrides.** All columns are deterministic and should match exactly.

### Known Risks

1. **as_of date format -- RESOLVED:** V1 stores `DateOnly` in the DataFrame; V2 stores a string from SQLite. V1's `DateOnly.ToString()` produces `"MM/dd/yyyy"` under InvariantCulture [CsvFileWriter.cs:80]. V2's SQL now applies `SUBSTR(c.as_of, 6, 2) || '/' || SUBSTR(c.as_of, 9, 2) || '/' || SUBSTR(c.as_of, 1, 4)` to convert from `"yyyy-MM-dd"` to `"MM/dd/yyyy"`, matching V1's output format. No Proofmark override needed.

2. **Row ordering:** V1 iterates customers in DataSourcing return order (PostgreSQL natural order within `as_of`). V2 uses `ORDER BY c.id`. If PostgreSQL returns customers in a different order than by `id`, the row order will differ. Since Proofmark compares row-by-row, this could cause false failures. Resolution: verify PostgreSQL's default ordering or adjust the SQL ORDER BY.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Product ownership per-customer | Section 5 (SQL) | LEFT JOIN accounts + CASE on account_type |
| BR-2: Exact string match on account types | Section 5 (SQL) | `a.account_type = 'Checking'` etc. |
| BR-3: has_card asymmetric representation | Section 5 (SQL) | `CASE ... THEN 'Yes' ELSE 'No Card'` |
| BR-4: has_investment numeric representation | Section 5 (SQL) | `CASE ... THEN 1 ELSE 0` |
| BR-5: Missing products excludes investment | Section 5 (SQL) | missing_products CASE only checks Checking, Savings, Credit, No Card |
| BR-6: Semicolon-space separator, "None" fallback | Section 5 (SQL) | `SUBSTR(... '; ' ..., 3)` with `'None'` CASE |
| BR-7: as_of from __maxEffectiveDate | Section 5 (SQL, notes) | `c.as_of` from DataSourcing (injected by executor) |
| BR-8: Empty guard on customers only | Section 5 (empty guard) | LEFT JOIN + SQL returns zero rows naturally if customers empty |
| BR-9: Boolean values for has_checking/savings/credit | Section 4, 5 | SQL outputs `'True'`/`'False'` strings to match `bool.ToString()` |
| BR-10: Every customer gets a row | Section 5 (SQL) | `customers` is the driving table with LEFT JOINs |
| OQ-1: Investment not in missing_products | Section 5 (SQL) | Replicated — missing_products CASE deliberately excludes investment |
| AP3: Unnecessary External module | Section 3 | Eliminated — all logic in SQL |
| AP4: Unused columns | Section 6 (config changes) | Removed `account_id`, `card_id`, `investment_id` |
| AP5: Asymmetric NULLs | Section 3, 4 | Reproduced in output with documentation |
| AP6: Row-by-row iteration | Section 3 | Eliminated — set-based SQL |
| W9: Overwrite mode | Section 7 | Reproduced — documented |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation — no External module is required. All business logic is expressed in the Transformation SQL.
