# QuarterlyExecutiveKpis -- Functional Specification Document

## 1. Job Summary

The `quarterly_executive_kpis` job produces 8 key performance indicators (KPIs) spanning customers, accounts, transactions, investments, and compliance events. Despite its name, it runs daily (AP9: misleading name). Each KPI is emitted as a row with `kpi_name`, `kpi_value`, and `as_of` columns. Values are rounded to 2 decimal places using banker's rounding (W5). A weekend fallback shifts `as_of` to the prior Friday (W2), though this is effectively dead code because the guard clause on empty customers fires first on weekends. Output is Parquet with Overwrite mode and 1 part file.

## 2. V2 Module Chain

**Tier: 1 -- Framework Only**

```
DataSourcing (customers) -> DataSourcing (accounts) -> DataSourcing (transactions) -> DataSourcing (investments) -> DataSourcing (compliance_events) -> Transformation (SQL) -> ParquetFileWriter
```

**Justification:** V1 uses an External module (AP3) with row-by-row iteration (AP6) for logic that is entirely expressible in SQL: counting rows, summing columns, and unpivoting results into a fixed 8-row output via UNION ALL. The weekend fallback date logic is achievable in SQLite using `strftime('%w', ...)` and `date(...)` functions. The guard clause (empty customers = 0 output rows) is naturally handled by the SQL: when the customers table is empty, COUNT yields 0, and the WHERE EXISTS guard produces no rows from the UNION ALL. No procedural code is required.

**Anti-patterns eliminated by this tier choice:**
- AP3: External module replaced with framework-native SQL Transformation
- AP6: Row-by-row `foreach` loops replaced with set-based SQL aggregation

## 3. DataSourcing Config

### 3.1 customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `id` |

**Changes from V1:**
- AP4 eliminated: V1 sources `first_name` and `last_name` but never uses them (BRD BR-10, evidence: [QuarterlyExecutiveKpiBuilder.cs:15-25] -- only `customers.Count` is used). V2 sources only `id`, which is sufficient for COUNT.

**Effective date handling:** Framework-injected via `__minEffectiveDate` / `__maxEffectiveDate` shared state keys. No manual date filtering needed.

### 3.2 accounts

| Property | Value |
|----------|-------|
| resultName | `accounts` |
| schema | `datalake` |
| table | `accounts` |
| columns | `account_id`, `current_balance` |

**Changes from V1:**
- AP4 eliminated: V1 sources `customer_id` but never uses it for joins or filtering (BRD BR-7/BR-8 -- only count and sum are computed). V2 drops `customer_id`.

### 3.3 transactions

| Property | Value |
|----------|-------|
| resultName | `transactions` |
| schema | `datalake` |
| table | `transactions` |
| columns | `transaction_id`, `amount` |

**Changes from V1:**
- AP4 eliminated: V1 sources `account_id` but never uses it. V2 drops `account_id`.

### 3.4 investments

| Property | Value |
|----------|-------|
| resultName | `investments` |
| schema | `datalake` |
| table | `investments` |
| columns | `investment_id`, `current_value` |

**Changes from V1:**
- AP4 eliminated: V1 sources `customer_id` but never uses it. V2 drops `customer_id`.

### 3.5 compliance_events

| Property | Value |
|----------|-------|
| resultName | `compliance_events` |
| schema | `datalake` |
| table | `compliance_events` |
| columns | `event_id` |

**Changes from V1:**
- AP4 eliminated: V1 sources `customer_id`, `event_type`, and `status` but never uses them for filtering or grouping (BRD BR-9, evidence: [QuarterlyExecutiveKpiBuilder.cs:76] -- `complianceEvents?.Count ?? 0`, simple count with no filtering). V2 sources only `event_id`, which is sufficient for COUNT.

## 4. Transformation SQL

**resultName:** `output`

