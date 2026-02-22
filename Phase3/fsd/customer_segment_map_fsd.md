# CustomerSegmentMap — Functional Specification Document

## Design Approach

**SQL-first.** The original already uses a SQL Transformation with a clean JOIN. The V2 retains the same SQL logic but removes the unused `branches` DataSourcing module (AP-1 fix).

No External module needed (original did not use one either).

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y | Y | Removed unused `branches` DataSourcing module entirely |
| AP-2    | N | N/A | Not applicable |
| AP-3    | N | N/A | Original already uses SQL Transformation |
| AP-4    | N | N/A | All sourced columns from customers_segments and segments are used in the output or join condition (AP-4 only applies to branches, covered by AP-1) |
| AP-5    | N | N/A | Not applicable |
| AP-6    | N | N/A | No External module |
| AP-7    | N | N/A | No magic values |
| AP-8    | N | N/A | SQL is already clean and minimal |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `customers_segments` — `datalake.customers_segments` (customer_id, segment_id)
2. **DataSourcing** `segments` — `datalake.segments` (segment_id, segment_name, segment_code)
3. **Transformation** `seg_map` — SQL joining customers_segments to segments
4. **DataFrameWriter** — writes to `double_secret_curated.customer_segment_map`, Append mode

## SQL Transformation Logic

```sql
SELECT
    cs.customer_id,
    cs.segment_id,
    s.segment_name,
    s.segment_code,
    cs.as_of
FROM customers_segments cs
JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of
ORDER BY cs.customer_id, cs.segment_id
```

This SQL is identical to the original Transformation SQL. The only change is removing the unused `branches` DataSourcing module from the pipeline.

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per customer-segment pair per date | INNER JOIN produces one output row per matching pair |
| BR-2: Join on segment_id AND as_of | `JOIN segments s ON cs.segment_id = s.segment_id AND cs.as_of = s.as_of` |
| BR-3: Ordered by customer_id, segment_id | `ORDER BY cs.customer_id, cs.segment_id` |
| BR-4: Append mode | DataFrameWriter `writeMode: "Append"` |
| BR-5: Weekend dates included | Both source tables have 7-day data |
| BR-6: Inner join excludes unmatched segments | INNER JOIN semantics exclude rows where segment_id is not in segments for that date |
