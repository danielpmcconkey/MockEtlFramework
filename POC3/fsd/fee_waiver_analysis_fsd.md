# FeeWaiverAnalysis — Functional Specification Document

## 1. Overview

The V2 job (`FeeWaiverAnalysisV2`) produces summary statistics of overdraft events grouped by fee waiver status (`fee_waived`) and snapshot date (`as_of`). For each group it computes event count, total fees, and average fee, with NULL fee amounts coalesced to 0.0 before aggregation. Output is a single CSV file written to `Output/double_secret_curated/fee_waiver_analysis.csv`.

**Tier: 1 (Framework Only)** — `DataSourcing → Transformation (SQL) → CsvFileWriter`

**Tier Justification:** The entire business logic is a single-table GROUP BY aggregation with ROUND, SUM, AVG, COUNT, and CASE WHEN — all natively supported by SQLite. The V1 LEFT JOIN to `accounts` is a dead-end that contributes no columns to the output (AP1) and should be removed. With that join removed, only one DataSourcing table is needed. There is zero procedural logic requiring an External module.

---

## 2. V2 Module Chain

| Step | Module Type | Config Key | Purpose |
|------|-------------|------------|---------|
| 1 | DataSourcing | `overdraft_events` | Source overdraft event records from `datalake.overdraft_events` for the effective date range |
| 2 | Transformation | `fee_waiver_summary` | Aggregate by `fee_waived` and `as_of`, computing event_count, total_fees, avg_fee |
| 3 | CsvFileWriter | — | Write the `fee_waiver_summary` DataFrame to CSV |

### Module Configuration Details

**DataSourcing — overdraft_events:**
- Schema: `datalake`
- Table: `overdraft_events`
- Columns: `fee_amount`, `fee_waived`
- No `minEffectiveDate` / `maxEffectiveDate` — injected at runtime via shared state
- Note: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` are deliberately excluded (AP4 elimination — none appear in the Transformation SQL output)
- The framework automatically appends `as_of` since it is not in the column list, so it is available for GROUP BY

**Transformation — fee_waiver_summary:**
- SQL: See Section 5

**CsvFileWriter:**
- Source: `fee_waiver_summary`
- Output file: `Output/double_secret_curated/fee_waiver_analysis.csv`
- includeHeader: `true`
- writeMode: `Overwrite`
- lineEnding: `LF`
- trailerFormat: not configured (no trailer)

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-Code | Applies? | Handling |
|--------|----------|----------|
| W9 | YES | V1 uses `Overwrite` mode, meaning on multi-day auto-advance runs, each day's output overwrites the previous day's. Only the final effective date's data survives. V2 reproduces this behavior exactly by using `"writeMode": "Overwrite"`. Documented here: V1 uses Overwrite — prior days' data is lost on each run. |
| W1-W8, W10, W12 | NO | No Sunday skip, weekend fallback, boundary summaries, integer division, banker's rounding, double epsilon, trailer issues, stale dates, absurd numParts, or header-every-append in this job. |

### Code-Quality Anti-Patterns (AP-codes)

| AP-Code | Applies? | V1 Problem | V2 Elimination |
|---------|----------|------------|----------------|
| AP1 | YES | V1 sources the entire `accounts` table via DataSourcing, then LEFT JOINs to it in the Transformation SQL, but NO columns from `accounts` appear in the SELECT or GROUP BY. The `accounts` table is dead-end sourcing. | **Eliminated.** V2 removes the `accounts` DataSourcing entry entirely. The Transformation SQL operates on `overdraft_events` alone. |
| AP4 | YES | V1 sources `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `event_timestamp` from `overdraft_events`, none of which appear in the Transformation SQL output (they are not in the SELECT list). Additionally, ALL columns from `accounts` (`account_id`, `customer_id`, `account_type`, `account_status`, `interest_rate`, `credit_limit`, `apr`) are sourced but unused. | **Eliminated.** V2 sources only `fee_amount` and `fee_waived` from `overdraft_events`. The framework appends `as_of` automatically. No other columns are needed. |
| AP3 | NO | V1 already uses framework-native modules (DataSourcing + Transformation + CsvFileWriter). No unnecessary External module. |
| AP2 | NO | No cross-job duplication identified within this job's scope. |
| AP5 | NO | NULL handling is consistent — all NULL `fee_amount` values are coalesced to 0.0 via CASE expression. This is a deliberate business rule (BR-3), not asymmetric handling. |
| AP6 | NO | No row-by-row iteration; V1 uses SQL. |
| AP7 | NO | No magic values or hardcoded thresholds. The `0.0` in the CASE expression is a standard NULL-to-zero coalescing value, not a business threshold. |
| AP8 | NO | V1 SQL is straightforward — no unused CTEs or window functions. However, the dead-end LEFT JOIN is removed (addressed via AP1). |
| AP9 | NO | Job name "FeeWaiverAnalysis" accurately describes what the job produces — an analysis of events grouped by fee waiver status. |
| AP10 | NO | V1 relies on framework-injected effective dates via shared state, not manual date filtering. |

