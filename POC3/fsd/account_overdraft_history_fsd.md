# AccountOverdraftHistory — Functional Specification Document

## 1. Overview

The V2 job (`AccountOverdraftHistoryV2`) produces a historical record of overdraft events enriched with account type information by joining `overdraft_events` with `accounts` on `account_id` and `as_of` date. The output is a date-partitioned Parquet dataset written to `Output/double_secret_curated/account_overdraft_history/`.

**Tier: 1 (Framework Only)** — `DataSourcing → Transformation (SQL) → ParquetFileWriter`

**Tier Justification:** All business logic is a straightforward SQL inner join with column selection and ordering. No procedural logic, no aggregation quirks, no operations outside SQL's capability. Tier 1 is the correct and simplest choice.

---

## 2. V2 Module Chain

| Step | Module Type | Config Key | Purpose |
|------|-------------|------------|---------|
| 1 | DataSourcing | `overdraft_events` | Source overdraft event records from `datalake.overdraft_events` for the effective date range |
| 2 | DataSourcing | `accounts` | Source account records from `datalake.accounts` for the effective date range |
| 3 | Transformation | `overdraft_history` | Join overdraft events to accounts, select output columns, order results |
| 4 | ParquetFileWriter | — | Write the `overdraft_history` DataFrame to Parquet part files |

### Module Configuration Details

**DataSourcing — overdraft_events:**
- Schema: `datalake`
- Table: `overdraft_events`
- Columns: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`
- No `minEffectiveDate` / `maxEffectiveDate` — injected at runtime via shared state
- Note: `event_timestamp` is deliberately excluded (see Anti-Pattern Analysis, AP4)

**DataSourcing — accounts:**
- Schema: `datalake`
- Table: `accounts`
- Columns: `account_id`, `account_type`
- No `minEffectiveDate` / `maxEffectiveDate` — injected at runtime via shared state
- Note: `customer_id`, `account_status`, `interest_rate`, `credit_limit` are deliberately excluded (see Anti-Pattern Analysis, AP4)

**Transformation — overdraft_history:**
- SQL: See Section 5

**ParquetFileWriter:**
- Source: `overdraft_history`
- Output directory: `Output/double_secret_curated/account_overdraft_history/`
- numParts: 50
- writeMode: `Overwrite`

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-Code | Applies? | Handling |
|--------|----------|----------|
| W9 | YES | V1 uses `Overwrite` mode, meaning on multi-day auto-advance runs, each day's output overwrites the previous day's. Only the final effective date's data survives. V2 reproduces this behavior exactly by using `"writeMode": "Overwrite"`. Comment in job config is not possible (JSON), but documented here: V1 uses Overwrite — prior days' data is lost on each run. |
| W10 | YES | V1 splits output into 50 Parquet part files despite the `overdraft_events` table having only ~139 total rows across 69 dates. V2 reproduces `"numParts": 50` exactly. This is excessive for the data volume but required for output equivalence. |
| W1-W8, W12 | NO | No Sunday skip, weekend fallback, boundary summaries, integer division, banker's rounding, double epsilon, trailer issues, or stale dates in this job. |

### Code-Quality Anti-Patterns (AP-codes)

| AP-Code | Applies? | V1 Problem | V2 Elimination |
|---------|----------|------------|----------------|
| AP1 | NO | No dead-end sourcing of entire tables — all sourced tables are used in the Transformation SQL. However, see AP4 for column-level dead-end sourcing. |
| AP3 | NO | V1 already uses framework-native modules (DataSourcing + Transformation + ParquetFileWriter). No unnecessary External module. |
| AP4 | YES | V1 sources `event_timestamp` from `overdraft_events` and `customer_id`, `account_status`, `interest_rate`, `credit_limit` from `accounts`, none of which appear in the Transformation SQL SELECT. V2 eliminates this by sourcing only the columns actually used: `overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived` from `overdraft_events` and `account_id`, `account_type` from `accounts`. |
| AP2 | NO | No cross-job duplication identified within this job's scope. |
| AP5 | NO | No asymmetric NULL handling; the INNER JOIN naturally excludes unmatched rows. |
| AP6 | NO | No row-by-row iteration; V1 uses SQL. |
| AP7 | NO | No magic values or hardcoded thresholds. |
| AP8 | NO | V1 SQL is straightforward — no unused CTEs or window functions. |
| AP9 | NO | Job name accurately describes what the job produces. |
| AP10 | NO | V1 relies on framework-injected effective dates via shared state, not manual date filtering. |

---

## 4. Output Schema

| Column | Source Table | Source Column | Transformation | Evidence |
|--------|-------------|---------------|----------------|----------|
| overdraft_id | overdraft_events | overdraft_id | Direct pass-through | [BRD:BR-1, account_overdraft_history.json:22] |
| account_id | overdraft_events | account_id | Direct pass-through (also used as join key) | [BRD:BR-1, account_overdraft_history.json:22] |
| customer_id | overdraft_events | customer_id | Direct pass-through | [BRD:BR-1, account_overdraft_history.json:22] |
| account_type | accounts | account_type | Direct pass-through via INNER JOIN on account_id + as_of | [BRD:BR-1, BRD:BR-5, account_overdraft_history.json:22] |
| overdraft_amount | overdraft_events | overdraft_amount | Direct pass-through | [BRD:BR-1, account_overdraft_history.json:22] |
| fee_amount | overdraft_events | fee_amount | Direct pass-through | [BRD:BR-1, account_overdraft_history.json:22] |
| fee_waived | overdraft_events | fee_waived | Direct pass-through | [BRD:BR-1, account_overdraft_history.json:22] |
| as_of | overdraft_events | as_of | Direct pass-through (framework-appended column) | [BRD:BR-1, BRD:BR-3, account_overdraft_history.json:22] |

**Column count: 8**

---

## 5. SQL Design

```sql
SELECT
    oe.overdraft_id,
    oe.account_id,
    oe.customer_id,
    a.account_type,
    oe.overdraft_amount,
    oe.fee_amount,
    oe.fee_waived,
    oe.as_of
