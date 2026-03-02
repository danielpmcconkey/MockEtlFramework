# CustomerAddressDeltas -- Functional Specification Document

## 1. Overview

The V2 job (`CustomerAddressDeltasV2`) detects day-over-day changes in customer address records by comparing the current effective date's address snapshot against the previous day's snapshot. It produces delta records (NEW or UPDATED) with change type, full address details, and customer name. Output is Parquet with Append mode and 1 part file, accumulating one delta set per effective date across the full run.

**Tier: 3 (Full External -- LAST RESORT)**
`External -> ParquetFileWriter`

**Tier Justification:** This job cannot be implemented at Tier 1 or Tier 2 for two independent reasons:

1. **Cross-date data access incompatible with DataSourcing:** The framework's auto-advancement executor sets `__minEffectiveDate` and `__maxEffectiveDate` to the same date on each run. DataSourcing therefore pulls data for a single date only. This job requires addresses from TWO dates: `currentDate` and `currentDate - 1` (the previous day). There is no mechanism in DataSourcing to express "current date minus one day" -- the `minEffectiveDate`/`maxEffectiveDate` config fields are static strings, not dynamic expressions. Setting a hardcoded two-day window in the config would not work because the executor advances one day at a time, making the window meaningless.

2. **PostgreSQL-specific SQL for customer names:** The customer name lookup uses `DISTINCT ON (id) ... WHERE as_of <= @date ORDER BY id, as_of DESC` to get the most recent customer name as of the current date. `DISTINCT ON` is a PostgreSQL extension with no SQLite equivalent. While a workaround using window functions (`ROW_NUMBER() OVER (PARTITION BY id ORDER BY as_of DESC)`) could work in SQLite, point #1 already forces an External module, so there is no benefit to splitting the pipeline.

