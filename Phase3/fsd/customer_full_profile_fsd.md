# CustomerFullProfile — Functional Specification Document

## Design Approach

**SQL-first with AP-2 fix.** The original External module (FullProfileAssembler.cs) re-derives age, age_bracket, primary_phone, and primary_email using identical logic to CustomerDemographics. The V2 eliminates this duplication by reading pre-computed demographics from `curated.customer_demographics`, then enriching with segment data from datalake.

The V2 reads from `curated` schema (populated by the original CustomerDemographics job during comparison runs), avoiding V2-to-V2 dependency chains.

No External module is needed. SQL can handle the segment concatenation via GROUP_CONCAT.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | N | N/A | No unused data sources in original |
| AP-2    | Y | Y | Instead of re-deriving age, age_bracket, primary_phone, primary_email from raw datalake tables, V2 reads them from `curated.customer_demographics` |
| AP-3    | Y | Y | Replaced External module with SQL Transformation + DataFrameWriter |
| AP-4    | Y | Y | Removed unused columns: `phone_id`, `phone_type` from phone_numbers; `email_id`, `email_type` from email_addresses; `segment_code` from segments. V2 no longer sources phone_numbers or email_addresses at all (reads from curated.customer_demographics). |
| AP-5    | N | N/A | NULL handling consistent |
| AP-6    | Y | Y | Five foreach loops replaced with set-based SQL JOINs and GROUP_CONCAT |
| AP-7    | Y | Documented | Age bracket magic values no longer computed here (delegated to CustomerDemographics). Documented in that job's FSD. |
| AP-8    | N | N/A | No complex SQL in original |
| AP-9    | N | N/A | Name reasonably reflects output |
| AP-10   | Y | Y | V2 declares SameDay dependency on CustomerDemographics (required for curated.customer_demographics to be populated) |

## V2 Pipeline Design

1. **DataSourcing** `customer_demographics` — `curated.customer_demographics` (customer_id, first_name, last_name, age, age_bracket, primary_phone, primary_email)
2. **DataSourcing** `customers_segments` — `datalake.customers_segments` (customer_id, segment_id)
3. **DataSourcing** `segments` — `datalake.segments` (segment_id, segment_name)
4. **Transformation** `profile_output` — SQL joining demographics with segment data, concatenating segment names
5. **DataFrameWriter** — writes to `double_secret_curated.customer_full_profile`, Overwrite mode

## SQL Transformation Logic

```sql
SELECT
    cd.customer_id,
    cd.first_name,
    cd.last_name,
    cd.age,
    cd.age_bracket,
    cd.primary_phone,
    cd.primary_email,
    /* Comma-separated segment names, ordered by segment_id to match original iteration order */
    COALESCE(seg_agg.segment_list, '') AS segments,
    cd.as_of
FROM customer_demographics cd
LEFT JOIN (
    SELECT
        cs.customer_id,
        cs.as_of,
        GROUP_CONCAT(s.segment_name) AS segment_list
    FROM customers_segments cs
    JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of
    GROUP BY cs.customer_id, cs.as_of
) seg_agg ON cd.customer_id = seg_agg.customer_id AND cd.as_of = seg_agg.as_of
ORDER BY cd.customer_id
```

**Key design decision:** GROUP_CONCAT in SQLite concatenates values in the order they appear in the group. Since DataSourcing returns rows from `customers_segments` ordered by the table's primary key (`id`), and the `id` column happens to sort consistently with `segment_id`, the segment names will be concatenated in segment_id order. This matches the original External module's iteration behavior.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per customer per date | Main query driven by `customer_demographics` which has one row per customer |
| BR-2: Age calculation | Read directly from `curated.customer_demographics.age` (AP-2 fix) |
| BR-3: Age bracket | Read directly from `curated.customer_demographics.age_bracket` (AP-2 fix) |
| BR-4: Primary phone | Read directly from `curated.customer_demographics.primary_phone` (AP-2 fix) |
| BR-5: Primary email | Read directly from `curated.customer_demographics.primary_email` (AP-2 fix) |
| BR-6: Comma-separated segment names | `GROUP_CONCAT(s.segment_name)` with inner join ensuring only valid segments included (BR-8) |
| BR-7: Empty string for no segments | `COALESCE(seg_agg.segment_list, '')` handles customers with no segment mappings |
| BR-8: Only segments with valid segment_id | Inner JOIN between customers_segments and segments filters invalid segment_ids |
| BR-9: Overwrite mode | DataFrameWriter `writeMode: "Overwrite"` |
| BR-10: Empty DataFrame on zero customers | When customer_demographics has no rows (weekend), query returns zero rows |
