# RepeatOverdraftCustomers -- Functional Specification Document

## 1. Job Summary

This job identifies customers with two or more overdraft events across the effective date range, producing a per-customer summary with event count and total overdraft amount, enriched with the customer's first and last name from the latest snapshot. Output is a single-part Parquet file in Overwrite mode. The V2 implementation replaces the V1 External module with a pure SQL Transformation, eliminating row-by-row iteration (AP6), unnecessary External usage (AP3), unused sourced columns (AP4), and magic threshold values (AP7), while preserving byte-identical output.

## 2. V2 Module Chain

**Tier: 1 -- Framework Only**

```
DataSourcing (overdraft_events) -> DataSourcing (customers) -> Transformation (SQL) -> ParquetFileWriter
```

**Justification:** All V1 business logic is expressible in SQL:
- Customer name deduplication (last-loaded-wins per customer_id) is a standard window function or GROUP BY + MAX(as_of) pattern.
- Overdraft aggregation (COUNT, SUM grouped by customer_id) is basic SQL.
- Repeat threshold filter (HAVING count >= 2) is basic SQL.
- The `as_of` output column (MIN from overdraft_events) is a scalar aggregate.
- Customer name lookup via LEFT JOIN with COALESCE for missing customers.

No procedural C# logic is required. Tier 1 eliminates AP3 (unnecessary External module) and AP6 (row-by-row iteration).

## 3. DataSourcing Config

### Source 1: overdraft_events

| Property | Value |
|----------|-------|
| resultName | `overdraft_events` |
| schema | `datalake` |
| table | `overdraft_events` |
| columns | `customer_id`, `overdraft_amount` |

**Effective date handling:** Injected by the executor via shared state (`__minEffectiveDate` / `__maxEffectiveDate`). DataSourcing filters `as_of >= @minDate AND as_of <= @maxDate` and appends `as_of` as a column automatically. DataSourcing also applies `ORDER BY as_of`. [Evidence: DataSourcing.cs:74-85]

**AP4 elimination:** V1 sources 7 columns (`overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`) but only uses `customer_id` and `overdraft_amount`. V2 sources only the 2 required columns. [Evidence: BRD EC-7, RepeatOverdraftCustomerProcessor.cs:44-45 vs repeat_overdraft_customers.json:10]

### Source 2: customers

| Property | Value |
|----------|-------|
| resultName | `customers` |
| schema | `datalake` |
| table | `customers` |
| columns | `id`, `first_name`, `last_name` |

**Effective date handling:** Same as above -- injected by executor. DataSourcing appends `as_of` automatically.

**Note:** V1 sources these same 3 columns. No unused columns to eliminate here.

## 4. Transformation SQL

**resultName:** `output`

```sql
SELECT
    oe.customer_id,
    COALESCE(cl.first_name, '') AS first_name,
    COALESCE(cl.last_name, '') AS last_name,
    oe.overdraft_count,
    oe.total_overdraft_amount,
    oe.as_of
FROM (
    -- BR-2: Group overdraft events by customer_id across all dates in range
    -- BR-3: Filter to customers with 2+ overdraft events (repeat threshold)
    -- BR-4: as_of taken as MIN(as_of) from overdraft_events (first row in date order)
    SELECT
        customer_id,
        COUNT(*) AS overdraft_count,
        SUM(overdraft_amount) AS total_overdraft_amount,
        (SELECT MIN(as_of) FROM overdraft_events) AS as_of
    FROM overdraft_events
    GROUP BY customer_id
    HAVING COUNT(*) >= 2  -- AP7: Named threshold; see comment below
) oe
LEFT JOIN (
    -- BR-1: Customer lookup uses last-loaded values (dictionary overwrite)
    -- DataSourcing orders by as_of, so last row per customer = max as_of
    SELECT id, first_name, last_name
    FROM customers
    WHERE rowid IN (
        SELECT MAX(rowid) FROM customers GROUP BY id
    )
) cl ON oe.customer_id = cl.id
```

### SQL Design Notes

**BR-1 (Customer lookup -- last-loaded-wins):** V1 builds a dictionary by iterating all customer rows in order; since DataSourcing orders by `as_of`, later `as_of` dates overwrite earlier ones for the same `id`. The SQL replicates this by selecting the row with the highest `rowid` per `id`. SQLite rowids reflect insertion order, which matches DataSourcing's `ORDER BY as_of` load order. [Evidence: RepeatOverdraftCustomerProcessor.cs:31-38, DataSourcing.cs:85]

**BR-2 (Cross-date aggregation):** The GROUP BY operates over ALL rows in the overdraft_events DataFrame (spanning the full effective date range), matching V1's behavior of iterating all rows without date filtering. [Evidence: RepeatOverdraftCustomerProcessor.cs:41-52]