Even at Tier 3, the V2 External module applies all anti-pattern remediation: named constants for compare fields, set-based operations where possible, proper null handling documentation, and clean code structure. The module queries PostgreSQL directly (matching V1's data access pattern) and places results into shared state for the framework's ParquetFileWriter.

---

## 2. V2 Module Chain

### Module 1: External

| Property | Value |
|----------|-------|
| type | External |
| assemblyPath | `/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll` |
| typeName | `ExternalModules.CustomerAddressDeltasV2Processor` |

The External module:
- Reads `__minEffectiveDate` from shared state to determine the current effective date (BR-1)
- Computes `previousDate = currentDate - 1` (BR-1)
- Queries PostgreSQL for address snapshots for both dates (BR-2)
- Queries PostgreSQL for the most recent customer names as of the current date (BR-8)
- Detects NEW and UPDATED deltas (BR-4, BR-5, BR-6)
- Handles baseline day (no previous data) and no-delta cases with null-filled sentinel rows (BR-3, BR-14)
- Stores the result DataFrame as `"output"` in shared state

### Module 2: ParquetFileWriter

| Property | Value |
|----------|-------|
| type | ParquetFileWriter |
| source | output |
| outputDirectory | `Output/double_secret_curated/customer_address_deltas/` |
| numParts | 1 |
| writeMode | Append |

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-code | Applies? | Analysis |
|--------|----------|----------|
| W1 (Sunday skip) | No | No Sunday-specific logic in V1. |
| W2 (Weekend fallback) | No | No weekend date fallback. Previous date is always currentDate - 1 regardless of day of week. |
| W3a/b/c (Boundary rows) | No | No summary rows appended. |
| W4 (Integer division) | No | No division operations. |
| W5 (Banker's rounding) | No | No rounding operations. |
| W6 (Double epsilon) | No | No floating-point accumulation. |
| W7 (Trailer inflated count) | No | Parquet writer, no trailers. |
| W8 (Trailer stale date) | No | Parquet writer, no trailers. |
| W9 (Wrong writeMode) | No | Append mode is correct for this job -- historical deltas accumulate across effective dates. This is intentional and appropriate. |
| W10 (Absurd numParts) | No | 1 part file is reasonable. |
| W12 (Header every append) | No | Parquet writer, not CSV. |

**Conclusion:** No W-codes apply to this job.

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| **AP3** | **NO** | V1 uses an External module, but this is NOT an unnecessary use. The job's data access pattern (cross-date comparison, `DISTINCT ON`) genuinely cannot be expressed in the framework's DataSourcing + Transformation chain. Tier 3 is justified. | N/A -- External module is necessary. |
| **AP6** | **YES** | V1 uses row-by-row `foreach` iteration with dictionary lookups for delta detection [CustomerAddressDeltaProcessor.cs:76-110]. The address iteration and dictionary build are inherently procedural (comparing two sets by key), but the code uses clean dictionary-based lookup rather than nested loops. | **Partially addressed.** The delta detection pattern (iterate current addresses, look up in previous-day dictionary) is inherently procedural -- SQL cannot express this cross-date comparison within the framework's constraints. V2 retains the dictionary-based approach but uses LINQ and cleaner data structures. The customer name fetch already uses set-based SQL. |
| **AP7** | **YES** | The compare fields array is defined inline without documentation [CustomerAddressDeltaProcessor.cs:10-14]. The format string "yyyy-MM-dd" appears as a repeated magic string. | **Eliminated.** V2 uses named constants: `CompareFields` array with a documenting comment explaining these are the fields checked for delta detection, and a `DateFormat` constant for the date format string. |
| AP1 (Dead-end sourcing) | No | V1 has no DataSourcing modules -- the External does all data access. All fetched data is used. |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. |
| AP4 (Unused columns) | No | All fetched columns (address_id, customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date from addresses; id, first_name, last_name from customers) are used in either comparison or output. |
| AP5 (Asymmetric NULLs) | No | NULL handling is consistent. The `Normalize` function treats null/DBNull as empty string for comparison [CustomerAddressDeltaProcessor.cs:215]. `FormatDate` returns null for null/DBNull values [CustomerAddressDeltaProcessor.cs:223]. Customer name defaults to empty string for unknown customers [CustomerAddressDeltaProcessor.cs:92]. These are all consistent within their respective contexts. |
| AP8 (Complex SQL) | No | V1 has no SQL transformations. V2 External module uses straightforward SQL queries. |
| AP9 (Misleading names) | No | Job name accurately describes output (customer address deltas). |
| AP10 (Over-sourcing dates) | No | V1 fetches only the specific dates needed (current and previous). V2 replicates this precise querying. |

---

## 4. Output Schema

| Column | Type | Source | Transformation | V1 Evidence |
|--------|------|--------|---------------|-------------|
| change_type | string | Computed | "NEW" or "UPDATED"; null on baseline/no-delta sentinel rows | [CustomerAddressDeltaProcessor.cs:78-86] |
| address_id | int (nullable) | addresses.address_id | Passthrough; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:97] |
| customer_id | int (nullable) | addresses.customer_id | Passthrough; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:98] |
| customer_name | string (nullable) | customers.first_name + " " + customers.last_name | Concatenated with space; empty string if customer not found; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:92-93,99] |
| address_line1 | string (nullable) | addresses.address_line1 | Passthrough; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:100] |
| city | string (nullable) | addresses.city | Passthrough; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:101] |
| state_province | string (nullable) | addresses.state_province | Passthrough; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:102] |
| postal_code | string (nullable) | addresses.postal_code | Passthrough; null on sentinel rows | [CustomerAddressDeltaProcessor.cs:103] |
| country | string (nullable) | addresses.country | **Trimmed** (`.Trim()`); null on sentinel rows | [CustomerAddressDeltaProcessor.cs:104] |
| start_date | string (nullable) | addresses.start_date | Formatted as "yyyy-MM-dd" string; null if source is null | [CustomerAddressDeltaProcessor.cs:105] |
| end_date | string (nullable) | addresses.end_date | Formatted as "yyyy-MM-dd" string; null if source is null | [CustomerAddressDeltaProcessor.cs:106] |
| as_of | string | __minEffectiveDate | Formatted as "yyyy-MM-dd" string; always present (even on sentinel rows) | [CustomerAddressDeltaProcessor.cs:107] |
| record_count | int | Computed | Total count of delta rows; 0 on baseline/no-delta sentinel rows; stamped on every row | [CustomerAddressDeltaProcessor.cs:112,137-140] |

