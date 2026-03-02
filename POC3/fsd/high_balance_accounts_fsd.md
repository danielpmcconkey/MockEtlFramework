# HighBalanceAccountsV2 — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** HighBalanceAccountsV2
**Config:** `high_balance_accounts_v2.json`
**Tier:** Tier 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> CsvFileWriter`

This job filters accounts with current_balance > $10,000, joins with customers to enrich with first/last name, and writes the result to CSV. The entire pipeline -- filtering, joining, and column projection -- is expressible in a single SQL statement. No External module is needed.

**Tier Justification:** V1 uses an External module (`HighBalanceFilter.cs`) to perform a dictionary-based customer lookup and row-by-row balance filtering. Both operations are trivially expressed as a SQL JOIN with a WHERE clause. Tier 1 eliminates the External module entirely (AP3, AP6).

---

## 2. V2 Module Chain

```
DataSourcing (accounts) -> DataSourcing (customers) -> Transformation (SQL) -> CsvFileWriter
```

| Step | Module | Purpose |
|------|--------|---------|
| 1 | DataSourcing | Fetch `accounts` from `datalake.accounts` with effective date injection |
| 2 | DataSourcing | Fetch `customers` from `datalake.customers` with effective date injection |
| 3 | Transformation | SQL: JOIN accounts to customers, filter balance > 10000, project output columns |
| 4 | CsvFileWriter | Write result to CSV with Overwrite mode, LF line endings, no trailer |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP3 | Unnecessary External module | V1 uses `HighBalanceFilter.cs` External module where SQL JOIN + WHERE would suffice | **Eliminated.** Replaced with Tier 1 Transformation SQL. The balance filter and customer name lookup are native SQL operations. |
| AP4 | Unused columns | V1 sources `account_status` from `datalake.accounts` but never includes it in the output columns [HighBalanceFilter.cs:10-14] | **Eliminated.** V2 DataSourcing omits `account_status` from the column list entirely. |
| AP6 | Row-by-row iteration | V1 iterates accounts row-by-row with `foreach`, builds a dictionary of customer names, then looks up each customer individually [HighBalanceFilter.cs:26-55] | **Eliminated.** Replaced with a single SQL JOIN + WHERE clause. Set-based operation. |

### Output-Affecting Wrinkles

No W-codes apply to this job:

- **W1/W2 (weekend):** Not applicable. The job does not have special weekend logic. DataSourcing returns whatever the datalake has for the effective date. If there are no account rows for a weekend date, the output is empty with header only -- same behavior in V1 and V2.
- **W4 (integer division):** Not applicable. No percentage calculations.
- **W5 (banker's rounding):** Not applicable. No rounding operations.
- **W6 (double epsilon):** Not applicable. The balance comparison in V1 uses `decimal` [HighBalanceFilter.cs:38], and passthrough values are not accumulated.
- **W7/W8 (trailer):** Not applicable. No trailer in this job.
- **W9 (wrong writeMode):** V1 uses Overwrite, which means only the last effective date's data persists. This is the documented V1 behavior. V2 reproduces the same write mode.
- **W12 (header every append):** Not applicable. Overwrite mode, not Append.

---

## 4. Output Schema

| Column | Source | Transformation | BRD Requirement |
|--------|--------|---------------|-----------------|
| account_id | accounts.account_id | Passthrough | BR-1 (qualifying accounts) |
| customer_id | accounts.customer_id | Passthrough | BR-1 |
| account_type | accounts.account_type | Passthrough | BR-1 |
| current_balance | accounts.current_balance | Passthrough (only rows where > 10000) | BR-1, BR-5 |
| first_name | customers.first_name | LEFT JOIN by customer_id; empty string if no match | BR-2 |
| last_name | customers.last_name | LEFT JOIN by customer_id; empty string if no match | BR-2 |
| as_of | accounts.as_of | Passthrough from account row | BR-6 (corrected -- see note) |

**Note on BR-6 (BRD Correction):** The BRD states that `as_of` is sourced from `sharedState["__maxEffectiveDate"]`. However, the actual V1 code at `HighBalanceFilter.cs:52` reads `acctRow["as_of"]` -- the account row's own `as_of` column, not the shared state key. In practice these are identical because the executor runs one effective date at a time (min == max == as_of in the data), so the output is byte-identical regardless of which source is used. The V2 SQL uses `a.as_of` from the account row, which matches the actual V1 code behavior.

---

## 5. SQL Design

```sql
SELECT
    a.account_id,
    a.customer_id,
    a.account_type,
    a.current_balance,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    a.as_of