```sql
-- V2 Transformation: quarterly_executive_kpis
-- Produces 8 KPI rows with kpi_name, kpi_value, as_of
--
-- W2: Weekend fallback to Friday. The as_of date shifts Saturday -> Friday,
--     Sunday -> Friday. In practice this is dead code: customers has no
--     weekend data, so the guard clause (empty customers = 0 rows) fires
--     first. Included for behavioral equivalence with V1.
--     [QuarterlyExecutiveKpiBuilder.cs:28-31]
--
-- W5: Banker's rounding (MidpointRounding.ToEven) via ROUND(). SQLite's
--     ROUND() uses banker's rounding by default, matching V1's Math.Round().
--     Effectively a no-op here since all source values are numeric(12,2) or
--     numeric(14,2) -- summing values with <= 2 decimal places cannot produce
--     > 2 decimal places, and counts are always integers.
--     [QuarterlyExecutiveKpiBuilder.cs:82-89]
--
-- Guard clause: V1 returns an empty DataFrame when customers is NULL or
--     empty (BRD BR-2, [QuarterlyExecutiveKpiBuilder.cs:21-25]). The
--     WHERE EXISTS subquery replicates this: if customers has no rows,
--     all 8 UNION ALL branches return 0 rows.
--
-- AP9: Misleading name -- "quarterly" but actually produces daily KPIs.
--     Cannot rename (output filename must match V1).
--     [QuarterlyExecutiveKpiBuilder.cs:33]
--
-- AP2: Duplicates logic from executive_dashboard and other summary jobs.
--     Cannot fix cross-job duplication within single-job scope.
--     [QuarterlyExecutiveKpiBuilder.cs:34]

SELECT kpi_name, ROUND(kpi_value, 2) AS kpi_value,
  CASE
    WHEN CAST(strftime('%w', target_date) AS INTEGER) = 6
      THEN date(target_date, '-1 day')
    WHEN CAST(strftime('%w', target_date) AS INTEGER) = 0
      THEN date(target_date, '-2 days')
    ELSE target_date
  END AS as_of
FROM (
  SELECT 'total_customers' AS kpi_name,
         CAST(COUNT(*) AS REAL) AS kpi_value,
         MAX(as_of) AS target_date
  FROM customers
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'total_accounts' AS kpi_name,
         CAST(COUNT(*) AS REAL) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM accounts
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'total_balance' AS kpi_name,
         COALESCE(SUM(current_balance), 0.0) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM accounts
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'total_transactions' AS kpi_name,
         CAST(COUNT(*) AS REAL) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM transactions
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'total_txn_amount' AS kpi_name,
         COALESCE(SUM(amount), 0.0) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM transactions
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'total_investments' AS kpi_name,
         CAST(COUNT(*) AS REAL) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM investments
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'total_investment_value' AS kpi_name,
         COALESCE(SUM(current_value), 0.0) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM investments
  WHERE EXISTS (SELECT 1 FROM customers)

  UNION ALL

  SELECT 'compliance_events' AS kpi_name,
         CAST(COUNT(*) AS REAL) AS kpi_value,
         (SELECT MAX(as_of) FROM customers) AS target_date
  FROM compliance_events
  WHERE EXISTS (SELECT 1 FROM customers)
)
```

### SQL Design Notes

1. **Guard clause replication:** Every UNION ALL branch includes `WHERE EXISTS (SELECT 1 FROM customers)`. When customers is empty (weekends), all branches return 0 rows, producing an empty output DataFrame -- exactly matching V1's guard clause behavior (BRD BR-2).

2. **Row ordering:** The UNION ALL produces rows in the fixed order defined by the query: total_customers, total_accounts, total_balance, total_transactions, total_txn_amount, total_investments, total_investment_value, compliance_events. This matches the V1 order (BRD BR-5, [QuarterlyExecutiveKpiBuilder.cs:79-89]).

3. **COUNT semantics:** All counts use `COUNT(*)` (row count, not distinct count), matching V1's `count++` loop iteration pattern (BRD BR-7).

4. **SUM semantics:** `SUM(current_balance)`, `SUM(amount)`, and `SUM(current_value)` match V1's accumulation via `Convert.ToDecimal(row[...])` (BRD BR-8). V1 uses `decimal` arithmetic (no W6 double-epsilon issue). COALESCE handles the edge case where the table has rows but the column sum is null (not expected with NOT NULL constraints, but defensive).

5. **Compliance events unfiltered:** `COUNT(*)` on compliance_events with no filtering on `event_type` or `status` matches V1's `complianceEvents?.Count ?? 0` behavior (BRD BR-9).

6. **Weekend fallback:** The outer CASE expression applies the W2 weekend-to-Friday date shift. `strftime('%w', date)` returns 0 for Sunday, 6 for Saturday. The `date()` function subtracts 1 or 2 days accordingly. This code path is effectively dead (customers is empty on weekends, so the WHERE EXISTS guard produces 0 rows), but it is included for behavioral equivalence.