**Column order matters.** V1 defines column order via the `OutputColumns` list [CustomerAddressDeltaProcessor.cs:16-21]: `change_type, address_id, customer_id, customer_name, address_line1, city, state_province, postal_code, country, start_date, end_date, as_of, record_count`. V2 must preserve this exact order.

---

## 5. SQL Design

Not applicable for the Transformation module (Tier 3 -- no Transformation module in chain).

However, the External module uses two PostgreSQL queries:

### Query 1: Fetch Addresses

```sql
SELECT address_id, customer_id, address_line1, city, state_province,
       postal_code, country, start_date, end_date
FROM datalake.addresses
WHERE as_of = @date
ORDER BY address_id
```

This query is issued twice: once for `currentDate` and once for `previousDate`. The `ORDER BY address_id` ensures consistent retrieval order. The `@date` parameter is bound as a `DateTime` converted from `DateOnly` via `ToDateTime(TimeOnly.MinValue)`.

Evidence: [CustomerAddressDeltaProcessor.cs:149-154]

### Query 2: Fetch Customer Names

```sql
SELECT DISTINCT ON (id) id, first_name, last_name
FROM datalake.customers
WHERE as_of <= @date
ORDER BY id, as_of DESC
```

This returns the most recent customer name for each customer as of the current date. `DISTINCT ON (id)` with `ORDER BY id, as_of DESC` picks the row with the latest `as_of` for each customer id.

Evidence: [CustomerAddressDeltaProcessor.cs:177-181]

---

## 6. V2 Job Config

