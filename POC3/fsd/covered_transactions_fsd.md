# CoveredTransactions — Functional Specification Document

## 1. Overview & Tier Justification

**Job:** CoveredTransactionsV2
**Tier:** Tier 3 — Full External (External -> Writer)

This job produces a denormalized view of transactions for Checking account holders who have an active US address on the effective date, enriched with customer demographics, address, account, and segment information. The External module assembles the data; the framework's ParquetFileWriter handles output.

### Tier 3 Justification

Tier 1 (DataSourcing -> Transformation -> Writer) is **not feasible** for two independent reasons:

1. **Snapshot fallback requires unbounded lower date range.** The accounts and customers tables use snapshot fallback (`as_of <= @date` with no lower bound). DataSourcing always applies `WHERE as_of >= @minDate AND as_of <= @maxDate` — it cannot express an open-ended lower bound. The executor injects `__minEffectiveDate` as the current effective date, so DataSourcing would only return rows for the exact date, missing historical snapshots.

2. **PostgreSQL `DISTINCT ON` has no SQLite equivalent.** The snapshot fallback pattern uses `DISTINCT ON (account_id) ... ORDER BY account_id, as_of DESC` to select the most recent snapshot per entity. SQLite (used by the Transformation module) does not support `DISTINCT ON`. While `ROW_NUMBER() OVER (PARTITION BY ...)` could substitute, we can't even get the data into SQLite to run that query (see point 1).

Tier 2 is also impractical — three of five data sources (accounts, customers, segments) require PostgreSQL-specific query patterns. An External module that handles only the queries but delegates to Transformation for joins would gain nothing: the join logic in C# is straightforward dictionary lookups, and splitting it across two modules would add complexity without benefit.

**Conclusion:** An External module that executes all PostgreSQL queries directly, performs in-memory joins, and places the result DataFrame into shared state for ParquetFileWriter is the cleanest design.

---

## 2. V2 Module Chain

```
External (CoveredTransactionsV2Processor)
  -> ParquetFileWriter (output -> Output/double_secret_curated/covered_transactions/)
```

### Module 1: External
- **Assembly:** `ExternalModules/bin/Debug/net8.0/ExternalModules.dll`
- **Type:** `ExternalModules.CoveredTransactionsV2Processor`
- **Inputs:** Reads `__minEffectiveDate` from shared state
- **Outputs:** Places a DataFrame named `output` into shared state
- **Queries:** 5 PostgreSQL queries against `datalake.*` (see Section 5)

### Module 2: ParquetFileWriter
- **Source:** `output`
- **Output Directory:** `Output/double_secret_curated/covered_transactions/`
- **numParts:** 4
- **writeMode:** Append

---

## 3. Anti-Pattern Analysis

### Applicable Anti-Patterns Identified

| Code | Name | Applies? | V2 Action |
|------|------|----------|-----------|
| AP3 | Unnecessary External | **No** | External is justified — Tier 3 is required (see Section 1). DataSourcing cannot express snapshot fallback queries. |
| AP6 | Row-by-row iteration | **Partially** | V1 uses `foreach` loops to populate dictionaries and join data. The dictionary-based lookup pattern is O(1) per transaction — this is NOT the nested-loop anti-pattern AP6 targets. However, V2 will use cleaner LINQ-based dictionary construction where possible. |
| AP1 | Dead-end sourcing | **No** | All five queries contribute to output. No unused data sources. |
| AP4 | Unused columns | **No** | Every column fetched is used in the output schema. |
| AP7 | Magic values | **Partially** | V1 hardcodes `"Checking"`, `"US"` as inline string literals. V2 will use named constants with comments. |
| AP10 | Over-sourcing dates | **No** | V1 External queries use precise date filters. No over-sourcing. |

### Applicable Output-Affecting Wrinkles

| Code | Name | Applies? | V2 Action |
|------|------|----------|-----------|
| W1-W12 | All wrinkle codes | **None apply** | No Sunday skips, weekend fallback, summary rows, integer division, banker's rounding, double epsilon, trailer issues, stale dates, wrong write modes, or absurd part counts identified in V1 code. |

### BRD Correction: BR-1 Account Type Filter

**CRITICAL:** The BRD states that both Checking and Savings accounts are included (BR-1), citing `[CoveredTransactionProcessor.cs:44]` with evidence text showing `== "Checking" || ... == "Savings"`. However, the actual V1 source code at line 44 reads:

```csharp
if (row["account_type"]?.ToString() == "Checking")
```

**V1 only filters for Checking accounts.** The BRD's evidence text is incorrect — the `|| ... == "Savings"` condition does not exist in V1 code. The variable is named `checkingAccounts`, further confirming that only Checking was intended.