7. **ROUND to 2 decimal places:** The outer `ROUND(kpi_value, 2)` replicates V1's `Math.Round(value, 2)` with banker's rounding (W5). SQLite's ROUND uses banker's rounding. For this job, it is effectively a no-op since all inputs already have <= 2 decimal places.

## 5. Writer Config

| Property | Value | Evidence |
|----------|-------|----------|
| type | `ParquetFileWriter` | [quarterly_executive_kpis.json:46] |
| source | `output` | [quarterly_executive_kpis.json:47] |
| outputDirectory | `Output/double_secret_curated/quarterly_executive_kpis/` | V2 output path convention |
| numParts | `1` | [quarterly_executive_kpis.json:49] |
| writeMode | `Overwrite` | [quarterly_executive_kpis.json:50] |

**Write mode implication:** Each effective date run replaces the Parquet directory entirely. During multi-day gap-fill, only the final day's KPIs persist. This matches V1 behavior (BRD: "Multi-day gap-fill: only the final day survives").

## 6. Wrinkle Replication

### W2 -- Weekend Fallback

- **V1 behavior:** If `__maxEffectiveDate` is Saturday, `as_of` is set to Friday (subtract 1 day). If Sunday, subtract 2 days. Weekday dates used as-is. [QuarterlyExecutiveKpiBuilder.cs:28-31]
- **V2 replication:** SQL CASE expression using `strftime('%w', ...)` and `date(target_date, '-N day')` in the outer SELECT. Produces identical date adjustment.
- **Note:** Effectively dead code. Customers has no weekend data in the datalake, so the guard clause (empty customers = 0 output rows) fires before the fallback can take effect (BRD Edge Case analysis). Included for behavioral equivalence.

### W5 -- Banker's Rounding

