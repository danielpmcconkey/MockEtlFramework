# CustomerDemographics — Functional Specification Document

## Design Approach

**SQL-first.** The original External module (CustomerDemographicsBuilder.cs) performs dictionary lookups for first phone/email per customer, age calculation from birthdate, and age bracket assignment. All of these are expressible in SQL:
- Age: date arithmetic using `JULIANDAY()` or `strftime('%Y')` with birthday adjustment
- Age bracket: `CASE WHEN` expression on computed age
- Primary phone/email: `ROW_NUMBER() OVER (PARTITION BY customer_id ORDER BY phone_id)` to select the first record

No External module is needed.

## Anti-Patterns Eliminated

| AP Code | Present in Original? | Eliminated in V2? | How? |
|---------|---------------------|--------------------|------|
| AP-1    | Y | Y | Removed unused `segments` DataSourcing module entirely |
| AP-2    | N | N/A | Not applicable |
| AP-3    | Y | Y | Replaced External module with SQL Transformation + DataFrameWriter |
| AP-4    | Y | Y | Removed unused columns: `prefix`, `sort_name`, `suffix` from customers; `phone_id`, `phone_type` from phone_numbers; `email_id`, `email_type` from email_addresses. Only sourcing columns referenced in SQL. |
| AP-5    | N | N/A | NULL handling is consistent (empty string for missing phone/email) |
| AP-6    | Y | Y | Row-by-row iteration replaced with set-based SQL (JOINs, window functions, GROUP BY) |
| AP-7    | Y | Documented | Age bracket boundaries (26, 35, 45, 55, 65) kept as literals with SQL comments explaining each bracket |
| AP-8    | N | N/A | No overly complex SQL in original |
| AP-9    | N | N/A | Name accurately reflects output |
| AP-10   | N | N/A | No undeclared dependencies |

## V2 Pipeline Design

1. **DataSourcing** `customers` — `datalake.customers` (id, first_name, last_name, birthdate)
2. **DataSourcing** `phone_numbers` — `datalake.phone_numbers` (phone_id, customer_id, phone_number)
3. **DataSourcing** `email_addresses` — `datalake.email_addresses` (email_id, customer_id, email_address)
4. **Transformation** `demographics_output` — SQL joining customers with first phone/email, computing age and bracket
5. **DataFrameWriter** — writes to `double_secret_curated.customer_demographics`, Overwrite mode

Note: `phone_id` and `email_id` are sourced because they are needed for `ROW_NUMBER() ORDER BY` to determine the "first" phone/email. They do not appear in the final output.

## SQL Transformation Logic

```sql
SELECT
    c.id AS customer_id,
    COALESCE(c.first_name, '') AS first_name,
    COALESCE(c.last_name, '') AS last_name,
    c.birthdate,
    /* Age: year difference with birthday adjustment */
    CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
        - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END AS age,
    /* Age bracket: standard demographic cohorts */
    CASE
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) < 26
            THEN '18-25'   /* Young adult cohort */
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 35
            THEN '26-35'   /* Early career cohort */
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 45
            THEN '36-45'   /* Mid-career cohort */
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 55
            THEN '46-55'   /* Established professional cohort */
        WHEN (CAST(strftime('%Y', c.as_of) AS INTEGER) - CAST(strftime('%Y', c.birthdate) AS INTEGER)
              - CASE WHEN strftime('%m-%d', c.as_of) < strftime('%m-%d', c.birthdate) THEN 1 ELSE 0 END) <= 65
            THEN '56-65'   /* Pre-retirement cohort */
        ELSE '65+'         /* Retirement cohort */
    END AS age_bracket,
    COALESCE(p.phone_number, '') AS primary_phone,
    COALESCE(e.email_address, '') AS primary_email,
    c.as_of
FROM customers c
LEFT JOIN (
    SELECT customer_id, phone_number, as_of,
           ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY phone_id) AS rn
    FROM phone_numbers
) p ON c.id = p.customer_id AND c.as_of = p.as_of AND p.rn = 1
LEFT JOIN (
    SELECT customer_id, email_address, as_of,
           ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY email_id) AS rn
    FROM email_addresses
) e ON c.id = e.customer_id AND c.as_of = e.as_of AND e.rn = 1
ORDER BY c.id
```

## Traceability to BRD

| BRD Requirement | FSD Design Element |
|-----------------|-------------------|
| BR-1: One row per customer per date | Main query iterates `customers` via FROM, producing one row per customer. LEFT JOINs with rn=1 ensure at most one phone/email match. |
| BR-2: Age calculation with birthday adjustment | `strftime('%Y')` difference with `CASE WHEN strftime('%m-%d', as_of) < strftime('%m-%d', birthdate)` birthday adjustment |
| BR-3: Age bracket assignment | CASE expression with boundaries at 26, 35, 45, 55, 65 matching original switch expression |
| BR-4: Primary phone = first phone per customer | ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY phone_id) = 1 selects lowest phone_id |
| BR-5: Primary email = first email per customer | ROW_NUMBER() OVER (PARTITION BY customer_id, as_of ORDER BY email_id) = 1 selects lowest email_id |
| BR-6: Overwrite mode | DataFrameWriter `writeMode: "Overwrite"` |
| BR-7: Empty DataFrame on zero customers | When customers table has no rows for the date, the query returns zero rows naturally |
| BR-8: Birthdate passthrough | `c.birthdate` passed through directly in SELECT |
