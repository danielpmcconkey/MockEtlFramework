# CustomerContactInfoV2 -- Functional Specification Document

## 1. Overview & Tier

**Job Name:** CustomerContactInfoV2
**Config File:** `JobExecutor/Jobs/customer_contact_info_v2.json`
**Tier:** 1 (Framework Only) -- `DataSourcing -> Transformation (SQL) -> ParquetFileWriter`

This job produces a unified contact information dataset by combining phone numbers and email addresses into a single denormalized structure via UNION ALL. Each row represents one contact method for a customer, tagged by type (Phone/Email) and subtype (Mobile/Home/Work or Personal/Work). Output is Parquet in Append mode, accumulating historical records across effective dates.

No External module is needed. The entire pipeline is expressible as standard SQL with framework-provided DataSourcing and ParquetFileWriter modules.

**Traces to:** BRD Overview, BR-1 through BR-6

---

## 2. V2 Module Chain

```
DataSourcing (phone_numbers)
    -> DataSourcing (email_addresses)
        -> Transformation (SQL: UNION ALL + ORDER BY)
            -> ParquetFileWriter (Append, 2 parts)
```

### Module 1: DataSourcing -- phone_numbers
- **resultName:** `phone_numbers`
- **schema:** `datalake`
- **table:** `phone_numbers`
- **columns:** `["phone_id", "customer_id", "phone_type", "phone_number"]`
- Effective dates injected via shared state (`__minEffectiveDate` / `__maxEffectiveDate`). The framework appends `as_of` automatically since it is not in the column list.

### Module 2: DataSourcing -- email_addresses
- **resultName:** `email_addresses`
- **schema:** `datalake`
- **table:** `email_addresses`
- **columns:** `["email_id", "customer_id", "email_address", "email_type"]`
- Same effective date behavior as Module 1.

### Module 3: Transformation
- **resultName:** `contact_info`
- **sql:** See Section 5 (SQL Design)

### Module 4: ParquetFileWriter
- **source:** `contact_info`
- **outputDirectory:** `Output/double_secret_curated/customer_contact_info/`
- **numParts:** 2
- **writeMode:** `Append`

---

## 3. Anti-Pattern Analysis

### Identified Anti-Patterns

| ID | Applies? | V1 Behavior | V2 Prescription | Status |
|----|----------|-------------|-----------------|--------|
| AP1 | YES | V1 sources `datalake.segments` (segment_id, segment_name) but never references it in SQL. Dead-end data source wasting a DB query. | **Eliminated.** V2 removes the segments DataSourcing entry entirely. Only phone_numbers and email_addresses are sourced. | ELIMINATED |
| AP4 | NO | All sourced columns are used in the transformation SQL. `phone_id` and `email_id` are sourced but not selected in the output -- however, they are needed for the DataSourcing query and are available in case of debugging. Note: V1 sources these columns too and they pass through DataSourcing without issue. They are not selected in the Transformation SQL, so they do not appear in output. | Carried forward as-is. The columns are sourced by V1 and while `phone_id` / `email_id` are not in the final SELECT, they are part of the source table structure. Removing them would not affect output but could differ from V1's DataSourcing behavior. **Decision: keep them to match V1's DataSourcing fingerprint exactly.** | RETAINED |
| AP3 | NO | V1 does not use an External module. Already using framework-native modules. | N/A -- already Tier 1. | N/A |
| AP8 | NO | The CTE `all_contacts` is straightforward and fully consumed. No unused CTEs or window functions. | N/A. | N/A |

### Output-Affecting Wrinkles

| ID | Applies? | Analysis |
|----|----------|----------|
| W1-W8 | NO | No Sunday skip, weekend fallback, boundary rows, integer division, banker's rounding, double epsilon, trailer issues, or hardcoded dates. |
| W9 | NO | Append mode is correct for this job's use case (building historical record across dates). The BRD documents this as intentional. |
| W10 | NO | numParts=2 is reasonable for this dataset size. Not absurd. |

**Summary:** One anti-pattern applies (AP1: dead-end segments sourcing). It is eliminated in V2. No output-affecting wrinkles apply.

---

## 4. Output Schema

