# HighRiskMerchantActivity — Functional Specification Document

## 1. Overview & Tier Selection

**Job:** HighRiskMerchantActivityV2
**Tier:** 1 — Framework Only (`DataSourcing → Transformation (SQL) → CsvFileWriter`)

This job extracts all card transactions at merchants classified as "High" risk, enriches each transaction with the merchant category description, and produces a detail-level CSV record for each qualifying transaction. The output supports risk monitoring and regulatory reporting.

**Tier Justification:** The V1 External module (`HighRiskMerchantActivityProcessor.cs`) performs a dictionary-based lookup (equivalent to a JOIN) and a single-condition filter on `risk_level == "High"` — both trivially expressible as a SQL INNER JOIN + WHERE clause. The output columns are all pass-through or simple lookups. There is no procedural logic, no cross-date-range querying, no complex aggregation, and no I/O quirk that requires an External module. Tier 1 is sufficient.

## 2. V2 Module Chain

```
DataSourcing ("card_transactions")
  → DataSourcing ("merchant_categories")
    → Transformation (SQL JOIN + WHERE)
      → CsvFileWriter
```

### Module 1: DataSourcing — card_transactions
- **resultName:** `card_transactions`
- **schema:** `datalake`
- **table:** `card_transactions`
- **columns:** `card_txn_id`, `merchant_name`, `merchant_category_code`, `amount`, `txn_timestamp`
- Effective dates injected by executor via shared state (no hardcoded dates)
- **Note:** `card_id` and `customer_id` removed vs V1 (AP4 — unused column elimination; per BR-10 these are sourced but never included in the output)

### Module 2: DataSourcing — merchant_categories
- **resultName:** `merchant_categories`
- **schema:** `datalake`
- **table:** `merchant_categories`
- **columns:** `mcc_code`, `mcc_description`, `risk_level`
- Effective dates injected by executor via shared state
- **Note:** All three columns are needed — `mcc_code` for join, `risk_level` for filtering, and `mcc_description` for output enrichment

### Module 3: Transformation
- **resultName:** `output`
- SQL performs: INNER JOIN card_transactions to merchant_categories on MCC code, filter by `risk_level = 'High'`, select output columns with rename of `merchant_category_code` to `mcc_code`

### Module 4: CsvFileWriter
- **source:** `output`
- **outputFile:** `Output/double_secret_curated/high_risk_merchant_activity.csv`
- **includeHeader:** `true`
- **writeMode:** `Overwrite`
- **lineEnding:** `LF`
- No trailer configured (matches V1)

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| ID | Name | Applies? | V1 Evidence | V2 Disposition |
|----|------|----------|-------------|----------------|
| AP3 | Unnecessary External | YES | `HighRiskMerchantActivityProcessor.cs` — entire logic is a dictionary lookup (JOIN) + single filter, trivially expressible in SQL | **ELIMINATED.** Replaced with Tier 1 SQL Transformation. |
| AP4 | Unused columns | YES | `high_risk_merchant_activity.json:10` sources `card_id` and `customer_id` but `HighRiskMerchantActivityProcessor.cs:10-13` output columns do not include them | **ELIMINATED.** V2 DataSourcing for card_transactions does not source `card_id` or `customer_id`. |
| AP6 | Row-by-row iteration | YES | `HighRiskMerchantActivityProcessor.cs:43-65` uses `foreach` loop with dictionary lookup instead of a set-based operation | **ELIMINATED.** Replaced with SQL JOIN. |
| AP7 | Magic values | YES | `HighRiskMerchantActivityProcessor.cs:51-52` hardcoded `"High"` string literal | **ELIMINATED.** SQL uses clearly commented filter: `-- V1 filter: only transactions at merchants with risk_level = 'High'` |
| AP1 | Dead-end sourcing | NO | Both sourced tables are used in the processing logic | N/A |
| AP9 | Misleading names | NO | Job name accurately describes output (high-risk merchant activity) | N/A |
| AP10 | Over-sourcing dates | NO | V1 uses framework effective date injection correctly; no additional date filtering in the External module | N/A |