**V2 must match V1's actual behavior: Checking only.** BR-1 should be corrected to:
> BR-1: Only transactions linked to **Checking** accounts are included.

This is documented here rather than silently implemented to maintain traceability. The BRD error does not affect output equivalence — V2 will reproduce V1's exact filtering logic.

---

## 4. Output Schema

24 columns, ordered as follows:

| # | Column | Source | Type | Transformation |
|---|--------|--------|------|----------------|
| 1 | transaction_id | transactions.transaction_id | int | Direct |
| 2 | txn_timestamp | transactions.txn_timestamp | string | Formatted as `yyyy-MM-dd HH:mm:ss` |
| 3 | txn_type | transactions.txn_type | string | Trimmed |
| 4 | amount | transactions.amount | decimal | Direct |
| 5 | description | transactions.description | string | Trimmed |
| 6 | customer_id | accounts.customer_id | int | Direct (via account lookup) |
| 7 | name_prefix | customers.prefix | string | Trimmed; null if customer not found |
| 8 | first_name | customers.first_name | string | Trimmed; null if customer not found |
| 9 | last_name | customers.last_name | string | Trimmed; null if customer not found |
| 10 | sort_name | customers.sort_name | string | Trimmed; null if customer not found |
| 11 | name_suffix | customers.suffix | string | Trimmed; null if customer not found |
| 12 | customer_segment | segments.segment_code | string | First alphabetically per customer; null if no mapping |
| 13 | address_id | addresses.address_id | int | Direct |
| 14 | address_line1 | addresses.address_line1 | string | Trimmed |
| 15 | city | addresses.city | string | Trimmed |
| 16 | state_province | addresses.state_province | string | Trimmed |
| 17 | postal_code | addresses.postal_code | string | Trimmed |
| 18 | country | addresses.country | string | Trimmed (always "US" due to filter) |
| 19 | account_id | accounts.account_id | int | Direct |
| 20 | account_type | accounts.account_type | string | Trimmed (always "Checking" due to filter) |
| 21 | account_status | accounts.account_status | string | Trimmed |
| 22 | account_opened | accounts.open_date | string | Formatted as `yyyy-MM-dd` |
| 23 | as_of | Effective date | string | Formatted as `yyyy-MM-dd` |
| 24 | record_count | Computed | int | Total qualifying row count; 0 for null-placeholder row |

---

## 5. SQL / Query Design

The External module executes 5 PostgreSQL queries. All use a parameterized `@date` value derived from `__minEffectiveDate`.

### Query 1: Transactions (exact date)
```sql
SELECT transaction_id, account_id, txn_timestamp, txn_type, amount, description
FROM datalake.transactions
WHERE as_of = @date
```
- **Date pattern:** Exact match (`as_of = @date`)
- **Rationale:** Transactions are point-in-time; no snapshot fallback needed. [BRD BR-13]

### Query 2: Accounts (snapshot fallback)
```sql
SELECT DISTINCT ON (account_id)
       account_id, customer_id, account_type, account_status, open_date
FROM datalake.accounts
WHERE as_of <= @date
ORDER BY account_id, as_of DESC
```
- **Date pattern:** Snapshot fallback (`as_of <= @date`, most recent per account_id)
- **Post-filter:** Only Checking accounts retained in-memory [BRD BR-1 corrected, BR-3]
- **Rationale:** Uses most recent account snapshot on or before the effective date

### Query 3: Customers (snapshot fallback)
```sql
SELECT DISTINCT ON (id)
       id, prefix, first_name, last_name, sort_name, suffix
FROM datalake.customers
WHERE as_of <= @date
ORDER BY id, as_of DESC
```
- **Date pattern:** Snapshot fallback (`as_of <= @date`, most recent per customer id)
- **Rationale:** Uses most recent customer snapshot on or before the effective date [BRD BR-4]

### Query 4: Addresses (exact date, active US only)
```sql
SELECT address_id, customer_id, address_line1, city, state_province,
       postal_code, country, start_date
FROM datalake.addresses
WHERE as_of = @date
  AND country = 'US'
  AND (end_date IS NULL OR end_date >= @date)
ORDER BY customer_id, start_date ASC
```
- **Date pattern:** Exact match (`as_of = @date`) — no snapshot fallback [BRD BR-5]
- **Filters:** `country = 'US'` and active address (`end_date IS NULL OR end_date >= @date`) [BRD BR-2]
- **Ordering:** `customer_id, start_date ASC` — first row per customer_id is the earliest active address [BRD BR-6]

