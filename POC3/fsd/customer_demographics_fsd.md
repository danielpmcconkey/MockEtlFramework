# CustomerDemographics -- Functional Specification Document

## 1. Overview & Tier Selection

**Job:** CustomerDemographicsV2
**Config:** `customer_demographics_v2.json`
**Tier:** Tier 1 -- Framework Only (`DataSourcing -> Transformation (SQL) -> CsvFileWriter`)

This job produces a per-customer demographics record including personal details, computed age and age bracket, and primary contact information (phone and email). Ages are calculated relative to the effective date. Output is a CSV with CRLF line endings.

**Tier Justification:** All V1 business logic can be expressed in SQL:
- Age calculation uses date arithmetic (year difference with birthday adjustment) -- achievable via SQLite `strftime` functions.
- Age bracket assignment uses range-based CASE/WHEN -- directly expressible in SQL.
- "First phone/email per customer" uses a first-encountered selection -- achievable via `MIN(rowid)` in SQLite, which preserves the DataFrame insertion order from DataSourcing.
- All other operations are pass-through or simple coalesce. No procedural logic is required.

The V1 External module (`CustomerDemographicsBuilder.cs`) is a textbook AP3 violation -- unnecessary C# where SQL suffices. Tier 1 eliminates it entirely.

## 2. V2 Module Chain

```
DataSourcing("customers")       -- datalake.customers: id, first_name, last_name, birthdate
DataSourcing("phone_numbers")   -- datalake.phone_numbers: phone_id, customer_id, phone_number
DataSourcing("email_addresses") -- datalake.email_addresses: email_id, customer_id, email_address
Transformation("output")        -- SQL: join, compute age/bracket, select first phone/email
CsvFileWriter                   -- Output/double_secret_curated/customer_demographics.csv
```

### Module Details

**DataSourcing #1 -- customers:**
- resultName: `customers`
- schema: `datalake`
- table: `customers`
- columns: `["id", "first_name", "last_name", "birthdate"]`
- Effective dates: injected by executor (no config override)
- Change from V1: Removed unused columns `prefix`, `sort_name`, `suffix` (AP4 elimination)

**DataSourcing #2 -- phone_numbers:**
- resultName: `phone_numbers`
- schema: `datalake`
- table: `phone_numbers`
- columns: `["phone_id", "customer_id", "phone_number"]`
- Effective dates: injected by executor
- Change from V1: Removed unused columns `phone_type` (AP4 elimination). Retained `phone_id` for deterministic first-phone selection via ordering.

**DataSourcing #3 -- email_addresses:**
- resultName: `email_addresses`
- schema: `datalake`
- table: `email_addresses`
- columns: `["email_id", "customer_id", "email_address"]`
- Effective dates: injected by executor
- Change from V1: Removed unused columns `email_type` (AP4 elimination). Retained `email_id` for deterministic first-email selection via ordering.

**Transformation:**
- resultName: `output`
- SQL: see Section 5

**CsvFileWriter:**
- source: `output`
- outputFile: `Output/double_secret_curated/customer_demographics.csv`
- includeHeader: `true`
- writeMode: `Overwrite`
- lineEnding: `CRLF`
- No trailer

## 3. Anti-Pattern Analysis

### Code-Quality Anti-Patterns Eliminated

| ID | Name | V1 Problem | V2 Resolution |
|----|------|------------|---------------|
| AP1 | Dead-end sourcing | V1 sources `datalake.segments` (segment_id, segment_name) but never uses it in the External module. | **Eliminated.** V2 does not source the segments table at all. |
| AP3 | Unnecessary External module | V1 uses `CustomerDemographicsBuilder.cs` for logic that is fully expressible in SQL (age calculation, age bracket assignment, first phone/email selection). | **Eliminated.** V2 uses a Transformation module with SQL. No External module needed. |
| AP4 | Unused columns | V1 sources `prefix`, `sort_name`, `suffix` from customers; `phone_type`, `phone_id` from phone_numbers; `email_type`, `email_id` from email_addresses. Of these, `prefix`, `sort_name`, `suffix`, `phone_type`, `email_type` never appear in output. | **Eliminated.** V2 sources only columns needed for output or for deterministic ordering (`phone_id`, `email_id`). |
| AP6 | Row-by-row iteration | V1 uses `foreach` loops to build phone/email lookup dictionaries and then iterates customers row-by-row. | **Eliminated.** V2 uses set-based SQL with subqueries and JOINs. |

