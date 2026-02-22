# CoveredTransactions -- Functional Specification Document

## Design Approach

**External module (justified).** This job requires direct PostgreSQL access with snapshot fallback queries (DISTINCT ON ... ORDER BY as_of DESC) that go beyond the effective date range provided by DataSourcing. The logic involves:
1. Fetching transactions for the exact effective date
2. Resolving accounts via snapshot fallback (most recent <= effective date), filtering to Checking only
3. Resolving customers via snapshot fallback
4. Resolving active US addresses for the effective date (earliest start_date per customer)
5. Resolving customer segments (first alphabetically per customer)
6. Joining all results with specific sort order and record_count computation
7. Zero-row handling with a null-row sentinel

These multi-query patterns with snapshot fallback cannot be expressed in the framework's SQL Transformation module (which operates on in-memory SQLite tables loaded by DataSourcing for a single date range).

The V2 External module cleans up the original by:
- Adding comments explaining business logic and magic values
- Using more descriptive variable names
- Keeping the same proven query patterns and join logic

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N                   | N/A                | No DataSourcing modules (External does its own queries) |
| AP-2    | N                   | N/A                | No duplicated logic |
| AP-3    | N                   | N/A                | External module is genuinely justified for snapshot fallback |
| AP-4    | N                   | N/A                | No DataSourcing modules to trim |
| AP-5    | Y                   | N (documented)     | Intentional asymmetry: account match required (row skipped), customer demographics optional (NULL on miss). Documented. |
| AP-6    | N                   | N/A                | Row iteration is necessary for multi-table join with lookups |
| AP-7    | Y                   | Y                  | Added comments explaining "Checking" = FDIC-insured checking accounts, "US" = US-based addresses |
| AP-8    | N                   | N/A                | SQL queries are appropriately complex for their purpose |
| AP-9    | N                   | N/A                | Name is reasonable (covered = FDIC-covered checking transactions) |
| AP-10   | N                   | N/A                | All sources are datalake tables; no inter-job dependencies |

## V2 Pipeline Design

1. **External** - `CoveredTransactionProcessorV2`: multi-query DB access with snapshot fallback, join logic, sorting, record_count, zero-row handling
2. **DataFrameWriter** - Write to `covered_transactions` in `double_secret_curated` schema, Append mode

## External Module Design

```
1. Get effective date from shared state
2. Query datalake.transactions for the effective date
3. Query datalake.accounts with snapshot fallback (<= date), filter to Checking accounts
4. Query datalake.customers with snapshot fallback (<= date)
5. Query datalake.addresses for active US addresses on the effective date (earliest start_date per customer)
6. Query datalake.customers_segments + datalake.segments for first-alphabetically segment per customer
7. For each transaction:
   - Look up Checking account (skip if not found)
   - Look up customer from account's customer_id
   - Look up active US address (skip if not found)
   - Look up customer demographics (optional, NULL on miss)
   - Look up segment (optional, NULL on miss)
   - Build output row with trimmed strings, formatted dates
8. Sort by customer_id ASC, transaction_id DESC
9. Set record_count on all rows
10. If zero rows, emit null-row sentinel with as_of and record_count = 0
```

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1 | Account lookup filtered to account_type = 'Checking' only |
| BR-2 | Account query uses DISTINCT ON with ORDER BY as_of DESC for snapshot fallback |
| BR-3 | Customer query uses DISTINCT ON with ORDER BY as_of DESC for snapshot fallback |
| BR-4 | Address query filters country = 'US' AND (end_date IS NULL OR end_date >= date) |
| BR-5 | Address query ORDER BY start_date ASC; first per customer wins |
| BR-6 | Segment query uses DISTINCT ON with ORDER BY segment_code ASC |
| BR-7 | Sort: customer_id ASC, transaction_id DESC |
| BR-8 | record_count set to total output row count for the date |
| BR-9 | Zero-row case emits null-row sentinel |
| BR-10 | String fields trimmed; timestamps formatted yyyy-MM-dd HH:mm:ss; dates formatted yyyy-MM-dd |
| BR-11 | DataFrameWriter configured with writeMode: Append |