### Query 5: Segments (exact date, deduplicated)
```sql
SELECT DISTINCT ON (cs.customer_id)
       cs.customer_id, s.segment_code
FROM datalake.customers_segments cs
JOIN datalake.segments s ON cs.segment_id = s.segment_id AND s.as_of = cs.as_of
WHERE cs.as_of = @date
ORDER BY cs.customer_id, s.segment_code ASC
```
- **Date pattern:** Exact match (`cs.as_of = @date`)
- **Deduplication:** First alphabetical segment_code per customer [BRD BR-7]

### In-Memory Join Logic

After executing all 5 queries, the External module performs the following joins:

1. Build dictionary: `accountId -> account` (Checking only)
2. Build dictionary: `customerId -> customer`
3. Build dictionary: `customerId -> address` (first per customer due to ORDER BY)
4. Build dictionary: `customerId -> segmentCode`
5. For each transaction:
   - Look up account by `account_id` — skip if not found (not a Checking account)
   - Get `customer_id` from the account
   - Look up address by `customer_id` — skip if not found (no active US address)
   - Look up customer demographics (null-safe if not found)
   - Look up segment (null if not found)
   - Assemble output row

6. Sort: `customer_id ASC, transaction_id DESC` [BRD BR-8]
7. Set `record_count` on all rows [BRD BR-9]
8. If zero qualifying rows: emit single null-placeholder row with `as_of` and `record_count = 0` [BRD BR-9]

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CoveredTransactionsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CoveredTransactionsV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/covered_transactions/",
      "numParts": 4,
      "writeMode": "Append"
    }
  ]
}
```

**Changes from V1:**
- `jobName`: `CoveredTransactions` -> `CoveredTransactionsV2`
- `typeName`: `ExternalModules.CoveredTransactionProcessor` -> `ExternalModules.CoveredTransactionsV2Processor`
- `outputDirectory`: `Output/curated/covered_transactions/` -> `Output/double_secret_curated/covered_transactions/`
- All other parameters preserved exactly: `numParts: 4`, `writeMode: "Append"`, `source: "output"`

---

## 7. Writer Configuration

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| source | `output` | `output` | Yes |
| numParts | 4 | 4 | Yes |
| writeMode | Append | Append | Yes |
| outputDirectory | `Output/curated/covered_transactions/` | `Output/double_secret_curated/covered_transactions/` | Path change only |

**Write mode implications:** Append mode means each effective date run adds 4 new part files to the directory. Over the full date range (2024-10-01 through 2024-12-31), the directory accumulates `4 * N` part files for N days of data. Part file naming uses the framework's incrementing scheme (`part-00000.parquet`, `part-00001.parquet`, etc.).

---

## 8. Proofmark Config Design

```yaml
comparison_target: "covered_transactions"
reader: parquet
threshold: 100.0
```

**Rationale for strict configuration (zero exclusions, zero fuzzy):**
- All 24 output columns are deterministic — no timestamps generated at runtime, no random values, no execution-dependent fields.
- The `as_of` column is derived from the effective date (deterministic input).
- The `record_count` column is a count of qualifying rows (deterministic).
- No floating-point accumulation is involved (`amount` is passed through directly, not computed).
- String trimming is deterministic.
- Date/timestamp formatting is deterministic.

No columns require EXCLUDED or FUZZY treatment. A 100% match threshold is expected and required.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision |
|-----------------|-------------|-----------------|
| BR-1 (Checking filter) | Section 3 (correction), Section 5 (Query 2 post-filter) | **CORRECTED:** V1 filters Checking only, not Checking+Savings as BRD claims. V2 matches V1 actual behavior. |
| BR-2 (Active US address) | Section 5 (Query 4, join step 5) | Address query filters `country = 'US'` and active date; join skips transactions without matching address. |
| BR-3 (Account snapshot fallback) | Section 5 (Query 2) | `DISTINCT ON (account_id) ... ORDER BY account_id, as_of DESC` with `as_of <= @date`. |
| BR-4 (Customer snapshot fallback) | Section 5 (Query 3) | `DISTINCT ON (id) ... ORDER BY id, as_of DESC` with `as_of <= @date`. |
| BR-5 (Address exact date) | Section 5 (Query 4) | `as_of = @date` — no snapshot fallback. |
| BR-6 (Earliest address) | Section 5 (Query 4, join step 3) | `ORDER BY customer_id, start_date ASC`; first row per customer retained. |
| BR-7 (First alphabetical segment) | Section 5 (Query 5) | `DISTINCT ON (cs.customer_id) ... ORDER BY cs.customer_id, s.segment_code ASC`. |
| BR-8 (Sort order) | Section 5 (join step 6) | `customer_id ASC, transaction_id DESC`. |
| BR-9 (record_count + zero-row) | Section 5 (join steps 7-8) | Count of qualifying rows applied to every row; null-placeholder row emitted if count is zero. |
| BR-10 (String trimming) | Section 4 (Transformation column), Section 5 (join step 5) | `.Trim()` on all string fields in output row assembly. |
| BR-11 (Timestamp/date formatting) | Section 4 (columns 2, 22), Section 5 (join step 5) | `yyyy-MM-dd HH:mm:ss` for txn_timestamp, `yyyy-MM-dd` for account_opened. |
| BR-12 (Effective date source) | Section 5 (query design preamble) | Read from `DataSourcing.MinDateKey` (`__minEffectiveDate`). |
| BR-13 (Transaction exact date vs. snapshot) | Section 5 (Query 1 vs. Queries 2-3) | Transactions use `as_of = @date`; accounts/customers use `as_of <= @date`. |

---

## 10. External Module Design: CoveredTransactionsV2Processor

### Class Structure

```
ExternalModules.CoveredTransactionsV2Processor : IExternalStep
```

### Named Constants (AP7 elimination)

```csharp
private const string CoveredAccountType = "Checking";       // Only Checking accounts qualify
private const string RequiredCountry = "US";                 // Only active US addresses qualify
private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";
private const string DateFormat = "yyyy-MM-dd";
```

### Output Column List

Static readonly list of 24 column names in exact order matching V1 output schema (Section 4).

### Execute Method Flow

1. Read effective date from `sharedState[DataSourcing.MinDateKey]`
2. Open PostgreSQL connection
3. Execute Query 1 (transactions) -> `List<Dictionary<string, object?>>`
4. Execute Query 2 (accounts with snapshot fallback) -> filter to Checking -> `Dictionary<int, Dictionary<string, object?>>` keyed by account_id
5. Execute Query 3 (customers with snapshot fallback) -> `Dictionary<int, Dictionary<string, object?>>` keyed by customer id
6. Execute Query 4 (active US addresses) -> `Dictionary<int, Dictionary<string, object?>>` keyed by customer_id (first per customer wins)
7. Execute Query 5 (segments deduplicated) -> `Dictionary<int, string>` keyed by customer_id
8. Join loop: for each transaction, look up account -> customer -> address -> segment, assemble output row with trimming and formatting
9. Sort output rows: customer_id ASC, transaction_id DESC
10. Set record_count on all rows (or emit null-placeholder if zero rows)
11. Place DataFrame into `sharedState["output"]`

### Key Improvements Over V1

| Aspect | V1 | V2 |
|--------|----|----|
| Account type filter | Inline string literal `"Checking"` | Named constant `CoveredAccountType` |
| Country filter | Inline string literal `"US"` | Named constant `RequiredCountry` |
| Date format strings | Inline literals in helper methods | Named constants `TimestampFormat`, `DateFormat` |
| Dictionary construction | Manual foreach with ContainsKey | LINQ `.ToDictionary()` or `.TryAdd()` where cleaner |
| Code structure | Single monolithic Execute method | Same flow but with named constants and clear section comments |

### Behavior Preserved Exactly

- Same 5 PostgreSQL queries (identical SQL)
- Same dictionary-based join logic
- Same sort order
- Same record_count computation
- Same zero-row null-placeholder behavior
- Same string trimming on all string output fields
- Same timestamp/date formatting
- Same output column order

### Edge Cases Handled

1. **Zero qualifying rows:** Single null-placeholder row with `as_of` set and `record_count = 0`. All other fields null.
2. **Customer not in customers table:** Name fields (prefix, first_name, last_name, sort_name, suffix) are null. Transaction is still included.
3. **Customer with no segment:** `customer_segment` is null. Transaction is still included.
4. **Multiple Checking accounts per customer:** Each account's transactions included independently.
5. **Account/customer with no snapshot on or before effective date:** Not in lookup dictionary; associated transactions excluded.
6. **Address end_date handling:** `end_date IS NULL` (open-ended) or `end_date >= @date` (still active) qualify. Expired addresses excluded.

---

## Appendix: V1 Source File Reference

- **V1 Job Config:** `JobExecutor/Jobs/covered_transactions.json`
- **V1 External Module:** `ExternalModules/CoveredTransactionProcessor.cs` (239 lines)
- **V2 External Module:** `ExternalModules/CoveredTransactionsV2Processor.cs` (to be created)
- **V2 Job Config:** `JobExecutor/Jobs/covered_transactions_v2.json` (to be created)