### Output-Affecting Wrinkles

No W-codes apply to this job:
- No Sunday skip (W1), weekend fallback (W2), or boundary rows (W3a/W3b/W3c).
- No integer division (W4), banker's rounding (W5), or double epsilon (W6).
- No trailer (W7/W8 not applicable).
- Overwrite mode is correct for this job's use case (W9 not applicable -- single effective date per run, only last day survives, which is the expected behavior).
- Not Parquet, so W10 not applicable.
- No append-mode header duplication (W12).

## 4. Output Schema

| Column | Source | Transformation | V1 Evidence |
|--------|--------|---------------|-------------|
| customer_id | customers.id | CAST to integer via SQLite `CAST(c.id AS INTEGER)` | [CustomerDemographicsBuilder.cs:58] `Convert.ToInt32(custRow["id"])` |
| first_name | customers.first_name | COALESCE to empty string | [CustomerDemographicsBuilder.cs:59] `custRow["first_name"]?.ToString() ?? ""` |
| last_name | customers.last_name | COALESCE to empty string | [CustomerDemographicsBuilder.cs:60] `custRow["last_name"]?.ToString() ?? ""` |
| birthdate | customers.birthdate | Pass-through (raw value from source) | [CustomerDemographicsBuilder.cs:86] `custRow["birthdate"]` |
| age | Computed | `year(as_of) - year(birthdate)`, subtract 1 if birthday hasn't occurred in `as_of` year | [CustomerDemographicsBuilder.cs:65-66] |
| age_bracket | Computed from age | CASE: <26->"18-25", 26-35->"26-35", 36-45->"36-45", 46-55->"46-55", 56-65->"56-65", >65->"65+" | [CustomerDemographicsBuilder.cs:68-76] |
| primary_phone | phone_numbers.phone_number | First phone per customer (by insertion order), or empty string if none | [CustomerDemographicsBuilder.cs:31-38, 78] |
| primary_email | email_addresses.email_address | First email per customer (by insertion order), or empty string if none | [CustomerDemographicsBuilder.cs:44-51, 79] |
| as_of | customers.as_of | Pass-through | [CustomerDemographicsBuilder.cs:91] |

**Column order:** customer_id, first_name, last_name, birthdate, age, age_bracket, primary_phone, primary_email, as_of

## 5. SQL Design

The SQL must accomplish:
1. Compute age from birthdate and as_of with birthday adjustment (BR-1)
2. Assign age bracket based on age ranges (BR-2)
3. Select the first phone number per customer (BR-3) -- "first encountered" in DataFrame order
4. Select the first email address per customer (BR-4) -- "first encountered" in DataFrame order
5. Default empty string for missing phone/email (BR-5)
6. Pass through birthdate and as_of raw values (BR-7, BR-8)

### Key SQL Techniques

**Age calculation (BR-1):** SQLite stores dates from DataSourcing as `yyyy-MM-dd` text strings (via `ToSqliteValue` in `Transformation.cs:110`). The age formula:
```sql
CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
- CASE
    WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
    ELSE 0
  END
```
This replicates V1's `asOfDate.Year - birthdate.Year; if (birthdate > asOfDate.AddYears(-age)) age--` logic. The `strftime('%m-%d', ...)` comparison is a lexicographic comparison of month-day strings, which is equivalent to checking whether the birthday has occurred in the as_of year. This works because both month and day are zero-padded to two digits.