```json
{
  "jobName": "CustomerAddressDeltasV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "External",
      "assemblyPath": "/workspace/MockEtlFramework/ExternalModules/bin/Debug/net8.0/ExternalModules.dll",
      "typeName": "ExternalModules.CustomerAddressDeltasV2Processor"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/customer_address_deltas/",
      "numParts": 1,
      "writeMode": "Append"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | YES |
| source | output | output | YES |
| outputDirectory | `Output/curated/customer_address_deltas/` | `Output/double_secret_curated/customer_address_deltas/` | Path changed per V2 spec |
| numParts | 1 | 1 | YES |
| writeMode | Append | Append | YES |

---

## 8. Proofmark Config Design

### Recommended Config: Default Strict

```yaml
comparison_target: "customer_address_deltas"
reader: parquet
threshold: 100.0
```

### Exclusions: None

No columns are non-deterministic. The BRD confirms: "Non-Deterministic Fields: None identified." All values are derived deterministically from database content and the effective date.

### Fuzzy Columns: None

No floating-point arithmetic is performed. `record_count` is an integer. All other fields are strings or integer passthroughs. No epsilon differences are expected.

### Rationale

Starting with zero exclusions and zero fuzzy overrides per the BLUEPRINT's prescription. All 13 output columns are deterministic:
- `change_type`: deterministic comparison result
- `address_id`, `customer_id`, `address_line1`, `city`, `state_province`, `postal_code`, `country`: deterministic passthroughs (country is trimmed but that's deterministic)
- `customer_name`: deterministic concatenation of most-recent name
- `start_date`, `end_date`: deterministic date formatting
- `as_of`: deterministic date formatting from effective date
- `record_count`: deterministic count of delta rows

If proofmark comparison reveals differences, investigate root cause rather than masking with overrides.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|--------------|-----------------|----------|
| Tier 3 (External + Writer) | BR-2 (direct DB query, no DataSourcing) | V1 bypasses DataSourcing; cross-date access and DISTINCT ON require PostgreSQL [CustomerAddressDeltaProcessor.cs:28-33] |
| Read `__minEffectiveDate` for current date | BR-1 | `sharedState[DataSourcing.MinDateKey]` [CustomerAddressDeltaProcessor.cs:25] |
| Compute previousDate as currentDate - 1 | BR-1 | `currentDate.AddDays(-1)` [CustomerAddressDeltaProcessor.cs:26] |
| Fetch addresses for both dates via PostgreSQL | BR-2 | Direct NpgsqlConnection queries [CustomerAddressDeltaProcessor.cs:28-32] |
| Baseline day sentinel row (nulls + as_of + record_count=0) | BR-3 | previousAddresses.Count == 0 guard [CustomerAddressDeltaProcessor.cs:36-56] |
| NEW detection: address_id in current but not previous | BR-4 | `!previousByAddressId.TryGetValue` [CustomerAddressDeltaProcessor.cs:80-82] |
| UPDATED detection: field change detected | BR-5 | `HasFieldChanged` [CustomerAddressDeltaProcessor.cs:83-86] |
| Compare fields: customer_id, address_line1, city, state_province, postal_code, country, start_date, end_date | BR-6 | `CompareFields` array [CustomerAddressDeltaProcessor.cs:10-14] |
| No DELETED detection | BR-7 | Only iterates currentByAddressId [CustomerAddressDeltaProcessor.cs:76] |
| Customer names via DISTINCT ON (most recent as of current date) | BR-8 | SQL with DISTINCT ON [CustomerAddressDeltaProcessor.cs:177-180] |
| Customer name = "first_name last_name" | BR-9 | `$"{firstName} {lastName}"` [CustomerAddressDeltaProcessor.cs:194] |
| Country field trimmed | BR-10 | `.Trim()` on country [CustomerAddressDeltaProcessor.cs:104] |
| Date fields formatted "yyyy-MM-dd" | BR-11 | `FormatDate` method [CustomerAddressDeltaProcessor.cs:221-227] |
| as_of stored as string "yyyy-MM-dd" | BR-12 | `currentDate.ToString("yyyy-MM-dd")` [CustomerAddressDeltaProcessor.cs:107] |
| record_count stamped on every row | BR-13 | Loop at [CustomerAddressDeltaProcessor.cs:137-140] |
| No-delta sentinel row (nulls + as_of + record_count=0) | BR-14 | No-delta guard [CustomerAddressDeltaProcessor.cs:114-133] |
| Delta rows ordered by address_id ascending | BR-15 | `.OrderBy(kv => kv.Key)` [CustomerAddressDeltaProcessor.cs:76] |
| Normalize: trim strings, format dates, null->empty for comparison | BR-16 | `Normalize` method [CustomerAddressDeltaProcessor.cs:213-219] |
| Unknown customer -> empty string name | BRD Edge Case | `GetValueOrDefault(customerId, "")` [CustomerAddressDeltaProcessor.cs:92] |
| Append write mode preserves historical deltas | BRD Write Mode Implications | [customer_address_deltas.json:15] |
| AP6 partially addressed (dictionary-based is inherently procedural) | AP6 | Cross-date comparison not expressible in framework SQL |
| AP7 eliminated (named constants) | AP7 | V2 uses `CompareFields` with documenting comment, `DateFormat` constant |
| Proofmark: default strict, no exclusions | BRD Non-Deterministic Fields: None | All output columns are deterministic |

---

## 10. External Module Design

### Class: `CustomerAddressDeltasV2Processor`

**File:** `ExternalModules/CustomerAddressDeltasV2Processor.cs`
**Namespace:** `ExternalModules`
**Implements:** `IExternalStep`

### Constants

```csharp
/// <summary>
/// Fields compared between current and previous day address snapshots
/// to determine if an address has been UPDATED.
/// </summary>
private static readonly string[] CompareFields =
{
    "customer_id", "address_line1", "city", "state_province",
    "postal_code", "country", "start_date", "end_date"
};

/// <summary>
/// Output column order -- must match V1 exactly for Parquet schema compatibility.
/// </summary>
private static readonly List<string> OutputColumns = new()
{
    "change_type", "address_id", "customer_id", "customer_name",
    "address_line1", "city", "state_province", "postal_code",
    "country", "start_date", "end_date", "as_of", "record_count"
};