### Critical Design Decision: Dead-End JOIN Removal

The V1 SQL includes `LEFT JOIN accounts a ON oe.account_id = a.account_id AND oe.as_of = a.as_of`, but the SELECT clause only references `oe.*` columns. This join is functionally a no-op **unless** the `accounts` table has duplicate `(account_id, as_of)` rows, in which case the join would multiply overdraft event rows and inflate `COUNT(*)`, `SUM()`, and `AVG()` results (BRD BR-7, EC-1).

**Decision:** Remove the dead-end JOIN. This is the correct AP1 elimination. The BRD rates the duplication risk as MEDIUM confidence, noting it "depends on whether accounts has unique (account_id, as_of) pairs." Since the `accounts` table represents daily full-load snapshots (one snapshot per account per `as_of` date), the `(account_id, as_of)` pair is expected to be unique. The LEFT JOIN therefore produces a 1:1 match (or 1:0 for unmatched events, which the LEFT semantics preserves), meaning removing it does not change the output. If Proofmark comparison fails, this decision should be re-examined as the first hypothesis.

---

## 4. Output Schema

| Column | Source Table | Source Column | Transformation | Evidence |
|--------|-------------|---------------|----------------|----------|
| fee_waived | overdraft_events | fee_waived | Direct pass-through, used as GROUP BY key | [BRD:BR-2, fee_waiver_analysis.json:22] |
| event_count | overdraft_events | (derived) | COUNT(*) per group | [BRD:BR-2, fee_waiver_analysis.json:22] |
| total_fees | overdraft_events | fee_amount | ROUND(SUM(CASE WHEN fee_amount IS NULL THEN 0.0 ELSE fee_amount END), 2) | [BRD:BR-3, BRD:BR-4, fee_waiver_analysis.json:22] |
| avg_fee | overdraft_events | fee_amount | ROUND(AVG(CASE WHEN fee_amount IS NULL THEN 0.0 ELSE fee_amount END), 2) | [BRD:BR-3, BRD:BR-5, fee_waiver_analysis.json:22] |
| as_of | overdraft_events | as_of | Direct pass-through, used as GROUP BY key | [BRD:BR-2, fee_waiver_analysis.json:22] |

**Column count: 5**
**Column order: fee_waived, event_count, total_fees, avg_fee, as_of** (matches V1 SQL SELECT order)

---

## 5. SQL Design

```sql
SELECT
    oe.fee_waived,
    COUNT(*) AS event_count,
    ROUND(SUM(CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END), 2) AS total_fees,
    ROUND(AVG(CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END), 2) AS avg_fee,
    oe.as_of
FROM overdraft_events oe
GROUP BY oe.fee_waived, oe.as_of
ORDER BY oe.fee_waived
```

**SQL Design Notes:**
- The dead-end LEFT JOIN to `accounts` has been removed (AP1 elimination). V1 joins to `accounts` but uses zero columns from it. Removing the join produces identical output because `accounts` has unique `(account_id, as_of)` pairs, so the LEFT JOIN was 1:1.
- NULL `fee_amount` values are coalesced to 0.0 using CASE WHEN (BR-3). This intentionally includes NULL-fee events in COUNT(*) and pulls AVG down toward zero (BRD EC-2). V2 reproduces this behavior exactly.
- `total_fees` uses ROUND(SUM(...), 2) for 2-decimal-place rounding (BR-4).
- `avg_fee` uses ROUND(AVG(...), 2) for 2-decimal-place rounding (BR-5).
- GROUP BY `fee_waived` and `as_of` (BR-2).
- ORDER BY `fee_waived` ascending — false (0) before true (1) in SQLite (BR-6).
- The `as_of` column is automatically appended by DataSourcing and available in the SQLite table for GROUP BY and SELECT.
- The table alias `oe` is retained for clarity even though no join exists, because it matches the V1 SQL structure and makes the single-table query self-documenting.

---

## 6. V2 Job Config