FROM accounts a
LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of
WHERE CAST(a.current_balance AS REAL) > 10000
```

### SQL Design Rationale

1. **LEFT JOIN** (not INNER JOIN): Matches V1 behavior where missing customers produce empty strings for names via `GetValueOrDefault` [HighBalanceFilter.cs:42]. A LEFT JOIN with COALESCE achieves the same default-to-empty-string behavior.

2. **JOIN condition includes `as_of`**: Both `accounts` and `customers` DataFrames are loaded by DataSourcing with an `as_of` column representing the snapshot date. Since both tables are snapshot-based, the join must be scoped to the same snapshot date to avoid cross-date joins.

3. **CAST(a.current_balance AS REAL) > 10000**: V1 uses `Convert.ToDecimal` for the comparison [HighBalanceFilter.cs:38] and applies a strictly-greater-than check (not >=) [HighBalanceFilter.cs:39]. SQLite stores values via the Transformation module's type inference, which maps `decimal` to `REAL` [Transformation.cs:103]. The `> 10000` operator in SQL correctly implements strict greater-than.

4. **COALESCE for names**: V1 uses `?.ToString() ?? ""` for customer names [HighBalanceFilter.cs:30-31] and `GetValueOrDefault(customerId, ("", ""))` for missing customers [HighBalanceFilter.cs:42]. COALESCE handles both NULL values and missing join matches (LEFT JOIN produces NULL).

5. **No ORDER BY**: V1 does not sort the output [HighBalanceFilter.cs:35-55]. The row order depends on the iteration order of the accounts DataFrame, which comes from DataSourcing's `ORDER BY as_of` query. Since both V1 and V2 iterate accounts in the same order (DataSourcing-produced order), the output order should match without an explicit ORDER BY in the Transformation SQL.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "HighBalanceAccountsV2",
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
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT a.account_id, a.customer_id, a.account_type, a.current_balance, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, a.as_of FROM accounts a LEFT JOIN customers c ON a.customer_id = c.id AND a.as_of = c.as_of WHERE CAST(a.current_balance AS REAL) > 10000"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/high_balance_accounts.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Design Notes

- **`account_status` removed** from accounts DataSourcing (AP4 elimination). V1 sourced this column but never used it in output or filtering.
- **No External module** (AP3 elimination). The entire pipeline is DataSourcing + Transformation + Writer.
- **`resultName: "output"`** on Transformation matches what CsvFileWriter expects via `"source": "output"`.
- **Writer config matches V1 exactly:** `includeHeader: true`, `writeMode: Overwrite`, `lineEnding: LF`, no `trailerFormat`.
- **Output path** changed to `Output/double_secret_curated/high_balance_accounts.csv` per V2 convention.
- **`firstEffectiveDate`** matches V1: `2024-10-01`.

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer Type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/high_balance_accounts.csv` | `Output/double_secret_curated/high_balance_accounts.csv` | Path change per V2 convention |
| includeHeader | `true` | `true` | Yes |
| writeMode | `Overwrite` | `Overwrite` | Yes |
| lineEnding | `LF` | `LF` | Yes |
| trailerFormat | Not specified | Not specified | Yes |

---

## 8. Proofmark Config Design

### Reader & Format Settings

- **Reader:** `csv` (matches V1 writer type: CsvFileWriter)
- **Header rows:** `1` (V1 config: `includeHeader: true`)
- **Trailer rows:** `0` (V1 config has no `trailerFormat`)

### Column Overrides

**None required.** Starting from the default strict configuration:

- **Excluded columns:** None. All output columns are deterministic. The `as_of` column is derived from the effective date injected by the executor, not from execution time.
- **Fuzzy columns:** None. No floating-point accumulation, no rounding, no percentage calculations. The `current_balance` values are passed through directly from the source data without arithmetic operations.

