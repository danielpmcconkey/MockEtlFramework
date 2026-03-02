# CardTransactionDaily — Functional Specification Document

## 1. Overview & Tier

**Job:** CardTransactionDailyV2
**Config:** `card_transaction_daily_v2.json`
**Tier:** 2 (Framework + Minimal External) — `DataSourcing -> Transformation (SQL) -> External (minimal) -> CsvFileWriter`

This job produces a daily summary of card transactions grouped by card type, including transaction count, total amount, and average amount. On the last day of each month, a "MONTHLY_TOTAL" summary row is appended.

**Why Tier 2 instead of Tier 1:** Two operations cannot be correctly expressed in SQLite SQL:

1. **Banker's rounding (W5):** V1 computes `avg_amount` using C#'s `Math.Round(value, 2)`, which defaults to `MidpointRounding.ToEven` (Banker's rounding). SQLite's `ROUND()` function uses half-away-from-zero rounding, which would produce different results at midpoint values (e.g., 1234.565 rounds to 1234.56 with Banker's but 1234.57 with half-away-from-zero). There is no reliable way to implement Banker's rounding in SQLite with double-precision floats.

2. **Decimal precision:** V1 accumulates monetary values using C# `decimal` type (`Convert.ToDecimal`). The framework's Transformation module converts `decimal` to SQLite `REAL` (double-precision float) when registering tables, which could introduce floating-point epsilon errors in SUM aggregation compared to V1's exact decimal arithmetic. The External module preserves decimal fidelity.

3. **End-of-month MONTHLY_TOTAL row (W3b):** V1 checks `__maxEffectiveDate` from shared state to determine if the current date is the last day of the month. While this could theoretically be derived from `MAX(as_of)` in SQL with SQLite date functions, combining it with the rounding and precision requirements above makes a minimal External the cleanest solution.

The SQL Transformation handles the set-based JOIN between card_transactions and cards (eliminating AP6 row-by-row iteration for the lookup). The External module handles only the aggregation (with correct decimal/rounding semantics) and the conditional MONTHLY_TOTAL row — substantially less logic than V1's monolithic External.

**Traces to:** BRD Overview, BR-1 through BR-13

---

## 2. V2 Module Chain

```
[1] DataSourcing  -> card_transactions  (datalake.card_transactions)
[2] DataSourcing  -> cards              (datalake.cards)
[3] Transformation -> enriched_txns     (SQL: LEFT JOIN to enrich transactions with card_type)
[4] External      -> output             (aggregate, Banker's round, MONTHLY_TOTAL)
[5] CsvFileWriter <- output             (Output/double_secret_curated/card_transaction_daily.csv)
```

### Module Details

**Module 1: DataSourcing — card_transactions**
- Schema: `datalake`
- Table: `card_transactions`
- Columns: `card_id`, `amount`
- Effective dates: injected by executor via shared state (`__minEffectiveDate`, `__maxEffectiveDate`)
- Notes: V1 sources 8 columns (`card_txn_id`, `card_id`, `customer_id`, `merchant_name`, `merchant_category_code`, `amount`, `txn_timestamp`, `authorization_status`). Only `card_id` and `amount` are used by the processing logic. V2 sources only these 2. (Eliminates AP4)

**Module 2: DataSourcing — cards**
- Schema: `datalake`
- Table: `cards`
- Columns: `card_id`, `card_type`
- Effective dates: injected by executor via shared state
- Notes: V1 sources `card_id`, `customer_id`, `card_type`. Only `card_id` and `card_type` are used. V2 removes `customer_id`. (Eliminates AP4)

**Module 3: Transformation — enriched_txns**
- SQL: See Section 5 below
- Result stored in shared state as `enriched_txns`
- Purpose: JOIN card_transactions with cards to enrich each transaction row with its `card_type`, defaulting to 'Unknown' for unmatched card_ids. This replaces V1's row-by-row dictionary lookup (AP6).

**Module 4: External — output**
- Assembly: `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- Type: `ExternalModules.CardTransactionDailyV2Processor`
- Purpose: Read `enriched_txns` DataFrame, group by `card_type` using decimal aggregation, compute `avg_amount` with Banker's rounding, conditionally append MONTHLY_TOTAL row. Store result as `output`.

**Module 5: CsvFileWriter**
- Source: `output`
- Output path: `Output/double_secret_curated/card_transaction_daily.csv`
- includeHeader: `true`
- trailerFormat: `TRAILER|{row_count}|{date}`
- writeMode: `Overwrite`
- lineEnding: `LF`
- All writer params match V1 exactly (BRD Writer Configuration section)

### V1 Modules Removed

- **DataSourcing: `accounts`** — Sourced in V1 but never referenced by the External module. Dead-end sourcing. (Eliminates AP1)
- **DataSourcing: `customers`** — Sourced in V1 but never referenced by the External module. Dead-end sourcing. (Eliminates AP1)

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (Reproduced)

| W-Code | Applies? | V1 Behavior | V2 Treatment |
|--------|----------|-------------|--------------|
| W3b | **YES** | On the last day of the month (when `maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month)`), a MONTHLY_TOTAL summary row is appended with aggregate totals across all card types. | Reproduced in the External module. The end-of-month check uses `__maxEffectiveDate` from shared state, matching V1 exactly. The MONTHLY_TOTAL row's `avg_amount` uses the same Banker's rounding as per-card-type rows. Clear comment documents the W3b behavior: `// W3b: End-of-month boundary — append MONTHLY_TOTAL summary row`. |
| W5 | **YES** | `avg_amount` is computed using `Math.Round(total / count, 2)` which defaults to `MidpointRounding.ToEven` (Banker's rounding). | Reproduced in the External module using `Math.Round(totalAmount / txnCount, 2, MidpointRounding.ToEven)`. Explicit rounding mode parameter makes the behavior self-documenting. Comment: `// W5: Banker's rounding (MidpointRounding.ToEven) matches V1's Math.Round default`. |

### Output-Affecting Wrinkles (Not Applicable)

| W-Code | Reason Not Applicable |
|--------|----------------------|
| W1 | No Sunday skip behavior in V1. All dates processed normally. (BRD BR-11) |
| W2 | No weekend fallback. V1 uses `maxDate` directly. (BRD BR-11) |
| W3a | No weekly boundary logic. |
| W3c | No quarterly boundary logic. |
| W4 | No integer division. Aggregates use decimal arithmetic. |
| W6 | V1 uses `decimal` (not `double`) for monetary accumulation. No epsilon errors. V2 also uses `decimal`. |
| W7 | V1 uses framework CsvFileWriter for output — trailer row count reflects actual output rows, not input rows. |
| W8 | V1's trailer date uses `{date}` token which resolves to `__maxEffectiveDate`, not a hardcoded value. |
| W9 | Overwrite mode is correct for this job's use case (daily summary, not cumulative). |
| W10 | Not a Parquet job. |
| W12 | Not an Append-mode CSV. |

### Code-Quality Anti-Patterns (Eliminated)

| AP-Code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| AP1 | **YES** | V1 sources `accounts` and `customers` DataFrames that are never used by the External module. Two entire DataSourcing queries run for nothing. | **Eliminated.** V2 removes both DataSourcing entries. Only `card_transactions` and `cards` are sourced. (BRD BR-9) |
| AP3 | **YES** | V1 uses a monolithic External module for the entire pipeline: lookup table construction, grouping, aggregation, rounding, and conditional row insertion. The JOIN/lookup is expressible in SQL. | **Partially eliminated.** V2 moves the JOIN to a SQL Transformation (Tier 2 instead of V1's de facto Tier 3). The External module is reduced to aggregation + rounding + MONTHLY_TOTAL — operations that genuinely require C# for correct Banker's rounding with decimal precision. |
| AP4 | **YES** | V1 sources 8 columns from `card_transactions` but only uses `card_id` and `amount`. Sources 3 columns from `cards` but only uses `card_id` and `card_type`. | **Eliminated.** V2 sources only the 2 columns needed from each table. (BRD BR-13) |
| AP6 | **YES** | V1 builds a card_type lookup dictionary via `foreach` over cards rows, then iterates card_transactions row-by-row to perform the lookup and accumulate groups. | **Partially eliminated.** The card_type lookup is now a SQL LEFT JOIN (set-based). The aggregation within the External module uses LINQ-style operations rather than manual foreach accumulation. |

### Anti-Patterns Not Applicable

| AP-Code | Reason Not Applicable |
|---------|----------------------|
| AP2 | No evidence of cross-job duplication for this specific job's logic. |
| AP5 | No asymmetric NULL handling. The only NULL-adjacent behavior is the "Unknown" fallback for unmatched card_ids, which is consistently applied. |
| AP7 | No magic values. `"MONTHLY_TOTAL"` is a domain constant, not a threshold. The end-of-month check is standard calendar logic. |
| AP8 | V1 does not use SQL Transformation — no CTEs or window functions to simplify. |
| AP9 | Job name accurately describes its output (card transaction daily summary). |
| AP10 | V1 already uses framework-injected effective dates. V2 continues this pattern. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | Traces to BRD |
|--------|------|--------|---------------|---------------|
| card_type | TEXT | cards.card_type | LEFT JOIN lookup by card_id; "Unknown" if card_id not found in cards; "MONTHLY_TOTAL" for summary row | BR-1, BR-2, BR-6, BR-7, BR-8 |
| txn_count | INTEGER | card_transactions | COUNT per card_type group | BR-3, BR-8 |
| total_amount | DECIMAL | card_transactions.amount | SUM(amount) per card_type group, accumulated as C# `decimal` | BR-4, BR-8 |
| avg_amount | DECIMAL | Derived | total_amount / txn_count, rounded to 2 dp using Banker's rounding (MidpointRounding.ToEven); 0 if txn_count is 0 | BR-5, BR-8, W5 |
| as_of | TEXT | card_transactions.as_of | First row's as_of value from the enriched transactions DataFrame | BR-10 |

Column order matches V1 exactly: `card_type`, `txn_count`, `total_amount`, `avg_amount`, `as_of`.

**Non-deterministic fields:** None. All output fields are deterministic given the same input data and effective date range. (Traces to BRD Non-Deterministic Fields section.)

**Empty input guard (BR-12):** If `enriched_txns` is null or empty, the External module returns an empty DataFrame with the correct column schema. The CsvFileWriter will produce a header-only file with a trailer showing `TRAILER|0|{date}`.

---

## 5. SQL Design

### V2 SQL

```sql
-- V2: SQL JOIN replaces V1's row-by-row dictionary lookup (AP6).
-- LEFT JOIN ensures transactions with unmatched card_ids get card_type = 'Unknown' (BR-6).
-- Individual transaction rows are preserved for decimal aggregation in External module.
SELECT
    COALESCE(c.card_type, 'Unknown') AS card_type,
    ct.amount,
    ct.as_of
FROM card_transactions ct
LEFT JOIN cards c ON ct.card_id = c.card_id AND ct.as_of = c.as_of
```

### SQL Design Rationale

1. **LEFT JOIN instead of INNER JOIN:** V1 uses a dictionary lookup with a fallback: `cardTypeLookup.ContainsKey(cardId) ? cardTypeLookup[cardId] : "Unknown"`. This means transactions with card_ids not found in the cards table get `card_type = "Unknown"` rather than being excluded. A LEFT JOIN with `COALESCE(c.card_type, 'Unknown')` replicates this behavior exactly. (BR-6)

2. **Join on both card_id and as_of:** The DataSourcing module returns data with an `as_of` column, and since both tables are snapshotted daily, joining on `as_of` in addition to `card_id` ensures we match card data from the same effective date snapshot. V1's dictionary lookup iterates all cards rows regardless of as_of, but in single-day auto-advance mode (`minDate == maxDate`), both tables have exactly one as_of value, so the behavior is equivalent. Including `as_of` in the join is more correct for multi-day ranges.

3. **No GROUP BY in SQL:** The aggregation is deferred to the External module, which performs it with C# `decimal` precision and Banker's rounding. If we grouped in SQL, the amounts would be summed as SQLite REAL (double), potentially introducing floating-point precision differences versus V1's decimal accumulation. Keeping individual rows allows the External module to aggregate with exact decimal arithmetic.

4. **Minimal SELECT:** Only the columns needed for downstream processing are selected: `card_type` (for grouping), `amount` (for aggregation), and `as_of` (for output). This keeps the DataFrame lean.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CardTransactionDailyV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "card_transactions",
      "schema": "datalake",
      "table": "card_transactions",
      "columns": ["card_id", "amount"]
    },
    {
      "type": "DataSourcing",
      "resultName": "cards",
      "schema": "datalake",
      "table": "cards",
      "columns": ["card_id", "card_type"]
    },
    {
      "type": "Transformation",
      "resultName": "enriched_txns",
      "sql": "SELECT COALESCE(c.card_type, 'Unknown') AS card_type, ct.amount, ct.as_of FROM card_transactions ct LEFT JOIN cards c ON ct.card_id = c.card_id AND ct.as_of = c.as_of"
    },
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CardTransactionDailyV2Processor"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/card_transaction_daily.csv",
      "includeHeader": true,
      "trailerFormat": "TRAILER|{row_count}|{date}",
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | output | output | YES |
| outputFile | Output/curated/card_transaction_daily.csv | Output/double_secret_curated/card_transaction_daily.csv | Path changed per spec |
| includeHeader | true | true | YES |
| trailerFormat | TRAILER\|{row_count}\|{date} | TRAILER\|{row_count}\|{date} | YES |
| writeMode | Overwrite | Overwrite | YES |
| lineEnding | LF | LF | YES |

The only change is the output directory (`curated` -> `double_secret_curated`), as required by the project spec.

### Trailer Behavior

- `{row_count}` resolves to the number of data rows in the output DataFrame. This includes the MONTHLY_TOTAL row when present (BR-8, Edge Case 3 from BRD). Handled by the framework's CsvFileWriter (`CsvFileWriter.cs:64` — uses `df.Count`).
- `{date}` resolves to `__maxEffectiveDate` from shared state, formatted as `yyyy-MM-dd`. Handled by the framework's CsvFileWriter (`CsvFileWriter.cs:60-62`).
- With Overwrite mode, each run produces exactly one trailer at the end of the file.

### Write Mode Implications

- **Overwrite** mode means each run completely replaces the CSV file. For multi-day auto-advance runs, only the last effective date's output survives in the file. This matches V1 behavior. (BRD Write Mode Implications section)

---

## 8. Proofmark Config Design

### Config

```yaml
comparison_target: "card_transaction_daily"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 1
```

### Rationale

| Setting | Value | Justification |
|---------|-------|---------------|
| reader | csv | V1 and V2 both use CsvFileWriter |
| threshold | 100.0 | All fields are deterministic; byte-identical output expected |
| header_rows | 1 | `includeHeader: true` in writer config |
| trailer_rows | 1 | `trailerFormat` present + `writeMode: Overwrite` means exactly one trailer at end of file |

### Column Overrides

**Excluded columns:** None. All output columns are deterministic. (BRD Non-Deterministic Fields: "None identified.")

**Fuzzy columns:** None. The V2 External module uses the same C# `decimal` type and `Math.Round` with `MidpointRounding.ToEven` as V1. The computation path for monetary values is identical in precision and rounding behavior. No fuzzy tolerance is warranted.

### Why No Overrides Are Needed

- `avg_amount` (W5): Both V1 and V2 use C# `Math.Round` with Banker's rounding on `decimal` values. Identical computation path, identical output.
- `total_amount`: Both V1 and V2 accumulate using C# `decimal`. No floating-point epsilon issues.
- No timestamps, UUIDs, or other non-deterministic fields exist in the output.
- `as_of` is sourced from input data, not generated at runtime.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|-----------------|-------------|-------------------|
| BR-1: card_type lookup via card_id | Section 5 (SQL: LEFT JOIN cards c ON ct.card_id = c.card_id) | SQL Transformation module |
| BR-2: Group by card_type | Section 10 (External: groups by card_type) | External module |
| BR-3: txn_count = COUNT per card_type | Section 10 (External: count accumulation) | External module |
| BR-4: total_amount = SUM(amount) per card_type | Section 10 (External: decimal sum) | External module |
| BR-5: avg_amount with Banker's rounding | Section 3 (W5), Section 10 (External: Math.Round with MidpointRounding.ToEven) | External module |
| BR-6: Unknown card_type fallback | Section 5 (SQL: COALESCE(c.card_type, 'Unknown')) | SQL Transformation module |
| BR-7: End-of-month MONTHLY_TOTAL row | Section 3 (W3b), Section 10 (External: end-of-month check) | External module |
| BR-8: MONTHLY_TOTAL calculation | Section 10 (External: sum across groups) | External module |
| BR-9: Dead accounts/customers sourcing | Section 2 (removed), Section 3 (AP1 eliminated) | DataSourcing entries removed |
| BR-10: as_of from first row | Section 10 (External: captures as_of from first enriched_txns row) | External module |
| BR-11: No weekend fallback | Section 3 (W2 not applicable) | No weekend logic in V2 |
| BR-12: Empty input guard | Section 4 (empty input guard), Section 10 (External: null/empty check) | External module |
| BR-13: Unused sourced columns | Section 2 (reduced column lists), Section 3 (AP4 eliminated) | DataSourcing configs source only needed columns |
| BRD Writer Config | Section 7 (full parameter match) | CsvFileWriter config in V2 job JSON |
| BRD Trailer Format | Section 7 (trailer behavior) | trailerFormat in V2 job JSON |
| BRD Edge Case 1 (end-of-month on weekends) | Section 10 (External: uses maxDate directly, no weekend adjustment) | External module |
| BRD Edge Case 2 (cards not found) | Section 5 (SQL: LEFT JOIN + COALESCE 'Unknown') | SQL Transformation module |
| BRD Edge Case 3 (trailer includes MONTHLY_TOTAL) | Section 7 (trailer row count = df.Count, includes all rows) | Framework CsvFileWriter |
| BRD Edge Case 4 (as_of from first row) | Section 10 (External: first row's as_of) | External module |
| BRD Edge Case 5 (division by zero) | Section 10 (External: guard clause for txnCount == 0) | External module |
| AP1: Dead-end sourcing | Section 3 (eliminated) | accounts, customers DataSourcing removed |
| AP3: Unnecessary External scope | Section 3 (partially eliminated — reduced to Tier 2) | JOIN moved to SQL; External handles only what SQL can't |
| AP4: Unused columns | Section 3 (eliminated) | V2 sources only needed columns |
| AP6: Row-by-row iteration | Section 3 (partially eliminated) | JOIN is now SQL; aggregation uses LINQ-style ops |

---

## 10. External Module Design

### Module: `CardTransactionDailyV2Processor`

**File:** `ExternalModules/CardTransactionDailyV2Processor.cs`
**Implements:** `IExternalStep`

### Responsibility

This External module handles three operations that cannot be correctly expressed in SQLite SQL:

1. **Decimal aggregation:** Group the pre-joined `enriched_txns` DataFrame by `card_type`, computing `txn_count` (COUNT) and `total_amount` (SUM) using C# `decimal` to preserve exact monetary arithmetic.
2. **Banker's rounding:** Compute `avg_amount` as `Math.Round(totalAmount / txnCount, 2, MidpointRounding.ToEven)` — matching V1's rounding behavior exactly.
3. **Conditional MONTHLY_TOTAL row:** Check `__maxEffectiveDate` from shared state and append a summary row when the date is the last day of its month.

### Input Contract

- **`enriched_txns`** (DataFrame in shared state): Pre-joined data from the SQL Transformation. Columns: `card_type` (TEXT), `amount` (REAL/decimal), `as_of` (TEXT/DateOnly). Each row is one transaction enriched with its card type.
- **`__maxEffectiveDate`** (DateOnly in shared state): The effective date, used for the end-of-month boundary check.

### Output Contract

- **`output`** (DataFrame in shared state): Final output with columns: `card_type`, `txn_count`, `total_amount`, `avg_amount`, `as_of`. One row per card_type group, plus an optional MONTHLY_TOTAL row.

### Pseudocode

```
function Execute(sharedState):
    outputColumns = ["card_type", "txn_count", "total_amount", "avg_amount", "as_of"]

    maxDate = sharedState["__maxEffectiveDate"] as DateOnly
              (fallback: DateOnly.FromDateTime(DateTime.Today))

    enrichedTxns = sharedState["enriched_txns"] as DataFrame

    // BR-12: Empty input guard
    if enrichedTxns is null or empty:
        sharedState["output"] = new DataFrame(empty, outputColumns)
        return sharedState

    // BR-10: Capture as_of from first row
    asOf = enrichedTxns.Rows[0]["as_of"]

    // Group by card_type with decimal accumulation
    groups = Dictionary<string, (int count, decimal total)>
    for each row in enrichedTxns.Rows:
        cardType = row["card_type"].ToString()
        amount = Convert.ToDecimal(row["amount"])

        if cardType not in groups:
            groups[cardType] = (0, 0m)
        groups[cardType] = (count + 1, total + amount)

    // Build output rows with Banker's rounding
    outputRows = []
    for each (cardType, (count, total)) in groups:
        // W5: Banker's rounding — MidpointRounding.ToEven matches V1
        avgAmount = count > 0
            ? Math.Round(total / count, 2, MidpointRounding.ToEven)
            : 0m
        outputRows.Add(Row{
            card_type = cardType,
            txn_count = count,
            total_amount = total,
            avg_amount = avgAmount,
            as_of = asOf
        })

    // W3b: End-of-month boundary — append MONTHLY_TOTAL summary row
    if maxDate.Day == DateTime.DaysInMonth(maxDate.Year, maxDate.Month):
        totalCount = sum of all groups' counts
        totalAmount = sum of all groups' totals
        // W5: Same Banker's rounding for MONTHLY_TOTAL avg
        avgAmount = totalCount > 0
            ? Math.Round(totalAmount / totalCount, 2, MidpointRounding.ToEven)
            : 0m
        outputRows.Add(Row{
            card_type = "MONTHLY_TOTAL",
            txn_count = totalCount,
            total_amount = totalAmount,
            avg_amount = avgAmount,
            as_of = asOf
        })

    sharedState["output"] = new DataFrame(outputRows, outputColumns)
    return sharedState
```

### Key Design Decisions

1. **`decimal` for monetary accumulation:** V1 uses `Convert.ToDecimal(txn["amount"])` and accumulates in `decimal` tuples. V2 does the same. This ensures exact monetary arithmetic with no floating-point epsilon drift. (Avoids W6 risk)

2. **Explicit `MidpointRounding.ToEven`:** V1 uses `Math.Round(value, 2)` which implicitly uses Banker's rounding. V2 passes `MidpointRounding.ToEven` explicitly, making the behavior self-documenting while producing identical results. (W5)

3. **as_of from first enriched_txns row:** Matches V1 behavior exactly. V1 uses `cardTransactions.Rows[0]["as_of"]`. Since the SQL Transformation preserves `as_of` in the output, the first row of `enriched_txns` has the same `as_of` value. The DataSourcing module orders by `as_of`, so in single-day mode this is deterministic. (BR-10, BRD Edge Case 4)

4. **Division by zero guard:** Both per-card-type rows and the MONTHLY_TOTAL row check `count > 0` before dividing. If count is 0, `avg_amount` defaults to `0m`. This matches V1 exactly. (BRD Edge Case 5)

5. **No dictionary lookup for card_type:** V1 builds a `Dictionary<int, string>` from the cards table and looks up each transaction's card_id. V2 receives pre-joined data from the SQL Transformation — the card_type is already on each row. This eliminates the manual lookup loop (AP6 partial fix).

6. **Group iteration order:** V1 iterates `Dictionary<string, (int, decimal)>` via `foreach`. .NET Dictionary enumeration order is insertion order (in practice, though not contractually guaranteed). V2 uses the same Dictionary-based grouping approach, so the row order will match V1. Since Proofmark is order-independent, this is not a correctness concern, but it helps with visual diff comparison.

---

## Appendix: V1 vs V2 Diff Summary

| Aspect | V1 | V2 | Change Type |
|--------|----|----|-------------|
| DataSourcing: sources | 4 tables (card_transactions, accounts, customers, cards) | 2 tables (card_transactions, cards) | AP1 eliminated |
| DataSourcing: card_transactions columns | 8 columns | 2 columns (card_id, amount) | AP4 eliminated |
| DataSourcing: cards columns | 3 columns (card_id, customer_id, card_type) | 2 columns (card_id, card_type) | AP4 eliminated |
| Card type lookup | C# Dictionary + foreach loop | SQL LEFT JOIN + COALESCE | AP6 eliminated, AP3 partially eliminated |
| Unknown card_type fallback | `ContainsKey ? lookup : "Unknown"` | `COALESCE(c.card_type, 'Unknown')` | Same behavior, cleaner implementation |
| Aggregation (SUM, COUNT) | C# foreach + tuple accumulation | C# grouping with decimal | Preserved for decimal precision |
| avg_amount rounding | `Math.Round(value, 2)` (implicit Banker's) | `Math.Round(value, 2, MidpointRounding.ToEven)` (explicit) | W5 preserved, made explicit |
| MONTHLY_TOTAL logic | Same External module, monolithic | Separate minimal External module | Cleaner separation of concerns |
| Module chain | DataSourcing x4 → External → CsvFileWriter | DataSourcing x2 → Transformation → External → CsvFileWriter | Tier 3 → Tier 2 |
| Writer config | All params identical | Path changed to double_secret_curated | Required by spec |
| Output columns | card_type, txn_count, total_amount, avg_amount, as_of | Same | No change |
