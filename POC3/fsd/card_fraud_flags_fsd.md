# CardFraudFlags — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** CardFraudFlagsV2
**Tier:** 1 — Framework Only (`DataSourcing → Transformation (SQL) → CsvFileWriter`)

This job identifies card transactions flagged as potentially fraudulent based on two criteria: the transaction's merchant category is classified as "High" risk AND the transaction amount exceeds $500. It outputs a detail-level record for each flagged transaction as CSV.

**Tier Justification:** The V1 External module (`CardFraudFlagsProcessor.cs`) performs a dictionary-based lookup (JOIN) and a dual-condition filter — both trivially expressible as a SQL JOIN + WHERE clause. Banker's rounding (W5) is handled by SQLite's `ROUND()` function. There is no procedural logic, no cross-date-range querying, and no I/O quirk that requires an External module. Tier 1 is sufficient.

## 2. V2 Module Chain

```
DataSourcing ("card_transactions")
  → DataSourcing ("merchant_categories")
    → Transformation (SQL JOIN + WHERE + ROUND)
      → CsvFileWriter
```

### Module 1: DataSourcing — card_transactions
- **resultName:** `card_transactions`
- **schema:** `datalake`
- **table:** `card_transactions`
- **columns:** `card_txn_id`, `card_id`, `customer_id`, `merchant_name`, `merchant_category_code`, `amount`, `txn_timestamp`
- Effective dates injected by executor via shared state (no hardcoded dates)

### Module 2: DataSourcing — merchant_categories
- **resultName:** `merchant_categories`
- **schema:** `datalake`
- **table:** `merchant_categories`
- **columns:** `mcc_code`, `risk_level`
- Effective dates injected by executor via shared state
- **Note:** `mcc_description` removed vs V1 (AP4 — unused column elimination)

### Module 3: Transformation
- **resultName:** `output`
- SQL performs: JOIN card_transactions to merchant_categories on MCC code, filter by risk_level = 'High' AND rounded amount > 500, apply Banker's rounding, rename columns

### Module 4: CsvFileWriter
- **source:** `output`
- **outputFile:** `Output/double_secret_curated/card_fraud_flags.csv`
- **includeHeader:** `true`
- **writeMode:** `Overwrite`
- **lineEnding:** `LF`
- No trailer configured (matches V1)

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| ID | Name | Applies? | V1 Evidence | V2 Disposition |
|----|------|----------|-------------|----------------|
| AP3 | Unnecessary External | YES | `CardFraudFlagsProcessor.cs` — entire logic is a dictionary lookup (JOIN) + filter, expressible in SQL | **ELIMINATED.** Replaced with Tier 1 SQL Transformation. |
| AP4 | Unused columns | YES | `card_fraud_flags.json:17` sources `mcc_description` but `CardFraudFlagsProcessor.cs:10-14` output columns do not include it | **ELIMINATED.** V2 DataSourcing for merchant_categories sources only `mcc_code` and `risk_level`. |
| AP6 | Row-by-row iteration | YES | `CardFraudFlagsProcessor.cs:42-64` uses `foreach` loop with dictionary lookup instead of a set-based operation | **ELIMINATED.** Replaced with SQL JOIN. |
| AP7 | Magic values | YES | `CardFraudFlagsProcessor.cs:50` hardcoded `500m` threshold and `"High"` string literal | **ELIMINATED.** SQL uses clearly commented named literals: `-- V1 fraud threshold: amount > 500 AND risk_level = 'High'` |
| AP1 | Dead-end sourcing | NO | Both sourced tables are used | N/A |
| AP9 | Misleading names | NO | Job name accurately describes output | N/A |
| AP10 | Over-sourcing dates | NO | V1 uses framework effective date injection correctly | N/A |

### Identified Wrinkles (Output-Affecting)