### Proofmark Config YAML

```yaml
comparison_target: "high_balance_accounts"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

---

## 9. Traceability Matrix

| BRD Requirement | FSD Design Element | Implementation |
|-----------------|-------------------|----------------|
| BR-1: Balance > 10000 (strictly greater than) | SQL WHERE clause: `CAST(a.current_balance AS REAL) > 10000` | Transformation SQL |
| BR-2: Customer name lookup with empty defaults | SQL LEFT JOIN + COALESCE: `COALESCE(c.first_name, '') AS first_name` | Transformation SQL |
| BR-3: account_status sourced but not in output | Removed from DataSourcing columns entirely (AP4 fix) | accounts DataSourcing config |
| BR-4: Empty output if accounts or customers null/empty | Transformation SQL naturally produces empty result if either table is empty (LEFT JOIN on empty table = no rows; WHERE filter on no rows = no rows). Note: If accounts is empty, SQL returns 0 rows. If customers is empty, LEFT JOIN produces NULLs for name columns, COALESCE converts to empty strings. Both match V1 behavior. | Transformation SQL + CsvFileWriter (writes header-only file for 0 rows) |
| BR-5: Decimal comparison for balance | SQLite CAST(... AS REAL) for comparison; V1 uses Convert.ToDecimal which for values > 10000 is equivalent to REAL comparison | Transformation SQL |
| BR-6: as_of from account row (corrected) | SQL: `a.as_of` -- sourced from account row, matching actual V1 code | Transformation SQL |
| Overwrite write mode | CsvFileWriter with `writeMode: Overwrite` | Job config |
| No trailer | No `trailerFormat` in CsvFileWriter config | Job config |
| LF line endings | CsvFileWriter with `lineEnding: LF` | Job config |
| firstEffectiveDate 2024-10-01 | Job config `firstEffectiveDate: 2024-10-01` | Job config |

---

## 10. External Module Design

**Not applicable.** This job is Tier 1 (Framework Only). No External module is needed.

The V1 External module (`HighBalanceFilter.cs`) performed two operations that are fully replaceable by SQL:

1. **Balance filtering** (`balance > 10000`) -> SQL `WHERE` clause
2. **Customer name lookup** (dictionary-based join with empty-string defaults) -> SQL `LEFT JOIN` + `COALESCE`

Both V1 anti-patterns (AP3: unnecessary External, AP6: row-by-row iteration) are eliminated by this Tier 1 design.

---

## Appendix: BR-4 Edge Case Analysis

The BRD states that if either `accounts` or `customers` is null or empty, an empty DataFrame is produced. In the V2 SQL implementation:

- **Empty `accounts` table:** The FROM clause produces zero rows. The WHERE filter and LEFT JOIN have nothing to operate on. Result: zero rows. CsvFileWriter writes a header-only file. Matches V1.
- **Empty `customers` table:** The LEFT JOIN produces NULL for `first_name` and `last_name`. COALESCE converts these to empty strings. The WHERE filter still applies. If any accounts have balance > 10000, they appear in output with empty name fields. This matches V1's `GetValueOrDefault` behavior [HighBalanceFilter.cs:42].
- **Note on V1 strictness:** V1 checks `customers == null || customers.Count == 0` and returns empty output. The SQL LEFT JOIN does NOT replicate this exactly -- if customers is empty but accounts has high-balance rows, the SQL will produce rows with empty names, whereas V1 produces zero rows. However, the Transformation module's `RegisterTable` method [Transformation.cs:46] returns early without creating the table if the DataFrame has no rows (`if (!df.Rows.Any()) return`). This means if `customers` is empty, the `customers` table will not exist in SQLite, and the LEFT JOIN will fail with a SQL error. This is a potential divergence point that needs attention during testing. If this edge case is encountered, a Tier 2 escalation may be needed, or the SQL could be restructured. In practice, the datalake `customers` table is expected to always have data for any valid effective date, so this edge case is unlikely to surface during the 2024-10-01 through 2024-12-31 date range.
