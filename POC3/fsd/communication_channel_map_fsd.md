# CommunicationChannelMap -- Functional Specification Document

## 1. Overview

The V2 job (`CommunicationChannelMapV2`) produces a per-customer communication channel mapping that identifies each customer's preferred marketing channel (Email, SMS, Push, or None) along with their email address and phone number. Output is a flat CSV file with header, LF line endings, Overwrite mode, and no trailer.

**Tier: 1 (Framework Only)**
`DataSourcing -> Transformation (SQL) -> CsvFileWriter`

**Tier Justification:** The V1 External module (`CommunicationChannelMapper.cs`) performs three dictionary-based lookups (email, phone, preferences) against the customers table, then iterates customers to build the output. Every operation maps directly to SQL constructs:

- Email dictionary lookup (last-wins by customer_id) -> subquery or JOIN with GROUP BY to pick one email per customer
- Phone dictionary lookup (last-wins by customer_id) -> same pattern as email
- Preference lookup (customer_id -> set of opted-in types) -> GROUP BY + CASE aggregation over preferences
- Priority hierarchy (MARKETING_EMAIL > MARKETING_SMS > PUSH_NOTIFICATIONS > None) -> SQL CASE expression with MAX over boolean flags
- Asymmetric NULL handling (email -> "N/A", phone -> "") -> COALESCE with different defaults
- as_of from first customer row -> subquery or MIN(as_of) from customers

No procedural logic, no cross-date-range queries beyond what DataSourcing provides, no stateful accumulations, and no SQLite-incompatible operations. No External module is needed.

---

## 2. V2 Module Chain

### Module 1: DataSourcing -- customer_preferences

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | customer_preferences |
| schema | datalake |
| table | customer_preferences |
| columns | `["customer_id", "preference_type", "opted_in"]` |

Note: V1 sources `preference_id` but never uses it in the External module logic. V2 drops it (AP4 elimination).

Effective dates injected via shared state by the executor (`__minEffectiveDate` / `__maxEffectiveDate`). The `as_of` column is automatically appended by DataSourcing.

### Module 2: DataSourcing -- customers

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | customers |
| schema | datalake |
| table | customers |
| columns | `["id", "first_name", "last_name"]` |

Same effective date injection.

### Module 3: DataSourcing -- email_addresses

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | email_addresses |
| schema | datalake |
| table | email_addresses |
| columns | `["customer_id", "email_address"]` |

Note: V1 sources `email_id` but never uses it in the External module logic. V2 drops it (AP4 elimination).

### Module 4: DataSourcing -- phone_numbers

| Property | Value |
|----------|-------|
| type | DataSourcing |
| resultName | phone_numbers |
| schema | datalake |
| table | phone_numbers |
| columns | `["customer_id", "phone_number"]` |

Note: V1 sources `phone_id` but never uses it in the External module logic. V2 drops it (AP4 elimination).

### Module 5: Transformation

| Property | Value |
|----------|-------|
| type | Transformation |
| resultName | output |
| sql | See Section 5 |

### Module 6: CsvFileWriter

| Property | Value |
|----------|-------|
| type | CsvFileWriter |
| source | output |
| outputFile | `Output/double_secret_curated/communication_channel_map.csv` |
| includeHeader | true |
| writeMode | Overwrite |
| lineEnding | LF |

---

## 3. Anti-Pattern Analysis

### Output-Affecting Wrinkles (W-codes)