FROM overdraft_events oe
JOIN accounts a
    ON oe.account_id = a.account_id
    AND oe.as_of = a.as_of
ORDER BY oe.as_of, oe.overdraft_id
```

**SQL Design Notes:**
- INNER JOIN on `account_id` AND `as_of` — same-day snapshot join (BR-1, BR-2)
- Unmatched overdraft events (no corresponding account record for the same as_of date) are excluded (BR-2, EC-1)
- Results ordered by `as_of` ascending, then `overdraft_id` ascending (BR-3)
- Only `account_type` is selected from the `accounts` table; all other accounts columns are excluded (BR-5, AP4 elimination)
- `event_timestamp` is not sourced and not selected (BR-6, AP4 elimination)
- The `as_of` column is automatically appended by the DataSourcing module and is available in both tables for the join and in the output

---

## 6. V2 Job Config

```json
{
  "jobName": "AccountOverdraftHistoryV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "overdraft_events",
      "schema": "datalake",
      "table": "overdraft_events",
      "columns": ["overdraft_id", "account_id", "customer_id", "overdraft_amount", "fee_amount", "fee_waived"]
    },
    {
      "type": "DataSourcing",
      "resultName": "accounts",
      "schema": "datalake",
      "table": "accounts",
      "columns": ["account_id", "account_type"]
    },
    {
      "type": "Transformation",
      "resultName": "overdraft_history",
      "sql": "SELECT oe.overdraft_id, oe.account_id, oe.customer_id, a.account_type, oe.overdraft_amount, oe.fee_amount, oe.fee_waived, oe.as_of FROM overdraft_events oe JOIN accounts a ON oe.account_id = a.account_id AND oe.as_of = a.as_of ORDER BY oe.as_of, oe.overdraft_id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "overdraft_history",
      "outputDirectory": "Output/double_secret_curated/account_overdraft_history/",
      "numParts": 50,
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
| source | `overdraft_history` | `overdraft_history` | YES |
| outputDirectory | `Output/curated/account_overdraft_history/` | `Output/double_secret_curated/account_overdraft_history/` | Path change only (required) |
| numParts | 50 | 50 | YES |
| writeMode | Overwrite | Overwrite | YES |

The writer configuration matches V1 exactly. Only the output directory changes from `Output/curated/` to `Output/double_secret_curated/` as required by the V2 convention.

---

## 8. Proofmark Config Design

### Excluded Columns
**None.**

No columns are non-deterministic. All output values are derived deterministically from source data filtered by the effective date range. There are no timestamps, UUIDs, random values, or execution-time-dependent fields in the output.

### Fuzzy Columns
**None.**

All columns are direct pass-throughs with no rounding, floating-point accumulation, or precision-sensitive computation. Exact match comparison is appropriate for every column.

### Rationale
The BRD explicitly states: "None identified. All output fields are deterministic based on source data and effective date range." (BRD: Non-Deterministic Fields section). Starting from the default of zero exclusions and zero fuzzy, there is no evidence to add any.

### Proofmark Config

```yaml
comparison_target: "account_overdraft_history"
reader: parquet
threshold: 100.0
```

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|-------------|-----------------|----------|
| Tier 1 module chain (no External) | All logic is SQL-expressible | V1 already uses DataSourcing + Transformation + ParquetFileWriter; no External module in V1 |
| INNER JOIN on account_id + as_of | BR-1 | [account_overdraft_history.json:22] `JOIN accounts a ON oe.account_id = a.account_id AND oe.as_of = a.as_of` |
| Unmatched events excluded | BR-2 | [account_overdraft_history.json:22] Uses `JOIN` (not `LEFT JOIN`) |
| ORDER BY as_of, overdraft_id | BR-3 | [account_overdraft_history.json:22] `ORDER BY oe.as_of, oe.overdraft_id` |
| Runtime date injection (no hardcoded dates) | BR-4 | [account_overdraft_history.json:4-18] No date fields; [Architecture.md:44] executor injects dates |
| Only account_type sourced from accounts | BR-5 + AP4 | [account_overdraft_history.json:22] SQL only references `a.account_type`; V2 eliminates unused columns |
| event_timestamp not sourced | BR-6 + AP4 | [account_overdraft_history.json:22] SQL SELECT does not include `event_timestamp`; V2 eliminates unused column |
| numParts: 50 | W10 | [account_overdraft_history.json:28] `"numParts": 50`; preserved for output equivalence |
| writeMode: Overwrite | W9 | [account_overdraft_history.json:29] `"writeMode": "Overwrite"`; preserved for output equivalence |
| No Proofmark exclusions/fuzzy | BRD: Non-Deterministic Fields = None | All fields are deterministic pass-throughs |
| 8-column output schema | BRD: Output Schema | All 8 columns traced to V1 SQL SELECT list |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 job. No External module is needed. All business logic is expressed in the Transformation SQL.