| Column | Type | Source | Transformation | Evidence |
|--------|------|--------|---------------|----------|
| customer_id | INTEGER | phone_numbers.customer_id / email_addresses.customer_id | Direct pass-through from both sides of UNION ALL | [customer_contact_info.json:29], BRD BR-1 |
| contact_type | TEXT | Literal | `'Phone'` for phone records, `'Email'` for email records | [customer_contact_info.json:29], BRD BR-1 |
| contact_subtype | TEXT | phone_numbers.phone_type / email_addresses.email_type | Aliased as `contact_subtype` | [customer_contact_info.json:29], BRD BR-2 |
| contact_value | TEXT | phone_numbers.phone_number / email_addresses.email_address | Aliased as `contact_value` | [customer_contact_info.json:29], BRD BR-3 |
| as_of | TEXT (date) | phone_numbers.as_of / email_addresses.as_of | Direct pass-through | [customer_contact_info.json:29], BRD BR-6 |

**Column order:** customer_id, contact_type, contact_subtype, contact_value, as_of

**Non-deterministic fields:** None. Output is fully deterministic given the ORDER BY clause and source data. Row order within tied sort keys may vary but Parquet is unordered by nature.

---

## 5. SQL Design

The V2 SQL is functionally identical to V1. The CTE wrapper is retained for readability.

```sql
WITH all_contacts AS (
    SELECT customer_id,
           'Phone' AS contact_type,
           phone_type AS contact_subtype,
           phone_number AS contact_value,
           as_of
    FROM phone_numbers
    UNION ALL
    SELECT customer_id,
           'Email' AS contact_type,
           email_type AS contact_subtype,
           email_address AS contact_value,
           as_of
    FROM email_addresses
)
SELECT customer_id,
       contact_type,
       contact_subtype,
       contact_value,
       as_of
FROM all_contacts
ORDER BY customer_id, contact_type, contact_subtype
```

**Design notes:**
- UNION ALL (not UNION) preserves all rows including potential duplicates, matching V1 behavior exactly (BR-1).
- ORDER BY on customer_id, contact_type, contact_subtype matches V1 exactly (BR-4).
- `as_of` is carried through from both source tables (BR-6). The DataSourcing module automatically appends `as_of` to the result DataFrame since it is not in the explicit column list.
- The `segments` table is NOT referenced -- this is the AP1 fix. V1 sources it but never uses it.

---

## 6. V2 Job Config JSON

```json
{
  "jobName": "CustomerContactInfoV2",
  "firstEffectiveDate": "2024-10-01",
  "modules": [
    {
      "type": "DataSourcing",
      "resultName": "phone_numbers",
      "schema": "datalake",
      "table": "phone_numbers",
      "columns": ["phone_id", "customer_id", "phone_type", "phone_number"]
    },
    {
      "type": "DataSourcing",
      "resultName": "email_addresses",
      "schema": "datalake",
      "table": "email_addresses",
      "columns": ["email_id", "customer_id", "email_address", "email_type"]
    },
    {
      "type": "Transformation",
      "resultName": "contact_info",
      "sql": "WITH all_contacts AS (SELECT customer_id, 'Phone' AS contact_type, phone_type AS contact_subtype, phone_number AS contact_value, as_of FROM phone_numbers UNION ALL SELECT customer_id, 'Email' AS contact_type, email_type AS contact_subtype, email_address AS contact_value, as_of FROM email_addresses) SELECT customer_id, contact_type, contact_subtype, contact_value, as_of FROM all_contacts ORDER BY customer_id, contact_type, contact_subtype"
    },
    {
      "type": "ParquetFileWriter",
      "source": "contact_info",
      "outputDirectory": "Output/double_secret_curated/customer_contact_info/",
      "numParts": 2,
      "writeMode": "Append"
    }
  ]
}
```

**Key differences from V1:**
1. `jobName` changed from `CustomerContactInfo` to `CustomerContactInfoV2`
2. `outputDirectory` changed from `Output/curated/customer_contact_info/` to `Output/double_secret_curated/customer_contact_info/`
3. **Removed:** The third DataSourcing module for `datalake.segments` (AP1 elimination)