### Identified Wrinkles (Output-Affecting)

| ID | Name | Applies? | V1 Evidence | V2 Disposition |
|----|------|----------|-------------|----------------|
| W9 | Wrong writeMode | POSSIBLE | V1 uses Overwrite mode — for multi-day auto-advance, only the last day's output survives. This may be intentional or a bug. | **REPRODUCED.** V2 uses `Overwrite` to match V1 exactly. `// V1 uses Overwrite — prior days' data is lost on each run.` |
| W1-W4, W5-W8, W10, W12 | Other wrinkles | NO | No Sunday skip, weekend fallback, boundary rows, integer division, Banker's rounding, double epsilon, trailer issues, header-every-append, or absurd numParts in this job | N/A |

### Notes on V1 Behavior

- **No amount rounding (BR-8):** Unlike CardFraudFlags which applies Banker's rounding, this job passes the `amount` field through without any rounding. V2 reproduces this pass-through behavior.
- **No amount threshold (BR-6):** Unlike CardFraudFlags which applies a $500 threshold, this job includes all transactions at high-risk merchants regardless of amount.
- **MCC dictionary overwrite (Edge Case 3 from BRD):** V1 iterates all `merchant_categories` rows and overwrites the dictionary entry for duplicate `mcc_code` values across `as_of` dates — the last-seen value wins. The SQL INNER JOIN will produce a cross-product for duplicate MCC codes across dates, which could yield different results if `mcc_description` or `risk_level` differ across `as_of` dates. However, since `merchant_categories` is reference data and descriptions/risk levels are expected to be consistent for a given `mcc_code` across snapshots, the INNER JOIN should produce equivalent results. If Proofmark comparison reveals discrepancies due to duplicate MCC entries with varying attributes, this would need a Tier 2 escalation — but the evidence strongly supports Tier 1.

## 4. Output Schema

| # | Column | Source | Transformation | Evidence |
|---|--------|--------|---------------|----------|
| 1 | card_txn_id | card_transactions.card_txn_id | Pass-through | HighRiskMerchantActivityProcessor.cs:56 |
| 2 | merchant_name | card_transactions.merchant_name | Pass-through | HighRiskMerchantActivityProcessor.cs:57 |
| 3 | mcc_code | card_transactions.merchant_category_code | Renamed from merchant_category_code | HighRiskMerchantActivityProcessor.cs:58 |
| 4 | mcc_description | merchant_categories.mcc_description | Joined via mcc_code | HighRiskMerchantActivityProcessor.cs:59 |
| 5 | amount | card_transactions.amount | Pass-through (no rounding) | HighRiskMerchantActivityProcessor.cs:60 |
| 6 | txn_timestamp | card_transactions.txn_timestamp | Pass-through | HighRiskMerchantActivityProcessor.cs:61 |
| 7 | as_of | card_transactions.as_of | Pass-through (injected by DataSourcing) | HighRiskMerchantActivityProcessor.cs:62 |

**Column ordering:** The output columns must appear in the exact order listed above to match V1 output. The SQL SELECT clause enforces this ordering.

**Columns excluded from output (per BR-10, BR-11):**
- `card_id` — sourced in V1 but not included in output columns
- `customer_id` — sourced in V1 but not included in output columns
- `risk_level` — used for filtering but not included in output columns

## 5. SQL Design

```sql
-- HighRiskMerchantActivityV2: Extract transactions at high-risk merchants
-- Business rule: include all transactions where merchant risk_level = 'High'
-- No amount threshold, no rounding — straight pass-through of transaction data
-- BR-2: Filter on risk_level = 'High'
-- BR-4: Transactions with unknown MCC codes are excluded (INNER JOIN handles this)
-- BR-6: No amount threshold applied
-- BR-8: Amount passes through without rounding