| ID | Name | Applies? | V1 Evidence | V2 Disposition |
|----|------|----------|-------------|----------------|
| W5 | Banker's rounding | YES | `CardFraudFlagsProcessor.cs:47` uses `MidpointRounding.ToEven` | **REPRODUCED.** SQLite `ROUND()` uses Banker's rounding by default. The SQL `ROUND(amount, 2)` replicates this behavior. Comment in SQL documents this choice. |
| W9 | Wrong writeMode | POSSIBLE | V1 uses Overwrite mode — for multi-day auto-advance, only the last day's output survives. This may be intentional or a bug. | **REPRODUCED.** V2 uses `Overwrite` to match V1 exactly. |
| W1-W4, W6-W8, W10, W12 | Other wrinkles | NO | No Sunday skip, weekend fallback, boundary rows, integer division, double epsilon, trailer issues, or absurd numParts in this job | N/A |

### BRD Correction: Threshold Value (RESOLVED)

The BRD originally stated a $750 threshold in BR-2 and BR-3, but the V1 source code at `CardFraudFlagsProcessor.cs:50` clearly shows `amount > 500m`. The BRD has been corrected to state $500, matching the V1 source code.

Evidence:
- `CardFraudFlagsProcessor.cs:49`: comment says `// AP7: Magic value — hardcoded $500 threshold`
- `CardFraudFlagsProcessor.cs:50`: `if (riskLevel == "High" && amount > 500m)`

## 4. Output Schema

| # | Column | Source | Transformation | Evidence |
|---|--------|--------|---------------|----------|
| 1 | card_txn_id | card_transactions.card_txn_id | Pass-through | CardFraudFlagsProcessor.cs:54 |
| 2 | card_id | card_transactions.card_id | Pass-through | CardFraudFlagsProcessor.cs:55 |
| 3 | customer_id | card_transactions.customer_id | Pass-through | CardFraudFlagsProcessor.cs:56 |
| 4 | merchant_name | card_transactions.merchant_name | Pass-through | CardFraudFlagsProcessor.cs:57 |
| 5 | mcc_code | card_transactions.merchant_category_code | Renamed from merchant_category_code | CardFraudFlagsProcessor.cs:58 |
| 6 | risk_level | merchant_categories.risk_level | Joined via mcc_code | CardFraudFlagsProcessor.cs:59 |
| 7 | amount | card_transactions.amount | ROUND(..., 2) with Banker's rounding (W5) | CardFraudFlagsProcessor.cs:47,60 |
| 8 | txn_timestamp | card_transactions.txn_timestamp | Pass-through | CardFraudFlagsProcessor.cs:61 |
| 9 | as_of | card_transactions.as_of | Pass-through (injected by DataSourcing) | CardFraudFlagsProcessor.cs:62 |

## 5. SQL Design

```sql
-- CardFraudFlagsV2: Identify potentially fraudulent card transactions
-- Business rule: flag transactions where merchant risk_level = 'High' AND amount > 500
-- W5: SQLite ROUND() uses Banker's rounding (MidpointRounding.ToEven), matching V1 behavior

SELECT
    ct.card_txn_id,
    ct.card_id,
    ct.customer_id,
    ct.merchant_name,
    ct.merchant_category_code AS mcc_code,        -- Renamed to match V1 output schema
    mc.risk_level,
    ROUND(CAST(ct.amount AS REAL), 2) AS amount,  -- W5: Banker's rounding to 2 decimal places
    ct.txn_timestamp,
    ct.as_of
FROM card_transactions ct
INNER JOIN (SELECT mcc_code, risk_level FROM merchant_categories GROUP BY mcc_code) mc
    ON ct.merchant_category_code = mc.mcc_code
WHERE mc.risk_level = 'High'                       -- V1 fraud criterion 1: high-risk merchant
  AND ROUND(CAST(ct.amount AS REAL), 2) > 500      -- V1 fraud criterion 2: amount > $500 (after rounding)
```

### SQL Design Notes

1. **INNER JOIN vs dictionary lookup:** V1 builds a dictionary from `merchant_categories` and looks up each transaction's MCC code. Transactions with no matching MCC get an empty string for risk_level and are excluded (they can never equal "High"). An INNER JOIN produces identical behavior — unmatched transactions are excluded from the result set entirely. This is semantically equivalent.

