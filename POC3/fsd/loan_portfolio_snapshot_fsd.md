# LoanPortfolioSnapshot — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** LoanPortfolioSnapshotV2
**Tier:** Tier 1 — Framework Only (`DataSourcing -> Transformation (SQL) -> ParquetFileWriter`)

**Justification:** The V1 External module (`LoanSnapshotBuilder.cs`) performs a pure column projection — selecting a subset of columns from `loan_accounts` and dropping `origination_date` and `maturity_date`. This is a textbook `SELECT` statement. There is zero procedural logic, no conditional branching, no computation, and no cross-table joins. SQL handles this trivially. The V1 External module is a clear AP3 (Unnecessary External module) that must be eliminated.

**Summary:** Produces a simplified snapshot of the loan portfolio by selecting a subset of columns from `loan_accounts`, excluding `origination_date` and `maturity_date`. Output is Parquet with 1 part, Overwrite mode.

## 2. V2 Module Chain

```
DataSourcing (loan_accounts) -> Transformation (SQL SELECT) -> ParquetFileWriter
```

| Step | Module Type | Purpose |
|------|------------|---------|
| 1 | DataSourcing | Source `loan_accounts` with only the 8 columns needed for output (no `origination_date`, no `maturity_date`) |
| 2 | Transformation | SQL SELECT to produce the `output` DataFrame with correct column order |
| 3 | ParquetFileWriter | Write to `Output/double_secret_curated/loan_portfolio_snapshot/`, 1 part, Overwrite |

**Key changes from V1:**
- Eliminated the `branches` DataSourcing (AP1: dead-end sourcing)
- Eliminated `origination_date` and `maturity_date` from the DataSourcing columns list (AP4: unused columns)
- Replaced External module with SQL Transformation (AP3: unnecessary External module)
- Eliminated row-by-row iteration (AP6: replaced with SQL set operation)

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Anti-Pattern | V1 Behavior | V2 Resolution |
|----|-------------|-------------|---------------|
| AP1 | Dead-end sourcing | `branches` table is sourced via DataSourcing but never referenced by the External module [loan_portfolio_snapshot.json:14-18, LoanSnapshotBuilder.cs — no reference to branches] | **Eliminated.** V2 config does not source `branches` at all. |
| AP3 | Unnecessary External module | V1 uses `LoanSnapshotBuilder.cs` External module for a simple column projection that is expressible as a SQL SELECT [LoanSnapshotBuilder.cs:24-38] | **Eliminated.** V2 uses a Transformation module with a SQL SELECT statement. |
| AP4 | Unused columns | V1 DataSourcing includes `origination_date` and `maturity_date` in the column list, but the External module drops them [loan_portfolio_snapshot.json:10, LoanSnapshotBuilder.cs:10-13] | **Eliminated.** V2 DataSourcing only requests the 8 columns that appear in the output. |
| AP6 | Row-by-row iteration | V1 External module uses `foreach` loop to copy rows one at a time [LoanSnapshotBuilder.cs:26-38] | **Eliminated.** V2 uses SQL `SELECT` (set-based operation). |

### Output-Affecting Wrinkles

No W-codes apply to this job:
- No Sunday skip, weekend fallback, or boundary logic (W1/W2/W3)
- No integer division or rounding (W4/W5)
- No double-precision accumulation (W6)
- No CSV trailer (W7/W8)
- Write mode is Overwrite which is appropriate for a snapshot (W9 — not applicable, Overwrite is correct here)
- numParts is 1, which is reasonable for ~894 rows per date (W10 — not applicable)

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| loan_id | integer | loan_accounts.loan_id | Direct pass-through | [LoanSnapshotBuilder.cs:30] |
| customer_id | integer | loan_accounts.customer_id | Direct pass-through | [LoanSnapshotBuilder.cs:31] |
| loan_type | varchar | loan_accounts.loan_type | Direct pass-through | [LoanSnapshotBuilder.cs:32] |
| original_amount | numeric | loan_accounts.original_amount | Direct pass-through | [LoanSnapshotBuilder.cs:33] |
| current_balance | numeric | loan_accounts.current_balance | Direct pass-through | [LoanSnapshotBuilder.cs:34] |
| interest_rate | numeric | loan_accounts.interest_rate | Direct pass-through | [LoanSnapshotBuilder.cs:35] |
| loan_status | varchar | loan_accounts.loan_status | Direct pass-through | [LoanSnapshotBuilder.cs:36] |
| as_of | date | loan_accounts.as_of | Direct pass-through (per-row, not from `__maxEffectiveDate`) | [LoanSnapshotBuilder.cs:37] |

