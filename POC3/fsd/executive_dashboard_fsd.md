# ExecutiveDashboard — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** ExecutiveDashboardV2
**Tier:** Tier 2 — Framework + Minimal External (SCALPEL)

The ExecutiveDashboard job produces a vertical table of 9 key business metrics (total customers, accounts, balances, transactions, loans, branch visits) with `metric_name`, `metric_value`, and `as_of` columns. Output is CSV with a SUMMARY trailer.

### Tier Justification

Tier 1 (pure SQL) is insufficient because of the following constraint:

1. **Guard clause behavior:** V1 checks whether `customers`, `accounts`, or `loan_accounts` are empty and returns an empty DataFrame with defined columns (`metric_name`, `metric_value`, `as_of`) if any are empty. The Transformation module's `RegisterTable` method skips empty DataFrames (`if (!df.Rows.Any()) return;`), meaning empty tables are never registered in SQLite. A SQL query referencing an unregistered table would throw an error rather than producing an empty result. The guard clause must produce a 0-row DataFrame with correct column names — this cannot be expressed in SQLite SQL alone.

2. **as_of fallback logic:** The `as_of` value is derived from the first customer row, with fallback to the first transaction row. This row-level lookup with conditional fallback is awkward in pure SQL and would require UNION + LIMIT constructs across separate DataFrames. While technically possible, combining it with the guard clause makes an External module the cleaner choice.

3. **Vertical pivot:** The output is a vertical metric table (9 rows, one per metric) rather than a horizontal aggregate. SQLite can do this with UNION ALL, but combined with the guard clause and as_of fallback, the SQL becomes brittle.

The External module handles ONLY the guard clause, as_of resolution, metric computation, and vertical pivot. DataSourcing pulls all data. CsvFileWriter handles output.

## 2. V2 Module Chain

```
DataSourcing (transactions)
  → DataSourcing (accounts)
  → DataSourcing (customers)
  → DataSourcing (loan_accounts)
  → DataSourcing (branch_visits)
  → External (ExecutiveDashboardV2Processor)
  → CsvFileWriter
```

### Module Details