2. **Dictionary overwrite behavior (Edge Case 4 from BRD):** V1 iterates all `merchant_categories` rows and overwrites the dictionary entry for duplicate `mcc_code` values across `as_of` dates. The last-seen `risk_level` wins. The SQL INNER JOIN will produce a cross-product for duplicate MCC codes across dates, which could yield different results if an MCC code has different risk levels on different `as_of` dates. However, per BR-7, the `merchant_categories` data is reference data where `risk_level` is consistent for a given `mcc_code` across all `as_of` dates (confirmed by the BRD's database query evidence). If this assumption holds, the INNER JOIN produces identical results. If it doesn't hold during Proofmark validation, this would need a Tier 2 escalation — but the evidence strongly supports Tier 1.

3. **ROUND before comparison:** V1 rounds the amount (line 47) before applying the `> 500m` comparison (line 50). The SQL applies `ROUND()` in both the SELECT and WHERE clauses to match this order of operations exactly.

4. **CAST to REAL:** SQLite stores numbers loaded via the Transformation module. The `CAST(ct.amount AS REAL)` ensures the ROUND function operates on a numeric value regardless of how the amount was loaded into SQLite.

5. **No date filter in SQL:** V1's External module does not filter by date within its logic (BR-6). The SQL likewise has no date-related WHERE clause — all rows from the DataSourcing effective date range are evaluated. The DataSourcing module handles date filtering at the source level.

## 6. V2 Job Config JSON

```json
{
  "jobName": "CardFraudFlagsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["card_txn_id", "card_id", "customer_id", "merchant_name", "merchant_category_code", "amount", "txn_timestamp"]
    },
    {
      "type": "DataSourcing",
      "resultName": "merchant_categories",
      "schema": "datalake",
      "table": "merchant_categories",
      "columns": ["mcc_code", "risk_level"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT ct.card_txn_id, ct.card_id, ct.customer_id, ct.merchant_name, ct.merchant_category_code AS mcc_code, mc.risk_level, ROUND(CAST(ct.amount AS REAL), 2) AS amount, ct.txn_timestamp, ct.as_of FROM card_transactions ct INNER JOIN (SELECT mcc_code, risk_level FROM merchant_categories GROUP BY mcc_code) mc ON ct.merchant_category_code = mc.mcc_code WHERE mc.risk_level = 'High' AND ROUND(CAST(ct.amount AS REAL), 2) > 500"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/card_fraud_flags.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

### Config Changes from V1

| Aspect | V1 | V2 | Reason |
|--------|----|----|--------|
| jobName | `CardFraudFlags` | `CardFraudFlagsV2` | V2 naming convention |
| merchant_categories columns | `mcc_code, mcc_description, risk_level` | `mcc_code, risk_level` | AP4: removed unused `mcc_description` |
| Module 3 | External (CardFraudFlagsProcessor) | Transformation (SQL) | AP3: replaced unnecessary External with SQL |
| Output path | `Output/curated/card_fraud_flags.csv` | `Output/double_secret_curated/card_fraud_flags.csv` | V2 output directory |
| Writer config | Identical | Identical | All writer params preserved (includeHeader, writeMode, lineEnding, no trailer) |

## 7. Writer Configuration

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | CsvFileWriter | YES |
| source | output | YES |
| outputFile | Output/double_secret_curated/card_fraud_flags.csv | Path updated per V2 convention |
| includeHeader | true | YES |
| writeMode | Overwrite | YES |
| lineEnding | LF | YES |
| trailerFormat | (not configured) | YES — V1 has no trailer |

## 8. Proofmark Config Design

```yaml
comparison_target: "card_fraud_flags"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Proofmark Design Rationale

- **reader: csv** — V1 output is a CSV file via CsvFileWriter
- **header_rows: 1** — V1 has `includeHeader: true`
- **trailer_rows: 0** — V1 has no `trailerFormat` configured
- **threshold: 100.0** — All fields are deterministic (BRD confirms "None identified" for non-deterministic fields). Output must be byte-identical.
- **No EXCLUDED columns** — No non-deterministic fields exist
- **No FUZZY columns** — All numeric values use Banker's rounding which is deterministic; SQLite's `ROUND()` should produce identical results to C#'s `Math.Round(..., MidpointRounding.ToEven)` for 2-decimal-place rounding. If Proofmark comparison reveals epsilon-level differences, a FUZZY override on `amount` with tight absolute tolerance (0.005) would be the fallback — but this is not expected.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: MCC risk lookup | SQL Design (INNER JOIN) | SQL JOIN on merchant_category_code = mcc_code replaces dictionary lookup | CardFraudFlagsProcessor.cs:30-39 |
| BR-2: Dual filter (High + >$500) | SQL Design (WHERE clause) | `WHERE mc.risk_level = 'High' AND ROUND(...) > 500` | CardFraudFlagsProcessor.cs:50 — `amount > 500m` |
| BR-3: Hardcoded $500 threshold | SQL Design, Anti-Pattern AP7 | Threshold is `500` in SQL with descriptive comment; BRD corrected to match V1 code | CardFraudFlagsProcessor.cs:49-50 |
| BR-4: Banker's rounding | SQL Design (ROUND) | `ROUND(CAST(ct.amount AS REAL), 2)` — SQLite uses Banker's rounding | CardFraudFlagsProcessor.cs:47 |
| BR-5: Unknown MCC handling | SQL Design (INNER JOIN) | INNER JOIN excludes transactions with no MCC match, identical to V1 empty-string-never-equals-High behavior | CardFraudFlagsProcessor.cs:45 |
| BR-6: No date filter in External | SQL Design (no date WHERE) | SQL has no date-based WHERE clause; DataSourcing handles date range | CardFraudFlagsProcessor.cs:42 |
| BR-7: High-risk MCCs (5094, 7995) | SQL Design (WHERE risk_level = 'High') | Data-driven — SQL doesn't hardcode MCCs, it filters on risk_level | DB query evidence in BRD |
| BR-8: No mcc_description in output | DataSourcing config, AP4 | `mcc_description` not sourced in V2 | CardFraudFlagsProcessor.cs:10-14 |
| BR-9: Empty input handling | Transformation module behavior | If card_transactions has zero rows after DataSourcing, the SQL JOIN produces zero rows; CsvFileWriter writes header-only file | CardFraudFlagsProcessor.cs:23-27 |
| BR-10: No weekend fallback | Module chain design | No weekend fallback logic in V2 — matches V1 absence | CardFraudFlagsProcessor.cs (absence) |
| Writer: Overwrite mode | Writer Config | `writeMode: Overwrite` — matches V1 exactly | card_fraud_flags.json:29 |
| Writer: LF line ending | Writer Config | `lineEnding: LF` — matches V1 exactly | card_fraud_flags.json:30 |
| Writer: includeHeader | Writer Config | `includeHeader: true` — matches V1 exactly | card_fraud_flags.json:28 |
| Writer: no trailer | Writer Config | No `trailerFormat` — matches V1 exactly | card_fraud_flags.json (absence) |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation — no External module is needed.

The V1 External module (`CardFraudFlagsProcessor.cs`) has been entirely replaced by the SQL Transformation module. All business logic (JOIN, filter, rounding, column renaming) is expressed in the single SQL statement in Module 3.

## Appendix: Row Ordering Consideration

V1's External module iterates `cardTransactions.Rows` in the order they were returned by DataSourcing (which orders by `as_of`). Within a given `as_of` date, the row order is determined by the PostgreSQL query's natural order. The SQL Transformation does not include an explicit ORDER BY clause, matching V1's behavior of relying on the natural join order. If Proofmark comparison fails due to row ordering differences, adding `ORDER BY ct.as_of, ct.card_txn_id` to the SQL would be a safe fix — but this should only be added if needed, as V1 does not explicitly sort.

**Risk assessment:** LOW. DataSourcing returns rows ordered by `as_of`. The INNER JOIN in SQLite should preserve this ordering for the left table's rows. If ordering becomes an issue, it is a Proofmark config or SQL fix, not a tier escalation.
