# CustomerAddressDeltas -- Functional Specification Document

## Design Approach

**External module (justified).** This job requires:
1. Direct PostgreSQL access to fetch TWO different date snapshots (current date AND previous day) -- DataSourcing only provides a single date range
2. Snapshot fallback for customer names (DISTINCT ON ... WHERE as_of <= date)
3. Field-by-field comparison between current and previous snapshots with normalization
4. Zero-row sentinel handling for baseline and no-change cases

The two-date comparison pattern and snapshot fallback for customer names cannot be expressed in the framework's SQL Transformation module, which operates on data loaded for a single date.

The V2 External module preserves the exact comparison logic from the original while:
- Adding comments for clarity
- Using cleaner code structure
- Documenting the day-over-day comparison window

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No DataSourcing modules (External does its own queries) |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | External module genuinely justified for two-date comparison + snapshot fallback |
| AP-4    | N                   | N/A                | No DataSourcing modules |
| AP-5    | N                   | N/A                | NULL handling is consistent (customer_name defaults to empty string on miss) |
| AP-6    | N                   | N/A                | Row iteration is necessary for field-by-field comparison |
| AP-7    | Y                   | Y                  | Added comment explaining the -1 day offset for day-over-day comparison |
| AP-8    | N                   | N/A                | No overly complex SQL |
| AP-9    | N                   | N/A                | Name accurately describes delta detection |
| AP-10   | N                   | N/A                | All sources are datalake tables; no inter-job dependencies |

## V2 Pipeline Design

1. **External** - `CustomerAddressDeltaProcessorV2`: two-date comparison, snapshot fallback for names, field change detection, zero-row handling
2. **DataFrameWriter** - Write to `customer_address_deltas` in `double_secret_curated` schema, Append mode

## External Module Design

```
1. Get effective date from shared state
2. Compute previous date = effective date - 1 day (day-over-day comparison window)
3. Fetch current day addresses from datalake.addresses
4. Fetch previous day addresses from datalake.addresses
5. Fetch customer names via snapshot fallback (DISTINCT ON, <= current date)
6. If previous addresses empty (baseline):
   -> Emit null-row sentinel with record_count = 0
7. Else:
   a. Build lookup by address_id for both current and previous
   b. For each current address (ordered by address_id ASC):
      - If not in previous: change_type = "NEW"
      - If in previous and fields changed: change_type = "UPDATED"
      - If unchanged: skip
   c. If zero deltas: emit null-row sentinel with record_count = 0
   d. Else: set record_count on all rows
```

Field comparison normalizes values:
- NULL/DBNull -> empty string
- DateTime/DateOnly -> "yyyy-MM-dd"
- Other values -> trimmed string
- Comparison is case-sensitive (Ordinal)

Compare fields: customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | previous date = currentDate.AddDays(-1) |
| BR-2 | NEW: address_id in current but not previous |
| BR-3 | UPDATED: address_id in both but HasFieldChanged returns true |
| BR-4 | CompareFields array defines the 8 fields checked |
| BR-5 | Normalize() handles NULL, DateTime, DateOnly, and trim |
| BR-6 | FetchCustomerNames uses DISTINCT ON with snapshot fallback |
| BR-7 | Baseline (previous empty): null-row sentinel |
| BR-8 | record_count set to total delta count |
| BR-9 | Zero deltas (previous exists, no changes): null-row sentinel |
| BR-10 | Iteration over currentByAddressId.OrderBy(key) |
| BR-11 | country field trimmed; dates formatted yyyy-MM-dd |
| BR-12 | DataFrameWriter configured with writeMode: Append |
| BR-13 | Only iterates current addresses -- deletions not detected |