SELECT
    ct.card_txn_id,
    ct.merchant_name,
    ct.merchant_category_code AS mcc_code,        -- Renamed to match V1 output schema
    mc.mcc_description,                            -- Enrichment from merchant_categories lookup
    ct.amount,                                     -- Pass-through, no rounding (BR-8)
    ct.txn_timestamp,
    ct.as_of
FROM card_transactions ct
INNER JOIN merchant_categories mc
    ON ct.merchant_category_code = mc.mcc_code
    AND ct.as_of = mc.as_of                        -- Join within same snapshot date
WHERE mc.risk_level = 'High'                       -- V1 filter: only high-risk merchants (BR-2)
```

### SQL Design Notes

1. **INNER JOIN vs dictionary lookup:** V1 builds a dictionary from all `merchant_categories` rows (across all `as_of` dates) and looks up each transaction's MCC code. Transactions with no matching MCC code are skipped via `continue` (line 48). An INNER JOIN produces identical behavior — unmatched transactions are excluded from the result set entirely. This is semantically equivalent.

2. **as_of join condition:** The V1 dictionary lookup iterates all `merchant_categories` rows without filtering by `as_of`, meaning the last-seen value for each `mcc_code` wins (dictionary overwrite). The SQL uses `ct.as_of = mc.as_of` to join within the same snapshot date, which is a cleaner approach. For reference data like `merchant_categories` where `risk_level` and `mcc_description` are consistent across snapshots for a given `mcc_code`, this produces identical results. If Proofmark comparison fails because of this, removing the `as_of` join condition and using a subquery with the latest `as_of` would be the fallback.

3. **No ORDER BY clause:** V1's External module iterates `cardTransactions.Rows` in the order returned by DataSourcing (which orders by `as_of`). The SQL does not include an explicit ORDER BY, matching V1's reliance on natural ordering. If row ordering differences cause Proofmark failures, adding `ORDER BY ct.as_of, ct.card_txn_id` would be a safe fix.

4. **No date filter in SQL:** V1's External module does not filter by date within its logic (BR-7). The SQL likewise has no date-related WHERE clause beyond the join condition — all rows from the DataSourcing effective date range are evaluated. DataSourcing handles date range filtering at the source level.

5. **Empty output expectation:** Per BRD Edge Cases 1-2, the high-risk MCC codes (5094/Precious Metals, 7995/Gambling) do not appear in the `card_transactions` data. This means the output will likely be an empty CSV (header only, no data rows) for all dates. The INNER JOIN naturally produces zero rows when no transactions match high-risk MCCs, and CsvFileWriter will write just the header — matching V1's empty output behavior (BR-9).

## 6. V2 Job Config JSON

```json
{
  "jobName": "HighRiskMerchantActivityV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["card_txn_id", "merchant_name", "merchant_category_code", "amount", "txn_timestamp"]
    },
    {
      "type": "DataSourcing",
      "resultName": "merchant_categories",
      "schema": "datalake",
      "table": "merchant_categories",
      "columns": ["mcc_code", "mcc_description", "risk_level"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT ct.card_txn_id, ct.merchant_name, ct.merchant_category_code AS mcc_code, mc.mcc_description, ct.amount, ct.txn_timestamp, ct.as_of FROM card_transactions ct INNER JOIN merchant_categories mc ON ct.merchant_category_code = mc.mcc_code AND ct.as_of = mc.as_of WHERE mc.risk_level = 'High'"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/high_risk_merchant_activity.csv",
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
| jobName | `HighRiskMerchantActivity` | `HighRiskMerchantActivityV2` | V2 naming convention |
| card_transactions columns | `card_txn_id, card_id, customer_id, merchant_name, merchant_category_code, amount, txn_timestamp` | `card_txn_id, merchant_name, merchant_category_code, amount, txn_timestamp` | AP4: removed unused `card_id` and `customer_id` |
| merchant_categories columns | `mcc_code, mcc_description, risk_level` | `mcc_code, mcc_description, risk_level` | No change — all three columns are used |
| Module 3 | External (HighRiskMerchantActivityProcessor) | Transformation (SQL) | AP3: replaced unnecessary External with SQL |
| Output path | `Output/curated/high_risk_merchant_activity.csv` | `Output/double_secret_curated/high_risk_merchant_activity.csv` | V2 output directory |
| Writer config | Identical | Identical | All writer params preserved (includeHeader, writeMode, lineEnding, no trailer) |

## 7. Writer Configuration

| Property | Value | Matches V1? |
|----------|-------|-------------|
| type | CsvFileWriter | YES |
| source | output | YES |
| outputFile | Output/double_secret_curated/high_risk_merchant_activity.csv | Path updated per V2 convention |
| includeHeader | true | YES |
| writeMode | Overwrite | YES — `// V1 uses Overwrite — prior days' data is lost on each run.` |
| lineEnding | LF | YES |
| trailerFormat | (not configured) | YES — V1 has no trailer |

## 8. Proofmark Config Design

```yaml
comparison_target: "high_risk_merchant_activity"
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
- **No EXCLUDED columns** — No non-deterministic fields exist in this job
- **No FUZZY columns** — No rounding or floating-point arithmetic is applied (amount is pass-through per BR-8). All values are deterministic pass-throughs or string lookups.

### Edge Case: Empty Output Comparison

Both V1 and V2 are expected to produce an empty CSV (header row only, no data rows) because the high-risk MCC codes (5094, 7995) do not appear in `card_transactions`. Proofmark should handle this gracefully — two files that both contain only the same header row should compare as PASS. If Proofmark errors on zero-data-row files, this would be a Proofmark config issue (exit code 2), not a data mismatch.

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: MCC lookup | SQL Design (INNER JOIN) | SQL JOIN on merchant_category_code = mcc_code replaces dictionary lookup | HighRiskMerchantActivityProcessor.cs:30-41 |
| BR-2: risk_level='High' filter | SQL Design (WHERE clause) | `WHERE mc.risk_level = 'High'` | HighRiskMerchantActivityProcessor.cs:52 |
| BR-3: Hardcoded risk level | SQL Design, Anti-Pattern AP7 | `'High'` in SQL with descriptive comment; magic value documented | HighRiskMerchantActivityProcessor.cs:51 |
| BR-4: Unknown MCC skipped | SQL Design (INNER JOIN) | INNER JOIN excludes transactions with no MCC match, identical to V1 `continue` on missing key | HighRiskMerchantActivityProcessor.cs:48 |
| BR-5: High-risk MCCs (5094, 7995) | SQL Design (WHERE risk_level = 'High') | Data-driven — SQL doesn't hardcode MCCs, it filters on risk_level from reference data | DB query evidence in BRD |
| BR-6: No amount threshold | SQL Design (no amount filter) | No amount condition in WHERE clause — all transactions at high-risk merchants included regardless of amount | HighRiskMerchantActivityProcessor.cs:44-65 (absence) |
| BR-7: No weekend fallback | Module chain design | No weekend fallback logic — all transaction dates processed | HighRiskMerchantActivityProcessor.cs (absence) |
| BR-8: No amount rounding | SQL Design (pass-through) | `ct.amount` selected directly without ROUND() | HighRiskMerchantActivityProcessor.cs:60 |
| BR-9: Empty input handling | Transformation module behavior | If card_transactions has zero matching rows after JOIN, SQL produces zero rows; CsvFileWriter writes header-only file | HighRiskMerchantActivityProcessor.cs:23-27 |
| BR-10: card_id/customer_id not in output | DataSourcing config, AP4 | `card_id` and `customer_id` not sourced in V2 | HighRiskMerchantActivityProcessor.cs:10-13, high_risk_merchant_activity.json:10 |
| BR-11: risk_level not in output | SQL Design (SELECT clause) | `risk_level` used in WHERE but not in SELECT — excluded from output | HighRiskMerchantActivityProcessor.cs:10-13 |
| Writer: Overwrite mode | Writer Config | `writeMode: Overwrite` — matches V1 exactly | high_risk_merchant_activity.json:29 |
| Writer: LF line ending | Writer Config | `lineEnding: LF` — matches V1 exactly | high_risk_merchant_activity.json:30 |
| Writer: includeHeader | Writer Config | `includeHeader: true` — matches V1 exactly | high_risk_merchant_activity.json:28 |
| Writer: no trailer | Writer Config | No `trailerFormat` — matches V1 exactly | high_risk_merchant_activity.json (absence) |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation — no External module is needed.

The V1 External module (`HighRiskMerchantActivityProcessor.cs`) has been entirely replaced by the SQL Transformation module. All business logic (JOIN on MCC code, filter on risk_level, column selection and renaming) is expressed in the single SQL statement in Module 3.

## Appendix A: Row Ordering Consideration

V1's External module iterates `cardTransactions.Rows` in the order they were returned by DataSourcing (which orders by `as_of`). Within a given `as_of` date, the row order is determined by the PostgreSQL query's natural order. The SQL Transformation does not include an explicit ORDER BY clause, matching V1's behavior of relying on the natural join order. If Proofmark comparison fails due to row ordering differences, adding `ORDER BY ct.as_of, ct.card_txn_id` to the SQL would be a safe fix — but this should only be added if needed, as V1 does not explicitly sort.

**Risk assessment:** LOW. DataSourcing returns rows ordered by `as_of`. The INNER JOIN in SQLite should preserve this ordering for the left table's rows. Since the output is expected to be empty anyway (no matching high-risk transactions), ordering is moot for the current data.

## Appendix B: Empty Output Analysis

Per BRD Edge Cases 1-2, the high-risk MCC codes (5094/Precious Metals and 7995/Gambling) are present in `merchant_categories` but do NOT appear in `card_transactions.merchant_category_code`. The 17 distinct MCC codes in card_transactions are: {4511, 4814, 5200, 5311, 5411, 5541, 5691, 5732, 5812, 5814, 5912, 5942, 5944, 5999, 7011, 7832, 8011}. None of these overlap with the high-risk codes {5094, 7995}.

This means:
- V1 produces an empty output (header only, zero data rows)
- V2 will produce an identical empty output (header only, zero data rows)
- Proofmark comparison should trivially pass — both files contain only the header row

This is expected behavior, not a data gap in V2. The job is designed to capture high-risk merchant activity when it exists; it happens that the current dataset contains no such activity.

## Appendix C: as_of JOIN Condition Risk Analysis

V1's dictionary-based approach iterates ALL `merchant_categories` rows (across all `as_of` dates) and builds a single dictionary keyed by `mcc_code`. If the same `mcc_code` appears on multiple `as_of` dates, the dictionary overwrites with the last-seen values. This means V1 effectively uses the "last snapshot date's" values for description and risk_level.

V2's SQL uses `ct.as_of = mc.as_of` to join within the same snapshot date. This is semantically cleaner — each transaction is matched to its contemporaneous merchant category data.

**Equivalence analysis:** For reference data like `merchant_categories`, the `mcc_description` and `risk_level` values for a given `mcc_code` should be identical across all `as_of` dates. If they are, both approaches produce the same results. Since the output is expected to be empty anyway (no transactions at high-risk MCCs), this join condition cannot affect the output for the current dataset. If future data introduces high-risk transactions, the `as_of` join condition provides more correct behavior than V1's accidental dictionary-overwrite pattern.

If Proofmark comparison fails due to this join condition (e.g., if `merchant_categories` data for a high-risk MCC differs across snapshots), the fix would be to remove the `AND ct.as_of = mc.as_of` condition and instead use a subquery or window function to pick the latest `as_of` per `mcc_code`. But this is extremely unlikely given the current data.