/// <summary>
/// Date format for as_of, start_date, and end_date output fields.
/// </summary>
private const string DateFormat = "yyyy-MM-dd";
```

### Execute Method Flow

1. **Read effective date:** `var currentDate = (DateOnly)sharedState[DataSourcing.MinDateKey]` (BR-1)
2. **Compute previous date:** `var previousDate = currentDate.AddDays(-1)` (BR-1)
3. **Open PostgreSQL connection** via `ConnectionHelper.GetConnectionString()`
4. **Fetch data:**
   - `FetchAddresses(connection, currentDate)` -- current snapshot
   - `FetchAddresses(connection, previousDate)` -- previous snapshot
   - `FetchCustomerNames(connection, currentDate)` -- most recent names
5. **Baseline guard:** If `previousAddresses.Count == 0`, produce a single null-filled sentinel row with `as_of = currentDate` formatted as string and `record_count = 0`. Store as DataFrame in `sharedState["output"]` and return. (BR-3)
6. **Build lookup dictionaries:** Key both current and previous address lists by `address_id` (int). (BR-15 ordering applied during iteration, not here)
7. **Detect deltas:** Iterate `currentByAddressId` ordered by key ascending (BR-15):
   - If address_id not in previous: `changeType = "NEW"` (BR-4)
   - If address_id in both and `HasFieldChanged`: `changeType = "UPDATED"` (BR-5)
   - If no change: skip
   - For each delta: look up `customerName` via `customerNames.GetValueOrDefault(customerId, "")` (BR-9, BRD Edge Case)
   - Build output row with all fields (BR-10: trim country; BR-11: format dates; BR-12: as_of as string)
8. **No-delta guard:** If `deltaRows.Count == 0`, produce a single null-filled sentinel row with `as_of` and `record_count = 0`. (BR-14)
9. **Stamp record_count:** Set `record_count = deltaRows.Count` on every row. (BR-13)
10. **Store result:** `sharedState["output"] = new DataFrame(deltaRows, OutputColumns)`

### Helper Methods

**`FetchAddresses(NpgsqlConnection connection, DateOnly asOfDate) -> List<Dictionary<string, object?>>`**
- Executes Query 1 (Section 5)
- Returns list of dictionaries, one per address row
- Parameterizes date as DateTime via `asOfDate.ToDateTime(TimeOnly.MinValue)`

**`FetchCustomerNames(NpgsqlConnection connection, DateOnly asOfDate) -> Dictionary<int, string>`**
- Executes Query 2 (Section 5)
- Returns dictionary mapping customer ID to "first_name last_name" string
- Uses `DISTINCT ON (id)` to get most recent name (BR-8)

**`HasFieldChanged(Dictionary<string, object?> current, Dictionary<string, object?> previous) -> bool`**
- Iterates `CompareFields` array (BR-6)
- Normalizes both values via `Normalize()` and compares with `StringComparison.Ordinal`
- Returns true on first difference

**`Normalize(object? value) -> string`**
- null / DBNull -> `""` (empty string) (BR-16)
- DateTime -> `DateOnly.FromDateTime(dt).ToString(DateFormat)` (BR-16)
- DateOnly -> `d.ToString(DateFormat)` (BR-16)
- Other -> `value.ToString()?.Trim() ?? ""` (BR-16)

**`FormatDate(object? value) -> string?`**
- null / DBNull -> `null` (BR-11)
- DateTime -> `DateOnly.FromDateTime(dt).ToString(DateFormat)` (BR-11)
- DateOnly -> `d.ToString(DateFormat)` (BR-11)
- Other -> `value.ToString()` (BR-11)

### Differences from V1

| Aspect | V1 | V2 | Rationale |
|--------|----|----|-----------|
| Date format string | Inline `"yyyy-MM-dd"` repeated 6 times | `DateFormat` constant | AP7: eliminate magic values |
| CompareFields documentation | No comment | Documenting comment explaining purpose | AP7: document magic values |
| OutputColumns documentation | No comment | Documenting comment explaining purpose and V1 compatibility requirement | AP7: document magic values |
| `record_count` during row construction | Set to `deltaRows.Count` (running index during add) then overwritten | Set to placeholder `0` during construction, then overwritten with final count | Clearer intent -- the intermediate value was never meaningful in V1 (BRD OQ-2) |

### Output Equivalence Guarantees

The V2 External module produces byte-identical output to V1 because:
- Same PostgreSQL queries with same parameters
- Same comparison logic (CompareFields, Normalize, HasFieldChanged)
- Same output column order (OutputColumns list)
- Same sentinel row structure for baseline/no-delta cases
- Same country trimming, date formatting, customer name concatenation
- Same ordering (address_id ascending)
- Same record_count stamping behavior