**Preserved from V1:**
- `firstEffectiveDate`: `2024-10-01`
- DataSourcing columns for phone_numbers and email_addresses: identical
- Transformation SQL: identical (produces same output)
- ParquetFileWriter config: `numParts: 2`, `writeMode: Append` (matching V1 exactly)

---

## 7. Writer Config

| Property | V1 Value | V2 Value | Match? |
|----------|----------|----------|--------|
| Writer type | ParquetFileWriter | ParquetFileWriter | YES |
| source | contact_info | contact_info | YES |
| outputDirectory | Output/curated/customer_contact_info/ | Output/double_secret_curated/customer_contact_info/ | Path change only (per spec) |
| numParts | 2 | 2 | YES |
| writeMode | Append | Append | YES |

The writer configuration is identical to V1 except for the output path, which is changed per project convention to write V2 output to `Output/double_secret_curated/`.

---

## 8. Proofmark Config Design

### Reader Type
**Parquet** -- V1 and V2 both use ParquetFileWriter.

### Exclusions
**None.** All output columns are deterministic and directly derived from source data. No timestamps, UUIDs, or runtime-generated values exist in the output.

### Fuzzy Columns
**None.** All columns are text or integer types. No floating-point arithmetic is involved. Strict comparison is appropriate for every column.

### Threshold
**100.0** -- Full match required. No known sources of non-determinism.

### Proposed Config

```yaml
comparison_target: "customer_contact_info"
reader: parquet
threshold: 100.0
```

**Justification for strict defaults:** The output schema contains only customer_id (integer), contact_type (text literal), contact_subtype (text), contact_value (text), and as_of (date-as-text). No floating-point computation, no runtime timestamps, no non-deterministic fields. Every value is deterministically derived from source data. Strict comparison with zero overrides is the correct starting point.

---

## 9. Traceability Matrix

| BRD Requirement | FSD Section | Design Decision | Evidence |
|-----------------|-------------|-----------------|----------|
| BR-1: UNION ALL combines phone and email | Sec 5 (SQL), Sec 6 (Config) | SQL uses UNION ALL with 'Phone'/'Email' literals | [customer_contact_info.json:29] |
| BR-2: phone_type/email_type -> contact_subtype | Sec 5 (SQL), Sec 4 (Schema) | Aliased in both halves of UNION ALL | [customer_contact_info.json:29] |
| BR-3: phone_number/email_address -> contact_value | Sec 5 (SQL), Sec 4 (Schema) | Aliased in both halves of UNION ALL | [customer_contact_info.json:29] |
| BR-4: ORDER BY customer_id, contact_type, contact_subtype | Sec 5 (SQL) | ORDER BY clause matches V1 exactly | [customer_contact_info.json:29] |
| BR-5: segments sourced but unused (dead-end) | Sec 3 (Anti-Pattern AP1) | Removed from V2 config -- AP1 eliminated | [customer_contact_info.json:20-22] |
| BR-6: as_of column carried through | Sec 5 (SQL), Sec 4 (Schema) | Selected in both halves of UNION ALL | [customer_contact_info.json:29] |
| BRD Output Type: ParquetFileWriter | Sec 7 (Writer Config) | V2 uses ParquetFileWriter, matching V1 | [customer_contact_info.json:32-37] |
| BRD Writer: numParts=2 | Sec 7 (Writer Config) | V2 uses numParts=2, matching V1 | [customer_contact_info.json:35] |
| BRD Writer: writeMode=Append | Sec 7 (Writer Config) | V2 uses Append, matching V1 | [customer_contact_info.json:36] |
| BRD Non-deterministic: None | Sec 8 (Proofmark) | No exclusions or fuzzy columns needed | BRD "Non-Deterministic Fields" section |
| AP1: Dead-end sourcing (segments) | Sec 3 (Anti-Pattern) | Eliminated in V2 | [customer_contact_info.json:20-22] vs SQL at [29] |

---

## 10. External Module Design

**Not applicable.** This is a Tier 1 (Framework Only) job. No External module is needed.

The entire pipeline -- data sourcing, SQL transformation (UNION ALL), and Parquet file writing -- is fully handled by the framework's built-in modules. V1 itself does not use an External module for this job, and V2 maintains the same clean architecture.