**First phone/email (BR-3, BR-4):** Use `MIN(rowid)` within a subquery to select the first row per customer. SQLite rowid values correspond to insertion order, which matches DataSourcing's `ORDER BY as_of` result order. Within a single effective date (which is how the executor runs), this is equivalent to PostgreSQL's natural row order.

```sql
-- First phone per customer
SELECT pn.customer_id, pn.phone_number
FROM phone_numbers pn
INNER JOIN (
    SELECT customer_id, MIN(rowid) AS min_rowid
    FROM phone_numbers
    GROUP BY customer_id
) pf ON pn.rowid = pf.min_rowid AND pn.customer_id = pf.customer_id
```

### Complete SQL

```sql
SELECT
    CAST(c.id AS INTEGER) AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    c.birthdate,
    CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
        - CASE
            WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
            ELSE 0
          END AS age,
    CASE
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE
                  WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
                  ELSE 0
                END) < 26 THEN '18-25'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE
                  WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
                  ELSE 0
                END) <= 35 THEN '26-35'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE
                  WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
                  ELSE 0
                END) <= 45 THEN '36-45'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE
                  WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
                  ELSE 0
                END) <= 55 THEN '46-55'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE
                  WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1
                  ELSE 0
                END) <= 65 THEN '56-65'
        ELSE '65+'
    END AS age_bracket,
    COALESCE(pp.phone_number, '') AS primary_phone,
    COALESCE(pe.email_address, '') AS primary_email,
    c.as_of
FROM customers c
LEFT JOIN (
    SELECT pn.customer_id, pn.phone_number
    FROM phone_numbers pn
    INNER JOIN (
        SELECT customer_id, MIN(rowid) AS min_rowid
        FROM phone_numbers
        GROUP BY customer_id
    ) pf ON pn.rowid = pf.min_rowid AND pn.customer_id = pf.customer_id
) pp ON CAST(c.id AS INTEGER) = pp.customer_id
LEFT JOIN (
    SELECT ea.customer_id, ea.email_address
    FROM email_addresses ea
    INNER JOIN (
        SELECT customer_id, MIN(rowid) AS min_rowid
        FROM email_addresses
        GROUP BY customer_id
    ) ef ON ea.rowid = ef.min_rowid AND ea.customer_id = ef.customer_id
) pe ON CAST(c.id AS INTEGER) = pe.customer_id
```

### SQL Design Notes

1. **Age expression repeated in age_bracket CASE:** SQLite does not support referencing column aliases in the same SELECT clause. The age expression must be repeated in the CASE statement. An alternative would be to use a CTE, but repeating the expression is simpler and avoids the overhead of an unnecessary CTE. This is not AP8 -- the expression is used in the final output, not discarded.

2. **COALESCE for NULL handling (BR-5):** `COALESCE(pp.phone_number, '')` handles both the case where the LEFT JOIN finds no match (NULL from the join) and the unlikely case where `phone_number` itself is NULL. This matches V1's `GetValueOrDefault(customerId, "")`.

3. **CAST(c.id AS INTEGER):** Ensures the customer_id output column is an integer, matching V1's `Convert.ToInt32(custRow["id"])`. Since `id` comes from PostgreSQL as an integer type, DataSourcing will store it as an integer in the DataFrame, and SQLite will type it as INTEGER. The CAST is defensive but harmless.