| # | Type | Config Key | Purpose |
|---|------|-----------|---------|
| 1 | DataSourcing | transactions | Pull transaction_id, account_id, amount from datalake.transactions |
| 2 | DataSourcing | accounts | Pull account_id, current_balance from datalake.accounts |
| 3 | DataSourcing | customers | Pull id from datalake.customers |
| 4 | DataSourcing | loan_accounts | Pull loan_id, current_balance from datalake.loan_accounts |
| 5 | DataSourcing | branch_visits | Pull visit_id from datalake.branch_visits |
| 6 | External | ExecutiveDashboardV2Processor | Guard clause, as_of resolution, metric computation, vertical pivot |
| 7 | CsvFileWriter | output | Write to Output/double_secret_curated/executive_dashboard.csv |

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| Code | Applies? | V1 Behavior | V2 Prescription |
|------|----------|-------------|-----------------|
| W5 | YES | `Math.Round(value, 2)` uses default MidpointRounding.ToEven (banker's rounding) | Reproduce: use `Math.Round(value, 2, MidpointRounding.ToEven)` explicitly with comment documenting the choice |
| W9 | YES | Overwrite mode means prior days' data is lost on each run | Reproduce: use Overwrite mode exactly as V1. Document: `// V1 uses Overwrite — prior days' data is lost on each run.` |
| AP1 | YES | V1 sources `branches` and `segments` tables that are never used by the External module | **Eliminate:** Remove `branches` and `segments` DataSourcing entries from V2 config |
| AP3 | PARTIAL | V1 uses External module where some logic could be SQL, but guard clause and pivot prevent pure SQL | **Partially addressed:** Use Tier 2 (External is minimal and focused). Cannot fully eliminate External due to guard clause/pivot constraints. |
| AP4 | YES | V1 sources unused columns: `txn_type` (transactions), `customer_id`/`account_type`/`account_status` (accounts), `first_name`/`last_name` (customers), `customer_id`/`loan_type` (loan_accounts), `customer_id`/`branch_id`/`visit_purpose` (branch_visits) | **Eliminate:** Only source columns actually used in metric computation |
| AP6 | YES | V1 uses `foreach` loops to sum `current_balance` and `amount` | **Eliminate:** Use LINQ `.Sum()` for set-based accumulation in External module |
| AP7 | NO | No magic values — metric names are self-documenting strings, rounding precision (2) is standard | N/A |

### Anti-Patterns NOT Present

| Code | Why Not |
|------|---------|
| W1 | No Sunday-specific skip logic; guard clause handles weekends via empty data |
| W2 | No weekend fallback date logic |
| W3a/b/c | No boundary summary rows |
| W4 | No integer division; all arithmetic is decimal |
| W6 | V1 uses `decimal` throughout (Convert.ToDecimal), not `double` — no epsilon issues |
| W7 | No trailer count inflation; framework CsvFileWriter counts output rows correctly |
| W8 | No hardcoded trailer date; uses `{date}` token resolved by framework |
| W10 | Not Parquet output |
| W12 | Not Append mode with repeated headers |
| AP2 | No cross-job duplication identified |
| AP5 | NULL handling is consistent (null branchVisits → 0, null transactions → 0) |
| AP8 | No SQL transformations with unused CTEs |
| AP9 | Job name "ExecutiveDashboard" accurately describes the output |
| AP10 | DataSourcing uses framework effective date injection, no manual date filtering |

## 4. Output Schema

| Column | Type | Source | Transformation |
|--------|------|--------|---------------|
| metric_name | string | Fixed strings | One of 9 metric names in fixed order |
| metric_value | decimal | Computed | Rounded to 2 decimal places (MidpointRounding.ToEven) |
| as_of | date | customers.Rows[0].as_of | Fallback to transactions.Rows[0].as_of if customer as_of is null |

### Metric Definitions (Fixed Order)

| # | metric_name | Computation | Evidence |
|---|-------------|-------------|----------|
| 1 | total_customers | COUNT of customer rows (not distinct) | [ExecutiveDashboardBuilder.cs:38] |
| 2 | total_accounts | COUNT of account rows (not distinct) | [ExecutiveDashboardBuilder.cs:40] |
| 3 | total_balance | SUM of accounts.current_balance (all accounts, no filter) | [ExecutiveDashboardBuilder.cs:43-47] |
| 4 | total_transactions | COUNT of transaction rows | [ExecutiveDashboardBuilder.cs:53-55] |
| 5 | total_txn_amount | SUM of transactions.amount | [ExecutiveDashboardBuilder.cs:55-58] |
| 6 | avg_txn_amount | total_txn_amount / total_transactions; 0 if no transactions | [ExecutiveDashboardBuilder.cs:63] |
| 7 | total_loans | COUNT of loan_accounts rows | [ExecutiveDashboardBuilder.cs:66] |
| 8 | total_loan_balance | SUM of loan_accounts.current_balance | [ExecutiveDashboardBuilder.cs:69-73] |
| 9 | total_branch_visits | COUNT of branch_visits rows; 0 if null | [ExecutiveDashboardBuilder.cs:76-80] |

### Guard Clause

If `customers`, `accounts`, OR `loan_accounts` is null or empty, produce a 0-row DataFrame with columns `[metric_name, metric_value, as_of]`. This fires on weekends when these tables have no data.

- Evidence: [ExecutiveDashboardBuilder.cs:22-28]
- Note: `transactions` and `branch_visits` being empty does NOT trigger the guard.

## 5. SQL Design

No SQL Transformation module is used. All business logic resides in the External module.

The External module performs:
1. Guard clause check on customers, accounts, loan_accounts
2. as_of resolution (first customer row, fallback to first transaction row)
3. Metric computation using LINQ aggregate operations
4. Vertical pivot into 9-row output DataFrame

## 6. V2 Job Config JSON

```json
{
  "jobName": "ExecutiveDashboardV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "transactions",
      "schema": "datalake",
      "table": "transactions",
      "columns": ["transaction_id", "account_id", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "current_balance"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "loan_accounts",
      "schema": "datalake",
      "table": "loan_accounts",
      "columns": ["loan_id", "current_balance"]
    },
    {
      "type": "DataSourcing",
      "resultName": "branch_visits",
      "schema": "datalake",
      "table": "branch_visits",
      "columns": ["visit_id"]
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.ExecutiveDashboardV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/executive_dashboard.csv",
      "includeHeader": true,
      "trailerFormat": "SUMMARY|{row_count}|{date}|{timestamp}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Changes from V1 Config

| Change | V1 | V2 | Rationale |
|--------|----|----|-----------|
| jobName | ExecutiveDashboard | ExecutiveDashboardV2 | V2 naming convention |
| outputFile | Output/curated/... | Output/double_secret_curated/... | V2 output path |
| branches DataSourcing | Present | **Removed** | AP1: sourced but never used |
| segments DataSourcing | Present | **Removed** | AP1: sourced but never used |
| transactions columns | transaction_id, account_id, txn_type, amount | transaction_id, account_id, amount | AP4: txn_type unused |
| accounts columns | account_id, customer_id, account_type, account_status, current_balance | account_id, current_balance | AP4: customer_id, account_type, account_status unused |
| customers columns | id, first_name, last_name | id | AP4: first_name, last_name unused |
| loan_accounts columns | loan_id, customer_id, loan_type, current_balance | loan_id, current_balance | AP4: customer_id, loan_type unused |
| branch_visits columns | visit_id, customer_id, branch_id, visit_purpose | visit_id | AP4: customer_id, branch_id, visit_purpose unused |
| External typeName | ExecutiveDashboardBuilder | ExecutiveDashboardV2Processor | V2 naming convention |

## 7. Writer Config

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | CsvFileWriter | YES |
| source | output | YES |
| outputFile | Output/double_secret_curated/executive_dashboard.csv | Path changed per V2 convention |
| includeHeader | true | YES |
| trailerFormat | SUMMARY\|{row_count}\|{date}\|{timestamp} | YES |
| writeMode | Overwrite | YES — W9: V1 uses Overwrite, prior days' data is lost on each run |
| lineEnding | LF | YES |

## 8. Proofmark Config Design

### Reader & CSV Settings

- **Reader:** `csv` (output is CSV)
- **header_rows:** `1` (includeHeader is true)
- **trailer_rows:** `1` (trailerFormat present + writeMode is Overwrite = single trailer at file end)

### Column Overrides

**Starting position:** Zero exclusions, zero fuzzy. Evaluate each column:

| Column | Treatment | Rationale |
|--------|-----------|-----------|
| metric_name | STRICT | Fixed deterministic strings |
| metric_value | STRICT | Deterministic decimal computation with banker's rounding. V1 uses `decimal` throughout (no double epsilon). Values should be identical. |
| as_of | STRICT | Deterministic — derived from source data, not execution time |

### Non-Deterministic Considerations

The trailer line contains `{timestamp}` which is non-deterministic (UTC now at execution time). However, `trailer_rows: 1` strips the trailer from comparison entirely, so no column exclusion is needed for the timestamp. The trailer is not compared.

### Proofmark Config YAML

```yaml
comparison_target: "executive_dashboard"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

No column exclusions or fuzzy overrides required. All data columns are deterministic and use `decimal` arithmetic.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Guard clause on customers + accounts + loans | Section 4 (Guard Clause), Section 10 (External Module) | External module checks all three for null/empty before computing |
| BR-2: branches and segments sourced but unused | Section 3 (AP1) | Removed from V2 DataSourcing config |
| BR-3: as_of from first customer row, fallback to transaction | Section 4 (Output Schema), Section 10 (External Module) | External module implements as_of resolution logic |
| BR-4: 9 metrics in fixed order | Section 4 (Metric Definitions) | External module produces metrics in same fixed order |
| BR-5: Banker's rounding to 2 decimals | Section 3 (W5), Section 10 (External Module) | `Math.Round(value, 2, MidpointRounding.ToEven)` with explicit enum |
| BR-6: total_customers/total_accounts are row counts (not distinct) | Section 4 (Metric Definitions) | `.Count` on DataFrame, matching V1 |
| BR-7: total_balance sums ALL account balances (no filter) | Section 4 (Metric Definitions) | LINQ Sum on all account rows |
| BR-8: avg_txn_amount = total/count, 0 if no txns | Section 4 (Metric Definitions) | Ternary with > 0 check |
| BR-9: total_branch_visits = count, 0 if null | Section 4 (Metric Definitions) | Null check with 0 default |
| BR-10: Trailer timestamp is non-deterministic | Section 8 (Proofmark Config) | trailer_rows: 1 strips trailer from comparison |
| W5: Banker's rounding | Section 3 (Anti-Pattern Analysis) | Explicitly specify MidpointRounding.ToEven |
| W9: Overwrite mode loses prior days | Section 3 (Anti-Pattern Analysis), Section 7 | Reproduce Overwrite mode, document behavior |
| AP1: Dead-end sourcing (branches, segments) | Section 3 (Anti-Pattern Analysis), Section 6 | Removed from V2 config |
| AP4: Unused columns | Section 3 (Anti-Pattern Analysis), Section 6 | Trimmed to only used columns |
| AP6: Row-by-row iteration | Section 3 (Anti-Pattern Analysis), Section 10 | Replace foreach loops with LINQ .Sum() |

## 10. External Module Design

### File

`ExternalModules/ExecutiveDashboardV2Processor.cs`

### Class

`ExternalModules.ExecutiveDashboardV2Processor : IExternalStep`

### Responsibility

The External module is responsible for:
1. Guard clause evaluation
2. as_of date resolution
3. Metric computation (9 metrics)
4. Building the vertical output DataFrame

### Interface

```csharp
public Dictionary<string, object> Execute(Dictionary<string, object> sharedState)
```

### Input DataFrames (from shared state)

| Key | Required for Guard | Columns Used |
|-----|-------------------|--------------|
| customers | YES | as_of (from DataSourcing injection) |
| accounts | YES | current_balance |
| transactions | NO | amount, as_of |
| loan_accounts | YES | current_balance |
| branch_visits | NO | (count only) |

### Algorithm

```
1. Read DataFrames from shared state (customers, accounts, transactions, loan_accounts, branch_visits)
2. GUARD: If customers is null/empty OR accounts is null/empty OR loan_accounts is null/empty:
     → Set sharedState["output"] = empty DataFrame with columns [metric_name, metric_value, as_of]
     → Return
3. RESOLVE as_of:
     → as_of = customers.Rows[0]["as_of"]
     → If as_of is null AND transactions is non-null and non-empty:
         → as_of = transactions.Rows[0]["as_of"]
4. COMPUTE METRICS (using LINQ, not foreach):
     → total_customers = (decimal)customers.Count
     → total_accounts = (decimal)accounts.Count
     → total_balance = accounts.Rows.Sum(r => Convert.ToDecimal(r["current_balance"]))
     → total_transactions = transactions?.Count ?? 0 (cast to decimal)
     → total_txn_amount = transactions?.Rows.Sum(r => Convert.ToDecimal(r["amount"])) ?? 0m
     → avg_txn_amount = total_transactions > 0 ? total_txn_amount / total_transactions : 0m
     → total_loans = (decimal)loan_accounts.Count
     → total_loan_balance = loan_accounts.Rows.Sum(r => Convert.ToDecimal(r["current_balance"]))
     → total_branch_visits = branchVisits?.Count ?? 0 (cast to decimal)
5. ROUND all 9 values: Math.Round(value, 2, MidpointRounding.ToEven)
     // W5: Banker's rounding — V1 uses default MidpointRounding.ToEven. Replicated explicitly.
6. BUILD OUTPUT: Create 9 Row objects in fixed order with columns [metric_name, metric_value, as_of]
7. Set sharedState["output"] = new DataFrame(outputRows, outputColumns)
8. Return sharedState
```

### Anti-Pattern Remediation in External Module

| Anti-Pattern | V1 Code | V2 Code |
|-------------|---------|---------|
| AP6 (foreach loops) | `foreach (var row in accounts.Rows) { totalBalance += ... }` | `accounts.Rows.Sum(r => Convert.ToDecimal(r["current_balance"]))` |
| W5 (implicit banker's rounding) | `Math.Round(value, 2)` | `Math.Round(value, 2, MidpointRounding.ToEven)` — explicit enum, same behavior |
| AP1/AP4 (unused data) | Receives branches, segments, unused columns | Does not receive branches/segments; only uses columns that are sourced |

### Output DataFrame

- Name: `output` (stored in shared state under key "output")
- Columns: `[metric_name, metric_value, as_of]`
- Rows: 9 (one per metric) on weekdays; 0 on weekends (guard clause)

### Edge Cases

| Scenario | Behavior | Evidence |
|----------|----------|---------|
| Weekend (no customers/accounts/loans) | Guard fires → 0-row output → CSV with header only + trailer | BR-1, [ExecutiveDashboardBuilder.cs:22-28] |
| No transactions (but customers/accounts/loans exist) | total_transactions = 0, total_txn_amount = 0, avg_txn_amount = 0 | BR-8, [ExecutiveDashboardBuilder.cs:53-63] |
| No branch visits | total_branch_visits = 0 | BR-9, [ExecutiveDashboardBuilder.cs:76-80] |
| Customer as_of is null | Fallback to first transaction row's as_of | BR-3, [ExecutiveDashboardBuilder.cs:31-35] |
| Multi-day effective date range | Counts include duplicates across dates (row count, not distinct) | BR-6, BRD edge case note |