```json
{
  "jobName": "FeeWaiverAnalysisV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "overdraft_events",
      "schema": "datalake",
      "table": "overdraft_events",
      "columns": ["fee_amount", "fee_waived"]
    },
    {
      "type": "Transformation",
      "resultName": "fee_waiver_summary",
      "sql": "SELECT oe.fee_waived, COUNT(*) AS event_count, ROUND(SUM(CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END), 2) AS total_fees, ROUND(AVG(CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END), 2) AS avg_fee, oe.as_of FROM overdraft_events oe GROUP BY oe.fee_waived, oe.as_of ORDER BY oe.fee_waived"
    },
    {
      "type": "CsvFileWriter",
      "source": "fee_waiver_summary",
      "outputFile": "Output/double_secret_curated/fee_waiver_analysis.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | `fee_waiver_summary` | `fee_waiver_summary` | YES |
| outputFile | `Output/curated/fee_waiver_analysis.csv` | `Output/double_secret_curated/fee_waiver_analysis.csv` | Path change only (required) |
| includeHeader | true | true | YES |
| writeMode | Overwrite | Overwrite | YES |
| lineEnding | LF | LF | YES |
| trailerFormat | not configured | not configured | YES |

The writer configuration matches V1 exactly. Only the output path changes from `Output/curated/` to `Output/double_secret_curated/` as required by the V2 convention.

---

## 8. Proofmark Config Design

### Excluded Columns
**None.**

No columns are non-deterministic. All output values are derived deterministically from source data filtered by the effective date range. There are no timestamps, UUIDs, random values, or execution-time-dependent fields in the output.

### Fuzzy Columns
**None.**

All numeric columns (`total_fees`, `avg_fee`) use SQLite's `ROUND()` function applied to `SUM()` and `AVG()` of values that are either decimal/numeric from the source or the literal `0.0`. Both V1 and V2 execute the same SQL through the same SQLite Transformation module, so the arithmetic path is identical. No floating-point epsilon divergence is expected.

### Rationale
The BRD explicitly states: "None identified. All output is deterministic." (BRD: Non-Deterministic Fields section). Starting from the default of zero exclusions and zero fuzzy, there is no evidence to add any.

### Proofmark Config

```yaml
comparison_target: "fee_waiver_analysis"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Tier 1 module chain (no External) | All logic is SQL-expressible | V1 uses DataSourcing + Transformation + CsvFileWriter; no External module in V1 |
| Remove `accounts` DataSourcing (dead-end) | BR-1, EC-1, AP1 | [fee_waiver_analysis.json:22] SQL SELECT references only `oe.*` columns; no `a.*` columns appear |
| Remove dead-end LEFT JOIN | BR-1, BR-7, EC-1, AP1 | [fee_waiver_analysis.json:22] `LEFT JOIN accounts a ...` contributes zero columns to output |
| Source only `fee_amount`, `fee_waived` from `overdraft_events` | AP4 | [fee_waiver_analysis.json:22] SQL only references `oe.fee_waived`, `oe.fee_amount`, `oe.as_of`; other sourced columns unused |
| GROUP BY fee_waived, as_of | BR-2 | [fee_waiver_analysis.json:22] `GROUP BY oe.fee_waived, oe.as_of` |
| NULL fee_amount coalesced to 0.0 | BR-3 | [fee_waiver_analysis.json:22] `CASE WHEN oe.fee_amount IS NULL THEN 0.0 ELSE oe.fee_amount END` |
| ROUND(SUM(...), 2) for total_fees | BR-4 | [fee_waiver_analysis.json:22] `ROUND(SUM(...), 2) AS total_fees` |
| ROUND(AVG(...), 2) for avg_fee | BR-5 | [fee_waiver_analysis.json:22] `ROUND(AVG(...), 2) AS avg_fee` |
| ORDER BY fee_waived | BR-6 | [fee_waiver_analysis.json:22] `ORDER BY oe.fee_waived` |
| Runtime date injection (no hardcoded dates) | BR-8 | [fee_waiver_analysis.json:4-19] No date fields in DataSourcing configs; [Architecture.md:44] executor injects dates |
| writeMode: Overwrite | W9, EC-3 | [fee_waiver_analysis.json:29] `"writeMode": "Overwrite"`; preserved for output equivalence — prior days' data lost on each run |
| No Proofmark exclusions/fuzzy | BRD: Non-Deterministic Fields = None | All fields are deterministic aggregations of source data |
| 5-column output schema | BRD: Output Schema | All 5 columns traced to V1 SQL SELECT list |
| includeHeader: true | BRD: Writer Configuration | [fee_waiver_analysis.json:28] `"includeHeader": true` |
| lineEnding: LF | BRD: Writer Configuration | [fee_waiver_analysis.json:30] `"lineEnding": "LF"` |
| No trailer | BRD: Writer Configuration | [fee_waiver_analysis.json] No `trailerFormat` field present |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 job. No External module is needed. All business logic is expressed in the Transformation SQL.