4. **Row ordering:** The SQL does not include an explicit `ORDER BY`. V1 iterates `customers.Rows` in DataSourcing order (`ORDER BY as_of`). The Transformation module inserts rows into SQLite in that order, and the SQL's FROM clause starts with `customers`, so the result order will follow the customers table's natural order in SQLite. This matches V1's row ordering.

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerDemographicsV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name", "birthdate"]
    },
    {
      "type": "DataSourcing",
      "resultName": "phone_numbers",
      "schema": "datalake",
      "table": "phone_numbers",
      "columns": ["phone_id", "customer_id", "phone_number"]
    },
    {
      "type": "DataSourcing",
      "resultName": "email_addresses",
      "schema": "datalake",
      "table": "email_addresses",
      "columns": ["email_id", "customer_id", "email_address"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT CAST(c.id AS INTEGER) AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, c.birthdate, CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END AS age, CASE WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) < 26 THEN '18-25' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 35 THEN '26-35' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 45 THEN '36-45' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 55 THEN '46-55' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 65 THEN '56-65' ELSE '65+' END AS age_bracket, COALESCE(pp.phone_number, '') AS primary_phone, COALESCE(pe.email_address, '') AS primary_email, c.as_of FROM customers c LEFT JOIN (SELECT pn.customer_id, pn.phone_number FROM phone_numbers pn INNER JOIN (SELECT customer_id, MIN(rowid) AS min_rowid FROM phone_numbers GROUP BY customer_id) pf ON pn.rowid = pf.min_rowid AND pn.customer_id = pf.customer_id) pp ON CAST(c.id AS INTEGER) = pp.customer_id LEFT JOIN (SELECT ea.customer_id, ea.email_address FROM email_addresses ea INNER JOIN (SELECT customer_id, MIN(rowid) AS min_rowid FROM email_addresses GROUP BY customer_id) ef ON ea.rowid = ef.min_rowid AND ea.customer_id = ef.customer_id) pe ON CAST(c.id AS INTEGER) = pe.customer_id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/customer_demographics.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "CRLF"
    }
  ]
}
```

## 7. Writer Config

| Parameter | V1 Value | V2 Value | Match? |
|-----------|----------|----------|--------|
| type | CsvFileWriter | CsvFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputFile | `Output/curated/customer_demographics.csv` | `Output/double_secret_curated/customer_demographics.csv` | Path changed per V2 convention |
| includeHeader | true | true | Yes |
| writeMode | Overwrite | Overwrite | Yes |
| lineEnding | CRLF | CRLF | Yes |
| trailerFormat | (not configured) | (not configured) | Yes |

All writer parameters match V1 exactly. Only the output path changes to `Output/double_secret_curated/` as required by the V2 convention.

## 8. Proofmark Config Design

**Reader:** CSV (matching V1 writer type)
**Header rows:** 1 (V1 has `includeHeader: true`)
**Trailer rows:** 0 (V1 has no trailer)
**Threshold:** 100.0 (strict -- no known non-deterministic fields)
**Excluded columns:** None
**Fuzzy columns:** None

**Justification for zero overrides:**
- All output columns are deterministic: customer_id, first_name, last_name, birthdate, age, age_bracket, primary_phone, primary_email, as_of.
- No timestamps, UUIDs, or runtime-generated values.
- No floating-point arithmetic (age is integer, age_bracket is string).
- The "first phone/email" selection is order-dependent (BRD OQ-1), but the order is deterministic for a given data state because both V1 and V2 receive the same DataSourcing output (PostgreSQL `ORDER BY as_of`, then natural row order within a date).

```yaml
comparison_target: "customer_demographics"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Implementation |
|-----------------|-------------|-----------------|----------------|
| BR-1: Age calculation with birthday adjustment | S5 SQL Design | SQLite `strftime` year diff with month-day comparison for birthday adjustment | SQL CASE expression in Transformation |
| BR-2: Age bracket ranges | S5 SQL Design | CASE/WHEN on computed age | SQL CASE expression in Transformation |
| BR-3: First phone per customer (first encountered) | S5 SQL Design | `MIN(rowid)` subquery on phone_numbers | LEFT JOIN with rowid-based subquery |
| BR-4: First email per customer (first encountered) | S5 SQL Design | `MIN(rowid)` subquery on email_addresses | LEFT JOIN with rowid-based subquery |
| BR-5: Empty string default for missing contact | S5 SQL Design | `COALESCE(..., '')` on LEFT JOIN results | SQL COALESCE |
| BR-6: Empty output when no customers | S5 SQL Design | SQL naturally returns 0 rows when customers table is empty (Transformation returns empty DataFrame) | Framework behavior -- no special handling needed |
| BR-7: as_of pass-through | S4 Output Schema | Direct column selection from customers | `c.as_of` in SELECT |
| BR-8: birthdate pass-through | S4 Output Schema | Direct column selection from customers | `c.birthdate` in SELECT |
| BR-9: Unused sourced columns removed | S3 AP4 | Columns not sourced in V2 | DataSourcing config |
| BR-10: Segments table not sourced | S3 AP1 | Table not sourced in V2 | DataSourcing config |
| BR-11: DateOnly conversion | S5 SQL Design | Not needed -- SQLite operates on date strings natively, DataSourcing provides dates as text via `ToSqliteValue` | N/A -- framework handles type conversion |
| BRD Edge: Customer under 18 gets "18-25" | S5 SQL Design | CASE uses `< 26` with no lower bound, matching V1 | SQL CASE first branch |
| BRD Edge: Multiple phones/emails | S5 SQL Design | `MIN(rowid)` selects first encountered | Subquery |
| BRD Edge: Customer with no phone/email | S5 SQL Design | LEFT JOIN + COALESCE | SQL |
| BRD OQ-2: Segments unused | S3 AP1 | Eliminated from DataSourcing | Config |
| BRD OQ-3: Unused columns | S3 AP4 | Eliminated from DataSourcing | Config |

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`ExternalModules/CustomerDemographicsBuilder.cs`) is replaced entirely by the Transformation module's SQL. This eliminates AP3 (unnecessary External module) and AP6 (row-by-row iteration).

