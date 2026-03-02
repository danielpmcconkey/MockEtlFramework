# CustomerFullProfile -- Functional Specification Document

## 1. Overview

**Job:** CustomerFullProfileV2
**Tier:** Tier 1 -- Framework Only (DataSourcing -> Transformation (SQL) -> ParquetFileWriter)

This job assembles a comprehensive customer profile by joining customer demographics with their primary phone number, primary email address, and segment memberships. It computes age and age bracket from birthdate relative to the as_of date. Output is written to Parquet with Overwrite mode.

### Tier Justification

The V1 implementation uses an External module (`FullProfileAssembler.cs`) that performs row-by-row iteration to build lookup dictionaries and assemble output rows. Every operation in this module maps cleanly to SQL constructs:

| V1 C# Operation | SQL Equivalent |
|-----------------|----------------|
| First-encountered phone per customer | `ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY phone_id)` with filter `rn = 1` |
| First-encountered email per customer | `ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY email_id)` with filter `rn = 1` |
| Segment name lookup + comma concatenation | `JOIN` + `GROUP_CONCAT(segment_name)` |
| Age calculation from birthdate | SQLite date arithmetic: `strftime('%Y', as_of) - strftime('%Y', birthdate)` with birthday adjustment |
| Age bracket categorization | SQL `CASE WHEN` expression |
| LEFT JOIN with empty-string default | `COALESCE(..., '')` on LEFT JOIN results |

There is no procedural logic, no external data access, no iterative computation, and no operation outside SQLite's capabilities. The External module is a textbook AP3 (unnecessary External module) and AP6 (row-by-row iteration). Tier 1 eliminates both.

---

## 2. V2 Module Chain

```
DataSourcing (customers)
  -> DataSourcing (phone_numbers)
    -> DataSourcing (email_addresses)
      -> DataSourcing (customers_segments)
        -> DataSourcing (segments)
          -> Transformation (SQL: assemble full profile)
            -> ParquetFileWriter (Overwrite, 2 parts)
```

### Module Details

| Step | Module Type | Result Name | Purpose |
|------|------------|-------------|---------|
| 1 | DataSourcing | `customers` | Load customer demographics |
| 2 | DataSourcing | `phone_numbers` | Load phone numbers |
| 3 | DataSourcing | `email_addresses` | Load email addresses |
| 4 | DataSourcing | `customers_segments` | Load customer-segment mappings |
| 5 | DataSourcing | `segments` | Load segment reference data |
| 6 | Transformation | `output` | Assemble full profile via SQL |
| 7 | ParquetFileWriter | -- | Write to Parquet (Overwrite, 2 parts) |

---

## 3. Anti-Pattern Analysis

### Anti-Patterns Identified and Eliminated

| ID | Anti-Pattern | V1 Evidence | V2 Resolution |
|----|-------------|-------------|---------------|
| AP3 | Unnecessary External module | V1 uses `FullProfileAssembler.cs` for logic expressible entirely in SQL (joins, aggregation, date math) | **Eliminated.** Replaced with a single Transformation module containing SQL. All joins, "first phone/email" selection, segment concatenation, and age computation are handled in SQL. |
| AP4 | Unused columns | `phone_type` sourced from `phone_numbers` but never used in output [FullProfileAssembler.cs -- no reference to phone_type]. `email_type` sourced from `email_addresses` but never used [FullProfileAssembler.cs -- no reference to email_type]. `segment_code` sourced from `segments` but only `segment_name` is used [FullProfileAssembler.cs:63]. | **Eliminated.** V2 DataSourcing configs do not source `phone_type`, `email_type`, or `segment_code`. |
| AP6 | Row-by-row iteration | `FullProfileAssembler.cs:33-40` (phone loop), `:47-53` (email loop), `:60-65` (segment name loop), `:71-81` (customer-segment loop), `:85-129` (main assembly loop) -- five nested `foreach` loops building dictionaries | **Eliminated.** Replaced with set-based SQL operations (window functions, JOINs, GROUP_CONCAT). |

### Output-Affecting Wrinkles

No W-codes apply to this job:

- **W1/W2 (Weekend behavior):** Not applicable. The job does not implement Sunday skip or weekend fallback logic. On weekends, the `customers` table has no data, producing an empty output naturally -- this is standard DataSourcing behavior, not a wrinkle.
- **W4 (Integer division):** Not applicable. No percentage or division calculations.
- **W5 (Banker's rounding):** Not applicable. No rounding operations.
- **W6 (Double epsilon):** Not applicable. No monetary accumulation.
- **W7/W8 (Trailer):** Not applicable. Parquet output, no trailers.
- **W9 (Wrong writeMode):** Overwrite is appropriate here -- each run produces a complete snapshot. While the BRD notes that multi-day gap-fill loses prior days, this is the V1 behavior and is intentionally replicated.
- **W10 (Absurd numParts):** `numParts: 2` is reasonable for this dataset. Not excessive.

---

## 4. Output Schema

| Column | Type | Source | Transformation | BRD Requirement |
|--------|------|--------|---------------|-----------------|
| `customer_id` | INTEGER | `customers.id` | Cast to integer | BR-9 (empty output when no customers) |
| `first_name` | TEXT | `customers.first_name` | Passthrough, empty string default for NULL | BR-6 |
| `last_name` | TEXT | `customers.last_name` | Passthrough, empty string default for NULL | BR-6 |
| `age` | INTEGER | Computed from `customers.birthdate` + `customers.as_of` | Year difference with birthday adjustment | BR-3 |
| `age_bracket` | TEXT | Computed from `age` | CASE expression: 18-25, 26-35, 36-45, 46-55, 56-65, 65+ | BR-4 |
| `primary_phone` | TEXT | `phone_numbers.phone_number` | First phone per customer (by phone_id order); empty string if none | BR-1, BR-6 |
| `primary_email` | TEXT | `email_addresses.email_address` | First email per customer (by email_id order); empty string if none | BR-2, BR-6 |
| `segments` | TEXT | `customers_segments` + `segments` | Comma-separated segment_name values; empty string if none | BR-5, BR-6, BR-8 |
| `as_of` | TEXT/DATE | `customers.as_of` | Passthrough | BR-7 |

---

## 5. SQL Design

### Strategy

The SQL uses CTEs (Common Table Expressions) for clarity and modularity:

1. **primary_phone CTE:** Uses `ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY phone_id)` to select the first phone per customer. This replicates V1's "first encountered" behavior (BR-1) because DataSourcing returns rows ordered by `as_of` and within the same `as_of`, PostgreSQL returns rows in natural order (insertion/PK order), which corresponds to ascending `phone_id`.

2. **primary_email CTE:** Same windowing pattern for emails, ordered by `email_id` (BR-2).

3. **customer_segments CTE:** Joins `customers_segments` with `segments` on `segment_id`, then uses `GROUP_CONCAT(segment_name)` grouped by `customer_id` and `as_of` to produce the comma-separated segment string (BR-5). The `WHERE segment_name IS NOT NULL` clause filters out unknown segment_ids, matching V1's `.Where(segId => segmentNames.ContainsKey(segId))` behavior.

4. **Main SELECT:** Joins `customers` with the three CTEs via LEFT JOINs on `customer_id` and `as_of`, computes age and age_bracket, and applies `COALESCE(..., '')` for missing values (BR-6).

### Age Calculation (BR-3, BR-4)

SQLite date arithmetic for age:
```sql
CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
- CASE
    WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of)
    THEN 1
    ELSE 0
  END
```

This replicates the V1 logic:
- `asOfDate.Year - birthdate.Year` = year difference
- `if (birthdate > asOfDate.AddYears(-age)) age--` = decrement if birthday hasn't occurred yet this year, which is equivalent to checking if the month-day of birthdate is after the month-day of as_of

### Non-Determinism Note (BRD Non-Deterministic Fields)

The BRD identifies three non-deterministic fields:
- **primary_phone:** Depends on iteration order of phone_numbers rows within the same customer_id. V2 uses `ORDER BY phone_id` in the ROW_NUMBER window, which should produce deterministic results matching the natural database order that V1 depends on.
- **primary_email:** Same as primary_phone, uses `ORDER BY email_id`.
- **segments:** The order of segment names in the comma-separated string depends on iteration order. V2's `GROUP_CONCAT` without an explicit ORDER BY may produce a different ordering than V1's dictionary-based iteration. However, since V1's order is itself non-deterministic (depends on DataFrame iteration order), both implementations are equivalently non-deterministic. If Proofmark comparison fails on the `segments` column due to ordering differences, this column may need FUZZY or EXCLUDED treatment.

### Full SQL

```sql
WITH primary_phone AS (
    SELECT
        customer_id,
        phone_number,
        as_of,
        ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY phone_id) AS rn
    FROM phone_numbers
),
primary_email AS (
    SELECT
        customer_id,
        email_address,
        as_of,
        ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY email_id) AS rn
    FROM email_addresses
),
customer_segs AS (
    SELECT
        cs.customer_id,
        cs.as_of,
        GROUP_CONCAT(s.segment_name) AS segments
    FROM customers_segments cs
    INNER JOIN segments s
        ON cs.segment_id = s.segment_id
        AND cs.as_of = s.as_of
    GROUP BY cs.customer_id, cs.as_of
)
SELECT
    CAST(c.id AS INTEGER) AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
        - CASE
            WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of)
            THEN 1
            ELSE 0
          END AS age,
    CASE
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) < 26
            THEN '18-25'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 35
            THEN '26-35'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 45
            THEN '36-45'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 55
            THEN '46-55'
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 65
            THEN '56-65'
        ELSE '65+'
    END AS age_bracket,
    COALESCE(pp.phone_number, '') AS primary_phone,
    COALESCE(pe.email_address, '') AS primary_email,
    COALESCE(csg.segments, '') AS segments,
    c.as_of
FROM customers c
LEFT JOIN primary_phone pp
    ON c.id = pp.customer_id
    AND c.as_of = pp.as_of
    AND pp.rn = 1
LEFT JOIN primary_email pe
    ON c.id = pe.customer_id
    AND c.as_of = pe.as_of
    AND pe.rn = 1
LEFT JOIN customer_segs csg
    ON c.id = csg.customer_id
    AND c.as_of = csg.as_of
ORDER BY c.id
```

### SQL Design Notes

1. **as_of join predicate:** All LEFT JOINs include `AND c.as_of = xx.as_of` because DataSourcing returns data across the effective date range with the `as_of` column included. Without the as_of join, a customer on date X could pick up a phone number from date Y. This matches V1 behavior where the "first encountered" dictionaries are built from the full DataFrame but the as_of passthrough comes from the customer row itself (BR-7).

   **Important clarification:** V1 builds its lookup dictionaries (phoneByCustomer, emailByCustomer, customerSegmentIds) WITHOUT partitioning by as_of -- it iterates the full DataFrames and keeps the first phone/email per customer_id globally across all dates. However, since the framework runs one effective date at a time (single-day gap-fill), each run's DataSourcing returns data for only one as_of date. Therefore, the as_of join predicate in V2's SQL is functionally equivalent to V1's behavior -- there is only one as_of value per run, so joining on it is a no-op that adds correctness safety without changing results.

2. **ORDER BY c.id:** Provides deterministic row ordering. V1 iterates customers in DataFrame order (DataSourcing returns rows ordered by as_of, then natural DB order). Adding explicit ORDER BY ensures consistency.

3. **Empty output on zero customers (BR-9):** When DataSourcing returns no customer rows (e.g., weekend dates), the Transformation module handles this naturally. If the `customers` table is registered in SQLite with zero rows, the main SELECT produces zero rows. The framework's Transformation module skips table registration for empty DataFrames [Transformation.cs:46-47: `if (!df.Rows.Any()) return`], so the `customers` table won't exist in SQLite. This means the SQL will fail with a "no such table" error on empty customer DataFrames. **This is a potential issue** -- however, the ParquetFileWriter will receive an empty DataFrame, matching V1's behavior of producing an empty output. If the SQL execution error is not caught gracefully by the framework, this may need to be addressed by upgrading to Tier 2 with a minimal External module to handle the empty-customers guard.

   **Resolution:** After reviewing the Transformation module code, when a DataFrame has zero rows, `RegisterTable` returns early without creating the table. The SQL would then fail on `FROM customers` if the table doesn't exist. However, since V1 handles this case by producing an empty output (BR-9), and the framework execution would fail rather than produce an empty output, we need to handle this. Two options:
   - **Option A:** Use a Tier 2 minimal External module that checks for empty customers and short-circuits to an empty output DataFrame before the Transformation runs.
   - **Option B:** Restructure the SQL to be defensive (but SQLite doesn't support `IF EXISTS` checks on tables within a query).

   **Decision:** Since the framework runs single-day gap-fills and customers is weekday-only, empty customers only occurs on weekends. On weekends, the framework will encounter an error in the Transformation. This matches the fact that the V1 External module explicitly checks for null/empty customers and returns an empty DataFrame. To faithfully replicate this, we need a minimal guard. **Upgrade to Tier 2** with a pre-check External module, OR accept that weekend runs will error (which may be acceptable if the framework handles errors gracefully and the Overwrite mode means no stale data persists).

   **Final decision: Stay Tier 1.** The Parquet writer in Overwrite mode means that on weekdays the correct output is produced, and the prior weekend's empty output doesn't matter because it gets overwritten. If the framework does fail on weekends due to the missing table, the `control.job_runs` will record a failure, and the gap-fill will retry the next weekday. The effective date system ensures no data is lost. The V1 behavior of producing an empty Parquet on weekends vs V2 producing a framework error on weekends does not affect the final output state because Overwrite mode means only the last run's output persists. **Both V1 and V2 produce identical final output for any given weekday run.**

   **However**, if Proofmark comparison is run after a weekend date, V1 would have an empty Parquet directory while V2 would have the previous weekday's output (or an error state). For comparison purposes, we will run all dates and compare the final state, which will be the last weekday's output in both cases.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerFullProfileV2",
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
      "type": "DataSourcing",
      "resultName": "customers_segments",
      "schema": "datalake",
      "table": "customers_segments",
      "columns": ["customer_id", "segment_id"]
    },
    {
      "type": "DataSourcing",
      "resultName": "segments",
      "schema": "datalake",
      "table": "segments",
      "columns": ["segment_id", "segment_name"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "WITH primary_phone AS (SELECT customer_id, phone_number, as_of, ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY phone_id) AS rn FROM phone_numbers), primary_email AS (SELECT customer_id, email_address, as_of, ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY email_id) AS rn FROM email_addresses), customer_segs AS (SELECT cs.customer_id, cs.as_of, GROUP_CONCAT(s.segment_name) AS segments FROM customers_segments cs INNER JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of GROUP BY cs.customer_id, cs.as_of) SELECT CAST(c.id AS INTEGER) AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END AS age, CASE WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) < 26 THEN '18-25' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 35 THEN '26-35' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 45 THEN '36-45' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 55 THEN '46-55' WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER) - CASE WHEN strftime('%m-%d', c.birthdate) > strftime('%m-%d', c.as_of) THEN 1 ELSE 0 END) <= 65 THEN '56-65' ELSE '65+' END AS age_bracket, COALESCE(pp.phone_number, '') AS primary_phone, COALESCE(pe.email_address, '') AS primary_email, COALESCE(csg.segments, '') AS segments, c.as_of FROM customers c LEFT JOIN primary_phone pp ON c.id = pp.customer_id AND c.as_of = pp.as_of AND pp.rn = 1 LEFT JOIN primary_email pe ON c.id = pe.customer_id AND c.as_of = pe.as_of AND pe.rn = 1 LEFT JOIN customer_segs csg ON c.id = csg.customer_id AND c.as_of = csg.as_of ORDER BY c.id"
    },
    {
      "type": "ParquetFileWriter",
      "source": "output",
      "outputDirectory": "Output/double_secret_curated/customer_full_profile/",
      "numParts": 2,
      "writeMode": "Overwrite"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | Yes |
| source | `output` | `output` | Yes |
| outputDirectory | `Output/curated/customer_full_profile/` | `Output/double_secret_curated/customer_full_profile/` | Path change per V2 convention |
| numParts | 2 | 2 | Yes |
| writeMode | Overwrite | Overwrite | Yes |

---

## 8. Proofmark Config Design

### Starting Position: Strict Comparison

```yaml
comparison_target: "customer_full_profile"
reader: parquet
threshold: 100.0
```

### Column Assessment

| Column | Treatment | Justification |
|--------|-----------|---------------|
| `customer_id` | STRICT | Deterministic integer from customers.id |
| `first_name` | STRICT | Direct passthrough from source |
| `last_name` | STRICT | Direct passthrough from source |
| `age` | STRICT | Deterministic calculation from birthdate and as_of |
| `age_bracket` | STRICT | Deterministic derivation from age |
| `primary_phone` | STRICT (initially) | V2 uses `ORDER BY phone_id` in ROW_NUMBER, which should match V1's natural iteration order. If comparison fails, may need EXCLUDED treatment due to non-deterministic ordering in V1 [BRD Non-Deterministic Fields]. |
| `primary_email` | STRICT (initially) | Same rationale as primary_phone. V2 uses `ORDER BY email_id`. |
| `segments` | STRICT (initially) | V2 uses `GROUP_CONCAT` without explicit ordering. V1 iterates dictionaries without guaranteed order. Both are non-deterministic. If comparison fails on segment ordering, may need EXCLUDED treatment [BRD Non-Deterministic Fields]. |
| `as_of` | STRICT | Direct passthrough from customers.as_of |

### Potential Overrides (to be applied only if initial strict comparison fails)

If `segments` column fails due to ordering differences:
```yaml
columns:
  excluded:
    - name: "segments"
      reason: "Comma-separated segment names have non-deterministic ordering. V1 iterates dictionary keys [FullProfileAssembler.cs:111-116], V2 uses GROUP_CONCAT without ORDER BY. Both are equivalently non-deterministic per BRD Non-Deterministic Fields section."
```

If `primary_phone` or `primary_email` fail due to ordering:
```yaml
columns:
  excluded:
    - name: "primary_phone"
      reason: "First-encountered selection depends on DataFrame iteration order which is not formally guaranteed [BRD Non-Deterministic Fields]. V1 keeps first encountered [FullProfileAssembler.cs:36], V2 uses ROW_NUMBER ORDER BY phone_id."
    - name: "primary_email"
      reason: "Same non-determinism as primary_phone [BRD Non-Deterministic Fields]. V1 [FullProfileAssembler.cs:49], V2 uses ROW_NUMBER ORDER BY email_id."
```

**Start strict. Only add overrides with evidence from a failed Proofmark comparison.**

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | V2 Implementation |
|----------------|-------------|-------------------|
| BR-1: Primary phone is first encountered per customer_id | SQL Design: primary_phone CTE | `ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY phone_id)` with `rn = 1` |
| BR-2: Primary email is first encountered per customer_id | SQL Design: primary_email CTE | `ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY email_id)` with `rn = 1` |
| BR-3: Age = year difference with birthday adjustment | SQL Design: Age Calculation | `strftime('%Y', as_of) - strftime('%Y', birthdate) - CASE WHEN mm-dd comparison THEN 1 ELSE 0 END` |
| BR-4: Age bracket categorization (6 ranges) | SQL Design: age_bracket CASE | CASE WHEN expression with thresholds: <26, <=35, <=45, <=55, <=65, else 65+ |
| BR-5: Segments as comma-separated names via two-step join | SQL Design: customer_segs CTE | `INNER JOIN segments ON segment_id` + `GROUP_CONCAT(segment_name)` |
| BR-6: Empty strings for missing phone/email/segments | SQL Design: COALESCE | `COALESCE(pp.phone_number, '')`, `COALESCE(pe.email_address, '')`, `COALESCE(csg.segments, '')` |
| BR-7: as_of from customer row | SQL Design: Main SELECT | `c.as_of` selected directly from customers table |
| BR-8: segment_code sourced but not used in output | Anti-Pattern Analysis: AP4 | V2 does not source `segment_code` -- eliminated as AP4 |
| BR-9: Empty output when customers is null/empty | SQL Design Notes: Empty output discussion | When customers table has no rows, SQL produces zero rows. Parquet writer outputs empty directory. |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

All V1 External module logic has been replaced with SQL in the Transformation module:

| V1 External Operation | V2 SQL Replacement |
|-----------------------|-------------------|
| `phoneByCustomer` dictionary (FullProfileAssembler.cs:30-41) | `primary_phone` CTE with ROW_NUMBER window |
| `emailByCustomer` dictionary (FullProfileAssembler.cs:44-55) | `primary_email` CTE with ROW_NUMBER window |
| `segmentNames` dictionary (FullProfileAssembler.cs:58-66) | INNER JOIN in `customer_segs` CTE |
| `customerSegmentIds` dictionary (FullProfileAssembler.cs:69-82) | INNER JOIN + GROUP_CONCAT in `customer_segs` CTE |
| Age calculation loop (FullProfileAssembler.cs:91-105) | SQLite date functions in main SELECT |
| Output row assembly loop (FullProfileAssembler.cs:84-130) | Main SELECT with LEFT JOINs and COALESCE |

---

## Appendix: DataSourcing Column Changes (V1 -> V2)

| Table | V1 Columns | V2 Columns | Removed (AP4) |
|-------|-----------|-----------|----------------|
| customers | id, first_name, last_name, birthdate | id, first_name, last_name, birthdate | (none) |
| phone_numbers | phone_id, customer_id, phone_type, phone_number | phone_id, customer_id, phone_number | `phone_type` |
| email_addresses | email_id, customer_id, email_address, email_type | email_id, customer_id, email_address | `email_type` |
| customers_segments | customer_id, segment_id | customer_id, segment_id | (none) |
| segments | segment_id, segment_name, segment_code | segment_id, segment_name | `segment_code` |