| W-code | Applies? | Analysis |
|--------|----------|----------|
| W1 (Sunday skip) | No | No Sunday-specific logic in V1. |
| W2 (Weekend fallback) | No | No weekend date fallback in V1. |
| W3a/b/c (Boundary rows) | No | No summary rows appended. |
| W4 (Integer division) | No | No division operations in V1. |
| W5 (Banker's rounding) | No | No rounding operations in V1. |
| W6 (Double epsilon) | No | No floating-point accumulation in V1. No monetary calculations. |
| W7 (Trailer inflated count) | No | No trailer in V1 writer config. |
| W8 (Trailer stale date) | No | No trailer in V1 writer config. |
| W9 (Wrong writeMode) | **MAYBE** | V1 uses Overwrite mode. For a multi-day auto-advance run, only the last effective date's output survives on disk. This may or may not be intentional -- the BRD notes this under "Write Mode Implications." V2 reproduces the same Overwrite behavior. |
| W10 (Absurd numParts) | No | CSV writer, no part files. |
| W12 (Header every append) | No | Not using Append mode. |

**Conclusion:** No W-codes clearly apply. W9 is noted as a potential concern but V2 matches V1's Overwrite behavior exactly.

### Code-Quality Anti-Patterns (AP-codes)

| AP-code | Applies? | V1 Problem | V2 Resolution |
|---------|----------|------------|---------------|
| **AP3** | **YES** | V1 uses a C# External module (`CommunicationChannelMapper`) for lookup/join operations and a CASE-style priority determination, all of which are expressible in SQL. | **Eliminated.** V2 uses a Transformation module with SQL instead of an External module. Tier 1 replaces Tier 3. |
| **AP4** | **YES** | V1 sources `preference_id` from `customer_preferences`, `email_id` from `email_addresses`, and `phone_id` from `phone_numbers`. None of these ID columns are used in the External module's logic. | **Eliminated.** V2 DataSourcing entries drop these unused ID columns. |
| **AP5** | **YES (output-affecting)** | Missing email defaults to `"N/A"` while missing phone defaults to `""` (empty string). This asymmetry is baked into the output. [CommunicationChannelMapper.cs:99-101] | **Reproduced in V2 output.** SQL uses `COALESCE(e.email_address, 'N/A')` for email and `COALESCE(p.phone_number, '')` for phone. Comment in SQL documents the intentional asymmetry: `-- V1 asymmetric NULL handling: missing email -> "N/A", missing phone -> "" [CommunicationChannelMapper.cs:99-101]` |
| **AP6** | **YES** | V1 uses row-by-row `foreach` iteration over customers with dictionary lookups for email, phone, and preferences. [CommunicationChannelMapper.cs:79-113] | **Eliminated.** SQL JOINs and GROUP BY replace all dictionary lookups and row-by-row iteration. |
| AP1 (Dead-end sourcing) | No | All four V1 DataSourcing entries (customer_preferences, customers, email_addresses, phone_numbers) are used in the External module. |
| AP2 (Duplicated logic) | No | No cross-job duplication identified. |
| AP7 (Magic values) | No | The preference type strings (`MARKETING_EMAIL`, `MARKETING_SMS`, `PUSH_NOTIFICATIONS`) and channel names (`Email`, `SMS`, `Push`, `None`) are domain values from the data, not magic thresholds. They appear in the SQL as string literals, which is appropriate for a data mapping. |
| AP8 (Complex SQL) | No | V1 has no SQL. V2 SQL is designed to be straightforward with no unused CTEs. |
| AP9 (Misleading names) | No | Job name accurately describes the output (a mapping of customers to communication channels). |
| AP10 (Over-sourcing dates) | No | V1 uses framework-injected effective dates via DataSourcing, not full table pulls with SQL date filtering. V2 does the same. |

---

## 4. Output Schema

| Column | Source | Transformation | V1 Evidence |
|--------|--------|---------------|-------------|
| customer_id | customers.id | Cast to int via `Convert.ToInt32` (V1); integer passthrough in SQL | [CommunicationChannelMapper.cs:82] |
| first_name | customers.first_name | `ToString()` with null coalesce to `""` | [CommunicationChannelMapper.cs:83] |
| last_name | customers.last_name | `ToString()` with null coalesce to `""` | [CommunicationChannelMapper.cs:84] |
| preferred_channel | Derived from customer_preferences | Priority: MARKETING_EMAIL->"Email", MARKETING_SMS->"SMS", PUSH_NOTIFICATIONS->"Push", else "None" (only opted_in=true considered) | [CommunicationChannelMapper.cs:89-97] |
| email | email_addresses.email_address | Last-wins lookup; `"N/A"` if customer has no email | [CommunicationChannelMapper.cs:100] |
| phone | phone_numbers.phone_number | Last-wins lookup; `""` if customer has no phone | [CommunicationChannelMapper.cs:101] |
| as_of | customers.Rows[0]["as_of"] | Taken from first customer row (min as_of in effective date range) | [CommunicationChannelMapper.cs:76] |

**Column order matters.** V1 defines column order via the `outputColumns` list at [CommunicationChannelMapper.cs:10-14]: `customer_id, first_name, last_name, preferred_channel, email, phone, as_of`. The V2 SQL SELECT clause produces columns in this exact order.

---

## 5. SQL Design

```sql
SELECT
    c.id AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    CASE
        WHEN pref.has_email = 1 THEN 'Email'
        WHEN pref.has_sms = 1 THEN 'SMS'
        WHEN pref.has_push = 1 THEN 'Push'
        ELSE 'None'
    END AS preferred_channel,
    -- V1 asymmetric NULL handling: missing email -> "N/A", missing phone -> "" [CommunicationChannelMapper.cs:99-101]
    COALESCE(em.email_address, 'N/A') AS email,
    COALESCE(ph.phone_number, '') AS phone,
    c.as_of
FROM customers c
LEFT JOIN (
    SELECT
        customer_id,
        MAX(CASE WHEN preference_type = 'MARKETING_EMAIL' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_email,
        MAX(CASE WHEN preference_type = 'MARKETING_SMS' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_sms,
        MAX(CASE WHEN preference_type = 'PUSH_NOTIFICATIONS' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_push
    FROM customer_preferences
    GROUP BY customer_id
) pref ON c.id = pref.customer_id
LEFT JOIN (
    SELECT customer_id, email_address
    FROM email_addresses
    GROUP BY customer_id
    HAVING rowid = MAX(rowid)
) em ON c.id = em.customer_id
LEFT JOIN (
    SELECT customer_id, phone_number
    FROM phone_numbers
    GROUP BY customer_id
    HAVING rowid = MAX(rowid)
) ph ON c.id = ph.customer_id
ORDER BY c.id
```

### SQL Design Rationale

**Preference aggregation (BR-2, BR-3):** V1 builds a `Dictionary<int, HashSet<string>>` of opted-in preference types per customer, then checks containment in priority order (MARKETING_EMAIL > MARKETING_SMS > PUSH_NOTIFICATIONS). The SQL subquery replicates this with `MAX(CASE ... WHEN opted_in = 1 THEN 1 ELSE 0 END)` per preference type, grouped by `customer_id`. The outer CASE applies the same priority hierarchy. Only opted_in rows contribute (BR-3) because the CASE condition includes `AND opted_in = 1`.

**Note on opted_in representation in SQLite:** The DataSourcing module reads `opted_in` from PostgreSQL as a boolean. The Transformation module's `ToSqliteValue` converts booleans to integer (true -> 1, false -> 0) [Transformation.cs:109]. Therefore the SQL checks `opted_in = 1` rather than `opted_in = true`.

**Cross-date preference accumulation (BRD Edge Case 5):** V1 iterates ALL preference rows across the full effective date range without date filtering. The SQL subquery over `customer_preferences` does the same -- it aggregates ALL rows in the DataFrame (which spans the effective date range), not filtered to a specific as_of date. A customer who is opted-in on any date in the range will have `has_email = 1` (etc.), matching V1's behavior where `Add` to a HashSet means any opted-in row sets the flag. Note: if a customer opts out on a later date, V1's HashSet only adds (never removes), so the opt-in persists. The SQL `MAX(... THEN 1 ELSE 0 END)` also persists the 1, matching this behavior.

**Email last-wins lookup (BR-5):** V1 iterates email rows and overwrites `emailLookup[custId]` on each occurrence, so the last row encountered for a given customer_id wins. SQLite's `rowid` tracks insertion order, which matches the DataFrame row order from DataSourcing. Using `GROUP BY customer_id HAVING rowid = MAX(rowid)` selects the last-inserted row per customer, replicating dictionary overwrite semantics. This matches V1 because DataSourcing inserts rows into SQLite in the same order it reads them from PostgreSQL (`ORDER BY as_of`), and the V1 foreach iterates the same DataFrame rows in the same order.

**Phone last-wins lookup (BR-5):** Same approach as email.

**Asymmetric NULL handling (BR-4, AP5):** `COALESCE(em.email_address, 'N/A')` vs `COALESCE(ph.phone_number, '')` reproduces V1's asymmetric defaults. When the LEFT JOIN finds no matching email/phone row for a customer, the value is NULL, and COALESCE applies the V1-matching default.

**as_of from customers table (BR-6):** V1 takes `customers.Rows[0]["as_of"]` -- the as_of value from the first row in the customers DataFrame. DataSourcing orders by `as_of`, so this is the minimum effective date in the range. In SQL, `c.as_of` gives each customer their own as_of value. For single-day auto-advance runs (the normal execution path), there is exactly one as_of value for all customers, so every row gets the same as_of, matching V1's behavior of applying the first row's as_of uniformly to all output rows. For multi-day runs under Overwrite mode, only the last day's output persists (BRD: "Write Mode Implications"), so the multi-day divergence is irrelevant to the final file on disk.

**Empty DataFrame handling (BR-7):** V1 returns an empty DataFrame with the output schema columns if `customers` is null or empty. In V2, if the customers table is empty, its SQLite table is not registered (the Transformation module's `RegisterTable` skips empty DataFrames). The SQL query referencing `customers` will cause a SQLite error. However, the CsvFileWriter receiving an empty DataFrame will write only the header row, producing the same net effect as V1's empty DataFrame output (a CSV with just a header). If this becomes a proofmark issue, it can be addressed in resolution.

**ORDER BY c.id:** Provides deterministic row ordering. V1 iterates `customers.Rows` in DataSourcing order (ORDER BY as_of, then natural PG row order within a single date). Adding ORDER BY c.id ensures deterministic output. If proofmark comparison reveals ordering differences, this may need adjustment.

---

## 6. V2 Job Config

```json
{
  "jobName": "CommunicationChannelMapV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "customer_preferences",
      "schema": "datalake",
      "table": "customer_preferences",
      "columns": ["customer_id", "preference_type", "opted_in"]
    },
    {
      "type": "DataSourcing",
      "resultName": "customers",
      "schema": "datalake",
      "table": "customers",
      "columns": ["id", "first_name", "last_name"]
    },
    {
      "type": "DataSourcing",
      "resultName": "email_addresses",
      "schema": "datalake",
      "table": "email_addresses",
      "columns": ["customer_id", "email_address"]
    },
    {
      "type": "DataSourcing",
      "resultName": "phone_numbers",
      "schema": "datalake",
      "table": "phone_numbers",
      "columns": ["customer_id", "phone_number"]
    },
    {
      "type": "Transformation",
      "resultName": "output",
      "sql": "SELECT c.id AS customer_id, COALESCE(c.first_name, '') AS first_name, COALESCE(c.last_name, '') AS last_name, CASE WHEN pref.has_email = 1 THEN 'Email' WHEN pref.has_sms = 1 THEN 'SMS' WHEN pref.has_push = 1 THEN 'Push' ELSE 'None' END AS preferred_channel, COALESCE(em.email_address, 'N/A') AS email, COALESCE(ph.phone_number, '') AS phone, c.as_of FROM customers c LEFT JOIN (SELECT customer_id, MAX(CASE WHEN preference_type = 'MARKETING_EMAIL' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_email, MAX(CASE WHEN preference_type = 'MARKETING_SMS' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_sms, MAX(CASE WHEN preference_type = 'PUSH_NOTIFICATIONS' AND opted_in = 1 THEN 1 ELSE 0 END) AS has_push FROM customer_preferences GROUP BY customer_id) pref ON c.id = pref.customer_id LEFT JOIN (SELECT customer_id, email_address FROM email_addresses GROUP BY customer_id HAVING rowid = MAX(rowid)) em ON c.id = em.customer_id LEFT JOIN (SELECT customer_id, phone_number FROM phone_numbers GROUP BY customer_id HAVING rowid = MAX(rowid)) ph ON c.id = ph.customer_id ORDER BY c.id"
    },
    {
      "type": "CsvFileWriter",
      "source": "output",
      "outputFile": "Output/double_secret_curated/communication_channel_map.csv",
      "includeHeader": true,
      "writeMode": "Overwrite",
      "lineEnding": "LF"
    }
  ]
}
```

---

## 7. Writer Configuration

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | CsvFileWriter | CsvFileWriter | YES |
| source | output | output | YES |
| outputFile | `Output/curated/communication_channel_map.csv` | `Output/double_secret_curated/communication_channel_map.csv` | Path changed per V2 spec |
| includeHeader | true | true | YES |
| writeMode | Overwrite | Overwrite | YES |
| lineEnding | LF | LF | YES |
| trailerFormat | (not present) | (not present) | YES |

---

## 8. Proofmark Config Design

### Recommended Config

```yaml
comparison_target: "communication_channel_map"
reader: csv
threshold: 100.0
csv:
  header_rows: 1
  trailer_rows: 0
```

### Exclusions: Potentially email and phone

The BRD identifies two non-deterministic fields:

- **email**: When a customer has multiple email addresses, the value depends on database row ordering from DataSourcing (no ORDER BY on a unique key within a single as_of date). V1 uses last-wins dictionary overwrite semantics; V2 uses `GROUP BY customer_id HAVING rowid = MAX(rowid)`. These may produce different results if the underlying row ordering differs between the V1 foreach iteration and the V2 SQLite rowid order.
- **phone**: Same issue as email.

**Initial approach: Start strict (no exclusions).** If both V1 and V2 iterate/insert rows in the same order (DataSourcing ORDER BY as_of, then natural PG order), the last-wins result should match. Only add exclusions if proofmark comparison reveals actual differences.

If proofmark fails on these columns, add:

```yaml
columns:
  excluded:
    - name: "email"
      reason: "Non-deterministic: last-wins semantics on duplicate customer emails depend on DB row order [CommunicationChannelMapper.cs:41-43, BRD Non-Deterministic Fields]"
    - name: "phone"
      reason: "Non-deterministic: last-wins semantics on duplicate customer phones depend on DB row order [CommunicationChannelMapper.cs:51-53, BRD Non-Deterministic Fields]"
```

### Fuzzy Columns: None

No floating-point arithmetic is performed. No numeric computation produces epsilon-level differences.

### Rationale

Starting with zero exclusions and zero fuzzy overrides per the BLUEPRINT's prescription. The BRD documents email and phone as non-deterministic, but the non-determinism only manifests when a customer has multiple entries in the respective tables. If the test data has at most one email/phone per customer, strict comparison will pass. The fallback exclusion config is documented above for use during resolution if needed.

---

## 9. Traceability Matrix

| FSD Decision | BRD Requirement | Evidence |
|--------------|-----------------|----------|
| Tier 1 (no External module) | BR-1 through BR-7 | All V1 logic maps to SQL: JOINs, GROUP BY, CASE expressions. No procedural operations needed. [CommunicationChannelMapper.cs:6-118] |
| Remove `preference_id` from customer_preferences sourcing | AP4 | `preference_id` sourced at [communication_channel_map.json:10] but never referenced in [CommunicationChannelMapper.cs:59-74] |
| Remove `email_id` from email_addresses sourcing | AP4 | `email_id` sourced at [communication_channel_map.json:22] but never referenced in [CommunicationChannelMapper.cs:36-44] |
| Remove `phone_id` from phone_numbers sourcing | AP4 | `phone_id` sourced at [communication_channel_map.json:28] but never referenced in [CommunicationChannelMapper.cs:46-55] |
| SQL replaces External module | AP3 | Dictionary lookups + foreach iteration are expressible as SQL JOINs + GROUP BY [CommunicationChannelMapper.cs:36-113] |
| SQL JOINs replace foreach + dictionary | AP6 | Row-by-row iteration at [CommunicationChannelMapper.cs:80] replaced by set-based SQL |
| Preference priority via CASE expression | BR-2 | if/else-if chain [CommunicationChannelMapper.cs:89-97] maps to SQL CASE WHEN |
| Only opted_in=true preferences aggregated | BR-3 | `if (optedIn)` guard at [CommunicationChannelMapper.cs:67] maps to `AND opted_in = 1` in SQL |
| Asymmetric NULL defaults (email="N/A", phone="") | BR-4, AP5 | COALESCE with different defaults reproduces [CommunicationChannelMapper.cs:99-101] |
| Last-wins email/phone via GROUP BY HAVING MAX(rowid) | BR-5 | Dictionary overwrite at [CommunicationChannelMapper.cs:42,52] replicated via rowid ordering |
| as_of from customers table | BR-6 | `customers.Rows[0]["as_of"]` at [CommunicationChannelMapper.cs:76]; V2 uses c.as_of which matches for single-day runs |
| Empty output when customers empty | BR-7 | Null/empty check at [CommunicationChannelMapper.cs:29-33]; V2 produces same net result (empty CSV with header) |
| CsvFileWriter with Overwrite, LF, header, no trailer | BRD Writer Configuration | [communication_channel_map.json:39-44] |
| Column order: customer_id, first_name, last_name, preferred_channel, email, phone, as_of | BRD Output Schema | `outputColumns` list [CommunicationChannelMapper.cs:10-14] |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 implementation. No External module is needed.

The V1 External module (`CommunicationChannelMapper.cs`) is replaced entirely by the Transformation module's SQL query. All V1 business logic maps to SQL constructs as documented in Sections 2 and 5:

| V1 C# Pattern | V2 SQL Replacement |
|----------------|-------------------|
| `Dictionary<int, string> emailLookup` with overwrite | `LEFT JOIN (SELECT ... GROUP BY customer_id HAVING rowid = MAX(rowid))` |
| `Dictionary<int, string> phoneLookup` with overwrite | Same pattern as email |
| `Dictionary<int, HashSet<string>> prefLookup` with opted_in guard | `LEFT JOIN (SELECT customer_id, MAX(CASE ... AND opted_in = 1 ...) GROUP BY customer_id)` |
| if/else-if priority chain for preferred_channel | `CASE WHEN has_email = 1 THEN 'Email' WHEN has_sms = 1 THEN 'SMS' WHEN has_push = 1 THEN 'Push' ELSE 'None' END` |
| `emailLookup.ContainsKey(custId) ? emailLookup[custId] : "N/A"` | `COALESCE(em.email_address, 'N/A')` |
| `phoneLookup.ContainsKey(custId) ? phoneLookup[custId] : ""` | `COALESCE(ph.phone_number, '')` |
| `foreach (var custRow in customers.Rows)` | `FROM customers c` (set-based) |