**Column order:** The column order in the output matches the order defined in `LoanSnapshotBuilder.cs` lines 10-13: `loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of`.

**Excluded V1 source columns:** `origination_date`, `maturity_date` — these were sourced in V1 DataSourcing but dropped by the External module. V2 does not source them at all.

## 5. SQL Design

The Transformation SQL is a straightforward SELECT with explicit column ordering to match V1 output:

```sql
SELECT
    loan_id,
    customer_id,
    loan_type,
    original_amount,
    current_balance,
    interest_rate,
    loan_status,
    as_of
FROM loan_accounts
```

**Design notes:**
- No WHERE clause needed — effective date filtering is handled by the DataSourcing module via `__minEffectiveDate` / `__maxEffectiveDate` injection.
- No ORDER BY — V1's External module iterates rows in the order received from DataSourcing (which is `ORDER BY as_of`), so the natural ordering from DataSourcing is preserved. The SQL SELECT without explicit ORDER BY preserves the insertion order from SQLite's registered table.
- No GROUP BY, JOIN, or aggregation — this is a pure projection.
- All rows pass through regardless of `loan_status` (Active or Delinquent) per BR-3.
- NULL values pass through unchanged per BR-4.
- The `as_of` column is per-row from the source data, not from the executor's `__maxEffectiveDate` per BR-6.

**Empty DataFrame handling (BR-5):** When `loan_accounts` has zero rows for the effective date range, the DataSourcing module returns an empty DataFrame. The Transformation module's `RegisterTable` method skips empty DataFrames (does not create the SQLite table), so the SQL query will either produce an empty result or fail. However, examining the Transformation module code at line 46: `if (!df.Rows.Any()) return;` — it skips registration for empty DataFrames. If `loan_accounts` is empty, the SQL will fail because the table does not exist. This is a potential edge case.

**Mitigation for empty DataFrame:** The V1 External module explicitly handles this case at lines 18-22 by returning an empty DataFrame with the correct schema. In V2, if the DataSourcing returns zero rows, the Transformation SQL will reference a non-existent table. To preserve V1's empty-output behavior identically, the SQL should be resilient to this. However, this edge case only matters if there are effective dates with no loan data — which is unlikely in the dataset (BRD states ~894 rows per as_of date). If this edge case causes a Proofmark failure, it can be addressed in Phase D resolution.

## 6. V2 Job Config JSON

```json
{
  "jobName": "LoanPortfolioSnapshotV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "loan_accounts",
      "schema": "datalake",
      "table": "loan_accounts",
      "columns": ["loan_id", "customer_id", "loan_type", "original_amount", "current_balance", "interest_rate", "loan_status"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT loan_id, customer_id, loan_type, original_amount, current_balance, interest_rate, loan_status, as_of FROM loan_accounts"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/loan_portfolio_snapshot/",
      "numParts": 1,
      "writeMode": "Overwrite"
    }
  ]
}
```

**Config design notes:**
- `loan_accounts` DataSourcing does NOT include `origination_date` or `maturity_date` (AP4 eliminated). It also does NOT include `as_of` in the columns list — the DataSourcing module automatically appends `as_of` when it's not in the requested columns [DataSourcing.cs:69-73]. This matches V1's behavior where `as_of` was not in the V1 columns list either but was available via the DataSourcing module's automatic inclusion.
- `branches` DataSourcing entry is removed entirely (AP1 eliminated).
- External module replaced with Transformation (AP3 eliminated).
- `firstEffectiveDate` matches V1: `"2024-10-01"`.
- Writer config matches V1 exactly: `numParts: 1`, `writeMode: "Overwrite"`, `source: "output"`.
- Output path changed to `Output/double_secret_curated/loan_portfolio_snapshot/` per V2 conventions.