## Appendix: Risks and Mitigations

### Risk 1: Row ordering mismatch

**Risk:** V1 iterates `customers.Rows` in DataSourcing order. V2's SQL output order depends on SQLite's query execution plan. If the SQL produces rows in a different order than V1, the CSV will differ byte-for-byte even if the data is the same.

**Mitigation:** The SQL starts with `FROM customers c` and applies LEFT JOINs. SQLite typically preserves the driving table's row order when no `ORDER BY` is specified and no aggregation occurs on the driving table. However, if Proofmark comparison fails due to row ordering alone, the fix is to add `ORDER BY c.rowid` to the SQL. This is a low-risk change that does not affect data correctness.

### Risk 2: `strftime` birthday adjustment edge case -- RESOLVED

**Risk:** The V1 birthday adjustment uses `DateOnly` comparison: `birthdate > asOfDate.AddYears(-age)`. The V2 SQL uses `strftime('%m-%d', birthdate) > strftime('%m-%d', as_of)`. These should be equivalent for all valid dates, but leap year edge cases (Feb 29 birthdate) could theoretically diverge.

**Resolution:** Database query confirms zero customers with a Feb 29 birthdate:
```sql
SELECT COUNT(*) FROM datalake.customers
WHERE EXTRACT(MONTH FROM birthdate) = 2 AND EXTRACT(DAY FROM birthdate) = 29;
-- Result: 0
```
No leap-year birthday edge case exists in the data. The `strftime('%m-%d')` comparison is equivalent to V1's `DateOnly.AddYears()` for all birthdates present in `datalake.customers`. No special-case SQL is needed.

### Risk 3: NULL birthdate

**Risk:** BRD edge case notes that a NULL birthdate would cause an exception in V1. V2's SQL would produce NULL for age and fall into the `'65+'` bracket (since no CASE condition matches NULL). If the data contains NULL birthdates, V1 would crash and V2 would silently produce incorrect output.

**Mitigation:** This is a data quality issue. If V1 runs successfully in production, there are no NULL birthdates in the data. No defensive handling needed for output equivalence.