- **V1 behavior:** All KPI values rounded to 2 decimal places via `Math.Round(value, 2)`, which defaults to `MidpointRounding.ToEven` (banker's rounding). [QuarterlyExecutiveKpiBuilder.cs:82-89]
- **V2 replication:** SQLite's `ROUND(kpi_value, 2)` uses banker's rounding, matching V1.
- **Note:** Effectively a no-op for this job. Source columns are `numeric(12,2)` or `numeric(14,2)`, and counts are integers. Summing values with <= 2 decimal places cannot produce > 2 decimal places. ROUND is included for explicit behavioral equivalence.

## 7. Anti-Pattern Elimination

### AP2 -- Duplicated Logic (DOCUMENTED, NOT FIXABLE)

- **V1 problem:** Overlaps significantly with `executive_dashboard` -- both compute total_customers, total_accounts, total_balance, total_transactions, total_txn_amount. [QuarterlyExecutiveKpiBuilder.cs:34]
- **V2 action:** Cannot fix cross-job duplication within single-job scope. Documented in SQL comments and this FSD. Implementation proceeds as needed for this job's output.

### AP3 -- Unnecessary External Module (ELIMINATED)

- **V1 problem:** Uses `ExternalModules.QuarterlyExecutiveKpiBuilder` for logic that is entirely expressible in SQL: counting rows, summing values, and building 8 output rows. [quarterly_executive_kpis.json:41-44]
- **V2 action:** Replaced with Tier 1 framework chain (DataSourcing + Transformation SQL + ParquetFileWriter). All business logic is in a single SQL query using UNION ALL for the unpivot and CASE for the weekend fallback.

### AP4 -- Unused Columns (ELIMINATED)

- **V1 problem:** Sources columns never used in processing:
  - `customers.first_name`, `customers.last_name` -- only `customers.Count` is used [QuarterlyExecutiveKpiBuilder.cs:15-25] (BRD BR-10)
  - `accounts.customer_id` -- no join or filter on this column
  - `transactions.account_id` -- no join or filter on this column
  - `investments.customer_id` -- no join or filter on this column
  - `compliance_events.customer_id`, `compliance_events.event_type`, `compliance_events.status` -- only `Count` is used, no filtering [QuarterlyExecutiveKpiBuilder.cs:76] (BRD BR-9)
- **V2 action:** DataSourcing configs reduced to only the columns actually needed:
  - customers: `id` only
  - accounts: `account_id`, `current_balance`
  - transactions: `transaction_id`, `amount`
  - investments: `investment_id`, `current_value`
  - compliance_events: `event_id` only

### AP6 -- Row-by-Row Iteration (ELIMINATED)

- **V1 problem:** Uses `foreach` loops to count rows and sum values across accounts, transactions, and investments. [QuarterlyExecutiveKpiBuilder.cs:42-48, 53-59, 64-70]
- **V2 action:** Replaced with set-based SQL aggregation: `COUNT(*)` and `SUM(column)`.

### AP9 -- Misleading Name (DOCUMENTED, NOT FIXABLE)

- **V1 problem:** Job named "quarterly" but runs daily and produces daily KPIs. [QuarterlyExecutiveKpiBuilder.cs:33]
- **V2 action:** Cannot rename -- output filename must match V1 for Proofmark comparison. Documented in SQL comments and this FSD.

## 8. Proofmark Config

```yaml
comparison_target: "quarterly_executive_kpis"
reader: parquet
threshold: 100.0
```

**Justification for strict comparison with zero overrides:**

- **No non-deterministic fields:** The BRD identifies no non-deterministic fields. All output values are deterministic: `kpi_name` is a fixed string, `kpi_value` is a deterministic aggregate (count or sum), and `as_of` is derived from the effective date.
- **No floating-point concerns:** V1 uses `decimal` arithmetic (not `double`), so there are no W6 epsilon issues. Source columns are `numeric(12,2)` or `numeric(14,2)`, so sums stay within 2 decimal places. No FUZZY tolerance is needed.
- **No excluded columns:** All 3 output columns (`kpi_name`, `kpi_value`, `as_of`) are deterministic and should be compared strictly.

## 9. Open Questions

1. **Weekend fallback is dead code:** The guard clause on empty customers fires before the weekend fallback can take effect (customers has no weekend data in the datalake). The V2 SQL includes the fallback logic for behavioral equivalence, but it can never be exercised with current data. If the datalake ever adds weekend customer snapshots, the fallback would activate. No action needed -- included for completeness. (Confidence: HIGH)

2. **Misleading "quarterly" name:** The code explicitly calls this out as misleading (AP9). Cannot rename without changing output paths. (Confidence: HIGH, per [QuarterlyExecutiveKpiBuilder.cs:33])

3. **Unused compliance_events columns:** `event_type` and `status` are sourced by V1 but never used for filtering. V2 eliminates them (AP4). If future requirements add filtering, the DataSourcing config would need to be updated. (Confidence: HIGH)

4. **Overlap with executive_dashboard:** Both jobs produce similar aggregate metrics. Consolidation is outside the scope of a single-job V2 rewrite. (Confidence: MEDIUM, per [QuarterlyExecutiveKpiBuilder.cs:34])

5. **SQLite ROUND behavior:** SQLite's `ROUND()` uses banker's rounding for `.5` midpoint values, matching C#'s default `Math.Round()`. This has been verified. If a future SQLite version changes rounding behavior, the output could diverge. For this specific job, the concern is moot since no input values produce `.5` midpoints at 2 decimal places. (Confidence: HIGH)

6. **CAST to REAL for counts:** The SQL casts count values to REAL to ensure the `kpi_value` column has a consistent numeric type across all UNION ALL branches (counts are integers, sums are reals). V1 stores all kpi_values as `decimal`. The Parquet writer should produce equivalent output since both represent the same numeric values. If Proofmark reports type mismatches, the CAST may need adjustment. (Confidence: MEDIUM)

## Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1: Weekend fallback | 4 (SQL), 6 (W2) | CASE expression on strftime('%w') with date subtraction |
| BR-2: Guard on customers only | 4 (SQL) | WHERE EXISTS (SELECT 1 FROM customers) on all UNION ALL branches |
| BR-3: Misleading name (AP9) | 7 (AP9) | Documented, cannot rename |
| BR-4: Overlapping logic (AP2) | 7 (AP2) | Documented, cannot fix cross-job |
| BR-5: 8 KPIs in fixed order | 4 (SQL) | UNION ALL produces fixed ordering |
| BR-6: Banker's rounding to 2dp | 4 (SQL), 6 (W5) | SQLite ROUND(value, 2) |
| BR-7: Row counts (not distinct) | 4 (SQL) | COUNT(*) |
| BR-8: SUM of balance/amount/value | 4 (SQL) | SUM(column) |
| BR-9: Compliance events unfiltered | 4 (SQL), 3.5 | COUNT(*) with no WHERE filter on event_type/status |
| BR-10: first_name/last_name unused | 3.1, 7 (AP4) | Columns removed from DataSourcing |
| Overwrite write mode | 5 | writeMode: Overwrite |
| 1 Parquet part file | 5 | numParts: 1 |
| First effective date 2024-10-01 | Job config | firstEffectiveDate: "2024-10-01" |