**BR-3 (Repeat threshold):** `HAVING COUNT(*) >= 2` replicates V1's `if (kvp.Value.count < 2) continue;`. The threshold value of 2 is a business constant. [Evidence: RepeatOverdraftCustomerProcessor.cs:55-58]

**BR-4 (as_of from first row):** V1 takes `overdraftEvents.Rows[0]["as_of"]`, which is the first row after DataSourcing's `ORDER BY as_of` -- i.e., the minimum `as_of` date. The SQL uses `(SELECT MIN(as_of) FROM overdraft_events)` to produce the same scalar value for all output rows. [Evidence: RepeatOverdraftCustomerProcessor.cs:28, DataSourcing.cs:85]

**BR-5 (Missing customer fallback):** The LEFT JOIN produces NULL for `first_name` and `last_name` when a customer_id has no match. `COALESCE(..., '')` converts these to empty strings, matching V1's `("", "")` fallback. [Evidence: RepeatOverdraftCustomerProcessor.cs:62-63]

**BR-6 (Decimal arithmetic):** SQLite stores numeric values from PostgreSQL as received. The SUM operation in SQLite preserves the precision of the input values. V1 uses `Convert.ToDecimal()` and accumulates with decimal arithmetic. Both approaches should produce identical sums given the same input values. [Evidence: RepeatOverdraftCustomerProcessor.cs:44]

**AP7 (Magic threshold):** The `HAVING COUNT(*) >= 2` threshold is documented inline via SQL comment. In V1 this was a bare `< 2` comparison with no explanation. The V2 SQL includes a comment identifying it as the repeat-overdraft business threshold.

## 5. Writer Config

| Property | Value |
|----------|-------|
| type | `ParquetFileWriter` |
| source | `output` |
| outputDirectory | `Output/double_secret_curated/repeat_overdraft_customers/` |
| numParts | `1` |
| writeMode | `Overwrite` |

All writer parameters match V1 exactly (same writer type, same numParts, same writeMode), with only the output path changed to `double_secret_curated`. [Evidence: repeat_overdraft_customers.json:24-29]

## 6. Wrinkle Replication

No output-affecting wrinkles (W-codes) are present in this job.

The BRD identifies no wrinkles. Reviewing the full W-code catalog:

| W-code | Applicable? | Reasoning |
|--------|-------------|-----------|
| W1 (Sunday skip) | No | No day-of-week logic in V1 processor |
| W2 (Weekend fallback) | No | No weekend date manipulation |
| W3a/b/c (Boundary rows) | No | No summary row generation |
| W4 (Integer division) | No | No percentage calculations |
| W5 (Banker's rounding) | No | No rounding in V1 processor |
| W6 (Double epsilon) | No | V1 uses decimal, not double [RepeatOverdraftCustomerProcessor.cs:44] |
| W7 (Trailer inflated count) | No | No trailer; Parquet output |
| W8 (Trailer stale date) | No | No trailer |
| W9 (Wrong writeMode) | No | Overwrite is appropriate for this job |
| W10 (Absurd numParts) | No | numParts=1 is reasonable |
| W12 (Header every append) | No | Not Append mode; not CSV |

## 7. Anti-Pattern Elimination

### AP3: Unnecessary External Module -- ELIMINATED

**V1 problem:** V1 uses a full External module (`RepeatOverdraftCustomerProcessor`) for logic that is entirely expressible in SQL: GROUP BY with COUNT/SUM, a threshold filter (HAVING), a LEFT JOIN for customer lookup, and COALESCE for NULL handling. [Evidence: repeat_overdraft_customers.json:20-23, RepeatOverdraftCustomerProcessor.cs:1-80]

**V2 approach:** Replaced with a Tier 1 framework-only chain: DataSourcing -> Transformation (SQL) -> ParquetFileWriter. The Transformation SQL handles all business logic that was previously in C# code.

### AP4: Unused Columns -- ELIMINATED

**V1 problem:** V1 sources 7 columns from overdraft_events (`overdraft_id`, `account_id`, `customer_id`, `overdraft_amount`, `fee_amount`, `fee_waived`, `event_timestamp`) but only uses `customer_id` and `overdraft_amount`. [Evidence: repeat_overdraft_customers.json:10 vs RepeatOverdraftCustomerProcessor.cs:44-45]

**V2 approach:** V2 DataSourcing for overdraft_events sources only `customer_id` and `overdraft_amount`. Five unused columns eliminated.

### AP6: Row-by-Row Iteration -- ELIMINATED

**V1 problem:** V1 uses two `foreach` loops -- one to build a customer lookup dictionary (lines 32-38), another to accumulate overdraft counts and amounts per customer (lines 42-52). Both are set-based operations implementable in SQL. [Evidence: RepeatOverdraftCustomerProcessor.cs:32-52]

**V2 approach:** Replaced with SQL GROUP BY, JOIN, and HAVING. All set-based, no procedural iteration.

### AP7: Magic Values -- ELIMINATED

**V1 problem:** The repeat threshold `2` appears as a bare literal: `if (kvp.Value.count < 2) continue;` with no documentation of business meaning. [Evidence: RepeatOverdraftCustomerProcessor.cs:55-58]

**V2 approach:** The SQL `HAVING COUNT(*) >= 2` includes an inline comment identifying it as the repeat-overdraft business threshold. While SQL does not support named constants, the comment provides the documentation that V1 lacks.

### Other AP-codes -- Not Applicable

| AP-code | Applicable? | Reasoning |
|---------|-------------|-----------|
| AP1 (Dead-end sourcing) | No | Both source DataFrames are used in the processor |
| AP2 (Duplicated logic) | No | No cross-job duplication identified |
| AP5 (Asymmetric NULLs) | No | NULL handling is consistent: missing customer -> empty strings for both first and last name |
| AP8 (Complex SQL / unused CTEs) | No | V1 uses C# not SQL; V2 SQL is straightforward with no unused CTEs |
| AP9 (Misleading names) | No | Job name accurately describes its purpose |
| AP10 (Over-sourcing dates) | No | V1 uses framework effective date injection, not manual date filtering |

## 8. Proofmark Config

```yaml
comparison_target: "repeat_overdraft_customers"
reader: parquet
threshold: 100.0
```

**Justification for strict-only config (no exclusions, no fuzzy):**

- **No non-deterministic fields:** The BRD states "None identified" for non-deterministic fields. All output values are derived deterministically from source data. [Evidence: BRD Non-Deterministic Fields section]
- **No floating-point accumulation issues:** V1 uses `decimal` for monetary amounts (BR-6), so there are no double-precision epsilon concerns requiring fuzzy matching.
- **All columns are deterministic:** `customer_id` (integer pass-through), `first_name`/`last_name` (string lookup), `overdraft_count` (integer count), `total_overdraft_amount` (decimal sum), `as_of` (date scalar).
- **Parquet reader** matches the V1 ParquetFileWriter output format.
- **100.0 threshold** requires every row to match exactly.

## 9. Open Questions

**OQ-1: Row ordering in Parquet output**
V1 output row order is determined by Dictionary insertion order (the order customers are first encountered in overdraft_events, which is ordered by `as_of`). V2 SQL output order is determined by the query's implicit ordering, which may differ. Parquet files do not have an inherent row order semantic, and Proofmark is expected to sort rows before comparison. If Proofmark comparison fails due to row ordering, an explicit `ORDER BY customer_id` can be added to the SQL.
- Risk: LOW
- Mitigation: Add ORDER BY if Proofmark comparison reveals ordering mismatch

**OQ-2: Empty customers table edge case**
V1 returns an empty DataFrame if either overdraft_events OR customers is empty (EC-4). V2's SQL approach would still return overdraft aggregations with empty-string names if customers is empty (the LEFT JOIN with COALESCE handles missing lookups gracefully). This behavioral difference only manifests if datalake.customers has zero rows in the effective date range, which is not expected in practice.
- Risk: VERY LOW
- Mitigation: If this edge case is triggered (highly unlikely given production-like data), a CASE/EXISTS guard can be added to the SQL

**OQ-3: SQLite rowid stability for customer deduplication**
The customer deduplication strategy relies on SQLite rowid reflecting DataSourcing's insertion order (`ORDER BY as_of`). This is well-documented SQLite behavior: rowids are assigned sequentially during INSERT. Since the Transformation module loads DataFrame rows into SQLite in order, rowid ordering matches DataFrame row ordering, which matches DataSourcing's `ORDER BY as_of`. If this assumption is violated, customer name resolution could differ from V1.
- Risk: LOW
- Evidence: SQLite documentation guarantees rowid monotonically increases for sequential inserts. Transformation.cs loads rows in DataFrame order.
- Mitigation: Verify via Proofmark comparison. If name mismatches occur, switch to a subquery using `MAX(as_of)` as the tiebreaker instead of rowid.

**OQ-4: Decimal precision through SQLite**
V1 accumulates overdraft_amount using C# `decimal`. V2 passes the values through SQLite's SUM function. SQLite uses 64-bit IEEE floating point for numeric operations, which could theoretically introduce precision differences for large sums. However, overdraft amounts are typically small monetary values (< $1000), and the sum across a handful of events per customer is unlikely to exceed the precision threshold of IEEE 754 double (~15 significant digits).
- Risk: LOW
- Mitigation: If Proofmark comparison reveals sum discrepancies, add `total_overdraft_amount` as a fuzzy column with a tight absolute tolerance (e.g., 0.01). Alternatively, round in both SQL and C# to 2 decimal places.