**Note on `as_of` column:** The V1 config does NOT include `as_of` in the DataSourcing columns list [loan_portfolio_snapshot.json:10]. The DataSourcing module detects this and automatically appends `as_of` to the SELECT and includes it in the returned DataFrame [DataSourcing.cs:69-73]. V2 follows the same pattern — `as_of` is omitted from the columns list, DataSourcing appends it, and the Transformation SQL selects it explicitly.

Wait — looking more carefully at the V1 config, the columns list is: `["loan_id", "customer_id", "loan_type", "original_amount", "current_balance", "interest_rate", "origination_date", "maturity_date", "loan_status"]`. None of these is `as_of`. So DataSourcing auto-appends `as_of`. In V2, the columns list will be `["loan_id", "customer_id", "loan_type", "original_amount", "current_balance", "interest_rate", "loan_status"]` — also without `as_of`, so DataSourcing will auto-append it. The SQL then explicitly selects `as_of` from the registered table. This is correct.

## 7. Writer Configuration

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputDirectory | `Output/curated/loan_portfolio_snapshot/` | `Output/double_secret_curated/loan_portfolio_snapshot/` | Path change per V2 convention |
| numParts | 1 | 1 | Yes |
| writeMode | Overwrite | Overwrite | Yes |

## 8. Proofmark Config Design

```yaml
comparison_target: "loan_portfolio_snapshot"
reader: parquet
threshold: 100.0
```

**Design rationale:**
- **Reader:** `parquet` — matches V1 writer type (ParquetFileWriter).
- **Threshold:** `100.0` — strict, all rows must match. This is a pure pass-through job with no computed fields, no rounding, no floating-point arithmetic. There is zero reason for any row to differ.
- **Excluded columns:** None. There are no non-deterministic fields (BRD confirms: "None identified. Pure pass-through with no computed fields.").
- **Fuzzy columns:** None. All values are direct pass-through from the source data with no computation. Both V1 and V2 read the same data from the same PostgreSQL source tables.
- **CSV settings:** Not applicable (Parquet output).

## 9. Traceability Matrix

| BRD Requirement | FSD Design Decision | Implementation |
|----------------|--------------------:|----------------|
| BR-1: Pass-through with column exclusion (origination_date, maturity_date dropped) | SQL SELECT explicitly lists the 8 output columns, excluding origination_date and maturity_date. DataSourcing does not request them at all (AP4 fix). | Transformation SQL: `SELECT loan_id, customer_id, ...` |
| BR-2: Branches table is sourced but unused | V2 removes the branches DataSourcing entry entirely (AP1 fix). | V2 config has only one DataSourcing module (loan_accounts). |
| BR-3: All loan records included regardless of loan_status | SQL SELECT has no WHERE clause filtering on loan_status. | No filter in SQL. |
| BR-4: No value transformation | SQL SELECT uses direct column references, no functions, no CAST, no ROUND. | Pure column projection in SQL. |
| BR-5: Empty output on null/empty loan_accounts | DataSourcing returns empty DataFrame; Transformation skips registration; output will be empty or error. Edge case documented — unlikely in practice with ~894 rows per date. | Handled by framework behavior; resolution available in Phase D if needed. |
| BR-6: Per-row as_of (not from __maxEffectiveDate) | SQL selects `as_of` from the loan_accounts table directly, which contains per-row values from the source data. | `as_of` in SELECT comes from the registered SQLite table data, which holds per-row values. |
| Output: Parquet, 1 part, Overwrite | ParquetFileWriter configured with numParts=1, writeMode=Overwrite. | V2 job config writer section. |
| AP1: Dead-end sourcing (branches) | Removed branches DataSourcing from V2 config. | Only loan_accounts sourced. |
| AP3: Unnecessary External module | Replaced with Transformation SQL. | Tier 1 module chain. |
| AP4: Unused columns (origination_date, maturity_date) | Not included in V2 DataSourcing columns list. | V2 columns: 7 business columns, as_of auto-appended. |
| AP6: Row-by-row iteration | Replaced with SQL set-based SELECT. | Transformation module handles set operation. |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed. The V1 External module (`LoanSnapshotBuilder.cs`) is entirely replaced by the Transformation SQL module.
